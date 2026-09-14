; ─────────────────────────────────────────────────────────────────────────
; Win11Optimizer.iss — Corn Systems installer script (Inno Setup 6.x)
;
; Builds a proper Setup.exe alongside the existing portable single-file exe.
; Requires Inno Setup 6+: https://jrsoftware.org/isinfo.php
;
; USAGE:
;   1. Publish the portable build first (from the project root):
;        dotnet publish -c Release -r win-x64 --self-contained true ^
;          -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true ^
;          -o .\publish
;   2. Compile this script:
;        ISCC.exe Win11Optimizer.iss
;   3. Output: installer_output\Win11Optimizer-Setup.exe
; ─────────────────────────────────────────────────────────────────────────

#define MyAppName "Win11 Optimizer"
#define MyAppVersion "1.4.2"
#define MyAppPublisher "Corn Systems"
#define MyAppURL "https://github.com/Corn-Systems/win11op"
#define MyAppExeName "Win11Optimizer.exe"

[Setup]
; Fixed GUID — do not change between releases, it's how Windows tracks upgrades/uninstalls
AppId={{6EED7A04-CF3B-459F-A98E-4C2BF563C3AC}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\Corn Systems\Win11 Optimizer
DefaultGroupName=Corn Systems
DisableProgramGroupPage=yes
OutputDir=installer_output
OutputBaseFilename=Win11Optimizer-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; The Setup.exe itself needs admin to write to Program Files / register the uninstaller.
; The app's own app.manifest separately requests admin on every launch.
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "publish\Win11Optimizer.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Clean up the app's own state files on uninstall — leave the user's registry
; tweaks alone (those are Windows settings, not app files, and undoing them
; on uninstall would be surprising/destructive behavior). Everything the app
; writes now lives under Data\ next to the exe (see AppPaths.cs) instead of
; loose in the install folder, so removing that one subfolder covers it.
Type: filesandordirs; Name: "{app}\Data"
