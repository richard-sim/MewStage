// MewStage vendor patch allocation/0001 (third_party/NOTICE): the scratch arrays MewVG rents while it builds a frame (the
// tessellator, the BGRA image swap) come from a pool of its own instead of ArrayPool<T>.Shared. The shared pool
// trims itself from a Gen2 GC callback (on the finalizer thread, by array age and memory pressure), so a GC caused
// by anything else in the process could empty the render thread's buckets and make the next frame allocate its
// scratch arrays again. A pool made by ArrayPool<T>.Create() is never trimmed: once a frame's arrays have been
// rented they are reused for good, and a steady-state frame allocates nothing.
namespace Aprillz.MewVG;

internal static class NvgScratchPool<T>
{
    /// <summary>The pool every scratch rent in MewVG.Core goes through.</summary>
    internal static readonly System.Buffers.ArrayPool<T> Shared = System.Buffers.ArrayPool<T>.Create();
}
