#!/bin/bash
# Roda DENTRO da VM Ubuntu (ver make-apfs.sh): o módulo apfs com escrita experimental
# grava um contêiner APFS de verdade em /dev/vdb, com arquivos e alguns apagados.
set -x
export DEBIAN_FRONTEND=noninteractive
say() { echo "LXSTEP|$*" > /dev/ttyS0; }
# o driver de escrita é experimental: cada passo tem tempo-limite e, se travar, o log do kernel vai para o console
t() { timeout -k 5 180 "$@" || { say "travou/falhou: $*"; dmesg | tail -40 | sed 's/^/LXDMESG|/' > /dev/ttyS0; }; }
say inicio
apt-get update -qq
apt-get install -y -qq apfsprogs git build-essential "linux-headers-$(uname -r)" python3 gdisk >/dev/null 2>&1 || say "apt falhou"
# o apfs-dkms do Ubuntu 24.04 dá pane (fortify_panic) no primeiro mkdir: usa o driver atual do autor
git clone -q --depth 1 https://github.com/linux-apfs/linux-apfs-rw /root/apfs-rw || say "clone falhou"
make -s -C /root/apfs-rw >/dev/null 2>&1 || say "compilação do módulo falhou"
insmod /root/apfs-rw/apfs.ko || say "insmod falhou"
say "modulo $(cat /sys/module/apfs/version 2>/dev/null)"
D=/dev/vdb
sgdisk -o -n1:2048:+400M -t1:af0a $D
partprobe $D; sleep 2
mkapfs -L LXAPFS ${D}1 || mkapfs ${D}1
mkdir -p /mnt/x
mount -t apfs -o readwrite ${D}1 /mnt/x || { say "mount falhou: $(dmesg | tail -3)"; poweroff; }
say gravando
timeout 300 python3 - <<'PY'
import os, hashlib, random
random.seed("apfs"); root = "/mnt/x"
man = open("/root/apfs-manifest.txt", "w")
def put(rel, size, record=True):
    p = os.path.join(root, rel); os.makedirs(os.path.dirname(p), exist_ok=True)
    data = random.randbytes(size)
    with open(p, "wb") as f: f.write(data)
    if record: print(f"apfs|{rel}|{hashlib.sha256(data).hexdigest()}", file=man)
put("Fotos/praia.jpg", 2_500_000)
put("Fotos/aniversário.jpg", 900_000)
put("Documentos/contrato.pdf", 70_000)
put("Documentos/notas.txt", 40)
put("Documentos/ação ñ 日本.txt", 3_000)
put("Projetos/codigo/main.py", 5_000)
for k in range(30): put(f"Muitos/arquivo{k:02d}.bin", random.randint(100, 60_000))
put("Video/clipe.mp4", 9_000_000)
for k in range(40): put(f"tmp/pedaco{k:02d}", 65536, record=False)
os.sync()
for k in range(0, 40, 2): os.remove(f"{root}/tmp/pedaco{k:02d}")
os.sync()
put("Fragmentado/grande.bin", 3_000_000)
put("Vazio/zero.txt", 0)
man.close()
PY
say gravado
t sync -f /mnt/x
say apagando
t rm /mnt/x/Fotos/praia.jpg /mnt/x/Documentos/contrato.pdf /mnt/x/Video/clipe.mp4
t rm -rf /mnt/x/Projetos
t sync -f /mnt/x
say desmontando
t umount /mnt/x
apfsck ${D}1 > /root/apfsck.txt 2>&1; echo "apfsck=$?" >> /root/apfsck.txt
sed 's/^/LXMAN|/' /root/apfs-manifest.txt > /dev/ttyS0
sed 's/^/LXFSCK|/' /root/apfsck.txt > /dev/ttyS0
say fim
sync
poweroff
