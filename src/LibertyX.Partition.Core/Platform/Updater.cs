using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LibertyX.Partition.Core.Platform;

/// <summary>
/// Atualização do app instalado: confere o release "latest" do GitHub (o CI publica cada versão
/// aprovada no autoteste), baixa o instalador e roda por cima — o AppId fixo do Inno Setup faz
/// substituir a versão anterior mantendo licença, backups e configurações.
/// </summary>
public static partial class Updater
{
    public const string Repo = "fxmx182/libertyx-partition";
    public sealed record Info(int Build, string Version, string SetupUrl, long SetupSize, string PortableUrl, string Notes);

    /// <summary>O CI carimba 1.0.N; o N é o número do build. Zero = build local, sem atualização.</summary>
    public static int CurrentBuild => System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.Build ?? 0;

    /// <summary>Rodando da pasta onde o instalador pôs (tem o desinstalador do lado).</summary>
    public static bool IsInstalled
    {
        get
        {
            try { return File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe")); } catch { return false; }
        }
    }

    private static HttpClient NewHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LibertyXPartition", CurrentBuild.ToString()));
        return h;
    }

    [GeneratedRegex(@"Versão 1\.0\.(\d+)")] private static partial Regex VersionRx();
    [GeneratedRegex(@"Novidades: (.+)")] private static partial Regex NotesRx();

    public static async Task<Info?> Check(CancellationToken ct = default) => await CheckFile(ct) ?? await CheckApi(ct);

    /// <summary>
    /// O versao.txt que o CI publica junto: download direto, sem o limite de 60 consultas por hora
    /// da API do GitHub (que redes compartilhadas e os próprios servidores do CI estouram).
    /// </summary>
    private static async Task<Info?> CheckFile(CancellationToken ct)
    {
        try
        {
            using var http = NewHttp();
            string txt = await http.GetStringAsync($"https://github.com/{Repo}/releases/download/latest/versao.txt", ct);
            var kv = txt.Split('\n').Select(l => l.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0].Trim(), p => p[1].Trim());
            if (!kv.TryGetValue("versao", out var v) || !v.StartsWith("1.0.") || !int.TryParse(v[4..], out int build)) return null;
            string baseUrl = $"https://github.com/{Repo}/releases/download/latest/";
            long.TryParse(kv.GetValueOrDefault("tamanho"), out long size);
            return new Info(build, v, baseUrl + kv.GetValueOrDefault("setup", "LibertyXPartition-Setup.exe"), size,
                            baseUrl + kv.GetValueOrDefault("portatil", "LibertyXPartition.exe"), kv.GetValueOrDefault("novidades", ""));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return null; }
    }

    private static async Task<Info?> CheckApi(CancellationToken ct)
    {
        try
        {
            using var http = NewHttp();
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            var json = await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/tags/latest", ct);
            using var doc = JsonDocument.Parse(json);
            string body = doc.RootElement.GetProperty("body").GetString() ?? "";
            var m = VersionRx().Match(body);
            var nm = NotesRx().Match(body);
            if (!m.Success) return null;
            int build = int.Parse(m.Groups[1].Value);
            string setup = "", portable = ""; long size = 0;
            foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                string name = a.GetProperty("name").GetString() ?? "";
                if (name == "LibertyXPartition-Setup.exe") { setup = a.GetProperty("browser_download_url").GetString() ?? ""; size = a.GetProperty("size").GetInt64(); }
                if (name == "LibertyXPartition.exe") portable = a.GetProperty("browser_download_url").GetString() ?? "";
            }
            return setup.Length == 0 ? null : new Info(build, "1.0." + build, setup, size, portable, nm.Success ? nm.Groups[1].Value.Trim() : "");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    public static bool IsNewer(Info i) => CurrentBuild > 0 && i.Build > CurrentBuild;

    /// <summary>Baixa o instalador para a pasta temporária e confere o tamanho.</summary>
    public static async Task<string> Download(Info i, IProgress<double>? progress, CancellationToken ct)
    {
        using var http = NewHttp();
        http.Timeout = TimeSpan.FromMinutes(20);
        string dest = Path.Combine(Path.GetTempPath(), $"LibertyXPartition-Setup-{i.Version}.exe");
        using (var resp = await http.GetAsync(i.SetupUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? i.SetupSize;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(dest);
            var buf = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
        }
        if (i.SetupSize > 0 && new FileInfo(dest).Length != i.SetupSize) throw new IOException("O download veio incompleto — tente de novo.");
        return dest;
    }

    /// <summary>Roda o instalador por cima (com janela de progresso) e ele reabre o app no fim.</summary>
    public static void LaunchSetup(string setup) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(setup, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /UPDATE=1") { UseShellExecute = true });
}
