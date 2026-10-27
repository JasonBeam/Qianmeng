"""Read a local GLB in Blender and save rig/weight metadata without changing it."""
import argparse, json, sys
from pathlib import Path
import bpy

cli=argparse.ArgumentParser()
cli.add_argument('--input',required=True)
cli.add_argument('--report',required=True)
args=cli.parse_args(sys.argv[sys.argv.index('--')+1:])
source=Path(args.input).resolve()
report=Path(args.report).resolve()
if report.exists(): raise RuntimeError('Use a new report path.')
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=str(source))
rigs=[o for o in bpy.context.scene.objects if o.type=='ARMATURE']
result={'source':str(source),'blender':bpy.app.version_string,'actions':[a.name for a in bpy.data.actions],'rigs':[]}
for rig in rigs:
    if rig.animation_data:
        for track in rig.animation_data.nla_tracks: track.mute=True
        idle=next((a for a in bpy.data.actions if 'idle' in a.name.lower()),None)
        if idle:
            rig.animation_data.action=idle
            if hasattr(idle,'slots') and idle.slots: rig.animation_data.action_slot=idle.slots[0]
    bpy.context.scene.frame_set(1)
    bones=[]
    for b in rig.pose.bones:
        world=rig.matrix_world@b.matrix
        bones.append({'name':b.name,'parent':b.parent.name if b.parent else None,
                      'head':list(rig.matrix_world@b.head),'tail':list(rig.matrix_world@b.tail),
                      'axes':[list(world.to_3x3().col[i].normalized()) for i in range(3)]})
    result['rigs'].append({'name':rig.name,'bones':bones})
result['meshes']=[]
for mesh in (o for o in bpy.context.scene.objects if o.type=='MESH'):
    weights=[sum(g.weight for g in v.groups) for v in mesh.data.vertices]
    result['meshes'].append({'name':mesh.name,'vertices':len(mesh.data.vertices),
                            'unweighted':sum(w<1e-6 for w in weights),
                            'overFourInfluences':sum(len(v.groups)>4 for v in mesh.data.vertices)})
report.parent.mkdir(parents=True,exist_ok=True)
report.write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
print('Rig inspection complete:',report)
