#!/bin/bash
# Imagens para a gravação estilo Rufus/balenaEtcher: uma imagem de disco particionada (como as de
# Raspberry Pi) em cru e compactada em todos os formatos, e três ISOs: comum, híbrida (boot de
# Linux) e com a estrutura de uma ISO de instalação do Windows (SOURCES + BOOTMGR).
set -euo pipefail
apt-get update -qq >/dev/null
DEBIAN_FRONTEND=noninteractive apt-get install -y -qq fdisk xz-utils bzip2 zstd zip genisoimage xorriso isolinux python3 >/dev/null 2>&1
mkdir -p /work/flash && cd /work/flash && rm -rf ./*
python3 - <<'PY'
import random
random.seed(7)
open("disco.img","wb").write(random.randbytes(48*1024*1024 + 12345))  # tamanho "quebrado" de propósito
PY
printf 'label: dos\nstart=2048, size=40960, type=c\nstart=43008, type=83\n' | sfdisk -q disco.img
gzip -k -9 disco.img; xz -k -T0 disco.img; bzip2 -k disco.img; zstd -q -k disco.img -o disco.img.zst
zip -q disco.img.zip disco.img
mkdir -p iso/comum iso/win/sources iso/win/boot
python3 -c "import random;random.seed(9);open('iso/comum/dados.bin','wb').write(random.randbytes(3_000_000))"
echo bootmgr > iso/win/bootmgr; python3 -c "open('iso/win/sources/install.wim','wb').write(b'MSWIM'+bytes(2_000_000))"
genisoimage -quiet -V COMUM -o comum.iso iso/comum
genisoimage -quiet -V WIN11_PTBR -o windows.iso iso/win
# híbrida como o isohybrid faz: a mesma ISO com uma tabela MBR no setor 0 (partição 0x17 cobrindo a ISO)
genisoimage -quiet -V LINUXLIVE -o hibrida.iso iso/comum
python3 - <<'PY2'
import struct, os
f = open("hibrida.iso", "r+b"); n = os.path.getsize("hibrida.iso") // 512
mbr = bytearray(512); mbr[446:462] = bytes([0x80, 0, 0, 0, 0x17, 0, 0, 0]) + struct.pack("<II", 0, n); mbr[510:512] = b"\x55\xaa"
f.write(mbr); f.close()
PY2
sha256sum disco.img | cut -d' ' -f1 > disco.sha256
chmod -R a+rwX .
ls -la
