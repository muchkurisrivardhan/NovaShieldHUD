; NovaShield HUD installer (Inno Setup 6). Built by installer\build.ps1, which passes
; /DAppVersion=1.2.3 /DArch=x64|arm64 /DSource=<published apps folder>.
; Per-user install: no admin prompt, nothing outside your own profile.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif

[Setup]
AppId={{6B1F2E7A-3C9D-4E58-A1B2-7C4D9E0F5A31}
AppName=NovaShield HUD
AppVersion={#AppVersion}
AppVerName=NovaShield HUD {#AppVersion}
AppPublisher=muchkurisrivardhan
AppPublisherURL=https://muchkurisrivardhan.github.io/vantage/
AppSupportURL=https://github.com/muchkurisrivardhan/vantage/issues
AppUpdatesURL=https://github.com/muchkurisrivardhan/vantage/releases
DefaultDirName={localappdata}\Programs\NovaShield HUD
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
OutputDir=out
OutputBaseFilename=NovaShieldHUD-Setup-{#Arch}
SetupIconFile=..\beacon\Assets\Beacon.ico
UninstallDisplayIcon={app}\Beacon.exe
UninstallDisplayName=NovaShield HUD
LicenseFile=..\LICENSE
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=force
RestartApplications=no
MinVersion=10.0.17763
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif

[Types]
Name: "full"; Description: "NovaShield HUD with Filee (recommended)"
Name: "compact"; Description: "NovaShield HUD only"
Name: "custom"; Description: "Choose"; Flags: iscustom

[Components]
Name: "core"; Description: "NovaShield HUD: Beacon launcher and Dotline widgets"; Types: full compact custom; Flags: fixed
Name: "filee"; Description: "Filee: drag-and-drop file converter (hold Ctrl and drag files). By Myeonggu Kim (KnifeLemon), MIT licence, styled for NovaShield"; Types: full custom

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#Source}\*"; DestDir: "{app}"; Excludes: "\filee\*"; Flags: recursesubdirs ignoreversion
Source: "{#Source}\filee\*"; DestDir: "{app}\filee"; Components: filee; Flags: recursesubdirs ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"
Source: "..\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"

[InstallDelete]
; Updating with Filee unticked removes the copy an earlier install put there.
Type: filesandordirs; Name: "{app}\filee"; Check: not WizardIsComponentSelected('filee')

[Icons]
Name: "{userprograms}\NovaShield HUD"; Filename: "{app}\Beacon.exe"
Name: "{userdesktop}\NovaShield HUD"; Filename: "{app}\Beacon.exe"; Tasks: desktopicon
Name: "{userprograms}\Filee (NovaShield HUD)"; Filename: "{app}\filee\Filee.exe"; Parameters: "--settings"; Components: filee

[Registry]
; Beacon writes this itself when "Start Beacon when I sign in" is on; uninstall removes it.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "Beacon"; Flags: uninsdeletevalue

[Run]
; Also runs after a silent install, so "Update" in Beacon ends with Beacon running again.
Filename: "{app}\Beacon.exe"; Description: "Start NovaShield HUD"; Flags: nowait postinstall

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM Beacon.exe /IM Dotline.exe /IM Filee.exe"; Flags: runhidden; RunOnceId: "StopApps"

[Code]
// ponytail: v0.5.0 shipped a lock screen HUD that has since been removed. When updating over
// it, put the user's own picture back, remove its scheduled task, then delete the old program.
// Drop this block once nobody is left on 0.5.0.
procedure CurStepChanged(CurStep: TSetupStep);
var
  Old: String;
  Rc: Integer;
begin
  // Filee keeps its files open while it runs (Beacon closes it before an update; a manual
  // reinstall may not have).
  if (CurStep = ssInstall) and FileExists(ExpandConstant('{app}\filee\Filee.exe')) then
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Filee.exe', '', SW_HIDE, ewWaitUntilTerminated, Rc);
  Old := ExpandConstant('{app}\Lockscreen');
  if (CurStep = ssInstall) and FileExists(Old + '.exe') then
  begin
    Exec(Old + '.exe', '--restore', '', SW_HIDE, ewWaitUntilTerminated, Rc);
    DeleteFile(Old + '.exe');
    DeleteFile(Old + '.dll');
    DeleteFile(Old + '.deps.json');
    DeleteFile(Old + '.runtimeconfig.json');
  end;
end;
