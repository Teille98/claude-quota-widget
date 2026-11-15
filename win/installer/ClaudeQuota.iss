; Windows installer for Claude Quota (Inno Setup 6).
; Built by win/build-installer.ps1, which first publishes the app (self-contained, no .NET to install).
; Per-user install: no administrator rights needed.

#define AppName "Claude Quota"
#ifndef AppVersion
  #define AppVersion "1.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\ClaudeQuota.Win\bin\publish"
#endif

[Setup]
AppId={{6C1E0F7A-3B7E-4C55-9F1D-2A6E7C9B4D10}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Teille98
AppPublisherURL=https://github.com/Teille98/claude-quota-widget
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\..\dist
OutputBaseFilename=ClaudeQuota-Setup-{#AppVersion}
SetupIconFile=..\ClaudeQuota.Win\app.ico
UninstallDisplayIcon={app}\ClaudeQuota.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
AppMutex=ClaudeQuotaWidget
ShowLanguageDialog=auto

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"

[CustomMessages]
en.TaskAutostart=Start Claude Quota when Windows starts
fr.TaskAutostart=Lancer Claude Quota au démarrage de Windows
en.TaskDesktopIcon=Create a desktop shortcut
fr.TaskDesktopIcon=Créer un raccourci sur le Bureau
en.LaunchApp=Launch Claude Quota
fr.LaunchApp=Lancer Claude Quota
en.DeleteData=Also delete Claude Quota data (claude.ai session, last values, preferences)?
fr.DeleteData=Supprimer aussi les données de Claude Quota (session claude.ai, dernières valeurs, préférences) ?

[Tasks]
Name: "autostart"; Description: "{cm:TaskAutostart}"
Name: "desktopicon"; Description: "{cm:TaskDesktopIcon}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\ClaudeQuota.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\ClaudeQuota.exe"; Tasks: desktopicon

[Registry]
; Same key as the app's "Start with Windows" menu option.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "ClaudeQuotaWidget"; ValueData: """{app}\ClaudeQuota.exe"""; Tasks: autostart; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "ClaudeQuotaWidget"; Flags: dontcreatekey uninsdeletevalue

[Run]
Filename: "{app}\ClaudeQuota.exe"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[Code]
// The app lives in the notification area: close it before replacing its files.
procedure StopApp();
var Code: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM ClaudeQuota.exe /F', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopApp();
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  StopApp();
  Result := True;
end;

// Data: cached values, preferences and the WebView2 profile (claude.ai session).
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Dir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    Dir := ExpandConstant('{localappdata}\ClaudeQuotaWidget');
    if DirExists(Dir) and (UninstallSilent or (MsgBox(CustomMessage('DeleteData'), mbConfirmation, MB_YESNO) = IDYES)) then
      DelTree(Dir, True, True, True);
  end;
end;
