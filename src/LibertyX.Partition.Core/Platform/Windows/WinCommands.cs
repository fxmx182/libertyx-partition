using System.Diagnostics;
using System.Text;

namespace LibertyX.Partition.Core.Platform.Windows;

public sealed record CommandResult(int ExitCode, string Output, string Error)
{
    public bool Ok => ExitCode == 0;
    public string Message => (Error.Trim().Length > 0 ? Error : Output).Trim();
}

/// <summary>
/// Operações que o próprio Windows faz melhor e com mais segurança que qualquer código de
/// terceiro: redimensionar NTFS com volume montado, formatar, chkdsk, BitLocker, VSS, bcdboot.
/// Tudo passa por aqui, com saída em UTF-8 e erro legível.
/// </summary>
public static class WinCommands
{
    public static string Q(string s) => "'" + s.Replace("'", "''") + "'";

    public static async Task<CommandResult> PowerShell(string script, CancellationToken ct = default, Action<string>? onLine = null)
    {
        string full = "$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';[Console]::OutputEncoding=[Text.Encoding]::UTF8;" +
                      "try{" + script + "}catch{$m=$_.Exception.Message;" +
                      // os cmdlets de Storage devolvem só "Failed": o motivo de verdade vem no ErrorData do CIM
                      "try{$d=$_.Exception.ErrorData;if($d){foreach($n in 'MessageId','Message','ErrorDescription'){$v=$d.CimInstanceProperties[$n].Value;if($v -and \"$v\" -ne $m){$m+=' · '+$v}}}}catch{};" +
                      "[Console]::Error.WriteLine($m);exit 1}";
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
        return await Run("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}", ct, onLine);
    }

    /// <summary>Comando nativo com o console em UTF-8 (chcp 65001), para acentos saírem certos.</summary>
    public static Task<CommandResult> Cmd(string commandLine, CancellationToken ct = default, Action<string>? onLine = null, string? stdin = null)
        => Run("cmd.exe", "/d /c chcp 65001 >nul & " + commandLine, ct, onLine, stdin);

    public static async Task<CommandResult> Run(string exe, string args, CancellationToken ct = default, Action<string>? onLine = null, string? stdin = null)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = stdin != null, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = new Process { StartInfo = psi };
        var outB = new StringBuilder();
        var errB = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data == null) return; lock (outB) outB.AppendLine(e.Data); onLine?.Invoke(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data == null) return; lock (errB) errB.AppendLine(e.Data); onLine?.Invoke(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (stdin != null) { await p.StandardInput.WriteAsync(stdin); p.StandardInput.Close(); }
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
        p.WaitForExit();
        return new CommandResult(p.ExitCode, outB.ToString(), errB.ToString());
    }

    static string PartSel(int disk, long offset) => $"(Get-Partition -DiskNumber {disk} | Where-Object {{ $_.Offset -eq {offset} }})";

    public static async Task<(long min, long max)?> SupportedSize(int disk, long offset)
    {
        var r = await PowerShell($"$s={PartSel(disk, offset)} | Get-PartitionSupportedSize; Write-Output \"$($s.SizeMin);$($s.SizeMax)\"");
        if (!r.Ok) return null;
        var parts = r.Output.Trim().Split(';');
        return parts.Length == 2 && long.TryParse(parts[0], out var a) && long.TryParse(parts[1], out var b) ? (a, b) : null;
    }

    public static Task<CommandResult> Resize(int disk, long offset, long newSize, CancellationToken ct)
        => PowerShell($"{PartSel(disk, offset)} | Resize-Partition -Size {newSize}", ct);

    public static Task<CommandResult> Delete(int disk, long offset)
        => PowerShell($"{PartSel(disk, offset)} | Remove-Partition -Confirm:$false");

    public static Task<CommandResult> SetLetter(int disk, long offset, char? letter, string? oldLetter)
    {
        string remove = oldLetter != null ? $"{PartSel(disk, offset)} | Remove-PartitionAccessPath -AccessPath {Q(oldLetter + "\\")};" : "";
        string add = letter != null ? $"{PartSel(disk, offset)} | Set-Partition -NewDriveLetter {letter}" : "";
        return PowerShell(remove + add);
    }

    public static Task<CommandResult> SetGptType(int disk, long offset, Guid type) => PowerShell($"{PartSel(disk, offset)} | Set-Partition -GptType {Q("{" + type + "}")}");
    public static Task<CommandResult> SetMbrType(int disk, long offset, byte type) => PowerShell($"{PartSel(disk, offset)} | Set-Partition -MbrType {type}");
    public static Task<CommandResult> SetActive(int disk, long offset, bool active) => PowerShell($"{PartSel(disk, offset)} | Set-Partition -IsActive ${active.ToString().ToLowerInvariant()}");
    public static Task<CommandResult> SetHidden(int disk, long offset, bool hidden) => PowerShell($"{PartSel(disk, offset)} | Set-Partition -IsHidden ${hidden.ToString().ToLowerInvariant()}");

    public static Task<CommandResult> SetLabel(int disk, long offset, string label)
        => PowerShell($"{PartSel(disk, offset)} | Get-Volume | Set-Volume -NewFileSystemLabel {Q(label)}");

    /// <summary>Cria partição no espaço livre e (opcionalmente) formata.</summary>
    public static Task<CommandResult> CreatePartition(int disk, long offset, long size, string? gptType, byte? mbrType, string? fs, string label, int cluster, bool letter, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append($"$p=New-Partition -DiskNumber {disk} -Offset {offset} -Size {size}");
        if (gptType != null) sb.Append($" -GptType {Q("{" + gptType + "}")}");
        if (mbrType != null) sb.Append($" -MbrType {mbrType}");
        if (letter) sb.Append(" -AssignDriveLetter");
        sb.Append(';');
        if (fs != null)
        {
            sb.Append($"$p | Format-Volume -FileSystem {fs} -NewFileSystemLabel {Q(label)} -Confirm:$false -Force");
            if (cluster > 0) sb.Append($" -AllocationUnitSize {cluster}");
            sb.Append(" | Out-Null;");
        }
        sb.Append("Write-Output $p.Offset");
        return PowerShell(sb.ToString(), ct);
    }

    public static Task<CommandResult> Format(int disk, long offset, string fs, string label, int cluster, bool quick, CancellationToken ct)
    {
        string s = $"{PartSel(disk, offset)} | Format-Volume -FileSystem {fs} -NewFileSystemLabel {Q(label)} -Confirm:$false -Force -Full:${(!quick).ToString().ToLowerInvariant()}";
        if (cluster > 0) s += $" -AllocationUnitSize {cluster}";
        return PowerShell(s + " | Out-Null", ct);
    }

    /// <summary>
    /// O format clássico do Windows (format.com): plano B quando o Format-Volume recusa — é o que
    /// o Explorador usa, e aceita cartões e pendrives que o Storage às vezes rejeita.
    /// </summary>
    public static Task<CommandResult> FormatCom(char letter, string fs, string label, int cluster, bool quick, CancellationToken ct, Action<string>? onLine = null)
    {
        var lab = new string(label.Where(c => c != '"').ToArray());
        string args = $"format {letter}: /FS:{fs} /V:\"{lab}\" /Y /X" + (quick ? " /Q" : "") + (cluster > 0 ? $" /A:{cluster}" : "");
        // cartões removíveis pedem ENTER ("insira o disco novo"): responde sozinho
        return Cmd(args, ct, onLine, "\r\n\r\n");
    }

    public static Task<CommandResult> AssignTemporaryLetter(int disk, long offset)
        => PowerShell($"$p={PartSel(disk, offset)}; if(-not $p.DriveLetter -or $p.DriveLetter -eq [char]0){{ $p | Add-PartitionAccessPath -AssignDriveLetter; $p={PartSel(disk, offset)} }}; Write-Output $p.DriveLetter");

    public static Task<CommandResult> Chkdsk(string target, bool fix, bool scanSurface, CancellationToken ct, Action<string> onLine)
        => Cmd($"chkdsk {target}{(fix ? " /f /x" : "")}{(scanSurface ? " /r" : "")}", ct, onLine, fix ? "S\r\nY\r\n" : null);

    public static Task<CommandResult> ConvertToNtfs(char letter, string? label, CancellationToken ct, Action<string> onLine)
        => Cmd($"convert {letter}: /FS:NTFS", ct, onLine, (label ?? "") + "\r\n");

    // ───────────── BitLocker ─────────────

    public sealed record BitLockerInfo(string Mount, string Status, int Percent, string Protection, string Lock, string Method, string Protectors);

    public static async Task<List<BitLockerInfo>> BitLockerStatus()
    {
        var r = await PowerShell("Get-BitLockerVolume | ForEach-Object { \"$($_.MountPoint)|$($_.VolumeStatus)|$($_.EncryptionPercentage)|$($_.ProtectionStatus)|$($_.LockStatus)|$($_.EncryptionMethod)|$(($_.KeyProtector | ForEach-Object { $_.KeyProtectorType }) -join ',')\" }");
        var list = new List<BitLockerInfo>();
        if (!r.Ok) return list;
        foreach (var line in r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var f = line.Split('|');
            if (f.Length < 7) continue;
            list.Add(new BitLockerInfo(f[0], f[1], int.TryParse(f[2], out var pc) ? pc : 0, f[3], f[4], f[5], f[6]));
        }
        return list;
    }

    public static Task<CommandResult> BitLockerUnlockPassword(string mount, string password)
        => PowerShell($"$s=ConvertTo-SecureString {Q(password)} -AsPlainText -Force; Unlock-BitLocker -MountPoint {Q(mount)} -Password $s | Out-Null");
    public static Task<CommandResult> BitLockerUnlockRecovery(string mount, string key)
        => PowerShell($"Unlock-BitLocker -MountPoint {Q(mount)} -RecoveryPassword {Q(key)} | Out-Null");
    public static Task<CommandResult> BitLockerLock(string mount) => PowerShell($"Lock-BitLocker -MountPoint {Q(mount)} -ForceDismount | Out-Null");
    public static Task<CommandResult> BitLockerDisable(string mount) => PowerShell($"Disable-BitLocker -MountPoint {Q(mount)} | Out-Null");
    public static Task<CommandResult> BitLockerEnablePassword(string mount, string password)
        => PowerShell($"$s=ConvertTo-SecureString {Q(password)} -AsPlainText -Force; Enable-BitLocker -MountPoint {Q(mount)} -EncryptionMethod XtsAes256 -UsedSpaceOnly -PasswordProtector -Password $s | Out-Null; Add-BitLockerKeyProtector -MountPoint {Q(mount)} -RecoveryPasswordProtector | Out-Null");
    public static Task<CommandResult> BitLockerRecoveryKeys(string mount)
        => PowerShell($"(Get-BitLockerVolume -MountPoint {Q(mount)}).KeyProtector | Where-Object {{ $_.KeyProtectorType -eq 'RecoveryPassword' }} | ForEach-Object {{ \"$($_.KeyProtectorId) $($_.RecoveryPassword)\" }}");

    // ───────────── VSS, boot ─────────────

    /// <summary>Cria um instantâneo VSS do volume: permite copiar o C: em uso como se estivesse parado.</summary>
    public static async Task<(string id, string device)?> CreateShadow(string volumeRoot, CancellationToken ct)
    {
        var r = await PowerShell($"$r=Invoke-CimMethod -ClassName Win32_ShadowCopy -MethodName Create -Arguments @{{Volume={Q(volumeRoot)};Context='ClientAccessible'}}; if($r.ReturnValue -ne 0){{throw \"VSS falhou (código $($r.ReturnValue))\"}}; $s=Get-CimInstance Win32_ShadowCopy | Where-Object {{ $_.ID -eq $r.ShadowID }}; Write-Output \"$($s.ID)|$($s.DeviceObject)\"", ct);
        if (!r.Ok) throw new IOException("Não foi possível criar o instantâneo VSS: " + r.Message);
        var f = r.Output.Trim().Split('|');
        return f.Length == 2 ? (f[0], f[1]) : null;
    }

    public static Task<CommandResult> DeleteShadow(string id)
        => PowerShell($"Get-CimInstance Win32_ShadowCopy | Where-Object {{ $_.ID -eq {Q(id)} }} | Remove-CimInstance");

    public static Task<CommandResult> BcdBoot(string windowsDir, string systemLetter, bool uefi, CancellationToken ct)
        => Cmd($"bcdboot \"{windowsDir}\" /s {systemLetter} /f {(uefi ? "UEFI" : "BIOS")}", ct);

    public static Task<CommandResult> Mbr2Gpt(int disk, CancellationToken ct, Action<string> onLine)
        => Cmd($"mbr2gpt /convert /disk:{disk} /allowFullOS", ct, onLine);

    public static async Task<string?> MountIso(string iso, CancellationToken ct)
    {
        var r = await PowerShell($"$i=Mount-DiskImage -ImagePath {Q(iso)} -PassThru; Start-Sleep -Milliseconds 800; ($i | Get-Volume).DriveLetter", ct);
        var l = r.Output.Trim();
        return r.Ok && l.Length == 1 ? l + ":" : null;
    }

    public static Task<CommandResult> DismountIso(string iso) => PowerShell($"Dismount-DiskImage -ImagePath {Q(iso)} | Out-Null");

    public static Task<CommandResult> Robocopy(string from, string to, CancellationToken ct, Action<string> onLine)
        => Cmd($"robocopy \"{from.TrimEnd('\\')}\" \"{to.TrimEnd('\\')}\" /E /COPY:DAT /DCOPY:DAT /XJ /R:1 /W:1 /NP /NFL /NDL /MT:8 /XD \"System Volume Information\" \"$RECYCLE.BIN\"", ct, onLine);

    public static Task<CommandResult> RobocopyExcluding(string from, string to, string excludeFile, CancellationToken ct, Action<string> onLine)
        => Cmd($"robocopy \"{from.TrimEnd('\\')}\" \"{to.TrimEnd('\\')}\" /E /R:1 /W:1 /NP /NDL /XF \"{excludeFile}\"", ct, onLine);

    public static Task<CommandResult> SplitWim(string wim, string swm, CancellationToken ct, Action<string> onLine)
        => Cmd($"dism /Split-Image /ImageFile:\"{wim}\" /SWMFile:\"{swm}\" /FileSize:3800", ct, onLine);

    public static Task<CommandResult> ExtendFileSystem(char letter, CancellationToken ct)
        => PowerShell($"$p=Get-Partition -DriveLetter {letter}; $m=($p | Get-PartitionSupportedSize).SizeMax; if($m -gt $p.Size){{ $p | Resize-Partition -Size $m }}", ct);
}
