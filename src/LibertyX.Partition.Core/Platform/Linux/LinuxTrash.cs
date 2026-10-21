using System.Runtime.Versioning;
using System.Text;

namespace LibertyX.Partition.Core.Platform.Linux;

/// <summary>
/// Lixeira do Linux no padrão freedesktop.org — a mesma que o Nautilus, Dolphin e Thunar usam:
/// arquivo vai para Trash/files, e um .trashinfo em Trash/info guarda de onde veio (para o
/// "Restaurar" do gerenciador de arquivos). Na pasta pessoal, ~/.local/share/Trash; em outro
/// disco, a lixeira dele ($topdir/.Trash-UID), para não copiar gigabytes entre discos.
/// </summary>
[SupportedOSPlatform("linux")]
public static class LinuxTrash
{
    public static bool Recycle(IEnumerable<string> paths)
    {
        bool all = true;
        foreach (var p in paths)
            try { Move(p); } catch { all = false; }
        return all;
    }

    private static string MountOf(string path)
    {
        string full = Path.GetFullPath(path);
        return LinuxDiskSystem.ReadMounts().Select(m => m.MountPoint)
            .Where(m => full == m || full.StartsWith(m.TrimEnd('/') + "/"))
            .OrderByDescending(m => m.Length).FirstOrDefault() ?? "/";
    }

    public static void Move(string path)
    {
        var user = LinuxSession.InvokingUser;
        int uid = user?.Uid ?? (int)LinuxNative.GetEuid();
        string home = LinuxSession.Home;
        string full = Path.GetFullPath(path);
        string top = MountOf(full);
        bool inHome = MountOf(home) == top;
        string trash = inHome ? Path.Combine(home, ".local", "share", "Trash") : Path.Combine(top, $".Trash-{uid}");
        string files = Path.Combine(trash, "files"), info = Path.Combine(trash, "info");
        LinuxSession.EnsureUserDir(files);
        LinuxSession.EnsureUserDir(info);
        if (!inHome) try { File.SetUnixFileMode(trash, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch { }
        string name = Path.GetFileName(full.TrimEnd('/'));
        string dest = Path.Combine(files, name);
        for (int i = 2; File.Exists(dest) || Directory.Exists(dest) || File.Exists(Path.Combine(info, Path.GetFileName(dest) + ".trashinfo")); i++)
            dest = Path.Combine(files, $"{Path.GetFileNameWithoutExtension(name)} {i}{Path.GetExtension(name)}");
        string infoFile = Path.Combine(info, Path.GetFileName(dest) + ".trashinfo");
        // caminho absoluto na lixeira de casa; relativo ao disco na lixeira do disco (como manda o padrão)
        string recorded = inHome ? full : Path.GetRelativePath(top, full);
        File.WriteAllText(infoFile, $"[Trash Info]\nPath={Escape(recorded)}\nDeletionDate={DateTime.Now:yyyy-MM-ddTHH:mm:ss}\n");
        LinuxSession.GiveBack(infoFile, false);
        try
        {
            if (Directory.Exists(full)) Directory.Move(full, dest); else File.Move(full, dest);
        }
        catch { File.Delete(infoFile); throw; }
    }

    private static string Escape(string p)
    {
        var sb = new StringBuilder();
        foreach (byte b in Encoding.UTF8.GetBytes(p))
            sb.Append(b is (>= (byte)'a' and <= (byte)'z') or (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'0' and <= (byte)'9') or (byte)'/' or (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~' ? ((char)b).ToString() : $"%{b:X2}");
        return sb.ToString();
    }
}
