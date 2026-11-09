namespace Aprillz.MewUI;

public partial class Window
{
    private const long PENDING_KEY_SEQUENCE_TIMEOUT_MS = 5000;

    private static readonly MewPropertyKey<KeySequence?> PendingKeySequencePropertyKey =
        MewProperty<KeySequence?>.RegisterReadOnly<Window>(nameof(PendingKeySequence), null, MewPropertyOptions.None);

    public static readonly MewProperty<KeySequence?> PendingKeySequenceProperty = PendingKeySequencePropertyKey.Property;

    private DispatcherTimer? _pendingKeySequenceTimer;
    private long _pendingKeySequenceDeadline;

    // Replaced by tests to move time without waiting.
    internal Func<long> PendingKeySequenceClock = static () => Environment.TickCount64;

    /// <summary>
    /// Gets the strokes pressed so far of a key sequence that needs more keys to complete, or
    /// <see langword="null"/> when none is in progress.
    /// </summary>
    /// <remarks>
    /// While this has a value every key press goes to the sequence: the next one completes it, extends it, or cancels
    /// it without reaching the focused element. It clears by itself a few seconds after the last stroke, on Escape,
    /// and when the window is deactivated.
    /// </remarks>
    public KeySequence? PendingKeySequence
    {
        get => GetValue(PendingKeySequenceProperty);
        private set => SetValue(PendingKeySequencePropertyKey, value);
    }

    /// <summary>The pending strokes, after dropping them if their time ran out.</summary>
    internal bool TryGetPendingKeySequence(out KeySequence pending)
    {
        var current = PendingKeySequence;
        if (current == null)
        {
            pending = default;
            return false;
        }

        // The timer is late when the UI thread was busy, and absent without a dispatcher.
        if (PendingKeySequenceClock() >= _pendingKeySequenceDeadline)
        {
            ClearPendingKeySequence();
            pending = default;
            return false;
        }

        pending = current.Value;
        return true;
    }

    internal void SetPendingKeySequence(KeySequence pending)
    {
        _pendingKeySequenceDeadline = PendingKeySequenceClock() + PENDING_KEY_SEQUENCE_TIMEOUT_MS;
        PendingKeySequence = pending;

        if (_pendingKeySequenceTimer == null)
        {
            _pendingKeySequenceTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(PENDING_KEY_SEQUENCE_TIMEOUT_MS));
            _pendingKeySequenceTimer.Tick += ClearPendingKeySequence;
        }

        _pendingKeySequenceTimer.Stop();
        _pendingKeySequenceTimer.Start();
    }

    internal void ClearPendingKeySequence()
    {
        _pendingKeySequenceTimer?.Stop();
        if (PendingKeySequence != null)
        {
            PendingKeySequence = null;
        }
    }
}
