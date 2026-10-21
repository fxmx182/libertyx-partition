#!/bin/bash
# XFS de teste: árvore com pasta pequena (shortform), pasta grande (blocos de diretório), arquivo
# fragmentado em árvore B+ (mais de 21 extents), esparso e link; depois apaga uma parte.
# Roda num contêiner privilegiado com xfsprogs: docker run --privileged -v /dev:/dev -v <images>:/work ...
set -euo pipefail
WORK=${WORK:-/work}
mkdir -p "$WORK"
cd "$WORK"
rm -f xfs.img xfs-manifest.txt xfs-apagados.txt
truncate -s 320M xfs.img
L=$(losetup -f --show xfs.img)
trap 'umount /mnt/x 2>/dev/null || true; losetup -d $L' EXIT
mkfs.xfs -q -f -L LXXFS $L
mkdir -p /mnt/x && mount $L /mnt/x
cd /mnt/x
python3 - <<'PY'
import os, random, hashlib
random.seed(42)
def put(path, data):
    os.makedirs(os.path.dirname(path) or ".", exist_ok=True)
    with open(path, "wb") as f: f.write(data)
put("Fotos/foto1.bin", random.randbytes(1_500_000))
put("Fotos/foto2.bin", random.randbytes(80_000))
put("Fotos/foto3.bin", random.randbytes(3_000_000))
put("Docs/leia-me.txt", b"LibertyX XFS\n" * 100)
put("Docs/Antigos/contrato.bin", random.randbytes(250_000))
put("Docs/Antigos/planilha.bin", random.randbytes(40_000))
put("Docs/Antigos/minúsculo.txt", b"pequeno")
for i in range(300):
    put(f"Muitos/arquivo_{i:03d}.dat", random.randbytes(random.randint(100, 9000)))
put("vazio.txt", b"")
with open("esparso.bin", "wb") as f:
    f.seek(5_000_000); f.write(b"fim do esparso")
os.symlink("Docs/leia-me.txt", "atalho")
# fragmentado: dois arquivos crescendo intercalados, com fsync, viram dezenas de extents
a = open("fragmentado.bin", "wb"); b = open("intercalado.bin", "wb")
fa = bytearray(); fb = bytearray()
for i in range(60):
    x = random.randbytes(8192); y = random.randbytes(8192)
    a.write(x); fa += x; a.flush(); os.fsync(a.fileno())
    b.write(y); fb += y; b.flush(); os.fsync(b.fileno())
a.close(); b.close()
# buracos alternados: dezenas de extents, garfo de dados em árvore B+ (formato 3)
import subprocess
for name in ("fragmentado.bin", "intercalado.bin"):
    for k in range(0, 60 * 8192, 8192):
        subprocess.run(["fallocate", "-p", "-o", str(k), "-l", "4096", name], check=True)
PY
sync
xfs_bmap -v fragmentado.bin | tail -n +3 | wc -l | sed 's/^/extents do fragmentado: /'
xfs_bmap fragmentado.bin | tail -n +2 | grep -vc hole | sed 's/^/extents com dados do fragmentado: /'
find . -type f -print0 | sort -z | xargs -0 sha256sum > $WORK/xfs-manifest.txt
# apaga: um arquivo solto, uma pasta inteira, 50 da pasta grande e o fragmentado
for f in Fotos/foto1.bin Docs/Antigos/contrato.bin Docs/Antigos/planilha.bin Docs/Antigos/minúsculo.txt fragmentado.bin $(ls Muitos | head -50 | sed 's#^#Muitos/#'); do grep -F "  ./$f" $WORK/xfs-manifest.txt >> $WORK/xfs-apagados.txt; done
ino=$(stat -c %i Fotos/foto1.bin)
rm -f Fotos/foto1.bin fragmentado.bin; rm -rf Docs/Antigos; ls Muitos | head -50 | sed 's#^#Muitos/#' | xargs rm -f
sync; cd /; umount /mnt/x
echo "inode apagado de foto1: $ino"
xfs_db -r -c "inode $ino" -c "p core.mode core.format core.size core.nextents" -c "p u3" $L 2>&1 | head -12
wc -l $WORK/xfs-manifest.txt $WORK/xfs-apagados.txt
chmod a+rw $WORK/xfs*
