using System.Text;
using LibertyX.Partition.Core.IO;
using LibertyX.Partition.Core.Recovery;
using static LibertyX.Partition.Core.IO.Bin;

namespace LibertyX.Partition.Core.FileSystems;

/// <summary>Inode do XFS (v1/v2 e v3 com CRC), já com o garfo de dados à mão.</summary>
public sealed class XfsInode
{
    public ulong Ino { get; init; }
    public ushort Mode { get; init; }
    public byte Version { get; init; }
    public byte Format { get; init; }       // 0 dev, 1 local, 2 extents, 3 btree
    public ulong Size { get; init; }
    public uint NExtents { get; init; }
    public uint Generation { get; init; }
    public DateTime? Mtime { get; init; }
    public DateTime? Ctime { get; init; }
    public DateTime? Crtime { get; init; }
    public required byte[] Raw { get; init; }
    public int ForkOffset => Version == 3 ? 176 : 100;
    public int ForkSize { get; init; }
    public bool Free => Mode == 0;
    public bool IsDir => (Mode & 0xF000) == 0x4000;
    public bool IsFile => (Mode & 0xF000) == 0x8000;
    public bool IsLink => (Mode & 0xF000) == 0xA000;
    public ReadOnlySpan<byte> Fork => Raw.AsSpan(ForkOffset, ForkSize);
}

/// <summary>
/// Leitor de XFS direto dos setores: superbloco, inodes, extents (lista e árvore B+), pastas
/// (curtas, em bloco, folha e nó) e o mapa de espaço livre (árvore B+ de blocos livres de cada
/// grupo de alocação). Lê também o que sobra de entradas apagadas nos blocos de pasta: o XFS
/// marca a entrada como livre por cima dos 4 primeiros bytes, mas o nome, a metade baixa do
/// número do inode e a etiqueta de posição continuam lá.
/// </summary>
public sealed class XfsVolume
{
    public IBlockDevice Dev { get; }
    public long BlockSize { get; private set; }
    public uint AgBlocks { get; private set; }
    public uint AgCount { get; private set; }
    public int AgBlkLog { get; private set; }
    public int InoPbLog { get; private set; }
    public int InodeSize { get; private set; }
    public int SectSize { get; private set; }
    public ulong RootIno { get; private set; }
    public long DBlocks { get; private set; }
    public bool V5 { get; private set; }
    public bool FType { get; private set; }
    public bool BigTime { get; private set; }
    public int DirBlkLog { get; private set; }
    public string Label { get; private set; } = "";
    public ulong LogStart { get; private set; }
    public uint LogBlocks { get; private set; }
    public bool UsedSecondarySuperblock { get; private set; }
    public long DirBlockSize => BlockSize << DirBlkLog;

    private XfsVolume(IBlockDevice dev) => Dev = dev;

    public static XfsVolume? Open(IBlockDevice dev, out string? err)
    {
        err = null;
        var v = new XfsVolume(dev);
        if (v.Parse(dev.ReadBytes(0, 512))) return v;
        // superbloco principal destruído: as cópias ficam no início de cada grupo de alocação
        for (long probe = 1 << 20; probe < Math.Min(dev.Length, 64L << 30); probe += 1 << 20)
        {
            var b = dev.ReadBytes(probe, 512);
            if (b.Length < 512 || !Match(b, 0, "XFSB"u8)) continue;
            var c = new XfsVolume(dev);
            if (c.Parse(b) && probe % (c.AgBlocks * c.BlockSize) == 0) { c.UsedSecondarySuperblock = true; return c; }
        }
        err = "Superbloco XFS ilegível.";
        return null;
    }

    private bool Parse(ReadOnlySpan<byte> sb)
    {
        if (sb.Length < 256 || !Match(sb, 0, "XFSB"u8)) return false;
        BlockSize = U32BE(sb, 4);
        DBlocks = (long)U64BE(sb, 8);
        LogStart = U64BE(sb, 48);
        RootIno = U64BE(sb, 56);
        AgBlocks = U32BE(sb, 84);
        AgCount = U32BE(sb, 88);
        LogBlocks = U32BE(sb, 96);
        int ver = U16BE(sb, 100) & 0xF;
        SectSize = U16BE(sb, 102);
        InodeSize = U16BE(sb, 104);
        Label = Encoding.UTF8.GetString(sb.Slice(108, 12)).TrimEnd('\0', ' ');
        InoPbLog = sb[123];
        AgBlkLog = sb[124];
        DirBlkLog = sb[192];
        V5 = ver >= 5;
        uint features2 = U32BE(sb, 200), incompat = V5 ? U32BE(sb, 216) : 0;
        FType = V5 ? (incompat & 1) != 0 : (features2 & 0x200) != 0;
        BigTime = V5 && (incompat & 0x8) != 0;
        return BlockSize is >= 512 and <= 65536 && AgBlocks > 0 && AgCount > 0 && InodeSize is >= 256 and <= 2048 && AgBlkLog is > 0 and < 32 && RootIno > 0;
    }

    public long FsbToByte(ulong fsb) => ((long)(fsb >> AgBlkLog) * AgBlocks + (long)(fsb & ((1UL << AgBlkLog) - 1))) * BlockSize;
    public long AgbToByte(uint agno, uint agbno) => ((long)agno * AgBlocks + agbno) * BlockSize;

    public long InoToByte(ulong ino)
    {
        ulong agno = ino >> (AgBlkLog + InoPbLog);
        ulong agbno = (ino >> InoPbLog) & ((1UL << AgBlkLog) - 1);
        ulong idx = ino & ((1UL << InoPbLog) - 1);
        return ((long)agno * AgBlocks + (long)agbno) * BlockSize + (long)idx * InodeSize;
    }

    public XfsInode? ReadInode(ulong ino)
    {
        long ofs = InoToByte(ino);
        if (ofs < 0 || ofs + InodeSize > Dev.Length) return null;
        var b = Dev.ReadBytes(ofs, InodeSize);
        return ParseInode(b, ino);
    }

    public XfsInode? ParseInode(byte[] b, ulong ino)
    {
        if (b.Length < 176 || b[0] != (byte)'I' || b[1] != (byte)'N') return null;
        byte ver = b[4];
        if (ver is < 1 or > 3) return null;
        int forkOff = ver == 3 ? 176 : 100;
        int afork = b[82];
        int forkSize = afork == 0 ? InodeSize - forkOff : afork * 8;
        if (forkSize <= 0 || forkOff + forkSize > b.Length) forkSize = Math.Max(0, b.Length - forkOff);
        ulong flags2 = ver == 3 ? U64BE(b, 120) : 0;
        bool big = BigTime && (flags2 & 0x8) != 0;
        // contadores de extents de 64 bits (nrext64): o número fica no antigo enchimento, no byte 24
        uint nextents = (flags2 & 0x10) != 0 ? (uint)Math.Min(U64BE(b, 24), uint.MaxValue) : U32BE(b, 76);
        return new XfsInode
        {
            Ino = ino, Mode = U16BE(b, 2), Version = ver, Format = b[5], Size = U64BE(b, 56), NExtents = nextents, Generation = U32BE(b, 92),
            Mtime = Time(b, 40, big), Ctime = Time(b, 48, big), Crtime = ver == 3 ? Time(b, 144, big) : null, Raw = b, ForkSize = forkSize,
        };
    }

    private static DateTime? Time(ReadOnlySpan<byte> b, int o, bool big)
    {
        try
        {
            if (big)
            {
                ulong ns = U64BE(b, o);
                if (ns == 0) return null;
                return DateTime.UnixEpoch.AddSeconds((double)(ns / 1_000_000_000) - 2147483648.0).AddTicks((long)(ns % 1_000_000_000) / 100);
            }
            int sec = (int)U32BE(b, o);
            if (sec == 0) return null;
            return DateTime.UnixEpoch.AddSeconds(sec).AddTicks(U32BE(b, o + 4) / 100);
        }
        catch { return null; }
    }

    // ───────── extents ─────────

    public readonly record struct Bmbt(ulong Off, ulong Block, ulong Count, bool Unwritten);

    private static Bmbt DecodeExtent(ReadOnlySpan<byte> r)
    {
        ulong l0 = U64BE(r, 0), l1 = U64BE(r, 8);
        return new Bmbt((l0 & 0x7FFFFFFFFFFFFFFFUL) >> 9, ((l0 & 0x1FF) << 43) | (l1 >> 21), l1 & 0x1FFFFF, (l0 >> 63) != 0);
    }

    /// <summary>Lista de extents do garfo de dados (lista direta ou árvore B+ com raiz no inode).</summary>
    public List<Bmbt> Extents(XfsInode ino, ReadOnlySpan<byte> forkOverride = default, int nextentsOverride = -1, int formatOverride = -1)
    {
        var list = new List<Bmbt>();
        var fork = forkOverride.Length > 0 ? forkOverride : ino.Fork;
        int fmt = formatOverride >= 0 ? formatOverride : ino.Format;
        if (fmt == 2)
        {
            int n = nextentsOverride >= 0 ? nextentsOverride : (int)ino.NExtents;
            for (int i = 0; i < n && (i + 1) * 16 <= fork.Length; i++)
            {
                var e = DecodeExtent(fork.Slice(i * 16, 16));
                if (e.Count > 0) list.Add(e);
            }
        }
        else if (fmt == 3 && fork.Length >= 4)
        {
            int level = U16BE(fork, 0), nrecs = U16BE(fork, 2);
            int maxrecs = (fork.Length - 4) / 16;
            for (int i = 0; i < nrecs && i < maxrecs; i++)
            {
                ulong ptr = U64BE(fork, 4 + maxrecs * 8 + i * 8);
                Walk(ptr, level - 1, list, 0);
            }
        }
        return list;
    }

    private void Walk(ulong fsb, int level, List<Bmbt> list, int depth)
    {
        if (depth > 8 || level < 0) return;
        byte[] blk;
        try { blk = Dev.ReadBytes(FsbToByte(fsb), (int)BlockSize); } catch (MediaReadException) { return; }
        if (blk.Length < 24) return;
        bool v5 = Match(blk, 0, "BMA3"u8);
        if (!v5 && !Match(blk, 0, "BMAP"u8)) return;
        int hdr = v5 ? 72 : 24;
        int lvl = U16BE(blk, 4), n = U16BE(blk, 6);
        if (lvl == 0)
        {
            for (int i = 0; i < n && hdr + (i + 1) * 16 <= blk.Length; i++)
            {
                var e = DecodeExtent(blk.AsSpan(hdr + i * 16, 16));
                if (e.Count > 0) list.Add(e);
            }
            return;
        }
        int maxrecs = (blk.Length - hdr) / 16;
        for (int i = 0; i < n && i < maxrecs; i++) Walk(U64BE(blk, hdr + maxrecs * 8 + i * 8), lvl - 1, list, depth + 1);
    }

    /// <summary>Os bytes do arquivo (buracos viram zeros; extents "não escritos" também).</summary>
    public IDataSource DataSource(XfsInode ino, List<Bmbt>? ext = null)
    {
        if (ino.Format == 1) return new ResidentSource(ino.Fork[..(int)Math.Min((ulong)ino.ForkSize, ino.Size)].ToArray());
        ext ??= Extents(ino);
        var parts = new List<Extent>();
        long cur = 0;
        foreach (var e in ext.OrderBy(e => e.Off))
        {
            long at = (long)e.Off * BlockSize, len = (long)e.Count * BlockSize;
            if (at > cur) parts.Add(new Extent(-1, at - cur));
            parts.Add(e.Unwritten ? new Extent(-1, len) : new Extent(FsbToByte(e.Block), len));
            cur = at + len;
        }
        long size = (long)ino.Size;
        if (size > cur) parts.Add(new Extent(-1, size - cur));
        return new ExtentSource(Dev, parts, size);
    }

    // ───────── pastas ─────────

    public sealed record DirEntry(string Name, ulong Ino, byte FType, bool Deleted, bool InoLowOnly);

    private const long LeafOffset = 32L << 30;   // XFS_DIR2_LEAF_OFFSET: daqui em diante, só índices

    public List<DirEntry> ReadDirectory(XfsInode dir, bool history)
    {
        var res = new List<DirEntry>();
        if (dir.Format == 1) { ReadShortform(dir, res); return res; }
        var ext = Extents(dir);
        long dbs = DirBlockSize;
        foreach (var e in ext)
        {
            long start = (long)e.Off * BlockSize;
            if (start >= LeafOffset || e.Unwritten) continue;
            long len = (long)e.Count * BlockSize;
            for (long o = 0; o + dbs <= len; o += dbs)
            {
                if (start + o >= LeafOffset) break;
                byte[] blk;
                try { blk = Dev.ReadBytes(FsbToByte(e.Block) + o, (int)dbs); } catch (MediaReadException) { continue; }
                ParseDataBlock(blk, res, history);
            }
        }
        return res;
    }

    private void ReadShortform(XfsInode dir, List<DirEntry> res) => ParseShortform(dir.Fork, res);

    public void ParseShortform(ReadOnlySpan<byte> f, List<DirEntry> res)
    {
        if (f.Length < 6) return;
        int count = f[0], i8 = f[1];
        int isz = i8 > 0 ? 8 : 4;
        int p = 2 + isz;
        for (int i = 0; i < Math.Max(count, i8) && p < f.Length; i++)
        {
            int nl = f[p];
            if (nl == 0 || p + 3 + nl + (FType ? 1 : 0) + isz > f.Length) break;
            string name = Encoding.UTF8.GetString(f.Slice(p + 3, nl));
            int q = p + 3 + nl;
            byte ft = FType ? f[q++] : (byte)0;
            ulong ino = isz == 8 ? U64BE(f, q) : U32BE(f, q);
            res.Add(new DirEntry(name, ino, ft, false, false));
            p = q + isz;
        }
    }

    private int DataHeader(ReadOnlySpan<byte> b, out bool block)
    {
        block = Match(b, 0, "XD2B"u8) || Match(b, 0, "XDB3"u8);
        if (Match(b, 0, "XD2B"u8) || Match(b, 0, "XD2D"u8)) return 16;
        if (Match(b, 0, "XDB3"u8) || Match(b, 0, "XDD3"u8)) return 64;
        return -1;
    }

    private int EntrySize(int nameLen) => (8 + 1 + nameLen + (FType ? 1 : 0) + 2 + 7) & ~7;

    private void ParseDataBlock(byte[] b, List<DirEntry> res, bool history)
    {
        int hdr = DataHeader(b, out bool block);
        if (hdr < 0) return;
        int end = b.Length;
        if (block)
        {
            // pasta pequena num bloco só: no fim, a "cauda" com a contagem de folhas, e as folhas antes dela
            uint count = U32BE(b, b.Length - 8);
            end = b.Length - 8 - (int)Math.Min(count, (uint)(b.Length / 8)) * 8;
        }
        int p = hdr;
        while (p + 8 <= end)
        {
            if (U16BE(b, p) == 0xFFFF)
            {
                int len = U16BE(b, p + 2);
                if (len < 8 || p + len > end) break;
                if (history) Salvage(b, p, p + len, res);
                p += len;
                continue;
            }
            int nl = b[p + 8];
            int size = EntrySize(nl);
            if (nl == 0 || p + size > end) break;
            if (U16BE(b, p + size - 2) != p) { p += 8; continue; }   // etiqueta não bate: fora de sincronia
            string name = Encoding.UTF8.GetString(b, p + 9, nl);
            byte ft = FType ? b[p + 9 + nl] : (byte)0;
            if (name is not ("." or "..")) res.Add(new DirEntry(name, U64BE(b, p), ft, false, false));
            p += size;
        }
    }

    /// <summary>
    /// Num trecho livre do bloco de pasta podem estar entradas apagadas inteiras: cada uma termina
    /// na etiqueta com a própria posição, o que separa entrada de lixo com segurança.
    /// </summary>
    private void Salvage(byte[] b, int from, int to, List<DirEntry> res)
    {
        for (int q = from; q + 16 <= to; q += 8)
        {
            int nl = b[q + 8];
            if (nl == 0) continue;
            int size = EntrySize(nl);
            if (q + size > to) continue;
            // a etiqueta é a posição da própria entrada — ou, se o XFS fundiu o trecho livre com a
            // entrada apagada antes dela, a posição do início do trecho
            int tag = U16BE(b, q + size - 2);
            if (tag != q && tag != from) continue;
            if (q != from && !PlausibleIno(U64BE(b, q))) continue;
            var nameBytes = b.AsSpan(q + 9, nl);
            if (nameBytes.IndexOf((byte)0) >= 0 || nameBytes.IndexOf((byte)'/') >= 0) continue;
            string name;
            try { name = new UTF8Encoding(false, true).GetString(nameBytes); } catch { continue; }
            if (name is "." or "..") continue;
            bool first = q == from;   // a primeira teve os 4 bytes altos do inode trocados pela marca de livre
            ulong ino = first ? U32BE(b, q + 4) : U64BE(b, q);
            byte ft = FType ? b[q + 9 + nl] : (byte)0;
            res.Add(new DirEntry(name, ino, ft, true, first));
            q += size - 8;
        }
    }

    private bool PlausibleIno(ulong ino)
    {
        if (ino == 0) return false;
        ulong agno = ino >> (AgBlkLog + InoPbLog);
        return agno < AgCount;
    }

    public string? ReadLink(XfsInode ino)
    {
        if (ino.Format == 1) return Encoding.UTF8.GetString(ino.Fork[..(int)Math.Min((ulong)ino.ForkSize, ino.Size)]);
        try
        {
            using var ms = new MemoryStream();
            var src = DataSource(ino);
            var buf = new byte[(int)Math.Min(4096, src.Length)];
            int n = src.Read(0, buf);
            int skip = V5 && n > 56 && Match(buf, 0, "XSLM"u8) ? 56 : 0;
            return Encoding.UTF8.GetString(buf, skip, Math.Max(0, Math.Min(n - skip, (int)ino.Size)));
        }
        catch { return null; }
    }

    // ───────── espaço livre ─────────

    /// <summary>Bit ligado = bloco livre (pela árvore B+ por endereço de cada grupo). Índice linear agno*agblocks+agbno.</summary>
    public byte[] FreeBitmap()
    {
        long total = (long)AgCount * AgBlocks;
        var bmp = new byte[(total + 7) / 8];
        for (uint ag = 0; ag < AgCount; ag++)
        {
            byte[] agf;
            try { agf = Dev.ReadBytes(AgbToByte(ag, 0) + SectSize, 512); } catch (MediaReadException) { continue; }
            if (!Match(agf, 0, "XAGF"u8)) continue;
            uint root = U32BE(agf, 16), level = U32BE(agf, 28);
            WalkFree(ag, root, (int)level - 1, bmp, 0);
        }
        return bmp;
    }

    private void WalkFree(uint ag, uint agbno, int level, byte[] bmp, int depth)
    {
        if (depth > 10 || agbno == 0 || agbno >= AgBlocks) return;
        byte[] blk;
        try { blk = Dev.ReadBytes(AgbToByte(ag, agbno), (int)BlockSize); } catch (MediaReadException) { return; }
        bool v5 = Match(blk, 0, "AB3B"u8);
        if (!v5 && !Match(blk, 0, "ABTB"u8)) return;
        int hdr = v5 ? 56 : 16;
        int lvl = U16BE(blk, 4), n = U16BE(blk, 6);
        if (lvl == 0)
        {
            for (int i = 0; i < n && hdr + (i + 1) * 8 <= blk.Length; i++)
            {
                uint start = U32BE(blk, hdr + i * 8), count = U32BE(blk, hdr + i * 8 + 4);
                long b0 = (long)ag * AgBlocks + start;
                for (long k = 0; k < count && b0 + k < (long)bmp.Length * 8; k++) bmp[(b0 + k) >> 3] |= (byte)(1 << (int)((b0 + k) & 7));
            }
            return;
        }
        int maxrecs = (blk.Length - hdr) / 12;
        for (int i = 0; i < n && i < maxrecs; i++) WalkFree(ag, U32BE(blk, hdr + maxrecs * 8 + i * 4), lvl - 1, bmp, depth + 1);
    }

    public static bool IsFree(byte[] bmp, long b) => b >= 0 && (b >> 3) < bmp.Length && (bmp[b >> 3] & (1 << (int)(b & 7))) != 0;

    /// <summary>Bloco linear (agno*agblocks+agbno) do bloco do sistema de arquivos.</summary>
    public long Linear(ulong fsb) => (long)(fsb >> AgBlkLog) * AgBlocks + (long)(fsb & ((1UL << AgBlkLog) - 1));

    // ───────── log (journal) ─────────

    public sealed record Historic(ulong Ino, ushort Mode, ulong Size, byte Format, byte[] Fork, List<Bmbt> Extents, long Lsn, DateTime? Mtime)
    {
        /// <summary>O log tem uma versão mais nova, ainda com tamanho, com outro mapa (buraco furado, reescrita em árvore):
        /// o mapa usado pode trazer blocos que já não eram do arquivo.</summary>
        public bool Stale { get; set; }
    }
    private readonly Dictionary<ulong, Historic> _latestSized = [];
    private Dictionary<ulong, Historic>? _hist;
    /// <summary>Versões antigas de pastas pequenas (formato curto, dentro do inode) que passaram pelo log.</summary>
    public Dictionary<ulong, List<byte[]>> HistoricDirForks { get; } = [];
    public int LogRecords { get; private set; }

    /// <summary>
    /// Cópias de inodes que passaram pelo log: para cada inode, a versão mais recente que ainda
    /// tinha tipo e extents — o mapa de blocos de um arquivo antes de ele ser apagado.
    /// </summary>
    public Dictionary<ulong, Historic> HistoricInodes()
    {
        if (_hist != null) return _hist;
        _hist = [];
        if (LogStart == 0 || LogBlocks == 0) return _hist;
        long start = FsbToByte(LogStart), len = (long)LogBlocks * BlockSize;
        byte[] log;
        try { log = Dev.ReadBytes(start, (int)Math.Min(len, 256L << 20)); } catch (MediaReadException) { return _hist; }
        for (int bb = 0; bb + 512 <= log.Length; bb += 512)
        {
            if (U32BE(log, bb) != 0xFEEDBABE) continue;
            try { bb = ParseRecord(log, bb) - 512; } catch { }
        }
        foreach (var (ino, h) in _hist)
            if (_latestSized.TryGetValue(ino, out var last) && last.Lsn > h.Lsn && !SameMap(last, h)) h.Stale = true;
        return _hist;
    }

    private static bool SameMap(Historic a, Historic b) =>
        a.Format == b.Format && a.Extents.Count == b.Extents.Count && a.Extents.Zip(b.Extents).All(z => z.First.Off == z.Second.Off && z.First.Block == z.Second.Block && z.First.Count == z.Second.Count);

    private static bool Better(Historic a, Historic b)
    {
        static ulong Mapped(Historic h) => h.Extents.Aggregate(0UL, (t, e) => t + e.Count);
        int sa = a.Size > 0 ? 1 : 0, sb = b.Size > 0 ? 1 : 0;
        if (sa != sb) return sa > sb;
        if ((a.Mode & 0xF000) == 0x8000 && (b.Mode & 0xF000) == 0x8000)
        {
            ulong ma = Mapped(a), mb = Mapped(b);
            if (ma != mb) return ma > mb;
        }
        return a.Lsn >= b.Lsn;
    }

    /// <summary>Lê um registro do log; devolve onde começa o próximo bloco a examinar.</summary>
    private int ParseRecord(byte[] log, int at)
    {
        int ver = (int)U32BE(log, at + 8);
        int hlen = (int)U32BE(log, at + 12);
        long lsn = (long)U64BE(log, at + 16);
        int hsize = ver == 2 ? (int)U32BE(log, at + 320) : 32768;   // h_size: tamanho do buffer de log
        if (hlen <= 0 || hlen > 1 << 24) return at + 512;
        int bbs = (hlen + 511) / 512;
        // cabeçalhos estendidos (log v2 com buffer > 32 KiB): mais blocos de dados de ciclo
        int extHdrs = ver == 2 && hsize > 32768 ? (hsize + 32767) / 32768 - 1 : 0;
        int dataAt = at + 512 * (1 + extHdrs);
        if (dataAt + bbs * 512 > log.Length) return at + 512;
        var data = new byte[bbs * 512];
        Buffer.BlockCopy(log, dataAt, data, 0, data.Length);
        // cada bloco do registro teve os 4 primeiros bytes trocados pelo número do ciclo; o original está no cabeçalho
        for (int i = 0; i < bbs; i++)
        {
            int h = i / 64, j = i % 64;
            int src = h == 0 ? at + 44 + j * 4 : at + 512 * h + 4 + j * 4;
            if (src + 4 > log.Length) break;
            Buffer.BlockCopy(log, src, data, i * 512, 4);
        }
        LogRecords++;
        ParseOps(data, hlen, lsn);
        return dataAt + bbs * 512;
    }

    private void ParseOps(byte[] d, int len, long lsn)
    {
        int p = 0;
        var pend = new Dictionary<uint, List<byte[]>>();   // regiões do item em montagem, por transação
        var expect = new Dictionary<uint, int>();
        while (p + 12 <= len)
        {
            uint tid = U32BE(d, p);
            int olen = (int)U32BE(d, p + 4);
            byte flags = d[p + 9];
            p += 12;
            if (olen < 0 || p + olen > len) break;
            var region = d.AsSpan(p, olen).ToArray();
            p += olen;
            if ((flags & 0x02) != 0 || (flags & 0x20) != 0) { pend.Remove(tid); expect.Remove(tid); continue; }   // commit / unmount
            if (region.Length >= 4 && U32BE(region, 0) == 0x5452414E) continue;                                // cabeçalho de transação
            // início de um item de inode: tipo 0x123b, no endian da máquina que gravou
            if (region.Length >= 56 && (U16(region, 0) == 0x123B || U16BE(region, 0) == 0x123B))
            {
                bool le = U16(region, 0) == 0x123B;
                int nreg = le ? U16(region, 2) : U16BE(region, 2);
                pend[tid] = [region];
                expect[tid] = nreg;
                continue;
            }
            if (pend.TryGetValue(tid, out var parts))
            {
                parts.Add(region);
                if (parts.Count >= expect[tid]) { Inode(parts, lsn); pend.Remove(tid); }
            }
        }
    }

    private void Inode(List<byte[]> parts, long lsn)
    {
        var f = parts[0];
        bool le = U16(f, 0) == 0x123B;
        uint fields = le ? U32(f, 4) : U32BE(f, 4);
        ulong ino = le ? U64(f, 16) : U64BE(f, 16);
        if (parts.Count < 2) return;
        var core = parts[1];
        if (core.Length < 100) return;
        ushort Mode() => le ? U16(core, 2) : U16BE(core, 2);
        ushort mode = Mode();
        byte ver = core[4], format = core[5];
        ulong size = le ? U64(core, 56) : U64BE(core, 56);
        ulong flags2 = ver == 3 && core.Length >= 128 ? (le ? U64(core, 120) : U64BE(core, 120)) : 0;
        long n = (flags2 & 0x10) != 0 ? (long)(le ? U64(core, 24) : U64BE(core, 24)) : (le ? U32(core, 76) : U32BE(core, 76));
        if (mode == 0 || (mode & 0xF000) is not (0x8000 or 0x4000) || format is not (1 or 2 or 3)) return;
        if ((fields & (0x2 | 0x4 | 0x8)) == 0 || parts.Count < 3) return;   // sem garfo de dados no item
        var fork = parts[2];
        var ext = format == 1 ? [] : Extents(null!, fork, (int)Math.Min(n, fork.Length / 16), format);
        if (format != 1 && ext.Count == 0) return;
        if (format == 1 && (mode & 0xF000) == 0x4000)
        {
            if (!HistoricDirForks.TryGetValue(ino, out var forks)) HistoricDirForks[ino] = forks = [];
            if (!forks.Any(x => x.AsSpan().SequenceEqual(fork))) forks.Add(fork);
        }
        DateTime? mt = null;
        try
        {
            bool big = BigTime && (flags2 & 0x8) != 0;
            if (big) { ulong ns = le ? U64(core, 40) : U64BE(core, 40); if (ns > 0) mt = DateTime.UnixEpoch.AddSeconds((double)(ns / 1_000_000_000) - 2147483648.0); }
            else { int sec = (int)(le ? U32(core, 40) : U32BE(core, 40)); if (sec > 0) mt = DateTime.UnixEpoch.AddSeconds(sec); }
        }
        catch { }
        // ao apagar, o XFS zera o tamanho e trunca em etapas, cada uma no log: vale a cópia com
        // tamanho e o maior mapa (truncar só diminui); empate, a mais recente
        var cand = new Historic(ino, mode, size, format, fork, ext, lsn, mt);
        if (!_hist!.TryGetValue(ino, out var old) || Better(cand, old)) _hist[ino] = cand;
        if (size > 0 && (!_latestSized.TryGetValue(ino, out var ls) || lsn >= ls.Lsn)) _latestSized[ino] = cand;
    }
}
