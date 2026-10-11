#!/bin/bash
# MBR com 3 primárias + estendida com 4 lógicas (NTFS, FAT32, ext4, HFS+, swap, exFAT, ext3); depois o MBR é zerado.
set -euo pipefail
apt-get update -qq >/dev/null
DEBIAN_FRONTEND=noninteractive apt-get install -y -qq ntfs-3g dosfstools exfatprogs e2fsprogs fdisk util-linux python3 >/dev/null 2>&1
cd /work
rm -f mixed.img mixed-lost.img mixed-manifest.txt
truncate -s 520M mixed.img
fdisk mixed.img >/dev/null 2>&1 <<'F'
o
n
p
1
2048
+60M
n
p
2

+60M
n
p
3

+60M
n
e


n

+60M
n

+50M
n

+60M
n

+60M
w
F
sfdisk -d mixed.img | grep -v 'type=5\b' | grep -E '^mixed' | awk '{print $1, $4, $6}' | tr -d ',' > /tmp/parts
i=0
for fs in ntfs vfat ext4 hfsplus swap exfat ext3; do
  i=$((i+1)); n=$i; [ $i -ge 4 ] && n=$((i+1))
  line=$(sfdisk -d mixed.img | grep "^mixed.img$n ")
  S=$(echo "$line" | sed -E 's/.*start= *([0-9]+).*/\1/'); Z=$(echo "$line" | sed -E 's/.*size= *([0-9]+).*/\1/')
  L=$(losetup -f --show -o $((S*512)) --sizelimit $((Z*512)) mixed.img)
  case $fs in
    ntfs) mkfs.ntfs -Q -L M_NTFS $L >/dev/null 2>&1 ;;
    vfat) mkfs.vfat -F 32 -n M_FAT $L >/dev/null ;;
    ext4) mkfs.ext4 -q -L M_EXT4 $L ;;
    hfsplus) python3 /scripts/hfshdr.py $L $((Z*512)) ;;
    swap) mkswap -L M_SWAP $L >/dev/null ;;
    exfat) mkfs.exfat -n M_EXFAT $L >/dev/null ;;
    ext3) mkfs.ext3 -q -L M_EXT3 $L ;;
  esac
  losetup -d $L
  echo "$fs=$S-$((S+Z-1))" >> mixed-manifest.txt
done
cp mixed.img mixed-lost.img
dd if=/dev/zero of=mixed-lost.img bs=512 count=1 conv=notrunc status=none
rm mixed.img
chmod 666 mixed-lost.img mixed-manifest.txt
cat mixed-manifest.txt
