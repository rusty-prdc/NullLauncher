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
