using System.Numerics;

namespace DesktopBuddy.ModelCore;

public enum CompanionActionKind
{
    None,
    Idle,
    RunningRight,
    RunningLeft,
    Waving,
    Jumping,
    Failed,
    Waiting,
    Running,
    Review,
    Celebrating,
    Thinking,
    Curious,
    Stretching,
    Dancing,
    Sleepy,
    Shy,
    Bowing,
    Playful,
    Nodding,
    LookingAround,
    Surprised,
    Laughing,
    Clapping,
    BlowingKiss,
    Curtsy,
    Peek,
    Typing,
    CoffeeBreak,
    EatingSnack,
    Resting,
    Saluting,
    HeadScratching,
    ShakingHead,
    Yawning,
    WakingUp,
    Swaying,
    StumbleRecover,
    AskingQuestion,
    VictoryPose,
    Squatting,
    Beckoning,
    Hugging,
    Pointing,
    ImpatientFootTap,
    Shrugging,
    Sneaking,
    Listening,
    Cheering,
    Facepalming,
    DoubleWaving,
    SideStepping,
    AirGuitar,
    HeartHands,
    Shivering,
    ConfidentPose,
    SendingLove,
}

public readonly record struct CompanionActionDefinition(
    CompanionActionKind Kind,
    string Id,
    string Label,
    string PetDexState,
    double DurationSeconds);

/// <summary>Small additive skeletal offsets. All values are local-rig radians or meters.</summary>
public readonly record struct CompanionActionPose(
    Vector3 Body,
    Vector3 Head,
    float LeftArm,
    float LeftForeArm,
    float LeftHand,
    float RightArm,
    float RightForeArm,
    float RightHand,
    float LeftLeg,
    float RightLeg,
    float LeftKnee,
    float RightKnee,
    float HipLift)
{
    public float LeftArmPitch { get; init; }
    public CompanionReach LeftReach { get; init; }
    public CompanionReach RightReach { get; init; }
    public float LeftForeArmPitch { get; init; }
    public float RightArmPitch { get; init; }
    public float RightForeArmPitch { get; init; }
    public float LeftFootPitch { get; init; }
    public float RightFootPitch { get; init; }
    public float SupportWeight { get; init; }
    public float HorizontalSupportWeight { get; init; }
    // Time and direction are weighted with the pose, so an interrupted run can
    // crossfade through the existing interaction blend without restarting its phase.
    public float NativeRunningWeight { get; init; }
    public float NativeRunningSeconds { get; init; }
    public float NativeRunningDirection { get; init; }
    // Relative contact confidence; normalized by RigSampler, independently of blend weight.
    public float LeftFootContact { get; init; }
    public float RightFootContact { get; init; }
    public float LeftLegSide { get; init; }
    public float RightLegSide { get; init; }

    public static CompanionActionPose operator +(CompanionActionPose a, CompanionActionPose b) => new(
        a.Body + b.Body, a.Head + b.Head,
        a.LeftArm + b.LeftArm, a.LeftForeArm + b.LeftForeArm, a.LeftHand + b.LeftHand,
        a.RightArm + b.RightArm, a.RightForeArm + b.RightForeArm, a.RightHand + b.RightHand,
        a.LeftLeg + b.LeftLeg, a.RightLeg + b.RightLeg,
        a.LeftKnee + b.LeftKnee, a.RightKnee + b.RightKnee, a.HipLift + b.HipLift)
    {
        LeftReach = a.LeftReach + b.LeftReach, RightReach = a.RightReach + b.RightReach,
        LeftArmPitch = a.LeftArmPitch + b.LeftArmPitch, LeftForeArmPitch = a.LeftForeArmPitch + b.LeftForeArmPitch,
        RightArmPitch = a.RightArmPitch + b.RightArmPitch, RightForeArmPitch = a.RightForeArmPitch + b.RightForeArmPitch,
        LeftFootPitch = a.LeftFootPitch + b.LeftFootPitch, RightFootPitch = a.RightFootPitch + b.RightFootPitch,
        SupportWeight = a.SupportWeight + b.SupportWeight,
        HorizontalSupportWeight = a.HorizontalSupportWeight + b.HorizontalSupportWeight,
        NativeRunningWeight = a.NativeRunningWeight + b.NativeRunningWeight,
        NativeRunningSeconds = a.NativeRunningSeconds + b.NativeRunningSeconds,
        NativeRunningDirection = a.NativeRunningDirection + b.NativeRunningDirection,
        LeftFootContact = a.LeftFootContact + b.LeftFootContact,
        RightFootContact = a.RightFootContact + b.RightFootContact,
        LeftLegSide = a.LeftLegSide + b.LeftLegSide, RightLegSide = a.RightLegSide + b.RightLegSide,
    };

    public static CompanionActionPose operator *(CompanionActionPose pose, float weight) => new(
        pose.Body * weight, pose.Head * weight,
        pose.LeftArm * weight, pose.LeftForeArm * weight, pose.LeftHand * weight,
        pose.RightArm * weight, pose.RightForeArm * weight, pose.RightHand * weight,
        pose.LeftLeg * weight, pose.RightLeg * weight,
        pose.LeftKnee * weight, pose.RightKnee * weight, pose.HipLift * weight)
    {
        LeftReach = pose.LeftReach * weight, RightReach = pose.RightReach * weight,
        LeftArmPitch = pose.LeftArmPitch * weight, LeftForeArmPitch = pose.LeftForeArmPitch * weight,
        RightArmPitch = pose.RightArmPitch * weight, RightForeArmPitch = pose.RightForeArmPitch * weight,
        LeftFootPitch = pose.LeftFootPitch * weight, RightFootPitch = pose.RightFootPitch * weight,
        SupportWeight = pose.SupportWeight * weight,
        HorizontalSupportWeight = pose.HorizontalSupportWeight * weight,
        NativeRunningWeight = pose.NativeRunningWeight * weight,
        NativeRunningSeconds = pose.NativeRunningSeconds * weight,
        NativeRunningDirection = pose.NativeRunningDirection * weight,
        LeftFootContact = pose.LeftFootContact * weight, RightFootContact = pose.RightFootContact * weight,
        LeftLegSide = pose.LeftLegSide * weight, RightLegSide = pose.RightLegSide * weight,
    };

    public static CompanionActionPose Lerp(CompanionActionPose from, CompanionActionPose to, float amount)
    {
        var t=Math.Clamp(amount,0,1);return from*(1-t)+to*t;
    }

    public bool IsFinite =>
        LeftReach.IsFinite && RightReach.IsFinite &&
        float.IsFinite(Body.X) && float.IsFinite(Body.Y) && float.IsFinite(Body.Z) &&
        float.IsFinite(Head.X) && float.IsFinite(Head.Y) && float.IsFinite(Head.Z) &&
        float.IsFinite(LeftArm) && float.IsFinite(LeftForeArm) && float.IsFinite(LeftHand) &&
        float.IsFinite(RightArm) && float.IsFinite(RightForeArm) && float.IsFinite(RightHand) &&
        float.IsFinite(LeftLeg) && float.IsFinite(RightLeg) &&
        float.IsFinite(LeftKnee) && float.IsFinite(RightKnee) && float.IsFinite(HipLift) &&
        float.IsFinite(LeftArmPitch) && float.IsFinite(LeftForeArmPitch) &&
        float.IsFinite(RightArmPitch) && float.IsFinite(RightForeArmPitch) &&
        float.IsFinite(LeftLegSide) && float.IsFinite(RightLegSide) &&
        float.IsFinite(LeftFootPitch) && float.IsFinite(RightFootPitch) && float.IsFinite(SupportWeight) &&
        float.IsFinite(LeftFootContact) && float.IsFinite(RightFootContact) && float.IsFinite(HorizontalSupportWeight) &&
        float.IsFinite(NativeRunningWeight) && float.IsFinite(NativeRunningSeconds) && float.IsFinite(NativeRunningDirection);
}

/// <summary>
/// A compact PetDex-inspired action library for the Mixamo-compatible rig. Actions are
/// timestamp sampled, allocation-free, deterministic, interruptible, and return to rest.
/// </summary>
public static class CompanionActionMotion
{
    private static readonly CompanionActionDefinition[] ActionDefinitions =
    [
        new(CompanionActionKind.Idle, "idle", "待机", "idle", 1.10),
        new(CompanionActionKind.RunningRight, "running-right", "向右跑", "running-right", 1.06),
        new(CompanionActionKind.RunningLeft, "running-left", "向左跑", "running-left", 1.06),
        new(CompanionActionKind.Waving, "waving", "挥手", "waving", 2.80),
        new(CompanionActionKind.Jumping, "jumping", "跳跃", "jumping", .84),
        new(CompanionActionKind.Failed, "failed", "受挫", "failed", 1.22),
        new(CompanionActionKind.Waiting, "waiting", "等候", "waiting", 1.01),
        new(CompanionActionKind.Running, "running", "原地跑", "running", .82),
        new(CompanionActionKind.Review, "review", "认真检查", "review", 1.03),
        new(CompanionActionKind.Celebrating, "celebrating", "欢呼", "celebrating", 1.25),
        new(CompanionActionKind.Thinking, "thinking", "思考", "review", 4.00),
        new(CompanionActionKind.Curious, "curious", "好奇张望", "waiting", 1.20),
        new(CompanionActionKind.Stretching, "stretching", "伸懒腰", "idle", 1.90),
        new(CompanionActionKind.Dancing, "dancing", "跳舞", "running", 2.40),
        new(CompanionActionKind.Sleepy, "sleepy", "困倦点头", "waiting", 1.90),
        new(CompanionActionKind.Shy, "shy", "害羞", "failed", 1.40),
        new(CompanionActionKind.Bowing, "bowing", "鞠躬", "review", 1.15),
        new(CompanionActionKind.Playful, "playful", "俏皮", "jumping", 1.30),
        new(CompanionActionKind.Nodding, "nodding", "点头", "waiting", 1.05),
        new(CompanionActionKind.LookingAround, "looking-around", "四处张望", "waiting", 1.60),
        new(CompanionActionKind.Surprised, "surprised", "惊讶", "failed", 1.05),
        new(CompanionActionKind.Laughing, "laughing", "开心摆动", "celebrating", 1.45),
        new(CompanionActionKind.Clapping, "clapping", "鼓掌", "celebrating", 2.80),
        new(CompanionActionKind.BlowingKiss, "blowing-kiss", "飞吻", "waving", 1.20),
        new(CompanionActionKind.Curtsy, "curtsy", "屈膝礼", "review", 1.65),
        new(CompanionActionKind.Peek, "peek", "探头看看", "curious", 1.30),
        new(CompanionActionKind.Typing, "typing", "模拟打字", "typing", 4.60),
        new(CompanionActionKind.CoffeeBreak, "coffee-break", "举杯示意", "coffee", 4.60),
        new(CompanionActionKind.EatingSnack, "eating-snack", "进食示意", "burger", 4.20),
        new(CompanionActionKind.Resting, "resting", "放松休息", "resting", 2.30),
        new(CompanionActionKind.Saluting, "saluting", "敬礼", "waving", 2.20),
        new(CompanionActionKind.HeadScratching, "head-scratching", "挠头思考", "review", 4.00),
        new(CompanionActionKind.ShakingHead, "shaking-head", "摇头否认", "failed", 1.20),
        new(CompanionActionKind.Yawning, "yawning", "打哈欠", "resting", 1.75),
        new(CompanionActionKind.WakingUp, "waking-up", "睡醒伸展", "resting", 2.10),
        new(CompanionActionKind.Swaying, "swaying", "轻轻摇摆", "idle", 1.85),
        new(CompanionActionKind.StumbleRecover, "stumble-recover", "踉跄站稳", "failed", 1.35),
        new(CompanionActionKind.AskingQuestion, "asking-question", "举手提问", "waiting", 1.20),
        new(CompanionActionKind.VictoryPose, "victory-pose", "单手胜利", "celebrating", 1.35),
        new(CompanionActionKind.Squatting, "squatting", "蹲下观察", "curious", 1.90),
        new(CompanionActionKind.Beckoning, "beckoning", "招手示意", "waving", 1.35),
        new(CompanionActionKind.Hugging, "hugging", "张臂拥抱", "celebrating", 3.60),
        new(CompanionActionKind.Pointing, "pointing", "指引方向", "curious", 1.20),
        new(CompanionActionKind.ImpatientFootTap, "impatient-foot-tap", "等得跺脚", "waiting", 1.70),
        new(CompanionActionKind.Shrugging, "shrugging", "无奈耸肩", "failed", 1.15),
        new(CompanionActionKind.Sneaking, "sneaking", "踮步观察", "running", 1.65),
        new(CompanionActionKind.Listening, "listening", "侧耳倾听", "review", 1.35),
        new(CompanionActionKind.Cheering, "cheering", "交替欢呼", "celebrating", 1.50),
        new(CompanionActionKind.Facepalming, "facepalming", "捂脸", "failed", 1.40),
        new(CompanionActionKind.DoubleWaving, "double-waving", "双手挥挥", "waving", 3.60),
        new(CompanionActionKind.SideStepping, "side-stepping", "左右踏步", "running", 1.80),
        new(CompanionActionKind.AirGuitar, "air-guitar", "空气吉他", "running", 2.20),
        new(CompanionActionKind.HeartHands, "heart-hands", "捧心", "celebrating", 1.50),
        new(CompanionActionKind.Shivering, "shivering", "冷得发抖", "waiting", 1.70),
        new(CompanionActionKind.ConfidentPose, "confident-pose", "得意叉腰", "review", 2.60),
        new(CompanionActionKind.SendingLove, "sending-love", "送出爱心", "waving", 1.55),
    ];

    public static IReadOnlyList<CompanionActionDefinition> Library { get; } = Array.AsReadOnly(ActionDefinitions);
    public static ReadOnlySpan<CompanionActionDefinition> Definitions => ActionDefinitions;

    public static bool TryGetDefinition(CompanionActionKind kind, out CompanionActionDefinition definition)
    {
        foreach (var candidate in ActionDefinitions)
        {
            if (candidate.Kind != kind) continue;
            definition = candidate;
            return true;
        }
        definition = default;
        return false;
    }

    public static CompanionActionPose Sample(CompanionActionKind kind, double seconds)
    {
        if (kind is CompanionActionKind.None or CompanionActionKind.Idle || !double.IsFinite(seconds) || seconds < 0 ||
            !TryGetDefinition(kind, out var definition) || seconds >= definition.DurationSeconds)
            return default;

        var t = (float)(seconds / definition.DurationSeconds);
        var task=CompanionTaskTiming.Applies(kind);
        var headContactAction=kind is CompanionActionKind.Thinking or CompanionActionKind.HeadScratching;
        var enter = task?CompanionTaskTiming.Ease(t/(headContactAction?.20f:.14f)):Smooth(t/.12f);
        var leaveStart=headContactAction?.74f:.80f;var leaveLength=headContactAction?.26f:.20f;
        var leave = 1 - (task?CompanionTaskTiming.Ease((t-leaveStart)/leaveLength):Smooth((t-.80f)/.20f));
        var envelope = enter * leave;
        var beat = MathF.Sin(MathF.Tau * t);
        var quickBeat = MathF.Sin(MathF.Tau * 2 * t + .35f);
        var pose = kind switch
        {
            CompanionActionKind.RunningRight => Run(t, 1),
            CompanionActionKind.RunningLeft => Run(t, -1),
            CompanionActionKind.Waving => Wave(t,beat),
            CompanionActionKind.Jumping => Jump(t),
            CompanionActionKind.Failed => new(
                new Vector3(-.015f, .14f * envelope, -.035f), new Vector3(-.025f, .12f * envelope, -.04f),
                .08f * envelope, -.10f * envelope, 0, -.08f * envelope, -.10f * envelope, 0,
                .04f * envelope, -.04f * envelope, .10f * envelope, .10f * envelope, -.012f * envelope),
            CompanionActionKind.Waiting => new(
                new Vector3(.055f * beat, .012f, .025f * beat), new Vector3(.08f * beat, .01f, -.035f),
                .035f, .04f, 0, -.035f, -.04f, 0, -.08f * beat, .08f * beat, .025f, .025f, .004f * MathF.Max(0, beat)),
            CompanionActionKind.Running => Run(t, 0),
            CompanionActionKind.Review => new(
                new Vector3(.012f, -.085f * envelope, .015f), new Vector3(.025f, .075f * envelope, -.015f),
                .04f, -.12f, 0, -.48f, -.95f, .04f,
                0, 0, 0, 0, -.004f * envelope),
            CompanionActionKind.Celebrating => new(
                new Vector3(.045f * beat, -.015f, .025f * beat), new Vector3(.035f * quickBeat, -.01f, .02f),
                2.35f * envelope, -.15f * envelope, -.05f, -2.35f * envelope, -.15f * envelope, .05f,
                .08f * beat, -.08f * beat, .04f * MathF.Max(0, beat), .04f * MathF.Max(0, beat), .035f * MathF.Max(0, beat)),
            CompanionActionKind.Thinking => Think(t),
            CompanionActionKind.Curious => new(
                new Vector3(-.045f * envelope, -.035f, .025f), new Vector3(.17f * beat, -.02f, .105f * envelope),
                .015f, 0, 0, -.025f, .015f, 0, 0, 0, 0, 0, 0),
            CompanionActionKind.Stretching => Stretch(t, envelope),
            CompanionActionKind.Dancing => Dance(t, beat, quickBeat),
            CompanionActionKind.Sleepy => new(
                new Vector3(.012f * beat, .105f * envelope, -.018f), new Vector3(.025f * beat, .13f * envelope + .035f * MathF.Max(0, -quickBeat), .025f),
                .035f, .12f, 0, -.035f, -.12f, 0,
                .035f * beat, -.035f * beat, .03f * MathF.Max(0, -beat), .03f * MathF.Max(0, beat), -.008f * envelope),
            CompanionActionKind.Shy => new(
                new Vector3(-.045f * beat, .045f, -.025f), new Vector3(.09f * beat, .035f, -.10f * envelope),
                -.10f * envelope, -.48f * envelope, 0, .10f * envelope, .48f * envelope, 0,
                -.03f * beat, .03f * beat, 0, 0, 0),
            CompanionActionKind.Bowing => new(
                new Vector3(0, .16f * envelope, 0), new Vector3(0, .10f * envelope, 0),
                .04f, .04f, 0, -.04f, -.04f, 0, 0, 0, 0, 0, 0),
            CompanionActionKind.Playful => new(
                new Vector3(.04f * beat, -.025f, .035f * beat), new Vector3(.11f * quickBeat, -.02f, .10f * beat),
                .28f * envelope, -.08f, 0, -.55f * envelope, -.92f * envelope, .16f * quickBeat,
                .18f * MathF.Max(0, beat), -.14f * MathF.Max(0, -beat), .08f * MathF.Max(0, -beat), .08f * MathF.Max(0, beat), .025f * MathF.Max(0, beat)),
            CompanionActionKind.Nodding => new(
                new Vector3(.012f * beat, .006f, .01f * MathF.Sin(MathF.Tau * t)), new Vector3(.015f, .20f * MathF.Sin(MathF.Tau * 1.5f * t) * envelope, .025f * beat),
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            CompanionActionKind.LookingAround => new(
                new Vector3(.015f * beat, 0, .065f * beat), new Vector3(.22f * MathF.Sin(MathF.Tau * 1.2f * t) * envelope, .018f, .035f * beat),
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            CompanionActionKind.Surprised => new(
                new Vector3(.045f * beat, .20f * envelope, .025f), new Vector3(.12f * beat, -.11f * envelope, .06f * quickBeat),
                -.68f * envelope, -.18f * envelope, 0, .68f * envelope, -.18f * envelope, 0,
                -.06f * envelope, .06f * envelope, .12f * envelope, .12f * envelope, .025f * MathF.Max(0, beat)),
            CompanionActionKind.Laughing => new(
                new Vector3(.075f * beat, .16f * MathF.Max(0, beat), .035f * quickBeat), new Vector3(.045f * beat, .15f * MathF.Max(0, quickBeat), .04f * beat),
                -.12f * envelope, -.16f * MathF.Max(0, quickBeat), 0, .12f * envelope, -.16f * MathF.Max(0, quickBeat), 0,
                .04f * MathF.Max(0, beat), -.04f * MathF.Max(0, beat), .08f * MathF.Max(0, quickBeat), .08f * MathF.Max(0, quickBeat), .012f * MathF.Max(0, beat)),
            CompanionActionKind.Clapping => Clap(t, envelope),
            CompanionActionKind.BlowingKiss => new(
                new Vector3(.025f * beat, .02f, .025f), new Vector3(.14f, -.08f * envelope, -.04f),
                0, 0, 0, .82f * envelope, -.98f * envelope, .35f * MathF.Sin(MathF.Tau * 2 * t) * envelope,
                0, 0, 0, 0, 0),
            CompanionActionKind.Curtsy => Curtsy(t),
            CompanionActionKind.Peek => new(
                new Vector3(.12f * beat * envelope, -.025f, .04f * beat), new Vector3(.16f * MathF.Sin(MathF.Tau * 1.3f * t) * envelope, .015f, .08f * envelope),
                -.16f * envelope, .08f, 0, .18f * envelope, -.06f, 0, 0, 0, 0, 0, 0),
            CompanionActionKind.Typing => Typing(t),
            CompanionActionKind.CoffeeBreak => CoffeeBreak(t),
            CompanionActionKind.EatingSnack => EatingSnack(t),
            CompanionActionKind.Resting => Resting(t),
            CompanionActionKind.Saluting => Saluting(t),
            CompanionActionKind.HeadScratching => HeadScratching(t),
            CompanionActionKind.ShakingHead => ShakingHead(t, beat),
            CompanionActionKind.Yawning => Yawning(t, beat),
            CompanionActionKind.WakingUp => WakingUp(t, beat),
            CompanionActionKind.Swaying => Swaying(t, beat, quickBeat),
            CompanionActionKind.StumbleRecover => StumbleRecover(t),
            CompanionActionKind.AskingQuestion => AskingQuestion(t),
            CompanionActionKind.VictoryPose => VictoryPose(t, beat),
            CompanionActionKind.Squatting => Squatting(t, beat),
            CompanionActionKind.Beckoning => Beckoning(t),
            CompanionActionKind.Hugging => Hugging(t),
            CompanionActionKind.Pointing => Pointing(t),
            CompanionActionKind.ImpatientFootTap => ImpatientFootTap(t),
            CompanionActionKind.Shrugging => Shrugging(t),
            CompanionActionKind.Sneaking => Sneaking(t),
            CompanionActionKind.Listening => Listening(t),
            CompanionActionKind.Cheering => Cheering(t),
            CompanionActionKind.Facepalming => Facepalming(t),
            CompanionActionKind.DoubleWaving => DoubleWaving(t),
            CompanionActionKind.SideStepping => SideStepping(t),
            CompanionActionKind.AirGuitar => AirGuitar(t),
            CompanionActionKind.HeartHands => HeartHands(t),
            CompanionActionKind.Shivering => Shivering(t),
            CompanionActionKind.ConfidentPose => ConfidentPose(t),
            CompanionActionKind.SendingLove => SendingLove(t),
            _ => default,
        };
        if(kind is CompanionActionKind.RunningRight or CompanionActionKind.RunningLeft)
            pose=pose with { NativeRunningWeight=1, NativeRunningSeconds=(float)(seconds * .82),
                NativeRunningDirection=kind==CompanionActionKind.RunningRight?1:-1 };
        pose=CompanionReach.Apply(kind,t,pose);
        return CompanionTaskTiming.Layer(kind,t,pose,pose*Math.Clamp(envelope,0,1));
    }

    // One shared contact clock and stroke profile keep finger flex, the elbow
    // target, and the head-surface path on the same 1.4 Hz scratch rhythm.
    internal static float HeadScratchContact(float t) =>
        CompanionTaskTiming.Hold(t,.24f,.34f,.72f,.82f);

    internal static float HeadScratchStroke(float t)
    {
        // t is normalized over the four-second action, so 5.6 cycles remain
        // 1.4 Hz. Start the cycle when contact reaches full weight. Pause for
        // 10% of a cycle at each reversal (about 71 ms), then
        // use a quintic stroke so position and speed settle before changing direction.
        var cycle=(t-.34f)*5.6f;
        cycle-=MathF.Floor(cycle);
        const float pause=.10f;
        if(cycle<pause)return -1;
        if(cycle<.5f)return -1+2*Smooth((cycle-pause)/(.5f-pause));
        if(cycle<.5f+pause)return 1;
        return 1-2*Smooth((cycle-(.5f+pause))/(.5f-pause));
    }

    public static CompanionActionPose SampleRunningLoop(CompanionActionKind kind,double seconds)
    {
        if(!double.IsFinite(seconds)||seconds<0)return default;
        var direction=kind switch{CompanionActionKind.RunningRight=>1f,CompanionActionKind.RunningLeft=>-1f,_=>0f};
        if(direction==0)return default;
        return RunCycle((float)(seconds*Math.Tau*1.35),direction) with
        { NativeRunningWeight=1, NativeRunningSeconds=(float)(seconds * .82), NativeRunningDirection=direction };
    }

    private static CompanionActionPose Wave(float t,float beat)
    {
        // Keep the greeting lift, then pass a slower lateral swing from shoulder to wrist.
        // Small phase offsets keep the arm from moving as one rigid lever.
        var lift=Smooth(t/.14f)*(1-Smooth((t-.84f)/.16f));
        var weight=beat*lift;
        var phase=MathF.Tau*2.25f*t;
        var shoulder=MathF.Sin(phase-.85f)*lift;
        var elbow=MathF.Sin(phase-.40f)*lift;
        var wrist=MathF.Sin(phase+.35f)*lift;
        var nod=MathF.Sin(MathF.PI*Math.Clamp(t/.58f,0,1))*lift;
        return new(
            new Vector3(-.08f*weight,.045f*lift-.020f*nod,-.055f*lift+.035f*weight),
            new Vector3(.035f*weight,.10f*nod-.012f*lift,.025f*weight-.025f*lift),
            -.035f*lift,.08f*lift,-.015f*weight,
            -1.28f*lift+.09f*shoulder,-1.02f*lift+.26f*elbow,.50f*wrist,
            -.03f*weight,.055f*weight,.035f*MathF.Max(0,weight),.025f*MathF.Max(0,-weight),.004f*lift)
        {
            LeftArmPitch=-.04f*lift,LeftForeArmPitch=.06f*lift,
            RightArmPitch=.12f*lift+.045f*shoulder,RightForeArmPitch=.16f*lift+.10f*elbow,
        };
    }

    private static CompanionActionPose Typing(float t)
    {
        var rhythm = MathF.Tau * 5.2f * t;
        var alternating = MathF.Sin(rhythm);
        var keyTap = .5f + .5f * MathF.Sin(rhythm * 2);
        var settle=CompanionTaskTiming.Hold(t,0,.27f,.75f,1);
        var attention=MathF.Sin(MathF.Tau*.7f*t);
        return new(
            new Vector3(.006f*attention,-.12f*settle,.008f*settle),
            new Vector3(.060f*settle,.012f*attention,.006f*settle),
            -1.15f + .05f * keyTap, 1.68f + .12f * alternating, .22f * alternating,
            1.45f - .05f * keyTap, -1.68f - .12f * alternating, -.22f * alternating,
            0,0,0,0,0)
        {
            LeftArmPitch = .42f, LeftForeArmPitch = .42f + .18f * MathF.Max(0, alternating),
            RightArmPitch = .42f, RightForeArmPitch = .42f + .18f * MathF.Max(0, -alternating),
        };
    }

    private static CompanionActionPose CoffeeBreak(float t)
    {
        var lift=CompanionTaskTiming.Hold(t,.02f,.24f,.73f,.98f);
        var sip=CompanionTaskTiming.Hold(t,.34f,.54f,.65f,.87f);
        return new(
            new Vector3(-.014f*lift,.025f*lift,-.012f*lift),
            new Vector3(.025f*lift,-.035f*sip,.014f*sip),
            .06f, .03f, 0,
            1.20f * lift, 1.80f * lift, .42f * sip,
            0,0,0,0,0)
        {
            RightArmPitch = .28f * lift, RightForeArmPitch = .62f * lift,
        };
    }

    private static CompanionActionPose EatingSnack(float t)
    {
        var reach=CompanionTaskTiming.Hold(t,0,.24f,.73f,1);
        var bite=CompanionTaskTiming.Hold(t,.34f,.54f,.64f,.88f);
        var handToMouth=reach*(.70f+.30f*bite);
        return new(
            new Vector3(-.012f*reach,.020f*reach,-.008f*reach),
            new Vector3(.018f*bite,-.012f*reach,.006f*bite),
            .02f, .02f, 0,
            1.20f * handToMouth, 1.80f * handToMouth, .42f * bite * handToMouth,
            0,0,0,0,0)
        {
            RightArmPitch = .28f * handToMouth, RightForeArmPitch = .68f * handToMouth,
        };
    }

    private static CompanionActionPose Think(float t)
    {
        var attention=CompanionTaskTiming.Hold(t,.01f,.24f,.73f,.99f);
        var chin=CompanionTaskTiming.Hold(t,.22f,.43f,.65f,.89f);
        var aside=CompanionTaskTiming.Hold(t,.43f,.59f,.66f,.84f);
        return new(new(-.014f*attention-.010f*aside,.022f*attention,.015f*attention-.012f*aside),
            new(.045f*chin+.020f*aside,-.035f*attention+.14f*aside,.025f*chin-.020f*aside),
            .02f,.02f,0,-.42f,-.90f,0,0,0,0,0,0);
    }

    private static CompanionActionPose Resting(float t)
    {
        var settle = Smooth(t / .18f) * (1 - Smooth((t - .80f) / .14f));
        var breath = MathF.Sin(MathF.Tau * 1.25f * t);
        return new(
            new Vector3(.050f * breath, .34f * settle, -.055f * settle),
            new Vector3(.10f * settle + .025f * breath, .13f * settle, .08f * settle),
            .12f * settle, .18f * settle, 0,
            -.12f * settle, -.18f * settle, 0,
            .035f * breath, -.035f * breath, .045f * settle, .045f * settle, -.012f * settle);
    }

    private static CompanionActionPose Saluting(float t)
    {
        var salute = Smooth(t / .18f) * (1 - Smooth((t - .66f) / .20f));
        var wrist = MathF.Sin(MathF.Tau * 1.7f * t) * salute;
        return new(
            new Vector3(.018f * wrist, -.012f * salute, .025f * salute),
            new Vector3(.035f * salute, .045f * salute, -.035f * salute),
            .02f * salute, .04f * salute, 0,
            1.50f * salute, 1.80f * salute, .24f * wrist,
            0, 0, 0, 0, .004f * salute)
        {
            RightArmPitch = .28f * salute, RightForeArmPitch = .62f * salute,
        };
    }

    private static CompanionActionPose HeadScratching(float t)
    {
        var reach = Smooth(t / .24f) * (1 - Smooth((t - .78f) / .14f));
        // Keep the head and trunk settled beside the contact point. The actual
        // wrist motion comes from the contact IK and palm-roll solve below.
        return new(
            new Vector3(0, -.018f * reach, -.045f * reach),
            new Vector3(.14f * reach, 0, .11f * reach),
            -.08f * reach, -.10f * reach, 0,
            1.50f * reach, 1.80f * reach, 0,
            0, 0, 0, .04f * reach, -.003f * reach)
        {
            RightArmPitch = .28f * reach, RightForeArmPitch = .62f * reach,
        };
    }

    private static CompanionActionPose ShakingHead(float t, float beat)
    {
        var shake = MathF.Sin(MathF.Tau * 2.1f * t + .75f);
        var settle = Smooth((t - .72f) / .18f);
        var amount = 1 - settle;
        return new(
            new Vector3(.035f * shake * amount, .022f * amount, .04f * shake),
            new Vector3(.36f * shake * amount, .012f * beat, .12f * shake),
            .06f * amount, -.12f * amount, 0,
            -.06f * amount, -.12f * amount, 0,
            .025f * shake, -.025f * shake, 0, 0, 0);
    }

    private static CompanionActionPose Yawning(float t, float beat)
    {
        var cover = Smooth(t / .15f) * (1 - Smooth((t - .67f) / .18f));
        var exhale = MathF.Sin(MathF.PI * Math.Clamp((t - .15f) / .68f, 0, 1));
        return new(
            new Vector3(.025f * beat, .10f * exhale, -.035f * cover),
            new Vector3(.15f * exhale, -.055f * cover, .025f * beat),
            .04f * cover, .06f * cover, 0,
            1.20f * cover, 1.80f * cover, .20f * cover,
            .025f * beat, -.025f * beat, .04f * cover, .04f * cover, -.006f * cover)
        {
            RightArmPitch = .28f * cover, RightForeArmPitch = .62f * cover,
        };
    }

    private static CompanionActionPose WakingUp(float t, float beat)
    {
        var rise = Smooth(t / .28f);
        var stretch = rise * (1 - Smooth((t - .68f) / .22f));
        var settle = Smooth((t - .72f) / .20f);
        var lift = stretch * (1 - .35f * settle);
        return new(
            new Vector3(.035f * beat, .08f * (1 - rise) - .02f * lift, .025f * beat),
            new Vector3(.08f * (1 - rise) + .035f * beat, -.035f * lift, .04f * beat),
            2.15f * lift, -.20f * lift, -.04f * lift,
            -2.15f * lift, -.20f * lift, .04f * lift,
            .025f * beat, -.025f * beat, .025f * MathF.Max(0, beat), .025f * MathF.Max(0, -beat), .012f * lift)
        {
            LeftArmPitch = .12f * lift, RightArmPitch = -.12f * lift,
        };
    }

    private static CompanionActionPose Swaying(float t, float beat, float quickBeat)
    {
        var sway = MathF.Sin(MathF.PI * 1.5f * t);
        var settle = 1 - Smooth((t - .76f) / .18f);
        return new(
            new Vector3(.12f * sway * settle, .008f * beat, .14f * sway * settle),
            new Vector3(.065f * quickBeat * settle, .012f, -.05f * sway * settle),
            .14f * sway * settle, -.04f, 0,
            -.14f * sway * settle, .04f, 0,
            .13f * sway * settle, -.13f * sway * settle,
            .045f * MathF.Max(0, sway), .045f * MathF.Max(0, -sway), .004f * MathF.Max(0, beat));
    }

    private static CompanionActionPose StumbleRecover(float t)
    {
        var trip = Smooth(t / .10f) * (1 - Smooth((t - .58f) / .16f));
        var recover = Smooth((t - .54f) / .28f);
        var wobble = MathF.Sin(MathF.Tau * 1.8f * t + .45f) * (1 - Smooth((t - .74f) / .16f));
        return new(
            new Vector3(.14f * trip - .025f * recover, .10f * trip - .02f * recover, .14f * trip + .035f * wobble),
            new Vector3(-.14f * trip, -.065f * wobble, -.10f * trip),
            1.18f * trip * (1 - .4f * recover), -.32f * trip, .08f * wobble,
            -.98f * trip * (1 - .4f * recover), -.38f * trip, -.08f * wobble,
            -.22f * trip, .18f * trip, .34f * trip, .28f * trip, -.025f * trip + .012f * recover)
        {
            LeftArmPitch = -.14f * trip, RightArmPitch = .12f * trip,
        };
    }

    private static CompanionActionPose AskingQuestion(float t)
    {
        var raise = Smooth(t / .20f) * (1 - Smooth((t - .68f) / .18f));
        var curious = MathF.Sin(MathF.PI * Math.Clamp(t / .72f, 0, 1));
        return new(
            new Vector3(.02f * curious, -.025f * raise, .035f * raise),
            new Vector3(.08f * curious, .015f, .09f * raise),
            2.28f * raise, -.30f * raise, .12f * curious,
            -.10f * raise, -.12f * raise, 0,
            0, .025f * curious, 0, .03f * raise, 0)
        {
            LeftArmPitch = .12f * raise, LeftForeArmPitch = .22f * raise,
        };
    }

    private static CompanionActionPose VictoryPose(float t, float beat)
    {
        var pump = Smooth(t / .16f) * (1 - Smooth((t - .74f) / .18f));
        var bounce = MathF.Max(0, beat);
        return new(
            new Vector3(.045f * beat, -.025f * pump, .035f * beat),
            new Vector3(.035f * beat, -.02f * pump, .02f),
            .12f * pump, -.08f * pump, 0,
            -2.05f * pump, -.35f * pump, .12f * beat,
            .045f * beat, -.045f * beat, .05f * bounce, .05f * bounce, .022f * bounce)
        {
            RightArmPitch = -.08f * pump, RightForeArmPitch = .10f * pump,
        };
    }

    private static CompanionActionPose Squatting(float t, float beat)
    {
        var lower = Smooth(t / .20f) * (1 - Smooth((t - .72f) / .18f));
        var inspect = MathF.Sin(MathF.Tau * 1.6f * t) * lower;
        return new(
            new Vector3(.035f * inspect, .24f * lower, -.025f * lower),
            new Vector3(.12f * inspect, -.035f * lower, .055f * lower),
            .10f * lower, .08f * lower, 0,
            -.10f * lower, .08f * lower, 0,
            -.275f * lower, -.275f * lower, .55f * lower, .55f * lower, 0)
        {
            LeftArmPitch = .08f * lower, RightArmPitch = -.08f * lower,
            LeftFootPitch=-.275f*lower,RightFootPitch=-.275f*lower,
            SupportWeight=1,HorizontalSupportWeight=1,LeftFootContact=1,RightFootContact=1,
        };
    }

    private static CompanionActionPose Curtsy(float t)
    {
        var lower=Smooth(t/.24f)*(1-Smooth((t-.68f)/.27f));
        return new(new Vector3(0,.16f*lower,0),new Vector3(0,.07f*lower,0),
            -.16f*lower,-.04f*lower,0,.16f*lower,-.04f*lower,0,
            -.18f*lower,-.18f*lower,.36f*lower,.36f*lower,0)
        {
            LeftFootPitch=-.18f*lower,RightFootPitch=-.18f*lower,
            SupportWeight=1,HorizontalSupportWeight=1,LeftFootContact=1,RightFootContact=1,
        };
    }

    private static CompanionActionPose Beckoning(float t)
    {
        var reach = Smooth(t / .16f) * (1 - Smooth((t - .82f) / .12f));
        var cue = Smooth((t - .16f) / .08f) * (1 - Smooth((t - .76f) / .10f));
        var beckon = MathF.Sin(MathF.Tau * 2.2f * (t - .18f)) * cue;
        var curl = MathF.Max(0, beckon);
        return new(
            new Vector3(-.035f * reach + .018f * beckon, .01f, .065f * reach),
            new Vector3(.12f * reach, .02f * reach, -.045f * reach + .025f * beckon),
            .08f * reach, .02f * reach, 0,
            (1.16f + .18f * beckon) * reach, (1.02f + .42f * curl) * reach, .34f * beckon,
            0, 0, 0, 0, .004f * reach)
        {
            RightArmPitch = .18f * reach, RightForeArmPitch = (.38f + .18f * curl) * reach,
        };
    }

    private static CompanionActionPose Hugging(float t)
    {
        var open = Smooth(t / .18f) * (1 - Smooth((t - .28f) / .10f));
        var hug = Smooth((t - .24f) / .18f) * (1 - Smooth((t - .76f) / .12f));
        var breath = MathF.Sin(MathF.Tau * 1.25f * t) * hug;
        return new(
            new Vector3(.025f * breath, -.025f * hug, -.055f * hug),
            new Vector3(.045f * hug, -.035f * hug, .025f * breath),
            -1.05f * open - 1.05f * hug, .24f * open + .92f * hug, .04f * breath,
            1.05f * open + 1.05f * hug, .24f * open - .92f * hug, -.04f * breath,
            .015f * breath, -.015f * breath, 0, 0, -.003f * hug);
    }

    private static CompanionActionPose Pointing(float t)
    {
        var anticipate = 1 - Smooth(t / .16f);
        var point = Smooth((t - .14f) / .12f) * (1 - Smooth((t - .76f) / .16f));
        return new(
            new Vector3(.025f * point, -.015f * point, .075f * point),
            new Vector3(.10f * anticipate + .035f * point, .015f, -.025f * point),
            -.10f * point, .08f * point, 0,
            -1.58f * point, -.10f * point, -.20f * point,
            0, 0, 0, 0, .002f * point)
        {
            RightArmPitch = .18f * point, RightForeArmPitch = .12f * point,
        };
    }

    private static CompanionActionPose ImpatientFootTap(float t)
    {
        var settle = 1 - Smooth((t - .82f) / .14f);
        var phase = MathF.Tau * 2.15f * t + .38f;
        var leftTap = MathF.Pow(MathF.Max(0, MathF.Sin(phase)), 4) * settle;
        var rightTap = MathF.Pow(MathF.Max(0, MathF.Sin(phase + MathF.PI)), 4) * settle;
        var shift = MathF.Sin(phase * .5f) * settle;
        var tap = MathF.Max(leftTap, rightTap);
        return new(
            new Vector3(.065f * shift, .012f, .045f * shift),
            new Vector3(.045f * shift, .015f, -.025f * shift),
            -.16f, .10f, 0, .16f, .10f, 0,
            .34f * leftTap, -.34f * rightTap,
            .48f * leftTap, .48f * rightTap,
            .016f * tap);
    }

    private static CompanionActionPose Shrugging(float t)
    {
        var lift = Smooth(t / .14f) * (1 - Smooth((t - .72f) / .16f));
        var uncertainty = MathF.Sin(MathF.Tau * 1.1f * t) * lift;
        return new(
            new Vector3(.02f * uncertainty, .02f * lift, -.015f),
            new Vector3(.045f * uncertainty, .025f * lift, .14f * lift),
            -.66f * lift, .52f * lift, .02f * uncertainty,
            .66f * lift, .52f * lift, -.02f * uncertainty,
            .025f * uncertainty, -.025f * uncertainty, 0, 0, .003f * lift);
    }

    private static CompanionActionPose Sneaking(float t)
    {
        var crouch = Smooth(t / .18f) * (1 - Smooth((t - .82f) / .14f));
        var step = MathF.Sin(MathF.Tau * 2.35f * t);
        var peek = MathF.Sin(MathF.Tau * .9f * t + .4f) * crouch;
        var leftKnee=(.30f+.08f*MathF.Max(0,-step))*crouch;
        var rightKnee=(.30f+.08f*MathF.Max(0,step))*crouch;
        var leftHip=-.5f*leftKnee+.055f*step*crouch;
        var rightHip=-.5f*rightKnee-.055f*step*crouch;
        return new(
            new Vector3(.055f * step * crouch, -.045f * crouch, .045f * step * crouch),
            new Vector3(.12f * peek, .055f * crouch, -.035f * peek),
            -.28f * crouch, -.38f * crouch, 0,
            .28f * crouch, -.38f * crouch, 0,
            leftHip,rightHip,leftKnee,rightKnee,0)
        {
            LeftFootPitch=-leftHip-leftKnee+.04f*crouch,RightFootPitch=-rightHip-rightKnee+.04f*crouch,
            SupportWeight=1,LeftFootContact=Smooth((step+.25f)/.5f),RightFootContact=Smooth((-step+.25f)/.5f),
        };
    }

    private static CompanionActionPose Listening(float t)
    {
        var listen = Smooth(t / .22f) * (1 - Smooth((t - .76f) / .16f));
        var still = MathF.Sin(MathF.Tau * .72f * t) * listen;
        return new(
            new Vector3(.015f * still, .012f * listen, -.035f * listen),
            new Vector3(.045f * still, .025f * listen, .13f * listen),
            .06f * listen, .02f, 0,
            1.10f * listen, 1.48f * listen, .08f * still,
            0, 0, 0, 0, .002f * listen)
        {
            RightArmPitch = .20f * listen, RightForeArmPitch = .52f * listen,
        };
    }

    private static CompanionActionPose Cheering(float t)
    {
        var phase = MathF.Tau * 1.7f * t;
        var leftPump = MathF.Max(0, MathF.Sin(phase));
        var rightPump = MathF.Max(0, MathF.Sin(phase + MathF.PI));
        var bounce = MathF.Max(0, MathF.Sin(phase * 2));
        return new(
            new Vector3(.045f * MathF.Sin(phase), .07f * bounce, .035f * MathF.Sin(phase + .4f)),
            new Vector3(.055f * MathF.Sin(phase + .7f), -.015f * bounce, .035f * MathF.Sin(phase)),
            .20f + 2.20f * leftPump, -.38f * leftPump, .12f * leftPump,
            -.20f - 2.20f * rightPump, -.38f * rightPump, -.12f * rightPump,
            .05f * MathF.Sin(phase), -.05f * MathF.Sin(phase),
            .12f * bounce, .12f * bounce, .026f * bounce);
    }
    private static CompanionActionPose Facepalming(float t)
    {
        var cover = Smooth(t / .18f) * (1 - Smooth((t - .72f) / .16f));
        var sigh = MathF.Sin(MathF.Tau * 1.2f * t) * cover;
        return new(
            new Vector3(.075f * cover, -.028f * cover, .055f * cover),
            new Vector3(.13f * cover, -.025f * cover, -.08f * cover),
            -.08f * cover, .06f * cover, 0,
            1.22f * cover, 1.78f * cover, .06f * sigh,
            .025f * sigh, -.025f * sigh, .035f * cover, .035f * cover, -.004f * cover)
        {
            RightArmPitch = .24f * cover, RightForeArmPitch = .58f * cover,
        };
    }

    private static CompanionActionPose DoubleWaving(float t)
    {
        // One shared rhythm in mirrored local Z, with quieter upper arms and delayed wrists.
        var phase = MathF.Tau * 1.8f * t;
        var lift = Smooth(t / .22f) * (1 - Smooth((t - .78f) / .22f));
        var elbow = MathF.Sin(phase - .25f);
        var leftWrist = MathF.Sin(phase - .65f);
        var rightWrist = MathF.Sin(phase - .82f);
        var sway = MathF.Sin(MathF.Tau * t) * lift;
        // Keep elbows below the shoulders, with hands near the head. A shoulder-
        // height upper arm and a shallow elbow read as open palms rather than hello.
        var arm = (1.10f + .025f * MathF.Sin(phase)) * lift;
        var forearm = (1.45f + .08f * elbow) * lift;
        return new(
            new Vector3(.018f * sway, 0, .012f * sway),
            new Vector3(.018f * sway, .012f * lift, -.018f * sway),
            arm, forearm, .15f * leftWrist * lift,
            -arm, -forearm, -.15f * rightWrist * lift,
            0, 0, 0, 0, 0)
        {
            LeftArmPitch = .045f * lift, RightArmPitch = .045f * lift,
            LeftForeArmPitch = .16f * lift, RightForeArmPitch = .16f * lift,
            SupportWeight = 1,
        };
    }

    private static CompanionActionPose SideStepping(float t)
    {
        // Small outward taps, one leg at a time. The planted foot sets the hip height.
        var phase = MathF.Tau * t;
        var step = MathF.Sin(phase);
        var leftMoving = MathF.Pow(MathF.Max(0, step), 2);
        var rightMoving = MathF.Pow(MathF.Max(0, -step), 2);
        var leftKnee = .035f + .27f * leftMoving;
        var rightKnee = .035f + .27f * rightMoving;
        var leftHip = -.055f * leftMoving;
        var rightHip = .055f * rightMoving;
        return new(
            new Vector3(-.055f * step, 0, .020f * step),
            new Vector3(.018f * step, .004f, -.024f * step),
            .040f * step, -.06f * leftMoving, .012f * step,
            -.040f * step, .06f * rightMoving, -.012f * step,
            leftHip, rightHip, leftKnee, rightKnee, 0)
        {
            LeftLegSide = -.20f * leftMoving, RightLegSide = .20f * rightMoving,
            HorizontalSupportWeight = 1,
            LeftFootContact = 1 - Smooth(leftMoving / .20f),
            RightFootContact = 1 - Smooth(rightMoving / .20f),
            LeftArmPitch = .05f + .11f * step, RightArmPitch = .05f - .11f * step,
            LeftForeArmPitch = .12f, RightForeArmPitch = .12f,
            LeftFootPitch = -leftHip - leftKnee, RightFootPitch = -rightHip - rightKnee,
            SupportWeight = 1,
        };
    }

    private static CompanionActionPose AirGuitar(float t)
    {
        var strum = MathF.Sin(MathF.Tau * 4.2f * t);
        var fret = MathF.Sin(MathF.Tau * 2.4f * t + .35f);
        var rock = MathF.Sin(MathF.Tau * 1.35f * t + .25f);
        var active = 1 - Smooth((t - .84f) / .12f);
        return new(
            new Vector3(.105f * rock * active, -.025f, .075f * rock * active),
            new Vector3(.045f * MathF.Sin(MathF.Tau * 2.1f * t), .035f * rock, -.035f * rock),
            -1.20f * active, 1.80f * active, .12f * fret * active,
            1.20f * active, -1.80f * active, .55f * strum * active,
            .13f * rock * active, -.13f * rock * active,
            .08f * MathF.Max(0, rock), .08f * MathF.Max(0, -rock), .010f * MathF.Max(0, rock))
        {
            LeftArmPitch = .42f * active, LeftForeArmPitch = .38f * active,
            RightArmPitch = .42f * active, RightForeArmPitch = .40f * active,
        };
    }

    private static CompanionActionPose HeartHands(float t)
    {
        var gather = Smooth(t / .20f) * (1 - Smooth((t - .80f) / .14f));
        var pulse = MathF.Sin(MathF.Tau * 1.15f * t) * gather;
        return new(
            new Vector3(.025f * pulse, -.025f * gather, .025f * gather),
            new Vector3(.035f * gather, .025f * pulse, .025f * gather),
            -1.20f * gather, 1.68f * gather, .16f * pulse,
            1.45f * gather, -1.68f * gather, -.16f * pulse,
            .015f * pulse, -.015f * pulse, 0, 0, .002f * gather)
        {
            LeftArmPitch = .42f * gather, LeftForeArmPitch = (.42f + .05f * pulse) * gather,
            RightArmPitch = .42f * gather, RightForeArmPitch = (.42f - .05f * pulse) * gather,
        };
    }

    private static CompanionActionPose Shivering(float t)
    {
        var tremor = MathF.Sin(MathF.Tau * 6.0f * t);
        var settle = 1 - Smooth((t - .82f) / .14f);
        var cold = Smooth(t / .12f) * settle;
        return new(
            new Vector3(.032f * tremor * cold, .015f, .026f * tremor * cold),
            new Vector3(.045f * tremor * cold, .018f, .035f * tremor * cold),
            -1.05f * cold + .025f * tremor, .92f * cold, .04f * tremor * cold,
            1.05f * cold - .025f * tremor, -.92f * cold, -.04f * tremor * cold,
            .045f * tremor * cold, -.045f * tremor * cold,
            .04f * MathF.Max(0, tremor) * cold, .04f * MathF.Max(0, -tremor) * cold, -.004f * cold)
        {
            LeftArmPitch = .42f * cold, LeftForeArmPitch = .42f * cold,
            RightArmPitch = .42f * cold, RightForeArmPitch = .42f * cold,
        };
    }

    private static CompanionActionPose ConfidentPose(float t)
    {
        var pose = Smooth(t / .20f) * (1 - Smooth((t - .78f) / .16f));
        var sway = MathF.Sin(MathF.Tau * 1.2f * t) * pose;
        return new(
            new Vector3(-.025f * pose, -.012f * pose, .055f * sway),
            new Vector3(-.045f * pose, .015f * sway, -.035f * pose),
            -.30f * pose, .60f * pose, .12f * sway,
            .60f * pose, -.90f * pose, -.12f * sway,
            .08f * pose, -.08f * pose, 0, 0, .002f * pose);
    }

    private static CompanionActionPose SendingLove(float t)
    {
        var gather = Smooth(t / .16f);
        var send = Smooth((t - .34f) / .16f) * (1 - Smooth((t - .82f) / .14f));
        var flutter = MathF.Sin(MathF.Tau * 2.4f * t) * send;
        var leftArm = -1.20f + (1.55f + 1.20f) * send;
        var rightArm = 1.45f + (-1.55f - 1.45f) * send;
        var leftForeArm = 1.68f + (-.25f - 1.68f) * send;
        var rightForeArm = -1.68f + (-.25f + 1.68f) * send;
        var forward = gather * (1 - send);
        return new(
            new Vector3(-.025f * forward + .045f * flutter, .012f, .035f * send),
            new Vector3(.07f * gather, -.025f * send, -.035f * forward + .02f * flutter),
            leftArm, leftForeArm, .12f * flutter,
            rightArm, rightForeArm, -.12f * flutter,
            0, 0, 0, 0, .008f * send)
        {
            LeftArmPitch = .42f * forward, LeftForeArmPitch = .42f * forward,
            RightArmPitch = .42f * forward, RightForeArmPitch = .42f * forward,
        };
    }

    private static CompanionActionPose Run(float t, float direction) => RunCycle(MathF.Tau * 1.35f * t, direction);

    private static CompanionActionPose RunCycle(float phase, float direction)
    {
        // Opposite arms and legs; the recovering foot folds while the stance leg stays long.
        var stride = MathF.Sin(phase);
        var leftSwing = MathF.Pow(MathF.Max(0, -stride), 2);
        var rightSwing = MathF.Pow(MathF.Max(0, stride), 2);
        var flight = MathF.Pow(MathF.Max(0, MathF.Sin(phase * 2 - .25f)), 2);
        var leftKnee = .035f + .30f * leftSwing;
        var rightKnee = .035f + .30f * rightSwing;
        var leftHip = .22f * stride - .42f * leftKnee;
        var rightHip = -.22f * stride - .42f * rightKnee;
        var armLag = MathF.Sin(phase - .16f);
        return new(
            new Vector3(.015f * stride, -.045f, -.018f * direction - .010f * stride),
            new Vector3(.012f * direction, .004f * flight, .008f * stride),
            -.018f * stride, .012f * leftSwing, .010f * armLag,
            .018f * stride, -.012f * rightSwing, -.010f * armLag,
            leftHip, rightHip, leftKnee, rightKnee, .006f * flight)
        {
            LeftArmPitch = .06f - .24f * armLag, RightArmPitch = .06f + .24f * armLag,
            // Transfer support before the recovering knee folds; overlap at heel strike.
            LeftFootContact = Smooth((stride + .20f) / .40f),
            RightFootContact = Smooth((-stride + .20f) / .40f),
            LeftForeArmPitch = .26f + .06f * leftSwing,
            RightForeArmPitch = .26f + .06f * rightSwing,
            // A small toe lead in recovery blends into a level sole at support.
            LeftFootPitch = -leftHip - leftKnee + .08f * leftSwing,
            RightFootPitch = -rightHip - rightKnee + .08f * rightSwing,
            SupportWeight = 1,
        };
    }

    private static CompanionActionPose Jump(float t)
    {
        // Both feet share one crouch, takeoff, flight and landing; no lateral hip swing.
        var crouch = Smooth(t / .16f) * (1 - Smooth((t - .20f) / .10f));
        var u = Math.Clamp((t - .28f) / .44f, 0, 1);
        var arc = 4 * u * (1 - u);
        var landing = Smooth((t - .70f) / .08f) * (1 - Smooth((t - .80f) / .18f));
        var reach = Smooth((t - .18f) / .18f) * (1 - Smooth((t - .66f) / .22f));
        var knee = .60f * crouch + .18f * arc + .50f * landing;
        var hip = -.5f * knee;
        return new(
            new Vector3(0, -.025f * crouch + .015f * landing, 0),
            new Vector3(0, -.015f * arc + .022f * landing, 0),
            1.18f * reach, .20f * reach, .015f * reach,
            -1.18f * reach, -.20f * reach, -.015f * reach,
            hip, hip, knee, knee, .085f * arc)
        {
            LeftArmPitch = .06f * reach, RightArmPitch = .06f * reach,
            LeftFootContact = 1, RightFootContact = 1,
            HorizontalSupportWeight = 1,
            LeftForeArmPitch = .18f * reach, RightForeArmPitch = .18f * reach,
            LeftFootPitch = -hip - knee + .06f * arc,
            RightFootPitch = -hip - knee + .06f * arc,
            SupportWeight = 1,
        };
    }

    private static CompanionActionPose Stretch(float t, float envelope)
    {
        var raise = Smooth(t / .22f) * (1 - Smooth((t - .72f) / .24f));
        var exhale = MathF.Sin(MathF.PI * Math.Clamp((t - .22f) / .58f, 0, 1));
        return new(
            new Vector3(.02f * MathF.Sin(MathF.Tau * t), -.035f * exhale, .015f),
            new Vector3(.015f, -.04f * exhale, -.02f * MathF.Sin(MathF.Tau * t)),
            2.35f * raise, -.22f * raise, -.04f * raise,
            -2.35f * raise, -.22f * raise, .04f * raise,
            0, 0, 0, 0, .012f * raise * envelope);
    }

    private static CompanionActionPose Dance(float t, float beat, float quickBeat)
    {
        var active = MathF.Sin(MathF.PI * Math.Clamp(t / .92f, 0, 1));
        return new(
            new Vector3(.145f * beat * active, -.015f, .09f * quickBeat * active),
            new Vector3(.085f * quickBeat * active, -.015f, -.05f * beat * active),
            .48f + .42f * beat * active, -.15f, 0,
            -.48f + .42f * beat * active, -.15f, 0,
            .30f * quickBeat * active, -.30f * quickBeat * active,
            .16f * MathF.Max(0, quickBeat), .16f * MathF.Max(0, -quickBeat), .012f * MathF.Max(0, beat));
    }

    private static CompanionActionPose Clap(float t, float envelope)
    {
        var pulse = MathF.Sin(MathF.Tau * 2.6f * t);
        var handsTogether = .62f + .22f * pulse;
        return new(
            new Vector3(.035f * pulse, .025f, .04f * MathF.Sin(MathF.Tau * t)), new Vector3(.02f, -.015f, .025f * pulse),
            -.64f * envelope, -handsTogether * envelope, .04f * pulse,
            .64f * envelope, -handsTogether * envelope, -.04f * pulse,
            .02f * pulse, -.02f * pulse, 0, 0, .008f * MathF.Max(0, pulse));
    }

    private static float Smooth(float value)
    {
        var t = Math.Clamp(value, 0, 1);
        return t * t * t * (t * (t * 6 - 15) + 10);
    }
}
