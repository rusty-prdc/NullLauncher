; NullLauncher.iss — скрипт инсталлятора для Inno Setup 6+
; Собирается скриптом installer\make-installer.ps1 (он сначала выполняет publish.ps1).
; Ручная сборка: iscc installer\NullLauncher.iss

#define AppName "NullLauncher"
#define AppVersion "1.0.0"
#define AppExe "NullLauncher.exe"
#define PublishDir "..\publish\NullLauncher"

[Setup]
AppId={{8D5A1B63-4C0E-4A7F-9E21-5B7F3C9A2D10}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=NullLauncher
AppPublisherURL=https://github.com/nulllauncher
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=NullLauncher-{#AppVersion}-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile=..\UI\assets\icon.ico

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "associate"; Description: "Ассоциировать файлы .mrpack и .nlpkg"; GroupDescription: "Дополнительно"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKA; Subkey: "Software\Classes\.mrpack"; ValueData: "NullLauncher.mrpack"; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.mrpack\OpenWithProgids"; ValueName: "NullLauncher.mrpack"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\NullLauncher.mrpack"; ValueData: "Модпак NullLauncher"; Flags: uninsdeletekey; Tasks: associate
Root: HKA; Subkey: "Software\Classes\NullLauncher.mrpack\shell\open\command"; ValueData: """{app}\{#AppExe}"" ""%1"""; Flags: uninsdeletekey; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.nlpkg"; ValueData: "NullLauncher.nlpkg"; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\NullLauncher.nlpkg"; ValueData: "Сборка NullLauncher"; Flags: uninsdeletekey; Tasks: associate
Root: HKA; Subkey: "Software\Classes\NullLauncher.nlpkg\shell\open\command"; ValueData: """{app}\{#AppExe}"" ""%1"""; Flags: uninsdeletekey; Tasks: associate

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
// --- автоустановка недостающих компонентов Windows (.NET Desktop, WebView2) ---
const
  WV2ClientKey = 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WV2ClientKey64 = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  DotNetFxKey = 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App';
  DotNetUrl = 'https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/8.0.31/windowsdesktop-runtime-8.0.31-win-x64.exe';
  WV2Url = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703';

function URLDownloadToFileW(pCaller: Integer; pszURL, pszFileName: String;
  dwReserved: DWORD; lpfnCB: Integer): Integer;
  external 'URLDownloadToFileW@urlmon.dll stdcall';

function WebView2Installed(): Boolean;
var
  pv: String;
begin
  Result := False;
  if RegQueryStringValue(HKLM64, WV2ClientKey, 'pv', pv) then
    Result := (pv <> '') and (pv <> '0.0.0.0');
  if (not Result) and RegQueryStringValue(HKLM64, WV2ClientKey64, 'pv', pv) then
    Result := (pv <> '') and (pv <> '0.0.0.0');
  if not Result then
    Result := DirExists(ExpandConstant('{commonpf64}\Microsoft\EdgeWebView\Application'));
end;

function DotNetDesktopInstalled(): Boolean;
begin
  Result := RegKeyExists(HKLM64, DotNetFxKey);
end;

function DownloadTo(const Url, FileName: String): Boolean;
begin
  Result := URLDownloadToFileW(0, Url, FileName, 0, 0) = 0;
end;

procedure SetStatus(const Text: String);
begin
  if Assigned(WizardForm) and Assigned(WizardForm.StatusLabel) then
  begin
    WizardForm.StatusLabel.Caption := Text;
    WizardForm.StatusLabel.Update;
  end;
end;

// Скачивает и ставит недостающие компоненты. Ошибка здесь не прерывает
// установку самого лаунчера — выводится предупреждение.
procedure InstallMissingComponents();
var
  SetupFile: String;
  Code: Integer;
  Warnings: String;
  NL: String;
begin
  Warnings := '';
  NL := #13#10;

  if not DotNetDesktopInstalled() then
  begin
    SetupFile := ExpandConstant('{tmp}\windowsdesktop-runtime.exe');
    SetStatus('Скачивание .NET Desktop Runtime 8.0.31...');
    if DownloadTo(DotNetUrl, SetupFile) then
    begin
      SetStatus('Установка .NET Desktop Runtime 8.0.31...');
      if (not ShellExec('runas', SetupFile, '/install /quiet /norestart', '', SW_HIDE, '', Code))
         or (Code <> 0) then
        Warnings := Warnings + #13#10 + '.NET Desktop Runtime: установка не выполнена';
    end
    else
      Warnings := Warnings + #13#10 + '.NET Desktop Runtime: не удалось скачать';
  end;

  if not WebView2Installed() then
  begin
    SetupFile := ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe');
    SetStatus('Скачивание WebView2 Runtime...');
    if DownloadTo(WV2Url, SetupFile) then
    begin
      SetStatus('Установка WebView2 Runtime...');
      ShellExec('', SetupFile, '/silent /install', '', SW_HIDE, '', Code);
      if not WebView2Installed() then
        Warnings := Warnings + #13#10 + 'WebView2 Runtime: установка не выполнена';
    end
    else
      Warnings := Warnings + #13#10 + 'WebView2 Runtime: не удалось скачать';
  end;

  if Warnings <> '' then
  begin
    if WizardSilent then
      Log('Компоненты не установлены:' + Warnings)
    else
      MsgBox('Не удалось установить системные компоненты:' + Warnings +
        NL + NL + 'Установите их вручную и запустите лаунчер ещё раз.',
        mbError, MB_OK);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    InstallMissingComponents();
end;
