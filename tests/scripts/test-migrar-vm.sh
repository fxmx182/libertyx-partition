#!/bin/bash
# Migração de verdade numa VM descartável (KVM, isolada do servidor): um Ubuntu 24.04 (raiz ext4,
# /boot separado e partição EFI, como a imagem de nuvem vem) migra a si mesmo para um disco vazio
# com o LibertyX Partition, desliga, e a VM liga de novo SÓ com o disco novo — sem a entrada de
# boot no NVRAM (UEFI pelo EFI/BOOT de reserva) ou pelo GRUB no MBR (BIOS).
# Uso: tests/scripts/test-migrar-vm.sh <uefi|bios> <pasta-da-vm> <executável LibertyXPartition>
set -euo pipefail
MODE=${1:-uefi}
W=${2:-/DATA/Claudinho/toolchain/apfs-vm}
EXE=$(readlink -f "${3:-dist/linux/LibertyXPartition}")
cd "$W"
test -f noble.img || curl -sL -o noble.img https://cloud-images.ubuntu.com/noble/current/noble-server-cloudimg-amd64.img
rm -f mig-run.qcow2 mig-alvo.qcow2 mig-c1.log mig-c2.log mig-vars.fd mig-vars2.fd mig-app.iso
qemu-img create -q -f qcow2 -b noble.img -F qcow2 mig-run.qcow2 8G
qemu-img create -q -f qcow2 mig-alvo.qcow2 12G

mkdir -p mig-seed
cat > mig-seed/meta-data <<M
instance-id: lx-mig-$(date +%s)
local-hostname: lxmigra
M
cat > mig-seed/user-data <<'U'
#cloud-config
password: libertyx
chpasswd: { expire: false }
ssh_pwauth: false
runcmd:
  - [sh, -c, 'echo "LXSTEP|início $(date +%T)" > /dev/ttyS0']
  - [sh, -c, 'mkdir -p /mnt/app && mount -o ro /dev/sr0 /mnt/app']
  - [sh, -c, 'touch /etc/libertyx-marca-de-origem; /mnt/app/LibertyXPartition --selftest /root/m.txt --migrar /dev/vdb > /root/m.log 2>&1; echo "LXSTEP|código $?" > /dev/ttyS0']
  - [sh, -c, 'sed "s/^/LXOUT|/" /root/m.txt > /dev/ttyS0; tail -40 /root/m.log | sed "s/^/LXLOG|/" > /dev/ttyS0']
  - [poweroff]
U
# semente do cloud-init (FAT "CIDATA") e a ISO com o executável: com as ferramentas locais se houver
# (runner do CI), senão num contêiner descartável
if command -v mcopy >/dev/null && command -v genisoimage >/dev/null && command -v mkfs.vfat >/dev/null; then
  rm -f mig-seed/seed.img
  mkfs.vfat -n CIDATA -C mig-seed/seed.img 2048 >/dev/null
  mcopy -i mig-seed/seed.img mig-seed/user-data mig-seed/meta-data ::/
  rm -rf mig-iso && mkdir mig-iso && cp "$EXE" mig-iso/LibertyXPartition
  genisoimage -quiet -R -V LXAPP -o mig-app.iso mig-iso
else
  docker run --rm -v "$W:/w" -v "$EXE:/app/LibertyXPartition:ro" ubuntu:24.04 bash -c "apt-get update -qq >/dev/null && apt-get install -y -qq dosfstools mtools genisoimage >/dev/null 2>&1 && cd /w && rm -f mig-seed/seed.img && mkfs.vfat -n CIDATA -C mig-seed/seed.img 2048 >/dev/null && mcopy -i mig-seed/seed.img mig-seed/user-data mig-seed/meta-data ::/ && mkdir -p /tmp/iso && cp /app/LibertyXPartition /tmp/iso/ && genisoimage -quiet -R -V LXAPP -o mig-app.iso /tmp/iso && chmod 666 mig-seed/seed.img mig-app.iso"
fi

FW=()
if [ "$MODE" = uefi ]; then
  cp /usr/share/OVMF/OVMF_VARS_4M.fd mig-vars.fd
  FW=(-drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd -drive if=pflash,format=raw,file=mig-vars.fd)
fi

echo "== 1ª partida ($MODE): o sistema migra a si mesmo para o disco novo"
timeout 1500 qemu-system-x86_64 -enable-kvm -m 3072 -smp 4 -nographic -no-reboot "${FW[@]}" \
  -drive file=mig-run.qcow2,if=virtio -drive file=mig-alvo.qcow2,if=virtio -drive file=mig-seed/seed.img,if=virtio,format=raw \
  -cdrom mig-app.iso -nic user,model=virtio -serial file:mig-c1.log -monitor none || true
grep -a "LXSTEP|\|^LXOUT|\(OK\|FALHA\|RESULTADO\|LXMAP\)" mig-c1.log | tr -d '\r' | sed 's/^LXOUT|//'
NEWROOT=$(grep -a "LXMAP|/|" mig-c1.log | tr -d '\r' | head -1 | cut -d'|' -f5)
grep -a "RESULTADO: TUDO OK" mig-c1.log >/dev/null || { echo "FALHA a migração não terminou bem"; grep -a "^LXLOG|" mig-c1.log | tr -d '\r' | tail -30; exit 1; }

echo "== 2ª partida: só o disco novo, NVRAM limpo (UUID novo da raiz: $NEWROOT)"
FW2=()
if [ "$MODE" = uefi ]; then
  cp /usr/share/OVMF/OVMF_VARS_4M.fd mig-vars2.fd
  FW2=(-drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd -drive if=pflash,format=raw,file=mig-vars2.fd)
fi
timeout 240 qemu-system-x86_64 -enable-kvm -m 2048 -smp 2 -nographic "${FW2[@]}" \
  -drive file=mig-alvo.qcow2,if=virtio -nic user,model=virtio -serial file:mig-c2.log -monitor none || true
CMD=$(grep -a "Command line:" mig-c2.log | tr -d '\r' | head -1)
echo "kernel: $CMD"
OK=1
echo "$CMD" | grep -q "$NEWROOT" && echo "OK    o kernel subiu com a raiz do disco novo" || { echo "FALHA o kernel não usou a raiz nova"; OK=0; }
grep -aq "lxmigra login:" mig-c2.log && echo "OK    o sistema migrado chegou ao login" || { echo "FALHA não chegou ao login"; OK=0; tail -40 mig-c2.log | tr -d '\r'; }
grep -aq "Failed to mount\|emergency mode\|Dependency failed" mig-c2.log && { echo "FALHA houve erro de montagem no boot:"; grep -a "Failed to mount\|emergency\|Dependency failed" mig-c2.log | tr -d '\r' | head; OK=0; } || echo "OK    nenhum erro de montagem no boot (fstab certo)"
[ $OK = 1 ] && echo "RESULTADO: MIGRAÇÃO $MODE OK" || { echo "RESULTADO: MIGRAÇÃO $MODE COM FALHA"; exit 1; }
