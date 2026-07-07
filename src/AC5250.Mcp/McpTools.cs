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
    [Description("Return the current 5250 screen: a text grid (one line per row) to read, the cursor position, keyboard/status flags, and the list of input fields (with the index used by set_field). Non-display (password) fields are masked. This is an instantaneous read — if keyboardInhibited is true the host is still working ('X SYSTEM'); use wait_for_screen to block until it is ready. Omit sessionId to use the active session.")]
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

    [McpServerTool(Name = "signon")]
    [Description("Sign on to the current session using credentials the user saved on this machine (Windows Credential Manager) for the session's host. Fills the user and password fields locally and presses Enter. You do NOT provide the password — it is read from the OS vault and never exposed. A host can have several saved logins under short labels; omit credentialLabel to use the host's default (or its only login), or pass a label to pick a specific one. If the label is missing, the error lists the labels that exist. Use this when the screen is an IBM i sign-on prompt. Omit sessionId for the active session.")]
    public static Task<ScreenSnapshot> SignOn(
        EmulatorController controller,
        [Description("Credential label to use (e.g. 'ADMIN'); omit for the host's default login.")] string? credentialLabel = null,
        [Description("Session id to sign on; omit for the active session.")] string? sessionId = null,
        CancellationToken ct = default)
        => controller.SignOnAsync(sessionId, credentialLabel, 0, ct);

    [McpServerTool(Name = "press_key")]
    [Description("Press a 5250 key. AID keys submit the screen to the host: Enter, F1-F24, Clear, Attn, SysReq, Help, Print, PageUp, PageDown. Navigation/editing keys act client-side: Tab, BackTab, Home, End, Up, Down, Left, Right, Backspace, Delete, Insert, FieldExit, Reset, EraseInput. For AID keys the tool BLOCKS until the host finishes working and re-invites input (the keyboard stays locked, 'X SYSTEM', while it works) — so the returned screen is the host's real response, not an intermediate paint. If the wait times out, the returned screen may still have keyboardInhibited=true; call wait_for_screen or press_key again. Omit sessionId for the active session.")]
    public static Task<ScreenSnapshot> PressKey(
        EmulatorController controller,
        [Description("Key name, e.g. 'Enter', 'F3', 'Tab', 'PageDown'.")] string key,
        [Description("For AID keys, block until the host finishes and re-invites input before returning (default true).")] bool waitForChange = true,
        [Description("For AID keys, max ms to wait for the host (default 30000). The wait ends as soon as the host re-enables input; raise it for a known long-running operation.")] int timeoutMs = 0,
        [Description("Session id to target; omit for the active session.")] string? sessionId = null,
        CancellationToken ct = default)
        => controller.PressKeyAsync(sessionId, key, waitForChange, timeoutMs, ct);

    [McpServerTool(Name = "wait_for_screen")]
    [Description("Block until the host has finished working and the screen is ready for input again, or until timeoutMs elapses. Use this after starting a long-running host operation, or to wait for a specific screen to appear. Unlike get_screen (an instant read), this keeps polling while the keyboard is inhibited ('X SYSTEM'). Returns as soon as the keyboard is re-enabled and the screen has settled — and, if containsText is given, only once that text is present. If it returns on timeout the screen may still show keyboardInhibited=true (still busy); call it again. Omit sessionId for the active session.")]
    public static Task<ScreenSnapshot> WaitForScreen(
        EmulatorController controller,
        [Description("Optional text that must appear on the screen before returning (case-insensitive).")] string? containsText = null,
        [Description("Max ms to wait (default 30000).")] int timeoutMs = 0,
        [Description("Session id to target; omit for the active session.")] string? sessionId = null,
        CancellationToken ct = default)
        => controller.WaitForScreenAsync(sessionId, containsText, timeoutMs, ct);
}
