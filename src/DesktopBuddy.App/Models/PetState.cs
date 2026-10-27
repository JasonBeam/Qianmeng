namespace DesktopBuddy.Models;

public sealed partial class PetState
{
    public int SchemaVersion { get; set; } = 2;
    public int Level { get; set; } = 1;
    public int Xp { get; set; }
    public int Affection { get; set; }
    public int Mood { get; set; } = 50;
    public int Energy { get; set; } = 80;
    public int Fullness { get; set; } = 80;
    public string DailyXpDate { get; set; } = DateTime.Now.ToString("yyyy-MM-dd");
    public int DailyXp { get; set; }
    public string DailyAffectionDate { get; set; } = DateTime.Now.ToString("yyyy-MM-dd");
    public int DailyAffection { get; set; }

    public int XpToNextLevel => 100 + 25 * Math.Max(0, Level - 1);

    public int AddXp(int amount)
    {
        ResetDailyIfNeeded();
        var allowed = Math.Min(Math.Max(0, amount), Math.Max(0, 25 - DailyXp));
        DailyXp += allowed;
        Xp += allowed;
        var gainedLevels = 0;
        while (Xp >= XpToNextLevel)
        {
            Xp -= XpToNextLevel;
            Level++;
            gainedLevels++;
        }
        return gainedLevels;
    }

    public void AddAffection(int amount)
    {
        ResetDailyIfNeeded();
        var allowed = Math.Min(Math.Max(0, amount), Math.Max(0, 20 - DailyAffection));
        DailyAffection += allowed;
        Affection = Math.Clamp(Affection + allowed, 0, 1000);
    }

    public void ResetDailyIfNeeded()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (!string.Equals(DailyXpDate, today, StringComparison.Ordinal))
        {
            DailyXpDate = today;
            DailyXp = 0;
        }
        if (!string.Equals(DailyAffectionDate, today, StringComparison.Ordinal))
        {
            DailyAffectionDate = today;
            DailyAffection = 0;
        }
    }
}
