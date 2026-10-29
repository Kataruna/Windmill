; Inno Setup script for Windmill. Build the app first, then the installer:
;   dotnet publish -p:PublishProfile=dist
;   ISCC installer\Windmill.iss
; Output: dist\WindmillSetup-<version>.exe

#define AppExe SourcePath + "..\dist\Windmill.exe"
#if !FileExists(AppExe)
  #error dist\Windmill.exe not found. Run "dotnet publish -p:PublishProfile=dist" first.
#endif
; Major.minor.patch from the exe, which gets it from <Version> in Windmill.csproj.
#define FullVersion GetVersionNumbersString(AppExe)
#define AppVersion Copy(FullVersion, 1, RPos(".", FullVersion) - 1)

[Setup]
; Never change AppId: Windows uses it to recognise upgrades and the uninstaller.
AppId={{292B306B-BBDE-4251-8A7F-B51AC1D57F23}
AppName=Windmill
AppVersion={#AppVersion}
AppVerName=Windmill {#AppVersion}
AppPublisher=Thanayos Siratranont
AppPublisherURL=https://github.com/Kataruna/Windmill
AppSupportURL=https://github.com/Kataruna/Windmill/issues
VersionInfoVersion={#FullVersion}
; Per-user by default (no admin prompt, installs to %LOCALAPPDATA%\Programs); the user can pick all users instead.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\Windmill
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
SetupIconFile=..\Windmill.ico
UninstallDisplayIcon={app}\Windmill.exe
UninstallDisplayName=Windmill
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
OutputDir=..\dist
OutputBaseFilename=WindmillSetup-{#AppVersion}

[Tasks]
; Per-user only: the app's own setting lives in the signed-in user's HKCU, but an admin install's HKCU can be a
; different (elevating) account's. "Start with Windows" in Settings still works after an all-users install.
Name: "startup"; Description: "Start Windmill when I sign in"; GroupDescription: "Options:"; Check: not IsAdminInstallMode
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\dist\Windmill.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Windmill"; Filename: "{app}\Windmill.exe"
Name: "{autodesktop}\Windmill"; Filename: "{app}\Windmill.exe"; Tasks: desktopicon

[Registry]
; Same value the app's own "Start with Windows" setting writes (Startup.cs), so the two stay in sync.
; Always removed on uninstall, even if it was turned on later from inside the app.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Windmill"; \
    ValueData: """{app}\Windmill.exe"""; Tasks: startup
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "Windmill"; ValueType: none; \
    Flags: uninsdeletevalue dontcreatekey

[Run]
Filename: "{app}\Windmill.exe"; Description: "Start Windmill now"; Flags: nowait postinstall skipifsilent
; A silent upgrade (/SILENT, /VERYSILENT, winget) skips the entry above, so start it again if setup closed it.
Filename: "{app}\Windmill.exe"; Flags: nowait runasoriginaluser; Check: WizardSilent and WasRunning

[Code]
// Windmill lives in the tray with no window, so the Restart Manager can't ask it to close.
// End it directly before replacing or removing the exe.
var
  Stopped: Boolean;

// True if Windmill was running and got closed.
function StopWindmill(): Boolean;
var
  Code: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im Windmill.exe', '', SW_HIDE, ewWaitUntilTerminated, Code)
    and (Code = 0);
end;

function WasRunning(): Boolean;
begin
  Result := Stopped;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Stopped := StopWindmill();
  Result := '';
end;

// Only once the user has confirmed: closing it in InitializeUninstall would leave it closed if they cancel.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    StopWindmill();
end;
