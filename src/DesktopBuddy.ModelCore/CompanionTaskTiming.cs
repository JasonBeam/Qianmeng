using System.Numerics;

namespace DesktopBuddy.ModelCore;

/// <summary>Separate preparation, arm travel, grip and release clocks for quiet hand tasks.
/// Position and acceleration settle at each authored boundary; sampling has no frame history.</summary>
internal static class CompanionTaskTiming
{
    public static bool Applies(CompanionActionKind kind) => kind is CompanionActionKind.CoffeeBreak
        or CompanionActionKind.EatingSnack or CompanionActionKind.Thinking or CompanionActionKind.Typing
        or CompanionActionKind.DoubleWaving or CompanionActionKind.Hugging
        or CompanionActionKind.ConfidentPose or CompanionActionKind.HeadScratching;

    public static float Ease(float t)
    {
        t=Math.Clamp(t,0,1);
        return t*t*t*(10+t*(-15+6*t));
    }

    public static float Hold(float t,float start,float arrive,float depart,float finish) =>
        Ease((t-start)/(arrive-start))*(1-Ease((t-depart)/(finish-depart)));

    public static Vector3 Arc(Vector3 from,Vector3 to,Vector3 bow,float progress) =>
        Vector3.Lerp(from,to,progress)+bow*(4*progress*(1-progress));

    public static CompanionActionPose Layer(CompanionActionKind kind,float t,CompanionActionPose full,CompanionActionPose body)
    {
        if(!Applies(kind))return body;
        var arm=kind switch
        {
            CompanionActionKind.Thinking=>Hold(t,.025f,.43f,.78f,1),
            // Keep the hand and contact targets fully weighted together long enough
            // for multiple real scratch cycles; align the arm release with the
            // body action's .74 exit boundary.
            CompanionActionKind.HeadScratching=>Hold(t,.025f,.35f,.74f,1),
            CompanionActionKind.Typing=>Hold(t,.035f,.25f,.78f,1),
            _=>Hold(t,.025f,.25f,.76f,1),
        };
        // The clavicle starts before the arm and continues through the slower
        // head-reach entry/exit, avoiding a single fast wrist-led movement.
        var shoulder=kind is CompanionActionKind.Thinking or CompanionActionKind.HeadScratching
            ?Hold(t,0,.27f,.82f,1):Hold(t,0,.19f,.79f,1);
        var leftArm=kind==CompanionActionKind.DoubleWaving?Hold(t,.06f,.30f,.74f,1):arm;
        return body with
        {
            LeftReach=(full.LeftReach*leftArm) with {Clavicle=full.LeftReach.Clavicle*shoulder},
            RightReach=(full.RightReach*arm) with {Clavicle=full.RightReach.Clavicle*shoulder},
        };
    }
}
