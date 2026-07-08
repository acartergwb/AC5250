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

        // Init WinForms before the update so the progress splash renders themed + DPI-correct.
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        // Update on launch from GitHub Releases, showing a progress splash so the user isn't
        // staring at a blank screen while it checks + downloads. Best-effort: an update problem
        // (offline, transient) must never stop the app from starting.
        try { UpdateOnLaunch(); }
        catch { /* start anyway */ }

        // Run an app context (not a single form) so the app lives until the LAST window
        // closes. The shell owns the shared SessionManager + MCP host across all windows.
        Application.Run(new AppShell(McpStartupOptions.Parse(args)));
    }

    private static void UpdateOnLaunch()
    {
        var mgr = new UpdateManager(new GithubSource("https://github.com/acartergwb/AC5250", null, false));

        // Only when running from an installed copy — the raw exe has nothing to update.
        if (!mgr.IsInstalled) return;

        // The splash runs the whole check/download/apply flow on its own message loop, showing a
        // real progress bar. It closes (and Run returns) when there's no update or on error; when
        // an update is applied it relaunches the process, so Run never returns here.
        using var splash = new UpdateSplashForm(mgr);
        Application.Run(splash);
    }
}
