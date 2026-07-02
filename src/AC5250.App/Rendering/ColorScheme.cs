using AC5250.Model;

namespace AC5250.Rendering;

public class ColorScheme
{
    // Chrome / fallback
    public Color Background { get; set; } = Color.FromArgb(10, 10, 10);
    public Color Normal { get; set; } = Color.FromArgb(0, 200, 0);
    public Color HighIntensity { get; set; } = Color.White;
    public Color ColumnSeparator { get; set; } = Color.FromArgb(0, 100, 0);
    public Color NonDisplay { get; set; } = Color.FromArgb(10, 10, 10);
    public Color StatusBarBackground { get; set; } = Color.FromArgb(24, 26, 24);
    public Color StatusBarText { get; set; } = Color.FromArgb(0, 180, 0);
    public Color CursorColor { get; set; } = Color.FromArgb(0, 255, 0);
    public Color InputFieldBackground { get; set; } = Color.FromArgb(14, 20, 14);
    public Color InputUnderline { get; set; } = Color.FromArgb(0, 110, 0);

    // 5250 color palette (what an attribute byte's color maps to on screen).
    public Color Green { get; set; } = Color.FromArgb(0, 208, 0);
    public Color White { get; set; } = Color.FromArgb(235, 235, 235);
    public Color Red { get; set; } = Color.FromArgb(255, 100, 92);
    public Color Turquoise { get; set; } = Color.FromArgb(80, 210, 210);
    public Color Yellow { get; set; } = Color.FromArgb(222, 216, 92);
    public Color Pink { get; set; } = Color.FromArgb(240, 138, 208);
    public Color Blue { get; set; } = Color.FromArgb(128, 162, 255);

    public Color ColorFor(Field5250Color c) => c switch
    {
        Field5250Color.White => White,
        Field5250Color.Red => Red,
        Field5250Color.Turquoise => Turquoise,
        Field5250Color.Yellow => Yellow,
        Field5250Color.Pink => Pink,
        Field5250Color.Blue => Blue,
        _ => Green,
    };

    /// <summary>ACS-style full color. This is the default.</summary>
    public static ColorScheme Color5250 => new();

    /// <summary>Classic green monochrome (all attribute colors render green; white = bright).</summary>
    public static ColorScheme Classic => Mono(
        Color.FromArgb(0, 200, 0), Color.FromArgb(170, 255, 170), Color.FromArgb(10, 10, 10),
        Color.FromArgb(0, 255, 0), Color.FromArgb(0, 110, 0), Color.FromArgb(14, 20, 14));

    public static ColorScheme Amber => Mono(
        Color.FromArgb(255, 176, 0), Color.FromArgb(255, 224, 140), Color.FromArgb(12, 8, 4),
        Color.FromArgb(255, 200, 60), Color.FromArgb(150, 100, 0), Color.FromArgb(18, 14, 6));

    public static ColorScheme WhiteOnBlack => Mono(
        Color.FromArgb(200, 205, 210), Color.White, Color.FromArgb(16, 16, 20),
        Color.FromArgb(210, 215, 225), Color.FromArgb(80, 85, 92), Color.FromArgb(24, 24, 30));

    // Build a monochrome scheme: every 5250 color maps to `normal`, except White,
    // which maps to `bright` (the classic high-intensity look).
    private static ColorScheme Mono(Color normal, Color bright, Color bg, Color cursor, Color underline, Color inputBg)
        => new()
        {
            Background = bg,
            Normal = normal,
            HighIntensity = bright,
            CursorColor = cursor,
            InputUnderline = underline,
            InputFieldBackground = inputBg,
            ColumnSeparator = underline,
            StatusBarBackground = Color.FromArgb(Math.Min(bg.R + 14, 255), Math.Min(bg.G + 16, 255), Math.Min(bg.B + 14, 255)),
            StatusBarText = normal,
            Green = normal, White = bright, Red = normal, Turquoise = normal,
            Yellow = bright, Pink = normal, Blue = normal,
        };
}
