; Instalador do LibertyX Partition (Inno Setup 6), compilado pelo CI no Windows.
; Instala em Arquivos de Programas, cria atalhos e a entrada em "Aplicativos instalados".
; O AppId nunca muda: é ele que faz a versão nova atualizar por cima da anterior.
#ifndef Version
  #define Version "1.0.0"
#endif

[Setup]
AppId={{0AF2D340-CF57-4FE4-9F85-1F24376D6F7F}
AppName=LibertyX Partition
AppVersion={#Version}
AppVerName=LibertyX Partition {#Version}
AppPublisher=LibertyX
AppPublisherURL=https://github.com/fxmx182/libertyx-partition
AppSupportURL=https://github.com/fxmx182/libertyx-partition
AppUpdatesURL=https://github.com/fxmx182/libertyx-partition/releases/latest
AppCopyright=LibertyX
DefaultDirName={autopf}\LibertyX Partition
DefaultGroupName=LibertyX Partition
DisableProgramGroupPage=yes
DisableDirPage=auto
; o app mexe em discos: sempre roda como administrador, então instala para todos
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=LibertyXPartition-Setup
SetupIconFile=..\src\LibertyX.Partition\Assets\app.ico
UninstallDisplayIcon={app}\LibertyXPartition.exe
UninstallDisplayName=LibertyX Partition
WizardStyle=modern
WizardImageFile=wizard.bmp,wizard-2x.bmp
WizardSmallImageFile=small.bmp,small-2x.bmp
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#Version}
VersionInfoProductName=LibertyX Partition
VersionInfoDescription=Instalador do LibertyX Partition

[Languages]
Name: "pt"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Messages]
pt.WelcomeLabel2=Isto vai instalar o [name/ver] no seu computador.%n%nDiscos, partições e recuperação de dados — com cada alteração esperando a sua confirmação antes de tocar no disco.%n%nFeche outros programas antes de continuar.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\dist\LibertyXPartition.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\LibertyX Partition"; Filename: "{app}\LibertyXPartition.exe"; Comment: "Discos, partições e recuperação de dados"
Name: "{autodesktop}\LibertyX Partition"; Filename: "{app}\LibertyXPartition.exe"; Tasks: desktopicon

[Run]
; shellexec: o .exe pede administrador pelo manifesto
Filename: "{app}\LibertyXPartition.exe"; Description: "{cm:LaunchProgram,LibertyX Partition}"; Flags: nowait postinstall skipifsilent shellexec
