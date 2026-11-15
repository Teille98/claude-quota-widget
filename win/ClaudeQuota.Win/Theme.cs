using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using ClaudeQuota.Core;
using Microsoft.Win32;

namespace ClaudeQuota.Win;

/// <summary>Colors from the v1 mockups.</summary>
public sealed record Theme(bool Dark, Color Surface, Color Border, Color Text, Color Muted, Color Track,
                           Color Normal, Color Warning, Color Critical, Color Accent)
{
    public static readonly Theme DarkTheme = new(true,
        Hex("#2b2b30"), Hex("#3c3c44"), Hex("#f2f2f5"), Hex("#a8a8b3"), Hex("#43434c"),
        Hex("#9d90ff"), Hex("#f2b33d"), Hex("#ff7a52"), Hex("#7c6cf0"));

    public static readonly Theme LightTheme = new(false,
        Hex("#f9f9fb"), Hex("#d9d9e0"), Hex("#1b1b1f"), Hex("#575763"), Hex("#e2e2e8"),
        Hex("#5b48e0"), Hex("#9a5c00"), Hex("#c4401f"), Hex("#6a58e8"));

    public Color For(Level l) => l switch { Level.Critical => Critical, Level.Warning => Warning, _ => Normal };

    /// <summary>Windows app theme (panel) or taskbar theme (icon).</summary>
    public static Theme Current(bool taskbar = false)
    {
        var name = taskbar ? "SystemUsesLightTheme" : "AppsUseLightTheme";
        var v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", name, 0);
        return v is int i && i == 1 ? LightTheme : DarkTheme;
    }

    static Color Hex(string h) => ColorTranslator.FromHtml(h);
}

public static class TrayIconRenderer
{
    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);

    /// <summary>Ring filled to <paramref name="percent"/>%, grey when data is stale, amber dot when action is required.</summary>
    public static Icon Ring(int? percent, bool stale, bool attention)
    {
        var size = SystemInformation.SmallIconSize.Width;
        var t = Theme.Current(taskbar: true);
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var w = Math.Max(2f, size * 0.17f);
            var r = new RectangleF(w / 2 + 0.5f, w / 2 + 0.5f, size - w - 1, size - w - 1);
            using (var track = new Pen(t.Dark ? Color.FromArgb(110, 110, 120) : Color.FromArgb(170, 170, 180), w))
                g.DrawEllipse(track, r);
            if (percent is > 0)
            {
                var color = stale ? (t.Dark ? Color.FromArgb(200, 200, 208) : Color.FromArgb(90, 90, 100)) : t.For(Thresholds.For(percent.Value));
                using var pen = new Pen(color, w) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(pen, r, -90, Math.Max(4f, 360f * percent.Value / 100));
            }
            if (attention)
            {
                var d = size * 0.42f;
                using var b = new SolidBrush(t.Warning);
                g.FillEllipse(b, size - d, size - d, d, d);
            }
        }
        var h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { DestroyIcon(h); }
    }
}
