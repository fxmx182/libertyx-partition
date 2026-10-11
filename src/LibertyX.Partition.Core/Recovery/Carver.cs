using System.Text;
using LibertyX.Partition.Core.FileSystems;
using LibertyX.Partition.Core.IO;

namespace LibertyX.Partition.Core.Recovery;

/// <summary>Leitura aleatória com cache para os analisadores de formato — eles andam byte a byte.</summary>
public sealed class CarveReader(IBlockDevice dev)
{
    private readonly byte[] _cache = new byte[256 * 1024];
    private long _start = -1;
    private int _len;
    public long Length => dev.Length;

    private bool Fill(long pos)
    {
        if (pos < 0 || pos >= dev.Length) return false;
        if (pos >= _start && pos < _start + _len) return true;
        long s = pos / 4096 * 4096;
        try { _len = dev.ReadBytes(s, _cache); }
        catch (MediaReadException) { _len = 0; _start = -1; return false; }
        _start = s;
        return pos < _start + _len;
    }

    public int Byte(long pos) => Fill(pos) ? _cache[pos - _start] : -1;

    public bool Read(long pos, Span<byte> dst)
    {
        int done = 0;
        while (done < dst.Length)
        {
            if (!Fill(pos + done)) return false;
            int off = (int)(pos + done - _start);
            int n = Math.Min(dst.Length - done, _len - off);
            _cache.AsSpan(off, n).CopyTo(dst[done..]);
            done += n;
        }
        return true;
    }

    public byte[]? Bytes(long pos, int n)
    {
        var b = new byte[n];
        return Read(pos, b) ? b : null;
    }

    public ushort BE16(long p) { Span<byte> b = stackalloc byte[2]; return Read(p, b) ? Bin.U16BE(b, 0) : (ushort)0; }
    public uint BE32(long p) { Span<byte> b = stackalloc byte[4]; return Read(p, b) ? Bin.U32BE(b, 0) : 0; }
    public ulong BE64(long p) { Span<byte> b = stackalloc byte[8]; return Read(p, b) ? Bin.U64BE(b, 0) : 0; }
    public ushort U16(long p) { Span<byte> b = stackalloc byte[2]; return Read(p, b) ? Bin.U16(b, 0) : (ushort)0; }
    public uint U32(long p) { Span<byte> b = stackalloc byte[4]; return Read(p, b) ? Bin.U32(b, 0) : 0; }
    public ulong U64(long p) { Span<byte> b = stackalloc byte[8]; return Read(p, b) ? Bin.U64(b, 0) : 0; }

    /// <summary>Procura um padrão entre [from, end). Devolve -1 se não achar.</summary>
    public long IndexOf(ReadOnlySpan<byte> pat, long from, long end)
    {
        end = Math.Min(end, dev.Length);
        const int win = 1 << 20;
        var buf = new byte[win + pat.Length];
        for (long p = from; p < end; p += win)
        {
            int n = (int)Math.Min(buf.Length, end - p);
            if (!Read(p, buf.AsSpan(0, n)))
            {
                // pode haver setor ruim: tenta pelo menos o que dá
                try { n = dev.ReadBytes(p, buf.AsSpan(0, n)); } catch (MediaReadException) { return -1; }
            }
            int i = buf.AsSpan(0, n).IndexOf(pat);
            if (i >= 0) return p + i;
        }
        return -1;
    }
}

public sealed record CarveHit(string Ext, long Size, string Kind);

/// <summary>
/// Varredura profunda: reconhece arquivos pelo conteúdo, setor a setor, e calcula o tamanho
/// lendo a estrutura interna de cada formato (não "até o próximo cabeçalho"), o que evita
/// arquivos cortados ou inchados.
/// </summary>
public static class Carver
{
    public const long MaxDefault = 4L << 30;

    public static readonly (string ext, string desc)[] SupportedTypes =
    [
        ("jpg", "Foto JPEG"), ("png", "Imagem PNG"), ("gif", "GIF"), ("bmp", "Bitmap"), ("tif", "TIFF / RAW de câmera (CR2, NEF, ARW, DNG)"),
        ("heic", "Foto HEIC do iPhone"), ("mp4", "Vídeo MP4 / MOV / 3GP"), ("avi", "Vídeo AVI"), ("mkv", "Vídeo MKV / WebM"), ("wmv", "Vídeo WMV / ASF"),
        ("mp3", "Áudio MP3"), ("wav", "Áudio WAV"), ("ogg", "Áudio OGG / Opus"), ("pdf", "PDF"), ("docx", "Office (docx, xlsx, pptx), ODF, EPUB, ZIP"),
        ("doc", "Office antigo (doc, xls, ppt, msg)"), ("rar", "RAR"), ("7z", "7-Zip"), ("sqlite", "SQLite (WhatsApp, navegadores)"), ("exe", "Programas (exe, dll)"),
    ];

    /// <summary>Grupo do filtro de tipos a que cada extensão produzida pertence.</summary>
    public static string GroupOf(string ext) => ext switch
    {
        "jpg" => "jpg", "png" => "png", "gif" => "gif", "bmp" => "bmp",
        "tif" or "cr2" or "nef" or "arw" or "dng" or "pef" => "tif",
        "heic" or "avif" or "cr3" => "heic",
        "mp4" or "mov" or "m4a" or "3gp" => "mp4", "avi" => "avi", "webp" => "png", "wav" => "wav",
        "mkv" or "webm" => "mkv", "wmv" => "wmv", "mp3" => "mp3", "ogg" or "opus" or "ogv" => "ogg", "pdf" => "pdf",
        "docx" or "xlsx" or "pptx" or "odt" or "ods" or "odp" or "epub" or "zip" or "apk" or "jar" => "docx",
        "doc" or "xls" or "ppt" or "msg" or "msi" or "cfb" => "doc",
        "rar" => "rar", "7z" => "7z", "sqlite" => "sqlite", "exe" or "dll" => "exe", _ => ext,
    };

    public static CarveHit? Identify(CarveReader r, long s, ReadOnlySpan<byte> h, long max)
    {
        if (h.Length < 32) return null;
        try
        {
            switch (h[0])
            {
                case 0xFF when h[1] == 0xD8 && h[2] == 0xFF:
                    return Hit("jpg", Jpeg(r, s, max), "JPEG");
                case 0x89 when Bin.Match(h, 0, "\x89PNG\r\n\x1a\n"u8):
                    return Hit("png", Png(r, s, max), "PNG");
                case (byte)'G' when Bin.Match(h, 0, "GIF87a"u8) || Bin.Match(h, 0, "GIF89a"u8):
                    return Hit("gif", Gif(r, s, max), "GIF");
                case (byte)'B' when h[1] == 'M':
                    return Hit("bmp", Bmp(h, max), "Bitmap");
                case (byte)'%' when Bin.Match(h, 0, "%PDF-"u8):
                    return Hit("pdf", Pdf(r, s, max), "PDF");
                case (byte)'P' when Bin.Match(h, 0, "PK\x03\x04"u8):
                    { var (sz, ext) = Zip(r, s, h, max); return Hit(ext, sz, "ZIP/Office"); }
                case (byte)'R' when Bin.Match(h, 0, "Rar!\x1a\x07\x01\x00"u8):
                    return Hit("rar", Rar5(r, s, max), "RAR");
                case (byte)'R' when Bin.Match(h, 0, "Rar!\x1a\x07\x00"u8):
                    return Hit("rar", Rar4(r, s, max), "RAR");
                case (byte)'R' when Bin.Match(h, 0, "RIFF"u8):
                    {
                        string? ext = Bin.Match(h, 8, "WAVE"u8) ? "wav" : Bin.Match(h, 8, "AVI "u8) ? "avi" : Bin.Match(h, 8, "WEBP"u8) ? "webp" : null;
                        if (ext == null) return null;
                        long sz = 8 + Bin.U32(h, 4);
                        return Hit(ext, sz >= 44 && sz <= max ? sz + (sz & 1) : null, "RIFF");
                    }
                case (byte)'7' when Bin.Match(h, 0, "7z\xBC\xAF\x27\x1C"u8):
                    {
                        long sz = 32 + (long)Bin.U64(h, 12) + (long)Bin.U64(h, 20);
                        return Hit("7z", h[6] == 0 && sz > 32 && sz <= max ? sz : null, "7-Zip");
                    }
                case (byte)'I' when Bin.Match(h, 0, "ID3"u8):
                    return Hit("mp3", Mp3(r, s, h, max), "MP3");
                case 0x1A when Bin.Match(h, 0, "\x1A\x45\xDF\xA3"u8):
                    { var (sz, ext) = Mkv(r, s, max); return Hit(ext, sz, "Matroska"); }
                case (byte)'S' when Bin.Match(h, 0, "SQLite format 3\0"u8):
                    {
                        long ps = Bin.U16BE(h, 16) == 1 ? 65536 : Bin.U16BE(h, 16);
                        long sz = ps * Bin.U32BE(h, 28);
                        return Hit("sqlite", ps >= 512 && (ps & (ps - 1)) == 0 && sz > 0 && sz <= max ? sz : null, "SQLite");
                    }
                case 0xD0 when Bin.Match(h, 0, "\xD0\xCF\x11\xE0\xA1\xB1\x1A\xE1"u8):
                    { var (sz, ext) = Ole(r, s, h, max); return Hit(ext, sz, "OLE2"); }
                case (byte)'I' when Bin.Match(h, 0, "II*\0"u8):
                case (byte)'M' when Bin.Match(h, 0, "MM\0*"u8):
                    { var (sz, ext) = Tiff(r, s, h[0] == 'I', h, max); return Hit(ext, sz, "TIFF"); }
                case (byte)'M' when h[1] == 'Z':
                    { var (sz, ext) = Pe(r, s, max); return Hit(ext, sz, "Executável"); }
                case (byte)'O' when Bin.Match(h, 0, "OggS"u8) && h[4] == 0 && (h[5] & 2) != 0:
                    { var (sz, ext) = Ogg(r, s, max); return Hit(ext, sz, "Ogg"); }
                case 0x30 when Bin.Match(h, 0, AsfHeader):
                    return Hit("wmv", Asf(r, s, max), "ASF");
            }
            if (Bin.Match(h, 4, "ftyp"u8))
            {
                var (sz, ext) = IsoBmff(r, s, h, max);
                return Hit(ext, sz, "MP4/MOV/HEIC");
            }
        }
        catch (MediaReadException) { }
        return null;
    }

    private static CarveHit? Hit(string ext, long? size, string kind) => size is > 0 ? new CarveHit(ext, size.Value, kind) : null;

    // ───────────── formatos ─────────────

    private static long? Jpeg(CarveReader r, long s, long max)
    {
        long p = s + 2, end = s + max;
        int first = r.Byte(s + 3);
        if (!(first is >= 0xE0 and <= 0xEF or 0xDB or 0xC0 or 0xC2 or 0xC4 or 0xFE or 0xDD)) return null;
        while (p < end)
        {
            if (r.Byte(p) != 0xFF) return null;
            while (r.Byte(p) == 0xFF) p++;
            int m = r.Byte(p++);
            if (m < 0) return null;
            if (m == 0xD9) return p - s >= 128 ? p - s : null;
            if (m == 0x01 || m is >= 0xD0 and <= 0xD7) continue;
            int len = r.BE16(p);
            if (len < 2) return null;
            p += len;
            if (m != 0xDA) continue;
            // dados de entropia: o próximo marcador é FF seguido de algo que não seja 00 nem RSTn
            while (true)
            {
                long q = r.IndexOf([0xFF], p, end);
                if (q < 0) return null;
                int n = r.Byte(q + 1);
                if (n < 0) return null;
                if (n == 0x00 || n is >= 0xD0 and <= 0xD7) { p = q + 2; continue; }
                if (n == 0xFF) { p = q + 1; continue; }
                p = q;
                break;
            }
        }
        return null;
    }

    private static long? Png(CarveReader r, long s, long max)
    {
        long p = s + 8, end = s + max;
        Span<byte> t = stackalloc byte[4];
        while (p < end)
        {
            uint len = r.BE32(p);
            if (len > 0x7FFFFFFF || !r.Read(p + 4, t)) return null;
            foreach (var c in t) if (!(c is >= (byte)'A' and <= (byte)'Z' || c is >= (byte)'a' and <= (byte)'z')) return null;
            p += 12 + len;
            if (t.SequenceEqual("IEND"u8)) return p - s;
        }
        return null;
    }

    private static long? Gif(CarveReader r, long s, long max)
    {
        long end = s + max;
        int flags = r.Byte(s + 10);
        long p = s + 13;
        if ((flags & 0x80) != 0) p += 3 * (2 << (flags & 7));
        bool SkipSub()
        {
            while (p < end) { int n = r.Byte(p); if (n < 0) return false; p++; if (n == 0) return true; p += n; }
            return false;
        }
        int frames = 0;
        while (p < end)
        {
            int b = r.Byte(p);
            if (b == 0x3B) return frames > 0 ? p + 1 - s : null;
            if (b == 0x21) { p += 2; if (!SkipSub()) return null; }
            else if (b == 0x2C)
            {
                int f = r.Byte(p + 9);
                p += 10;
                if ((f & 0x80) != 0) p += 3 * (2 << (f & 7));
                p++;
                if (!SkipSub()) return null;
                frames++;
            }
            else return null;
        }
        return null;
    }

    private static long? Bmp(ReadOnlySpan<byte> h, long max)
    {
        long size = Bin.U32(h, 2);
        uint off = Bin.U32(h, 10), dib = Bin.U32(h, 14);
        if (Bin.U32(h, 6) != 0 || dib is not (12 or 40 or 52 or 56 or 108 or 124)) return null;
        if (off < 14 + dib || off >= size || size < 64 || size > Math.Min(max, 512L << 20)) return null;
        return size;
    }

    private static long? Pdf(CarveReader r, long s, long max)
    {
        long end = s + max;
        long e = r.IndexOf("%%EOF"u8, s, end);
        if (e < 0) return null;
        long stop = e + 5;
        // atualizações incrementais acrescentam mais blocos com %%EOF próprio
        while (true)
        {
            long next = r.IndexOf("%%EOF"u8, stop, Math.Min(end, stop + (1 << 20)));
            if (next < 0) break;
            long hdr = r.IndexOf("%PDF-"u8, stop, next);
            if (hdr >= 0) break;
            stop = next + 5;
        }
        for (int i = 0; i < 2 && (r.Byte(stop) == '\r' || r.Byte(stop) == '\n'); i++) stop++;
        return stop - s;
    }

    private static (long?, string) Zip(CarveReader r, long s, ReadOnlySpan<byte> h, long max)
    {
        long end = s + max;
        long p = s;
        long? size = null;
        while (true)
        {
            long e = r.IndexOf("PK\x05\x06"u8, p, end);
            if (e < 0) break;
            long cdSize = r.U32(e + 12), cdOfs = r.U32(e + 16);
            int comment = r.U16(e + 20);
            if (cdOfs == 0xFFFFFFFF && r.U32(e - 20) == 0x07064B50)
            {
                long z64 = (long)r.U64(e - 20 + 8);
                if (r.U32(s + z64) == 0x06064B50) { cdSize = (long)r.U64(s + z64 + 40); cdOfs = (long)r.U64(s + z64 + 48); if (cdOfs + cdSize == z64) { size = e + 22 + comment - s; break; } }
            }
            else if (cdOfs + cdSize == e - s) { size = e + 22 + comment - s; break; }
            p = e + 4;
        }
        if (size == null) return (null, "zip");

        int nameLen = Bin.U16(h, 26);
        string first = nameLen < 200 && 30 + nameLen <= h.Length ? Encoding.UTF8.GetString(h.Slice(30, nameLen)) : "";
        string ext = "zip";
        long cdStart = s + (long)size - 22 - Math.Min(1 << 20, (long)size);
        var tail = r.Bytes(Math.Max(s, cdStart), (int)Math.Min(1 << 20, size.Value)) ?? [];
        string names = Encoding.Latin1.GetString(tail);
        if (first == "mimetype")
        {
            string mt = Encoding.ASCII.GetString(h.Slice(38, Math.Min(h.Length - 38, 60)));
            ext = mt.Contains("opendocument.text") ? "odt" : mt.Contains("spreadsheet") ? "ods" : mt.Contains("presentation") ? "odp" : mt.Contains("epub") ? "epub" : "zip";
        }
        else if (names.Contains("word/document.xml")) ext = "docx";
        else if (names.Contains("xl/workbook.xml")) ext = "xlsx";
        else if (names.Contains("ppt/presentation.xml")) ext = "pptx";
        else if (names.Contains("AndroidManifest.xml") && names.Contains("classes.dex")) ext = "apk";
        else if (names.Contains("META-INF/MANIFEST.MF")) ext = "jar";
        return (size, ext);
    }

    private static (long v, int n) VInt(CarveReader r, long p)
    {
        long v = 0;
        for (int i = 0; i < 10; i++)
        {
            int b = r.Byte(p + i);
            if (b < 0) return (-1, 0);
            v |= (long)(b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0) return (v, i + 1);
        }
        return (-1, 0);
    }

    private static long? Rar5(CarveReader r, long s, long max)
    {
        long p = s + 8, end = s + max;
        for (int blocks = 0; p < end && blocks < 1_000_000; blocks++)
        {
            var (hs, n1) = VInt(r, p + 4);
            if (hs <= 0 || hs > (2 << 20)) return null;
            long hstart = p + 4 + n1;
            var (ht, n2) = VInt(r, hstart);
            var (hf, n3) = VInt(r, hstart + n2);
            if (ht is < 1 or > 5 || hf < 0) return null;
            long q = hstart + n2 + n3, data = 0;
            if ((hf & 1) != 0) { var (_, n) = VInt(r, q); q += n; }
            if ((hf & 2) != 0) { var (d, _) = VInt(r, q); data = d; }
            if (data < 0) return null;
            p = hstart + hs + data;
            if (ht == 5) return p - s;
        }
        return null;
    }

    private static long? Rar4(CarveReader r, long s, long max)
    {
        long p = s + 7, end = s + max;
        for (int blocks = 0; p < end && blocks < 1_000_000; blocks++)
        {
            int type = r.Byte(p + 2);
            ushort flags = r.U16(p + 3), size = r.U16(p + 5);
            if (type is < 0x72 or > 0x7B || size < 7) return null;
            long add = (flags & 0x8000) != 0 ? r.U32(p + 7) : 0;
            p += size + add;
            if (type == 0x7B) return p - s;
        }
        return null;
    }

    private static readonly int[][] Mp3Rates =
    [
        [0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448],
        [0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384],
        [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320],
        [0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256],
        [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160],
    ];

    private static int Mp3Frame(uint hdr)
    {
        if ((hdr & 0xFFE00000) != 0xFFE00000) return 0;
        int ver = (int)(hdr >> 19) & 3, layer = (int)(hdr >> 17) & 3, bri = (int)(hdr >> 12) & 15, sri = (int)(hdr >> 10) & 3, pad = (int)(hdr >> 9) & 1;
        if (ver == 1 || layer == 0 || bri is 0 or 15 || sri == 3) return 0;
        int[] srs = ver == 3 ? [44100, 48000, 32000] : ver == 2 ? [22050, 24000, 16000] : [11025, 12000, 8000];
        int sr = srs[sri];
        bool v1 = ver == 3;
        int table = v1 ? (layer == 3 ? 0 : layer == 2 ? 1 : 2) : (layer == 3 ? 3 : 4);
        int br = Mp3Rates[table][bri] * 1000;
        if (layer == 3) return (12 * br / sr + pad) * 4;
        if (layer == 1 && !v1) return 72 * br / sr + pad;
        return 144 * br / sr + pad;
    }

    private static long? Mp3(CarveReader r, long s, ReadOnlySpan<byte> h, long max)
    {
        if (h[3] > 9 || h[6] >= 0x80 || h[7] >= 0x80 || h[8] >= 0x80 || h[9] >= 0x80) return null;
        long tag = (h[6] << 21) | (h[7] << 14) | (h[8] << 7) | h[9];
        long p = s + 10 + tag + ((h[5] & 0x10) != 0 ? 10 : 0), end = s + max;
        // alguns gravadores deixam zeros entre a tag e o primeiro quadro
        for (int i = 0; i < 4096 && r.Byte(p) == 0; i++) p++;
        int frames = 0;
        while (p < end)
        {
            int len = Mp3Frame(r.BE32(p));
            if (len < 24) break;
            p += len;
            frames++;
        }
        if (frames < 4) return null;
        Span<byte> t = stackalloc byte[3];
        if (r.Read(p, t) && t.SequenceEqual("TAG"u8)) p += 128;
        return p - s;
    }

    private static (long v, int n) EbmlSize(CarveReader r, long p)
    {
        int b = r.Byte(p);
        if (b <= 0) return (-1, 0);
        int len = System.Numerics.BitOperations.LeadingZeroCount((uint)b) - 24 + 1;
        if (len > 8) return (-1, 0);
        long v = b & (0xFF >> len);
        bool allOnes = v == (0xFF >> len);
        for (int i = 1; i < len; i++) { int c = r.Byte(p + i); if (c < 0) return (-1, 0); v = (v << 8) | (uint)c; if (c != 0xFF) allOnes = false; }
        return (allOnes ? -2 : v, len);
    }

    private static (long?, string) Mkv(CarveReader r, long s, long max)
    {
        var (hsz, n) = EbmlSize(r, s + 4);
        if (hsz <= 0 || hsz > 4096) return (null, "mkv");
        var head = r.Bytes(s + 4 + n, (int)hsz) ?? [];
        string ext = Encoding.ASCII.GetString(head).Contains("webm") ? "webm" : "mkv";
        long p = s + 4 + n + hsz;
        if (r.BE32(p) != 0x18538067) return (null, ext);
        var (ssz, n2) = EbmlSize(r, p + 4);
        if (ssz <= 0) return (null, ext); // tamanho "desconhecido" (gravação interrompida)
        long total = p + 4 + n2 + ssz - s;
        return (total <= max ? total : null, ext);
    }

    private static (long?, string) Ole(CarveReader r, long s, ReadOnlySpan<byte> h, long max)
    {
        int shift = Bin.U16(h, 30);
        if (shift is not (9 or 12) || h.Length < 512) return (null, "doc");
        int ss = 1 << shift;
        uint nFat = Bin.U32(h, 44);
        if (nFat == 0 || nFat > 100_000) return (null, "doc");
        var fatSecs = new List<uint>();
        for (int i = 0; i < 109 && fatSecs.Count < nFat; i++) fatSecs.Add(Bin.U32(h, 76 + i * 4));
        uint difat = Bin.U32(h, 68);
        for (int guard = 0; fatSecs.Count < nFat && difat < 0xFFFFFFFA && guard < 10000; guard++)
        {
            var d = r.Bytes(s + (difat + 1L) * ss, ss);
            if (d == null) return (null, "doc");
            for (int i = 0; i < ss / 4 - 1 && fatSecs.Count < nFat; i++) fatSecs.Add(Bin.U32(d, i * 4));
            difat = Bin.U32(d, ss - 4);
        }
        long last = -1;
        for (int f = 0; f < fatSecs.Count; f++)
        {
            var fs = r.Bytes(s + (fatSecs[f] + 1L) * ss, ss);
            if (fs == null) return (null, "doc");
            for (int i = 0; i < ss / 4; i++) if (Bin.U32(fs, i * 4) != 0xFFFFFFFF) last = (long)f * (ss / 4) + i;
        }
        if (last < 0) return (null, "doc");
        long size = (last + 2) * ss;
        if (size > max) return (null, "doc");
        var body = r.Bytes(s, (int)Math.Min(size, 4 << 20)) ?? [];
        string u = Encoding.Unicode.GetString(body);
        string ext = u.Contains("WordDocument") ? "doc" : u.Contains("Workbook") || u.Contains("Book\0") ? "xls"
                   : u.Contains("PowerPoint Document") ? "ppt" : u.Contains("__substg1.0_") ? "msg" : "cfb";
        return (size, ext);
    }

    private static (long?, string) Tiff(CarveReader r, long s, bool le, ReadOnlySpan<byte> h, long max)
    {
        uint R32(long p) => le ? r.U32(p) : r.BE32(p);
        ushort R16(long p) => le ? r.U16(p) : r.BE16(p);
        long maxEnd = 8;
        string ext = h[8] == 'C' && h[9] == 'R' ? "cr2" : "tif";
        string make = "";
        bool dng = false;
        var visited = new HashSet<long>();
        var pending = new Queue<long>();
        pending.Enqueue(le ? Bin.U32(h, 4) : Bin.U32BE(h, 4));
        int[] typeSize = [0, 1, 1, 2, 4, 8, 1, 1, 2, 4, 8, 4, 8, 4, 0, 0, 8, 8, 8];
        while (pending.Count > 0 && visited.Count < 64)
        {
            long ifd = pending.Dequeue();
            if (ifd < 8 || ifd >= max || !visited.Add(ifd)) continue;
            int n = R16(s + ifd);
            if (n is 0 or > 1000) { if (visited.Count == 1) return (null, ext); continue; }
            maxEnd = Math.Max(maxEnd, ifd + 2 + n * 12L + 4);
            var offs = new Dictionary<int, (int type, uint count, long valPos)>();
            for (int i = 0; i < n; i++)
            {
                long e = s + ifd + 2 + i * 12L;
                int tag = R16(e), type = R16(e + 2);
                uint count = R32(e + 4);
                if (type <= 0 || type >= typeSize.Length || typeSize[type] == 0) continue;
                long bytes = (long)count * typeSize[type];
                long valPos = bytes > 4 ? R32(e + 8) : e + 8 - s;
                if (bytes > 4) maxEnd = Math.Max(maxEnd, valPos + bytes);
                offs[tag] = (type, count, valPos);
                if (tag == 271 && bytes < 64) make = Encoding.ASCII.GetString(r.Bytes(s + valPos, (int)bytes) ?? []);
                if (tag == 50706) dng = true;
            }
            long Val(int type, long pos, int i) => type == 3 ? R16(s + pos + i * 2) : R32(s + pos + i * 4);
            void Pair(int a, int b)
            {
                if (!offs.TryGetValue(a, out var o) || !offs.TryGetValue(b, out var c)) return;
                for (int i = 0; i < Math.Min(o.count, 100_000); i++)
                    maxEnd = Math.Max(maxEnd, Val(o.type, o.valPos, i) + Val(c.type, c.valPos, i));
            }
            Pair(273, 279); Pair(324, 325); Pair(513, 514);
            foreach (var sub in new[] { 330, 34665 })
                if (offs.TryGetValue(sub, out var so))
                    for (int i = 0; i < Math.Min(so.count, 16); i++) pending.Enqueue(Val(so.type == 3 ? 3 : 4, so.valPos, i));
            pending.Enqueue(R32(s + ifd + 2 + n * 12L));
        }
        if (maxEnd < 1024 || maxEnd > max) return (null, ext);
        if (ext == "tif")
        {
            if (dng) ext = "dng";
            else if (make.StartsWith("NIKON", StringComparison.OrdinalIgnoreCase)) ext = "nef";
            else if (make.StartsWith("SONY", StringComparison.OrdinalIgnoreCase)) ext = "arw";
            else if (make.StartsWith("PENTAX", StringComparison.OrdinalIgnoreCase)) ext = "pef";
        }
        return (maxEnd, ext);
    }

    private static (long?, string) Pe(CarveReader r, long s, long max)
    {
        uint lfanew = r.U32(s + 0x3C);
        if (lfanew is < 0x40 or > 4096 || r.U32(s + lfanew) != 0x00004550) return (null, "exe");
        long coff = s + lfanew + 4;
        int nsec = r.U16(coff + 2), optSize = r.U16(coff + 16), chars = r.U16(coff + 18);
        long opt = coff + 20;
        int magic = r.U16(opt);
        if (nsec is 0 or > 96 || magic is not (0x10B or 0x20B)) return (null, "exe");
        long end = opt + optSize + nsec * 40L - s;
        long secTab = opt + optSize;
        for (int i = 0; i < nsec; i++)
        {
            long raw = r.U32(secTab + i * 40 + 16), ptr = r.U32(secTab + i * 40 + 20);
            if (raw > 0) end = Math.Max(end, ptr + raw);
        }
        long dd = opt + (magic == 0x10B ? 96 : 112);
        if (r.U32(opt + (magic == 0x10B ? 92 : 108)) > 4)
        {
            long certOfs = r.U32(dd + 4 * 8), certSize = r.U32(dd + 4 * 8 + 4);
            if (certOfs > 0 && certSize > 0) end = Math.Max(end, certOfs + certSize);
        }
        if (end > max || end < 512) return (null, "exe");
        return (end, (chars & 0x2000) != 0 ? "dll" : "exe");
    }

    private static (long?, string) Ogg(CarveReader r, long s, long max)
    {
        long p = s, end = s + max;
        int pages = 0;
        string ext = "ogg";
        var first = r.Bytes(s + 28, 8) ?? [];
        if (Bin.Match(first, 0, "OpusHead"u8)) ext = "opus";
        else if (first.Length > 6 && first[0] == 0x80 && Bin.Match(first, 1, "theora"u8)) ext = "ogv";
        Span<byte> sig = stackalloc byte[4];
        while (p < end)
        {
            if (!r.Read(p, sig) || !sig.SequenceEqual("OggS"u8)) break;
            int type = r.Byte(p + 5), nseg = r.Byte(p + 26);
            if (nseg < 0) break;
            var lace = r.Bytes(p + 27, nseg);
            if (lace == null) break;
            p += 27 + nseg + lace.Sum(b => (long)b);
            pages++;
            if ((type & 4) != 0) return (p - s, ext);
        }
        return (pages >= 2 ? p - s : null, ext);
    }

    private static ReadOnlySpan<byte> AsfHeader => [0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C];
    private static readonly byte[][] AsfTop =
    [
        [0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C],
        [0x36, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C],
        [0x90, 0x08, 0x00, 0x33, 0xB1, 0xE5, 0xCF, 0x11, 0x89, 0xF4, 0x00, 0xA0, 0xC9, 0x03, 0x49, 0xCB],
        [0xD3, 0x29, 0xE2, 0xD6, 0xDA, 0x35, 0xD1, 0x11, 0x90, 0x34, 0x00, 0xA0, 0xC9, 0x03, 0x49, 0xBE],
    ];

    private static long? Asf(CarveReader r, long s, long max)
    {
        long p = s, end = s + max;
        int objs = 0;
        while (p < end)
        {
            var g = r.Bytes(p, 16);
            if (g == null || !AsfTop.Any(t => t.AsSpan().SequenceEqual(g))) break;
            long sz = (long)r.U64(p + 16);
            if (sz < 24) break;
            p += sz;
            objs++;
        }
        return objs >= 2 && p - s <= max ? p - s : null;
    }

    private static readonly HashSet<string> IsoBoxes = ["ftyp", "moov", "mdat", "free", "skip", "wide", "uuid", "meta", "moof", "mfra", "pdin", "sidx", "styp", "emsg", "prft", "pnot", "junk", "idat"];

    private static (long?, string) IsoBmff(CarveReader r, long s, ReadOnlySpan<byte> h, long max)
    {
        string brand = Encoding.ASCII.GetString(h.Slice(8, 4));
        string ext = brand switch
        {
            "qt  " => "mov", "M4A " or "M4B " => "m4a", "heic" or "heix" or "mif1" or "msf1" or "hevc" or "heim" or "heis" => "heic",
            "avif" => "avif", "crx " => "cr3",
            _ when brand.StartsWith("3g") => "3gp",
            _ => "mp4",
        };
        long p = s, end = s + max;
        bool payload = false;
        Span<byte> t = stackalloc byte[4];
        while (p < end)
        {
            long size = r.BE32(p);
            if (!r.Read(p + 4, t)) break;
            string type = Encoding.ASCII.GetString(t);
            if (!IsoBoxes.Contains(type)) break;
            if (size == 1) size = (long)r.BE64(p + 8);
            else if (size == 0) break; // "até o fim do arquivo": sem como saber
            if (size < 8) break;
            if (type is "moov" or "mdat" or "meta") payload = true;
            p += size;
        }
        if (!payload || p - s > max) return (null, ext);
        return (p - s, ext);
    }

    // ───────────── varredura ─────────────

    /// <summary>
    /// Percorre as faixas dadas do dispositivo, setor a setor, e pendura o que achar em
    /// <paramref name="into"/>, numa pasta por tipo.
    /// </summary>
    public static void Carve(IBlockDevice dev, IReadOnlyList<(long offset, long length)> ranges, ScanResult into, ScanOptions opt,
                             IProgress<ScanProgress>? progress, CancellationToken ct, long? ntfsClusterSize = null)
    {
        var reader = new CarveReader(dev);
        int ss = dev.SectorSize;
        long total = ranges.Sum(x => x.length), done = 0;
        const int block = 4 << 20;
        var buf = new byte[block];
        var root = into.GetOrAddFolder(into.Root, "Encontrados por assinatura", "varredura profunda: sem nome nem pasta originais");
        var counters = new Dictionary<string, int>();
        var orphans = new List<(NtfsRecord rec, long abs)>();
        long found = 0, lastReport = 0;
        int ntfsRecSize = 1024;

        foreach (var (rStart, rLen) in ranges)
        {
            long pos = (rStart + ss - 1) / ss * ss, end = rStart + rLen;
            while (pos < end)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(block, end - pos);
                int got;
                try { got = dev.Read(pos, buf.AsSpan(0, n)); }
                catch (MediaReadException)
                {
                    // bloco com setor ruim: pula um setor por vez em vez de perder o bloco inteiro
                    into.Log.Add($"Setor ilegível perto de {pos / ss:N0}.");
                    pos += ss; done += ss; continue;
                }
                if (got <= 0) break;
                long advanceTo = pos + got;
                for (int o = 0; o + 32 <= got; o += ss)
                {
                    var h = buf.AsSpan(o, Math.Min(512, got - o));
                    long abs = pos + o;
                    if (ntfsClusterSize != null && opt.OrphanMftRecords && Bin.Match(h, 0, "FILE"u8) && h.Length >= 48)
                    {
                        // registro MFT solto (de um NTFS formatado ou com o MFT perdido): guarda para montar a árvore no fim
                        var raw = reader.Bytes(abs, ntfsRecSize);
                        if (raw != null)
                        {
                            uint recNo = Bin.U16(raw, 4) >= 0x30 ? Bin.U32(raw, 44) : uint.MaxValue;
                            var rec = NtfsVolume.ParseRecordStatic(raw, recNo);
                            if (rec?.BestName != null && rec.BaseIndex == 0) orphans.Add((rec, abs));
                        }
                        continue;
                    }
                    var hit = Identify(reader, abs, h, Math.Min(MaxDefault, dev.Length - abs));
                    if (hit == null) continue;
                    if (opt.CarveTypes != null && !opt.CarveTypes.Contains(GroupOf(hit.Ext))) continue;
                    counters.TryGetValue(hit.Ext, out int k);
                    counters[hit.Ext] = ++k;
                    var folder = into.GetOrAddFolder(root, hit.Kind);
                    into.Attach(folder, new RecoveredItem
                    {
                        Id = into.NewId(), Name = $"{hit.Ext.ToUpperInvariant()}_{k:D6}.{hit.Ext}", Origin = ItemOrigin.Carved,
                        Size = hit.Size, Data = new ExtentSource(dev, [new Extent(abs, hit.Size)], hit.Size),
                        Condition = Condition.Unknown, DeviceOffset = abs, Note = $"setor {abs / ss:N0}",
                    });
                    found++;
                    // pula o arquivo inteiro: evita escavar miniaturas de dentro dele de novo
                    long next = (abs + hit.Size + ss - 1) / ss * ss;
                    if (next >= pos + got) { advanceTo = Math.Min(next, end); break; }
                    o = (int)(next - pos) - ss;
                }
                done += advanceTo - pos;
                pos = advanceTo;
                if (done - lastReport > (32 << 20) || pos >= end)
                {
                    lastReport = done;
                    progress?.Report(new("Varredura profunda (por assinatura)", total == 0 ? 1 : (double)done / total, found, pos, dev.Length));
                }
            }
        }
        into.Log.Add($"Varredura profunda: {found:N0} arquivos reconhecidos pelo conteúdo.");
        if (orphans.Count > 0) BuildOrphanTree(dev, orphans, into, ntfsClusterSize ?? 4096);
    }

    /// <summary>
    /// Monta a árvore de um MFT antigo a partir dos registros soltos: cada registro sabe o próprio
    /// número e o número da pasta-mãe. O tamanho de cluster antigo (pode diferir do atual depois de
    /// uma formatação) é deduzido do registro do $MFT antigo: ele diz em que cluster começa, e nós
    /// sabemos em que byte o encontramos.
    /// </summary>
    /// <summary>Quanto dos dados caiu em área que o sistema de arquivos atual usa (= foi sobrescrito).</summary>
    private static Condition Overlap(AllocationMap? map, IDataSource src)
    {
        if (map == null || src is not ExtentSource es) return Condition.Unknown;
        long total = 0, hit = 0;
        foreach (var e in es.Extents)
        {
            if (e.Sparse) continue;
            long step = Math.Max(map.ClusterSize, e.Length / 512);
            for (long o = 0; o < e.Length; o += step)
            {
                long c = (e.DeviceOffset + o - map.DataStart) / map.ClusterSize;
                if (c < 0 || c >= map.ClusterCount) continue;
                total++;
                if (map.IsClusterAllocated(c)) hit++;
            }
        }
        return total == 0 ? Condition.Unknown : hit == 0 ? Condition.Good : hit >= total ? Condition.Overwritten : Condition.Partial;
    }

    private static void BuildOrphanTree(IBlockDevice dev, List<(NtfsRecord rec, long abs)> orphans, ScanResult into, long fallbackCluster)
    {
        // qual $MFT e qual cluster explicam a posição dos registros soltos? (o $MFT novo também
        // aparece por aí, e com outro tamanho de cluster: votação, não o primeiro que achar)
        long cs = fallbackCluster;
        int best = 0;
        var numbered = orphans.Where(o => o.rec.Index != uint.MaxValue).ToList();
        foreach (var cand in orphans.Where(o => o.rec.Index == 0 && o.rec.BestName?.Name == "$MFT" && o.rec.UnnamedData?.Runs is { Count: > 0 }))
        {
            var runs = cand.rec.UnnamedData!.Runs!;
            for (long c = 512; c <= 65536; c *= 2)
            {
                if (runs[0].Lcn * c != cand.abs) continue;
                int votes = 0;
                foreach (var o in numbered.Take(4000))
                {
                    long byteOfs = (long)o.rec.Index * 1024, vcnBase = 0;
                    foreach (var r in runs)
                    {
                        long len = r.Length * c;
                        if (byteOfs < vcnBase + len) { if (!r.Sparse && r.Lcn * c + byteOfs - vcnBase == o.abs) votes++; break; }
                        vcnBase += len;
                    }
                }
                if (votes > best) { best = votes; cs = c; }
            }
        }
        if (best == 0)
        {
            // sem o $MFT antigo: índices de pasta ("INDX") e assinaturas de arquivo dizem qual cluster acerta
            var reader = new CarveReader(dev);
            var magic = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["jpg"] = [0xFF, 0xD8, 0xFF], ["jpeg"] = [0xFF, 0xD8, 0xFF], ["png"] = [0x89, 0x50, 0x4E, 0x47], ["pdf"] = "%PDF"u8.ToArray(),
                ["zip"] = "PK"u8.ToArray(), ["docx"] = "PK"u8.ToArray(), ["xlsx"] = "PK"u8.ToArray(), ["pptx"] = "PK"u8.ToArray(),
                ["gif"] = "GIF8"u8.ToArray(), ["mp3"] = "ID3"u8.ToArray(), ["exe"] = "MZ"u8.ToArray(), ["dll"] = "MZ"u8.ToArray(),
            };
            var probes = new List<(long lcn, byte[] sig)>();
            foreach (var (rec, _) in orphans)
            {
                if (rec.IsDir)
                {
                    var ia = rec.Attrs.FirstOrDefault(a => a.Type == 0xA0 && a.NonResident && a.Runs is { Count: > 0 });
                    if (ia != null && !ia.Runs![0].Sparse) probes.Add((ia.Runs[0].Lcn, "INDX"u8.ToArray()));
                }
                else if (rec.UnnamedData is { NonResident: true, Runs.Count: > 0 } d && !d.Runs![0].Sparse
                         && magic.TryGetValue(Path.GetExtension(rec.BestName!.Name).TrimStart('.'), out var m))
                    probes.Add((d.Runs[0].Lcn, m));
                if (probes.Count > 400) break;
            }
            for (long c = 512; c <= 65536 && probes.Count > 0; c *= 2)
            {
                int votes = 0;
                foreach (var (lcn, sig) in probes)
                {
                    var b = lcn * c + sig.Length <= dev.Length ? reader.Bytes(lcn * c, sig.Length) : null;
                    if (b != null && b.AsSpan().SequenceEqual(sig)) votes++;
                }
                if (votes > best) { best = votes; cs = c; }
            }
        }
        // o mesmo registro pode aparecer mais de uma vez (cópias antigas): fica a de maior sequência
        var byIndex = orphans.Where(o => o.rec.Index != uint.MaxValue && o.rec.Index >= 16)
            .GroupBy(o => o.rec.Index).ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.rec.Seq).First());
        var unnumbered = orphans.Where(o => o.rec.Index == uint.MaxValue).ToList();
        var top = into.GetOrAddFolder(into.Root, "MFT antigo (antes da formatação)", $"registros soltos reorganizados · cluster antigo de {cs / 1024.0:0.#} KB");
        var items = new Dictionary<long, RecoveredItem>();
        foreach (var (idx, (rec, abs)) in byIndex)
        {
            var fn = rec.BestName!;
            var item = new RecoveredItem
            {
                Id = into.NewId(), Name = fn.Name, IsDirectory = rec.IsDir, Origin = ItemOrigin.Orphan,
                Created = rec.Created, Modified = rec.Modified, Condition = Condition.Unknown, DeviceOffset = abs,
            };
            if (!rec.IsDir && rec.UnnamedData is { } data)
            {
                item.Data = !data.NonResident ? new ResidentSource(data.Resident ?? [])
                    : new ExtentSource(dev, (data.Runs ?? []).Select(x => new Extent(x.Sparse ? -1 : x.Lcn * cs, x.Length * cs)), data.RealSize);
                item.Size = item.Data.Length;
                if (item.Size <= 0 || item.Size > dev.Length) continue;
                item.Condition = Overlap(into.Allocation, item.Data);
            }
            else if (!rec.IsDir) continue;
            items[idx] = item;
        }
        int linked = 0;
        foreach (var (idx, item) in items)
        {
            var fn = byIndex[(uint)idx].rec.BestName!;
            RecoveredItem parent = top;
            if (fn.ParentIndex == 5) parent = top;
            else if (items.TryGetValue(fn.ParentIndex, out var p) && p.IsDirectory) { parent = p; linked++; }
            else parent = into.GetOrAddFolder(top, "$Pasta perdida", "a pasta-mãe deste registro não foi encontrada");
            parent.Children ??= [];
            parent.Children.Add(item);
            item.Parent = parent;
            item.ParentId = parent.Id;
            into.Items.Add(item);
        }
        // pastas vazias (sem nenhum arquivo dentro, em nenhum nível) não ajudam
        static bool HasFiles(RecoveredItem d) => d.Children?.Any(c => !c.IsDirectory || HasFiles(c)) == true;
        foreach (var d in items.Values.Where(i => i.IsDirectory && !HasFiles(i)).ToList())
        {
            d.Parent?.Children?.Remove(d);
            into.Items.Remove(d);
        }
        into.Log.Add($"MFT antigo: {items.Count(i => !i.Value.IsDirectory):N0} arquivos com nome original, {items.Count(i => i.Value.IsDirectory)} pastas reconstruídas, cluster antigo {cs} bytes.");
        _ = unnumbered;
    }
}
