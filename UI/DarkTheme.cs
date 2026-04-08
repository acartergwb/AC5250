namespace AC5250.UI;

/// <summary>
/// Centralized dark theme colors and constants.
/// </summary>
public static class DarkTheme
{
    // Base colors
    public static readonly Color Background = Color.FromArgb(24, 24, 28);
    public static readonly Color Surface = Color.FromArgb(32, 33, 38);
    public static readonly Color SurfaceLight = Color.FromArgb(42, 43, 48);
    public static readonly Color SurfaceLighter = Color.FromArgb(55, 56, 62);
    public static readonly Color Border = Color.FromArgb(58, 60, 66);
    public static readonly Color BorderSubtle = Color.FromArgb(42, 44, 50);

    // Text colors
    public static readonly Color TextPrimary = Color.FromArgb(220, 222, 228);
    public static readonly Color TextSecondary = Color.FromArgb(150, 153, 162);
    public static readonly Color TextMuted = Color.FromArgb(100, 103, 112);

    // Accent - green tint to match terminal heritage
    public static readonly Color Accent = Color.FromArgb(72, 199, 142);
    public static readonly Color AccentDim = Color.FromArgb(40, 120, 90);
    public static readonly Color AccentHover = Color.FromArgb(90, 220, 160);
    public static readonly Color AccentBg = Color.FromArgb(30, 60, 48);

    // Semantic
    public static readonly Color Danger = Color.FromArgb(220, 80, 80);
    public static readonly Color Warning = Color.FromArgb(220, 180, 60);
    public static readonly Color Success = Color.FromArgb(72, 199, 142);

    // Fonts
    public static readonly Font UIFont = new("Segoe UI", 9f, FontStyle.Regular);
    public static readonly Font UIFontSmall = new("Segoe UI", 8f, FontStyle.Regular);
    public static readonly Font UIFontBold = new("Segoe UI", 9f, FontStyle.Bold);
    public static readonly Font MonoFont = new("Consolas", 9f, FontStyle.Regular);

    public static void ApplyTo(Control control)
    {
        control.BackColor = Background;
        control.ForeColor = TextPrimary;
        control.Font = UIFont;
    }
}

/// <summary>
/// Custom renderer for dark-themed MenuStrip and ToolStrip controls.
/// </summary>
public class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkMenuColorTable()) { }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var rc = new Rectangle(Point.Empty, e.Item.Size);
        if (e.Item.Selected || e.Item.Pressed)
        {
            using var brush = new SolidBrush(DarkTheme.SurfaceLighter);
            using var pen = new Pen(DarkTheme.BorderSubtle);
            var inner = Rectangle.Inflate(rc, -1, -1);
            e.Graphics.FillRectangle(brush, inner);
        }
        e.Item.ForeColor = e.Item.Enabled ? DarkTheme.TextPrimary : DarkTheme.TextMuted;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(DarkTheme.Surface);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(DarkTheme.BorderSubtle);
        e.Graphics.DrawLine(pen, 0, e.AffectedBounds.Bottom - 1,
            e.AffectedBounds.Width, e.AffectedBounds.Bottom - 1);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Height / 2;
        using var pen = new Pen(DarkTheme.BorderSubtle);
        e.Graphics.DrawLine(pen, 24, y, e.Item.Width - 4, y);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var rect = new Rectangle(e.ImageRectangle.X - 2, e.ImageRectangle.Y - 2,
            e.ImageRectangle.Width + 4, e.ImageRectangle.Height + 4);
        using var brush = new SolidBrush(DarkTheme.AccentBg);
        using var pen = new Pen(DarkTheme.Accent);
        e.Graphics.FillRectangle(brush, rect);
        e.Graphics.DrawRectangle(pen, rect);

        // Draw checkmark
        using var checkPen = new Pen(DarkTheme.Accent, 2);
        var cx = rect.X + rect.Width / 2;
        var cy = rect.Y + rect.Height / 2;
        e.Graphics.DrawLines(checkPen, new[] {
            new Point(cx - 4, cy), new Point(cx - 1, cy + 3), new Point(cx + 5, cy - 3)
        });
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        // No image margin background
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = DarkTheme.TextSecondary;
        base.OnRenderArrow(e);
    }
}

public class DarkMenuColorTable : ProfessionalColorTable
{
    public override Color MenuBorder => DarkTheme.Border;
    public override Color MenuItemBorder => Color.Transparent;
    public override Color MenuItemSelected => DarkTheme.SurfaceLighter;
    public override Color MenuStripGradientBegin => DarkTheme.Surface;
    public override Color MenuStripGradientEnd => DarkTheme.Surface;
    public override Color MenuItemSelectedGradientBegin => DarkTheme.SurfaceLighter;
    public override Color MenuItemSelectedGradientEnd => DarkTheme.SurfaceLighter;
    public override Color MenuItemPressedGradientBegin => DarkTheme.SurfaceLight;
    public override Color MenuItemPressedGradientEnd => DarkTheme.SurfaceLight;
    public override Color ToolStripDropDownBackground => DarkTheme.Surface;
    public override Color ImageMarginGradientBegin => DarkTheme.Surface;
    public override Color ImageMarginGradientMiddle => DarkTheme.Surface;
    public override Color ImageMarginGradientEnd => DarkTheme.Surface;
    public override Color SeparatorDark => DarkTheme.BorderSubtle;
    public override Color SeparatorLight => DarkTheme.BorderSubtle;
}
