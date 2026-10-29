using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LibertyX.Partition.Core.IO;
using static LibertyX.Partition.Core.Platform.Linux.LinuxNative;

namespace LibertyX.Partition.Core.Platform.Linux;

/// <summary>
/// Dispositivo de bloco do Linux (/dev/sdb, /dev/nvme0n1p2, /dev/mapper/…) com E/S direta:
/// O_DIRECT para não sujar o cache nem esconder setor ruim atrás dele, e escrita com O_DSYNC
/// (o "write-through" do Windows) — cada escrita só volta depois de estar no disco.
/// Setor ilegível vira <see cref="MediaReadException"/>, como no Windows.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed unsafe class LinuxDevice : IBlockDevice
{
    private readonly int _fd;
    private readonly byte* _bounce;
    private const int BounceSize = 4 << 20;
    private readonly object _lock = new();
    public string Name { get; }
    public long Length { get; }
    public int SectorSize { get; }
    public bool CanWrite { get; }
    public bool Direct { get; }

    public LinuxDevice(string path, bool write, int sectorSize = 0, long? length = null)
    {
        Name = path;
        CanWrite = write;
        int flags = (write ? O_RDWR | O_DSYNC : O_RDONLY) | O_CLOEXEC;
        int fd = Open(path, flags | O_DIRECT);
        if (fd < 0 && Errno == EINVAL) fd = Open(path, flags);   // sistema de arquivos/driver sem O_DIRECT
        else if (fd >= 0) Direct = true;
        if (fd < 0)
        {
            int e = Errno;
            throw new IOException(e switch
            {
                EACCES or EPERM => L.T($"Acesso negado a {path}. O LibertyX Partition precisa rodar como administrador (root)."),
                EROFS => L.T($"{path} está protegido contra gravação (trava do cartão SD ou mídia só de leitura)."),
                ENOMEDIUM or ENXIO or ENODEV => L.T($"{path}: sem mídia ou dispositivo desconectado."),
                EBUSY => L.T($"{path} está em uso exclusivo por outro programa."),
                _ => L.T($"Não foi possível abrir {path}: {ErrText(e)}."),
            });
        }
        _fd = fd;
        int ss = sectorSize;
        if (ss <= 0) { int v = 0; if (Ioctl(_fd, BLKSSZGET, &v) == 0 && v > 0) ss = v; else ss = 512; }
        SectorSize = ss;
        long len = length ?? 0;
        if (len <= 0)
        {
            ulong sz = 0;
            if (Ioctl(_fd, BLKGETSIZE64, &sz) == 0) len = (long)sz;
            else try { len = new FileInfo(path).Length; } catch { }
        }
        Length = len - len % ss;
        _bounce = (byte*)NativeMemory.AlignedAlloc(BounceSize, 4096);
    }

    internal int Fd => _fd;

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
                nint got = PRead(_fd, _bounce, (nuint)n, pos);
                if (got < 0)
                {
                    int e = Errno;
                    if (e == EINTR) continue;
                    throw new MediaReadException(pos, n, e, L.T($"Erro de leitura no setor {pos / SectorSize:N0}: {ErrText(e)}."));
                }
                if (got == 0) break;
                new Span<byte>(_bounce, (int)got).CopyTo(buffer[done..]);
                done += (int)got;
                if (got < n && got % SectorSize != 0) break;
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
                nint wrote = PWrite(_fd, _bounce, (nuint)n, pos);
                if (wrote < 0)
                {
                    int e = Errno;
                    if (e == EINTR) continue;
                    throw new IOException(e switch
                    {
                        EROFS or EPERM => L.T($"Escrita recusada no setor {pos / SectorSize:N0}: mídia protegida contra gravação."),
                        _ => L.T($"Erro de escrita no setor {pos / SectorSize:N0}: {ErrText(e)}."),
                    });
                }
                if (wrote == 0) throw new IOException(L.T($"O dispositivo parou de aceitar escrita no setor {pos / SectorSize:N0}."));
                done += (int)wrote;
            }
        }
    }

    public void Flush()
    {
        lock (_lock) FSync(_fd);
    }

    /// <summary>Descarta (TRIM) uma faixa — SSD e cartões devolvem zeros e ganham fôlego.</summary>
    public bool Discard(long offset, long length, bool secure = false)
    {
        var range = stackalloc ulong[2];
        range[0] = (ulong)offset; range[1] = (ulong)length;
        lock (_lock) return Ioctl(_fd, secure ? BLKSECDISCARD : BLKDISCARD, range) == 0;
    }

    /// <summary>Zera uma faixa pelo próprio dispositivo (WRITE ZEROES / WRITE SAME) quando ele sabe.</summary>
    public bool ZeroOut(long offset, long length)
    {
        var range = stackalloc ulong[2];
        range[0] = (ulong)offset; range[1] = (ulong)length;
        lock (_lock) return Ioctl(_fd, BLKZEROOUT, range) == 0;
    }

    private bool _disposed;
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            Close(_fd);
            NativeMemory.AlignedFree(_bounce);
        }
    }
}
