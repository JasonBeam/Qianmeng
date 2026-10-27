import bpy,math,json,time,sys
from pathlib import Path
from mathutils import Vector,Quaternion,Matrix
import argparse
cli=argparse.ArgumentParser(description="Prepare a single Meshy/Mixamo skinned FBX in a separate Blender background process.")
cli.add_argument('--input',required=True);cli.add_argument('--output',required=True);args=cli.parse_args(sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else [])
source=Path(args.input).resolve();base=Path(args.output).resolve();base.mkdir(exist_ok=True,parents=True)
if source.suffix.lower()!='.fbx' or source.stat().st_size>256*1024*1024:raise RuntimeError('Choose a local FBX under 256 MiB.')
if any((base/n).exists() for n in ('companion-weights-cleaned.fbx','companion-animated-source.glb','companion-weight-cleaned.blend')):raise RuntimeError('Output files already exist; use a new output directory.')
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=str(source),use_anim=True,automatic_bone_orientation=False)
scene=bpy.context.scene;fps=scene.render.fps/scene.render.fps_base
arms=[o for o in scene.objects if o.type=='ARMATURE'];skinned=[o for o in scene.objects if o.type=='MESH' and any(m.type=='ARMATURE' for m in o.modifiers)]
if len(arms)!=1 or len(skinned)!=1:raise RuntimeError('This helper requires exactly one rig and one skinned mesh; review multipart assets manually.')
arm=arms[0];mesh=skinned[0]
if not all(n in arm.data.bones for n in ('mixamorig:Hips','mixamorig:LeftArm','mixamorig:RightArm')):raise RuntimeError('This helper expects a Mixamo-style rig; do not guess another skeleton orientation.')
if any(o.type=='MESH' and o!=mesh and o.name!='Icosphere' for o in scene.objects):raise RuntimeError('Additional meshes need manual review; will not silently omit them.')
report={'originalVertices':len(mesh.data.vertices),'originalTriangles':sum(len(p.vertices)-2 for p in mesh.data.polygons),'excludedHelpers':[],'weightChanges':0,'overFourBefore':0,'unboundBefore':0}
for obj in list(scene.objects):
 if obj.type=='MESH' and obj!=mesh and obj.name=='Icosphere':report['excludedHelpers'].append(obj.name);bpy.data.objects.remove(obj,do_unlink=True)
groups={g.index:g for g in mesh.vertex_groups if g.name in arm.data.bones};weighted=set();changed=[]
for v in mesh.data.vertices:
 influences=sorted([(g.group,g.weight) for g in v.groups if g.group in groups and g.weight>1e-8],key=lambda x:-x[1]);weighted.update(groups[g].name for g,w in influences)
 if not influences:report['unboundBefore']+=1;continue
 total=sum(w for g,w in influences);report['overFourBefore']+=len(influences)>4
 if len(influences)<=4 and abs(total-1)<1e-5:continue
 changed.append((v.index,influences));keep=influences[:4];denom=sum(w for g,w in keep)
 for g,w in influences:
  if (g,w) not in keep:groups[g].remove([v.index])
  else:groups[g].add([v.index],w/denom,'REPLACE')
 report['weightChanges']+=1
if report['unboundBefore']:raise RuntimeError('Unbound vertices require manual review; do not invent weights.')
for b in arm.data.bones:b.use_deform=b.name in weighted
report['weightedBones']=len(weighted);report['bones']=len(arm.data.bones)
action=arm.animation_data.action;start,end=map(int,action.frame_range);frames={};root=next(b for b in arm.pose.bones if b.parent is None);root_world=[]
for f in range(start,end+1):
 scene.frame_set(f);frames[f]={b.name:b.matrix_basis.copy() for b in arm.pose.bones};root_world.append(root.matrix.translation.copy())
first=frames[start];last=frames[end]
angles=[first[n].to_quaternion().rotation_difference(last[n].to_quaternion()).angle for n in first]
report['sourceLoop']={'frames':[start,end],'fps':fps,'duration':(end-start)/fps,'maxJointAngleDegrees':max(angles)*180/math.pi,'rootDelta':list(root_world[-1]-root_world[0])}
# Remove planar travel in the exported copy while retaining natural vertical bounce.
for f in frames:
 posed=root.bone.matrix_local@frames[f][root.name];posed.translation.x=root_world[0].x;posed.translation.y=root_world[0].y;frames[f][root.name]=root.bone.matrix_local.inverted()@posed
# Preserve already seamless cycles; otherwise use a short tail crossfade with quaternion interpolation.
blend_frames=4 if max(angles)>.01 or (root_world[-1]-root_world[0]).length>.001 else 0
for f in range(end-blend_frames,end+1) if blend_frames else []:
 t=(f-(end-blend_frames))/max(1,blend_frames);t=t*t*(3-2*t)
 for name,m in list(frames[f].items()):
  loc,q,s=m.decompose();loc0,q0,s0=frames[start][name].decompose();frames[f][name]=Matrix.LocRotScale(loc.lerp(loc0,t),q.slerp(q0,t),s.lerp(s0,t))
report['tailBlendFrames']=blend_frames
arm.animation_data.action=None
for a in list(bpy.data.actions):bpy.data.actions.remove(a)
for b in arm.pose.bones:b.rotation_mode='QUATERNION'
def bake(name,poses):
 action=bpy.data.actions.new(name);action.use_fake_user=True;arm.animation_data.action=action
 prev={}
 for f,pose in poses.items():
  scene.frame_set(f)
  for b in arm.pose.bones:
   loc,q,s=pose[b.name].decompose()
   if b.name in prev:q.make_compatible(prev[b.name])
   prev[b.name]=q.copy();b.location=loc;b.rotation_quaternion=q;b.scale=s
   b.keyframe_insert('location',frame=f,group=b.name);b.keyframe_insert('rotation_quaternion',frame=f,group=b.name);b.keyframe_insert('scale',frame=f,group=b.name)
 for fc in action.fcurves:
  for k in fc.keyframe_points:k.interpolation='LINEAR'
 return action
running=bake('Running',frames)
# Derived companion idle: bind legs/spine, relaxed arms, subtle breathing. This is new, not a supplied source clip.
idle_base={b.name:Matrix.Identity(4) for b in arm.pose.bones}
for name in ('mixamorig:LeftArm','mixamorig:RightArm'):
 b=arm.data.bones.get(name)
 if not b:continue
 original=(b.tail_local-b.head_local).normalized();side=1 if 'Left' in name else -1
 target=Vector((side*.15,-.04,-1)).normalized();world_rotation=original.rotation_difference(target)
 local=b.matrix_local.to_quaternion().inverted()@world_rotation@b.matrix_local.to_quaternion();idle_base[name]=local.to_matrix().to_4x4()
idle_frames={}
idle_end=round(3*fps)+1
keys=list(range(1,idle_end+1,max(1,round(fps/10))))
if keys[-1]!=idle_end:keys.append(idle_end)
for f in keys:
 pose={n:m.copy() for n,m in idle_base.items()};phase=(f-1)/(idle_end-1)*2*math.pi
 n='mixamorig:Spine2'
 if n in pose:pose[n]=pose[n]@Matrix.Diagonal(Vector((1,1+.004*math.sin(phase),1,1)))
 idle_frames[f]=pose
idle=bake('Idle',idle_frames)
report['derivedIdle']={'duration':3,'description':'Bind legs and spine, relaxed arms, subtle breathing; generated companion idle.'}
# Repack only textures used by the character. Preserve the full original in the untouched FBX/import blend.
# Export the editable FBX before texture/material simplification. Original packed texture resolution is retained.
bpy.ops.object.select_all(action='DESELECT');arm.select_set(True);mesh.select_set(True);bpy.context.view_layer.objects.active=arm
arm.animation_data.action=running;scene.frame_start=1;scene.frame_end=end;scene.frame_set(1)
bpy.ops.export_scene.fbx(filepath=str(base/'companion-weights-cleaned.fbx'),use_selection=True,object_types={'MESH','ARMATURE'},add_leaf_bones=False,use_armature_deform_only=True,path_mode='COPY',embed_textures=True,bake_anim=True,bake_anim_use_all_actions=True,bake_anim_simplify_factor=0,axis_forward='-Z',axis_up='Y')
used_images=set()
for mat in mesh.data.materials:
 if mat and mat.use_nodes:
  for n in mat.node_tree.nodes:
   if n.type=='TEX_IMAGE' and n.image:used_images.add(n.image)
tex=base/'textures';tex.mkdir(exist_ok=True)
for i in used_images:
 if max(i.size)>1024:i.scale(max(1,round(i.size[0]*1024/max(i.size))),max(1,round(i.size[1]*1024/max(i.size))))
 i.filepath_raw=str(tex/(i.name.replace('.','_')+'.png'));i.file_format='PNG';i.save();i.pack()
report['textures']=[{'name':i.name,'size':list(i.size)} for i in used_images]
# Ensure a simple local base-color material without unused auxiliary textures in runtime output.
for mat in mesh.data.materials:
 if mat and mat.use_nodes:
  for n in mat.node_tree.nodes:
   if n.type=='BSDF_PRINCIPLED':
    n.inputs['Metallic'].default_value=0
    for link in list(n.inputs['Metallic'].links):mat.node_tree.links.remove(link)
    n.inputs['Roughness'].default_value=.8
    for link in list(n.inputs['Roughness'].links):mat.node_tree.links.remove(link)
bpy.ops.object.select_all(action='DESELECT');arm.select_set(True);mesh.select_set(True);bpy.context.view_layer.objects.active=arm
arm.animation_data.action=running;scene.frame_start=1;scene.frame_end=end;scene.frame_set(1)
bpy.ops.wm.save_as_mainfile(filepath=str(base/'companion-weight-cleaned.blend'))
bpy.ops.export_scene.gltf(filepath=str(base/'companion-animated-source.glb'),export_format='GLB',use_selection=True,export_animations=True,export_animation_mode='ACTIONS',export_force_sampling=True,export_anim_slide_to_zero=True,export_def_bones=True,export_skins=True,export_all_influences=False,export_materials='EXPORT',export_image_format='JPEG',export_jpeg_quality=90)
(base/'rig-repair-report.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf8')
print('RIG_PREPARATION_COMPLETE')
