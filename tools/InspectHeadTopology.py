"""Read-only Blender 5 topology audit for a local GLB; never edits or saves the model."""
import argparse
import json
import sys
from collections import defaultdict
from pathlib import Path

import bpy


cli = argparse.ArgumentParser()
cli.add_argument("--input", required=True)
cli.add_argument("--report", required=True)
args = cli.parse_args(sys.argv[sys.argv.index("--") + 1 :])
source = Path(args.input).resolve()
report = Path(args.report).resolve()
if report.exists():
    raise RuntimeError("Refuse to overwrite an existing report.")

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=str(source))

objects = []
for obj in sorted((item for item in bpy.context.scene.objects if item.type == "MESH"), key=lambda item: item.name):
    mesh = obj.data
    count = len(mesh.vertices)
    parent = list(range(count))
    rank = [0] * count

    def find(value):
        while parent[value] != value:
            parent[value] = parent[parent[value]]
            value = parent[value]
        return value

    def union(first, second):
        first = find(first)
        second = find(second)
        if first == second:
            return
        if rank[first] < rank[second]:
            first, second = second, first
        parent[second] = first
        if rank[first] == rank[second]:
            rank[first] += 1

    for polygon in mesh.polygons:
        vertices = polygon.vertices
        if len(vertices) < 2:
            continue
        for index in range(1, len(vertices)):
            union(vertices[0], vertices[index])

    component_vertices = defaultdict(list)
    for vertex in mesh.vertices:
        component_vertices[find(vertex.index)].append(vertex.index)
    component_faces = defaultdict(int)
    component_materials = defaultdict(lambda: defaultdict(int))
    for polygon in mesh.polygons:
        if polygon.vertices:
            root = find(polygon.vertices[0])
            component_faces[root] += 1
            component_materials[root][str(polygon.material_index)] += 1

    head_group = next((group for group in obj.vertex_groups if group.name.casefold() == "mixamorig:head"), None)
    head_group_weights = [0.0] * count
    if head_group is not None:
        for vertex in mesh.vertices:
            for membership in vertex.groups:
                if membership.group == head_group.index:
                    head_group_weights[vertex.index] = float(membership.weight)
                    break

    components = []
    for root, indices in component_vertices.items():
        points = [obj.matrix_world @ mesh.vertices[index].co for index in indices]
        minimum = [min(point[axis] for point in points) for axis in range(3)] if points else [0.0] * 3
        maximum = [max(point[axis] for point in points) for axis in range(3)] if points else [0.0] * 3
        weights = [head_group_weights[index] for index in indices]
        positive = [weight for weight in weights if weight > 1e-5]
        components.append({
            "rootVertex": root,
            "vertices": len(indices),
            "faces": component_faces[root],
            "headWeightedVertices": len(positive),
            "headOver50PercentVertices": sum(weight > 0.5 for weight in weights),
            "headWeightMean": sum(weights) / len(weights) if weights else 0.0,
            "headWeightMeanPositive": sum(positive) / len(positive) if positive else 0.0,
            "worldBounds": {"min": minimum, "max": maximum},
            "worldCenter": [(minimum[axis] + maximum[axis]) * 0.5 for axis in range(3)],
            "facesByMaterialIndex": dict(component_materials[root]),
        })
    components.sort(key=lambda component: (-component["faces"], -component["vertices"], component["rootVertex"]))
    objects.append({
        "name": obj.name,
        "dataName": mesh.name,
        "vertices": count,
        "faces": len(mesh.polygons),
        "materialSlots": [slot.material.name if slot.material else None for slot in obj.material_slots],
        "headGroupPresent": head_group is not None,
        "topologyComponentCount": len(components),
        "components": components,
    })

result = {
    "source": str(source),
    "blenderVersion": bpy.app.version_string,
    "readOnly": True,
    "savedModel": False,
    "classificationBasis": "edge-connected polygon topology and Head vertex-group weights; no texture color, UV island, or bounding-box classifier",
    "meshObjects": objects,
}
report.parent.mkdir(parents=True, exist_ok=True)
report.write_text(json.dumps(result, ensure_ascii=False, indent=2, allow_nan=False), encoding="utf-8")
print("Read-only topology inspection complete:", report)
