using System.Runtime.Versioning;
using System.Text.Json;
using LibertyX.Partition.Core.FileSystems;
using LibertyX.Partition.Core.Operations;

namespace LibertyX.Partition.Core.Platform.Linux;

/// <summary>
/// LUKS (o padrão de criptografia do Linux) e BitLocker (o do Windows, que o cryptsetup abre
/// desde a versão 2.3) pelo cryptsetup. Senha nunca vai na linha de comando: entra pelo stdin
/// ou por arquivo temporário em /run (memória, só o root lê) que é apagado logo depois.
/// </summary>
[SupportedOSPlatform("linux")]
public static class LinuxCrypto
{
    public sealed record Volume(PartitionInfo Part, string Kind, string? Version, string? Label, string? Uuid, string? MapperName, List<string> MountedAt)
    {
        public bool IsOpen => MapperName != null;
        public string Device => Part.DeviceNode ?? Part.Disk.DevicePath;
    }

    /// <summary>Todos os volumes criptografados dos discos (abertos ou não).</summary>
    public static List<Volume> List(IEnumerable<DiskInfo> disks)
    {
        var res = new List<Volume>();
        foreach (var p in disks.Where(d => !d.IsImage).SelectMany(d => d.Partitions).Where(p => p.FileSystem is FsKind.Luks or FsKind.BitLocker && p.DeviceNode != null))
        {
            string kn = p.KernelName ?? p.Disk.KernelName;
            string? mapper = null;
            foreach (var h in LinuxDiskSystem.HoldersOf(kn))
            {
                string uuid = Read($"/sys/class/block/{h}/dm/uuid");
                if (uuid.StartsWith("CRYPT-")) { mapper = Read($"/sys/class/block/{h}/dm/name"); break; }
            }
            res.Add(new Volume(p, p.FileSystem == FsKind.Luks ? "LUKS" : "BitLocker", p.FsVariant, p.Label, p.FsUuid, mapper, [.. p.MountPoints]));
        }
        return res;
    }

    private static string Read(string p) { try { return File.ReadAllText(p).Trim(); } catch { return ""; } }

    private static void Need()
    {
        if (LinuxTools.Missing("cryptsetup") is { } m) throw new InvalidOperationException(m);
    }

    private static string Msg(CommandResult r) => r.ExitCode switch
    {
        2 => "Senha ou chave incorreta.",
        5 => "O dispositivo já está aberto ou em uso.",
        _ => r.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? $"cryptsetup terminou com o código {r.ExitCode}",
    };

    /// <summary>Nome do mapeamento no padrão do sistema (luks-&lt;uuid&gt;), que o udisks e o systemd reconhecem.</summary>
    public static string MapperFor(Volume v) =>
        v.Kind == "LUKS" ? $"luks-{v.Uuid ?? Guid.NewGuid().ToString()}" : $"bitlk-{(v.Part.KernelName ?? "vol")}";

    /// <summary>Abre com senha, chave de recuperação do BitLocker (48 dígitos) ou arquivo-chave.</summary>
    public static async Task<string> Open(Volume v, string? secret, string? keyFile, bool readOnly, OpSink s)
    {
        Need();
        string name = MapperFor(v);
        var args = new List<string> { "open" };
        if (v.Kind == "BitLocker") args.AddRange(["--type", "bitlk"]);
        if (readOnly) args.Add("--readonly");
        args.Add(keyFile != null ? $"--key-file={keyFile}" : "--key-file=-");
        args.AddRange([v.Device, name]);
        var r = await Proc.Run("cryptsetup", args, s.Token, l => { if (l.Trim().Length > 0) s.Log(l.Trim()); }, stdin: keyFile == null ? secret : null);
        if (!r.Ok) throw new InvalidOperationException("Não abriu: " + Msg(r));
        KernelSync.Settle();
        return "/dev/mapper/" + name;
    }

    public static async Task Close(Volume v, OpSink s)
    {
        Need();
        if (v.MapperName == null) return;
        foreach (var mp in v.MountedAt.OrderByDescending(m => m.Length))
        {
            var u = await Proc.Run("umount", [mp], s.Token);
            if (!u.Ok)
            {
                var who = LinuxDiskSystem.WhoUses(mp);
                throw new InvalidOperationException($"{mp} está em uso{(who.Count > 0 ? " por: " + string.Join(", ", who.Take(6)) : "")}.");
            }
        }
        // LVM dentro do LUKS: desativa o grupo antes de fechar
        string? dm = LinuxDiskSystem.KernelNameOf("/dev/mapper/" + v.MapperName);
        if (dm != null && LinuxDiskSystem.HoldersOf(dm).Count > 0 && LinuxTools.Has("lvm"))
            foreach (var h in LinuxDiskSystem.HoldersOf(dm))
                if (Read($"/sys/class/block/{h}/dm/uuid").StartsWith("LVM-"))
                    await Proc.Run("lvm", ["lvchange", "-an", "/dev/mapper/" + Read($"/sys/class/block/{h}/dm/name")], s.Token);
        var r = await Proc.Run("cryptsetup", ["close", v.MapperName], s.Token);
        if (!r.Ok) throw new InvalidOperationException("Não fechou: " + Msg(r));
    }

    private static async Task<T> WithSecretFiles<T>(Func<string[], Task<T>> work, params string[] secrets)
    {
        Directory.CreateDirectory("/run/libertyx");
        var files = secrets.Select(_ => Path.Combine("/run/libertyx", "k-" + Guid.NewGuid().ToString("N"))).ToArray();
        try
        {
            for (int i = 0; i < files.Length; i++)
            {
                File.WriteAllText(files[i], "");
                File.SetUnixFileMode(files[i], UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.WriteAllText(files[i], secrets[i]);
            }
            return await work(files);
        }
        finally
        {
            foreach (var f in files) try { File.WriteAllBytes(f, new byte[64]); File.Delete(f); } catch { }
        }
    }

    public static Task AddKey(Volume v, string existing, string added, OpSink s) => WithSecretFiles(async f =>
    {
        Need();
        var r = await Proc.Run("cryptsetup", ["luksAddKey", "-q", $"--key-file={f[0]}", v.Device, f[1]], s.Token);
        if (!r.Ok) throw new InvalidOperationException("Não adicionou: " + Msg(r));
        return true;
    }, existing, added);

    public static async Task RemoveKey(Volume v, string secret, OpSink s)
    {
        Need();
        var r = await Proc.Run("cryptsetup", ["luksRemoveKey", "-q", "--key-file=-", v.Device], s.Token, stdin: secret);
        if (!r.Ok) throw new InvalidOperationException("Não removeu: " + Msg(r));
    }

    public static async Task ChangeKey(Volume v, string oldSecret, string newSecret, OpSink s) => await WithSecretFiles(async f =>
    {
        Need();
        var r = await Proc.Run("cryptsetup", ["luksChangeKey", "-q", $"--key-file={f[0]}", v.Device, f[1]], s.Token);
        if (!r.Ok) throw new InvalidOperationException("Não trocou: " + Msg(r));
        return true;
    }, oldSecret, newSecret);

    /// <summary>
    /// Cópia do cabeçalho: sem ela, um cabeçalho danificado (formatação acidental dos primeiros
    /// megabytes, setor ruim) torna os dados irrecuperáveis mesmo com a senha certa.
    /// </summary>
    public static async Task<string> BackupHeader(Volume v, string folder, OpSink s)
    {
        Need();
        string file = Path.Combine(folder, $"cabecalho-luks-{v.Part.KernelName ?? "volume"}-{(v.Uuid ?? "")[..Math.Min(8, (v.Uuid ?? "").Length)]}-{DateTime.Now:yyyyMMdd-HHmmss}.img");
        var r = await Proc.Run("cryptsetup", ["luksHeaderBackup", v.Device, "--header-backup-file", file], s.Token);
        if (!r.Ok) throw new InvalidOperationException("Não salvou: " + Msg(r));
        LinuxSession.GiveBack(file, false);
        return file;
    }

    public static async Task RestoreHeader(Volume v, string file, OpSink s)
    {
        Need();
        var r = await Proc.Run("cryptsetup", ["luksHeaderRestore", "-q", v.Device, "--header-backup-file", file], s.Token);
        if (!r.Ok) throw new InvalidOperationException("Não restaurou: " + Msg(r));
    }

    public sealed record KeySlot(string Id, string Type, string? Kdf);

    /// <summary>Resumo do cabeçalho: versão, cifra, slots de chave ocupados.</summary>
    public static async Task<(string summary, List<KeySlot> slots)> Dump(Volume v)
    {
        Need();
        if (v.Kind == "BitLocker")
        {
            var b = await Proc.Run("cryptsetup", ["bitlkDump", v.Device]);
            return (b.Ok ? b.Output : Msg(b), []);
        }
        var j = await Proc.Run("cryptsetup", ["luksDump", "--dump-json-metadata", v.Device]);
        var slots = new List<KeySlot>();
        string cipher = "";
        if (j.Ok && j.Output.TrimStart().StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(j.Output);
                if (doc.RootElement.TryGetProperty("keyslots", out var ks))
                    foreach (var k in ks.EnumerateObject())
                        slots.Add(new KeySlot(k.Name, k.Value.GetProperty("type").GetString() ?? "", k.Value.TryGetProperty("kdf", out var kdf) && kdf.TryGetProperty("type", out var kt) ? kt.GetString() : null));
                if (doc.RootElement.TryGetProperty("segments", out var sg))
                    foreach (var x in sg.EnumerateObject()) { if (x.Value.TryGetProperty("encryption", out var en)) cipher = en.GetString() ?? ""; break; }
            }
            catch { }
            return ($"LUKS2 · {cipher}", slots);
        }
        var d = await Proc.Run("cryptsetup", ["luksDump", v.Device]);
        foreach (var line in d.Output.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("Cipher name:")) cipher = t[12..].Trim() + cipher;
            if (t.StartsWith("Cipher mode:")) cipher += "-" + t[12..].Trim();
            if (t.StartsWith("Key Slot") && t.EndsWith("ENABLED")) slots.Add(new KeySlot(t[9..10], "luks1", "pbkdf2"));
        }
        return ($"{v.Version ?? "LUKS"} · {cipher}", slots);
    }
}

/// <summary>LVM e RAID por software: o que existe, o que está ativo, e ligar/desligar.</summary>
[SupportedOSPlatform("linux")]
public static class LinuxVolumes
{
    public sealed record Vg(string Name, long Size, long Free, int Pvs, int Lvs, string Attr);
    public sealed record Lv(string Vg, string Name, long Size, bool Active, string Attr, string Path);
    public sealed record Md(string Name, string Level, string State, string Devices, string Sync);

    public static async Task<(List<Vg> vgs, List<Lv> lvs, string? error)> Lvm()
    {
        var vgs = new List<Vg>();
        var lvs = new List<Lv>();
        if (!LinuxTools.Has("lvm")) return (vgs, lvs, LinuxTools.Missing("lvm"));
        var a = await Proc.Run("lvm", ["vgs", "--reportformat", "json", "--units", "b", "--nosuffix", "-o", "vg_name,vg_size,vg_free,pv_count,lv_count,vg_attr"]);
        var b = await Proc.Run("lvm", ["lvs", "--reportformat", "json", "--units", "b", "--nosuffix", "-o", "vg_name,lv_name,lv_size,lv_active,lv_attr,lv_path"]);
        try
        {
            foreach (var r in Rows(a.Output, "vg"))
                vgs.Add(new Vg(S(r, "vg_name"), L(r, "vg_size"), L(r, "vg_free"), (int)L(r, "pv_count"), (int)L(r, "lv_count"), S(r, "vg_attr")));
            foreach (var r in Rows(b.Output, "lv"))
                lvs.Add(new Lv(S(r, "vg_name"), S(r, "lv_name"), L(r, "lv_size"), S(r, "lv_active") == "active" || S(r, "lv_attr").Length > 4 && S(r, "lv_attr")[4] == 'a', S(r, "lv_attr"), S(r, "lv_path")));
        }
        catch (Exception ex) { return (vgs, lvs, ex.Message); }
        return (vgs, lvs, a.Ok ? null : a.Message);
    }

    private static IEnumerable<JsonElement> Rows(string json, string kind)
    {
        if (string.IsNullOrWhiteSpace(json)) yield break;
        using var doc = JsonDocument.Parse(json);
        foreach (var rep in doc.RootElement.GetProperty("report").EnumerateArray())
            if (rep.TryGetProperty(kind, out var arr))
                foreach (var r in arr.EnumerateArray()) yield return r.Clone();
    }

    private static string S(JsonElement r, string k) => r.TryGetProperty(k, out var v) ? v.GetString() ?? "" : "";
    private static long L(JsonElement r, string k) => long.TryParse(S(r, k), out var v) ? v : 0;

    public static Task<CommandResult> SetVgActive(string vg, bool on, CancellationToken ct = default) => Proc.Run("lvm", ["vgchange", on ? "-ay" : "-an", vg], ct);

    public static Task<CommandResult> ScanAll(CancellationToken ct = default) => Proc.Run("lvm", ["vgscan", "--mknodes"], ct);

    public static List<Md> Raid()
    {
        var res = new List<Md>();
        try
        {
            if (!File.Exists("/proc/mdstat")) return res;
            var lines = File.ReadAllLines("/proc/mdstat");
            for (int i = 0; i < lines.Length; i++)
            {
                var l = lines[i];
                if (!l.StartsWith("md")) continue;
                // md0 : active raid1 sdb1[1] sda1[0]
                var parts = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                string state = parts.Length > 2 ? parts[2] : "";
                string level = parts.Length > 3 && parts[3].StartsWith("raid") ? parts[3] : "";
                string devs = string.Join(" ", parts.Skip(level.Length > 0 ? 4 : 3));
                string next = i + 1 < lines.Length ? lines[i + 1].Trim() : "";
                string sync = i + 2 < lines.Length && lines[i + 2].Contains('%') ? lines[i + 2].Trim() : "";
                var m = System.Text.RegularExpressions.Regex.Match(next, @"\[(\d+)/(\d+)\]\s*\[([U_]+)\]");
                res.Add(new Md(parts[0], level, state + (m.Success ? $" · {m.Groups[2].Value} de {m.Groups[1].Value} discos ativos [{m.Groups[3].Value}]" : ""), devs, sync));
            }
        }
        catch { }
        return res;
    }

    public static Task<CommandResult> AssembleRaid(CancellationToken ct = default) => Proc.Run("mdadm", ["--assemble", "--scan"], ct);
    public static Task<CommandResult> StopRaid(string md, CancellationToken ct = default) => Proc.Run("mdadm", ["--stop", "/dev/" + md], ct);
}
