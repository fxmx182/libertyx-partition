#!/bin/bash
# Disco de teste com ext4 (+ ext3 e ext2) para a busca de partições; depois a tabela é destruída.
set -euo pipefail
apt-get update -qq >/dev/null
DEBIAN_FRONTEND=noninteractive apt-get install -y -qq e2fsprogs gdisk util-linux fdisk >/dev/null 2>&1
cd /work
rm -f ext.img ext-lost.img extmbr-lost.img
truncate -s 600M ext.img
sgdisk -o -n1:2048:+300M -t1:8300 -c1:"raiz" -n2:0:+150M -t2:8300 -n3:0:+100M -t3:8300 ext.img >/dev/null
for i in 1 2 3; do
  read S E < <(sgdisk -i$i ext.img | awk '/First sector/{f=$3}/Last sector/{l=$3}END{print f, l}')
  L=$(losetup -f --show -o $((S*512)) --sizelimit $(((E-S+1)*512)) ext.img)
  case $i in 1) mkfs.ext4 -q -L LINUXRAIZ $L ;; 2) mkfs.ext3 -q -L DADOS3 $L ;; 3) mkfs.ext2 -q -b 1024 -L VELHO2 $L ;; esac
  losetup -d $L
  echo "P$i=$S-$E" >> ext-manifest.txt
done
cp ext.img ext-lost.img
dd if=/dev/zero of=ext-lost.img bs=512 count=34 conv=notrunc status=none
SZ=$(stat -c %s ext-lost.img)
dd if=/dev/zero of=ext-lost.img bs=512 seek=$((SZ/512-33)) count=33 conv=notrunc status=none
# variante MBR (ext4 de 64 bits, blocos de 4K) com a tabela zerada
truncate -s 400M extmbr.img
printf 'o\nn\np\n1\n2048\n+250M\nt\n83\nw\n' | fdisk extmbr.img >/dev/null 2>&1
L=$(losetup -f --show -o $((2048*512)) --sizelimit $((250*1024*1024)) extmbr.img)
mkfs.ext4 -q -O 64bit -L MBR64 $L
losetup -d $L
cp extmbr.img extmbr-lost.img
dd if=/dev/zero of=extmbr-lost.img bs=512 count=1 conv=notrunc status=none
rm extmbr.img
chmod 666 /work/ext*.img /work/ext-manifest.txt
cat ext-manifest.txt
