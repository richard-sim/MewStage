using System;
using System.Runtime.InteropServices;

using MewStage.Graphics;
using MewStage.Rendering.Graph;

namespace MewStage.Host.Tests.Lavapipe;

/// <summary>
/// Reads a window's composed backbuffer back as BGRA8: plugs into <see cref="AppHost.BeforePresent"/> (or a
/// <see cref="SecondaryWindow"/>'s) and adds a readback pass that copies the backbuffer into a host-visible buffer the
/// test reads after the frame's GPU work completed (<see cref="GpuWindow.WaitIdle"/>).
/// </summary>
public sealed class BackbufferReadback : IDisposable
{
    private readonly IGraphicsDevice _device;
    private IBuffer? _buffer;

    public BackbufferReadback(IGraphicsDevice device)
    {
        _device = device;
    }

    /// <summary>The width of the last frame read back.</summary>
    public uint Width { get; private set; }

    /// <summary>The height of the last frame read back.</summary>
    public uint Height { get; private set; }

    /// <summary>The hook: call from BeforePresent with the frame's graph, backbuffer handle and texture.</summary>
    public void Record(RenderGraph graph, RgHandle backbuffer, ITexture texture)
    {
        uint w = texture.Desc.Width;
        uint h = texture.Desc.Height;
        if (_buffer is null || w != Width || h != Height)
        {
            _device.GraphicsQueue.WaitIdle();
            _buffer?.Dispose();
            _buffer = _device.CreateBuffer(new BufferDesc { SizeBytes = w * h * 4, Memory = MemoryClass.Readback, Usage = ResourceUsage.CopyDest, DebugName = "test-readback" });
            Width = w;
            Height = h;
        }

        IBuffer rb = _buffer;
        RgHandle rbHandle = graph.ImportBuffer(new RgBufferDesc { Name = "test-readback", SizeBytes = w * h * 4 }, rb.Handle, ResourceState.Common);
        graph.AddPass("test-readback", PassKind.Graphics, s =>
        {
            s.Read(backbuffer, ShaderStage.Pixel, ResourceState.CopySource);
            s.Write(rbHandle, ResourceState.CopyDest);
            s.SetExecute((in c) => c.CommandList.CopyTextureToBuffer(texture, rb, w, h));
        });
    }

    /// <summary>The (R, G, B) of one pixel of the last frame (call after the frame's GPU work completed).</summary>
    public (int R, int G, int B) Px(int x, int y)
    {
        if (_buffer is null)
        {
            throw new InvalidOperationException("no frame was read back");
        }

        IntPtr p = ((IMappedBuffer)_buffer).MappedPointer;
        int o = ((y * (int)Width) + x) * 4;
        return (Marshal.ReadByte(p, o + 2), Marshal.ReadByte(p, o + 1), Marshal.ReadByte(p, o));
    }

    /// <summary>The mean (R, G, B) over a block.</summary>
    public (double R, double G, double B) Mean(int x0, int y0, int w, int h)
    {
        double r = 0;
        double g = 0;
        double b = 0;
        for (int y = y0; y < y0 + h; y++)
        {
            for (int x = x0; x < x0 + w; x++)
            {
                (int pr, int pg, int pb) = Px(x, y);
                r += pr;
                g += pg;
                b += pb;
            }
        }

        int n = w * h;
        return (r / n, g / n, b / n);
    }

    /// <summary>How many pixels of the last frame satisfy <paramref name="predicate"/>.</summary>
    public int Count(Func<(int R, int G, int B), bool> predicate)
    {
        int n = 0;
        for (int y = 0; y < (int)Height; y++)
        {
            for (int x = 0; x < (int)Width; x++)
            {
                if (predicate(Px(x, y)))
                {
                    n++;
                }
            }
        }

        return n;
    }

    public void Dispose()
    {
        _buffer?.Dispose();
        _buffer = null;
    }
}
