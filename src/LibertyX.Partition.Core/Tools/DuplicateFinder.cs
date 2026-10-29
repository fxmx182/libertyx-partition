using System.IO.Hashing;

namespace LibertyX.Partition.Core.Tools;

public sealed class DupFile
{
    public required string Path { get; init; }
    public long Size { get; init; }
    public DateTime Modified { get; init; }
    public DateTime Created { get; init; }
}

public sealed class DupGroup
{
    public required string Hash { get; init; }
    public long Size { get; init; }
    public List<DupFile> Files { get; } = [];
    public long Wasted => Size * (Files.Count - 1);
}

public readonly record struct DupProgress(string Phase, long Done, long Total, long Groups, long WastedBytes);

/// <summary>
/// Duplicados de verdade, não "mesmo nome": agrupa por tamanho, depois por uma impressão
/// rápida (início + fim), e só então pelo conteúdo inteiro (xxHash128).
/// </summary>
public static class DuplicateFinder
{
    public static List<DupGroup> Find(IEnumerable<string> roots, long minSize, Func<string, bool>? filter,
                                      IProgress<DupProgress>? progress, CancellationToken ct)
    {
        var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
        var bySize = new Dictionary<long, List<DupFile>>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long listed = 0;
        foreach (var root in roots)
        {
            IEnumerable<FileInfo> files;
            try { files = new DirectoryInfo(root).EnumerateFiles("*", opts); } catch { continue; }
            foreach (var fi in files)
            {
                ct.ThrowIfCancellationRequested();
                long len;
                try { len = fi.Length; } catch { continue; }
                if (len < minSize || (filter != null && !filter(fi.FullName)) || !seen.Add(fi.FullName)) continue;
                if (!bySize.TryGetValue(len, out var l)) bySize[len] = l = [];
                l.Add(new DupFile { Path = fi.FullName, Size = len, Modified = fi.LastWriteTime, Created = fi.CreationTime });
                if (++listed % 2000 == 0) progress?.Report(new(L.T("Listando arquivos"), listed, 0, 0, 0));
            }
        }

        var candidates = bySize.Values.Where(l => l.Count > 1).ToList();
        long totalCand = candidates.Sum(l => l.Count), done = 0;
        var result = new List<DupGroup>();
        long wasted = 0;
        foreach (var group in candidates.OrderByDescending(g => g[0].Size))
        {
            ct.ThrowIfCancellationRequested();
            var byQuick = group.GroupBy(f => Quick(f.Path)).Where(g => g.Key != null && g.Count() > 1);
            foreach (var q in byQuick)
            {
                var full = q.Count() == 1 ? [] : q.GroupBy(f => Full(f.Path, ct)).Where(g => g.Key != null && g.Count() > 1);
                foreach (var g in full)
                {
                    var dg = new DupGroup { Hash = g.Key!, Size = group[0].Size };
                    dg.Files.AddRange(g.OrderBy(f => f.Created));
                    result.Add(dg);
                    wasted += dg.Wasted;
                }
            }
            done += group.Count;
            progress?.Report(new(L.T("Comparando conteúdo"), done, totalCand, result.Count, wasted));
        }
        return result.OrderByDescending(g => g.Wasted).ToList();
    }

    private static string? Quick(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.RandomAccess);
            var h = new XxHash128();
            var buf = new byte[64 * 1024];
            int n = fs.Read(buf);
            h.Append(buf.AsSpan(0, n));
            if (fs.Length > buf.Length * 2)
            {
                fs.Seek(-buf.Length, SeekOrigin.End);
                n = fs.Read(buf);
                h.Append(buf.AsSpan(0, n));
            }
            return Convert.ToHexString(h.GetCurrentHash());
        }
        catch { return null; }
    }

    private static string? Full(string path, CancellationToken ct)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan);
            var h = new XxHash128();
            var buf = new byte[1 << 20];
            int n;
            while ((n = fs.Read(buf)) > 0) { ct.ThrowIfCancellationRequested(); h.Append(buf.AsSpan(0, n)); }
            return Convert.ToHexString(h.GetCurrentHash());
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    public enum KeepRule { Oldest, Newest, ShortestPath, FirstFolder }

    /// <summary>Marca quais apagar: todos menos um por grupo, segundo a regra.</summary>
    public static IEnumerable<DupFile> SelectForRemoval(DupGroup g, KeepRule rule, string? preferredFolder = null)
    {
        DupFile keep = rule switch
        {
            KeepRule.Newest => g.Files.MaxBy(f => f.Modified)!,
            KeepRule.ShortestPath => g.Files.MinBy(f => f.Path.Length)!,
            KeepRule.FirstFolder when preferredFolder != null => g.Files.FirstOrDefault(f => f.Path.StartsWith(preferredFolder, StringComparison.OrdinalIgnoreCase)) ?? g.Files[0],
            _ => g.Files.MinBy(f => f.Modified)!,
        };
        return g.Files.Where(f => f != keep);
    }
}
