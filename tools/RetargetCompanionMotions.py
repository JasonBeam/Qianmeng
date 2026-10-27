"""Local Blender retarget experiment: CC0 Mesh2Motion humanoid clips onto a Mixamo GLB.
Retains the target character and original clip names; Blender re-export may resample them.
Never overwrites inputs. Output geometry and every clip still require comparison.
"""
import argparse,json,sys
from pathlib import Path
import bpy
from mathutils import Matrix,Vector,Quaternion
import math

cli=argparse.ArgumentParser()
cli.add_argument('--target',required=True);cli.add_argument('--motions',required=True);cli.add_argument('--output',required=True)
cli.add_argument('--base-motions');cli.add_argument('--mocap-motions')
cli.add_argument('--idle-source',default='Idle_Subtle')
cli.add_argument('--greeting-source',default='Greeting')
cli.add_argument('--nod-source',default='Head Nod')
cli.add_argument('--walk-source',default='Walk_Female')
cli.add_argument('--listening-source',default='Idle Listening')
cli.add_argument('--bow-source',default='Bow')
cli.add_argument('--relaxed-arms-for',nargs='*',default=[])
cli.add_argument('--head-only-for',nargs='*',default=[])
cli.add_argument('--right-arm-only-for',nargs='*',default=[])
cli.add_argument('--greeting-time-scale',type=float,default=1.0)
cli.add_argument('--idle-breath-degrees',type=float,default=0.0)
cli.add_argument('--greeting-wave-degrees',type=float,default=0.0)
cli.add_argument('--greeting-wave-cycles',type=float,default=2.5)
args=cli.parse_args(sys.argv[sys.argv.index('--')+1:])
out=Path(args.output).resolve()
if out.exists() or out.with_suffix('.blend').exists():raise RuntimeError('Choose a new output path.')
bpy.ops.wm.read_factory_settings(use_empty=True)
scene=bpy.context.scene;scene.render.fps=30
bpy.ops.import_scene.gltf(filepath=str(Path(args.target).resolve()))
target=next(o for o in scene.objects if o.type=='ARMATURE')
target_objects=set(scene.objects);original_actions=set(bpy.data.actions)
garment_sections={}
for mesh in (o for o in target_objects if o.type=='MESH'):
    groups={g.index:g.name for g in mesh.vertex_groups}
    for vertex in mesh.data.vertices:
        point=mesh.matrix_world@vertex.co
        if not .60<point.z<1.25:continue
        garment=sum(g.weight for g in vertex.groups if any(token in groups[g.group] for token in ('Hips','Spine','UpLeg','Leg')))
        if garment<.6:continue
        bucket=round(point.z/.025)
        garment_sections[bucket]=max(garment_sections.get(bucket,0),abs(point.x))
if target.animation_data:
    for track in target.animation_data.nla_tracks:track.mute=True
    target.animation_data.action=None
source_paths=[Path(args.motions).resolve()]
source_paths.extend(Path(p).resolve() for p in (args.base_motions,args.mocap_motions) if p)
if len(set(source_paths))!=len(source_paths):raise RuntimeError('Each motion source file must be unique.')
source_records=[];source_objects=set()
for source_path in source_paths:
    objects_before=set(scene.objects);actions_before=set(bpy.data.actions)
    bpy.ops.import_scene.gltf(filepath=str(source_path))
    imported_objects=set(scene.objects)-objects_before
    rigs=[o for o in imported_objects if o.type=='ARMATURE']
    if len(rigs)!=1:raise RuntimeError(f'Expected one motion rig in {source_path}, found {len(rigs)}.')
    source=rigs[0];source_objects.update(imported_objects)
    if source.animation_data:
        for track in source.animation_data.nla_tracks:track.mute=True
        source.animation_data.action=None
    actions={a.name:a for a in bpy.data.actions if a not in actions_before}
    source_records.append({'path':str(source_path),'rig':source,'actions':actions})
bpy.context.view_layer.update()
mapping_template={'Hips':'pelvis','Spine':'spine_01','Spine1':'spine_02','Spine2':'spine_03','Neck':'neck_01','Head':'head'}
for side,suffix in [('Left','l'),('Right','r')]:
    for dst,src in [('Shoulder','clavicle'),('Arm','upperarm'),('ForeArm','lowerarm'),('Hand','hand'),('UpLeg','thigh'),('Leg','calf'),('Foot','foot'),('ToeBase','ball')]:mapping_template[side+dst]=src+'_'+suffix
    for finger in ['Thumb','Index','Middle','Ring','Pinky']:
        for i in range(1,4):mapping_template[f'{side}Hand{finger}{i}']=f'{finger.lower()}_0{i}_{suffix}'
bones=sorted(target.pose.bones,key=lambda b:len(b.parent_recursive))
tr={b.name:target.matrix_world@b.bone.matrix_local for b in bones}
for record in source_records:
    source=record['rig']
    mapping={'mixamorig:'+k:v for k,v in mapping_template.items() if 'mixamorig:'+k in target.pose.bones and v in source.pose.bones}
    sr={b.name:source.matrix_world@b.bone.matrix_local for b in source.pose.bones}
    correction={}
    for name,mapped in mapping.items():
        tb=target.data.bones[name];sb=source.data.bones[mapped]
        # Preserve the target's exact bind/rest orientation when the source is
        # at rest. The previous extra bone-axis rotation made that invariant
        # false and introduced a systematic twist into every retargeted pose.
        correction[name]=sr[mapped].to_quaternion().inverted()@tr[name].to_quaternion()
    scale=(tr['mixamorig:Head'].translation-tr['mixamorig:Hips'].translation).length/(sr['head'].translation-sr['pelvis'].translation).length
    leg_scale=sum((tr['mixamorig:'+b].translation-tr['mixamorig:'+a].translation).length for a,b in [('LeftUpLeg','LeftLeg'),('LeftLeg','LeftFoot')])/sum((sr[b].translation-sr[a].translation).length for a,b in [('thigh_l','calf_l'),('calf_l','foot_l')])
    relaxed_hands={}
    for side in ('Left','Right'):
        name='mixamorig:'+side+'Hand'
        direction=(tr[name+'Middle1'].translation-tr[name].translation).normalized()
        relaxed_hands[name]=direction.rotation_difference(Vector((0,-.08,-1)).normalized())@tr[name].to_quaternion()
    record.update({'mapping':mapping,'rest':sr,'target_rest':tr,'correction':correction,'scale':scale,'leg_scale':leg_scale,'relaxed_hands':relaxed_hands})
    if len(mapping)<40:raise RuntimeError(f'Only {len(mapping)} bones map from {record["path"]}.')
def solve_leg(desired,side,suffix,context):
    names=['mixamorig:'+side+b for b in ('UpLeg','Leg','Foot','ToeBase')]
    if any(n not in desired for n in names):return
    upper,lower,foot,toe=names
    source=context['rig'];tr=context['target_rest'];sr=context['rest'];leg_scale=context['leg_scale']
    a,b,c=[desired[n].translation for n in names[:3]]
    src=source.matrix_world@source.pose.bones['foot_'+suffix].matrix
    target_foot=tr[foot].translation+(src.translation-sr['foot_'+suffix].translation)*leg_scale
    l1=(b-a).length;l2=(c-b).length;direction=target_foot-a;distance=direction.length
    if distance<1e-6:return
    direction.normalize();distance=max(abs(l1-l2)+.0001,min(distance,(l1+l2)*.999))
    target_foot=a+direction*distance
    bend=b-a-direction*(b-a).dot(direction)
    if bend.length<1e-5:
        bend=Vector((0,-1,0))-direction*Vector((0,-1,0)).dot(direction)
    bend.normalize()
    along=(l1*l1-l2*l2+distance*distance)/(2*distance)
    knee=a+direction*along+bend*max(0,l1*l1-along*along)**.5
    upper_q=(b-a).normalized().rotation_difference((knee-a).normalized())@desired[upper].to_quaternion()
    lower_q=(c-b).normalized().rotation_difference((target_foot-knee).normalized())@desired[lower].to_quaternion()
    toe_head=desired[toe].translation+(target_foot-c)
    desired[upper]=Matrix.LocRotScale(a,upper_q,Vector((1,1,1)))
    desired[lower]=Matrix.LocRotScale(knee,lower_q,Vector((1,1,1)))
    desired[foot]=Matrix.LocRotScale(target_foot,desired[foot].to_quaternion(),Vector((1,1,1)))
    desired[toe]=Matrix.LocRotScale(toe_head,desired[toe].to_quaternion(),Vector((1,1,1)))
def clear_garment(desired,side,context):
    prefix='mixamorig:'+side
    upper,lower,hand=[prefix+b for b in ('Arm','ForeArm','Hand')]
    a,b,c=[desired[n].translation for n in (upper,lower,hand)]
    hips='mixamorig:Hips'
    to_rest=tr[hips]@desired[hips].inverted()
    point=to_rest@c
    def smooth(value):
        value=max(0,min(1,value));return value*value*value*(value*(value*6-15)+10)
    hip_height=tr[hips].translation.z
    weight=smooth((point.z-(hip_height-.45))/.18)*(1-smooth((point.z-(hip_height+.08))/.22))*smooth((abs(point.x)-.02)/.08)
    if weight<1e-6:return
    def radius_at(height):
        at=height/.025;lo=int(at//1);fraction=at-lo
        a=garment_sections.get(lo,.17);b=garment_sections.get(lo+1,a)
        return a+(b-a)*fraction
    radius=max(radius_at(point.z-.14+i*.02) for i in range(9))
    penetration=radius+.045-abs(point.x)
    if penetration<=0:return
    point.x+=(1 if side=='Left' else -1)*penetration*weight
    target_hand=to_rest.inverted()@point
    l1=(b-a).length;l2=(c-b).length;direction=target_hand-a;distance=direction.length
    if distance<1e-6:return
    direction.normalize();distance=max(abs(l1-l2)+.0001,min(distance,(l1+l2)*.98))
    target_hand=a+direction*distance
    side_sign=1 if side=='Left' else -1
    hint=(b-a).normalized().lerp(Vector((side_sign*.65,.35,-.7)).normalized(),weight)
    bend=hint-direction*hint.dot(direction)
    if bend.length<1e-5:return
    bend.normalize();along=(l1*l1-l2*l2+distance*distance)/(2*distance)
    elbow=a+direction*along+bend*max(0,l1*l1-along*along)**.5
    uq=(b-a).normalized().rotation_difference((elbow-a).normalized())@desired[upper].to_quaternion()
    lq=(c-b).normalized().rotation_difference((target_hand-elbow).normalized())@desired[lower].to_quaternion()
    desired[upper]=Matrix.LocRotScale(a,uq,Vector((1,1,1)))
    desired[lower]=Matrix.LocRotScale(elbow,lq,Vector((1,1,1)))
    old=desired[hand]
    body_yaw=(desired[hips].to_quaternion()@tr[hips].to_quaternion().inverted()).to_euler('XYZ').z
    hand_q=Quaternion((0,0,1),body_yaw)@context['relaxed_hands'][hand]
    hand_q=old.to_quaternion().slerp(hand_q,weight*smooth(penetration/.06))
    desired[hand]=Matrix.LocRotScale(target_hand,hand_q,Vector((1,1,1)))
clips={args.idle_source:'Human_Idle',args.greeting_source:'Human_Greeting',args.nod_source:'Human_Nod',
       args.listening_source:'Human_Listening',args.bow_source:'Human_Bow',args.walk_source:'Human_Walk'}
if len(clips)!=6:raise RuntimeError('Each requested source action must be unique.')
selected={}
for source_name in clips:
    matches=[(record,record['actions'][source_name]) for record in source_records if source_name in record['actions']]
    if not matches:raise RuntimeError(f'Animation source action not found: {source_name}')
    if len(matches)!=1:raise RuntimeError(f'Animation source action is ambiguous across motion files: {source_name}')
    selected[source_name]=matches[0]
report={'target':str(Path(args.target).resolve()),'motionSources':[record['path'] for record in source_records],
        'mappedBones':{Path(record['path']).name:len(record['mapping']) for record in source_records},
        'scale':{Path(record['path']).name:record['scale'] for record in source_records},
        'clips':[],'status':'retarget experiment; visual acceptance required'}
created=[]
relaxed_arm_cache=None
neutral_pose_cache=None
relaxed_arm_names={b.name for b in bones if b.name.startswith(('mixamorig:LeftShoulder','mixamorig:LeftArm','mixamorig:LeftForeArm','mixamorig:LeftHand','mixamorig:RightShoulder','mixamorig:RightArm','mixamorig:RightForeArm','mixamorig:RightHand'))}
for source_name,name in clips.items():
    context,action=selected[source_name]
    for record in source_records:record['rig'].animation_data.action=None
    source=context['rig'];source.animation_data.action=action
    if hasattr(action,'slots') and action.slots:source.animation_data.action_slot=action.slots[0]
    use_relaxed_arms=name in args.relaxed_arms_for
    use_head_only=name in args.head_only_for
    use_right_arm_only=name in args.right_arm_only_for
    if (use_relaxed_arms or use_head_only or use_right_arm_only) and neutral_pose_cache is None:
        idle=next((candidate for candidate in created if candidate.name=='Human_Idle'),None)
        if idle is None:raise RuntimeError('Generate Human_Idle before applying relaxed arm poses.')
        target.animation_data.action=idle
        if idle.slots:target.animation_data.action_slot=idle.slots[0]
        idle_first,idle_last=idle.frame_range
        scene.frame_set(round((idle_first+idle_last)/2));bpy.context.view_layer.update()
        neutral_pose_cache={b.name:(b.location.copy(),b.rotation_quaternion.copy(),b.scale.copy()) for b in bones}
        relaxed_arm_cache={bone_name:neutral_pose_cache[bone_name] for bone_name in relaxed_arm_names}
    start,end=action.frame_range;count=max(2,round(end-start)+1)
    output=bpy.data.actions.new(name);output.use_fake_user=True;created.append(output)
    # Blender 5 uses layered Actions with explicit slots and channel bags.
    # Keyframe insertion writes to this channel bag; Action.fcurves is gone.
    slot=output.slots.new(id_type='OBJECT',name=target.name)
    layer=output.layers.new('Motion')
    strip=layer.strips.new(type='KEYFRAME')
    bag=strip.channelbag(slot,ensure=True)
    target.animation_data_create();target.animation_data.action=output;target.animation_data.action_slot=slot
    previous={};previous_hands={}
    for i in range(count):
        scene.frame_set(int(start+i));bpy.context.view_layer.update()
        desired={}
        for bone in bones:
            rest=tr[bone.name]
            if bone.name in context['mapping']:
                pose=source.matrix_world@source.pose.bones[context['mapping'][bone.name]].matrix
                rotation=pose.to_quaternion()@context['correction'][bone.name]
            else:rotation=rest.to_quaternion()
            if bone.parent:
                parent=bone.parent.name
                head=desired[parent]@tr[parent].inverted()@rest.translation
            else:head=rest.translation.copy()
            if bone.name=='mixamorig:Hips':head+=(pose.translation-context['rest']['pelvis'].translation)*context['leg_scale']
            desired[bone.name]=Matrix.LocRotScale(head,rotation,Vector((1,1,1)))
        solve_leg(desired,'Left','l',context);solve_leg(desired,'Right','r',context)
        clear_garment(desired,'Left',context);clear_garment(desired,'Right',context)
        # Shortest-path aim near an opposite finger direction can flip the twist
        # plane. Limit the baked wrist track, rather than hiding a jump at playback.
        for hand in ('mixamorig:LeftHand','mixamorig:RightHand'):
            q=desired[hand].to_quaternion()
            if hand in previous_hands:
                q.make_compatible(previous_hands[hand]);angle=previous_hands[hand].rotation_difference(q).angle
                if angle>math.radians(240)/30:q=previous_hands[hand].slerp(q,(math.radians(240)/30)/angle)
            previous_hands[hand]=q.copy()
            desired[hand]=Matrix.LocRotScale(desired[hand].translation,q,Vector((1,1,1)))
        # These daily gestures need relaxed/open fingers, not a foreign rig's grip.
        # Inherit the target's own neutral finger shape after correcting each wrist.
        for bone in bones:
            if 'Hand' in bone.name and bone.name not in ('mixamorig:LeftHand','mixamorig:RightHand') and bone.parent:
                parent=bone.parent.name
                desired[bone.name]=desired[parent]@tr[parent].inverted()@tr[bone.name]
        for bone in bones:
            local=target.matrix_world.inverted()@desired[bone.name]
            parent_args={} if not bone.parent else {'parent_matrix':target.matrix_world.inverted()@desired[bone.parent.name],'parent_matrix_local':bone.parent.bone.matrix_local}
            bone.rotation_mode='QUATERNION'
            bone.matrix_basis=bone.bone.convert_local_to_pose(local,bone.bone.matrix_local,invert=True,**parent_args)
            neutralize=False
            if use_head_only:neutralize=bone.name not in ('mixamorig:Neck','mixamorig:Head')
            if use_right_arm_only:neutralize=not bone.name.startswith(('mixamorig:RightShoulder','mixamorig:RightArm','mixamorig:RightForeArm','mixamorig:RightHand'))
            use_idle_pose=neutralize or (use_relaxed_arms and bone.name in relaxed_arm_cache)
            if use_idle_pose and bone.name in neutral_pose_cache:
                location,rotation,scale=neutral_pose_cache[bone.name]
                bone.location=location
                rotation=rotation.copy()
                if bone.name in previous:rotation.make_compatible(previous[bone.name])
                bone.rotation_quaternion=rotation
                bone.scale=scale
            elif bone.name in previous:bone.rotation_quaternion.make_compatible(previous[bone.name])
            if name=='Human_Idle' and args.idle_breath_degrees and bone.name in ('mixamorig:Spine','mixamorig:Spine1','mixamorig:Spine2','mixamorig:Neck'):
                breath_weights={'mixamorig:Spine':.2,'mixamorig:Spine1':.35,'mixamorig:Spine2':.45,'mixamorig:Neck':-.12}
                phase=2*math.pi*i/max(1,count-1)
                axis=(desired[bone.name].to_quaternion().inverted()@Vector((1,0,0))).normalized()
                extra=Quaternion(axis,math.radians(args.idle_breath_degrees*breath_weights[bone.name])*math.sin(phase))
                bone.rotation_quaternion=(bone.rotation_quaternion@extra).normalized()
            if name=='Human_Greeting' and args.greeting_wave_degrees and bone.name=='mixamorig:RightHand':
                t=i/max(1,count-1)
                clamp01=lambda x:max(0.0,min(1.0,x))
                smooth=lambda x:(lambda y:y*y*(3-2*y))(clamp01(x))
                gate=smooth((t-.18)/.08)*(1-smooth((t-.72)/.08))
                wave_phase=2*math.pi*args.greeting_wave_cycles*clamp01((t-.22)/.50)
                axis=(desired[bone.name].to_quaternion().inverted()@Vector((0,0,1))).normalized()
                extra=Quaternion(axis,math.radians(args.greeting_wave_degrees)*gate*math.sin(wave_phase))
                bone.rotation_quaternion=(bone.rotation_quaternion@extra).normalized()
            previous[bone.name]=bone.rotation_quaternion.copy()
            for channel in ('location','rotation_quaternion','scale'):bone.keyframe_insert(channel,frame=i+1,group=bone.name)
    for curve in bag.fcurves:
        for key in curve.keyframe_points:key.interpolation='LINEAR'
    if name=='Human_Greeting' and args.greeting_time_scale!=1.0:
        if args.greeting_time_scale<=0:raise RuntimeError('Greeting time scale must be positive.')
        for curve in bag.fcurves:
            for key in curve.keyframe_points:
                key.co.x=1+(key.co.x-1)*args.greeting_time_scale
                key.handle_left.x=1+(key.handle_left.x-1)*args.greeting_time_scale
                key.handle_right.x=1+(key.handle_right.x-1)*args.greeting_time_scale
            curve.update()
    report['clips'].append({'name':name,'source':source_name,'sourceFile':context['path'],'frames':count,
                            'duration':(count-1)/30*(args.greeting_time_scale if name=='Human_Greeting' else 1.0),
                            'relaxedArms':use_relaxed_arms,'headOnly':use_head_only,'rightArmOnly':use_right_arm_only})
for obj in list(scene.objects):
    if obj not in target_objects:bpy.data.objects.remove(obj,do_unlink=True)
for action in list(bpy.data.actions):
    if action not in original_actions and action not in created:bpy.data.actions.remove(action)
target.animation_data.action=created[0]
for obj in scene.objects:obj.select_set(True)
bpy.context.view_layer.objects.active=target
scene.frame_set(1);scene.frame_start=1;scene.frame_end=91
out.parent.mkdir(parents=True,exist_ok=True)
bpy.ops.file.pack_all();bpy.ops.wm.save_as_mainfile(filepath=str(out.with_suffix('.blend')))
bpy.ops.export_scene.gltf(filepath=str(out),export_format='GLB',use_selection=True,export_animations=True,export_animation_mode='ACTIONS',export_force_sampling=True,export_frame_range=False,export_all_influences=False,export_skins=True,export_morph=False,export_yup=True)
out.with_suffix('.report.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print('Retarget experiment exported:',out)
