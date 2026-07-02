using AC5250.Model;

namespace AC5250.Input;

/// <summary>
/// High-level, UI-independent input action. Produced by the WinForms KeyMapper
/// (in AC5250.App) or synthesized directly by the MCP layer (in AC5250.Mcp),
/// then applied by <see cref="AC5250.Session.TerminalSession.HandleKeyAction"/>.
/// </summary>
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
