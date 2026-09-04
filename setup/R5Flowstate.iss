; Player-facing wizard. Copies nothing itself: it runs the Velopack
; Setup.exe into {app} so updates stay Velopack. Default is
; Program Files\R5Flowstate\Launcher; the wizard elevates once and grants
; Users modify on the product root so the launcher can self-update and
; download the game into ..\Game without UAC afterwards.

#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif
#ifndef VeloSetup
  #define VeloSetup "..\artifacts\velopack\R5Flowstate-win-Setup.exe"
#endif

#define MyAppName "R5Flowstate"
#define MyAppPublisher "CafeFPS"
#define MyAppURL "https://r5flowstate.org"
#define MyAppExeName "R5FlowstateLauncher.exe"

[Setup]
AppId={{E6B1C4A8-3F2D-4B9E-8A71-5C0D9E2F4B18}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL=https://github.com/CafeFPS/r5f-launcher/releases
DefaultDirName={autopf}\{#MyAppName}\Launcher
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\artifacts
OutputBaseFilename=R5FlowstateSetup
SetupIconFile=setup.ico
WizardImageFile=wizard-side.bmp
WizardSmallImageFile=wizard-small.bmp
WizardImageStretch=yes
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
UsePreviousAppDir=yes
DisableDirPage=no
AllowCancelDuringInstall=yes
DirExistsWarning=auto
Uninstallable=no
CreateUninstallRegKey=no
CloseApplications=no
RestartApplications=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
ShowLanguageDialog=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "polish"; MessagesFile: "compiler:Languages\Polish.isl"
Name: "portuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "schinese"; MessagesFile: "languages\ChineseSimplified.isl"
Name: "tchinese"; MessagesFile: "languages\ChineseTraditional.isl"

[CustomMessages]
english.InstallingApp=Installing R5Flowstate…
french.InstallingApp=Installation de R5Flowstate…
german.InstallingApp=R5Flowstate wird installiert…
italian.InstallingApp=Installazione di R5Flowstate…
japanese.InstallingApp=R5Flowstate をインストールしています…
korean.InstallingApp=R5Flowstate를 설치하는 중…
polish.InstallingApp=Instalowanie R5Flowstate…
portuguese.InstallingApp=A instalar o R5Flowstate…
russian.InstallingApp=Установка R5Flowstate…
spanish.InstallingApp=Instalando R5Flowstate…
schinese.InstallingApp=正在安装 R5Flowstate…
tchinese.InstallingApp=正在安裝 R5Flowstate…
english.LaunchApp=Launch R5Flowstate
french.LaunchApp=Lancer R5Flowstate
german.LaunchApp=R5Flowstate starten
italian.LaunchApp=Avvia R5Flowstate
japanese.LaunchApp=R5Flowstate を起動
korean.LaunchApp=R5Flowstate 실행
polish.LaunchApp=Uruchom R5Flowstate
portuguese.LaunchApp=Iniciar o R5Flowstate
russian.LaunchApp=Запустить R5Flowstate
spanish.LaunchApp=Iniciar R5Flowstate
schinese.LaunchApp=启动 R5Flowstate
tchinese.LaunchApp=啟動 R5Flowstate
english.ReservedFolder=That folder is reserved for the one-click updater layout. Pick a different path (the default Programs folder is fine).
french.ReservedFolder=Ce dossier est réservé à la disposition du mise à jour en un clic. Choisissez un autre chemin (le dossier Programmes par défaut convient).
german.ReservedFolder=Dieser Ordner ist für das Ein-Klick-Update-Layout reserviert. Wählen Sie einen anderen Pfad (der Standard-Programme-Ordner ist in Ordnung).
italian.ReservedFolder=Questa cartella è riservata al layout dell'updater con un clic. Scegli un altro percorso (la cartella Programmi predefinita va bene).
japanese.ReservedFolder=そのフォルダーはワンクリック更新用です。別のパスを選んでください（既定のプログラムフォルダーで問題ありません）。
korean.ReservedFolder=그 폴더는 원클릭 업데이터 레이아웃용입니다. 다른 경로를 고르세요(기본 프로그램 폴더면 됩니다).
polish.ReservedFolder=Ten folder jest zarezerwowany dla układu aktualizacji jednym kliknięciem. Wybierz inną ścieżkę (domyślny folder Programy jest w porządku).
portuguese.ReservedFolder=Essa pasta está reservada para o esquema do atualizador com um clique. Escolha outro caminho (a pasta Programas predefinida serve).
russian.ReservedFolder=Эта папка занята раскладкой обновления в один клик. Выберите другой путь (стандартная папка Programs подойдёт).
spanish.ReservedFolder=Esa carpeta está reservada para el diseño del actualizador de un clic. Elige otra ruta (la carpeta Programas predeterminada vale).
schinese.ReservedFolder=该文件夹留给一键更新布局使用。请另选路径（默认的程序文件夹即可）。
tchinese.ReservedFolder=該資料夾留給一鍵更新配置使用。請另選路徑（預設的程式資料夾即可）。

[Registry]
Root: HKCU; Subkey: "Software\R5Flowstate"; ValueType: string; ValueName: "UiLanguage"; ValueData: "{language}"
Root: HKCU; Subkey: "Software\R5Flowstate"; ValueType: string; ValueName: "EulaLanguage"; ValueData: "{language}"

[Dirs]
; Users-modify so Velopack self-update and game downloads run without UAC.
Name: "{app}"; Permissions: users-modify
Name: "{autopf}\{#MyAppName}"; Permissions: users-modify; Check: InstallInDefaultRoot
Name: "{autopf}\{#MyAppName}\Game"; Permissions: users-modify; Check: InstallInDefaultRoot

[Files]
Source: "{#VeloSetup}"; DestDir: "{tmp}"; DestName: "R5Flowstate-win-Setup.exe"; Flags: deleteafterinstall ignoreversion

[Run]
Filename: "{tmp}\R5Flowstate-win-Setup.exe"; Parameters: "--silent --installto ""{app}"""; StatusMsg: "{cm:InstallingApp}"; Flags: waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[Code]
function InstallInDefaultRoot: Boolean;
begin
  Result := Pos(
    LowerCase(ExpandConstant('{autopf}\{#MyAppName}')),
    LowerCase(ExpandConstant('{app}'))) = 1;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Dest: String;
begin
  Result := True;
  if CurPageID = wpSelectDir then
  begin
    Dest := ExpandConstant('{app}');
    if Pos(LowerCase(ExpandConstant('{localappdata}') + '\R5Flowstate'), LowerCase(Dest)) = 1 then
    begin
      MsgBox(ExpandConstant('{cm:ReservedFolder}'), mbError, MB_OK);
      Result := False;
      exit;
    end;
  end;
end;
