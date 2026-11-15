# AGENT.MD — SubtitleMerger 工程说明（供后续 AI / 开发者修改）

> 本文件由一次「全项目代码审查 + BUG 修复 + 构建/自检/发布验证」后整理，目的是让后续修改者
> 不用重新通读 2k+ 行代码就能定位功能、复现验证、避开已知陷阱。
> 项目基线：Python 3/Tkinter 版 v1.1 → C# / WinUI 3 等价重构；业务行为、配置项、文案逐项对齐。

---

## 1. 项目定位

批量把外挂字幕合进视频：MKV/MP4 输出、软字幕（内挂多轨）/硬字幕（烧录）、拖放添加、
按文件名自动匹配视频与字幕、GB18030/Big5/Shift-JIS/UTF-16 等字幕自动转 UTF-8、默认不重编码。

**所有行为基线**以 `docs/01-功能清单.md` 为准；`docs/02-功能对照表.md` 是 Python→C# 的逐项映射，
`docs/04-替代方案与交互差异.md` 列出了所有「有意与旧版不同」的地方（D1–D17），
**不要把这些显式差异当成 BUG 改回去**，例如：

- `container="same"` + 源为 MP4 时仍用 `-c:s copy`；
- 硬字幕 `plan.Vcodec` 仍记 `copy`，故不校验实际编码；
- `forced` 覆盖 `default`；`overwrite` 不持久化；
- 带连字符的语言键（`zh-cn`）在 tokenize 后永远命中不到；
- 字幕对话框没有「启用」复选框（文案沿用旧版）。

配置文件 `%USERPROFILE%\.subtitle_merger.json` 与 Python 版双向兼容，19 个键的键名/类型/格式不能改。
运行时唯一外部依赖是 `ffmpeg` / `ffprobe`（硬字幕还需 `libass`），定位顺序见 `ToolLocator.Locate`。

---

## 2. 代码结构

```
SubtitleMerger/
├── SubtitleMerger.sln                 # 3 个项目（Core / App / SelfTest）
├── README.md                          # 用户文档（功能、FAQ、构建命令）
├── AGENT.MD                           # 本文件
├── app.ico                            # 图标（App.csproj 以 Content 链到 Assets\app.ico）
├── docs/                              # 5 份迁移交付文档（行为基线）
└── src/
    ├── SubtitleMerger.Core/           # net8.0 类库：纯业务逻辑，禁止引用 WinRT/UI
    │   ├── Constants.cs               #   常量、语言表、简繁字频表、噪声正则、默认值
    │   ├── Models/
    │   │   ├── SubTrack.cs            #   字幕轨（Path/Lang/Title/Default/Forced/Kind/Encoding/Enabled）
    │   │   ├── Job.cs                 #   任务（Video/Subs/Output/State/Progress/VerifyOk…）
    │   │   ├── Options.cs             #   一次批处理的全部选项（含 ReplaceSource）
    │   │   ├── MediaInfo.cs           #   ffprobe 结果 + Probed 标记
    │   │   └── OutputPlan.cs          #   build_command 的预期结构 + 校验结果/能力探测结果
    │   └── Services/
    │       ├── AppConfig.cs           #   JSON 配置读写（保留未知键、字符串化数值）
    │       ├── ToolLocator.cs         #   ffmpeg/ffprobe 定位、能力/版本探测
    │       ├── ProcessUtil.cs         #   子进程 RunAsync（超时/取消/Kill 进程树/管道收尾）、Which
    │       ├── FileCollector.cs       #   文件/文件夹收集、去重、跳过隐藏子目录、排序
    │       ├── NameNormalizer.cs      #   旧版 tokenize/normalize/similarity + 1.1 Analyze 结构化切分
    │       ├── SequenceMatcher.cs     #   Python difflib.SequenceMatcher.ratio() 等价移植
    │       ├── SubtitleMatcher.cs     #   1.1 匹配打分（标题×0.85 + 词重合×0.15、集数/目录约束、竞争）
    │       ├── EncodingService.cs     #   编码检测/打分/转 UTF-8 缓存/ASS [Script Info] 修复/硬字幕副本
    │       ├── MediaProbe.cs          #   probe_streams / probe_subtitle / probe_many（有界并发）
    │       ├── CommandBuilder.cs      #   输出路径、软/硬字幕 ffmpeg 命令、plan
    │       ├── FfmpegRunner.cs        #   -progress 解析、stderr 环形缓冲、取消即 Kill
    │       ├── OutputVerifier.cs      #   流结构/时长/体积/开头结尾解码完整性校验
    │       ├── Formatting.cs          #   时长/体积/命令回显/CSV 数值格式化（InvariantCulture）
    │       └── BatchProcessor.cs      #   批量编排：IBatchEvents 6 类事件、Channel 按完成顺序回调
    ├── SubtitleMerger.App/            # net8.0-windows10.0.19041.0 / win-x64 / WinUI 3 unpackaged
    │   ├── App.xaml(.cs)              #   入口、资源字典、--smoke 验收钩子、CrashGuard 安装
    │   ├── app.manifest               #   requireAdministrator + PerMonitorV2 + longPathAware
    │   ├── Views/
    │   │   ├── MainWindow.xaml(.cs)   #   窗口外壳、IUiServices 实现、拖放、文件对话框、关闭确认
    │   │   └── SubtitleTrackDialog.xaml(.cs)  # 字幕轨编辑（候选/已选/语言/默认/强制/排序）
    │   ├── ViewModels/
    │   │   ├── MainViewModel.cs       #   全部状态与命令、IBatchEvents 实现、日志/进度合并刷新
    │   │   └── JobViewModel.cs        #   任务行 9 列绑定、状态文本/配色
    │   └── Services/
    │       ├── IUiServices.cs         #   ViewModel 与 WinRT 之间的窄接口
    │       ├── ClassicDialogs.cs      #   提升进程下的 Win32 GetOpenFileNameW/Save/SHBrowseForFolder
    │       ├── CrashGuard.cs          #   未处理异常/SEH 落盘 %TEMP%\subtitle_merger_crash.txt
    │       ├── DragDropUnblocker.cs   #   UIPI 消息放行（提升进程接收拖放）
    │       ├── NativeWindowHelper.cs  #   WM_GETMINMAXINFO 最小尺寸 + WM_DROPFILES 兜底
    │       └── StateToBrushConverter.cs # 状态配色 + Hex/文件名转换器
    └── SubtitleMerger.SelfTest/       # net8.0 控制台，仅引用 Core（不依赖 WinUI）
        ├── Program.cs                 #   A 逻辑等价 / B ffmpeg 端到端 / C 布局 / D 健壮性 / E 替换 / F 并发隔离
        └── Reference/reference.json  #   原 Python core.py 产出的冻结基准值，不能随意改
```

### 线程模型（改并发代码前必读）

- `BatchProcessor` 用 `SemaphoreSlim(workers)` 限流 + `Channel<Job>` 按完成顺序回调 `JobDone`；
- 所有 `IBatchEvents` 回调可在任意线程触发，`MainViewModel` 统一经 `IUiServices.RunOnUiThread` 投递；
- 日志后台入队、UI 侧合并刷新并限制 4000 行；进度用「脏行集合 + 一次 UI 刷新」合并；
- `FfmpegRunner` 注册 `CancellationToken.Register` 立即 `Kill(entireProcessTree)`；
- `ProcessUtil` 超时/取消时 `KillTree` + `SettleAsync` 等管道读取收尾（不等就会泄漏句柄）。

---

## 3. 构建 / 运行 / 验证

```powershell
dotnet build SubtitleMerger.sln -c Release                 # 0 Warning / 0 Error
dotnet run --project src/SubtitleMerger.SelfTest/SubtitleMerger.SelfTest.csproj -c Release
dotnet run --project src/SubtitleMerger.SelfTest/SubtitleMerger.SelfTest.csproj -c Release -- --skip-e2e
dotnet publish src/SubtitleMerger.App/SubtitleMerger.App.csproj -c Release -o dist/SubtitleMerger
dist/SubtitleMerger/SubtitleMerger.exe --smoke             # 退出码 0 + %TMP%/subtitle_merger_smoke.txt
```

`--smoke` / `--layout-report` / `--dialog-report` / `--dir <路径>` 都是验收钩子，正常双击不触发。

**本机环境注意**：`C:\Users\LocalAcc\AppData\Local\Temp` 对部分 .NET 进程创建目录会抛
`UnauthorizedAccessException`（PowerShell 直接创建正常）。跑自检时把 `TMP`/`TEMP` 指到工作区目录：

```powershell
$tmp = Join-Path (Get-Location) '.workbuddy\selftest-tmp'
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
$env:TMP = $tmp; $env:TEMP = $tmp
& src\SubtitleMerger.SelfTest\bin\Release\net8.0\SubtitleMerger.SelfTest.exe
```

`TreatWarningsAsErrors=true`（Core/App）；`CommunityToolkit.Mvvm` 固定 8.3.2（8.4.x 在 WinUI 3
字段式 `[ObservableProperty]` 会报 54 条 MVVMTK0045）；`Microsoft.WindowsAppSDK` 2.6.5；
`System.Text.Encoding.CodePages` 9.0.0 用于旧代码页，注册在 `EncodingService` 静态构造里。

---

## 4. 本次审查修复的问题（均已通过 227 项自检）

按文件列出，后续若出现回归优先看这些位置：

| 文件 | 问题 | 修复 |
| --- | --- | --- |
| `App/Services/ClassicDialogs.cs` | `OPENFILENAMEW.lpstrFile` 用 `string`/`StringBuilder` 字段封送，前者缓冲不足会内存越界、后者在 struct 中不可封送（实测 `ArgumentException`）；`SHBrowseForFolder` 路径缓冲仅 260 字符 | 改为手写 `IntPtr` 非托管缓冲 + 全字段指针化，`Marshal.StringToHGlobalUni` 管理 filter/title/defExt，`ReadMultiSz` 回读多选结果；`CommDlgExtendedError` 识别 `FNERR_BUFFERTOOSMALL`；文件夹路径用 32K `StringBuilder` |
| `App/Services/CrashGuard.cs` | SEH 回调把 `EXCEPTION_POINTERS*` 直接当 `EXCEPTION_RECORD` 读，记录的异常码/地址全错；结构体第 6 字段类型错 | 增加 `ExceptionPointers` 结构，先取 `ExceptionRecord` 指针再解析；修正 `Unused` 字段 |
| `App/ViewModels/MainViewModel.cs` | 关闭窗口只 `Cancel()` 不等批量任务收尾，临时目录/CTS/ffmpeg 子进程可能残留；`Start` 中 `Directory.CreateDirectory` 与探测异常逃逸；`_cancel`/探测 CTS 从不释放；`ClearLog` 后 `_logFlushScheduled` 卡死导致后续日志不刷新；`Progress` 的脏行集合跨线程无锁；`ProbeJobsAsync` 用 `ToDictionary` 遇重复视频抛异常；CTS 令牌与校验阶段错配 | `Start` 提前建本轮 CTS 并贯穿校验/批量，`await _batchTask` + `finally` 确定性清理；关闭时 `await _batchTask`；探测用去重字典 + 生成代际 + `_probeSources` 集合按「取消 → 任务自身 finally 释放」管理；`ClearLog` 在锁内重置排程标志；进度改为锁内 `_dirtyRows.Add` 与 `_progressFlushScheduled` 联动 |
| `App/Views/SubtitleTrackDialog.xaml.cs` | `LangCombo` 用 `ItemsSource` 绑定字符串列表时 `SelectedItem` 设自定义 label 不生效，语言下拉显示为空；新增轨用 `DetectLangFromName` 不如自动匹配一致 | 改为 `Items.Add` 直接填项、按索引选中、未知语言码追加项；新增轨改用 `DetectTypedLang` |
| `Core/Services/BatchProcessor.cs` | **并发硬字幕共用 `hard` 目录，`hardsub_0.ext` 互相覆盖**（Workers>1 时烧错字幕）；重跑任务残留上一轮 `Size/VerifyOk/StartedAt/FinishedAt`；取消/异常路径未设 `FinishedAt` | 工作目录按 `job.Uid` 隔离（空 Uid 用 GUID）；`ResetJob` 清空全部运行态；取消/异常统一补 `FinishedAt` |
| `Core/Services/ProcessUtil.cs` | 正常退出路径未等 stdout/stderr 读取任务收尾就离开 `using`，超时子进程句柄累积（自检 D 组 20 次超时增长 66~71）；被 Kill 的管道读取异常未观察 | 所有路径统一 `SettleAsync` 等待（最多 2 秒）+ `Observe` 未观察异常；自检 D 组改为等待 GC/终结器后再量句柄 |
| `Core/Services/NameNormalizer.cs` | `Analyze` 里 `episodeSeenThisName` 两次赋值后从未读取，每次循环多做两次正则；`TryEpisode` 中 `return false` 后还设置 `episode/consumed` 属于死分支 | 删除无用变量与死注释（不改输出，A 组参考数据仍逐项通过） |
| `Core/Services/EncodingService.cs` | UTF-16 启发式里的零字节统计曾被临时改动 | 恢复与 Python `range(0, pairs*2-1, 2)` 完全一致的写法（等价性基线） |
| `SelfTest/Program.cs` | 句柄增长测试未等异步收尾；缺并发硬字幕回归 | 加 GC/等待与 F 组「并发硬字幕工作副本隔离」用例 |

**验证结果**：`dotnet build -c Release` 0 Warning / 0 Error；完整自检
`通过：227 项，失败：0 项`（含 ffmpeg 端到端软/硬字幕、GBK 转码、输出校验、取消、替换原文件、
界面布局约束、重负载句柄、并发隔离）；`dotnet publish -c Release -o dist/SubtitleMerger` 成功，
发布目录含 `SubtitleMerger.exe`、`App.xbf`、`Views/*.xbf`、`SubtitleMerger.pri` 与全部运行时。

**`--smoke` 环境限制**：`app.manifest` 是 `requireAdministrator`，当前自动化会话非提升且无交互桌面，
启动发布版 exe 会停在提权/UAC 阶段，既不退出也不写 `%TMP%/subtitle_merger_smoke.txt`；
`__COMPAT_LAYER=RunAsInvoker` 也因缺少 GUI 会话无法完成窗口激活。这不是代码回归：
历史上在可交互/已提升会话里该钩子退出码 0。后续若在普通桌面验证，用已提升的 PowerShell 执行：

```powershell
dist\SubtitleMerger\SubtitleMerger.exe --smoke
# 预期：进程退出码 0，%TMP%\subtitle_merger_smoke.txt 写入 "OK <时间>"
```

---

## 5. 后续修改指引

- **改匹配算法**：只动 `NameNormalizer.Analyze` / `SubtitleMatcher.Score`；旧接口
  `Normalize/Tokenize/Similarity` 供 A 组冻结基准使用，不要改语义。新增用例优先加到
  `SelfTest/Program.cs` 而不是改 `Reference/reference.json`。
- **改 ffmpeg 命令**：`CommandBuilder.BuildCommand` 的输出路径必须是命令最后一个参数；
  `plan` 决定 `OutputVerifier` 的预期；改了要同步 B/E 组端到端断言。
- **改编码处理**：`EncodingService` 的候选表、打分系数、BOM 顺序都与 Python 等价，
  加缓存时注意 key 是 `(字幕绝对路径, 临时目录绝对路径)`，每轮结束 `ClearUtf8Cache()`。
- **改 UI**：`MainViewModel` 不引用任何 WinUI 类型（只依赖 `IUiServices`）；所有平台能力放
  `MainWindow.xaml.cs`。新增异步事件处理器一律用 `CrashGuard.FireAndForget` 包住，避免
  `async void` 异常逃逸成 0xc000027b。
- **改并发/子进程**：保持「有界并发 + 取消即 Kill 整棵进程树 + 等管道收尾」三条；
  `Parallel.ForEachAsync` 的 `MaxDegreeOfParallelism` 不要去掉。
- **不要顺手清理** `docs/` 里记录的「原版固有行为」（见第 1 节），也不要改配置键名、
  日志文案前缀 `[HH:MM:SS]`、CSV 的 UTF-8 BOM + CRLF 格式。
- **输出目录同源同步（1.2 新增）**：`输出目录同源同步` 是持久开关，默认**打开**，状态存于配置
  可选键 `sync_outdir`（`AppConfig` 保留未知键，故 19 个原有键名/类型/格式不变，Python 版互读不受影响；
  旧配置无该键时按默认打开处理）。开启时，`AddPathsAsync` 检测到本次添加的目录相对上一次同步发生变化
  就自动把 `OutputDir` 对齐过去；`ClearAll` 重置基准；手动「浏览」或点「同源文件」会更新基准，
  因此同一目录继续添加文件不会冲掉用户手动指定的输出目录。逻辑全在 `MainViewModel`，
  `SyncOutputDirTo` 是唯一改写点；改这里要同步 `SelfTest` 的布局断言（C 组）。
- **提交前必跑**：`dotnet build -c Release` + 完整自检 + 单 EXE 发布（见第 7 节）。
  若自检报临时目录 `UnauthorizedAccessException`，按第 3 节覆盖 `TMP/TEMP` 再跑。

---

## 6. 工作区清理约定

`bin/`、`obj/`、`dist/`、`.workbuddy/` 均为构建/临时产物，`.gitignore` 已忽略；
根目录的 `*.lnk` 快捷方式是本机残留，不属于项目文件。本次交付已清理这些无用文件，
仓库只保留源码、文档、`app.ico`、解决方案与 `AGENT.MD`。

---

## 7. 单 EXE 发布（WinUI 3 官方方案）

### 7.1 工具链审查结论

| 项 | 实测值 |
| --- | --- |
| .NET SDK | 10.0.403（工程 TFM 仍为 `net8.0`，未升级） |
| Microsoft.WindowsAppSDK | 2.6.5（未升级） |
| Windows SDK BuildTools | 10.0.26100.0 |
| CommunityToolkit.Mvvm | 8.3.2（未升级，8.4.x 会报 MVVMTK0045） |
| 官方单文件支持 | 2.6.5 自带 `Microsoft.WindowsAppSDK.SingleFile.targets`，**支持** unpackaged + self-contained + single-file |

官方 targets 的硬性要求（`PrepareForBuild` 前校验）：`EnableMsixTooling=true`（生成嵌入版
`resources.pri`）、`WindowsPackageType=None`、`IncludeAllContentForSelfExtract=true`（原生 DLL
SxS 重定向）、`WindowsAppSDKSelfContained=true` + `SelfContained=true`。
`WindowsAppSDKSelfContained=true` 会自动启用 `UndockedRegFreeWinRT` 的 `ModuleInitializer`，
在程序入口前设置 `MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY`（单文件解压目录），
这是 `.pri` / 原生 DLL 能正确定位的关键，**无需改业务代码或 GUI**。

### 7.2 配置（可重复执行）

新增 `src/SubtitleMerger.App/Properties/PublishProfiles/SingleFile.pubxml`（独立发布配置，
默认目录发布 `dist/SubtitleMerger` 不受影响），关键属性：

```xml
<SelfContained>true</SelfContained>
<PublishSingleFile>true</PublishSingleFile>
<IncludeAllContentForSelfExtract>true</IncludeAllContentForSelfExtract>
<IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
<EnableMsixTooling>true</EnableMsixTooling>
<WindowsPackageType>None</WindowsPackageType>
<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>
<WindowsAppSdkUndockedRegFreeWinRTInitialize>true</WindowsAppSdkUndockedRegFreeWinRTInitialize>
<RuntimeIdentifier>win-x64</RuntimeIdentifier>
<DebugType>embedded</DebugType>
<PublishDir>...\dist\SingleExe\</PublishDir>
```

配套最小改动：

- `SubtitleMerger.App.csproj`：`IncludeXamlArtifactsInPublish` 目标加
  `Condition="'$(PublishSingleFile)' != 'true'"`。目录发布仍需外部 `.xbf/.pri`；
  单文件模式下由 `EnableMsixTooling` 生成的嵌入 `resources.pri` 承载，继续复制会让发布目录残留文件。
- `Directory.Build.props`：仅当 `PublishProfile=SingleFile` 时给被引用的 Core 项目设
  `DebugType=embedded`，消除松散的 `SubtitleMerger.Core.pdb`（`.pubxml` 属性不会传给 ProjectReference）。
- `启动.bat`：工作区根目录一键启动；`dist\SingleExe\SubtitleMerger.exe` 不存在时自动执行发布。

**发布命令**：

```powershell
dotnet publish src/SubtitleMerger.App/SubtitleMerger.App.csproj -c Release -p:PublishProfile=SingleFile --nologo
# 或双击根目录 启动.bat
```

### 7.3 实际发布结果

- 输出目录 `dist/SingleExe/` **只有一个文件**：`SubtitleMerger.exe`（230,708,861 B ≈ 220 MiB）。
- 顶层条目数 1，文件数 1，目录数 0；无 `.xbf` / `.pri` / `.dll` / `.pdb` / `.json` 残留。
- 对比：改造前目录发布约 155 MB 分散在 200+ 文件；单文件把所有依赖、WinAppSDK 运行时、
  .NET 8 运行时、XAML 资源、原生 DLL 与图标全部打进 bundle，体积增至 220 MiB（未压缩，换取启动速度）。
- `publish` 退出码 0，0 Warning / 0 Error；`dotnet build -c Release` 与完整自检
  **227 项 / 失败 0** 在配置改动后仍通过（A 组冻结基准未漂移）。
- 用户已在本机实际启动单 EXE 验证可运行（本会话非提升、无交互桌面，
  `app.manifest` 的 `requireAdministrator` 会让自动化 `--smoke` 停在提权阶段，属环境限制）。

### 7.4 兼容性与风险检查结论

| 检查项 | 结论 |
| --- | --- |
| WinUI 3 初始化 / XAML 资源加载 | 由官方 `ModuleInitializer` + 嵌入 `resources.pri` 处理；首次运行释放到 `%TEMP%\.net\` 后按 `MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY` 定位 |
| `.xbf` / `.pri` | 单文件发布目标不再复制外部副本，改由 `EnableMsixTooling` 嵌入；未删除任何必要资源 |
| WinAppSDK 自包含运行时 | `WindowsAppSDKSelfContained=true` + `SelfContained=true`，不要求目标机预装 .NET 或 Windows App SDK Runtime |
| 原生 DLL 加载路径 | `IncludeAllContentForSelfExtract` + 清单 `loadFrom='%MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY%...'` SxS 重定向 |
| 首次运行释放 | .NET bundle 自动释放；`IncludeNativeLibrariesForSelfExtract=true` 一并释放原生库 |
| 普通用户权限 | 受**既有** `app.manifest` `requireAdministrator` 限制，会请求 UAC 提升；这是原有行为（v1.1 为写输出目录/拖放兼容而设），单文件改造未改变权限模型，也未改 GUI/业务逻辑 |
| 杀软误报 | 220 MiB 自解压单文件 + 内嵌原生 DLL，属 .NET SingleFile 常见启发式误报形态；分发时建议附 SHA256 与代码签名（本项目暂未签名） |
| 退出 / 二次启动 / 升级覆盖 | bundle 释放目录按程序集版本/内容哈希复用，二次启动不再解压；升级时替换 exe 即可，旧释放目录由 .NET 运行时自行管理 |
| 干净环境（无 .NET / 无 WinAppSDK 预装） | 配置上满足（self-contained）；本会话无干净 VM，未做隔离虚拟机实测，此项如实标注为**未实测** |

### 7.5 性能与体积优化评估

- **ReadyToRun**：`PublishReadyToRun=false`。WinUI 3 + WinRT 投影下 R2R 会显著放大体积、
  且对本应用（启动后主要耗时在 ffmpeg 子进程与媒体探测）收益有限；未启用。
- **裁剪 / Native AOT**：`PublishTrimmed=false`，**明确不启用**——XAML 反射绑定与 WinRT
  激活不兼容激进裁剪，强行启用会破坏功能。
- **压缩**：`EnableCompressionInSingleFile=false`，避免每次启动 CPU 解压 220 MiB；
  若分发带宽优先，可单独打开（预计体积可降约 35–40%，冷启动变慢）。
- **无用依赖清理**：仅通过 `DebugType=embedded` 消除了 2 个 PDB 残留；未删任何运行时依赖，
  未移除重复资源。发布目录里除 EXE 外无其它文件。

### 7.6 运行限制（必须如实告知）

1. 目标 OS：**Windows x64**，Windows 10 1809 (10.0.17763) 及以上。
2. 仍需本机 `ffmpeg` / `ffprobe`（硬字幕需 `libass`）——这是业务外部依赖，**未**内嵌进 EXE；
   查找顺序与配置 `%USERPROFILE%\.subtitle_merger.json` 不变。
3. 首次启动会向 `%TEMP%\.net\` 释放约 200+ 个运行时文件；这是 .NET 官方单文件机制，
   不是「隐藏依赖」，也不需要用户手动管理。
4. 发布配置只在显式 `-p:PublishProfile=SingleFile` 时生效；README 里的目录发布命令保持原样。
