using AC5250.Input;
using AC5250.Model;

namespace AC5250.Mcp;

/// <summary>
/// Parses a UI-independent key name (e.g. "Enter", "F3", "PageDown", "Tab")
/// into a <see cref="KeyAction"/>. This is the headless/MCP counterpart to the
/// WinForms <c>KeyMapper</c> — it maps from strings instead of <c>System.Windows.Forms.Keys</c>.
/// </summary>
public static class KeyNames
{
    public static bool TryParse(string name, out KeyAction action)
    {
        action = KeyAction.Ignored;
        if (string.IsNullOrWhiteSpace(name)) return false;

        string k = name.Trim().ToLowerInvariant()
            .Replace("-", "").Replace("_", "").Replace(" ", "");

        // F1..F24
        if (k.Length >= 2 && k[0] == 'f' && int.TryParse(k.AsSpan(1), out int fn) && fn is >= 1 and <= 24)
        {
            action = KeyAction.FromAidKey(Enum.Parse<AidKey>("F" + fn));
            return true;
        }

        switch (k)
        {
            // AID keys (submit the screen to the host)
            case "enter": action = KeyAction.FromAidKey(AidKey.Enter); return true;
            case "clear": action = KeyAction.FromAidKey(AidKey.Clear); return true;
            case "help": action = KeyAction.FromAidKey(AidKey.Help); return true;
            case "print": action = KeyAction.FromAidKey(AidKey.Print); return true;
            case "attn": case "attention": action = KeyAction.FromAidKey(AidKey.Attn); return true;
            case "sysreq": case "systemrequest": action = KeyAction.FromAidKey(AidKey.SysReq); return true;
            case "pageup": case "rolldown": action = KeyAction.FromAidKey(AidKey.PageUp); return true;
            case "pagedown": case "rollup": action = KeyAction.FromAidKey(AidKey.PageDown); return true;

            // Navigation / editing (client-side)
            case "tab": action = KeyAction.FromType(KeyActionType.Tab); return true;
            case "backtab": case "shifttab": action = KeyAction.FromType(KeyActionType.BackTab); return true;
            case "home": action = KeyAction.FromType(KeyActionType.Home); return true;
            case "end": action = KeyAction.FromType(KeyActionType.End); return true;
            case "up": case "arrowup": action = KeyAction.FromType(KeyActionType.ArrowUp); return true;
            case "down": case "arrowdown": action = KeyAction.FromType(KeyActionType.ArrowDown); return true;
            case "left": case "arrowleft": action = KeyAction.FromType(KeyActionType.ArrowLeft); return true;
            case "right": case "arrowright": action = KeyAction.FromType(KeyActionType.ArrowRight); return true;
            case "backspace": action = KeyAction.FromType(KeyActionType.Backspace); return true;
            case "delete": case "del": action = KeyAction.FromType(KeyActionType.Delete); return true;
            case "insert": case "ins": action = KeyAction.FromType(KeyActionType.Insert); return true;
            case "fieldexit": action = KeyAction.FromType(KeyActionType.FieldExit); return true;
            case "reset": action = KeyAction.FromType(KeyActionType.Reset); return true;
            case "eraseinput": action = KeyAction.FromType(KeyActionType.EraseInput); return true;

            default: return false;
        }
    }

    /// <summary>True if the action is an AID key (one that transmits to the host).</summary>
    public static bool IsAidKey(KeyAction a) => a.Type == KeyActionType.AidKey;

    /// <summary>Human-readable list of accepted key names, for error messages.</summary>
    public const string HelpList =
        "Enter, F1-F24, Clear, Attn, SysReq, Help, Print, PageUp, PageDown, " +
        "Tab, BackTab, Home, End, Up, Down, Left, Right, Backspace, Delete, " +
        "Insert, FieldExit, Reset, EraseInput";
}
