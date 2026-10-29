using System.Text;
using LibertyX.Partition.Core.IO;
using LibertyX.Partition.Core.Recovery;
using static LibertyX.Partition.Core.IO.Bin;

namespace LibertyX.Partition.Core.FileSystems;

/// <summary>Um volume dentro do contêiner APFS (Macintosh HD, Data, Preboot...).</summary>
public sealed class ApfsVolumeInfo
{
    public int Index;
    public ulong Oid, Paddr, Xid;
    public string Name = "";
    public ushort Role;
    public ulong Flags, Incompat, AllocCount;
    public ulong OmapOid, RootTreeOid;
    /// <summary>Sem a marca "não criptografado": FileVault ou "APFS (criptografado)" — precisa da senha.</summary>
    public bool Encrypted => (Flags & 1) == 0;
    /// <summary>O volume do sistema do macOS 11+ (selado, só leitura): arquivos do sistema, nada do usuário.</summary>
    public bool Sealed => (Incompat & 0x20) != 0;
    public string RoleName => Role switch
    {
        0x1 => L.T("Sistema"), 0x2 => L.T("Usuário"), 0x4 => L.T("Recuperação"), 0x8 => L.T("Memória virtual"), 0x10 => "Preboot",
        0x40 => L.T("Dados"), 0x80 => "Baseband", 0xC0 => L.T("Atualização"), 0x100 => "xART", 0x140 => L.T("Hardware"), 0x180 => L.T("Backup"),
        _ => "",
    };
}

/// <summary>Um inode do APFS: pasta, arquivo ou link, com o pai e o nome guardados no próprio inode.</summary>
public sealed class ApfsInode
{
    public ulong Id, ParentId, PrivateId, Xid;
    public ulong Created, Modified;     // nanossegundos desde 1970
    public ushort Mode;
    public uint BsdFlags;
    public long Size;
    public string? Name;
    public bool IsDir => (Mode & 0xF000) == 0x4000;
    public bool IsFile => (Mode & 0xF000) == 0x8000;
    public bool IsSymlink => (Mode & 0xF000) == 0xA000;
    public bool Compressed => (BsdFlags & 0x20) != 0;
}

/// <summary>Os registros de uma árvore de arquivos (ou de vários pedaços soltos dela).</summary>
public sealed class ApfsRecords
{
    public readonly Dictionary<ulong, ApfsInode> Inodes = [];
    public readonly Dictionary<ulong, List<(string name, ulong id, int type)>> Children = [];
    public readonly Dictionary<ulong, SortedDictionary<ulong, (ulong phys, ulong len, ulong xid)>> Extents = [];
    public readonly Dictionary<(ulong id, string name), (ushort flags, byte[] data)> Xattrs = [];
    public long Nodes;

    /// <summary>Junta os registros de um nó folha; com <paramref name="keepNewest"/>, a versão mais nova (maior xid) vence.</summary>
    internal void Ingest(byte[] b, ulong nodeXid, List<(int k, int kl, int v, int vl)> entries, bool keepNewest)
    {
        Nodes++;
        foreach (var (k, kl, v, vl) in entries)
        {
            if (kl < 8 || k + kl > b.Length || v < 0 || v + vl > b.Length) continue;
            ulong hdr = U64(b, k);
            int type = (int)(hdr >> 60);
            ulong id = hdr & 0x0FFFFFFFFFFFFFFF;
            switch (type)
            {
                case 3 when vl >= 92:
                    {
                        var ino = new ApfsInode
                        {
                            Id = id, ParentId = U64(b, v), PrivateId = U64(b, v + 8), Created = U64(b, v + 16), Modified = U64(b, v + 24),
                            BsdFlags = U32(b, v + 68), Mode = U16(b, v + 80), Xid = nodeXid,
                        };
                        ParseInodeXfields(b, v + 92, v + vl, ino);
                        if (!keepNewest || !Inodes.TryGetValue(id, out var old) || old.Xid < nodeXid) Inodes[id] = ino;
                        break;
                    }
                case 9 when vl >= 18:
                    {
                        string name;
                        uint lh = kl >= 12 ? U32(b, k + 8) : 0;
                        int hl = (int)(lh & 0x3FF);
                        if (kl == 12 + hl && hl > 0) name = Utf8(b, k + 12, hl);          // chave com hash (o comum)
                        else
                        {
                            int nl = U16(b, k + 8);
                            if (kl != 10 + nl || nl == 0) continue;
                            name = Utf8(b, k + 10, nl);
                        }
                        ulong fid = U64(b, v);
                        int dt = U16(b, v + 16) & 0xF;
                        if (!Children.TryGetValue(id, out var list)) Children[id] = list = [];
                        int i = list.FindIndex(c => c.name == name);
                        if (i < 0) list.Add((name, fid, dt));
                        else if (!keepNewest) list[i] = (name, fid, dt);
                        break;
                    }
                case 8 when kl >= 16 && vl >= 16:
                    {
                        ulong logical = U64(b, k + 8);
                        ulong len = U64(b, v) & 0x00FFFFFFFFFFFFFF, phys = U64(b, v + 8);
                        if (len == 0) continue;
                        if (!Extents.TryGetValue(id, out var m)) Extents[id] = m = [];
                        if (!m.TryGetValue(logical, out var old) || !keepNewest || old.xid < nodeXid) m[logical] = (phys, len, nodeXid);
                        break;
                    }
                case 4 when kl >= 10 && vl >= 4:
                    {
                        int nl = U16(b, k + 8);
                        if (10 + nl > kl) continue;
                        string name = Utf8(b, k + 10, nl);
                        if (name is not ("com.apple.decmpfs" or "com.apple.ResourceFork")) continue;
                        ushort fl = U16(b, v);
                        int dl = U16(b, v + 2);
                        if (4 + dl > vl) continue;
                        Xattrs[(id, name)] = (fl, b.AsSpan(v + 4, dl).ToArray());
                        break;
                    }
            }
        }
    }

    private static void ParseInodeXfields(byte[] b, int o, int end, ApfsInode ino)
    {
        if (o + 4 > end) return;
        int n = U16(b, o);
        int data = o + 4 + n * 4;
        for (int i = 0; i < n && o + 4 + i * 4 + 4 <= end; i++)
        {
            int t = b[o + 4 + i * 4], size = U16(b, o + 4 + i * 4 + 2);
            if (data + size > end) break;
            if (t == 4 && size > 0) ino.Name = Utf8(b, data, size);
            else if (t == 8 && size >= 16) ino.Size = (long)U64(b, data);
            data += (size + 7) & ~7;
        }
    }

    private static string Utf8(byte[] b, int o, int n)
    {
        var s = Encoding.UTF8.GetString(b, o, n).TrimEnd('\0');
        try { return s.Normalize(NormalizationForm.FormC); } catch (ArgumentException) { return s; }
    }
}

/// <summary>
/// O contêiner APFS (Macs desde 2017, pendrives e HDs formatados no macOS atual), lido direto dos
/// setores. Tudo no APFS é cópia-na-escrita: as versões antigas da árvore de arquivos ficam no
/// disco até serem reaproveitadas. Os apagados vêm das árvores dos pontos de verificação
/// anteriores e, na varredura profunda, de qualquer nó de árvore solto no contêiner.
/// </summary>
public sealed class ApfsContainer
{
    public IBlockDevice Dev { get; }
    public uint BlockSize { get; private set; }
    public ulong BlockCount { get; private set; }
    public ulong Xid { get; private set; }
    public List<ApfsVolumeInfo> Volumes { get; } = [];
    public bool UsedOlderSuperblock { get; private set; }
    public int CheckpointCount { get; private set; }
    private byte[] _nx = [];
    private readonly List<byte[]> _older = [];
    private Dictionary<ulong, List<(ulong xid, ulong paddr, uint flags)>>? _containerOmap;

    private ApfsContainer(IBlockDevice dev) { Dev = dev; }

    public static bool IsNxSuperblock(ReadOnlySpan<byte> b) => b.Length >= 1408 && Match(b, 32, "NXSB"u8) && (U32(b, 24) & 0xFFFF) == 1;

    public static ApfsContainer? Open(IBlockDevice dev, out string? error)
    {
        error = null;
        var c = new ApfsContainer(dev);
        var b0 = dev.ReadBytes(0, 4096);
        if (!IsNxSuperblock(b0)) { error = L.T("Superbloco do contêiner APFS não encontrado."); return null; }
        uint bs = U32(b0, 36);
        if (bs is < 4096 or > 65536 || (bs & (bs - 1)) != 0) { error = L.T("Tamanho de bloco do APFS inválido."); return null; }
        c.BlockSize = bs;
        if (bs != 4096) b0 = dev.ReadBytes(0, (int)bs);
        // o bloco 0 é só uma cópia: o superbloco atual é o de maior xid íntegro na área de pontos de verificação
        var all = new List<byte[]>();
        if (ChecksumOk(b0)) all.Add(b0);
        ulong descBase = U64(b0, 112);
        uint descBlocks = U32(b0, 104);
        if ((descBlocks & 0x80000000) == 0 && descBlocks < 1_000_000)
            for (uint i = 0; i < descBlocks; i++)
            {
                try
                {
                    var b = c.ReadBlock(descBase + i);
                    if (IsNxSuperblock(b) && ChecksumOk(b)) all.Add(b);
                }
                catch (MediaReadException) { }
            }
        if (all.Count == 0) { error = L.T("Nenhum superbloco APFS íntegro (soma de verificação)."); return null; }
        var ordered = all.GroupBy(b => U64(b, 16)).Select(g => g.First()).OrderByDescending(b => U64(b, 16)).ToList();
        c.CheckpointCount = ordered.Count;
        // o mais novo cujo mapa de objetos abre de verdade (um ponto de verificação pela metade é pulado)
        foreach (var nx in ordered)
        {
            try
            {
                c._nx = nx; c._containerOmap = null;
                c.LoadVolumes();
                if (c.Volumes.Count > 0) break;
            }
            catch (Exception) { c.Volumes.Clear(); }
            c.UsedOlderSuperblock = true;
        }
        if (c.Volumes.Count == 0) { error = L.T("Nenhum volume legível no contêiner APFS."); return null; }
        if (ordered[0] == c._nx) c.UsedOlderSuperblock = false;
        c.Xid = U64(c._nx, 16);
        c.BlockCount = U64(c._nx, 40);
        c._older.AddRange(ordered.Where(b => U64(b, 16) < c.Xid));
        return c;
    }

    private void LoadVolumes()
    {
        Volumes.Clear();
        _containerOmap = LoadOmap(U64(_nx, 160));
        ulong xid = U64(_nx, 16);
        uint max = Math.Min(U32(_nx, 180), 100);
        for (int i = 0; i < max; i++)
        {
            ulong oid = U64(_nx, 184 + i * 8);
            if (oid == 0) continue;
            var v = ReadVolume(_containerOmap, oid, xid, i);
            if (v != null) Volumes.Add(v);
        }
    }

    private ApfsVolumeInfo? ReadVolume(Dictionary<ulong, List<(ulong xid, ulong paddr, uint flags)>> omap, ulong oid, ulong maxXid, int index)
    {
        var pa = Lookup(omap, oid, maxXid);
        if (pa == null) return null;
        var b = ReadBlock(pa.Value);
        if (!Match(b, 32, "APSB"u8) || !ChecksumOk(b)) return null;
        int nl = Array.IndexOf(b, (byte)0, 704, 256);
        return new ApfsVolumeInfo
        {
            Index = index, Oid = oid, Paddr = pa.Value, Xid = U64(b, 16),
            Name = Encoding.UTF8.GetString(b, 704, (nl < 0 ? 960 : nl) - 704),
            Role = U16(b, 964), Flags = U64(b, 264), Incompat = U64(b, 56), AllocCount = U64(b, 88),
            OmapOid = U64(b, 128), RootTreeOid = U64(b, 136),
        };
    }

    public long UsedBytes => (long)Volumes.Sum(v => (decimal)v.AllocCount) * BlockSize;

    public byte[] ReadBlock(ulong paddr)
    {
        if (paddr == 0 || (paddr + 1) * BlockSize > (ulong)Dev.Length) throw new MediaReadException((long)paddr * BlockSize, (int)BlockSize, 0, L.T("bloco APFS fora do volume"));
        return Dev.ReadBytes((long)paddr * BlockSize, (int)BlockSize);
    }

    /// <summary>Fletcher-64 do APFS, sobre o bloco inteiro menos os 8 primeiros bytes.</summary>
    public static bool ChecksumOk(byte[] b)
    {
        ulong s1 = 0, s2 = 0;
        const ulong M = 0xFFFFFFFF;
        for (int i = 8; i + 4 <= b.Length; i += 4)
        {
            s1 = (s1 + U32(b, i)) % M;
            s2 = (s2 + s1) % M;
        }
        ulong c1 = M - ((s1 + s2) % M), c2 = M - ((s1 + c1) % M);
        return ((c2 << 32) | c1) == U64(b, 0) && U64(b, 0) != 0;
    }

    // ───────── árvores B ─────────

    /// <summary>As entradas de um nó: (início da chave, tamanho, início do valor, tamanho).</summary>
    internal List<(int k, int kl, int v, int vl)> Entries(byte[] b, int fixedKey, int fixedVal)
    {
        var list = new List<(int, int, int, int)>();
        int flags = U16(b, 32), level = U16(b, 34);
        uint n = U32(b, 36);
        int toc = 56 + U16(b, 40), tocLen = U16(b, 42);
        int keyStart = toc + tocLen, valEnd = b.Length - ((flags & 1) != 0 ? 40 : 0);
        bool fixedKv = (flags & 4) != 0;
        if (n > b.Length / 4 || keyStart > b.Length) return list;
        for (int i = 0; i < n; i++)
        {
            int k, kl, v, vl;
            if (fixedKv)
            {
                if (toc + i * 4 + 4 > keyStart) break;
                k = U16(b, toc + i * 4); v = U16(b, toc + i * 4 + 2);
                kl = fixedKey; vl = level == 0 ? fixedVal : 8;
            }
            else
            {
                if (toc + i * 8 + 8 > keyStart) break;
                k = U16(b, toc + i * 8); kl = U16(b, toc + i * 8 + 2); v = U16(b, toc + i * 8 + 4); vl = U16(b, toc + i * 8 + 6);
            }
            if (v == 0xFFFF) continue;
            list.Add((keyStart + k, kl, valEnd - v, vl));
        }
        return list;
    }

    /// <summary>O mapa de objetos inteiro: oid virtual → versões (xid, endereço físico).</summary>
    internal Dictionary<ulong, List<(ulong xid, ulong paddr, uint flags)>> LoadOmap(ulong omapPaddr)
    {
        var om = ReadBlock(omapPaddr);
        if ((U32(om, 24) & 0xFFFF) != 0xB || !ChecksumOk(om)) throw new InvalidDataException(L.T("mapa de objetos ilegível"));
        var map = new Dictionary<ulong, List<(ulong, ulong, uint)>>();
        var stack = new Stack<ulong>();
        stack.Push(U64(om, 48));
        int guard = 0;
        while (stack.Count > 0 && guard++ < 2_000_000)
        {
            var b = ReadBlock(stack.Pop());
            if (!ChecksumOk(b)) continue;
            int level = U16(b, 34);
            foreach (var (k, _, v, _) in Entries(b, 16, 16))
            {
                if (level > 0) { stack.Push(U64(b, v)); continue; }
                ulong oid = U64(b, k), xid = U64(b, k + 8);
                if (!map.TryGetValue(oid, out var l)) map[oid] = l = [];
                l.Add((xid, U64(b, v + 8), U32(b, v)));
            }
        }
        return map;
    }

    internal static ulong? Lookup(Dictionary<ulong, List<(ulong xid, ulong paddr, uint flags)>> map, ulong oid, ulong maxXid)
    {
        if (!map.TryGetValue(oid, out var l)) return null;
        (ulong xid, ulong paddr, uint flags)? best = null;
        foreach (var e in l) if (e.xid <= maxXid && (best == null || e.xid > best.Value.xid)) best = e;
        return best == null || (best.Value.flags & 1) != 0 ? null : best.Value.paddr;
    }

    /// <summary>Percorre a árvore de arquivos de um volume e junta os registros das folhas.</summary>
    internal void WalkFsTree(ApfsVolumeInfo vol, Dictionary<ulong, List<(ulong xid, ulong paddr, uint flags)>> volOmap, ulong maxXid, ApfsRecords into, bool keepNewest, CancellationToken ct)
    {
        var root = Lookup(volOmap, vol.RootTreeOid, maxXid);
        if (root == null) throw new InvalidDataException(L.T("raiz da árvore de arquivos não está no mapa de objetos"));
        var rb = ReadBlock(root.Value);
        if (!ChecksumOk(rb)) throw new InvalidDataException(L.T("raiz da árvore de arquivos corrompida"));
        uint btFlags = U32(rb, rb.Length - 40);
        bool physical = (btFlags & 0x10) != 0;
        var stack = new Stack<byte[]>();
        stack.Push(rb);
        int guard = 0;
        while (stack.Count > 0 && guard++ < 50_000_000)
        {
            ct.ThrowIfCancellationRequested();
            var b = stack.Pop();
            int level = U16(b, 34);
            var entries = Entries(b, 0, 0);
            if (level == 0) { into.Ingest(b, U64(b, 16), entries, keepNewest); continue; }
            foreach (var (_, _, v, _) in entries)
            {
                ulong child = U64(b, v);
                var pa = physical ? child : Lookup(volOmap, child, maxXid);
                if (pa == null) continue;
                try
                {
                    var cb = ReadBlock(pa.Value);
                    if (ChecksumOk(cb) && U64(cb, 16) <= maxXid) stack.Push(cb);
                }
                catch (MediaReadException) { }
            }
        }
    }

    public ApfsRecords ReadLive(ApfsVolumeInfo vol, CancellationToken ct)
    {
        var r = new ApfsRecords();
        WalkFsTree(vol, LoadOmap(vol.OmapOid), Xid, r, keepNewest: true, ct);
        return r;
    }

    /// <summary>
    /// As árvores dos pontos de verificação anteriores: o APFS não apaga nada no lugar, então o
    /// estado de alguns momentos atrás — com os arquivos que acabaram de ser excluídos — costuma
    /// estar inteiro no disco.
    /// </summary>
    public ApfsRecords ReadHistory(ApfsVolumeInfo vol, CancellationToken ct, out int usable)
    {
        var r = new ApfsRecords();
        usable = 0;
        foreach (var nx in _older)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                ulong xid = U64(nx, 16);
                var com = LoadOmap(U64(nx, 160));
                var old = ReadVolume(com, vol.Oid, xid, vol.Index);
                if (old == null || old.Encrypted) continue;
                WalkFsTree(old, LoadOmap(old.OmapOid), xid, r, keepNewest: true, ct);
                usable++;
            }
            catch (Exception ex) when (ex is MediaReadException or InvalidDataException or IndexOutOfRangeException or ArgumentException) { }
        }
        return r;
    }

    /// <summary>
    /// Varredura profunda: lê o contêiner inteiro atrás de nós folha da árvore de arquivos (íntegros
    /// pela soma de verificação) que pertençam a este volume — versões antigas soltas no espaço livre.
    /// </summary>
    public void ScanOrphanNodes(ApfsVolumeInfo vol, ApfsRecords into, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var known = new HashSet<ulong>(LoadOmap(vol.OmapOid).Keys);
        bool onlyVolume = Volumes.Count(v => !v.Sealed && !v.Encrypted) == 1;
        long total = Dev.Length, chunk = 8L << 20;
        for (long p = 0; p < total; p += chunk)
        {
            ct.ThrowIfCancellationRequested();
            byte[] buf;
            try { buf = Dev.ReadBytes(p, (int)Math.Min(chunk, total - p)); } catch (MediaReadException) { continue; }
            for (int o = 0; o + BlockSize <= buf.Length; o += (int)BlockSize)
            {
                uint type = U32(buf, o + 24) & 0xFFFF, sub = U32(buf, o + 28);
                if (type is not (2 or 3) || sub != 0xE || U16(buf, o + 34) != 0) continue;
                var b = buf.AsSpan(o, (int)BlockSize).ToArray();
                if (!ChecksumOk(b)) continue;
                if (!onlyVolume && !known.Contains(U64(b, 8))) continue;
                into.Ingest(b, U64(b, 16), Entries(b, 0, 0), keepNewest: true);
            }
            progress?.Report(new(L.T("Procurando versões antigas da árvore"), (double)p / total, into.Inodes.Count, p, total));
        }
    }

    /// <summary>Os bytes de um arquivo a partir dos pedaços (lacunas viram zeros).</summary>
    public IDataSource DataSource(SortedDictionary<ulong, (ulong phys, ulong len, ulong xid)>? ext, long size)
    {
        var list = new List<Extent>();
        long pos = 0;
        if (ext != null)
            foreach (var (logical, (phys, len, _)) in ext)
            {
                if ((long)logical > pos) list.Add(new Extent(-1, (long)logical - pos));
                if ((long)logical < pos) continue;
                list.Add(new Extent(phys == 0 ? -1 : (long)phys * BlockSize, (long)len));
                pos = (long)logical + (long)len;
            }
        if (pos < size) list.Add(new Extent(-1, size - pos));
        return new ExtentSource(Dev, list, size);
    }

    public static DateTime? Time(ulong ns) => ns == 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds((long)(ns / 1_000_000)).UtcDateTime;
}
