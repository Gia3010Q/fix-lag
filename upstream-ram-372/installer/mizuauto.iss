#ifndef AppVersion
  #define AppVersion "3.7.2.2"
#endif
[Setup]
AppId={{66F908FC-B7D0-45DA-9050-C98E1095A3D1}
AppName=Mizuauto
AppVersion={#AppVersion}
AppPublisher=Mizuauto
DefaultDirName={localappdata}\Programs\Mizuauto
DefaultGroupName=Mizuauto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
AppMutex=Local\Mizuauto.Manager.66f908fc-b7d0-45da-9050-c98e1095a3d1
CloseApplications=no
LicenseFile=..\LICENSE
OutputDir=..\artifacts\installer
OutputBaseFilename=Mizuauto-Setup-{#AppVersion}-x64
Compression=lzma2
SolidCompression=yes
UninstallDisplayIcon={app}\mizuauto.exe

[Tasks]
Name: "desktopicon"; Description: "Create a Mizuauto desktop shortcut"; Flags: unchecked

[Files]
Source: "..\artifacts\win-x64\mizuauto.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\artifacts\win-x64\mizuauto_engine.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\artifacts\win-x64\*"; DestDir: "{app}"; Excludes: "mizuauto.exe,mizuauto_engine.dll,Roblox Account Manager.exe,RBX Alt Manager.exe,Auto Update.exe,AU.exe,AccountData.json,AccountData.json.backup,RAMSettings.ini,RAMTheme.ini,RecentGames.json,FavoriteGames.json,AccountControlData.json,*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\STANDALONE.vi.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Mizuauto"; Filename: "{app}\mizuauto.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Mizuauto"; Filename: "{app}\mizuauto.exe"; WorkingDir: "{app}"; Tasks: desktopicon

; No UninstallDelete entries: the user's Mizuauto profile survives uninstall.
[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if FileExists(ExpandConstant('{app}\Roblox Account Manager.exe')) or
     FileExists(ExpandConstant('{app}\RBX Alt Manager.exe')) then
    Result := 'Choose a separate Mizuauto folder. Do not install into the original Roblox Account Manager folder.';
end;
