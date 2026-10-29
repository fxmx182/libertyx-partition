using Microsoft.Win32.SafeHandles;

namespace LibertyX.Partition.Core.IO;

/// <summary>Uma imagem de disco crua (.img, .dd, .raw, .bin, .iso) tratada como disco.
/// Também serve para dispositivos de bloco no Linux (/dev/sdX), usado no desenvolvimento.</summary>
public sealed class ImageFileDevice : IBlockDevice
{
    private readonly SafeFileHandle _h;
    public string Name { get; }
    public string Path { get; }
    public long Length { get; private set; }
    public int SectorSize { get; }
    public bool CanWrite { get; }

    public ImageFileDevice(string path, bool writable = false, int sectorSize = 512, long? lengthOverride = null)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        SectorSize = sectorSize;
        CanWrite = writable;
        _h = File.OpenHandle(path, FileMode.Open, writable ? FileAccess.ReadWrite : FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        Length = lengthOverride ?? RandomAccess.GetLength(_h);
        Length -= Length % sectorSize;
    }

    public static ImageFileDevice Create(string path, long length, int sectorSize = 512)
    {
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write)) fs.SetLength(length);
        return new ImageFileDevice(path, true, sectorSize);
    }

    public int Read(long offset, Span<byte> buffer)
    {
        if (offset >= Length) return 0;
        int want = (int)Math.Min(buffer.Length, Length - offset);
        int total = 0;
        while (total < want)
        {
            int n = RandomAccess.Read(_h, buffer[total..want], offset + total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    public void Write(long offset, ReadOnlySpan<byte> buffer)
    {
        if (!CanWrite) throw new IOException(L.T("imagem aberta só para leitura"));
        RandomAccess.Write(_h, buffer, offset);
        if (offset + buffer.Length > Length) Length = offset + buffer.Length;
    }

    public void Flush() => RandomAccess.FlushToDisk(_h);
    public void Dispose() => _h.Dispose();
}
