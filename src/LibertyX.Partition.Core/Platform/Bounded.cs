namespace LibertyX.Partition.Core.Platform;

/// <summary>
/// Limite de tempo para chamadas ao Windows que podem ficar presas para sempre: um cartão SD com
/// defeito ou formatado pela metade faz "informações do volume" e leituras travarem dentro do
/// driver. Cada chamada roda numa thread própria; se passar do limite, a tela segue sem ela
/// (a thread presa é abandonada) e o disco aparece como "não respondeu".
/// </summary>
public static class Bounded
{
    public static T Run<T>(Func<T> work, TimeSpan limit, T fallback, out bool timedOut)
    {
        var t = Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            if (t.Wait(limit)) { timedOut = false; return t.Result; }
        }
        catch (AggregateException ex) when (ex.InnerException != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); }
        timedOut = true;
        _ = t.ContinueWith(x => _ = x.Exception, TaskScheduler.Default);
        return fallback;
    }

    public sealed record DriveEntry(string Root, string Label, long Free, DriveType Type);

    /// <summary>
    /// Linux: só o que vem de um disco de verdade (/dev/…), sem /proc, /sys, tmpfs, overlay de
    /// contêiner nem as imagens squashfs do snap; um volume montado em dois lugares aparece uma vez.
    /// </summary>
    private static List<DriveEntry> LinuxDrives(TimeSpan perDrive, DriveType[] types)
    {
        var list = new List<DriveEntry>();
        var seen = new HashSet<string>();
        foreach (var m in Linux.LinuxDiskSystem.ReadMounts().Where(m => m.Source.StartsWith("/dev/") && m.FsType is not ("squashfs" or "iso9660" or "udf")).OrderBy(m => m.MountPoint.Length))
        {
            if (!seen.Add(m.Source) || m.MountPoint.StartsWith("/snap/") || m.MountPoint.StartsWith("/var/lib/docker") || m.MountPoint.StartsWith("/boot")) continue;
            string kn = Linux.LinuxDiskSystem.KernelNameOf(m.Source) ?? "";
            string disk = Directory.Exists($"/sys/class/block/{kn}/partition") || File.Exists($"/sys/class/block/{kn}/partition") ? Path.GetFileName(Path.GetDirectoryName(new DirectoryInfo($"/sys/class/block/{kn}").ResolveLinkTarget(true)?.FullName ?? "") ?? "") : kn;
            bool removable = File.Exists($"/sys/block/{disk}/removable") && File.ReadAllText($"/sys/block/{disk}/removable").Trim() == "1";
            var t = removable ? DriveType.Removable : DriveType.Fixed;
            if (types.Length > 0 && !types.Contains(t)) continue;
            var e = Run(() =>
            {
                try { var di = new DriveInfo(m.MountPoint); return new DriveEntry(m.MountPoint, m.MountPoint == "/" ? "sistema" : "", di.AvailableFreeSpace, t); }
                catch { return null; }
            }, perDrive, null, out _);
            if (e != null) list.Add(e);
        }
        return list;
    }

    /// <summary>
    /// Unidades prontas, cada uma consultada com limite de tempo: "está pronta?", rótulo e espaço
    /// livre leem a mídia, e um cartão com defeito trava essas chamadas. Quem não responde fica de fora.
    /// </summary>
    public static List<DriveEntry> ReadyDrives(TimeSpan perDrive, params DriveType[] types)
    {
        if (OperatingSystem.IsLinux()) return LinuxDrives(perDrive, types);
        var list = new List<DriveEntry>();
        DriveInfo[] all;
        try { all = DriveInfo.GetDrives(); } catch { return list; }
        foreach (var d in all)
        {
            DriveType t;
            try { t = d.DriveType; } catch { continue; }
            if (types.Length > 0 && !types.Contains(t)) continue;
            var e = Run(() =>
            {
                try { return d.IsReady ? new DriveEntry(d.RootDirectory.FullName, d.VolumeLabel, d.AvailableFreeSpace, t) : null; }
                catch { return null; }
            }, perDrive, null, out _);
            if (e != null) list.Add(e);
        }
        return list;
    }
}
