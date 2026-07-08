using System.Runtime.InteropServices;
using AC5250.Hosting;
using AC5250.Input;
using AC5250.Rendering;
using AC5250.Security;
using AC5250.Session;

namespace AC5250.UI;

internal class MainForm : Form
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_BORDER_COLOR = 34;

    // Custom title bar: keep the native sizing frame (thin border + native resize/snap/maximize)
    // but reclaim the caption area for our own tab strip via WM_NCCALCSIZE. No client padding,
    // so there's no visible gray border.
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr w, IntPtr l);

    private readonly AppShell _shell;
    private readonly SessionManager _sessionManager;
    private readonly SessionTabBar _tabBar;
    private Panel _captionBar = null!;
    private CaptionButtons _captionButtons = null!;
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

        // Chrome-style custom frame: the native title bar is removed in WndProc (WM_NCCALCSIZE)
        // while the native sizing frame is kept, so the caption strip (tabs + window buttons)
        // sits at the very top with the menu below and the terminal beneath — and there's no
        // gray client border. FormBorderStyle stays the default Sizable.

        // Menu
        _menu = CreateMenu();
        MainMenuStrip = _menu;

        // Tab bar — fills the caption strip, left of the window buttons.
        _tabBar = new SessionTabBar { Dock = DockStyle.Fill };
        _tabBar.SelectedIndexChanged += OnTabChanged;
        _tabBar.TabCloseClicked += OnTabClose;
        _tabBar.NewTabClicked += (_, _) => OpenHomeTab();
        _tabBar.TabDetached += OnTabDetached;
        _tabBar.WindowDragReleased += pt => _shell.TryDockSingleTab(this, pt);
        _tabBar.TabDragOver += pt => _shell.UpdateDropTarget(this, pt);
        _tabBar.TabDragEnded += () => _shell.ClearDropTarget();

        _captionButtons = new CaptionButtons(this) { Dock = DockStyle.Right };
        _captionBar = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = DarkTheme.Background };
        _captionBar.Controls.Add(_captionButtons);   // Right
        _captionBar.Controls.Add(_tabBar);           // Fill (added last so it takes the remainder)

        // Terminal panel
        _terminalPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = DarkTheme.Background,
            Padding = new Padding(2, 0, 2, 2),
        };

        // Add order: Fill first, then Top controls inner→outer (menu below the caption strip,
        // caption strip at the very top).
        Controls.Add(_terminalPanel);
        Controls.Add(_menu);
        Controls.Add(_captionBar);

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
        welcome.LaunchProfile += (_, settings) => StartSessionInTab(ctx, settings); // launch a saved connection (no sign-on)
        welcome.QuickSignOn += (settings, label) => StartQuickSignOn(ctx, settings, label); // connect + sign on
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

    /// <summary>Launch a saved connection from the Home page's Quick Launch column: connect,
    /// then sign on with the credential bound to that connection once the sign-on screen is up.</summary>
    private void StartQuickSignOn(TabContext ctx, ConnectionSettings settings, string label)
    {
        (string User, string Password)? creds = OperatingSystem.IsWindows()
            ? CredentialStore.GetForConnection(settings.Id, label)
            : null;
        StartSessionInTab(ctx, settings, creds);
    }

    /// <summary>Bring a connection up inside the given tab, replacing its current content
    /// (a Home page, or a previous disconnected session). When <paramref name="autoSignOn"/> is
    /// supplied, the sign-on screen is filled and submitted automatically once it appears.</summary>
    private async void StartSessionInTab(TabContext ctx, ConnectionSettings settings,
        (string User, string Password)? autoSignOn = null)
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
        catch { return; /* ConnectionClosed already fired → the tab shows [Closed] + a toast */ }

        if (autoSignOn is { } c)
            _ = AutoSignOnAsync(session, c.User, c.Password);
    }

    /// <summary>Wait (up to a bounded time) for a fillable sign-on screen to arrive after
    /// connecting, then fill the user/password fields and press Enter. Runs on the UI thread
    /// (awaits resume on the WinForms context). The password lives only in this local and the
    /// hidden field — it is never logged.</summary>
    private static async Task AutoSignOnAsync(TerminalSession session, string user, string password)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 20_000)
        {
            if (!session.IsConnected) return;
            if (!session.Screen.InputInhibited && TryFindSignOnFields(session, out int userIdx, out int pwIdx))
            {
                if (session.SetFieldValue(userIdx, user, true) &&
                    session.SetFieldValue(pwIdx, password, true) &&
                    AC5250.Mcp.KeyNames.TryParse("Enter", out var enter))
                    session.HandleKeyAction(enter);
                return;
            }
            await Task.Delay(200);
        }
    }

    /// <summary>Locate the sign-on user field (first visible non-bypass input) and password
    /// field (first non-display input) by index into the current screen's field list.</summary>
    private static bool TryFindSignOnFields(TerminalSession session, out int userIdx, out int pwIdx)
    {
        userIdx = -1; pwIdx = -1;
        var fields = session.Screen.Fields;
        for (int i = 0; i < fields.Count; i++)
        {
            var a = fields[i].Attribute;
            if (a.IsBypass) continue;
            if (a.IsNonDisplay) { if (pwIdx < 0) pwIdx = i; }
            else if (userIdx < 0) userIdx = i;
        }
        return userIdx >= 0 && pwIdx >= 0;
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

    // Manual counterpart to the MCP `signon` tool: fill the active session's sign-on screen
    // from a saved credential and submit. The password is read from Windows Credential Manager
    // and only ever written into the (hidden) field.
    private void OnSignOn(object? sender, EventArgs e)
    {
        var s = _sessionManager.ActiveSession;
        if (s == null)
        {
            MessageBox.Show("No active session.", "AC5250", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var creds = ResolveSignOnCredential(s);
        if (creds is null) return;   // already messaged the user, or they cancelled the picker

        if (!TryFindSignOnFields(s, out int userIdx, out int pwIdx))
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

    /// <summary>Pick the saved credential to sign on with for the active session. Credentials are
    /// bound to a saved connection: the session's own connection id, or — for an ad-hoc connection
    /// to a known host — a saved connection matched by endpoint. Prompts when the connection has
    /// more than one login. Returns null (after messaging the user, or on cancel) when none resolve.</summary>
    private (string User, string Password)? ResolveSignOnCredential(TerminalSession s)
    {
        string? connId = string.IsNullOrEmpty(s.Settings.Id)
            ? ConnectionStore.FindByEndpoint(s.Settings.HostName, s.Settings.Port, s.Settings.DeviceName)?.Id
            : s.Settings.Id;

        var labels = string.IsNullOrEmpty(connId)
            ? (IReadOnlyList<string>)Array.Empty<string>()
            : CredentialStore.LabelsForConnection(connId);

        if (labels.Count == 0)
        {
            MessageBox.Show(
                $"No saved credentials for '{s.Settings.DisplayName}'.\nAdd them via Session > Manage Saved Credentials.",
                "AC5250", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }

        string? label = labels.Count == 1 ? labels[0] : CredentialPicker.Choose(this, s.Settings.DisplayName, labels);
        if (label == null) return null;   // user cancelled the picker

        var creds = CredentialStore.GetForConnection(connId!, label);
        if (creds is null)
            MessageBox.Show($"Could not read the saved credential for '{s.Settings.DisplayName}'.",
                "AC5250", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return creds;
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

        control.PasteRequested += text => PasteIntoSession(session, text);

        return control;
    }

    /// <summary>Type pasted clipboard text into the active field via the session's normal
    /// input path. Tabs and newlines advance to the next field (so a spreadsheet row or a
    /// multi-line block fills consecutive fields); only printable characters are typed.
    /// Newlines are never turned into an Enter/AID, so a paste can never submit the screen.</summary>
    private static void PasteIntoSession(TerminalSession session, string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        // Normalise line endings, then split on tab OR newline into field-sized tokens.
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        string[] tokens = text.Split('\t', '\n');

        for (int i = 0; i < tokens.Length; i++)
        {
            var sb = new System.Text.StringBuilder(tokens[i].Length);
            foreach (char ch in tokens[i])
                if (ch >= ' ' && ch <= '~') sb.Append(ch);   // drop stray control chars

            if (sb.Length > 0) session.TypeString(sb.ToString());
            if (i < tokens.Length - 1)
                session.HandleKeyAction(KeyAction.FromType(KeyActionType.Tab));
        }
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

    // A tab dragged out of the bar: hand it to the shell, which docks it into another window
    // if dropped over that window's tab strip, otherwise opens a new window at the drop point.
    // The session stays in the shared SessionManager, so MCP keeps driving it.
    private void OnTabDetached(int index, Point screenLocation)
    {
        if (_tabBar.GetTabTag(index) is TabContext ctx)
            _shell.DropDetachedTab(this, DetachTab(ctx), screenLocation);
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
        if (_tabs.Count == 0)
        {
            // Closing the last tab closes the window — unless it's the last window, which
            // instead falls back to the Home page so the app stays open. Defer the Close so the
            // current tab-close / mouse handler unwinds before the form (and tab bar) dispose.
            if (_shell.WindowCount > 1) BeginInvoke(new Action(Close));
            else OpenHomeTab();
        }
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
        // No Home fallback here: multi-tab tear-out leaves other tabs; single-tab dock closes
        // the now-empty source window (the shell handles that).
        return ctx;
    }

    /// <summary>Detach this window's only tab — used when merging a single-tab window into
    /// another. The source window is expected to close afterwards.</summary>
    internal TabContext? DetachSingleTab() => _tabs.Count > 0 ? DetachTab(_tabs[0]) : null;

    /// <summary>Show/move the combine insertion placeholder in this window's tab strip.</summary>
    internal void SetDropTarget(Point screen) => _tabBar.SetDropTarget(screen);
    internal void ClearDrop() => _tabBar.ClearDrop();

    /// <summary>Take a tab detached from another window into this one and select it.</summary>
    internal void AdoptTab(TabContext ctx)
    {
        _tabs.Add(ctx);
        _terminalPanel.Controls.Add(ctx.Content);
        string title = ctx.Session is { } s ? s.Title : "Home";
        _tabBar.AdoptTabAtDrop(title, ctx);   // inserts at the placeholder slot (or appends) + selects
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

    // ---- custom-frame plumbing (native sizing frame, caption reclaimed for our tab strip) ---

    protected override void WndProc(ref Message m)
    {
        const int WM_NCCALCSIZE = 0x0083;

        if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
        {
            // rgrc[0] (first RECT at lParam) is the proposed window rect. Let Windows apply its
            // default frame (adds the native resize borders + caption inset), then give the
            // caption height back to the client so our tab strip fills the top. The native side/
            // bottom sizing borders remain, so resize + snap + maximize all stay native — and
            // there's no client padding, so no gray border. When maximized we keep the default
            // top inset (otherwise the top would be clipped off-screen).
            int originalTop = Marshal.PtrToStructure<RECT>(m.LParam).top;
            DefWindowProc(m.HWnd, m.Msg, m.WParam, m.LParam);
            var r = Marshal.PtrToStructure<RECT>(m.LParam);
            if (WindowState != FormWindowState.Maximized) r.top = originalTop;
            Marshal.StructureToPtr(r, m.LParam, false);
            m.Result = IntPtr.Zero;
            return;
        }

        base.WndProc(ref m);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _captionButtons?.Invalidate();   // refresh the max/restore glyph
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }

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
/// Welcome / launcher screen shown for a Home tab. Two columns:
///  • left — saved connections; clicking one connects (no sign-on), like the old quick-launch.
///  • right — "quick launch": one entry per saved credential (bound to a connection); clicking
///    connects to that connection AND signs on with that credential in a single step.
/// </summary>
internal class WelcomePanel : Control
{
    public event EventHandler? ConnectClicked;                    // "+ New connection"
    public event EventHandler<ConnectionSettings>? LaunchProfile; // left card: connect only
    public event Action<ConnectionSettings, string>? QuickSignOn; // right card: connect + sign on as (label)

    private static readonly List<(string Label, string User)> NoLogins = new();

    private List<ConnectionSettings> _connections = new();
    // Saved logins (label + user) per connection id — shown inside each connection's card.
    private Dictionary<string, List<(string Label, string User)>> _logins = new(StringComparer.OrdinalIgnoreCase);

    // Hit regions, rebuilt every paint so they always match what's drawn.
    private readonly List<(Rectangle Rect, int Conn)> _headerHits = new();          // header -> connect (no sign-on)
    private readonly List<(Rectangle Rect, int Conn, int Login)> _loginHits = new(); // login row -> connect + sign on
    private Rectangle _newConnRect;

    private enum HotKind { None, Header, Login, New }
    private HotKind _hotKind = HotKind.None;
    private int _hotConn = -1;
    private int _hotLogin = -1;

    // Card geometry. Cards size to their login count; the two columns pack independently.
    private const int CardW = 320, CardGap = 12, ColGap = 24, PadX = 14;
    private const int NameY = 11, DetailY = 32, DividerY = 53, FirstLoginY = 61, LoginRowH = 22, CardBottomPad = 10;
    private const int NewBtnW = 210, NewBtnH = 34, GridBtnGap = 18, HeadingH = 30;

    public WelcomePanel()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
        BackColor = DarkTheme.Background;
        LoadData();
    }

    // Re-read connections + credentials whenever the Home tab is shown, so changes made in the
    // Manage dialogs while this tab was in the background appear when the user returns to it.
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) return;
        LoadData();
        _hotKind = HotKind.None;
        _hotConn = _hotLogin = -1;
        Invalidate();
    }

    /// <summary>Load saved connections and, per connection, its saved logins (label + user).</summary>
    private void LoadData()
    {
        _connections = ConnectionStore.Load();
        _logins = new(StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows()) return;   // Credential Manager is Windows-only

        foreach (var (connId, label, user) in AC5250.Security.CredentialStore.ListForConnection())
        {
            if (!_logins.TryGetValue(connId, out var list)) _logins[connId] = list = new();
            list.Add((label, user));
        }
        foreach (var list in _logins.Values)
            list.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
    }

    private List<(string Label, string User)> LoginsFor(ConnectionSettings c)
        => !string.IsNullOrEmpty(c.Id) && _logins.TryGetValue(c.Id, out var l) ? l : NoLogins;

    private int CardHeight(ConnectionSettings c)
        => FirstLoginY + Math.Max(LoginsFor(c).Count, 1) * LoginRowH + CardBottomPad;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        g.Clear(DarkTheme.Background);
        _headerHits.Clear();
        _loginHits.Clear();
        _newConnRect = Rectangle.Empty;

        using var titleFont = new Font("Consolas", 22f, FontStyle.Bold);
        using var subFont = new Font("Segoe UI", 10f);
        using var headingFont = new Font("Segoe UI", 9f, FontStyle.Bold);
        using var nameFont = new Font("Segoe UI", 11f, FontStyle.Bold);
        using var detailFont = new Font("Segoe UI", 8.5f);
        using var loginFont = new Font("Segoe UI", 9.5f);

        int centerX = Width / 2;
        var titleSize = TextRenderer.MeasureText(g, "AC5250", titleFont);
        var sub = "Aidan's Custom TN5250 Terminal Emulator";
        var subSize = TextRenderer.MeasureText(g, sub, subFont);

        // Two-column grid, centered as a pair; colW shrinks to fit a narrow window.
        int availW = Width - 32;
        int colW = Math.Clamp((availW - ColGap) / 2, 200, CardW);
        int totalW = colW * 2 + ColGap;
        int leftX = Math.Max(16, centerX - totalW / 2);
        int[] colX = { leftX, leftX + colW + ColGap };

        // Assign each connection to the currently-shorter column (masonry), so a tall card on
        // one side doesn't leave a gap on the other. Track each card's column + relative Y.
        int[] colBottom = { 0, 0 };
        var place = new (int Col, int RelY, int H)[_connections.Count];
        for (int i = 0; i < _connections.Count; i++)
        {
            int h = CardHeight(_connections[i]);
            int col = colBottom[0] <= colBottom[1] ? 0 : 1;
            place[i] = (col, colBottom[col], h);
            colBottom[col] += h + CardGap;
        }
        int gridH = Math.Max(0, Math.Max(colBottom[0], colBottom[1]) - CardGap);

        // Center the whole block (title + heading + grid + New-connection button) vertically.
        int headerBlockH = titleSize.Height + 2 + subSize.Height + 26;
        int contentH = headerBlockH + HeadingH + gridH + GridBtnGap + NewBtnH;
        int y = Math.Max(20, (Height - contentH) / 2);

        TextRenderer.DrawText(g, "AC5250", titleFont, new Point(centerX - titleSize.Width / 2, y), DarkTheme.Accent);
        y += titleSize.Height + 2;
        TextRenderer.DrawText(g, sub, subFont, new Point(centerX - subSize.Width / 2, y), DarkTheme.TextSecondary);
        y += subSize.Height + 26;

        TextRenderer.DrawText(g, "SAVED CONNECTIONS", headingFont, new Point(leftX + 2, y), DarkTheme.TextMuted);
        int cardsTop = y + HeadingH;

        for (int i = 0; i < _connections.Count; i++)
        {
            var (col, relY, h) = place[i];
            DrawConnectionCard(g, new Rectangle(colX[col], cardsTop + relY, colW, h), i, nameFont, detailFont, loginFont);
        }

        // Single "+ New connection" button, centered beneath both columns.
        int btnW = Math.Min(NewBtnW, colW);
        _newConnRect = new Rectangle(centerX - btnW / 2, cardsTop + gridH + GridBtnGap, btnW, NewBtnH);
        DrawNewButton(g, _newConnRect, _hotKind == HotKind.New, detailFont);
    }

    private void DrawConnectionCard(Graphics g, Rectangle rect, int connIndex, Font nameFont, Font detailFont, Font loginFont)
    {
        var c = _connections[connIndex];
        var logins = LoginsFor(c);
        bool headerHot = _hotKind == HotKind.Header && _hotConn == connIndex;

        PaintCardBackground(g, rect, headerHot);

        // Header: name + endpoint detail + chevron (clicking here connects, no sign-on).
        TextRenderer.DrawText(g, c.DisplayName, nameFont,
            new Rectangle(rect.X + PadX, rect.Y + NameY, rect.Width - PadX - 30, 20),
            headerHot ? DarkTheme.AccentHover : DarkTheme.TextPrimary,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

        string size = c.ScreenSize == ScreenSize.Wide ? "27x132" : "24x80";
        string dev = string.IsNullOrEmpty(c.DeviceName) ? "auto device" : c.DeviceName;
        string detail = $"{c.HostName}:{c.Port}  ·  {dev}  ·  {size}{(c.UseSsl ? "  ·  SSL" : "")}";
        TextRenderer.DrawText(g, detail, detailFont,
            new Rectangle(rect.X + PadX, rect.Y + DetailY, rect.Width - PadX - 30, 18), DarkTheme.TextSecondary,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

        TextRenderer.DrawText(g, "›", nameFont, new Rectangle(rect.Right - 30, rect.Y, 22, DividerY),
            headerHot ? DarkTheme.AccentHover : DarkTheme.TextMuted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        _headerHits.Add((new Rectangle(rect.X, rect.Y, rect.Width, DividerY), connIndex));

        using (var divider = new Pen(DarkTheme.BorderSubtle))
            g.DrawLine(divider, rect.X + PadX, rect.Y + DividerY, rect.Right - PadX, rect.Y + DividerY);

        // Body: one clickable "Sign on as …" row per saved login (connect + sign on), or a hint.
        if (logins.Count == 0)
        {
            TextRenderer.DrawText(g, "(no saved logins)", loginFont,
                new Rectangle(rect.X + PadX, rect.Y + FirstLoginY, rect.Width - PadX * 2, LoginRowH),
                DarkTheme.TextMuted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            return;
        }

        for (int k = 0; k < logins.Count; k++)
        {
            int rowY = rect.Y + FirstLoginY + k * LoginRowH;
            var rowRect = new Rectangle(rect.X + 6, rowY, rect.Width - 12, LoginRowH);
            bool rowHot = _hotKind == HotKind.Login && _hotConn == connIndex && _hotLogin == k;
            if (rowHot)
                using (var rb = new SolidBrush(Color.FromArgb(48, DarkTheme.Accent)))
                    FillRoundedRect(g, rb, rowRect, 5);

            var (label, user) = logins[k];
            string who = string.IsNullOrEmpty(label) ? "(default login)" : label;
            string suffix = !string.IsNullOrEmpty(user) && !string.Equals(user, label, StringComparison.OrdinalIgnoreCase)
                ? $"  ({user})" : "";
            TextRenderer.DrawText(g, $"›  Sign on as {who}{suffix}", loginFont,
                new Rectangle(rect.X + PadX, rowY, rect.Width - PadX * 2, LoginRowH),
                rowHot ? DarkTheme.AccentHover : DarkTheme.TextPrimary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            _loginHits.Add((rowRect, connIndex, k));
        }
    }

    private void DrawNewButton(Graphics g, Rectangle rect, bool hot, Font font)
    {
        using var fill = new SolidBrush(hot ? Color.FromArgb(40, DarkTheme.Accent) : Color.Transparent);
        using var pen = new Pen(hot ? DarkTheme.Accent : DarkTheme.Border, hot ? 1.5f : 1f);
        FillRoundedRect(g, fill, rect, 8);
        DrawRoundedRect(g, pen, rect, 8);
        TextRenderer.DrawText(g, "+  New connection", font, rect,
            hot ? DarkTheme.AccentHover : DarkTheme.TextMuted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private void PaintCardBackground(Graphics g, Rectangle rect, bool hot)
    {
        using var fill = new SolidBrush(hot ? Color.FromArgb(40, DarkTheme.Accent) : DarkTheme.Surface);
        using var pen = new Pen(hot ? DarkTheme.Accent : DarkTheme.Border, hot ? 1.5f : 1f);
        FillRoundedRect(g, fill, rect, 8);
        DrawRoundedRect(g, pen, rect, 8);
    }

    /// <summary>Which region (if any) is at a point: a connection header, a login row, or the New button.</summary>
    private (HotKind Kind, int Conn, int Login) HitTest(Point pt)
    {
        foreach (var (rect, conn, login) in _loginHits)
            if (rect.Contains(pt)) return (HotKind.Login, conn, login);
        foreach (var (rect, conn) in _headerHits)
            if (rect.Contains(pt)) return (HotKind.Header, conn, -1);
        if (_newConnRect.Contains(pt)) return (HotKind.New, -1, -1);
        return (HotKind.None, -1, -1);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var (kind, conn, login) = HitTest(e.Location);
        if (kind != _hotKind || conn != _hotConn || login != _hotLogin)
        {
            _hotKind = kind;
            _hotConn = conn;
            _hotLogin = login;
            Cursor = kind == HotKind.None ? Cursors.Default : Cursors.Hand;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hotKind != HotKind.None)
        {
            _hotKind = HotKind.None;
            _hotConn = _hotLogin = -1;
            Cursor = Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        var (kind, conn, login) = HitTest(e.Location);
        switch (kind)
        {
            case HotKind.Header:
                LaunchProfile?.Invoke(this, _connections[conn]);                                     // connect, no sign-on
                break;
            case HotKind.Login:
                QuickSignOn?.Invoke(_connections[conn], LoginsFor(_connections[conn])[login].Label); // connect + sign on
                break;
            case HotKind.New:
                ConnectClicked?.Invoke(this, EventArgs.Empty);                                        // new connection dialog
                break;
        }
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
        var c = _items[i];

        bool hasCreds = !string.IsNullOrEmpty(c.Id) && CredentialStore.LabelsForConnection(c.Id).Count > 0;
        string msg = hasCreds
            ? $"Delete '{c.DisplayName}' and its saved sign-on credential(s)?"
            : $"Delete '{c.DisplayName}'?";
        if (MessageBox.Show(this, msg, "AC5250", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        if (!string.IsNullOrEmpty(c.Id)) CredentialStore.DeleteAllForConnection(c.Id);
        ConnectionStore.Remove(c.DisplayName);
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
