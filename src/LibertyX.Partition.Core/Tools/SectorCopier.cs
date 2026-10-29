using System.Diagnostics;
using System.Security.Cryptography;
using LibertyX.Partition.Core.IO;

namespace LibertyX.Partition.Core.Tools;

public readonly record struct CopyProgress(long Done, long Total, double BytesPerSecond, long BadSectors, long Position, string Phase);

public sealed class CopyOptions
{
    /// <summary>Faixas a copiar (deslocamento na origem, tamanho). Nulo = tudo.</summary>
    public List<(long offset, long length)>? Ranges { get; set; }
    public int Retries { get; set; } = 1;
    /// <summary>Calcula SHA-256 da origem durante a cópia (para imagem forense).</summary>
    public bool Hash { get; set; }
    public long TargetOffset { get; set; }
    /// <summary>Copiando para trás (destino depois da origem e sobrepostos): de trás para frente.</summary>
    public bool Backwards { get; set; }
    /// <summary>Hash compartilhado entre várias chamadas (imagem montada por regiões).</summary>
    public IncrementalHash? HashSink { get; set; }
    /// <summary>Somado ao progresso reportado (várias regiões numa operação só).</summary>
    public long ProgressBase { get; set; }
    public long? ProgressTotal { get; set; }
}

public sealed class CopyResult
{
    public long BytesCopied;
    public List<long> BadSectors { get; } = [];
    public string? Sha256;
    public TimeSpan Elapsed;
}

/// <summary>
/// Copiador setor a setor no estilo ddrescue: blocos grandes enquanto dá, e ao primeiro erro
/// desce para setor a setor naquele bloco, anota os ilegíveis e segue — um disco morrendo
/// precisa sair com o máximo possível na primeira passada.
/// </summary>
public static class SectorCopier
{
    public static CopyResult Copy(IBlockDevice src, IBlockDevice dst, CopyOptions opt, IProgress<CopyProgress>? progress,
                                  CancellationToken ct, ManualResetEventSlim? pause = null)
    {
        int ss = src.SectorSize;
        var ranges = opt.Ranges ?? [(0, src.Length)];
        long total = ranges.Sum(r => r.length);
        var res = new CopyResult();
        using var sha = opt.Hash ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
        const int block = 1 << 20;
        var buf = new byte[block];
        var sw = Stopwatch.StartNew();
        long done = 0, lastBytes = 0;
        double speed = 0;
        var lastTick = sw.Elapsed;

        IEnumerable<(long, int)> Blocks(long start, long len)
        {
            if (!opt.Backwards)
                for (long p = start; p < start + len; p += block) yield return (p, (int)Math.Min(block, start + len - p));
            else
                for (long e = start + len; e > start; e -= block) { long b = Math.Max(start, e - block); yield return (b, (int)(e - b)); }
        }

        foreach (var (rs, rl) in opt.Backwards ? ranges.AsEnumerable().Reverse() : ranges)
        {
            foreach (var (p, n) in Blocks(rs, rl))
            {
                ct.ThrowIfCancellationRequested();
                pause?.Wait(ct);
                var span = buf.AsSpan(0, n);
                try
                {
                    int got = src.Read(p, span);
                    if (got < n) span[got..].Clear();
                }
                catch (MediaReadException)
                {
                    for (int o = 0; o < n; o += ss)
                    {
                        var s = span.Slice(o, Math.Min(ss, n - o));
                        bool ok = false;
                        for (int t = 0; t <= opt.Retries && !ok; t++)
                        {
                            try { src.Read(p + o, s); ok = true; } catch (MediaReadException) { }
                        }
                        if (!ok) { s.Clear(); res.BadSectors.Add((p + o) / ss); }
                    }
                }
                sha?.AppendData(span);
                opt.HashSink?.AppendData(span);
                dst.Write(opt.TargetOffset + p, span);
                done += n;
                var now = sw.Elapsed;
                if ((now - lastTick).TotalMilliseconds >= 400)
                {
                    double inst = (done - lastBytes) / (now - lastTick).TotalSeconds;
                    speed = speed == 0 ? inst : speed * 0.7 + inst * 0.3;
                    lastBytes = done; lastTick = now;
                    progress?.Report(new(opt.ProgressBase + done, opt.ProgressTotal ?? total, speed, res.BadSectors.Count, p, L.T("Copiando")));
                }
            }
        }
        progress?.Report(new(opt.ProgressBase + done, opt.ProgressTotal ?? total, speed, res.BadSectors.Count, src.Length, L.T("Gravando cache")));
        dst.Flush();
        res.BytesCopied = done;
        res.Elapsed = sw.Elapsed;
        if (sha != null) res.Sha256 = Convert.ToHexString(sha.GetHashAndReset());
        progress?.Report(new(opt.ProgressBase + done, opt.ProgressTotal ?? total, speed, res.BadSectors.Count, src.Length, L.T("Concluído")));
        return res;
    }

    /// <summary>Confere a cópia relendo os dois lados e comparando SHA-256 por bloco.</summary>
    public static long Verify(IBlockDevice a, IBlockDevice b, long offsetB, IReadOnlyList<(long offset, long length)> ranges,
                              IProgress<CopyProgress>? progress, CancellationToken ct)
    {
        const int block = 1 << 20;
        var x = new byte[block]; var y = new byte[block];
        long total = ranges.Sum(r => r.length), done = 0, mismatches = 0;
        var sw = Stopwatch.StartNew();
        foreach (var (rs, rl) in ranges)
            for (long p = rs; p < rs + rl; p += block)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(block, rs + rl - p);
                try { a.Read(p, x.AsSpan(0, n)); } catch (MediaReadException) { done += n; continue; }
                b.Read(offsetB + p, y.AsSpan(0, n));
                if (!x.AsSpan(0, n).SequenceEqual(y.AsSpan(0, n))) mismatches++;
                done += n;
                if (done % (64 << 20) == 0) progress?.Report(new(done, total, done / Math.Max(0.001, sw.Elapsed.TotalSeconds), mismatches, p, L.T("Verificando")));
            }
        return mismatches;
    }
}
