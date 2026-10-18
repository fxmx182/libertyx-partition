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
    /// Unidades prontas, cada uma consultada com limite de tempo: "está pronta?", rótulo e espaço
    /// livre leem a mídia, e um cartão com defeito trava essas chamadas. Quem não responde fica de fora.
    /// </summary>
    public static List<DriveEntry> ReadyDrives(TimeSpan perDrive, params DriveType[] types)
    {
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
