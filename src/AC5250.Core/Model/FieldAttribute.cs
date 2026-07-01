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

        // Attribute byte - display characteristics
        // Bits 0-2: column separator / underline
        // Bits 3-6: highlight
        int highlight = (attr >> 1) & 0x07;

        fa.IsNonDisplay = highlight == 0x07;
        fa.IsHighIntensity = highlight == 0x02 || highlight == 0x04;
        fa.IsUnderline = (attr & 0x04) != 0;
        fa.IsColumnSeparator = (attr & 0x02) != 0;
        fa.IsReverse = highlight == 0x04;
        fa.IsBlink = highlight == 0x03;

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
