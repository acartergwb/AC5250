namespace AC5250.Session;

public class SessionManager
{
    private readonly List<TerminalSession> _sessions = new();

    public IReadOnlyList<TerminalSession> Sessions => _sessions;
    public TerminalSession? ActiveSession { get; private set; }

    public event Action<TerminalSession>? SessionAdded;
    public event Action<TerminalSession>? SessionRemoved;
    public event Action<TerminalSession?>? ActiveSessionChanged;

    public TerminalSession CreateSession(ConnectionSettings settings)
    {
        var session = new TerminalSession(settings);
        _sessions.Add(session);
        SessionAdded?.Invoke(session);

        if (ActiveSession == null)
            SetActive(session);

        return session;
    }

    public void CloseSession(TerminalSession session)
    {
        session.Disconnect();
        _sessions.Remove(session);
        SessionRemoved?.Invoke(session);

        if (ActiveSession == session)
        {
            ActiveSession = _sessions.Count > 0 ? _sessions[0] : null;
            ActiveSessionChanged?.Invoke(ActiveSession);
        }

        session.Dispose();
    }

    public void SetActive(TerminalSession session)
    {
        if (_sessions.Contains(session))
        {
            ActiveSession = session;
            ActiveSessionChanged?.Invoke(session);
        }
    }

    public void CloseAll()
    {
        foreach (var session in _sessions.ToList())
        {
            session.Disconnect();
            session.Dispose();
        }
        _sessions.Clear();
        ActiveSession = null;
    }
}
