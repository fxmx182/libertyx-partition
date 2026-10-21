#!/bin/bash
# Disco com LUKS em todos os estados que o detector precisa distinguir (roda no contêiner lx-linux-test,
# --privileged --ipc=host). Senha de todos: senha-teste.
#   p1  LUKS2 em uso                                  (1 MiB, 40 MiB)
#   p2  LUKS2 formatado por cima com mkfs.ntfs; mais um cabeçalho+chaves LUKS2 solto no espaço
#       livre do NTFS (+60 MiB)                       (41 MiB, 100 MiB)
#   p3  ext4 com um arquivo-contêiner LUKS2 dentro (cofre.img: é arquivo, não pode ser apagado)
#   livre: LUKS1 de partição apagada (190 MiB), LUKS2 com a chave zerada (215 MiB) e LUKS2 com o
#       cabeçalho principal destruído mas a reserva inteira (240 MiB)
set -euo pipefail
W=${WORK:-/work/luks}
mkdir -p "$W" && cd "$W" && rm -rf ./*.img ./*.txt ./d
M=$((1024*1024))
pw() { printf 'senha-teste'; }
fmt() { # arquivo tamanhoMiB tipo rótulo
  head -c $(($2*M)) /dev/urandom > "$1"
  if [ "$3" = luks2 ]; then pw | cryptsetup luksFormat -q --type luks2 --pbkdf pbkdf2 --pbkdf-force-iterations 1000 --label "$4" --key-file=- "$1"
  else pw | cryptsetup luksFormat -q --type luks1 --pbkdf-force-iterations 1000 --key-file=- "$1"; fi
}
abre() { pw | cryptsetup open --test-passphrase --key-file=- "$1" >/dev/null 2>&1 && echo abre || echo nao-abre; }
fmt p1.img 40 luks2 LXVIVO
fmt p2.img 100 luks2 LXNTFS
fmt solto.img 20 luks2 LXSOLTO
mkdir d && fmt d/cofre.img 20 luks2 LXARQUIVO
fmt l1.img 20 luks1 -
fmt l2.img 20 luks2 LXMORTO
fmt l3.img 20 luks2 LXRESERVA
mkfs.ntfs -Q -F -q p2.img >/dev/null 2>&1
echo "p2-ntfs $(abre p2.img)" > antes.txt
truncate -s $((40*M)) p3.img && mkfs.ext4 -q -F -E nodiscard -d d p3.img
off=$(cryptsetup luksDump --dump-json-metadata l2.img | python3 -c 'import json,sys;print(json.load(sys.stdin)["keyslots"]["0"]["area"]["offset"])')
dd if=/dev/zero of=l2.img bs=4096 seek=$((off/4096)) count=4 conv=notrunc status=none
dd if=/dev/zero of=l3.img bs=4096 count=1 conv=notrunc status=none
# numa cópia: ao abrir, o cryptsetup refaz sozinho o cabeçalho principal a partir da reserva
for f in p1 solto l1 l2 l3; do cp --sparse=always $f.img t.img; echo "$f $(abre t.img)"; done >> antes.txt; rm -f t.img
truncate -s $((300*M)) luks.img
printf 'label: gpt\nstart=2048, size=81920, type=CA7D7CCB-63ED-4C53-861C-1742536059CC, name=vivo\nstart=83968, size=204800, type=EBD0A0A2-B9E5-4433-87C0-68B6B72699C7, name=ntfs\nstart=288768, size=81920, type=0FC63DAF-8483-4772-8E79-3D69D8477DE4, name=ext4\n' | sfdisk -q luks.img
put() { dd if=$1 of=luks.img bs=$M seek=$2 ${3:+count=$3} conv=notrunc status=none; }
put p1.img 1; put p2.img 41; put solto.img 101 1; put p3.img 141; put l1.img 190; put l2.img 215; put l3.img 240
rm -rf p?.img l?.img solto.img d
cat antes.txt
