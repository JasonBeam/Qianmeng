using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DesktopBuddy.Services;

public sealed class PluginManifest
{
    public int SchemaVersion { get; set; }
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public List<string> Permissions { get; set; } = [];
    public List<PluginTrigger> Triggers { get; set; } = [];
    [JsonIgnore]
    public string? SourcePath { get; set; }
    public override string ToString() => $"{Name}  ·  {Version}\n{Triggers.Count} 个触发器 · pet.say";
}

public sealed class PluginTrigger
{
    public string Id { get; set; } = "";
    public List<string> ProcessNames { get; set; } = [];
    public string? Say { get; set; }
    public int CooldownMinutes { get; set; } = 60;
}

/// <summary>Loads data-only JSON plugins. It never loads code, scripts, URLs, or shell commands.</summary>
public sealed class PluginService
{
    private readonly string _directory;
    private readonly Dictionary<string, DateTime> _lastTriggered = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public PluginService(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public IReadOnlyList<PluginManifest> LoadAll()
    {
        var result = new List<PluginManifest>();
        IEnumerable<string> paths;
        try { paths = Directory.EnumerateFiles(_directory, "*.json").ToArray(); }
        catch { return result; }
        foreach (var path in paths)
        {
            try
            {
                if (new FileInfo(path).Length > 128 * 1024) continue;
                var manifest = Parse(File.ReadAllText(path));
                manifest.SourcePath = path;
                result.Add(manifest);
            }
            catch { /* An invalid plugin is ignored and cannot block application startup. */ }
        }
        return result;
    }

    public PluginManifest Inspect(string sourcePath)
    {
        var info = new FileInfo(sourcePath);
        if (!info.Exists || info.Length > 128 * 1024) throw new InvalidOperationException("插件文件不存在或超过 128 KB。");
        return Parse(File.ReadAllText(sourcePath));
    }

    public PluginManifest Import(string sourcePath, bool replaceExisting = false)
    {
        var manifest = Inspect(sourcePath);
        var destination = Path.Combine(_directory, manifest.Id + ".json");
        if (File.Exists(destination) && !replaceExisting)
            throw new InvalidOperationException("已经安装了相同 ID 的插件；请在替换确认后重新导入。");
        File.WriteAllText(destination, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        manifest.SourcePath = destination;
        return manifest;
    }

    public bool Remove(PluginManifest manifest) =>
        !string.IsNullOrWhiteSpace(manifest.SourcePath)
        && File.Exists(manifest.SourcePath)
        && TryDelete(manifest.SourcePath);

    public string? GetTriggeredLine(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return null;
        foreach (var plugin in LoadAll())
        foreach (var trigger in plugin.Triggers)
        {
            if (!plugin.Permissions.Contains("pet.say", StringComparer.Ordinal)
                || !trigger.ProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(trigger.Say)) continue;
            var key = plugin.Id + ":" + trigger.Id;
            if (_lastTriggered.TryGetValue(key, out var last)
                && DateTime.UtcNow - last < TimeSpan.FromMinutes(Math.Clamp(trigger.CooldownMinutes, 1, 1440))) continue;
            _lastTriggered[key] = DateTime.UtcNow;
            return trigger.Say;
        }
        return null;
    }

    private static PluginManifest Parse(string json)
    {
        var plugin = JsonSerializer.Deserialize<PluginManifest>(json, JsonOptions)
            ?? throw new InvalidOperationException("插件 JSON 为空。");
        plugin.Permissions ??= [];
        plugin.Triggers ??= [];
        if (plugin.SchemaVersion != 1) throw new InvalidOperationException("不支持的插件 schemaVersion。");
        if (!Regex.IsMatch(plugin.Id, "^[a-zA-Z0-9][a-zA-Z0-9._-]{1,63}$")) throw new InvalidOperationException("插件 ID 格式无效。");
        if (string.IsNullOrWhiteSpace(plugin.Name) || plugin.Name.Length > 80) throw new InvalidOperationException("插件名称长度无效。");
        if (string.IsNullOrWhiteSpace(plugin.Version) || plugin.Version.Length > 32) throw new InvalidOperationException("插件版本长度无效。");
        if (plugin.Triggers.Count > 64) throw new InvalidOperationException("插件触发器数量过多。");
        if (plugin.Permissions.Any(x => !string.Equals(x, "pet.say", StringComparison.Ordinal)))
            throw new InvalidOperationException("MVP 仅允许 pet.say 权限。");
        if (plugin.Triggers.Count > 0 && !plugin.Permissions.Contains("pet.say", StringComparer.Ordinal))
            throw new InvalidOperationException("包含台词触发器的插件必须声明 pet.say 权限。");
        foreach (var trigger in plugin.Triggers)
        {
            trigger.ProcessNames ??= [];
            if (!Regex.IsMatch(trigger.Id, "^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$")) throw new InvalidOperationException("触发器 ID 格式无效。");
            if (trigger.ProcessNames.Count > 32 || trigger.Say?.Length > 240
                || (trigger.Say?.Any(char.IsControl) ?? false))
                throw new InvalidOperationException("触发器内容超出限制。");
            trigger.ProcessNames = trigger.ProcessNames
                .Select(x => Path.GetFileName(x))
                .Where(x => !string.IsNullOrWhiteSpace(x) && x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                .Select(x => x!)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            trigger.CooldownMinutes = Math.Clamp(trigger.CooldownMinutes, 1, 1440);
        }
        if (plugin.Triggers.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plugin.Triggers.Count)
            throw new InvalidOperationException("触发器 ID 不可重复。");
        return plugin;
    }

    private static bool TryDelete(string path)
    {
        try { File.Delete(path); return !File.Exists(path); }
        catch { return false; }
    }
}
