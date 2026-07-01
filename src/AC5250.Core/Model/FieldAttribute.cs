namespace AC5250.Model;

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

    // MDT - Modified Data Tag
    public bool IsModified { get; set; }

    public static FieldAttribute Decode(byte ffw1, byte ffw2, byte attr)
    {
        var fa = new FieldAttribute();

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
            7 => FieldFormat.AlphaShift, // reserved, default
            _ => FieldFormat.AlphaShift,
        };

        // Attribute byte - 5250 display characteristics
        // Low 3 bits (0-2) determine display mode:
        //   000 = Normal
        //   001 = Reverse image
        //   010 = High intensity
        //   011 = High intensity + Reverse
        //   100 = Underline
        //   101 = Underline + Reverse
        //   110 = High intensity + Underline
        //   111 = Non-display
        int mode = attr & 0x07;

        fa.IsNonDisplay = mode == 0x07;
        fa.IsReverse = (mode & 0x01) != 0 && !fa.IsNonDisplay;
        fa.IsHighIntensity = (mode & 0x02) != 0 && !fa.IsNonDisplay;
        fa.IsUnderline = (mode & 0x04) != 0 && !fa.IsNonDisplay;
        fa.IsColumnSeparator = (attr & 0x08) != 0;

        return fa;
    }
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
