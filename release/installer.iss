#ifndef StageDir
  #error StageDir is required
#endif
#ifndef ReleaseVersion
  #error ReleaseVersion is required
#endif
#ifndef ReleaseDir
  #error ReleaseDir is required
#endif
#ifndef RuntimeUrl
  #error Pinned runtime download parameters are required
#endif

[Setup]
AppId={{08BD293A-A8E1-43C7-9816-638315DC028B}
AppName={cm:ManagerName}
AppVersion={#ReleaseVersion}
DefaultDirName={localappdata}\Programs\PhantomBladeModManager
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.17763
OutputDir={#ReleaseDir}
OutputBaseFilename=PhantomBladeModManager-{#ReleaseVersion}-Setup
Compression=lzma2/ultra64
SolidCompression=yes
LZMANumBlockThreads=2
WizardStyle=modern
UninstallDisplayIcon={app}\PhantomBladeModManager.exe
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Files]
; StageDir is assembled from the publish output and verified package manifests.
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Refresh only bundled packages; preserve manager settings and installation records.
Type: filesandordirs; Name: "{app}\mods\features\phantom-blade-sprint\dist\runtime"
Type: filesandordirs; Name: "{app}\mods\features\phantom-blade-map\dist\runtime"
Type: filesandordirs; Name: "{app}\mods\features\phantom-blade-infinite-talisman\dist\runtime"
Type: filesandordirs; Name: "{app}\mods\features\phantom-blade-quick-actions\dist\runtime"
Type: filesandordirs; Name: "{app}\mods\features\phantom-blade-targeting\dist\runtime"

[Icons]
Name: "{autoprograms}\{cm:ManagerName}"; Filename: "{app}\PhantomBladeModManager.exe"; WorkingDir: "{app}"

[Run]
Filename: "{app}\PhantomBladeModManager.exe"; Description: "{cm:LaunchProgram,{cm:ManagerName}}"; Flags: nowait postinstall skipifsilent; Check: CanLaunchManager

[CustomMessages]
english.ManagerName=Phantom Blade Zero Mod Manager
chinesesimplified.ManagerName=影之刃零 Mod 管理器
english.RuntimeDownload=Downloading .NET Desktop Runtime
chinesesimplified.RuntimeDownload=正在下载 .NET 桌面运行时
english.RuntimeInstall=Installing .NET Desktop Runtime
chinesesimplified.RuntimeInstall=正在安装 .NET 桌面运行时
english.RuntimeFailed=.NET installation failed (%1). Retry setup.
chinesesimplified.RuntimeFailed=.NET 安装失败（%1），请重试。
english.RuntimeRestart=Restart Windows, then run setup again.
chinesesimplified.RuntimeRestart=请重启 Windows 后再次运行安装包。
english.RuntimeRequired=.NET Desktop Runtime 10 (x64) will be downloaded and installed. Windows authorization is required.
chinesesimplified.RuntimeRequired=将下载并安装 .NET 10 桌面运行时（x64），需要 Windows 授权。

[Code]
var
  RuntimeDownloadPage: TDownloadWizardPage;
  RuntimeInstallPage: TOutputMarqueeProgressWizardPage;
  RuntimeNeedsRestart: Boolean;

function LatestRuntimePatch(const Directory, RequiredFile: String): Integer;
var
  Entry: TFindRec;
  Patch: Integer;
begin
  Result := -1;
  if FindFirst(AddBackslash(Directory) + '10.0.*', Entry) then begin
    try
      repeat
        Patch := StrToIntDef(Copy(Entry.Name, 6, MaxInt), -1);
        if (Patch > Result) and
          FileExists(AddBackslash(Directory) + Entry.Name + '\' + RequiredFile) then
          Result := Patch;
      until not FindNext(Entry);
    finally
      FindClose(Entry);
    end;
  end;
end;

function HasDesktopRuntime: Boolean;
var
  Location: String;
  DesktopPatch, CorePatch: Integer;
begin
  if not RegQueryStringValue(HKLM32, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'InstallLocation', Location) then
    Location := ExpandConstant('{commonpf64}\dotnet');
  Location := AddBackslash(Location);
  DesktopPatch := LatestRuntimePatch(Location + 'shared\Microsoft.WindowsDesktop.App', 'System.Windows.Forms.dll');
  CorePatch := LatestRuntimePatch(Location + 'shared\Microsoft.NETCore.App', 'coreclr.dll');
  Result := (DesktopPatch >= 0) and (CorePatch >= DesktopPatch) and
    (LatestRuntimePatch(Location + 'host\fxr', 'hostfxr.dll') >= 0);
end;

procedure InitializeWizard;
begin
  RuntimeDownloadPage := CreateDownloadPage(CustomMessage('RuntimeDownload'), '', nil);
  RuntimeDownloadPage.ShowBaseNameInsteadOfUrl := True;
  RuntimeInstallPage := CreateOutputMarqueeProgressPage(CustomMessage('RuntimeInstall'), '');
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo;
  if not HasDesktopRuntime then
    Result := Result + NewLine + NewLine + CustomMessage('RuntimeRequired');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
begin
  Result := '';
  if HasDesktopRuntime then Exit;
  RuntimeDownloadPage.Clear;
  RuntimeDownloadPage.Add('{#RuntimeUrl}', '{#RuntimeFile}', '{#RuntimeSha256}');
  RuntimeDownloadPage.Show;
  try
    try
      RuntimeDownloadPage.Download;
    except
      Result := GetExceptionMessage;
    end;
  finally
    RuntimeDownloadPage.Hide;
  end;
  if Result <> '' then Exit;
  RuntimeInstallPage.Show;
  try
    RuntimeInstallPage.Animate;
    if not ShellExec('runas', ExpandConstant('{tmp}\{#RuntimeFile}'),
      '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then begin
      Result := FmtMessage(CustomMessage('RuntimeFailed'), [SysErrorMessage(ExitCode)]);
      Exit;
    end;
  finally
    RuntimeInstallPage.Hide;
  end;
  RuntimeNeedsRestart := ExitCode = 3010;
  if (ExitCode <> 0) and not RuntimeNeedsRestart then
    Result := FmtMessage(CustomMessage('RuntimeFailed'), [IntToStr(ExitCode)])
  else if not HasDesktopRuntime then begin
    NeedsRestart := RuntimeNeedsRestart;
    if NeedsRestart then Result := CustomMessage('RuntimeRestart')
    else Result := FmtMessage(CustomMessage('RuntimeFailed'), [IntToStr(ExitCode)]);
  end;
end;

function NeedRestart: Boolean;
begin
  Result := RuntimeNeedsRestart;
end;

function CanLaunchManager: Boolean;
begin
  Result := not RuntimeNeedsRestart;
end;
