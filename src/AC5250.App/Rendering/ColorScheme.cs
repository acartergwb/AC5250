namespace AC5250.Rendering;

public class ColorScheme
{
    public Color Background { get; set; } = Color.FromArgb(10, 10, 10);
    public Color Normal { get; set; } = Color.FromArgb(0, 200, 0);
    public Color HighIntensity { get; set; } = Color.White;
    public Color Underline { get; set; } = Color.FromArgb(0, 200, 0);
    public Color ColumnSeparator { get; set; } = Color.FromArgb(0, 100, 0);
    public Color NonDisplay { get; set; } = Color.FromArgb(10, 10, 10);
    public Color Reverse { get; set; } = Color.FromArgb(0, 200, 0);
    public Color StatusBarBackground { get; set; } = Color.FromArgb(24, 26, 24);
    public Color StatusBarText { get; set; } = Color.FromArgb(0, 180, 0);
    public Color CursorColor { get; set; } = Color.FromArgb(0, 255, 0);
    public Color FieldAttributeMarker { get; set; } = Color.FromArgb(0, 40, 0);
    public Color InputFieldBackground { get; set; } = Color.FromArgb(12, 18, 12);

    public static ColorScheme Classic => new();

    public static ColorScheme WhiteOnBlack => new()
    {
        Background = Color.FromArgb(16, 16, 20),
        Normal = Color.FromArgb(190, 195, 200),
        HighIntensity = Color.White,
        Underline = Color.FromArgb(190, 195, 200),
        ColumnSeparator = Color.FromArgb(80, 85, 90),
        NonDisplay = Color.FromArgb(16, 16, 20),
        Reverse = Color.FromArgb(190, 195, 200),
        StatusBarBackground = Color.FromArgb(24, 24, 28),
        StatusBarText = Color.FromArgb(160, 165, 170),
        CursorColor = Color.FromArgb(200, 210, 220),
        FieldAttributeMarker = Color.FromArgb(40, 40, 45),
        InputFieldBackground = Color.FromArgb(22, 22, 28),
    };

    public static ColorScheme Amber => new()
    {
        Background = Color.FromArgb(12, 8, 4),
        Normal = Color.FromArgb(255, 176, 0),
        HighIntensity = Color.FromArgb(255, 220, 120),
        Underline = Color.FromArgb(255, 176, 0),
        ColumnSeparator = Color.FromArgb(140, 96, 0),
        NonDisplay = Color.FromArgb(12, 8, 4),
        Reverse = Color.FromArgb(255, 176, 0),
        StatusBarBackground = Color.FromArgb(24, 18, 8),
        StatusBarText = Color.FromArgb(200, 140, 0),
        CursorColor = Color.FromArgb(255, 200, 60),
        FieldAttributeMarker = Color.FromArgb(50, 35, 0),
        InputFieldBackground = Color.FromArgb(18, 14, 6),
    };
}
