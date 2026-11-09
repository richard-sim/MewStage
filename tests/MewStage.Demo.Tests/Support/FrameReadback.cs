using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;

using MewStage.Graphics;
using MewStage.Rendering.Graph;

namespace MewStage.Demo.Tests.Support;

/// <summary>
/// Reads the composed backbuffer back as BGRA8 (a readback pass added from <c>AppHost.BeforePresent</c>) and saves it
/// as a PNG for the journey dumps. Pixel reads are valid after the frame's GPU work completed (<c>WaitIdle</c>).
/// </summary>
public sealed class FrameReadback : IDisposable
{
    private readonly IGraphicsDevice _device;
    private IBuffer? _buffer;

    public FrameReadback(IGraphicsDevice device)
    {
        _device = device;
    }

    /// <summary>The width of the last frame read back.</summary>
    public int Width { get; private set; }

    /// <summary>The height of the last frame read back.</summary>
    public int Height { get; private set; }

    /// <summary>The hook for BeforePresent.</summary>
    public void Record(RenderGraph graph, RgHandle backbuffer, ITexture texture)
    {
        uint w = texture.Desc.Width;
        uint h = texture.Desc.Height;
        if (_buffer is null || (int)w != Width || (int)h != Height)
        {
            _device.GraphicsQueue.WaitIdle();
            _buffer?.Dispose();
            _buffer = _device.CreateBuffer(new BufferDesc { SizeBytes = w * h * 4, Memory = MemoryClass.Readback, Usage = ResourceUsage.CopyDest, DebugName = "journey-readback" });
            Width = (int)w;
            Height = (int)h;
        }

        IBuffer rb = _buffer;
        RgHandle rbHandle = graph.ImportBuffer(new RgBufferDesc { Name = "journey-readback", SizeBytes = w * h * 4 }, rb.Handle, ResourceState.Common);
        graph.AddPass("journey-readback", PassKind.Graphics, s =>
        {
            s.Read(backbuffer, ShaderStage.Pixel, ResourceState.CopySource);
            s.Write(rbHandle, ResourceState.CopyDest);
            s.SetExecute((in c) => c.CommandList.CopyTextureToBuffer(texture, rb, w, h));
        });
    }

    /// <summary>The (R, G, B) of one pixel of the last frame.</summary>
    public (int R, int G, int B) Px(int x, int y)
    {
        if (_buffer is null)
        {
            throw new InvalidOperationException("no frame was read back");
        }

        if (x < 0 || y < 0 || x >= Width || y >= Height)
        {
            throw new ArgumentOutOfRangeException(nameof(x), $"({x}, {y}) is outside {Width} x {Height}");
        }

        IntPtr p = ((IMappedBuffer)_buffer).MappedPointer;
        int o = ((y * Width) + x) * 4;
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

    /// <summary>How many pixels inside a block satisfy <paramref name="predicate"/>.</summary>
    public int Count(int x0, int y0, int w, int h, Func<(int R, int G, int B), bool> predicate)
    {
        int n = 0;
        for (int y = y0; y < y0 + h; y++)
        {
            for (int x = x0; x < x0 + w; x++)
            {
                if (predicate(Px(x, y)))
                {
                    n++;
                }
            }
        }

        return n;
    }

    /// <summary>How many pixels of the whole frame satisfy <paramref name="predicate"/>.</summary>
    public int Count(Func<(int R, int G, int B), bool> predicate)
    {
        return Count(0, 0, Width, Height, predicate);
    }

    /// <summary>Save the last frame as an 8-bit RGB PNG (alpha dropped) when a dump directory is configured; returns the path or null.</summary>
    public string? Dump(string name)
    {
        string? dir = DemoLavapipeGate.OutputDirectory;
        if (dir is null || _buffer is null)
        {
            return null;
        }

        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name + ".png");
        File.WriteAllBytes(path, EncodePng());
        return path;
    }

    public void Dispose()
    {
        _buffer?.Dispose();
        _buffer = null;
    }

    private byte[] EncodePng()
    {
        // Raw scanlines: filter byte 0 + RGB.
        int stride = (Width * 3) + 1;
        byte[] raw = new byte[stride * Height];
        IntPtr p = ((IMappedBuffer)_buffer!).MappedPointer;
        for (int y = 0; y < Height; y++)
        {
            int ro = y * stride;
            raw[ro] = 0;
            for (int x = 0; x < Width; x++)
            {
                int so = ((y * Width) + x) * 4;
                int d = ro + 1 + (x * 3);
                raw[d] = Marshal.ReadByte(p, so + 2);
                raw[d + 1] = Marshal.ReadByte(p, so + 1);
                raw[d + 2] = Marshal.ReadByte(p, so);
            }
        }

        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            {
                z.Write(raw, 0, raw.Length);
            }

            compressed = ms.ToArray();
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, Width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], Height);
        ihdr[8] = 8;    // bit depth
        ihdr[9] = 2;    // color type RGB
        ihdr[10] = 0;   // compression
        ihdr[11] = 0;   // filter
        ihdr[12] = 0;   // interlace
        WriteChunk(png, "IHDR", ihdr);
        WriteChunk(png, "IDAT", compressed);
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        byte[] typeBytes = new byte[4];
        for (int i = 0; i < 4; i++)
        {
            typeBytes[i] = (byte)type[i];
        }

        s.Write(typeBytes);
        s.Write(data);
        uint crc = Crc32(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        s.Write(crcBytes);
    }

    private static uint Crc32(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        uint c = 0xFFFFFFFF;
        c = Crc32Update(c, a);
        c = Crc32Update(c, b);
        return c ^ 0xFFFFFFFF;
    }

    private static uint Crc32Update(uint c, ReadOnlySpan<byte> data)
    {
        foreach (byte v in data)
        {
            c ^= v;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }
        }

        return c;
    }
}
