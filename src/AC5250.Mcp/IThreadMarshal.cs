namespace AC5250.Mcp;

/// <summary>
/// Marshals work onto the thread that owns the <c>ScreenBuffer</c> /
/// <c>TerminalSession</c> — the WinForms UI thread in the desktop host, or a
/// dedicated single-thread dispatcher in the headless host.
///
/// Every read or mutation of session/screen state performed by an MCP tool goes
/// through here, so it is serialized with the host-driven data-stream parser
/// (which the session marshals onto the same thread via its dispatcher). This is
/// what makes concurrent MCP access to the emulator thread-safe.
/// </summary>
public interface IThreadMarshal
{
    /// <summary>Run <paramref name="func"/> on the owning thread and return its result.</summary>
    T Invoke<T>(Func<T> func);

    /// <summary>Run <paramref name="action"/> on the owning thread and wait for completion.</summary>
    void Invoke(Action action);
}
