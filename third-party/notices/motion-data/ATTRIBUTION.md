# Motion data used in the local character build

These credits cover the motion sources listed below. They do not grant rights
to the user-provided character mesh, textures, or its pre-existing Idle/Running
clips. The user character and generated model remain local build assets.

## Standing gestures

Human_Idle, Human_Greeting, Human_Nod, and Human_Listening derive from the
Mesh2Motion humanoid animation assets (Idle_Subtle, Greeting, Head Nod, and
Idle Listening). Mesh2Motion / Scott Petrovic; some human animations originate
from Quaternius. Art and animations are dedicated under CC0 1.0.

Sources: https://mesh2motion.org/ and
https://github.com/Mesh2Motion/mesh2motion-app

The local source checkpoint was 653a9698f5f315523072f1c3ff3496100401317b.
Changes include skeleton retargeting, garment-aware hand placement, timing,
standing posture, planted soles, native-length leg calibration, and breathing.
The application does not bundle the Mesh2Motion web application.
See CC0-1.0.txt.

## Human_Run

The 100STYLE Dataset — Ian Mason, Sebastian Starke and Taku Komura (2022).
Source: https://www.ianxmason.com/100style/
Original recording: Neutral_FR.bvh, segment 497–546 at 60 Hz.
License: Creative Commons Attribution 4.0 International (CC BY 4.0).
https://creativecommons.org/licenses/by/4.0/

Changes: retargeting onto the user's native skeleton, removal of horizontal
travel for an in-place cycle, resampling, native leg-length and bend-plane
adaptation, sole calibration, and loop-boundary adjustments.

Reference publication: Mason, Starke and Komura, “Real-Time Style Modelling of
Human Locomotion via Feature-Wise Transformations and Local Motion Phases”,
Proceedings of the ACM on Computer Graphics and Interactive Techniques 5(1),
2022, DOI https://doi.org/10.1145/3522618 . See CC-BY-4.0.txt.

## Human_Walk

HOLD: Humanoid Omniskill Loco-manipulation Dataset — RAI Institute and
Boston Dynamics (2026).
Source: https://github.com/rai-opensource/hold
Original recording: S0_normal_walk_0.bvh, segment 7841–7973 at 120 Hz.
License: Creative Commons Attribution 4.0 International (CC BY 4.0).
https://creativecommons.org/licenses/by/4.0/

Changes: skeleton retargeting, in-place heading and gravity alignment, native
leg-length adaptation, stable anatomical knee plane, world-space stance
calibration, contact-aware trajectory optimization, resampling to 30 Hz, and
loop-boundary adjustments. These are derived motions, not unmodified source
recordings. See CC-BY-4.0.txt.

The original authors do not endorse this application or the derived character.

## Rebuilt hand geometry candidate (2026-10-25, not yet accepted)

The isolated v109/v110/v111 hand-repair experiments also use the CC0
Mannequin.001 hand geometry and anatomical joint locations from the existing
Mesh2Motion human-addon-animations.glb reference asset at the source checkpoint
listed above. Changes include continuous skin reconstruction of the separate
mechanical sections, density reduction, fitting only the hands, explicit wrist
boundary bridging, finger-chain rebinding, and transfer of the user character's
local skin appearance. This does not grant rights to the user's original mesh
or textures. It does not imply endorsement, acceptance or delivery of a candidate.
No orangeduck/Ubisoft animation database or restricted motion data are included.

## Upper-body gesture candidates (2026-10-25, not yet accepted)

Human_HeadScratch is derived from Confused in the existing Mesh2Motion
human-addon-animations.glb asset. Human_DoubleWave is derived from
Help_Two_Arms in human-mocap-animations.glb. Both use the CC0 art assets at the
source checkpoint recorded above. The mocap filename alone does not establish
capture method or naturalness; the second gesture is an overhead signaling
reference. Changes include upper-body rest-relative retargeting, target idle
lower-body sampling, entry/exit blending, time scaling, target-relative finger
neutral alignment and contact-trajectory-preserving wrist adaptation for the
head gesture. The original eight clips and source assets are retained. These
isolated candidates have not passed the complete product acceptance gate.
