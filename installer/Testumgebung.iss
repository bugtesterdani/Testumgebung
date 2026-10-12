; Installer der Testumgebung (Inno Setup 6). Aufruf über build\publish.ps1 -Installer.
;   ISCC /DAppVersion=1.0.0 /DSourceDir=..\artifacts\publish\Testumgebung /DOutputDir=..\artifacts\installer installer\Testumgebung.iss
;
; Rechte:  %ProgramData%\Testumgebung (pruefplaetze.json, zugang.json) – Administratoren/SYSTEM Vollzugriff, Benutzer nur lesen.
;          Benutzerdaten (einstellungen.json, Outbox, Releases, Zwischenaufzeichnung) liegen je Benutzer in %LOCALAPPDATA%.
; Deinstallation entfernt KEINE Konfiguration, Ergebnisse oder Outbox (Nachweise bleiben erhalten).

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish\Testumgebung"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif
#ifndef SelfContained
  #define SelfContained "false"
#endif

[Setup]
AppId={{6C0B6C55-7E0A-4D53-9A8C-1D6F2E3B9A41}
AppName=Testumgebung
AppVersion={#AppVersion}
AppPublisher=Testumgebung
DefaultDirName={autopf}\Testumgebung
DefaultGroupName=Testumgebung
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=Testumgebung-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
WizardStyle=modern
UninstallDisplayIcon={app}\Testumgebung.Desktop.exe
SetupLogging=yes

[Languages]
Name: "de"; MessagesFile: "compiler:Languages\German.isl"

[Tasks]
Name: "desktopicon"; Description: "Verknüpfung auf dem Desktop"; GroupDescription: "Zusätzliche Symbole:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Dirs]
Name: "{commonappdata}\Testumgebung"; Flags: uninsneveruninstall

[Icons]
Name: "{group}\Testumgebung"; Filename: "{app}\Testumgebung.Desktop.exe"
Name: "{group}\Handbuch Werker"; Filename: "{app}\docs\handbuch\werker.md"
Name: "{group}\Handbuch Administrator"; Filename: "{app}\docs\handbuch\administrator.md"
Name: "{autodesktop}\Testumgebung"; Filename: "{app}\Testumgebung.Desktop.exe"; Tasks: desktopicon

[Run]
; Rechnerkonfiguration: Vererbung lösen, Administratoren (S-1-5-32-544) und SYSTEM (S-1-5-18) voll, Benutzer (S-1-5-32-545) lesen.
; SIDs statt Namen – funktioniert auch auf deutschem Windows („Administratoren“, „Benutzer“).
Filename: "{sys}\icacls.exe"; Parameters: """{commonappdata}\Testumgebung"" /inheritance:r /grant:r *S-1-5-32-544:(OI)(CI)F *S-1-5-18:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX"; Flags: runhidden waituntilterminated; StatusMsg: "Rechte für die Rechnerkonfiguration setzen …"
Filename: "{app}\Testumgebung.Desktop.exe"; Description: "Testumgebung starten"; Flags: nowait postinstall skipifsilent unchecked

[Code]
// Framework-abhängige Veröffentlichung braucht die .NET 10 Desktop Runtime (x64).
function HasDesktopRuntime(): Boolean;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if RegGetValueNames(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if Copy(Names[I], 1, 3) = '10.' then Result := True;
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if ('{#SelfContained}' <> 'true') and (not HasDesktopRuntime()) then
    Result := MsgBox('Die .NET 10 Desktop Runtime (x64) ist nicht installiert.' + #13#10 +
      'Bitte zuerst von https://dotnet.microsoft.com/download/dotnet/10.0 installieren.' + #13#10#13#10 +
      'Trotzdem fortfahren?', mbConfirmation, MB_YESNO) = IDYES;
end;
