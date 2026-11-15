using System.Reflection;
using ClaudeQuota.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ClaudeQuota.Win;

/// <summary>
/// Reads the quota from claude.ai inside a WebView2.
/// The session lives in the WebView2 profile (<see cref="WebProfileDir"/>): our code never reads or copies any cookie.
/// </summary>
public static class ClaudeAiUsageSource
{
    public static readonly string AppDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeQuotaWidget");
    public static readonly string WebProfileDir = Path.Combine(AppDir, "WebView2");

    /// <summary>Lightweight page on the right origin: no need to load the whole claude.ai app.</summary>
    const string HostUrl = "https://claude.ai/api/organizations";
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    static readonly Lazy<string> Script = new(() =>
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("usage-script.js")
            ?? throw new InvalidOperationException("usage-script.js missing");
        return new StreamReader(s).ReadToEnd();
    });

    internal static string UsageScript => Script.Value;

    internal static Task<CoreWebView2Environment> CreateEnvironmentAsync() =>
        CoreWebView2Environment.CreateAsync(null, WebProfileDir);

    /// <summary>One reading: creates an invisible WebView2, reads, disposes it (~1.3 s).</summary>
    public static async Task<FetchResult> FetchAsync()
    {
        using var host = new Form
        {
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            Size = new Size(400, 300),
            Opacity = 0,
        };
        using var web = new WebView2 { Dock = DockStyle.Fill };
        host.Controls.Add(web);
        host.Show();

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await web.EnsureCoreWebView2Async(await CreateEnvironmentAsync());
            var core = web.CoreWebView2;
            core.WebMessageReceived += (_, e) => tcs.TrySetResult(e.TryGetWebMessageAsString());
            core.NavigationCompleted += async (_, e) =>
            {
                // HttpStatusCode 0 = no response from the server (network down, DNS…).
                if (!e.IsSuccess && e.HttpStatusCode == 0)
                {
                    tcs.TrySetResult($$"""{"ok":false,"reason":"offline","message":"{{e.WebErrorStatus}}"}""");
                    return;
                }
                // Otherwise (even 401/403) we are on the claude.ai origin: the script does the diagnosis.
                try { await core.ExecuteScriptAsync(UsageScript); }
                catch (Exception ex) { tcs.TrySetResult(ErrorJson(ex)); }
            };
            core.Navigate(HostUrl);

            var done = await Task.WhenAny(tcs.Task, Task.Delay(Timeout));
            var json = done == tcs.Task ? tcs.Task.Result : """{"ok":false,"reason":"timeout"}""";
            return UsageParser.Parse(json, DateTimeOffset.Now);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            return FetchResult.Fail(FetchError.Unknown, L10n.T("WebView2 runtime not found", "Runtime WebView2 introuvable"));
        }
        catch (Exception ex)
        {
            return FetchResult.Fail(FetchError.Unknown, ex.Message);
        }
        finally
        {
            host.Close();
        }
    }

    static string ErrorJson(Exception ex) =>
        System.Text.Json.JsonSerializer.Serialize(new { ok = false, reason = "error", message = ex.Message });
}

/// <summary>Visible claude.ai sign-in window. Closes itself (OK) as soon as a quota reading succeeds.</summary>
public sealed class LoginForm : Form
{
    readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    readonly Label _hint = new()
    {
        Dock = DockStyle.Top, Height = 36, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 0, 0),
        Text = L10n.T("Sign in to claude.ai as usual. This window will close by itself.",
                      "Connectez-vous à claude.ai comme d'habitude. Cette fenêtre se fermera toute seule."),
    };
    bool _checking;

    public FetchResult? Result { get; private set; }

    public LoginForm()
    {
        Text = L10n.T("Claude Quota — sign in to claude.ai", "Claude Quota — connexion à claude.ai");
        ClientSize = new Size(1000, 760);
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(_web);
        Controls.Add(_hint);
        Load += async (_, _) => await InitAsync();
    }

    async Task InitAsync()
    {
        try
        {
            await _web.EnsureCoreWebView2Async(await ClaudeAiUsageSource.CreateEnvironmentAsync());
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(this, L10n.T("The Microsoft Edge WebView2 runtime was not found.", "Le runtime Microsoft Edge WebView2 est introuvable."), Text);
            Close();
            return;
        }
        var core = _web.CoreWebView2;
        core.WebMessageReceived += (_, e) =>
        {
            _checking = false;
            var r = UsageParser.Parse(e.TryGetWebMessageAsString(), DateTimeOffset.Now);
            if (r.Ok)
            {
                Result = r;
                DialogResult = DialogResult.OK;
                Close();
            }
        };
        // claude.ai is a SPA: after sign-in the URL changes without a real navigation (NavigationCompleted
        // does not fire). So we also check periodically while the window is open.
        core.HistoryChanged += async (_, _) => await CheckAsync();
        core.NavigationCompleted += async (_, _) => await CheckAsync();
        _poll.Tick += async (_, _) => await CheckAsync();
        _poll.Start();
        core.Navigate("https://claude.ai/login");
    }

    readonly System.Windows.Forms.Timer _poll = new() { Interval = 3000 };

    async Task CheckAsync()
    {
        var core = _web.CoreWebView2;
        if (core is null || _checking || IsDisposed) return;
        var url = core.Source;
        if (!url.StartsWith("https://claude.ai/", StringComparison.Ordinal) || url.Contains("/login")) return;
        _checking = true;
        try { await core.ExecuteScriptAsync(ClaudeAiUsageSource.UsageScript); }
        catch { _checking = false; }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _poll.Dispose();
        base.OnFormClosed(e);
    }
}
