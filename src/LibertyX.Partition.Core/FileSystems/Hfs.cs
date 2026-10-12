using System.Text;
using LibertyX.Partition.Core.IO;
using LibertyX.Partition.Core.Recovery;
using static LibertyX.Partition.Core.IO.Bin;

namespace LibertyX.Partition.Core.FileSystems;

/// <summary>Uma fork do HFS+ (dados ou recursos): tamanho e os 8 primeiros pedaços.</summary>
public sealed class HfsFork
{
    public long LogicalSize;
    public uint TotalBlocks;
    public (uint Start, uint Count)[] Extents = [];

    public static HfsFork Parse(ReadOnlySpan<byte> b, int o)
    {
        var f = new HfsFork { LogicalSize = (long)U64BE(b, o), TotalBlocks = U32BE(b, o + 12) };
        var list = new List<(uint, uint)>(8);
        for (int i = 0; i < 8; i++)
        {
            uint s = U32BE(b, o + 16 + i * 8), c = U32BE(b, o + 20 + i * 8);
            if (c == 0) break;
            list.Add((s, c));
        }
        f.Extents = [.. list];
        return f;
    }

    public long InlineBlocks => Extents.Sum(e => (long)e.Count);
}

/// <summary>Um registro do catálogo: pasta, arquivo ou "fio" (o nome e o pai de um número de nó).</summary>
public sealed class HfsRecord
{
    public uint ParentId;
    public string Name = "";
    public short Type;          // 1 pasta, 2 arquivo, 3/4 fio de pasta/arquivo
    public uint Id;
    public uint Created, Modified;
    public ushort Mode;
    public byte OwnerFlags;
    public uint Special;
    public uint FileType, Creator;
    public HfsFork? Data, Rsrc;
    public bool IsFolder => Type == 1;
    public bool IsFile => Type == 2;
    public bool IsThread => Type is 3 or 4;
    public bool Compressed => (OwnerFlags & 0x20) != 0;
    public bool IsHardLink => FileType == 0x686C6E6B && Creator == 0x6866732B;   // 'hlnk' 'hfs+'
    public bool IsDirLink => FileType == 0x66647270 && Creator == 0x4D414353;    // 'fdrp' 'MACS'
    public bool IsSymlink => FileType == 0x736C6E6B && Creator == 0x72686170     // 'slnk' 'rhap'
                             || (Mode & 0xF000) == 0xA000;
}

/// <summary>
/// HFS+ e HFSX (discos de Mac antigos, Time Machine, pendrives formatados no Mac) lidos direto dos
/// setores. A árvore viva vem do catálogo (uma árvore B); os apagados vêm dos restos de registros
/// que ficam nos nós livres do catálogo, na sobra dos nós em uso e no journal — é onde o Mac
/// deixa as versões antigas.
/// </summary>
public sealed class HfsVolume
{
    public IBlockDevice Dev { get; }
    public uint BlockSize { get; private set; }
    public uint TotalBlocks { get; private set; }
    public uint FreeBlocks { get; private set; }
    public uint NextCatalogId { get; private set; }
    public bool IsHfsx { get; private set; }
    public bool Journaled { get; private set; }
    public bool UsedAlternateHeader { get; private set; }
    public string Label { get; private set; } = "";
    public string Variant => IsHfsx ? "HFSX" : "HFS+";
    public uint FileCount { get; private set; }
    public uint FolderCount { get; private set; }

    private HfsFork _allocFork = null!, _extFork = null!, _catFork = null!, _attrFork = null!;
    internal BTree Catalog = null!;
    private BTree? _extents, _attributes;
    private Dictionary<(uint, byte), List<(uint start, (uint, uint)[] ext)>>? _overflow;
    private Dictionary<uint, byte[]>? _decmpfs;

    private HfsVolume(IBlockDevice dev) { Dev = dev; }

    public static bool IsHeader(ReadOnlySpan<byte> h) =>
        h.Length >= 512 && ((h[0] == (byte)'H' && h[1] == (byte)'+' && U16BE(h, 2) == 4) || (h[0] == (byte)'H' && h[1] == (byte)'X' && U16BE(h, 2) == 5))
        && U32BE(h, 40) is >= 512 and <= 1 << 20 && (U32BE(h, 40) & (U32BE(h, 40) - 1)) == 0 && U32BE(h, 44) > 0;

    public static HfsVolume? Open(IBlockDevice dev, out string? error)
    {
        error = null;
        if (dev.Length < 4096) { error = "Volume pequeno demais para HFS+."; return null; }
        var v = new HfsVolume(dev);
        byte[] h = dev.ReadBytes(1024, 512);
        if (!IsHeader(h))
        {
            // cabeçalho destruído: a cópia fica 1024 bytes antes do fim do volume
            var alt = dev.ReadBytes(dev.Length - 1024, 512);
            if (!IsHeader(alt)) { error = "Cabeçalho do HFS+ não encontrado (nem a cópia do fim do volume)."; return null; }
            h = alt; v.UsedAlternateHeader = true;
        }
        v.IsHfsx = h[1] == (byte)'X';
        v.Journaled = (U32BE(h, 4) & 0x2000) != 0;
        v.FileCount = U32BE(h, 32);
        v.FolderCount = U32BE(h, 36);
        v.BlockSize = U32BE(h, 40);
        v.TotalBlocks = U32BE(h, 44);
        v.FreeBlocks = U32BE(h, 48);
        v.NextCatalogId = U32BE(h, 64);
        v._journalInfoBlock = U32BE(h, 12);
        v._allocFork = HfsFork.Parse(h, 112);
        v._extFork = HfsFork.Parse(h, 192);
        v._catFork = HfsFork.Parse(h, 272);
        v._attrFork = HfsFork.Parse(h, 352);
        try
        {
            // o arquivo de extents primeiro: o catálogo grande pode ter pedaços registrados nele
            if (v._extFork.LogicalSize > 0) v._extents = new BTree(v, v.ForkSource(3, v._extFork, 0, overflow: false));
            v.Catalog = new BTree(v, v.ForkSource(4, v._catFork, 0));
            if (v._attrFork.LogicalSize > 0) v._attributes = new BTree(v, v.ForkSource(8, v._attrFork, 0));
        }
        catch (Exception ex) { error = "Catálogo do HFS+ ilegível: " + ex.Message; return null; }
        if (v.Catalog.NodeSize < 512) { error = "Catálogo do HFS+ com cabeçalho inválido."; return null; }
        // o nome do volume é o nome da pasta raiz (id 2), cujo fio fica no registro de chave (2, "")
        foreach (var r in v.Catalog.LeafRecords().Select(x => ParseRecord(x.node, x.ofs, x.end)).Take(64))
            if (r is { Id: 2 } && (r.IsThread || r.IsFolder) && r.Name.Length > 0) { v.Label = r.Name; break; }
        return v;
    }

    private uint _journalInfoBlock;

    // ───────── forks e pedaços ─────────

    /// <summary>Os pedaços de uma fork, em bytes no volume — os 8 do registro e os do arquivo de extents de reserva.</summary>
    public List<Extent> Map(uint fileId, HfsFork fork, byte forkType, bool overflow = true)
    {
        var list = new List<Extent>();
        long blocks = 0;
        foreach (var (s, c) in fork.Extents) { list.Add(new Extent((long)s * BlockSize, (long)c * BlockSize)); blocks += c; }
        if (overflow && blocks < fork.TotalBlocks && _extents != null)
        {
            LoadOverflow();
            if (_overflow!.TryGetValue((fileId, forkType), out var more))
                foreach (var (start, ext) in more.OrderBy(m => m.start))
                {
                    if (start < blocks) continue;
                    foreach (var (s, c) in ext) { list.Add(new Extent((long)s * BlockSize, (long)c * BlockSize)); blocks += c; }
                    if (blocks >= fork.TotalBlocks) break;
                }
        }
        return list;
    }

    private IDataSource ForkSource(uint fileId, HfsFork fork, byte forkType, bool overflow = true) =>
        new ExtentSource(Dev, Map(fileId, fork, forkType, overflow), fork.LogicalSize);

    private void LoadOverflow()
    {
        if (_overflow != null) return;
        _overflow = [];
        if (_extents == null) return;
        foreach (var (node, ofs, _) in _extents.LeafRecords())
        {
            int kl = U16BE(node, ofs);
            if (kl < 10) continue;
            byte forkType = node[ofs + 2];
            uint id = U32BE(node, ofs + 4), start = U32BE(node, ofs + 8);
            int d = ofs + 2 + kl;
            if (d + 64 > node.Length) continue;
            var ext = new List<(uint, uint)>();
            for (int i = 0; i < 8; i++) { uint s = U32BE(node, d + i * 8), c = U32BE(node, d + 4 + i * 8); if (c == 0) break; ext.Add((s, c)); }
            if (!_overflow.TryGetValue((id, forkType), out var l)) _overflow[(id, forkType)] = l = [];
            l.Add((start, [.. ext]));
        }
    }

    /// <summary>O atributo com.apple.decmpfs dos arquivos comprimidos (só os guardados inline).</summary>
    private void LoadDecmpfs()
    {
        if (_decmpfs != null) return;
        _decmpfs = [];
        if (_attributes == null) return;
        foreach (var (node, ofs, _) in _attributes.LeafRecords())
        {
            int kl = U16BE(node, ofs);
            if (kl < 12) continue;
            uint id = U32BE(node, ofs + 4);
            int nl = U16BE(node, ofs + 12);
            if (ofs + 14 + nl * 2 > node.Length) continue;
            string name = Encoding.BigEndianUnicode.GetString(node, ofs + 14, nl * 2);
            if (name != "com.apple.decmpfs") continue;
            int d = ofs + 2 + kl;
            if (d + 16 > node.Length || U32BE(node, d) != 0x10) continue;
            int size = (int)U32BE(node, d + 12);
            if (size <= 0 || d + 16 + size > node.Length) continue;
            _decmpfs[id] = node.AsSpan(d + 16, size).ToArray();
        }
    }

    /// <summary>O conteúdo do arquivo: a fork de dados ou, se comprimido pelo macOS, descomprimido.</summary>
    public IDataSource? DataSource(HfsRecord r, out string? note, out long size)
    {
        note = null;
        size = r.Data?.LogicalSize ?? 0;
        if (r.Compressed && (r.Data == null || r.Data.LogicalSize == 0))
        {
            LoadDecmpfs();
            if (_decmpfs!.TryGetValue(r.Id, out var x))
            {
                IDataSource? rsrc = r.Rsrc is { LogicalSize: > 0 } rf ? ForkSource(r.Id, rf, 0xFF) : null;
                var src = Decmpfs.Open(x, rsrc, out note);
                if (src != null) size = src.Length;
                else if (x.Length >= 16) size = (long)U64(x, 8);
                return src;
            }
            note = "comprimido pelo macOS, atributo de compressão não encontrado";
            return null;
        }
        return r.Data == null ? null : ForkSource(r.Id, r.Data, 0);
    }

    // ───────── catálogo ─────────

    public static HfsRecord? ParseRecord(byte[] node, int ofs, int end)
    {
        if (ofs + 8 > end) return null;
        int kl = U16BE(node, ofs);
        if (kl < 6 || ofs + 2 + kl > end) return null;
        int nl = U16BE(node, ofs + 6);
        if (6 + nl * 2 > kl) return null;
        var r = new HfsRecord { ParentId = U32BE(node, ofs + 2), Name = Name(node, ofs + 8, nl) };
        int d = ofs + 2 + kl;
        if (d + 2 > end) return null;
        r.Type = (short)U16BE(node, d);
        switch (r.Type)
        {
            case 1:
                if (d + 88 > end) return null;
                r.Id = U32BE(node, d + 8); r.Created = U32BE(node, d + 12); r.Modified = U32BE(node, d + 16);
                r.Mode = U16BE(node, d + 42);
                break;
            case 2:
                if (d + 248 > end) return null;
                r.Id = U32BE(node, d + 8); r.Created = U32BE(node, d + 12); r.Modified = U32BE(node, d + 16);
                r.OwnerFlags = node[d + 41]; r.Mode = U16BE(node, d + 42); r.Special = U32BE(node, d + 44);
                r.FileType = U32BE(node, d + 48); r.Creator = U32BE(node, d + 52);
                r.Data = HfsFork.Parse(node, d + 88); r.Rsrc = HfsFork.Parse(node, d + 168);
                break;
            case 3 or 4:
                if (d + 10 > end) return null;
                // fio: a chave é (id, ""), e o registro traz o pai e o nome de verdade
                r.Id = r.ParentId;
                r.ParentId = U32BE(node, d + 4);
                int tn = U16BE(node, d + 8);
                if (tn > 255 || d + 10 + tn * 2 > end) return null;
                r.Name = Name(node, d + 10, tn);
                break;
            default: return null;
        }
        return r;
    }

    private static string Name(byte[] b, int o, int chars)
    {
        if (chars <= 0) return "";
        var s = Encoding.BigEndianUnicode.GetString(b, o, chars * 2);
        try { s = s.Normalize(NormalizationForm.FormC); } catch (ArgumentException) { }
        // no catálogo a barra do nome vira dois-pontos (o separador antigo do Mac)
        return s.Replace(':', '/');
    }

    public IEnumerable<HfsRecord> LiveRecords()
    {
        foreach (var (node, ofs, end) in Catalog.LeafRecords())
            if (ParseRecord(node, ofs, end) is { } r) yield return r;
    }

    // ───────── restos de registros (apagados) ─────────

    /// <summary>
    /// Procura registros de catálogo em lugares que a árvore viva não aponta mais: nós livres do
    /// catálogo, a sobra no fim dos nós em uso e o journal. Devolve tudo o que parecer um registro
    /// íntegro; quem chama separa o que ainda está vivo.
    /// </summary>
    public IEnumerable<(HfsRecord rec, string where)> StaleRecords(CancellationToken ct)
    {
        int ns = Catalog.NodeSize;
        for (uint n = 1; n < Catalog.TotalNodes; n++)
        {
            ct.ThrowIfCancellationRequested();
            byte[] node;
            try { node = Catalog.ReadNode(n); } catch (MediaReadException) { continue; }
            bool used = Catalog.IsNodeUsed(n);
            int from = 14;
            if (used && (sbyte)node[8] == -1)
            {
                // nó em uso: só a sobra depois do último registro interessa
                int nrec = U16BE(node, 10);
                if (nrec * 2 + 2 > ns) continue;
                from = U16BE(node, ns - 2 * (nrec + 1));
            }
            else if (used) continue;
            foreach (var r in Carve(node, from, ns - 2)) yield return (r, used ? "sobra de nó do catálogo" : "nó livre do catálogo");
        }
        foreach (var (buf, _) in JournalChunks())
        {
            ct.ThrowIfCancellationRequested();
            foreach (var r in Carve(buf, 0, buf.Length)) yield return (r, "journal");
        }
    }

    private IEnumerable<HfsRecord> Carve(byte[] b, int from, int end)
    {
        for (int o = Math.Max(0, from) & ~1; o + 10 < end; o += 2)
        {
            int kl = U16BE(b, o);
            if (kl < 6 || kl > 516 || (kl & 1) != 0) continue;
            int nl = U16BE(b, o + 6);
            if (kl != 6 + nl * 2) continue;
            uint parent = U32BE(b, o + 2);
            if (parent == 0 || parent > NextCatalogId + 4096) continue;
            int d = o + 2 + kl;
            if (d + 2 > end) continue;
            ushort t = U16BE(b, d);
            if (t is not (1 or 2 or 3 or 4)) continue;
            if (nl > 0 && !PlausibleName(b, o + 8, nl)) continue;
            var r = ParseRecord(b, o, end);
            if (r == null || r.Id < 2 || r.Id > NextCatalogId + 4096) continue;
            if (r.IsThread ? (nl != 0 || r.Name.Length == 0) : nl == 0) continue;
            if (r.IsFile && !PlausibleFork(r.Data!)) continue;
            yield return r;
        }
    }

    private static bool PlausibleName(byte[] b, int o, int chars)
    {
        for (int i = 0; i < chars; i++)
        {
            ushort c = U16BE(b, o + i * 2);
            if (c < 0x20 && c != 0 && c != 0x0D) return false;
            if (c is 0xFFFF or 0xFFFE) return false;
        }
        return true;
    }

    private bool PlausibleFork(HfsFork f)
    {
        if (f.LogicalSize < 0 || f.LogicalSize > (long)TotalBlocks * BlockSize) return false;
        if ((long)f.TotalBlocks * BlockSize < f.LogicalSize) return false;
        if (f.TotalBlocks > 0 && f.Extents.Length == 0) return false;
        foreach (var (s, c) in f.Extents) if ((long)s + c > TotalBlocks) return false;
        return f.InlineBlocks <= f.TotalBlocks;
    }

    /// <summary>O buffer circular do journal, em pedaços (é onde o Mac guarda as versões recentes dos nós).</summary>
    private IEnumerable<(byte[] buf, long ofs)> JournalChunks()
    {
        if (!Journaled || _journalInfoBlock == 0) yield break;
        byte[] jib;
        try { jib = Dev.ReadBytes((long)_journalInfoBlock * BlockSize, 512); } catch (MediaReadException) { yield break; }
        uint flags = U32BE(jib, 0);
        if ((flags & 1) == 0) yield break;          // journal em outro dispositivo
        long ofs = (long)U64BE(jib, 36), size = (long)U64BE(jib, 44);
        if (ofs <= 0 || size <= 0 || ofs + size > Dev.Length) yield break;
        const int chunk = 1 << 20;
        for (long p = 0; p < size; p += chunk)
        {
            byte[] b;
            try { b = Dev.ReadBytes(ofs + p, (int)Math.Min(chunk + 4096, size - p)); } catch (MediaReadException) { continue; }
            yield return (b, ofs + p);
        }
    }

    // ───────── mapa de alocação ─────────

    public byte[] LoadAllocation()
    {
        var src = ForkSource(6, _allocFork, 0);
        var b = new byte[Math.Min(src.Length, (TotalBlocks + 7L) / 8)];
        src.Read(0, b);
        return b;
    }

    /// <summary>No HFS+ o bit mais alto do byte é o primeiro bloco (o contrário do NTFS).</summary>
    public static bool IsAllocated(byte[] bmp, long block) => block >= 0 && block / 8 < bmp.Length && (bmp[block / 8] & (0x80 >> (int)(block & 7))) != 0;

    public long CountAllocated(byte[] bmp)
    {
        long n = 0;
        for (long i = 0; i < TotalBlocks; i++) if (IsAllocated(bmp, i)) n++;
        return n;
    }

    public static DateTime? Time(uint t) => t == 0 ? null : new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(t);

    // ───────── árvore B ─────────

    internal sealed class BTree
    {
        private readonly IDataSource _file;
        private readonly byte[] _map;
        public int NodeSize { get; }
        public uint Root { get; }
        public uint FirstLeaf { get; }
        public uint TotalNodes { get; }

        public BTree(HfsVolume v, IDataSource file)
        {
            _file = file;
            var head = new byte[512];
            file.Read(0, head);
            if ((sbyte)head[8] != 1) throw new InvalidDataException("nó de cabeçalho ausente");
            Root = U32BE(head, 16);
            FirstLeaf = U32BE(head, 24);
            NodeSize = U16BE(head, 32);
            TotalNodes = U32BE(head, 36);
            if (NodeSize is < 512 or > 32768 || (NodeSize & (NodeSize - 1)) != 0) throw new InvalidDataException("tamanho de nó inválido");
            var hn = ReadNode(0);
            int mapOfs = U16BE(hn, NodeSize - 6), mapEnd = U16BE(hn, NodeSize - 8);
            var map = new List<byte>(hn.AsSpan(mapOfs, Math.Max(0, Math.Min(mapEnd, NodeSize) - mapOfs)).ToArray());
            // nós além do mapa do cabeçalho: nós de mapa encadeados pelo fLink
            uint next = U32BE(hn, 0);
            for (int guard = 0; next != 0 && guard < 100000 && map.Count * 8L < TotalNodes; guard++)
            {
                var mn = ReadNode(next);
                int o = U16BE(mn, NodeSize - 2), e = U16BE(mn, NodeSize - 4);
                map.AddRange(mn.AsSpan(o, Math.Max(0, Math.Min(e, NodeSize) - o)).ToArray());
                next = U32BE(mn, 0);
            }
            _map = [.. map];
        }

        public byte[] ReadNode(uint n)
        {
            var b = new byte[NodeSize];
            _file.Read((long)n * NodeSize, b);
            return b;
        }

        public bool IsNodeUsed(uint n) => n / 8 < _map.Length && (_map[n / 8] & (0x80 >> (int)(n & 7))) != 0;

        /// <summary>Todos os registros das folhas, seguindo a corrente de folhas da esquerda para a direita.</summary>
        public IEnumerable<(byte[] node, int ofs, int end)> LeafRecords()
        {
            var seen = new HashSet<uint>();
            for (uint n = FirstLeaf; n != 0 && n < TotalNodes && seen.Add(n);)
            {
                byte[] node;
                try { node = ReadNode(n); } catch (MediaReadException) { yield break; }
                if ((sbyte)node[8] != -1) yield break;
                int nrec = U16BE(node, 10);
                for (int i = 0; i < nrec && 2 * (i + 2) <= NodeSize; i++)
                {
                    int o = U16BE(node, NodeSize - 2 * (i + 1));
                    int e = U16BE(node, NodeSize - 2 * (i + 2));
                    if (o < 14 || o >= NodeSize || e <= o || e > NodeSize) continue;
                    yield return (node, o, e);
                }
                n = U32BE(node, 0);
            }
        }
    }
}
