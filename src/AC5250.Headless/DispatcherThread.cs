using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using AC5250.Mcp;

namespace AC5250.Headless;

/// <summary>
/// A dedicated single thread that owns the emulator's screen state in the
/// headless host (the analogue of the WinForms UI thread in the desktop app).
/// It installs its own <see cref="SynchronizationContext"/> and pumps a work
/// queue, so both the data-stream parser (posted by the session's dispatcher)
/// and the MCP tool operations (marshalled here via <see cref="IThreadMarshal"/>)
/// run serially on this one thread — no locking required, no races.
/// </summary>
internal sealed class DispatcherThread : IThreadMarshal, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback cb, object? state)> _queue = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private QueueSyncContext? _ctx;

    public DispatcherThread()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "AC5250-dispatcher" };
        _thread.Start();
        _ready.Wait();
    }

    /// <summary>The context to hand to <c>TerminalSession</c> so it marshals parsing here.</summary>
    public SynchronizationContext Context => _ctx!;

    private void Run()
    {
        _ctx = new QueueSyncContext(_queue);
        SynchronizationContext.SetSynchronizationContext(_ctx);
        _ready.Set();
        foreach (var (cb, state) in _queue.GetConsumingEnumerable())
        {
            // A faulted callback must not tear down the pump.
            try { cb(state); } catch { /* ignore */ }
        }
    }

    public T Invoke<T>(Func<T> func)
    {
        if (SynchronizationContext.Current == _ctx)
            return func();

        T result = default!;
        ExceptionDispatchInfo? error = null;
        using var done = new ManualResetEventSlim(false);
        _ctx!.Post(_ =>
        {
            try { result = func(); }
            catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
            finally { done.Set(); }
        }, null);
        done.Wait();
        error?.Throw();
        return result;
    }

    public void Invoke(Action action) => Invoke<object?>(() => { action(); return null; });

    public void Dispose() => _queue.CompleteAdding();
}

/// <summary>A <see cref="SynchronizationContext"/> that dispatches to a work queue.</summary>
internal sealed class QueueSyncContext : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback, object?)> _queue;

    public QueueSyncContext(BlockingCollection<(SendOrPostCallback, object?)> queue) => _queue = queue;

    public override void Post(SendOrPostCallback d, object? state)
    {
        try { _queue.Add((d, state)); }
        catch (InvalidOperationException) { /* queue completed during shutdown */ }
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (Current == this) { d(state); return; }

        ExceptionDispatchInfo? error = null;
        using var done = new ManualResetEventSlim(false);
        Post(_ =>
        {
            try { d(state); }
            catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
            finally { done.Set(); }
        }, null);
        done.Wait();
        error?.Throw();
    }
}
