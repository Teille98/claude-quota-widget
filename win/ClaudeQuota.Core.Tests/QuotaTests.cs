using ClaudeQuota.Core;

namespace ClaudeQuota.Core.Tests;

public class UsageParserTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 10, 0, TimeSpan.Zero);

    // Real shape observed on 2026-11-15 (Spike B), after going through shared/usage-script.js.
    const string Real = """
        {"ok":true,"limits":[
          {"kind":"session","percent":8,"resets_at":"2026-11-15T14:49:59.715667+00:00","severity":"normal","model":null},
          {"kind":"weekly_all","percent":32,"resets_at":"2026-11-16T18:59:59.715687+00:00","severity":"normal","model":null},
          {"kind":"weekly_scoped","percent":0,"resets_at":"2026-11-16T19:00:00+00:00","severity":"normal","model":"Fable"}]}
        """;

    [Fact]
    public void Parse_real_response()
    {
        var r = UsageParser.Parse(Real, Now);
        Assert.True(r.Ok);
        var l = r.Snapshot!.Limits;
        Assert.Equal(3, l.Count);
        Assert.Equal(("session", 8), (l[0].Kind, l[0].Percent));
        Assert.Equal(new DateTimeOffset(2026, 11, 15, 14, 49, 59, TimeSpan.Zero), l[0].ResetsAt!.Value.AddTicks(-(l[0].ResetsAt!.Value.Ticks % TimeSpan.TicksPerSecond)));
        Assert.Equal("Semaine · Fable", l[2].LabelIn(Lang.Fr));
        Assert.Equal("Week · Fable", l[2].LabelIn(Lang.En));
        Assert.Equal("weekly_all", r.Snapshot.Highest!.Kind);
        Assert.Equal(Now, r.Snapshot.FetchedAt);
    }

    [Theory]
    [InlineData("""{"ok":false,"reason":"auth","status":401}""", FetchError.AuthRequired)]
    [InlineData("""{"ok":false,"reason":"offline","message":"TypeError: Failed to fetch"}""", FetchError.Offline)]
    [InlineData("""{"ok":false,"reason":"no-limits","orgs":3}""", FetchError.NoLimits)]
    [InlineData("""{"ok":false,"reason":"timeout"}""", FetchError.Timeout)]
    [InlineData("""{"ok":false,"reason":"error","status":500}""", FetchError.Unknown)]
    public void Parse_failures(string json, FetchError expected)
    {
        var r = UsageParser.Parse(json, Now);
        Assert.False(r.Ok);
        Assert.Equal(expected, r.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"limits":[]}""")]
    [InlineData("""{"ok":true}""")]
    [InlineData("""{"ok":true,"limits":[{"kind":"session"}]}""")]
    [InlineData("""{"ok":true,"limits":[{"percent":"8"}]}""")]
    public void Parse_unexpected_format_is_BadFormat(string json)
    {
        Assert.Equal(FetchError.BadFormat, UsageParser.Parse(json, Now).Error);
    }

    [Fact]
    public void Parse_clamps_and_rounds_percent()
    {
        var r = UsageParser.Parse("""{"ok":true,"limits":[{"kind":"session","percent":104.6},{"kind":"weekly_all","percent":31.5}]}""", Now);
        Assert.Equal(100, r.Snapshot!.Limits[0].Percent);
        Assert.Equal(32, r.Snapshot.Limits[1].Percent);
        Assert.Null(r.Snapshot.Limits[0].ResetsAt);
    }
}

public class ThresholdsTests
{
    [Theory]
    [InlineData(0, Level.Normal)]
    [InlineData(49, Level.Normal)]
    [InlineData(50, Level.Warning)]
    [InlineData(79, Level.Warning)]
    [InlineData(80, Level.Critical)]
    [InlineData(100, Level.Critical)]
    public void Levels(int pct, Level expected) => Assert.Equal(expected, Thresholds.For(pct));
}

public class TimeTextTests
{
    static readonly TimeZoneInfo Paris = TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris");
    static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 10, 0, TimeSpan.Zero); // 13:10 in Paris

    [Fact]
    public void Reset_within_a_day_shows_duration_and_time()
    {
        var reset = new DateTimeOffset(2026, 11, 15, 14, 49, 59, TimeSpan.Zero);
        Assert.Equal("Réinitialisation dans 2 h 39 · 15:49", TimeText.Reset(reset, Now, Paris, Lang.Fr));
        Assert.Equal("Resets in 2h 39 · 15:49", TimeText.Reset(reset, Now, Paris, Lang.En));
        Assert.Equal("15:49", TimeText.ResetShort(reset, Now, Paris, Lang.Fr));
        Assert.Equal("15:49", TimeText.ResetShort(reset, Now, Paris, Lang.En));
    }

    [Fact]
    public void Reset_later_shows_day_name()
    {
        var reset = new DateTimeOffset(2026, 11, 16, 18, 59, 59, TimeSpan.Zero);
        Assert.Equal("Réinitialisation lundi 19:59", TimeText.Reset(reset, Now, Paris, Lang.Fr));
        Assert.Equal("Resets Monday 19:59", TimeText.Reset(reset, Now, Paris, Lang.En));
        Assert.Equal("lun.", TimeText.ResetShort(reset, Now, Paris, Lang.Fr));
        Assert.Equal("Mon", TimeText.ResetShort(reset, Now, Paris, Lang.En));
    }

    [Fact]
    public void Reset_past_or_missing()
    {
        Assert.Equal("Réinitialisation imminente", TimeText.Reset(Now.AddMinutes(-1), Now, Paris, Lang.Fr));
        Assert.Equal("Reset imminent", TimeText.Reset(Now.AddMinutes(-1), Now, Paris, Lang.En));
        Assert.Equal("", TimeText.Reset(null, Now, Paris, Lang.En));
    }

    [Theory]
    [InlineData(30, "à l'instant")]
    [InlineData(12 * 60, "il y a 12 min")]
    [InlineData(3 * 3600, "il y a 3 h")]
    [InlineData(2 * 86400, "il y a 2 j")]
    public void Age_fr(int seconds, string expected) => Assert.Equal(expected, TimeText.Age(Now.AddSeconds(-seconds), Now, Lang.Fr));

    [Theory]
    [InlineData(30, "just now")]
    [InlineData(12 * 60, "12 min ago")]
    [InlineData(3 * 3600, "3h ago")]
    [InlineData(2 * 86400, "2d ago")]
    public void Age_en(int seconds, string expected) => Assert.Equal(expected, TimeText.Age(Now.AddSeconds(-seconds), Now, Lang.En));

    [Fact]
    public void Duration_under_an_hour() => Assert.Equal("22 min", TimeText.Duration(TimeSpan.FromMinutes(21.2), Lang.En));

    [Fact]
    public void Duration_hours()
    {
        Assert.Equal("1 h 40", TimeText.Duration(TimeSpan.FromMinutes(100), Lang.Fr));
        Assert.Equal("1h 40", TimeText.Duration(TimeSpan.FromMinutes(100), Lang.En));
    }
}

public class LabelTests
{
    static readonly QuotaLimit Session = new("session", 8, null, null, null);
    static readonly QuotaLimit Week = new("weekly_all", 32, null, null, null);

    [Fact]
    public void Labels_fr()
    {
        Assert.Equal("Session (5 h)", Session.LabelIn(Lang.Fr));
        Assert.Equal("Semaine · tous les modèles", Week.LabelIn(Lang.Fr));
        Assert.Equal("5 h", Session.ShortLabelIn(Lang.Fr));
        Assert.Equal("7 j", Week.ShortLabelIn(Lang.Fr));
    }

    [Fact]
    public void Labels_en()
    {
        Assert.Equal("Session (5h)", Session.LabelIn(Lang.En));
        Assert.Equal("Week · all models", Week.LabelIn(Lang.En));
        Assert.Equal("5h", Session.ShortLabelIn(Lang.En));
        Assert.Equal("7d", Week.ShortLabelIn(Lang.En));
        Assert.Equal("Week · dedicated limit", new QuotaLimit("weekly_scoped", 0, null, null, null).LabelIn(Lang.En));
    }
}

public class QuotaCacheTests
{
    [Fact]
    public void Save_then_load_roundtrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cq-{Guid.NewGuid():N}", "last.json");
        var cache = new QuotaCache(path);
        Assert.Null(cache.Load());
        var snap = new QuotaSnapshot([new QuotaLimit("session", 8, DateTimeOffset.UnixEpoch, "normal", null)], DateTimeOffset.UnixEpoch);
        cache.Save(snap);
        var back = cache.Load()!;
        Assert.Equal(snap.FetchedAt, back.FetchedAt);
        Assert.Equal(snap.Limits[0], back.Limits[0]);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public void Corrupt_file_loads_as_null()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cq-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{oops");
        Assert.Null(new QuotaCache(path).Load());
        File.Delete(path);
    }
}

public class HistoryTests
{
    static readonly DateTimeOffset T0 = new(2026, 11, 15, 13, 10, 0, TimeSpan.Zero);

    static QuotaSnapshot Snap(DateTimeOffset at, int session, int weekly) =>
        new([new QuotaLimit("session", session, null, null, null), new QuotaLimit("weekly_all", weekly, null, null, null)], at);

    [Fact]
    public void KeepsChangesAndHeartbeat()
    {
        IReadOnlyList<HistoryPoint> h = QuotaHistory.Add([], Snap(T0, 10, 30));
        h = QuotaHistory.Add(h, Snap(T0.AddMinutes(5), 10, 30));   // same values, 5 min later: skipped
        Assert.Single(h);
        h = QuotaHistory.Add(h, Snap(T0.AddMinutes(6), 12, 30));   // changed: kept
        h = QuotaHistory.Add(h, Snap(T0.AddMinutes(30), 12, 30));  // unchanged but 24 min later: kept
        Assert.Equal([10, 12, 12], h.Select(p => p.Session!.Value));
    }

    [Fact]
    public void DropsOlderThanSevenDays()
    {
        var h = QuotaHistory.Add(QuotaHistory.Add([], Snap(T0, 1, 1)), Snap(T0.AddDays(8), 2, 2));
        Assert.Equal([2], h.Select(p => p.Session!.Value));
    }

    [Fact]
    public void FileRoundTripAndCorruption()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cq-history-{Guid.NewGuid():N}.json");
        try
        {
            var f = new HistoryFile(path);
            Assert.Empty(f.Load());
            var h = QuotaHistory.Add([], Snap(T0, 5, 40));
            f.Save(h);
            Assert.Equal(h, f.Load());
            File.WriteAllText(path, "{broken");
            Assert.Empty(f.Load());
        }
        finally { File.Delete(path); }
    }
}

public class ExportTests
{
    [Fact]
    public void StableSchema()
    {
        var s = new QuotaSnapshot(
            [new QuotaLimit("session", 16, new DateTimeOffset(2026, 11, 15, 14, 50, 0, TimeSpan.Zero), "normal", null),
             new QuotaLimit("weekly_scoped", 0, null, "normal", "Fable")],
            new DateTimeOffset(2026, 11, 15, 15, 10, 0, TimeSpan.FromHours(1)));
        using var doc = System.Text.Json.JsonDocument.Parse(QuotaExport.ToJson(s));
        var r = doc.RootElement;
        Assert.Equal(1, r.GetProperty("schema").GetInt32());
        Assert.Equal("2026-11-15T14:10:00Z", r.GetProperty("fetched_at").GetString());
        Assert.Equal(16, r.GetProperty("session_percent").GetInt32());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, r.GetProperty("weekly_percent").ValueKind);
        var l0 = r.GetProperty("limits")[0];
        Assert.Equal("Session (5h)", l0.GetProperty("label").GetString());
        Assert.Equal("2026-11-15T14:50:00Z", l0.GetProperty("resets_at").GetString());
        Assert.Equal("Fable", r.GetProperty("limits")[1].GetProperty("model").GetString());
    }
}
