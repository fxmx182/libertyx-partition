using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LibertyX.Partition.Core.Platform.Linux;

/// <summary>
/// O /dev de um disco ou partição. Quem cria os nós é o udev; sem ele (contêiner Docker como o
/// webtop, chroot, initramfs mínimo) o kernel mostra o pendrive em /sys/block mas /dev/sdc não
/// existe — nem para o que foi plugado com o app aberto, nem para a partição que o próprio app
/// acabou de criar. Aí o LibertyX (root) cria o nó com o major:minor que o kernel informa em
/// /sys/class/block/&lt;nome&gt;/dev: em /dev, ou numa pasta própria se /dev não aceitar.
/// </summary>
[SupportedOSPlatform("linux")]
public static class DevNodes
{
    public const string AltDir = "/run/libertyx-partition/dev";

    /// <summary>
    /// Caminho do nó, conferido: um /dev que ficou de antes (o Docker copia o /dev do host quando o
    /// contêiner liga) pode apontar para outro major:minor — partições de loop, NVMe e cartões
    /// ganham números novos a cada vez — e abrir dá "No such device or address". Esse é recriado.
    /// </summary>
    public static string Node(string name)
    {
        string dev = "/dev/" + name;
        string mm;
        try { mm = File.ReadAllText($"/sys/class/block/{name}/dev").Trim(); } catch { return dev; }
        var p = mm.Split(':');
        if (p.Length != 2 || !uint.TryParse(p[0], out uint major) || !uint.TryParse(p[1], out uint minor)) return dev;
        var cur = Rdev(dev);
        if (cur == (major, minor) || (cur == null && File.Exists(dev) && !CanCheck)) return dev;
        if (LinuxNative.GetEuid() != 0) return dev;
        if (cur != null) try { File.Delete(dev); } catch { }
        if (Make(dev, p[0], p[1])) return dev;
        string alt = Path.Combine(AltDir, name);
        if (Rdev(alt) == (major, minor)) return alt;
        try
        {
            Directory.CreateDirectory(AltDir);
            if (File.Exists(alt)) File.Delete(alt);
            if (Make(alt, p[0], p[1])) return alt;
        }
        catch { }
        return dev;
    }

    private static bool CanCheck = true;

    /// <summary>major:minor de um nó de bloco (statx), ou nulo se não existe ou não é nó de bloco.</summary>
    private static unsafe (uint, uint)? Rdev(string path)
    {
        if (!CanCheck) return null;
        byte* b = stackalloc byte[256];
        try
        {
            if (Statx(-100, path, 0, 0x3, b) != 0) return null;
        }
        catch (EntryPointNotFoundException) { CanCheck = false; return null; }
        ushort mode = *(ushort*)(b + 28);
        if ((mode & 0xF000) != 0x6000) return null;          // S_IFBLK
        return (*(uint*)(b + 128), *(uint*)(b + 132));
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern unsafe int Statx(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, byte* buf);

    private static bool Make(string path, string major, string minor)
    {
        var r = Proc.RunSync("mknod", ["-m", "660", path, "b", major, minor], TimeSpan.FromSeconds(5));
        if (!r.Ok || !File.Exists(path)) return false;
        Proc.RunSync("chgrp", ["disk", path], TimeSpan.FromSeconds(5));
        return true;
    }
}
