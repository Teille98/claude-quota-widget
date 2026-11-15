using ClaudeQuota.Core;
using System.Text.Json;

namespace ClaudeQuota.Win;

/// <summary>App preferences (floating window), in %LOCALAPPDATA%\ClaudeQuotaWidget\settings.json.</summary>
public sealed class Settings
{
    public bool Floating { get; set; }
    public bool Compact { get; set; }
    /// <summary>Floating window position in screen pixels; null = default bottom-right corner.</summary>
    public int? X { get; set; }
    public int? Y { get; set; }

    /// <summary>claude.ai accounts, in tab order. The first install has only the default account.</summary>
    public List<AccountInfo> Accounts { get; set; } = [];
    /// <summary>Id of the account shown in the panel and the floating window.</summary>
    public string? Selected { get; set; }

    static string FilePath => Path.Combine(ClaudeAiUsageSource.AppDir, "settings.json");
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static Settings Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch (Exception e) when (e is JsonException or IOException or NotSupportedException) { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ClaudeAiUsageSource.AppDir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Opts));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (IOException) { /* preference not saved: harmless */ }
    }
}
