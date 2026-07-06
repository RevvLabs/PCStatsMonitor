; PC Stats Monitor — per-machine installer
; The app requires administrator at runtime (ring0 sensor access), so the installer
; is per-machine/admin too: the elevated uninstaller can then stop the running app
; and remove every file it created. One UAC prompt at install, one at uninstall.
; Launches after that go through a pre-authorized scheduled task (see [Run]/[Icons])
; so the app itself never re-prompts UAC.
;
; Bundles PawnIO (pawnio.eu) — LibreHardwareMonitor needs this signed kernel driver
; for CPU temp/clock/power (MSR access). Microsoft revoked the old WinRing0 signing
; certificate in 2024, so LHM can no longer load its own driver; PawnIO must be
; installed system-wide beforehand. Installed silently, first, before the app.

#define MyAppName "PC Stats Monitor"
; Version is normally injected by CI: ISCC /DMyAppVersion=x.y.z
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#define MyAppPublisher "RevvLabs"
#define MyAppExeName "PCStatsMonitor.exe"

[Setup]
AppId={{B7E7A3D1-4C22-4E8B-9A67-PCSTATSMON01}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\PCStatsMonitor
DisableProgramGroupPage=yes
PrivilegesRequired=admin
OutputDir=..\dist
OutputBaseFilename=PCStatsMonitor-Setup-{#MyAppVersion}
SetupIconFile=..\src\PCStatsMonitor.App\Assets\tray-icon.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Code]
// If an existing install is found (any version), silently uninstall it first so every
// install is a clean install — old files, launcher task, and logs are removed by the
// old uninstaller before the new files land.
const
  // AppId with the leading brace escaping resolved; Inno registers the uninstaller
  // under <AppId>_is1. Keep in sync with AppId in [Setup].
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{B7E7A3D1-4C22-4E8B-9A67-PCSTATSMON01}_is1';

function GetUninstallString(): String;
begin
  Result := '';
  if not RegQueryStringValue(HKLM, UninstallKey, 'UninstallString', Result) then
    RegQueryStringValue(HKCU, UninstallKey, 'UninstallString', Result);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  UninstStr: String;
  ResultCode: Integer;
  Tries: Integer;
begin
  Result := '';
  UninstStr := GetUninstallString();
  if UninstStr = '' then
    Exit; // no previous install — nothing to do

  UninstStr := RemoveQuotes(UninstStr);
  // /SILENT (not /VERYSILENT): shows the uninstaller's progress window so the user
  // can see the old version being removed; still asks no questions.
  if not Exec(UninstStr, '/SILENT /NORESTART /SUPPRESSMSGBOXES', '',
              SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode) then
  begin
    Result := 'Could not remove the previous version (failed to start its uninstaller). ' +
              'Please uninstall "PC Stats Monitor" manually, then run this setup again.';
    Exit;
  end;

  // Inno uninstallers copy themselves to %TEMP% and the original process returns
  // before removal finishes — wait until the uninstaller exe is actually gone
  // (up to ~15 s) so the new install doesn't race the old one's file deletion.
  Tries := 0;
  while FileExists(UninstStr) and (Tries < 30) do
  begin
    Sleep(500);
    Tries := Tries + 1;
  end;
end;

[Tasks]
; Both checked by default — omit the 'unchecked' flag to pre-check
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startmenuicon"; Description: "Create a &Start Menu shortcut"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; PCStatsMonitor.exe is a framework-dependent apphost stub — it needs PCStatsMonitor.dll
; and every dependency (Avalonia, Serilog, locale satellite folders, etc.) alongside it,
; not just the exe. Ship the whole publish output tree.
Source: "..\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; PawnIO installer — extracted to {tmp} only, run once during setup, not left on disk.
; {tmp} is wiped by Setup on exit regardless, so no dontcopy/ExtractTemporaryFile needed.
Source: "redist\PawnIO_setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
; Shortcuts trampoline through the scheduled task created in [Run] below instead of
; launching the exe directly — the task is pre-authorized with RunLevel=Highest at
; install time (one UAC prompt), so schtasks /run launches it elevated with no further
; prompt. IconFilename points at the real exe since schtasks.exe has no useful icon.
Name: "{autodesktop}\{#MyAppName}"; Filename: "{sys}\schtasks.exe"; Parameters: "/run /tn ""PCStatsMonitorLauncher"""; IconFilename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{autoprograms}\{#MyAppName}"; Filename: "{sys}\schtasks.exe"; Parameters: "/run /tn ""PCStatsMonitorLauncher"""; IconFilename: "{app}\{#MyAppExeName}"; Tasks: startmenuicon

[Run]
; Install PawnIO first — required for LHM's CPU sensors (see header comment).
; -install -silent is the documented silent switch (namazso/PawnIO.Setup); idempotent,
; safe to run even if already installed/up to date.
Filename: "{tmp}\PawnIO_setup.exe"; Parameters: "-install -silent"; StatusMsg: "Installing hardware sensor driver (PawnIO)..."; Flags: runhidden waituntilterminated

; Scheduled-task trampoline so the app doesn't re-prompt UAC on every launch (manifest
; requires admin — see header comment). Earlier attempt used PowerShell
; Register-ScheduledTask without an explicit Principal/LogonType: the task "succeeded"
; per schtasks but never actually started the process (LastTaskResult 0x8000809A).
; Fix: classic schtasks.exe /create with /it (interactive logon token — runs inside the
; user's desktop session, no stored password needed) and /rl highest. /ru is omitted so
; it defaults to whichever user is running this (elevated) install. /sc onlogon is
; required by schtasks (a schedule type is mandatory), but we don't want autostart at
; logon — and "schtasks /change /disable" is NOT the answer: a disabled TASK refuses
; manual /run too ("could not run because it is disabled", verified 2026-07-05). The
; task must stay enabled with only its TRIGGER disabled; schtasks.exe can't do that,
; so a PowerShell one-liner flips Triggers[0].Enabled instead. Same step also sets
; MultipleInstances=Parallel: the default IgnoreNew makes Task Scheduler silently
; ignore "/run" while the app (a still-running task instance) is open, so clicking
; the shortcut again would do nothing — Parallel lets the second exe start, signal
; the first instance's show-window pipe, and exit (single-instance guard).
Filename: "{sys}\schtasks.exe"; Parameters: "/create /tn ""PCStatsMonitorLauncher"" /tr ""\""{app}\{#MyAppExeName}\"""" /sc onlogon /rl highest /it /f"; StatusMsg: "Registering launcher task..."; Flags: runhidden waituntilterminated
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""$t = Get-ScheduledTask -TaskName 'PCStatsMonitorLauncher'; $t.Triggers[0].Enabled = $false; $t.Settings.MultipleInstances = 'Parallel'; Set-ScheduledTask -InputObject $t"""; StatusMsg: "Configuring launcher task..."; Flags: runhidden waituntilterminated

Filename: "{sys}\schtasks.exe"; Parameters: "/run /tn ""PCStatsMonitorLauncher"""; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent runhidden

[UninstallRun]
; App hides to tray instead of closing — force-stop it so files aren't locked
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#MyAppExeName}"; Flags: runhidden; RunOnceId: "KillApp"
; Remove the launcher task created in [Run]
Filename: "{sys}\schtasks.exe"; Parameters: "/delete /f /tn ""PCStatsMonitorLauncher"""; Flags: runhidden; RunOnceId: "DelTask"

[UninstallDelete]
; Everything the app writes at runtime: logs under %LocalAppData%\PCStatsMonitor
Type: filesandordirs; Name: "{localappdata}\PCStatsMonitor"
; .NET single-file native-lib extraction cache
Type: filesandordirs; Name: "{localappdata}\Temp\.net\PCStatsMonitor"
; Install dir itself
Type: filesandordirs; Name: "{app}"
