using AC5250.Session;
using ModelContextProtocol;

namespace AC5250.Mcp;

/// <summary>
/// Host-agnostic core of the MCP integration. Both the in-process (WinForms) host
/// and the headless host construct one of these with:
///   - the shared <see cref="SessionManager"/>,
///   - an <see cref="IThreadMarshal"/> that runs work on the session-owning thread, and
///   - a factory that creates a session wired to that same thread's dispatcher.
/// The MCP tool methods (see <see cref="McpTools"/>) are thin wrappers over this.
/// </summary>
public sealed class EmulatorController
{
    private readonly SessionManager _sessions;
    private readonly IThreadMarshal _marshal;
    private readonly Func<ConnectionSettings, TerminalSession> _createSession;

    // Resolves saved sign-on credentials for a session, or null if none. Supplied by the
    // host (the WinForms app reads them from Windows Credential Manager). Kept as a
    // delegate so the password is never a tool parameter and never enters the MCP layer
    // except transiently to fill the field locally.
    private readonly Func<ConnectionSettings, (string User, string Password)?>? _credentials;

    private const int DefaultSettleMs = 5000;

    public EmulatorController(
        SessionManager sessions,
        IThreadMarshal marshal,
        Func<ConnectionSettings, TerminalSession> createSession,
        Func<ConnectionSettings, (string User, string Password)?>? credentials = null)
    {
        _sessions = sessions;
        _marshal = marshal;
        _createSession = createSession;
        _credentials = credentials;
    }

    public IReadOnlyList<SessionSummary> ListSessions() =>
        _marshal.Invoke(() => (IReadOnlyList<SessionSummary>)_sessions.Sessions
            .Select(s => new SessionSummary(
                s.Id, s.Title, s.Settings.HostName, s.Settings.Port,
                s.IsConnected, ReferenceEquals(s, _sessions.ActiveSession)))
            .ToList());

    public async Task<ScreenSnapshot> ConnectAsync(
        string host, int port, bool ssl, string? device, bool wide, int settleMs, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new McpException("host is required.");

        var settings = new ConnectionSettings
        {
            HostName = host.Trim(),
            Port = port <= 0 ? 23 : port,
            UseSsl = ssl,
            DeviceName = device?.Trim() ?? "",
            ScreenSize = wide ? ScreenSize.Wide : ScreenSize.Normal,
        };

        var session = _marshal.Invoke(() => _createSession(settings));
        long baseline = _marshal.Invoke(() => session.Screen.Version);

        await session.ConnectAsync();
        await WaitForSettleAsync(session, baseline, settleMs <= 0 ? DefaultSettleMs : settleMs, ct);
        return Snapshot(session);
    }

    public void Disconnect(string? sessionId)
    {
        var s = Resolve(sessionId);
        _marshal.Invoke(() => _sessions.CloseSession(s));
    }

    public ScreenSnapshot GetScreen(string? sessionId) => Snapshot(Resolve(sessionId));

    public ScreenSnapshot SendText(string? sessionId, string text)
    {
        var s = Resolve(sessionId);
        _marshal.Invoke(() => s.TypeString(text ?? ""));
        return Snapshot(s);
    }

    public ScreenSnapshot SetField(string? sessionId, int fieldIndex, string value, bool clearFirst)
    {
        var s = Resolve(sessionId);
        bool ok = _marshal.Invoke(() => s.SetFieldValue(fieldIndex, value ?? "", clearFirst));
        if (!ok)
            throw new McpException(
                $"Could not set field {fieldIndex}: keyboard inhibited, index out of range, or a protected field. Call get_screen to see current fields.");
        return Snapshot(s);
    }

    public async Task<ScreenSnapshot> PressKeyAsync(
        string? sessionId, string key, bool waitForChange, int timeoutMs, CancellationToken ct)
    {
        var s = Resolve(sessionId);
        if (!KeyNames.TryParse(key, out var action))
            throw new McpException($"Unknown key '{key}'. Valid keys: {KeyNames.HelpList}.");

        long baseline = _marshal.Invoke(() => s.Screen.Version);
        _marshal.Invoke(() => s.HandleKeyAction(action));

        if (waitForChange && KeyNames.IsAidKey(action))
            await WaitForSettleAsync(s, baseline, timeoutMs <= 0 ? DefaultSettleMs : timeoutMs, ct);

        return Snapshot(s);
    }

    /// <summary>
    /// Sign on to the current session using the saved credentials for its host. Fills the
    /// user and password fields locally and presses Enter. The password is read from the
    /// OS credential store on this machine and is never accepted as a parameter nor
    /// returned — the resulting snapshot masks the (non-display) password field.
    /// </summary>
    public async Task<ScreenSnapshot> SignOnAsync(string? sessionId, int settleMs, CancellationToken ct)
    {
        var s = Resolve(sessionId);

        if (_credentials is null)
            throw new McpException("Credential sign-on is not available in this host.");
        var creds = _credentials(s.Settings);
        if (creds is null)
            throw new McpException(
                $"No saved credentials for host '{s.Settings.HostName}'. Add them in the emulator: Session > Manage Saved Credentials.");

        var (userIdx, pwIdx) = _marshal.Invoke(() => FindSignOnFields(s));
        if (userIdx < 0 || pwIdx < 0)
            throw new McpException("The current screen is not a sign-on screen (no visible user field + hidden password field found).");

        var (user, password) = creds.Value;
        bool filled = _marshal.Invoke(() =>
            s.SetFieldValue(userIdx, user, true) && s.SetFieldValue(pwIdx, password, true));
        if (!filled)
            throw new McpException("Could not fill the sign-on fields (keyboard inhibited?).");

        if (!KeyNames.TryParse("Enter", out var enter))
            throw new McpException("internal: Enter key unavailable.");
        long baseline = _marshal.Invoke(() => s.Screen.Version);
        _marshal.Invoke(() => s.HandleKeyAction(enter));
        await WaitForSettleAsync(s, baseline, settleMs <= 0 ? DefaultSettleMs : settleMs, ct);
        return Snapshot(s);
    }

    /// <summary>Locate the sign-on user field (first visible, non-protected input) and
    /// password field (first non-display input) by index into the screen's field list.</summary>
    private static (int userIdx, int pwIdx) FindSignOnFields(TerminalSession s)
    {
        var fields = s.Screen.Fields;
        int userIdx = -1, pwIdx = -1;
        for (int i = 0; i < fields.Count; i++)
        {
            var a = fields[i].Attribute;
            if (a.IsBypass) continue;
            if (a.IsNonDisplay) { if (pwIdx < 0) pwIdx = i; }
            else if (userIdx < 0) userIdx = i;
        }
        return (userIdx, pwIdx);
    }

    // --- helpers ---

    private TerminalSession Resolve(string? sessionId)
    {
        var s = _marshal.Invoke(() =>
            string.IsNullOrWhiteSpace(sessionId) ? _sessions.ActiveSession : _sessions.FindById(sessionId!));
        if (s == null)
            throw new McpException(string.IsNullOrWhiteSpace(sessionId)
                ? "No active session. Use 'connect' first (or open one in the emulator window)."
                : $"No session with id '{sessionId}'. Use 'list_sessions' to see open sessions.");
        return s;
    }

    private ScreenSnapshot Snapshot(TerminalSession s) => _marshal.Invoke(() => ScreenSerializer.Capture(s));

    /// <summary>
    /// Wait for the host to finish repainting after an AID key. After a submit the
    /// keyboard is inhibited; the host reply advances the screen version and normally
    /// re-enables input. We treat the screen as settled once the version has advanced
    /// past <paramref name="baseline"/> and either input is re-enabled or the screen
    /// has been quiet for a short spell (e.g. an error message that keeps input locked).
    /// </summary>
    private async Task WaitForSettleAsync(TerminalSession s, long baseline, int timeoutMs, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long lastVer = baseline;
        long? quietSince = null;

        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(40, ct);
            var (ver, inhibited) = _marshal.Invoke(() => (s.Screen.Version, s.Screen.InputInhibited));

            if (ver != lastVer) { lastVer = ver; quietSince = null; }

            if (ver > baseline)
            {
                if (!inhibited) return;                    // reply landed and input is enabled
                quietSince ??= sw.ElapsedMilliseconds;
                if (sw.ElapsedMilliseconds - quietSince > 300) return; // painted but still locked
            }
        }
        // Timed out: return whatever is on screen now.
    }
}
