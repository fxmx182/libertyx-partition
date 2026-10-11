using System.Security.Cryptography;
using LibertyX.Partition.Core.FileSystems;
using LibertyX.Partition.Core.IO;
using LibertyX.Partition.Core.Partitioning;
using LibertyX.Partition.Core.Platform;
using LibertyX.Partition.Core.Recovery;

string dir = args.Length > 0 ? args[0] : "../images";
var manifest = File.ReadAllLines(Path.Combine(dir, "manifest.txt")).Where(l => l.Length > 66 && l[64] == ' ')
    .ToDictionary(l => l[66..].Trim(), l => l[..64]);
int fails = 0;
void Check(bool ok, string what) { Console.WriteLine((ok ? "  OK   " : "  FALHA ") + what); if (!ok) fails++; }

using var disk = new ImageFileDevice(Path.Combine(dir, "disk.img"));
var t = PartitionCodec.Read(disk);
Console.WriteLine($"Tabela: {t.Style}, {t.Entries.Count} partições; avisos: {string.Join(" | ", t.Warnings)}");
Check(t.Style == TableStyle.Gpt && t.Entries.Count == 3, "GPT com 3 partições");
foreach (var e in t.Entries) Console.WriteLine($"  #{e.Slot} {e.StartLba}-{e.EndLba} {PartitionTypes.GptName(e.TypeGuid)} '{e.Name}'");

// regrava a tabela em uma cópia e confere que é lida igual
var copyPath = Path.Combine(Path.GetTempPath(), "lx-copy.img");
File.Copy(Path.Combine(dir, "disk.img"), copyPath, true);
using (var cp = new ImageFileDevice(copyPath, true))
{
    var t2 = t.Clone(); t2.Entries.ForEach(e => e.Slot = 0);
    PartitionCodec.Write(cp, t2);
    var t3 = PartitionCodec.Read(cp);
    Check(t3.Warnings.Count == 0 && t3.Entries.Count == 3 && t3.Entries.Zip(t.Entries).All(z => z.First.StartLba == z.Second.StartLba && z.First.TypeGuid == z.Second.TypeGuid && z.First.Name == z.Second.Name), "GPT regravada e relida sem avisos");
    // GPT -> MBR -> GPT
    var m = new PartitionTable { Style = TableStyle.Mbr, SectorSize = 512, DiskSectors = t.DiskSectors };
    foreach (var e in t.Entries) m.Entries.Add(new PartitionEntry { StartLba = e.StartLba, SectorCount = e.SectorCount, MbrType = PartitionTypes.GptToMbr(e.TypeGuid, FsKind.Ntfs) ?? 7 });
    PartitionCodec.Write(cp, m);
    var mr = PartitionCodec.Read(cp);
    Check(mr.Style == TableStyle.Mbr && mr.Entries.Count == 3, "MBR escrita e lida");
    // lógicas
    var ml = new PartitionTable { Style = TableStyle.Mbr, SectorSize = 512, DiskSectors = t.DiskSectors };
    ml.Entries.Add(new PartitionEntry { StartLba = 2048, SectorCount = 100000, MbrType = 7 });
    ml.Entries.Add(new PartitionEntry { StartLba = 200000, SectorCount = 50000, MbrType = 7, IsLogical = true });
    ml.Entries.Add(new PartitionEntry { StartLba = 260000, SectorCount = 50000, MbrType = 0x0C, IsLogical = true });
    PartitionCodec.Write(cp, ml);
    var mlr = PartitionCodec.Read(cp);
    Check(mlr.Entries.Count == 3 && mlr.Entries.Count(e => e.IsLogical) == 2 && mlr.Entries.Any(e => e.StartLba == 260000 && e.MbrType == 0x0C), "MBR com partições lógicas (EBR)");
}
File.Delete(copyPath);

var info = new DiskInfo { Number = 9, IsImage = true, ImagePath = "x", Size = disk.Length };
LayoutBuilder.Build(info, disk);
foreach (var p in info.Partitions)
{
    var u = p.IsUnallocated ? null : UsageCalculator.Compute(disk.Slice(p.Offset, p.Length), p.FileSystem);
    Console.WriteLine($"  {p.DisplayName,-12} {p.Length / 1048576,6} MiB {FsDetect.Name(p.FileSystem),-10} {p.TypeText,-40} usado={u?.used / 1024}K livre={u?.free / 1024}K rótulo={u?.label}");
}
Check(info.Partitions.Count(p => !p.IsUnallocated) == 3 && info.Partitions[0].FileSystem == FsKind.Fat32 && info.Partitions[1].FileSystem == FsKind.Ntfs && info.Partitions[2].FileSystem == FsKind.ExFat, "sistemas de arquivos detectados");

string outDir = Path.Combine(Path.GetTempPath(), "lx-recover");
if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
var recovered = new Dictionary<string, string>();
foreach (var p in info.Partitions.Where(p => !p.IsUnallocated))
{
    var vol = disk.Slice(p.Offset, p.Length);
    var res = FileSystemScanner.Scan(vol, new ScanOptions(), null, CancellationToken.None);
    Console.WriteLine($"\n[{res.FileSystem} '{res.Label}'] log: {string.Join(" | ", res.Log)}");
    foreach (var it in res.Items)
        Console.WriteLine($"  {(it.IsDirectory ? "D" : "F")} {it.Origin,-8} {it.Condition,-11} {it.Size,9} {it.FullPath} {(it.Note != null ? "(" + it.Note + ")" : "")}");
    var files = res.Items.Where(i => !i.IsDirectory && i.Origin != ItemOrigin.Live).ToList();
    var rep = RecoveryExporter.Export(files, Path.Combine(outDir, res.FileSystem), true, null, CancellationToken.None);
    foreach (var f in Directory.GetFiles(Path.Combine(outDir, res.FileSystem), "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".csv")))
        recovered[Path.GetFileName(f)] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f)));
}
Console.WriteLine();
foreach (var name in new[] { "ferias2.jpg", "contrato.pdf", "notas.txt", "big.bin", "mensagens.sqlite", "texto_comprimido.txt", "foto_fat.jpg", "Documento com nome longo de verdade.pdf", "logo.png", "backup.zip" })
    Check(recovered.TryGetValue(name, out var h) && h == manifest[name], $"recuperado com hash idêntico: {name}");

// partições perdidas
using var lost = new ImageFileDevice(Path.Combine(dir, "lost.img"));
Check(PartitionCodec.Read(lost).Style == TableStyle.None, "lost.img sem tabela");
foreach (var deep in new[] { false, true })
{
    var found = PartitionFinder.Scan(lost, deep, null, CancellationToken.None);
    Console.WriteLine($"\nBusca {(deep ? "profunda" : "rápida")}: {found.Count} partições");
    foreach (var f in found) Console.WriteLine($"  {f.StartLba}-{f.EndLba} {FsDetect.Name(f.FileSystem)} '{f.Label}' verificada={f.Verified} esp={f.LooksLikeEsp} ({f.Evidence})");
    Check(found.Count == 3 && found.Select(f => f.StartLba).SequenceEqual(t.Entries.OrderBy(e => e.StartLba).Select(e => e.StartLba))
          && found.Zip(t.Entries.OrderBy(e => e.StartLba)).All(z => z.First.EndLba <= z.Second.EndLba && z.First.EndLba >= z.Second.EndLba - 2048), $"busca {(deep ? "profunda" : "rápida")} acha as 3 partições com limites certos");
}

// varredura profunda (assinaturas) no NTFS, só espaço livre
{
    var p = info.Partitions.First(p => p.FileSystem == FsKind.Ntfs);
    var vol = disk.Slice(p.Offset, p.Length);
    var res = FileSystemScanner.Scan(vol, new ScanOptions(), null, CancellationToken.None);
    var ranges = res.Allocation!.FreeRanges(vol.Length).ToList();
    Carver.Carve(vol, ranges, res, new ScanOptions(), null, CancellationToken.None);
    var carved = res.Items.Where(i => i.Origin == ItemOrigin.Carved && !i.IsDirectory).ToList();
    foreach (var c in carved) Console.WriteLine($"  escavado {c.Name} {c.Size}");
    var hashes = carved.Select(c => { var b = new byte[c.Size]; c.Data!.Read(0, b); return Convert.ToHexStringLower(SHA256.HashData(b)); }).ToHashSet();
    foreach (var n in new[] { "ferias2.jpg", "contrato.pdf", "mensagens.sqlite" })
        Check(hashes.Contains(manifest[n]), $"escavado por assinatura com hash idêntico: {n}");
}
fails += await Ops.Run(dir);
fails += ExtTests.Run(dir);
fails += DiskScanTests.Run(dir);
fails += FormattedTests.Run(dir);
Console.WriteLine(fails == 0 ? "\nTUDO OK" : $"\n{fails} FALHA(S)");
return fails;
