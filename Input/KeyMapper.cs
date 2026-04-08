using AC5250.Model;

namespace AC5250.Input;

public enum KeyActionType
{
    None,
    AidKey,
    Character,
    FieldExit,
    FieldPlus,
    FieldMinus,
    Backspace,
    Delete,
    Home,
    End,
    Tab,
    BackTab,
    ArrowUp,
    ArrowDown,
    ArrowLeft,
    ArrowRight,
    Insert,
    Reset,
    EraseInput,
    DupKey,
}

public readonly struct KeyAction
{
    public KeyActionType Type { get; }
    public AidKey Aid { get; }
    public char Character { get; }

    private KeyAction(KeyActionType type, AidKey aid = AidKey.None, char ch = '\0')
    {
        Type = type;
        Aid = aid;
        Character = ch;
    }

    public static KeyAction FromAidKey(AidKey aid) => new(KeyActionType.AidKey, aid);
    public static KeyAction FromChar(char ch) => new(KeyActionType.Character, ch: ch);
    public static KeyAction FromType(KeyActionType type) => new(type);
    public static readonly KeyAction Ignored = new(KeyActionType.None);
}

public static class KeyMapper
{
    public static KeyAction Map(Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        bool shift = (keyData & Keys.Shift) != 0;
        bool ctrl = (keyData & Keys.Control) != 0;
        bool alt = (keyData & Keys.Alt) != 0;

        // Function keys
        if (key >= Keys.F1 && key <= Keys.F12)
        {
            int fNum = key - Keys.F1 + 1;
            if (shift) fNum += 12; // F13-F24

            AidKey aid = fNum switch
            {
                1 => AidKey.F1, 2 => AidKey.F2, 3 => AidKey.F3, 4 => AidKey.F4,
                5 => AidKey.F5, 6 => AidKey.F6, 7 => AidKey.F7, 8 => AidKey.F8,
                9 => AidKey.F9, 10 => AidKey.F10, 11 => AidKey.F11, 12 => AidKey.F12,
                13 => AidKey.F13, 14 => AidKey.F14, 15 => AidKey.F15, 16 => AidKey.F16,
                17 => AidKey.F17, 18 => AidKey.F18, 19 => AidKey.F19, 20 => AidKey.F20,
                21 => AidKey.F21, 22 => AidKey.F22, 23 => AidKey.F23, 24 => AidKey.F24,
                _ => AidKey.None
            };
            return aid != AidKey.None ? KeyAction.FromAidKey(aid) : KeyAction.Ignored;
        }

        return key switch
        {
            Keys.Enter => KeyAction.FromAidKey(AidKey.Enter),
            Keys.PageUp => KeyAction.FromAidKey(AidKey.PageUp),
            Keys.PageDown => KeyAction.FromAidKey(AidKey.PageDown),
            Keys.Escape when shift => KeyAction.FromAidKey(AidKey.SysReq),
            Keys.Escape => KeyAction.FromAidKey(AidKey.Attn),
            Keys.Pause => KeyAction.FromAidKey(AidKey.Clear),

            Keys.Tab when shift => KeyAction.FromType(KeyActionType.BackTab),
            Keys.Tab => KeyAction.FromType(KeyActionType.Tab),

            Keys.Up => KeyAction.FromType(KeyActionType.ArrowUp),
            Keys.Down => KeyAction.FromType(KeyActionType.ArrowDown),
            Keys.Left => KeyAction.FromType(KeyActionType.ArrowLeft),
            Keys.Right => KeyAction.FromType(KeyActionType.ArrowRight),

            Keys.Back => KeyAction.FromType(KeyActionType.Backspace),
            Keys.Delete => KeyAction.FromType(KeyActionType.Delete),
            Keys.Home => KeyAction.FromType(KeyActionType.Home),
            Keys.End => KeyAction.FromType(KeyActionType.End),
            Keys.Insert => KeyAction.FromType(KeyActionType.Insert),

            Keys.Add when !shift => KeyAction.FromType(KeyActionType.FieldPlus),      // numpad +
            Keys.Subtract => KeyAction.FromType(KeyActionType.FieldMinus),              // numpad -

            // Ctrl combos
            Keys.R when ctrl => KeyAction.FromType(KeyActionType.Reset),
            Keys.A when ctrl => KeyAction.FromAidKey(AidKey.Attn),
            Keys.E when ctrl => KeyAction.FromType(KeyActionType.EraseInput),
            Keys.D when ctrl => KeyAction.FromType(KeyActionType.DupKey),
            Keys.H when ctrl => KeyAction.FromAidKey(AidKey.Help),
            Keys.P when ctrl => KeyAction.FromAidKey(AidKey.Print),

            // Printable characters (A-Z, 0-9, symbols) - let OnKeyPress handle these
            _ when !ctrl && !alt && key >= Keys.Space && key <= Keys.OemBackslash => MapPrintable(keyData),

            _ => KeyAction.Ignored,
        };
    }

    private static KeyAction MapPrintable(Keys keyData)
    {
        // We'll handle actual character mapping via OnKeyPress in the control
        // For now, mark it as needing character processing
        return KeyAction.Ignored; // will be handled by character event
    }

    public static KeyAction MapChar(char ch)
    {
        if (ch >= ' ' && ch <= '~')
            return KeyAction.FromChar(ch);
        return KeyAction.Ignored;
    }
}
