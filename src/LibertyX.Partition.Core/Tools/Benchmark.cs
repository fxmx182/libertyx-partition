using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using LibertyX.Partition.Core.IO;
using Microsoft.Win32.SafeHandles;

namespace LibertyX.Partition.Core.Tools;

public sealed record BenchTest(string Id, string Label, bool Sequential, int BlockSize, int QueueDepth, bool Write);

public sealed record BenchResult(BenchTest Test, double MBps, double Iops, double AvgLatencyUs);

public readonly record struct BenchProgress(string Phase, double Fraction, BenchResult? Partial);

/// <summary>Buffer alinhado para E/S sem cache (exigência do FILE_FLAG_NO_BUFFERING).</summary>
public sealed unsafe class AlignedBuffer : MemoryManager<byte>
{
    private readonly byte* _p;
    private readonly int _len;
    public AlignedBuffer(int length, int align = 4096)
    {
        _len = length;
        _p = (byte*)NativeMemory.AlignedAlloc((nuint)length, (nuint)align);
        new Span<byte>(_p, length).Clear();
    }
    public override Span<byte> GetSpan() => new(_p, _len);
    public override MemoryHandle Pin(int elementIndex = 0) => new(_p + elementIndex);
    public override void Unpin() { }
    protected override void Dispose(bool disposing) => NativeMemory.AlignedFree(_p);
}

/// <summary>
/// Benchmark no estilo CrystalDiskMark: arquivo de teste no volume, E/S sem cache do sistema,
/// várias requisições em voo (profundidade de fila) para medir o que o SSD aguenta de verdade.
/// </summary>
public static class DiskBenchmark
{
    public static readonly BenchTest[] Tests =
    [
        new("SEQ1M-Q8", "Sequencial 1 MiB · Q8", true, 1 << 20, 8, false),
        new("SEQ1M-Q8", "Sequencial 1 MiB · Q8", true, 1 << 20, 8, true),
        new("SEQ1M-Q1", "Sequencial 1 MiB · Q1", true, 1 << 20, 1, false),
        new("SEQ1M-Q1", "Sequencial 1 MiB · Q1", true, 1 << 20, 1, true),
        new("RND4K-Q32", "Aleatório 4 KiB · Q32", false, 4096, 32, false),
        new("RND4K-Q32", "Aleatório 4 KiB · Q32", false, 4096, 32, true),
        new("RND4K-Q1", "Aleatório 4 KiB · Q1", false, 4096, 1, false),
        new("RND4K-Q1", "Aleatório 4 KiB · Q1", false, 4096, 1, true),
    ];

    private const FileOptions NoBuffering = (FileOptions)0x20000000;

    public static async Task<List<BenchResult>> RunAsync(string folder, long fileSize, TimeSpan perTest, bool includeWrite,
                                                         IProgress<BenchProgress>? progress, CancellationToken ct)
    {
        string path = Path.Combine(folder, $"LibertyX-bench-{Guid.NewGuid():N}.tmp");
        var results = new List<BenchResult>();
        var opts = FileOptions.Asynchronous | FileOptions.WriteThrough | (OperatingSystem.IsWindows() ? NoBuffering : 0);
        try
        {
            progress?.Report(new("Preparando arquivo de teste", 0, null));
            // sem pré-alocar: no FAT32/exFAT de cartões o Windows zeraria o arquivo inteiro antes de
            // devolver o controle (um minuto ou mais parado); gravar em sequência já estende o arquivo
            using (var h = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, opts))
            using (var b = new AlignedBuffer(8 << 20))
            {
                // dados aleatórios: SSD com compressão não pode trapacear com zeros
                RandomNumberGenerator.Fill(b.GetSpan());
                for (long p = 0; p < fileSize; p += 8 << 20)
                {
                    ct.ThrowIfCancellationRequested();
                    await RandomAccess.WriteAsync(h, b.Memory[..(int)Math.Min(8 << 20, fileSize - p)], p, ct);
                    progress?.Report(new("Preparando arquivo de teste", (double)p / fileSize * 0.1, null));
                }
            }
            var tests = Tests.Where(t => includeWrite || !t.Write).ToList();
            for (int i = 0; i < tests.Count; i++)
            {
                var t = tests[i];
                progress?.Report(new(t.Label + (t.Write ? " — escrita" : " — leitura"), 0.1 + 0.9 * i / tests.Count, null));
                using var h = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, opts);
                var r = await RunOne(h, fileSize, t, perTest, ct);
                results.Add(r);
                progress?.Report(new(t.Label, 0.1 + 0.9 * (i + 1) / tests.Count, r));
                await Task.Delay(500, ct); // deixa o SSD respirar entre testes, como os outros benchmarks
            }
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
        return results;
    }

    private static async Task<BenchResult> RunOne(SafeFileHandle h, long fileSize, BenchTest t, TimeSpan duration, CancellationToken ct)
    {
        long ops = 0, bytes = 0;
        double latencySum = 0;
        long blocks = fileSize / t.BlockSize;
        long seqNext = 0;
        var sw = Stopwatch.StartNew();
        async Task Worker(int id)
        {
            using var buf = new AlignedBuffer(t.BlockSize);
            if (t.Write) RandomNumberGenerator.Fill(buf.GetSpan());
            var rnd = new Random(id * 7919 + 1);
            var lat = new Stopwatch();
            while (sw.Elapsed < duration && !ct.IsCancellationRequested)
            {
                long blk = t.Sequential ? Interlocked.Increment(ref seqNext) % blocks : rnd.NextInt64(blocks);
                long ofs = blk * t.BlockSize;
                lat.Restart();
                if (t.Write) await RandomAccess.WriteAsync(h, buf.Memory, ofs, ct);
                else await RandomAccess.ReadAsync(h, buf.Memory, ofs, ct);
                double us = lat.Elapsed.TotalMicroseconds;
                Interlocked.Increment(ref ops);
                Interlocked.Add(ref bytes, t.BlockSize);
                lock (t) latencySum += us;
            }
        }
        await Task.WhenAll(Enumerable.Range(0, t.QueueDepth).Select(i => Task.Run(() => Worker(i), ct)));
        double secs = sw.Elapsed.TotalSeconds;
        return new BenchResult(t, bytes / 1e6 / secs, ops / secs, ops > 0 ? latencySum / ops : 0);
    }

    /// <summary>
    /// Curva de leitura ao longo do disco (só leitura, direto no dispositivo): mostra zonas lentas,
    /// típicas de HD com setores fracos ou de SSD com células degradadas.
    /// </summary>
    public static List<(double position, double mbps, double accessMs)> ReadCurve(IBlockDevice dev, int points, IProgress<double>? progress, CancellationToken ct)
    {
        var res = new List<(double, double, double)>();
        const int chunk = 8 << 20;
        var buf = new byte[chunk];
        long span = dev.Length - chunk;
        var rnd = new Random(42);
        for (int i = 0; i < points; i++)
        {
            ct.ThrowIfCancellationRequested();
            long pos = span <= 0 ? 0 : (long)(span * (i / (double)(points - 1))) / dev.SectorSize * dev.SectorSize;
            var sw = Stopwatch.StartNew();
            try { dev.Read(pos, buf.AsSpan(0, 4096)); } catch (MediaReadException) { }
            double access = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            try { dev.Read(pos, buf); } catch (MediaReadException) { res.Add((i / (double)(points - 1), 0, access)); continue; }
            res.Add((i / (double)(points - 1), chunk / 1e6 / sw.Elapsed.TotalSeconds, access));
            progress?.Report((i + 1) / (double)points);
        }
        _ = rnd;
        return res;
    }
}
