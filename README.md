# LibertyX Partition

Ferramenta de discos para **Windows e Linux**, da família LibertyX (Player, Folder), com foco em
**recuperação de dados**. Interface em Avalonia (.NET 10), ouro sobre preto, Manrope. O mesmo
código nos dois sistemas: o motor de recuperação é próprio; muda só a camada nativa.

## O que faz

**Resgate** — recuperar arquivos apagados (NTFS, FAT12/16/32, exFAT direto dos setores, com
nome, pasta e data originais, inclusive pastas inteiras, arquivos comprimidos do NTFS e
nomes reais da Lixeira); varredura profunda por assinatura (JPEG, PNG, GIF, BMP, TIFF e RAW
de câmera, HEIC, MP4/MOV, AVI, MKV, WMV, MP3, WAV, OGG, PDF, Office novo e antigo, ZIP,
RAR, 7z, SQLite, EXE) com o tamanho calculado pela estrutura de cada formato; registros
MFT avulsos de NTFS formatado; recuperar partição (busca rápida e completa por setores de
boot e suas cópias); imagem de disco no estilo ddrescue com SHA-256; teste de superfície;
S.M.A.R.T. (SATA e NVMe).

**Disco** — migrar o Windows para SSD/HD e copiar disco (instantâneo VSS, só espaço usado,
crescer para o disco novo, bcdboot); copiar partição; converter GPT ⇄ MBR sem perda (e
MBR2GPT para o disco do sistema); alinhar; limpar (zeros, aleatório, DoD); excluir todas.

**Partição** — mover/redimensionar, estender, unir, dividir, cluster, NTFS ⇄ FAT,
formatar (inclusive FAT32 acima de 32 GB), excluir, rótulo, letra, ID de tipo, número de
série, ativa, verificar (chkdsk), explorar, propriedades.

**Utilidades** — BitLocker (desbloquear, bloquear, ativar, desativar, chave de
recuperação), benchmark (estilo CrystalDiskMark + curva de leitura), analisador de espaço
(treemap), duplicados (xxHash128, para a Lixeira), mídia inicializável (ISO do Windows em
FAT32 UEFI+BIOS com install.wim dividido, ou gravação DD).

## No Linux

Tudo o que está acima (o motor é o mesmo), mais o que é do Linux:

- **Sistemas de arquivos**: cria, formata, redimensiona, verifica, rotula e troca o UUID de ext2/3/4,
  XFS, Btrfs, F2FS, NTFS, exFAT, FAT16/32 e swap, pelas ferramentas oficiais de cada um (as mesmas
  do GParted). ext4, XFS e Btrfs **crescem montados**, inclusive o `/` — o kernel é avisado pelo BLKPG.
- **Disco do sistema**: como no GParted, dá para criar e alterar partições no espaço livre do disco
  onde está o `/`; só as partições em uso (`/`, `/boot`, `/home`, swap ativa…) ficam travadas.
- **Criptografia**: LUKS1/LUKS2 e BitLocker (pelo cryptsetup) — desbloquear, montar, recuperar
  arquivos de dentro, senhas (slots), cópia e restauração do cabeçalho; criar partição LUKS2.
- **LVM e RAID**: volumes lógicos e RAID por software aparecem como discos (recuperar, verificar,
  formatar); ativar e desativar grupos; montar e parar conjuntos.
- **Sinalizadores e nomes**: esp, bios_grub, lvm, raid, swap, legacy_boot, oculta; nome GPT (PARTLABEL);
  tipos da especificação de partições detectáveis (raiz, /home, /var, XBOOTLDR…).
- **Recuperação em XFS e Btrfs**, lidos direto dos setores (também volumes que não montam):
  - XFS: árvore viva completa (extents e árvore B+), nomes dos apagados pelos restos das entradas nas
    pastas e pelas cópias antigas no log, e o mapa de blocos de cada apagado tirado do log (journal)
    enquanto ele não foi reciclado; a varredura profunda lê só o espaço livre.
  - Btrfs: subvolumes e snapshots, compressão zstd/LZO/zlib, arquivos embutidos; apagados pelas
    versões antigas das folhas da árvore (cópia na escrita), conferidos pelo checksum do próprio Btrfs.
- **Busca de partições perdidas** também acha XFS, Btrfs, F2FS e LUKS (cópias de superbloco não viram fantasmas).
- **Migrar o sistema** para outro disco, pronto para dar boot (GRUB UEFI/BIOS, systemd-boot), com
  UUIDs novos e fstab/GRUB corrigidos — testado numa VM que liga só pelo disco novo.
- **Limpar SSD do jeito certo**: TRIM do disco inteiro ou apagamento do fabricante (NVMe Format,
  ATA Secure Erase); **Otimizar**: TRIM no SSD, desfragmentação no HD.
- **S.M.A.R.T. sem programa externo**: NVMe pelo comando de administração e SATA/USB por SG_IO (ATA
  PASS-THROUGH), com o smartctl de reserva para pontes USB difíceis.
- **Montar / desmontar**, ligar/desligar swap, lixeira do padrão freedesktop, montagem automática do
  ambiente gráfico suspensa durante as operações, quem está usando um volume que não desmonta.
- **Ferramentas do sistema**: mostra o que falta e instala pelo gerenciador de pacotes da distribuição.
- Abre pedindo a senha de administrador numa janela do sistema (pkexec/polkit); os arquivos
  recuperados voltam com o dono certo e a pasta abre no gerenciador de arquivos de quem usa.

Downloads (x64 e ARM64), sempre a última versão aprovada no autoteste:

- Ubuntu, Debian, Mint, Pop!_OS: [`libertyx-partition_amd64.deb`](https://github.com/fxmx182/libertyx-partition/releases/download/linux-latest/libertyx-partition_amd64.deb)
- Fedora, openSUSE, RHEL: [`libertyx-partition.x86_64.rpm`](https://github.com/fxmx182/libertyx-partition/releases/download/linux-latest/libertyx-partition.x86_64.rpm)
- Qualquer distribuição: [`LibertyXPartition-x86_64.AppImage`](https://github.com/fxmx182/libertyx-partition/releases/download/linux-latest/LibertyXPartition-x86_64.AppImage)
- ARM64 (Raspberry Pi 4/5, notebooks ARM): `libertyx-partition_arm64.deb`, `libertyx-partition.aarch64.rpm`, `LibertyXPartition-aarch64.AppImage`

O app se atualiza sozinho: o AppImage troca o próprio arquivo; o .deb/.rpm atualiza pelo apt/dnf.

## Segurança

- Nada destrutivo no disco do Windows em uso; operações perigosas pedem a palavra digitada.
- Toda escrita de tabela salva antes um `.lxbak` em `Documentos\LibertyX Partition\Backups de tabela`
  (restaurável pelo próprio app).
- Volumes são trancados e desmontados antes de qualquer escrita direta.
- Onde o Windows faz melhor (redimensionar NTFS montado, formatar, chkdsk, BitLocker, VSS,
  bcdboot), o app usa o próprio Windows.

## Estrutura

- `src/LibertyX.Partition.Core` — motor (multiplataforma): leitura de setores, MBR/GPT,
  NTFS/FAT/exFAT/ext/HFS+/APFS, recuperação, escavação, operações; `Platform/Windows` e
  `Platform/Linux` são as camadas nativas.
- `src/LibertyX.Partition` — interface Avalonia; `--selftest relatorio.txt [--vhd]` roda o autoteste
  (no Linux, `--loop`: opera só num disco loop que o próprio teste cria).
- `packaging/linux` — atalho, política do polkit, AppStream, ícones e `empacotar.sh` (AppImage, .deb, .rpm, .tar.gz).
- `tests/Probe` — testes do motor contra imagens de disco reais (geradas por `tests/scripts/make-images.sh`
  num contêiner Debian privilegiado).
- `tests/Snap` — capturas de tela sem monitor (Avalonia.Headless).
- `Scripts/icone.py` — ícone e marca, gerados por geometria.

## Idiomas

Português, inglês e espanhol. O app abre no idioma do sistema (no Linux, `LANGUAGE`/`LC_ALL`/
`LC_MESSAGES`/`LANG`; no Windows, o idioma da interface); outro idioma qualquer abre em inglês. O
botão "Aa" no rodapé da barra lateral troca o idioma, e o app reabre. O autoteste roda sempre em
português.

O texto em português fica no código e é a própria chave (`L.cs`):

- C#: `L.T("Cancelar")` e `L.T($"Copiando {n} de {total}")` (a chave fica `Copiando {0} de {1}`).
- AXAML: `Text="{l:T 'Cancelar'}"`.
- As traduções ficam em `src/LibertyX.Partition.Core/Lang/en.json` e `es.json`.

Comando, caminho, nome de ícone e saída de ferramenta comparada com `Contains` não levam `L.T`.
Texto novo vai para os dois JSON (texto que não muda, como uma marca, entra com o próprio
português). `python3 tests/i18n/conferir.py` lista o que falta, e o CI do Linux mostra isso como
aviso. Para testar um idioma: `LANG=en_US.UTF-8`, ou `LX_SNAP_LANG=es` no `tests/Snap`;
`LIBERTYX_LANG_MISSING=arquivo` registra o que apareceu sem tradução.

## Build

    dotnet publish src/LibertyX.Partition -c Release -r win-x64 --self-contained \
      -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true

O CI (`.github/workflows/partition-windows.yml`) compila num runner Windows, roda o autoteste
num disco virtual (inicializa, cria NTFS, apaga e recupera, reduz, move, alinha, converte,
troca série, formata FAT32, faz imagem, exclui tudo, acha e restaura, limpa), monta o
instalador (Inno Setup, `installer/`), testa instalar e desinstalar em silêncio, e publica:

- **Instalador** (recomendado): [`LibertyXPartition-Setup.exe`](https://github.com/fxmx182/libertyx-partition/releases/download/latest/LibertyXPartition-Setup.exe)
  — Arquivos de Programas, Menu Iniciar, atalho na área de trabalho, desinstalação em
  "Aplicativos instalados"; a versão nova atualiza por cima.
- **Portátil**: [`LibertyXPartition.exe`](https://github.com/fxmx182/libertyx-partition/releases/download/latest/LibertyXPartition.exe) — roda sem instalar (bom para levar num pendrive).

As artes do instalador saem de `Scripts/instalador.py`.

No Linux: `.github/workflows/partition-linux.yml` compila em x64 e ARM64 (runners nativos), roda o
autoteste num disco loop (cria GPT, formata em 8 sistemas de arquivos, apaga e recupera pelo
journal, reduz, move, cresce montado, LUKS2, exclui tudo e acha de volta, GPT⇄MBR, grava imagem
compactada, limpa), empacota, instala o .deb (Ubuntu) e o .rpm (Fedora) para provar, e publica no
release `linux-latest`.

    dotnet publish src/LibertyX.Partition -c Release -r linux-x64 --self-contained \
      -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
    packaging/linux/empacotar.sh x64 1.0.0 <executável> pacotes

## Família LibertyX

| App | Repositório |
|---|---|
| LibertyX Player — iPhone | [libertyx-player-ios](https://github.com/fxmx182/libertyx-player-ios) |
| LibertyX Player — Android e TV | [libertyx-player-android](https://github.com/fxmx182/libertyx-player-android) |
| LibertyX Folder — iPhone | [libertyx-folder-ios](https://github.com/fxmx182/libertyx-folder-ios) |
| LibertyX Folder — Android e TV | [libertyx-folder-android](https://github.com/fxmx182/libertyx-folder-android) |
| LibertyX Partition — Windows e Linux | [libertyx-partition](https://github.com/fxmx182/libertyx-partition) |
