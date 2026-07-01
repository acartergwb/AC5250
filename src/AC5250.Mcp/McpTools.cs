using System.ComponentModel;
using ModelContextProtocol.Server;

namespace AC5250.Mcp;

/// <summary>
/// The MCP tool surface for controlling the AC5250 emulator. The same tools are
/// exposed by both hosts (in-process HTTP inside the WinForms app, and the
/// headless stdio server). Each method receives the shared
/// <see cref="EmulatorController"/> from dependency injection; the remaining
/// parameters are bound from the tool-call arguments.
/// </summary>
[McpServerToolType]
public static class McpTools
{
    [McpServerTool(Name = "list_sessions")]
    [Description("List all open 5250 terminal sessions with their id, title, host, connected state, and which one is active.")]
    public static IReadOnlyList<SessionSummary> ListSessions(EmulatorController controller)
        => controller.ListSessions();

    [McpServerTool(Name = "get_screen")]
    [Description("Return the current 5250 screen: a text grid (one line per row) to read, the cursor position, keyboard/status flags, and the list of input fields (with the index used by set_field). Non-display (password) fields are masked. Omit sessionId to use the active session.")]
    public static ScreenSnapshot GetScreen(
        EmulatorController controller,
        [Description("Session id to target; omit for the active session.")] string? sessionId = null)
        => controller.GetScreen(sessionId);

    [McpServerTool(Name = "connect")]
    [Description("Open a new TN5250 session to an IBM i / AS-400 host and wait for the first screen. SECURITY: this drives a live host session — do not connect to production systems without explicit authorization from the user.")]
    public static Task<ScreenSnapshot> Connect(
        EmulatorController controller,
        [Description("Host name or IP address of the IBM i system.")] string host,
        [Description("TCP port (default 23; use 992 for SSL).")] int port = 23,
        [Description("Use a TLS/SSL connection.")] bool ssl = false,
        [Description("Optional 5250 device name to request from the host.")] string? device = null,
        [Description("Use a wide 27x132 screen instead of the default 24x80.")] bool wide = false,
        CancellationToken ct = default)
        => controller.ConnectAsync(host, port, ssl, device, wide, 0, ct);

    [McpServerTool(Name = "disconnect")]
    [Description("Close a 5250 session. Omit sessionId to close the active session.")]
    public static string Disconnect(
        EmulatorController controller,
        [Description("Session id to close; omit for the active session.")] string? sessionId = null)
    {
        controller.Disconnect(sessionId);
        return "disconnected";
    }

    [McpServerTool(Name = "send_text")]
    [Description("Type text at the current cursor position into the current input field. This is a client-side edit only; it does NOT submit to the host. Follow with press_key (Enter or an Fn key) to send. Omit sessionId for the active session.")]
    public static ScreenSnapshot SendText(
        EmulatorController controller,
        [Description("The text to type at the cursor.")] string text,
        [Description("Session id to target; omit for the active session.")] string? sessionId = null)
        => controller.SendText(sessionId, text);

    [McpServerTool(Name = "set_field")]
    [Description("Set the value of an input field by its index (from get_screen's 'fields' list). Clears the field first by default and stops at the field boundary. Client-side only; follow with press_key to submit. Omit sessionId for the active session.")]
    public static ScreenSnapshot SetField(
        EmulatorController controller,
        [Description("Field index from get_screen's fields list.")] int fieldIndex,
        [Description("Value to place in the field.")] string value,
        [Description("Clear the field before typing (default true).")] bool clearFirst = true,
        [Description("Session id to target; omit for the active session.")] string? sessionId = null)
        => controller.SetField(sessionId, fieldIndex, value, clearFirst);

    [McpServerTool(Name = "press_key")]
    [Description("Press a 5250 key. AID keys submit the screen to the host: Enter, F1-F24, Clear, Attn, SysReq, Help, Print, PageUp, PageDown. Navigation/editing keys act client-side: Tab, BackTab, Home, End, Up, Down, Left, Right, Backspace, Delete, Insert, FieldExit, Reset, EraseInput. For AID keys the tool waits for the host to repaint before returning. Omit sessionId for the active session.")]
    public static Task<ScreenSnapshot> PressKey(
        EmulatorController controller,
        [Description("Key name, e.g. 'Enter', 'F3', 'Tab', 'PageDown'.")] string key,
        [Description("For AID keys, wait for the host to repaint the screen (default true).")] bool waitForChange = true,
        [Description("Session id to target; omit for the active session.")] string? sessionId = null,
        CancellationToken ct = default)
        => controller.PressKeyAsync(sessionId, key, waitForChange, 0, ct);
}
