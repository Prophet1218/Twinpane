; Glass Commander telepítő (Inno Setup 6)
; A GitHub Actions adja át: /DAppVersion=x.y.z /DSourceDir=<publish mappa>

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

[Setup]
AppId={{B6E0C1E2-5B5F-4E37-9C2A-6A1F2B3C4D5E}
AppName=Glass Commander
AppVersion={#AppVersion}
AppVerName=Glass Commander {#AppVersion}
AppPublisher=Prophet1218
AppPublisherURL=https://github.com/Prophet1218/Glass-Commander-
DefaultDirName={autopf}\Glass Commander
DefaultGroupName=Glass Commander
DisableProgramGroupPage=yes
; Rendszergazdai jog nélkül is telepíthető (saját felhasználóra), de választható "mindenkinek" is.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\dist
OutputBaseFilename=GlassCommander-{#AppVersion}-Setup
SetupIconFile=..\src\GlassCommander\app.ico
UninstallDisplayIcon={app}\GlassCommander.exe
UninstallDisplayName=Glass Commander
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "hungarian"; MessagesFile: "compiler:Languages\Hungarian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Glass Commander"; Filename: "{app}\GlassCommander.exe"
Name: "{autodesktop}\Glass Commander"; Filename: "{app}\GlassCommander.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\GlassCommander.exe"; Description: "{cm:LaunchProgram,Glass Commander}"; Flags: nowait postinstall skipifsilent
