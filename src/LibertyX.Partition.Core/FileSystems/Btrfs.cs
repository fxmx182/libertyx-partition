using System.IO.Compression;
using System.Numerics;
using System.Text;
using LibertyX.Partition.Core.IO;
using LibertyX.Partition.Core.Recovery;
using static LibertyX.Partition.Core.IO.Bin;

namespace LibertyX.Partition.Core.FileSystems;

/// <summary>
/// Leitor de Btrfs direto dos setores (um dispositivo; perfis single, DUP e RAID1 pelo primeiro
/// espelho). Árvores B com cópia na escrita: uma versão antiga de cada folha continua no disco até
/// o espaço ser reaproveitado — é daí que voltam os apagados, inclusive com o checksum do próprio
/// Btrfs para provar que o conteúdo é o original.
/// </summary>
public sealed class BtrfsVolume
{
    public IBlockDevice Dev { get; }
    public uint NodeSize { get; private set; }
    public uint SectorSize { get; private set; }
    public ulong Generation { get; private set; }
    public ulong RootTree { get; private set; }
    public int RootLevel { get; private set; }
    public ulong ChunkRoot { get; private set; }
    public int ChunkLevel { get; private set; }
    public ulong DevId { get; private set; }
    public ulong NumDevices { get; private set; }
    public byte[] Fsid { get; private set; } = [];
    public string Label { get; private set; } = "";
    public ushort CsumType { get; private set; }
    public bool UsedMirrorSuperblock { get; private set; }
    public List<(ulong fsRoot, int level, ulong gen)> BackupFsRoots { get; } = [];

    public sealed record Chunk(ulong Logical, ulong Length, ulong Physical, ulong Type);
    public List<Chunk> Chunks { get; } = [];

    private BtrfsVolume(IBlockDevice dev) => Dev = dev;

    public static BtrfsVolume? Open(IBlockDevice dev, out string? err)
    {
        err = null;
        foreach (long at in new[] { 0x10000L, 64L << 20, 256L << 30 })
        {
            if (at + 4096 > dev.Length) break;
            var sb = dev.ReadBytes(at, 4096);
            if (sb.Length < 4096 || !Match(sb, 0x40, "_BHRfS_M"u8)) continue;
            var v = new BtrfsVolume(dev) { UsedMirrorSuperblock = at != 0x10000 };
            if (!v.Parse(sb)) continue;
            try { v.LoadChunks(sb); }
            catch (Exception ex) { err = L.T("Mapa de chunks ilegível: ") + ex.Message; return null; }
            if (v.NumDevices > 1) err = L.T($"Volume Btrfs com {v.NumDevices} dispositivos: só este é lido (perfis RAID entre discos não são montados aqui).");
            return v;
        }
        err = L.T("Superbloco Btrfs ilegível.");
        return null;
    }

    private bool Parse(byte[] sb)
    {
        Fsid = sb.AsSpan(0x20, 16).ToArray();
        Generation = U64(sb, 0x48);
        RootTree = U64(sb, 0x50);
        ChunkRoot = U64(sb, 0x58);
        NumDevices = U64(sb, 0x88);
        SectorSize = U32(sb, 0x90);
        NodeSize = U32(sb, 0x94);
        CsumType = U16(sb, 0xC4);
        RootLevel = sb[0xC6];
        ChunkLevel = sb[0xC7];
        DevId = U64(sb, 0xC9);
        Label = Encoding.UTF8.GetString(sb.AsSpan(0x12B, 256)).TrimEnd('\0');
        for (int i = 0; i < 4; i++)
        {
            int o = 0xB2B + i * 168;
            ulong fsRoot = U64(sb, o + 48), gen = U64(sb, o + 56);
            if (fsRoot != 0) BackupFsRoots.Add((fsRoot, sb[o + 160 + 3], gen));
        }
        return NodeSize is >= 4096 and <= 65536 && SectorSize is >= 512 and <= 65536;
    }

    private void AddChunk(ulong logical, ReadOnlySpan<byte> c)
    {
        ulong length = U64(c, 0), type = U64(c, 24);
        int stripes = U16(c, 44);
        for (int s = 0; s < stripes && 48 + (s + 1) * 32 <= c.Length; s++)
        {
            ulong devid = U64(c, 48 + s * 32), off = U64(c, 48 + s * 32 + 8);
            if (devid != DevId && NumDevices > 1) continue;
            if (!Chunks.Any(x => x.Logical == logical)) Chunks.Add(new Chunk(logical, length, off, type));
            break;
        }
    }

    private void LoadChunks(byte[] sb)
    {
        int size = (int)U32(sb, 0xA0);
        int p = 0x32B, end = 0x32B + Math.Min(size, 2048);
        while (p + 17 + 48 <= end)
        {
            ulong logical = U64(sb, p + 9);
            int stripes = U16(sb, p + 17 + 44);
            int len = 48 + stripes * 32;
            AddChunk(logical, sb.AsSpan(p + 17, len));
            p += 17 + len;
        }
        foreach (var (key, data) in Items(ChunkRoot, ChunkLevel))
            if (key.Type == 228) AddChunk(key.Offset, data.Span);
        Chunks.Sort((a, b) => a.Logical.CompareTo(b.Logical));
    }

    public long Physical(ulong logical)
    {
        int lo = 0, hi = Chunks.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            var c = Chunks[mid];
            if (logical < c.Logical) hi = mid - 1;
            else if (logical >= c.Logical + c.Length) lo = mid + 1;
            else return (long)(c.Physical + (logical - c.Logical));
        }
        return -1;
    }

    // ───────── nós e itens ─────────

    public readonly record struct Key(ulong ObjectId, byte Type, ulong Offset);

    public static uint Crc32C(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        int i = 0;
        for (; i + 8 <= data.Length; i += 8) crc = BitOperations.Crc32C(crc, U64(data, i));
        for (; i < data.Length; i++) crc = BitOperations.Crc32C(crc, data[i]);
        return ~crc;
    }

    /// <summary>Lê um nó da árvore e confere: endereço no cabeçalho, fsid e checksum.</summary>
    public byte[]? ReadNode(ulong logical, bool verify = true)
    {
        long phys = Physical(logical);
        if (phys < 0 || phys + NodeSize > Dev.Length) return null;
        byte[] b;
        try { b = Dev.ReadBytes(phys, (int)NodeSize); } catch (MediaReadException) { return null; }
        if (b.Length < NodeSize || U64(b, 0x30) != logical) return null;
        if (verify && !NodeOk(b)) return null;
        return b;
    }

    private bool NodeOk(byte[] b) => b.AsSpan(0x20, 16).SequenceEqual(Fsid) && (CsumType != 0 || U32(b, 0) == Crc32C(b.AsSpan(32)));

    public IEnumerable<(Key key, ReadOnlyMemory<byte> data)> Items(ulong root, int level, int depth = 0)
    {
        if (depth > 12) yield break;
        var n = ReadNode(root);
        if (n == null) yield break;
        foreach (var it in NodeItems(n, depth)) yield return it;
    }

    private IEnumerable<(Key, ReadOnlyMemory<byte>)> NodeItems(byte[] n, int depth)
    {
        uint count = U32(n, 0x60);
        int lvl = n[0x64];
        if (lvl == 0)
        {
            for (int i = 0; i < count; i++)
            {
                int o = 0x65 + i * 25;
                if (o + 25 > n.Length) yield break;
                var k = new Key(U64(n, o), n[o + 8], U64(n, o + 9));
                int dOff = (int)U32(n, o + 17), dSize = (int)U32(n, o + 21);
                if (0x65 + dOff + dSize > n.Length) continue;
                yield return (k, n.AsMemory(0x65 + dOff, dSize));
            }
            yield break;
        }
        for (int i = 0; i < count; i++)
        {
            int o = 0x65 + i * 33;
            if (o + 33 > n.Length) yield break;
            foreach (var it in Items(U64(n, o + 17), lvl - 1, depth + 1)) yield return it;
        }
    }

    // ───────── árvores de arquivos ─────────

    public sealed class InodeItem
    {
        public ulong Ino, Size, Generation, Transid;
        public uint Mode, Nlink;
        public DateTime? Mtime, Ctime, Otime;
        public bool IsDir => (Mode & 0xF000) == 0x4000;
        public bool IsFile => (Mode & 0xF000) == 0x8000;
    }

    public sealed record DirEnt(ulong Dir, ulong Index, string Name, ulong Child, byte ChildKeyType, byte FileType, ulong Gen);

    public sealed record FileExtent(ulong Ino, ulong FileOffset, byte Compression, byte Type, ulong DiskBytenr, ulong DiskNumBytes, ulong Offset, ulong NumBytes, ulong RamBytes, byte[]? Inline, ulong Gen);

    public sealed class FsTree
    {
        public ulong Id;
        public Dictionary<ulong, InodeItem> Inodes = [];
        public Dictionary<ulong, List<DirEnt>> Dirs = [];
        public Dictionary<ulong, SortedDictionary<ulong, FileExtent>> Extents = [];
    }

    public sealed record RootInfo(ulong Id, ulong Bytenr, int Level, ulong Gen);

    public List<RootInfo> Roots()
    {
        var res = new List<RootInfo>();
        foreach (var (k, d) in Items(RootTree, RootLevel))
        {
            if (k.Type != 132 || !(k.ObjectId == 5 || (k.ObjectId >= 256 && k.ObjectId < 0xFFFFFFFFFFFFFF00UL))) continue;
            var s = d.Span;
            if (s.Length < 239) continue;
            res.Add(new RootInfo(k.ObjectId, U64(s, 176), s[238], U64(s, 160)));
        }
        return res;
    }

    private static DateTime? Ts(ReadOnlySpan<byte> s, int o)
    {
        try { long sec = (long)U64(s, o); return sec <= 0 ? null : DateTime.UnixEpoch.AddSeconds(sec).AddTicks(U32(s, o + 8) / 100); } catch { return null; }
    }

    /// <summary>Junta um item de uma folha na árvore (atual ou histórica, ficando com a geração mais nova).</summary>
    public static void Absorb(FsTree t, Key k, ReadOnlySpan<byte> s, ulong gen)
    {
        switch (k.Type)
        {
            case 1 when s.Length >= 160:
                if (t.Inodes.TryGetValue(k.ObjectId, out var old) && old.Transid > U64(s, 8)) return;
                t.Inodes[k.ObjectId] = new InodeItem
                {
                    Ino = k.ObjectId, Generation = U64(s, 0), Transid = U64(s, 8), Size = U64(s, 16), Nlink = U32(s, 40), Mode = U32(s, 52),
                    Ctime = Ts(s, 124), Mtime = Ts(s, 136), Otime = Ts(s, 148),
                };
                break;
            case 96 when s.Length >= 30:
                {
                    int nl = U16(s, 27);
                    if (30 + nl > s.Length) return;
                    var e = new DirEnt(k.ObjectId, k.Offset, Encoding.UTF8.GetString(s.Slice(30, nl)), U64(s, 0), s[8], s[29], gen);
                    if (!t.Dirs.TryGetValue(k.ObjectId, out var list)) t.Dirs[k.ObjectId] = list = [];
                    int i = list.FindIndex(x => x.Index == e.Index);
                    if (i < 0) list.Add(e); else if (list[i].Gen <= gen) list[i] = e;
                    break;
                }
            case 108 when s.Length >= 21:
                {
                    byte type = s[20];
                    FileExtent fe = type == 0
                        ? new FileExtent(k.ObjectId, k.Offset, s[16], 0, 0, 0, 0, U64(s, 8), U64(s, 8), s[21..].ToArray(), gen)
                        : s.Length >= 53 ? new FileExtent(k.ObjectId, k.Offset, s[16], type, U64(s, 21), U64(s, 29), U64(s, 37), U64(s, 45), U64(s, 8), null, gen) : null!;
                    if (fe == null) return;
                    if (!t.Extents.TryGetValue(k.ObjectId, out var m)) t.Extents[k.ObjectId] = m = [];
                    if (!m.TryGetValue(k.Offset, out var prev) || prev.Gen <= gen) m[k.Offset] = fe;
                    break;
                }
        }
    }

    public FsTree LoadTree(RootInfo r)
    {
        var t = new FsTree { Id = r.Id };
        foreach (var (k, d) in Items(r.Bytenr, r.Level)) Absorb(t, k, d.Span, r.Gen);
        return t;
    }

    // ───────── o passado: folhas antigas no espaço de metadados ─────────

    /// <summary>Uma folha antiga (já substituída) de uma árvore de arquivos, ou de checksums.</summary>
    public sealed record StaleLeaf(ulong Logical, ulong Owner, ulong Gen, byte[] Node);

    /// <summary>
    /// Percorre os chunks de metadados atrás de folhas válidas (checksum certo) de árvores de
    /// arquivos e da árvore de checksums — inclusive as que a árvore atual já não usa.
    /// </summary>
    public IEnumerable<StaleLeaf> StaleLeaves(IProgress<double>? progress, CancellationToken ct)
    {
        var meta = Chunks.Where(c => (c.Type & 0x6) != 0).ToList();   // METADATA | SYSTEM (e misto)
        ulong total = meta.Aggregate(0UL, (a, c) => a + c.Length), done = 0;
        int chunkRead = (int)Math.Max(NodeSize, 4u << 20);
        foreach (var c in meta)
        {
            for (ulong o = 0; o < c.Length; o += (ulong)chunkRead)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min((ulong)chunkRead, c.Length - o);
                byte[] buf;
                try { buf = Dev.ReadBytes((long)(c.Physical + o), n); } catch (MediaReadException) { continue; }
                for (int p = 0; p + NodeSize <= buf.Length; p += (int)NodeSize)
                {
                    ulong logical = c.Logical + o + (ulong)p;
                    if (U64(buf, p + 0x30) != logical || buf[p + 0x64] != 0) continue;
                    var node = buf.AsSpan(p, (int)NodeSize).ToArray();
                    if (!NodeOk(node)) continue;
                    ulong owner = U64(node, 0x58);
                    if (!(owner == 5 || owner == 7 || (owner >= 256 && owner < 0xFFFFFFFFFFFFFF00UL))) continue;
                    yield return new StaleLeaf(logical, owner, U64(node, 0x50), node);
                }
                done += (ulong)n;
                progress?.Report(total == 0 ? 1 : (double)done / total);
            }
        }
    }

    public static IEnumerable<(Key key, ReadOnlyMemory<byte> data)> LeafItems(byte[] n)
    {
        uint count = U32(n, 0x60);
        for (int i = 0; i < count; i++)
        {
            int o = 0x65 + i * 25;
            if (o + 25 > n.Length) yield break;
            var k = new Key(U64(n, o), n[o + 8], U64(n, o + 9));
            int dOff = (int)U32(n, o + 17), dSize = (int)U32(n, o + 21);
            if (0x65 + dOff + dSize > n.Length) continue;
            yield return (k, n.AsMemory(0x65 + dOff, dSize));
        }
    }

    // ───────── alocação ─────────

    /// <summary>Faixas lógicas em uso pela árvore de extents atual (dados e metadados).</summary>
    public List<(ulong start, ulong len)> AllocatedLogical()
    {
        var res = new List<(ulong, ulong)>();
        var extentRoot = Roots0().FirstOrDefault(r => r.Id == 2);
        if (extentRoot == null) return res;
        foreach (var (k, _) in Items(extentRoot.Bytenr, extentRoot.Level))
        {
            if (k.Type == 168) res.Add((k.ObjectId, k.Offset));
            else if (k.Type == 169) res.Add((k.ObjectId, NodeSize));
        }
        res.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return res;
    }

    private List<RootInfo> Roots0()
    {
        var res = new List<RootInfo>();
        foreach (var (k, d) in Items(RootTree, RootLevel))
            if (k.Type == 132 && d.Length >= 239) res.Add(new RootInfo(k.ObjectId, U64(d.Span, 176), d.Span[238], U64(d.Span, 160)));
        return res;
    }

    // ───────── conteúdo ─────────

    /// <summary>Bytes de um arquivo: trechos crus, comprimidos (zlib, LZO, zstd), embutidos e buracos.</summary>
    public IDataSource DataSource(InodeItem ino, IEnumerable<FileExtent> ext) => new BtrfsFile(this, ext.OrderBy(e => e.FileOffset).ToList(), (long)ino.Size);

    public byte[] ReadLogical(ulong logical, int len)
    {
        var res = new byte[len];
        int done = 0;
        while (done < len)
        {
            long phys = Physical(logical + (ulong)done);
            var c = Chunks.First(x => logical + (ulong)done >= x.Logical && logical + (ulong)done < x.Logical + x.Length);
            int n = (int)Math.Min((ulong)(len - done), c.Logical + c.Length - (logical + (ulong)done));
            if (phys < 0) break;
            Dev.ReadBytes(phys, res.AsSpan(done, n));
            done += n;
        }
        return res;
    }

    public byte[] Decompress(byte kind, ReadOnlySpan<byte> src, int ramBytes)
    {
        switch (kind)
        {
            case 1:
                {
                    using var z = new ZLibStream(new MemoryStream(src.ToArray()), CompressionMode.Decompress);
                    return ReadAll(z, ramBytes);
                }
            case 3:
                {
                    using var z = new SharpCompress.Compressors.ZStandard.DecompressionStream(new MemoryStream(src.ToArray()), 1 << 17, false, false);
                    return ReadAll(z, ramBytes);
                }
            case 2: return Lzo.BtrfsSegments(src, ramBytes, (int)SectorSize);
            default: throw new NotSupportedException(L.T($"compressão {kind}"));
        }
    }

    private static byte[] ReadAll(Stream s, int max)
    {
        var res = new byte[max];
        int done = 0, n;
        // o trecho comprimido é arredondado ao setor: depois do fim do fluxo vêm zeros, que o
        // decodificador pode tentar ler como outro quadro — o que já saiu é o conteúdo
        try { while (done < max && (n = s.Read(res, done, max - done)) > 0) done += n; } catch (Exception) when (done > 0) { }
        if (done < max) Array.Resize(ref res, done);
        return res;
    }

    private sealed class BtrfsFile(BtrfsVolume v, List<FileExtent> ext, long size) : IDataSource
    {
        private readonly Dictionary<ulong, byte[]> _cache = [];
        public long Length => size;

        public int Read(long offset, Span<byte> buffer)
        {
            if (offset >= size) return 0;
            int want = (int)Math.Min(buffer.Length, size - offset), done = 0;
            buffer[..want].Clear();
            while (done < want)
            {
                ulong pos = (ulong)(offset + done);
                var e = ext.LastOrDefault(x => x.FileOffset <= pos);
                ulong eLen = e == null ? 0 : e.Type == 0 ? (ulong)(e.Compression == 0 ? e.Inline!.Length : (long)e.RamBytes) : e.NumBytes;
                if (e == null || pos >= e.FileOffset + eLen)
                {
                    // buraco até o próximo trecho
                    var next = ext.FirstOrDefault(x => x.FileOffset > pos);
                    long gap = next == null ? want - done : (long)Math.Min((ulong)(want - done), next.FileOffset - pos);
                    done += (int)gap;
                    continue;
                }
                ulong inExt = pos - e.FileOffset;
                int n = (int)Math.Min((ulong)(want - done), eLen - inExt);
                if (e.Type == 0)
                {
                    var data = e.Compression == 0 ? e.Inline! : Cached(e, () => v.Decompress(e.Compression, e.Inline, (int)e.RamBytes));
                    data.AsSpan((int)inExt, Math.Min(n, data.Length - (int)inExt)).CopyTo(buffer.Slice(done));
                }
                else if (e.Type == 2 || e.DiskBytenr == 0) { /* pré-alocado ou buraco: zeros */ }
                else if (e.Compression == 0)
                {
                    long phys = v.Physical(e.DiskBytenr + e.Offset + inExt);
                    if (phys >= 0) v.Dev.ReadBytes(phys, buffer.Slice(done, n));
                }
                else
                {
                    var data = Cached(e, () => v.Decompress(e.Compression, v.ReadLogical(e.DiskBytenr, (int)e.DiskNumBytes), (int)e.RamBytes));
                    long from = (long)(e.Offset + inExt);
                    if (from < data.Length) data.AsSpan((int)from, (int)Math.Min(n, data.Length - from)).CopyTo(buffer.Slice(done));
                }
                done += n;
            }
            return want;
        }

        private byte[] Cached(FileExtent e, Func<byte[]> make)
        {
            ulong k = e.Type == 0 ? e.FileOffset | (1UL << 63) : e.DiskBytenr;
            if (!_cache.TryGetValue(k, out var d))
            {
                if (_cache.Count > 16) _cache.Clear();
                _cache[k] = d = make();
            }
            return d;
        }
    }
}

/// <summary>LZO1X (o formato do Btrfs: segmentos por setor) — descompressão.</summary>
public static class Lzo
{
    public static byte[] BtrfsSegments(ReadOnlySpan<byte> src, int ramBytes, int sector)
    {
        var res = new byte[ramBytes];
        int total = (int)Math.Min(U32(src, 0), (uint)src.Length);
        int p = 4, outPos = 0;
        while (p + 4 <= total && outPos < ramBytes)
        {
            // o cabeçalho de um segmento nunca atravessa o fim de um setor
            int left = sector - p % sector;
            if (left < 4) p += left;
            if (p + 4 > total) break;
            int len = (int)U32(src, p);
            p += 4;
            if (len <= 0 || p + len > src.Length) break;
            int got = Decompress(src.Slice(p, len), res.AsSpan(outPos));
            outPos += got;
            p += len;
        }
        if (outPos < ramBytes) Array.Resize(ref res, outPos);
        return res;
    }

    /// <summary>Descompressor LZO1X clássico. Devolve quantos bytes escreveu.</summary>
    public static int Decompress(ReadOnlySpan<byte> s, Span<byte> d)
    {
        int ip = 0, op = 0, t, m;
        void Copy(Span<byte> dd, int from, int count, ref int o) { for (int i = 0; i < count; i++) dd[o + i] = dd[from + i]; o += count; }
        if (s[ip] > 17)
        {
            t = s[ip++] - 17;
            if (t < 4) goto match_next;
            s.Slice(ip, t).CopyTo(d[op..]); op += t; ip += t;
            goto first_literal_run;
        }
    loop:
        t = s[ip++];
        if (t >= 16) goto match;
        if (t == 0)
        {
            while (s[ip] == 0) { t += 255; ip++; }
            t += 15 + s[ip++];
        }
        s.Slice(ip, t + 3).CopyTo(d[op..]); op += t + 3; ip += t + 3;
    first_literal_run:
        t = s[ip++];
        if (t >= 16) goto match;
        m = op - (1 + 0x0800) - (t >> 2) - (s[ip++] << 2);
        Copy(d, m, 3, ref op);
        goto match_done;
    match:
        if (t >= 64)
        {
            m = op - 1 - ((t >> 2) & 7) - (s[ip++] << 3);
            t = (t >> 5) - 1;
            Copy(d, m, t + 2, ref op);
            goto match_done;
        }
        else if (t >= 32)
        {
            t &= 31;
            if (t == 0) { while (s[ip] == 0) { t += 255; ip++; } t += 31 + s[ip++]; }
            m = op - 1 - ((s[ip] | (s[ip + 1] << 8)) >> 2);
            ip += 2;
        }
        else if (t >= 16)
        {
            m = op - ((t & 8) << 11);
            t &= 7;
            if (t == 0) { while (s[ip] == 0) { t += 255; ip++; } t += 7 + s[ip++]; }
            m -= (s[ip] | (s[ip + 1] << 8)) >> 2;
            ip += 2;
            if (m == op) return op;   // fim do fluxo
            m -= 0x4000;
        }
        else
        {
            m = op - 1 - (t >> 2) - (s[ip++] << 2);
            Copy(d, m, 2, ref op);
            goto match_done;
        }
        Copy(d, m, t + 2, ref op);
    match_done:
        t = s[ip - 2] & 3;
        if (t == 0) goto loop;
    match_next:
        s.Slice(ip, t).CopyTo(d[op..]); op += t; ip += t;
        t = s[ip++];
        goto match;
    }
}
