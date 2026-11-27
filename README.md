# SubFusion

SubFusion 是一款 Windows x64 桌面工具，用于处理本地视频与字幕文件。程序将原本分散在多套命令行工具中的常用操作整合到同一界面，当前包含四个相互独立的功能模块，以及一个统一的任务中心。

| 模块 | 用途 |
| --- | --- |
| 字幕合并 | 将外挂字幕合并到视频中，支持软字幕内挂与硬字幕烧录 |
| 视频合并 | 将多段视频合并为单个文件，优先采用无损拼接 |
| 视频裁剪 | 截取指定时间区间，并可同时调整画面方向 |
| 视频压制 | 重新编码以减小文件体积，优先使用硬件编码 |

各模块共享底层媒体基础设施（工具定位、子进程执行、媒体探测、输出校验、临时目录管理与文件搬运），业务逻辑互不耦合。

---

## 一、功能说明

### 1.1 字幕合并

| 项目 | 说明 |
| --- | --- |
| 输出容器 | MKV、MP4，或与源文件保持一致 |
| 字幕形式 | 软字幕（内挂多轨）与硬字幕（烧录至画面）；硬字幕可选择叠加全部字幕或仅第一条 |
| 自动匹配 | 依据标题相似度、集数与语言标记综合打分，阈值可调（默认 0.72）；同目录下多语言字幕可同时挂载；匹配结果支持手动调整 |
| 字幕编码 | 自动识别并转换为 UTF-8，支持 GB18030、Big5、Shift-JIS、UTF-16 等编码；MKV 保留 ASS 特效，MP4 自动转为 `mov_text` |
| 多轨设置 | 每条字幕轨可单独设置语言、轨道名、是否默认轨、是否强制显示及排列顺序 |
| 批量与并发 | 支持文件夹递归添加（可关闭，默认跳过隐藏子目录）；探测阶段使用有界并发（4 线程）；任务失败相互隔离 |
| 替换原文件 | 校验通过后可用结果替换原视频与已合并字幕。执行顺序为先写入结果、后删除原件；校验被跳过时不执行删除 |
| 输出校验 | 核对字幕轨、音轨、视频流、编码、时长与体积，并检查首尾解码完整性；异常项标黄并写入报告 |
| 报告 | 支持导出 CSV（UTF-8 BOM、CRLF 换行） |

### 1.2 视频合并

| 项目 | 说明 |
| --- | --- |
| 无损优先 | 编码、分辨率、像素格式、帧率、时间基准、SAR、色彩空间、HDR 标记与音轨参数逐项一致时，直接以 `-c copy` 拼接 |
| 不兼容重编码 | 任一参数不一致时统一编码（H.264 / H.265）后再拼接；可关闭自动重编码，此时参数不兼容将直接失败 |
| 片段级重编码 | 多数片段参数一致时，仅重编码与之不符的少数片段，其余片段保持原样；完成后自动复核能否拼接，不能则退回全量重编码 |
| 分辨率适配 | 完整显示（补黑边）、铺满画面（裁切）、拉伸填充；默认按输入中的最大分辨率处理且不降质，自动保证尺寸为偶数 |
| 音频统一 | 统一采样率与声道；缺失音轨的片段补静音以保证时间轴对齐；多音轨仅取第一条并给出提示 |
| HDR 处理 | HDR 与 SDR 混合输入时执行 `zscale` + `tonemap` 色调映射；纯 HDR 输入使用 `setparams` 保留色彩标记 |
| 自动章节 | MKV 输出时每段生成一章，标题取自文件名且可自定义；时间轴按成品实际时长归一化，经 ffmetadata 与 `-map_chapters` 写入，全程不触发重编码 |
| 无损组合识别 | 自动分析输入中可直接无损拼接的组合，并可将各组合移动至独立文件夹，便于逐个检查 |
| 截断素材处理 | 输入文件尾部数据缺失时，可仅保留可读取部分继续合并，并在结果中说明缺失时长 |
| 安全落盘 | 校验通过后才移动至最终路径；失败不残留半成品 |

### 1.3 视频裁剪

| 项目 | 说明 |
| --- | --- |
| 无损切割 | 仅复制码流，画质逐比特不变；切点必须落在关键帧上，因此实际起点可能早于请求值，程序会在界面中显示对齐结果 |
| 精确切割 | 按请求时间点精确切割，仅重编码被选中的片段，画质有轻微损失 |
| 关键帧扫描 | 读取素材的关键帧时间轴，用于计算无损模式的实际切点 |
| 画面方向 | 可调整旋转方向：无损模式仅写入方向标记，精确模式会实际旋转画面 |
| 音画同步校验 | 对比成品与原片中声音与画面的相对位置，偏差过大即判定失败 |

### 1.4 视频压制

| 项目 | 说明 |
| --- | --- |
| 编码格式 | H.264（兼容性最佳）与 H.265（同画质下体积更小） |
| 硬件编码 | 支持 AMD AMF、NVIDIA NVENC、Intel QSV；编码器可用性经实际初始化检测，而非仅读取编码器列表 |
| 编码器策略 | 自动（优先硬件，不可用时回落 CPU）、仅硬件（不可用即失败，不降级）、仅 CPU |
| 码率控制 | 画质优先（CRF / CQP）或目标码率（VBR） |
| 分辨率与帧率 | 保持原样、按目标长边缩放、按固定宽高适配、按整数倍缩小；帧率仅降低不提高 |
| 音频处理 | 可原样复制音轨（音质不变、速度更快），或重新编码为 AAC；容器不支持时会提前拦截 |
| 结果说明 | 实际使用的编码器、体积变化与分辨率变化均写入结果与诊断日志 |

### 1.5 输出文件命名

四个模块统一遵循同一规则：**目标位置存在同名文件时不覆盖，也不会令任务失败**，而是自动改用 `名称 (1).扩展名` 这类未被占用的文件名，并在结果提示中注明实际写入的文件名。

覆盖是不可逆操作，一旦误覆盖，上一次的成品无法恢复；避让的代价仅是多一个文件。两者代价不对等，因此程序固定采用避让策略，未提供覆盖开关。

---

## 二、下载与运行

从 [Releases](https://github.com/Utpop/SubFusion/releases) 页面下载最新版本的 `SubFusion.exe`。

- 该文件为单文件自包含程序，已内置 .NET 8 运行时与 Windows App SDK，**无需另行安装** .NET 或 Windows App SDK。
- 建议保持文件名为 `SubFusion.exe` 后直接双击运行。
- 程序清单声明 `requireAdministrator`，首次运行将请求管理员权限（UAC）提升，用于写入受保护目录及拖放兼容。
- 单文件版首次启动会向 `%TEMP%\.net\` 释放运行时文件，属 .NET 官方单文件机制，后续启动不再重复释放。

### 2.1 外部依赖

运行时需要本机已安装 `ffmpeg` 与 `ffprobe`。其中硬字幕功能需要 ffmpeg 包含 `libass`，HDR 色调映射需要 `zscale` 与 `tonemap` 滤镜。

程序按以下顺序查找这两个可执行文件：

> 配置文件中的路径 → 环境变量（`FFMPEG_PATH` / `FFPROBE_PATH`）→ 程序所在目录 → 系统 `PATH` → 常见安装位置（如 `C:\ffmpeg\bin`）

若均未找到，可在「设置 → 媒体工具」中手动指定 `ffmpeg.exe` 与 `ffprobe.exe` 的完整路径，并可点击「重新检测」确认状态。

### 2.2 支持的格式

- **视频**：MP4、MKV、MOV、M4V、AVI、WMV、FLV、F4V、WebM、TS、M2TS、MPG、MPEG、VOB
- **文本字幕**：SRT、ASS、SSA、VTT
- **图形字幕**：SUP、SUB

图形字幕无法封装进 MP4 容器，相关操作请选择 MKV。

---

## 三、从源码构建

### 3.1 环境要求

- Windows x64，Windows 10 1809（10.0.17763）及以上
- .NET 8 SDK（更高版本的 SDK 亦可构建）
- Windows SDK 10.0.17763 及以上

### 3.2 常用命令

```bat
:: 构建解决方案（Core / App / SelfTest）
dotnet build SubFusion.sln -c Release

:: 开发运行
dotnet run --project src/SubFusion.App/SubFusion.App.csproj

:: 自检（跳过需要真实 ffmpeg 的端到端用例）
dotnet run --project src/SubFusion.SelfTest/SubFusion.SelfTest.csproj -c Release -- --skip-e2e

:: 完整自检（包含真实 ffmpeg 端到端验证）
dotnet run --project src/SubFusion.SelfTest/SubFusion.SelfTest.csproj -c Release

:: 发布为目录形式（输出至 dist/SubFusion）
dotnet publish src/SubFusion.App/SubFusion.App.csproj -c Release -o dist/SubFusion

:: 发布为单文件形式（输出至 dist/SingleExe/SubFusion.exe）
dotnet publish src/SubFusion.App/SubFusion.App.csproj -c Release -p:PublishProfile=SingleFile
```

根目录的 `启动.bat` 会先以 Release 配置发布目录版，再启动 `dist/SubFusion/SubFusion.exe`，确保运行的是当前源码。

> 若自检报告临时目录 `UnauthorizedAccessException`，请先将环境变量 `TMP` 与 `TEMP` 指向工作区内的目录再执行。详见 [AGENT.MD](AGENT.MD)。

---

## 四、项目结构

```
SubFusion/
├── SubFusion.sln              # 解决方案（Core / App / SelfTest）
├── AGENT.MD                   # 工程说明，面向后续维护者
├── README.md                  # 本文件
├── Directory.Build.props      # 单文件发布时对引用项目嵌入 PDB
├── app.ico                    # 多尺寸图标（PE 文件图标与窗口图标）
├── assets/app-icon.png        # 图标母版
├── tools/                     # 图标生成脚本
├── docs/                      # 设计与调研文档
├── 启动.bat                   # 发布并启动脚本
└── src/
    ├── SubFusion.Core/        # net8.0 类库：纯业务逻辑，不依赖任何 UI 类型
    │   ├── Models/            #   字幕侧模型（SubTrack / Job / Options 等）
    │   ├── Services/          #   配置、工具定位、编码、匹配、命令构建、输出校验、文件搬运
    │   ├── Media/             #   媒体探测与 ffmpeg 工具门面（各模块共享）
    │   ├── Merge/             #   视频合并引擎、兼容性判定、命令规划、组合导出
    │   ├── Trim/              #   视频裁剪引擎、关键帧扫描、旋转方案、音画同步检测
    │   ├── Compress/          #   视频压制引擎、编码器探测与选择
    │   └── Chapters/          #   MKV 章节时间轴与 ffmetadata 写入
    ├── SubFusion.App/         # WinUI 3 桌面应用
    │   ├── Pages/             #   字幕合并 / 视频合并 / 视频裁剪 / 视频压制 / 任务中心 / 设置
    │   ├── ViewModels/        #   各页面 ViewModel（不引用任何 WinUI 类型）
    │   ├── Views/             #   主窗口与字幕轨编辑对话框
    │   ├── Services/          #   UI 服务、对话框、崩溃保护、主题、设置存储
    │   └── Styles/            #   设计令牌与共享控件样式
    └── SubFusion.SelfTest/    # 逻辑等价比对、真实 ffmpeg 端到端验证、健壮性自检
        └── Reference/         #   Python 版的冻结基准值
```

---

## 五、配置

配置文件位于 `%USERPROFILE%\.subfusion.json`，编码为 UTF-8 无 BOM，缩进 2 空格，中文不转义。文件中无法识别的配置项会被原样保留并写回，不会被丢弃。

主要配置项：

| 分类 | 键 |
| --- | --- |
| 外观 | `theme`（system / light / dark） |
| 媒体工具 | `ffmpeg`、`ffprobe` |
| 字幕合并 | `container`、`mode`、`vcodec`、`acodec`、`crf`、`preset`、`workers`、`suffix`、`outdir`、`sync_outdir`、`keep_existing_subs`、`keep_metadata`、`convert_encoding`、`hard_multi`、`verify`、`replace_source`、`overwrite`、`threshold`、`recursive` |
| 视频合并 | `merge_container`、`merge_allow_reencode`、`merge_codec`、`merge_crf`、`merge_preset`、`merge_fit`、`merge_audio`、`merge_chapters`、`merge_verify`、`merge_delete_source`、`merge_partial_reencode`、`merge_auto_fallback`、`merge_salvage`、`merge_outdir` |
| 视频裁剪 | `trim_container`、`trim_mode`、`trim_rotation`、`trim_codec`、`trim_crf`、`trim_preset`、`trim_verify`、`trim_outdir` |
| 视频压制 | `compress_container`、`compress_codec`、`compress_encoder`、`compress_backend`、`compress_hw_quality`、`compress_quality`、`compress_rate_control`、`compress_bitrate`、`compress_preset`、`compress_resize`、`compress_long_edge`、`compress_fps`、`compress_reencode_audio`、`compress_audio_bitrate`、`compress_hw_decode`、`compress_verify`、`compress_outdir` |
| 会话 | `last_dir` |

> `overwrite` 仅作用于字幕合并（用于替换原文件流程）；视频合并、裁剪与压制固定采用自动改名策略，不读取该键。

---

## 六、已知限制

1. 仅支持 Windows x64，要求 Windows 10 1809（17763）及以上；程序清单声明 `requireAdministrator`，每次启动均会请求 UAC 提升。
2. `ffmpeg` 与 `ffprobe` 未内嵌，需自行安装并保证位于可查找路径中。
3. 运行日志仅在界面内显示，不写入磁盘；未处理异常会记录至 `%TEMP%\subfusion_crash.txt`。
4. MP4 容器不支持图形字幕与自动章节；HDR 色调映射要求 ffmpeg 包含 `zscale` 与 `tonemap` 滤镜。
5. 视频裁剪的「保留字幕轨」默认关闭：字幕时间轴不会随裁剪重算，保留后可能不同步。

---

## 七、验证状态

- `dotnet build SubFusion.sln -c Release`：**0 警告，0 错误**
- `SubFusion.SelfTest --skip-e2e`：**662 项通过，0 失败**
- 完整自检（含真实 ffmpeg 端到端）：**676 项通过，0 失败**

自检覆盖范围包括：与 Python 版逐项等价性比对、真实编码与合并端到端验证、界面布局约束、重负载健壮性、替换原文件、并发隔离、视频合并引擎、MKV 章节、校验阶梯、片段级重编码、视频裁剪、视频压制与硬件编码探测、输出命名避让、进度可达性等。

更多实现细节与维护约定见 [AGENT.MD](AGENT.MD)。

---

## 八、许可证

本仓库暂未附带开源许可证文件。如需在项目中复用代码，请先与作者联系。
