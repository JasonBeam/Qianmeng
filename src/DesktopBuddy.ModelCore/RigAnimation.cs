using System.Numerics;

namespace DesktopBuddy.ModelCore;

public readonly record struct Joint4(int X, int Y, int Z, int W)
{
    public int this[int i] => i switch { 0=>X, 1=>Y, 2=>Z, _=>W };
}
public readonly record struct NodePose(Vector3 Translation, Quaternion Rotation, Vector3 Scale)
{
    public Matrix4x4 Matrix => Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Translation);
    public static NodePose Blend(NodePose a, NodePose b, float t) => new(Vector3.Lerp(a.Translation,b.Translation,t),Quaternion.Slerp(a.Rotation,b.Rotation,t),Vector3.Lerp(a.Scale,b.Scale,t));
}
public sealed record RigNode(string Name, int Parent, NodePose Rest);
public sealed record RigSkin(int[] Joints, Matrix4x4[] InverseBind);
public sealed record RigMesh(int Node, int Skin, Matrix4x4 InverseMeshWorld, Joint4[] Joints, Vector4[] Weights);
public sealed record RigTrack(int Node, string Path, bool Step, float[] Times, Vector4[] Values);
public sealed record RigClip(string Name, float Duration, RigTrack[] Tracks);
public sealed record RigAnimation(RigNode[] Nodes, int[] Order, RigSkin[] Skins, RigMesh[] Meshes, RigClip[] Clips);

/// <summary>Bounded local glTF skeletal sampling. Reuses all per-frame buffers; no WPF dependency.</summary>
public sealed class RigSampler
{
    private readonly record struct FingerSurfaceSample(int Mesh,int Vertex,float Influence,float FingerChainInfluence,float StrongestOtherFingerInfluence);
    private readonly record struct HeadSurfaceTriangle(int Mesh,int A,int B,int C);
    private readonly record struct HeadSurfaceProjection(Vector3 Point,Vector3 Normal,Vector3 RawNormal,float RawOutwardDot,float Distance,HeadSurfaceTriangle Triangle,bool UsedPreviousSamplePositions)
    {
        public Vector3 InterpolatedNormal { get; init; }
        public Vector3 Barycentric { get; init; }
        public float InterpolatedOutwardDot { get; init; }
        public float FaceInterpolatedNormalDot { get; init; }
        public int PatchIndex { get; init; }=-1;
        public Vector3 OneRingNormal { get; init; }
        public int OneRingTriangleCount { get; init; }
        public float FaceOneRingNormalDot { get; init; }
    }
    private readonly ModelAsset _asset;
    private readonly RigAnimation _rig;
    private readonly int _nativeRunningClip;
    private readonly NodePose[] _pose, _from, _transitionPose;
    private bool _hasSample;
    public float PoseTransitionProgress { get; set; } = 1;
    private readonly int _head, _neck, _hips, _spine, _spine1, _spine2;
    private readonly int _leftShoulder, _leftArm, _leftForeArm, _leftHand;
    private readonly int _rightShoulder, _rightArm, _rightForeArm, _rightHand;
    private readonly int _leftUpLeg, _leftLeg, _rightUpLeg, _rightLeg;
    private readonly int _leftFoot, _rightFoot, _leftToe, _rightToe;
    private readonly int _leftMiddle, _leftIndex, _leftPinky, _rightMiddle, _rightIndex, _rightPinky;
    public bool SupportsCompanionGestures => _head >= 0;
    public bool SupportsCompanionActions => _head >= 0 || _hips >= 0 || _spine >= 0 || _leftArm >= 0 || _rightArm >= 0 || _leftUpLeg >= 0 || _rightUpLeg >= 0;
    public float CompanionWaveSwing { get; set; }
    public Vector3 CompanionGesture { get; set; } // head yaw/pitch and arm raise (radians / 0..1)
    public Vector3 CompanionBodyMotion { get; set; } // side sway, forward lean, torso twist (radians)
    public CompanionActionPose CompanionAction { get; set; }
    public CompanionActionPose CompanionInteractionAction { get; set; }
    public bool CompanionActionOverride { get; set; }
    public Vector3 CompanionIdleBody { get; set; }
    public Vector3 CompanionIdleHead { get; set; }
    public float CompanionBreathing { get; set; }
    private readonly Matrix4x4[] _world;
    private readonly Matrix4x4[][] _skin, _normal;
    private readonly Vector3[][] _localPositions, _localNormals;
    private readonly (int Mesh,int Vertex)[] _footSupportVertices;
    private HeadSurfaceTriangle[] _headSurfacePatch=[];
    private int[][] _headSurfacePatchOneRingIndices=[];
    private int[][][] _headSurfacePatchNeighborhoods=[];
    private int _previousHeadSurfaceContactPatchIndex=-1;
    private Vector3 _previousRateLimitedHeadSurfaceNormal;
    private int _previousRateLimitedHeadSurfaceNormalClip=-1;
    private double _previousRateLimitedHeadSurfaceNormalSeconds=double.NaN;
    private Vector3 _currentRateLimitedHeadSurfaceNormal;
    private int _currentRateLimitedHeadSurfaceNormalClip=-1;
    private double _currentRateLimitedHeadSurfaceNormalSeconds=double.NaN;
    private bool _hasPreviousRateLimitedHeadSurfaceNormal;
    private bool _hasCurrentRateLimitedHeadSurfaceNormal;
    private float _headSurfaceSeedDistanceMeters=float.NaN;
    private readonly float _restSoleFloor;
    private readonly int[][][] _handDigits;
    private readonly FingerSurfaceSample[][][] _fingertipSurfaceSamples;
    private readonly float[] _previousFingertipPalmRoll=new float[2];
    private readonly float[] _previousFingertipPalmRollApplied=new float[2];
    private readonly bool[] _hasPreviousFingertipPalmRoll=new bool[2];
    private int _lastPalmRollClip=-1;
    private double _lastPalmRollSampleSeconds=double.NaN;
    private float _palmRollSampleDeltaSeconds=1f/30f;
    private bool _lastRequiresSoleSupport, _transitionRequiresSoleSupport;
    private float _transitionSoleFloor;
    public float SoleSupportCorrectionMeters { get; private set; }
    public float LeftFingertipContactErrorMeters { get; private set; }=float.NaN;
    public float RightFingertipContactErrorMeters { get; private set; }=float.NaN;
    public Vector3 LeftFingertipContactTargetWorld { get; private set; }=new(float.NaN);
    public Vector3 RightFingertipContactTargetWorld { get; private set; }=new(float.NaN);
    public Vector3 LeftFingertipSurfaceCenterWorld { get; private set; }=new(float.NaN);
    public Vector3 RightFingertipSurfaceCenterWorld { get; private set; }=new(float.NaN);
    public Vector3 RightIndexFingertipSurfaceCenterWorld { get; private set; }=new(float.NaN);
    public Vector3 RightMiddleFingertipSurfaceCenterWorld { get; private set; }=new(float.NaN);
    public Vector3 RightHeadSurfacePointWorld { get; private set; }=new(float.NaN);
    public Vector3 RightHeadSurfaceNormalWorld { get; private set; }=new(float.NaN);
    public Vector3 RightHeadSurfaceContactTargetWorld { get; private set; }=new(float.NaN);
    public Vector3 RightHeadSurfaceRawNormalWorld { get; private set; }=new(float.NaN);
    public Vector3 RightHeadSurfaceContactFaceNormalWorld { get; private set; }=new(float.NaN);
    public Vector3 RightHeadSurfaceInterpolatedNormalWorld { get; private set; }=new(float.NaN);
    public Vector3 RightHeadSurfaceOneRingNormalWorld { get; private set; }=new(float.NaN);
    public Vector3 RightHeadSurfaceContactBarycentric { get; private set; }=new(float.NaN);
    public float RightHeadSurfaceInterpolatedOutwardDot { get; private set; }=float.NaN;
    public float RightHeadSurfaceFaceInterpolatedNormalDot { get; private set; }=float.NaN;
    public float RightHeadSurfaceFaceOneRingNormalDot { get; private set; }=float.NaN;
    public int RightHeadSurfaceOneRingTriangleCount { get; private set; }
    public float RightHeadSurfaceReachUpProjectionLength { get; private set; }=float.NaN;
    public float RightHeadSurfaceFingerUpProjectionLength { get; private set; }=float.NaN;
    public Vector3 RightHeadSurfacePalmNormalWorld { get; private set; }=new(float.NaN);
    public int RightHeadSurfaceContactMesh { get; private set; }=-1;
    public int RightHeadSurfaceContactTriangleA { get; private set; }=-1;
    public int RightHeadSurfaceContactTriangleB { get; private set; }=-1;
    public int RightHeadSurfaceContactTriangleC { get; private set; }=-1;
    public float RightHeadSurfaceRawOutwardDot { get; private set; }=float.NaN;
    public float RightHeadSurfaceFacePalmAlignmentDot { get; private set; }=float.NaN;
    public float RightHeadSurfaceSeedDistanceMeters { get; private set; }=float.NaN;
    public float RightHeadSurfaceSignedClearanceMeters { get; private set; }=float.NaN;
    public float RightHeadSurfaceFaceSignedClearanceMeters { get; private set; }=float.NaN;
    public float RightHeadSurfacePalmAlignmentDot { get; private set; }=float.NaN;
    public bool RightHeadSurfaceContactActive { get; private set; }
    public int HeadSurfacePatchTriangleCount=>_headSurfacePatch.Length;
    public bool RightHeadSurfaceTopologySearchRestricted { get; private set; }
    public bool RightHeadSurfaceTopologySearchSucceeded { get; private set; }
    public int RightHeadSurfaceTopologyCandidateCount { get; private set; }
    public int LeftFingertipCorrectionPass { get; private set; }=-1;
    public int RightFingertipCorrectionPass { get; private set; }=-1;
    public float RightFingertipCorrectionGate { get; private set; }=float.NaN;
    public float RightFingertipIndexFraction { get; private set; }=float.NaN;
    public float RightFingertipPreAimAlignmentDot { get; private set; }=float.NaN;
    public float RightFingertipPalmRollAngleRadians { get; private set; }=float.NaN;
    public float RightFingertipAppliedPalmRollRadians { get; private set; }=float.NaN;
    public float RightFingertipPalmForearmAppliedRadians { get; private set; }=float.NaN;
    public float RightFingertipPalmWristAppliedRadians { get; private set; }=float.NaN;
    public float RightFingertipPalmRollDiagnosticOffsetRadians { get; private set; }
    public float RightFingertipPalmNormalDot { get; private set; }=float.NaN;
    public float RightFingertipPalmRollPrincipalRadians { get; private set; }=float.NaN;
    public Vector3 RightFingertipDesiredPalmWorld { get; private set; }=new(float.NaN);
    public Vector3 RightFingertipPalmNormalPreWristRollWorld { get; private set; }=new(float.NaN);
    public Vector3 RightFingertipPalmNormalPostWristRollWorld { get; private set; }=new(float.NaN);
    public int RightIndexFingertipSurfaceSampleCount=>_fingertipSurfaceSamples[1][0].Length;
    public int RightMiddleFingertipSurfaceSampleCount=>_fingertipSurfaceSamples[1][1].Length;
    public bool RightFingertipContactSampleAvailable { get; private set; }
    public float PalmRollLimitRadians { get; set; }=1.15f;
    public float PalmRollRateRadiansPerSecond { get; set; }=2.8f;
    public float PalmRollForearmShare { get; set; }=0f;
    public float PalmRollWristLimitRadians { get; set; }=1.15f;
    private float _fingertipPalmRollDiagnosticOffsetRadians;
    public float FingertipPalmRollDiagnosticOffsetRadians
    {
        get=>_fingertipPalmRollDiagnosticOffsetRadians;
        set=>_fingertipPalmRollDiagnosticOffsetRadians=float.IsFinite(value)?Math.Clamp(value,-.15f,.15f):0f;
    }
    // Diagnostic A/B switch. Runtime uses the current sampled pose so a moving head does not
    // leave the hand solver chasing yesterday's rendered head surface by one frame.
    public bool UsePreviousHeadSurfaceSamplePositions { get; set; }=false;
    // Test-only A/B: preserve shoulder-wrist radius while blending the full-chain target.
    // The production/default path remains Cartesian Lerp unless a diagnostic explicitly opts in.
    public bool UseRadialHandTargetBlendForDiagnostic { get; set; }=false;
    // Opt-in report-only diagnostic. It compares the triangle face normal with
    // the renderer-equivalent skinned vertex normal interpolated at the projection.
    public bool TraceHeadSurfaceSmoothNormals { get; set; }=false;
    // Opt-in solver A/B. Uses the interpolated renderer-equivalent normal for
    // contact-frame direction while preserving triangle geometry and reporting it separately.
    public bool UseInterpolatedHeadSurfaceNormalForSolver { get; set; }=false;
    // The one-ring is built only from head-weighted triangles in the captured
    // patch that share a mesh edge. Its current-pose face normals are area
    // weighted each sample; this is geometric stabilization, not scalp labeling.
    public bool TraceHeadSurfaceOneRingNormal { get; set; }=false;
    public bool UseHeadSurfaceOneRingNormalForSolver { get; set; }=true;
    // Keep consecutive closest-point searches within a small shared-edge
    // neighborhood of the previous contact face. Tests can disable this for A/B.
    public bool UseHeadSurfaceTopologicalContinuity { get; set; }=true;
    public int HeadSurfaceTopologicalSearchRadius { get; set; }=2;
    // Bound solver-normal changes caused by switching between nearby surface
    // triangles. Infinite disables the temporal filter for tests.
    public float HeadSurfaceNormalAngularRateLimitRadiansPerSecond { get; set; }=MathF.PI;
    // Test-only trace for separating requested, principal and achieved palm orientation.
    // Extra post-roll world recomputation is disabled in normal runtime.
    public bool TraceFingertipPalmOrientation { get; set; }=false;
    public float LeftFingertipReachWeight { get; private set; }=float.NaN;
    public float RightFingertipReachWeight { get; private set; }=float.NaN;
    public Vector3 LeftFingertipWristTargetWorld { get; private set; }=new(float.NaN);
    public Vector3 RightFingertipWristTargetWorld { get; private set; }=new(float.NaN);
    public Vector3 RightFingertipTargetBiasWorld { get; private set; }=new(float.NaN);
    public float RightFingertipPass0ErrorMeters { get; private set; }=float.NaN;
    public float RightFingertipPass1ErrorMeters { get; private set; }=float.NaN;
    public float RightFingertipPass2ErrorMeters { get; private set; }=float.NaN;
    public float RightFingertipPass3ErrorMeters { get; private set; }=float.NaN;
    public float RightFingertipPass4ErrorMeters { get; private set; }=float.NaN;
    // Optional, test-only observer. With no observer assigned the runtime path allocates nothing.
    public Func<bool>? ShouldCaptureRightReachPassDiagnostic { get; set; }
    public Action<object>? RightReachPassDiagnosticObserver { get; set; }
    public Vector3[][] Positions { get; }
    public Vector3[][] Normals { get; }
    public RigSampler(ModelAsset asset)
    {
        _asset=asset; _rig=asset.Rig??throw new ModelImportException("模型没有骨骼动画。");
        _nativeRunningClip=RigClipResolver.FindRunning(_rig.Clips);
        // Only apply procedural offsets to known Mixamo bones; arbitrary rigs retain their own animation.
        int Find(string name) => Array.FindIndex(_rig.Nodes,n=>string.Equals(n.Name,"mixamorig:"+name,StringComparison.OrdinalIgnoreCase));
        _head=Find("Head");_neck=Find("Neck");_hips=Find("Hips");_spine=Find("Spine");_spine1=Find("Spine1");_spine2=Find("Spine2");
        _leftShoulder=Find("LeftShoulder");_leftArm=Find("LeftArm");_leftForeArm=Find("LeftForeArm");_leftHand=Find("LeftHand");
        _rightShoulder=Find("RightShoulder");_rightArm=Find("RightArm");_rightForeArm=Find("RightForeArm");_rightHand=Find("RightHand");
        _leftUpLeg=Find("LeftUpLeg");_leftLeg=Find("LeftLeg");_rightUpLeg=Find("RightUpLeg");_rightLeg=Find("RightLeg");
        _leftFoot=Find("LeftFoot");_rightFoot=Find("RightFoot");_leftToe=Find("LeftToeBase");_rightToe=Find("RightToeBase");
        _leftMiddle=Find("LeftHandMiddle1");_leftIndex=Find("LeftHandIndex1");_leftPinky=Find("LeftHandPinky1");
        _rightMiddle=Find("RightHandMiddle1");_rightIndex=Find("RightHandIndex1");_rightPinky=Find("RightHandPinky1");
        _handDigits=new[]{"Left","Right"}.Select(side=>new[]{"Index","Middle","Ring","Pinky","Thumb"}
            .Select(finger=>Enumerable.Range(1,3).Select(joint=>Find(side+"Hand"+finger+joint)).ToArray()).ToArray()).ToArray();
        _fingertipSurfaceSamples=new FingerSurfaceSample[2][][];
        for(var side=0;side<2;side++)
        {
            _fingertipSurfaceSamples[side]=new FingerSurfaceSample[5][];
            for(var finger=0;finger<5;finger++)_fingertipSurfaceSamples[side][finger]=Array.Empty<FingerSurfaceSample>();
        }
        _pose=new NodePose[_rig.Nodes.Length];_from=new NodePose[_pose.Length];_transitionPose=new NodePose[_pose.Length];_world=new Matrix4x4[_pose.Length];
        _skin=_rig.Skins.Select(s=>new Matrix4x4[s.Joints.Length]).ToArray();_normal=_rig.Skins.Select(s=>new Matrix4x4[s.Joints.Length]).ToArray();
        _localPositions=new Vector3[asset.Meshes.Length][];_localNormals=new Vector3[asset.Meshes.Length][];
        for(var m=0;m<asset.Meshes.Length;m++)
        {
            var mesh=asset.Meshes[m];var inverse=_rig.Meshes[m].InverseMeshWorld;
            if(!Matrix4x4.Invert(inverse,out var meshWorld))throw new ModelImportException("网格变换不可逆。");
            var normalToLocal=Matrix4x4.Transpose(meshWorld);
            _localPositions[m]=mesh.Positions.Select(p=>Vector3.Transform(p,inverse)).ToArray();
            _localNormals[m]=mesh.Normals.Select(n=>Vector3.TransformNormal(n,normalToLocal)).ToArray();
        }
        var supportVertices=new List<(int Mesh,int Vertex)>();
        for(var m=0;m<_rig.Meshes.Length;m++)
        {
            var binding=_rig.Meshes[m];if(binding.Skin<0)continue;
            var joints=_rig.Skins[binding.Skin].Joints;
            for(var v=0;v<binding.Joints.Length;v++)
            {
                var weight=binding.Weights[v];var ids=binding.Joints[v];var footWeight=0f;
                for(var k=0;k<4;k++)
                {
                    var node=joints[ids[k]];
                    if(node==_leftFoot||node==_rightFoot||node==_leftToe||node==_rightToe)
                        footWeight+=k switch{0=>weight.X,1=>weight.Y,2=>weight.Z,_=>weight.W};
                }
                if(footWeight>=.4f)supportVertices.Add((m,v));
            }
        }
        _footSupportVertices=supportVertices.ToArray();
        _restSoleFloor=_footSupportVertices.Length>0?_footSupportVertices.Min(v=>asset.Meshes[v.Mesh].Positions[v.Vertex].Y):0;
        Positions=asset.Meshes.Select(m=>new Vector3[m.Positions.Length]).ToArray();Normals=asset.Meshes.Select(m=>new Vector3[m.Positions.Length]).ToArray();
        CaptureHandTipSamples();
        CaptureHeadSurfacePatch();
    }
    public void BeginPoseTransition()
    {
        if (!_hasSample) return;
        _transitionRequiresSoleSupport=_lastRequiresSoleSupport;
        if(_footSupportVertices.Length>0)_transitionSoleFloor=MinimumSoleY();
        Array.Copy(_pose, _transitionPose, _pose.Length);
        PoseTransitionProgress = 0;
    }
    public void Sample(int clip, double seconds, int fromClip=-1, double fromSeconds=0, float blend=1, bool updateNormals=true)
    {
        if(clip==_lastPalmRollClip&&double.IsFinite(seconds)&&double.IsFinite(_lastPalmRollSampleSeconds))
        {
            var delta=seconds-_lastPalmRollSampleSeconds;
            if(delta>0&&delta<=.25)_palmRollSampleDeltaSeconds=(float)delta;
            else if(delta==0)_palmRollSampleDeltaSeconds=0;
            else
            {
                _palmRollSampleDeltaSeconds=1f/30f;
                if(delta<0){Array.Clear(_hasPreviousFingertipPalmRoll);Array.Clear(_previousFingertipPalmRoll);Array.Clear(_previousFingertipPalmRollApplied);}
            }
        }
        else _palmRollSampleDeltaSeconds=1f/30f;
        _lastPalmRollClip=clip;_lastPalmRollSampleSeconds=seconds;
        _hasCurrentRateLimitedHeadSurfaceNormal=false;
        LeftFingertipContactErrorMeters=float.NaN;RightFingertipContactErrorMeters=float.NaN;
        LeftFingertipContactTargetWorld=new(float.NaN);RightFingertipContactTargetWorld=new(float.NaN);
        LeftFingertipSurfaceCenterWorld=new(float.NaN);RightFingertipSurfaceCenterWorld=new(float.NaN);
        RightIndexFingertipSurfaceCenterWorld=new(float.NaN);RightMiddleFingertipSurfaceCenterWorld=new(float.NaN);
        RightFingertipContactSampleAvailable=false;
        RightHeadSurfacePointWorld=new(float.NaN);RightHeadSurfaceNormalWorld=new(float.NaN);RightHeadSurfaceRawNormalWorld=new(float.NaN);RightHeadSurfacePalmNormalWorld=new(float.NaN);
        RightHeadSurfaceContactTargetWorld=new(float.NaN);RightHeadSurfaceContactFaceNormalWorld=new(float.NaN);
        RightHeadSurfaceInterpolatedNormalWorld=new(float.NaN);RightHeadSurfaceOneRingNormalWorld=new(float.NaN);RightHeadSurfaceContactBarycentric=new(float.NaN);
        RightHeadSurfaceInterpolatedOutwardDot=float.NaN;RightHeadSurfaceFaceInterpolatedNormalDot=float.NaN;
        RightHeadSurfaceFaceOneRingNormalDot=float.NaN;RightHeadSurfaceOneRingTriangleCount=0;
        RightHeadSurfaceReachUpProjectionLength=float.NaN;RightHeadSurfaceFingerUpProjectionLength=float.NaN;
        RightHeadSurfaceTopologySearchRestricted=false;RightHeadSurfaceTopologySearchSucceeded=false;RightHeadSurfaceTopologyCandidateCount=0;
        RightHeadSurfaceContactMesh=RightHeadSurfaceContactTriangleA=RightHeadSurfaceContactTriangleB=RightHeadSurfaceContactTriangleC=-1;
        RightHeadSurfaceRawOutwardDot=float.NaN;RightHeadSurfaceSeedDistanceMeters=float.NaN;
        RightHeadSurfaceSignedClearanceMeters=float.NaN;RightHeadSurfaceFacePalmAlignmentDot=float.NaN;RightHeadSurfaceFaceSignedClearanceMeters=float.NaN;
        RightHeadSurfacePalmAlignmentDot=float.NaN;RightHeadSurfaceContactActive=false;
        LeftFingertipCorrectionPass=-1;RightFingertipCorrectionPass=-1;
        RightFingertipCorrectionGate=float.NaN;
        RightFingertipIndexFraction=float.NaN;
        RightFingertipPreAimAlignmentDot=float.NaN;
        RightFingertipPalmRollAngleRadians=float.NaN;RightFingertipAppliedPalmRollRadians=float.NaN;RightFingertipPalmForearmAppliedRadians=float.NaN;RightFingertipPalmWristAppliedRadians=float.NaN;RightFingertipPalmRollDiagnosticOffsetRadians=0;RightFingertipPalmNormalDot=float.NaN;
        RightFingertipPalmRollPrincipalRadians=float.NaN;RightFingertipDesiredPalmWorld=new(float.NaN);
        RightFingertipPalmNormalPreWristRollWorld=new(float.NaN);RightFingertipPalmNormalPostWristRollWorld=new(float.NaN);
        LeftFingertipReachWeight=float.NaN;RightFingertipReachWeight=float.NaN;
        LeftFingertipWristTargetWorld=new(float.NaN);RightFingertipWristTargetWorld=new(float.NaN);
        RightFingertipTargetBiasWorld=new(float.NaN);
        RightFingertipPass0ErrorMeters=float.NaN;RightFingertipPass1ErrorMeters=float.NaN;RightFingertipPass2ErrorMeters=float.NaN;
        RightFingertipPass3ErrorMeters=float.NaN;RightFingertipPass4ErrorMeters=float.NaN;
        FillPose(_pose,clip,seconds);
        if(fromClip>=0&&blend<1)
        {FillPose(_from,fromClip,fromSeconds);for(var i=0;i<_pose.Length;i++)_pose[i]=NodePose.Blend(_from[i],_pose[i],Math.Clamp(blend,0,1));}
        var selectedAction=CompanionActionOverride?CompanionInteractionAction:CompanionAction;
        var action=selectedAction.IsFinite?selectedAction:default;
        if(action.RightReach.Weight<=1e-5f||action.RightReach.HeadSurfaceFollow<=1e-5f)
        {
            _previousHeadSurfaceContactPatchIndex=-1;
            ResetHeadSurfaceNormalRateFilter();
        }
        if(action.LeftReach.Weight<=1e-5f||action.LeftReach.FingertipTarget<=1e-5f)_hasPreviousFingertipPalmRoll[0]=false;
        if(action.RightReach.Weight<=1e-5f||action.RightReach.FingertipTarget<=1e-5f)_hasPreviousFingertipPalmRoll[1]=false;
        var nativeRunWeight=_nativeRunningClip>=0?Math.Clamp(action.NativeRunningWeight,0,1):0;
        if(nativeRunWeight>1e-5f)
        {
            // Reuse the authored in-place gait for directional buttons and drag loops.
            // Weighted time preserves phase during the existing enter/leave blends.
            // Directional pose time is encoded at the legacy rate; authored
            // Human_Run uses its measured cadence, with legacy models unchanged.
            var timelineScale=RigClipResolver.PlaybackRate(_rig.Clips[_nativeRunningClip])/RigClipResolver.LegacyRunRate;
            FillPose(_from,_nativeRunningClip,Math.Max(0,action.NativeRunningSeconds/action.NativeRunningWeight*timelineScale));
            for(var i=0;i<_pose.Length;i++)_pose[i]=NodePose.Blend(_pose[i],_from[i],nativeRunWeight);
            // This model faces +Z. +/-70 degrees plus the drag view's bounded
            // +/-20 degrees faces the travel direction, without accumulating yaw.
            UpdateWorld();
            RotateWorld(_hips,Quaternion.CreateFromAxisAngle(Vector3.UnitY,
                Math.Clamp(action.NativeRunningDirection,-1,1)*(MathF.PI*70/180)));
            // Keep the procedural gait only as a missing-clip/transition fallback.
            action*=1-nativeRunWeight;
        }
        // Preserve the authored gait as a complete pose. Relaxing thighs and shins
        // toward a static Idle independently of the ankles breaks foot contact.
        var supportWeight=Math.Clamp(action.SupportWeight,0,1);
        var directionalBlend=nativeRunWeight>1e-5f&&nativeRunWeight<1-1e-5f;
        var preserveSoleFloor=_hips>=0&&_footSupportVertices.Length>0&&
            (action!=default||CompanionBodyMotion!=Vector3.Zero||CompanionIdleBody!=Vector3.Zero||
             (_transitionRequiresSoleSupport&&PoseTransitionProgress<1));
        var sourceSoleFloor=0f;
        if(preserveSoleFloor)
        {
            UpdateWorld();sourceSoleFloor=MinimumSoleY();
            // Local-rotation interpolation can dip below both valid endpoint
            // soles. Protect only the directional gait's partial blend; a pure
            // authored run keeps its original landing and flight trajectory.
            if(directionalBlend)sourceSoleFloor=Math.Max(sourceSoleFloor,_restSoleFloor);
        }
        SoleSupportCorrectionMeters=0;
        var hasSupport=supportWeight>0&&_hips>=0&&_leftFoot>=0&&_rightFoot>=0;
        var leftContact=Math.Clamp(action.LeftFootContact,0,1);
        var rightContact=Math.Clamp(action.RightFootContact,0,1);
        if(leftContact+rightContact<1e-5f){leftContact=1;rightContact=1;}
        var contactSum=leftContact+rightContact;
        leftContact/=contactSum;rightContact/=contactSum;
        var sourceSupport=Vector3.Zero;
        if(hasSupport){UpdateWorld();sourceSupport=SupportPoint(leftContact,rightContact);}
        var body=CompanionBodyMotion+CompanionIdleBody;
        if(float.IsFinite(body.X)&&float.IsFinite(body.Y)&&float.IsFinite(body.Z))
        {
            var sway=Math.Clamp(body.X,-.24f,.24f);
            var lean=Math.Clamp(body.Y,-.18f,.18f);
            var twist=Math.Clamp(body.Z,-.20f,.20f);
            Offset(_hips,Quaternion.CreateFromYawPitchRoll(twist*.10f,lean*.08f,-sway*.10f));
            Offset(_spine,Quaternion.CreateFromYawPitchRoll(twist*.22f,lean*.22f,sway*.30f));
            Offset(_spine1,Quaternion.CreateFromYawPitchRoll(twist*.28f,lean*.28f,sway*.27f));
            Offset(_spine2,Quaternion.CreateFromYawPitchRoll(twist*.24f,lean*.22f,sway*.17f));
            Offset(_neck,Quaternion.CreateFromYawPitchRoll(twist*.12f,lean*.08f,-sway*.12f));
            Offset(_leftShoulder,Quaternion.CreateFromAxisAngle(Vector3.UnitZ,sway*.08f));
            Offset(_leftArm,Quaternion.CreateFromAxisAngle(Vector3.UnitZ,-sway*.16f));
        }
        var actionBody=action.Body;
        var rightArmEuler=_rightMiddle>=0&&_rightIndex>=0&&_rightPinky>=0&&(action.RightReach.KnuckleTarget>0||action.RightReach.ForearmAlignment>0||action.RightReach.FullPose>0||action.RightReach.Clavicle!=Vector2.Zero)?0f:1f;
        var leftArmEuler=_leftMiddle>=0&&_leftIndex>=0&&_leftPinky>=0&&(action.LeftReach.KnuckleTarget>0||action.LeftReach.ForearmAlignment>0||action.LeftReach.FullPose>0||action.LeftReach.Clavicle!=Vector2.Zero)?0f:1f;
        if(float.IsFinite(actionBody.X)&&float.IsFinite(actionBody.Y)&&float.IsFinite(actionBody.Z))
        {
            var actionSway=Math.Clamp(actionBody.X,-.5f,.5f);
            var actionLean=Math.Clamp(actionBody.Y,-.55f,.55f);
            var actionTwist=Math.Clamp(actionBody.Z,-.4f,.4f);
            Offset(_hips,Quaternion.CreateFromYawPitchRoll(actionTwist*.12f,actionLean*.12f,-actionSway*.10f));
            Offset(_spine,Quaternion.CreateFromYawPitchRoll(actionTwist*.14f,actionLean*.20f,actionSway*.20f));
            Offset(_spine1,Quaternion.CreateFromYawPitchRoll(actionTwist*.16f,actionLean*.22f,actionSway*.18f));
            Offset(_spine2,Quaternion.CreateFromYawPitchRoll(actionTwist*.12f,actionLean*.16f,actionSway*.12f));
        }
        var breathing=float.IsFinite(CompanionBreathing)?Math.Clamp(CompanionBreathing,-1.2f,1.2f):0;
        Offset(_spine2,Quaternion.CreateFromYawPitchRoll(0,breathing*.010f,0));
        Offset(_leftShoulder,Quaternion.CreateFromAxisAngle(Vector3.UnitZ,breathing*.006f));
        Offset(_rightShoulder,Quaternion.CreateFromAxisAngle(Vector3.UnitZ,breathing*.006f));
        var gesture=CompanionGesture;
        if(float.IsFinite(gesture.X)&&float.IsFinite(gesture.Y)&&float.IsFinite(gesture.Z))
        {
            var wave=Math.Clamp(gesture.Z,0,1);
            var headRoll=float.IsFinite(body.X)?Math.Clamp(body.X*.18f,-.08f,.08f):0;
            var idleHead=CompanionIdleHead;
            var idleYaw=float.IsFinite(idleHead.X)?Math.Clamp(idleHead.X,-.12f,.12f):0;
            var idlePitch=float.IsFinite(idleHead.Y)?Math.Clamp(idleHead.Y,-.06f,.06f):0;
            var idleRoll=float.IsFinite(idleHead.Z)?Math.Clamp(idleHead.Z,-.04f,.04f):0;
            var swing=float.IsFinite(CompanionWaveSwing)?Math.Clamp(CompanionWaveSwing,-.52f,.52f)*wave:0;
            var lookYaw=Math.Clamp(gesture.X,-.55f,.55f);
            Offset(_neck,Quaternion.CreateFromYawPitchRoll(lookYaw*.32f,0,0));
            Offset(_head,Quaternion.CreateFromYawPitchRoll(Math.Clamp(lookYaw*.68f+idleYaw+action.Head.X,-.38f,.38f),Math.Clamp(gesture.Y+idlePitch+action.Head.Y,-.24f,.24f),Math.Clamp(headRoll+idleRoll+action.Head.Z,-.16f,.16f)));
            Offset(_rightShoulder,Quaternion.CreateFromAxisAngle(Vector3.UnitZ,-.10f*wave+.12f*swing+Math.Clamp(action.RightArm,-1.4f,1.4f)*.08f*rightArmEuler));
            Offset(_rightArm,Quaternion.CreateFromAxisAngle(Vector3.UnitZ,-1.10f*wave+.15f*swing+Math.Clamp(action.RightArm,-2.6f,2.6f)*rightArmEuler));
            Offset(_rightArm,Quaternion.CreateFromAxisAngle(Vector3.UnitX,Math.Clamp(action.RightArmPitch,-1.1f,1.1f)*rightArmEuler));
            Offset(_rightForeArm,Quaternion.CreateFromAxisAngle(Vector3.UnitZ,-1.50f*wave+.68f*swing+Math.Clamp(action.RightForeArm,-1.8f,1.8f)*rightArmEuler));
            Offset(_rightForeArm,Quaternion.CreateFromAxisAngle(Vector3.UnitX,Math.Clamp(action.RightForeArmPitch,-1.1f,1.1f)*rightArmEuler));
            Offset(_rightHand,Quaternion.CreateFromAxisAngle(Vector3.UnitZ,.95f*swing+Math.Clamp(action.RightHand,-1.2f,1.2f)*rightArmEuler));
            Offset(_leftShoulder,Quaternion.CreateFromAxisAngle(Vector3.UnitZ,Math.Clamp(action.LeftArm,-1.4f,1.4f)*.08f*leftArmEuler));
            Offset(_leftArm,Quaternion.CreateFromAxisAngle(Vector3.UnitZ,Math.Clamp(action.LeftArm,-2.6f,2.6f)*leftArmEuler));
            Offset(_leftArm,Quaternion.CreateFromAxisAngle(Vector3.UnitX,Math.Clamp(action.LeftArmPitch,-1.1f,1.1f)*leftArmEuler));
            Offset(_leftForeArm,Quaternion.CreateFromAxisAngle(Vector3.UnitZ,Math.Clamp(action.LeftForeArm,-1.8f,1.8f)*leftArmEuler));
            Offset(_leftForeArm,Quaternion.CreateFromAxisAngle(Vector3.UnitX,Math.Clamp(action.LeftForeArmPitch,-1.1f,1.1f)*leftArmEuler));
            Offset(_leftHand,Quaternion.CreateFromAxisAngle(Vector3.UnitZ,Math.Clamp(action.LeftHand,-1.2f,1.2f)*leftArmEuler));
            // This Mixamo rig bends in the sagittal plane around local X. Local Z is
            // hip abduction and must be reserved for a deliberate side-step.
            // Both knees use +X for flexion (world -Z at the lower leg); negative
            // values would hyperextend. Arm +X points roughly world -X in Idle,
            // so equal arm pitches and opposite hip pitches produce counter-swing.
            Offset(_leftUpLeg,Quaternion.CreateFromAxisAngle(Vector3.UnitX,Math.Clamp(action.LeftLeg,-.65f,.65f)));
            Offset(_rightUpLeg,Quaternion.CreateFromAxisAngle(Vector3.UnitX,Math.Clamp(action.RightLeg,-.65f,.65f)));
            Offset(_leftUpLeg,Quaternion.CreateFromAxisAngle(Vector3.UnitZ,Math.Clamp(action.LeftLegSide,-.35f,.35f)));
            Offset(_rightUpLeg,Quaternion.CreateFromAxisAngle(Vector3.UnitZ,Math.Clamp(action.RightLegSide,-.35f,.35f)));
            Offset(_leftLeg,Quaternion.CreateFromAxisAngle(Vector3.UnitX,Math.Clamp(action.LeftKnee,0,.90f)));
            Offset(_rightLeg,Quaternion.CreateFromAxisAngle(Vector3.UnitX,Math.Clamp(action.RightKnee,0,.90f)));
            Offset(_leftFoot,Quaternion.CreateFromAxisAngle(Vector3.UnitX,Math.Clamp(action.LeftFootPitch,-.8f,.8f)));
            Offset(_rightFoot,Quaternion.CreateFromAxisAngle(Vector3.UnitX,Math.Clamp(action.RightFootPitch,-.8f,.8f)));
        }
        if(hasSupport)
        {
            // Contact weights hand support to the other foot before the swing knee
            // folds, without snapping when the lower toe changes sides. Horizontal
            // anchoring is opt-in: an in-place run needs its support foot to travel
            // backward, rather than pulling the pelvis back and forth each step.
            UpdateWorld();
            var correction=(sourceSupport-SupportPoint(leftContact,rightContact))*supportWeight;
            var horizontalSupport=Math.Clamp(action.HorizontalSupportWeight,0,1);
            correction.X*=horizontalSupport;correction.Z*=horizontalSupport;
            var parent=_rig.Nodes[_hips].Parent;
            if(parent>=0&&Matrix4x4.Invert(_world[parent],out var inverseParent))correction=Vector3.TransformNormal(correction,inverseParent);
            Translate(_hips,correction);
        }
        Translate(_hips,new Vector3(0,Math.Clamp(action.HipLift,-.04f,.11f),0));
        UpdateWorld();
        // Freeze the torso-space contact frame before either clavicle is posed.
        // Recomputing it from the moving upper-arm nodes during fingertip IK made
        // the contact target drift as the shoulder/arm changed across correction passes.
        var contactFrameAvailable=
            _leftArm>=0&&_leftArm<_world.Length&&_rightArm>=0&&_rightArm<_world.Length&&
            _head>=0&&_head<_world.Length&&_hips>=0&&_hips<_world.Length;
        var contactLateral=Vector3.Zero;
        var contactUp=Vector3.Zero;
        var contactForward=Vector3.Zero;
        if(contactFrameAvailable)
        {
            var lateral=JointPosition(_leftArm)-JointPosition(_rightArm);
            var up=JointPosition(_head)-JointPosition(_hips);
            var lateralLengthSquared=lateral.LengthSquared();
            var upLengthSquared=up.LengthSquared();
            if(float.IsFinite(lateral.X)&&float.IsFinite(lateral.Y)&&float.IsFinite(lateral.Z)&&float.IsFinite(lateralLengthSquared)&&lateralLengthSquared>1e-10f&&
               float.IsFinite(up.X)&&float.IsFinite(up.Y)&&float.IsFinite(up.Z)&&float.IsFinite(upLengthSquared)&&upLengthSquared>1e-10f)
            {
                contactLateral=Vector3.Normalize(lateral);
                var normalizedUp=Vector3.Normalize(up);
                var forward=Vector3.Cross(contactLateral,normalizedUp);
                var forwardLengthSquared=forward.LengthSquared();
                if(float.IsFinite(contactLateral.X)&&float.IsFinite(contactLateral.Y)&&float.IsFinite(contactLateral.Z)&&
                   float.IsFinite(normalizedUp.X)&&float.IsFinite(normalizedUp.Y)&&float.IsFinite(normalizedUp.Z)&&
                   float.IsFinite(forward.X)&&float.IsFinite(forward.Y)&&float.IsFinite(forward.Z)&&float.IsFinite(forwardLengthSquared)&&forwardLengthSquared>1e-10f)
                {
                    contactForward=Vector3.Normalize(forward);
                    var orthogonalUp=Vector3.Cross(contactForward,contactLateral);
                    var orthogonalUpLengthSquared=orthogonalUp.LengthSquared();
                    if(float.IsFinite(contactForward.X)&&float.IsFinite(contactForward.Y)&&float.IsFinite(contactForward.Z)&&
                       float.IsFinite(orthogonalUp.X)&&float.IsFinite(orthogonalUp.Y)&&float.IsFinite(orthogonalUp.Z)&&
                       float.IsFinite(orthogonalUpLengthSquared)&&orthogonalUpLengthSquared>1e-10f)
                    {
                        contactUp=Vector3.Normalize(orthogonalUp);
                        contactFrameAvailable=float.IsFinite(contactUp.X)&&float.IsFinite(contactUp.Y)&&float.IsFinite(contactUp.Z);
                    }
                    else contactFrameAvailable=false;
                }
                else contactFrameAvailable=false;
            }
            else contactFrameAvailable=false;
        }
        if(contactFrameAvailable)
        {
            ApplyClavicle(_leftShoulder,_leftArm,_leftForeArm,_leftHand,action.LeftReach.Clavicle);
            ApplyClavicle(_rightShoulder,_rightArm,_rightForeArm,_rightHand,action.RightReach.Clavicle);
            ApplyReach(_leftArm,_leftForeArm,_leftHand,action.LeftReach,1,contactLateral,contactUp,contactForward);
            ApplyReach(_rightArm,_rightForeArm,_rightHand,action.RightReach,-1,contactLateral,contactUp,contactForward);
        }
        if (_hasSample && PoseTransitionProgress < 1)
        {
            var t=Math.Clamp(PoseTransitionProgress,0,1);
            t=t*t*t*(t*(t*6-15)+10);
            if(preserveSoleFloor)sourceSoleFloor=_transitionSoleFloor+(sourceSoleFloor-_transitionSoleFloor)*t;
            for(var i=0;i<_pose.Length;i++)_pose[i]=NodePose.Blend(_transitionPose[i],_pose[i],t);
        }
        _hasSample=true;
        _lastRequiresSoleSupport=preserveSoleFloor;
        UpdateWorld();
        if(preserveSoleFloor)
        {
            if(directionalBlend)sourceSoleFloor=Math.Max(sourceSoleFloor,_restSoleFloor);
            // A toe/ankle proxy misses a tilted heel. Preserve the base animation's
            // actual shoe clearance, allowing authored flight and deliberate lifts.
            // Only a root translation is corrected; leg lengths and poses are intact.
            var correction=Math.Max(0,sourceSoleFloor-MinimumSoleY());
            SoleSupportCorrectionMeters=correction;
            if(correction>0)
            {
                var offset=new Vector3(0,correction,0);var parent=_rig.Nodes[_hips].Parent;
                if(parent>=0&&Matrix4x4.Invert(_world[parent],out var inverse))offset=Vector3.TransformNormal(offset,inverse);
                Translate(_hips,offset);UpdateWorld();
            }
        }
        for(var s=0;s<_rig.Skins.Length;s++)for(var j=0;j<_skin[s].Length;j++)
        {
            // Start with mesh-local source vertices; glTF skin matrices map them directly into world space.
            var matrix=_rig.Skins[s].InverseBind[j]*_world[_rig.Skins[s].Joints[j]];_skin[s][j]=matrix;
            if(updateNormals)_normal[s][j]=Matrix4x4.Invert(matrix,out var inv)?Matrix4x4.Transpose(inv):Matrix4x4.Identity;
        }
        for(var m=0;m<_asset.Meshes.Length;m++)
        {
            var mesh=_asset.Meshes[m];var binding=_rig.Meshes[m];
            var rigidNormal=Matrix4x4.Identity;if(updateNormals)rigidNormal=Matrix4x4.Invert(_world[binding.Node],out var rigidInv)?Matrix4x4.Transpose(rigidInv):Matrix4x4.Identity;
            for(var i=0;i<mesh.Positions.Length;i++)
            {
                var local=_localPositions[m][i];
                var sourceNormal=_localNormals[m][i];
                Vector3 p,n=Vector3.Zero;
                if(binding.Skin<0)
                {
                    p=Vector3.Transform(local,_world[binding.Node]);
                    if(updateNormals)n=Vector3.TransformNormal(sourceNormal,rigidNormal);
                }
                else
                {
                    p=Vector3.Zero;var w=binding.Weights[i];var js=binding.Joints[i];
                    for(var k=0;k<4;k++)
                    {
                        var weight=k switch{0=>w.X,1=>w.Y,2=>w.Z,_=>w.W};if(weight<=0)continue;
                        p+=Vector3.Transform(local,_skin[binding.Skin][js[k]])*weight;
                        if(updateNormals)n+=Vector3.TransformNormal(sourceNormal,_normal[binding.Skin][js[k]])*weight;
                    }
                }
                Positions[m][i]=p;if(updateNormals)Normals[m][i]=n.LengthSquared()>1e-12f?Vector3.Normalize(n):Vector3.UnitZ;
            }
        }
    }
    private void UpdateWorld()
    {
        foreach(var i in _rig.Order)_world[i]=_rig.Nodes[i].Parent<0?_pose[i].Matrix:_pose[i].Matrix*_world[_rig.Nodes[i].Parent];
    }
    private float MinimumSoleY()
    {
        for(var s=0;s<_rig.Skins.Length;s++)for(var j=0;j<_skin[s].Length;j++)
            _skin[s][j]=_rig.Skins[s].InverseBind[j]*_world[_rig.Skins[s].Joints[j]];
        var minimum=float.PositiveInfinity;
        foreach(var (mesh,vertex) in _footSupportVertices)
        {
            var binding=_rig.Meshes[mesh];var local=_localPositions[mesh][vertex];
            var weights=binding.Weights[vertex];var joints=binding.Joints[vertex];var y=0f;
            for(var k=0;k<4;k++)
            {
                var weight=k switch{0=>weights.X,1=>weights.Y,2=>weights.Z,_=>weights.W};
                if(weight>0)y+=Vector3.Transform(local,_skin[binding.Skin][joints[k]]).Y*weight;
            }
            minimum=Math.Min(minimum,y);
        }
        return minimum;
    }
    private Vector3 JointPosition(int node)=>new(_world[node].M41,_world[node].M42,_world[node].M43);
    private void ApplyClavicle(int shoulder,int arm,int forearm,int hand,Vector2 cue)
    {
        if(shoulder<0||arm<0||forearm<0||hand<0||_head<0||_hips<0||_leftArm<0||_rightArm<0||
            !float.IsFinite(cue.X)||!float.IsFinite(cue.Y)||cue.LengthSquared()<1e-12f)return;
        var lateral=Vector3.Normalize(JointPosition(_leftArm)-JointPosition(_rightArm));
        var up=Vector3.Normalize(JointPosition(_head)-JointPosition(_hips));
        var forward=Vector3.Normalize(Vector3.Cross(lateral,up));
        up=Vector3.Normalize(Vector3.Cross(forward,lateral));
        var length=Vector3.Distance(JointPosition(arm),JointPosition(forearm))+Vector3.Distance(JointPosition(forearm),JointPosition(hand));
        var axis=JointPosition(arm)-JointPosition(shoulder);
        AimBone(shoulder,axis,axis+length*(forward*Math.Clamp(cue.X,-.06f,.06f)+up*Math.Clamp(cue.Y,-.04f,.04f)));
        UpdateWorld();
    }
    private void ApplyReach(int upper,int lower,int hand,CompanionReach reach,float side,Vector3 lateral,Vector3 up,Vector3 forward,Vector3 targetBias=default,int correctionPass=0)
    {
        if(upper<0||lower<0||hand<0||_leftArm<0||_rightArm<0||_hips<0||_head<0||reach.Weight<=1e-5f||!reach.IsFinite||
            !float.IsFinite(targetBias.X)||!float.IsFinite(targetBias.Y)||!float.IsFinite(targetBias.Z))return;
        var shoulder=JointPosition(upper);
        var a=Vector3.Distance(shoulder,JointPosition(lower));
        var b=Vector3.Distance(JointPosition(lower),JointPosition(hand));
        if(a<1e-5f||b<1e-5f)return;
        var offset=reach.Hand/reach.Weight;
        var pole=reach.Elbow/reach.Weight;
        var fingertipIndexFraction=Math.Clamp(reach.FingertipIndexFraction/reach.Weight,0f,1f);
        var baseHandPosition=JointPosition(hand);
        var length=a+b;
        var anchor=Vector3.Lerp(shoulder,JointPosition(_head),Math.Clamp(reach.HeadAnchor/reach.Weight,0,1));
        var target=anchor+(lateral*(offset.X*side)+up*offset.Y+forward*offset.Z)*length;
        var contactTarget=target;
        var sideIndex=side>0?0:1;
        if(sideIndex==1)RightFingertipPalmRollDiagnosticOffsetRadians=0;
        float? fingertipPalmRollForContinuity=null;
        float? fingertipAppliedPalmRollForContinuity=null;
        var digits=_handDigits[sideIndex];
        var middle=side>0?_leftMiddle:_rightMiddle;
        var index=side>0?_leftIndex:_rightIndex;
        var pinky=side>0?_leftPinky:_rightPinky;
        Vector3 Direction(Vector3 v)=>lateral*(v.X*side)+up*v.Y+forward*v.Z;
        var intendedFingers=Direction(reach.Fingers);
        var knuckleAmount=Math.Clamp(reach.KnuckleTarget/reach.Weight,0,1);
        var fingertipAmount=Math.Clamp(reach.FingertipTarget/reach.Weight,0,1);
        var headSurfaceResolved=false;var headSurfacePoint=Vector3.Zero;var headSurfaceNormal=Vector3.Zero;var headSurfaceSeedDistance=float.NaN;
        var headSurfaceSeedProjection=default(HeadSurfaceProjection);var headSurfaceContactProjection=default(HeadSurfaceProjection);
        var headSurfaceFollow=Math.Clamp(reach.HeadSurfaceFollow/reach.Weight,0,1);
        var headSurfaceContactAmount=Math.Clamp(reach.FingertipContactWeight/reach.Weight,0,1);
        if(headSurfaceFollow>1e-5f&&TryGetHeadSurfaceGoal(target,up,forward,reach.HeadSurfaceTangentOffset/reach.Weight,
            reach.HeadSurfaceTangentHeading/reach.Weight,reach.HeadSurfaceClearance/reach.Weight,headSurfaceContactAmount,
            out var surfaceTarget,out headSurfacePoint,out headSurfaceNormal,out _,out headSurfaceSeedDistance,
            out headSurfaceSeedProjection,out headSurfaceContactProjection))
        {
            contactTarget=Vector3.Lerp(contactTarget,surfaceTarget,headSurfaceFollow);
            target=contactTarget;
            headSurfaceResolved=true;
            if(sideIndex==1)
            {
                RightHeadSurfacePointWorld=headSurfacePoint;RightHeadSurfaceNormalWorld=headSurfaceNormal;
                RightHeadSurfaceContactTargetWorld=surfaceTarget;
                RightHeadSurfaceContactFaceNormalWorld=headSurfaceContactProjection.Normal;
                RightHeadSurfaceRawNormalWorld=headSurfaceContactProjection.RawNormal;
                RightHeadSurfaceRawOutwardDot=headSurfaceContactProjection.RawOutwardDot;
                if(TraceHeadSurfaceSmoothNormals)
                {
                    RightHeadSurfaceInterpolatedNormalWorld=headSurfaceContactProjection.InterpolatedNormal;
                    RightHeadSurfaceContactBarycentric=headSurfaceContactProjection.Barycentric;
                    RightHeadSurfaceInterpolatedOutwardDot=headSurfaceContactProjection.InterpolatedOutwardDot;
                    RightHeadSurfaceFaceInterpolatedNormalDot=headSurfaceContactProjection.FaceInterpolatedNormalDot;
                }
                if(TraceHeadSurfaceOneRingNormal||UseHeadSurfaceOneRingNormalForSolver)
                {
                    RightHeadSurfaceOneRingNormalWorld=headSurfaceContactProjection.OneRingNormal;
                    RightHeadSurfaceOneRingTriangleCount=headSurfaceContactProjection.OneRingTriangleCount;
                    RightHeadSurfaceFaceOneRingNormalDot=headSurfaceContactProjection.FaceOneRingNormalDot;
                }
                RightHeadSurfaceContactMesh=headSurfaceContactProjection.Triangle.Mesh;
                RightHeadSurfaceContactTriangleA=headSurfaceContactProjection.Triangle.A;
                RightHeadSurfaceContactTriangleB=headSurfaceContactProjection.Triangle.B;
                RightHeadSurfaceContactTriangleC=headSurfaceContactProjection.Triangle.C;
                RightHeadSurfaceSeedDistanceMeters=headSurfaceSeedDistance;RightHeadSurfaceContactActive=true;
            }
        }
        var fullChain=(reach.KnuckleTarget>0||reach.FingertipTarget>0||reach.ForearmAlignment>0||reach.FullPose>0)&&middle>=0&&index>=0&&pinky>=0&&intendedFingers.LengthSquared()>1e-8f&&reach.Palm.LengthSquared()>1e-8f;
        if(intendedFingers.LengthSquared()>1e-8f)
        {
            var retreat=0f;
            if(middle>=0&&knuckleAmount>0)retreat+=Vector3.Distance(JointPosition(hand),JointPosition(middle))*knuckleAmount;
            if(fingertipAmount>0)retreat+=EstimateFingertipReach(hand,sideIndex,fingertipIndexFraction)*fingertipAmount;
            if(retreat>0)target-=Vector3.Normalize(intendedFingers)*retreat;
        }
        // Keep the requested contact point fixed across passes. The correction
        // bias belongs only to the wrist IK target; including it in contactTarget
        // made every pass chase a newly shifted fingertip goal and never converge.
        var targetAfterFingerRetreat=target;
        target+=targetBias;
        var targetAfterBias=target;
        var weight=Math.Clamp(reach.Weight,0,1);
        var targetBeforeWeightBlend=target;
        if(fullChain)
        {
            // Follow a continuous world-space wrist path and solve shoulder/elbow
            // for that point each frame. Blending the full IK rotations locally
            // made the wrist arc overshoot far from both the idle and contact poses.
            if(UseRadialHandTargetBlendForDiagnostic)
            {
                var baseOffset=baseHandPosition-shoulder;
                var targetOffset=target-shoulder;
                var baseRadius=baseOffset.Length();
                var targetRadius=targetOffset.Length();
                if(!float.IsFinite(baseRadius)||!float.IsFinite(targetRadius)||baseRadius<=1e-6f||targetRadius<=1e-6f)
                    throw new InvalidOperationException("Radial wrist blend has a degenerate endpoint radius.");
                var baseDirection=baseOffset/baseRadius;
                var targetDirection=targetOffset/targetRadius;
                var directionDot=Math.Clamp(Vector3.Dot(baseDirection,targetDirection),-1f,1f);
                var directionCross=Vector3.Cross(baseDirection,targetDirection);
                var crossLength=directionCross.Length();
                Vector3 blendedDirection;
                if(crossLength<=1e-6f)
                {
                    if(directionDot<0f)throw new InvalidOperationException("Radial wrist blend has an ambiguous shortest arc.");
                    blendedDirection=Vector3.Normalize(Vector3.Lerp(baseDirection,targetDirection,weight));
                }
                else
                {
                    var axis=directionCross/crossLength;
                    var angle=MathF.Acos(directionDot)*weight;
                    blendedDirection=Vector3.Normalize(Vector3.Transform(baseDirection,Quaternion.CreateFromAxisAngle(axis,angle)));
                }
                var blendedRadius=baseRadius+(targetRadius-baseRadius)*weight;
                target=shoulder+blendedDirection*blendedRadius;
            }
            else target=Vector3.Lerp(baseHandPosition,target,weight);
            if(weight<=1e-5f)return;
        }
        var targetBeforeReachClamp=target;
        var hint=(lateral*(pole.X*side)+up*pole.Y+forward*pole.Z)*length;
        var direction=target-shoulder;
        var distance=direction.Length();
        if(distance<1e-5f)return;
        direction/=distance;
        // Keep a small bend at full reach, and a stable elbow plane near folded poses.
        var unclampedDistance=distance;
        var minimumReachDistance=Math.Abs(a-b)+length*.02f;
        var maximumReachDistance=length*.98f;
        distance=Math.Clamp(distance,minimumReachDistance,maximumReachDistance);
        target=shoulder+direction*distance;
        var bend=hint-direction*Vector3.Dot(hint,direction);
        var bendAfterProjection=bend;
        if(bend.LengthSquared()<1e-8f)return;
        bend=Vector3.Normalize(bend);
        var along=(a*a-b*b+distance*distance)/(2*distance);
        var height=MathF.Sqrt(Math.Max(0,a*a-along*along));
        var elbow=shoulder+direction*along+bend*height;
        var originalUpper=_pose[upper];var originalLower=_pose[lower];
        var originalHand=_pose[hand];
        Span<NodePose> originalDigits=stackalloc NodePose[15];
        if(fullChain)for(var finger=0;finger<5;finger++)for(var joint=0;joint<3;joint++)
            if(digits[finger][joint]>=0)originalDigits[finger*3+joint]=_pose[digits[finger][joint]];
        AimBone(upper,JointPosition(lower)-shoulder,elbow-shoulder);
        UpdateWorld();
        AimBone(lower,JointPosition(hand)-JointPosition(lower),target-JointPosition(lower));
        if(!fullChain)
        {
            _pose[upper]=NodePose.Blend(originalUpper,_pose[upper],weight);
            _pose[lower]=NodePose.Blend(originalLower,_pose[lower],weight);
        }
        UpdateWorld();
        if(middle<0||index<0||pinky<0)return;
        var fingers=Direction(reach.Fingers);
        var palm=Direction(reach.Palm);
        if(headSurfaceResolved&&headSurfaceFollow>1e-5f)
        {
            // Compare this surface-facing palm target against the authored palm
            // candidate while keeping the same fingertip contact and hand axis.
            palm=-headSurfaceNormal;
        }
        if(fingers.LengthSquared()<1e-8f||palm.LengthSquared()<1e-8f)return;
        fingers=Vector3.Normalize(fingers);
        if(reach.ForearmAlignment>0)
        {
            var armAxis=JointPosition(hand)-JointPosition(lower);
            var levelAxis=armAxis-up*Vector3.Dot(armAxis,up);
            if(levelAxis.LengthSquared()>1e-8f)
                fingers=Vector3.Normalize(Vector3.Lerp(fingers,Vector3.Normalize(levelAxis),Math.Clamp(reach.ForearmAlignment/reach.Weight,0,1)));
        }
        if(headSurfaceResolved&&headSurfaceFollow>1e-5f)
        {
            // During head contact, point the fingers along the local scalp
            // tangent. A fixed body-relative finger axis was tilted strongly
            // into the surface normal, making the hand read as a flat press.
            var surfaceTangent=up-headSurfaceNormal*Vector3.Dot(up,headSurfaceNormal);
            if(sideIndex==1)RightHeadSurfaceFingerUpProjectionLength=surfaceTangent.Length();
            if(surfaceTangent.LengthSquared()<1e-8f)
                surfaceTangent=forward-headSurfaceNormal*Vector3.Dot(forward,headSurfaceNormal);
            if(surfaceTangent.LengthSquared()>1e-8f)fingers=Vector3.Normalize(surfaceTangent);
        }
        var sourceFinger=JointPosition(middle)-JointPosition(hand);
        if(sideIndex==1&&sourceFinger.LengthSquared()>1e-8f)
            RightFingertipPreAimAlignmentDot=Vector3.Dot(Vector3.Normalize(sourceFinger),fingers);
        palm-=fingers*Vector3.Dot(palm,fingers);
        if(palm.LengthSquared()<1e-8f)return;
        palm=Vector3.Normalize(palm);
        if(sideIndex==1&&TraceFingertipPalmOrientation)RightFingertipDesiredPalmWorld=palm;
        AimBone(hand,sourceFinger,fingers,palm);
        UpdateWorld();
        var normal=Vector3.Cross(JointPosition(index)-JointPosition(pinky),JointPosition(middle)-JointPosition(hand))*(-side);
        normal-=fingers*Vector3.Dot(normal,fingers);
        if(normal.LengthSquared()>1e-8f&&palm.LengthSquared()>1e-8f)
        {
            normal=Vector3.Normalize(normal);palm=Vector3.Normalize(palm);
            var principalAngle=MathF.Atan2(Vector3.Dot(fingers,Vector3.Cross(normal,palm)),Vector3.Dot(normal,palm));
            var angle=principalAngle;
            if(sideIndex==1&&TraceFingertipPalmOrientation)RightFingertipPalmRollPrincipalRadians=principalAngle;
            // atan2 wraps at +/-PI. Choose the equivalent branch nearest the
            // previous frame, rather than switching branches at a fixed angle:
            // the old -PI+.20 threshold changed the limited wrist pose by 2.3rad.
            if(fullChain&&fingertipAmount>1e-5f&&_hasPreviousFingertipPalmRoll[sideIndex])
                angle+=MathF.Tau*MathF.Round((_previousFingertipPalmRoll[sideIndex]-angle)/MathF.Tau);
            var palmRollLimit=float.IsFinite(PalmRollLimitRadians)?Math.Clamp(PalmRollLimitRadians,0f,MathF.PI):1.15f;
            var desiredAppliedAngle=Math.Clamp(angle,-palmRollLimit,palmRollLimit);
            var appliedAngle=desiredAppliedAngle;
            if(fullChain&&fingertipAmount>1e-5f)
            {
                var palmRollRate=float.IsFinite(PalmRollRateRadiansPerSecond)?Math.Max(0f,PalmRollRateRadiansPerSecond):2.8f;
                var maximumStep=palmRollRate*_palmRollSampleDeltaSeconds;
                var previous=_hasPreviousFingertipPalmRoll[sideIndex]?_previousFingertipPalmRollApplied[sideIndex]:0;
                appliedAngle=previous+Math.Clamp(desiredAppliedAngle-previous,-maximumStep,maximumStep);
                fingertipPalmRollForContinuity=angle;
                fingertipAppliedPalmRollForContinuity=appliedAngle;
            }
            var forearmAppliedAngle=0f;var wristAppliedAngle=appliedAngle;
            var forearmShare=float.IsFinite(PalmRollForearmShare)?Math.Clamp(PalmRollForearmShare,0f,1f):0f;
            if(fullChain&&fingertipAmount>1e-5f&&forearmShare>1e-5f&&Math.Abs(appliedAngle)>1e-5f)
            {
                var forearmAxis=JointPosition(hand)-JointPosition(lower);
                if(forearmAxis.LengthSquared()>1e-10f)
                {
                    var lowerBeforeForearmRoll=_pose[lower];var handBeforeForearmRoll=_pose[hand];
                    forearmAppliedAngle=appliedAngle*forearmShare;
                    RotateWorld(lower,Quaternion.CreateFromAxisAngle(Vector3.Normalize(forearmAxis),forearmAppliedAngle));UpdateWorld();
                    var rolledSourceFinger=JointPosition(middle)-JointPosition(hand);
                    AimBone(hand,rolledSourceFinger,fingers);UpdateWorld();
                    var rolledNormal=Vector3.Cross(JointPosition(index)-JointPosition(pinky),JointPosition(middle)-JointPosition(hand))*(-side);
                    rolledNormal-=fingers*Vector3.Dot(rolledNormal,fingers);
                    if(rolledNormal.LengthSquared()>1e-8f)
                    {
                        normal=Vector3.Normalize(rolledNormal);
                        var remainingAngle=MathF.Atan2(Vector3.Dot(fingers,Vector3.Cross(normal,palm)),Vector3.Dot(normal,palm));
                        var desiredRemainingAngle=angle-appliedAngle;
                        remainingAngle+=MathF.Tau*MathF.Round((desiredRemainingAngle-remainingAngle)/MathF.Tau);
                        var wristLimit=float.IsFinite(PalmRollWristLimitRadians)?Math.Clamp(PalmRollWristLimitRadians,0f,MathF.PI):1.15f;
                        wristAppliedAngle=Math.Clamp(remainingAngle-desiredRemainingAngle,-wristLimit,wristLimit);
                    }
                    else
                    {
                        _pose[lower]=lowerBeforeForearmRoll;_pose[hand]=handBeforeForearmRoll;UpdateWorld();
                        forearmAppliedAngle=0;wristAppliedAngle=appliedAngle;
                    }
                }
            }
            var diagnosticPalmRollOffset=headSurfaceResolved&&fullChain&&fingertipAmount>1e-5f
                ?FingertipPalmRollDiagnosticOffsetRadians:0f;
            wristAppliedAngle+=diagnosticPalmRollOffset;
            if(sideIndex==1)
            {
                RightFingertipPalmRollAngleRadians=angle;RightFingertipAppliedPalmRollRadians=appliedAngle;
                RightFingertipPalmRollDiagnosticOffsetRadians=diagnosticPalmRollOffset;
                RightFingertipPalmForearmAppliedRadians=forearmAppliedAngle;RightFingertipPalmWristAppliedRadians=wristAppliedAngle;
                RightFingertipPalmNormalDot=Vector3.Dot(normal,palm);
                if(TraceFingertipPalmOrientation)RightFingertipPalmNormalPreWristRollWorld=normal;
            }
            RotateWorld(hand,Quaternion.CreateFromAxisAngle(fingers,wristAppliedAngle));
            if(sideIndex==1&&TraceFingertipPalmOrientation)
            {
                UpdateWorld();
                var postRollNormal=Vector3.Cross(JointPosition(index)-JointPosition(pinky),JointPosition(middle)-JointPosition(hand))*(-side);
                postRollNormal-=fingers*Vector3.Dot(postRollNormal,fingers);
                if(postRollNormal.LengthSquared()>1e-8f)RightFingertipPalmNormalPostWristRollWorld=Vector3.Normalize(postRollNormal);
            }
        }
        if(!fullChain)_pose[hand]=NodePose.Blend(originalHand,_pose[hand],weight);
        UpdateWorld();
        var curls=fullChain?reach.Curl/reach.Weight:reach.Curl;
        var thumbCurl=fullChain?reach.ThumbCurl/reach.Weight:reach.ThumbCurl;
        if(curls.LengthSquared()>1e-10f||Math.Abs(reach.ThumbCurl)>1e-5f)
        {
            var palmNormal=Vector3.Cross(JointPosition(index)-JointPosition(pinky),JointPosition(middle)-JointPosition(hand))*(-side);
            if(palmNormal.LengthSquared()<1e-10f)palmNormal=Vector3.Normalize(palm);
            palmNormal=Vector3.Normalize(palmNormal);
            var gather=Math.Clamp(fullChain?reach.FingerGather/reach.Weight:reach.FingerGather,0,1);
            if(gather>1e-5f&&digits[1][0]>=0&&digits[1][1]>=0)
            {
                // Adduct only in the palm plane. Keep flexion and thumb opposition separate.
                var middleAxis=JointPosition(digits[1][1])-JointPosition(digits[1][0]);
                middleAxis-=palmNormal*Vector3.Dot(middleAxis,palmNormal);
                if(middleAxis.LengthSquared()>1e-10f)
                {
                    middleAxis=Vector3.Normalize(middleAxis);
                    for(var finger=0;finger<4;finger++)
                    {
                        if(finger==1)continue;
                        if(digits[finger][0]<0||digits[finger][1]<0)continue;
                        var fingerAxis=JointPosition(digits[finger][1])-JointPosition(digits[finger][0]);
                        fingerAxis-=palmNormal*Vector3.Dot(fingerAxis,palmNormal);
                        if(fingerAxis.LengthSquared()<1e-10f)continue;
                        fingerAxis=Vector3.Normalize(fingerAxis);
                        var angle=MathF.Atan2(Vector3.Dot(palmNormal,Vector3.Cross(fingerAxis,middleAxis)),Vector3.Dot(fingerAxis,middleAxis));
                        RotateWorld(digits[finger][0],Quaternion.CreateFromAxisAngle(palmNormal,Math.Clamp(angle,-.35f,.35f)*gather));UpdateWorld();
                    }
                }
            }
            CurlDigit(digits[0],curls.X,palmNormal,false);CurlDigit(digits[1],curls.Y,palmNormal,false);
            CurlDigit(digits[2],curls.Z,palmNormal,false);CurlDigit(digits[3],curls.W,palmNormal,false);
            if(thumbCurl>1e-5f&&digits[4][0]>=0&&digits[4][1]>=0&&digits[0][0]>=0)
            {
                var thumb=digits[4][0];var oldThumb=_pose[thumb];
                var opposition=JointPosition(digits[0][0])+palmNormal*.012f-JointPosition(thumb);
                AimBone(thumb,JointPosition(digits[4][1])-JointPosition(thumb),opposition);
                _pose[thumb]=NodePose.Blend(oldThumb,_pose[thumb],Math.Clamp(thumbCurl*1.8f,0,.85f));UpdateWorld();
            }
            CurlDigit(digits[4],thumbCurl,palmNormal,true);
        }
        if(fullChain)
        {
            // The solved upper/lower chain already follows the interpolated wrist
            // point. Blend only distal orientation/finger curl so the endpoint
            // path is not pulled off course by a second local-rotation blend.
            _pose[hand]=NodePose.Blend(originalHand,_pose[hand],weight);
            for(var finger=0;finger<5;finger++)for(var joint=0;joint<3;joint++)
                if(digits[finger][joint]>=0)_pose[digits[finger][joint]]=NodePose.Blend(originalDigits[finger*3+joint],_pose[digits[finger][joint]],weight);
            UpdateWorld();
        }
        if(fingertipAmount>1e-5f&&fullChain&&TryGetFingertipCenter(sideIndex,fingertipIndexFraction,out var actualFingertips,out var indexTip,out var middleTip))
        {
            if(sideIndex==1)
            {
                RightFingertipContactSampleAvailable=true;
                RightIndexFingertipSurfaceCenterWorld=indexTip;RightMiddleFingertipSurfaceCenterWorld=middleTip;RightFingertipIndexFraction=fingertipIndexFraction;
                if(headSurfaceResolved)
                {
                    RightHeadSurfaceSignedClearanceMeters=Vector3.Dot(actualFingertips-headSurfacePoint,headSurfaceNormal);
                    RightHeadSurfaceFaceSignedClearanceMeters=Vector3.Dot(actualFingertips-headSurfacePoint,RightHeadSurfaceContactFaceNormalWorld);
                    var padNormal=Vector3.Cross(JointPosition(index)-JointPosition(pinky),JointPosition(middle)-JointPosition(hand))*(-side);
                    if(padNormal.LengthSquared()>1e-10f)
                    {
                        RightHeadSurfacePalmNormalWorld=Vector3.Normalize(padNormal);
                        RightHeadSurfacePalmAlignmentDot=Vector3.Dot(RightHeadSurfacePalmNormalWorld,-headSurfaceNormal);
                        RightHeadSurfaceFacePalmAlignmentDot=Vector3.Dot(RightHeadSurfacePalmNormalWorld,-RightHeadSurfaceContactFaceNormalWorld);
                    }
                }
            }
            var correction=contactTarget-actualFingertips;
            var error=correction.Length();
            var gateT=Math.Clamp((reach.Weight-.80f)/.20f,0,1);
            var contactWeight=Math.Clamp(reach.FingertipContactWeight/reach.Weight,0,1);
            var correctionGate=gateT*gateT*gateT*(gateT*(gateT*6-15)+10)*contactWeight;
            if(sideIndex==1)
            {
                if(correctionPass==0)RightFingertipPass0ErrorMeters=error;
                else if(correctionPass==1)RightFingertipPass1ErrorMeters=error;
                else if(correctionPass==2)RightFingertipPass2ErrorMeters=error;
                else if(correctionPass==3)RightFingertipPass3ErrorMeters=error;
                else if(correctionPass==4)RightFingertipPass4ErrorMeters=error;
            }
            var correctionBeforeReachLimit=correction;
            var limitedCorrection=error>.08f?correction*(.08f/error):correction;
            var gatedCorrection=limitedCorrection*correctionGate;
            if(sideIndex==1&&RightReachPassDiagnosticObserver is { } trace&&ShouldCaptureRightReachPassDiagnostic?.Invoke()==true)
            {
                static float[] Point(Vector3 p)=>new[]{p.X,p.Y,p.Z};
                object FingerSamples(int finger)=>_fingertipSurfaceSamples[sideIndex][finger].Select(sample=>new
                {
                    mesh=sample.Mesh,vertex=sample.Vertex,influence=sample.Influence,
                    fingerChainInfluence=sample.FingerChainInfluence,
                    strongestOtherFingerInfluence=sample.StrongestOtherFingerInfluence,
                    positionWorld=Point(SkinnedVertexWorld(sample.Mesh,sample.Vertex))
                }).ToArray();
                trace(new
                {
                    correctionPass,reachWeight=reach.Weight,blendWeight=weight,w=weight,
                    baseHandPositionWorld=Point(baseHandPosition),targetBeforeWeightBlend=Point(targetBeforeWeightBlend),
                    targetAfterWeightBlend=Point(targetBeforeReachClamp),
                    headSurfaceProjectionUsesPreviousSamplePositions=headSurfaceResolved&&
                        (headSurfaceSeedProjection.UsedPreviousSamplePositions||headSurfaceContactProjection.UsedPreviousSamplePositions),fullChain,
                    targetBeforeHeadProjection=Point(anchor+(lateral*(offset.X*side)+up*offset.Y+forward*offset.Z)*length),
                    headSurfaceResolved,headSurfaceSeedDistanceMeters=headSurfaceSeedDistance,
                    headSurfaceSeedProjection=headSurfaceResolved?TraceHeadProjection(headSurfaceSeedProjection):null,
                    headSurfaceContactProjection=headSurfaceResolved?TraceHeadProjection(headSurfaceContactProjection):null,
                    headSurfacePointWorld=Point(headSurfacePoint),headSurfaceNormalWorld=Point(headSurfaceNormal),
                    contactTargetWorld=Point(contactTarget),targetAfterFingerRetreat=Point(targetAfterFingerRetreat),
                    targetBiasWorld=Point(targetBias),targetAfterBias=Point(targetAfterBias),
                    poleInput=Point(reach.Elbow),poleAfterWeightDivision=Point(pole),poleHintWorld=Point(hint),
                    directionWorld=Point(direction),bendProjectionDot=Vector3.Dot(hint,direction),
                    bendBeforeProjectionWorld=Point(hint),bendAfterProjectionWorld=Point(bendAfterProjection),
                    bendAfterNormalizationWorld=Point(bend),projectedBendLengthMeters=bendAfterProjection.Length(),
                    alongMeters=along,heightMeters=height,
                    targetBeforeReachClamp=Point(targetBeforeReachClamp),targetAfterReachClamp=Point(target),
                    armLengthUpperMeters=a,armLengthForearmMeters=b,armLengthTotalMeters=length,
                    aMeters=a,bMeters=b,solverPointsWorld=new{S=Point(shoulder),E=Point(elbow),W=Point(target)},
                    unclampedDistanceMeters=unclampedDistance,minimumReachDistanceMeters=minimumReachDistance,
                    maximumReachDistanceMeters=maximumReachDistance,clampedDistanceMeters=distance,
                    shoulderWorld=Point(shoulder),elbowTargetWorld=Point(elbow),wristTargetWorld=Point(target),
                    shoulderActualWorld=Point(JointPosition(upper)),elbowActualWorld=Point(JointPosition(lower)),wristActualWorld=Point(JointPosition(hand)),
                    fingertipCenterWorld=Point(actualFingertips),indexTipWorld=Point(indexTip),middleTipWorld=Point(middleTip),
                    errorMeters=error,correctionBeforeReachLimit=Point(correctionBeforeReachLimit),
                    correctionAfterReachLimit=Point(limitedCorrection),correctionAfterGate=Point(gatedCorrection),
                    correctionGate,requestedContactWeight=reach.FingertipContactWeight,effectiveContactWeight=contactWeight,
                    palmRollLimitRadians=PalmRollLimitRadians,palmRollRateRadiansPerSecond=PalmRollRateRadiansPerSecond,palmRollForearmShare=PalmRollForearmShare,palmRollWristLimitRadians=PalmRollWristLimitRadians,palmRollRequestedRadians=RightFingertipPalmRollAngleRadians,palmRollForearmAppliedRadians=RightFingertipPalmForearmAppliedRadians,palmRollWristAppliedRadians=RightFingertipPalmWristAppliedRadians,palmRollDiagnosticOffsetRadians=RightFingertipPalmRollDiagnosticOffsetRadians,
                    palmRollAppliedRadians=RightFingertipAppliedPalmRollRadians,
                    palmAlignmentDot=RightHeadSurfacePalmAlignmentDot,
                    rightIndexSurfaceSamples=FingerSamples(0),rightMiddleSurfaceSamples=FingerSamples(1)
                });
            }
            // Re-solve from the sampled pose, once or twice, against the actual
            // skinned index/middle fingertip surfaces after wrist aim and curl.
            // Ramp correction smoothly as the reach settles. A hard weight
            // threshold caused the wrist to snap when entering/leaving contact.
            if(correctionPass<4&&correctionGate>1e-5f&&error>.0025f)
            {
                _pose[upper]=originalUpper;_pose[lower]=originalLower;_pose[hand]=originalHand;
                for(var finger=0;finger<5;finger++)for(var joint=0;joint<3;joint++)
                    if(digits[finger][joint]>=0)_pose[digits[finger][joint]]=originalDigits[finger*3+joint];
                UpdateWorld();
                ApplyReach(upper,lower,hand,reach,side,lateral,up,forward,targetBias+gatedCorrection,correctionPass+1);
                return;
            }
            if(sideIndex==0)
            {
                LeftFingertipContactErrorMeters=error;LeftFingertipContactTargetWorld=contactTarget;
                LeftFingertipSurfaceCenterWorld=actualFingertips;LeftFingertipCorrectionPass=correctionPass;
                LeftFingertipReachWeight=reach.Weight;LeftFingertipWristTargetWorld=target;
            }
            else
            {
                RightFingertipContactErrorMeters=error;RightFingertipContactTargetWorld=contactTarget;
                RightFingertipSurfaceCenterWorld=actualFingertips;RightFingertipCorrectionPass=correctionPass;
                RightFingertipReachWeight=reach.Weight;RightFingertipWristTargetWorld=target;
                RightFingertipTargetBiasWorld=targetBias;
                RightFingertipCorrectionGate=correctionGate;
            }
        }
        if(fingertipPalmRollForContinuity is float resolvedPalmRoll)
        {
            _previousFingertipPalmRoll[sideIndex]=resolvedPalmRoll;
            _previousFingertipPalmRollApplied[sideIndex]=fingertipAppliedPalmRollForContinuity??0;
            _hasPreviousFingertipPalmRoll[sideIndex]=true;
        }
    }
    private void CaptureHandTipSamples()
    {
        var idle=RigClipResolver.FindIdle(_rig.Clips);
        var time=idle>=0?Math.Min(.667,Math.Max(0,_rig.Clips[idle].Duration*.25)):0;
        FillPose(_pose,idle,time);UpdateWorld();
        for(var side=0;side<2;side++)for(var finger=0;finger<5;finger++)
        {
            var chain=_handDigits[side][finger];
            if(chain.Length>=3&&chain[1]>=0&&chain[2]>=0)
                _fingertipSurfaceSamples[side][finger]=BuildFingerTipSamples(side,finger,chain[1],chain[2]);
        }
    }
    private void CaptureHeadSurfacePatch()
    {
        _headSurfacePatch=[];_headSurfaceSeedDistanceMeters=float.NaN;
        if(_head<0||_hips<0||_leftArm<0||_rightArm<0||_rightForeArm<0||_rightHand<0)return;
        var lateral=JointPosition(_leftArm)-JointPosition(_rightArm);
        var up=JointPosition(_head)-JointPosition(_hips);
        if(lateral.LengthSquared()<1e-10f||up.LengthSquared()<1e-10f)return;
        lateral=Vector3.Normalize(lateral);up=Vector3.Normalize(up);
        var forward=Vector3.Cross(lateral,up);if(forward.LengthSquared()<1e-10f)return;
        forward=Vector3.Normalize(forward);up=Vector3.Normalize(Vector3.Cross(forward,lateral));
        var armLength=Vector3.Distance(JointPosition(_rightArm),JointPosition(_rightForeArm))+Vector3.Distance(JointPosition(_rightForeArm),JointPosition(_rightHand));
        // v226 fixed-curl candidate: shift the seed toward the observed
        // camera-visible head-weighted shell; all other motion inputs match v225.
        var seed=JointPosition(_head)+(lateral*(-.22082f)+up*.40f+forward*.24f)*armLength;
        var outward=seed-JointPosition(_head);if(outward.LengthSquared()>1e-10f)outward=Vector3.Normalize(outward);
        var candidates=new List<HeadSurfaceTriangle>();
        for(var mesh=0;mesh<_rig.Meshes.Length;mesh++)
        {
            var binding=_rig.Meshes[mesh];if(binding.Skin<0)continue;
            var indices=_asset.Meshes[mesh].Indices;
            for(var i=0;i+2<indices.Length;i+=3)
            {
                var a=indices[i];var b=indices[i+1];var c=indices[i+2];
                var wa=HeadInfluence(mesh,a);var wb=HeadInfluence(mesh,b);var wc=HeadInfluence(mesh,c);
                if(Math.Min(wa,Math.Min(wb,wc))<.15f||(wa+wb+wc)/3<.55f)continue;
                candidates.Add(new(mesh,a,b,c));
            }
        }
        var bestDistance=float.PositiveInfinity;var bestPoint=Vector3.Zero;var bestNormal=Vector3.Zero;var bestTriangle=default(HeadSurfaceTriangle);
        foreach(var triangle in candidates)
        {
            var a=SkinnedVertexWorld(triangle.Mesh,triangle.A);var b=SkinnedVertexWorld(triangle.Mesh,triangle.B);var c=SkinnedVertexWorld(triangle.Mesh,triangle.C);
            var normal=Vector3.Cross(b-a,c-a);if(normal.LengthSquared()<1e-10f)continue;normal=Vector3.Normalize(normal);
            var center=(a+b+c)/3;
            if(Vector3.Dot(normal,center-JointPosition(_head))<0)normal=-normal;
            if(Vector3.Dot(normal,up)<-.05f||outward.LengthSquared()>1e-10f&&Vector3.Dot(normal,outward)<.10f)continue;
            var point=ClosestPointOnTriangle(seed,a,b,c);var distance=Vector3.Distance(seed,point);
            if(distance<bestDistance){bestDistance=distance;bestPoint=point;bestNormal=normal;bestTriangle=triangle;}
        }
        if(!float.IsFinite(bestDistance))return;
        _headSurfaceSeedDistanceMeters=bestDistance;
        var patch=new List<HeadSurfaceTriangle>();
        foreach(var triangle in candidates)
        {
            if(triangle.Mesh!=bestTriangle.Mesh)continue;
            var a=SkinnedVertexWorld(triangle.Mesh,triangle.A);var b=SkinnedVertexWorld(triangle.Mesh,triangle.B);var c=SkinnedVertexWorld(triangle.Mesh,triangle.C);
            var center=(a+b+c)/3;if(Vector3.DistanceSquared(center,bestPoint)>.0025f)continue;
            var normal=Vector3.Cross(b-a,c-a);if(normal.LengthSquared()<1e-10f)continue;normal=Vector3.Normalize(normal);
            if(Vector3.Dot(normal,center-JointPosition(_head))<0)normal=-normal;
            if(Vector3.Dot(normal,bestNormal)<.20f)continue;
            patch.Add(triangle);
        }
        if(patch.Count==0)patch.Add(bestTriangle);
        _headSurfacePatch=patch.ToArray();
        BuildHeadSurfacePatchOneRingIndices();
    }
    private void BuildHeadSurfacePatchOneRingIndices()
    {
        _headSurfacePatchOneRingIndices=[];
        if(_headSurfacePatch.Length==0)return;
        var edgeTriangles=new Dictionary<(int Mesh,int A,int B),List<int>>();
        static (int Mesh,int A,int B) EdgeKey(HeadSurfaceTriangle triangle,int a,int b)
            =>(triangle.Mesh,Math.Min(a,b),Math.Max(a,b));
        void AddEdge(int patchIndex,int a,int b)
        {
            var key=EdgeKey(_headSurfacePatch[patchIndex],a,b);
            if(!edgeTriangles.TryGetValue(key,out var users))edgeTriangles.Add(key,users=[]);
            users.Add(patchIndex);
        }
        for(var i=0;i<_headSurfacePatch.Length;i++)
        {
            var triangle=_headSurfacePatch[i];
            AddEdge(i,triangle.A,triangle.B);AddEdge(i,triangle.B,triangle.C);AddEdge(i,triangle.C,triangle.A);
        }
        _headSurfacePatchOneRingIndices=new int[_headSurfacePatch.Length][];
        for(var i=0;i<_headSurfacePatch.Length;i++)
        {
            var triangle=_headSurfacePatch[i];var adjacent=new HashSet<int>{i};
            foreach(var key in new[]{EdgeKey(triangle,triangle.A,triangle.B),EdgeKey(triangle,triangle.B,triangle.C),EdgeKey(triangle,triangle.C,triangle.A)})
                if(edgeTriangles.TryGetValue(key,out var users))foreach(var user in users)adjacent.Add(user);
            _headSurfacePatchOneRingIndices[i]=adjacent.Order().ToArray();
        }
        const int maximumSearchRadius=3;
        _headSurfacePatchNeighborhoods=new int[maximumSearchRadius+1][][];
        for(var radius=0;radius<=maximumSearchRadius;radius++)
        {
            var neighborhoods=new int[_headSurfacePatch.Length][];
            for(var root=0;root<_headSurfacePatch.Length;root++)
            {
                var visited=new HashSet<int>{root};var frontier=new List<int>{root};
                for(var step=0;step<radius&&frontier.Count>0;step++)
                {
                    var next=new List<int>();
                    foreach(var current in frontier)foreach(var neighbor in _headSurfacePatchOneRingIndices[current])
                        if(visited.Add(neighbor))next.Add(neighbor);
                    frontier=next;
                }
                neighborhoods[root]=visited.Order().ToArray();
            }
            _headSurfacePatchNeighborhoods[radius]=neighborhoods;
        }
    }
    private float HeadInfluence(int meshIndex,int vertex)
    {
        var binding=_rig.Meshes[meshIndex];if(binding.Skin<0)return 0;
        var skin=_rig.Skins[binding.Skin];var ids=binding.Joints[vertex];var weights=binding.Weights[vertex];var total=0f;
        for(var k=0;k<4;k++)if(skin.Joints[ids[k]]==_head)total+=k switch{0=>weights.X,1=>weights.Y,2=>weights.Z,_=>weights.W};
        return total;
    }
    private Vector3 SkinnedVertexWorld(int meshIndex,int vertex)
    {
        var binding=_rig.Meshes[meshIndex];var local=_localPositions[meshIndex][vertex];
        if(binding.Skin<0)return Vector3.Transform(local,_world[binding.Node]);
        var skin=_rig.Skins[binding.Skin];var ids=binding.Joints[vertex];var weights=binding.Weights[vertex];var point=Vector3.Zero;
        for(var k=0;k<4;k++)
        {
            var weight=k switch{0=>weights.X,1=>weights.Y,2=>weights.Z,_=>weights.W};if(weight<=0)continue;
            var joint=ids[k];if(joint<0||joint>=skin.Joints.Length)continue;
            point+=Vector3.Transform(local,skin.InverseBind[joint]*_world[skin.Joints[joint]])*weight;
        }
        return point;
    }
    private Vector3 SkinnedNormalWorld(int meshIndex,int vertex)
    {
        if(meshIndex<0||meshIndex>=_rig.Meshes.Length||meshIndex>=_localNormals.Length||
            vertex<0||vertex>=_localNormals[meshIndex].Length)return Vector3.Zero;
        var binding=_rig.Meshes[meshIndex];var sourceNormal=_localNormals[meshIndex][vertex];var normal=Vector3.Zero;
        if(binding.Skin<0)
        {
            var matrix=_world[binding.Node];
            var normalMatrix=Matrix4x4.Invert(matrix,out var inverse)?Matrix4x4.Transpose(inverse):Matrix4x4.Identity;
            normal=Vector3.TransformNormal(sourceNormal,normalMatrix);
        }
        else
        {
            var skin=_rig.Skins[binding.Skin];var ids=binding.Joints[vertex];var weights=binding.Weights[vertex];
            for(var k=0;k<4;k++)
            {
                var weight=k switch{0=>weights.X,1=>weights.Y,2=>weights.Z,_=>weights.W};if(weight<=0)continue;
                var joint=ids[k];if(joint<0||joint>=skin.Joints.Length)continue;
                var matrix=skin.InverseBind[joint]*_world[skin.Joints[joint]];
                var normalMatrix=Matrix4x4.Invert(matrix,out var inverse)?Matrix4x4.Transpose(inverse):Matrix4x4.Identity;
                normal+=Vector3.TransformNormal(sourceNormal,normalMatrix)*weight;
            }
        }
        return normal.LengthSquared()>1e-12f&&float.IsFinite(normal.X)&&float.IsFinite(normal.Y)&&float.IsFinite(normal.Z)?Vector3.Normalize(normal):Vector3.Zero;
    }
    private bool TryGetHeadSurfaceGoal(Vector3 seed,Vector3 up,Vector3 forward,Vector2 tangentOffset,Vector2 tangentHeading,float clearance,float contactAmount,
        out Vector3 contactTarget,out Vector3 surfacePoint,out Vector3 surfaceNormal,out Vector3 moveDirection,out float seedDistance,
        out HeadSurfaceProjection seedProjection,out HeadSurfaceProjection contactProjection)
    {
        contactTarget=surfacePoint=surfaceNormal=moveDirection=Vector3.Zero;seedDistance=float.NaN;
        seedProjection=contactProjection=default;
        if(_headSurfacePatch.Length==0)return false;
        if(contactAmount<=.01f)
        {
            _previousHeadSurfaceContactPatchIndex=-1;
            ResetHeadSurfaceNormalRateFilter();
        }
        var topologyRoot=UseHeadSurfaceTopologicalContinuity?_previousHeadSurfaceContactPatchIndex:-1;
        var topologyRadius=topologyRoot>=0?Math.Clamp(HeadSurfaceTopologicalSearchRadius,1,3):0;
        if(!TryFindClosestHeadSurfacePoint(seed,Vector3.Zero,topologyRoot,topologyRadius,out seedProjection))return false;
        seedDistance=seedProjection.Distance;if(seedDistance>.12f)return false;
        // Keep contact-point traversal attached to the selected geometric face.
        // A smoothed orientation is allowed to steer the palm, but must not
        // feed back into the next closest-triangle search and switch the sheet.
        var seedFaceNormal=seedProjection.Normal;
        var tangentUp=up-seedFaceNormal*Vector3.Dot(up,seedFaceNormal);
        RightHeadSurfaceReachUpProjectionLength=tangentUp.Length();
        if(tangentUp.LengthSquared()<1e-8f)tangentUp=forward-seedFaceNormal*Vector3.Dot(forward,seedFaceNormal);
        if(tangentUp.LengthSquared()<1e-8f)return false;
        tangentUp=Vector3.Normalize(tangentUp);
        var tangentForward=Vector3.Cross(seedFaceNormal,tangentUp);
        if(tangentForward.LengthSquared()<1e-8f)return false;
        tangentForward=Vector3.Normalize(tangentForward);
        if(Vector3.Dot(tangentForward,forward)<0)tangentForward=-tangentForward;
        var desired=seedProjection.Point+tangentUp*tangentOffset.X+tangentForward*tangentOffset.Y;
        if(!TryFindClosestHeadSurfacePoint(desired,seedFaceNormal,topologyRoot,topologyRadius,out contactProjection))return false;
        surfacePoint=contactProjection.Point;surfaceNormal=SurfaceNormalForReach(contactProjection);
        if(contactAmount>.01f)surfaceNormal=RateLimitHeadSurfaceNormal(surfaceNormal);
        contactTarget=surfacePoint+surfaceNormal*Math.Clamp(clearance,0,.02f);
        moveDirection=tangentUp*tangentHeading.X+tangentForward*tangentHeading.Y;
        if(moveDirection.LengthSquared()<1e-8f)moveDirection=tangentUp;
        moveDirection=Vector3.Normalize(moveDirection);
        RightHeadSurfaceTopologySearchSucceeded=true;
        if(UseHeadSurfaceTopologicalContinuity&&(contactAmount>=.5f||topologyRoot>=0))
            _previousHeadSurfaceContactPatchIndex=contactProjection.PatchIndex;
        return float.IsFinite(contactTarget.X)&&float.IsFinite(contactTarget.Y)&&float.IsFinite(contactTarget.Z);
    }
    private void ResetHeadSurfaceNormalRateFilter()
    {
        _hasPreviousRateLimitedHeadSurfaceNormal=false;_hasCurrentRateLimitedHeadSurfaceNormal=false;
        _previousRateLimitedHeadSurfaceNormalClip=_currentRateLimitedHeadSurfaceNormalClip=-1;
        _previousRateLimitedHeadSurfaceNormalSeconds=_currentRateLimitedHeadSurfaceNormalSeconds=double.NaN;
    }
    private Vector3 RateLimitHeadSurfaceNormal(Vector3 desired)
    {
        var rate=HeadSurfaceNormalAngularRateLimitRadiansPerSecond;
        if(!float.IsFinite(rate)||rate<=0||desired.LengthSquared()<1e-10f)return desired;
        desired=Vector3.Normalize(desired);
        var clip=_lastPalmRollClip;var seconds=_lastPalmRollSampleSeconds;
        if(_hasCurrentRateLimitedHeadSurfaceNormal&&clip==_currentRateLimitedHeadSurfaceNormalClip&&
            seconds==_currentRateLimitedHeadSurfaceNormalSeconds)
            return _currentRateLimitedHeadSurfaceNormal;
        var filtered=desired;
        if(_hasPreviousRateLimitedHeadSurfaceNormal&&clip==_previousRateLimitedHeadSurfaceNormalClip&&double.IsFinite(seconds))
        {
            var delta=seconds-_previousRateLimitedHeadSurfaceNormalSeconds;
            if(delta==0)filtered=_previousRateLimitedHeadSurfaceNormal;
            else if(delta>0&&delta<=.25)
                filtered=RotateDirectionTowards(_previousRateLimitedHeadSurfaceNormal,desired,rate*(float)delta);
        }
        _currentRateLimitedHeadSurfaceNormal=filtered;_currentRateLimitedHeadSurfaceNormalClip=clip;
        _currentRateLimitedHeadSurfaceNormalSeconds=seconds;_hasCurrentRateLimitedHeadSurfaceNormal=true;
        _previousRateLimitedHeadSurfaceNormal=filtered;_previousRateLimitedHeadSurfaceNormalClip=clip;
        _previousRateLimitedHeadSurfaceNormalSeconds=seconds;_hasPreviousRateLimitedHeadSurfaceNormal=true;
        return filtered;
    }
    private static Vector3 RotateDirectionTowards(Vector3 current,Vector3 target,float maximumRadians)
    {
        if(current.LengthSquared()<1e-10f||target.LengthSquared()<1e-10f||!float.IsFinite(maximumRadians))return target;
        current=Vector3.Normalize(current);target=Vector3.Normalize(target);
        var dot=Math.Clamp(Vector3.Dot(current,target),-1f,1f);
        var angle=MathF.Acos(dot);
        if(angle<=1e-6f||maximumRadians>=angle)return target;
        var axis=Vector3.Cross(current,target);
        if(axis.LengthSquared()<1e-10f)
        {
            var basis=Math.Abs(current.X)<.8f?Vector3.UnitX:Vector3.UnitY;
            axis=Vector3.Cross(current,basis);
        }
        if(axis.LengthSquared()<1e-10f)return target;
        axis=Vector3.Normalize(axis);
        return Vector3.Normalize(Vector3.Transform(current,Quaternion.CreateFromAxisAngle(axis,Math.Clamp(maximumRadians,0,MathF.PI))));
    }
    private Vector3 SurfaceNormalForReach(HeadSurfaceProjection projection)
    {
        if(UseHeadSurfaceOneRingNormalForSolver&&projection.OneRingNormal.LengthSquared()>1e-12f)
            return Vector3.Dot(projection.OneRingNormal,projection.Normal)<0?-projection.OneRingNormal:projection.OneRingNormal;
        if(UseInterpolatedHeadSurfaceNormalForSolver&&projection.InterpolatedNormal.LengthSquared()>1e-12f)
            return Vector3.Dot(projection.InterpolatedNormal,projection.Normal)<0
                ?-projection.InterpolatedNormal:projection.InterpolatedNormal;
        return projection.Normal;
    }
    private bool TryFindClosestHeadSurfacePoint(Vector3 query,Vector3 preferredNormal,int topologyRootPatchIndex,int topologyRadius,out HeadSurfaceProjection projection)
    {
        var bestDistanceSquared=float.PositiveInfinity;projection=default;
        int[]? topologyCandidates=null;
        if(topologyRootPatchIndex>=0&&topologyRootPatchIndex<_headSurfacePatch.Length&&
            topologyRadius>0&&topologyRadius<_headSurfacePatchNeighborhoods.Length)
            topologyCandidates=_headSurfacePatchNeighborhoods[topologyRadius][topologyRootPatchIndex];
        RightHeadSurfaceTopologySearchRestricted=topologyCandidates is not null;
        RightHeadSurfaceTopologyCandidateCount=topologyCandidates?.Length??_headSurfacePatch.Length;
        var candidateCount=topologyCandidates?.Length??_headSurfacePatch.Length;
        for(var candidateOrdinal=0;candidateOrdinal<candidateCount;candidateOrdinal++)
        {
            var patchIndex=topologyCandidates is null?candidateOrdinal:topologyCandidates[candidateOrdinal];
            var triangle=_headSurfacePatch[patchIndex];
            var usedPreviousSamplePositions=false;
            Vector3 Position(int vertex)
            {
                if(UsePreviousHeadSurfaceSamplePositions&&_hasSample&&triangle.Mesh<Positions.Length&&vertex<Positions[triangle.Mesh].Length)
                {
                    var p=Positions[triangle.Mesh][vertex];if(float.IsFinite(p.X)&&float.IsFinite(p.Y)&&float.IsFinite(p.Z))
                    {usedPreviousSamplePositions=true;return p;}
                }
                return SkinnedVertexWorld(triangle.Mesh,vertex);
            }
            var a=Position(triangle.A);var b=Position(triangle.B);var c=Position(triangle.C);
            var cross=Vector3.Cross(b-a,c-a);if(cross.LengthSquared()<1e-10f)continue;
            var rawNormal=Vector3.Normalize(cross);var center=(a+b+c)/3;
            var rawOutwardDot=Vector3.Dot(rawNormal,center-JointPosition(_head));
            var normal=rawOutwardDot<0?-rawNormal:rawNormal;
            if(preferredNormal.LengthSquared()>1e-8f&&Vector3.Dot(normal,preferredNormal)<.15f)continue;
            var point=ClosestPointOnTriangle(query,a,b,c);var distanceSquared=Vector3.DistanceSquared(query,point);
            if(distanceSquared<bestDistanceSquared)
            {bestDistanceSquared=distanceSquared;projection=new(point,normal,rawNormal,rawOutwardDot,MathF.Sqrt(distanceSquared),triangle,usedPreviousSamplePositions){PatchIndex=patchIndex};}
        }
        if(float.IsFinite(bestDistanceSquared)&&(TraceHeadSurfaceOneRingNormal||UseHeadSurfaceOneRingNormalForSolver)&&projection.PatchIndex>=0)
        {
            var weightedNormal=Vector3.Zero;var normalWeight=0f;var usedTriangleCount=0;
            foreach(var neighborIndex in _headSurfacePatchOneRingIndices[projection.PatchIndex])
            {
                var triangle=_headSurfacePatch[neighborIndex];
                var a=SkinnedVertexWorld(triangle.Mesh,triangle.A);var b=SkinnedVertexWorld(triangle.Mesh,triangle.B);var c=SkinnedVertexWorld(triangle.Mesh,triangle.C);
                var areaNormal=Vector3.Cross(b-a,c-a);var twiceArea=areaNormal.Length();if(twiceArea<1e-10f)continue;
                var center=(a+b+c)/3;var headOutward=center-JointPosition(_head);
                if(Vector3.Dot(areaNormal,headOutward)<0)areaNormal=-areaNormal;
                var faceNormal=areaNormal/twiceArea;
                if(Vector3.Dot(faceNormal,projection.Normal)<.20f)continue;
                weightedNormal+=faceNormal*twiceArea;normalWeight+=twiceArea;usedTriangleCount++;
            }
            if(normalWeight>1e-10f&&weightedNormal.LengthSquared()>1e-12f)
            {
                var oneRingNormal=Vector3.Normalize(weightedNormal);
                projection=projection with{OneRingNormal=oneRingNormal,OneRingTriangleCount=usedTriangleCount,
                    FaceOneRingNormalDot=Vector3.Dot(projection.Normal,oneRingNormal)};
            }
        }
        if(float.IsFinite(bestDistanceSquared)&&(TraceHeadSurfaceSmoothNormals||UseInterpolatedHeadSurfaceNormalForSolver))
        {
            var triangle=projection.Triangle;
            var a=SkinnedVertexWorld(triangle.Mesh,triangle.A);var b=SkinnedVertexWorld(triangle.Mesh,triangle.B);var c=SkinnedVertexWorld(triangle.Mesh,triangle.C);
            var barycentric=BarycentricWeights(projection.Point,a,b,c);
            var interpolated=SkinnedNormalWorld(triangle.Mesh,triangle.A)*barycentric.X+
                SkinnedNormalWorld(triangle.Mesh,triangle.B)*barycentric.Y+
                SkinnedNormalWorld(triangle.Mesh,triangle.C)*barycentric.Z;
            if(interpolated.LengthSquared()>1e-12f&&float.IsFinite(interpolated.X)&&float.IsFinite(interpolated.Y)&&float.IsFinite(interpolated.Z))
            {
                interpolated=Vector3.Normalize(interpolated);
                var center=(a+b+c)/3;
                projection=projection with
                {
                    InterpolatedNormal=interpolated,
                    Barycentric=barycentric,
                    InterpolatedOutwardDot=Vector3.Dot(interpolated,center-JointPosition(_head)),
                    FaceInterpolatedNormalDot=Vector3.Dot(projection.Normal,interpolated)
                };
            }
        }
        return float.IsFinite(bestDistanceSquared);
    }
    private static Vector3 BarycentricWeights(Vector3 point,Vector3 a,Vector3 b,Vector3 c)
    {
        var ab=b-a;var ac=c-a;var ap=point-a;
        var d00=Vector3.Dot(ab,ab);var d01=Vector3.Dot(ab,ac);var d11=Vector3.Dot(ac,ac);
        var d20=Vector3.Dot(ap,ab);var d21=Vector3.Dot(ap,ac);var denominator=d00*d11-d01*d01;
        if(!float.IsFinite(denominator)||Math.Abs(denominator)<1e-12f)return new(1f/3,1f/3,1f/3);
        var bWeight=(d11*d20-d01*d21)/denominator;var cWeight=(d00*d21-d01*d20)/denominator;
        var weights=Vector3.Max(Vector3.Zero,new Vector3(1-bWeight-cWeight,bWeight,cWeight));
        var sum=weights.X+weights.Y+weights.Z;
        return sum>1e-8f?weights/sum:new(1f/3,1f/3,1f/3);
    }
    private object TraceHeadProjection(HeadSurfaceProjection projection)
    {
        var triangle=projection.Triangle;
        object Vertex(int vertex)
        {
            var current=SkinnedVertexWorld(triangle.Mesh,vertex);
            var hasPrevious=triangle.Mesh>=0&&triangle.Mesh<Positions.Length&&vertex>=0&&vertex<Positions[triangle.Mesh].Length;
            var previous=hasPrevious?Positions[triangle.Mesh][vertex]:(Vector3?)null;
            return new
            {
                vertex,
                previousSampleWorld=previous is Vector3 p?new[]{p.X,p.Y,p.Z}:null,
                currentSolverWorld=new[]{current.X,current.Y,current.Z},
                previousToCurrentMm=previous is Vector3 old?(float?)(Vector3.Distance(old,current)*1000):null
            };
        }
        return new
        {
            mesh=triangle.Mesh,triangleVertices=new[]{triangle.A,triangle.B,triangle.C},
            usedPreviousSamplePositions=projection.UsedPreviousSamplePositions,
            pointWorld=new[]{projection.Point.X,projection.Point.Y,projection.Point.Z},
            normalWorld=new[]{projection.Normal.X,projection.Normal.Y,projection.Normal.Z},
            rawNormalWorld=new[]{projection.RawNormal.X,projection.RawNormal.Y,projection.RawNormal.Z},rawOutwardDot=projection.RawOutwardDot,
            interpolatedSkinnedVertexNormalWorld=new[]{projection.InterpolatedNormal.X,projection.InterpolatedNormal.Y,projection.InterpolatedNormal.Z},
            barycentricWeights=new[]{projection.Barycentric.X,projection.Barycentric.Y,projection.Barycentric.Z},
            interpolatedOutwardDot=projection.InterpolatedOutwardDot,faceInterpolatedNormalDot=projection.FaceInterpolatedNormalDot,
            distanceMeters=projection.Distance,
            vertices=new[]{Vertex(triangle.A),Vertex(triangle.B),Vertex(triangle.C)}
        };
    }
    private static Vector3 ClosestPointOnTriangle(Vector3 p,Vector3 a,Vector3 b,Vector3 c)
    {
        var ab=b-a;var ac=c-a;var ap=p-a;var d1=Vector3.Dot(ab,ap);var d2=Vector3.Dot(ac,ap);
        if(d1<=0&&d2<=0)return a;
        var bp=p-b;var d3=Vector3.Dot(ab,bp);var d4=Vector3.Dot(ac,bp);
        if(d3>=0&&d4<=d3)return b;
        var vc=d1*d4-d3*d2;if(vc<=0&&d1>=0&&d3<=0)return a+ab*(d1/(d1-d3));
        var cp=p-c;var d5=Vector3.Dot(ab,cp);var d6=Vector3.Dot(ac,cp);
        if(d6>=0&&d5<=d6)return c;
        var vb=d5*d2-d1*d6;if(vb<=0&&d2>=0&&d6<=0)return a+ac*(d2/(d2-d6));
        var va=d3*d6-d5*d4;if(va<=0&&(d4-d3)>=0&&(d5-d6)>=0)return b+(c-b)*((d4-d3)/((d4-d3)+(d5-d6)));
        var denom=1/(va+vb+vc);var v=vb*denom;var w=vc*denom;return a+ab*v+ac*w;
    }
    private FingerSurfaceSample[] BuildFingerTipSamples(int sideIndex,int fingerIndex,int previous,int terminal)
    {
        var axis=JointPosition(terminal)-JointPosition(previous);
        if(axis.LengthSquared()<1e-10f)return Array.Empty<FingerSurfaceSample>();
        axis=Vector3.Normalize(axis);var origin=JointPosition(terminal);
        var candidates=new List<(FingerSurfaceSample Sample,float Projection)>();
        Span<float> fingerChainInfluences=stackalloc float[5];
        for(var meshIndex=0;meshIndex<_rig.Meshes.Length;meshIndex++)
        {
            var binding=_rig.Meshes[meshIndex];if(binding.Skin<0)continue;
            var skin=_rig.Skins[binding.Skin];
            for(var vertex=0;vertex<binding.Joints.Length;vertex++)
            {
                fingerChainInfluences.Clear();
                var weights=binding.Weights[vertex];var joints=binding.Joints[vertex];var point=Vector3.Zero;var terminalInfluence=0f;
                for(var k=0;k<4;k++)
                {
                    var weight=k switch{0=>weights.X,1=>weights.Y,2=>weights.Z,_=>weights.W};
                    if(weight<=0)continue;
                    var joint=joints[k];if(joint<0||joint>=skin.Joints.Length)continue;
                    var node=skin.Joints[joint];if(node==terminal)terminalInfluence+=weight;
                    for(var owner=0;owner<5;owner++)
                    {
                        var chain=_handDigits[sideIndex][owner];
                        for(var chainJoint=0;chainJoint<chain.Length;chainJoint++)
                        {
                            if(chain[chainJoint]!=node)continue;
                            fingerChainInfluences[owner]+=weight;
                            break;
                        }
                    }
                    point+=Vector3.Transform(_localPositions[meshIndex][vertex],skin.InverseBind[joint]*_world[node])*weight;
                }
                if(terminalInfluence<.08f)continue;
                var ownChainInfluence=fingerChainInfluences[fingerIndex];
                var strongestOtherFingerInfluence=0f;
                for(var owner=0;owner<fingerChainInfluences.Length;owner++)
                    if(owner!=fingerIndex)strongestOtherFingerInfluence=Math.Max(strongestOtherFingerInfluence,fingerChainInfluences[owner]);
                // A terminal-bone weight alone does not establish digit identity:
                // a neighboring finger can dominate the same skinned vertex.
                // Reject ambiguous/cross-finger vertices before finding the distal band.
                if(ownChainInfluence<=strongestOtherFingerInfluence+1e-4f)continue;
                var projection=Vector3.Dot(point-origin,axis);
                if(projection<0)continue;
                candidates.Add((new FingerSurfaceSample(meshIndex,vertex,terminalInfluence,ownChainInfluence,strongestOtherFingerInfluence),projection));
            }
        }
        if(candidates.Count==0)return Array.Empty<FingerSurfaceSample>();
        var maximum=candidates.Max(c=>c.Projection);var minimum=candidates.Min(c=>c.Projection);
        var band=Math.Clamp((maximum-minimum)*.15f,.0025f,.008f);
        return candidates.Where(c=>c.Projection>=maximum-band)
            .OrderByDescending(c=>c.Sample.Influence).ThenByDescending(c=>c.Projection)
            .Take(12).Select(c=>c.Sample).ToArray();
    }
    private float EstimateFingertipReach(int hand,int sideIndex,float indexFraction)
    {
        if(TryGetFingertipCenter(sideIndex,indexFraction,out var center))return Vector3.Distance(JointPosition(hand),center);
        var fraction=Math.Clamp(indexFraction,0,1);
        var indexReach=EstimateDigitChainReach(hand,sideIndex,0);
        var middleReach=EstimateDigitChainReach(hand,sideIndex,1);
        if(float.IsFinite(indexReach)&&float.IsFinite(middleReach))
            return middleReach+(indexReach-middleReach)*fraction;
        // This is only a reach-length fallback; contact remains unavailable when
        // the requested surface sample could not be resolved. Never borrow the
        // other digit's length for an exact single-digit request.
        if(fraction<=1e-5f&&float.IsFinite(middleReach))return middleReach;
        if(fraction>=1-1e-5f&&float.IsFinite(indexReach))return indexReach;
        return 0;
    }
    private float EstimateDigitChainReach(int hand,int sideIndex,int fingerIndex)
    {
        var chain=_handDigits[sideIndex][fingerIndex];var distance=0f;var previous=hand;var hasJoint=false;
        foreach(var node in chain)
        {
            if(node<0)continue;
            distance+=Vector3.Distance(JointPosition(previous),JointPosition(node));previous=node;hasJoint=true;
        }
        if(chain[1]>=0&&chain[2]>=0)distance+=Vector3.Distance(JointPosition(chain[1]),JointPosition(chain[2]))*.45f;
        return hasJoint?distance:float.NaN;
    }
    private bool TryGetFingertipCenter(int sideIndex,out Vector3 center)=>TryGetFingertipCenter(sideIndex,.5f,out center,out _,out _);
    private bool TryGetFingertipCenter(int sideIndex,float indexFraction,out Vector3 center)=>TryGetFingertipCenter(sideIndex,indexFraction,out center,out _,out _);
    private bool TryGetFingertipCenter(int sideIndex,out Vector3 center,out Vector3 indexCenter,out Vector3 middleCenter)
        =>TryGetFingertipCenter(sideIndex,.5f,out center,out indexCenter,out middleCenter);
    private bool TryGetFingertipCenter(int sideIndex,float indexFraction,out Vector3 center,out Vector3 indexCenter,out Vector3 middleCenter)
    {
        var index=TryGetFingerSurfaceCenter(_fingertipSurfaceSamples[sideIndex][0],out indexCenter);
        var middle=TryGetFingerSurfaceCenter(_fingertipSurfaceSamples[sideIndex][1],out middleCenter);
        if(!index)indexCenter=new(float.NaN);
        if(!middle)middleCenter=new(float.NaN);
        var fraction=Math.Clamp(indexFraction,0,1);
        if(index&&middle){center=Vector3.Lerp(middleCenter,indexCenter,fraction);return true;}
        // Preserve an explicitly requested single-digit contact, but do not
        // substitute Index for Middle (or vice versa) when that semantic sample
        // is missing. Fractional two-digit contacts require both real samples.
        if(middle&&fraction<=1e-5f){center=middleCenter;return true;}
        if(index&&fraction>=1-1e-5f){center=indexCenter;return true;}
        center=default;return false;
    }
    private bool TryGetFingerSurfaceCenter(FingerSurfaceSample[] samples,out Vector3 center)
    {
        var sum=Vector3.Zero;var total=0f;
        foreach(var sample in samples)
        {
            var binding=_rig.Meshes[sample.Mesh];if(binding.Skin<0)continue;
            var skin=_rig.Skins[binding.Skin];var weights=binding.Weights[sample.Vertex];var joints=binding.Joints[sample.Vertex];var point=Vector3.Zero;
            for(var k=0;k<4;k++)
            {
                var weight=k switch{0=>weights.X,1=>weights.Y,2=>weights.Z,_=>weights.W};
                if(weight<=0)continue;
                var joint=joints[k];if(joint<0||joint>=skin.Joints.Length)continue;
                var node=skin.Joints[joint];point+=Vector3.Transform(_localPositions[sample.Mesh][sample.Vertex],skin.InverseBind[joint]*_world[node])*weight;
            }
            sum+=point*sample.Influence;total+=sample.Influence;
        }
        if(total<=1e-6f){center=default;return false;}
        center=sum/total;return true;
    }
    private void CurlDigit(int[] chain,float amount,Vector3 palm,bool thumb)
    {
        amount=Math.Clamp(amount,0,1);if(amount<=1e-5f)return;
        for(var joint=0;joint<chain.Length;joint++)
        {
            var node=chain[joint];if(node<0)continue;
            var direction=joint+1<chain.Length&&chain[joint+1]>=0?JointPosition(chain[joint+1])-JointPosition(node):Vector3.TransformNormal(Vector3.UnitY,_world[node]);
            var axis=Vector3.Cross(direction,palm);if(axis.LengthSquared()<1e-10f)continue;
            var angle=thumb?(joint==0?.12f:joint==1?.42f:.22f):(joint==0?.95f:joint==1?1.00f:.40f);
            RotateWorld(node,Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis),angle*amount));UpdateWorld();
        }
    }
    private void AimBone(int node,Vector3 current,Vector3 target,Vector3 preferredAxis=default)
    {
        if(current.LengthSquared()<1e-10f||target.LengthSquared()<1e-10f)return;
        current=Vector3.Normalize(current);target=Vector3.Normalize(target);
        var dot=Math.Clamp(Vector3.Dot(current,target),-1,1);
        var cross=Vector3.Cross(current,target);
        Quaternion delta;
        if(dot<-.95f&&preferredAxis.LengthSquared()>1e-8f)
        {
            var stable=preferredAxis-current*Vector3.Dot(preferredAxis,current);
            if(stable.LengthSquared()<1e-8f)
                stable=Vector3.Cross(current,Math.Abs(current.X)<.8f?Vector3.UnitX:Vector3.UnitY);
            stable=Vector3.Normalize(stable);
            var crossLength=cross.Length();
            var axis=crossLength>1e-8f?cross/crossLength:stable;
            var blend=Math.Clamp((-dot-.95f)/.05f,0,1);
            axis=Vector3.Normalize(Vector3.Lerp(axis,stable,blend));
            delta=Quaternion.CreateFromAxisAngle(axis,MathF.Atan2(crossLength,dot));
        }
        else if(dot<-.9999f)
        {
            var axis=Vector3.Cross(current,Math.Abs(current.X)<.8f?Vector3.UnitX:Vector3.UnitY);
            delta=Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis),MathF.PI);
        }
        else delta=Quaternion.Normalize(new Quaternion(Vector3.Cross(current,target),1+dot));
        RotateWorld(node,delta);
    }
    private void RotateWorld(int node,Quaternion delta)
    {
        var rotated=_world[node]*Matrix4x4.CreateFromQuaternion(delta);
        var parent=_rig.Nodes[node].Parent;
        if(parent>=0)
        {
            if(!Matrix4x4.Invert(_world[parent],out var inverse))return;
            rotated*=inverse;
        }
        if(Matrix4x4.Decompose(rotated,out _,out var rotation,out _))
            _pose[node]=_pose[node] with {Rotation=Quaternion.Normalize(rotation)};
    }
    private Vector3 SupportPoint(float leftContact,float rightContact)
    {
        var left=new Vector3(_world[_leftFoot].M41,_world[_leftFoot].M42,_world[_leftFoot].M43);
        var right=new Vector3(_world[_rightFoot].M41,_world[_rightFoot].M42,_world[_rightFoot].M43);
        // An ankle and its toe rotate around each other. Taking only the toe would
        // lift or sink the heel whenever foot pitch changes.
        if(_leftToe>=0)left.Y=Math.Min(left.Y,_world[_leftToe].M42);
        if(_rightToe>=0)right.Y=Math.Min(right.Y,_world[_rightToe].M42);
        return left*leftContact+right*rightContact;
    }
    private void Offset(int node,Quaternion delta)
    {
        if(node<0||delta==Quaternion.Identity)return;
        _pose[node]=_pose[node] with {Rotation=Quaternion.Normalize(_pose[node].Rotation*delta)};
    }
    private void Translate(int node,Vector3 delta)
    {
        if(node<0||!float.IsFinite(delta.X)||!float.IsFinite(delta.Y)||!float.IsFinite(delta.Z))return;
        _pose[node]=_pose[node] with {Translation=_pose[node].Translation+delta};
    }
    private void FillPose(NodePose[] output,int clipIndex,double seconds)
    {
        for(var i=0;i<output.Length;i++)output[i]=_rig.Nodes[i].Rest;
        if(clipIndex<0||clipIndex>=_rig.Clips.Length)return;
        var clip=_rig.Clips[clipIndex];var t=(float)((Math.Max(0,seconds)%clip.Duration));
        foreach(var track in clip.Tracks)
        {
            var times=track.Times;var at=Array.BinarySearch(times,t);if(at<0)at=~at-1;at=Math.Clamp(at,0,times.Length-1);
            var value=track.Values[at];
            if(!track.Step&&at+1<times.Length)
            {
                var mix=Math.Clamp((t-times[at])/(times[at+1]-times[at]),0,1);var b=track.Values[at+1];
                if(track.Path=="rotation")
                {var q=Quaternion.Slerp(new(value.X,value.Y,value.Z,value.W),new(b.X,b.Y,b.Z,b.W),mix);value=new(q.X,q.Y,q.Z,q.W);}
                else value=Vector4.Lerp(value,b,mix);
            }
            var p=output[track.Node];output[track.Node]=track.Path switch
            {"translation"=>p with{Translation=new(value.X,value.Y,value.Z)},"scale"=>p with{Scale=new(value.X,value.Y,value.Z)},_=>p with{Rotation=Quaternion.Normalize(new(value.X,value.Y,value.Z,value.W))}};
        }
    }
}
