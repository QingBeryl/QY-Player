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
Name: "assoc"; Description: "把 QY Player 注册为音频文件的播放器（右键「打开方式」与系统「默认应用」里可选）"; GroupDescription: "系统集成"; Flags: checkedonce
Name: "defaultassoc"; Description: "同时把 QY Player 设为这些格式的默认播放器（会覆盖当前的默认关联）"; GroupDescription: "系统集成"; Flags: unchecked

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

[Registry]
;
; ============================== 文件关联 ==============================
;
; 达到的三个效果：
;   1) 资源管理器里右键某个音频文件 →「打开方式」里能看到 QY Player；
;   2) 勾了"设为默认播放器"后，双击音频文件直接用本程序打开；
;   3) 多选一批文件、或右键一个文件夹，也能一次交给本程序，按顺序播放。
;
; 全部只写 HKCU，不写 HKLM，理由：
;   - 默认安装是"仅为我安装"（PrivilegesRequired=lowest），HKCU 必定可写，不弹 UAC；
;     写 HKLM 会让这种默认安装下的关联静默失败。
;   - 用户切到"为所有用户安装"时 HKCU 依然生效，只是关联对该用户可见，功能不受影响。
;   - 卸载时只清自己写的那一份，不会误删其他用户或其他播放器的关联。
;
; ProgID 统一命名 QYPlayer.<扩展名>，与安装脚本顶部的 AppId 同源，
; 便于在注册表里一眼认出哪些键是本程序留下的。
;
; 加密格式（ncm / kgm / kwm 等）刻意不注册：
; 它们要靠默认不随主程序分发的解密插件才能播，
; 若把它们也写成默认关联，用户双击后会得到一个打不开的文件。
;
; 「打开方式」候选列表里有一项就够了，为什么还写 Applications\ 那一组：
; 前者的名字来自 ProgID 的默认值，后者才是系统"默认应用"设置页、
; 以及"始终使用此应用打开"勾选框真正读取的位置，两者缺一不可。
;
; 每个 ProgID 都带 MultiSelectModel=Player：
; 这样按住 Ctrl 多选一批音乐再右键时，菜单里才会出现本程序，
; 并把一整批路径交给它（配合单实例转发，最终在一个窗口里按顺序播放）。
;

; ---------- 1) 每种格式一个 ProgID，并挂进该扩展名的「打开方式」候选 ----------
Root: HKCU; Subkey: "Software\Classes\QYPlayer.mp3"; ValueType: string; ValueName: ""; ValueData: "MP3 音频"; Flags: uninsdeletekey; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\QYPlayer.mp3"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Player"; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\QYPlayer.mp3\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\QYPlayer.mp3\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\.mp3\OpenWithProgids"; ValueType: string; ValueName: "QYPlayer.mp3"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc or defaultassoc

Root: HKCU; Subkey: "Software\Classes\QYPlayer.flac"; ValueType: string; ValueName: ""; ValueData: "FLAC 无损音频"; Flags: uninsdeletekey; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\QYPlayer.flac"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Player"; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\QYPlayer.flac\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\QYPlayer.flac\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\.flac\OpenWithProgids"; ValueType: string; ValueName: "QYPlayer.flac"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc or defaultassoc

Root: HKCU; Subkey: "Software\Classes\QYPlayer.wav"; ValueType: string; ValueName: ""; ValueData: "WAV 音频"; Flags: uninsdeletekey; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\QYPlayer.wav"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Player"; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\QYPlayer.wav\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\QYPlayer.wav\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\.wav\OpenWithProgids"; ValueType: string; ValueName: "QYPlayer.wav"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc or defaultassoc

Root: HKCU; Subkey: "Software\Classes\QYPlayer.m4a"; ValueType: string; ValueName: ""; ValueData: "M4A 音频"; Flags: uninsdeletekey; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\QYPlayer.m4a"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Player"; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\QYPlayer.m4a\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\QYPlayer.m4a\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\.m4a\OpenWithProgids"; ValueType: string; ValueName: "QYPlayer.m4a"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc or defaultassoc

Root: HKCU; Subkey: "Software\Classes\QYPlayer.aac"; ValueType: string; ValueName: ""; ValueData: "AAC 音频"; Flags: uninsdeletekey; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\QYPlayer.aac"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Player"; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\QYPlayer.aac\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\QYPlayer.aac\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\.aac\OpenWithProgids"; ValueType: string; ValueName: "QYPlayer.aac"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc or defaultassoc

Root: HKCU; Subkey: "Software\Classes\QYPlayer.ogg"; ValueType: string; ValueName: ""; ValueData: "OGG 音频"; Flags: uninsdeletekey; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\QYPlayer.ogg"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Player"; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\QYPlayer.ogg\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\QYPlayer.ogg\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\.ogg\OpenWithProgids"; ValueType: string; ValueName: "QYPlayer.ogg"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc or defaultassoc

Root: HKCU; Subkey: "Software\Classes\QYPlayer.opus"; ValueType: string; ValueName: ""; ValueData: "Opus 音频"; Flags: uninsdeletekey; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\QYPlayer.opus"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Player"; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\QYPlayer.opus\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\QYPlayer.opus\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\.opus\OpenWithProgids"; ValueType: string; ValueName: "QYPlayer.opus"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc or defaultassoc

Root: HKCU; Subkey: "Software\Classes\QYPlayer.wma"; ValueType: string; ValueName: ""; ValueData: "WMA 音频"; Flags: uninsdeletekey; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\QYPlayer.wma"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Player"; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\QYPlayer.wma\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\QYPlayer.wma\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\.wma\OpenWithProgids"; ValueType: string; ValueName: "QYPlayer.wma"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc or defaultassoc

Root: HKCU; Subkey: "Software\Classes\QYPlayer.ape"; ValueType: string; ValueName: ""; ValueData: "APE 无损音频"; Flags: uninsdeletekey; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\QYPlayer.ape"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Player"; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\QYPlayer.ape\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\QYPlayer.ape\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\.ape\OpenWithProgids"; ValueType: string; ValueName: "QYPlayer.ape"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc or defaultassoc

Root: HKCU; Subkey: "Software\Classes\QYPlayer.alac"; ValueType: string; ValueName: ""; ValueData: "ALAC 无损音频"; Flags: uninsdeletekey; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\QYPlayer.alac"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Player"; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\QYPlayer.alac\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\QYPlayer.alac\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\.alac\OpenWithProgids"; ValueType: string; ValueName: "QYPlayer.alac"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc or defaultassoc

; ---------- 2) Applications 登记：系统"默认应用"页与"始终使用此应用"读的就是这里 ----------
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#MyAppName}"; Flags: uninsdeletekey; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#MyAppExeName}"; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}"; ValueType: string; ValueName: "ApplicationCompany"; ValueData: "{#MyAppPublisher}"; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}\shell\open"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#MyAppName}"; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".mp3"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".flac"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".wav"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".m4a"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".aac"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".ogg"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".opus"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".wma"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".ape"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".alac"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc or defaultassoc

; ---------- 3) 目录右键：直接对一个文件夹用本程序播放（内部文件按名升序播完） ----------
Root: HKCU; Subkey: "Software\Classes\Directory\shell\QYPlayer"; ValueType: string; ValueName: ""; ValueData: "用 {#MyAppName} 播放"; Flags: uninsdeletekey; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\Directory\shell\QYPlayer"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#MyAppExeName},0"; Tasks: assoc or defaultassoc
Root: HKCU; Subkey: "Software\Classes\Directory\shell\QYPlayer\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: assoc or defaultassoc

; ---------- 4) 设为默认播放器（只有勾了该任务才写） ----------
;
; 为什么还要删 UserChoice：
; Win8 起系统把"用户选择的默认程序"记在
;   HKCU\...\Explorer\FileExts\.<ext>\UserChoice
; 并给它存了一份校验值。只要这个键还在，我们在 Software\Classes 下写的默认值
; 就会被系统判定为"非用户选择"而忽略，双击仍然走原来那个程序。
; 这里把 UserChoice 删掉，等于把选择权退回给系统，让它按我们写的默认值走。
; 这是一次性、可逆的动作：用户在"默认应用"里改回去即可。
Root: HKCU; Subkey: "Software\Classes\.mp3"; ValueType: string; ValueName: ""; ValueData: "QYPlayer.mp3"; Flags: uninsdeletevalue; Tasks: defaultassoc
Root: HKCU; Subkey: "Software\Classes\.flac"; ValueType: string; ValueName: ""; ValueData: "QYPlayer.flac"; Flags: uninsdeletevalue; Tasks: defaultassoc
Root: HKCU; Subkey: "Software\Classes\.wav"; ValueType: string; ValueName: ""; ValueData: "QYPlayer.wav"; Flags: uninsdeletevalue; Tasks: defaultassoc
Root: HKCU; Subkey: "Software\Classes\.m4a"; ValueType: string; ValueName: ""; ValueData: "QYPlayer.m4a"; Flags: uninsdeletevalue; Tasks: defaultassoc
Root: HKCU; Subkey: "Software\Classes\.aac"; ValueType: string; ValueName: ""; ValueData: "QYPlayer.aac"; Flags: uninsdeletevalue; Tasks: defaultassoc
Root: HKCU; Subkey: "Software\Classes\.ogg"; ValueType: string; ValueName: ""; ValueData: "QYPlayer.ogg"; Flags: uninsdeletevalue; Tasks: defaultassoc
Root: HKCU; Subkey: "Software\Classes\.opus"; ValueType: string; ValueName: ""; ValueData: "QYPlayer.opus"; Flags: uninsdeletevalue; Tasks: defaultassoc
Root: HKCU; Subkey: "Software\Classes\.wma"; ValueType: string; ValueName: ""; ValueData: "QYPlayer.wma"; Flags: uninsdeletevalue; Tasks: defaultassoc
Root: HKCU; Subkey: "Software\Classes\.ape"; ValueType: string; ValueName: ""; ValueData: "QYPlayer.ape"; Flags: uninsdeletevalue; Tasks: defaultassoc
Root: HKCU; Subkey: "Software\Classes\.alac"; ValueType: string; ValueName: ""; ValueData: "QYPlayer.alac"; Flags: uninsdeletevalue; Tasks: defaultassoc

Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.mp3\UserChoice"; ValueType: none; Flags: deletekey; Tasks: defaultassoc
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.flac\UserChoice"; ValueType: none; Flags: deletekey; Tasks: defaultassoc
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.wav\UserChoice"; ValueType: none; Flags: deletekey; Tasks: defaultassoc
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.m4a\UserChoice"; ValueType: none; Flags: deletekey; Tasks: defaultassoc
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.aac\UserChoice"; ValueType: none; Flags: deletekey; Tasks: defaultassoc
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.ogg\UserChoice"; ValueType: none; Flags: deletekey; Tasks: defaultassoc
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.opus\UserChoice"; ValueType: none; Flags: deletekey; Tasks: defaultassoc
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.wma\UserChoice"; ValueType: none; Flags: deletekey; Tasks: defaultassoc
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.ape\UserChoice"; ValueType: none; Flags: deletekey; Tasks: defaultassoc
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.alac\UserChoice"; ValueType: none; Flags: deletekey; Tasks: defaultassoc

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
