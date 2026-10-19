using System.Security.Cryptography;
using LibertyX.Partition.Core.Licensing;

/// <summary>Licença: chave assinada (formato, assinatura, digitação), estado gravado e cota do Básico.</summary>
public static class LicenseTests
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  OK   " : "  FALHA ") + what); if (!ok) fails++; }

    public static int Run()
    {
        Console.WriteLine("\nLicença");
        // a chave de verdade (emitida pelo Scripts/licenca.py) confere com a pública embutida no app
        var real = Environment.GetEnvironmentVariable("LXP_CHAVE_REAL");
        if (real != null) Check(LicenseKey.Verify(real, out var e0) != null, $"chave emitida pelo Python confere no app {e0}");

        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        LicenseKey.TestPublicKey = ec.ExportSubjectPublicKeyInfo();
        try
        {
            var data = LicenseKey.Payload(new DateTime(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc), RandomNumberGenerator.GetBytes(8));
            string key = LicenseKey.Format(data, ec.SignData(data, HashAlgorithmName.SHA256));
            var p = LicenseKey.Verify(key, out var err);
            Check(p != null && p.Edition == Edition.Premium && p.Issued == new DateTime(2026, 10, 18), $"chave válida aceita ({key.Length} caracteres) {err}");
            Check(LicenseKey.Verify(key.ToLowerInvariant().Replace("-", " "), out _) != null, "aceita minúsculas e espaços no lugar dos traços");
            var o = key.Replace('0', 'O');
            Check(LicenseKey.Verify(o, out _) != null, "aceita a letra O digitada no lugar do zero");
            var bad = key[..^3] + (key[^3] == 'A' ? 'B' : 'A') + key[^2..];
            Check(LicenseKey.Verify(bad, out var e1) == null && e1 == "Chave inválida.", "chave adulterada recusada");
            var other = data.ToArray(); other[4] ^= 1;
            Check(LicenseKey.Verify(LicenseKey.Format(other, ec.SignData(data, HashAlgorithmName.SHA256)), out _) == null, "assinatura de outra licença recusada");
            Check(LicenseKey.Verify(key[..40], out _) == null, "chave cortada recusada");
            string DoProduto(byte produto) { var d = LicenseKey.Payload(DateTime.UtcNow, RandomNumberGenerator.GetBytes(8), produto); return LicenseKey.Format(d, ec.SignData(d, HashAlgorithmName.SHA256)); }
            Check(LicenseKey.Verify(DoProduto(9), out _) != null, "chave do pacote LibertyX (produto 9) aceita");
            Check(LicenseKey.Verify(DoProduto(2), out var e2) == null && e2!.Contains("outro app"), "chave do Player (produto 2) recusada com aviso");
            Check(LicenseKey.Verify(DoProduto(3), out var e3) == null && e3!.Contains("outro app"), "chave do Folder (produto 3) recusada com aviso");

            var dir = Path.Combine(Path.GetTempPath(), "lx-lic-" + Guid.NewGuid().ToString("N"));
            var m = new LicenseManager(dir);
            Check(!m.IsPremium && m.FreeRemaining == LicenseManager.FreeLimit, "começa no Básico com 1 GB de recuperação grátis");
            m.AddFreeRecovered(300L << 20);
            Check(new LicenseManager(dir).FreeRemaining == LicenseManager.FreeLimit - (300L << 20), "o consumo da cota fica gravado (300 MB usados)");
            Check(m.Activate("isto não é chave").Result != null, "texto qualquer recusado");
            Check(m.Activate(Guid.NewGuid().ToString()).Result is { } se && se.Contains("ainda não foi aberta"), "chave de loja recusada com aviso enquanto a loja não está configurada");
            Check(m.Activate(key).Result == null && m.IsPremium, "ativa o Premium com a chave");
            var again = new LicenseManager(dir);
            Check(again.IsPremium && again.State.LicenseId == p!.Id, "Premium continua ao abrir de novo");
            again.AddFreeRecovered(5L << 30);
            Check(again.State.FreeRecoveredBytes == 300L << 20, "no Premium a cota não é consumida");
            File.WriteAllText(Path.Combine(dir, "licenca.json"), File.ReadAllText(Path.Combine(dir, "licenca.json")).Replace(key, bad));
            Check(!new LicenseManager(dir).IsPremium, "licença editada à mão no arquivo não vale");
            new LicenseManager(dir).Activate(key).Wait();
            var d = new LicenseManager(dir); d.Deactivate().Wait();
            Check(!new LicenseManager(dir).IsPremium, "desativar volta ao Básico");
            Directory.Delete(dir, true);
        }
        finally { LicenseKey.TestPublicKey = null; }
        return fails;
    }
}
