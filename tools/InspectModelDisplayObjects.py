"""Read-only Blender 5 GLB inspection. Imports a model and writes metadata only; never saves a blend or GLB."""
import argparse
import json
import math
import sys
from pathlib import Path

import bpy


cli = argparse.ArgumentParser()
cli.add_argument("--input", required=True)
cli.add_argument("--report", required=True)
args = cli.parse_args(sys.argv[sys.argv.index("--") + 1 :])
source = Path(args.input).resolve()
report = Path(args.report).resolve()
if report.exists():
    raise RuntimeError("Refusing to overwrite an existing report.")


def vec(value):
    return [float(value.x), float(value.y), float(value.z)]


def finite(value):
    return all(math.isfinite(float(component)) for component in value)


def image_info(image):
    return {
        "name": image.name,
        "filepath": image.filepath,
        "size": list(image.size),
        "hasData": bool(image.has_data),
        "packed": bool(image.packed_file),
        "colorspace": image.colorspace_settings.name,
    }


bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=str(source))
scene = bpy.context.scene
scene.frame_set(1)
depsgraph = bpy.context.evaluated_depsgraph_get()

mesh_objects = []
for obj in sorted((item for item in scene.objects if item.type == "MESH"), key=lambda item: item.name):
    mesh = obj.data
    evaluated_object = obj.evaluated_get(depsgraph)
    evaluated_mesh = evaluated_object.to_mesh(preserve_all_data_layers=True, depsgraph=depsgraph)
    try:
        world_points = [evaluated_object.matrix_world @ vertex.co for vertex in evaluated_mesh.vertices]
        bounds_min = [min(point[axis] for point in world_points) for axis in range(3)] if world_points else [0, 0, 0]
        bounds_max = [max(point[axis] for point in world_points) for axis in range(3)] if world_points else [0, 0, 0]
        group_by_index = {group.index: group.name for group in obj.vertex_groups}
        focus_groups = {}
        for name in ("mixamorig:Head", "mixamorig:Neck", "mixamorig:RightHand", "mixamorig:RightHandIndex1",
                     "mixamorig:RightHandIndex2", "mixamorig:RightHandIndex3", "mixamorig:RightHandMiddle1",
                     "mixamorig:RightHandMiddle2", "mixamorig:RightHandMiddle3"):
            group = next((group for group in obj.vertex_groups if group.name.casefold() == name.casefold()), None)
            if group is None:
                focus_groups[name] = {"present": False}
                continue
            weights = []
            for vertex in mesh.vertices:
                try:
                    weights.append(vertex.groups[next(i for i, membership in enumerate(vertex.groups) if membership.group == group.index)].weight)
                except StopIteration:
                    weights.append(0.0)
            positive = [weight for weight in weights if weight > 1e-6]
            focus_groups[name] = {
                "present": True,
                "weightedVertices": len(positive),
                "over50Percent": sum(weight > 0.5 for weight in weights),
                "maxWeight": max(weights, default=0.0),
                "meanPositiveWeight": sum(positive) / len(positive) if positive else 0.0,
            }

        normals = [vertex.normal[:] for vertex in evaluated_mesh.vertices]
        polygon_normals = [polygon.normal[:] for polygon in evaluated_mesh.polygons]
        materials = []
        for slot in obj.material_slots:
            material = slot.material
            if material is None:
                materials.append(None)
                continue
            nodes = []
            if material.use_nodes and material.node_tree:
                for node in material.node_tree.nodes:
                    record = {"type": node.type, "name": node.name}
                    if node.type == "TEX_IMAGE" and node.image:
                        record["image"] = image_info(node.image)
                    if node.type == "BSDF_PRINCIPLED":
                        record["baseColor"] = list(node.inputs["Base Color"].default_value)
                        if "Normal" in node.inputs:
                            record["normalLinked"] = bool(node.inputs["Normal"].is_linked)
                        if "Metallic" in node.inputs:
                            record["metallic"] = float(node.inputs["Metallic"].default_value)
                        if "Roughness" in node.inputs:
                            record["roughness"] = float(node.inputs["Roughness"].default_value)
                    nodes.append(record)
            materials.append({
                "name": material.name,
                "useNodes": bool(material.use_nodes),
                "diffuseColor": list(material.diffuse_color),
                "nodes": nodes,
            })

        mesh_objects.append({
            "name": obj.name,
            "dataName": mesh.name,
            "parent": obj.parent.name if obj.parent else None,
            "parentType": obj.parent_type,
            "modifiers": [{"name": modifier.name, "type": modifier.type,
                           "object": modifier.object.name if hasattr(modifier, "object") and modifier.object else None}
                          for modifier in obj.modifiers],
            "vertices": len(mesh.vertices),
            "triangles": sum(max(0, len(polygon.vertices) - 2) for polygon in mesh.polygons),
            "vertexGroups": [group.name for group in obj.vertex_groups],
            "focusGroups": focus_groups,
            "worldBounds": {"min": bounds_min, "max": bounds_max},
            "worldCenter": [(bounds_min[i] + bounds_max[i]) * 0.5 for i in range(3)],
            "materialSlots": materials,
            "vertexNormalCount": len(normals),
            "vertexNormalNonFinite": sum(not finite(normal) for normal in normals),
            "vertexNormalZero": sum(sum(component * component for component in normal) < 1e-12 for normal in normals),
            "vertexNormalLengthMin": min((math.sqrt(sum(component * component for component in normal)) for normal in normals), default=0.0),
            "vertexNormalLengthMax": max((math.sqrt(sum(component * component for component in normal)) for normal in normals), default=0.0),
            "polygonNormalCount": len(polygon_normals),
            "polygonNormalNonFinite": sum(not finite(normal) for normal in polygon_normals),
            "polygonNormalZero": sum(sum(component * component for component in normal) < 1e-12 for normal in polygon_normals),
        })
    finally:
        evaluated_object.to_mesh_clear()

armatures = []
for rig in sorted((item for item in scene.objects if item.type == "ARMATURE"), key=lambda item: item.name):
    armatures.append({
        "name": rig.name,
        "bones": [{"name": bone.name, "parent": bone.parent.name if bone.parent else None,
                   "headWorld": vec(rig.matrix_world @ bone.head_local),
                   "tailWorld": vec(rig.matrix_world @ bone.tail_local)} for bone in rig.data.bones],
    })

result = {
    "source": str(source),
    "blenderVersion": bpy.app.version_string,
    "readOnly": True,
    "savedModel": False,
    "meshObjects": mesh_objects,
    "armatures": armatures,
    "actions": [action.name for action in bpy.data.actions],
}
report.parent.mkdir(parents=True, exist_ok=True)
report.write_text(json.dumps(result, ensure_ascii=False, indent=2, allow_nan=False), encoding="utf-8")
print("Read-only model inspection complete:", report)
