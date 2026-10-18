# LibertyX Partition

Ferramenta de discos para Windows, da família LibertyX (Player, Folder), com foco em
**recuperação de dados**. Interface em Avalonia (.NET 10), ouro sobre preto, Manrope.

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

## Segurança

- Nada destrutivo no disco do Windows em uso; operações perigosas pedem a palavra digitada.
- Toda escrita de tabela salva antes um `.lxbak` em `Documentos\LibertyX Partition\Backups de tabela`
  (restaurável pelo próprio app).
- Volumes são trancados e desmontados antes de qualquer escrita direta.
- Onde o Windows faz melhor (redimensionar NTFS montado, formatar, chkdsk, BitLocker, VSS,
  bcdboot), o app usa o próprio Windows.

## Estrutura

- `src/LibertyX.Partition.Core` — motor (multiplataforma onde dá): leitura de setores, MBR/GPT,
  NTFS/FAT/exFAT, recuperação, escavação, operações; `Platform/Windows` é a camada nativa.
- `src/LibertyX.Partition` — interface Avalonia; `--selftest relatorio.txt [--vhd]` roda o autoteste.
- `tests/Probe` — testes do motor contra imagens de disco reais (geradas por `tests/scripts/make-images.sh`
  num contêiner Debian privilegiado).
- `tests/Snap` — capturas de tela sem monitor (Avalonia.Headless).
- `Scripts/icone.py` — ícone e marca, gerados por geometria.

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

## Família LibertyX

| App | Repositório |
|---|---|
| LibertyX Player — iPhone | [libertyx-player-ios](https://github.com/fxmx182/libertyx-player-ios) |
| LibertyX Player — Android e TV | [libertyx-player-android](https://github.com/fxmx182/libertyx-player-android) |
| LibertyX Folder — iPhone | [libertyx-folder-ios](https://github.com/fxmx182/libertyx-folder-ios) |
| LibertyX Folder — Android e TV | [libertyx-folder-android](https://github.com/fxmx182/libertyx-folder-android) |
| LibertyX Partition — Windows | [libertyx-partition](https://github.com/fxmx182/libertyx-partition) |
