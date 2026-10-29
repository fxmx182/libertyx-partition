using System.Runtime.Versioning;
using LibertyX.Partition.Core.IO;
using LibertyX.Partition.Core.Partitioning;
using static LibertyX.Partition.Core.Platform.Linux.LinuxNative;

namespace LibertyX.Partition.Core.Platform.Linux;

/// <summary>
/// Depois de gravar a tabela, o kernel precisa saber. BLKRRPART relê tudo, mas recusa (EBUSY)
/// se qualquer partição do disco estiver em uso — o caso do disco do sistema. Aí vai partição
/// por partição pelo BLKPG, como o partx e o GParted: apaga as que sumiram, cria as novas e
/// ajusta o tamanho das que mudaram, sem tocar nas que estão montadas.
/// </summary>
[SupportedOSPlatform("linux")]
public static unsafe class KernelSync
{
    /// <summary>sda → sda1; nvme0n1 → nvme0n1p1; mmcblk0 → mmcblk0p1; loop0 → loop0p1.</summary>
    public static string PartName(string disk, int n) => char.IsDigit(disk[^1]) ? $"{disk}p{n}" : $"{disk}{n}";

    public static void Reread(DiskInfo d)
    {
        string name = d.KernelName.Length > 0 ? d.KernelName : Path.GetFileName(d.DevicePath);
        int fd = Open(d.DevicePath, O_RDONLY | O_CLOEXEC | O_NONBLOCK);
        if (fd < 0) return;
        try
        {
            IoctlInt(fd, BLKFLSBUF, 0);
            // o udev e o blkid abrem as partições por um instante depois de cada mudança: insiste um pouco
            bool ok = false;
            for (int i = 0; i < 6 && !ok; i++)
            {
                ok = IoctlInt(fd, BLKRRPART, 0) == 0;
                if (!ok && Errno != EBUSY) break;
                if (!ok) Thread.Sleep(400);
            }
            for (int i = 0; i < 5 && !ok; i++)
            {
                ok = Incremental(fd, d, name);
                if (!ok) Thread.Sleep(600);
            }
        }
        finally { Close(fd); }
        Settle();
        // o cache de cada partição pode guardar o que havia antes da escrita direta no disco
        foreach (var pd in Directory.GetDirectories($"/sys/block/{name}").Where(p => File.Exists(Path.Combine(p, "partition"))))
        {
            int pf = Open(DevNodes.Node(Path.GetFileName(pd)), O_RDONLY | O_CLOEXEC | O_NONBLOCK);
            if (pf >= 0) { IoctlInt(pf, BLKFLSBUF, 0); Close(pf); }
        }
    }

    /// <summary>Devolve verdadeiro quando o kernel ficou igual à tabela (ou só restam diferenças em partições em uso).</summary>
    private static bool Incremental(int fd, DiskInfo d, string name)
    {
        PartitionTable t;
        using (var dev = new LinuxDevice(d.DevicePath, false, d.SectorSize, d.Size)) t = PartitionCodec.Read(dev);
        int ss = t.SectorSize;
        var want = t.Entries.Where(e => e.Slot > 0).ToDictionary(e => e.Slot, e => (start: e.StartLba * ss, len: e.SectorCount * ss));
        var have = new Dictionary<int, (long start, long len)>();
        foreach (var pd in Directory.GetDirectories($"/sys/block/{name}").Where(p => File.Exists(Path.Combine(p, "partition"))))
        {
            if (!int.TryParse(Read(Path.Combine(pd, "partition")), out int pno)) continue;
            long.TryParse(Read(Path.Combine(pd, "start")), out long st);
            long.TryParse(Read(Path.Combine(pd, "size")), out long sz);
            have[pno] = (st * 512, sz * 512);
        }
        // a estendida do MBR aparece no kernel com 1 KiB: não é nossa, fica como está
        bool IsExtendedStub(int pno) => t.Style == TableStyle.Mbr && pno <= 4 && have[pno].len <= 1024 && !want.ContainsKey(pno);
        foreach (var (pno, h) in have)
        {
            if (IsExtendedStub(pno)) continue;
            if (want.TryGetValue(pno, out var w) && w.start == h.start)
            {
                if (w.len != h.len) Blkpg(fd, BLKPG_RESIZE_PARTITION, pno, w.start, w.len, out _);
                continue;
            }
            Blkpg(fd, BLKPG_DEL_PARTITION, pno, 0, 0, out _);   // em uso → EBUSY, e fica como estava
        }
        have.Clear();
        foreach (var pd in Directory.GetDirectories($"/sys/block/{name}").Where(p => File.Exists(Path.Combine(p, "partition"))))
            if (int.TryParse(Read(Path.Combine(pd, "partition")), out int pno))
            {
                long.TryParse(Read(Path.Combine(pd, "start")), out long st);
                long.TryParse(Read(Path.Combine(pd, "size")), out long sz);
                have[pno] = (st * 512, sz * 512);
            }
        bool all = true;
        foreach (var (pno, w) in want)
        {
            if (have.TryGetValue(pno, out var h)) { if (h.start != w.start || h.len != w.len) all = false; continue; }
            if (!Blkpg(fd, BLKPG_ADD_PARTITION, pno, w.start, w.len, out _)) all = false;
        }
        // sobrou no kernel alguma que não está mais na tabela?
        if (have.Keys.Any(pno => !want.ContainsKey(pno) && !(t.Style == TableStyle.Mbr && pno <= 4 && have[pno].len <= 1024))) all = false;
        return all;
    }

    private static string Read(string p) { try { return File.ReadAllText(p).Trim(); } catch { return ""; } }

    /// <summary>Espera o udev terminar de criar/remover os /dev e atualizar rótulos e UUIDs.</summary>
    public static void Settle()
    {
        if (LinuxTools.Has("udevadm")) Proc.RunSync("udevadm", ["settle", "--timeout=15"], TimeSpan.FromSeconds(20));
        else Thread.Sleep(1000);
    }

    /// <summary>O /dev da partição que começa em <paramref name="offset"/> (espera o kernel criar, até ~10 s).</summary>
    public static string? WaitPartitionNode(DiskInfo d, long offset, int timeoutMs = 10000)
    {
        string name = d.KernelName.Length > 0 ? d.KernelName : Path.GetFileName(d.DevicePath);
        var end = DateTime.Now.AddMilliseconds(timeoutMs);
        do
        {
            foreach (var pd in Directory.GetDirectories($"/sys/block/{name}").Where(p => File.Exists(Path.Combine(p, "partition"))))
                if (long.TryParse(Read(Path.Combine(pd, "start")), out long st) && st * 512 == offset)
                {
                    string node = DevNodes.Node(Path.GetFileName(pd));
                    if (File.Exists(node)) return node;
                }
            Thread.Sleep(250);
        } while (DateTime.Now < end);
        return null;
    }
}

/// <summary>
/// Enquanto o LibertyX mexe num disco, o ambiente gráfico não pode montar sozinho a partição
/// recém-criada (o udisks faz isso com pendrives) — senão o mkfs seguinte acha o volume em uso.
/// Mesma técnica do udisks2-inhibit usado pelo GParted: uma regra do udev em /run.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class AutomountInhibitor : IDisposable
{
    private const string Rule = "/run/udev/rules.d/90-libertyx-inhibit.rules";
    private static int _refs;
    private static readonly object Gate = new();

    public static IDisposable? Start()
    {
        lock (Gate)
        {
            try
            {
                if (_refs++ == 0)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Rule)!);
                    File.WriteAllText(Rule, "# LibertyX Partition: operação em andamento — sem montagem automática\nSUBSYSTEM==\"block\", ENV{UDISKS_IGNORE}=\"1\"\n");
                    if (LinuxTools.Has("udevadm")) Proc.RunSync("udevadm", ["control", "--reload"], TimeSpan.FromSeconds(10));
                }
                return new AutomountInhibitor();
            }
            catch { _refs--; return null; }
        }
    }

    private bool _done;
    public void Dispose()
    {
        lock (Gate)
        {
            if (_done) return;
            _done = true;
            if (--_refs > 0) return;
            try { File.Delete(Rule); } catch { }
            if (LinuxTools.Has("udevadm")) Proc.RunSync("udevadm", ["control", "--reload"], TimeSpan.FromSeconds(10));
        }
    }
}
