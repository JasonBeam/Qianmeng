"""Compare two local GLBs' bind geometry in both directions with Blender's KD tree."""
import argparse,json,sys
from pathlib import Path
import bpy
from mathutils.kdtree import KDTree

cli=argparse.ArgumentParser()
cli.add_argument('--before',required=True);cli.add_argument('--after',required=True);cli.add_argument('--report',required=True)
args=cli.parse_args(sys.argv[sys.argv.index('--')+1:])
report=Path(args.report)
if report.exists():raise RuntimeError('Choose a new report path.')
def read(path):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=str(Path(path).resolve()))
    points=[];triangles=0
    for obj in bpy.context.scene.objects:
        if obj.type!='MESH':continue
        points.extend(obj.matrix_world@v.co for v in obj.data.vertices)
        triangles+=sum(len(p.vertices)-2 for p in obj.data.polygons)
    tree=KDTree(len(points))
    for i,p in enumerate(points):tree.insert(p,i)
    tree.balance()
    return points,tree,triangles
before,old_tree,old_triangles=read(args.before)
after,new_tree,new_triangles=read(args.after)
forward=max(new_tree.find(p)[2] for p in before)
backward=max(old_tree.find(p)[2] for p in after)
result={'before':str(Path(args.before).resolve()),'after':str(Path(args.after).resolve()),'beforeVertices':len(before),'afterVertices':len(after),'beforeTriangles':old_triangles,'afterTriangles':new_triangles,'maxBeforeToAfter':forward,'maxAfterToBefore':backward,'passed':max(forward,backward)<1e-5 and old_triangles==new_triangles,'scope':'bind geometry only; does not certify skin weights, textures, motion or collision'}
report.parent.mkdir(parents=True,exist_ok=True);report.write_text(json.dumps(result,indent=2),encoding='utf-8')
print(json.dumps(result))
if not result['passed']:sys.exit(1)
