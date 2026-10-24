using System.IO.MemoryMappedFiles;

namespace VCamNetSampleSourceAOT;

/// <summary>
/// Copies the newest NV12 frame published by the iToPC receiver into a
/// Media Foundation sample. The frame file uses two slots, so the writer can
/// publish the next frame without blocking the virtual-camera consumer.
/// </summary>
public sealed unsafe class FrameGenerator : IDisposable
{
    private readonly SharedFrameReader _reader = new();
    private readonly byte[] _nv12Frame = new byte[Shared.FrameSize];
    private Guid _loggedFormat;
    private ulong _frameCount;

    public bool HasD3DManager => false;
    public ulong FrameCount => _frameCount;

    public HRESULT SetD3DManager(nint manager, uint width, uint height) => Constants.S_OK;

    public HRESULT EnsureRenderTarget(uint width, uint height)
    {
        if (width != Shared.Width || height != Shared.Height)
            return Constants.E_INVALIDARG;

        return Constants.S_OK;
    }

    public void Generate(IComObject<IMFSample> sample, Guid format)
    {
        ArgumentNullException.ThrowIfNull(sample);

        using var buffer = sample.GetBufferByIndex(0);
        if (format == Constants.MFVideoFormat_RGB32)
        {
            buffer.WithLock((scanline, length, _) =>
            {
                if (length < Shared.Rgb32FrameSize)
                    throw new ArgumentException("The Media Foundation sample is smaller than one RGB32 frame.");

                fixed (byte* source = _nv12Frame)
                {
                    _reader.CopyLatest((nint)source, _nv12Frame.Length);
                    ConvertNv12ToRgb32(source, (byte*)scanline, Shared.Width, Shared.Height);
                    if (_loggedFormat != format)
                    {
                        _loggedFormat = format;
                        var destination = (byte*)scanline;
                        SharedFrameReader.Log($"rgb32 len={length} src={source[0]}/{source[1280]}/{source[Shared.FrameSize - 1]} dst={destination[0]}/{destination[1]}/{destination[2]}/{destination[3]}/{destination[4]}/{destination[5]}/{destination[6]}/{destination[7]}");
                    }
                }
            });
            buffer.SetCurrentLength(Shared.Rgb32FrameSize);
        }
        else if (format == Constants.MFVideoFormat_NV12)
        {
            buffer.WithLock((scanline, length, _) =>
            {
                _reader.CopyLatest(scanline, checked((int)length));
                if (_loggedFormat != format)
                {
                    _loggedFormat = format;
                    var destination = (byte*)scanline;
                    SharedFrameReader.Log($"nv12 len={length} dst={destination[0]}/{destination[1280]}/{destination[Shared.FrameSize - 1]}");
                }
            });
            buffer.SetCurrentLength(Shared.FrameSize);
        }
        else
        {
            throw new NotSupportedException("Lenscord virtual camera only exposes RGB32 and NV12 frames.");
        }

        _frameCount++;
    }

    private static void ConvertNv12ToRgb32(byte* nv12, byte* rgb, int width, int height)
    {
        var yPlane = nv12;
        var uvPlane = nv12 + width * height;
        for (var y = 0; y < height; y++)
        {
            var yRow = yPlane + y * width;
            var uvRow = uvPlane + (y / 2) * width;
            var destination = rgb + y * width * 4;
            for (var x = 0; x < width; x += 2)
            {
                var u = uvRow[x] - 128;
                var v = uvRow[x + 1] - 128;
                WritePixel(yRow[x], u, v, destination + x * 4);
                WritePixel(yRow[x + 1], u, v, destination + (x + 1) * 4);
            }
        }
    }

    private static void WritePixel(int luma, int u, int v, byte* destination)
    {
        var c = 298 * (luma - 16);
        var b = (c + 516 * u + 128) >> 8;
        var g = (c - 100 * u - 208 * v + 128) >> 8;
        var r = (c + 409 * v + 128) >> 8;
        destination[0] = Clamp(b);
        destination[1] = Clamp(g);
        destination[2] = Clamp(r);
        destination[3] = 255;
    }

    private static byte Clamp(int value) => value < 0 ? (byte)0 : value > 255 ? (byte)255 : (byte)value;

    public void Dispose()
    {
        _reader.Dispose();
        GC.SuppressFinalize(this);
    }
}

internal sealed unsafe class SharedFrameReader : IDisposable
{
    private MemoryMappedFile? _mapping;
    private MemoryMappedViewAccessor? _view;
    private DateTime _nextOpenAttemptUtc;

    public void CopyLatest(nint destination, int capacity)
    {
        if (destination == 0 || capacity < Shared.FrameSize)
            throw new ArgumentException("The Media Foundation sample is smaller than one NV12 frame.");

        if (!EnsureOpen() || !TryCopy(destination))
            WriteStandbyFrame(destination);
    }

    private bool EnsureOpen()
    {
        if (_view is not null) return true;
        if (DateTime.UtcNow < _nextOpenAttemptUtc) return false;
        _nextOpenAttemptUtc = DateTime.UtcNow.AddSeconds(1);

        try
        {
            var stream = new FileStream(
                Shared.FrameFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            _mapping = MemoryMappedFile.CreateFromFile(
                stream,
                mapName: null,
                capacity: 0,
                MemoryMappedFileAccess.Read,
                HandleInheritability.None,
                leaveOpen: false);
            _view = _mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            Log($"opened {Shared.FrameFilePath} length={stream.Length} header={_view.ReadUInt32(0):X8}/{_view.ReadInt32(4)}/{_view.ReadInt32(8)}x{_view.ReadInt32(12)}/{_view.ReadInt32(16)}/{_view.ReadInt32(20)}/{_view.ReadInt64(24)}");
            return true;
        }
        catch (Exception e)
        {
            Log("open failed: " + e);
            DisposeMapping();
            return false;
        }
    }

    private bool TryCopy(nint destination)
    {
        var view = _view;
        if (view is null) return false;

        try
        {
            if (view.ReadUInt32(0) != Shared.FrameMagic ||
                view.ReadInt32(4) != 1 ||
                view.ReadInt32(8) != Shared.Width ||
                view.ReadInt32(12) != Shared.Height ||
                view.ReadInt32(16) != Shared.FrameSize)
            {
                return false;
            }

            for (var attempt = 0; attempt < 2; attempt++)
            {
                var sequenceBefore = view.ReadInt64(24);
                var slot = view.ReadInt32(20);
                if (sequenceBefore <= 0 || slot is < 0 or > 1) return false;

                byte* viewBase = null;
                view.SafeMemoryMappedViewHandle.AcquirePointer(ref viewBase);
                try
                {
                    var source = viewBase + view.PointerOffset + Shared.HeaderSize + slot * Shared.FrameSize;
                    Buffer.MemoryCopy(source, (void*)destination, Shared.FrameSize, Shared.FrameSize);
                }
                finally
                {
                    view.SafeMemoryMappedViewHandle.ReleasePointer();
                }

                if (sequenceBefore == view.ReadInt64(24)) return true;
            }
            return false;
        }
        catch (Exception e)
        {
            Log("copy failed: " + e);
            DisposeMapping();
            return false;
        }
    }

    private static void WriteStandbyFrame(nint destination)
    {
        var frame = new Span<byte>((void*)destination, Shared.FrameSize);
        frame[..(Shared.Width * Shared.Height)].Fill(16);
        frame[(Shared.Width * Shared.Height)..].Fill(128);
    }

    internal static void Log(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetDirectoryName(Shared.FrameFilePath)!, "vcam.log"),
                $"{DateTime.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private void DisposeMapping()
    {
        Interlocked.Exchange(ref _view, null)?.Dispose();
        Interlocked.Exchange(ref _mapping, null)?.Dispose();
    }

    public void Dispose()
    {
        DisposeMapping();
        GC.SuppressFinalize(this);
    }
}

