"""Create a local, editable Blender review file from exact WPF sampler joint matrices.
No model or animation data is uploaded; source GLB and runtime JSON remain unchanged.
"""
import argparse,hashlib,json,sys
from pathlib import Path
import bpy
from mathutils import Matrix,Vector

cli=argparse.ArgumentParser()
cli.add_argument('--poses',required=True)
cli.add_argument('--output',required=True)
args=cli.parse_args(sys.argv[sys.argv.index('--')+1:])
source=json.loads(Path(args.poses).read_text(encoding='utf-8'))
source_path=Path(source['source']).resolve()
source_hash_before=hashlib.sha256(source_path.read_bytes()).hexdigest().upper()
output=Path(args.output).resolve()
if output.suffix.lower()!='.blend': raise RuntimeError('Review output must be a separate .blend file.')
if output.exists(): raise RuntimeError('Review file exists; choose a new output path.')
validation_output=output.with_suffix('.validation.json')
if validation_output.exists(): raise RuntimeError('Review validation exists; choose a new output path.')
if not source.get('frames'): raise RuntimeError('No runtime pose frames were supplied.')
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=str(source_path))
rigs=[o for o in bpy.context.scene.objects if o.type=='ARMATURE']
if len(rigs)!=1: raise RuntimeError('Review requires exactly one armature.')
rig=rigs[0]
if rig.animation_data:
    for track in rig.animation_data.nla_tracks: track.mute=True
    rig.animation_data.action=None
scene=bpy.context.scene
scene.render.fps=30
convert=Matrix(((1,0,0,0),(0,0,-1,0),(0,1,0,0),(0,0,0,1)))
def matrix(values):
    return Matrix([values[i:i+4] for i in range(0,16,4)]).transposed()
nodes=source['nodes']
rest={n['name']:matrix(n['restWorld']) for n in nodes}
inverse_rig=rig.matrix_world.inverted()
bones=sorted(rig.pose.bones,key=lambda b:len(b.parent_recursive))
max_joint_error=0
review_actions=[]
for name in dict.fromkeys(f['action'] for f in source['frames']):
    action=bpy.data.actions.new('Review_'+name)
    review_actions.append(action)
    action.use_fake_user=True
    rig.animation_data_create();rig.animation_data.action=action
    previous={}
    for sample in (f for f in source['frames'] if f['action']==name):
        frame=1+sample['seconds']*30
        scene.frame_set(int(frame))
        world={n['name']:matrix(m) for n,m in zip(nodes,sample['world'])}
        desired={}
        for bone in bones:
            if bone.name not in world:continue
            delta=convert@(world[bone.name]@rest[bone.name].inverted())@convert.inverted()
            desired[bone.name]=inverse_rig@delta@rig.matrix_world@bone.bone.matrix_local
        for bone in bones:
            if bone.name not in world: continue
            bone.rotation_mode='QUATERNION'
            parent_args={} if not bone.parent else {'parent_matrix':desired[bone.parent.name],'parent_matrix_local':bone.parent.bone.matrix_local}
            bone.matrix_basis=bone.bone.convert_local_to_pose(desired[bone.name],bone.bone.matrix_local,invert=True,**parent_args)
            if bone.name in previous:bone.rotation_quaternion.make_compatible(previous[bone.name])
            previous[bone.name]=bone.rotation_quaternion.copy()
            bone.keyframe_insert('location',frame=frame,group=bone.name)
            bone.keyframe_insert('rotation_quaternion',frame=frame,group=bone.name)
            bone.keyframe_insert('scale',frame=frame,group=bone.name)
        bpy.context.view_layer.update()
        for bone in bones:
            if bone.name not in world:continue
            expected=convert@world[bone.name].translation
            max_joint_error=max(max_joint_error,(rig.matrix_world@bone.head-expected).length)
        scene.frame_end=max(scene.frame_end,int(frame+1))
    # Blender 5 stores F-curves in layered action channel bags.
    curves=action.fcurves if hasattr(action,'fcurves') else (
        curve for layer in action.layers for strip in layer.strips
        if hasattr(strip,'channelbags') for bag in strip.channelbags for curve in bag.fcurves)
    for curve in curves:
        for key in curve.keyframe_points:key.interpolation='LINEAR'
rig.animation_data.action=next((a for a in review_actions if a.name=='Review_waving'),review_actions[0])
scene.frame_start=1;scene.frame_end=max(1,int(rig.animation_data.action.frame_range[1]));scene.frame_set(min(16,scene.frame_end))
rig.show_in_front=True
for obj in bpy.context.selected_objects:obj.select_set(False)
rig.select_set(True);bpy.context.view_layer.objects.active=rig
# Keep textures in the review file, so it is usable without temporary extraction paths.
bpy.ops.file.pack_all()
if max_joint_error>.001:raise RuntimeError('Runtime-to-Blender joint mismatch: '+str(max_joint_error))
if hashlib.sha256(source_path.read_bytes()).hexdigest().upper()!=source_hash_before:
    raise RuntimeError('Source model changed during review; refusing to save the diagnostic scene.')
output.parent.mkdir(parents=True,exist_ok=True)
bpy.ops.wm.save_as_mainfile(filepath=str(output))
source_hash_after=hashlib.sha256(source_path.read_bytes()).hexdigest().upper()
validation_output.write_text(json.dumps({'maxJointError':max_joint_error,'sourcePoses':str(Path(args.poses).resolve()),'actions':[a.name for a in review_actions],
    'blenderVersion':bpy.app.version_string,'sourceModel':str(source_path),'sourceHashBefore':source_hash_before,'sourceHashAfter':source_hash_after,
    'sourceHashStable':source_hash_before==source_hash_after,'sourceModelWasSavedOrExported':False,'diagnosticBlend':str(output)}),encoding='utf-8')
if source_hash_before!=source_hash_after: raise RuntimeError('Source model hash changed after diagnostic save.')
print('Editable motion review:',output)
