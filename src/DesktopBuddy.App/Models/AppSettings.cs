namespace DesktopBuddy.Models;

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 2;
    public bool AnimationEnabled { get; set; } = true;
    public bool AutomaticActivityAnimation { get; set; } = true;
    public double AnimationSpeed { get; set; } = 1;
    public int AnimationFrameRate { get; set; } = 30;
    public string PreferredAnimationClip { get; set; } = "";
    public double PetSize { get; set; } = 1;
    public bool UseDefaultAppearance { get; set; }
    public bool ClickThrough { get; set; }
    public bool AutoHideFullscreen { get; set; } = true;
    public bool ContextDetectionEnabled { get; set; } = true;
    public bool PrivacyMode { get; set; }
    public bool AutoStart { get; set; }
    public bool SpeakReplies { get; set; }
    public bool RemoteChatConsent { get; set; }
    public string RemoteChatConsentEndpoint { get; set; } = "";
    public string ChatEndpoint { get; set; } = "";
    public string ChatModel { get; set; } = "gpt-4o-mini";
    public string PetName { get; set; } = "浅梦";
    public string PetModelFile { get; set; } = "";
    public string PetModelName { get; set; } = "";
    public double PetModelYaw { get; set; }
    public double PetModelScale { get; set; } = 1;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public List<string> WatchedProcesses { get; set; } =
    [
        "Code.exe", "Code - Insiders.exe", "chrome.exe", "msedge.exe", "firefox.exe",
        "Steam.exe", "WeChat.exe", "Weixin.exe", "WINWORD.EXE", "EXCEL.EXE",
        "POWERPNT.EXE", "Teams.exe", "ms-teams.exe", "MSTeams.exe"
    ];
    public List<ApprovedTarget> ApprovedTargets { get; set; } = [];
}

public sealed class ApprovedTarget
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public string Kind { get; set; } = "path"; // path, url, uwp
}
