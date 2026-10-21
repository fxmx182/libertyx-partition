using System.Diagnostics;
using System.Text;

namespace LibertyX.Partition.Core.Platform;

public sealed record CommandResult(int ExitCode, string Output, string Error)
{
    public bool Ok => ExitCode == 0;
    public string Message => (Error.Trim().Length > 0 ? Error : Output).Trim();
}

/// <summary>Roda programas externos com os argumentos separados (nada passa por shell), saída em UTF-8.</summary>
public static class Proc
{
    public static async Task<CommandResult> Run(string exe, IEnumerable<string> args, CancellationToken ct = default, Action<string>? onLine = null,
        string? stdin = null, IDictionary<string, string>? env = null, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // mensagens das ferramentas em inglês estável (o app traduz o que importa) e sem paginador
        psi.Environment["LC_ALL"] = "C.UTF-8";
        psi.Environment["LANG"] = "C.UTF-8";
        psi.Environment["PAGER"] = "cat";
        psi.Environment["SYSTEMD_PAGER"] = "";
        // sem udev neste namespace (contêiner, sistema mínimo), o libdevmapper do cryptsetup/lvm
        // esperaria para sempre a confirmação do udev: cria os /dev/mapper ele mesmo
        if (OperatingSystem.IsLinux() && !File.Exists("/run/udev/control") && Path.GetFileName(exe) is "cryptsetup" or "lvm" or "dmsetup")
            psi.Environment["DM_DISABLE_UDEV"] = "1";
        if (env != null) foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = new Process { StartInfo = psi };
        var outB = new StringBuilder();
        var errB = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data == null) return; lock (outB) outB.AppendLine(e.Data); onLine?.Invoke(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data == null) return; lock (errB) errB.AppendLine(e.Data); onLine?.Invoke(e.Data); };
        try { p.Start(); }
        catch (System.ComponentModel.Win32Exception ex) { return new CommandResult(127, "", $"{Path.GetFileName(exe)}: {ex.Message}"); }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            if (stdin != null) await p.StandardInput.WriteAsync(stdin);
            p.StandardInput.Close();
        }
        catch (IOException) { }
        using var lim = timeout is { } t ? new CancellationTokenSource(t) : null;
        using var both = lim != null ? CancellationTokenSource.CreateLinkedTokenSource(ct, lim.Token) : null;
        try { await p.WaitForExitAsync(both?.Token ?? ct); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            if (ct.IsCancellationRequested) throw;
            return new CommandResult(124, outB.ToString(), $"{Path.GetFileName(exe)} não respondeu em {timeout!.Value.TotalSeconds:0} s.");
        }
        p.WaitForExit();
        return new CommandResult(p.ExitCode, outB.ToString(), errB.ToString());
    }

    public static Task<CommandResult> Run(string exe, params string[] args) => Run(exe, (IEnumerable<string>)args);

    /// <summary>Versão síncrona com limite de tempo, para consultas rápidas fora de operações.</summary>
    public static CommandResult RunSync(string exe, IEnumerable<string> args, TimeSpan timeout, string? stdin = null)
    {
        try { return Run(exe, args, default, null, stdin, null, timeout).GetAwaiter().GetResult(); }
        catch (Exception ex) { return new CommandResult(-1, "", ex.Message); }
    }
}
