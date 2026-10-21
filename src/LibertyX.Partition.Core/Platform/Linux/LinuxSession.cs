using System.Diagnostics;
using System.Runtime.Versioning;

namespace LibertyX.Partition.Core.Platform.Linux;

/// <summary>
/// Quem está usando o app. Para ler discos o LibertyX roda como root (pelo pkexec, que pede a
/// senha numa janela do sistema), mas a pessoa continua sendo ela: os arquivos recuperados
/// voltam com o dono certo, a pasta abre no gerenciador de arquivos dela e os documentos vão
/// para a pasta pessoal dela — não para /root.
/// </summary>
[SupportedOSPlatform("linux")]
public static class LinuxSession
{
    public sealed record User(int Uid, int Gid, string Name, string Home);

    private static User? _user;
    private static bool _looked;

    /// <summary>O usuário que abriu o app (antes do pkexec/sudo). Nulo se for o próprio root.</summary>
    public static User? InvokingUser
    {
        get
        {
            if (_looked) return _user;
            _looked = true;
            string? uidS = Environment.GetEnvironmentVariable("LIBERTYX_UID") ?? Environment.GetEnvironmentVariable("PKEXEC_UID") ?? Environment.GetEnvironmentVariable("SUDO_UID");
            int uid = int.TryParse(uidS, out var u) ? u : (int)LinuxNative.GetEuid();
            if (uid == 0) return _user = null;
            _user = Lookup(uid);
            return _user;
        }
    }

    public static User? Lookup(int uid)
    {
        try
        {
            foreach (var line in File.ReadAllLines("/etc/passwd"))
            {
                var f = line.Split(':');
                if (f.Length >= 7 && int.TryParse(f[2], out var id) && id == uid)
                    return new User(uid, int.TryParse(f[3], out var g) ? g : uid, f[0], f[5]);
            }
        }
        catch { }
        // usuário de diretório (LDAP/SSSD) não está no /etc/passwd: o getent sabe
        var r = Proc.RunSync("getent", ["passwd", uid.ToString()], TimeSpan.FromSeconds(5));
        var p = r.Output.Trim().Split(':');
        return p.Length >= 7 ? new User(uid, int.TryParse(p[3], out var gg) ? gg : uid, p[0], p[5]) : null;
    }

    /// <summary>Pasta pessoal de quem usa (não a do root).</summary>
    public static string Home => InvokingUser?.Home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>A pasta Documentos de verdade (xdg-user-dirs: "Documentos" em português, "Documents" em inglês…).</summary>
    public static string Documents
    {
        get
        {
            try
            {
                var cfg = Path.Combine(Home, ".config", "user-dirs.dirs");
                if (File.Exists(cfg))
                    foreach (var line in File.ReadAllLines(cfg))
                        if (line.StartsWith("XDG_DOCUMENTS_DIR="))
                        {
                            var v = line[18..].Trim('"').Replace("$HOME", Home);
                            if (Directory.Exists(v)) return v;
                        }
            }
            catch { }
            foreach (var n in new[] { "Documentos", "Documents" })
                if (Directory.Exists(Path.Combine(Home, n))) return Path.Combine(Home, n);
            return Home;
        }
    }

    /// <summary>Devolve ao usuário o que o app criou como root (arquivos recuperados, imagens, relatórios).</summary>
    public static void GiveBack(string path, bool recursive = true)
    {
        var u = InvokingUser;
        if (u == null || LinuxNative.GetEuid() != 0) return;
        try
        {
            LinuxNative.Chown(path, u.Uid, u.Gid);
            if (recursive && Directory.Exists(path))
                foreach (var e in Directory.EnumerateFileSystemEntries(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }))
                    LinuxNative.Chown(e, u.Uid, u.Gid);
        }
        catch { }
    }

    /// <summary>Cria a pasta (e as que faltarem até ela) já com o dono certo.</summary>
    public static string EnsureUserDir(string dir)
    {
        var missing = new Stack<string>();
        for (var d = dir; d.Length > 1 && !Directory.Exists(d); d = Path.GetDirectoryName(d) ?? "/") missing.Push(d);
        Directory.CreateDirectory(dir);
        foreach (var d in missing) GiveBack(d, false);
        return dir;
    }

    /// <summary>Abre pasta/arquivo/site como o usuário (o navegador e o gerenciador de arquivos não devem rodar como root).</summary>
    public static void OpenAsUser(string target)
    {
        try
        {
            var u = InvokingUser;
            if (u != null && LinuxNative.GetEuid() == 0)
            {
                var env = new List<string> { $"HOME={u.Home}", $"USER={u.Name}", $"LOGNAME={u.Name}" };
                foreach (var k in new[] { "DISPLAY", "WAYLAND_DISPLAY", "XAUTHORITY", "DBUS_SESSION_BUS_ADDRESS", "XDG_RUNTIME_DIR", "XDG_CURRENT_DESKTOP", "XDG_SESSION_TYPE" })
                    if (Environment.GetEnvironmentVariable(k) is { Length: > 0 } v) env.Add($"{k}={v}");
                if (!env.Any(e => e.StartsWith("XDG_RUNTIME_DIR="))) env.Add($"XDG_RUNTIME_DIR=/run/user/{u.Uid}");
                if (!env.Any(e => e.StartsWith("DBUS_SESSION_BUS_ADDRESS="))) env.Add($"DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/{u.Uid}/bus");
                var psi = new ProcessStartInfo(File.Exists("/usr/sbin/runuser") || File.Exists("/sbin/runuser") ? "runuser" : "su") { UseShellExecute = false };
                if (psi.FileName == "runuser") { foreach (var a in new[] { "-u", u.Name, "--", "env" }) psi.ArgumentList.Add(a); }
                else { foreach (var a in new[] { "-s", "/bin/sh", u.Name, "-c", "exec env \"$@\"", "sh" }) psi.ArgumentList.Add(a); }
                foreach (var e in env) psi.ArgumentList.Add(e);
                psi.ArgumentList.Add("xdg-open");
                psi.ArgumentList.Add(target);
                Process.Start(psi);
                return;
            }
            var p = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
            p.ArgumentList.Add(target);
            Process.Start(p);
        }
        catch { }
    }

    // ───────── pedir administrador ─────────

    /// <summary>
    /// Reabre o app como root pelo pkexec (janela de senha do próprio sistema). Leva junto o que a
    /// interface gráfica precisa (DISPLAY, Wayland, XAUTHORITY) e quem é o usuário de verdade.
    /// Devolve falso se não havia como (sem pkexec) ou se a pessoa cancelou.
    /// </summary>
    public static bool RelaunchAsRoot(string[] args)
    {
        if (LinuxNative.GetEuid() == 0) return false;
        args = LinuxDisplay.RelaunchArgs(args.Where(a => !a.StartsWith("--pronto="))).ToArray();
        string? pkexec = LinuxTools.Find("pkexec");
        if (pkexec == null) return false;
        string self = Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } ai ? ai : Environment.ProcessPath ?? "";
        if (self.Length == 0) return false;
        // no Wayland, um cliente X do root precisa de licença para desenhar na tela do usuário
        if (LinuxTools.Has("xhost") && Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 })
            Proc.RunSync("xhost", ["+SI:localuser:root"], TimeSpan.FromSeconds(5));
        // a instância elevada cria este arquivo quando a janela abre; até lá, a senha ainda está sendo pedida
        string ready = Path.Combine(Path.GetTempPath(), $"libertyx-pronto-{Environment.ProcessId}-{Guid.NewGuid():N}");
        var psi = new ProcessStartInfo(pkexec) { UseShellExecute = false };
        if (self == PackagedExe && File.Exists(PolicyFile))
        {
            // instalado por pacote: a política própria (texto em português na janela de senha, e o
            // polkit mantém DISPLAY/XAUTHORITY); o pkexec informa o usuário original em PKEXEC_UID
            psi.ArgumentList.Add(self);
            psi.ArgumentList.Add("--pronto=" + ready);
            foreach (var a in args) psi.ArgumentList.Add(a);
            return WaitElevated(psi, ready);
        }
        psi.ArgumentList.Add("env");
        foreach (var k in new[] { "DISPLAY", "WAYLAND_DISPLAY", "XAUTHORITY", "XDG_RUNTIME_DIR", "XDG_CURRENT_DESKTOP", "XDG_SESSION_TYPE", "LANG", "LANGUAGE", "LC_ALL", "GDK_SCALE", "QT_SCALE_FACTOR", "AVALONIA_SCREEN_SCALE_FACTORS", "APPIMAGE", "APPDIR" })
            if (Environment.GetEnvironmentVariable(k) is { Length: > 0 } v) psi.ArgumentList.Add($"{k}={v}");
        if (Environment.GetEnvironmentVariable("XAUTHORITY") is not { Length: > 0 } && File.Exists(Path.Combine(Home, ".Xauthority")))
            psi.ArgumentList.Add($"XAUTHORITY={Path.Combine(Home, ".Xauthority")}");
        psi.ArgumentList.Add($"LIBERTYX_UID={LinuxNative.GetEuid()}");
        psi.ArgumentList.Add("LIBERTYX_ELEVATED=1");
        psi.ArgumentList.Add($"LIBERTYX_READY={ready}");
        psi.ArgumentList.Add(self);
        foreach (var a in args) psi.ArgumentList.Add(a);
        return WaitElevated(psi, ready);
    }

    /// <summary>
    /// Reabre o app do mesmo jeito (root continua root, sem perguntar a senha de novo) — para valer
    /// uma escala nova da interface, que o Avalonia só lê na abertura.
    /// </summary>
    public static bool Restart(IEnumerable<string> args)
    {
        string self = Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } ai ? ai : Environment.ProcessPath ?? "";
        if (self.Length == 0) return false;
        var psi = new ProcessStartInfo(self) { UseShellExecute = false };
        foreach (var a in LinuxDisplay.RelaunchArgs(args.Where(a => !a.StartsWith("--pronto=")))) psi.ArgumentList.Add(a);
        if (!IsRoot && !psi.ArgumentList.Contains("--sem-root")) psi.ArgumentList.Add("--sem-root");
        psi.Environment.Remove("LIBERTYX_READY");
        try { return Process.Start(psi) != null; }
        catch { return false; }
    }

    public const string PackagedExe = "/opt/libertyx-partition/LibertyXPartition";
    public const string PolicyFile = "/usr/share/polkit-1/actions/io.github.fxmx182.libertyx-partition.policy";

    private static bool WaitElevated(ProcessStartInfo psi, string ready)
    {
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return false;
            while (true)
            {
                if (File.Exists(ready)) { try { File.Delete(ready); } catch { } return true; }
                // o pkexec terminou sem a janela abrir: senha cancelada (126), sem agente de autenticação (127) ou erro
                if (p.WaitForExit(250)) return File.Exists(ready);
            }
        }
        catch { return false; }
    }

    public static bool IsRoot => LinuxNative.GetEuid() == 0;

    /// <summary>Chegou aqui já elevado pelo próprio app (não precisa perguntar de novo).</summary>
    public static bool ElevatedByUs => Environment.GetEnvironmentVariable("LIBERTYX_ELEVATED") == "1";

    /// <summary>
    /// AppImage não entra no menu de aplicativos sozinho: o app cria o atalho e o ícone na pasta de
    /// quem usa (~/.local/share), apontando para onde o AppImage está agora — e atualiza se ele mudar de lugar.
    /// </summary>
    public static void IntegrateAppImage(byte[] icon256)
    {
        try
        {
            string? appimage = Environment.GetEnvironmentVariable("APPIMAGE");
            if (string.IsNullOrEmpty(appimage) || !File.Exists(appimage)) return;
            string share = Path.Combine(Home, ".local", "share");
            string desktop = Path.Combine(share, "applications", "libertyx-partition-appimage.desktop");
            string iconPath = Path.Combine(share, "icons", "hicolor", "256x256", "apps", "libertyx-partition.png");
            string content = $"""
                [Desktop Entry]
                Type=Application
                Name=LibertyX Partition
                GenericName=Gerenciador de partições e recuperação de dados
                Comment=Recuperar arquivos e partições, clonar, redimensionar e formatar discos
                Exec="{appimage}" %F
                Icon=libertyx-partition
                Terminal=false
                Categories=System;Filesystem;Utility;
                Keywords=partition;disk;recovery;undelete;partição;disco;recuperar;
                StartupWMClass=LibertyXPartition
                X-AppImage-Path={appimage}

                """;
            if (File.Exists(desktop) && File.ReadAllText(desktop) == content && File.Exists(iconPath)) return;
            EnsureUserDir(Path.GetDirectoryName(desktop)!);
            EnsureUserDir(Path.GetDirectoryName(iconPath)!);
            File.WriteAllText(desktop, content);
            File.WriteAllBytes(iconPath, icon256);
            GiveBack(desktop, false);
            GiveBack(iconPath, false);
        }
        catch { }
    }
}
