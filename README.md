# SubtitleMerger（C# / WinUI 3 版）

批量把字幕合进视频：支持 MKV / MP4 输出、软字幕（内挂多轨）与硬字幕（烧录）、拖放添加、自动匹配视频与字幕、中文/日文等 Unicode 字幕自动转码，默认**不重新编码**视频。

> 本项目由 Python 3 + Tkinter 版完整重构为 C# / WinUI 3，业务逻辑、配置项、文件路径与格式、提示文案、日志行为逐项等价保留。
> 完整的[功能清单](docs/01-功能清单.md)、[逐项对照表](docs/02-功能对照表.md)、[架构说明](docs/03-架构与工程结构.md)、[替代方案与交互差异](docs/04-替代方案与交互差异.md)、[迁移与验证说明](docs/05-迁移与验证说明.md)见 `docs/`。

---

## 快速开始

**运行已发布程序**：把 `dist/SubtitleMerger/` 整个目录拷到任意 Windows 电脑，双击 `SubtitleMerger.exe`。
不需要安装 .NET、Python 或 Windows App SDK 运行时（发布目录已自带）。

**从源码构建**：

```bash
# 需要 .NET 8 SDK（本机验证过 9.0/10.0 SDK）、Windows SDK 10.0.17763+
dotnet build SubtitleMerger.sln -c Release
dotnet run --project src/SubtitleMerger.App/SubtitleMerger.App.csproj

# 生成可分发目录
dotnet publish src/SubtitleMerger.App/SubtitleMerger.App.csproj -c Release -o dist/SubtitleMerger
```

**运行时唯一外部依赖**：`ffmpeg` / `ffprobe`（硬字幕需要带 `libass`）。程序按
`配置 → 环境变量 FFMPEG_PATH / FFPROBE_PATH → 程序目录 / bin → PATH → C:\ffmpeg\bin 等常见位置`
自动查找；找不到时点右上角「更改」手动指定，路径会记住。

配置仍写在 `%USERPROFILE%\.subtitle_merger.json`，**与 Python 旧版双向兼容**，升级无需改动任何设置。

---

## 功能

| 需求 | 支持情况 |
| --- | --- |
| 视频格式 | MP4、MKV、MOV、M4V、AVI、WMV、FLV、F4V、WebM、TS、M2TS、MPG、MPEG、VOB |
| 字幕格式 | SRT、ASS/SSA、VTT（文本）；SUP、SUB（图形） |
| 批量处理 | 整个文件夹，可含子文件夹（可关闭）；自动跳过隐藏子目录 |
| 自动匹配 | 按文件名智能匹配，阈值可调（默认 0.72），支持 `.zh` / `.en` / `.jpn` 等语言后缀 |
| 拖放 | WinUI 3 原生拖放；也可用「添加视频 / 添加字幕 / 添加文件夹」按钮 |
| 软字幕 / 硬字幕 | 两种模式；硬字幕可选「叠加全部字幕轨」或「只烧录第一条」 |
| 输出容器 | MKV / MP4 / 与源文件相同 |
| 多字幕轨道 | 一个视频挂任意多条，可设语言、轨道名、默认轨、强制轨、顺序 |
| Unicode 字幕 | 自动检测 GB18030 / Big5 / Shift-JIS / EUC / UTF-16 等并转 UTF-8，中文日文不乱码 |
| 保留原视频编码 | 默认 `-c:v copy -c:a copy`，秒级完成，画质零损失 |
| 进度 | 逐文件实时显示进度、速度、剩余时间，另有总进度条 |
| 失败隔离 | 单个失败仅标记该行，其余继续；最后汇总完成/失败/跳过/取消 |
| 输出校验 | 每个输出自动核对字幕轨、音轨、视频流、编码、时长、体积与数据完整性，异常标黄并写进报告 |
| 报告 | 导出 CSV（UTF-8 BOM，`合并报告_YYYYMMDD_HHMMSS.csv`） |

---

## 使用

1. **添加素材**：把视频、字幕或文件夹拖进窗口顶部虚线框，或点「添加视频 / 添加字幕 / 添加文件夹」。
2. **检查匹配**：列表每行一个视频，「字幕轨」列显示自动匹配结果；双击行可手动增删字幕、设置语言与默认轨；右键还有移除、打开所在文件夹等。
3. **设置输出**：选容器、字幕方式、输出目录；想和源文件放一起点「同源文件」（不会覆盖原文件，同名自动加 `_sub`）。
4. **点「开始处理」**：实时看进度、速度、剩余时间；随时可「停止」。
5. 完成后可「打开输出目录」或「导出报告」，日志页可整段选中复制。

> 开始处理前**必须选择输出目录**（与原版一致）；未选时会明确弹窗提示。

---

## 软字幕 vs 硬字幕

| | 软字幕（内挂） | 硬字幕（烧录） |
| --- | --- | --- |
| 速度 | 极快（不重编码，通常 1–3 秒） | 慢（必须重新编码视频） |
| 画质 | 无损 | 有损（取决于 CRF） |
| 可关闭 / 切换 | 可以 | 不可以 |
| 样式 | MKV 保留 ASS 特效 | 由 libass 渲染，依赖系统字体 |
| 推荐场景 | 绝大多数情况 | 短视频平台、不支持字幕轨的播放器 |

三个格式限制：

- **MP4 文本字幕必须转 `mov_text`**（ASS 特效会丢失）；MKV 可完整保留。
- **SUP / SUB 图形字幕无法放进 MP4**，程序会自动跳过并在日志提示，请选 MKV。
- 源视频是 VP9 / AV1 等编码时输出到 MP4 可能需要重编码，把视频编码改成 `libx264` 即可。

---

## 自动匹配规则

- 先规范化文件名：去扩展名、括号内容、分辨率/编码/来源噪声词（1080p、x264、WEB-DL…），去掉语言后缀，只留核心标题。
- 完全一致 → 命中；包含关系 → 高置信度（≥ 0.72）；否则用 `difflib.SequenceMatcher` 相似度，默认阈值 **0.72**（可调 0.4–1.0）。
- 一个视频可挂多条字幕；匹配不上的字幕不会乱挂，计数栏会提示未匹配数量。
- 特殊命名可双击视频行手动指定；手工编辑过的行在「重新匹配」时会保留手动增删的轨。

---

## 字幕编码处理

MKV 字幕轨不记录编码，播放器一律按 UTF-8 解析，GBK / Big5 老字幕直接内挂会乱码。程序会：

1. 检测原字幕编码（BOM、严格 UTF-8、GB18030 / Big5 / Shift-JIS / EUC 评分）；
2. 非 UTF-8 时自动转成 UTF-8 再合并（可在「字幕与匹配」页关闭）；
3. 按文件名语言标记（`.zh` `.en` `.jpn` `.cht`…）自动写入轨道语言。

硬字幕另会把字幕复制成纯 ASCII 文件名的工作副本，规避 libass 在 Windows 上的路径转义问题。

---

## 输出校验

默认开启，可在「输出」页关闭：

| 检查 | 能发现的问题 |
| --- | --- |
| 字幕轨数量 | 字幕没挂上、图形字幕被跳过后数量不符 |
| 音轨 / 视频流 / 编码 | 音轨丢失、输出为空壳、重编码结果不符预期 |
| 时长对比 | 与源时长差异过大（容差 `max(2 秒, 5%)`） |
| 数据完整性 | 解码开头 1 秒与结尾 2 秒，发现截断/损坏 |
| 体积对比 | 纯封装模式下体积明显缩水 |

全部正常 → 「完成（已校验）」；有异常 → 「完成（校验异常）」并整行标黄，日志逐条列出，报告单独一列。
**校验异常不是处理失败**（文件已生成），只提醒人工确认。

### 性能与稳定性

- 添加文件后 4 线程并发探测媒体信息，开始处理前再核对一次；
- 同一份字幕被多个视频引用时只检测/转换一次（带缓存，每轮结束清空）；
- 单个任务或单个字幕文件失败只影响自己；
- 随时「停止」会立即终止 ffmpeg，已完成输出保留；
- 所有探测与合并都在后台任务，界面不阻塞。

---

## 项目结构

```
SubtitleMerger/
├── SubtitleMerger.sln
├── README.md
├── app.ico
├── docs/                          # 迁移交付文档（5 份）
├── src/
│   ├── SubtitleMerger.Core/       # 业务核心，net8.0，无 UI 依赖
│   ├── SubtitleMerger.App/        # WinUI 3 桌面应用（Views / ViewModels / Services）
│   └── SubtitleMerger.SelfTest/   # 等价性 + 端到端自检
└── dist/                          # dotnet publish 产物（不入库）
```

`Core` 与 `App` 拆分、MVVM、`IUiServices` / `IBatchEvents` 接口的设计理由见 [03 · 架构与工程结构](docs/03-架构与工程结构.md)。

---

## 验证

```bash
dotnet run --project src/SubtitleMerger.SelfTest/SubtitleMerger.SelfTest.csproj -c Release
# => 通过：206 项，失败：0 项（含 ffmpeg 端到端）

dist/SubtitleMerger/SubtitleMerger.exe --smoke
# => 退出码 0，写出 %TMP%/subtitle_merger_smoke.txt
```

自检 A 组用原 Python 版 `core.py` 生成的冻结基准 `Reference/reference.json` 逐项比对，B 组用本机 ffmpeg 真实跑软/硬字幕、GBK 转码、输出校验与取消行为。

---

## 常见问题

**Q：提示没有 ffmpeg？**
安装 ffmpeg 并加入 PATH，或点右上角「更改」选 `ffmpeg.exe`（会自动找同目录的 `ffprobe.exe`）。路径会被记住。

**Q：处理失败怎么排查？**
「日志」页会打印每条任务的 ffmpeg 命令与错误信息，可整段选中复制。常见原因：输出目录无写入权限、源文件损坏、MP4 装不下图形字幕、硬字幕缺少 libass。

**Q：发布版 exe 单独拷出来打不开？**
`dist/SubtitleMerger/` 需整体拷贝（含 `.xbf`、`.pri` 与运行时 DLL）。

**Q：拖放没反应？**
改用「添加视频 / 添加字幕 / 添加文件夹」按钮，功能完全一致。

**Q：旧版 Python 配置和设置还在吗？**
在。`%USERPROFILE%\.subtitle_merger.json` 路径与 19 个键完全兼容，直接沿用。

**Q：想回退到 Python 版？**
Python 源码仍在本仓库 git 历史中，详见 [05 · 迁移与验证说明](docs/05-迁移与验证说明.md)。
