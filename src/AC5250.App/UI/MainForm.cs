using System.Runtime.InteropServices;
using AC5250.Hosting;
using AC5250.Input;
using AC5250.Rendering;
using AC5250.Session;

namespace AC5250.UI;

public class MainForm : Form
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_BORDER_COLOR = 34;

    private readonly SessionManager _sessionManager = new();
    private readonly SessionTabBar _tabBar;
    private readonly Panel _terminalPanel;
    private readonly MenuStrip _menu;
    private readonly Dictionary<TerminalSession, TerminalControl> _sessionControls = new();
    private TerminalControl? _activeControl;

    // MCP in-process host (started on demand via the Tools menu, or at launch with --mcp).
    private const int McpPort = 8250;
    private McpHost? _mcpHost;
    private SynchronizationContext? _uiContext;
    private readonly McpStartupOptions? _mcpStartup;
    private int EffectivePort => _mcpStartup?.Port ?? McpPort;

    private bool _uppercaseInput = true;
    private ToolStripMenuItem? _uppercaseMenuItem;

    private readonly AppSettings _settings = AppSettingsStore.Load();
    private ToolStripMenuItem? _mcpStartupMenuItem;

    public MainForm(McpStartupOptions? mcpStartup = null)
    {
        _mcpStartup = mcpStartup;

        Text = "AC5250";
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

        // Show welcome screen
        ShowWelcome();

        // Wire session manager events
        _sessionManager.SessionAdded += OnSessionAdded;
        _sessionManager.SessionRemoved += OnSessionRemoved;

        KeyPreview = true;
    }

    private void ShowWelcome()
    {
        var welcome = new WelcomePanel();
        welcome.ConnectClicked += (_, _) => OnConnect(this, EventArgs.Empty);
        welcome.LaunchProfile += (_, settings) => ConnectWith(settings);
        welcome.Dock = DockStyle.Fill;
        _terminalPanel.Controls.Add(welcome);
    }

    private void HideWelcome()
    {
        foreach (Control c in _terminalPanel.Controls)
        {
            if (c is WelcomePanel)
            {
                _terminalPanel.Controls.Remove(c);
                c.Dispose();
                break;
            }
        }
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
        fileMenu.DropDownItems.Add(CreateMenuItem("&Connect...", Keys.Control | Keys.N, OnConnect));
        fileMenu.DropDownItems.Add(CreateMenuItem("&Disconnect", Keys.Control | Keys.W, OnDisconnect));
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add(CreateMenuItem("E&xit", Keys.Alt | Keys.F4, (_, _) => Close()));
        menu.Items.Add(fileMenu);

        var sessionMenu = CreateMenuItem("&Session");
        sessionMenu.DropDownItems.Add(CreateMenuItem("&New Session...", Keys.Control | Keys.T, OnConnect));
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
        toolsMenu.DropDownItems.Add(CreateMenuItem("&Start MCP Server", Keys.Control | Keys.M, OnStartMcp));
        toolsMenu.DropDownItems.Add(CreateMenuItem("S&top MCP Server", onClick: OnStopMcp));
        toolsMenu.DropDownItems.Add(new ToolStripSeparator());
        toolsMenu.DropDownItems.Add(CreateMenuItem("MCP Connection &Info...", onClick: OnMcpInfo));
        toolsMenu.DropDownItems.Add(new ToolStripSeparator());
        _mcpStartupMenuItem = CreateMenuItem("Start MCP on &Startup", onClick: OnToggleMcpStartup);
        _mcpStartupMenuItem.Checked = _settings.StartMcpOnStartup;
        toolsMenu.DropDownItems.Add(_mcpStartupMenuItem);
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

    private void OnConnect(object? sender, EventArgs e)
    {
        using var dialog = new ConnectDialog();
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        ConnectWith(dialog.Settings);
    }

    /// <summary>Open and connect a session directly from settings (quick-launch), no dialog.</summary>
    private async void ConnectWith(ConnectionSettings settings)
    {
        HideWelcome();

        _uiContext ??= SynchronizationContext.Current;
        var session = _sessionManager.CreateSession(settings, _uiContext);
        session.UppercaseInput = _uppercaseInput;

        try
        {
            await session.ConnectAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to connect:\n{ex.Message}", "AC5250",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            _sessionManager.CloseSession(session);
        }
    }

    private void OnDisconnect(object? sender, EventArgs e)
    {
        var session = _sessionManager.ActiveSession;
        if (session != null)
            _sessionManager.CloseSession(session);
    }

    private void OnCloseSession(object? sender, EventArgs e) => OnDisconnect(sender, e);

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // Capture the WinForms synchronization context; sessions and the MCP host
        // use it to marshal host-driven parsing and MCP calls onto this thread.
        _uiContext = SynchronizationContext.Current;

        // Start the MCP server automatically unless the user turned it off, so an MCP
        // client (Claude) can connect without any manual step. --mcp forces it on too.
        if (_mcpStartup?.AutoStart == true || _settings.StartMcpOnStartup)
            _ = StartMcpAsync();
    }

    private async void OnStartMcp(object? sender, EventArgs e)
    {
        if (_mcpHost != null) { OnMcpInfo(sender, e); return; }

        if (await StartMcpAsync() && _mcpHost != null)
        {
            using var dlg = new McpInfoDialog(_mcpHost);
            dlg.ShowDialog(this);
        }
    }

    private async Task<bool> StartMcpAsync()
    {
        _uiContext ??= SynchronizationContext.Current;
        try
        {
            var host = new McpHost(_sessionManager, this, _uiContext!, EffectivePort);
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

    private async void OnStopMcp(object? sender, EventArgs e)
    {
        if (_mcpHost == null)
        {
            MessageBox.Show("MCP server is not running.", "AC5250",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var host = _mcpHost;
        _mcpHost = null;
        await host.DisposeAsync();
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
        var creds = AC5250.Security.CredentialStore.Get(s.Settings.HostName);
        if (creds is null)
        {
            MessageBox.Show(
                $"No saved credentials for host '{s.Settings.HostName}'.\nAdd them via Session > Manage Saved Credentials.",
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
        if (_settings.StartMcpOnStartup && _mcpHost == null)
            _ = StartMcpAsync();
    }

    private void OnMcpInfo(object? sender, EventArgs e)
    {
        if (_mcpHost == null)
        {
            MessageBox.Show("MCP server is not running. Use Tools → Start MCP Server.",
                "AC5250", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dlg = new McpInfoDialog(_mcpHost);
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

    private void OnSessionAdded(TerminalSession session)
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

        _terminalPanel.Controls.Add(control);
        _sessionControls[session] = control;

        _tabBar.AddTab(session.Title, session);
        ActivateSession(session);

        session.ConnectionClosed += reason =>
        {
            if (InvokeRequired)
                BeginInvoke(() => OnSessionDisconnected(session, reason));
            else
                OnSessionDisconnected(session, reason);
        };

        session.StatusMessage += msg =>
        {
            if (InvokeRequired)
                BeginInvoke(() => UpdateStatus(session, msg));
            else
                UpdateStatus(session, msg);
        };

        if (!string.IsNullOrEmpty(_mcpStartup?.LogFile))
            session.DebugLogged += AppendLog;
    }

    private readonly object _logLock = new();

    // Append host-side trace lines to the --logfile. Lines for records we SEND are
    // skipped so the operator's keystrokes (including passwords) are never written.
    private void AppendLog(string line)
    {
        var path = _mcpStartup?.LogFile;
        if (string.IsNullOrEmpty(path)) return;
        if (line.Contains("SEND record")) return;
        try { lock (_logLock) System.IO.File.AppendAllText(path, line + Environment.NewLine); }
        catch { /* diagnostics only */ }
    }

    private void OnSessionRemoved(TerminalSession session)
    {
        if (!_sessionControls.TryGetValue(session, out var control)) return;

        control.DetachBuffer();
        _terminalPanel.Controls.Remove(control);
        control.Dispose();
        _sessionControls.Remove(session);

        int tabIdx = _tabBar.FindTabByTag(session);
        if (tabIdx >= 0) _tabBar.RemoveTabAt(tabIdx);

        if (_sessionControls.Count == 0)
        {
            _activeControl = null;
            ShowWelcome();
        }
    }

    private void OnSessionDisconnected(TerminalSession session, string reason)
    {
        if (_sessionControls.TryGetValue(session, out var control))
        {
            control.HostInfo = $"Disconnected: {reason}";
            int tabIdx = _tabBar.FindTabByTag(session);
            if (tabIdx >= 0) _tabBar.SetTabTitle(tabIdx, $"[Closed] {session.Title}");
            control.Invalidate();
        }
    }

    private void UpdateStatus(TerminalSession session, string msg)
    {
        if (_sessionControls.TryGetValue(session, out var control))
        {
            control.HostInfo = msg;
            control.Invalidate();
        }
    }

    private void OnTabChanged(object? sender, EventArgs e)
    {
        var tag = _tabBar.GetTabTag(_tabBar.SelectedIndex);
        if (tag is TerminalSession session)
            ActivateSession(session);
    }

    private void OnTabClose(object? sender, int index)
    {
        var tag = _tabBar.GetTabTag(index);
        if (tag is TerminalSession session)
            _sessionManager.CloseSession(session);
    }

    private void ActivateSession(TerminalSession session)
    {
        // Hide all controls
        foreach (var ctrl in _sessionControls.Values)
            ctrl.Visible = false;

        if (_sessionControls.TryGetValue(session, out var control))
        {
            control.Visible = true;
            control.BringToFront();
            control.Focus();
            _activeControl = control;
        }

        _sessionManager.SetActive(session);
    }

    private void SetColorScheme(ColorScheme scheme)
    {
        foreach (var control in _sessionControls.Values)
            control.Colors = scheme;
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

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_mcpHost != null)
        {
            try { _mcpHost.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)); } catch { /* shutting down */ }
            _mcpHost = null;
        }
        _sessionManager.CloseAll();
        base.OnFormClosing(e);
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
