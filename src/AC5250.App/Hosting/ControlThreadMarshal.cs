using AC5250.Mcp;

namespace AC5250.Hosting;

/// <summary>
/// <see cref="IThreadMarshal"/> that runs MCP work on the WinForms UI thread via
/// <see cref="Control.Invoke(Delegate)"/>. Because host-driven screen parsing is
/// also marshalled onto the UI thread (the session is created with the UI
/// <see cref="SynchronizationContext"/>), all screen access — from the keyboard,
/// the renderer, and MCP — is serialized on one thread.
/// </summary>
internal sealed class ControlThreadMarshal : IThreadMarshal
{
    private readonly Control _control;

    public ControlThreadMarshal(Control control) => _control = control;

    public T Invoke<T>(Func<T> func)
    {
        // During shutdown the handle may be gone; fall back to the caller thread.
        if (_control.IsDisposed || !_control.IsHandleCreated) return func();
        if (!_control.InvokeRequired) return func();
        return (T)_control.Invoke(func);
    }

    public void Invoke(Action action)
    {
        if (_control.IsDisposed || !_control.IsHandleCreated) { action(); return; }
        if (!_control.InvokeRequired) { action(); return; }
        _control.Invoke(action);
    }
}
