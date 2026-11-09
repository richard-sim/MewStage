// GPU resource lifetime: the ONE deferred-destroy queue every backend device uses, and the debug-only check that
// nothing is disposed while a frame in flight may still read it.
//
// Deferred destruction: IGraphicsDevice.DeferDestroy stamps a resource with the device's retire frame (the frame being
// recorded, set by GpuScheduler.BeginFrame through RetireDeferred) and frees it only once the scheduler has waited the
// frame fence of that frame, i.e. FramesInFlight frames later. The queue is a preallocated ring of (resource, frame)
// entries: stamps are monotonic, so retirement pops from the head; no closures, no per-frame allocation (a steady frame
// defers nothing; a burst beyond the high-water mark grows the ring once, by doubling).
//
// Lifetime validation (MEWSTAGE_GPU_VALIDATE=1, or the backend debug-layer switch MEWSTAGE_D3D12_DEBUG=1 /
// MEWSTAGE_VK_VALIDATION=1): every resource a command list touches (bind, barrier, copy, attachment) is stamped with the
// serial of the submission that will carry it (submits so far + 1). Completion advances when the device learns that GPU
// work finished: a waited fence (the serial of the submit that signalled that value), a queue or device idle, and the
// scheduler's retire frame (frames <= current - FramesInFlight have had their fence waited). A direct Dispose() of a
// resource whose last-use serial is above the completed serial throws and is counted. Stamps are never later than the
// real submit and completion is never earlier than the real one, so the check can miss a hazard but never reports a
// false one. Off by default: one predictable branch per bind.

using System;
using System.Globalization;
using System.Text;
using System.Threading;

using MewStage.Graphics;

namespace MewStage.Rendering.Backends;

/// <summary>The fence-keyed deferred-destroy queue of a device.</summary>
public sealed class DeferredDestroyQueue
{
    /// <summary>Initial ring capacity; a burst beyond it grows the ring once by doubling.</summary>
    public const int InitialCapacity = 1024;

    private IGpuResource[] _res = new IGpuResource[InitialCapacity];
    private long[] _frame = new long[InitialCapacity];
    private int _head;

    /// <summary>Resources waiting for their frame fence.</summary>
    public int Count { get; private set; }

    /// <summary>The most entries ever pending at once.</summary>
    public int HighWater { get; private set; }

    /// <summary>Queues <paramref name="resource"/> stamped with <paramref name="frame"/> (monotonic per device).</summary>
    /// <param name="resource">The resource.</param>
    /// <param name="frame">The frame being recorded.</param>
    public void Add(IGpuResource resource, long frame)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (Count == _res.Length)
        {
            Grow();
        }

        int i = (_head + Count) % _res.Length;
        _res[i] = resource;
        _frame[i] = frame;
        Count++;
        HighWater = Math.Max(HighWater, Count);
    }

    /// <summary>Disposes every entry stamped at least <paramref name="framesInFlight"/> frames before
    /// <paramref name="currentFrame"/> (its frame fence was waited).</summary>
    /// <param name="currentFrame">The frame being recorded.</param>
    /// <param name="framesInFlight">The scheduler's frames in flight.</param>
    /// <returns>The count freed.</returns>
    public int Retire(long currentFrame, int framesInFlight)
    {
        int freed = 0;
        while (Count > 0 && _frame[_head] + framesInFlight <= currentFrame)
        {
            IGpuResource r = _res[_head];
            _res[_head] = null!;
            _head = (_head + 1) % _res.Length;
            Count--;
            freed++;
            r.Dispose();
        }

        return freed;
    }

    /// <summary>Disposes everything (teardown; the caller drained the GPU).</summary>
    /// <returns>The count freed.</returns>
    public int Flush()
    {
        int n = Count;
        while (Count > 0)
        {
            IGpuResource r = _res[_head];
            _res[_head] = null!;
            _head = (_head + 1) % _res.Length;
            Count--;
            r.Dispose();
        }

        _head = 0;
        return n;
    }

    private void Grow()
    {
        int cap = _res.Length * 2;
        var res = new IGpuResource[cap];
        long[] frame = new long[cap];
        for (int k = 0; k < Count; k++)
        {
            int i = (_head + k) % _res.Length;
            res[k] = _res[i];
            frame[k] = _frame[i];
        }

        _res = res;
        _frame = frame;
        _head = 0;
    }
}

/// <summary>The debug-only GPU lifetime check of a device.</summary>
public sealed class GpuLifetimeValidator
{
    /// <summary>The switch: MEWSTAGE_GPU_VALIDATE=1 (any backend).</summary>
    public const string EnvVar = "MEWSTAGE_GPU_VALIDATE";

    private readonly Lock _gate = new();
    private long _submitted;
    private long _completed;
    private long[] _frameStart;   // submit serial at RetireDeferred(frame), a ring indexed by frame
    private long[] _frameOf;      // the frame each ring slot holds (-1 = none)
    private int _violations;
    private string? _lastViolation;

    /// <summary>Creates a validator.</summary>
    /// <param name="enabled">Whether the checks run (see <see cref="IsRequested"/>).</param>
    /// <param name="maxFramesInFlight">The deepest ring a scheduler on this device will run.</param>
    public GpuLifetimeValidator(bool enabled, int maxFramesInFlight)
    {
        Enabled = enabled;
        _frameStart = new long[Math.Max(1, maxFramesInFlight) + 1];
        _frameOf = new long[_frameStart.Length];
        Array.Fill(_frameOf, -1);
    }

    /// <summary>Checks run (false = every call below is a no-op).</summary>
    public bool Enabled { get; }

    /// <summary>Disposes of in-flight resources detected so far.</summary>
    public int Violations => Volatile.Read(ref _violations);

    /// <summary>The last violation's message (null = none).</summary>
    public string? LastViolation => Volatile.Read(ref _lastViolation);

    /// <summary>The serial a resource used in a list being recorded now is stamped with (the next submit).</summary>
    public long RecordSerial => Volatile.Read(ref _submitted) + 1;

    /// <summary>True when <see cref="EnvVar"/> is "1" or the backend's own debug switch <paramref name="backendEnv"/> is "1".</summary>
    /// <param name="backendEnv">The backend's debug-layer variable name, or null.</param>
    /// <returns>Whether validation is requested.</returns>
    public static bool IsRequested(string? backendEnv)
    {
        return Environment.GetEnvironmentVariable(EnvVar) == "1" || (backendEnv is not null && Environment.GetEnvironmentVariable(backendEnv) == "1");
    }

    /// <summary>A queue submit.</summary>
    /// <returns>Its serial (0 when disabled).</returns>
    public long OnSubmit()
    {
        return Enabled ? Interlocked.Increment(ref _submitted) : 0;
    }

    /// <summary>GPU work up to <paramref name="serial"/> is complete (a fence that submit signalled was waited).</summary>
    /// <param name="serial">The submit serial.</param>
    public void OnCompleted(long serial)
    {
        if (!Enabled || serial <= 0)
        {
            return;
        }

        lock (_gate)
        {
            _completed = Math.Max(_completed, serial);
        }
    }

    /// <summary>A queue or device idle wait: everything submitted so far is complete.</summary>
    public void OnIdle()
    {
        OnCompleted(Volatile.Read(ref _submitted));
    }

    /// <summary>The scheduler began <paramref name="currentFrame"/> after waiting the slot fence: frames up to
    /// currentFrame - framesInFlight are complete, i.e. every submit made before frame (that + 1) began.</summary>
    /// <param name="currentFrame">The frame being recorded.</param>
    /// <param name="framesInFlight">The scheduler's frames in flight.</param>
    public void OnFrame(long currentFrame, int framesInFlight)
    {
        if (!Enabled)
        {
            return;
        }

        lock (_gate)
        {
            if (framesInFlight + 1 > _frameStart.Length)   // once, when a scheduler runs deeper than the device max
            {
                _frameStart = new long[framesInFlight + 1];
                _frameOf = new long[framesInFlight + 1];
                Array.Fill(_frameOf, -1);
            }

            int slot = (int)(currentFrame % _frameStart.Length);
            _frameStart[slot] = _submitted;
            _frameOf[slot] = currentFrame;
            long next = currentFrame - framesInFlight + 1;
            if (next >= 0)
            {
                int ns = (int)(next % _frameStart.Length);
                if (_frameOf[ns] == next && _frameStart[ns] > _completed)
                {
                    _completed = _frameStart[ns];
                }
            }
        }
    }

    /// <summary>Before a direct Dispose: throws when the resource's last use has not completed on the GPU.</summary>
    /// <param name="lastUseSerial">The serial the resource was last stamped with.</param>
    /// <param name="what">Names the resource.</param>
    /// <exception cref="InvalidOperationException">The resource may still be in flight.</exception>
    public void CheckDispose(long lastUseSerial, string what)
    {
        if (!Enabled || lastUseSerial <= 0)
        {
            return;
        }

        long completed;
        lock (_gate)
        {
            completed = _completed;
        }

        if (lastUseSerial <= completed)
        {
            return;
        }

        string msg = "GPU lifetime violation: " + what + " disposed while a submit that uses it may still be in flight (last use serial "
            + lastUseSerial.ToString(CultureInfo.InvariantCulture) + ", completed serial " + completed.ToString(CultureInfo.InvariantCulture)
            + "). Release it with IGraphicsDevice.DeferDestroy.";
        _ = Interlocked.Increment(ref _violations);
        Volatile.Write(ref _lastViolation, msg);
        GpuDeviceDiagnostics.Note(msg);
        throw new InvalidOperationException(msg);
    }
}

/// <summary>Process-wide GPU diagnostics for crash logs: the last device fault, the last lifetime violation and the
/// validation-layer error count the backends reported.</summary>
public static class GpuDeviceDiagnostics
{
    // Khronos validation wording for use-after-free / destroy-while-in-use (VUID-vkDestroy*-*: "in use by a command
    // buffer"; a bound or recorded handle that no longer exists: "Invalid VkBuffer Object", "has been destroyed").
    private static readonly string[] LifetimePatterns = ["VUID-vkDestroy", "in use by", "has been destroyed", "Invalid VkBuffer", "Invalid VkImage", "Invalid VkDescriptorSet", "Invalid VkSampler"];

    private static string? s_lastFault;
    private static string? s_lastNote;
    private static int s_validationErrors;
    private static string? s_lastValidationError;
    private static int s_lifetimeErrors;
    private static string? s_lastLifetimeError;

    /// <summary>Validation-layer errors about a destroyed resource still in use.</summary>
    public static int LifetimeValidationErrors => Volatile.Read(ref s_lifetimeErrors);

    /// <summary>The last such message (null = none).</summary>
    public static string? LastLifetimeValidationError => Volatile.Read(ref s_lastLifetimeError);

    /// <summary>Validation-layer error messages since start (or <see cref="ResetValidationErrors"/>).</summary>
    public static int ValidationErrors => Volatile.Read(ref s_validationErrors);

    /// <summary>The last validation-layer error message (null = none).</summary>
    public static string? LastValidationError => Volatile.Read(ref s_lastValidationError);

    /// <summary>The last device fault text (null = none).</summary>
    public static string? LastFault => Volatile.Read(ref s_lastFault);

    /// <summary>Records a device fault (the D3D12 removed reason, Vulkan VK_ERROR_DEVICE_LOST).</summary>
    /// <param name="text">The fault text.</param>
    public static void Fault(string text)
    {
        Volatile.Write(ref s_lastFault, text);
    }

    /// <summary>Records a diagnostic line (a lifetime violation).</summary>
    /// <param name="text">The line.</param>
    public static void Note(string text)
    {
        Volatile.Write(ref s_lastNote, text);
    }

    /// <summary>Counts a debug-layer or validation-layer error message.</summary>
    /// <param name="text">The message.</param>
    public static void ValidationError(string text)
    {
        _ = Interlocked.Increment(ref s_validationErrors);
        Volatile.Write(ref s_lastValidationError, text);
        if (IsLifetimeMessage(text))
        {
            _ = Interlocked.Increment(ref s_lifetimeErrors);
            Volatile.Write(ref s_lastLifetimeError, text);
        }
    }

    /// <summary>True when <paramref name="text"/> reports a destroyed-in-use resource.</summary>
    /// <param name="text">A validation message.</param>
    /// <returns>Whether it is a lifetime message.</returns>
    public static bool IsLifetimeMessage(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (string p in LifetimePatterns)
        {
            if (text.Contains(p, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Clears the validation error counts (a test journey starts from zero).</summary>
    public static void ResetValidationErrors()
    {
        Volatile.Write(ref s_validationErrors, 0);
        Volatile.Write(ref s_lastValidationError, null);
        Volatile.Write(ref s_lifetimeErrors, 0);
        Volatile.Write(ref s_lastLifetimeError, null);
    }

    /// <summary>The crash-log section: empty when nothing was reported.</summary>
    /// <returns>The section text.</returns>
    public static string Describe()
    {
        string? fault = LastFault;
        string? note = Volatile.Read(ref s_lastNote);
        string? verr = LastValidationError;
        if (fault is null && note is null && verr is null)
        {
            return "";
        }

        StringBuilder sb = new();
        _ = sb.Append("gpu:\n");
        if (fault is not null)
        {
            _ = sb.Append("  device fault: ").Append(fault).Append('\n');
        }

        if (note is not null)
        {
            _ = sb.Append("  lifetime: ").Append(note).Append('\n');
        }

        if (verr is not null)
        {
            _ = sb.Append("  validation errors: ").Append(ValidationErrors.ToString(CultureInfo.InvariantCulture)).Append(", last: ").Append(verr).Append('\n');
        }

        return sb.ToString();
    }
}
