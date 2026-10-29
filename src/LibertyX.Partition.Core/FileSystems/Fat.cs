using System.Text;
using LibertyX.Partition.Core.IO;
using LibertyX.Partition.Core.Recovery;
using static LibertyX.Partition.Core.IO.Bin;

namespace LibertyX.Partition.Core.FileSystems;

/// <summary>FAT12/16/32 lido direto dos setores.</summary>
public sealed class FatVolume
{
    public IBlockDevice Dev { get; }
    public FsKind Kind { get; private set; }
    public int BytesPerSector { get; private set; }
    public long ClusterSize { get; private set; }
    public long FatOffset { get; private set; }
    public long FatSizeBytes { get; private set; }
    public int NumFats { get; private set; }
    public long RootDirOffset { get; private set; }
    public int RootEntries { get; private set; }
    public long DataOffset { get; private set; }
    public long ClusterCount { get; private set; }
    public uint RootCluster { get; private set; }
    public long TotalSectors { get; private set; }
    public string? Label { get; private set; }
    public uint Serial { get; private set; }
    public bool UsedBackupBoot { get; private set; }

    private readonly Dictionary<long, uint[]> _pages = [];
    private byte[]? _fat12;
    private const int PageEntries = 65536;

    private FatVolume(IBlockDevice dev) { Dev = dev; }

    public static FatVolume? Open(IBlockDevice dev, out string? error)
    {
        error = null;
        var b = dev.ReadBytes(0, 512);
        var kind = b.Length == 512 ? FsDetect.FatKindFromBpb(b) : FsKind.Unknown;
        bool backup = false;
        if (kind == FsKind.Unknown && dev.Length > 7 * 512)
        {
            // FAT32 guarda uma cópia do setor de boot no setor 6.
            var b6 = dev.ReadBytes(6 * 512, 512);
            if (FsDetect.FatKindFromBpb(b6) == FsKind.Fat32) { b = b6; kind = FsKind.Fat32; backup = true; }
        }
        if (kind == FsKind.Unknown) { error = L.T("Setor de boot FAT inválido."); return null; }
        var v = new FatVolume(dev) { Kind = kind, UsedBackupBoot = backup };
        int bps = U16(b, 11), spc = b[13], rsv = U16(b, 14);
        v.BytesPerSector = bps;
        v.ClusterSize = (long)bps * spc;
        v.NumFats = b[16];
        v.RootEntries = U16(b, 17);
        v.TotalSectors = U16(b, 19) != 0 ? U16(b, 19) : U32(b, 32);
        long fatSz = U16(b, 22) != 0 ? U16(b, 22) : U32(b, 36);
        v.FatOffset = (long)rsv * bps;
        v.FatSizeBytes = fatSz * bps;
        v.RootDirOffset = v.FatOffset + v.NumFats * v.FatSizeBytes;
        long rootBytes = (v.RootEntries * 32L + bps - 1) / bps * bps;
        v.DataOffset = v.RootDirOffset + rootBytes;
        v.ClusterCount = (v.TotalSectors * bps - v.DataOffset) / v.ClusterSize;
        if (kind == FsKind.Fat32)
        {
            v.RootCluster = U32(b, 44);
            v.Serial = U32(b, 67);
            v.Label = Ascii(b, 71, 11);
        }
        else
        {
            v.Serial = U32(b, 39);
            v.Label = Ascii(b, 43, 11);
        }
        if (v.Label is "NO NAME" or "") v.Label = null;
        return v;
    }

    public uint Eoc => Kind switch { FsKind.Fat12 => 0xFF8, FsKind.Fat16 => 0xFFF8, _ => 0x0FFFFFF8 };
    public uint Bad => Kind switch { FsKind.Fat12 => 0xFF7, FsKind.Fat16 => 0xFFF7, _ => 0x0FFFFFF7 };
    public bool IsValidCluster(long c) => c >= 2 && c < ClusterCount + 2;
    public long ClusterOffset(long c) => DataOffset + (c - 2) * ClusterSize;

    public uint Next(long c)
    {
        if (c < 0 || c >= ClusterCount + 2) return 0;
        if (Kind == FsKind.Fat12)
        {
            _fat12 ??= Dev.ReadBytes(FatOffset, (int)Math.Min(FatSizeBytes, 6144 * 2));
            long o = c * 3 / 2;
            if (o + 1 >= _fat12.Length) return 0;
            int v = _fat12[o] | (_fat12[o + 1] << 8);
            return (uint)((c & 1) == 0 ? v & 0xFFF : v >> 4);
        }
        long page = c / PageEntries;
        if (!_pages.TryGetValue(page, out var arr))
        {
            int width = Kind == FsKind.Fat16 ? 2 : 4;
            long start = page * PageEntries * width;
            int bytes = (int)Math.Min((long)PageEntries * width, FatSizeBytes - start);
            var raw = bytes > 0 ? Dev.ReadBytes(FatOffset + start, bytes) : [];
            arr = new uint[PageEntries];
            for (int i = 0; i * width + width <= raw.Length; i++)
                arr[i] = width == 2 ? U16(raw, i * 2) : U32(raw, i * 4) & 0x0FFFFFFF;
            if (_pages.Count > 512) _pages.Clear();
            _pages[page] = arr;
        }
        return arr[c % PageEntries];
    }

    public bool IsAllocated(long c) => Next(c) != 0;

    /// <summary>Segue a cadeia; para em ciclo, cluster inválido ou no limite.</summary>
    public List<long> Chain(long start, long maxClusters)
    {
        var res = new List<long>();
        var seen = new HashSet<long>();
        long c = start;
        while (IsValidCluster(c) && res.Count < maxClusters && seen.Add(c))
        {
            res.Add(c);
            uint n = Next(c);
            if (n >= Eoc || n == 0 || n == Bad) break;
            c = n;
        }
        return res;
    }

    public IEnumerable<Extent> ClustersToExtents(IEnumerable<long> clusters)
    {
        long runStart = -1, runLen = 0;
        foreach (var c in clusters)
        {
            if (runStart >= 0 && c == runStart + runLen) { runLen++; continue; }
            if (runStart >= 0) yield return new Extent(ClusterOffset(runStart), runLen * ClusterSize);
            runStart = c; runLen = 1;
        }
        if (runStart >= 0) yield return new Extent(ClusterOffset(runStart), runLen * ClusterSize);
    }

    public long CountFreeClusters()
    {
        long free = 0;
        for (long c = 2; c < ClusterCount + 2; c++) if (Next(c) == 0) free++;
        return free;
    }

    // ───────────────────────────── diretórios ─────────────────────────────

    public sealed class DirEntry
    {
        public string Name = "";
        public byte Attr;
        public long FirstCluster;
        public long Size;
        public bool Deleted;
        public bool NameGuessed;
        public DateTime? Created, Modified;
        public bool IsDir => (Attr & 0x10) != 0;
        public bool IsDotEntry;
    }

    public static byte LfnChecksum(ReadOnlySpan<byte> sfn)
    {
        byte sum = 0;
        for (int i = 0; i < 11; i++) sum = (byte)(((sum & 1) << 7) + (sum >> 1) + sfn[i]);
        return sum;
    }

    private static string ShortName(ReadOnlySpan<byte> e, byte ntFlags)
    {
        var lat = Encoding.Latin1;
        string b = lat.GetString(e[..8]).TrimEnd(), x = lat.GetString(e.Slice(8, 3)).TrimEnd();
        if ((ntFlags & 0x08) != 0) b = b.ToLowerInvariant();
        if ((ntFlags & 0x10) != 0) x = x.ToLowerInvariant();
        return x.Length > 0 ? b + "." + x : b;
    }

    private static bool PlausibleShort(ReadOnlySpan<byte> e)
    {
        for (int i = 1; i < 11; i++)
        {
            byte c = e[i];
            if (c < 0x20 || c is (byte)'"' or (byte)'*' or (byte)'/' or (byte)':' or (byte)'<' or (byte)'>' or (byte)'?' or (byte)'\\' or (byte)'|') return false;
        }
        return (e[11] & 0xC0) == 0;
    }

    /// <summary>Interpreta entradas de diretório, inclusive as apagadas (0xE5) e seus nomes longos.</summary>
    public List<DirEntry> ParseDirectory(ReadOnlySpan<byte> d)
    {
        var res = new List<DirEntry>();
        var lfn = new List<(int ord, string part, byte sum, bool deleted)>();
        for (int o = 0; o + 32 <= d.Length; o += 32)
        {
            var e = d.Slice(o, 32);
            if (e[0] == 0x00) break;
            byte attr = e[11];
            if (attr == 0x0F)
            {
                var part = new StringBuilder();
                foreach (var (s, n) in new[] { (1, 5), (14, 6), (28, 2) })
                    for (int i = 0; i < n; i++)
                    {
                        char ch = (char)U16(e, s + i * 2);
                        if (ch == '\0' || ch == '￿') goto done;
                        part.Append(ch);
                    }
                done:
                lfn.Add((e[0] & 0x1F, part.ToString(), e[13], e[0] == 0xE5));
                continue;
            }
            bool deleted = e[0] == 0xE5;
            if ((attr & 0x08) != 0 && (attr & 0x10) == 0)
            {
                if (!deleted && o < 64 * 32) Label ??= Encoding.Latin1.GetString(e[..11]).TrimEnd();
                lfn.Clear();
                continue;
            }
            if (!PlausibleShort(e)) { lfn.Clear(); continue; }
            var sfn = e[..11].ToArray();
            if (sfn[0] == 0x05) sfn[0] = 0xE5;

            var de = new DirEntry
            {
                Attr = attr, Deleted = deleted, Size = U32(e, 28),
                FirstCluster = (Kind == FsKind.Fat32 ? (long)U16(e, 20) << 16 : 0) | U16(e, 26),
                Created = Dos(U16(e, 16), U16(e, 14), e[13]),
                Modified = Dos(U16(e, 24), U16(e, 22)),
            };
            if (!deleted && (sfn[0] == '.' && (sfn[1] == ' ' || sfn[1] == '.')))
            {
                de.IsDotEntry = true; lfn.Clear(); continue;
            }

            string? longName = null;
            if (lfn.Count > 0)
            {
                // As partes vêm da última para a primeira; nas apagadas o número de ordem se perdeu.
                var parts = lfn.AsEnumerable().Reverse().Select(l => l.part);
                longName = string.Concat(parts);
                if (deleted && longName.Length > 0)
                {
                    sfn[0] = (byte)char.ToUpperInvariant(longName[0]);
                    if (sfn[0] < 0x20 || sfn[0] > 0x7E || LfnChecksum(sfn) != lfn[0].sum) sfn[0] = (byte)'_';
                }
                else if (!deleted && LfnChecksum(sfn) != lfn[0].sum) longName = null;
            }
            else if (deleted) { sfn[0] = (byte)'_'; de.NameGuessed = true; }

            de.Name = longName ?? ShortName(sfn, e[12]);
            lfn.Clear();
            if (de.Name.Length == 0) continue;
            res.Add(de);
        }
        return res;
    }

    public byte[] ReadDirectory(long firstCluster, bool deleted)
    {
        if (firstCluster == 0 && Kind != FsKind.Fat32)
            return Dev.ReadBytes(RootDirOffset, RootEntries * 32);
        if (!IsValidCluster(firstCluster)) return [];
        List<long> chain;
        if (!deleted) chain = Chain(firstCluster, 65536 * 32 / ClusterSize + 1);
        else
        {
            // Pasta apagada: a cadeia foi zerada; supõe clusters contíguos ainda livres.
            chain = [firstCluster];
            for (long c = firstCluster + 1; chain.Count < 64 && IsValidCluster(c) && !IsAllocated(c); c++) chain.Add(c);
        }
        var buf = new byte[chain.Count * ClusterSize];
        int k = 0;
        foreach (var ext in ClustersToExtents(chain))
        {
            Dev.ReadBytes(ext.DeviceOffset, buf.AsSpan(k, (int)ext.Length));
            k += (int)ext.Length;
        }
        if (deleted)
        {
            // corta no primeiro cluster que não parece mais diretório
            for (int c = 1; c < chain.Count; c++)
            {
                var s = buf.AsSpan((int)(c * ClusterSize), 32);
                if (s[0] == 0 || (!PlausibleShort(s) && s[11] != 0x0F)) { Array.Resize(ref buf, (int)(c * ClusterSize)); break; }
            }
        }
        return buf;
    }

    /// <summary>Uma pasta apagada só vale a pena se o primeiro cluster ainda começa com "." e "..".</summary>
    public bool LooksLikeDirectory(long cluster)
    {
        if (!IsValidCluster(cluster)) return false;
        var s = Dev.ReadBytes(ClusterOffset(cluster), 64);
        return s.Length == 64 && s[0] == '.' && s[1] == ' ' && (s[11] & 0x10) != 0 && s[32] == '.' && s[33] == '.';
    }
}

/// <summary>exFAT lido direto dos setores.</summary>
public sealed class ExFatVolume
{
    public IBlockDevice Dev { get; }
    public long ClusterSize { get; private set; }
    public int BytesPerSector { get; private set; }
    public long FatOffset { get; private set; }
    public long HeapOffset { get; private set; }
    public long ClusterCount { get; private set; }
    public uint RootCluster { get; private set; }
    public long VolumeLength { get; private set; }
    public uint Serial { get; private set; }
    public string? Label { get; private set; }
    public byte[]? Bitmap { get; private set; }
    public bool UsedBackupBoot { get; private set; }

    private readonly Dictionary<long, uint[]> _pages = [];

    private ExFatVolume(IBlockDevice dev) { Dev = dev; }

    public static ExFatVolume? Open(IBlockDevice dev, out string? error)
    {
        error = null;
        var b = dev.ReadBytes(0, 512);
        bool backup = false;
        if (!Match(b, 3, "EXFAT   "u8))
        {
            // a região de boot de reserva começa no setor 12
            for (int shift = 9; shift <= 12 && !backup; shift++)
            {
                var bb = dev.ReadBytes(12L << shift, 512);
                if (Match(bb, 3, "EXFAT   "u8) && bb[108] == shift) { b = bb; backup = true; }
            }
            if (!backup) { error = L.T("Setor de boot exFAT inválido."); return null; }
        }
        int bpsShift = b[108], spcShift = b[109];
        if (bpsShift < 9 || bpsShift > 12 || bpsShift + spcShift > 25) { error = L.T("Parâmetros exFAT inválidos."); return null; }
        var v = new ExFatVolume(dev)
        {
            UsedBackupBoot = backup,
            BytesPerSector = 1 << bpsShift,
            ClusterSize = 1L << (bpsShift + spcShift),
        };
        v.FatOffset = (long)U32(b, 80) << bpsShift;
        v.HeapOffset = (long)U32(b, 88) << bpsShift;
        v.ClusterCount = U32(b, 92);
        v.RootCluster = U32(b, 96);
        v.Serial = U32(b, 100);
        v.VolumeLength = (long)U64(b, 72) << bpsShift;
        return v;
    }

    public bool IsValidCluster(long c) => c >= 2 && c < ClusterCount + 2;
    public long ClusterOffset(long c) => HeapOffset + (c - 2) * ClusterSize;

    public uint Next(long c)
    {
        if (!IsValidCluster(c)) return 0;
        long page = c / 65536;
        if (!_pages.TryGetValue(page, out var arr))
        {
            var raw = Dev.ReadBytes(FatOffset + page * 65536 * 4, 65536 * 4);
            arr = new uint[65536];
            for (int i = 0; i * 4 + 4 <= raw.Length; i++) arr[i] = U32(raw, i * 4);
            if (_pages.Count > 512) _pages.Clear();
            _pages[page] = arr;
        }
        return arr[c % 65536];
    }

    public bool IsAllocated(long c) => Bitmap != null && NtfsVolume.IsSet(Bitmap, c - 2);

    public List<long> Chain(long start, long count, bool contiguous)
    {
        var res = new List<long>();
        if (contiguous)
        {
            for (long i = 0; i < count && IsValidCluster(start + i); i++) res.Add(start + i);
            return res;
        }
        var seen = new HashSet<long>();
        long c = start;
        while (IsValidCluster(c) && res.Count < count && seen.Add(c))
        {
            res.Add(c);
            uint n = Next(c);
            if (n >= 0xFFFFFFF7 || n == 0) break;
            c = n;
        }
        return res;
    }

    public IEnumerable<Extent> ClustersToExtents(IEnumerable<long> clusters)
    {
        long runStart = -1, runLen = 0;
        foreach (var c in clusters)
        {
            if (runStart >= 0 && c == runStart + runLen) { runLen++; continue; }
            if (runStart >= 0) yield return new Extent(ClusterOffset(runStart), runLen * ClusterSize);
            runStart = c; runLen = 1;
        }
        if (runStart >= 0) yield return new Extent(ClusterOffset(runStart), runLen * ClusterSize);
    }

    public byte[] ReadClusters(List<long> chain, long maxBytes)
    {
        long total = Math.Min(maxBytes, chain.Count * ClusterSize);
        var buf = new byte[total];
        long k = 0;
        foreach (var ext in ClustersToExtents(chain))
        {
            if (k >= total) break;
            int n = (int)Math.Min(ext.Length, total - k);
            Dev.ReadBytes(ext.DeviceOffset, buf.AsSpan((int)k, n));
            k += n;
        }
        return buf;
    }

    public sealed class DirEntry
    {
        public string Name = "";
        public bool IsDir, Deleted, NoFatChain;
        public long FirstCluster, Size;
        public DateTime? Created, Modified;
    }

    private static DateTime? Stamp(uint ts)
    {
        int sec = (int)(ts & 31) * 2, min = (int)(ts >> 5) & 63, hour = (int)(ts >> 11) & 31;
        int day = (int)(ts >> 16) & 31, mon = (int)(ts >> 21) & 15, year = 1980 + (int)(ts >> 25);
        if (day == 0 || mon == 0 || mon > 12 || hour > 23 || min > 59 || sec > 59 || day > DateTime.DaysInMonth(year, mon)) return null;
        return new DateTime(year, mon, day, hour, min, sec, DateTimeKind.Local);
    }

    /// <summary>Lê os conjuntos de entradas (arquivo + stream + nomes), vivos e apagados (bit 0x80 apagado).</summary>
    public List<DirEntry> ParseDirectory(ReadOnlySpan<byte> d, bool isRoot)
    {
        var res = new List<DirEntry>();
        for (int o = 0; o + 32 <= d.Length; o += 32)
        {
            byte t = d[o];
            if (t == 0x00) break;
            if (isRoot && t == 0x81 && Bitmap == null)
            {
                long fc = U32(d, o + 20), len = (long)U64(d, o + 24);
                if (IsValidCluster(fc) && len <= (ClusterCount + 7) / 8 + ClusterSize)
                    Bitmap = ReadClusters(Chain(fc, (len + ClusterSize - 1) / ClusterSize, false), len);
                continue;
            }
            if (isRoot && t == 0x83) { int n = Math.Min((int)d[o + 1], 11); Label = Utf16(d, o + 2, n); continue; }
            if ((t & 0x7F) != 0x05) continue;
            bool deleted = (t & 0x80) == 0;
            int secondary = d[o + 1];
            if (secondary < 2 || secondary > 18 || o + 32 * (secondary + 1) > d.Length) continue;
            int so = o + 32;
            if ((d[so] & 0x7F) != 0x40 || ((d[so] & 0x80) == 0) != deleted) continue;
            var e = new DirEntry
            {
                Deleted = deleted,
                IsDir = (U16(d, o + 4) & 0x10) != 0,
                Created = Stamp(U32(d, o + 8)),
                Modified = Stamp(U32(d, o + 12)),
                NoFatChain = (d[so + 1] & 0x02) != 0,
                FirstCluster = U32(d, so + 20),
                Size = (long)U64(d, so + 24),
            };
            int nameLen = d[so + 3];
            var sb = new StringBuilder();
            for (int k = 2; k <= secondary && sb.Length < nameLen; k++)
            {
                int no = o + 32 * k;
                if ((d[no] & 0x7F) != 0x41) break;
                for (int c = 0; c < 15 && sb.Length < nameLen; c++) sb.Append((char)U16(d, no + 2 + c * 2));
            }
            e.Name = sb.ToString();
            if (e.Name.Length == 0 || e.Name.Any(ch => ch < 0x20)) continue;
            res.Add(e);
            o += 32 * secondary;
        }
        return res;
    }

    public byte[] ReadDirectory(long firstCluster, long size, bool noFatChain)
    {
        if (!IsValidCluster(firstCluster)) return [];
        long clusters = size > 0 ? (size + ClusterSize - 1) / ClusterSize : 4096;
        clusters = Math.Min(clusters, 64L * 1024 * 1024 / ClusterSize);
        var chain = Chain(firstCluster, clusters, noFatChain);
        return ReadClusters(chain, size > 0 ? size : chain.Count * ClusterSize);
    }
}
