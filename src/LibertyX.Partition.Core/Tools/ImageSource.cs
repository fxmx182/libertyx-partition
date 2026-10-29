using System.IO.Compression;
using System.Text;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.Xz;
using ZstdStream = SharpCompress.Compressors.ZStandard.DecompressionStream;
using static LibertyX.Partition.Core.IO.Bin;

namespace LibertyX.Partition.Core.Tools;

public enum ImageKind { Unknown, WindowsIso, HybridIso, PlainIso, DiskImage }

/// <summary>
/// Uma imagem para gravar num pendrive ou cartão, como o balenaEtcher aceita: ISO, IMG/RAW/BIN/DD/WIC
/// e as mesmas compactadas em ZIP, GZ, XZ, BZ2 ou ZST — descompactadas enquanto gravam, sem
/// extrair antes. Reconhece o tipo pelo conteúdo (não pela extensão) para escolher o modo certo.
/// </summary>
public sealed class ImageSource
{
    public string Path { get; }
    public string? Compression { get; private set; }   // "ZIP", "GZIP", "XZ", "BZIP2", "ZSTD" ou nulo
    public string? ZipEntry { get; private set; }
    public long FileLength { get; }
    /// <summary>Tamanho descompactado, quando a imagem diz (ZIP sempre; XZ/GZ nem sempre).</summary>
    public long? Length { get; private set; }
    public ImageKind Kind { get; private set; }
    public bool HasMbr { get; private set; }
    public bool HasGpt { get; private set; }
    public bool IsIso { get; private set; }
    public string? VolumeLabel { get; private set; }

    private ImageSource(string path) { Path = path; FileLength = new FileInfo(path).Length; }

    public static ImageSource Inspect(string path)
    {
        var s = new ImageSource(path);
        var head = new byte[8];
        using (var f = File.OpenRead(path)) f.ReadExactly(head, 0, (int)Math.Min(8, f.Length));
        if (head[0] == 0x1F && head[1] == 0x8B) s.Compression = "GZIP";
        else if (head[0] == 0xFD && head[1] == 0x37 && head[2] == 0x7A && head[3] == 0x58 && head[4] == 0x5A) s.Compression = "XZ";
        else if (head[0] == 0x50 && head[1] == 0x4B && head[2] == 0x03 && head[3] == 0x04) s.Compression = "ZIP";
        else if (head[0] == 0x42 && head[1] == 0x5A && head[2] == 0x68) s.Compression = "BZIP2";
        else if (head[0] == 0x28 && head[1] == 0xB5 && head[2] == 0x2F && head[3] == 0xFD) s.Compression = "ZSTD";

        if (s.Compression == "ZIP")
        {
            using var z = ZipFile.OpenRead(path);
            // a maior entrada que pareça imagem (há zips com um README junto)
            var e = z.Entries.Where(x => x.Length > 0).OrderByDescending(x => x.Length).FirstOrDefault()
                    ?? throw new InvalidDataException(L.T("O ZIP está vazio."));
            s.ZipEntry = e.FullName;
            s.Length = e.Length;
        }
        else if (s.Compression == null) s.Length = s.FileLength;
        else if (s.Compression == "GZIP" && s.FileLength < (4L << 30))
        {
            // o GZIP guarda o tamanho original nos 4 últimos bytes (módulo 4 GB): só confiável abaixo disso
            using var f = File.OpenRead(path);
            f.Seek(-4, SeekOrigin.End);
            var t = new byte[4];
            f.ReadExactly(t);
            long isize = U32(t, 0);
            if (isize >= s.FileLength) s.Length = isize;
        }

        // o começo da imagem (já descompactado) diz o que ela é
        var buf = new byte[64 * 1024];
        int n;
        using (var st = s.Open()) n = ReadFull(st, buf);
        var b = buf.AsSpan(0, n);
        s.HasMbr = n >= 512 && b[510] == 0x55 && b[511] == 0xAA;
        s.HasGpt = n >= 1024 && Match(b, 512, "EFI PART"u8);
        s.IsIso = n >= 0x8006 && b[0x8000] == 1 && Match(b, 0x8001, "CD001"u8);
        if (s.IsIso) s.VolumeLabel = Encoding.ASCII.GetString(b.Slice(0x8028, 32)).Trim();
        s.Kind = !s.IsIso ? (s.HasMbr || s.HasGpt || n > 0 ? ImageKind.DiskImage : ImageKind.Unknown)
               : IsWindowsSetup(s) ? ImageKind.WindowsIso
               : s.HasMbr || s.HasGpt ? ImageKind.HybridIso : ImageKind.PlainIso;
        return s;
    }

    /// <summary>A imagem descompactada, do começo, como fluxo de leitura.</summary>
    public Stream Open() => Open(out _);

    /// <summary>Idem, devolvendo também o arquivo cru: a posição nele dá o progresso quando o tamanho final é desconhecido.</summary>
    public Stream Open(out FileStream raw)
    {
        var f = raw = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        switch (Compression)
        {
            case null: return f;
            case "GZIP": return new GZipStream(f, CompressionMode.Decompress);
            case "XZ": return new XZStream(f);
            // leaveOpen: o BZIP2 fecharia o arquivo ao chegar ao fim, e a posição ainda serve ao progresso
            case "BZIP2": return new OwnerStream(BZip2Stream.Create(f, SharpCompress.Compressors.CompressionMode.Decompress, true, true, false), f);
            case "ZSTD": return new ZstdStream(f, 1 << 20, true, false);
            case "ZIP":
                {
                    var z = new ZipArchive(f, ZipArchiveMode.Read);
                    var e = z.GetEntry(ZipEntry!) ?? throw new InvalidDataException(L.T("Entrada sumiu do ZIP."));
                    return new OwnerStream(e.Open(), z);
                }
            default: throw new NotSupportedException(Compression);
        }
    }

    /// <summary>Uma linha para a tela: o que é, tamanho e compressão.</summary>
    public string Describe()
    {
        string kind = Kind switch
        {
            ImageKind.WindowsIso => L.T("ISO de instalação do Windows"),
            ImageKind.HybridIso => L.T("ISO híbrida (Linux e ferramentas): grava direto, inicia em BIOS e UEFI"),
            ImageKind.PlainIso => L.T("ISO comum (sem boot híbrido)"),
            ImageKind.DiskImage => HasGpt ? L.T("Imagem de disco (GPT)") : HasMbr ? L.T("Imagem de disco (MBR)") : L.T("Imagem de disco"),
            _ => L.T("Formato não reconhecido"),
        };
        string size = Length is { } l ? DiskOperations_Size(l) : L.T("tamanho final desconhecido");
        return $"{kind} · {size}" + (Compression != null ? L.T($" · compactada ({Compression}, {DiskOperations_Size(FileLength)})") : "")
               + (VolumeLabel is { Length: > 0 } v ? $" · “{v}”" : "");
    }

    private static string DiskOperations_Size(long b) => Operations.DiskOperations.Size(b);

    internal static int ReadFull(Stream s, byte[] buf) => ReadFull(s, buf, buf.Length);

    internal static int ReadFull(Stream s, byte[] buf, int count)
    {
        int n = 0;
        while (n < count)
        {
            int r = s.Read(buf, n, count - n);
            if (r <= 0) break;
            n += r;
        }
        return n;
    }

    /// <summary>
    /// ISO do Windows: o diretório raiz (ISO 9660) tem a pasta SOURCES e o BOOTMGR. As ISOs da
    /// Microsoft trazem a árvore ISO 9660 junto com a UDF, com nomes curtos.
    /// </summary>
    private static bool IsWindowsSetup(ImageSource s)
    {
        if (s.Compression != null) return false; // ISO do Windows compactada não se grava pelo modo Windows
        try
        {
            using var f = File.OpenRead(s.Path);
            var pvd = new byte[2048];
            f.Seek(0x8000, SeekOrigin.Begin);
            f.ReadExactly(pvd);
            long lba = U32(pvd, 156 + 2), size = U32(pvd, 156 + 10);
            if (size is <= 0 or > 1 << 20) return false;
            var dir = new byte[size];
            f.Seek(lba * 2048, SeekOrigin.Begin);
            f.ReadExactly(dir);
            bool sources = false, bootmgr = false;
            for (int o = 0; o < dir.Length;)
            {
                int len = dir[o];
                if (len == 0) { o = (o / 2048 + 1) * 2048; continue; }
                int nl = dir[o + 32];
                // ISO 9660 nível 1: "BOOTMGR.;1" — sem a versão e sem o ponto de extensão vazia
                string name = Encoding.ASCII.GetString(dir, o + 33, nl).Split(';')[0].TrimEnd('.').ToUpperInvariant();
                if (name == "SOURCES") sources = true;
                if (name == "BOOTMGR") bootmgr = true;
                o += len;
            }
            return sources && bootmgr;
        }
        catch { return false; }
    }

    /// <summary>Fluxo que fecha junto o arquivo ZIP de onde veio.</summary>
    private sealed class OwnerStream(Stream inner, IDisposable owner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); owner.Dispose(); } base.Dispose(disposing); }
    }
}
