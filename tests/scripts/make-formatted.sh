#!/bin/bash
# NTFS cheio de pastas e arquivos, depois formatado rapidamente por cima (com outro tamanho de cluster).
set -euo pipefail
apt-get update -qq >/dev/null
DEBIAN_FRONTEND=noninteractive apt-get install -y -qq ntfs-3g gdisk util-linux python3 >/dev/null 2>&1
cd /work
rm -f formatted.img formatted-manifest.txt
truncate -s 300M formatted.img
sgdisk -o -n1:2048:0 -t1:0700 formatted.img >/dev/null
read S E < <(sgdisk -i1 formatted.img | awk '/First sector/{f=$3}/Last sector/{l=$3}END{print f, l}')
L=$(losetup -f --show -o $((S*512)) --sizelimit $(((E-S+1)*512)) formatted.img)
mkfs.ntfs -Q -c 4096 -L ANTIGO $L >/dev/null 2>&1
mkdir -p /mnt/n && ntfs-3g $L /mnt/n
python3 - <<'PY'
import os, random, hashlib
random.seed(42)
for d in ["Fotos/2025/Viagem", "Fotos/2026", "Documentos/Trabalho", "Musicas"]:
    os.makedirs("/mnt/n/" + d, exist_ok=True)
files = [f"Fotos/2025/Viagem/img{k:03d}.jpg" for k in range(25)] + [f"Fotos/2026/foto{k}.jpg" for k in range(10)] + \
        [f"Documentos/Trabalho/relatorio{k}.docx" for k in range(8)] + [f"Musicas/faixa{k}.mp3" for k in range(6)] + ["leia-me.txt"]
with open("/work/formatted-manifest.txt", "w") as m:
    for f in files:
        data = random.randbytes(random.choice([300, 5000, 80_000, 400_000, 1_200_000]))
        open("/mnt/n/" + f, "wb").write(data)
        m.write(f"{f}|{hashlib.sha256(data).hexdigest()}\n")
PY
sync; umount /mnt/n
mkfs.ntfs -Q -c 8192 -L NOVO $L >/dev/null 2>&1
losetup -d $L
chmod 666 formatted.img formatted-manifest.txt
wc -l formatted-manifest.txt
