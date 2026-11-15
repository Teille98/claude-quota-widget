using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using ClaudeQuota.Core;

namespace ClaudeQuota.Win;

/// <summary>
/// Small always-on-top floating window: session and week at a glance.
/// Never takes focus, can be dragged with the mouse, hides when an app is fullscreen.
/// </summary>
public sealed class FloatForm : Form
{
    #region Win32
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x8;
    static readonly IntPtr HWND_TOPMOST = new(-1);
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
    #endregion

    const double RestOpacity = 0.82;

    readonly Settings _settings;
    readonly System.Windows.Forms.Timer _watch = new() { Interval = 1500 };
    readonly ToolTip _tip = new() { InitialDelay = 600, ShowAlways = true };
    QuotaSnapshot? _snap;
    FetchError _error;
    bool _busy, _hiddenForFullscreen, _dragging;
    Point _down;
    Theme _t = Theme.Current();
    int _ticks;

    /// <summary>Simple click (without dragging): open the detailed panel.</summary>
    public event EventHandler? Clicked;

    public FloatForm(Settings settings, ContextMenuStrip menu)
    {
        _settings = settings;
        Text = L10n.T("Claude Quota (floating)", "Claude Quota (flottant)");
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        Opacity = RestOpacity;
        ContextMenuStrip = menu;
        Cursor = Cursors.Hand;
        _watch.Tick += (_, _) => Watch();
    }

    // Tool window (not in Alt+Tab) that never steals focus.
    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST; return p; }
    }
    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 3; // DWMWCP_ROUNDSMALL (Windows 11)
        DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
    }

    float S(float v) => v * DeviceDpi / 96f;

    /// <summary>Shows or hides according to preferences, at the right size and position.</summary>
    public void Apply()
    {
        if (!_settings.Floating) { _watch.Stop(); Hide(); return; }
        _t = Theme.Current();
        Resize2();
        Place();
        _watch.Start();
        if (!_hiddenForFullscreen) Show();
        Invalidate();
    }

    void Resize2() => ClientSize = _settings.Compact ? new Size((int)S(96), (int)S(52)) : new Size((int)S(236), (int)S(58));

    void Place()
    {
        var p = _settings.X is int x && _settings.Y is int y ? new Point(x, y) : (Point?)null;
        // Use the saved position only if it is still (at least partly) on a screen: display unplugged, etc.
        if (p is { } q && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new Rectangle(q, Size) with { Width = Width / 2, Height = Height / 2 })))
        {
            Location = q;
            return;
        }
        var a = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(a.Right - Width - (int)S(16), a.Bottom - Height - (int)S(16));
    }

    public void SetState(QuotaSnapshot? snap, FetchError error, bool busy)
    {
        _snap = snap; _error = error; _busy = busy;
        _tip.SetToolTip(this, Tooltip());
        Invalidate();
    }

    string Tooltip()
    {
        if (_snap is null) return L10n.T("Claude Quota: no data yet. Click: open the panel.", "Claude Quota : pas encore de données. Clic : ouvrir le panneau.");
        var now = DateTimeOffset.Now;
        var lines = _snap.Limits.Select(l => L10n.T($"{l.Label}: {L10n.Pct(l.Percent)} · {TimeText.Reset(l.ResetsAt, now)}", $"{l.Label} : {L10n.Pct(l.Percent)} · {TimeText.Reset(l.ResetsAt, now)}"));
        var state = _error switch
        {
            FetchError.None => L10n.T($"Updated {TimeText.Age(_snap.FetchedAt, now)}", $"Mis à jour {TimeText.Age(_snap.FetchedAt, now)}"),
            FetchError.AuthRequired => L10n.T("Sign-in required", "Reconnexion nécessaire"),
            _ => L10n.T($"Last successful reading {TimeText.Age(_snap.FetchedAt, now)}", $"Dernière lecture réussie {TimeText.Age(_snap.FetchedAt, now)}"),
        };
        return string.Join("\n", lines.Append(state).Append(L10n.T("Click: panel · drag: move · right-click: menu", "Clic : panneau · glisser : déplacer · clic droit : menu")));
    }

    // --- Hover, drag, click --------------------------------------------------------------------

    protected override void OnMouseEnter(EventArgs e) { Opacity = 1; base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { if (!_dragging) Opacity = RestOpacity; base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) { _down = e.Location; _dragging = false; Capture = true; }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (Capture && e.Button == MouseButtons.Left)
        {
            var d = SystemInformation.DragSize;
            if (!_dragging && (Math.Abs(e.X - _down.X) > d.Width || Math.Abs(e.Y - _down.Y) > d.Height)) _dragging = true;
            if (_dragging) Location = Snap(new Point(Cursor.Position.X - _down.X, Cursor.Position.Y - _down.Y));
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            Capture = false;
            if (_dragging)
            {
                _dragging = false;
                _settings.X = Left; _settings.Y = Top;
                _settings.Save();
                if (!ClientRectangle.Contains(e.Location)) Opacity = RestOpacity;
            }
            else Clicked?.Invoke(this, EventArgs.Empty);
        }
        base.OnMouseUp(e);
    }

    /// <summary>Snaps to the screen edges (within 12 px).</summary>
    Point Snap(Point p)
    {
        var a = Screen.FromPoint(Cursor.Position).WorkingArea;
        var m = (int)S(12);
        int x = p.X, y = p.Y;
        if (Math.Abs(x - a.Left) < m) x = a.Left; else if (Math.Abs(a.Right - (x + Width)) < m) x = a.Right - Width;
        if (Math.Abs(y - a.Top) < m) y = a.Top; else if (Math.Abs(a.Bottom - (y + Height)) < m) y = a.Bottom - Height;
        return new Point(x, y);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Resize2();
        Invalidate();
    }

    // --- Fullscreen and staying on top ----------------------------------------------------------

    void Watch()
    {
        if (!_settings.Floating) return;
        var full = ForegroundIsFullscreen();
        if (full && Visible) { _hiddenForFullscreen = true; Hide(); }
        else if (!full && _hiddenForFullscreen) { _hiddenForFullscreen = false; Show(); }
        if (Visible)
        {
            // Other always-on-top windows (taskbar…) may come in front: re-assert our position.
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            if (++_ticks % 20 == 0) Invalidate(); // "… ago" and reset times
        }
    }

    /// <summary>True if the active window covers the whole screen the floating window is on (video, game, presentation).</summary>
    bool ForegroundIsFullscreen()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == Handle || !GetWindowRect(fg, out var r)) return false;
        var cls = new System.Text.StringBuilder(64);
        GetClassName(fg, cls, cls.Capacity);
        if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd") return false; // desktop, taskbar
        var screen = Screen.FromHandle(fg).Bounds;
        if (!screen.IntersectsWith(Bounds)) return false;
        return r.L <= screen.Left && r.T <= screen.Top && r.R >= screen.Right && r.B >= screen.Bottom;
    }

    // --- Drawing --------------------------------------------------------------------------------

    protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(_t.Surface);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using (var border = new Pen(_t.Border)) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        var stale = _error != FetchError.None;
        var limits = new[] { _snap?.Limits.FirstOrDefault(l => l.Kind == "session"), _snap?.Limits.FirstOrDefault(l => l.Kind == "weekly_all") };
        if (_settings.Compact) PaintCompact(g, limits, stale);
        else PaintWide(g, limits, stale);

        if (_error == FetchError.AuthRequired)
        {
            var d = S(7);
            using var b = new SolidBrush(_t.Warning);
            g.FillEllipse(b, Width - d - S(5), S(5), d, d);
        }
    }

    Color ColorFor(QuotaLimit? l, bool stale) => l is null || stale ? _t.Muted : _t.For(Thresholds.For(l.Percent));

    void PaintWide(Graphics g, QuotaLimit?[] limits, bool stale)
    {
        using var label = new Font("Segoe UI", 8.5f);
        using var pctFont = new Font("Segoe UI Semibold", 9.5f);
        using var muted = new SolidBrush(_t.Muted);
        using var text = new SolidBrush(_t.Text);
        if (_snap is null)
        {
            var msg = _busy ? L10n.T("Reading…", "Lecture…") : _error == FetchError.AuthRequired ? L10n.T("Sign-in required", "Reconnexion nécessaire") : L10n.T("No data", "Pas de données");
            using var c = new SolidBrush(_error == FetchError.AuthRequired ? _t.Warning : _t.Muted);
            var sz = g.MeasureString(msg, label);
            g.DrawString(msg, label, c, (Width - sz.Width) / 2, (Height - sz.Height) / 2);
            return;
        }
        var now = DateTimeOffset.Now;
        var rowH = S(21);
        var y0 = (Height - 2 * rowH) / 2;
        string[] names = [L10n.T("5h", "5 h"), L10n.T("Wk", "Sem.")];
        for (var i = 0; i < 2; i++)
        {
            var l = limits[i];
            var cy = y0 + i * rowH + rowH / 2;
            var color = ColorFor(l, stale);
            float x = S(10);
            // ring
            var rs = S(12);
            DrawRing(g, new RectangleF(x, cy - rs / 2, rs, rs), l?.Percent, color, S(2.2f));
            x += rs + S(7);
            // label
            var ls = g.MeasureString(names[i], label);
            g.DrawString(names[i], label, stale ? muted : text, x, cy - ls.Height / 2);
            x += S(32);
            // percentage, right-aligned in its column
            var pct = l is null ? "–" : L10n.Pct(l.Percent);
            var ps = g.MeasureString(pct, pctFont);
            using (var cb = new SolidBrush(color)) g.DrawString(pct, pctFont, cb, x + S(40) - ps.Width, cy - ps.Height / 2);
            x += S(46);
            // bar
            var bw = S(64);
            var bh = S(5);
            using (var track = new SolidBrush(_t.Track)) FillRound(g, track, x, cy - bh / 2, bw, bh);
            if (l is { Percent: > 0 }) using (var cb = new SolidBrush(color)) FillRound(g, cb, x, cy - bh / 2, Math.Max(bh, bw * l.Percent / 100f), bh);
            x += bw + S(7);
            // reset time (or age of the values when they are stale)
            var reset = stale && i == 1 ? ShortAge(TimeText.Age(_snap.FetchedAt, now)) : "↺ " + TimeText.ResetShort(l?.ResetsAt, now);
            var rsz = g.MeasureString(reset, label);
            g.DrawString(reset, label, muted, x, cy - rsz.Height / 2);
        }
    }

    /// <summary>"12 min ago" / « il y a 12 min » → "−12 min".</summary>
    static string ShortAge(string age) =>
        age.StartsWith("il y a ") ? "−" + age[7..] : age.EndsWith(" ago") ? "−" + age[..^4] : age;

    void PaintCompact(Graphics g, QuotaLimit?[] limits, bool stale)
    {
        using var pctFont = new Font("Segoe UI Semibold", 8f);
        var size = S(36);
        var gap = S(8);
        var x = (Width - 2 * size - gap) / 2;
        var y = (Height - size) / 2;
        for (var i = 0; i < 2; i++)
        {
            var l = limits[i];
            var color = ColorFor(l, stale);
            var r = new RectangleF(x + i * (size + gap), y, size, size);
            DrawRing(g, r, l?.Percent, color, S(3.5f));
            var pct = l?.Percent.ToString() ?? "–";
            var ps = g.MeasureString(pct, pctFont);
            using var tb = new SolidBrush(stale ? _t.Muted : _t.Text);
            g.DrawString(pct, pctFont, tb, r.X + (size - ps.Width) / 2, r.Y + (size - ps.Height) / 2);
        }
    }

    void DrawRing(Graphics g, RectangleF r, int? percent, Color color, float w)
    {
        r.Inflate(-w / 2, -w / 2);
        using (var track = new Pen(_t.Track, w)) g.DrawEllipse(track, r);
        if (percent is > 0)
        {
            using var pen = new Pen(color, w) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(pen, r, -90, Math.Max(4f, 360f * percent.Value / 100));
        }
    }

    static void FillRound(Graphics g, Brush b, float x, float y, float w, float h)
    {
        using var p = new GraphicsPath();
        p.AddArc(x, y, h, h, 90, 180);
        p.AddArc(x + w - h, y, h, h, 270, 180);
        p.CloseFigure();
        g.FillPath(b, p);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _watch.Dispose(); _tip.Dispose(); }
        base.Dispose(disposing);
    }
}
