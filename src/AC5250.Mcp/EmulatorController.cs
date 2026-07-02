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

    private const int DefaultSettleMs = 5000;

    public EmulatorController(
        SessionManager sessions,
        IThreadMarshal marshal,
        Func<ConnectionSettings, TerminalSession> createSession)
    {
        _sessions = sessions;
        _marshal = marshal;
        _createSession = createSession;
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
