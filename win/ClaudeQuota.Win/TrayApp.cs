using ClaudeQuota.Core;
using Microsoft.Win32;

namespace ClaudeQuota.Win;

/// <summary>State of one claude.ai account: cached values, history, last read result.</summary>
sealed class AccountState
{
    public AccountInfo Info;
    public readonly QuotaCache Cache;
    public readonly HistoryFile HistoryFile;
    public QuotaSnapshot? Snap;
    public IReadOnlyList<HistoryPoint> History;
    public FetchError Error = FetchError.None;
    public string? Detail;

    public AccountState(AccountInfo info)
    {
        Info = info;
        Cache = new QuotaCache(Path.Combine(ClaudeAiUsageSource.AppDir, info.File("last.json")));
        HistoryFile = new HistoryFile(Path.Combine(ClaudeAiUsageSource.AppDir, info.File("history.json")));
        Snap = Cache.Load();
        History = HistoryFile.Load();
        if (Snap is not null) Error = FetchError.Timeout; // cached value: shown greyed out until the first reading
    }

    public string ExportPath => Path.Combine(ClaudeAiUsageSource.AppDir, Info.File("quota.json"));
    public bool NeedsLogin => Error == FetchError.AuthRequired || (Snap is null && Error == FetchError.NoLimits);

    /// <summary>Deletes this account's local files (cache, history, export).</summary>
    public void DeleteFiles()
    {
        foreach (var f in new[] { "last.json", "history.json", "quota.json" })
            try { File.Delete(Path.Combine(ClaudeAiUsageSource.AppDir, Info.File(f))); } catch (IOException) { }
    }
}

/// <summary>The app: tray icon, periodic refresh, panel with one tab per account, sign-in, threshold notifications.</summary>
public sealed class TrayApp : ApplicationContext
{
    static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "ClaudeQuotaWidget";

    readonly NotifyIcon _tray = new() { Text = "Claude Quota", Visible = true };
    readonly FlyoutForm _flyout = new();
    readonly System.Windows.Forms.Timer _timer = new() { Interval = (int)Interval.TotalMilliseconds };
    readonly ToolStripMenuItem _autostart = new(L10n.T("Start with Windows", "Lancer au démarrage de Windows")) { CheckOnClick = true };
    readonly ToolStripMenuItem _floatItem = new(L10n.T("Floating window", "Fenêtre flottante")) { CheckOnClick = true };
    readonly ToolStripMenuItem _compactItem = new(L10n.T("Compact floating window", "Fenêtre flottante compacte")) { CheckOnClick = true };
    readonly Settings _settings = Settings.Load();
    readonly FloatForm _float;
    readonly List<AccountState> _accounts = [];

    bool _busy, _loginOpen;
    Icon? _icon;

    AccountState Selected => _accounts.FirstOrDefault(a => a.Info.Id == _settings.Selected) ?? _accounts[0];
    bool Multi => _accounts.Count > 1;
    string LabelOf(AccountState a) => a.Info.Label(_accounts.IndexOf(a));

    public TrayApp()
    {
        if (_settings.Accounts.Count == 0) _settings.Accounts.Add(new AccountInfo(AccountInfo.DefaultId));
        foreach (var info in _settings.Accounts) _accounts.Add(new AccountState(info));

        var menu = new ContextMenuStrip();
        menu.Items.Add(L10n.T("Open panel", "Ouvrir le panneau"), null, (_, _) => ShowFlyout());
        menu.Items.Add(L10n.T("Refresh", "Rafraîchir"), null, async (_, _) => await RefreshAsync());
        menu.Items.Add(L10n.T("Sign in to claude.ai…", "Se connecter à claude.ai…"), null, async (_, _) => await LoginAsync(Selected));
        menu.Items.Add(L10n.T("Add an account…", "Ajouter un compte…"), null, async (_, _) => await AddAccountAsync());
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
        _flyout.LoginRequested += async (_, _) => await LoginAsync(Selected);
        _flyout.TabSelected += (_, i) => { _settings.Selected = _accounts[i].Info.Id; _settings.Save(); Render(); };
        _flyout.TabMenuRequested += (_, e) => ShowTabMenu(e.Index, e.Location);
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
        var first = _accounts[0];
        if (!Multi && first.Snap is null && first.Error is FetchError.AuthRequired or FetchError.NoLimits) await LoginAsync(first);
    }

    /// <summary>Reads every account, one after the other (one hidden browser at a time).</summary>
    async Task RefreshAsync()
    {
        if (_busy || _loginOpen) return;
        _busy = true;
        Render();
        try
        {
            foreach (var a in _accounts.ToList())
            {
                var r = await ClaudeAiUsageSource.FetchAsync(a.Info.Profile);
                if (_accounts.Contains(a)) Apply(a, r);
            }
        }
        finally
        {
            _busy = false;
            Render();
        }
    }

    void Apply(AccountState a, FetchResult r)
    {
        if (r.Ok)
        {
            NotifyThresholds(a, a.Snap, r.Snapshot!);
            a.Snap = r.Snapshot;
            a.Cache.Save(a.Snap!);
            a.History = QuotaHistory.Add(a.History, a.Snap!);
            try
            {
                a.HistoryFile.Save(a.History);
                QuotaExport.Write(a.ExportPath, a.Snap!); // stable JSON for other tools (see README)
            }
            catch (IOException) { /* best effort: the display does not depend on these files */ }
            a.Error = FetchError.None;
            a.Detail = null;
            // First successful read of an unnamed added account: name it after its organization.
            if (!a.Info.IsDefault && string.IsNullOrWhiteSpace(a.Info.Name) && !string.IsNullOrWhiteSpace(r.Org))
                Rename(a, r.Org!);
        }
        else
        {
            if (r.Error == FetchError.AuthRequired && a.Error != FetchError.AuthRequired)
                _tray.ShowBalloonTip(5000, Title(a), L10n.T("claude.ai session expired: click the icon to sign in again.", "Session claude.ai expirée : cliquez sur l'icône pour vous reconnecter."), ToolTipIcon.Warning);
            a.Error = r.Error;
            a.Detail = r.Detail;
        }
        Render();
    }

    string Title(AccountState a) => Multi ? $"Claude Quota · {LabelOf(a)}" : "Claude Quota";

    async Task<bool> LoginAsync(AccountState a)
    {
        if (_loginOpen) return false;
        _loginOpen = true;
        try
        {
            using var f = new LoginForm(a.Info.Profile, Multi ? LabelOf(a) : null);
            if (f.ShowDialog() == DialogResult.OK && f.Result is { } r) { Apply(a, r); return true; }
            return false;
        }
        finally { _loginOpen = false; await Task.CompletedTask; }
    }

    /// <summary>Adds an account with its own browser profile and opens its sign-in window; cancelled = removed.</summary>
    async Task AddAccountAsync()
    {
        if (_loginOpen || _busy) return;
        var a = new AccountState(AccountInfo.New());
        _accounts.Add(a);
        _settings.Accounts.Add(a.Info);
        _settings.Selected = a.Info.Id;
        _settings.Save();
        Render();
        if (!await LoginAsync(a)) await RemoveAccountAsync(a, ask: false);
    }

    async Task RemoveAccountAsync(AccountState a, bool ask = true)
    {
        if (a.Info.IsDefault || !_accounts.Contains(a)) return;
        if (ask && MessageBox.Show(
                L10n.T($"Remove the account \"{LabelOf(a)}\"? Its claude.ai session and history will be deleted from this PC.",
                       $"Supprimer le compte « {LabelOf(a)} » ? Sa session claude.ai et son historique seront effacés de ce PC."),
                "Claude Quota", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _accounts.Remove(a);
        _settings.Accounts.RemoveAll(i => i.Id == a.Info.Id);
        if (_settings.Selected == a.Info.Id) _settings.Selected = _accounts[0].Info.Id;
        _settings.Save();
        a.DeleteFiles();
        Render();
        await ClaudeAiUsageSource.DeleteProfileAsync(a.Info.Profile!);
    }

    void Rename(AccountState a, string name)
    {
        name = name.Trim();
        if (name.Length > 40) name = name[..40];
        a.Info = a.Info with { Name = name };
        var i = _settings.Accounts.FindIndex(x => x.Id == a.Info.Id);
        if (i >= 0) _settings.Accounts[i] = a.Info;
        _settings.Save();
        Render();
    }

    void ShowTabMenu(int index, Point screen)
    {
        if (index < 0 || index >= _accounts.Count) return;
        var a = _accounts[index];
        var m = new ContextMenuStrip();
        m.Items.Add(L10n.T("Rename…", "Renommer…"), null, (_, _) =>
        {
            var name = InputBox.Show(L10n.T("Account name", "Nom du compte"), LabelOf(a));
            if (name is not null) Rename(a, name);
        });
        m.Items.Add(L10n.T("Sign in again…", "Se reconnecter…"), null, async (_, _) => await LoginAsync(a));
        if (!a.Info.IsDefault) m.Items.Add(L10n.T("Remove this account…", "Supprimer ce compte…"), null, async (_, _) => await RemoveAccountAsync(a));
        m.Closed += (_, _) => m.BeginInvoke(m.Dispose);
        m.Show(screen);
    }

    /// <summary>Notification when a limit crosses 80% (once per crossing).</summary>
    void NotifyThresholds(AccountState a, QuotaSnapshot? before, QuotaSnapshot after)
    {
        if (before is null) return;
        var who = Multi ? $" ({LabelOf(a)})" : "";
        foreach (var l in after.Limits)
        {
            var prev = before.Limits.FirstOrDefault(p => p.Kind == l.Kind && p.Model == l.Model);
            if (prev is not null && prev.Percent < Thresholds.Critical && l.Percent >= Thresholds.Critical)
                _tray.ShowBalloonTip(8000, L10n.T($"Claude: {l.Label} at {L10n.Pct(l.Percent)}", $"Claude : {l.Label} à {L10n.Pct(l.Percent)}") + who,
                    TimeText.Reset(l.ResetsAt, DateTimeOffset.Now), ToolTipIcon.Warning);
        }
    }

    void Render()
    {
        // Tray icon: the most constrained limit across all accounts.
        var worst = _accounts.Where(a => a.Snap is not null).MaxBy(a => a.Snap!.Highest?.Percent ?? 0);
        var old = _icon;
        _icon = TrayIconRenderer.Ring(worst?.Snap!.Highest?.Percent, worst is null || worst.Error != FetchError.None,
            _accounts.Any(a => a.NeedsLogin));
        _tray.Icon = _icon;
        old?.Dispose();

        string Line(AccountState a) => a.Snap is null ? L10n.T("no data", "pas de données")
            : string.Join(" · ", a.Snap.Limits.Where(l => l.Kind != "weekly_scoped").Select(l => $"{l.ShortLabel} {L10n.Pct(l.Percent)}"))
              + (a.Error != FetchError.None ? $" ({TimeText.Age(a.Snap.FetchedAt, DateTimeOffset.Now)})" : "");
        var tip = Multi ? string.Join("\n", _accounts.Select(a => $"{LabelOf(a)}: {Line(a)}"))
            : _accounts[0].Snap is null ? L10n.T("Claude Quota: no data", "Claude Quota : pas de données") : "Claude · " + Line(_accounts[0]);
        _tray.Text = tip.Length > 127 ? tip[..127] : tip;

        var s = Selected;
        IReadOnlyList<(string Label, bool Attention)> tabs = Multi ? _accounts.Select(a => (LabelOf(a), a.NeedsLogin)).ToList() : [];
        _flyout.SetState(s.Snap, s.Error, s.Detail, _busy, s.History, tabs, _accounts.IndexOf(s));
        _float.SetState(s.Snap, s.Error, _busy, Multi ? LabelOf(s) : null);
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
        var snap = Selected.Snap;
        if (snap is null || DateTimeOffset.Now - snap.FetchedAt > TimeSpan.FromMinutes(1)) _ = RefreshAsync();
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

/// <summary>Minimal text prompt (rename an account).</summary>
static class InputBox
{
    public static string? Show(string title, string value)
    {
        using var f = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, AutoScaleMode = AutoScaleMode.Dpi,
            ClientSize = new Size(360, 96), Font = new Font("Segoe UI", 9f),
        };
        var box = new TextBox { Text = value, Location = new Point(12, 14), Width = 336, MaxLength = 40 };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(192, 54), Width = 75 };
        var cancel = new Button { Text = L10n.T("Cancel", "Annuler"), DialogResult = DialogResult.Cancel, Location = new Point(273, 54), Width = 75 };
        f.Controls.AddRange([box, ok, cancel]);
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        return f.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(box.Text) ? box.Text : null;
    }
}
