// Upload, readback and shared buffers are persistently mapped. This optional sub-interface exposes that mapping as a
// raw pointer so uploads and readbacks write and read in place without a map / unmap per frame. Device-local buffers
// never implement it.

using System;

namespace MewStage.Graphics;

/// <summary>A persistently mapped host-visible buffer (upload, readback or shared). <see cref="MappedPointer"/> is valid
/// for the buffer's whole lifetime; callers write and read [0, SizeBytes).</summary>
public interface IMappedBuffer : IBuffer
{
    /// <summary>Persistent CPU-visible base pointer of the mapped range.</summary>
    IntPtr MappedPointer { get; }
}
