using System.Security.Cryptography;
using System.Xml.Linq;
using LibertyX.Partition.Core.Operations;
using LibertyX.Partition.Core.Platform;
using LibertyX.Partition.Core.Tools;

/// <summary>Gravação estilo balenaEtcher/Rufus: formatos, detecção, gravação verificada e o autounattend.</summary>
public static class FlashTests
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  OK   " : "  FALHA ") + what); if (!ok) fails++; }

    public static int Run(string dir)
    {
        string f = Path.Combine(dir, "flash");
        if (!File.Exists(Path.Combine(f, "disco.img"))) { Console.WriteLine("\n(sem imagens de gravação — rode tests/scripts/make-flash.sh)"); return 0; }
        Console.WriteLine("\nGravar imagem (Rufus / balenaEtcher)");
        string want = File.ReadAllText(Path.Combine(f, "disco.sha256")).Trim();
        long len = new FileInfo(Path.Combine(f, "disco.img")).Length;
        foreach (var (name, comp) in new[] { ("disco.img", (string?)null), ("disco.img.gz", "GZIP"), ("disco.img.xz", "XZ"), ("disco.img.bz2", "BZIP2"), ("disco.img.zip", "ZIP"), ("disco.img.zst", "ZSTD") })
        {
            var src = ImageSource.Inspect(Path.Combine(f, name));
            Check(src.Compression == comp && src.Kind == ImageKind.DiskImage && src.HasMbr, $"{name}: reconhecida ({src.Describe()})");
            string target = Path.Combine(Path.GetTempPath(), $"lx-flash-{Guid.NewGuid():N}.img");
            using (var fs = File.Create(target)) fs.SetLength(64L << 20);
            try
            {
                var usb = new DiskInfo { Number = 900, IsImage = true, ImagePath = target, DevicePath = target, ReadOnly = false, Size = 64L << 20, SectorSize = 512, Model = "pendrive de teste" };
                var sink = new OpSink();
                var lines = new List<string>();
                sink.LogLine += lines.Add;
                BootMedia.WriteImage(src, usb, verify: true, sink);
                using var t = File.OpenRead(target);
                var buf = new byte[len];
                t.ReadExactly(buf);
                bool same = Convert.ToHexStringLower(SHA256.HashData(buf)) == want;
                Check(same && lines.Any(l => l.Contains("Verificado")), $"{name}: gravada e verificada byte a byte (SHA-256 igual ao original)");
            }
            catch (Exception ex) { Check(false, $"{name}: {ex.Message}"); }
            finally { File.Delete(target); }
        }
        // destino pequeno demais: recusa antes de escrever
        {
            var src = ImageSource.Inspect(Path.Combine(f, "disco.img"));
            string target = Path.Combine(Path.GetTempPath(), $"lx-flash-{Guid.NewGuid():N}.img");
            using (var fs = File.Create(target)) fs.SetLength(32L << 20);
            var usb = new DiskInfo { Number = 901, IsImage = true, ImagePath = target, DevicePath = target, ReadOnly = false, Size = 32L << 20, SectorSize = 512 };
            string? err = null;
            try { BootMedia.WriteImage(src, usb, true, new OpSink()); } catch (Exception ex) { err = ex.Message; }
            Check(err != null && err.Contains("não cabe"), "pendrive menor que a imagem: recusado antes de gravar");
            File.Delete(target);
        }
        var c = ImageSource.Inspect(Path.Combine(f, "comum.iso"));
        var h = ImageSource.Inspect(Path.Combine(f, "hibrida.iso"));
        var w = ImageSource.Inspect(Path.Combine(f, "windows.iso"));
        Check(c.Kind == ImageKind.PlainIso && c.VolumeLabel == "COMUM", $"ISO comum reconhecida ({c.Describe()})");
        Check(h.Kind == ImageKind.HybridIso, $"ISO híbrida reconhecida ({h.Describe()})");
        Check(w.Kind == ImageKind.WindowsIso && w.VolumeLabel == "WIN11_PTBR", $"ISO de instalação do Windows reconhecida ({w.Describe()})");

        // autounattend.xml: XML válido e com as chaves certas
        var all = new WindowsSetupOptions(true, true, "Mau & Cia", true, true);
        var xml = Unattend.Build(all);
        XDocument doc = null!;
        string? perr = null;
        try { doc = XDocument.Parse(xml); } catch (Exception ex) { perr = ex.Message; }
        Check(perr == null, "autounattend.xml é XML válido " + perr);
        if (perr == null)
        {
            string txt = doc.ToString();
            Check(new[] { "BypassTPMCheck", "BypassSecureBootCheck", "BypassRAMCheck", "BypassNRO", "PreventDeviceEncryption", "HideOnlineAccountScreens", "<ProtectYourPC>3", "Mau &amp; Cia" }.All(txt.Contains),
                "autounattend: TPM/Secure Boot/RAM, sem conta Microsoft, privacidade, criptografia e conta local");
            Check(doc.Descendants().Count(e => e.Name.LocalName == "component" && (string?)e.Attribute("processorArchitecture") == "arm64") == 3, "autounattend vale para PCs x64 e ARM");
        }
        Check(Unattend.Build(new WindowsSetupOptions(false, false, null, false, false)).Contains("<unattend") && !new WindowsSetupOptions(false, false, " ", false, false).Any, "sem opções marcadas, nada é aplicado");
        Check(WindowsSetupOptions.ValidateUser("a/b") != null && WindowsSetupOptions.ValidateUser("Maurício") == null, "nome de conta inválido é recusado");
        return fails;
    }
}
