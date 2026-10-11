using System.Security.Cryptography;
using LibertyX.Partition.Core.IO;
using LibertyX.Partition.Core.Recovery;

/// <summary>Formatos novos da varredura profunda, gravados num disco sintético em posições alinhadas.</summary>
public static class CarveExtraTests
{
    public static int Run()
    {
        int fails = 0;
        void Check(bool ok, string what) { Console.WriteLine((ok ? "  OK   " : "  FALHA ") + what); if (!ok) fails++; }
        Console.WriteLine("\nFormatos de vídeo de câmera e Photoshop");
        var rnd = new Random(5);
        byte[] Ts(int packet, int n)
        {
            var b = new byte[packet * n];
            for (int i = 0; i < n; i++) { var pk = b.AsSpan(i * packet, packet); rnd.NextBytes(pk); pk[packet == 192 ? 4 : 0] = 0x47; }
            return b;
        }
        byte[] Psd(bool rle)
        {
            int ch = 3, hgt = 40, wid = 50;
            var ms = new MemoryStream();
            void BE16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
            void BE32(long v) { BE16((int)(v >> 16)); BE16((int)(v & 0xFFFF)); }
            ms.Write("8BPS"u8); BE16(1); ms.Write(new byte[6]); BE16(ch); BE32(hgt); BE32(wid); BE16(8); BE16(3);
            BE32(0); BE32(12); ms.Write(new byte[12]); BE32(0);
            BE16(rle ? 1 : 0);
            if (!rle) { var px = new byte[ch * hgt * wid]; rnd.NextBytes(px); ms.Write(px); }
            else
            {
                var rows = new List<byte[]>();
                for (int k = 0; k < ch * hgt; k++) { var row = new byte[rnd.Next(10, 60)]; rnd.NextBytes(row); rows.Add(row); BE16(row.Length); }
                foreach (var row in rows) ms.Write(row);
            }
            return ms.ToArray();
        }
        var files = new (string ext, byte[] data)[] { ("mts", Ts(192, 500)), ("ts", Ts(188, 300)), ("psd", Psd(false)), ("psd", Psd(true)) };
        var path = Path.Combine(Path.GetTempPath(), "lx-carve2.img");
        using (var f = new FileStream(path, FileMode.Create))
        {
            var noise = new byte[64 * 1024];
            foreach (var (_, data) in files)
            {
                rnd.NextBytes(noise); noise[0] = 0; f.Write(noise);              // lixo entre os arquivos
                f.Write(data);
                f.Write(new byte[(512 - f.Position % 512) % 512]);
            }
            f.Write(new byte[1 << 20]);
        }
        using var dev = new ImageFileDevice(path);
        var res = new ScanResult { Root = new RecoveredItem { Id = 0, Name = "x", IsDirectory = true } };
        Carver.Carve(dev, [(0, dev.Length)], res, new ScanOptions(), null, CancellationToken.None);
        var got = res.Items.Where(i => !i.IsDirectory).Select(i => { var b = new byte[i.Size]; i.Data!.Read(0, b); return (i.Extension, h: Convert.ToHexString(SHA256.HashData(b))); }).ToList();
        foreach (var (ext, data) in files)
            Check(got.Any(g => g.Extension == ext && g.h == Convert.ToHexString(SHA256.HashData(data))), $"{ext.ToUpperInvariant()} ({data.Length:N0} bytes) escavado com tamanho exato");
        File.Delete(path);
        return fails;
    }
}
