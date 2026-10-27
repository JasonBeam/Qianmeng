"""Measure pose and velocity seams across candidate loop windows in a local GLB."""
import argparse,json,math,sys
from pathlib import Path
import bpy
from mathutils import Quaternion,Vector

cli=argparse.ArgumentParser()
cli.add_argument('--input',required=True)
cli.add_argument('--action',required=True)
cli.add_argument('--report',required=True)
cli.add_argument('--min-frames',type=int,default=12)
args=cli.parse_args(sys.argv[sys.argv.index('--')+1:])
source=Path(args.input).resolve();report=Path(args.report).resolve()
if report.exists():raise RuntimeError('Choose a new report path.')
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.context.scene.render.fps=30
bpy.ops.import_scene.gltf(filepath=str(source))
rig=next(o for o in bpy.context.scene.objects if o.type=='ARMATURE')
action=next(a for a in bpy.data.actions if a.name==args.action)
rig.animation_data_create();rig.animation_data.action=action
if action.slots:rig.animation_data.action_slot=action.slots[0]
scene=bpy.context.scene;fps=scene.render.fps/scene.render.fps_base
start,end=action.frame_range
bones=[n for n in ('mixamorig:Hips','mixamorig:Spine','mixamorig:Head',
 'mixamorig:LeftArm','mixamorig:LeftForeArm','mixamorig:LeftHand',
 'mixamorig:RightArm','mixamorig:RightForeArm','mixamorig:RightHand',
 'mixamorig:LeftUpLeg','mixamorig:LeftLeg','mixamorig:LeftFoot','mixamorig:LeftToeBase',
 'mixamorig:RightUpLeg','mixamorig:RightLeg','mixamorig:RightFoot','mixamorig:RightToeBase') if n in rig.pose.bones]
frames=[];first=int(start);last=int(end)
for frame in range(first,last+1):
    scene.frame_set(frame);bpy.context.view_layer.update()
    pose={}
    for name in bones:
        matrix=rig.matrix_world@rig.pose.bones[name].matrix
        pose[name]={'p':list(matrix.translation),'q':list(matrix.to_quaternion())}
    frames.append(pose)
def get(i):return frames[max(0,min(len(frames)-1,i-first))]
def metrics(a,b):
    pos=[];rot=[]
    for name in bones:
        pa=Vector(get(a)[name]['p']);pb=Vector(get(b)[name]['p'])
        qa=Quaternion(get(a)[name]['q']).normalized();qb=Quaternion(get(b)[name]['q']).normalized()
        # q and -q encode the same rotation. Use the shortest sign-invariant
        # geodesic so a wrap seam cannot be misreported as a ~360-degree jump.
        dot=max(0.0,min(1.0,abs(qa.dot(qb))))
        angle=2*math.acos(dot)
        pos.append((name,(pb-pa).length));rot.append((name,angle))
    return pos,rot
def rms(values):return (sum(v*v for _,v in values)/max(1,len(values)))**.5
ranked=[]
min_len=max(2,args.min_frames)
for a in range(first,last-min_len+1):
    for b in range(a+min_len,last+1):
        p,r=metrics(a,b)
        p0,r0=metrics(a,a+1);p1,r1=metrics(b-1,b)
        pv=[(n,v0-v1) for (n,v0),(_,v1) in zip(p0,p1)]
        rv=[(n,v0-v1) for (n,v0),(_,v1) in zip(r0,r1)]
        positionRms=rms(p);rotationRms=rms(r);velocityRms=rms(pv);angularVelocityRms=rms(rv)
        ranked.append({'startFrame':a,'endFrame':b,'durationSeconds':(b-a)/fps,
          'positionRmsMeters':positionRms,'positionMaxMeters':max(v for _,v in p),
          'rotationRmsRadians':rotationRms,'rotationMaxDegrees':max(v for _,v in r)*180/3.141592653589793,
          'linearVelocitySeamRmsMetersPerFrame':velocityRms,
          'angularVelocitySeamRmsRadiansPerFrame':angularVelocityRms,
          'score':positionRms+.12*rotationRms+.50*velocityRms+.15*angularVelocityRms,
          'largestPositionSeams':sorted(({'bone':n,'meters':v} for n,v in p),key=lambda x:x['meters'],reverse=True)[:5],
          'largestRotationSeams':sorted(({'bone':n,'degrees':v*180/3.141592653589793} for n,v in r),key=lambda x:x['degrees'],reverse=True)[:5]})
ranked.sort(key=lambda row:row['score'])
result={'source':str(source),'action':args.action,'blender':bpy.app.version_string,'fps':fps,
 'frameRange':[first,last],'sampledFrames':len(frames),'minimumLoopFrames':min_len,
 'currentFullClip':next((x for x in ranked if x['startFrame']==first and x['endFrame']==last),None),
 'bestWindows':ranked[:20],
 'footTrajectory':[{'frame':first+i,
   **{side:{'position':get(first+i)[bone]['p'],
      'speedMetersPerSecond':0.0 if i==0 else
      (Vector(get(first+i)[bone]['p'])-Vector(get(first+i-1)[bone]['p'])).length*fps}
     for side,bone in (('left','mixamorig:LeftFoot'),('right','mixamorig:RightFoot')) if bone in bones}}
   for i in range(len(frames))],
 'scope':'sampled bone-pose and endpoint velocity continuity; does not certify visible foot locking or collision'}
report.parent.mkdir(parents=True,exist_ok=True);report.write_text(json.dumps(result,indent=2),encoding='utf-8')
print('Loop analysis written:',report)
