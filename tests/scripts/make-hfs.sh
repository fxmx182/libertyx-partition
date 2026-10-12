#!/bin/bash
# HFS+ e HFSX (sensível a maiúsculas) gravados pelo driver hfsplus do Linux, com arquivos,
# um arquivo fragmentado (mais de 8 pedaços, vai para o arquivo de extents de reserva),
# nomes acentuados, link simbólico e alguns apagados.
set -euo pipefail
apt-get update -qq >/dev/null
DEBIAN_FRONTEND=noninteractive apt-get install -y -qq hfsprogs gdisk util-linux python3 >/dev/null 2>&1
cd /work
rm -f hfs.img hfs-manifest.txt
truncate -s 300M hfs.img
sgdisk -o -n1:2048:+150M -t1:af00 -n2:0:+120M -t2:af00 hfs.img >/dev/null
mkdir -p /mnt/x
i=0
for fs in hfsplus hfsx; do
  i=$((i+1))
  read S E < <(sgdisk -i$i hfs.img | awk '/First sector/{f=$3}/Last sector/{l=$3}END{print f, l}')
  L=$(losetup -f --show -o $((S*512)) --sizelimit $(((E-S+1)*512)) hfs.img)
  if [ $fs = hfsx ]; then mkfs.hfsplus -s -v "Mac_$fs" $L >/dev/null; else mkfs.hfsplus -v "Mac_$fs" $L >/dev/null; fi
  mount -t hfsplus $L /mnt/x
  python3 - "$fs" <<'PY'
import os, sys, hashlib, random
fs = sys.argv[1]; random.seed(fs)
root = "/mnt/x"
man = open("/work/hfs-manifest.txt", "a")
def put(rel, size, record=True):
    p = os.path.join(root, rel); os.makedirs(os.path.dirname(p), exist_ok=True)
    data = random.randbytes(size)
    with open(p, "wb") as f: f.write(data)
    if record: print(f"{fs}|{rel}|{hashlib.sha256(data).hexdigest()}", file=man)
put("Fotos/praia.jpg", 2_500_000)
put("Fotos/aniversário.jpg", 900_000)
put("Documentos/contrato.pdf", 70_000)
put("Documentos/notas.txt", 40)
put("Documentos/ação ñ 日本.txt", 3_000)
put("Projetos/codigo/main.py", 5_000)
for k in range(30): put(f"Muitos/arquivo{k:02d}.bin", random.randint(100, 60_000))
put("Video/clipe.mp4", 9_000_000)
# fragmentado: enche de pedaços, apaga um sim um não e grava um arquivo grande nos buracos
for k in range(40): put(f"tmp/pedaco{k:02d}", 65536, record=False)
os.sync()
for k in range(0, 40, 2): os.remove(f"{root}/tmp/pedaco{k:02d}")
os.sync()
put("Fragmentado/grande.bin", 3_000_000)
os.symlink("../Documentos/notas.txt", f"{root}/Fotos/atalho")
PY
  sync; sleep 1
  rm /mnt/x/Fotos/praia.jpg /mnt/x/Documentos/contrato.pdf /mnt/x/Video/clipe.mp4
  rm -rf /mnt/x/Projetos
  sync; sleep 1
  umount /mnt/x
  losetup -d $L
done
chmod 666 hfs.img hfs-manifest.txt
wc -l hfs-manifest.txt
