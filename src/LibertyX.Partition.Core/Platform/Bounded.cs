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
}
