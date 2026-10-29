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
        // idioma da interface: o do sistema que a instância sem root viu vem como argumento (o pkexec
        // não repassa LANG); o autoteste confere textos e roda sempre em português
        string? idioma = args.FirstOrDefault(a => a.StartsWith("--idioma="))?["--idioma=".Length..];
        args = args.Where(a => !a.StartsWith("--idioma=")).ToArray();
        L.Init(idioma, args.Contains("--selftest") ? "pt" : null);
        int st = Array.IndexOf(args, "--selftest");
        if (st >= 0)
        {
            string outPath = st + 1 < args.Length ? args[st + 1] : "autoteste.txt";
            int rp = Array.IndexOf(args, "--reparar");
            if (rp >= 0 && rp + 1 < args.Length) Environment.Exit(SelfTest.RunRepair(outPath, args[rp + 1]));
            int mg = Array.IndexOf(args, "--migrar");
            if (mg >= 0 && mg + 1 < args.Length) Environment.Exit(SelfTest.RunMigrate(outPath, args[mg + 1]));
            Environment.Exit(SelfTest.Run(outPath, args.Contains("--vhd") || args.Contains("--loop")));
            return;
        }
        // diagnóstico do tamanho da interface: o que a área de trabalho e os monitores dizem
        if (OperatingSystem.IsLinux() && args.Contains("--escala-info"))
        {
            Console.WriteLine(LibertyX.Partition.Core.Platform.Linux.LinuxDisplay.Report());
            return;
        }
        // Linux: a escala da interface (monitor 4K, "tamanho do texto" do desktop, escolha no app)
        // precisa estar decidida antes de o Avalonia abrir — e vai junto quando o app reabre como root
        string? escala = args.FirstOrDefault(a => a.StartsWith("--escala="))?["--escala=".Length..];
        args = args.Where(a => !a.StartsWith("--escala=")).ToArray();
        if (OperatingSystem.IsLinux()) LibertyX.Partition.Core.Platform.Linux.LinuxDisplay.Apply(escala);
        // Linux: discos só com root. Pede a senha logo na abertura (como o UAC do Windows faz pelo
        // manifesto); se a pessoa cancelar, o app abre mesmo assim, com imagens e o botão para tentar de novo.
        if (OperatingSystem.IsLinux() && !args.Contains("--sem-root") && Environment.GetEnvironmentVariable("LIBERTYX_NO_ELEVATE") != "1"
            && !LibertyX.Partition.Core.Platform.Linux.LinuxSession.IsRoot && !LibertyX.Partition.Core.Platform.Linux.LinuxSession.ElevatedByUs
            && LibertyX.Partition.Core.Platform.Linux.LinuxSession.RelaunchAsRoot(args))
            return;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args.Where(a => a != "--sem-root").ToArray());
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var b = AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
        // Linux como root (pkexec): sem acesso ao D-Bus da sessão, o seletor de arquivos do sistema
        // (portal) não abre — usa o do próprio Avalonia, com o tema do app
        if (OperatingSystem.IsLinux() && LibertyX.Partition.Core.Platform.Linux.LinuxSession.IsRoot)
            b = Avalonia.Dialogs.ManagedFileDialogExtensions.UseManagedSystemDialogs(b);
        return b;
    }
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
