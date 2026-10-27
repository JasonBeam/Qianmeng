namespace DesktopBuddy.Models;

public sealed partial class PetState
{
    public int Coins { get; set; } = 60;
    public int Hydration { get; set; } = 80;
    public int Health { get; set; } = 100;
    public int MaxHealth { get; set; } = 100;
    public int SkillPoints { get; set; }
    public double FoodDecayFraction { get; set; }
    public double WaterDecayFraction { get; set; }
    public double EnergyDecayFraction { get; set; }
    public int Fitness { get; set; }
    public string Skin { get; set; } = "blue";
    public Dictionary<string, int> Inventory { get; set; } = new();
    public List<string> OwnedSkins { get; set; } = ["blue"];
    public string Activity { get; set; } = "";
    public DateTimeOffset? ActivityStartedUtc { get; set; }
    public DateTimeOffset? ActivityEndsUtc { get; set; }
    public DateTimeOffset LastCareUtc { get; set; } = DateTimeOffset.UtcNow;
    public int CareVersion { get; set; } = 1;
}
