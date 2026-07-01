namespace AC5250.Model;

/// <summary>The seven 5250 display colors.</summary>
public enum Field5250Color
{
    Green,
    White,
    Red,
    Turquoise,
    Yellow,
    Pink,
    Blue,
}

public class FieldAttribute
{
    // From FFW byte 1
    public bool IsBypass { get; set; }          // Protected/output field
    public bool IsAutoEnter { get; set; }
    public bool IsMandatoryFill { get; set; }

    // From FFW byte 2
    public FieldFormat Format { get; set; } = FieldFormat.AlphaShift;

    // From attribute byte - display characteristics
    public bool IsNonDisplay { get; set; }
    public bool IsHighIntensity { get; set; }
    public bool IsUnderline { get; set; }
    public bool IsColumnSeparator { get; set; }
    public bool IsBlink { get; set; }
    public bool IsReverse { get; set; }
    public Field5250Color Color { get; set; } = Field5250Color.Green;

    /// <summary>The raw 5250 display-attribute byte (0x20-0x3F).</summary>
    public byte Raw { get; set; }

    // MDT - Modified Data Tag
    public bool IsModified { get; set; }

    public static FieldAttribute Decode(byte ffw1, byte ffw2, byte attr)
    {
        var fa = new FieldAttribute { Raw = attr };

        // FFW byte 1
        fa.IsBypass = (ffw1 & 0x20) != 0;
        fa.IsModified = (ffw1 & 0x08) != 0;
        fa.IsAutoEnter = (ffw2 & 0x80) != 0;
        fa.IsMandatoryFill = (ffw2 & 0x40) != 0;

        // FFW byte 2 - field format/shift
        int shift = ffw2 & 0x07;
        fa.Format = shift switch
        {
            0 => FieldFormat.AlphaShift,
            1 => FieldFormat.AlphaOnly,
            2 => FieldFormat.NumericShift,
            3 => FieldFormat.NumericOnly,
            5 => FieldFormat.DigitsOnly,
            6 => FieldFormat.SignedNumeric,
            _ => FieldFormat.AlphaShift,
        };

        var d = DecodeDisplay(attr);
        fa.Color = d.Color;
        fa.IsReverse = d.Reverse;
        fa.IsUnderline = d.Underline;
        fa.IsNonDisplay = d.NonDisplay;
        fa.IsColumnSeparator = d.ColumnSeparator;
        fa.IsBlink = d.Blink;
        fa.IsHighIntensity = d.Color == Field5250Color.White; // white == the old "high intensity"

        return fa;
    }

    /// <summary>
    /// Decode a 5250 display-attribute byte (the standard 0x20-0x3F table used by
    /// color-capable 5250 displays, e.g. 3179-2 / 3477-FC). This is the mapping
    /// IBM ACS uses to color the screen.
    /// </summary>
    public static DisplayAttr DecodeDisplay(byte attr) => attr switch
    {
        0x20 => new(Field5250Color.Green,     false, false, false, false, false),
        0x21 => new(Field5250Color.Green,     true,  false, false, false, false),
        0x22 => new(Field5250Color.White,     false, false, false, false, false),
        0x23 => new(Field5250Color.White,     true,  false, false, false, false),
        0x24 => new(Field5250Color.Green,     false, true,  false, false, false),
        0x25 => new(Field5250Color.Green,     true,  true,  false, false, false),
        0x26 => new(Field5250Color.White,     false, true,  false, false, false),
        0x27 => new(Field5250Color.Green,     false, false, true,  false, false), // non-display
        0x28 => new(Field5250Color.Red,       false, false, false, false, false),
        0x29 => new(Field5250Color.Red,       true,  false, false, false, false),
        0x2A => new(Field5250Color.Red,       false, false, false, false, true),
        0x2B => new(Field5250Color.Red,       true,  false, false, false, true),
        0x2C => new(Field5250Color.Red,       false, true,  false, false, false),
        0x2D => new(Field5250Color.Red,       true,  true,  false, false, false),
        0x2E => new(Field5250Color.Red,       false, true,  false, false, true),
        0x2F => new(Field5250Color.Red,       false, false, true,  false, false), // non-display
        0x30 => new(Field5250Color.Turquoise, false, false, false, true,  false),
        0x31 => new(Field5250Color.Turquoise, true,  false, false, true,  false),
        0x32 => new(Field5250Color.Yellow,    false, false, false, true,  false),
        0x33 => new(Field5250Color.Yellow,    true,  false, false, true,  false),
        0x34 => new(Field5250Color.Turquoise, false, true,  false, false, false),
        0x35 => new(Field5250Color.Turquoise, true,  true,  false, false, false),
        0x36 => new(Field5250Color.Yellow,    false, true,  false, false, false),
        0x37 => new(Field5250Color.Yellow,    false, false, true,  false, false), // non-display
        0x38 => new(Field5250Color.Pink,      false, false, false, false, false),
        0x39 => new(Field5250Color.Pink,      true,  false, false, false, false),
        0x3A => new(Field5250Color.Blue,      false, false, false, false, false),
        0x3B => new(Field5250Color.Blue,      true,  false, false, false, false),
        0x3C => new(Field5250Color.Pink,      false, true,  false, false, false),
        0x3D => new(Field5250Color.Pink,      true,  true,  false, false, false),
        0x3E => new(Field5250Color.Blue,      false, true,  false, false, false),
        0x3F => new(Field5250Color.Blue,      false, false, true,  false, false), // non-display
        _    => new(Field5250Color.Green,     false, false, false, false, false),
    };

    public readonly record struct DisplayAttr(
        Field5250Color Color, bool Reverse, bool Underline, bool NonDisplay, bool ColumnSeparator, bool Blink);
}

public enum FieldFormat
{
    AlphaShift,
    AlphaOnly,
    NumericShift,
    NumericOnly,
    DigitsOnly,
    SignedNumeric,
}
