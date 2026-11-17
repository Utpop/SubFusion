# AGENT.MD — SubFusion 工程说明

> 面向后续 AI / 开发者的唯一工程文档。只记录**当前实际存在**的事实：
> 目录结构、模块职责、构建与测试方式、外部依赖、已知限制。
> 计划中的功能不写入本文件。
> 基线：由 Python/Tkinter v1.1 → WinUI 3 重构完成后，再统一更名为 **SubFusion**，
> 并在同一 Core 架构上新增「视频合并 + MKV 自动章节」引擎。

---

## 1. 项目定位

SubFusion 是一个 Windows x64 桌面工具，包含两个**相互独立**的业务模块：

| 模块 | 作用 | 入口 |
| --- | --- | --- |
| 字幕合并 | 批量把外挂字幕合进视频（软字幕内挂多轨 / 硬字幕烧录），自动识别并转换字幕编码 | `BatchProcessor.RunAsync(BatchRequest, CancellationToken)` |
| 视频合并 | 多段视频合并为一个文件，优先无损拼接，不兼容时统一重编码，MKV 自动生成章节 | `VideoMergeTask.RunAsync(VideoMergeOptions, IProgress<MergeProgress>, CancellationToken)` |

两个模块共享底层媒体基础设施（工具定位、子进程执行、媒体探测、输出校验、临时目录管理），
但**不共享业务逻辑**：字幕匹配、编码转换、章节时间轴互不耦合。

运行时外部依赖只有 `ffmpeg` / `ffprobe`（硬字幕还需 `libass`，HDR 色调映射需 `zscale`/`tonemap`）。
**不依赖 MKVToolNix**：章节通过 ffmetadata + `-map_chapters` 写入，全程 `-c copy`，不触发重编码。
外部工具定位顺序见 `ToolLocator.Locate`（配置值 → 环境变量 → 内置目录 → 程序目录 → PATH → 常见安装位置）。

---

## 2. 目录结构

```
SubFusion/
├── SubFusion.sln                     # 3 个项目（Core / App / SelfTest）
├── Directory.Build.props             # 仅 SingleFile 发布时给被引用项目嵌入 PDB
├── AGENT.MD                          # 本文件（唯一工程文档）
├── app.ico                           # 图标（App.csproj 以 Content 链到 Assets\app.ico）
├── 启动.bat                          # 一键启动；dist\SingleExe\SubFusion.exe 不存在时自动发布
└── src/
    ├── SubFusion.Core/               # net8.0 类库：纯业务逻辑，禁止引用 WinRT/UI
    │   ├── Constants.cs              #   常量、语言表、扩展名集合、默认阈值、配置路径
    │   ├── Models/                   #   字幕侧的轻量模型
    │   │   ├── SubTrack.cs           #     字幕轨（Path/Lang/Title/Default/Forced/Kind/Encoding/Enabled）
    │   │   ├── Job.cs                #     字幕合并任务（Video/Subs/Output/State/Progress/VerifyOk…）
    │   │   ├── Options.cs            #     一次字幕批处理的全部选项（含 ReplaceSource）
    │   │   ├── MediaInfo.cs          #     字幕侧 ffprobe 摘要 + Probed 标记
    │   │   └── OutputPlan.cs         #     build_command 预期结构 / 校验结果 / 能力探测结果
    │   ├── Media/                    #   ★ 共享媒体基础设施（两个业务模块都用）
    │   │   ├── MediaModels.cs        #     VideoStreamInfo / AudioStreamInfo / ContainerInfo / MediaBundle
    │   │   ├── MediaInspector.cs     #     ffprobe → MediaBundle（流级完整参数、HDR 判定、批量探测）
    │   │   └── FfmpegTools.cs        #     ffmpeg/ffprobe 定位 + 能力/版本探测的门面
    │   ├── Merge/                    #   ★ 视频合并业务模块
    │   │   ├── VideoMergeOptions.cs  #     参数对象（输入顺序/容器/编码/适配/音频/章节/覆盖策略）
    │   │   ├── MergeCompatibility.cs #     能否无损拼接的逐参数判定
    │   │   ├── MergePlan.cs          #     统一输出参数推导 + 分段重编码/拼接命令构建
    │   │   ├── MergeResult.cs        #     结果模型（状态/阶段/输出/章节/原因/诊断）
    │   │   ├── VideoMergeEngine.cs   #     编排：校验→探测→判定→编码→拼接→章节→校验
    │   │   └── VideoMergeTask.cs     #     对外任务接口（RunAsync / AnalyzeAsync / ProbeAsync）
    │   ├── Chapters/                 #   ★ MKV 自动章节
    │   │   ├── ChapterEntry.cs       #     章节模型（秒 + 毫秒格式化）
    │   │   ├── ChapterTimeline.cs    #     时间轴计算与合法性校验
    │   │   └── ChapterWriter.cs      #     ffmetadata 生成/转义、剥离旧章节、写入、回读、比对
    │   └── Services/                 #   共享服务层
    │       ├── AppConfig.cs          #     JSON 配置读写（`.subfusion.json`）
    │       ├── ToolLocator.cs        #     ffmpeg/ffprobe 定位、能力/版本探测
    │       ├── ProcessUtil.cs        #     子进程 RunAsync（超时/取消/Kill 进程树/管道收尾）、Which
    │       ├── FileCollector.cs      #     文件/文件夹收集、去重、跳过隐藏子目录、排序
    │       ├── NameNormalizer.cs     #     文件名规范化/切分/语言识别
    │       ├── SequenceMatcher.cs    #     Python difflib.SequenceMatcher.ratio() 等价移植
    │       ├── SubtitleMatcher.cs    #     字幕匹配打分（标题×0.85 + 词重合×0.15、集数/目录约束）
    │       ├── EncodingService.cs    #     编码检测/转 UTF-8 缓存/ASS [Script Info] 修复/硬字幕副本
    │       ├── MediaProbe.cs         #     字幕侧 ffprobe 探测（probe_streams / probe_subtitle / probe_many）
    │       ├── CommandBuilder.cs     #     字幕侧输出路径与 ffmpeg 命令、plan
    │       ├── FfmpegRunner.cs       #     `-progress` 解析、stderr 环形缓冲、取消即 Kill
    │       ├── OutputVerifier.cs     #     流结构/时长/体积/首尾解码完整性校验
    │       ├── Formatting.cs         #     时长/体积/命令回显（InvariantCulture）
    │       └── BatchProcessor.cs     #     字幕批处理编排：IBatchEvents 6 类事件、按完成顺序回调
    ├── SubFusion.App/                # net8.0-windows10.0.19041.0 / win-x64 / WinUI 3 unpackaged
    │   ├── App.xaml(.cs)             #   入口、资源字典合并、CrashGuard、--smoke/--ui-report 钩子
    │   ├── app.manifest              #   requireAdministrator + PerMonitorV2 + longPathAware
    │   ├── Styles/                   #   设计令牌与共享控件样式
    │   │   ├── Theme.xaml            #     语义色（浅/深/高对比）、间距、字号、圆角、弹窗宽度
    │   │   └── Controls.xaml         #     页面标题/分组标题/辅助文字/卡片/徽标/列表行等样式
    │   ├── Views/                    #   MainWindow（外壳 + IUiServices 实现）、SubtitleTrackDialog
    │   ├── Pages/                    #   四个页面（XAML + 极薄 code-behind）
    │   │   ├── SubtitlePage          #     字幕合并工作区
    │   │   ├── MergePage             #     视频合并工作区
    │   │   ├── TaskCenterPage        #     任务中心
    │   │   └── SettingsPage          #     设置
    │   ├── ViewModels/               #   Shell / Subtitle / VideoMerge / TaskCenter / Settings
    │   └── Services/                 #   IUiServices、AppSettings、TaskCenter、ThemeService、
    │                                 #   Converters、ClassicDialogs、CrashGuard、窗口/拖放辅助
    └── SubFusion.SelfTest/           # net8.0 控制台，仅引用 Core（不依赖 WinUI）
        ├── Program.cs                #   A 逻辑等价 / B 端到端 / C 布局 / D 健壮性 / E 替换 / F 并发隔离
        │                             #   G 视频合并引擎 / H MKV 自动章节
        └── Reference/reference.json  #   原 Python core.py 产出的冻结基准值，不能随意改
```

### 分层规则（改代码前必读）

- **Core 不得引用 WinUI / WinRT / WindowsAppSDK**；Core 的 TFM 是 `net8.0`，物理上无法引用。
- 所有核心任务通过**明确的参数对象**启动（`BatchRequest` / `VideoMergeOptions`），
  返回带状态、阶段、进度、输出信息、错误与诊断的**结果对象**。
- 业务逻辑不放进 ViewModel 或 UI 事件处理器；ViewModel 只负责参数收集、状态展示与线程投递。
- **GUI 不得重新实现 Core 的判断**：能否无损拼接、章节时间轴、ffmpeg 命令、输出校验
  全部来自 Core；页面只渲染 Core 返回的对象（`MergeCompatibility` / `MergeResult` / `ChapterEntry`）。

### 线程模型

- `BatchProcessor` 用 `SemaphoreSlim(workers)` 限流 + `Channel<Job>` 按完成顺序回调 `JobDone`；
- 所有 `IBatchEvents` 回调可在任意线程触发，`MainViewModel` 统一经 `IUiServices.RunOnUiThread` 投递；
- 日志后台入队、UI 侧合并刷新并限制 4000 行；进度用「脏行集合 + 一次 UI 刷新」合并；
- `FfmpegRunner` / `ProcessUtil` 注册 `CancellationToken.Register` 立即 `Kill(entireProcessTree)`；
- 超时/取消路径一律 `KillTree` + `SettleAsync` 等管道读取收尾（不等就会泄漏句柄）。
- GUI 侧：日志批量入队后一次性刷新、滚动最多 120 ms 一次；视频合并进度合并到约 10 Hz；
  页面 `Unloaded` 释放选中，窗口关闭先取消任务再落盘配置。

---

## 3. 视频合并引擎

### 3.1 接口

```csharp
var tools = await FfmpegTools.ResolveAsync();          // 定位 ffmpeg/ffprobe + 能力探测
var task  = new VideoMergeTask(tools);

// 只分析（不产生任何文件）
(MergeCompatibility compat, List<MediaBundle> inputs) = await task.AnalyzeAsync(options);

// 执行
MergeResult r = await task.RunAsync(options, new Progress<MergeProgress>(p =>
    Console.WriteLine($"{p.Stage} {p.Percent:F1}% {p.Message}")), cancellationToken);
```

`VideoMergeOptions` 覆盖：输入列表与顺序、输出路径、容器、允许重编码、视频编码器/CRF/Preset/像素格式、
目标分辨率 + 适配模式、目标帧率、音频策略/编码/码率/采样率/声道布局、自动章节与自定义标题、
覆盖策略、校验开关、临时目录、探测超时。

`MergeResult` 覆盖：任务 ID、状态（Completed/Failed/Cancelled）、阶段、输出路径、最终 `MediaBundle`、
章节列表、不兼容原因、是否复制视频/音频、提示信息、错误文本、诊断日志、耗时。

### 3.2 无损拼接判定

`CompatibilityAnalyzer` 不是只比编码名，而是逐项比较所有影响解码与时间轴的参数：

视频：编码、Profile、分辨率、像素格式、帧率（±0.01）、时间基准、SAR、色彩空间/传递函数/色域/范围、HDR 标记、视频流数量；
音频：编码、采样率、声道数、声道布局、音频流数量（逐轨比较）；
容器：容器族（mkv / mp4 / other）。

任何一项不一致都会给出**具体的**不兼容原因（`MergeReason.Code`），并进入统一重编码流程。
`AllowReencode=false` 时直接失败并列出全部原因。

### 3.3 继承与执行流程

1. 校验参数（含「输出路径不得等于任何输入」）→ 建独立临时目录；
2. `MediaInspector.InspectManyAsync` 探测全部输入；
3. `CompatibilityAnalyzer.Analyze` 判定 → `MergePlan.Create` 推导统一输出参数；
4. 兼容：直接 concat；不兼容：逐段统一编码到临时目录，再 concat；
5. 探测实际输出时长 → `ChapterTimeline.Build` 归一化 → 必要时 `ChapterWriter.ApplyAsync` 重写章节；
6. 校验（流结构 / 时长 / 分辨率 / 编码 / 采样率 / 章节 / 首尾解码）；
7. **校验通过后**才 `File.Move` 到最终路径，失败不留半成品。

### 3.4 分辨率适配

三种模式都在滤镜链内完成，且先 `setsar=1` 还原方形像素，避免画面变形：

| 模式 | 滤镜 | 语义 |
| --- | --- | --- |
| `Contain`（默认） | `scale=…:force_original_aspect_ratio=decrease` + `pad` | 完整显示，必要时补黑边 |
| `Cover` | `scale=…:force_original_aspect_ratio=increase` + `crop` | 填满画面，必要时裁切 |
| `Stretch` | `scale=…:increase` + `scale=W:H` | 直接拉伸 |

目标分辨率/帧率默认取输入的**最大值**（不降质）。

### 3.5 音频统一

- 参数一致且走无损拼接时，音频同样 `copy`；
- 需要统一时用 `-c:a` + `-ar` + `-ac`，采样率默认取输入最高（封顶 48000），声道默认取输入最多（封顶 5.1）；
- **部分输入没有音轨**：该段用 `anullsrc` 补静音并 `-shortest`，保证每段音视频等长，concat 时间轴不错位；
- **多音轨输入**：策略是只取第一条音轨，并在 `MergeResult.Notices` 中明确提示（不会产生错误映射）。

### 3.6 HDR / SDR

- 判定依据是传输特性（PQ=smpte2084、HLG=arib-std-b67）、杜比视界/HDR10+ 侧数据、主控显示元数据；
- HDR 与 SDR 混合输入：**真正做色调映射**（`zscale` 线性化 → `tonemap=hable` → BT.709），
  而不是只改色彩标记；
- 纯 HDR 输入重新编码时用 `setparams` 写入 bt2020/PQ 标记，保证输出仍是 HDR；
- 色彩标记**必须用滤镜内的 `setparams`** 写入：编码器的 `-colorspace/-color_trc/-color_primaries`
  在 mp4/mkv 封装下不一定会写进码流（已实测）。

---

## 4. MKV 自动章节

- 输出容器为 `mkv` 时 `AutoChapters` 默认开启；`mp4` 不写章节。
- 默认每个输入生成一章，标题取输入文件名（去扩展名）；`ChapterTitles` 可逐项覆盖，不足时用「第 N 段」补齐。
- 时间轴：`ChapterTimeline.Build(片段时长, 标题, 最终实际时长)`
  - 首章从 0 开始，每章起点等于上一章终点；
  - 累加值与最终输出实际时长不一致时按比例归一化（消除时间戳漂移）；
  - 对齐到毫秒，强制严格递增、不重叠、不越界；最后一章结束于输出实际时长。
- 写入方案：`ffmetadata`（`TIMEBASE=1/1000`、毫秒精度）+ `-map_chapters` + `-c copy`。
  `ChapterWriter.ApplyAsync` 先**剥离已有章节**再挂新目录——concat 会把各输入的章节带进输出，
  不剥离会导致章节翻倍。
- 校验：写完立即用 `ffprobe -show_chapters` 回读，比对数量/标题/起止时间（容差 80 ms），
  并跑一遍时间轴合法性校验。

---

## 4.5 GUI 架构（WinUI 3）

GUI 层是一次完整重构，界面已全部重建：**没有沿用旧 MainWindow 布局，也不存在旧页面残留**。

### 4.5.1 外壳与导航

`MainWindow` 只做四件事：窗口配置、`NavigationView` 导航、状态栏、实现 `IUiServices`。

- 顶部自绘标题栏（`TitleBarRegion`）：左侧应用图标 + 名称 + 当前页面名，
  右侧常驻 **ffmpeg 状态胶囊**（圆点 + 简短状态，悬停显示完整版本），再往右留 150 DIP 给系统标题按钮；
- `NavigationView`（`PaneDisplayMode=Auto`）四个顶层入口：字幕合并 / 视频合并 / 任务中心，
  设置固定在 `FooterMenuItems`；启动默认进入字幕合并；
- 内容区是 `Frame`，带 `NavigationThemeTransition`，页面切换有原生过渡；
- 底部状态栏只保留**文字摘要**（当前操作 + 任务计数）；不放进度条（进度属于各工作区内容区）、
  不放导航按钮（任务中心已在左侧一级入口）；
- 背景用 `MicaBackdrop`（`MicaKind.BaseAlt`），不支持时 WinUI 自动回退纯色。

页面实例由 `Frame.Navigate` 创建，ViewModel 不创建视图：`ShellViewModel.Navigate(tag)`
只更新 `SelectedTag`/`Header` 并抛 `NavigationRequested`，由 `MainWindow.NavigateTo` 映射成页面类型。

> 注意：页面在 `MainWindow` 构造期间就会被创建，所以外壳必须先登记
> `App.ActiveShell`，再开始导航；页面通过 `App.Shell` 取 ViewModel。

### 4.5.2 页面与 ViewModel 职责

| 页面 | ViewModel | 职责 |
| --- | --- | --- |
| 字幕合并 | `SubtitleViewModel` | 输入收集/匹配/字幕轨编辑/参数/执行/日志（实现 `IBatchEvents`） |
| 视频合并 | `VideoMergeViewModel` | 输入与排序、兼容性分析展示、参数、执行与结果 |
| 任务中心 | `TaskCenterViewModel` | 统一任务列表、筛选、详情投影、取消/清理/诊断 |
| 设置 | `SettingsViewModel` | 外观/工具/字幕默认值/视频默认值/配置文件 |

- `ShellViewModel` 只做编排：持有五个页面 VM、导航状态、主题、全局状态栏。
- 行级模型 `SubtitleRowViewModel`、`MergeInputViewModel` 是展示投影，不持有业务逻辑。
- **ViewModel 不引用任何 WinUI/WinRT 类型**（SelfTest C 组有断言）。平台能力一律经 `IUiServices`。

### 4.5.3 UI 服务层

`IUiServices`（`Services/IUiServices.cs`）由 `MainWindow` 实现：

文件/文件夹/可执行文件/CSV/视频保存对话框、信息与确认对话框、字幕轨编辑对话框、
剪贴板、打开资源管理器、日志滚动、`RunOnUiThread`。

> 提升（管理员）进程里 WinRT picker 会以 `E_ACCESSDENIED` 失败并可能触发 fail-fast，
> 因此 `ClassicDialogs.IsElevated()` 为真时直接走经典 Win32 对话框。

辅助服务：

| 类型 | 作用 |
| --- | --- |
| `AppSettings` | Core `AppConfig` 的强类型门面，读写 `.subfusion.json`，不参与媒体处理 |
| `TaskCenter` | 唯一的任务状态来源，所有更新经 `RunOnUiThread` 投递，日志上限 2000 行 |
| `ThemeService` | `system/light/dark` → `ElementTheme`，作用于窗口根元素 |
| `Converters` | 状态→语义画刷、bool/空→Visibility、非空→bool、bool→图标字形 |

### 4.5.4 主题与设计令牌

- `Styles/Theme.xaml`：`ThemeDictionaries` 三套（`Default`=深色、`Light`、`HighContrast`）
  只定义语义色（成功/警告/危险/中性/信息及其 tint），页面不出现硬编码颜色。
- 同一文件里还有间距刻度、页面/卡片内边距、字号层级、列表行高、`ContentDialogMaxWidth`。
- `Styles/Controls.xaml`：页面标题、分组标题、辅助文字、字段标签、等宽文本、
  卡片表面、轻量表面、状态徽标、工具条按钮、列表行、空状态样式。
- 深浅切换通过 `Root.RequestedTheme` 完成，弹层（ContentDialog/Flyout）随 `XamlRoot` 一起切换；
  实测三套主题下语义画刷都取到对应值。

### 4.5.4.1 窗口尺寸

默认窗口是 **1180×780 DIP**，按当前显示器 DPI 换算成物理像素后再居中
（125% 缩放下即 1475×975）。只有当工作区本身就装不下这个尺寸时才最大化。
最小尺寸限制为 min(960, 工作区) × min(640, 工作区)，不超过工作区，避免小屏被撑出屏幕。

> 早期版本按工作区 90% 计算，在 2250×1296 的屏幕上会开成 2025×1166（近乎全屏），已改掉。

### 4.5.5 动效与可访问性

- 页面切换用 `NavigationThemeTransition`；返回/前进不做自定义编排。
- 进度与日志都做了合并：日志按批入队后一次性追加，滚动最多每 120 ms 跟随一次；
  视频合并进度回调再合并到约 10 Hz。**没有持续性装饰动画。**
- 所有导航项、按钮都有文字标签（无纯图标无名称交互）；列表项支持键盘选择与右键菜单；
  颜色全部走主题资源，高对比度主题下仍可用。

### 4.5.6 线程与生命周期

- Core 回调可来自任意线程：`SubtitleViewModel`（`IBatchEvents`）与
  `VideoMergeViewModel`（`IProgress<MergeProgress>`）一律先 `RunOnUiThread`。
- 页面离开（`Unloaded`）清空选中集合；`MergePage` 离开与窗口关闭都会取消分析/运行。
- 高频更新只刷新可变属性，不重建集合；任务中心列表做增量同步，保留选择与滚动位置。
- 关闭窗口先取消任务并等待批处理收尾，再落盘配置，避免残留 ffmpeg 子进程。

### 4.5.7 运行时验收钩子

以下参数只用于自动化验证，正常双击不触发：

| 参数 | 作用 |
| --- | --- |
| `--smoke` | 窗口加载后写 `%TEMP%\subfusion_smoke.txt` 并以 0 退出 |
| `--ui-report` | 依次切换五个页面与三套主题，写 `%TEMP%\subfusion_ui.txt`（页面尺寸/裁切/主题取值） |
| `--ui-report --resize WxH` | 先改窗口尺寸再量测，用于窄窗口布局验证 |
| `--merge-e2e <目录>` | 真实跑一遍视频合并并把结论（策略/章节/输出/任务中心状态/诊断行数）写进 `subfusion_merge_e2e.txt` |
| `--subtitle-e2e <目录>` | 真实跑一遍字幕合并，写 `subfusion_subtitle_e2e.txt` |
| `--dir <目录>` | 走与拖放相同的入口导入文件 |
| `--page <tag>` | 启动时直接进入指定页面 |

---

## 5. 构建 / 运行 / 验证

```powershell
dotnet build SubFusion.sln -c Release --no-incremental   # 期望 0 Warning / 0 Error
dotnet run --project src/SubFusion.SelfTest/SubFusion.SelfTest.csproj -c Release
dotnet run --project src/SubFusion.SelfTest/SubFusion.SelfTest.csproj -c Release -- --skip-e2e
dotnet publish src/SubFusion.App/SubFusion.App.csproj -c Release -o dist/SubFusion
dotnet publish src/SubFusion.App/SubFusion.App.csproj -c Release -p:PublishProfile=SingleFile --nologo
```

`--smoke` / `--dir <路径>` 是验收钩子，正常双击不触发。

**本机环境注意**：`%TEMP%` 对部分 .NET 进程创建目录会抛 `UnauthorizedAccessException`。
跑自检时把 `TMP`/`TEMP` 指到工作区目录，且**必须用 Windows 绝对路径**
（MSYS 的 `$PWD` 会被拼成 `C:\c\Users\…` 这种非法路径）：

```powershell
$tmp = Join-Path (Get-Location) '.workbuddy\selftest-tmp'
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
$env:TMP = $tmp; $env:TEMP = $tmp
& src\SubFusion.SelfTest\bin\Release\net8.0\SubFusion.SelfTest.exe
```

**构建陷阱**：`ops_edit_file` 会保留文件原始 mtime，MSBuild 增量构建可能据此跳过编译，
表现是「改了代码但测试仍是旧结果」。改完源码后一律用 `--no-incremental` 构建（或 `touch` 目标文件）。

**依赖版本**（已评估，暂不升级）：`Microsoft.WindowsAppSDK` 2.6.5、`CommunityToolkit.Mvvm` 8.3.2
（8.4.x 在 WinUI 3 字段式 `[ObservableProperty]` 下会报 54 条 MVVMTK0045）、
`System.Text.Encoding.CodePages` 9.0.0。TFM 保持 `net8.0`。

---

## 6. 测试

`SelfTest` 是唯一测试入口，当前 **319 项断言**，全部通过：

| 分组 | 内容 |
| --- | --- |
| A | 与 Python 版逐项等价比对（`Reference/reference.json` 冻结基准） |
| B | 字幕端到端：软字幕 / GBK 转码 / 硬字幕 / 取消 |
| C | 界面结构约束（只断言文件文本，不引用 UI 类型）：NavigationView 五入口、Frame 承载页面、无固定像素高度、三套主题字典、ViewModel 不引用 WinUI、页面 code-behind 不含媒体处理、GUI 只经 Core 接口起任务 |
| D | 重负载健壮性：20 次超时进程句柄增长、批量中途取消收尾 |
| E | 「合并后替换源文件」的三条安全保证 |
| F | 并发硬字幕工作副本隔离 |
| G | 视频合并引擎（48 项）：探测字段完整性、SAR/DAR、兼容性判定、无损拼接、自动重编码、三种分辨率适配、无音轨、多音轨、HDR/SDR、取消、并发隔离、损坏/缺失输入、拒绝覆盖、分析接口 |
| H | MKV 自动章节（32 项）：时间轴计算与校正、合法性校验、ffmetadata 转义、默认/自定义/关闭标题、章节不触发重编码、MP4 不写章节、输入自带章节不翻倍 |

G/H 的素材全部由 ffmpeg 现场生成（`lavfi` testsrc/smptebars/sine），不依赖任何外部视频文件。
测试素材显式用 `setparams` 写 BT.709 标记，否则同一批素材的色彩字段会是「未知」，兼容性判定无法对齐。

---

## 7. 发布

### 7.1 目录发布（默认）

`dotnet publish src/SubFusion.App/SubFusion.App.csproj -c Release -o dist/SubFusion`，
输出包含 `SubFusion.exe`、`App.xbf`、`Views/*.xbf`、`SubFusion.pri` 与全部运行时。
`csproj` 里的 `IncludeXamlArtifactsInPublish` 目标负责把这些资源带进发布目录。

### 7.2 单文件发布

`-p:PublishProfile=SingleFile` 时输出**只有一个文件** `dist/SingleExe/SubFusion.exe`（约 220 MiB）。
关键属性：`EnableMsixTooling=true`（嵌入 resources.pri）、`WindowsPackageType=None`、
`IncludeAllContentForSelfExtract=true`、`IncludeNativeLibrariesForSelfExtract=true`、
`WindowsAppSDKSelfContained=true`、`SelfContained=true`、`RuntimeIdentifier=win-x64`。
未启用裁剪 / R2R / 压缩（XAML 反射绑定与 WinRT 激活不兼容激进裁剪；压缩会拖慢每次启动）。

`NETSDK1244` 已在该 pubxml 内定向抑制并注明原因：WinAppSDK 的单文件 targets 依赖
「全部内容解压到磁盘 + SxS 重定向」，去掉 `IncludeAllContentForSelfExtract` 会让产物无法启动。

### 7.3 运行限制（如实说明）

1. 目标：**Windows x64**，Windows 10 1809 (10.0.17763) 及以上。
2. 仍需本机 `ffmpeg` / `ffprobe`——业务外部依赖，**未**内嵌进 EXE。
3. 首次启动会向 `%TEMP%\.net\` 释放运行时文件，属 .NET 官方单文件机制。
4. `app.manifest` 为 `requireAdministrator`（沿用 v1.1 行为），双击会请求 UAC 提升。
   **非提升、无交互桌面的自动化会话无法完成启动**：`--smoke` 钩子会停在提权阶段，
   既不退出也不写 `%TMP%\subfusion_smoke.txt`。这是环境限制，不是代码缺陷；
   在已提升的交互式桌面上运行可正常退出码 0。
5. 干燥环境（无 .NET / 无 WinAppSDK）由 self-contained 配置覆盖，但本会话没有干净 VM，
   该项**未实测**。

---

## 8. 配置

配置文件：`%USERPROFILE%\.subfusion.json`（UTF-8 无 BOM、不转义中文、indent=2）。
**不再兼容旧版 `.subtitle_merger.json`**，也不迁移旧键名。

当前键：

- 外观：`theme`（system / light / dark）
- 工具：`ffmpeg`、`ffprobe`
- 字幕：`container`、`mode`、`vcodec`、`acodec`、`crf`、`preset`、`workers`、`suffix`、`outdir`、
  `sync_outdir`、`keep_existing_subs`、`keep_metadata`、`convert_encoding`、`hard_multi`、
  `verify`、`replace_source`、`overwrite`、`threshold`、`recursive`
- 视频合并：`merge_container`、`merge_allow_reencode`、`merge_codec`、`merge_crf`、`merge_preset`、
  `merge_fit`、`merge_audio`、`merge_chapters`、`merge_verify`、`merge_outdir`
- 会话：`last_dir`

`AppConfig` 读入的未知键会原样保留并写回（不丢失用户手工添加的字段）。
视频合并参数经由 `AppSettings` 持久化；实际执行时由页面构造 `VideoMergeOptions` 传给 Core。

---

## 9. 后续修改指引

- **改「替换原文件」**：`ApplySourceReplacement` 里 `needMove`（是否改名）与
  `sourceIsPresent`（是否删源）是**两个独立判断**。源 mp4 + 输出 mkv 时结果直接写在最终路径，
  但原 mp4 仍必须删除——曾用一个 `samePath` 同时表达两件事，导致原视频永远留着。
  E 组有专门的 `替换（mp4→mkv）` 用例守住这条。
- **改字幕匹配**：只动 `NameNormalizer.Analyze` / `SubtitleMatcher.Score`；旧接口
  `Normalize/Tokenize/Similarity` 供 A 组冻结基准使用，不要改语义。
- **改字幕 ffmpeg 命令**：`CommandBuilder.BuildCommand` 的输出路径必须是命令最后一个参数；
  `plan` 决定 `OutputVerifier` 的预期；改了要同步 B/E 组断言。
- **改合并判定**：`CompatibilityAnalyzer.Analyze` 是唯一判定点。新增参数比较时同步更新 G 组用例。
- **改合并命令**：`MergePlan.BuildSegmentCommand`（单段统一编码）与
  `MergePlan.BuildConcatCommand`（拼接）是两条唯一出口；`ffmpeg` 可执行文件由引擎传入，
  不要在这里写死 "ffmpeg"。
- **改章节**：`ChapterTimeline.Build` 是唯一时间轴算法；`ChapterWriter.ApplyAsync` 负责
  剥离旧章节 + 挂新章节（两步都 `-c copy`）。任何让章节写入触发重编码的改法都不接受。
- **加测试**：优先加到 `SelfTest/Program.cs` 的对应分组，不要改 `Reference/reference.json`。
- **改 UI**：页面 ViewModel 不引用任何 WinUI 类型（只依赖 `IUiServices`）；
  新增异步事件处理器一律用 `CrashGuard.FireAndForget` 包住。
- **加页面**：在 `Pages/` 建 `XxxPage.xaml`、在 `ViewModels/` 建 VM、在 `ShellViewModel.PageMeta`
  与 `MainWindow.NavigateTo` 各加一条映射，并在 `NavigationView` 里加一个 `NavigationViewItem`。
- **改主题**：只改 `Styles/Theme.xaml` 的语义色或 `Controls.xaml` 的样式，
  不要在页面里写颜色字面量（C 组断言会检查 ViewModel 边界，人工审查检查页面）。
- **改配置**：`AppSettings` 加属性 + `Read`/`Save` 各一行；设置页相应加控件。
- **并发/子进程**：保持「有界并发 + 取消即 Kill 整棵进程树 + 等管道收尾」三条。
- **取消语义**：引擎在任何"把子进程结果当作失败"的判断前都必须先
  `ThrowIfCancellationRequested()`——被 Kill 的进程退出码没有意义，探测结果也会被记成"已取消"。
  漏了这一步，用户点取消会看到"输入文件无法读取"这类假故障。
- **提交前必跑**：`dotnet build --no-incremental` + 完整自检 + 单 EXE 发布。

---

## 10. 工作区约定

`bin/`、`obj/`、`dist/`、`.workbuddy/` 为构建/临时产物，已在 `.gitignore` 忽略。
工程文档只保留本文件；不要新增其它 Markdown 说明文档。
