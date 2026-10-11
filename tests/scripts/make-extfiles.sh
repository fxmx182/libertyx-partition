#!/bin/bash
# ext4/ext3/ext2 com arquivos, e alguns apagados — para ler a árvore e recuperar pelo journal.
set -euo pipefail
apt-get update -qq >/dev/null
DEBIAN_FRONTEND=noninteractive apt-get install -y -qq e2fsprogs gdisk util-linux python3 >/dev/null 2>&1
cd /work
rm -f extfiles.img extfiles-manifest.txt
truncate -s 480M extfiles.img
sgdisk -o -n1:2048:+220M -t1:8300 -n2:0:+130M -t2:8300 -n3:0:+100M -t3:8300 extfiles.img >/dev/null
mkdir -p /mnt/x
i=0
for fs in ext4 ext3 ext2; do
  i=$((i+1))
  read S E < <(sgdisk -i$i extfiles.img | awk '/First sector/{f=$3}/Last sector/{l=$3}END{print f, l}')
  L=$(losetup -f --show -o $((S*512)) --sizelimit $(((E-S+1)*512)) extfiles.img)
  mkfs.$fs -q -L "T_$fs" $L
  mount -t $fs $L /mnt/x
  python3 - "$fs" <<'PY'
import os, sys, hashlib, random
fs = sys.argv[1]; random.seed(fs)
root = "/mnt/x"
def put(rel, size):
    p = os.path.join(root, rel); os.makedirs(os.path.dirname(p), exist_ok=True)
    data = random.randbytes(size)
    open(p, "wb").write(data)
    print(f"{fs}|{rel}|{hashlib.sha256(data).hexdigest()}", file=open("/work/extfiles-manifest.txt", "a"))
put("fotos/praia.jpg", 2_500_000)
put("fotos/aniversario.jpg", 900_000)
put("docs/contrato.pdf", 70_000)
put("docs/notas.txt", 40)
put("projetos/codigo/main.py", 5_000)
for k in range(30): put(f"muitos/arquivo{k:02d}.bin", random.randint(100, 60_000))
put("video/clipe.mp4", 9_000_000)
PY
  sync; sleep 1
  # apagados: arquivos soltos e uma pasta inteira
  rm /mnt/x/fotos/praia.jpg /mnt/x/docs/contrato.pdf /mnt/x/video/clipe.mp4
  rm -rf /mnt/x/projetos
  sync; sleep 1
  umount /mnt/x
  losetup -d $L
done
chmod 666 extfiles.img extfiles-manifest.txt
wc -l extfiles-manifest.txt
