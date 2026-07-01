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
        sessionMenu.DropDownItems.Add(CreateMenuItem("&Debug Log...", Keys.Control | Keys.Shift | Keys.D, OnShowDebugLog));
        menu.Items.Add(sessionMenu);

        var viewMenu = CreateMenuItem("&View");
        var colorMenu = CreateMenuItem("Color &Scheme");
        colorMenu.DropDownItems.Add(CreateMenuItem("Classic &Green", onClick: (_, _) => SetColorScheme(ColorScheme.Classic)));
        colorMenu.DropDownItems.Add(CreateMenuItem("&Amber", onClick: (_, _) => SetColorScheme(ColorScheme.Amber)));
        colorMenu.DropDownItems.Add(CreateMenuItem("&White on Black", onClick: (_, _) => SetColorScheme(ColorScheme.WhiteOnBlack)));
        viewMenu.DropDownItems.Add(colorMenu);
        menu.Items.Add(viewMenu);

        var toolsMenu = CreateMenuItem("&Tools");
        toolsMenu.DropDownItems.Add(CreateMenuItem("&Start MCP Server", Keys.Control | Keys.M, OnStartMcp));
        toolsMenu.DropDownItems.Add(CreateMenuItem("S&top MCP Server", onClick: OnStopMcp));
        toolsMenu.DropDownItems.Add(new ToolStripSeparator());
        toolsMenu.DropDownItems.Add(CreateMenuItem("MCP Connection &Info...", onClick: OnMcpInfo));
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

    private async void OnConnect(object? sender, EventArgs e)
    {
        using var dialog = new ConnectDialog();
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        HideWelcome();

        _uiContext ??= SynchronizationContext.Current;
        var session = _sessionManager.CreateSession(dialog.Settings, _uiContext);

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

        if (_mcpStartup?.AutoStart == true)
            _ = StartMcpAsync(_mcpStartup.Token);
    }

    private async void OnStartMcp(object? sender, EventArgs e)
    {
        if (_mcpHost != null) { OnMcpInfo(sender, e); return; }

        if (await StartMcpAsync(null) && _mcpHost != null)
        {
            using var dlg = new McpInfoDialog(_mcpHost);
            dlg.ShowDialog(this);
        }
    }

    private async Task<bool> StartMcpAsync(string? token)
    {
        _uiContext ??= SynchronizationContext.Current;
        try
        {
            var host = new McpHost(_sessionManager, this, _uiContext!, EffectivePort, token);
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
    private Rectangle _buttonRect;
    private bool _buttonHover;

    public WelcomePanel()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
        BackColor = DarkTheme.Background;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        g.Clear(DarkTheme.Background);

        int centerX = Width / 2;
        int y = Height / 2 - 80;

        // Title
        using var titleFont = new Font("Consolas", 28f, FontStyle.Bold);
        var titleSize = TextRenderer.MeasureText(g, "AC5250", titleFont);
        TextRenderer.DrawText(g, "AC5250", titleFont,
            new Point(centerX - titleSize.Width / 2, y), DarkTheme.Accent);
        y += titleSize.Height + 8;

        // Subtitle
        using var subFont = new Font("Segoe UI", 11f);
        var sub = "Aidan's Custom TN5250 Terminal Emulator";
        var subSize = TextRenderer.MeasureText(g, sub, subFont);
        TextRenderer.DrawText(g, sub, subFont,
            new Point(centerX - subSize.Width / 2, y), DarkTheme.TextSecondary);
        y += subSize.Height + 32;

        // Connect button
        int btnW = 180, btnH = 40;
        _buttonRect = new Rectangle(centerX - btnW / 2, y, btnW, btnH);

        var btnColor = _buttonHover ? DarkTheme.AccentHover : DarkTheme.Accent;
        using var btnBrush = new SolidBrush(_buttonHover ? Color.FromArgb(30, DarkTheme.Accent) : Color.Transparent);
        using var btnPen = new Pen(btnColor, 1.5f);

        FillRoundedRect(g, btnBrush, _buttonRect, 8);
        DrawRoundedRect(g, btnPen, _buttonRect, 8);

        using var btnFont = new Font("Segoe UI", 10f, FontStyle.Bold);
        TextRenderer.DrawText(g, "Connect", btnFont, _buttonRect, btnColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        y += btnH + 20;

        // Hint
        using var hintFont = new Font("Segoe UI", 8.5f);
        var hint = "Ctrl+N to connect  |  F1 for key mappings";
        var hintSize = TextRenderer.MeasureText(g, hint, hintFont);
        TextRenderer.DrawText(g, hint, hintFont,
            new Point(centerX - hintSize.Width / 2, y), DarkTheme.TextMuted);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool hover = _buttonRect.Contains(e.Location);
        if (hover != _buttonHover)
        {
            _buttonHover = hover;
            Cursor = hover ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_buttonHover) { _buttonHover = false; Invalidate(); }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (_buttonRect.Contains(e.Location))
            ConnectClicked?.Invoke(this, EventArgs.Empty);
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
        Size = new Size(360, 220);
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

        var desc = new Label
        {
            Text = "Aidan's Custom TN5250 Terminal Emulator\nfor IBM AS/400 (iSeries) systems.\n\n.NET 10 / WinForms",
            ForeColor = DarkTheme.TextSecondary,
            Font = DarkTheme.UIFont,
            AutoSize = true,
            Location = new Point(24, 60),
        };
        Controls.Add(desc);

        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Size = new Size(80, 30),
            Location = new Point(260, 145),
            FlatStyle = FlatStyle.Flat,
            BackColor = DarkTheme.SurfaceLighter,
            ForeColor = DarkTheme.TextPrimary,
        };
        ok.FlatAppearance.BorderColor = DarkTheme.Border;
        Controls.Add(ok);
        AcceptButton = ok;
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
