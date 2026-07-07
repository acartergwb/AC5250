using System.Runtime.InteropServices;
using AC5250.Hosting;
using AC5250.Input;
using AC5250.Rendering;
using AC5250.Session;

namespace AC5250.UI;

internal class MainForm : Form
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_BORDER_COLOR = 34;

    private readonly AppShell _shell;
    private readonly SessionManager _sessionManager;
    private readonly SessionTabBar _tabBar;
    private readonly Panel _terminalPanel;
    private readonly MenuStrip _menu;

    // Each tab hosts either a Home page (WelcomePanel, no session) or a live session
    // (a TerminalControl bound to a TerminalSession). A tab is no longer forced to be a session.
    private readonly List<TabContext> _tabs = new();
    private TabContext? _activeTab;
    private TabContext? _pendingTab;   // tab awaiting the next CreateSession (a UI-initiated connect)

    // Menu items whose enabled state is recomputed when their menu opens.
    private ToolStripMenuItem? _connectItem;
    private ToolStripMenuItem? _disconnectItem;
    private ToolStripMenuItem? _startMcpItem;
    private ToolStripMenuItem? _stopMcpItem;

    // The MCP host, SessionManager, and settings live in the shared AppShell so an MCP client
    // drives sessions across every window. This form is one view onto that shared state.
    private readonly McpStartupOptions? _mcpStartup;

    private bool _uppercaseInput = true;
    private ToolStripMenuItem? _uppercaseMenuItem;

    private readonly AppSettings _settings;
    private ToolStripMenuItem? _mcpStartupMenuItem;

    public MainForm(AppShell shell)
    {
        _shell = shell;
        _sessionManager = shell.Sessions;
        _settings = shell.Settings;
        _mcpStartup = shell.McpStartup;

        Text = "AC5250";
        if (LoadAppIcon() is { } appIcon) Icon = appIcon;
        Size = new Size(960, 700);
        MinimumSize = new Size(640, 480);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = DarkTheme.Background;
        ForeColor = DarkTheme.TextPrimary;
        Font = DarkTheme.UIFont;

        ApplyDarkTitleBar();

        // Menu
        _menu = CreateMenu();
        MainMenuStrip = _menu;

        // Tab bar
        _tabBar = new SessionTabBar();
        _tabBar.SelectedIndexChanged += OnTabChanged;
        _tabBar.TabCloseClicked += OnTabClose;
        _tabBar.NewTabClicked += (_, _) => OpenHomeTab();
        _tabBar.TabDetached += OnTabDetached;

        // Terminal panel
        _terminalPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = DarkTheme.Background,
            Padding = new Padding(2, 0, 2, 2),
        };

        // Add in correct order (Fill must be added first)
        Controls.Add(_terminalPanel);
        Controls.Add(_tabBar);
        Controls.Add(_menu);

        // SessionManager events are handled centrally by the AppShell, which routes each
        // session to the owning window (this form exposes PlaceSessionTab/Handle* for it).

        KeyPreview = true;
    }

    /// <summary>Open a new Home tab (the connection chooser). Used by "+", Session ▸ New
    /// Session, and whenever the window would otherwise have no tabs.</summary>
    internal void OpenHomeTab()
    {
        var welcome = new WelcomePanel { Dock = DockStyle.Fill, Visible = false };
        var ctx = new TabContext { Content = welcome };
        welcome.ConnectClicked += (_, _) => StartConnectFlow(ctx);           // "+ New connection" / Connect
        welcome.LaunchProfile += (_, settings) => StartSessionInTab(ctx, settings); // quick-launch a saved profile
        _terminalPanel.Controls.Add(welcome);
        _tabs.Add(ctx);
        _tabBar.AddTab("Home", ctx);   // selects the tab -> OnTabChanged -> ActivateTab
    }

    /// <summary>Prompt for connection details, then bring the connection up in the given tab.</summary>
    private void StartConnectFlow(TabContext ctx)
    {
        using var dialog = new ConnectDialog();
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        StartSessionInTab(ctx, dialog.Settings);
    }

    private MenuStrip CreateMenu()
    {
        var menu = new MenuStrip
        {
            BackColor = DarkTheme.Surface,
            ForeColor = DarkTheme.TextPrimary,
            Renderer = new DarkMenuRenderer(),
            Padding = new Padding(6, 2, 0, 2),
        };

        var fileMenu = CreateMenuItem("&File");
        _connectItem = CreateMenuItem("&Connect...", Keys.Control | Keys.N, OnConnect);
        _disconnectItem = CreateMenuItem("&Disconnect", onClick: OnDisconnect);
        fileMenu.DropDownItems.Add(_connectItem);
        fileMenu.DropDownItems.Add(_disconnectItem);
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add(CreateMenuItem("&Manage Saved Connections...", onClick: OnManageConnections));
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add(CreateMenuItem("E&xit", Keys.Alt | Keys.F4, (_, _) => Close()));
        fileMenu.DropDownOpening += (_, _) => UpdateFileMenuState();
        menu.Items.Add(fileMenu);

        var sessionMenu = CreateMenuItem("&Session");
        sessionMenu.DropDownItems.Add(CreateMenuItem("&New Session", Keys.Control | Keys.T, (_, _) => OpenHomeTab()));
        sessionMenu.DropDownItems.Add(CreateMenuItem("New &Window", Keys.Control | Keys.N, (_, _) => _shell.OpenWindow()));
        sessionMenu.DropDownItems.Add(CreateMenuItem("&Close Session", Keys.Control | Keys.W, OnCloseSession));
        sessionMenu.DropDownItems.Add(new ToolStripSeparator());
        sessionMenu.DropDownItems.Add(CreateMenuItem("Sign &On (saved credentials)", onClick: OnSignOn));
        sessionMenu.DropDownItems.Add(CreateMenuItem("&Manage Saved Credentials...", onClick: OnManageCredentials));
        sessionMenu.DropDownItems.Add(new ToolStripSeparator());
        sessionMenu.DropDownItems.Add(CreateMenuItem("&Debug Log...", Keys.Control | Keys.Shift | Keys.D, OnShowDebugLog));
        menu.Items.Add(sessionMenu);

        var viewMenu = CreateMenuItem("&View");
        var colorMenu = CreateMenuItem("Color &Scheme");
        colorMenu.DropDownItems.Add(CreateMenuItem("&Color (ACS)", onClick: (_, _) => SetColorScheme(ColorScheme.Color5250)));
        colorMenu.DropDownItems.Add(CreateMenuItem("Classic &Green", onClick: (_, _) => SetColorScheme(ColorScheme.Classic)));
        colorMenu.DropDownItems.Add(CreateMenuItem("&Amber", onClick: (_, _) => SetColorScheme(ColorScheme.Amber)));
        colorMenu.DropDownItems.Add(CreateMenuItem("&White on Black", onClick: (_, _) => SetColorScheme(ColorScheme.WhiteOnBlack)));
        viewMenu.DropDownItems.Add(colorMenu);
        viewMenu.DropDownItems.Add(new ToolStripSeparator());
        _uppercaseMenuItem = CreateMenuItem("&Uppercase Input", onClick: OnToggleUppercase);
        _uppercaseMenuItem.Checked = _uppercaseInput;
        viewMenu.DropDownItems.Add(_uppercaseMenuItem);
        menu.Items.Add(viewMenu);

        var toolsMenu = CreateMenuItem("&Tools");
        _startMcpItem = CreateMenuItem("&Start MCP Server", Keys.Control | Keys.M, OnStartMcp);
        _stopMcpItem = CreateMenuItem("S&top MCP Server", onClick: OnStopMcp);
        toolsMenu.DropDownItems.Add(_startMcpItem);
        toolsMenu.DropDownItems.Add(_stopMcpItem);
        toolsMenu.DropDownItems.Add(new ToolStripSeparator());
        toolsMenu.DropDownItems.Add(CreateMenuItem("MCP Connection &Info...", onClick: OnMcpInfo));
        toolsMenu.DropDownItems.Add(new ToolStripSeparator());
        _mcpStartupMenuItem = CreateMenuItem("Start MCP on &Startup", onClick: OnToggleMcpStartup);
        _mcpStartupMenuItem.Checked = _settings.StartMcpOnStartup;
        toolsMenu.DropDownItems.Add(_mcpStartupMenuItem);
        toolsMenu.DropDownOpening += (_, _) => UpdateToolsMenuState();
        menu.Items.Add(toolsMenu);

        var helpMenu = CreateMenuItem("&Help");
        helpMenu.DropDownItems.Add(CreateMenuItem("&Key Mappings", Keys.F1, OnKeyMappings));
        helpMenu.DropDownItems.Add(new ToolStripSeparator());
        helpMenu.DropDownItems.Add(CreateMenuItem("&About AC5250", onClick: OnAbout));
        menu.Items.Add(helpMenu);

        return menu;
    }

    private static ToolStripMenuItem CreateMenuItem(string text, Keys shortcut = Keys.None, EventHandler? onClick = null)
    {
        var item = new ToolStripMenuItem(text)
        {
            ForeColor = DarkTheme.TextPrimary,
        };
        if (shortcut != Keys.None) item.ShortcutKeys = shortcut;
        if (onClick != null) item.Click += onClick;
        return item;
    }

    // File ▸ Connect acts on the active tab: a Home tab prompts for connection details;
    // a disconnected session tab reconnects using its settings. Disabled while connected.
    private void OnConnect(object? sender, EventArgs e)
    {
        var ctx = _activeTab;
        if (ctx == null) { OpenHomeTab(); return; }
        if (ctx.Session is { IsConnected: true }) return;                        // nothing to connect
        if (ctx.Session != null) StartSessionInTab(ctx, ctx.Session.Settings);   // reconnect same settings
        else StartConnectFlow(ctx);                                              // Home tab -> dialog
    }

    /// <summary>Bring a connection up inside the given tab, replacing its current content
    /// (a Home page, or a previous disconnected session).</summary>
    private async void StartSessionInTab(TabContext ctx, ConnectionSettings settings)
    {
        // If the tab already holds a (disconnected) session, tear it down first — but null the
        // ref so OnSessionRemoved doesn't delete the tab; we're reusing it.
        if (ctx.Session is { } dead)
        {
            ctx.Session = null;
            _sessionManager.CloseSession(dead);
        }

        _pendingTab = ctx;                 // PlaceSessionTab puts the new terminal into this tab
        _shell.SetPendingWindow(this);     // and this window owns the session
        var session = _sessionManager.CreateSession(settings, _shell.UiContext);
        session.UppercaseInput = _uppercaseInput;

        try { await session.ConnectAsync(); }
        catch { /* ConnectionClosed already fired → the tab shows [Closed] + a toast */ }
    }

    // File ▸ Disconnect drops the socket but KEEPS the tab (as [Closed]) so it can be
    // reconnected. Full teardown (removing the tab) is Close Session / the tab's ✕.
    private void OnDisconnect(object? sender, EventArgs e)
    {
        if (_activeTab?.Session is { IsConnected: true } s)
            s.Disconnect();
    }

    private void OnCloseSession(object? sender, EventArgs e)
    {
        if (_activeTab is { } ctx) CloseTab(ctx);
    }

    // The AppShell opens the initial tab after Show() (a Home tab for a fresh window, or an
    // adopted tab for a dragged-out session) and owns MCP auto-start, so OnLoad is a no-op.

    private async void OnStartMcp(object? sender, EventArgs e)
    {
        if (_shell.McpRunning) { OnMcpInfo(sender, e); return; }

        if (await _shell.StartMcpAsync() && _shell.McpHost is { } host)
        {
            using var dlg = new McpInfoDialog(host);
            dlg.ShowDialog(this);
        }
    }

    private async void OnStopMcp(object? sender, EventArgs e)
    {
        if (!_shell.McpRunning)
        {
            MessageBox.Show("MCP server is not running.", "AC5250",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        await _shell.StopMcpAsync();
        MessageBox.Show("MCP server stopped.", "AC5250",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void OnToggleUppercase(object? sender, EventArgs e)
    {
        _uppercaseInput = !_uppercaseInput;
        if (_uppercaseMenuItem != null) _uppercaseMenuItem.Checked = _uppercaseInput;
        foreach (var s in _sessionManager.Sessions)
            s.UppercaseInput = _uppercaseInput;
    }

    private void OnManageCredentials(object? sender, EventArgs e)
    {
        using var dlg = new CredentialsDialog();
        dlg.ShowDialog(this);
    }

    // Manual counterpart to the MCP `signon` tool: fill the sign-on screen from the
    // saved credentials for the active session's host and submit. The password is read
    // from Windows Credential Manager and only ever written into the (hidden) field.
    private void OnSignOn(object? sender, EventArgs e)
    {
        var s = _sessionManager.ActiveSession;
        if (s == null)
        {
            MessageBox.Show("No active session.", "AC5250", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        string host = s.Settings.HostName;
        var labels = AC5250.Security.CredentialStore.Labels(host);
        if (labels.Count == 0)
        {
            MessageBox.Show(
                $"No saved credentials for host '{host}'.\nAdd them via Session > Manage Saved Credentials.",
                "AC5250", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // With more than one login for the host, ask which; otherwise use the default/only one.
        string? label = null;
        if (labels.Count > 1)
        {
            label = CredentialPicker.Choose(this, host, labels);
            if (label == null) return; // cancelled
        }
        var creds = AC5250.Security.CredentialStore.Get(host, label);
        if (creds is null)
        {
            MessageBox.Show($"Could not read the saved credential for '{host}'.",
                "AC5250", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var fields = s.Screen.Fields;
        int userIdx = -1, pwIdx = -1;
        for (int i = 0; i < fields.Count; i++)
        {
            var a = fields[i].Attribute;
            if (a.IsBypass) continue;
            if (a.IsNonDisplay) { if (pwIdx < 0) pwIdx = i; }
            else if (userIdx < 0) userIdx = i;
        }
        if (userIdx < 0 || pwIdx < 0)
        {
            MessageBox.Show("This screen isn't a sign-on prompt (no user + hidden password field found).",
                "AC5250", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!s.SetFieldValue(userIdx, creds.Value.User, true) || !s.SetFieldValue(pwIdx, creds.Value.Password, true))
        {
            MessageBox.Show("Could not fill the sign-on fields (keyboard inhibited?).",
                "AC5250", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (AC5250.Mcp.KeyNames.TryParse("Enter", out var enter))
            s.HandleKeyAction(enter);
    }

    private void OnToggleMcpStartup(object? sender, EventArgs e)
    {
        _settings.StartMcpOnStartup = !_settings.StartMcpOnStartup;
        if (_mcpStartupMenuItem != null) _mcpStartupMenuItem.Checked = _settings.StartMcpOnStartup;
        AppSettingsStore.Save(_settings);

        // Convenience: enabling it starts the server now if it isn't already running.
        if (_settings.StartMcpOnStartup && !_shell.McpRunning)
            _ = _shell.StartMcpAsync();
    }

    private void OnMcpInfo(object? sender, EventArgs e)
    {
        if (_shell.McpHost is not { } host)
        {
            MessageBox.Show("MCP server is not running. Use Tools → Start MCP Server.",
                "AC5250", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dlg = new McpInfoDialog(host);
        dlg.ShowDialog(this);
    }

    private void OnShowDebugLog(object? sender, EventArgs e)
    {
        var session = _sessionManager.ActiveSession;
        if (session == null)
        {
            MessageBox.Show("No active session.", "AC5250", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var log = session.GetDebugLog();
        var text = log.Count == 0 ? "(no data received yet)" : string.Join(Environment.NewLine, log);

        using var dlg = new Form
        {
            Text = "Debug Log — connection & negotiation trace",
            Size = new Size(820, 520),
            StartPosition = FormStartPosition.CenterParent,
            BackColor = DarkTheme.Surface,
            ForeColor = DarkTheme.TextPrimary,
        };
        dlg.HandleCreated += (_, _) => ApplyDarkTitleBar(dlg);

        var tb = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9f),
            BackColor = DarkTheme.Background,
            ForeColor = DarkTheme.TextPrimary,
            WordWrap = false,
            Text = text,
        };
        dlg.Controls.Add(tb);
        dlg.ShowDialog(this);
    }

    /// <summary>Build a terminal control + tab for a newly-created session in this window.
    /// Called by the AppShell, which routes each new session to its owning window.</summary>
    internal void PlaceSessionTab(TerminalSession session)
    {
        var control = BuildTerminalControl(session);

        TabContext ctx;
        if (_pendingTab is { } pending)
        {
            // UI-initiated: reuse the initiating tab, swapping its Home/old content for the terminal.
            _pendingTab = null;
            ctx = pending;
            SwapTabContent(ctx, control);
            ctx.Session = session;
            int idx = _tabBar.FindTabByTag(ctx);
            if (idx >= 0) _tabBar.SetTabTitle(idx, session.Title);
        }
        else
        {
            // MCP-initiated (Claude called connect with no initiating tab): open a fresh session tab.
            ctx = new TabContext { Content = control, Session = session };
            _tabs.Add(ctx);
            _terminalPanel.Controls.Add(control);
            _tabBar.AddTab(session.Title, ctx);   // selects the tab -> OnTabChanged -> ActivateTab
        }

        // ConnectionClosed / StatusMessage / DebugLogged are subscribed once by the AppShell
        // and routed to whichever window owns the tab, so moving a tab needs no rewiring.
        ActivateTab(ctx);
    }

    private TerminalControl BuildTerminalControl(TerminalSession session)
    {
        var control = new TerminalControl { Dock = DockStyle.Fill, Visible = false };
        control.AttachBuffer(session.Screen);
        control.HostInfo = session.Settings.DisplayName;

        control.KeyInput += keys =>
        {
            var action = KeyMapper.Map(keys);
            if (action.Type != KeyActionType.None)
                session.HandleKeyAction(action);
        };

        control.KeyPress += (_, kpe) =>
        {
            var action = KeyMapper.MapChar(kpe.KeyChar);
            if (action.Type != KeyActionType.None)
                session.HandleKeyAction(action);
            kpe.Handled = true;
        };

        return control;
    }

    /// <summary>Replace a tab's content control (Home page or old terminal) with a new one.</summary>
    private void SwapTabContent(TabContext ctx, Control newContent)
    {
        if (ctx.Content is { } old)
        {
            _terminalPanel.Controls.Remove(old);
            (old as TerminalControl)?.DetachBuffer();
            old.Dispose();
        }
        ctx.Content = newContent;
        _terminalPanel.Controls.Add(newContent);
    }

    /// <summary>True when this window currently shows a tab for <paramref name="session"/>.
    /// The AppShell uses it to route a session's events to the window that owns its tab.</summary>
    internal bool OwnsSession(TerminalSession session) => FindContextBySession(session) != null;

    // Fires only for teardown we didn't start from the UI (e.g. the MCP `disconnect` tool).
    // UI-initiated closes null ctx.Session first, so this finds nothing and no-ops then.
    internal void HandleRemoved(TerminalSession session)
    {
        var ctx = FindContextBySession(session);
        if (ctx == null) return;
        ctx.Session = null;
        RemoveTab(ctx);
    }

    // Socket dropped (host, error, or user Disconnect): keep the tab as [Closed] and toast.
    internal void HandleDisconnected(TerminalSession session, string reason)
    {
        var ctx = FindContextBySession(session);
        if (ctx == null) return;
        if (ctx.Content is TerminalControl tc)
        {
            tc.HostInfo = $"Disconnected: {reason}";
            tc.Invalidate();
        }
        int idx = _tabBar.FindTabByTag(ctx);
        if (idx >= 0) _tabBar.SetTabTitle(idx, $"[Closed] {session.Title}");
        ShowDisconnectToast(session.Title, reason);
    }

    internal void HandleStatus(TerminalSession session, string msg)
    {
        if (FindContextBySession(session)?.Content is TerminalControl tc)
        {
            tc.HostInfo = msg;
            tc.Invalidate();
        }
    }

    private void OnTabChanged(object? sender, EventArgs e)
    {
        if (_tabBar.SelectedIndex >= 0 && _tabBar.GetTabTag(_tabBar.SelectedIndex) is TabContext ctx)
            ActivateTab(ctx);
    }

    private void OnTabClose(object? sender, int index)
    {
        if (_tabBar.GetTabTag(index) is TabContext ctx) CloseTab(ctx);
    }

    // A tab dragged out of the bar: move it (session + live control) into a fresh window at
    // the drop point. The session stays in the shared SessionManager, so MCP keeps driving it.
    private void OnTabDetached(int index, Point screenLocation)
    {
        if (_tabBar.GetTabTag(index) is TabContext ctx)
            _shell.OpenWindowWithTab(DetachTab(ctx), screenLocation);
    }

    /// <summary>Fully close a tab: tear down its session (if any) and remove the tab.</summary>
    private void CloseTab(TabContext ctx)
    {
        if (ctx.Session is { } s)
        {
            ctx.Session = null;                  // suppress OnSessionRemoved's own removal
            _sessionManager.CloseSession(s);
        }
        RemoveTab(ctx);
    }

    private void RemoveTab(TabContext ctx)
    {
        int idx = _tabBar.FindTabByTag(ctx);
        if (ctx.Content is { } c)
        {
            _terminalPanel.Controls.Remove(c);
            (c as TerminalControl)?.DetachBuffer();
            c.Dispose();
        }
        _tabs.Remove(ctx);
        if (idx >= 0) _tabBar.RemoveTabAt(idx);   // fires OnTabChanged for the new selection
        if (ReferenceEquals(_activeTab, ctx)) _activeTab = null;
        if (_tabs.Count == 0) OpenHomeTab();      // never leave the window tab-less
    }

    /// <summary>Remove a tab from THIS window without tearing down its session or content —
    /// used to hand it to another window. The caller owns the returned context afterwards.</summary>
    internal TabContext DetachTab(TabContext ctx)
    {
        int idx = _tabBar.FindTabByTag(ctx);
        _terminalPanel.Controls.Remove(ctx.Content);   // reparent out; keep control + buffer alive
        _tabs.Remove(ctx);
        if (idx >= 0) _tabBar.RemoveTabAt(idx);
        if (ReferenceEquals(_activeTab, ctx)) _activeTab = null;
        if (_tabs.Count == 0) OpenHomeTab();            // never leave the window tab-less
        return ctx;
    }

    /// <summary>Take a tab detached from another window into this one and select it.</summary>
    internal void AdoptTab(TabContext ctx)
    {
        _tabs.Add(ctx);
        _terminalPanel.Controls.Add(ctx.Content);
        string title = ctx.Session is { } s ? s.Title : "Home";
        _tabBar.AddTab(title, ctx);   // selects -> OnTabChanged -> ActivateTab
    }

    private void ActivateTab(TabContext ctx)
    {
        foreach (var t in _tabs) t.Content.Visible = false;
        ctx.Content.Visible = true;
        ctx.Content.BringToFront();
        ctx.Content.Focus();
        _activeTab = ctx;
        if (ctx.Session is { } s) _sessionManager.SetActive(s);
    }

    private TabContext? FindContextBySession(TerminalSession session)
    {
        foreach (var t in _tabs)
            if (ReferenceEquals(t.Session, session)) return t;
        return null;
    }

    private void UpdateFileMenuState()
    {
        bool connected = _activeTab?.Session is { IsConnected: true };
        if (_connectItem != null) _connectItem.Enabled = !connected;   // Home or disconnected → can connect
        if (_disconnectItem != null) _disconnectItem.Enabled = connected;
    }

    private void UpdateToolsMenuState()
    {
        if (_startMcpItem != null) _startMcpItem.Enabled = !_shell.McpRunning;
        if (_stopMcpItem != null) _stopMcpItem.Enabled = _shell.McpRunning;
    }

    private void OnManageConnections(object? sender, EventArgs e)
    {
        using var dlg = new ManageConnectionsDialog();
        dlg.ShowDialog(this);
    }

    private void ShowDisconnectToast(string title, string reason)
    {
        var toast = new ToastNotification(title, $"Disconnected — {reason}");
        Controls.Add(toast);
        toast.BringToFront();
        toast.ShowAt(this);
    }

    private void SetColorScheme(ColorScheme scheme)
    {
        foreach (var t in _tabs)
            if (t.Content is TerminalControl tc) tc.Colors = scheme;
    }

    private void OnAbout(object? sender, EventArgs e)
    {
        using var about = new AboutDialog();
        about.ShowDialog(this);
    }

    private void OnKeyMappings(object? sender, EventArgs e)
    {
        using var help = new KeyMappingsDialog();
        help.ShowDialog(this);
    }

    private void ApplyDarkTitleBar()
    {
        try
        {
            // Dark mode
            int darkMode = 1;
            DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

            // Caption color (COLORREF: 0x00BBGGRR)
            int captionColor = DarkTheme.Background.R | (DarkTheme.Background.G << 8) | (DarkTheme.Background.B << 16);
            DwmSetWindowAttribute(Handle, DWMWA_CAPTION_COLOR, ref captionColor, sizeof(int));

            // Border color
            int borderColor = DarkTheme.BorderSubtle.R | (DarkTheme.BorderSubtle.G << 8) | (DarkTheme.BorderSubtle.B << 16);
            DwmSetWindowAttribute(Handle, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));
        }
        catch
        {
            // DWM attributes not supported on older Windows versions — ignore
        }
    }

    internal static void ApplyDarkTitleBar(Form form)
    {
        try
        {
            int darkMode = 1;
            DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

            int captionColor = DarkTheme.Surface.R | (DarkTheme.Surface.G << 8) | (DarkTheme.Surface.B << 16);
            DwmSetWindowAttribute(form.Handle, DWMWA_CAPTION_COLOR, ref captionColor, sizeof(int));

            int borderColor = DarkTheme.BorderSubtle.R | (DarkTheme.BorderSubtle.G << 8) | (DarkTheme.BorderSubtle.B << 16);
            DwmSetWindowAttribute(form.Handle, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));
        }
        catch { }
    }

    // The window/taskbar icon, loaded from the embedded multi-size .ico (same art as the .exe
    // ApplicationIcon) so the title bar and taskbar show the AC5250 logo crisply at any DPI.
    // Best-effort: a missing/failed resource just leaves the default icon.
    private static Icon? LoadAppIcon()
    {
        try
        {
            using var s = typeof(MainForm).Assembly.GetManifestResourceStream("AC5250.ico");
            return s != null ? new Icon(s) : null;
        }
        catch { return null; }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        // Close only THIS window's sessions (a shared SessionManager may hold others shown in
        // other windows). Null ctx.Session first so HandleRemoved doesn't churn the closing
        // tab bar. The AppShell tears down the MCP host and remaining state when the LAST
        // window closes (see AppShell.OnWindowClosed).
        foreach (var ctx in _tabs.ToArray())
        {
            if (ctx.Session is { } s)
            {
                ctx.Session = null;
                _sessionManager.CloseSession(s);
            }
        }
    }

    /// <summary>What a single tab is showing. A tab holds a content control that is either a
    /// <see cref="WelcomePanel"/> (Home — no session yet) or a <see cref="TerminalControl"/>
    /// bound to <see cref="Session"/>. IsHome ⇔ Session is null.</summary>
    internal sealed class TabContext
    {
        public Control Content = null!;
        public TerminalSession? Session;
        public bool IsHome => Session == null;
    }
}

/// <summary>
/// Welcome screen shown when no sessions are active.
/// </summary>
internal class WelcomePanel : Control
{
    public event EventHandler? ConnectClicked;
    public event EventHandler<ConnectionSettings>? LaunchProfile;

    private readonly List<ConnectionSettings> _profiles;
    private readonly List<Rectangle> _profileRects = new();
    private Rectangle _actionRect;   // "Connect" button (no profiles) OR "+ New connection" link
    private int _hover = -1;         // 0..n-1 = profile card, n = action, -1 = none

    private const int CardW = 380, CardH = 58, CardGap = 12;

    public WelcomePanel()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
        BackColor = DarkTheme.Background;
        _profiles = ConnectionStore.Load();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        g.Clear(DarkTheme.Background);
        _profileRects.Clear();

        int centerX = Width / 2;
        bool hasProfiles = _profiles.Count > 0;

        using var titleFont = new Font("Consolas", 28f, FontStyle.Bold);
        using var subFont = new Font("Segoe UI", 11f);
        using var headingFont = new Font("Segoe UI", 9f, FontStyle.Bold);
        using var nameFont = new Font("Segoe UI", 11f, FontStyle.Bold);
        using var detailFont = new Font("Segoe UI", 8.5f);
        using var btnFont = new Font("Segoe UI", 10f, FontStyle.Bold);
        using var hintFont = new Font("Segoe UI", 8.5f);

        var titleSize = TextRenderer.MeasureText(g, "AC5250", titleFont);
        var subSize = TextRenderer.MeasureText(g, "x", subFont);

        // Compute total content height so the block is vertically centered.
        int contentH = titleSize.Height + 8 + subSize.Height + 30;
        if (hasProfiles)
            contentH += 22 /*heading*/ + _profiles.Count * (CardH + CardGap) + 30 /*new link*/;
        else
            contentH += 40 /*connect btn*/ + 22 /*hint*/;

        int y = Math.Max(24, (Height - contentH) / 2);

        // Title
        TextRenderer.DrawText(g, "AC5250", titleFont,
            new Point(centerX - titleSize.Width / 2, y), DarkTheme.Accent);
        y += titleSize.Height + 8;

        // Subtitle
        var sub = "Aidan's Custom TN5250 Terminal Emulator";
        var subMeasured = TextRenderer.MeasureText(g, sub, subFont);
        TextRenderer.DrawText(g, sub, subFont,
            new Point(centerX - subMeasured.Width / 2, y), DarkTheme.TextSecondary);
        y += subMeasured.Height + 30;

        if (hasProfiles)
        {
            // "QUICK LAUNCH" heading, left-aligned to the card column
            int cardX = centerX - CardW / 2;
            TextRenderer.DrawText(g, "QUICK LAUNCH", headingFont,
                new Point(cardX + 2, y), DarkTheme.TextMuted);
            y += 22;

            for (int i = 0; i < _profiles.Count; i++)
            {
                var rect = new Rectangle(cardX, y, CardW, CardH);
                _profileRects.Add(rect);
                bool hot = _hover == i;

                using var fill = new SolidBrush(hot ? Color.FromArgb(40, DarkTheme.Accent) : DarkTheme.Surface);
                using var pen = new Pen(hot ? DarkTheme.Accent : DarkTheme.Border, hot ? 1.5f : 1f);
                FillRoundedRect(g, fill, rect, 8);
                DrawRoundedRect(g, pen, rect, 8);

                var p = _profiles[i];
                TextRenderer.DrawText(g, p.DisplayName, nameFont,
                    new Point(rect.X + 16, rect.Y + 9), hot ? DarkTheme.AccentHover : DarkTheme.TextPrimary,
                    TextFormatFlags.Left);

                string size = p.ScreenSize == ScreenSize.Wide ? "27x132" : "24x80";
                string dev = string.IsNullOrEmpty(p.DeviceName) ? "auto device" : p.DeviceName;
                string detail = $"{p.HostName}:{p.Port}   ·   {dev}   ·   {size}{(p.UseSsl ? "   ·   SSL" : "")}";
                TextRenderer.DrawText(g, detail, detailFont,
                    new Point(rect.X + 16, rect.Y + 32), DarkTheme.TextSecondary, TextFormatFlags.Left);

                // ">" launch chevron on the right
                TextRenderer.DrawText(g, "›", nameFont,
                    new Rectangle(rect.Right - 34, rect.Y, 24, CardH),
                    hot ? DarkTheme.AccentHover : DarkTheme.TextMuted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

                y += CardH + CardGap;
            }

            // "+ New connection…" link
            var linkText = "+  New connection…";
            var linkSize = TextRenderer.MeasureText(g, linkText, detailFont);
            _actionRect = new Rectangle(centerX - linkSize.Width / 2 - 8, y + 4, linkSize.Width + 16, linkSize.Height + 8);
            bool linkHot = _hover == _profiles.Count;
            TextRenderer.DrawText(g, linkText, detailFont, _actionRect,
                linkHot ? DarkTheme.AccentHover : DarkTheme.TextMuted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        else
        {
            // No saved profiles: single Connect button (original behavior)
            int btnW = 180, btnH = 40;
            _actionRect = new Rectangle(centerX - btnW / 2, y, btnW, btnH);
            bool hot = _hover == 0;

            var btnColor = hot ? DarkTheme.AccentHover : DarkTheme.Accent;
            using var btnBrush = new SolidBrush(hot ? Color.FromArgb(30, DarkTheme.Accent) : Color.Transparent);
            using var btnPen = new Pen(btnColor, 1.5f);
            FillRoundedRect(g, btnBrush, _actionRect, 8);
            DrawRoundedRect(g, btnPen, _actionRect, 8);
            TextRenderer.DrawText(g, "Connect", btnFont, _actionRect, btnColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            y += btnH + 22;

            var hint = "Ctrl+N to connect  |  F1 for key mappings";
            var hintSize = TextRenderer.MeasureText(g, hint, hintFont);
            TextRenderer.DrawText(g, hint, hintFont,
                new Point(centerX - hintSize.Width / 2, y), DarkTheme.TextMuted);
        }
    }

    /// <summary>Hit-test index: 0..n-1 profile card, n = action (new link / connect btn), -1 none.</summary>
    private int HitTest(Point pt)
    {
        for (int i = 0; i < _profileRects.Count; i++)
            if (_profileRects[i].Contains(pt)) return i;
        if (_actionRect.Contains(pt)) return _profiles.Count > 0 ? _profiles.Count : 0;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hit = HitTest(e.Location);
        if (hit != _hover)
        {
            _hover = hit;
            Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover != -1) { _hover = -1; Cursor = Cursors.Default; Invalidate(); }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        int hit = HitTest(e.Location);
        if (hit < 0) return;

        if (_profiles.Count > 0 && hit < _profiles.Count)
            LaunchProfile?.Invoke(this, _profiles[hit]);   // quick-launch a saved profile
        else
            ConnectClicked?.Invoke(this, EventArgs.Empty);  // "+ New connection" or "Connect"
    }

    private static void FillRoundedRect(Graphics g, Brush brush, Rectangle rect, int radius)
    {
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        int d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }

    private static void DrawRoundedRect(Graphics g, Pen pen, Rectangle rect, int radius)
    {
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        int d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.DrawPath(pen, path);
    }
}

/// <summary>
/// Dark-themed About dialog.
/// </summary>
internal class AboutDialog : Form
{
    public AboutDialog()
    {
        Text = "About AC5250";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(360, 245);
        BackColor = DarkTheme.Surface;
        ForeColor = DarkTheme.TextPrimary;

        HandleCreated += (_, _) => MainForm.ApplyDarkTitleBar(this);

        var title = new Label
        {
            Text = "AC5250",
            Font = new Font("Consolas", 20f, FontStyle.Bold),
            ForeColor = DarkTheme.Accent,
            AutoSize = true,
            Location = new Point(24, 20),
        };
        Controls.Add(title);

        var version = new Label
        {
            Text = $"Version {AppVersion()}",
            ForeColor = DarkTheme.TextMuted,
            Font = DarkTheme.UIFont,
            AutoSize = true,
            Location = new Point(26, 56),
        };
        Controls.Add(version);

        var desc = new Label
        {
            Text = "Aidan's Custom TN5250 Terminal Emulator\nfor IBM AS/400 (iSeries) systems.\n\n.NET 10 / WinForms",
            ForeColor = DarkTheme.TextSecondary,
            Font = DarkTheme.UIFont,
            AutoSize = true,
            Location = new Point(24, 84),
        };
        Controls.Add(desc);

        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Size = new Size(80, 30),
            Location = new Point(260, 170),
            FlatStyle = FlatStyle.Flat,
            BackColor = DarkTheme.SurfaceLighter,
            ForeColor = DarkTheme.TextPrimary,
        };
        ok.FlatAppearance.BorderColor = DarkTheme.Border;
        Controls.Add(ok);
        AcceptButton = ok;
    }

    /// <summary>The running app version — the release version when installed/published
    /// (stamped from the build), or the assembly version for a local build.</summary>
    private static string AppVersion()
    {
        var asm = typeof(AboutDialog).Assembly;
        var info = (System.Reflection.AssemblyInformationalVersionAttribute?)
            System.Attribute.GetCustomAttribute(asm, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
        string v = info?.InformationalVersion ?? asm.GetName().Version?.ToString() ?? "dev";
        int plus = v.IndexOf('+'); // strip build-metadata suffix (+<git-hash>)
        return plus >= 0 ? v[..plus] : v;
    }
}

/// <summary>
/// Dark-themed key mappings help dialog.
/// </summary>
internal class KeyMappingsDialog : Form
{
    public KeyMappingsDialog()
    {
        Text = "Key Mappings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(420, 420);
        BackColor = DarkTheme.Surface;
        ForeColor = DarkTheme.TextPrimary;

        HandleCreated += (_, _) => MainForm.ApplyDarkTitleBar(this);

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 2,
            AutoScroll = true,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));

        var mappings = new (string key, string action)[]
        {
            ("Enter", "Send / Enter"),
            ("F1 - F12", "F1 - F12"),
            ("Shift + F1-F12", "F13 - F24"),
            ("Page Up", "Roll Down"),
            ("Page Down", "Roll Up"),
            ("Escape", "Attention"),
            ("Shift + Escape", "System Request"),
            ("Pause", "Clear"),
            ("Tab", "Next Field"),
            ("Shift + Tab", "Previous Field"),
            ("Insert", "Toggle Insert Mode"),
            ("Ctrl + R", "Reset"),
            ("Ctrl + E", "Erase Input"),
            ("Ctrl + H", "Help"),
            ("Ctrl + P", "Print"),
        };

        // Header
        AddRow(grid, "KEY", "5250 FUNCTION", true);

        foreach (var (key, action) in mappings)
            AddRow(grid, key, action, false);

        Controls.Add(grid);
    }

    private static void AddRow(TableLayoutPanel grid, string left, string right, bool isHeader)
    {
        var font = isHeader ? DarkTheme.UIFontBold : DarkTheme.UIFont;
        var fg = isHeader ? DarkTheme.Accent : DarkTheme.TextPrimary;
        var fgRight = isHeader ? DarkTheme.Accent : DarkTheme.TextSecondary;

        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        int row = grid.RowCount++;

        grid.Controls.Add(new Label
        {
            Text = left,
            Font = isHeader ? font : DarkTheme.MonoFont,
            ForeColor = fg,
            AutoSize = true,
            Padding = new Padding(0, 3, 0, 3),
        }, 0, row);

        grid.Controls.Add(new Label
        {
            Text = right,
            Font = font,
            ForeColor = fgRight,
            AutoSize = true,
            Padding = new Padding(0, 3, 0, 3),
        }, 1, row);
    }
}

/// <summary>
/// A small non-modal toast that floats at the bottom-right of the window and auto-dismisses.
/// Added to the form's Controls and brought to front; removes itself on timeout or click.
/// </summary>
internal sealed class ToastNotification : Panel
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 4500 };

    public ToastNotification(string title, string message)
    {
        DoubleBuffered = true;
        Size = new Size(300, 62);
        BackColor = DarkTheme.SurfaceLighter;

        var titleLabel = new Label
        {
            Text = title,
            Location = new Point(14, 9),
            Size = new Size(274, 20),
            ForeColor = DarkTheme.Accent,
            Font = DarkTheme.UIFontBold,
            BackColor = Color.Transparent,
            Enabled = false,   // let clicks fall through to the panel (dismiss)
        };
        var msgLabel = new Label
        {
            Text = message,
            Location = new Point(14, 31),
            Size = new Size(274, 22),
            ForeColor = DarkTheme.TextSecondary,
            Font = DarkTheme.UIFont,
            BackColor = Color.Transparent,
            AutoEllipsis = true,
            Enabled = false,
        };
        Controls.Add(titleLabel);
        Controls.Add(msgLabel);

        _timer.Tick += (_, _) => Dismiss();
    }

    public void ShowAt(Form owner)
    {
        Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        Location = new Point(owner.ClientSize.Width - Width - 18, owner.ClientSize.Height - Height - 18);
        _timer.Start();
    }

    protected override void OnMouseClick(MouseEventArgs e) => Dismiss();

    private void Dismiss()
    {
        _timer.Stop();
        Parent?.Controls.Remove(this);
        Dispose();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        using var border = new Pen(DarkTheme.Border);
        g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        using var accent = new SolidBrush(DarkTheme.Danger);
        g.FillRectangle(accent, 0, 0, 3, Height);
    }
}

/// <summary>
/// Dark-themed manager for saved connections (File ▸ Manage Saved Connections). Lists the
/// entries in <see cref="ConnectionStore"/> and adds/edits (via <see cref="ConnectDialog"/>
/// in save-only mode) or deletes them. Does not connect anything.
/// </summary>
internal sealed class ManageConnectionsDialog : Form
{
    private readonly ListBox _list;
    private List<ConnectionSettings> _items = new();

    public ManageConnectionsDialog()
    {
        Text = "Manage Saved Connections";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(444, 316);
        BackColor = DarkTheme.Surface;
        ForeColor = DarkTheme.TextPrimary;
        Font = DarkTheme.UIFont;
        HandleCreated += (_, _) => MainForm.ApplyDarkTitleBar(this);

        Controls.Add(new Label
        {
            Text = "Saved Connections",
            Font = new Font("Segoe UI", 13f, FontStyle.Bold),
            ForeColor = DarkTheme.TextPrimary,
            Location = new Point(16, 14),
            AutoSize = true,
        });

        _list = new ListBox
        {
            Location = new Point(16, 48),
            Size = new Size(300, 252),
            BackColor = DarkTheme.Background,
            ForeColor = DarkTheme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Font = DarkTheme.UIFont,
            IntegralHeight = false,
        };
        _list.DoubleClick += (_, _) => EditSelected();
        Controls.Add(_list);

        const int bx = 328;
        AddButton("New…", bx, 48, (_, _) =>
        {
            using var d = new ConnectDialog(saveOnly: true);
            if (d.ShowDialog(this) == DialogResult.OK) Reload();
        });
        AddButton("Edit…", bx, 88, (_, _) => EditSelected());
        AddButton("Delete", bx, 128, (_, _) => DeleteSelected());
        AddButton("Close", bx, 270, (_, _) => Close());

        Reload();
    }

    private void EditSelected()
    {
        int i = _list.SelectedIndex;
        if (i < 0 || i >= _items.Count) return;
        using var d = new ConnectDialog(_items[i], saveOnly: true);
        if (d.ShowDialog(this) == DialogResult.OK) Reload();
    }

    private void DeleteSelected()
    {
        int i = _list.SelectedIndex;
        if (i < 0 || i >= _items.Count) return;
        ConnectionStore.Remove(_items[i].DisplayName);
        Reload();
    }

    private void Reload()
    {
        _items = ConnectionStore.Load();
        _list.Items.Clear();
        foreach (var c in _items) _list.Items.Add(c.DisplayName);
    }

    private void AddButton(string text, int x, int y, EventHandler onClick)
    {
        var b = new Button
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(100, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = DarkTheme.SurfaceLighter,
            ForeColor = DarkTheme.TextPrimary,
            Font = DarkTheme.UIFont,
        };
        b.FlatAppearance.BorderColor = DarkTheme.Border;
        b.Click += onClick;
        Controls.Add(b);
    }
}
