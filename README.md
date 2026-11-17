# SubFusion

SubFusion 是一个 Windows x64 桌面工具，把`字幕合并`与`视频合并`整合在同一界面里。它由早期的 Python/Tkinter 版重构而来，现基于 C# / WinUI 3，包含两个相互独立的业务模块：

- `字幕合并`：批量把外挂字幕合进视频（软字幕内挂多轨 / 硬字幕烧录），按文件名智能匹配，自动识别并转码 GB18030 / Big5 / Shift-JIS / UTF-16 等字幕编码。
- `视频合并`：把多段视频合成一个文件，优先无损拼接（`-c copy`），参数不兼容时统一重编码；支持 MKV 自动生成章节、HDR→SDR 色调映射、分辨率/帧率/音频统一。

两个模块共享底层媒体基础设施（ffmpeg/ffprobe 定位、子进程执行、媒体探测、输出校验），业务逻辑互不耦合。

---

## 功能

### 字幕合并
| 功能 | 说明 |
| --- | --- |
| 输出容器 | MKV / MP4 / 与源相同 |
| 字幕方式 | 软字幕（内挂多轨）/ 硬字幕（烧录），硬字幕可选叠加全部或只烧第一条 |
| 自动匹配 | 标题 + 集数 + 语言标记打分，阈值可调（默认 0.72）；同目录多语言字幕可同时挂载；支持手动调整 |
| 字幕编码 | 自动检测并转 UTF-8，中文/日文不乱码；MKV 保留 ASS 特效，MP4 自动转 `mov_text` |
| 多轨设置 | 每条字幕可设语言、轨道名、默认轨、强制轨与顺序 |
| 批量与并发 | 文件夹递归添加（可关，跳过隐藏子目录）、有界并发探测（4 线程）；失败隔离，单个失败不影响其他任务 |
| 替换原文件 | 校验通过后可用结果替换原视频与原字幕（安全顺序：先落盘结果，再删原件；校验跳过时不删） |
| 输出校验 | 核对字幕轨/音轨/视频流/编码/时长/体积及首尾解码完整性，异常标黄并写入报告 |
| 报告 | 导出 CSV（UTF-8 BOM、CRLF） |

### 视频合并
| 功能 | 说明 |
| --- | --- |
| 无损优先 | 编码、分辨率、像素格式、帧率、时间基准、SAR、色彩空间、HDR 标记、音轨参数逐项一致时直接 `-c copy` 拼接 |
| 不兼容重编码 | 任一参数不一致即统一编码（H.264/H.265），再拼接；可关闭自动重编码（不兼容则直接失败） |
| 分辨率适配 | Contain（补黑边）/ Cover（裁切）/ Stretch（拉伸），默认取输入最大值不降质；自动保证偶数尺寸 |
| 音频统一 | 采样率/声道统一；缺音轨的片段补静音保证时间轴对齐；多音轨只取第一条并给出提示 |
| HDR 处理 | HDR/SDR 混合输入做真正的 `zscale`+`tonemap` 色调映射；纯 HDR 用 `setparams` 保留色彩标记 |
| MKV 自动章节 | 每段一章，标题取文件名可自定义；时间轴按实际输出时长归一化；ffmetadata + `-map_chapters` 写入，全程不重编码 |
| 安全落盘 | 校验通过后才移动到最终路径，失败不留半成品 |

---

## 下载与运行

从 [Releases](https://github.com/Utpop/SubFusion/releases) 下载 `SubFusion-vX.Y.Z-win-x64.zip`，解压后：

- 双击 `启动.bat`，或直接双击 `SubFusion.exe`。
- 首次运行会请求`管理员权限（UAC）`提升（批量写输出目录与拖放兼容所需）。
- 程序自带 .NET 8 / Windows App SDK 运行时，`无需安装` .NET 或 WinAppSDK。

### 外部依赖

运行时需要本机安装 `ffmpeg / ffprobe`（硬字幕需带 `libass`，HDR 色调映射需 `zscale`/`tonemap`）。程序按以下顺序自动查找：

> 配置文件 → 环境变量（`FFMPEG_PATH` / `FFPROBE_PATH`）→ 程序目录 → `PATH` → 常见安装位置（如 `C:\ffmpeg\bin`）

找不到时可在`设置`页手动指定 `ffmpeg.exe` 路径。

### 支持格式

- **视频**：MP4、MKV、MOV、M4V、AVI、WMV、FLV、F4V、WebM、TS、M2TS、MPG、MPEG、VOB
- **文本字幕**：SRT、ASS、SSA、VTT
- **图形字幕**：SUP、SUB（图形字幕无法放进 MP4，请选 MKV）

---

## 从源码构建

环境要求：Windows x64、.NET 8 SDK（本机验证过 9.0/10.0 SDK）、Windows SDK 10.0.17763+。

```
# 解决方案构建（Core / App / SelfTest）
dotnet build SubFusion.sln -c Release

# 开发运行
dotnet run --project src/SubFusion.App/SubFusion.App.csproj

# 自检（A 逻辑等价 + B 端到端 + C 布局 + D 健壮性 + E 替换 + F 并发 + G 合并 + H 章节）
dotnet run --project src/SubFusion.SelfTest/SubFusion.SelfTest.csproj -c Release

# 发布：目录版（dist/SubFusion）
dotnet publish src/SubFusion.App/SubFusion.App.csproj -c Release -o dist/SubFusion

# 发布：单文件版（dist/SingleExe/SubFusion.exe，约 220 MB）
dotnet publish src/SubFusion.App/SubFusion.App.csproj -c Release -p:PublishProfile=SingleFile
```

根目录 `启动.bat` 在单文件 EXE 不存在时会自动执行单文件发布。

> 若自检报临时目录 `UnauthorizedAccessException`，先把 `TMP` / `TEMP` 指到工作区目录再运行（详见 [AGENT.MD](AGENT.MD)）。

---

## 项目结构

```
SubFusion/
├── SubFusion.sln              # 解决方案（Core / App / SelfTest）
├── AGENT.MD                   # 工程说明（面向后续维护者）
├── 启动.bat                   # 一键启动/发布脚本
└── src/
    ├── SubFusion.Core/        # net8.0 类库，纯业务逻辑，无 UI 依赖
    │   ├── Models/            #   字幕侧模型（SubTrack / Job / Options / ...）
    │   ├── Services/          #   配置、工具定位、编码、匹配、命令、校验、批处理
    │   ├── Media/             #   媒体探测与 ffmpeg 工具门面（两模块共享）
    │   ├── Merge/             #   视频合并引擎、兼容性判定、命令规划
    │   └── Chapters/          #   MKV 章节时间轴与 ffmetadata 写入
    ├── SubFusion.App/         # WinUI 3 桌面应用
    │   ├── Pages/             #   字幕合并 / 视频合并 / 任务中心 / 设置
    │   ├── ViewModels/        #   Shell 及各页面 ViewModel（不引用 WinUI 类型）
    │   ├── Views/             #   主窗口与字幕轨编辑对话框
    │   └── Services/          #   UI 服务、对话框、崩溃保护、主题等
    └── SubFusion.SelfTest/    # 逻辑等价 + ffmpeg 端到端 + 健壮性自检
        └── Reference/         #   Python v1.1 冻结基准值
```

---

## 配置

配置文件：`%USERPROFILE%\.subfusion.json`（UTF-8 无 BOM、中文不转义、indent=2）。未知键会原样保留并写回。主要键：

- 外观：`theme`（system / light / dark）
- 工具：`ffmpeg`、`ffprobe`
- 字幕：`container`、`mode`、`vcodec`、`acodec`、`crf`、`preset`、`workers`、`suffix`、`outdir`、`sync_outdir`、`keep_existing_subs`、`keep_metadata`、`convert_encoding`、`hard_multi`、`verify`、`replace_source`、`overwrite`、`threshold`、`recursive`
- 视频合并：`merge_container`、`merge_allow_reencode`、`merge_codec`、`merge_crf`、`merge_preset`、`merge_fit`、`merge_audio`、`merge_chapters`、`merge_verify`、`merge_outdir`
- 会话：`last_dir`

---

## 已知限制

1. 仅支持 `Windows x64`，Windows 10 1809 (17763) 及以上，`requireAdministrator` 清单会请求 UAC 提升。
2. `ffmpeg / ffprobe 未内嵌`，需自行安装；单文件版首次启动会向 `%TEMP%\.net\` 释放运行时文件（.NET 官方机制）。
3. 日志只在界面显示、不落盘；崩溃信息写入 `%TEMP%\subtitle_merger_crash.txt`。
4. MP4 容器不支持图形字幕与自动章节；HDR 色调映射需要 ffmpeg 带 `zscale` / `tonemap` 滤镜。

---

## 验证状态

- `dotnet build SubFusion.sln -c Release`：**0 Warning / 0 Error**
- `SubFusion.SelfTest`：**327 项通过 / 0 失败**（含 ffmpeg 端到端、界面布局、重负载健壮性、替换原文件、并发隔离、视频合并、MKV 章节）

更多信息见 [AGENT.MD](AGENT.MD)。
