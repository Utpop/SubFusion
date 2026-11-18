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
            if (File.Exists(outputPath) && !options.Overwrite)
                return Fail(taskId, MergeStage.Validating,
                    "输出文件已存在且未允许覆盖：" + Path.GetFileName(outputPath), diagnostics, startedAt);

            // 每个任务独立临时目录：绝不与其它任务共用文件名。
            tempDir = string.IsNullOrEmpty(options.TempDirectory)
                ? Path.Combine(Path.GetTempPath(), "subfusion_merge_" + taskId[..12])
                : Path.Combine(Path.GetFullPath(options.TempDirectory!), taskId[..12]);
            Directory.CreateDirectory(tempDir);

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

            var plan = MergePlan.Create(options, bundles, streamCopy || remuxOnly, _tools.Capabilities);
            notices.AddRange(plan.Notes);
            diagnostics.Add("[方案] " + plan.Describe());

            List<string> titles = ChapterTitles(options, bundles);
            List<double> durations = bundles.Select(b => Math.Max(0, b.Duration)).ToList();
            double totalDuration = durations.Sum();

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
            else
            {
                // 先落到「合并中」，再启动第一个编码器：在 ffmpeg 的首条 -progress 到达之前，
                // 阶段栏不应继续显示上一阶段的「判定合并方案…」。
                Report(MergeStage.Merging, 5, "统一重新编码输入片段…", 0, bundles.Count);
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
                        (percent, speed, eta) => Report(MergeStage.Merging,
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
                    chapters = readBack.Count > 0 ? readBack : exact;
                }
            }

            // 7) 输出校验
            var issues = new List<string>();
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
                    // OutputVerifier 不接受 CancellationToken：用带 token 的 Task.Run 包装，
                    // 取消时立即抛出，不让用户等待整条校验链跑完。
                    Task<List<string>> seamTask = Task.Run(
                        () => OutputVerifier.CheckSeamsAsync(_tools.Ffmpeg, tempOutput, seams, 120),
                        cancellationToken);
                    List<string> seamIssues = await seamTask.ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    issues.AddRange(seamIssues);
                    if (seamIssues.Count == 0)
                        diagnostics.Add("[接缝] " + seams.Count + " 个拼接点全部可解码");
                }

                cancellationToken.ThrowIfCancellationRequested();
                Task<List<string>> integrity = Task.Run(
                    () => OutputVerifier.CheckIntegrityAsync(_tools.Ffmpeg, tempOutput, 90), cancellationToken);
                List<string> integrityIssues = await integrity.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                issues.AddRange(integrityIssues);
            }
            if (issues.Count > 0)
                return Fail(taskId, MergeStage.Verifying, "输出校验未通过：" + string.Join("；", issues),
                    diagnostics, startedAt);

            // 8) 校验通过后才落到最终路径，任何失败都不会污染用户目录
            Report(MergeStage.Completed, 99, "写入输出文件…");
            try
            {
                File.Move(tempOutput, outputPath, overwrite: true);
            }
            catch (Exception ex)
            {
                return Fail(taskId, MergeStage.Completed, "写入输出文件失败：" + ex.Message,
                    diagnostics, startedAt);
            }

            // 9) 确认输出完好后再删除源文件（仅在用户明确要求时）。
            // 顺序不可颠倒：先证明输出可完整解码，再动源文件；任何一步存疑都保留源文件。
            var deletedSources = new List<string>();
            if (options.DeleteSourceAfterVerify)
            {
                // 删源前的最后一道闸：全片解码。
                // 前面的接缝检查只覆盖「已知接缝」，全片解码还能兜住校验窗口之外的偶发损坏，
                // 因为是不可逆操作，这里不接受任何近似。
                Report(MergeStage.Verifying, 99, "全片解码校验（删除源文件前）…");
                List<string> fullDecode = await OutputVerifier.CheckFullDecodeAsync(
                    _tools.Ffmpeg, outputPath, 1800).ConfigureAwait(false);
                issues.AddRange(fullDecode);

                if (issues.Count > 0)
                {
                    // 校验不通过：绝不删源，按失败上报，输出文件保留供用户检查。
                    return Fail(taskId, MergeStage.Verifying,
                        "输出未通过全片校验，已保留源文件：" + string.Join("；", issues),
                        diagnostics, startedAt);
                }

                diagnostics.Add("[删源] 全片解码校验通过，开始删除源文件");
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
            return Fail(taskId, MergeStage.Failed, ex.Message, diagnostics, startedAt);
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

    private static MergeResult Fail(string taskId, MergeStage stage, string error,
                                    List<string> diagnostics, double startedAt)
        => new()
        {
            TaskId = taskId,
            Status = MergeStatus.Failed,
            Stage = stage,
            Error = error,
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

    private static string LastLines(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "（无输出）";
        string[] lines = text.Trim().Split('\n');
        return string.Join(" | ", lines.TakeLast(3).Select(l => l.Trim())).Trim();
    }
}
