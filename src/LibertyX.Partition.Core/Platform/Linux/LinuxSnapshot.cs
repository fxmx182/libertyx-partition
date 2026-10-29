using System.Globalization;
using System.Runtime.Versioning;
using LibertyX.Partition.Core.IO;
using LibertyX.Partition.Core.Operations;

namespace LibertyX.Partition.Core.Platform.Linux;

/// <summary>
/// O "instantâneo VSS" do Linux: copiar um volume montado sem pegá-lo no meio de uma gravação.
/// Volume lógico LVM → instantâneo do LVM (o kernel congela o sistema de arquivos por um instante
/// e a cópia sai consistente com tudo rodando). Partição comum fora do sistema → remontada só
/// leitura durante a cópia. Raiz e /boot sem LVM → cópia com o sistema rodando, avisando.
/// </summary>
[SupportedOSPlatform("linux")]
public static class LinuxSnapshot
{
    public static async Task<(IBlockDevice dev, Func<Task> release)?> Open(PartitionInfo p, OpSink sink)
    {
        if (!p.IsMounted || p.DeviceNode == null) return null;
        if (p.Disk.VirtualKind == "LVM" && await Lvm(p, sink) is { } snap) return snap;
        if (p.IsSystemCritical || p.IsSystemVolume || p.HeldBy != null)
        {
            sink.Log(L.T($"{p.DisplayName} está em uso pelo sistema e não fica num volume LVM: a cópia é feita com ele rodando. ") +
                     L.T("O que estiver sendo gravado agora pode sair pela metade — o Linux confere o diário (journal) quando a cópia for montada."));
            return null;
        }
        return await ReadOnly(p, sink);
    }

    private static async Task<(IBlockDevice, Func<Task>)?> Lvm(PartitionInfo p, OpSink sink)
    {
        if (!LinuxTools.Has("lvm")) return null;
        // o lvs não aceita /dev/dm-N: o nome do mapeamento (vg-lv) em /dev/mapper aceita
        string lvDev = p.DeviceNode!;
        string dm = Path.GetFileName(Path.GetFullPath(lvDev));
        try { if (File.ReadAllText($"/sys/class/block/{dm}/dm/name").Trim() is { Length: > 0 } n) lvDev = "/dev/mapper/" + n; } catch { }
        var info = await Proc.Run("lvs", ["--noheadings", "--separator", "|", "--units", "b", "--nosuffix", "-o", "vg_name,lv_name,vg_free,lv_size", lvDev], sink.Token);
        if (!info.Ok) sink.Log(L.T("O LVM não reconheceu o volume para o instantâneo: ") + info.Message);
        var f = info.Output.Trim().Split('|');
        if (!info.Ok || f.Length < 4 || !long.TryParse(f[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long free)
            || !long.TryParse(f[3].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long lvSize))
            return null;
        string vg = f[0].Trim(), lv = f[1].Trim();
        // o instantâneo guarda só o que mudar durante a cópia: 10% do volume, no mínimo 1 GiB, até o livre do grupo
        long size = Math.Min(free / (4 << 20) * (4 << 20), Math.Max(1L << 30, lvSize / 10 / (4 << 20) * (4 << 20)));
        if (size < 256L << 20)
        {
            sink.Log(L.T($"O grupo {vg} não tem espaço livre para um instantâneo LVM (precisa de pelo menos 256 MB)."));
            return null;
        }
        string name = $"lx-snap-{Guid.NewGuid().ToString("N")[..8]}";
        sink.Log(L.T($"Criando instantâneo LVM de {vg}/{lv} ({size >> 20} MB para as mudanças durante a cópia)…"));
        var r = await Proc.Run("lvcreate", ["-s", "-n", name, "-L", size + "b", $"{vg}/{lv}"], sink.Token);
        if (!r.Ok) { sink.Log(L.T("O LVM não criou o instantâneo: ") + r.Message); return null; }
        KernelSync.Settle();
        string path = $"/dev/{vg}/{name}";
        try
        {
            var dev = new LinuxDevice(path, false, p.Disk.SectorSize, p.Length);
            sink.Log(L.T("Instantâneo pronto: a cópia sai consistente mesmo com o sistema em uso."));
            return (dev, async () =>
            {
                dev.Dispose();
                var rm = await Proc.Run("lvremove", ["-f", $"{vg}/{name}"], CancellationToken.None);
                if (!rm.Ok) sink.Log(L.T($"Não deu para apagar o instantâneo {vg}/{name}: apague com  lvremove {vg}/{name}"));
            });
        }
        catch
        {
            await Proc.Run("lvremove", ["-f", $"{vg}/{name}"], CancellationToken.None);
            throw;
        }
    }

    private static async Task<(IBlockDevice, Func<Task>)?> ReadOnly(PartitionInfo p, OpSink sink)
    {
        var mounts = LinuxDiskSystem.ReadMounts().Where(m => p.MountPoints.Contains(m.MountPoint)).ToList();
        var main = mounts.FirstOrDefault();
        if (main == null) return null;
        if (main.Options.Split(',').Contains("ro"))
        {
            sink.Log(L.T($"{p.DisplayName} já está montada só para leitura: a cópia sai consistente."));
            return null;
        }
        var r = await Proc.Run("mount", ["-o", "remount,ro", main.MountPoint], sink.Token);
        if (!r.Ok)
        {
            sink.Log(L.T($"Não deu para deixar {main.MountPoint} só leitura durante a cópia (algum programa grava nela agora): copiando assim mesmo. {r.Message}"));
            return null;
        }
        sink.Log(L.T($"{main.MountPoint} ficou só leitura durante a cópia (volta ao normal no fim): a cópia sai consistente."));
        var dev = DiskSystem.OpenPartition(p);
        return (dev, async () =>
        {
            dev.Dispose();
            var back = await Proc.Run("mount", ["-o", "remount,rw", main.MountPoint], CancellationToken.None);
            if (!back.Ok) sink.Log(L.T($"{main.MountPoint} continua só leitura: rode  mount -o remount,rw {main.MountPoint}"));
        });
    }
}
