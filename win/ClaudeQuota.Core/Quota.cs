using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeQuota.Core;

/// <summary>UI language: English by default, French when the Windows display language is French.</summary>
public enum Lang { En, Fr }

public static class L10n
{
    /// <summary>Language used when none is passed explicitly. Resolved once from the UI culture.</summary>
    public static Lang Current { get; set; } =
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fr" ? Lang.Fr : Lang.En;

    /// <summary>Picks the English or French text.</summary>
    public static string T(string en, string fr, Lang? lang = null) => (lang ?? Current) == Lang.Fr ? fr : en;

    /// <summary>"80%" in English, « 80 % » in French.</summary>
    public static string Pct(int percent, Lang? lang = null) => T($"{percent}%", $"{percent} %", lang);

    public static CultureInfo Culture(Lang? lang = null) =>
        CultureInfo.GetCultureInfo((lang ?? Current) == Lang.Fr ? "fr-FR" : "en-US");
}

/// <summary>One usage limit as shown by claude.ai (5-hour session, week, week per model…).</summary>
public sealed record QuotaLimit(string Kind, int Percent, DateTimeOffset? ResetsAt, string? Severity, string? Model)
{
    [JsonIgnore]
    public string Label => LabelIn(L10n.Current);

    public string LabelIn(Lang lang) => Kind switch
    {
        "session" => L10n.T("Session (5h)", "Session (5 h)", lang),
        "weekly_all" => L10n.T("Week · all models", "Semaine · tous les modèles", lang),
        "weekly_scoped" => Model is null ? L10n.T("Week · dedicated limit", "Semaine · limite dédiée", lang)
                                         : L10n.T($"Week · {Model}", $"Semaine · {Model}", lang),
        _ => Kind,
    };

    /// <summary>Short label for the floating window and widgets.</summary>
    [JsonIgnore]
    public string ShortLabel => ShortLabelIn(L10n.Current);

    public string ShortLabelIn(Lang lang) => Kind switch
    {
        "session" => L10n.T("5h", "5 h", lang),
        "weekly_all" => L10n.T("7d", "7 j", lang),
        _ => Model ?? Kind,
    };
}

public sealed record QuotaSnapshot(IReadOnlyList<QuotaLimit> Limits, DateTimeOffset FetchedAt)
{
    /// <summary>The limit closest to being reached (drives the tray icon).</summary>
    [JsonIgnore]
    public QuotaLimit? Highest => Limits.Count == 0 ? null : Limits.MaxBy(l => l.Percent);

    [JsonIgnore] public QuotaLimit? Session => Limits.FirstOrDefault(l => l.Kind == "session");
    [JsonIgnore] public QuotaLimit? Weekly => Limits.FirstOrDefault(l => l.Kind == "weekly_all");
}

public enum FetchError { None, AuthRequired, Offline, BadFormat, NoLimits, Timeout, Unknown }

public sealed record FetchResult(QuotaSnapshot? Snapshot, FetchError Error, string? Detail = null)
{
    public bool Ok => Error == FetchError.None && Snapshot is not null;
    public static FetchResult Fail(FetchError e, string? detail = null) => new(null, e, detail);
}

public enum Level { Normal, Warning, Critical }

public static class Thresholds
{
    public const int Warning = 50;
    public const int Critical = 80;

    public static Level For(int percent) =>
        percent >= Critical ? Level.Critical : percent >= Warning ? Level.Warning : Level.Normal;
}

/// <summary>Turns the JSON message from <c>shared/usage-script.js</c> into a <see cref="FetchResult"/>.</summary>
public static class UsageParser
{
    public static FetchResult Parse(string? json, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(json)) return FetchResult.Fail(FetchError.BadFormat, "empty response");
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("ok", out var ok))
                return FetchResult.Fail(FetchError.BadFormat, "missing ok field");

            if (ok.ValueKind != JsonValueKind.True)
            {
                var reason = root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
                var detail = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()
                           : root.TryGetProperty("status", out var s) ? $"HTTP {s}" : null;
                return FetchResult.Fail(reason switch
                {
                    "auth" => FetchError.AuthRequired,
                    "offline" => FetchError.Offline,
                    "no-limits" => FetchError.NoLimits,
                    "timeout" => FetchError.Timeout,
                    _ => FetchError.Unknown,
                }, detail ?? reason);
            }

            if (!root.TryGetProperty("limits", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return FetchResult.Fail(FetchError.BadFormat, "missing limits");

            var limits = new List<QuotaLimit>();
            foreach (var l in arr.EnumerateArray())
            {
                var kind = Str(l, "kind");
                if (kind is null || !l.TryGetProperty("percent", out var p) || p.ValueKind != JsonValueKind.Number)
                    return FetchResult.Fail(FetchError.BadFormat, "limit without kind or percent");
                DateTimeOffset? reset = DateTimeOffset.TryParse(Str(l, "resets_at"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var d) ? d : null;
                limits.Add(new QuotaLimit(kind, (int)Math.Round(Math.Clamp(p.GetDouble(), 0, 100)), reset, Str(l, "severity"), Str(l, "model")));
            }
            if (limits.Count == 0) return FetchResult.Fail(FetchError.NoLimits);
            return new FetchResult(new QuotaSnapshot(limits, now), FetchError.None);
        }
        catch (JsonException e)
        {
            return FetchResult.Fail(FetchError.BadFormat, e.Message);
        }
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>Time texts: "Resets in 1h 40 · 15:49", "12 min ago" (French: « Réinitialisation dans 1 h 40 · 15:49 », « il y a 12 min »).</summary>
public static class TimeText
{
    public static string Reset(DateTimeOffset? resetsAt, DateTimeOffset now, TimeZoneInfo? tz = null, Lang? lang = null)
    {
        if (resetsAt is null) return "";
        var l = lang ?? L10n.Current;
        tz ??= TimeZoneInfo.Local;
        var local = TimeZoneInfo.ConvertTime(resetsAt.Value, tz);
        var left = resetsAt.Value - now;
        if (left <= TimeSpan.Zero) return L10n.T("Reset imminent", "Réinitialisation imminente", l);
        if (left < TimeSpan.FromHours(24))
        {
            var hm = local.ToString("HH:mm", CultureInfo.InvariantCulture);
            return L10n.T($"Resets in {Duration(left, l)} · {hm}", $"Réinitialisation dans {Duration(left, l)} · {hm}", l);
        }
        var day = local.ToString("dddd HH:mm", L10n.Culture(l));
        return L10n.T($"Resets {day}", $"Réinitialisation {day}", l);
    }

    /// <summary>Short form: "15:49" within 24 hours, otherwise the day ("Mon" / « lun. »).</summary>
    public static string ResetShort(DateTimeOffset? resetsAt, DateTimeOffset now, TimeZoneInfo? tz = null, Lang? lang = null)
    {
        if (resetsAt is null) return "";
        tz ??= TimeZoneInfo.Local;
        var c = L10n.Culture(lang);
        var local = TimeZoneInfo.ConvertTime(resetsAt.Value, tz);
        return resetsAt.Value - now < TimeSpan.FromHours(24) ? local.ToString("HH:mm", c) : local.ToString("ddd", c);
    }

    public static string Duration(TimeSpan t, Lang? lang = null)
    {
        var fr = (lang ?? L10n.Current) == Lang.Fr;
        if (t.TotalHours >= 1) return fr ? $"{(int)t.TotalHours} h {t.Minutes:00}" : $"{(int)t.TotalHours}h {t.Minutes:00}";
        return $"{Math.Max(1, (int)Math.Ceiling(t.TotalMinutes))} min";
    }

    public static string Age(DateTimeOffset at, DateTimeOffset now, Lang? lang = null)
    {
        var l = lang ?? L10n.Current;
        var a = now - at;
        if (a < TimeSpan.FromMinutes(1)) return L10n.T("just now", "à l'instant", l);
        if (a < TimeSpan.FromHours(1)) return L10n.T($"{(int)a.TotalMinutes} min ago", $"il y a {(int)a.TotalMinutes} min", l);
        if (a < TimeSpan.FromDays(1)) return L10n.T($"{(int)a.TotalHours}h ago", $"il y a {(int)a.TotalHours} h", l);
        return L10n.T($"{(int)a.TotalDays}d ago", $"il y a {(int)a.TotalDays} j", l);
    }
}

/// <summary>Last successful reading, on disk, shown (greyed out) at startup or while offline.</summary>
public sealed class QuotaCache(string path)
{
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public void Save(QuotaSnapshot s)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, Opts));
        File.Move(tmp, path, overwrite: true);
    }

    public QuotaSnapshot? Load()
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<QuotaSnapshot>(File.ReadAllText(path)) : null; }
        catch (Exception e) when (e is JsonException or IOException or NotSupportedException) { return null; }
    }
}

/// <summary>One history sample: session and weekly percentages at a given time.</summary>
public sealed record HistoryPoint(DateTimeOffset At, int? Session, int? Weekly);

/// <summary>7-day usage history, kept small: one point per change, or per 15 min when nothing changes.</summary>
public static class QuotaHistory
{
    public static readonly TimeSpan Keep = TimeSpan.FromDays(7);
    static readonly TimeSpan MinGap = TimeSpan.FromMinutes(15);

    public static IReadOnlyList<HistoryPoint> Add(IReadOnlyList<HistoryPoint> points, QuotaSnapshot s)
    {
        var p = new HistoryPoint(s.FetchedAt, s.Session?.Percent, s.Weekly?.Percent);
        var last = points.Count > 0 ? points[^1] : null;
        var keep = last is null || last.Session != p.Session || last.Weekly != p.Weekly || p.At - last.At >= MinGap;
        var all = keep ? points.Append(p) : points;
        return all.Where(x => p.At - x.At <= Keep).ToList();
    }
}

/// <summary>History file (history.json next to the cache).</summary>
public sealed class HistoryFile(string path)
{
    public IReadOnlyList<HistoryPoint> Load()
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<List<HistoryPoint>>(File.ReadAllText(path)) ?? [] : []; }
        catch (Exception e) when (e is JsonException or IOException or NotSupportedException) { return []; }
    }

    public void Save(IReadOnlyList<HistoryPoint> points)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(points));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>
/// Stable, documented JSON export of the last successful read (quota.json), for other tools
/// (Claude Code status line, scripts…). Field names never change within a schema version.
/// </summary>
public static class QuotaExport
{
    public const int Schema = 1;
    static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep "·" readable
    };

    public static string ToJson(QuotaSnapshot s)
    {
        object Limit(QuotaLimit l) => new Dictionary<string, object?>
        {
            ["kind"] = l.Kind,
            ["label"] = l.LabelIn(Lang.En),
            ["model"] = l.Model,
            ["percent"] = l.Percent,
            ["resets_at"] = l.ResetsAt?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        };
        var doc = new Dictionary<string, object?>
        {
            ["schema"] = Schema,
            ["source"] = "claude-quota-widget",
            ["fetched_at"] = s.FetchedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            ["session_percent"] = s.Session?.Percent,
            ["weekly_percent"] = s.Weekly?.Percent,
            ["limits"] = s.Limits.Select(Limit).ToList(),
        };
        return JsonSerializer.Serialize(doc, Opts);
    }

    public static void Write(string path, QuotaSnapshot s)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, ToJson(s));
        File.Move(tmp, path, overwrite: true);
    }
}
