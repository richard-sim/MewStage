// The allocation-time poison switch. MEWSTAGE_GPU_POISON=1 (debug and tests only) makes every device fill each newly
// allocated buffer and image with GpuPoison.Byte BEFORE first use, so a pass that reads memory nobody wrote reads a
// recognizable, deterministic garbage pattern instead of whatever the driver happened to hand out (zero pages on Linux
// lavapipe, recycled heap on Windows lavapipe, zeroed committed resources on D3D12). A render that changes when the
// switch flips has an unwritten read. The fill runs once per allocation at creation time and never on the frame path.

using System;
using System.Threading;

namespace MewStage.Rendering.Backends;

/// <summary>The allocation poison switch shared by the D3D12 and Vulkan devices.</summary>
public static class GpuPoison
{
    /// <summary>The environment variable that turns the poison on ("1" or "nan").</summary>
    public const string EnvVar = "MEWSTAGE_GPU_POISON";

    /// <summary>The fill byte: 0xCD is the MSVC debug heap's "clean land fill" (newly allocated, never written memory).
    /// As data it is loud in every format the renderer uses: float32 0xCDCDCDCD = -4.316e8, half 0xCDCD = -23.2,
    /// UNORM8 205/255, a uint32 index 3452816845, never zero.</summary>
    public const byte Byte = 0xCD;

    /// <summary>The NaN fill (MEWSTAGE_GPU_POISON=nan): all bits set is a NaN in float32, half and every R11G11B10 channel,
    /// and 1.0 in UNORM8. 0xCD is finite in every float format, so it cannot reveal an unwritten texel that is multiplied
    /// by zero; this fill can.</summary>
    public const byte NanByte = 0xFF;

    /// <summary>The <see cref="EnvVar"/> value that selects <see cref="NanByte"/>.</summary>
    public const string NanValue = "nan";

    private const uint ByteToWord = 0x01010101u;   // replicates a byte into the 4 bytes of a 32-bit word

    private static long s_fills;

    /// <summary>Allocations poisoned by any device in this process (evidence that the switch ran).</summary>
    public static long Fills => Interlocked.Read(ref s_fills);

    /// <summary>The fill as a 32-bit word (vkCmdFillBuffer, a clear's raw bits): <paramref name="fill"/> in each of the 4 bytes.</summary>
    /// <param name="fill">The fill byte.</param>
    /// <returns>The word.</returns>
    public static uint Word(byte fill)
    {
        return fill * ByteToWord;
    }

    /// <summary>The fill read as an IEEE-754 float32 (a color clear on a device whose image memory is not host-visible).</summary>
    /// <param name="fill">The fill byte.</param>
    /// <returns>The float.</returns>
    public static float AsFloat(byte fill)
    {
        return BitConverter.UInt32BitsToSingle(Word(fill));
    }

    /// <summary>The fill byte <see cref="EnvVar"/> asks for: "1" = <see cref="Byte"/>, "nan" = <see cref="NanByte"/>, else null
    /// (off). Each device reads it once in its constructor.</summary>
    /// <returns>The fill byte, or null.</returns>
    public static byte? Requested()
    {
        string? v = Environment.GetEnvironmentVariable(EnvVar);
        return v == "1" ? Byte : v == NanValue ? NanByte : null;
    }

    /// <summary>True when <see cref="Requested"/> is not null.</summary>
    /// <returns>Whether poisoning is requested.</returns>
    public static bool IsRequested()
    {
        return Requested() is not null;
    }

    internal static void CountFill()
    {
        _ = Interlocked.Increment(ref s_fills);
    }

    /// <summary>Fills <paramref name="bytes"/> bytes at <paramref name="p"/> with <paramref name="fill"/>.</summary>
    internal static unsafe void Fill(void* p, ulong bytes, byte fill)
    {
        byte* b = (byte*)p;
        while (bytes > 0)
        {
            int n = (int)Math.Min(bytes, int.MaxValue);   // a Span is int-indexed
            new Span<byte>(b, n).Fill(fill);
            b += n;
            bytes -= (ulong)n;
        }

        CountFill();
    }
}
