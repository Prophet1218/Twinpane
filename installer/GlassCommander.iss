; Twinpane telepítő (Inno Setup 6)
; (A program korábbi neve: Glass Commander – az AppId ugyanaz, így a régi változat helyére települ.)
; A GitHub Actions adja át: /DAppVersion=x.y.z /DSourceDir=<publish mappa>

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

[Setup]
AppId={{B6E0C1E2-5B5F-4E37-9C2A-6A1F2B3C4D5E}
AppName=Twinpane
AppVersion={#AppVersion}
AppVerName=Twinpane {#AppVersion}
AppPublisher=Prophet1218
AppPublisherURL=https://github.com/Prophet1218/Glass-Commander-
DefaultDirName={autopf}\Twinpane
UsePreviousAppDir=no
DefaultGroupName=Twinpane
DisableProgramGroupPage=yes
; Rendszergazdai jog nélkül is telepíthető (saját felhasználóra), de választható "mindenkinek" is.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\dist
OutputBaseFilename=Twinpane-{#AppVersion}-Setup
SetupIconFile=..\src\GlassCommander\app.ico
UninstallDisplayIcon={app}\Twinpane.exe
UninstallDisplayName=Twinpane
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

[InstallDelete]
; A régi "Glass Commander" telepítés és parancsikonjai
Type: filesandordirs; Name: "{autopf}\Glass Commander"
Type: files; Name: "{autoprograms}\Glass Commander.lnk"
Type: files; Name: "{autodesktop}\Glass Commander.lnk"
Type: files; Name: "{app}\GlassCommander.exe"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Twinpane"; Filename: "{app}\Twinpane.exe"
Name: "{autodesktop}\Twinpane"; Filename: "{app}\Twinpane.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Twinpane.exe"; Description: "{cm:LaunchProgram,Twinpane}"; Flags: nowait postinstall skipifsilent
