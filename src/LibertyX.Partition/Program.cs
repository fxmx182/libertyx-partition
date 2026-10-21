using Avalonia;

namespace LibertyX.Partition;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { CrashLog.Write(e.Exception); e.SetObserved(); };
        // reaberto pelo pkexec com a política do pacote: o aviso de "janela aberta" vem como argumento
        if (args.FirstOrDefault(a => a.StartsWith("--pronto=")) is { } pr)
        {
            Environment.SetEnvironmentVariable("LIBERTYX_READY", pr["--pronto=".Length..]);
            args = args.Where(a => a != pr).ToArray();
        }
        int st = Array.IndexOf(args, "--selftest");
        if (st >= 0)
        {
            string outPath = st + 1 < args.Length ? args[st + 1] : "autoteste.txt";
            Environment.Exit(SelfTest.Run(outPath, args.Contains("--vhd") || args.Contains("--loop")));
            return;
        }
        // Linux: discos só com root. Pede a senha logo na abertura (como o UAC do Windows faz pelo
        // manifesto); se a pessoa cancelar, o app abre mesmo assim, com imagens e o botão para tentar de novo.
        if (OperatingSystem.IsLinux() && !args.Contains("--sem-root") && Environment.GetEnvironmentVariable("LIBERTYX_NO_ELEVATE") != "1"
            && !LibertyX.Partition.Core.Platform.Linux.LinuxSession.IsRoot && !LibertyX.Partition.Core.Platform.Linux.LinuxSession.ElevatedByUs
            && LibertyX.Partition.Core.Platform.Linux.LinuxSession.RelaunchAsRoot(args))
            return;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args.Where(a => a != "--sem-root").ToArray());
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}

public static class CrashLog
{
    public static string Folder => LibertyX.Partition.Core.Platform.AppPaths.Documents;

    public static void Write(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            LibertyX.Partition.Core.Platform.AppPaths.EnsureDocuments();
            File.AppendAllText(Path.Combine(Folder, "erros.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\r\n\r\n");
        }
        catch { }
    }
}
