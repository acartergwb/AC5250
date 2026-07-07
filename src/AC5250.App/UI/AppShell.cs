using AC5250.Hosting;
using AC5250.Session;

namespace AC5250.UI;

/// <summary>
/// App-level context that owns everything shared across windows: the single
/// <see cref="SessionManager"/>, the one MCP host (so an MCP client drives every session
/// regardless of which window shows it), app settings, and the list of open windows. The
/// application lives until the LAST window closes (not the first), which is why the entry
/// point runs this instead of a single form.
///
/// Session lifecycle is centralised here: when a session is added, its tab is placed in the
/// window that initiated it (or the active window for MCP-initiated sessions), and its
/// events are routed to whichever window currently owns its tab — looked up at event time,
/// so moving a tab between windows needs no event rewiring.
/// </summary>
internal sealed class AppShell : ApplicationContext
{
    private const int McpPort = 8250;

    private readonly SessionManager _sessions = new();
    private readonly AppSettings _settings = AppSettingsStore.Load();
    private readonly McpStartupOptions? _mcpStartup;
    private readonly Control _anchor;                 // stable UI-thread handle for marshaling
    private readonly List<MainForm> _windows = new();
    private readonly object _logLock = new();

    private McpHost? _mcpHost;
    private MainForm? _pendingWindow;                 // window awaiting the next CreateSession

    public SessionManager Sessions => _sessions;
    public AppSettings Settings => _settings;
    public McpStartupOptions? McpStartup => _mcpStartup;
    public SynchronizationContext? UiContext { get; private set; }
    public int EffectivePort => _mcpStartup?.Port ?? McpPort;
    public bool McpRunning => _mcpHost != null;
    public McpHost? McpHost => _mcpHost;
    public MainForm? ActiveWindow { get; private set; }

    public AppShell(McpStartupOptions? mcpStartup)
    {
        _mcpStartup = mcpStartup;

        // A never-shown control whose handle anchors the UI thread for MCP marshaling. It
        // outlives individual windows, so the MCP host keeps working as windows open/close.
        _anchor = new Control();
        _ = _anchor.Handle;                            // force handle creation on this thread
        UiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _sessions.SessionAdded += OnSessionAdded;
        _sessions.SessionRemoved += OnSessionRemoved;

        OpenWindow();                                  // first window

        if (_mcpStartup?.AutoStart == true || _settings.StartMcpOnStartup)
            _ = StartMcpAsync();
    }

    // ---- windows -----------------------------------------------------------------------

    /// <summary>Open a new empty window (starts on a Home tab).</summary>
    public MainForm OpenWindow()
    {
        var w = new MainForm(this);
        w.Activated += (_, _) => ActiveWindow = w;
        w.FormClosed += (_, _) => OnWindowClosed(w);
        _windows.Add(w);
        ActiveWindow = w;
        w.Show();
        w.OpenHomeTab();
        return w;
    }

    /// <summary>Open a new window and move an existing tab (a dragged-out session) into it.</summary>
    public MainForm OpenWindowWithTab(MainForm.TabContext tab, Point screenLocation)
    {
        var w = new MainForm(this);
        w.Activated += (_, _) => ActiveWindow = w;
        w.FormClosed += (_, _) => OnWindowClosed(w);
        _windows.Add(w);
        ActiveWindow = w;
        w.StartPosition = FormStartPosition.Manual;
        w.Location = screenLocation;
        w.Show();
        w.AdoptTab(tab);       // reparent the content + add the tab; no Home tab needed
        return w;
    }

    private void OnWindowClosed(MainForm w)
    {
        _windows.Remove(w);
        if (ReferenceEquals(ActiveWindow, w))
            ActiveWindow = _windows.Count > 0 ? _windows[^1] : null;

        if (_windows.Count == 0)
        {
            // Last window closed: best-effort MCP teardown off-thread (see McpHost — a
            // connected client's session would otherwise block), close sockets, and exit.
            var host = _mcpHost;
            _mcpHost = null;
            if (host != null)
                _ = Task.Run(async () => { try { await host.DisposeAsync(); } catch { /* exiting */ } });
            _sessions.CloseAll();
            ExitThread();
        }
    }

    /// <summary>The window whose tabs currently show <paramref name="session"/>, or null.</summary>
    public MainForm? WindowForSession(TerminalSession session)
    {
        foreach (var w in _windows)
            if (w.OwnsSession(session)) return w;
        return null;
    }

    // ---- session routing ---------------------------------------------------------------

    /// <summary>A window calls this just before creating a session so its tab lands there.</summary>
    public void SetPendingWindow(MainForm window) => _pendingWindow = window;

    private void OnSessionAdded(TerminalSession session)
    {
        var target = _pendingWindow ?? ActiveWindow ?? (_windows.Count > 0 ? _windows[0] : null);
        _pendingWindow = null;
        target?.PlaceSessionTab(session);

        // Route per-session events to whichever window owns the tab at the time (found via
        // WindowForSession), marshaled onto the UI thread through the anchor.
        session.ConnectionClosed += reason => Post(() => WindowForSession(session)?.HandleDisconnected(session, reason));
        session.StatusMessage += msg => Post(() => WindowForSession(session)?.HandleStatus(session, msg));
        if (!string.IsNullOrEmpty(_mcpStartup?.LogFile))
            session.DebugLogged += AppendLog;
    }

    private void OnSessionRemoved(TerminalSession session) =>
        Post(() => WindowForSession(session)?.HandleRemoved(session));

    /// <summary>Run an action on the UI thread via the anchor (safe during window churn).</summary>
    private void Post(Action action)
    {
        if (_anchor.IsDisposed) return;
        if (_anchor.InvokeRequired) { try { _anchor.BeginInvoke(action); } catch { /* shutting down */ } }
        else action();
    }

    // ---- MCP ---------------------------------------------------------------------------

    public async Task<bool> StartMcpAsync()
    {
        if (_mcpHost != null) return true;
        try
        {
            var host = new McpHost(_sessions, _anchor, UiContext!, EffectivePort);
            await host.StartAsync();
            _mcpHost = host;
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not start the MCP server on 127.0.0.1:{EffectivePort}.\n\n{ex.Message}",
                "AC5250", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    public async Task StopMcpAsync()
    {
        var host = _mcpHost;
        _mcpHost = null;
        if (host != null) await host.DisposeAsync();
    }

    // App-level trace log (one file across all windows/sessions). SEND records are skipped
    // so operator keystrokes (including passwords) are never written.
    private void AppendLog(string line)
    {
        var path = _mcpStartup?.LogFile;
        if (string.IsNullOrEmpty(path) || line.Contains("SEND record")) return;
        try { lock (_logLock) System.IO.File.AppendAllText(path, line + Environment.NewLine); }
        catch { /* diagnostics only */ }
    }
}
