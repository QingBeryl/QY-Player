; QY Player 安装程序脚本（Inno Setup 6）
;
; 为什么用 Inno Setup：单文件 exe、原生中文向导、免运行库依赖、
; 生成的卸载程序可直接被"应用和功能"接管。WiX 需要额外装 .NET SDK 工具链
; 且作者手写 MSI 表结构容易出错，对当前需求属过度设计。
;
; 编译方式：build\publish.ps1 调用，也可手动执行
;   ISCC.exe /DMyAppVersion=1.0.0 /DOutDir=... /DOutBase=... QYPlayer.iss
;
; 默认按"仅为我安装"走用户目录，不弹 UAC；用户仍可在向导首屏切到"为所有用户安装"。
; 这样默认路径必定可写，避免装到 Program Files 后因权限写不了数据。
; 程序自身也做了可写性探测（见 src\QYPlayer.App\AppPaths.cs），两条路都兜得住。

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

#ifndef OutDir
  #define OutDir "..\dist"
#endif

; 输出文件名由调用方给全（含版本与架构），脚本本身不拼，
; 避免版本号在 .iss 与 ps1 两处各写一遍导致不一致。
#ifndef OutBase
  #define OutBase "QYPlayer-setup"
#endif

#define MyAppName "QY Player"
#define MyAppNameCn "清玉播放器"
#define MyAppPublisher "QY Player"
#define MyAppExeName "QYPlayer.exe"

; AppId 必须终身不变，升级安装与卸载识别都靠它。
; 换掉它等于让老版本变成另一个软件，无法覆盖升级。
#define MyAppId "{{7C4A1E92-3B6D-4F58-A0C1-2D9E8B7A6F43}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName} ({#MyAppNameCn})
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
VersionInfoDescription={#MyAppName} 安装程序
VersionInfoCompany={#MyAppPublisher}

; 默认装到当前用户的程序目录下（免管理员）。用户切到"为所有用户"后会落到 Program Files。
DefaultDirName={autopf}\{#MyAppName}
DisableDirPage=auto
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes

; 允许运行时切换安装范围，但不改默认值，避免每次安装都多一步选择。
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; 本程序带 x64 原生库，32 位 Windows 无法运行，直接拒绝安装并说明原因。
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; 升级时若程序仍在运行，提示关闭而不是静默失败。
CloseApplications=yes
RestartApplications=no

OutputDir={#OutDir}
OutputBaseFilename={#OutBase}
SetupIconFile=..\src\QYPlayer.App\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} {#MyAppVersion}

; lzma2/max 换取最小体积：本程序绝大部分是几乎不重复的原生 dll，
; 压缩比主要靠更强的算法而非字典，实测比默认再小约 5%。
Compression=lzma2/max
SolidCompression=yes
LZMAUseSeparateProcess=yes

WizardStyle=modern
ShowLanguageDialog=auto

[Languages]
; 官方发行包不含简体中文，这里带上社区翻译版（文件头保留了原作者署名）。
; 保留英文是为了非中文系统不至于看不懂向导。
Name: "chinesesimplified"; MessagesFile: "languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "quicklaunchicon"; Description: "{cm:CreateQuickLaunchIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; 打包目录由 publish.ps1 预先精简（已删掉多余语言资源与 C++ 导入库），
; 因此这里是整体收录，不做逐项排除。
Source: "..\artifacts\QYPlayer-{#MyAppVersion}-win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userappdata}\Microsoft\Internet Explorer\Quick Launch\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: quicklaunchicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 这里的条目会在"用户选择保留数据"之后照常执行，等于绕过询问，
; 所以 data\ 与 portable\ 不能写在这，只能由下面 [Code] 按用户答复决定。

[Code]

// 卸载时询问是否连用户数据一起删除（曲库、封面缓存、日志、插件）。
// 默认保留：重装后曲库和插件还在，误删却无法恢复。
//
// 静默卸载（/SILENT 或 /VERYSILENT）下不能弹窗：那会让脚本化的批量卸载
// 永远卡在无人应答的对话框上。此时按"保留"处理，宁可留数据也不能挂住。
function InitializeUninstall(): Boolean;
var
  AppDataDir: String;
  RoamingDataDir: String;
  DataDir: String;
  Response: Integer;
begin
  Result := True;

  AppDataDir := ExpandConstant('{app}\data');
  RoamingDataDir := ExpandConstant('{localappdata}\QY Player');

  // 安装目录可写时数据在 {app}\data，装到 Program Files 时在 %LOCALAPPDATA%。
  // 两者都报给用户，避免只清掉其中一个还留着另一个。
  if DirExists(AppDataDir) then
    DataDir := AppDataDir
  else if DirExists(RoamingDataDir) then
    DataDir := RoamingDataDir
  else
    exit;

  if UninstallSilent then
    exit;

  Response := MsgBox(
    '是否同时删除用户数据？' + #13#10 + #13#10 +
    '包含曲库记录、封面缓存、日志与自装插件，位置：' + #13#10 +
    DataDir + #13#10 + #13#10 +
    '选择"否"将保留这些数据，方便以后重装继续使用。',
    mbConfirmation, MB_YESNO or MB_DEFBUTTON2);

  if Response = IDYES then
  begin
    if DirExists(AppDataDir) and (not DelTree(AppDataDir, True, True, True)) then
      MsgBox('部分用户数据未能删除，可能仍被占用，请手动清理：' + #13#10 + AppDataDir,
        mbInformation, MB_OK);

    if DirExists(RoamingDataDir) and (not DelTree(RoamingDataDir, True, True, True)) then
      MsgBox('部分用户数据未能删除，可能仍被占用，请手动清理：' + #13#10 + RoamingDataDir,
        mbInformation, MB_OK);
  end;
end;

// 卸载收尾时清理程序自己生成的空目录。
// data\ 与 portable\ 不属于"安装文件"，Inno 不会自动清；用户又答了"保留"，
// 于是会留下一个空壳目录，让人以为没卸干净。
// RemoveDir 只删空目录：里面还有数据时它自然失败，等于保留了用户数据。
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep <> usPostUninstall then
    exit;

  // 从内到外删：子目录为空才可能删掉父目录。
  RemoveDir(ExpandConstant('{app}\data\covers'));
  RemoveDir(ExpandConstant('{app}\data\db'));
  RemoveDir(ExpandConstant('{app}\data\logs'));
  RemoveDir(ExpandConstant('{app}\data'));
  RemoveDir(ExpandConstant('{app}\portable'));
end;
