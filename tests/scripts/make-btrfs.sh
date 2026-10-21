#!/bin/bash
# Btrfs de teste: subvolume, compressão zstd, lzo e zlib, arquivos embutidos na árvore (inline),
# esparso e fragmentado; depois apaga uma parte. Roda como root (contêiner privilegiado ou CI).
set -euo pipefail
WORK=${WORK:-/work}
mkdir -p "$WORK"
cd "$WORK"
rm -f btrfs.img btrfs-manifest.txt btrfs-apagados.txt
truncate -s 400M btrfs.img
L=$(losetup -f --show btrfs.img)
trap 'umount /mnt/b 2>/dev/null || true; losetup -d $L' EXIT
mkfs.btrfs -q -f -L LXBTRFS $L
mkdir -p /mnt/b
mount -o compress=zstd $L /mnt/b
btrfs subvolume create /mnt/b/Sub >/dev/null
cd /mnt/b
python3 - <<'PY'
import os, random
random.seed(7)
def put(path, data):
    os.makedirs(os.path.dirname(path) or ".", exist_ok=True)
    with open(path, "wb") as f: f.write(data)
texto = ("LibertyX recupera arquivos do Btrfs. " * 4000).encode()
put("Zstd/texto.txt", texto)                       # comprimido com zstd
put("Zstd/texto-vivo.txt", texto[::-1])
put("Zstd/aleatorio.bin", random.randbytes(700_000))   # incompressível: fica cru
put("pequeno.txt", b"cabe dentro da arvore")          # inline
put("Fotos/foto1.bin", random.randbytes(900_000))
put("Fotos/foto2.bin", random.randbytes(60_000))
put("Docs/Antigos/contrato.txt", ("clausula " * 20000).encode())
put("Docs/Antigos/planilha.bin", random.randbytes(120_000))
put("Sub/no-subvolume.bin", random.randbytes(300_000))
for i in range(120):
    put(f"Muitos/item_{i:03d}.dat", random.randbytes(random.randint(50, 6000)))
with open("esparso.bin", "wb") as f:
    f.seek(3_000_000); f.write(b"fim")
PY
sync
mount -o remount,compress=lzo /mnt/b
python3 -c "open('/mnt/b/lzo.txt','wb').write(('lzo comprime rapido ' * 9000).encode()); open('/mnt/b/lzo-vivo.txt','wb').write(('lzo continua vivo ' * 30000).encode())"
sync
mount -o remount,compress=zlib /mnt/b
python3 -c "open('/mnt/b/zlib.txt','wb').write(('zlib comprime bem ' * 9000).encode())"
sync
find . -type f -print0 | sort -z | xargs -0 sha256sum > $WORK/btrfs-manifest.txt
btrfs filesystem sync /mnt/b
for f in Fotos/foto1.bin Zstd/texto.txt lzo.txt Docs/Antigos/contrato.txt Docs/Antigos/planilha.bin $(ls Muitos | head -40 | sed 's#^#Muitos/#'); do grep -F "  ./$f" $WORK/btrfs-manifest.txt >> $WORK/btrfs-apagados.txt; done
rm -f Fotos/foto1.bin Zstd/texto.txt lzo.txt; rm -rf Docs/Antigos; ls Muitos | head -40 | sed 's#^#Muitos/#' | xargs rm -f
sync; btrfs filesystem sync /mnt/b
cd /; umount /mnt/b
wc -l $WORK/btrfs-manifest.txt $WORK/btrfs-apagados.txt
chmod a+rw $WORK/btrfs*
