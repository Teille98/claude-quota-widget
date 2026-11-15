namespace ClaudeQuota.Win;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Single instance per Windows session.
        using var mutex = new Mutex(true, @"Local\ClaudeQuotaWidget", out var first);
        if (!first) return;

        ApplicationConfiguration.Initialize();
        Directory.CreateDirectory(ClaudeAiUsageSource.AppDir);
        Application.Run(new TrayApp());
    }
}
