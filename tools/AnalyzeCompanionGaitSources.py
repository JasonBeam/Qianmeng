"""Measure source humanoid gait before retargeting; metrics are not visual acceptance."""
import argparse
import json
import math
import sys
from pathlib import Path

import bpy
from mathutils import Vector


parser = argparse.ArgumentParser()
parser.add_argument("--motions", required=True)
parser.add_argument("--report", required=True)
parser.add_argument(
    "--actions",
    nargs="+",
    default=[
        "Walk_Female",
        "Walk_Large",
        "Walk_Stealth",
        "Run_Female",
        "Run_Anime",
        "Run_Stealth",
        "Walk",
        "Walk_Formal",
        "Walk_Carry",
        "Crouch_Walk",
    ],
)
args = parser.parse_args(sys.argv[sys.argv.index("--") + 1 :])
source_path = Path(args.motions).resolve()
report_path = Path(args.report).resolve()
if report_path.exists():
    raise RuntimeError("Choose a new report path.")

bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene
scene.render.fps = 30
bpy.ops.import_scene.gltf(filepath=str(source_path))
rig = next(obj for obj in scene.objects if obj.type == "ARMATURE")
actions = {action.name: action for action in bpy.data.actions}
bone_names = (
    "root",
    "pelvis",
    "thigh_l",
    "calf_l",
    "foot_l",
    "ball_l",
    "thigh_r",
    "calf_r",
    "foot_r",
    "ball_r",
)


def world_position(name):
    return rig.matrix_world @ rig.pose.bones[name].matrix.translation


def internal_angle(a, joint, c):
    left = (a - joint).normalized()
    right = (c - joint).normalized()
    return math.degrees(math.acos(max(-1.0, min(1.0, left.dot(right)))))


results = []
for action_name in args.actions:
    action = actions.get(action_name)
    if action is None:
        results.append({"action": action_name, "available": False})
        continue
    rig.animation_data_create()
    rig.animation_data.action = action
    if action.slots:
        rig.animation_data.action_slot = action.slots[0]
    first, last = map(int, action.frame_range)
    samples = []
    for frame in range(first, last + 1):
        scene.frame_set(frame)
        bpy.context.view_layer.update()
        positions = {name: world_position(name) for name in bone_names if name in rig.pose.bones}
        row = {name: list(point) for name, point in positions.items()}
        for side in ("l", "r"):
            hip = positions.get("thigh_" + side)
            knee = positions.get("calf_" + side)
            ankle = positions.get("foot_" + side)
            if hip is not None and knee is not None and ankle is not None:
                row["knee_flex_" + side] = 180.0 - internal_angle(hip, knee, ankle)
        samples.append(row)

    def vector_field(field, side=None):
        name = field if side is None else field + "_" + side
        return [Vector(sample[name]) for sample in samples if name in sample]

    def span(values):
        if not values:
            return None
        return [min(values), max(values)]

    def summarize_track(field, side=None):
        values = vector_field(field, side)
        if not values:
            return None
        speeds = [0.0]
        speeds.extend((b - a).length * 30.0 for a, b in zip(values, values[1:]))
        return {
            "first": list(values[0]),
            "last": list(values[-1]),
            "axisRangeMeters": [
                span([value[axis] for value in values]) for axis in range(3)
            ],
            "speedMetersPerSecond": {
                "median": sorted(speeds)[len(speeds) // 2],
                "p90": sorted(speeds)[min(len(speeds) - 1, int(len(speeds) * 0.9))],
                "maximum": max(speeds),
            },
        }

    knee_tracks = {
        side: [sample["knee_flex_" + side] for sample in samples if "knee_flex_" + side in sample]
        for side in ("l", "r")
    }
    contact_hints = {}
    for side in ("l", "r"):
        ankle_name = "foot_" + side
        toe_name = "ball_" + side
        if ankle_name not in samples[0] or toe_name not in samples[0]:
            continue
        ankles = [Vector(sample[ankle_name]) for sample in samples]
        toes = [Vector(sample[toe_name]) for sample in samples]
        soles = [min(ankle.z, toe.z) for ankle, toe in zip(ankles, toes)]
        horizontal_speeds = [0.0]
        vertical_speeds = [0.0]
        for previous, current in zip(ankles, ankles[1:]):
            delta = current - previous
            horizontal_speeds.append(math.hypot(delta.x, delta.y) * 30.0)
            vertical_speeds.append(abs(delta.z) * 30.0)
        threshold = min(soles) + 0.035
        planted = [
            height <= threshold and hspeed <= 0.30 and vspeed <= 0.24
            for height, hspeed, vspeed in zip(soles, horizontal_speeds, vertical_speeds)
        ]
        intervals = []
        interval_start = None
        for index, is_planted in enumerate(planted + [False]):
            if is_planted and interval_start is None:
                interval_start = index
            elif not is_planted and interval_start is not None:
                if index - interval_start >= 3:
                    intervals.append([first + interval_start, first + index - 1])
                interval_start = None
        contact_hints[side] = {
            "heuristic": "ankle low relative to clip minimum plus horizontal/vertical speed thresholds; inspect visually",
            "horizontalSpeedThresholdMetersPerSecond": 0.30,
            "verticalSpeedThresholdMetersPerSecond": 0.24,
            "soleHeightThresholdMeters": threshold,
            "estimatedPlantedWindows": intervals,
            "samples": [
                {
                    "frame": first + index,
                    "ankle": list(ankle),
                    "toe": list(toe),
                    "soleZ": soles[index],
                    "horizontalSpeedMetersPerSecond": horizontal_speeds[index],
                    "verticalSpeedMetersPerSecond": vertical_speeds[index],
                    "plantedHint": planted[index],
                }
                for index, (ankle, toe) in enumerate(zip(ankles, toes))
            ],
        }
    results.append(
        {
            "action": action_name,
            "available": True,
            "frames": [first, last],
            "durationSeconds": (last - first) / 30.0,
            "root": summarize_track("root"),
            "pelvis": summarize_track("pelvis"),
            "contactHints": contact_hints,
            "feet": {
                side: {
                    "ankle": summarize_track("foot", side),
                    "toe": summarize_track("ball", side),
                    "kneeFlexDegrees": span(knee_tracks[side]),
                }
                for side in ("l", "r")
            },
        }
    )

report = {
    "source": str(source_path),
    "blender": bpy.app.version_string,
    "fps": 30,
    "results": results,
    "scope": "source rig position, speed and knee-flex measurements; no retarget or visual acceptance",
}
report_path.parent.mkdir(parents=True, exist_ok=True)
report_path.write_text(json.dumps(report, indent=2), encoding="utf-8")
print("Source gait measurements written:", report_path)
