using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopBuddy.Models;

namespace DesktopBuddy.Services;

public sealed class LocalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string DataDirectory { get; }
    public string PluginsDirectory => Path.Combine(DataDirectory, "plugins");
    public string ModelsDirectory => Path.Combine(DataDirectory, "models");
    public IReadOnlyList<string> RecoveryWarnings => _recoveryWarnings;
    public bool SettingsReadOnly => _settingsReadOnly;
    public bool PetStateReadOnly => _petStateReadOnly;
    private readonly List<string> _recoveryWarnings = [];
    private bool _settingsReadOnly;
    private bool _petStateReadOnly;

    public LocalStore() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopBuddy"))
    {
    }

    internal LocalStore(string dataDirectory)
    {
        DataDirectory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(PluginsDirectory);
    }

    public AppSettings LoadSettings()
    {
        var settings = Load("settings.json", new AppSettings());
        if (settings.SchemaVersion > 2)
        {
            _settingsReadOnly = true;
            _recoveryWarnings.Add("设置文件来自较新版本；为避免覆盖数据，已使用默认设置。本版本不会修改原文件。");
            settings = new AppSettings();
        }
        settings.SchemaVersion = 2;
        if (settings.PetName == "小伴") settings.PetName = "浅梦";
        settings.PetName = Limit(settings.PetName, 40, "浅梦");
        settings.ChatEndpoint = Limit(settings.ChatEndpoint, 2048, "");
        settings.ChatModel = Limit(settings.ChatModel, 128, "gpt-4o-mini");
        settings.AnimationSpeed = double.IsFinite(settings.AnimationSpeed) ? Math.Clamp(settings.AnimationSpeed, 0.5, 1.5) : 1;
        settings.AnimationFrameRate = settings.AnimationFrameRate is 15 or 24 or 30 ? settings.AnimationFrameRate : 30;
        settings.PreferredAnimationClip = Limit(settings.PreferredAnimationClip, 64, "");
        settings.PetModelName = Limit(settings.PetModelName, 120, "");
        if (!IsModelFileName(settings.PetModelFile)) settings.PetModelFile = "";
        settings.PetModelYaw = double.IsFinite(settings.PetModelYaw) ? Math.Clamp(settings.PetModelYaw, -180, 180) : 0;
        settings.PetSize = double.IsFinite(settings.PetSize) ? Math.Clamp(settings.PetSize, 0.6, 1.8) : 1;
        settings.PetModelScale = double.IsFinite(settings.PetModelScale) ? Math.Clamp(settings.PetModelScale, 0.5, 1.25) : 1;
        settings.RemoteChatConsentEndpoint = Limit(settings.RemoteChatConsentEndpoint, 2048, "");
        settings.WatchedProcesses = (settings.WatchedProcesses ?? new AppSettings().WatchedProcesses)
            .Where(IsExecutableName).Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToList();
        settings.ApprovedTargets = (settings.ApprovedTargets ?? [])
            .Where(x => x is not null && !string.IsNullOrWhiteSpace(x.Name) && !string.IsNullOrWhiteSpace(x.Value))
            .Take(100).Select(x => new ApprovedTarget
            {
                Name = Limit(x.Name, 80, "应用"),
                Value = Limit(x.Value, 4096, ""),
                Kind = x.Kind is "path" or "url" or "uwp" ? x.Kind : "invalid"
            }).ToList();
        return settings;
    }
    public PetState LoadPetState()
    {
        var pet = Load("pet-state.json", new PetState());
        if (pet.SchemaVersion > 2)
        {
            _petStateReadOnly = true;
            _recoveryWarnings.Add("成长存档来自较新版本；为避免覆盖数据，已使用默认成长状态。本版本不会修改原文件。");
            pet = new PetState();
        }
        if (pet.SchemaVersion < 2) pet.Mood = (Math.Clamp(pet.Mood, -100, 100) + 100) / 2;
        pet.SchemaVersion = 2;
        pet.Level = Math.Clamp(pet.Level, 1, 9999);
        pet.Xp = Math.Clamp(pet.Xp, 0, Math.Max(0, pet.XpToNextLevel - 1));
        pet.Affection = Math.Clamp(pet.Affection, 0, 1000);
        pet.Mood = Math.Clamp(pet.Mood, 0, 100);
        pet.Energy = Math.Clamp(pet.Energy, 0, 100);
        pet.Fullness = Math.Clamp(pet.Fullness, 0, 100);
        pet.DailyXp = Math.Clamp(pet.DailyXp, 0, 25);
        pet.DailyAffection = Math.Clamp(pet.DailyAffection, 0, 20);
        PetCareService.Normalize(pet, DateTimeOffset.UtcNow);
        pet.ResetDailyIfNeeded();
        return pet;
    }
    public void SaveSettings(AppSettings settings)
    {
        if (!_settingsReadOnly) Save("settings.json", settings);
    }

    public void SavePetState(PetState state)
    {
        if (!_petStateReadOnly) Save("pet-state.json", state);
    }

    private T Load<T>(string fileName, T fallback)
    {
        var path = Path.Combine(DataDirectory, fileName);
        try
        {
            if (!File.Exists(path)) return fallback;
            var value = JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
            return value is null ? fallback : value;
        }
        catch
        {
            var recoveryPath = "";
            try
            {
                var candidatePath = path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Move(path, candidatePath, overwrite: true);
                recoveryPath = candidatePath;
            }
            catch
            {
                // A readable fallback must never allow a later save to destroy the only original.
                if (fileName == "settings.json") _settingsReadOnly = true;
                if (fileName == "pet-state.json") _petStateReadOnly = true;
            }
            _recoveryWarnings.Add(string.IsNullOrWhiteSpace(recoveryPath)
                ? $"无法读取 {fileName}，已使用默认数据；原文件未能备份。"
                : $"无法读取 {fileName}，已使用默认数据；原文件备份为 {Path.GetFileName(recoveryPath)}。保存设置后会创建新文件。");
            return fallback;
        }
    }

    private void Save<T>(string fileName, T value)
    {
        Directory.CreateDirectory(DataDirectory);
        var path = Path.Combine(DataDirectory, fileName);
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(tempPath, path, overwrite: true);
    }

    private static string Limit(string? value, int maxLength, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static bool IsExecutableName(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        && !value.Contains('/')
        && !value.Contains('\\');

    internal static bool IsModelFileName(string? value) => !string.IsNullOrEmpty(value)
        && System.Text.RegularExpressions.Regex.IsMatch(value, "^model-[A-F0-9]{24}-v[123]\\.glb$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}
