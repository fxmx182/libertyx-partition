using LibertyX.Partition.Core.IO;

namespace LibertyX.Partition.Core.Recovery;

public enum ItemOrigin { Live, Deleted, Carved, Orphan }

/// <summary>Quanto do conteúdo ainda está lá. Medido contra o mapa de alocação do volume.</summary>
public enum Condition { Unknown, Excellent, Good, Partial, Overwritten }

public enum FileCategory { Folder, Image, Video, Audio, Document, Archive, Code, Executable, Database, Other }

/// <summary>Os bytes de um arquivo, onde quer que estejam (dentro do registro MFT, em extents, comprimidos...).</summary>
public interface IDataSource
{
    long Length { get; }
    int Read(long offset, Span<byte> buffer);
}

public sealed class ResidentSource(byte[] data) : IDataSource
{
    public long Length => data.Length;
    public int Read(long offset, Span<byte> buffer)
    {
        if (offset >= data.Length) return 0;
        int n = (int)Math.Min(buffer.Length, data.Length - offset);
        data.AsSpan((int)offset, n).CopyTo(buffer);
        return n;
    }
}

/// <summary>Um pedaço do arquivo no dispositivo; deslocamento negativo = esparso (zeros).</summary>
public readonly record struct Extent(long DeviceOffset, long Length)
{
    public bool Sparse => DeviceOffset < 0;
}

public sealed class ExtentSource : IDataSource
{
    private readonly IBlockDevice _dev;
    private readonly Extent[] _ext;
    private readonly long[] _starts;
    public long Length { get; }
    public IReadOnlyList<Extent> Extents => _ext;
    public IBlockDevice Device => _dev;

    public ExtentSource(IBlockDevice dev, IEnumerable<Extent> extents, long length)
    {
        _dev = dev;
        _ext = extents.ToArray();
        _starts = new long[_ext.Length];
        long p = 0;
        for (int i = 0; i < _ext.Length; i++) { _starts[i] = p; p += _ext[i].Length; }
        Length = Math.Min(length, p);
    }

    public int Read(long offset, Span<byte> buffer)
    {
        if (offset >= Length) return 0;
        int want = (int)Math.Min(buffer.Length, Length - offset), done = 0;
        int i = Array.BinarySearch(_starts, offset);
        if (i < 0) i = ~i - 1;
        while (done < want && i < _ext.Length)
        {
            var e = _ext[i];
            long inExt = offset + done - _starts[i];
            int n = (int)Math.Min(want - done, e.Length - inExt);
            if (n <= 0) { i++; continue; }
            var dst = buffer.Slice(done, n);
            if (e.Sparse) dst.Clear();
            else
            {
                int got = _dev.ReadBytes(e.DeviceOffset + inExt, dst);
                if (got < n) dst[got..].Clear();
            }
            done += n;
            i++;
        }
        return done;
    }
}

/// <summary>Um arquivo ou pasta encontrado: vivo, apagado ou escavado por assinatura.</summary>
public sealed class RecoveredItem
{
    public long Id { get; init; }
    public long ParentId { get; set; } = -1;
    public string Name { get; set; } = "";
    public bool IsDirectory { get; init; }
    public ItemOrigin Origin { get; set; }
    public long Size { get; set; }
    public DateTime? Created { get; set; }
    public DateTime? Modified { get; set; }
    public IDataSource? Data { get; set; }
    public Condition Condition { get; set; }
    /// <summary>Observação curta: "comprimido NTFS", "criptografado (EFS)", "pasta perdida"...</summary>
    public string? Note { get; set; }
    /// <summary>Deslocamento no dispositivo examinado (para escavados e órfãos).</summary>
    public long? DeviceOffset { get; set; }
    public RecoveredItem? Parent { get; set; }
    public List<RecoveredItem>? Children { get; set; }

    public string Extension
    {
        get
        {
            int i = Name.LastIndexOf('.');
            return i > 0 && i < Name.Length - 1 ? Name[(i + 1)..].ToLowerInvariant() : "";
        }
    }

    public FileCategory Category => IsDirectory ? FileCategory.Folder : FileTypes.Categorize(Extension);

    public string FullPath
    {
        get
        {
            var parts = new Stack<string>();
            for (var p = this; p != null && parts.Count < 512; p = p.Parent) parts.Push(p.Name);
            return string.Join('\\', parts);
        }
    }

    public bool IsDeletedOrLost => Origin != ItemOrigin.Live;
}

public static class FileTypes
{
    private static readonly Dictionary<string, FileCategory> Map = new(StringComparer.OrdinalIgnoreCase);

    static FileTypes()
    {
        void Add(FileCategory c, params string[] exts) { foreach (var e in exts) Map[e] = c; }
        Add(FileCategory.Image, "jpg", "jpeg", "png", "gif", "bmp", "tif", "tiff", "webp", "heic", "heif", "cr2", "cr3", "nef", "arw", "dng", "raf", "orf", "rw2", "psd", "svg", "ico", "avif");
        Add(FileCategory.Video, "mp4", "mov", "m4v", "avi", "mkv", "webm", "wmv", "flv", "3gp", "mts", "m2ts", "ts", "mpg", "mpeg", "vob");
        Add(FileCategory.Audio, "mp3", "wav", "flac", "ogg", "opus", "m4a", "aac", "wma", "aiff", "amr", "mid");
        Add(FileCategory.Document, "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "odt", "ods", "odp", "rtf", "txt", "csv", "epub", "msg", "eml", "md", "xml", "html", "htm", "pages", "key", "numbers");
        Add(FileCategory.Archive, "zip", "rar", "7z", "gz", "tar", "bz2", "xz", "iso", "cab", "zst", "apk", "jar");
        Add(FileCategory.Code, "cs", "js", "ts", "py", "java", "c", "cpp", "h", "json", "css", "sql", "sh", "ps1", "kt", "swift", "go", "rs");
        Add(FileCategory.Executable, "exe", "dll", "msi", "sys", "bat", "cmd", "com", "scr");
        Add(FileCategory.Database, "sqlite", "db", "mdb", "accdb", "pst", "ost", "dbf");
    }

    public static FileCategory Categorize(string ext) => Map.TryGetValue(ext, out var c) ? c : FileCategory.Other;

    public static string CategoryName(FileCategory c) => c switch
    {
        FileCategory.Folder => L.T("Pastas"), FileCategory.Image => L.T("Imagens"), FileCategory.Video => L.T("Vídeos"),
        FileCategory.Audio => L.T("Áudio"), FileCategory.Document => L.T("Documentos"), FileCategory.Archive => L.T("Compactados"),
        FileCategory.Code => L.T("Código"), FileCategory.Executable => L.T("Programas"), FileCategory.Database => L.T("Bancos de dados"),
        _ => L.T("Outros"),
    };
}
