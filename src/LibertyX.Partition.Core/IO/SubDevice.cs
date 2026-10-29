namespace LibertyX.Partition.Core.IO;

/// <summary>Uma janela de outro dispositivo — normalmente uma partição dentro do disco.
/// Não é dona do pai: descartar a fatia não fecha o disco.</summary>
public sealed class SubDevice : IBlockDevice
{
    private readonly IBlockDevice _parent;
    public long Offset { get; }
    public long Length { get; }
    public string Name { get; }
    public int SectorSize => _parent.SectorSize;
    public bool CanWrite => _parent.CanWrite;
    public IBlockDevice Parent => _parent;

    public SubDevice(IBlockDevice parent, long offset, long length, string? name = null)
    {
        if (offset < 0 || offset % parent.SectorSize != 0) throw new ArgumentException(L.T("deslocamento fora de setor"), nameof(offset));
        _parent = parent;
        Offset = offset;
        Length = Math.Max(0, Math.Min(length, parent.Length - offset));
        Name = name ?? $"{parent.Name} @ {offset}";
    }

    public int Read(long offset, Span<byte> buffer)
    {
        if (offset >= Length) return 0;
        int n = (int)Math.Min(buffer.Length, Length - offset);
        return _parent.Read(Offset + offset, buffer[..n]);
    }

    public void Write(long offset, ReadOnlySpan<byte> buffer)
    {
        if (offset + buffer.Length > Length) throw new IOException(L.T("escrita além do fim da partição"));
        _parent.Write(Offset + offset, buffer);
    }

    public void Flush() => _parent.Flush();
    public void Dispose() { }
}
