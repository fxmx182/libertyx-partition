using System.Text;
using LibertyX.Partition.Core.IO;
using LibertyX.Partition.Core.Recovery;
using static LibertyX.Partition.Core.IO.Bin;

namespace LibertyX.Partition.Core.FileSystems;

/// <summary>Um trecho de clusters; LCN negativo = esparso.</summary>
public readonly record struct DataRun(long Lcn, long Length)
{
    public bool Sparse => Lcn < 0;
}

public sealed class NtfsAttr
{
    public uint Type;
    public string Name = "";
    public bool NonResident;
    public ushort Flags;
    public byte[]? Resident;
    public List<DataRun>? Runs;
    public long StartVcn, LastVcn, RealSize, AllocSize;
    public int CompressionUnit;
    public bool Compressed => (Flags & 0x0001) != 0 && CompressionUnit > 0;
    public bool Encrypted => (Flags & 0x4000) != 0;
}

public sealed class NtfsFileName
{
    public long ParentIndex;
    public ushort ParentSeq;
    public string Name = "";
    public byte Namespace;
    public DateTime? Created, Modified;
    public long RealSize;
}

public sealed class NtfsRecord
{
    public long Index;
    public ushort Seq;
    public bool InUse, IsDir;
    public long BaseIndex;
    public bool TornWrite;
    public List<NtfsAttr> Attrs = [];
    public List<NtfsFileName> Names = [];
    public DateTime? Created, Modified;

    public NtfsFileName? BestName =>
        Names.FirstOrDefault(n => n.Namespace is 1 or 3) ?? Names.FirstOrDefault(n => n.Namespace == 0) ?? Names.FirstOrDefault();

    public NtfsAttr? UnnamedData => Attrs.FirstOrDefault(a => a.Type == 0x80 && a.Name.Length == 0);
}

/// <summary>
/// Leitor de NTFS direto dos setores — não depende do Windows montar o volume, que é o
/// que permite ler partição RAW, perdida ou com o setor de boot destruído.
/// </summary>
public sealed class NtfsVolume
{
    public IBlockDevice Dev { get; }
    public int BytesPerSector { get; private set; }
    public long ClusterSize { get; private set; }
    public int RecordSize { get; private set; }
    public long TotalSectors { get; private set; }
    public long MftLcn { get; private set; }
    public long MftMirrLcn { get; private set; }
    public ulong Serial { get; private set; }
    public bool UsedBackupBoot { get; private set; }
    public List<DataRun> MftRuns { get; private set; } = [];
    public long MftSize { get; private set; }
    public long TotalClusters => TotalSectors * BytesPerSector / ClusterSize;
    public long RecordCount => MftSize / RecordSize;

    private NtfsVolume(IBlockDevice dev) { Dev = dev; }

    public sealed record BootInfo(int BytesPerSector, long ClusterSize, int RecordSize, long TotalSectors, long MftLcn, long MftMirrLcn, ulong Serial);

    public static BootInfo? ParseBoot(ReadOnlySpan<byte> b)
    {
        if (b.Length < 512 || !Match(b, 3, "NTFS    "u8)) return null;
        int bps = U16(b, 11);
        if (bps is not (512 or 1024 or 2048 or 4096)) return null;
        int spcRaw = b[13];
        long spc = spcRaw <= 0x80 ? spcRaw : 1L << (256 - spcRaw);
        if (spc == 0 || (spc & (spc - 1)) != 0) return null;
        long cs = bps * spc;
        long total = I64(b, 40), mft = I64(b, 48), mirr = I64(b, 56);
        sbyte rc = (sbyte)b[64];
        long rs = rc > 0 ? rc * cs : 1L << -rc;
        if (total <= 0 || mft <= 0 || rs < 512 || rs > 65536) return null;
        if (mft * spc > total) return null;
        return new BootInfo(bps, cs, (int)rs, total, mft, mirr, U64(b, 72));
    }

    /// <summary>Abre o volume. Se o setor de boot estiver destruído, tenta a cópia no último setor da partição.</summary>
    public static NtfsVolume? Open(IBlockDevice dev, out string? error)
    {
        error = null;
        var v = new NtfsVolume(dev);
        var b0 = dev.ReadBytes(0, 512);
        var boot = ParseBoot(b0);
        if (boot == null && dev.Length > 512)
        {
            var bl = dev.ReadBytes(dev.Length - dev.SectorSize, dev.SectorSize);
            boot = ParseBoot(bl);
            if (boot == null && dev.SectorSize != 512) boot = ParseBoot(dev.ReadBytes(dev.Length - 512, 512));
            v.UsedBackupBoot = boot != null;
        }
        if (boot == null) { error = L.T("Setor de boot NTFS não encontrado (nem a cópia de reserva)."); return null; }
        v.BytesPerSector = boot.BytesPerSector;
        v.ClusterSize = boot.ClusterSize;
        v.RecordSize = boot.RecordSize;
        v.TotalSectors = boot.TotalSectors;
        v.MftLcn = boot.MftLcn;
        v.MftMirrLcn = boot.MftMirrLcn;
        v.Serial = boot.Serial;

        // O $MFT descreve a si mesmo no registro 0; se estiver ilegível, o $MFTMirr guarda uma cópia.
        foreach (var lcn in new[] { boot.MftLcn, boot.MftMirrLcn })
        {
            var raw = dev.ReadBytes(lcn * v.ClusterSize, v.RecordSize);
            var rec = raw.Length == v.RecordSize ? v.ParseRecord(raw, 0) : null;
            var data = rec?.UnnamedData;
            if (data is { NonResident: true, Runs.Count: > 0 })
            {
                v.MftRuns = data.Runs;
                v.MftSize = data.RealSize;
                break;
            }
        }
        if (v.MftRuns.Count == 0) { error = L.T("O registro do $MFT e sua cópia estão ilegíveis."); return null; }
        return v;
    }

    public static bool ApplyFixup(Span<byte> rec)
    {
        int usaOfs = U16(rec, 4), usaCount = U16(rec, 6);
        if (usaCount < 2 || usaOfs < 8 || usaOfs + usaCount * 2 > rec.Length || (usaCount - 1) * 512 > rec.Length) return false;
        ushort usn = U16(rec, usaOfs);
        bool ok = true;
        for (int i = 1; i < usaCount; i++)
        {
            int pos = i * 512 - 2;
            if (U16(rec, pos) != usn) ok = false;
            rec[pos] = rec[usaOfs + i * 2];
            rec[pos + 1] = rec[usaOfs + i * 2 + 1];
        }
        return ok;
    }

    public static List<DataRun>? DecodeRuns(ReadOnlySpan<byte> s)
    {
        var runs = new List<DataRun>();
        long lcn = 0;
        int p = 0;
        while (p < s.Length)
        {
            byte h = s[p++];
            if (h == 0) break;
            int ls = h & 0xF, os = h >> 4;
            if (ls == 0 || ls > 8 || os > 8 || p + ls + os > s.Length) return runs.Count > 0 ? runs : null;
            long len = 0;
            for (int i = 0; i < ls; i++) len |= (long)s[p + i] << (8 * i);
            p += ls;
            if (len <= 0) return null;
            if (os == 0) { runs.Add(new DataRun(-1, len)); continue; }
            long delta = 0;
            for (int i = 0; i < os; i++) delta |= (long)s[p + i] << (8 * i);
            if ((s[p + os - 1] & 0x80) != 0 && os < 8) delta |= -1L << (8 * os);
            p += os;
            lcn += delta;
            if (lcn < 0) return null;
            runs.Add(new DataRun(lcn, len));
        }
        return runs;
    }

    /// <summary>Interpreta um registro MFT cru (aplica a correção de setores).</summary>
    public NtfsRecord? ParseRecord(Span<byte> raw, long index) => ParseRecordStatic(raw, index);

    public static NtfsRecord? ParseRecordStatic(Span<byte> raw, long index)
    {
        if (!Match(raw, 0, "FILE"u8)) return null;
        var r = new NtfsRecord { Index = index };
        r.TornWrite = !ApplyFixup(raw);
        r.Seq = U16(raw, 16);
        ushort flags = U16(raw, 22);
        r.InUse = (flags & 1) != 0;
        r.IsDir = (flags & 2) != 0;
        r.BaseIndex = (long)(U64(raw, 32) & 0xFFFFFFFFFFFF);
        int used = (int)Math.Min(U32(raw, 24), (uint)raw.Length);
        int o = U16(raw, 20);
        while (o + 16 <= used)
        {
            uint type = U32(raw, o);
            if (type == 0xFFFFFFFF) break;
            int len = (int)U32(raw, o + 4);
            if (len < 16 || o + len > raw.Length) break;
            var a = raw.Slice(o, len);
            var attr = new NtfsAttr { Type = type, NonResident = a[8] != 0, Flags = U16(a, 12) };
            int nlen = a[9], nofs = U16(a, 10);
            if (nlen > 0 && nofs + nlen * 2 <= len) attr.Name = Encoding.Unicode.GetString(a.Slice(nofs, nlen * 2));
            if (!attr.NonResident)
            {
                int vlen = (int)U32(a, 16), vofs = U16(a, 20);
                if (vofs + vlen <= len) attr.Resident = a.Slice(vofs, vlen).ToArray();
                attr.RealSize = attr.Resident?.Length ?? 0;
            }
            else if (len >= 64)
            {
                attr.StartVcn = I64(a, 16);
                attr.LastVcn = I64(a, 24);
                int rofs = U16(a, 32);
                attr.CompressionUnit = U16(a, 34);
                attr.AllocSize = I64(a, 40);
                attr.RealSize = I64(a, 48);
                if (rofs < len) attr.Runs = DecodeRuns(a[rofs..]);
            }
            r.Attrs.Add(attr);

            if (type == 0x10 && attr.Resident is { Length: >= 32 } si)
            {
                r.Created = FileTime(I64(si, 0));
                r.Modified = FileTime(I64(si, 8));
            }
            else if (type == 0x30 && attr.Resident is { Length: >= 66 } fn)
            {
                int chars = fn[64];
                if (66 + chars * 2 <= fn.Length)
                {
                    ulong pref = U64(fn, 0);
                    r.Names.Add(new NtfsFileName
                    {
                        ParentIndex = (long)(pref & 0xFFFFFFFFFFFF), ParentSeq = (ushort)(pref >> 48),
                        Created = FileTime(I64(fn, 8)), Modified = FileTime(I64(fn, 16)), RealSize = I64(fn, 48),
                        Namespace = fn[65], Name = Encoding.Unicode.GetString(fn, 66, chars * 2),
                    });
                }
            }
            o += len;
        }
        return r;
    }

    /// <summary>Converte VCN do $MFT para posição no volume.</summary>
    public long MftRecordOffset(long index)
    {
        long byteOfs = index * RecordSize;
        long vcn = byteOfs / ClusterSize, within = byteOfs % ClusterSize;
        long cur = 0;
        foreach (var r in MftRuns)
        {
            if (vcn < cur + r.Length) return r.Sparse ? -1 : (r.Lcn + vcn - cur) * ClusterSize + within;
            cur += r.Length;
        }
        return -1;
    }

    public NtfsRecord? ReadRecord(long index)
    {
        long ofs = MftRecordOffset(index);
        if (ofs < 0) return null;
        var raw = Dev.ReadBytes(ofs, RecordSize);
        return raw.Length == RecordSize ? ParseRecord(raw, index) : null;
    }

    public IDataSource DataSourceFor(NtfsAttr attr, IEnumerable<NtfsAttr>? continuation = null)
    {
        if (!attr.NonResident) return new ResidentSource(attr.Resident ?? []);
        var pieces = new List<NtfsAttr> { attr };
        if (continuation != null) pieces.AddRange(continuation);
        var runs = pieces.Where(p => p.Runs != null).OrderBy(p => p.StartVcn).SelectMany(p => p.Runs!).ToList();
        long size = attr.StartVcn == 0 ? attr.RealSize : pieces.Where(p => p.StartVcn == 0).Select(p => p.RealSize).FirstOrDefault();
        if (attr.Compressed)
            return new NtfsCompressedSource(Dev, ClusterSize, runs, 1 << attr.CompressionUnit, size);
        return new ExtentSource(Dev, runs.Select(r => new Extent(r.Sparse ? -1 : r.Lcn * ClusterSize, r.Length * ClusterSize)), size);
    }

    /// <summary>O $Bitmap: um bit por cluster, 1 = em uso.</summary>
    public byte[]? LoadBitmap()
    {
        var rec = ReadRecord(6);
        var data = rec?.UnnamedData;
        if (data == null) return null;
        var src = DataSourceFor(data);
        var bmp = new byte[src.Length];
        const int chunk = 4 << 20;
        for (long p = 0; p < bmp.Length; p += chunk)
            src.Read(p, bmp.AsSpan((int)p, (int)Math.Min(chunk, bmp.Length - p)));
        return bmp;
    }

    public string? ReadLabel()
    {
        var rec = ReadRecord(3);
        var name = rec?.Attrs.FirstOrDefault(a => a.Type == 0x60);
        return name?.Resident is { } r ? Encoding.Unicode.GetString(r) : null;
    }

    public static long CountSetBits(byte[] bmp, long totalBits)
    {
        long n = 0;
        long full = totalBits / 8;
        for (long i = 0; i < full && i < bmp.Length; i++) n += System.Numerics.BitOperations.PopCount(bmp[i]);
        for (long b = full * 8; b < totalBits && b / 8 < bmp.Length; b++) if ((bmp[b / 8] & (1 << (int)(b % 8))) != 0) n++;
        return n;
    }

    public static bool IsSet(byte[] bmp, long bit) => bit >= 0 && bit / 8 < bmp.Length && (bmp[bit / 8] & (1 << (int)(bit & 7))) != 0;
}

/// <summary>Arquivo comprimido do NTFS (LZNT1), lido por unidade de compressão.</summary>
public sealed class NtfsCompressedSource : IDataSource
{
    private readonly IBlockDevice _dev;
    private readonly long _cs;
    private readonly long[] _lcnByVcn;
    private readonly int _cu;
    private long _cachedUnit = -1;
    private byte[] _cache = [];
    public long Length { get; }

    public NtfsCompressedSource(IBlockDevice dev, long clusterSize, List<DataRun> runs, int unitClusters, long length)
    {
        _dev = dev; _cs = clusterSize; _cu = unitClusters; Length = length;
        long total = runs.Sum(r => r.Length);
        total = Math.Min(total, (length + clusterSize - 1) / clusterSize + unitClusters);
        _lcnByVcn = new long[total];
        long v = 0;
        foreach (var r in runs)
            for (long i = 0; i < r.Length && v < total; i++) _lcnByVcn[v++] = r.Sparse ? -1 : r.Lcn + i;
    }

    public int Read(long offset, Span<byte> buffer)
    {
        if (offset >= Length) return 0;
        int want = (int)Math.Min(buffer.Length, Length - offset), done = 0;
        long unitBytes = _cu * _cs;
        while (done < want)
        {
            long pos = offset + done;
            long unit = pos / unitBytes;
            var data = Unit(unit);
            int inUnit = (int)(pos % unitBytes);
            int n = Math.Min(want - done, (int)unitBytes - inUnit);
            data.AsSpan(inUnit, n).CopyTo(buffer.Slice(done, n));
            done += n;
        }
        return done;
    }

    private byte[] Unit(long u)
    {
        if (u == _cachedUnit) return _cache;
        int unitBytes = (int)(_cu * _cs);
        var outBuf = new byte[unitBytes];
        long v0 = u * _cu;
        var lcns = new List<long>();
        for (long v = v0; v < v0 + _cu; v++) lcns.Add(v < _lcnByVcn.Length ? _lcnByVcn[v] : -1);
        int alloc = lcns.Count(l => l >= 0);
        if (alloc == _cu)
        {
            for (int i = 0; i < _cu; i++) _dev.ReadBytes(lcns[i] * _cs, outBuf.AsSpan((int)(i * _cs), (int)_cs));
        }
        else if (alloc > 0)
        {
            var comp = new byte[alloc * _cs];
            int k = 0;
            foreach (var l in lcns) if (l >= 0) _dev.ReadBytes(l * _cs, comp.AsSpan((int)(k++ * _cs), (int)_cs));
            Lznt1.Decompress(comp, outBuf);
        }
        _cachedUnit = u;
        _cache = outBuf;
        return outBuf;
    }
}

public static class Lznt1
{
    /// <summary>Descomprime LZNT1 (blocos de 4 KiB). Retorna quantos bytes foram produzidos.</summary>
    public static int Decompress(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int sp = 0, dp = 0;
        while (sp + 2 <= src.Length && dp < dst.Length)
        {
            ushort hdr = U16(src, sp);
            if (hdr == 0) break;
            int chunkEnd = sp + 2 + (hdr & 0x0FFF) + 1;
            bool compressed = (hdr & 0x8000) != 0;
            sp += 2;
            int sbStart = dp;
            if (chunkEnd > src.Length) chunkEnd = src.Length;
            if (!compressed)
            {
                int n = Math.Min(Math.Min(4096, chunkEnd - sp), dst.Length - dp);
                src.Slice(sp, n).CopyTo(dst[dp..]);
                dp += n;
                sp = chunkEnd;
                continue;
            }
            while (sp < chunkEnd && dp < dst.Length)
            {
                byte tags = src[sp++];
                for (int bit = 0; bit < 8 && sp < chunkEnd && dp < dst.Length; bit++)
                {
                    if ((tags & (1 << bit)) == 0) { dst[dp++] = src[sp++]; continue; }
                    if (sp + 2 > chunkEnd) { sp = chunkEnd; break; }
                    int lg = 0;
                    for (int i = dp - sbStart - 1; i >= 0x10; i >>= 1) lg++;
                    ushort pt = U16(src, sp);
                    sp += 2;
                    int back = (pt >> (12 - lg)) + 1;
                    int len = (pt & (0xFFF >> lg)) + 3;
                    int from = dp - back;
                    if (from < sbStart) return dp;
                    for (int i = 0; i < len && dp < dst.Length; i++) dst[dp++] = dst[from + i];
                }
            }
            sp = chunkEnd;
            // um bloco comprimido curto ainda ocupa 4 KiB na saída
            int target = sbStart + 4096;
            if (dp < target && sp < src.Length && U16(src, sp) != 0) { dst.Slice(dp, Math.Min(target, dst.Length) - dp).Clear(); dp = Math.Min(target, dst.Length); }
        }
        return dp;
    }
}
