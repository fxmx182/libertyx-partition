using System.Buffers.Binary;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LibertyX.Partition.Core.Licensing;

public enum Edition { Basic, Premium }

/// <summary>
/// Chave Premium assinada: "LXP1-" + base32 de 12 bytes de dados (versão, produto, data, número
/// da licença) e 64 de assinatura ECDSA P-256. O app só tem a chave pública — confere sozinho,
/// sem internet. Quem emite é o Scripts/licenca.py (bot de venda, ou à mão), com a chave privada.
/// </summary>
public static class LicenseKey
{
    public const string Prefix = "LXP1";
    private const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEGpvUuBp8j84J//alLtBfrcVrOFbf0DhDOIczndBFwCsaOIR6qBKfXWpTNuWoBdXIWXzaPmpT60Q80TXgUCRsRw==";
    /// <summary>Só para os testes: troca a chave pública por uma gerada na hora.</summary>
    public static byte[]? TestPublicKey { get; set; }
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"; // Crockford: sem I, L, O, U

    public sealed record Parsed(string Id, DateTime Issued, Edition Edition);

    public static bool LooksLikeSigned(string text) => Normalize(text).StartsWith(Prefix, StringComparison.Ordinal);

    public static Parsed? Verify(string text, out string? error)
    {
        error = null;
        var s = Normalize(text);
        if (!s.StartsWith(Prefix, StringComparison.Ordinal)) { error = "A chave deve começar com LXP1."; return null; }
        var raw = Decode(s[Prefix.Length..]);
        if (raw == null || raw.Length != 76) { error = "Chave incompleta ou com caracteres trocados — copie e cole de novo."; return null; }
        var data = raw.AsSpan(0, 12);
        var sig = raw.AsSpan(12, 64);
        using var ec = ECDsa.Create();
        ec.ImportSubjectPublicKeyInfo(TestPublicKey ?? Convert.FromBase64String(PublicKey), out _);
        if (!ec.VerifyData(data, sig, HashAlgorithmName.SHA256)) { error = "Chave inválida."; return null; }
        // data[1] é o produto: 1 = Partition, 9 = pacote com todos os apps LibertyX. As chaves do
        // Player (2) e do Folder (3) saem da mesma loja e com a mesma assinatura, mas não valem aqui.
        if (data[0] == 1 && data[1] is 2 or 3) { error = "Esta chave é de outro app LibertyX (Player ou Folder), não do Partition."; return null; }
        if (data[0] != 1 || data[1] is not (1 or 9)) { error = "Chave de uma versão que este app não conhece — atualize o app."; return null; }
        var issued = Epoch.AddDays(BinaryPrimitives.ReadUInt16BigEndian(data[2..]));
        return new Parsed(Convert.ToHexString(data[4..12]), issued, Edition.Premium);
    }

    /// <summary>Monta a chave a partir dos dados e da assinatura (usado pelos testes; o emissor real é o Python).</summary>
    public static string Format(byte[] data12, byte[] sig64)
    {
        var all = data12.Concat(sig64).ToArray();
        var b32 = Encode(all);
        var groups = Enumerable.Range(0, (b32.Length + 5) / 6).Select(i => b32.Substring(i * 6, Math.Min(6, b32.Length - i * 6)));
        return Prefix + "-" + string.Join('-', groups);
    }

    public static byte[] Payload(DateTime issued, byte[] id8, byte product = 1)
    {
        var d = new byte[12];
        d[0] = 1; d[1] = product;
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(2), (ushort)Math.Clamp((issued - Epoch).TotalDays, 0, 65535));
        id8.AsSpan(0, 8).CopyTo(d.AsSpan(4));
        return d;
    }

    private static string Normalize(string t)
    {
        var sb = new StringBuilder(t.Length);
        foreach (var ch in t.ToUpperInvariant())
        {
            if (ch is '-' or ' ' or '\t' or '\r' or '\n') continue;
            sb.Append(ch switch { 'O' => '0', 'I' or 'L' => '1', _ => ch });
        }
        // o prefixo tem L e P: desfaz a troca acima só nele
        var s = sb.ToString();
        return s.StartsWith("1XP1", StringComparison.Ordinal) ? "LXP1" + s[4..] : s;
    }

    private static string Encode(byte[] b)
    {
        var sb = new StringBuilder();
        int buf = 0, bits = 0;
        foreach (var x in b)
        {
            buf = (buf << 8) | x; bits += 8;
            while (bits >= 5) { sb.Append(Alphabet[(buf >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(Alphabet[(buf << (5 - bits)) & 31]);
        return sb.ToString();
    }

    private static byte[]? Decode(string s)
    {
        var o = new List<byte>();
        int buf = 0, bits = 0;
        foreach (var ch in s)
        {
            int v = Alphabet.IndexOf(ch);
            if (v < 0) return null;
            buf = (buf << 5) | v; bits += 5;
            if (bits >= 8) { o.Add((byte)(buf >> (bits - 8))); bits -= 8; }
            buf &= (1 << bits) - 1;
        }
        return [.. o];
    }
}

/// <summary>
/// Chave vendida pela loja (Lemon Squeezy, pagamento com cartão): é conferida na loja uma vez
/// ao ativar, presa a este computador, e reconferida de vez em quando (reembolso = volta ao Básico).
/// </summary>
public static class StoreLicense
{
    /// <summary>Loja e produto do Premium na Lemon Squeezy. Zero = loja ainda não configurada.</summary>
    public static long StoreId { get; set; } = 0;
    public static long ProductId { get; set; } = 0;
    public static bool Configured => StoreId != 0 && ProductId != 0;
    private const string Api = "https://api.lemonsqueezy.com/v1/licenses/";
    public static HttpClient Http { get; set; } = new() { Timeout = TimeSpan.FromSeconds(20) };

    public sealed record Result(bool Ok, string? Error, string? InstanceId, string? Holder);

    public static bool LooksLikeStore(string t) => Guid.TryParse(t.Trim(), out _);

    public static async Task<Result> Activate(string key, string machine, CancellationToken ct = default)
    {
        if (!Configured) return new(false, "A venda com cartão ainda não foi aberta — use uma chave LXP1.", null, null);
        var r = await Post("activate", new() { ["license_key"] = key.Trim(), ["instance_name"] = machine }, ct);
        if (r == null) return new(false, "Sem conexão com a loja. A ativação de chave da loja precisa de internet uma vez.", null, null);
        if (!Mine(r)) return new(false, "Esta chave é de outro produto.", null, null);
        bool ok = r.RootElement.TryGetProperty("activated", out var a) && a.GetBoolean();
        if (!ok) return new(false, Error(r) ?? "A loja recusou a chave.", null, null);
        string? inst = r.RootElement.GetProperty("instance").GetProperty("id").GetString();
        return new(true, null, inst, Holder(r));
    }

    /// <summary>Nulo = sem internet (mantém o Premium); falso = a loja diz que não vale mais.</summary>
    public static async Task<bool?> Validate(string key, string instanceId, CancellationToken ct = default)
    {
        if (!Configured) return null;
        var r = await Post("validate", new() { ["license_key"] = key.Trim(), ["instance_id"] = instanceId }, ct);
        if (r == null) return null;
        return Mine(r) && r.RootElement.TryGetProperty("valid", out var v) && v.GetBoolean();
    }

    public static async Task Deactivate(string key, string instanceId, CancellationToken ct = default)
    {
        if (Configured) await Post("deactivate", new() { ["license_key"] = key.Trim(), ["instance_id"] = instanceId }, ct);
    }

    private static async Task<JsonDocument?> Post(string what, Dictionary<string, string> form, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Api + what) { Content = new FormUrlEncodedContent(form) };
            req.Headers.Accept.ParseAdd("application/json");
            using var resp = await Http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            return JsonDocument.Parse(body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return null; }
    }

    private static bool Mine(JsonDocument r) =>
        r.RootElement.TryGetProperty("meta", out var m) && m.ValueKind == JsonValueKind.Object
        && m.TryGetProperty("store_id", out var s) && s.GetInt64() == StoreId
        && m.TryGetProperty("product_id", out var p) && p.GetInt64() == ProductId;

    private static string? Error(JsonDocument r) => r.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static string? Holder(JsonDocument r) =>
        r.RootElement.TryGetProperty("meta", out var m) && m.TryGetProperty("customer_name", out var n) ? n.GetString() : null;
}

/// <summary>O que fica gravado no computador: a licença ativa e quanto o Básico já recuperou.</summary>
public sealed class LicenseState
{
    public string? Key { get; set; }
    public string? Kind { get; set; }              // "assinada" ou "loja"
    public string? InstanceId { get; set; }
    public string? Holder { get; set; }
    public string? LicenseId { get; set; }
    public DateTime? ActivatedAt { get; set; }
    public DateTime? LastValidated { get; set; }
    public long FreeRecoveredBytes { get; set; }

    [JsonIgnore] public Edition Edition { get; set; }
}

/// <summary>Licença do app: edição atual, ativar/desativar e a cota de recuperação do Básico.</summary>
public sealed class LicenseManager
{
    /// <summary>O Básico recupera até 1 GB (somado entre todas as recuperações), como os do mercado.</summary>
    public const long FreeLimit = 1L << 30;
    public static readonly TimeSpan Revalidate = TimeSpan.FromDays(30);

    private readonly string _path;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public LicenseState State { get; private set; } = new();
    public Edition Edition => State.Edition;
    public bool IsPremium => Edition == Edition.Premium;
    public long FreeRemaining => Math.Max(0, FreeLimit - State.FreeRecoveredBytes);

    public LicenseManager(string? dir = null)
    {
        dir ??= DefaultDir();
        _path = Path.Combine(dir, "licenca.json");
        Load();
    }

    /// <summary>Para todos os usuários do computador (o app roda como administrador).</summary>
    public static string DefaultDir() => Platform.AppPaths.MachineData;

    public static string MachineName() => Environment.MachineName;

    private void Load()
    {
        try { if (File.Exists(_path)) State = JsonSerializer.Deserialize<LicenseState>(File.ReadAllText(_path)) ?? new(); }
        catch { State = new(); }
        State.Edition = Edition.Basic;
        if (State.Key == null) return;
        if (State.Kind == "assinada" && LicenseKey.Verify(State.Key, out _) != null) State.Edition = Edition.Premium;
        else if (State.Kind == "loja" && State.InstanceId != null) State.Edition = Edition.Premium;   // reconferida em segundo plano
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(State, Json));
    }

    public async Task<string?> Activate(string text, CancellationToken ct = default)
    {
        text = text.Trim();
        if (text.Length == 0) return "Cole a chave.";
        if (LicenseKey.LooksLikeSigned(text))
        {
            var p = LicenseKey.Verify(text, out var err);
            if (p == null) return err;
            State.Key = text; State.Kind = "assinada"; State.LicenseId = p.Id; State.InstanceId = null; State.Holder = null;
        }
        else if (StoreLicense.LooksLikeStore(text))
        {
            var r = await StoreLicense.Activate(text, MachineName(), ct);
            if (!r.Ok) return r.Error;
            State.Key = text; State.Kind = "loja"; State.InstanceId = r.InstanceId; State.Holder = r.Holder; State.LicenseId = null;
            State.LastValidated = DateTime.UtcNow;
        }
        else return "Isto não parece uma chave do LibertyX Partition.";
        State.ActivatedAt = DateTime.UtcNow;
        State.Edition = Edition.Premium;
        Save();
        return null;
    }

    /// <summary>Tira a licença deste computador (na loja, libera a vaga para usar em outro).</summary>
    public async Task Deactivate(CancellationToken ct = default)
    {
        if (State.Kind == "loja" && State.Key != null && State.InstanceId != null)
            try { await StoreLicense.Deactivate(State.Key, State.InstanceId, ct); } catch { }
        State.Key = null; State.Kind = null; State.InstanceId = null; State.Holder = null; State.LicenseId = null; State.ActivatedAt = null;
        State.Edition = Edition.Basic;
        Save();
    }

    /// <summary>Chave da loja: reconfere a cada 30 dias. Sem internet não tira o Premium; reembolso tira.</summary>
    public async Task<bool> RevalidateIfDue(CancellationToken ct = default)
    {
        if (State.Kind != "loja" || State.Key == null || State.InstanceId == null) return false;
        if (State.LastValidated is { } t && DateTime.UtcNow - t < Revalidate) return false;
        var ok = await StoreLicense.Validate(State.Key, State.InstanceId, ct);
        if (ok == null) return false;
        if (ok == true) { State.LastValidated = DateTime.UtcNow; Save(); return false; }
        State.Key = null; State.Kind = null; State.InstanceId = null; State.Edition = Edition.Basic;
        Save();
        return true;
    }

    public void AddFreeRecovered(long bytes)
    {
        if (IsPremium || bytes <= 0) return;
        State.FreeRecoveredBytes += bytes;
        Save();
    }

    public string ShortKey => State.Key is { Length: > 10 } k ? "…" + k.Replace("-", "")[^6..] : "";
}
