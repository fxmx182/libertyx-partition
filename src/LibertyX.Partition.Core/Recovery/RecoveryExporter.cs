using System.Text;
using LibertyX.Partition.Core.IO;

namespace LibertyX.Partition.Core.Recovery;

public readonly record struct ExportProgress(long FilesDone, long FilesTotal, long BytesDone, long BytesTotal, string Current);

public sealed class ExportReport
{
    public int Saved, Failed, WithBadSectors;
    public long Bytes;
    public string ReportPath = "";
}

/// <summary>Grava os itens escolhidos numa pasta de destino, recriando a árvore de pastas.</summary>
public static class RecoveryExporter
{
    public static ExportReport Export(IReadOnlyList<RecoveredItem> files, string destination, bool keepTree,
                                      IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        var rep = new ExportReport();
        long totalBytes = files.Sum(f => f.Size), done = 0;
        var log = new StringBuilder(L.T("Status;Arquivo;Tamanho;Origem;Observação\r\n"));
        var buf = new byte[1 << 20];
        int i = 0;
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            i++;
            string rel = keepTree ? RelativePath(f) : Sanitize(f.Name);
            string target = Unique(Path.Combine(destination, rel));
            progress?.Report(new(i - 1, files.Count, done, totalBytes, rel));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                int bad = 0;
                using (var outF = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    if (f.Data != null)
                    {
                        long len = f.Data.Length;
                        for (long p = 0; p < len; p += buf.Length)
                        {
                            ct.ThrowIfCancellationRequested();
                            int n = (int)Math.Min(buf.Length, len - p);
                            try { f.Data.Read(p, buf.AsSpan(0, n)); }
                            catch (MediaReadException)
                            {
                                // setor ruim: tenta por setor e completa com zeros só o que não deu
                                bad += ReadSalvage(f.Data, p, buf.AsSpan(0, n));
                            }
                            outF.Write(buf, 0, n);
                            done += n;
                            if ((p & ((8 << 20) - 1)) == 0) progress?.Report(new(i - 1, files.Count, done, totalBytes, rel));
                        }
                    }
                }
                try
                {
                    if (f.Modified is { } m) File.SetLastWriteTime(target, m);
                    if (f.Created is { } c) File.SetCreationTime(target, c);
                }
                catch { }
                rep.Saved++;
                rep.Bytes += f.Size;
                if (bad > 0) rep.WithBadSectors++;
                log.Append(bad > 0 ? "PARCIAL" : "OK").Append(';').Append(rel).Append(';').Append(f.Size).Append(';')
                   .Append(f.Origin).Append(';').Append(bad > 0 ? L.T($"{bad} setores ilegíveis preenchidos com zeros") : f.Note ?? "").Append("\r\n");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                rep.Failed++;
                log.Append("FALHA;").Append(rel).Append(';').Append(f.Size).Append(';').Append(f.Origin).Append(';').Append(ex.Message).Append("\r\n");
            }
        }
        rep.ReportPath = Path.Combine(destination, $"Relatório de recuperação {DateTime.Now:yyyy-MM-dd HH-mm}.csv");
        File.WriteAllText(rep.ReportPath, log.ToString(), new UTF8Encoding(true));
        progress?.Report(new(files.Count, files.Count, totalBytes, totalBytes, ""));
        return rep;
    }

    private static int ReadSalvage(IDataSource src, long pos, Span<byte> dst)
    {
        int bad = 0;
        for (int o = 0; o < dst.Length; o += 512)
        {
            var piece = dst.Slice(o, Math.Min(512, dst.Length - o));
            try { src.Read(pos + o, piece); }
            catch (MediaReadException) { piece.Clear(); bad++; }
        }
        return bad;
    }

    public static string RelativePath(RecoveredItem f)
    {
        var parts = new List<string>();
        for (var p = f; p != null; p = p.Parent) parts.Add(Sanitize(p.Name));
        parts.Reverse();
        if (parts.Count > 1) parts.RemoveAt(0); // a raiz do volume
        return Path.Combine([.. parts]);
    }

    public static string Sanitize(string name)
    {
        var bad = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).ToHashSet();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name) sb.Append(bad.Contains(c) || c < 32 ? '_' : c);
        var s = sb.ToString().Trim().TrimEnd('.');
        return s.Length == 0 ? "_" : s.Length > 200 ? s[..200] : s;
    }

    private static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!, name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var p = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
    }
}
