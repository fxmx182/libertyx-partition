#!/bin/bash
# Empacota o LibertyX Partition para Linux a partir do executável único já publicado:
#   .tar.gz (pasta solta), .deb (Debian/Ubuntu/Mint), .rpm (Fedora/openSUSE/RHEL) e AppImage (qualquer um).
# Uso: empacotar.sh <x64|arm64> <versão 1.0.N> <executável> <pasta de saída>
set -euo pipefail
ARCH="$1"; VER="$2"; EXE="$3"; OUT="$4"
AQUI="$(cd "$(dirname "$0")" && pwd)"
mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
case "$ARCH" in
  x64)   DEB_ARCH=amd64; RPM_ARCH=x86_64;  AI_ARCH=x86_64;  SUF="" ;;
  arm64) DEB_ARCH=arm64; RPM_ARCH=aarch64; AI_ARCH=aarch64; SUF="_arm64" ;;
  *) echo "arquitetura desconhecida: $ARCH" >&2; exit 2 ;;
esac

# a árvore instalada, igual para .deb e .rpm
raiz() {
  local R="$1"
  install -Dm755 "$EXE" "$R/opt/libertyx-partition/LibertyXPartition"
  install -Dm755 "$AQUI/libertyx-partition.sh" "$R/usr/bin/libertyx-partition"
  install -Dm644 "$AQUI/libertyx-partition.desktop" "$R/usr/share/applications/libertyx-partition.desktop"
  install -Dm644 "$AQUI/io.github.fxmx182.libertyx-partition.policy" "$R/usr/share/polkit-1/actions/io.github.fxmx182.libertyx-partition.policy"
  install -Dm644 "$AQUI/io.github.fxmx182.libertyx-partition.metainfo.xml" "$R/usr/share/metainfo/io.github.fxmx182.libertyx-partition.metainfo.xml"
  for s in 16 24 32 48 64 128 256 512; do
    install -Dm644 "$AQUI/icons/$s.png" "$R/usr/share/icons/hicolor/${s}x${s}/apps/libertyx-partition.png"
  done
}

# ── tar.gz ──
T="$TMP/tar/libertyx-partition"
install -Dm755 "$EXE" "$T/LibertyXPartition"
install -Dm644 "$AQUI/libertyx-partition.desktop" "$T/libertyx-partition.desktop"
install -Dm644 "$AQUI/icons/256.png" "$T/libertyx-partition.png"
cat > "$T/LEIA-ME.txt" <<TXT
LibertyX Partition $VER para Linux ($ARCH)

Abrir:   ./LibertyXPartition   (pede a senha de administrador numa janela do sistema)
Instalar de verdade (menu, atualização automática): prefira o .deb, o .rpm ou o AppImage
em https://github.com/fxmx182/libertyx-partition/releases/tag/linux-latest
TXT
tar -C "$TMP/tar" -czf "$OUT/LibertyXPartition-linux${SUF:+-arm64}.tar.gz" libertyx-partition

# ── .deb ──
D="$TMP/deb"
raiz "$D"
SIZE=$(du -sk "$D" | cut -f1)
mkdir -p "$D/DEBIAN"
cat > "$D/DEBIAN/control" <<CTL
Package: libertyx-partition
Version: $VER
Architecture: $DEB_ARCH
Maintainer: LibertyX <fxmx182@users.noreply.github.com>
Installed-Size: $SIZE
Section: admin
Priority: optional
Homepage: https://github.com/fxmx182/libertyx-partition
Depends: libc6, libfontconfig1, libx11-6, libice6, libsm6, libssl3t64 | libssl3, pkexec | policykit-1
Recommends: e2fsprogs, dosfstools, ntfs-3g, exfatprogs, xfsprogs, btrfs-progs, f2fs-tools, cryptsetup-bin, smartmontools, rsync, lvm2, util-linux
Suggests: mdadm, wimtools, fatresize, nvme-cli, hdparm
Description: Recuperação de dados e gerenciador de partições
 Recupera arquivos apagados e partições perdidas, faz imagem de disco com
 tratamento de setores ruins, teste de superfície e S.M.A.R.T.; cria, redimensiona,
 move, formata, verifica e clona partições (ext4, XFS, Btrfs, F2FS, NTFS, exFAT,
 FAT32, swap, LUKS2), abre LUKS e BitLocker, mostra LVM e RAID.
CTL
cat > "$D/DEBIAN/postinst" <<'SH'
#!/bin/sh
set -e
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q /usr/share/applications || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -t /usr/share/icons/hicolor || true
exit 0
SH
cp "$D/DEBIAN/postinst" "$D/DEBIAN/postrm"
chmod 755 "$D/DEBIAN/postinst" "$D/DEBIAN/postrm"
dpkg-deb --root-owner-group -Zxz --build "$D" "$OUT/libertyx-partition_${DEB_ARCH}.deb" >/dev/null

# ── .rpm ──
if command -v rpmbuild >/dev/null; then
  R="$TMP/rpm"
  mkdir -p "$R"/{BUILD,RPMS,SOURCES,SPECS,SRPMS,BUILDROOT}
  RPMVER="${VER}"
  cat > "$R/SPECS/libertyx-partition.spec" <<SPEC
Name:           libertyx-partition
Version:        $RPMVER
Release:        1
Summary:        Recuperação de dados e gerenciador de partições
License:        Proprietary
URL:            https://github.com/fxmx182/libertyx-partition
BuildArch:      $RPM_ARCH
Requires:       fontconfig libX11 libICE libSM polkit openssl-libs
Recommends:     e2fsprogs dosfstools exfatprogs xfsprogs btrfs-progs f2fs-tools cryptsetup smartmontools rsync lvm2 util-linux
Suggests:       ntfs-3g ntfsprogs mdadm wimlib-utils nvme-cli hdparm
AutoReqProv:    no
%define __strip /bin/true
%define debug_package %{nil}
%define _build_id_links none

%description
Recupera arquivos apagados e partições perdidas, faz imagem de disco com tratamento
de setores ruins, teste de superfície e S.M.A.R.T.; cria, redimensiona, move, formata,
verifica e clona partições, abre LUKS e BitLocker, mostra LVM e RAID.

%install
cp -a "$TMP/rpmroot/." %{buildroot}/

%post
update-desktop-database -q /usr/share/applications >/dev/null 2>&1 || :
gtk-update-icon-cache -q -t /usr/share/icons/hicolor >/dev/null 2>&1 || :

%files
/opt/libertyx-partition
/usr/bin/libertyx-partition
/usr/share/applications/libertyx-partition.desktop
/usr/share/polkit-1/actions/io.github.fxmx182.libertyx-partition.policy
/usr/share/metainfo/io.github.fxmx182.libertyx-partition.metainfo.xml
/usr/share/icons/hicolor/*/apps/libertyx-partition.png
SPEC
  raiz "$TMP/rpmroot"
  rpmbuild --quiet --define "_topdir $R" --target "$RPM_ARCH" -bb "$R/SPECS/libertyx-partition.spec"
  cp "$R"/RPMS/*/libertyx-partition-*.rpm "$OUT/libertyx-partition.${RPM_ARCH}.rpm"
fi

# ── AppImage ──
A="$TMP/LibertyXPartition.AppDir"
install -Dm755 "$EXE" "$A/usr/bin/LibertyXPartition"
install -Dm755 "$AQUI/AppRun" "$A/AppRun"
install -Dm644 "$AQUI/libertyx-partition.desktop" "$A/libertyx-partition.desktop"
install -Dm644 "$AQUI/icons/256.png" "$A/libertyx-partition.png"
for s in 16 24 32 48 64 128 256 512; do install -Dm644 "$AQUI/icons/$s.png" "$A/usr/share/icons/hicolor/${s}x${s}/apps/libertyx-partition.png"; done
ln -s libertyx-partition.png "$A/.DirIcon"
TOOL="$TMP/appimagetool"
curl -fsSL -o "$TOOL" "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-$(uname -m).AppImage"
chmod +x "$TOOL"
RUNTIME=()
if [ "$AI_ARCH" != "$(uname -m)" ]; then
  curl -fsSL -o "$TMP/runtime" "https://github.com/AppImage/type2-runtime/releases/download/continuous/runtime-$AI_ARCH"
  RUNTIME=(--runtime-file "$TMP/runtime")
fi
ARCH="$AI_ARCH" APPIMAGE_EXTRACT_AND_RUN=1 "$TOOL" --no-appstream "${RUNTIME[@]}" "$A" "$OUT/LibertyXPartition-$AI_ARCH.AppImage" >/dev/null
ls -la "$OUT"
