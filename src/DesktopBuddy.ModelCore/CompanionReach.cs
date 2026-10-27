using System.Numerics;

namespace DesktopBuddy.ModelCore;

/// <summary>Weighted hand and elbow targets, in arm-length units relative to the shoulder.
/// X points outwards, Y upwards and Z forwards. Keep targets weighted during interruption blends.</summary>
public readonly record struct CompanionReach(Vector3 Hand, Vector3 Elbow, float Weight)
{
    public Vector3 Fingers { get; init; }
    public Vector3 Palm { get; init; }
    // Index, middle, ring and little finger flexion; weighted with the arm target.
    public Vector4 Curl { get; init; }
    public float ThumbCurl { get; init; }
    // MCP adduction in the palm plane, independent of flexion.
    public float FingerGather { get; init; }
    public float HeadAnchor { get; init; }
    public float KnuckleTarget { get; init; }
    // Position the actual index/middle fingertip surface, not the wrist or MCP joint.
    public float FingertipTarget { get; init; }
    // Smoothly gates contact correction independently from finger aiming/retreat.
    public float FingertipContactWeight { get; init; }
    // Fraction of the contact point assigned to the index fingertip; 0.5 means
    // the midpoint between index and middle, 1 means index support only.
    public float FingertipIndexFraction { get; init; }
    public float HeadSurfaceFollow { get; init; }
    public Vector2 HeadSurfaceTangentOffset { get; init; }
    // Keep this direction stable during cyclic scratching; only the contact point moves.
    public Vector2 HeadSurfaceTangentHeading { get; init; }
    public float HeadSurfaceClearance { get; init; }
    public float ForearmAlignment { get; init; }
    // Forward/up clavicle preparation in arm-length units. Its clock is independent of hand travel.
    public Vector2 Clavicle { get; init; }
    public float FullPose { get; init; }
    public static CompanionReach operator +(CompanionReach a, CompanionReach b) => new(a.Hand+b.Hand,a.Elbow+b.Elbow,a.Weight+b.Weight)
    {
        Fingers=a.Fingers+b.Fingers,Palm=a.Palm+b.Palm,Curl=a.Curl+b.Curl,ThumbCurl=a.ThumbCurl+b.ThumbCurl,FingerGather=a.FingerGather+b.FingerGather,
        HeadAnchor=a.HeadAnchor+b.HeadAnchor,KnuckleTarget=a.KnuckleTarget+b.KnuckleTarget,FingertipTarget=a.FingertipTarget+b.FingertipTarget,
        FingertipContactWeight=a.FingertipContactWeight+b.FingertipContactWeight,
        FingertipIndexFraction=a.FingertipIndexFraction+b.FingertipIndexFraction,
        HeadSurfaceFollow=a.HeadSurfaceFollow+b.HeadSurfaceFollow,HeadSurfaceTangentOffset=a.HeadSurfaceTangentOffset+b.HeadSurfaceTangentOffset,HeadSurfaceTangentHeading=a.HeadSurfaceTangentHeading+b.HeadSurfaceTangentHeading,HeadSurfaceClearance=a.HeadSurfaceClearance+b.HeadSurfaceClearance,
        ForearmAlignment=a.ForearmAlignment+b.ForearmAlignment,Clavicle=a.Clavicle+b.Clavicle,FullPose=a.FullPose+b.FullPose
    };
    public static CompanionReach operator *(CompanionReach a, float w) => new(a.Hand*w,a.Elbow*w,a.Weight*w)
    {
        Fingers=a.Fingers*w,Palm=a.Palm*w,Curl=a.Curl*w,ThumbCurl=a.ThumbCurl*w,FingerGather=a.FingerGather*w,
        HeadAnchor=a.HeadAnchor*w,KnuckleTarget=a.KnuckleTarget*w,FingertipTarget=a.FingertipTarget*w,
        FingertipContactWeight=a.FingertipContactWeight*w,
        FingertipIndexFraction=a.FingertipIndexFraction*w,
        HeadSurfaceFollow=a.HeadSurfaceFollow*w,HeadSurfaceTangentOffset=a.HeadSurfaceTangentOffset*w,HeadSurfaceTangentHeading=a.HeadSurfaceTangentHeading*w,HeadSurfaceClearance=a.HeadSurfaceClearance*w,
        ForearmAlignment=a.ForearmAlignment*w,Clavicle=a.Clavicle*w,FullPose=a.FullPose*w
    };
    public bool IsFinite => float.IsFinite(Weight)&&Finite(Hand)&&Finite(Elbow)&&Finite(Fingers)&&Finite(Palm)&&float.IsFinite(Curl.X)&&float.IsFinite(Curl.Y)&&float.IsFinite(Curl.Z)&&float.IsFinite(Curl.W)&&float.IsFinite(ThumbCurl)&&float.IsFinite(FingerGather)&&float.IsFinite(HeadAnchor)&&float.IsFinite(KnuckleTarget)&&float.IsFinite(FingertipTarget)&&float.IsFinite(FingertipContactWeight)&&float.IsFinite(FingertipIndexFraction)&&float.IsFinite(HeadSurfaceFollow)&&Finite(HeadSurfaceTangentOffset)&&Finite(HeadSurfaceTangentHeading)&&float.IsFinite(HeadSurfaceClearance)&&float.IsFinite(ForearmAlignment)&&float.IsFinite(Clavicle.X)&&float.IsFinite(Clavicle.Y)&&float.IsFinite(FullPose);
    private static bool Finite(Vector2 v)=>float.IsFinite(v.X)&&float.IsFinite(v.Y);
    private static bool Finite(Vector3 v)=>float.IsFinite(v.X)&&float.IsFinite(v.Y)&&float.IsFinite(v.Z);

    internal static CompanionActionPose Apply(CompanionActionKind kind,float t,CompanionActionPose pose)
    {
        // Targets describe the task, rather than guessing mirrored Euler angles.
        // The bend plane keeps elbows outside the torso; contact gestures stay in front of it.
        var wave=MathF.Sin(MathF.Tau*2*t);
        CompanionReach Target(float x,float y,float z)=>new(new(x,y,z),new(.50f,-.85f,.30f),1){Fingers=Vector3.UnitY,Palm=Vector3.UnitZ,FingertipIndexFraction=.5f};
        var left=default(CompanionReach);var right=default(CompanionReach);
        switch(kind)
        {
            case CompanionActionKind.Waving:
                right=Target(.40f+.07f*wave,.38f,.36f) with {Elbow=new(.65f,-.35f,.35f)};break;
            case CompanionActionKind.DoubleWaving:
                var waveGate=CompanionTaskTiming.Hold(t,.18f,.31f,.69f,.82f);
                var waveClock=MathF.Tau*2.1f*(t-.23f)/.49f;
                var rw=MathF.Sin(waveClock)*waveGate;var lw=MathF.Sin(waveClock-.65f)*waveGate;
                right=Target(.40f+.025f*rw,.34f,.36f) with {Elbow=new(.65f,-.35f,.35f),FullPose=1,Clavicle=new(.012f,.018f),
                    Fingers=new(.17f*rw,1,.10f),Curl=new(.10f,.13f,.16f,.20f),ThumbCurl=.08f};
                left=Target(.40f-.025f*lw,.34f,.36f) with {Elbow=new(.65f,-.35f,.35f),FullPose=1,Clavicle=new(.012f,.018f),
                    Fingers=new(-.17f*lw,1,.10f),Curl=new(.10f,.13f,.16f,.20f),ThumbCurl=.08f};break;
            case CompanionActionKind.Clapping:
                var spread=.005f+.20f*(.5f+.5f*MathF.Cos(MathF.Tau*3*t));
                left=Target(-.36f+spread,-.28f,.57f) with {Palm=-Vector3.UnitX};right=left;break;
            case CompanionActionKind.Typing:
                float Key(float phase)=>.5f+.5f*MathF.Sin(MathF.Tau*7.2f*t+phase);
                var keyReady=CompanionTaskTiming.Hold(t,.14f,.30f,.70f,.88f);
                left=Target(.03f,-.42f+.0015f*wave,.48f) with {Fingers=Vector3.UnitZ,Palm=-Vector3.UnitY,ForearmAlignment=1,Clavicle=new(.022f,-.006f),
                    Curl=new Vector4(.14f+.16f*Key(0),.16f+.16f*Key(1.8f),.20f+.10f*Key(3.6f),.26f)*keyReady,ThumbCurl=.10f*keyReady};
                right=Target(.03f,-.42f-.0015f*wave,.48f) with {Fingers=Vector3.UnitZ,Palm=-Vector3.UnitY,ForearmAlignment=1,Clavicle=new(.022f,-.006f),
                    Curl=new Vector4(.14f+.16f*Key(2.6f),.16f+.16f*Key(4.4f),.20f+.10f*Key(.9f),.26f)*keyReady,ThumbCurl=.10f*keyReady};break;
            case CompanionActionKind.CoffeeBreak:
                var sip=CompanionTaskTiming.Hold(t,.30f,.49f,.60f,.79f);
                var cupGrip=CompanionTaskTiming.Hold(t,.10f,.28f,.70f,.90f);
                var cupPath=CompanionTaskTiming.Arc(new(.34f,-.44f,.46f),new(.32f,-.13f,.40f),new(.025f,-.025f,.045f),sip);
                right=Target(cupPath.X,cupPath.Y,cupPath.Z)
                    with {HeadAnchor=1,KnuckleTarget=1,Fingers=new(-.25f+.05f*sip,.72f+.16f*sip,.60f-.48f*sip),Palm=-Vector3.UnitX,
                        Elbow=new(.30f+.25f*sip,-1.0f+.52f*sip,.22f+.06f*sip),Clavicle=new(.025f,.015f),Curl=new Vector4(.62f,.66f,.70f,.70f)*cupGrip,ThumbCurl=.45f*cupGrip};break;
            case CompanionActionKind.EatingSnack:
                var bite=CompanionTaskTiming.Hold(t,.28f,.47f,.59f,.79f);
                var pinch=CompanionTaskTiming.Hold(t,.12f,.30f,.69f,.90f);
                // Keep the palm below and beside the lips; approach with the fingertips,
                // rather than laying the entire hand horizontally across the face.
                var snackPath=CompanionTaskTiming.Arc(new(.32f,-.48f,.46f),new(.21f,-.19f,.36f),new(.025f,-.018f,.040f),bite);
                right=Target(snackPath.X,snackPath.Y,snackPath.Z)
                    with {HeadAnchor=1,KnuckleTarget=1,Fingers=new(-.18f-.20f*bite,.35f+.53f*bite,.85f-.95f*bite),Palm=Vector3.Lerp(-Vector3.UnitY,-Vector3.UnitX,bite),
                        Elbow=new(.30f-.05f*bite,-1.0f+.15f*bite,.22f),Clavicle=new(.020f,.012f),Curl=new Vector4(.16f,.40f,.48f,.50f)*pinch,ThumbCurl=.36f*pinch};break;
            case CompanionActionKind.BlowingKiss:
                var send=Math.Clamp((t-.40f)/.30f,0,1);send=send*send*(3-2*send);
                right=Target(-.18f+.34f*send,.20f-.06f*send,.40f+.34f*send)
                    with {Fingers=Vector3.Lerp(new(-.85f,.25f,0),Vector3.UnitY,send),Palm=-Vector3.UnitZ};break;
            case CompanionActionKind.Yawning:
                right=Target(-.18f,.04f,.42f) with {Palm=-Vector3.UnitZ};break;
            case CompanionActionKind.Thinking:
                var chinGrip=CompanionTaskTiming.Hold(t,.16f,.37f,.67f,.91f);
                var chinSettle=CompanionTaskTiming.Hold(t,.27f,.43f,.60f,.79f);
                var lookAside=CompanionTaskTiming.Hold(t,.43f,.59f,.66f,.84f);
                // Rest the index fingertip beneath the chin and to the side of the
                // mouth; the prior high/front target placed the hand over the lips.
                right=Target(.12f-.003f*chinSettle,-.18f+.003f*chinSettle-.002f*lookAside,.16f-.002f*chinSettle+.002f*lookAside)
                    with {HeadAnchor=1,FingertipTarget=1,FingertipContactWeight=chinSettle,FingertipIndexFraction=1,Fingers=new(.016f,.993f,.083f),Palm=new(-.1898f,.0847f,-.9782f),
                    Elbow=new(.48f,-.74f,.22f),Clavicle=new(.016f,.006f),FingerGather=.75f*chinGrip,
                    Curl=new Vector4(.15f,.50f,.56f,.60f)*chinGrip,ThumbCurl=.22f*chinGrip};break;
            case CompanionActionKind.Facepalming:
                right=Target(-.13f,.31f,.44f) with {Palm=-Vector3.UnitZ};break;
            case CompanionActionKind.HeartHands:
                left=Target(-.195f,-.28f,.49f) with {Fingers=new(-.65f,.76f,0)};right=left;break;
            case CompanionActionKind.SendingLove:
                var release=Math.Clamp((t-.35f)/.25f,0,1);
                release=release*release*(3-2*release);
                left=Target(-.195f+1.10f*release,-.28f+.38f*release,.49f-.25f*release)
                    with {Fingers=Vector3.Lerp(new(-.65f,.76f,0),Vector3.UnitY,release)};
                right=left;break;
            case CompanionActionKind.Hugging:
                var gather=CompanionTaskTiming.Ease((t-.28f)/.32f);
                var palmTurn=CompanionTaskTiming.Ease((t-.23f)/.25f);
                // Gather around an imagined partner while keeping the wrists apart.
                // Inward, nearly horizontal fingers finish the arc instead of forming praying hands.
                left=Target(.62f-.54f*gather,-.10f-.25f*gather,.34f+.30f*gather)
                    with {Fingers=Vector3.Lerp(new(.75f,.65f,.45f),new(-.85f,-.05f,.50f),palmTurn),
                        Palm=Vector3.Lerp(-Vector3.UnitX,new(-.75f,0,-.25f),palmTurn),Elbow=new(.90f,-.30f,.10f),
                        FullPose=1,Clavicle=new(.020f,.008f),Curl=new(.15f,.20f,.25f,.30f),ThumbCurl=.10f};
                right=left;break;
            case CompanionActionKind.Saluting:
                right=Target(.02f,.37f,.34f);break;
            case CompanionActionKind.ConfidentPose:
                left=Target(.02f,-.55f,.16f) with {Fingers=new(-.45f,-.89f,.04f),Palm=-Vector3.UnitX,Elbow=new(.86f,-.32f,-.04f),
                    FullPose=1,Clavicle=new(.005f,-.010f),Curl=new(.12f,.14f,.18f,.20f),ThumbCurl=.16f};
                right=left;break;
            case CompanionActionKind.HeadScratching:
                // Keep the existing head target and maximum range. One eased
                // stroke drives both tangent axes; the second bows from the first
                // so the contact path curves and returns without added reach.
                var scratchContact=CompanionActionMotion.HeadScratchContact(t);
                var scratchStroke=CompanionActionMotion.HeadScratchStroke(t);
                var scratchGrip=.5f+.5f*scratchStroke;
                var elbowFollow=CompanionActionMotion.HeadScratchStroke(t-.0125f)*scratchContact;
                // Keep the one middle-finger contact pad steady during full
                // contact. The other digits form a curved, slightly closing
                // scratch hand; the suspect Index proxy is not treated as truth.
                var indexScratchCurl=.30f+.28f*scratchContact*scratchGrip;
                var middleScratchCurl=.22f+.17f*scratchContact;
                var ringScratchCurl=.38f+.14f*scratchContact*scratchGrip;
                var littleScratchCurl=.46f+.14f*scratchContact*scratchGrip;
                var thumbScratchCurl=.14f+.10f*scratchContact*scratchGrip;
                var scratchBow=1-scratchStroke*scratchStroke;
                var scratchTangentOffset=new Vector2(.014f*scratchStroke,.0045f*scratchBow)*scratchContact;
                var scratchTangentHeading=Vector2.UnitX;
                right=Target(.22082f,.40f,.080f)
                    with {HeadAnchor=1,HeadSurfaceFollow=1,HeadSurfaceTangentOffset=scratchTangentOffset,HeadSurfaceTangentHeading=scratchTangentHeading,HeadSurfaceClearance=.002f,
                        FingertipTarget=1,FingertipContactWeight=scratchContact,FingertipIndexFraction=0,Fingers=new(.36293f,.931806f,-.004351f),Palm=new(-.931739f,.362956f,.01117f),
                        Elbow=new(.55f+.006f*elbowFollow,-.42f+.004f*elbowFollow,.14f),Clavicle=new(.015f,.018f),FingerGather=.10f,
                        Curl=new(indexScratchCurl,middleScratchCurl,ringScratchCurl,littleScratchCurl),ThumbCurl=thumbScratchCurl};break;
            case CompanionActionKind.Beckoning:
                right=Target(.25f,-.08f+.025f*wave,.58f+.08f*wave)
                    with {Palm=-Vector3.UnitZ,Elbow=new(.55f,-.60f,.35f)};break;
        }
        return pose with { LeftReach=left, RightReach=right };
    }
}
