using System.Diagnostics;
using System.Security.Cryptography;
using LibertyX.Partition.Core.IO;

namespace LibertyX.Partition.Core.Tools;

public enum WipeMethod { Zeros, Random, DoD3Pass, Gutmann7, Discard, Sanitize }

public readonly record struct WipeProgress(int Pass, int Passes, long Done, long Total, double BytesPerSecond);

/// <summary>Sobrescreve uma faixa do dispositivo. Depois disto, nenhuma recuperação traz nada de volta.</summary>
public static class Wiper
{
    public static string Describe(WipeMethod m) => m switch
    {
        WipeMethod.Zeros => "Preencher com zeros (1 passada) — rápido; suficiente para SSD e para reaproveitar o disco",
        WipeMethod.Random => "Dados aleatórios (1 passada)",
        WipeMethod.DoD3Pass => "DoD 5220.22-M (3 passadas: zeros, uns, aleatório)",
        WipeMethod.Gutmann7 => "Reforçado (7 passadas alternadas) — lento; para discos mecânicos antigos",
        WipeMethod.Discard => "SSD: descartar todos os blocos (TRIM) — segundos, e o SSD volta ao desempenho de novo",
        WipeMethod.Sanitize => "SSD: apagamento seguro do fabricante (NVMe Format / ATA Secure Erase) — inclusive as células de reserva",
        _ => "",
    };

    public static int Passes(WipeMethod m) => m switch { WipeMethod.DoD3Pass => 3, WipeMethod.Gutmann7 => 7, _ => 1 };

    public static void Wipe(IBlockDevice dev, long start, long length, WipeMethod method, IProgress<WipeProgress>? progress, CancellationToken ct)
    {
        int passes = Passes(method);
        const int block = 4 << 20;
        var buf = new byte[block];
        var sw = Stopwatch.StartNew();
        long overall = 0;
        for (int pass = 0; pass < passes; pass++)
        {
            byte? fill = method switch
            {
                WipeMethod.Zeros => 0,
                WipeMethod.Random => null,
                WipeMethod.DoD3Pass => pass == 0 ? (byte)0 : pass == 1 ? (byte)0xFF : null,
                _ => pass % 3 == 0 ? (byte)0x00 : pass % 3 == 1 ? (byte)0xFF : null,
            };
            if (fill is { } f) buf.AsSpan().Fill(f);
            long done = 0;
            for (long p = start; p < start + length; p += block)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(block, start + length - p);
                if (fill == null) RandomNumberGenerator.Fill(buf.AsSpan(0, n));
                dev.Write(p, buf.AsSpan(0, n));
                done += n; overall += n;
                if ((done & ((64 << 20) - 1)) == 0 || done == length)
                    progress?.Report(new(pass + 1, passes, done, length, overall / Math.Max(0.001, sw.Elapsed.TotalSeconds)));
            }
            dev.Flush();
        }
    }
}
