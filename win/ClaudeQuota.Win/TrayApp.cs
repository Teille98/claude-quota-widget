using ClaudeQuota.Core;
using Microsoft.Win32;

namespace ClaudeQuota.Win;

/// <summary>The app: tray icon, periodic refresh, panel, sign-in, threshold notifications.</summary>
public sealed class TrayApp : ApplicationContext
{
    static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "ClaudeQuotaWidget";

    readonly NotifyIcon _tray = new() { Text = "Claude Quota", Visible = true };
    readonly FlyoutForm _flyout = new();
    readonly QuotaCache _cache = new(Path.Combine(ClaudeAiUsageSource.AppDir, "last.json"));
    readonly HistoryFile _historyFile = new(Path.Combine(ClaudeAiUsageSource.AppDir, "history.json"));
    static readonly string ExportPath = Path.Combine(ClaudeAiUsageSource.AppDir, "quota.json");
    IReadOnlyList<HistoryPoint> _history = [];
    readonly System.Windows.Forms.Timer _timer = new() { Interval = (int)Interval.TotalMilliseconds };
    readonly ToolStripMenuItem _autostart = new(L10n.T("Start with Windows", "Lancer au démarrage de Windows")) { CheckOnClick = true };
    readonly ToolStripMenuItem _floatItem = new(L10n.T("Floating window", "Fenêtre flottante")) { CheckOnClick = true };
    readonly ToolStripMenuItem _compactItem = new(L10n.T("Compact floating window", "Fenêtre flottante compacte")) { CheckOnClick = true };
    readonly Settings _settings = Settings.Load();
    readonly FloatForm _float;

    QuotaSnapshot? _snap;
    FetchError _error = FetchError.None;
    string? _detail;
    bool _busy, _loginOpen;
    Icon? _icon;

    public TrayApp()
    {
        _snap = _cache.Load();
        _history = _historyFile.Load();
        if (_snap is not null) _error = FetchError.Timeout; // cached value: shown greyed out until the first reading

        var menu = new ContextMenuStrip();
        menu.Items.Add(L10n.T("Open panel", "Ouvrir le panneau"), null, (_, _) => ShowFlyout());
        menu.Items.Add(L10n.T("Refresh", "Rafraîchir"), null, async (_, _) => await RefreshAsync());
        menu.Items.Add(L10n.T("Sign in to claude.ai…", "Se connecter à claude.ai…"), null, async (_, _) => await LoginAsync());
        menu.Items.Add(new ToolStripSeparator());
        _floatItem.Checked = _settings.Floating;
        _compactItem.Checked = _settings.Compact;
        _compactItem.Enabled = _settings.Floating;
        _floatItem.CheckedChanged += (_, _) => { _settings.Floating = _floatItem.Checked; _compactItem.Enabled = _floatItem.Checked; SaveFloat(); };
        _compactItem.CheckedChanged += (_, _) => { _settings.Compact = _compactItem.Checked; SaveFloat(); };
        menu.Items.Add(_floatItem);
        menu.Items.Add(_compactItem);
        _autostart.Checked = Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue(RunName) is not null;
        _autostart.CheckedChanged += (_, _) => SetAutostart(_autostart.Checked);
        menu.Items.Add(_autostart);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L10n.T("Quit", "Quitter"), null, (_, _) => ExitThread());
        _tray.ContextMenuStrip = menu;
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ToggleFlyout(); };

        _float = new FloatForm(_settings, menu);
        _float.Clicked += (_, _) => ToggleFlyout(_float.Bounds);
        _float.Apply();

        _flyout.RefreshRequested += async (_, _) => await RefreshAsync();
        _flyout.LoginRequested += async (_, _) => await LoginAsync();
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        SystemEvents.PowerModeChanged += OnPower;
        SystemEvents.UserPreferenceChanged += OnPrefs;
        SystemEvents.DisplaySettingsChanged += OnDisplay;

        Render();
        _ = StartAsync();
    }

    async Task StartAsync()
    {
        await Task.Yield();
        await RefreshAsync();
        if (_snap is null && _error is FetchError.AuthRequired or FetchError.NoLimits) await LoginAsync();
    }

    async Task RefreshAsync()
    {
        if (_busy || _loginOpen) return;
        _busy = true;
        Render();
        var r = await ClaudeAiUsageSource.FetchAsync();
        _busy = false;
        Apply(r);
    }

    void Apply(FetchResult r)
    {
        if (r.Ok)
        {
            NotifyThresholds(_snap, r.Snapshot!);
            _snap = r.Snapshot;
            _cache.Save(_snap!);
            _history = QuotaHistory.Add(_history, _snap!);
            try
            {
                _historyFile.Save(_history);
                QuotaExport.Write(ExportPath, _snap!); // stable JSON for other tools (see README)
            }
            catch (IOException) { /* best effort: the display does not depend on these files */ }
            _error = FetchError.None;
            _detail = null;
        }
        else
        {
            if (r.Error == FetchError.AuthRequired && _error != FetchError.AuthRequired)
                _tray.ShowBalloonTip(5000, "Claude Quota", L10n.T("claude.ai session expired: click the icon to sign in again.", "Session claude.ai expirée : cliquez sur l'icône pour vous reconnecter."), ToolTipIcon.Warning);
            _error = r.Error;
            _detail = r.Detail;
        }
        Render();
    }

    async Task LoginAsync()
    {
        if (_loginOpen) return;
        _loginOpen = true;
        try
        {
            using var f = new LoginForm();
            if (f.ShowDialog() == DialogResult.OK && f.Result is { } r) Apply(r);
        }
        finally { _loginOpen = false; }
        await Task.CompletedTask;
    }

    /// <summary>Notification when a limit crosses 80% (once per crossing).</summary>
    void NotifyThresholds(QuotaSnapshot? before, QuotaSnapshot after)
    {
        if (before is null) return;
        foreach (var l in after.Limits)
        {
            var prev = before.Limits.FirstOrDefault(p => p.Kind == l.Kind && p.Model == l.Model);
            if (prev is not null && prev.Percent < Thresholds.Critical && l.Percent >= Thresholds.Critical)
                _tray.ShowBalloonTip(8000, L10n.T($"Claude: {l.Label} at {L10n.Pct(l.Percent)}", $"Claude : {l.Label} à {L10n.Pct(l.Percent)}"),
                    TimeText.Reset(l.ResetsAt, DateTimeOffset.Now), ToolTipIcon.Warning);
        }
    }

    void Render()
    {
        var stale = _error != FetchError.None;
        var old = _icon;
        _icon = TrayIconRenderer.Ring(_snap?.Highest?.Percent, stale, _error == FetchError.AuthRequired);
        _tray.Icon = _icon;
        old?.Dispose();

        var tip = _snap is null ? L10n.T("Claude Quota: no data", "Claude Quota : pas de données")
            : "Claude · " + string.Join(" · ", _snap.Limits.Where(l => l.Kind != "weekly_scoped").Select(l => $"{l.ShortLabel} {L10n.Pct(l.Percent)}"))
              + (stale ? $" ({TimeText.Age(_snap.FetchedAt, DateTimeOffset.Now)})" : "");
        _tray.Text = tip.Length > 127 ? tip[..127] : tip;
        _flyout.SetState(_snap, _error, _detail, _busy, _history);
        _float.SetState(_snap, _error, _busy);
    }

    void ToggleFlyout(Rectangle? anchor = null)
    {
        if (_flyout.Visible) _flyout.Hide();
        else ShowFlyout(anchor);
    }

    void ShowFlyout(Rectangle? anchor = null)
    {
        if (anchor is { } a) _flyout.ShowNear(a);
        else _flyout.ShowNearTray();
        if (_snap is null || DateTimeOffset.Now - _snap.FetchedAt > TimeSpan.FromMinutes(1)) _ = RefreshAsync();
    }

    static void SetAutostart(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) key.SetValue(RunName, $"\"{Application.ExecutablePath}\"");
        else key.DeleteValue(RunName, throwOnMissingValue: false);
    }

    async void OnPower(object? s, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        await Task.Delay(TimeSpan.FromSeconds(10)); // let the network come back
        await RefreshAsync();
    }

    void OnPrefs(object? s, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General) { Render(); _float.Apply(); } // light / dark theme
    }

    void OnDisplay(object? s, EventArgs e) => _float.Apply(); // display plugged / unplugged, resolution

    void SaveFloat()
    {
        _settings.Save();
        _float.Apply();
    }

    protected override void ExitThreadCore()
    {
        SystemEvents.PowerModeChanged -= OnPower;
        SystemEvents.UserPreferenceChanged -= OnPrefs;
        SystemEvents.DisplaySettingsChanged -= OnDisplay;
        _timer.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _flyout.Dispose();
        _float.Dispose();
        _icon?.Dispose();
        base.ExitThreadCore();
    }
}
