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
    public sealed record Info(int Build, string Version, string SetupUrl, long SetupSize, string PortableUrl);

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

    public static async Task<Info?> Check(CancellationToken ct = default)
    {
        try
        {
            using var http = NewHttp();
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            var json = await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/tags/latest", ct);
            using var doc = JsonDocument.Parse(json);
            var m = VersionRx().Match(doc.RootElement.GetProperty("body").GetString() ?? "");
            if (!m.Success) return null;
            int build = int.Parse(m.Groups[1].Value);
            string setup = "", portable = ""; long size = 0;
            foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                string name = a.GetProperty("name").GetString() ?? "";
                if (name == "LibertyXPartition-Setup.exe") { setup = a.GetProperty("browser_download_url").GetString() ?? ""; size = a.GetProperty("size").GetInt64(); }
                if (name == "LibertyXPartition.exe") portable = a.GetProperty("browser_download_url").GetString() ?? "";
            }
            return setup.Length == 0 ? null : new Info(build, "1.0." + build, setup, size, portable);
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
