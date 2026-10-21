using LibertyX.Partition.Core.Platform.Linux;

namespace LibertyX.Partition.Core.Platform;

/// <summary>Onde o app guarda o que é da pessoa (backups de tabela, log de erros) e o que é do computador (licença).</summary>
public static class AppPaths
{
    /// <summary>Documentos/LibertyX Partition — no Linux, os Documentos de quem abriu o app, não os do root.</summary>
    public static string Documents
    {
        get
        {
            if (!OperatingSystem.IsLinux()) return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LibertyX Partition");
            return Path.Combine(LinuxSession.Documents, "LibertyX Partition");
        }
    }

    /// <summary>Cria a pasta dentro dos Documentos (no Linux, já com o dono certo).</summary>
    public static string EnsureDocuments(params string[] sub)
    {
        string dir = Path.Combine([Documents, .. sub]);
        if (OperatingSystem.IsLinux()) return LinuxSession.EnsureUserDir(dir);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Dados do computador, para todos os usuários (o app roda como administrador).</summary>
    public static string MachineData =>
        OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LibertyX Partition")
        : OperatingSystem.IsLinux() && (Linux.LinuxNative.GetEuid() == 0 || Directory.Exists("/var/lib/libertyx-partition")) ? "/var/lib/libertyx-partition"
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "libertyx-partition");
}
