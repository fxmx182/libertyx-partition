using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LibertyX.Partition.Core.IO;
using Microsoft.Win32.SafeHandles;

namespace LibertyX.Partition.Core.Platform.Windows;

/// <summary>Disco físico (\\.\PhysicalDriveN) ou volume (\\.\Volume{...}) aberto para E/S direta.</summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WinDevice : IBlockDevice
{
    private readonly SafeFileHandle _h;
    private readonly byte* _bounce;
    private const int BounceSize = 4 << 20;
    private readonly object _lock = new();
    public string Name { get; }
    public long Length { get; }
    public int SectorSize { get; }
    public bool CanWrite { get; }

    public WinDevice(string path, bool write, int sectorSize, long? length = null, bool isVolume = false)
    {
        Name = path;
        CanWrite = write;
        SectorSize = sectorSize;
        _h = Native.Open(path, Native.GENERIC_READ | (write ? Native.GENERIC_WRITE : 0), write ? Native.FILE_FLAG_WRITE_THROUGH : 0);
        if (_h.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            throw new IOException(err == Native.ERROR_ACCESS_DENIED
                ? L.T($"Acesso negado a {path}. O LibertyX Partition precisa rodar como administrador.")
                : L.T($"Não foi possível abrir {path} (erro {err})."));
        }
        if (isVolume) Native.IoctlNoData(_h, Native.FSCTL_ALLOW_EXTENDED_DASD_IO);
        long len = length ?? 0;
        if (len <= 0)
        {
            var li = Native.Ioctl(_h, Native.IOCTL_DISK_GET_LENGTH_INFO, null, 8);
            if (li is { Length: >= 8 }) len = BitConverter.ToInt64(li, 0);
        }
        Length = len - len % sectorSize;
        _bounce = (byte*)NativeMemory.AlignedAlloc(BounceSize, 4096);
    }

    internal SafeFileHandle Handle => _h;

    public int Read(long offset, Span<byte> buffer)
    {
        if (offset >= Length) return 0;
        int want = (int)Math.Min(buffer.Length, Length - offset);
        if (offset % SectorSize != 0 || want % SectorSize != 0) throw new ArgumentException(L.T("leitura desalinhada"));
        int done = 0;
        lock (_lock)
        {
            while (done < want)
            {
                int n = Math.Min(BounceSize, want - done);
                long pos = offset + done;
                var ov = new NativeOverlapped { OffsetLow = (int)(pos & 0xFFFFFFFF), OffsetHigh = (int)(pos >> 32) };
                if (!Native.ReadFile(_h, _bounce, n, out int got, &ov))
                {
                    int err = Marshal.GetLastWin32Error();
                    throw new MediaReadException(pos, n, err, L.T($"Erro de leitura em {pos / SectorSize:N0} (código {err})."));
                }
                if (got <= 0) break;
                new Span<byte>(_bounce, got).CopyTo(buffer[done..]);
                done += got;
                if (got < n) break;
            }
        }
        return done;
    }

    public void Write(long offset, ReadOnlySpan<byte> buffer)
    {
        if (!CanWrite) throw new IOException(L.T("dispositivo aberto só para leitura"));
        if (offset % SectorSize != 0 || buffer.Length % SectorSize != 0) throw new ArgumentException(L.T("escrita desalinhada"));
        int done = 0;
        lock (_lock)
        {
            while (done < buffer.Length)
            {
                int n = Math.Min(BounceSize, buffer.Length - done);
                buffer.Slice(done, n).CopyTo(new Span<byte>(_bounce, n));
                long pos = offset + done;
                var ov = new NativeOverlapped { OffsetLow = (int)(pos & 0xFFFFFFFF), OffsetHigh = (int)(pos >> 32) };
                if (!Native.WriteFile(_h, _bounce, n, out int wrote, &ov) || wrote != n)
                {
                    int err = Marshal.GetLastWin32Error();
                    throw new IOException(err == Native.ERROR_ACCESS_DENIED
                        ? L.T($"O Windows bloqueou a escrita no setor {pos / SectorSize:N0}: há um volume montado ali.")
                        : L.T($"Erro de escrita no setor {pos / SectorSize:N0} (código {err})."));
                }
                done += n;
            }
        }
    }

    public void Flush() => Native.FlushFileBuffers(_h);

    private bool _disposed;
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _h.Dispose();
            NativeMemory.AlignedFree(_bounce);
        }
    }
}
