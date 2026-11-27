using System.Globalization;
using System.Text;
using SubFusion.Core.Chapters;
using SubFusion.Core.Media;
using SubFusion.Core.Services;

namespace SubFusion.Core.Merge;

/// <summary>
/// 视频合并引擎。
///
/// 处理流程：
/// <list type="number">
/// <item>校验参数 → 探测全部输入（<see cref="MediaInspector"/>）→ 兼容性判定（<see cref="CompatibilityAnalyzer"/>）；</item>
/// <item>兼容时走 concat demuxer + stream copy 的无损拼接；不兼容时逐段统一参数重新编码，再无损拼接中间文件；</item>
/// <item>按最终时间轴生成 MKV 章节并写入（ffmetadata + map_chapters + copy，不触发重编码）；</item>
/// <item>用 ffprobe 回读校验输出结构与章节时间轴，并做开头/结尾解码完整性检查。</item>
/// </list>
/// 全程异步、可取消；取消时终止整棵子进程树，并等待管道收尾。
/// </summary>
public sealed class VideoMergeEngine
{
    private readonly FfmpegTools _tools;
    private readonly IProgress<MergeProgress>? _progress;

    public VideoMergeEngine(FfmpegTools tools, IProgress<MergeProgress>? progress = null)
    {
        _tools = tools;
        _progress = progress;
    }

    /// <summary>只做分析，不产生任何输出文件。</summary>
    public async Task<(MergeCompatibility Compatibility, List<MediaBundle> Bundles)> AnalyzeAsync(
        VideoMergeOptions options, CancellationToken cancellationToken = default)
    {
        var bundles = await MediaInspector.InspectManyAsync(_tools.Ffprobe ?? "", options.Inputs,
            cancellationToken, 4, options.ProbeTimeoutSeconds).ConfigureAwait(false);
        return (CompatibilityAnalyzer.Analyze(bundles), bundles);
    }

    public async Task<MergeResult> MergeAsync(VideoMergeOptions options, CancellationToken cancellationToken = default)
    {
        string taskId = Guid.NewGuid().ToString("N");
        double startedAt = Formatting.UnixNow();
        var diagnostics = new List<string>();
        var notices = new List<string>();
        string? tempDir = null;

        void Report(MergeStage stage, double percent, string message, int step = 0, int steps = 0,
                    double speed = 0, double? eta = null)
            => _progress?.Report(new MergeProgress(stage, Math.Clamp(percent, 0, 100), message,
                step, steps, speed, eta));

        try
        {
            // 1) 参数校验
            Report(MergeStage.Validating, 0, "校验参数…");
            List<string> errors = options.Validate();
            if (errors.Count > 0)
                return Fail(taskId, MergeStage.Validating, string.Join("；", errors), diagnostics, startedAt);

            if (!_tools.Available)
                return Fail(taskId, MergeStage.Validating, "未找到 ffmpeg，无法合并视频", diagnostics, startedAt);
            if (!_tools.ProbeAvailable)
                return Fail(taskId, MergeStage.Validating, "未找到 ffprobe，无法探测媒体信息", diagnostics, startedAt);

            string outputPath = Path.GetFullPath(options.OutputPath);
            string? outputDir = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrEmpty(outputDir))
                return Fail(taskId, MergeStage.Validating, "输出路径无效：" + options.OutputPath, diagnostics, startedAt);
            Directory.CreateDirectory(outputDir);
            // 目标已存在时自动避让，而不是覆盖或直接失败：
            // 覆盖会静默毁掉上一份成品，直接失败又会让用户每次都要手动改名字。
            string requestedOutputPath = outputPath;
            outputPath = OutputNaming.Unique(outputPath);
            if (!string.Equals(outputPath, requestedOutputPath, StringComparison.OrdinalIgnoreCase))
                notices.Add("目标文件已存在，本次另存为 " + Path.GetFileName(outputPath));

            // 每个任务独立临时目录：绝不与其它任务共用文件名。
            //
            // 位置有讲究：默认落在系统临时目录，而系统临时目录几乎总在 C:。
            // 输出若在别的盘，最后那一步「搬到目标路径」就是一次整文件跨盘复制——
            // 几十 GB 的输出要多花几分钟，界面上看起来像卡在 99%。
            // 所以这里优先让临时目录贴着输出盘：同卷时最后一步只是改名，瞬时完成。
            tempDir = CreateTempDirectory(options, outputDir, taskId);

            // 2) 探测输入
            Report(MergeStage.Probing, 2, "读取输入媒体信息…", 0, options.Inputs.Count);
            var bundles = await MediaInspector.InspectManyAsync(_tools.Ffprobe!, options.Inputs,
                cancellationToken, 4, options.ProbeTimeoutSeconds).ConfigureAwait(false);
            foreach (MediaBundle b in bundles)
                diagnostics.Add("[探测] " + b.Summary);
            if (bundles.Any(b => !b.Success))
            {
                // 取消会被 MediaInspector 记为「已取消」，那不是文件损坏。
                // 必须在这里分辨出来，否则用户点了取消却看到「输入文件无法读取」的假故障。
                cancellationToken.ThrowIfCancellationRequested();
                string bad = string.Join("；", bundles.Where(b => !b.Success)
                    .Select(b => Path.GetFileName(b.Path) + "（" + b.Error + "）"));
                return Fail(taskId, MergeStage.Probing, "输入文件无法读取：" + bad, diagnostics, startedAt);
            }

            // 纯音频文件没有视频流；不拦截会在 MergePlan.Create 的 Max 上抛出难以理解的错误。
            var noVideo = bundles.Where(b => b.PrimaryVideo is null)
                .Select(b => Path.GetFileName(b.Path)).ToList();
            if (noVideo.Count > 0)
                return Fail(taskId, MergeStage.Probing, "输入文件没有视频流：" + string.Join("；", noVideo),
                    diagnostics, startedAt);

            // 2.5) 输入完整性：容器声明的时长，后面是否真有那么多数据。
            //
            // 这一步必须放在兼容性判定之前：截断的文件在参数上与好文件完全一致，
            // 兼容性判定会愉快地放行，问题要等到输出校验才以「时长不符」的面目出现，
            // 那时已经白跑了一遍几十 GB 的拼接，而且报错指向的是输出、不是坏素材。
            Report(MergeStage.Probing, 3, "检查输入文件是否完整…", 0, bundles.Count);
            List<InputHealth> health = await InputIntegrity.ProbeManyAsync(_tools.Ffprobe!, bundles,
                cancellationToken).ConfigureAwait(false);
            List<InputHealth> truncated = health.Where(h => h.Truncated).ToList();
            foreach (InputHealth h in truncated) diagnostics.Add("[完整性] " + h.Describe());
            if (truncated.Count > 0)
            {
                if (!options.SalvageTruncatedInputs)
                    return Fail(taskId, MergeStage.Probing,
                        "输入文件尾部被截断（容器声明的时长后面没有数据）：" +
                        string.Join("；", truncated.Select(h => h.Describe())) +
                        "。开启「自动保留可读部分」可用剩余数据继续合并，或先重新获取这些文件。",
                        diagnostics, startedAt);

                Report(MergeStage.Probing, 4, "按可读部分收尾截断的输入…", 0, truncated.Count);
                for (int i = 0; i < bundles.Count; i++)
                {
                    InputHealth h = health[i];
                    if (!h.Truncated) continue;
                    cancellationToken.ThrowIfCancellationRequested();
                    // 收尾的前提是「真的算出了可保留长度」。可读终点未知或余量吃光时
                    // KeepSeconds 为 0，收尾会产出一个 0 秒片段并把整次合并拖垮，
                    // 这里必须先拦住，并明确说明是「无法确定」而不是「文件坏了」。
                    if (!h.ReadableKnown || h.KeepSeconds <= 0)
                        return Fail(taskId, MergeStage.Probing,
                            "无法确定该文件的可读范围，已停止合并以免破坏素材：" + h.Describe(),
                            diagnostics, startedAt);
                    string salvaged = Path.Combine(tempDir,
                        "salvaged_" + i.ToString("D3", CultureInfo.InvariantCulture) +
                        InputIntegrity.SalvagedExtension(bundles[i]));
                    (int salvageCode, string salvageError) = await FfmpegRunner.RunFfmpegAsync(
                        InputIntegrity.BuildSalvageCommand(_tools.Ffmpeg, bundles[i], salvaged, h.KeepSeconds),
                        h.KeepSeconds, null, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (salvageCode != 0 || !File.Exists(salvaged))
                        return Fail(taskId, MergeStage.Probing,
                            "截断输入无法按可读部分收尾：" + h.Describe() + "（" +
                            LastLines(salvageError) + "）", diagnostics, startedAt);

                    // 收尾后必须重新探测：拼接与章节时间轴都要用「实际可读时长」，
                    // 继续沿用容器声明的时长会让输出的时间轴从头短到尾。
                    MediaBundle repaired = await MediaInspector.InspectAsync(_tools.Ffprobe!, salvaged,
                        cancellationToken, options.ProbeTimeoutSeconds).ConfigureAwait(false);
                    if (!repaired.Success)
                        return Fail(taskId, MergeStage.Probing,
                            "收尾后的片段无法读取：" + h.Describe() + "（" + repaired.Error + "）",
                            diagnostics, startedAt);

                    diagnostics.Add(string.Format(CultureInfo.InvariantCulture,
                        "[完整性] {0} → 保留前 {1:F2}s（原声明 {2:F2}s）",
                        Path.GetFileName(bundles[i].Path), h.KeepSeconds, h.DeclaredSeconds));
                    notices.Add(string.Format(CultureInfo.InvariantCulture,
                        "{0} 尾部数据缺失（声明 {1:F2}s，实际只读到 {2:F2}s），已保留可读的 {3:F2}s 继续合并",
                        Path.GetFileName(bundles[i].Path), h.DeclaredSeconds, h.ReadableSeconds,
                        repaired.Duration));
                    bundles[i] = repaired;
                }
            }

            // 3) 兼容性判定与方案选择
            Report(MergeStage.Planning, 5, "判定合并方案…");
            MergeCompatibility compatibility = CompatibilityAnalyzer.Analyze(bundles);
            foreach (MergeReason reason in compatibility.Reasons)
                diagnostics.Add("[兼容性] " + reason.Detail);

            bool streamCopy = compatibility.CanStreamCopy;
            // 差异全部落在时间轴参数上时，重封装即可对齐，不触碰画面数据。
            bool remuxOnly = compatibility.CanRemuxOnly;
            if (!streamCopy && !remuxOnly && !options.AllowReencode)
                return Fail(taskId, MergeStage.Planning,
                    "输入文件无法无损拼接，且未允许自动重新编码：" +
                    string.Join("；", compatibility.Reasons.Select(r => r.Detail)),
                    diagnostics, startedAt);
            if (remuxOnly)
                notices.Add("输入参数仅时间轴不一致，已用无损重封装对齐（不重新编码）：" +
                            compatibility.PrimaryReason);
            else if (!streamCopy)
                notices.Add("输入参数不一致，已自动统一重新编码：" + compatibility.PrimaryReason);
            if (bundles.Any(b => b.HasAudio) && bundles.Any(b => !b.HasAudio))
                notices.Add("部分输入没有音轨，已用静音补齐以对齐时间轴");
            if (bundles.Any(b => b.Audios.Count > 1))
                notices.Add("多音轨输入只合并第一条音轨，其余音轨不进入输出");

            // 全量重编码是最后手段：先看能不能只动少数派。
            // 真实素材更常见的是「一批同源文件 + 一个参数不同的文件」，
            // 为那一个文件把其余全部重压一遍，既费时又有画质损失。
            PartialReencodePlan partial = (streamCopy || remuxOnly || !options.AllowReencode || !options.PartialReencode)
                ? new PartialReencodePlan()
                : PartialReencodeAnalyzer.Analyze(bundles);

            var plan = partial.IsPartial
                ? MergePlan.CreateForPartial(options,
                    partial.ReferenceIndices.Select(i => bundles[i]).ToList(), bundles, _tools.Capabilities)
                : MergePlan.Create(options, bundles, streamCopy || remuxOnly, _tools.Capabilities);
            notices.AddRange(plan.Notes);
            diagnostics.Add("[方案] " + plan.Describe());
            // 记住这条提示的下标：一旦核实不过退回全量重编码，它就不再成立，必须撤回。
            // 不撤回的话界面会同时显示「已保留 N 段原样」与「已改为全量重编码」两句互斥的话。
            int partialNoticeIndex = -1;
            if (partial.IsPartial)
            {
                partialNoticeIndex = notices.Count;
                notices.Add("已保留 " + partial.ReferenceIndices.Count + " 段原样，仅重编码 " +
                            partial.ReencodeIndices.Count + " 段参数不一致的输入");
                diagnostics.Add("[部分重编码] " + partial.Describe());
            }

            List<string> titles = ChapterTitles(options, bundles);
            List<double> durations = bundles.Select(b => Math.Max(0, b.Duration)).ToList();
            double totalDuration = durations.Sum();

            // 时长未知（视频流与容器都没写 duration，TS / 裸流 / 部分 MKV 常见）会让整条链静默失效：
            // 进度分母为 0（进度条永远停在起点）、章节被压成 1 毫秒、
            // OutputVerifier.CheckDuration 因 expected<=0 直接放行（时长断言形同关闭）。
            // 这里明确失败，而不是产出一个「看起来成功、其实什么都没校验」的结果。
            for (int i = 0; i < bundles.Count; i++)
            {
                if (durations[i] > 0) continue;
                return Fail(taskId, MergeStage.Probing,
                    "无法确定输入文件的时长：" + Path.GetFileName(bundles[i].Path) +
                    "。容器与视频流都没有写时长（TS / 裸流常见），合并后的进度、章节与时长校验都会失真，" +
                    "请先用 ffmpeg 转封装成带时长的 MP4/MKV 再合并。",
                    diagnostics, startedAt, notices);
            }

            // 4) 需要时逐段统一编码（中间文件始终放在本任务临时目录）
            var segmentPaths = new List<string>();
            if (streamCopy)
            {
                segmentPaths.AddRange(bundles.Select(b => b.Path));
            }
            else if (remuxOnly)
            {
                // 只重封装：每段 -c copy 到统一时基，速度取决于磁盘而不是 CPU。
                Report(MergeStage.Merging, 5, "统一时间基准（无损重封装）…", 0, bundles.Count);
                for (int i = 0; i < bundles.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string segment = Path.Combine(tempDir,
                        "remux_" + i.ToString("D3", CultureInfo.InvariantCulture) + ".mkv");
                    int index = i;
                    (int code, string error) = await FfmpegRunner.RunFfmpegAsync(
                        plan.BuildRemuxCommand(bundles[i], segment, _tools.Ffmpeg),
                        durations[i],
                        (percent, _, _) => Report(MergeStage.Merging,
                            5 + percent / 100.0 * 75.0,
                            string.Format(CultureInfo.InvariantCulture, "重封装 {0}/{1}：{2}",
                                index + 1, bundles.Count, Path.GetFileName(bundles[index].Path)),
                            index, bundles.Count),
                        cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (code != 0 || !File.Exists(segment))
                        return Fail(taskId, MergeStage.Merging,
                            string.Format(CultureInfo.InvariantCulture, "第 {0} 段重封装失败：{1}", i + 1,
                                LastLines(error)), diagnostics, startedAt);
                    diagnostics.Add("[重封装] " + Path.GetFileName(segment) + " ← " +
                                    Path.GetFileName(bundles[i].Path));
                    segmentPaths.Add(segment);
                }
            }
            else if (partial.IsPartial)
            {
                // 只重编码少数派：保留段直接用原文件，被编码的段对齐到保留段的参数。
                // 编码完成后会重新探测一次核实（见下面的 ValidatePartial），核实不过自动退回全量。
                var reencodeSet = new HashSet<int>(partial.ReencodeIndices);
                Report(MergeStage.Merging, 5, "重编码参数不一致的片段…", 0, partial.ReencodeIndices.Count);
                var produced = new Dictionary<int, string>();
                int doneCount = 0;
                foreach (int i in partial.ReencodeIndices)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string segment = Path.Combine(tempDir,
                        "partial_" + i.ToString("D3", CultureInfo.InvariantCulture) + ".mkv");
                    int index = i;
                    double length = Math.Max(0.1, durations[i]);
                    (int code, string error) = await FfmpegRunner.RunFfmpegAsync(
                        plan.BuildSegmentCommand(bundles[i], segment, _tools.Ffmpeg),
                        length,
                        (percent, speed, eta) => Report(MergeStage.Merging,
                            5 + (doneCount + percent / 100.0) / Math.Max(1, partial.ReencodeIndices.Count) * 70.0,
                            string.Format(CultureInfo.InvariantCulture, "重编码 {0}/{1}：{2}",
                                doneCount + 1, partial.ReencodeIndices.Count,
                                Path.GetFileName(bundles[index].Path)),
                            doneCount, partial.ReencodeIndices.Count, speed, eta),
                        cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (code != 0 || !File.Exists(segment))
                        return Fail(taskId, MergeStage.Merging,
                            string.Format(CultureInfo.InvariantCulture, "第 {0} 段重编码失败：{1}", i + 1,
                                LastLines(error)), diagnostics, startedAt);
                    diagnostics.Add("[部分重编码] 重编码 " + Path.GetFileName(bundles[i].Path));
                    produced[i] = segment;
                    doneCount++;
                }

                // 统一容器与时基：重编码产出的片段必然是 mkv/1:1000，而保留段可能是 mp4/1:12288。
                // 这两项不统一，拼接依然不可用（核实会直接拦下）。
                // 保留段走的是 -c copy 重封装，视频码流逐比特不变，不算「重新编码」。
                Report(MergeStage.Merging, 76, "统一容器与时间基准…", 0, bundles.Count);
                for (int i = 0; i < bundles.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string source = reencodeSet.Contains(i) ? produced[i] : bundles[i].Path;
                    string unified = Path.Combine(tempDir,
                        "unified_" + i.ToString("D3", CultureInfo.InvariantCulture) + ".mkv");
                    int index = i;
                    (int ucode, string uerror) = await FfmpegRunner.RunFfmpegAsync(
                        plan.BuildRemuxCommandForFile(source, unified, _tools.Ffmpeg),
                        durations[i],
                        (percent, _, _) => Report(MergeStage.Merging,
                            76 + percent / 100.0 * 2.0,
                            string.Format(CultureInfo.InvariantCulture, "统一时间基准 {0}/{1}",
                                index + 1, bundles.Count),
                            index, bundles.Count),
                        cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (ucode != 0 || !File.Exists(unified))
                        return Fail(taskId, MergeStage.Merging,
                            string.Format(CultureInfo.InvariantCulture, "第 {0} 段统一时间基准失败：{1}", i + 1,
                                LastLines(uerror)), diagnostics, startedAt);
                    if (!reencodeSet.Contains(i))
                        diagnostics.Add("[保留段] " + Path.GetFileName(bundles[i].Path) + " 未重新编码（仅重封装）");
                    segmentPaths.Add(unified);
                }

                // 核实：重编码段的实际参数必须真的能与保留段拼接。
                // 这一步不能省——编码器未必如实兑现 -r / -pix_fmt / SAR 等请求，
                // 而「以为统一了、其实没统一」产出的坏文件比直接失败更糟。
                Report(MergeStage.Merging, 78, "核实重编码结果…");
                var probeTargets = new List<(int Index, string Path)>();
                for (int i = 0; i < bundles.Count; i++)
                    if (reencodeSet.Contains(i)) probeTargets.Add((i, segmentPaths[i]));
                List<string> verifyIssues = await ValidatePartialAsync(segmentPaths, cancellationToken)
                    .ConfigureAwait(false);
                if (verifyIssues.Count > 0)
                {
                    // 核实不过就退回全量重编码。「以为统一了、其实没统一」产出的坏文件
                    // 比多花点时间重编码糟得多，所以这里不做任何宽容。
                    diagnostics.Add("[部分重编码] 核实未通过，退回全量重编码：" + string.Join("；", verifyIssues));
                    if (partialNoticeIndex >= 0 && partialNoticeIndex < notices.Count)
                        notices.RemoveAt(partialNoticeIndex);
                    notices.Add("重编码结果与保留段仍不一致，已自动改为全量重编码（" +
                                verifyIssues[0] + "）");
                    segmentPaths.Clear();
                    partial = new PartialReencodePlan();
                    plan = MergePlan.Create(options, bundles, streamCopy: false, _tools.Capabilities);
                    MergeResult? fallbackFailure = await ReencodeAllSegmentsAsync(taskId, plan, bundles, durations,
                        totalDuration, tempDir, segmentPaths, diagnostics, startedAt, cancellationToken)
                        .ConfigureAwait(false);
                    if (fallbackFailure is not null) return fallbackFailure;
                }
                else
                {
                    diagnostics.Add("[部分重编码] 核实通过：" + string.Join("，",
                        probeTargets.Select(t => Path.GetFileName(t.Path))));
                }
            }
            else
            {
                // 全量重编码：所有输入统一到同一参数后再拼接。
                MergeResult? failure = await ReencodeAllSegmentsAsync(taskId, plan, bundles, durations,
                    totalDuration, tempDir, segmentPaths, diagnostics, startedAt, cancellationToken)
                    .ConfigureAwait(false);
                if (failure is not null) return failure;
            }

            // 5) 拼接。章节与拼接合成一条命令，避免多一次整文件重写。
            Report(MergeStage.Merging, 82, "拼接输出…", bundles.Count, bundles.Count);
            string tempOutput = Path.Combine(tempDir, "merged" + plan.ContainerExtension);
            string listPath = Path.Combine(tempDir, "segments.txt");
            await ConcatList.WriteAsync(listPath, segmentPaths, cancellationToken).ConfigureAwait(false);

            // 先按预计时间轴生成 ffmetadata，让章节在 concat 阶段直接嵌入输出；
            // 这样正常情况下无需再走「剥离章节 + 重写章节」两次整文件 remux。
            string? concatMetaPath = null;
            if (options.ChaptersEnabled)
            {
                List<ChapterEntry> planned = ChapterTimeline.Build(durations, titles, totalDuration);
                if (planned.Count > 0)
                {
                    concatMetaPath = Path.Combine(tempDir, "concat_chapters.ffmeta");
                    await File.WriteAllTextAsync(concatMetaPath, ChapterWriter.BuildMetadata(planned),
                        new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                }
            }
            (int mergeCode, string mergeError) = await FfmpegRunner.RunFfmpegAsync(
                plan.BuildConcatCommand(listPath, tempDir, tempOutput, _tools.Ffmpeg, concatMetaPath),
                totalDuration,
                (percent, speed, eta) => Report(MergeStage.Merging, 82 + percent / 100.0 * 12.0,
                    "拼接输出…", bundles.Count, bundles.Count, speed, eta),
                cancellationToken).ConfigureAwait(false);
            // 同样先判定取消，避免把「被取消」误报成「拼接失败」。
            cancellationToken.ThrowIfCancellationRequested();

            // 无损拼接（全 copy）失败时的可选降级。
            //
            // 默认关闭，理由：参数全部一致时 concat 极少失败，真失败通常是文件本身有问题，
            // 自动重编码只会把「立刻报错」变成「跑很久最后仍失败」。
            // 但需要「无论如何都要出片」时，重编码确实能救回一部分参数看起来一致、
            // 实际码流却有差异的素材，所以做成显式开关而不是完全不做。
            if ((mergeCode != 0 || !File.Exists(tempOutput)) && options.AutoFallbackOnFailure && plan.VideoCopy)
            {
                diagnostics.Add("[降级] 无损拼接失败，改用重新编码重试：" + LastLines(mergeError));
                notices.Add("无损拼接失败，已自动改用重新编码重试（" + LastLines(mergeError) + "）");

                segmentPaths.Clear();
                plan = MergePlan.Create(options, bundles, streamCopy: false, _tools.Capabilities);
                diagnostics.Add("[降级] 方案改为 " + plan.Describe());
                MergeResult? reencodeFailure = await ReencodeAllSegmentsAsync(taskId, plan, bundles, durations,
                    totalDuration, tempDir, segmentPaths, diagnostics, startedAt, cancellationToken)
                    .ConfigureAwait(false);
                if (reencodeFailure is not null) return reencodeFailure;

                await ConcatList.WriteAsync(listPath, segmentPaths, cancellationToken).ConfigureAwait(false);
                tempOutput = Path.Combine(tempDir, "merged_retry" + plan.ContainerExtension);
                Report(MergeStage.Merging, 82, "重新拼接输出…", bundles.Count, bundles.Count);
                (mergeCode, mergeError) = await FfmpegRunner.RunFfmpegAsync(
                    plan.BuildConcatCommand(listPath, tempDir, tempOutput, _tools.Ffmpeg, null),
                    totalDuration,
                    (percent, speed, eta) => Report(MergeStage.Merging, 82 + percent / 100.0 * 12.0,
                        "重新拼接输出…", bundles.Count, bundles.Count, speed, eta),
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (mergeCode != 0 || !File.Exists(tempOutput))
                // 失败信息要能指导下一步：ffmpeg 的英文短语对使用者不构成信息，
                // 这里按关键词附一条「可以试什么」，但不据此改变引擎的判定逻辑。
                return Fail(taskId, MergeStage.Merging,
                    FfmpegDiagnostics.Describe("拼接失败：" + LastLines(mergeError), mergeError),
                    diagnostics, startedAt);

            // 6) 探测最终输出 → 用实际时间轴校正章节 → 需要时重写章节
            Report(MergeStage.WritingChapters, 95, "核对输出时间轴…");
            MediaBundle finalInfo = await MediaInspector.InspectAsync(_tools.Ffprobe!, tempOutput,
                cancellationToken, options.ProbeTimeoutSeconds).ConfigureAwait(false);
            double actualDuration = finalInfo.Success && finalInfo.Duration > 0
                ? finalInfo.Duration : totalDuration;

            List<ChapterEntry> chapters = new();
            var issues = new List<string>();   // 章节写入问题也汇总到这里，供第 7 步统一判定
            if (options.ChaptersEnabled)
            {
                List<ChapterEntry> exact = ChapterTimeline.Build(durations, titles, actualDuration);
                List<ChapterEntry> readBack = await ChapterWriter.ReadAsync(_tools.Ffprobe!, tempOutput,
                    cancellationToken).ConfigureAwait(false);
                List<string> drift = ChapterWriter.Compare(exact, readBack, 0.08);
                if (drift.Count == 0 && readBack.Count > 0)
                {
                    // concat 阶段已写入且时间轴一致：跳过整文件重写，省去两次 remux。
                    diagnostics.Add("[章节] concat 已写入 " + readBack.Count + " 个章节，时间轴一致");
                    chapters = readBack;
                }
                else if (exact.Count > 0)
                {
                    Report(MergeStage.WritingChapters, 96, "按实际时间轴修正章节…");
                    (bool ok, string error) = await ChapterWriter.ApplyAsync(_tools.Ffmpeg, tempOutput,
                        exact, tempDir, cancellationToken).ConfigureAwait(false);
                    if (!ok)
                        return Fail(taskId, MergeStage.WritingChapters, "写入章节失败：" + error,
                            diagnostics, startedAt);
                    diagnostics.Add("[章节] 已按最终时间轴重新写入 " + exact.Count + " 个章节");
                    readBack = await ChapterWriter.ReadAsync(_tools.Ffprobe!, tempOutput, cancellationToken)
                        .ConfigureAwait(false);
                    // 写入后回读为空 = 写入失败或 ffprobe 读不到章节；不能回填期望值，
                    // 否则第 7 步的 Compare(exact, chapters) 会变成自己比自己而永远通过。
                    if (readBack.Count == 0)
                        issues.Add("写入章节后回读为空，无法确认章节已正确写入");
                    chapters = readBack;
                }
            }

            // 7) 输出校验
            if (options.VerifyOutput)
            {
                Report(MergeStage.Verifying, 97, "校验输出文件…");
                finalInfo = await MediaInspector.InspectAsync(_tools.Ffprobe!, tempOutput, cancellationToken,
                    options.ProbeTimeoutSeconds).ConfigureAwait(false);
                issues.AddRange(VerifyOutput(plan, finalInfo, actualDuration, bundles));
                if (options.ChaptersEnabled)
                {
                    List<ChapterEntry> exact = ChapterTimeline.Build(durations, titles, actualDuration);
                    issues.AddRange(ChapterWriter.Compare(exact, chapters, 0.08));
                    issues.AddRange(ChapterTimeline.Validate(chapters, actualDuration));
                }
                // 校验阶梯：时长断言（已在上面 VerifyOutput 里做过）→ 接缝定点解码 → 头尾抽样。
                // 先说清楚顺序为什么是这样：「时长对不对」最便宜，「接缝有没有坏」最可能命中，
                // 「头尾有没有截断」覆盖收尾。三者互不替代，全都要过。
                // 接缝位置由各输入段的时长累加得出，与章节时间轴用的是同一份数据。
                var seams = new List<double>();
                double cursor = 0;
                for (int i = 0; i < durations.Count - 1; i++)
                {
                    cursor += Math.Max(0, durations[i]);
                    if (cursor > 0) seams.Add(cursor);
                }
                if (seams.Count > 0)
                {
                    Report(MergeStage.Verifying, 97, "检查各段接缝…", 0, seams.Count);
                    cancellationToken.ThrowIfCancellationRequested();
                    // 注意：Task.Run(..., token) 只在「任务尚未开始」时生效，
                    // 已经开始的任务不会因为 token 被取消而停下。
                    // 因此这里用 WaitAsync 只取消**等待**，让用户立刻收到取消，
                    // 后台的校验进程会自行跑完（每个接缝各自有 120 秒上限）。
                    // 真正要紧的是别让界面卡在「正在校验」上等它。
                    Task<List<string>> seamTask = OutputVerifier.CheckSeamsAsync(
                        _tools.Ffmpeg, tempOutput, seams, 120);
                    List<string> seamIssues = await seamTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    issues.AddRange(seamIssues);
                    if (seamIssues.Count == 0)
                        diagnostics.Add("[接缝] " + seams.Count + " 个拼接点全部可解码");
                }

                cancellationToken.ThrowIfCancellationRequested();
                List<string> integrityIssues = await OutputVerifier
                    .CheckIntegrityAsync(_tools.Ffmpeg, tempOutput, 90)
                    .WaitAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                issues.AddRange(integrityIssues);

                // 内容终点：声明时长后面必须真有数据。
                // 头尾抽样只解「最后 2 秒」并看解码器报不报错，尾部数据整体缺失时它是安静的，
                // 因此「抽样没告警」不能当作「文件完整」。这一项看的是真解出来多少内容。
                issues.AddRange(await OutputVerifier.CheckContentEndAsync(
                    _tools.Ffmpeg, tempOutput, actualDuration).WaitAsync(cancellationToken).ConfigureAwait(false));
            }
            if (issues.Count > 0)
                return Fail(taskId, MergeStage.Verifying, "输出校验未通过：" + string.Join("；", issues),
                    diagnostics, startedAt, notices);

            // 8) 校验通过后才落到最终路径，任何失败都不会污染用户目录
            long expectedOutputBytes = 0;
            //
            // 这一步曾经是「卡在 99% 很久」的元凶：File.Move 在同卷时只是改名（微秒），
            // 跨卷（临时目录在 C:、输出在 D:）时却退化成整文件复制，且全程零进度。
            // 几 GB 的输出要走上分钟，界面就停在 99% 一动不动。
            // 现在跨卷复制按字节推进进度，并如实说明这是复制而不是卡住。
            try
            {
                long totalBytes = new FileInfo(tempOutput).Length;
                // 记下来供删源前比对：输出必须与刚通过校验的临时文件逐字节同长。
                expectedOutputBytes = totalBytes;
                bool sameVolume = FileMover.SameVolume(tempOutput, outputPath);
                if (sameVolume)
                {
                    Report(MergeStage.Completed, 99, "写入输出文件…");
                }
                else
                {
                    Report(MergeStage.Completed, 99,
                        "复制输出到目标位置…（跨磁盘，" + FileMover.Describe(totalBytes) + "）");
                }

                double lastShown = -1;
                await FileMover.MoveAsync(tempOutput, outputPath, ratio =>
                {
                    // 同卷是改名，一瞬间就完成，不该出现「复制…%」这种看着像在搬家的字样。
                    if (sameVolume) return;
                    // 复制阶段占用 99–99.9：真正的写入进度不该把完成感压到 50%，
                    // 但也不能一直停在同一个数字上假装没事。
                    double percent = 99.0 + Math.Clamp(ratio, 0, 1) * 0.9;
                    // 回调来自复制循环，比率每变 1% 才回一次；这里再按显示精度过滤，
                    // 避免同一行文字刷屏把进度条拖慢。
                    if (Math.Abs(ratio - lastShown) < 0.009) return;
                    lastShown = ratio;
                    Report(MergeStage.Completed, percent, string.Format(CultureInfo.InvariantCulture,
                        "复制输出到目标位置…{0:F0}%（共 {1}）", ratio * 100, FileMover.Describe(totalBytes)));
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }   // 交给外层按「已取消」上报
            catch (Exception ex)
            {
                return Fail(taskId, MergeStage.Completed, "写入输出文件失败：" + ex.Message,
                    diagnostics, startedAt);
            }

            // 搬运完成：立刻换掉「写入输出文件…」这句，避免界面继续停在 99% 的那条旧消息上。
            // 从这一行到任务结束之间还有删源/全片校验等步骤，它们各自会再报告，但万一某一步
            // 没有任何输出（例如删源时文件很多，逐个删除），至少这里不会留下误导性的旧文案。
            Report(MergeStage.Completed, 99.2, "输出已就位，正在收尾…");

            // 9) 确认输出完好后再删除源文件（仅在用户明确要求时）。
            // 顺序不可颠倒：先证明输出可完整解码，再动源文件；任何一步存疑都保留源文件。
            var deletedSources = new List<string>();
            if (options.DeleteSourceAfterVerify)
            {
                // 删源前的最后一道闸。
                //
                // 原来的做法是「把整个输出逐帧解码一遍」：它是整条流程里最慢的一步，
                // 耗时与时长线性相关（36 分钟实测 116 秒，2 小时要十几分钟），
                // 而且不可跳过。用户每次合并都要在收尾阶段干等这么久。
                //
                // 现在换成两条**更便宜且更确定**的证据：
                // 1) 字节数一致：输出与刚刚通过全部校验的临时文件必须逐字节同长。
                //    这能确定性地排除「搬运写坏/写短」——而那正是搬到最终路径后才可能出现的新风险。
                // 2) 全片均匀抽样解码：固定 6~16 个窗口，代价封顶在几秒，
                //    覆盖整条时间轴，而不是只看接缝。
                // 接缝 + 头尾 + 内容终点 + 均匀抽样 + 字节数一致，合起来比「只做全片解码」
                // 覆盖得更全面，而代价从「随时长增长」变成「接近常数」。
                Report(MergeStage.Verifying, 99.4, "抽样解码校验（删除源文件前）…");
                long finalSize = 0;
                try { finalSize = new FileInfo(outputPath).Length; }
                catch (Exception ex) { issues.Add("无法读取输出文件大小：" + ex.Message); }
                if (finalSize != expectedOutputBytes)
                    issues.Add(string.Format(CultureInfo.InvariantCulture,
                        "输出字节数与校验通过时不一致（期望 {0}，实际 {1}），搬运过程可能出错",
                        expectedOutputBytes, finalSize));

                issues.AddRange(await OutputVerifier.CheckSampledDecodeAsync(
                    _tools.Ffmpeg, outputPath, actualDuration).ConfigureAwait(false));

                if (issues.Count > 0)
                {
                    // 校验不通过：绝不删源，按失败上报，输出文件保留供用户检查。
                    return Fail(taskId, MergeStage.Verifying,
                        "输出未通过删源前校验，已保留源文件：" + string.Join("；", issues),
                        diagnostics, startedAt, notices);
                }

                diagnostics.Add("[删源] 抽样解码 + 字节数校验通过，开始删除源文件");
                foreach (string input in options.Inputs)
                {
                    string full = Path.GetFullPath(input);
                    // 双保险：绝不删除与输出相同的文件，也不删除已经不存在的。
                    if (string.Equals(full, outputPath, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!File.Exists(full)) continue;
                    try
                    {
                        File.Delete(full);
                        deletedSources.Add(full);
                        diagnostics.Add("[删源] " + Path.GetFileName(full));
                    }
                    catch (Exception ex)
                    {
                        // 删不掉不算致命：输出已可用，源文件留着只是占用空间。
                        notices.Add("源文件删除失败（可手动删除）：" + Path.GetFileName(full) + " — " + ex.Message);
                        diagnostics.Add("[删源失败] " + full + "：" + ex.Message);
                    }
                }
                if (deletedSources.Count > 0)
                    notices.Add("已删除 " + deletedSources.Count + " 个源文件（输出校验通过后）");
            }

            Report(MergeStage.Completed, 100, "合并完成");
            return new MergeResult
            {
                TaskId = taskId,
                Status = MergeStatus.Completed,
                Stage = MergeStage.Completed,
                OutputPath = outputPath,
                Output = finalInfo,
                Chapters = chapters,
                Compatibility = compatibility.Reasons,
                VideoCopied = plan.VideoCopy,
                AudioCopied = plan.AudioCopy,
                Notices = notices,
                Diagnostics = diagnostics,
                DeletedSources = deletedSources,
                ElapsedSeconds = Formatting.UnixNow() - startedAt,
            };
        }
        catch (OperationCanceledException)
        {
            return new MergeResult
            {
                TaskId = taskId,
                Status = MergeStatus.Cancelled,
                Stage = MergeStage.Cancelled,
                Error = "已取消",
                Diagnostics = diagnostics,
                Notices = notices,
                ElapsedSeconds = Formatting.UnixNow() - startedAt,
            };
        }
        catch (Exception ex)
        {
            diagnostics.Add("[异常] " + ex);
            return Fail(taskId, MergeStage.Failed, ex.Message, diagnostics, startedAt, notices);
        }
        finally
        {
            if (tempDir is not null)
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); }
                catch { /* 临时目录清理失败不影响结果 */ }
            }
        }
    }

    /// <summary>
    /// 解析本任务的临时目录。
    ///
    /// 优先级：用户指定 &gt; 与输出同卷 &gt; 系统临时目录。
    /// 「与输出同卷」这一档是为了让最后一步搬运退化成改名而不是整文件复制；
    /// 它建在输出目录下的隐藏子目录里，任务结束（含失败与取消）在 finally 中删除。
    /// 用户显式指定临时目录时一律尊重用户的选择，即使那意味着要跨盘复制。
    /// </summary>
    private static string CreateTempDirectory(VideoMergeOptions options, string outputDir, string taskId)
    {
        string suffix = "subfusion_merge_" + taskId[..12];
        if (!string.IsNullOrEmpty(options.TempDirectory))
        {
            string custom = Path.Combine(Path.GetFullPath(options.TempDirectory!), taskId[..12]);
            Directory.CreateDirectory(custom);
            return custom;
        }

        string systemTemp = Path.Combine(Path.GetTempPath(), suffix);
        string? besideOutput = null;
        try
        {
            string outputRoot = Path.GetPathRoot(Path.GetFullPath(outputDir)) ?? "";
            string tempRoot = Path.GetPathRoot(Path.GetFullPath(systemTemp)) ?? "";
            if (!string.Equals(outputRoot, tempRoot, StringComparison.OrdinalIgnoreCase))
                besideOutput = Path.Combine(outputDir, ".subfusion-temp-" + taskId[..8]);
        }
        catch
        {
            // 路径判断出问题就不冒险往用户目录里建东西，下面直接用系统临时目录。
            besideOutput = null;
        }

        if (besideOutput is not null)
        {
            try
            {
                Directory.CreateDirectory(besideOutput);
                return besideOutput;
            }
            catch
            {
                // 输出目录可能只读、在只读网络盘上、或被策略限制。
                // 建不出同卷目录不算故障：退回系统临时目录，最多最后多一次跨盘复制。
            }
        }

        Directory.CreateDirectory(systemTemp);
        return systemTemp;
    }

    /// <summary>
    /// 构造失败结果。<paramref name="notices"/> 必须传进来：
    /// 失败路径同样装配了「多音轨只取第一条」「源文件删除失败」这类提示，
    /// 丢掉它们会让用户只看到一句错误、看不到与之并列的事实。
    /// </summary>
    private static MergeResult Fail(string taskId, MergeStage stage, string error,
                                    List<string> diagnostics, double startedAt,
                                    List<string>? notices = null)
        => new()
        {
            TaskId = taskId,
            Status = MergeStatus.Failed,
            Stage = stage,
            Error = error,
            Notices = notices ?? new List<string>(),
            Diagnostics = diagnostics,
            ElapsedSeconds = Formatting.UnixNow() - startedAt,
        };

    private static List<string> ChapterTitles(VideoMergeOptions options, List<MediaBundle> bundles)
    {
        var titles = new List<string>(bundles.Count);
        for (int i = 0; i < bundles.Count; i++)
        {
            string custom = i < options.ChapterTitles.Count ? options.ChapterTitles[i] : "";
            titles.Add(string.IsNullOrWhiteSpace(custom)
                ? Path.GetFileNameWithoutExtension(bundles[i].Path)
                : custom.Trim());
        }
        return titles;
    }

    private static List<string> VerifyOutput(MergePlan plan, MediaBundle info, double actualDuration,
                                             List<MediaBundle> inputs)
    {
        var issues = new List<string>();
        if (!info.Success)
        {
            issues.Add("ffprobe 无法读取输出：" + info.Error);
            return issues;
        }
        if (info.Videos.Count != plan.ExpectedVideoStreams)
            issues.Add(string.Format(CultureInfo.InvariantCulture,
                "视频流数量不符（期望 {0}，实际 {1}）", plan.ExpectedVideoStreams, info.Videos.Count));
        if (info.Audios.Count != plan.ExpectedAudioStreams)
            issues.Add(string.Format(CultureInfo.InvariantCulture,
                "音频流数量不符（期望 {0}，实际 {1}）", plan.ExpectedAudioStreams, info.Audios.Count));
        if (info.Container.Size <= 0) issues.Add("输出文件为空");

        // 时长断言走统一的实现：容差按「2% 与若干帧」取较大者，
        // 与删源前的全片校验用的是同一套判定，不会出现两处标准不一致。
        double expected = inputs.Sum(i => Math.Max(0, i.Duration));
        double fps = info.PrimaryVideo?.EffectiveFrameRate ?? 0;
        issues.AddRange(OutputVerifier.CheckDuration(expected, actualDuration, fps));

        VideoStreamInfo? v = info.PrimaryVideo;
        if (v is not null)
        {
            if (plan.TargetWidth > 0 && v.Width > 0 &&
                (v.Width > plan.TargetWidth + 2 || v.Height > plan.TargetHeight + 2))
                issues.Add(string.Format(CultureInfo.InvariantCulture,
                    "输出分辨率超出目标（{0}x{1} > {2}x{3}）", v.Width, v.Height,
                    plan.TargetWidth, plan.TargetHeight));
            if (!plan.VideoCopy && !string.IsNullOrEmpty(plan.VideoCodecOutputName) &&
                !string.Equals(v.Codec, plan.VideoCodecOutputName, StringComparison.OrdinalIgnoreCase))
                issues.Add(string.Format(CultureInfo.InvariantCulture,
                    "输出视频编码为 {0}，预期 {1}", v.Codec, plan.VideoCodecOutputName));
        }
        AudioStreamInfo? a = info.PrimaryAudio;
        if (plan.ExpectedAudioStreams == 1 && a is not null && !plan.AudioCopy &&
            plan.SampleRate > 0 && a.SampleRate != plan.SampleRate)
            issues.Add(string.Format(CultureInfo.InvariantCulture,
                "输出采样率为 {0} Hz，预期 {1} Hz", a.SampleRate, plan.SampleRate));
        return issues;
    }

    /// <summary>上报进度（供独立方法使用；与 MergeAsync 内局部 Report 行为一致）。</summary>
    private void Send(MergeStage stage, double percent, string message, int step = 0, int steps = 0,
                      double speed = 0, double? eta = null)
        => _progress?.Report(new MergeProgress(stage, Math.Clamp(percent, 0, 100), message,
            step, steps, speed, eta));

    /// <summary>
    /// 全量重编码：把每个输入统一编码成一个中间中间片段并追加到 <paramref name="segmentPaths"/>。
    /// 返回 null 表示成功；返回非 null 表示应当直接终止任务（已构造好失败结果）。
    /// 抽成独立方法是为了让「部分重编码核实失败 → 退回全量」这条路径复用同一段逻辑，
    /// 而不是复制一遍编码循环（两份循环迟早会改歪其中一份）。
    /// </summary>
    private async Task<MergeResult?> ReencodeAllSegmentsAsync(string taskId, MergePlan plan,
        List<MediaBundle> bundles, List<double> durations, double totalDuration, string tempDir,
        List<string> segmentPaths, List<string> diagnostics, double startedAt,
        CancellationToken cancellationToken)
    {
        // 先落到「合并中」，再启动第一个编码器：在 ffmpeg 的首条 -progress 到达之前，
        // 阶段栏不应继续显示上一阶段的「判定合并方案…」。
        // 这里直接写 _progress（而不是调用 MergeAsync 内的局部函数 Report），
        // 因为本方法是独立方法，取不到那个局部函数。
        Send(MergeStage.Merging, 5, "统一重新编码输入片段…", 0, bundles.Count);
        double done = 0;
        for (int i = 0; i < bundles.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string segment = Path.Combine(tempDir,
                "segment_" + i.ToString("D3", CultureInfo.InvariantCulture) + ".mkv");
            int index = i;
            double offset = done;
            double length = Math.Max(0.1, durations[i]);
            (int code, string error) = await FfmpegRunner.RunFfmpegAsync(
                plan.BuildSegmentCommand(bundles[i], segment, _tools.Ffmpeg),
                length,
                    (percent, speed, eta) => Send(MergeStage.Merging,
                    (offset + percent / 100.0 * length) / Math.Max(0.1, totalDuration) * 80.0,
                    string.Format(CultureInfo.InvariantCulture, "重新编码 {0}/{1}：{2}",
                        index + 1, bundles.Count, Path.GetFileName(bundles[index].Path)),
                    index, bundles.Count, speed, eta),
                cancellationToken).ConfigureAwait(false);
            // 取消时 ffmpeg 被强杀，返回码不可信：先判定取消，再判定失败。
            cancellationToken.ThrowIfCancellationRequested();
            if (code != 0 || !File.Exists(segment))
                return Fail(taskId, MergeStage.Merging,
                    string.Format(CultureInfo.InvariantCulture, "第 {0} 个片段编码失败：{1}", i + 1,
                        LastLines(error)), diagnostics, startedAt);
            diagnostics.Add("[片段] " + Path.GetFileName(segment) + " ← " + Path.GetFileName(bundles[i].Path));
            segmentPaths.Add(segment);
            done += length;
        }
        return null;
    }

    /// <summary>
    /// 核实「部分重编码」的结果：把保留段与重编码段一起重新探测，
    /// 确认它们现在真的能无损拼接。
    ///
    /// 为什么必须重新探测而不是相信命令参数：编码器未必如实兑现请求——
    /// 例如 -r 在某些格式下只改容器时间戳、SAR 可能被约分成近似值、
    /// 老编码的像素格式可能被静默替换。这些偏差都会让拼接产出坏文件，
    /// 所以判定一律以「重新探测到的实际参数」为准。
    /// </summary>
    /// <param name="segmentPaths">
    /// 即将真正送进 concat 的那串文件（顺序即拼接顺序）。
    /// 必须探测**这些文件本身**，而不是原始输入：重编码段与保留段都已重封装到统一容器与时基，
    /// 拿原始输入去比对会拿 mp4/1:12288 与 mkv/1:1000 比较，必然误判为不一致。
    /// </param>
    /// <returns>问题描述列表；空列表表示核实通过。</returns>
    private async Task<List<string>> ValidatePartialAsync(List<string> segmentPaths,
                                                          CancellationToken cancellationToken)
    {
        var issues = new List<string>();
        if (segmentPaths.Count == 0) return issues;

        List<MediaBundle> probed = await MediaInspector.InspectManyAsync(_tools.Ffprobe!, segmentPaths,
            cancellationToken, 4, 60).ConfigureAwait(false);
        foreach (MediaBundle b in probed.Where(x => !x.Success))
            issues.Add("无法读取待拼接片段 " + Path.GetFileName(b.Path) + "：" + b.Error);
        if (issues.Count > 0) return issues;

        // 与引擎最终执行用的是同一个判定函数，避免"这里通过、那里重编码"的两套标准。
        MergeCompatibility compatibility = CompatibilityAnalyzer.Analyze(probed);
        if (!compatibility.CanStreamCopy && !compatibility.CanRemuxOnly)
            issues.AddRange(compatibility.Reasons.Select(r => r.Detail));
        return issues;
    }

    private static string LastLines(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "（无输出）";
        string[] lines = text.Trim().Split('\n');
        return string.Join(" | ", lines.TakeLast(3).Select(l => l.Trim())).Trim();
    }
}
