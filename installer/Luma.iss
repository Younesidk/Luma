; Inno Setup script for Luma. Built by scripts/build-installer.ps1, which passes
;   /DAppVersion=<version> /DSourceDir=<published folder> /DOutputDir=<installer folder>
; Run on its own with ISCC.exe only if you pass those three defines yourself.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\Luma-win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

#define AppName "Luma"
#define AppExe "Luma.exe"

[Setup]
; Fixed GUID so upgrades replace the previous install instead of installing side by side.
AppId={{6F1B3C52-8A0E-4D7B-9C3A-5E2D41B7A9F0}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Luma
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
SetupIconFile=..\src\Luma.App\Assets\luma.ico
OutputDir={#OutputDir}
OutputBaseFilename=Luma-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Installs for the current user without admin rights by default; the user can choose
; "all users" in the dialog Inno shows.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

; Settings, favorites, downloads and the library live in %LocalAppData%\Luma and are
; deliberately left alone on uninstall, so reinstalling never loses a user's data.
