#!/bin/bash
# APFS de teste: não há mkfs+escrita de APFS no Linux comum, então uma VM Ubuntu 24.04
# (KVM, isolada do servidor) instala o módulo apfs-dkms e grava tests/images/apfs.img.
# Uso: tests/scripts/make-apfs.sh <pasta-de-trabalho-da-vm>
set -euo pipefail
W=${1:-/DATA/Claudinho/toolchain/apfs-vm}
HERE=$(cd "$(dirname "$0")" && pwd)
IMG=$HERE/../images
cd "$W"
test -f noble.img || curl -sL -o noble.img https://cloud-images.ubuntu.com/noble/current/noble-server-cloudimg-amd64.img
rm -f run.qcow2 console.log apfs.img
qemu-img create -q -f qcow2 -b noble.img -F qcow2 run.qcow2 10G
truncate -s 480M apfs.img
mkdir -p seed
cat > seed/meta-data <<M
instance-id: lx-apfs-$(date +%s)
local-hostname: lxapfs
M
{
  echo "#cloud-config"
  echo "write_files:"
  echo "  - path: /root/guest.sh"
  echo "    permissions: '0755'"
  echo "    encoding: b64"
  echo "    content: $(base64 -w0 "$HERE/make-apfs-guest.sh")"
  echo "runcmd:"
  echo "  - [bash, /root/guest.sh]"
} > seed/user-data
docker run --rm -v "$W/seed:/s" ubuntu:24.04 bash -c "apt-get update -qq >/dev/null && apt-get install -y -qq dosfstools mtools >/dev/null 2>&1 && rm -f /s/seed.img && mkfs.vfat -n CIDATA -C /s/seed.img 2048 >/dev/null && mcopy -i /s/seed.img /s/user-data /s/meta-data ::/ && chmod 666 /s/seed.img"
timeout 1800 qemu-system-x86_64 -enable-kvm -m 4096 -smp 4 -nographic -no-reboot \
  -drive file=run.qcow2,if=virtio -drive file=apfs.img,if=virtio,format=raw -drive file=seed/seed.img,if=virtio,format=raw \
  -nic user,model=virtio -serial file:console.log -monitor none || true
grep -a "LXSTEP|" console.log | tr -d '\r'
grep -a "^LXMAN|" console.log | tr -d '\r' | sed 's/^LXMAN|//' > "$IMG/apfs-manifest.txt"
grep -a "^LXFSCK|" console.log | tr -d '\r' | sed 's/^LXFSCK|//'
cp apfs.img "$IMG/apfs.img"
wc -l "$IMG/apfs-manifest.txt"
