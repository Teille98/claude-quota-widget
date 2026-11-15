using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using ClaudeQuota.Core;

namespace ClaudeQuota.Win;

/// <summary>Panel opened by clicking the tray icon (or the floating window).</summary>
public sealed class FlyoutForm : Form
{
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    const int W = 360, Pad = 18, HeaderH = 62, RowH = 64, BannerH = 64, FooterH = 56, HistoryH = 140;

    readonly Button _refresh = new() { Text = L10n.T("Refresh", "Rafraîchir") };
    readonly Button _login = new() { Text = L10n.T("Sign in", "Se connecter"), Visible = false };
    readonly FlowLayoutPanel _footer = new() { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };

    QuotaSnapshot? _snap;
    FetchError _error;
    string? _detail;
    bool _busy;
    IReadOnlyList<HistoryPoint> _history = [];
    Theme _t = Theme.Current();

    public event EventHandler? RefreshRequested;
    public event EventHandler? LoginRequested;

    public FlyoutForm()
    {
        Text = "Claude Quota";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9f);
        foreach (var b in new[] { _refresh, _login })
        {
            b.FlatStyle = FlatStyle.Flat;
            b.AutoSize = true;
            b.MinimumSize = new Size(0, 36);
            b.Padding = new Padding(10, 0, 10, 0);
            b.Margin = new Padding(0, 0, 8, 0);
            _footer.Controls.Add(b);
        }
        _footer.Padding = new Padding(12, 10, 12, 10);
        _footer.Height = FooterH;
        Controls.Add(_footer);
        _refresh.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        _login.Click += (_, _) => { Hide(); LoginRequested?.Invoke(this, EventArgs.Empty); };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
        Deactivate += (_, _) => Hide();
        ApplyTheme();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2; // DWMWCP_ROUND (Windows 11)
        DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
    }

    float S(float v) => v * DeviceDpi / 96f;

    void ApplyTheme()
    {
        _t = Theme.Current();
        BackColor = _t.Surface;
        _footer.BackColor = _t.Surface;
        foreach (var b in new[] { _refresh, _login })
        {
            b.ForeColor = _t.Text;
            b.BackColor = _t.Surface;
            b.FlatAppearance.BorderColor = _t.Border;
        }
        _login.ForeColor = _t.Warning;
        _login.FlatAppearance.BorderColor = _t.Warning;
    }

    public void SetState(QuotaSnapshot? snap, FetchError error, string? detail, bool busy, IReadOnlyList<HistoryPoint> history)
    {
        _snap = snap; _error = error; _detail = detail; _busy = busy; _history = history;
        _refresh.Enabled = !busy;
        _refresh.Text = busy ? L10n.T("Reading…", "Lecture…") : L10n.T("Refresh", "Rafraîchir");
        _login.Visible = error == FetchError.AuthRequired || snap is null;
        var rows = snap?.Limits.Count ?? 0;
        var h = HeaderH + (HasBanner ? BannerH : 0) + rows * RowH + (rows > 0 ? 8 + HistoryH : 0) + FooterH;
        ClientSize = new Size((int)S(W), (int)S(h));
        Invalidate();
    }

    bool HasBanner => _error != FetchError.None || _snap is null;

    public void ShowNearTray()
    {
        ApplyTheme();
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        var m = (int)S(12);
        Location = new Point(area.Right - Width - m, area.Bottom - Height - m);
        Show();
        Activate();
    }

    /// <summary>Opens the panel next to <paramref name="anchor"/> (the floating window): above if there is room, otherwise below.</summary>
    public void ShowNear(Rectangle anchor)
    {
        ApplyTheme();
        var area = Screen.FromRectangle(anchor).WorkingArea;
        var gap = (int)S(8);
        var x = Math.Clamp(anchor.Right - Width, area.Left, area.Right - Width);
        var y = anchor.Top - gap - Height >= area.Top ? anchor.Top - gap - Height : anchor.Bottom + gap;
        Location = new Point(x, Math.Clamp(y, area.Top, area.Bottom - Height));
        Show();
        Activate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        var now = DateTimeOffset.Now;
        using var title = new Font("Segoe UI Semibold", 11f);
        using var small = new Font("Segoe UI", 8.5f);
        using var label = new Font("Segoe UI Semibold", 9.5f);
        using var big = new Font("Segoe UI Semibold", 14f);
        using var muted = new SolidBrush(_t.Muted);
        using var text = new SolidBrush(_t.Text);
        using (var border = new Pen(_t.Border)) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        float x = S(Pad), y = S(14), right = Width - S(Pad);
        g.DrawString("Claude", title, text, x, y);
        var age = _busy ? L10n.T("Reading…", "Lecture en cours…") : _snap is null ? "" : L10n.T($"Updated {TimeText.Age(_snap.FetchedAt, now)}", $"Mis à jour {TimeText.Age(_snap.FetchedAt, now)}");
        var ageSize = g.MeasureString(age, small);
        g.DrawString(age, small, muted, right - ageSize.Width, y + S(4));
        g.DrawString(L10n.T("claude.ai plan usage", "Quota de l'abonnement claude.ai"), small, muted, x, y + S(22));
        y = S(HeaderH);

        if (HasBanner)
        {
            var (head, body, color) = Banner(now);
            using var cb = new SolidBrush(color);
            g.DrawString(head, label, cb, x, y);
            g.DrawString(body, small, muted, new RectangleF(x, y + S(20), right - x, S(BannerH - 24)));
            y += S(BannerH);
        }

        if (_snap is null) return;
        var stale = _error != FetchError.None;
        foreach (var l in _snap.Limits)
        {
            var color = stale ? _t.Muted : _t.For(Thresholds.For(l.Percent));
            using var cb = new SolidBrush(color);
            g.DrawString(l.Label, label, stale ? muted : text, x, y + S(6));
            var pct = L10n.Pct(l.Percent);
            var ps = g.MeasureString(pct, big);
            g.DrawString(pct, big, cb, right - ps.Width, y);
            var barY = y + S(32);
            var barW = right - x;
            using (var track = new SolidBrush(_t.Track)) FillRound(g, track, x, barY, barW, S(6));
            if (l.Percent > 0) FillRound(g, cb, x, barY, Math.Max(S(6), barW * l.Percent / 100f), S(6));
            g.DrawString(TimeText.Reset(l.ResetsAt, now), small, muted, x, barY + S(10));
            y += S(RowH);
        }
        PaintHistory(g, x, y + S(4), right, small, muted, now);
    }

    /// <summary>7-day chart: weekly usage (thick line), session usage (thin line), 50 % / 80 % thresholds.</summary>
    void PaintHistory(Graphics g, float left, float top, float right, Font small, Brush muted, DateTimeOffset now)
    {
        using var label = new Font("Segoe UI Semibold", 9.5f);
        using var text = new SolidBrush(_t.Text);
        g.DrawString(L10n.T("Last 7 days", "7 derniers jours"), label, text, left, top);
        float axis = S(22), cl = left + axis, ct = top + S(24), cb = top + S(HistoryH - 30);
        var start = now - QuotaHistory.Keep;
        float X(DateTimeOffset t) => cl + (right - cl) * (float)((t - start) / QuotaHistory.Keep);
        float Y(int p) => cb - (cb - ct) * p / 100f;

        using var grid = new Pen(_t.Track);
        foreach (var p in new[] { 0, 50, 100 })
        {
            g.DrawLine(grid, cl, Y(p), right, Y(p));
            var sz = g.MeasureString($"{p}", small);
            g.DrawString($"{p}", small, muted, cl - sz.Width - S(2), Y(p) - sz.Height / 2);
        }
        using (var w = new Pen(_t.Warning) { DashStyle = DashStyle.Dash }) g.DrawLine(w, cl, Y(50), right, Y(50));
        using (var c = new Pen(_t.Critical) { DashStyle = DashStyle.Dash }) g.DrawLine(c, cl, Y(80), right, Y(80));

        // Day labels at local midnights.
        for (var d = start.LocalDateTime.Date.AddDays(1); d <= now.LocalDateTime; d = d.AddDays(1))
        {
            var x = X(new DateTimeOffset(d));
            g.DrawLine(grid, x, ct, x, cb);
            g.DrawString(d.ToString("ddd", L10n.Culture()), small, muted, x + S(1), cb + S(2));
        }

        if (_history.Count < 2)
        {
            g.DrawString(L10n.T("The history fills up as the quota is read.", "L'historique se remplit au fil des lectures."),
                small, muted, cl + S(6), Y(30) - S(8));
            return;
        }
        Series(g, p => p.Session, _t.Muted, S(1.5f), X, Y);
        Series(g, p => p.Weekly, _t.Normal, S(2.5f), X, Y);
    }

    /// <summary>Draws one series; a gap of more than an hour (PC asleep, offline) breaks the line.</summary>
    void Series(Graphics g, Func<HistoryPoint, int?> value, Color color, float width, Func<DateTimeOffset, float> x, Func<int, float> y)
    {
        using var pen = new Pen(color, width) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        var run = new List<PointF>();
        HistoryPoint? prev = null;
        void Flush() { if (run.Count > 1) g.DrawLines(pen, run.ToArray()); run.Clear(); }
        foreach (var p in _history)
        {
            if (value(p) is not int v) { Flush(); prev = null; continue; }
            if (prev is not null && p.At - prev.At > TimeSpan.FromHours(1)) Flush();
            run.Add(new PointF(x(p.At), y(v)));
            prev = p;
        }
        Flush();
    }

    (string Head, string Body, Color Color) Banner(DateTimeOffset now)
    {
        var since = _snap is null ? "" : L10n.T($" Last values: {TimeText.Age(_snap.FetchedAt, now)}.", $" Dernières valeurs : {TimeText.Age(_snap.FetchedAt, now)}.");
        return _error switch
        {
            FetchError.AuthRequired => (L10n.T("Sign-in required", "Reconnexion nécessaire"), L10n.T("The claude.ai session has expired.", "La session claude.ai a expiré.") + since, _t.Warning),
            FetchError.Offline or FetchError.Timeout => (L10n.T("Offline", "Hors ligne"), L10n.T("claude.ai is not responding.", "claude.ai ne répond pas.") + since, _t.Muted),
            FetchError.BadFormat or FetchError.NoLimits => (L10n.T("Unreadable data", "Données illisibles"),
                L10n.T("claude.ai answered in an unexpected format: the widget probably needs an update.",
                       "claude.ai a répondu dans un format inattendu : le widget a sans doute besoin d'une mise à jour."), _t.Critical),
            FetchError.Unknown => (L10n.T("Error", "Erreur"), (_detail ?? L10n.T("Unknown error.", "Erreur inconnue.")) + since, _t.Critical),
            _ when _busy => (L10n.T("Reading…", "Lecture en cours…"), L10n.T("Querying claude.ai.", "Interrogation de claude.ai."), _t.Muted),
            _ => (L10n.T("No data yet", "Pas encore de données"), L10n.T("Sign in to your Claude account to show the quota.", "Connectez-vous à votre compte Claude pour afficher le quota."), _t.Muted),
        };
    }

    static void FillRound(Graphics g, Brush b, float x, float y, float w, float h)
    {
        using var p = new GraphicsPath();
        var d = h;
        p.AddArc(x, y, d, d, 90, 180);
        p.AddArc(x + w - d, y, d, d, 270, 180);
        p.CloseFigure();
        g.FillPath(b, p);
    }
}
