using Velopack;

namespace AC5250.UI;

/// <summary>
/// Small dark splash shown at launch while an update is checked for and downloaded, so the user
/// isn't staring at a blank screen ("nothing happens for a while"). It runs the whole update flow
/// itself on its own message loop — reporting real download progress to a custom progress bar —
/// and:
///   • closes (so <see cref="Application.Run(Form)"/> returns and the app starts) when there is no
///     update or on any error, and
///   • never returns when an update is applied, because ApplyUpdatesAndRestart relaunches the
///     process.
/// </summary>
internal sealed class UpdateSplashForm : Form
{
    private readonly UpdateManager _mgr;
    private readonly System.Windows.Forms.Timer _marquee;

    private string _status = "Checking for updates…";
    private int _percent = -1;   // -1 = indeterminate (checking / installing); 0..100 = downloading
    private int _marqueePos;     // animated offset for the indeterminate bar

    public UpdateSplashForm(UpdateManager mgr)
    {
        _mgr = mgr;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        TopMost = true;
        ClientSize = new Size(420, 150);
        BackColor = DarkTheme.Surface;
        Text = "AC5250";
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);

        _marquee = new System.Windows.Forms.Timer { Interval = 30 };
        _marquee.Tick += (_, _) =>
        {
            if (_percent < 0) { _marqueePos = (_marqueePos + 6) % 10000; Invalidate(); }
        };
        _marquee.Start();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _ = RunUpdateAsync();   // fire-and-forget; continuations resume on this UI thread
    }

    private async Task RunUpdateAsync()
    {
        try
        {
            var updates = await _mgr.CheckForUpdatesAsync();
            if (updates == null) { Close(); return; }   // already up to date

            SetState($"Downloading update {updates.TargetFullRelease.Version}…", 0);
            await _mgr.DownloadUpdatesAsync(updates, p => SetState(null, p));

            SetState("Installing…", -1);
            _mgr.ApplyUpdatesAndRestart(updates);        // relaunches — we do not return
        }
        catch
        {
            Close();   // best-effort: start the app on the current version
        }
    }

    // Update status/progress from any thread; the download progress callback runs off the UI thread.
    private void SetState(string? status, int percent)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(() => SetState(status, percent)); } catch { /* closing */ } return; }
        if (status != null) _status = status;
        _percent = percent;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(DarkTheme.Surface);

        using (var border = new Pen(DarkTheme.Border))
            g.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);

        using var titleFont = new Font("Consolas", 22f, FontStyle.Bold);
        using var statusFont = new Font("Segoe UI", 9.5f);

        var titleSize = TextRenderer.MeasureText(g, "AC5250", titleFont);
        TextRenderer.DrawText(g, "AC5250", titleFont,
            new Point((ClientSize.Width - titleSize.Width) / 2, 26), DarkTheme.Accent);

        var statusSize = TextRenderer.MeasureText(g, _status, statusFont);
        TextRenderer.DrawText(g, _status, statusFont,
            new Point((ClientSize.Width - statusSize.Width) / 2, 76), DarkTheme.TextSecondary);

        // Progress bar: rounded track + accent fill (real percent), or a sliding segment while
        // the percentage is unknown (checking / installing).
        int barX = 34, barY = 108, barW = ClientSize.Width - 68, barH = 8;
        using (var track = new SolidBrush(DarkTheme.Background))
            FillRoundedRect(g, track, new Rectangle(barX, barY, barW, barH), 4);

        using var fill = new SolidBrush(DarkTheme.Accent);
        if (_percent >= 0)
        {
            int w = (int)(barW * (_percent / 100.0));
            if (w > 0) FillRoundedRect(g, fill, new Rectangle(barX, barY, Math.Max(w, barH), barH), 4);
        }
        else
        {
            const int seg = 100;
            int span = barW + seg;
            int x = barX - seg + (_marqueePos % span);
            int left = Math.Max(barX, x);
            int right = Math.Min(barX + barW, x + seg);
            if (right > left) FillRoundedRect(g, fill, new Rectangle(left, barY, right - left, barH), 4);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _marquee.Dispose();
        base.Dispose(disposing);
    }

    private static void FillRoundedRect(Graphics g, Brush brush, Rectangle rect, int radius)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;
        int d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }
}
