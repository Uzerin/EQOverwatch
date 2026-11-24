; Windows installer for the app, built by the release workflow (Inno Setup 6.7):
;   ISCC.exe /DAppName="..." /DAppVersion=1.2.3 /DAppFolder=... /DRepository=owner/name /DSourceDir=publish /DOutputName=... installer\EQOverwatch.iss
; It installs for the current user only (no admin prompt) in %LocalAppData%\Programs\<AppName>, so the app's own "Install update" can
; replace its exe there later. Settings live in %LocalAppData%\<AppFolder> and are kept unless the user asks to delete them on uninstall.

#ifndef AppName
  #define AppName "EQ Overwatch"
#endif
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef AppFolder
  #define AppFolder "EQOverwatch"
#endif
#ifndef Repository
  #define Repository "Uzerin/EQOverwatch"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif
#ifndef OutputName
  #define OutputName AppFolder + "-Setup-v" + AppVersion
#endif
#define ExeName "EQOverwatch.exe"

[Setup]
; The app writes DisplayVersion under this id after an in-app update (see InstalledAppEntry.cs), so keep the two in step.
AppId={#AppFolder}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Uzerin
AppPublisherURL=https://github.com/{#Repository}
AppSupportURL=https://github.com/{#Repository}/issues
AppUpdatesURL=https://github.com/{#Repository}/releases/latest
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=.
OutputBaseFilename={#OutputName}
SetupIconFile=..\src\EQOverwatch.App\Assets\EQOverwatch.ico
UninstallDisplayIcon={app}\{#ExeName}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
; The app's colors: dark wizard on the app's window background.
WizardStyle=modern dark hidebevels
WizardBackColor=#0F111A
WizardSmallImageFile=WizardSmall.png

[Tasks]
Name: "desktopicon"; Description: "Desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#SourceDir}\{#ExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#ExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#ExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#ExeName}"; Description: "Start {#AppName} when setup finishes"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; What the app's Install update leaves next to the exe.
Type: files; Name: "{app}\*.old"
Type: filesandordirs; Name: "{app}\.update"

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    Data := ExpandConstant('{localappdata}\{#AppFolder}');
    if DirExists(Data) and not UninstallSilent() then
      if MsgBox('Also delete your {#AppName} settings and data (triggers, fights, loot, overlays)?' + #13#10#13#10 +
                'Choose No to keep them, so a reinstall picks up where you left off.',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(Data, True, True, True);
  end;
end;
