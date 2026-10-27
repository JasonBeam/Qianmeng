namespace DesktopBuddy.Services;

public sealed record ContextInfo(string Category, string Status, string SuggestedLine, bool QuietByDefault);

public static class ContextCatalog
{
    public static ContextInfo FromExecutable(string? executableName)
    {
        var name = executableName ?? "";
        if (Is(name, "Code.exe", "Code - Insiders.exe"))
            return new("coding", "专注陪伴", "我在旁边陪你，按你的节奏来。", true);
        if (Is(name, "Steam.exe"))
            return new("gaming", "游戏情境", "游戏中我会保持安静。", true);
        if (Is(name, "WeChat.exe", "Weixin.exe"))
            return new("messaging", "沟通情境", "我会安静待在旁边。", true);
        if (Is(name, "WINWORD.EXE", "EXCEL.EXE", "POWERPNT.EXE"))
            return new("office", "办公专注", "慢慢来，我在这里。", true);
        if (Is(name, "Teams.exe", "ms-teams.exe", "MSTeams.exe"))
            return new("meeting", "会议安静模式", "会议期间我不打扰你。", true);
        if (Is(name, "chrome.exe", "msedge.exe", "firefox.exe"))
            return new("browser", "浏览陪伴", "我会轻轻陪着你。", true);
        return new("unknown", "普通待机", "需要我时，按 Ctrl + Alt + Space。", true);
    }

    private static bool Is(string candidate, params string[] names) =>
        names.Any(name => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase));
}
