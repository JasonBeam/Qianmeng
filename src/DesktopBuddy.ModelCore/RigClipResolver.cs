namespace DesktopBuddy.ModelCore;

/// <summary>Resolve authored roles before legacy names without changing unknown rigs.</summary>
public static class RigClipResolver
{
    public const double LegacyRunRate = .82;

    public static int FindExact(IReadOnlyList<RigClip> clips, string name)
    {
        for (var i = 0; i < clips.Count; i++)
            if (IsPlayable(clips[i]) && string.Equals(clips[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static bool IsPlayable(RigClip clip) => float.IsFinite(clip.Duration) && clip.Duration > 0;

    public static int FindIdle(IReadOnlyList<RigClip> clips)
    {
        var authored = FindExact(clips, "Human_Idle");
        if (authored >= 0) return authored;
        for (var i = 0; i < clips.Count; i++)
            if (IsPlayable(clips[i]) && (clips[i].Name.Contains("idle", StringComparison.OrdinalIgnoreCase) || clips[i].Name.Contains("待机"))) return i;
        return -1;
    }

    public static int FindRunning(IReadOnlyList<RigClip> clips)
    {
        foreach (var name in new[] { "Human_Run", "Running", "Run" })
        {
            var index = FindExact(clips, name);
            if (index >= 0) return index;
        }
        for (var i = 0; i < clips.Count; i++)
            if (IsPlayable(clips[i]) && (clips[i].Name.Contains("run", StringComparison.OrdinalIgnoreCase) || clips[i].Name.Contains("跑步"))) return i;
        return -1;
    }

    public static int FindAction(IReadOnlyList<RigClip> clips, CompanionActionKind action) => action switch
    {
        CompanionActionKind.Idle => FindIdle(clips),
        CompanionActionKind.Running => FindRunning(clips),
        CompanionActionKind.Waving => FindExact(clips, "Human_Greeting"),
        CompanionActionKind.Nodding => FindExact(clips, "Human_Nod"),
        CompanionActionKind.Listening => FindExact(clips, "Human_Listening"),
        CompanionActionKind.Bowing => FindExact(clips, "Human_Bow"),
        CompanionActionKind.HeadScratching => FindExact(clips, "Human_HeadScratch"),
        CompanionActionKind.DoubleWaving => FindExact(clips, "Human_DoubleWave"),
        _ => -1,
    };

    public static double PlaybackRate(RigClip clip) =>
        clip.Name.Contains("Running", StringComparison.OrdinalIgnoreCase) ? LegacyRunRate : 1;
}
