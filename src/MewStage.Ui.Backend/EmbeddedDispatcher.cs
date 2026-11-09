// EmbeddedDispatcher: the dispatcher of an embedded host's UI thread. MewUI defers work to a dispatcher: DispatcherTimer
// ticks (TextBox caret blink, RepeatButton auto-repeat, the ScrollViewer idle timer, key-chord timeouts, toast expiry,
// long press), BeginInvoke work (deferred Tab focus into ListBox / TreeView / GridView rows, the hover re-check after a
// scroll) and the merged layout, render and command-state passes. MewUI's own platform hosts get a dispatcher from the
// running Application; an embedded host runs none, so vendor patch multi-host/0003 lets a host bind one to its thread
// (Application.ThreadDispatcher). This is upstream's managed dispatcher used the way the Browser and Headless hosts use
// it: no OS event source, pumped by the host once per frame (EmbeddedUiHost.PumpDispatcher), due timers run inline.
//
// Its clock is the frame's. A frame given an explicit timestamp (the tests' fixed timestamps) moves timers and
// animations together; a frame without one runs on the wall clock.
//
// One per UI thread, shared by every host on it (a main window's host and its secondary windows' hosts) and counted:
// the first host on a thread creates and binds it, and installs it as the thread's SynchronizationContext when the
// thread has none, so an await in a UI handler resumes on the UI thread. The last host disposed unbinds it and drops
// it, so work a disposed host's windows left queued never runs on a later host's frames.

using System.Diagnostics;

using Aprillz.MewUI;
using Aprillz.MewUI.Platform;

namespace MewStage.Ui.Backend;

internal sealed class EmbeddedDispatcher : ManagedUiDispatcher
{
    private long _frameTimestamp;
    private bool _installedContext;

    private EmbeddedDispatcher()
    {
    }

    /// <summary>The dispatcher bound to the calling thread, or null when no host runs on it.</summary>
    [field: ThreadStatic]
    internal static EmbeddedDispatcher? ForThread { get; private set; }

    /// <summary>Hosts on this thread holding the dispatcher.</summary>
    internal int HostCount { get; private set; }

    protected override int MaxPumpIterations => 8;

    /// <summary>The frame's timestamp (Stopwatch ticks) when the host was given one, else the wall clock.</summary>
    protected override long GetTimestamp()
    {
        return _frameTimestamp != 0 ? _frameTimestamp : Stopwatch.GetTimestamp();
    }

    protected override int NoTimerPollTimeout(int maxMs)
    {
        return maxMs;
    }

    protected override void DispatchDueTimer(Action action)
    {
        action();
    }

    /// <summary>The calling thread's dispatcher, created and bound by the first host on the thread.</summary>
    internal static EmbeddedDispatcher Acquire()
    {
        EmbeddedDispatcher? dispatcher = ForThread;
        if (dispatcher is null)
        {
            dispatcher = new EmbeddedDispatcher();
            ForThread = dispatcher;
            Application.ThreadDispatcher = dispatcher;
            if (Current is null)   // the thread's SynchronizationContext: install ours only where none is set
            {
                SetSynchronizationContext(dispatcher);
                dispatcher._installedContext = true;
            }
        }

        dispatcher.HostCount++;
        return dispatcher;
    }

    /// <summary>A host on this thread is done with the dispatcher; the last one unbinds it. A release from a thread the
    /// dispatcher is not bound to only drops the count (the binding is per thread).</summary>
    internal void Release()
    {
        if (HostCount > 0)
        {
            HostCount--;
        }

        if (HostCount > 0 || !ReferenceEquals(ForThread, this))
        {
            return;
        }

        ForThread = null;
        if (ReferenceEquals(Application.ThreadDispatcher, this))
        {
            Application.ThreadDispatcher = null;
        }

        if (_installedContext && ReferenceEquals(Current, this))
        {
            SetSynchronizationContext(null);
        }
    }

    /// <summary>Set the clock the timers read until the next frame: <paramref name="timestamp"/> in Stopwatch ticks, or
    /// 0 for the wall clock.</summary>
    internal void SetFrameTimestamp(long timestamp)
    {
        _frameTimestamp = timestamp;
    }
}
