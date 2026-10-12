using System.IO.Compression;
using LibertyX.Partition.Core.Recovery;
using static LibertyX.Partition.Core.IO.Bin;

namespace LibertyX.Partition.Core.FileSystems;

/// <summary>
/// Compressão transparente do macOS (decmpfs), comum no HFS+ e no APFS: o arquivo aparece vazio
/// e o conteúdo mora no atributo com.apple.decmpfs (pequenos) ou na resource fork (grandes),
/// em blocos de 64 KiB. Lê zlib e sem compressão; LZVN e LZFSE são avisados.
/// </summary>
public static class Decmpfs
{
    public const uint Magic = 0x636D7066; // "fpmc" gravado em little-endian

    public static string? Unsupported(ReadOnlySpan<byte> xattr)
    {
        if (xattr.Length < 16 || U32(xattr, 0) != Magic) return "atributo de compressão inválido";
        return U32(xattr, 4) switch
        {
            1 or 3 or 4 or 9 or 10 => null,
            7 or 8 => "comprimido pelo macOS em LZVN — ainda não suportado",
            11 or 12 => "comprimido pelo macOS em LZFSE — ainda não suportado",
            var t => $"comprimido pelo macOS (tipo {t}) — não suportado",
        };
    }

    /// <summary>Monta a fonte dos bytes descomprimidos. <paramref name="rsrc"/> é a resource fork (pode ser nula).</summary>
    public static IDataSource? Open(byte[] xattr, IDataSource? rsrc, out string? note)
    {
        note = Unsupported(xattr);
        if (note != null) return null;
        uint type = U32(xattr, 4);
        long size = (long)U64(xattr, 8);
        try
        {
            switch (type)
            {
                case 1 or 9:
                    return new ResidentSource(Fit(xattr.AsSpan(16).ToArray(), size));
                case 3:
                    {
                        var body = xattr.AsSpan(16);
                        if (body.Length > 0 && (body[0] & 0x0F) == 0x0F) return new ResidentSource(Fit(body[1..].ToArray(), size));
                        return new ResidentSource(Fit(Inflate(body.ToArray()), size));
                    }
                case 4 or 10:
                    {
                        if (rsrc == null || rsrc.Length < 260) { note = "resource fork da compressão ausente"; return null; }
                        var head = new byte[16];
                        rsrc.Read(0, head);
                        long dataOfs = U32BE(head, 0);
                        var cnt = new byte[8];
                        rsrc.Read(dataOfs + 4, cnt);
                        uint n = U32(cnt, 0);
                        if (n == 0 || n > 1 << 20) { note = "tabela de blocos da compressão inválida"; return null; }
                        var table = new byte[n * 8];
                        rsrc.Read(dataOfs + 8, table);
                        var blocks = new (long ofs, int len)[n];
                        for (int i = 0; i < n; i++) blocks[i] = (dataOfs + 4 + U32(table, i * 8), (int)U32(table, i * 8 + 4));
                        return new BlockSource(rsrc, blocks, size, raw: type == 10);
                    }
            }
        }
        catch (Exception ex) { note = "compressão ilegível: " + ex.Message; }
        return null;
    }

    private static byte[] Fit(byte[] b, long size) => b.Length == size ? b : b.AsSpan(0, (int)Math.Min(b.Length, size)).ToArray();

    internal static byte[] Inflate(byte[] z)
    {
        using var src = new ZLibStream(new MemoryStream(z), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        src.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Blocos de 64 KiB, cada um comprimido à parte; descomprime sob demanda e guarda o último.</summary>
    private sealed class BlockSource(IDataSource src, (long ofs, int len)[] blocks, long size, bool raw) : IDataSource
    {
        private const int Chunk = 65536;
        private int _cached = -1;
        private byte[] _buf = [];
        public long Length => size;

        private byte[] Block(int i)
        {
            if (i == _cached) return _buf;
            var (ofs, len) = blocks[i];
            var z = new byte[len];
            src.Read(ofs, z);
            byte[] d;
            if (raw || (len > 0 && (z[0] & 0x0F) == 0x0F && z[0] != 0x78)) d = raw ? z : z[1..];
            else d = Inflate(z);
            _cached = i; _buf = d;
            return d;
        }

        public int Read(long offset, Span<byte> buffer)
        {
            if (offset >= Length) return 0;
            int want = (int)Math.Min(buffer.Length, Length - offset), done = 0;
            while (done < want)
            {
                long p = offset + done;
                int bi = (int)(p / Chunk);
                if (bi >= blocks.Length) break;
                var blk = Block(bi);
                int inBlk = (int)(p % Chunk);
                int n = Math.Min(want - done, blk.Length - inBlk);
                if (n <= 0) break;
                blk.AsSpan(inBlk, n).CopyTo(buffer[done..]);
                done += n;
            }
            if (done < want) buffer[done..want].Clear();
            return want;
        }
    }
}
