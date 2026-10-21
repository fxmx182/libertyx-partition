using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using static LibertyX.Partition.Core.Platform.Linux.LinuxNative;

namespace LibertyX.Partition.Core.Platform.Linux;

/// <summary>
/// S.M.A.R.T. no Linux sem programa externo: NVMe pelo comando de administração (log de saúde,
/// página 02h) e SATA/USB pelo SG_IO com ATA PASS-THROUGH (16) — o mesmo caminho do smartctl.
/// Adaptador USB que não repassa comandos ATA: tenta o smartctl, se estiver instalado.
/// </summary>
[SupportedOSPlatform("linux")]
public static unsafe class LinuxSmart
{
    public static SmartReport Read(DiskInfo d)
    {
        try
        {
            if (d.Bus == "NVMe" && Nvme(d.DevicePath) is { } log) return SmartParser.ParseNvme(log);
            if (d.Bus is not ("NVMe" or "SD" or "MMC" or "Virtio" or "Loop"))
            {
                var vals = Ata(d.DevicePath, 0xD0);
                if (vals != null) return SmartParser.ParseAta(vals, Ata(d.DevicePath, 0xD1));
            }
        }
        catch { }
        if (LinuxTools.Has("smartctl") && SmartCtl(d.DevicePath) is { } r) return r;
        bool virtualDisk = d.Bus == "Virtio" || System.Text.RegularExpressions.Regex.IsMatch(d.Model, @"Virtual|QEMU|VMware|VBOX|Xen|Msft", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return d.Bus switch
        {
            "SD" or "MMC" => new SmartReport { Summary = "Cartões SD e eMMC não têm S.M.A.R.T. — o teste de superfície é o jeito de avaliar." },
            _ when virtualDisk => new SmartReport { Summary = "Disco virtual: a saúde é a do disco físico do hospedeiro." },
            "USB" => new SmartReport { Summary = "Este adaptador USB não repassa o S.M.A.R.T. (instale o smartmontools: ele conhece mais pontes USB)." },
            _ => new SmartReport(),
        };
    }

    private static byte[]? Nvme(string dev)
    {
        int fd = Open(dev, O_RDONLY | O_CLOEXEC | O_NONBLOCK);
        if (fd < 0) return null;
        byte* buf = (byte*)NativeMemory.AlignedAlloc(4096, 4096);
        try
        {
            new Span<byte>(buf, 4096).Clear();
            var cmd = new NvmeAdminCmd
            {
                Opcode = 0x02, Nsid = 0xFFFFFFFF, Addr = (ulong)buf, DataLen = 512,
                Cdw10 = (127u << 16) | 0x02,   // NUMD = 128 dwords − 1, página 02h (SMART / saúde)
                TimeoutMs = 5000,
            };
            if (Ioctl(fd, NVME_IOCTL_ADMIN_CMD, &cmd) != 0) return null;
            return new Span<byte>(buf, 512).ToArray();
        }
        finally { NativeMemory.AlignedFree(buf); Close(fd); }
    }

    /// <summary>SMART READ DATA (D0h) ou READ THRESHOLDS (D1h) por ATA PASS-THROUGH (16).</summary>
    private static byte[]? Ata(string dev, byte feature)
    {
        int fd = Open(dev, O_RDONLY | O_CLOEXEC | O_NONBLOCK);
        if (fd < 0) return null;
        byte* buf = (byte*)NativeMemory.AlignedAlloc(512, 512);
        byte* cdb = stackalloc byte[16];
        byte* sense = stackalloc byte[32];
        try
        {
            new Span<byte>(buf, 512).Clear();
            new Span<byte>(cdb, 16).Clear();
            new Span<byte>(sense, 32).Clear();
            cdb[0] = 0x85;          // ATA PASS-THROUGH (16)
            cdb[1] = 4 << 1;        // protocolo PIO data-in
            cdb[2] = 0x0E;          // T_DIR=do disco, BYT_BLOK=1, T_LENGTH=contagem de setores
            cdb[4] = feature;
            cdb[6] = 1;             // 1 setor
            cdb[10] = 0x4F; cdb[12] = 0xC2;   // assinatura SMART em LBA mid/high
            cdb[14] = 0xB0;         // comando SMART
            var h = new SgIoHdr
            {
                InterfaceId = 'S', DxferDirection = -3, CmdLen = 16, MxSbLen = 32, DxferLen = 512,
                Dxferp = buf, Cmdp = cdb, Sbp = sense, Timeout = 5000,
            };
            if (Ioctl(fd, SG_IO, &h) != 0) return null;
            if (h.HostStatus != 0 || (h.DriverStatus & 0x0F) is not (0 or 8) || h.Status is not (0 or 2)) return null;
            if (h.Status == 2 && h.SbLenWr > 0 && (sense[1] & 0x0F) is not (0 or 1)) return null;   // CHECK CONDITION de verdade
            var data = new Span<byte>(buf, 512).ToArray();
            // o setor de dados SMART fecha com soma zero; adaptador que finge responder devolve lixo ou zeros
            byte sum = 0;
            foreach (var b in data) sum += b;
            if (sum != 0 || data.Skip(2).Take(360).All(b => b == 0)) return null;
            return data;
        }
        finally { NativeMemory.AlignedFree(buf); Close(fd); }
    }

    /// <summary>smartctl -j: cobre pontes USB com comando próprio (JMicron, Realtek, Cypress…).</summary>
    private static SmartReport? SmartCtl(string dev)
    {
        var r = Proc.RunSync("smartctl", ["-j", "-a", dev], TimeSpan.FromSeconds(20));
        if (r.Output.Length == 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(r.Output);
            var root = doc.RootElement;
            if (root.TryGetProperty("nvme_smart_health_information_log", out _))
                return null;   // NVMe já foi tentado direto; o caminho do smartctl só complica
            if (!root.TryGetProperty("ata_smart_attributes", out var at) || !at.TryGetProperty("table", out var table)) return null;
            var vals = new byte[512];
            var thr = new byte[512];
            int i = 0;
            foreach (var a in table.EnumerateArray())
            {
                if (i >= 30) break;
                int o = 2 + i * 12;
                byte id = (byte)a.GetProperty("id").GetInt32();
                vals[o] = id; thr[o] = id;
                vals[o + 3] = (byte)a.GetProperty("value").GetInt32();
                vals[o + 4] = (byte)a.GetProperty("worst").GetInt32();
                thr[o + 1] = (byte)a.GetProperty("thresh").GetInt32();
                long raw = a.GetProperty("raw").GetProperty("value").GetInt64();
                for (int b = 0; b < 6; b++) vals[o + 5 + b] = (byte)(raw >> (8 * b));
                i++;
            }
            var rep = SmartParser.ParseAta(vals, thr);
            rep.Source = "ATA S.M.A.R.T. (smartctl)";
            if (root.TryGetProperty("smart_status", out var st) && st.TryGetProperty("passed", out var passed) && !passed.GetBoolean())
            {
                rep.Health = HealthLevel.Bad;
                rep.Summary = "O próprio disco reporta falha (S.M.A.R.T. reprovado). Faça uma imagem dele agora.";
            }
            return rep;
        }
        catch { return null; }
    }
}
