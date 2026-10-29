using System.Text;
using LibertyX.Partition.Core.IO;
using LibertyX.Partition.Core.Recovery;
using static LibertyX.Partition.Core.IO.Bin;

namespace LibertyX.Partition.Core.FileSystems;

public sealed class ExtInode
{
    public uint Number;
    public ushort Mode;
    public long Size;
    public uint Mtime, Ctime, Dtime, Crtime;
    public ushort Links;
    public uint Flags;
    public byte[] Block = new byte[60];
    public bool IsDir => (Mode & 0xF000) == 0x4000;
    public bool IsFile => (Mode & 0xF000) == 0x8000;
    public bool IsSymlink => (Mode & 0xF000) == 0xA000;
    public bool Deleted => Links == 0 || Dtime != 0;
    public bool UsesExtents => (Flags & 0x80000) != 0;
    public bool InlineData => (Flags & 0x10000000) != 0;
    /// <summary>O mapa de blocos ainda existe (ext3/ext4 o apagam ao excluir).</summary>
    public bool HasMapping
    {
        get
        {
            if (InlineData) return Size > 0;
            if (UsesExtents) return U16(Block, 0) == 0xF30A && U16(Block, 2) > 0;
            for (int i = 0; i < 15; i++) if (U32(Block, i * 4) != 0) return true;
            return false;
        }
    }
}

public readonly record struct ExtRun(long Logical, long Physical, long Length, bool Unwritten);

/// <summary>
/// ext2/ext3/ext4 lido direto dos setores — o Windows não abre partições Linux. Lê a árvore viva
/// e, para apagados, procura no journal (jbd2) a última cópia do inode de antes da exclusão:
/// é como o extundelete devolve arquivos de ext3/ext4, que zeram o mapa de blocos ao apagar.
/// </summary>
public sealed class ExtVolume
{
    public IBlockDevice Dev { get; }
    public FsDetect.ExtInfo Info { get; private set; } = null!;
    public long BlockSize => Info.BlockSize;
    public uint InodesCount { get; private set; }
    public uint InodesPerGroup { get; private set; }
    public int InodeSize { get; private set; }
    public bool Is64 { get; private set; }
    public bool FileType { get; private set; }
    public bool UsedBackupSuperblock { get; private set; }
    private long[] _inodeTable = [], _blockBitmap = [];
    private ushort[] _bgFlags = [];
    private readonly Dictionary<long, byte[]> _cache = [];

    private ExtVolume(IBlockDevice dev) { Dev = dev; }

    public static ExtVolume? Open(IBlockDevice dev, out string? error)
    {
        error = null;
        var v = new ExtVolume(dev);
        byte[] sb = dev.ReadBytes(1024, 1024);
        var info = FsDetect.ParseExt(sb);
        if (info == null)
        {
            // superbloco destruído: tenta as cópias do grupo 1 nos tamanhos de bloco comuns
            foreach (var (bs, bpg, fdb) in new[] { (4096L, 32768L, 0L), (1024L, 8192L, 1L), (2048L, 16384L, 0L) })
            {
                long ofs = (bpg + fdb) * bs;
                if (ofs + 1024 > dev.Length) continue;
                var c = dev.ReadBytes(ofs, 1024);
                var ci = FsDetect.ParseExt(c);
                if (ci != null && ci.Group == 1) { sb = c; info = ci; v.UsedBackupSuperblock = true; break; }
            }
        }
        if (info == null) { error = L.T("Superbloco ext não encontrado (nem as cópias de reserva)."); return null; }
        v.Info = info;
        v.InodesCount = U32(sb, 0);
        v.InodesPerGroup = U32(sb, 40);
        v.InodeSize = U32(sb, 76) >= 1 ? U16(sb, 88) : 128;
        uint incompat = U32(sb, 96);
        v.Is64 = (incompat & 0x80) != 0;
        v.FileType = (incompat & 0x2) != 0;
        int descSize = v.Is64 ? Math.Max(32, (int)U16(sb, 0xFE)) : 32;
        long groups = (info.Blocks - info.FirstDataBlock + info.BlocksPerGroup - 1) / info.BlocksPerGroup;
        if (v.InodeSize < 128 || v.InodeSize > 4096 || v.InodesPerGroup == 0 || groups <= 0 || groups > 50_000_000) { error = L.T("Parâmetros ext inválidos."); return null; }
        long gdtOfs = (info.FirstDataBlock + 1) * info.BlockSize;
        if (v.UsedBackupSuperblock) gdtOfs = (info.BlocksPerGroup + info.FirstDataBlock + 1) * info.BlockSize;
        var gdt = dev.ReadBytes(gdtOfs, (int)Math.Min(groups * descSize, 64L << 20));
        int n = (int)Math.Min(groups, gdt.Length / descSize);
        v._inodeTable = new long[n]; v._blockBitmap = new long[n]; v._bgFlags = new ushort[n];
        for (int g = 0; g < n; g++)
        {
            int o = g * descSize;
            v._blockBitmap[g] = U32(gdt, o) | (v.Is64 && descSize >= 64 ? (long)U32(gdt, o + 0x20) << 32 : 0);
            v._inodeTable[g] = U32(gdt, o + 8) | (v.Is64 && descSize >= 64 ? (long)U32(gdt, o + 0x28) << 32 : 0);
            v._bgFlags[g] = U16(gdt, o + 0x12);
        }
        return v;
    }

    // ───────── inodes ─────────

    private long InodeOffset(uint n)
    {
        if (n == 0 || n > InodesCount) return -1;
        long g = (n - 1) / InodesPerGroup, idx = (n - 1) % InodesPerGroup;
        if (g >= _inodeTable.Length || _inodeTable[g] == 0) return -1;
        return _inodeTable[g] * BlockSize + idx * InodeSize;
    }

    /// <summary>Bloco do sistema de arquivos que contém o inode (para achá-lo no journal).</summary>
    public long InodeBlock(uint n, out int offsetInBlock)
    {
        long o = InodeOffset(n);
        offsetInBlock = o < 0 ? 0 : (int)(o % BlockSize);
        return o < 0 ? -1 : o / BlockSize;
    }

    private byte[]? Chunk(long ofs, int len)
    {
        const int C = 64 * 1024;
        long key = ofs / C;
        if (!_cache.TryGetValue(key, out var buf))
        {
            if (_cache.Count > 512) _cache.Clear();
            buf = Dev.ReadBytes(key * C, C);
            _cache[key] = buf;
        }
        int within = (int)(ofs - key * C);
        if (within + len > buf.Length) return Dev.ReadBytes(ofs, len);
        return buf.AsSpan(within, len).ToArray();
    }

    public ExtInode? ReadInode(uint n)
    {
        long o = InodeOffset(n);
        if (o < 0) return null;
        var raw = Chunk(o, Math.Min(InodeSize, 256));
        return raw == null || raw.Length < 128 ? null : ParseInode(raw, n);
    }

    public static ExtInode ParseInode(ReadOnlySpan<byte> r, uint n)
    {
        var i = new ExtInode
        {
            Number = n, Mode = U16(r, 0), Size = U32(r, 4) | ((long)U32(r, 108) << 32),
            Ctime = U32(r, 12), Mtime = U32(r, 16), Dtime = U32(r, 20), Links = U16(r, 26), Flags = U32(r, 32),
        };
        r.Slice(40, 60).CopyTo(i.Block);
        if (r.Length >= 0x94 && U16(r, 128) >= 0x14) i.Crtime = U32(r, 0x90);
        return i;
    }

    // ───────── mapa de blocos ─────────

    public List<ExtRun> Map(ExtInode ino)
    {
        var runs = new List<ExtRun>();
        long maxBlocks = (ino.Size + BlockSize - 1) / BlockSize;
        if (ino.UsesExtents) WalkExtents(ino.Block, runs, 0);
        else
        {
            long logical = 0;
            void Add(long phys) { if (logical < maxBlocks) { if (phys != 0) runs.Add(new ExtRun(logical, phys, 1, false)); logical++; } }
            for (int k = 0; k < 12; k++) Add(U32(ino.Block, k * 4));
            WalkIndirect(U32(ino.Block, 48), 1, ref logical, maxBlocks, runs);
            WalkIndirect(U32(ino.Block, 52), 2, ref logical, maxBlocks, runs);
            WalkIndirect(U32(ino.Block, 56), 3, ref logical, maxBlocks, runs);
        }
        // junta vizinhos
        var merged = new List<ExtRun>();
        foreach (var r in runs.OrderBy(r => r.Logical))
        {
            if (merged.Count > 0 && merged[^1].Logical + merged[^1].Length == r.Logical && merged[^1].Physical + merged[^1].Length == r.Physical && merged[^1].Unwritten == r.Unwritten)
                merged[^1] = merged[^1] with { Length = merged[^1].Length + r.Length };
            else merged.Add(r);
        }
        return merged;
    }

    private void WalkExtents(ReadOnlySpan<byte> node, List<ExtRun> runs, int guard)
    {
        if (guard > 6 || node.Length < 12 || U16(node, 0) != 0xF30A) return;
        int entries = U16(node, 2), depth = U16(node, 6);
        for (int e = 0; e < entries && 12 + e * 12 + 12 <= node.Length; e++)
        {
            int o = 12 + e * 12;
            if (depth == 0)
            {
                int len = U16(node, o + 4);
                bool unwritten = len > 32768;
                if (unwritten) len -= 32768;
                long phys = ((long)U16(node, o + 6) << 32) | U32(node, o + 8);
                if (len > 0 && phys > 0 && phys < Info.Blocks) runs.Add(new ExtRun(U32(node, o), phys, len, unwritten));
            }
            else
            {
                long child = ((long)U16(node, o + 8) << 32) | U32(node, o + 4);
                if (child <= 0 || child >= Info.Blocks) continue;
                var blk = Dev.ReadBytes(child * BlockSize, (int)BlockSize);
                WalkExtents(blk, runs, guard + 1);
            }
        }
    }

    private void WalkIndirect(long block, int level, ref long logical, long maxBlocks, List<ExtRun> runs)
    {
        long per = BlockSize / 4;
        long span = level == 1 ? per : level == 2 ? per * per : per * per * per;
        if (logical >= maxBlocks) return;
        if (block == 0 || block >= Info.Blocks) { logical += span; return; }
        var b = Dev.ReadBytes(block * BlockSize, (int)BlockSize);
        for (int k = 0; k < per && logical < maxBlocks; k++)
        {
            long p = U32(b, k * 4);
            if (level == 1) { if (p != 0) runs.Add(new ExtRun(logical, p, 1, false)); logical++; }
            else WalkIndirect(p, level - 1, ref logical, maxBlocks, runs);
        }
    }

    public IDataSource DataSource(ExtInode ino)
    {
        if (ino.InlineData) return new ResidentSource(ino.Block.AsSpan(0, (int)Math.Min(60, ino.Size)).ToArray());
        var runs = Map(ino);
        var ext = new List<Extent>();
        long cur = 0;
        foreach (var r in runs)
        {
            if (r.Logical > cur) ext.Add(new Extent(-1, (r.Logical - cur) * BlockSize));
            ext.Add(new Extent(r.Unwritten ? -1 : r.Physical * BlockSize, r.Length * BlockSize));
            cur = r.Logical + r.Length;
        }
        long total = (ino.Size + BlockSize - 1) / BlockSize;
        if (cur < total) ext.Add(new Extent(-1, (total - cur) * BlockSize));
        return new ExtentSource(Dev, ext, ino.Size);
    }

    // ───────── diretórios ─────────

    public readonly record struct DirEntry(uint Inode, string Name, byte Type, bool Deleted);

    /// <summary>
    /// Entradas da pasta. Com <paramref name="history"/>, junta as versões antigas de cada bloco
    /// guardadas no journal: o Linux moderno apaga a entrada (nome e inode) junto com o arquivo,
    /// e só essas cópias ainda dizem o que existia ali.
    /// </summary>
    public List<DirEntry> ReadDirectory(ExtInode dir, bool history = false)
    {
        var current = new List<DirEntry>();
        if (dir.InlineData) { ParseDirBlock(dir.Block.AsSpan(4), current); return current; }
        var runs = Map(dir);
        var blocks = new List<long>();
        foreach (var r in runs) for (long k = 0; k < r.Length && blocks.Count < 16384; k++) blocks.Add(r.Physical + k);
        foreach (var b in blocks)
        {
            var buf = Dev.ReadBytes(b * BlockSize, (int)BlockSize);
            ParseDirBlock(buf, current);
        }
        if (!history) return current;
        LoadJournal();
        var live = current.Where(e => !e.Deleted).Select(e => (e.Name, e.Inode)).ToHashSet();
        var seen = current.Select(e => (e.Name, e.Inode)).ToHashSet();
        var res = new List<DirEntry>(current);
        if (_journal == null) return res;
        foreach (var b in blocks)
        {
            if (!_journal.TryGetValue(b, out var copies)) continue;
            foreach (var (_, jb) in copies.OrderByDescending(c => c.seq))
            {
                var old = JRead(jb);
                if (old == null) continue;
                var tmp = new List<DirEntry>();
                ParseDirBlock(old, tmp);
                foreach (var e in tmp)
                    if (!live.Contains((e.Name, e.Inode)) && seen.Add((e.Name, e.Inode)))
                        res.Add(e with { Deleted = true });
            }
        }
        return res;
    }

    public bool HasJournal { get { LoadJournal(); return JournalCopies > 0; } }

    private bool Plausible(ReadOnlySpan<byte> d, int o, int maxLen)
    {
        if (o + 8 > d.Length) return false;
        uint ino = U32(d, o);
        int rec = U16(d, o + 4), nl = d[o + 6], ft = d[o + 7];
        if (ino == 0 || ino > InodesCount || nl == 0 || 8 + nl > maxLen || (FileType && ft > 7)) return false;
        if (rec < 8 + nl || rec % 4 != 0) return false;
        for (int k = 0; k < nl; k++) { byte c = d[o + 8 + k]; if (c == 0 || c == '/') return false; }
        return true;
    }

    private void ParseDirBlock(ReadOnlySpan<byte> d, List<DirEntry> res)
    {
        int o = 0;
        while (o + 8 <= d.Length)
        {
            uint ino = U32(d, o);
            int rec = U16(d, o + 4), nl = d[o + 6];
            if (rec < 8 || o + rec > d.Length) break;
            if (ino != 0 && nl > 0 && 8 + nl <= rec)
            {
                string name = Encoding.UTF8.GetString(d.Slice(o + 8, nl));
                if (name is not ("." or "..")) res.Add(new DirEntry(ino, name, d[o + 7], false));
            }
            // a sobra (slack) de cada entrada guarda as que foram apagadas depois dela
            int used = ino == 0 ? 0 : (8 + nl + 3) & ~3;
            for (int s = o + Math.Max(used, 8); s + 12 <= o + rec; s += 4)
            {
                if (!Plausible(d, s, o + rec - s)) continue;
                int snl = d[s + 6];
                string sname = Encoding.UTF8.GetString(d.Slice(s + 8, snl));
                if (sname is not ("." or "..")) res.Add(new DirEntry(U32(d, s), sname, d[s + 7], true));
                s += ((8 + snl + 3) & ~3) - 4;
            }
            o += rec;
        }
    }

    // ───────── alocação ─────────

    public byte[] LoadBlockBitmap()
    {
        var bmp = new byte[(Info.Blocks + 7) / 8];
        long bpg = Info.BlocksPerGroup;
        for (int g = 0; g < _blockBitmap.Length; g++)
        {
            if ((_bgFlags[g] & 0x2) != 0 || _blockBitmap[g] == 0) continue; // BLOCK_UNINIT: grupo nunca usado
            var b = Dev.ReadBytes(_blockBitmap[g] * BlockSize, (int)Math.Min(BlockSize, (bpg + 7) / 8));
            long first = Info.FirstDataBlock + g * bpg;
            for (long k = 0; k < bpg && first + k < Info.Blocks && k / 8 < b.Length; k++)
                if ((b[k / 8] & (1 << (int)(k % 8))) != 0) bmp[(first + k) / 8] |= (byte)(1 << (int)((first + k) % 8));
        }
        return bmp;
    }

    // ───────── journal ─────────

    private Dictionary<long, List<(uint seq, long jblock)>>? _journal;
    private List<ExtRun>? _jmap;
    private long _jlen;
    public int JournalCopies { get; private set; }

    /// <summary>Indexa o journal: para cada bloco do sistema de arquivos, as cópias guardadas (transação, posição).</summary>
    public void LoadJournal()
    {
        if (_journal != null) return;
        _journal = [];
        var ji = ReadInode(8);
        if (ji == null || ji.Size < BlockSize * 4 || !ji.HasMapping) return;
        _jmap = Map(ji);
        _jlen = ji.Size / BlockSize;
        var sb = JRead(0);
        if (sb == null || U32BE(sb, 0) != 0xC03B3998) return;
        uint incompat = U32BE(sb, 40);
        bool is64 = (incompat & 0x2) != 0, csum3 = (incompat & 0x10) != 0, csum2 = (incompat & 0x8) != 0;
        int tagSize = csum3 ? 16 : 8 + (is64 ? 4 : 0);
        long first = U32BE(sb, 20);
        if (first <= 0 || first >= _jlen) first = 1;
        for (long j = first; j < _jlen; j++)
        {
            var b = JRead(j);
            if (b == null || U32BE(b, 0) != 0xC03B3998 || U32BE(b, 4) != 1) continue;
            uint seq = U32BE(b, 8);
            int o = 12;
            long data = j;
            while (o + tagSize <= b.Length - (csum2 || csum3 ? 4 : 0))
            {
                long fsBlock = U32BE(b, o);
                uint flags = csum3 ? U32BE(b, o + 4) : U16BE(b, o + 6);
                if (is64) fsBlock |= (long)U32BE(b, o + (csum3 ? 8 : 8)) << 32;
                data = data + 1 >= _jlen ? first : data + 1;
                if (!_journal.TryGetValue(fsBlock, out var l)) _journal[fsBlock] = l = [];
                l.Add((seq, data));
                JournalCopies++;
                o += tagSize;
                if ((flags & 0x2) == 0) o += 16;   // sem SAME_UUID: segue o UUID
                if ((flags & 0x8) != 0) break;      // LAST_TAG
            }
        }
    }

    private byte[]? JRead(long j)
    {
        if (_jmap == null) return null;
        foreach (var r in _jmap)
            if (j >= r.Logical && j < r.Logical + r.Length)
                return Dev.ReadBytes((r.Physical + j - r.Logical) * BlockSize, (int)BlockSize);
        return null;
    }

    /// <summary>A versão mais recente do inode, guardada no journal, que ainda tinha mapa de blocos.</summary>
    public ExtInode? HistoricInode(uint n, ushort expectedType = 0)
    {
        LoadJournal();
        long blk = InodeBlock(n, out int within);
        if (blk < 0 || _journal == null || !_journal.TryGetValue(blk, out var copies)) return null;
        foreach (var (_, jb) in copies.OrderByDescending(c => c.seq))
        {
            var b = JRead(jb);
            if (b == null || within + 128 > b.Length) continue;
            var ino = ParseInode(b.AsSpan(within, Math.Min(InodeSize, b.Length - within)), n);
            if (ino.Links > 0 && ino.Dtime == 0 && ino.HasMapping && ino.Size > 0 && (expectedType == 0 || (ino.Mode & 0xF000) == expectedType))
                return ino;
        }
        return null;
    }

    public static DateTime? Time(uint t) => t == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(t).UtcDateTime;
}
