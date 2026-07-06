using AC5250.Hosting;

namespace AC5250.UI;

/// <summary>
/// Shows the running MCP server's loopback URL and a ready-to-paste
/// <c>claude mcp add</c> command. No token: the server is loopback-only.
/// </summary>
internal sealed class McpInfoDialog : Form
{
    public McpInfoDialog(McpHost host)
    {
        Text = "MCP Server";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(660, 340);
        BackColor = DarkTheme.Surface;
        ForeColor = DarkTheme.TextPrimary;
        HandleCreated += (_, _) => MainForm.ApplyDarkTitleBar(this);

        var title = new Label
        {
            Text = "MCP server is running",
            Font = new Font("Consolas", 14f, FontStyle.Bold),
            ForeColor = DarkTheme.Accent,
            AutoSize = true,
            Location = new Point(20, 16),
        };

        var status = new Label
        {
            Text = $"Listening on {host.Url}  (127.0.0.1 only)",
            ForeColor = DarkTheme.TextSecondary,
            Font = DarkTheme.UIFont,
            AutoSize = true,
            Location = new Point(22, 48),
        };

        var lbl = new Label
        {
            Text = "Register with Claude Code (loopback only — no token needed):",
            ForeColor = DarkTheme.TextSecondary,
            Font = DarkTheme.UIFont,
            AutoSize = true,
            Location = new Point(22, 84),
        };

        var box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = true,
            Font = DarkTheme.MonoFont,
            BackColor = DarkTheme.Background,
            ForeColor = DarkTheme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Location = new Point(22, 108),
            Size = new Size(606, 100),
            Text = host.ClaudeAddCommand,
        };

        var copyBtn = new Button
        {
            Text = "Copy command",
            Size = new Size(150, 32),
            Location = new Point(22, 224),
            FlatStyle = FlatStyle.Flat,
            BackColor = DarkTheme.SurfaceLighter,
            ForeColor = DarkTheme.TextPrimary,
        };
        copyBtn.FlatAppearance.BorderColor = DarkTheme.Border;
        copyBtn.Click += (_, _) =>
        {
            try { Clipboard.SetText(host.ClaudeAddCommand); copyBtn.Text = "Copied!"; }
            catch { /* clipboard occasionally unavailable */ }
        };

        var noteLbl = new Label
        {
            Text = "Reachable only from this machine while the app is running.",
            ForeColor = DarkTheme.TextMuted,
            Font = DarkTheme.UIFont,
            AutoSize = true,
            Location = new Point(22, 268),
        };

        var ok = new Button
        {
            Text = "Close",
            DialogResult = DialogResult.OK,
            Size = new Size(90, 32),
            Location = new Point(538, 224),
            FlatStyle = FlatStyle.Flat,
            BackColor = DarkTheme.SurfaceLighter,
            ForeColor = DarkTheme.TextPrimary,
        };
        ok.FlatAppearance.BorderColor = DarkTheme.Border;

        Controls.AddRange(new Control[] { title, status, lbl, box, copyBtn, noteLbl, ok });
        AcceptButton = ok;
    }
}
