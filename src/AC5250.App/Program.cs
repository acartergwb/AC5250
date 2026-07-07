using AC5250.Hosting;
using AC5250.UI;
using Velopack;
using Velopack.Sources;

namespace AC5250;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Velopack MUST run first in Main: it handles the install/update/uninstall hooks
        // for the pinnable stub and versioned layout. It's a no-op when the app isn't
        // launched from a Velopack install (e.g. the single-file debug/test build).
        VelopackApp.Build().Run();

        // Update on launch from GitHub Releases. Best-effort: an update problem (offline,
        // transient) must never stop the app from starting.
        try { UpdateOnLaunch(); }
        catch { /* start anyway */ }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        // Run an app context (not a single form) so the app lives until the LAST window
        // closes. The shell owns the shared SessionManager + MCP host across all windows.
        Application.Run(new AppShell(McpStartupOptions.Parse(args)));
    }

    private static void UpdateOnLaunch()
    {
        var mgr = new UpdateManager(new GithubSource("https://github.com/acartergwb/AC5250", null, false));

        // Only when running from an installed copy — the raw exe has nothing to update.
        if (!mgr.IsInstalled) return;

        var updates = mgr.CheckForUpdatesAsync().GetAwaiter().GetResult();
        if (updates == null) return; // already on the latest release

        mgr.DownloadUpdatesAsync(updates).GetAwaiter().GetResult();
        mgr.ApplyUpdatesAndRestart(updates); // swaps to the new version and relaunches
    }
}
