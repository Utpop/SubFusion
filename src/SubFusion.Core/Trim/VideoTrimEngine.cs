using System.Globalization;
using SubFusion.Core.Media;
using SubFusion.Core.Services;

namespace SubFusion.Core.Trim;

/// <summary>
/// 视频裁剪引擎。
///
/// 处理流程：
/// <list type="number">
/// <item>校验参数 → 探测输入（编码参数 / 音轨 / 源旋转角）→ 扫描关键帧时间轴；</item>
/// <item>算出实际切点（无损模式向前吸附到关键帧）与旋转方案（元数据或烘焙）；</item>
/// <item>执行一条 ffmpeg 命令（不生成任何中间文件，裁剪本身是一次转封装）；</item>
/// <item>回读输出：时长是否等于请求时长、音画相对偏移是否与源文件一致、首尾能否解码。</item>
/// </list>
/// 全程异步、可取消；取消时终止整棵子进程树。
///
/// 设计要点（都来自实测，不是推测）：
/// 音频在精确模式下与视频一起重编码——「视频重编码 + 音频 copy」实测会产生约 1.4 秒错位且退出码为 0；
/// 输出统一加 <c>-avoid_negative_ts make_zero</c>——否则 mp4 会静默丢掉负时间戳、音频整段前移；
/// 章节一律不继承——它的时间轴仍对应整片，裁掉前面后必然错位。
/// </summary>
public sealed class VideoTrimEngine
{
    private readonly FfmpegTools _tools;
    private readonly IProgress<TrimProgress>? _progress;

    /// <summary>本次任务渲染出的临时文件（最终落盘前的一切操作都只碰它）。</summary>
    private string? _renderPath;

    public VideoTrimEngine(FfmpegTools tools, IProgress<TrimProgress>? progress = null)
    {
        _tools = tools;
        _progress = progress;
    }

    /// <summary>只做分析：探测输入并扫描关键帧，不产生任何输出文件。</summary>
    /// <param name="SourceRotation">
    /// 源文件显示角度；null 表示**没能读到**（不等于 0°）。界面据此区分
    /// 「没有显示方向」与「无法读取显示方向」，后者要提示用户可能丢失方向。
    /// </param>
    public async Task<(MediaBundle Bundle, KeyframeIndex Keyframes, int? SourceRotation)> AnalyzeAsync(
        string inputPath, CancellationToken cancellationToken = default)
    {
        MediaBundle bundle = await MediaInspector.InspectAsync(_tools.Ffprobe ?? "", inputPath,
            cancellationToken).ConfigureAwait(false);
        KeyframeIndex keyframes = await KeyframeScanner.ScanAsync(_tools.Ffprobe ?? "", inputPath,
            cancellationToken).ConfigureAwait(false);
        int? rotation = await RotationProbe.TryReadAsync(_tools.Ffprobe ?? "", inputPath, cancellationToken)
            .ConfigureAwait(false);
        return (bundle, keyframes, rotation);
    }

    public async Task<TrimResult> RunAsync(VideoTrimOptions options, CancellationToken cancellationToken = default)
    {
        string taskId = Guid.NewGuid().ToString("N");
        double startedAt = Formatting.UnixNow();
        var diagnostics = new List<string>();
        var notices = new List<string>();
        _renderPath = null;

        void Report(TrimStage stage, double percent, string message, double speed = 0, double? eta = null)
            => _progress?.Report(new TrimProgress(stage, Math.Clamp(percent, 0, 100), message, speed, eta));

        try
        {
            // 1) 参数校验
            Report(TrimStage.Validating, 0, "校验参数…");
            List<string> errors = options.Validate();
            if (errors.Count > 0)
                return Fail(taskId, TrimStage.Validating, string.Join("；", errors), diagnostics, startedAt);

            if (!_tools.Available)
                return Fail(taskId, TrimStage.Validating, "未找到 ffmpeg，无法裁剪视频", diagnostics, startedAt);
            if (!_tools.ProbeAvailable)
                return Fail(taskId, TrimStage.Validating, "未找到 ffprobe，无法探测媒体信息", diagnostics, startedAt);

            string outputPath = Path.GetFullPath(options.OutputPath);
            string? outputDir = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrEmpty(outputDir))
                return Fail(taskId, TrimStage.Validating, "输出路径无效：" + options.OutputPath,
                    diagnostics, startedAt);
            Directory.CreateDirectory(outputDir);
            if (File.Exists(outputPath) && !options.Overwrite)
                return Fail(taskId, TrimStage.Validating,
                    "输出文件已存在且未允许覆盖：" + Path.GetFileName(outputPath), diagnostics, startedAt);

            // 2) 探测输入
            Report(TrimStage.Probing, 2, "读取输入媒体信息…");
            MediaBundle bundle = await MediaInspector.InspectAsync(_tools.Ffprobe!, options.Input,
                cancellationToken, options.ProbeTimeoutSeconds).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!bundle.Success)
                return Fail(taskId, TrimStage.Probing, "输入文件无法读取：" + bundle.Error, diagnostics, startedAt);

            // mp4 + 图形字幕：容器装不下 PGS/VobSub，直接 -map 会让 ffmpeg 以非 0 退出，
            // 用户只能看到「切割失败」。提前拦下并给出可操作的提示。
            if (options.KeepSubtitles && options.NormalizedContainer() == "mp4" &&
                TrimCommandBuilder.HasBitmapSubtitles(bundle))
                return Fail(taskId, TrimStage.Validating,
                    "MP4 容器无法保存图形字幕（PGS/VobSub）。请改选 MKV，或关闭「保留字幕轨」。",
                    diagnostics, startedAt);
            diagnostics.Add("[探测] " + bundle.Summary);

            VideoStreamInfo? video = bundle.PrimaryVideo;
            if (video is null)
                return Fail(taskId, TrimStage.Probing, "输入文件没有视频流", diagnostics, startedAt);

            double sourceDuration = bundle.Duration;
            if (sourceDuration <= 0)
                return Fail(taskId, TrimStage.Probing, "无法确定输入文件时长，不能用时间点裁剪",
                    diagnostics, startedAt);

            // 终点越界时按实际时长收敛，而不是直接失败：用户大多是从播放器里看到一个「差不多」的终点。
            // 收敛规则与 Validate / TrimCommandBuilder 共用 NormalizeRange，避免三处各算一遍。
            double requestedEnd = options.NormalizeRange(sourceDuration).End;
            if (requestedEnd < options.EndSeconds - 0.05)
                notices.Add(string.Format(CultureInfo.InvariantCulture,
                    "终点 {0:0.###}s 超出文件时长，已按实际时长 {1:0.###}s 裁剪",
                    options.EndSeconds, sourceDuration));
            if (options.StartSeconds >= requestedEnd)
                return Fail(taskId, TrimStage.Probing,
                    string.Format(CultureInfo.InvariantCulture,
                        "起点 {0:0.###}s 大于等于文件时长 {1:0.###}s，没有可裁剪的内容",
                        options.StartSeconds, sourceDuration), diagnostics, startedAt);

            // 3) 关键帧扫描（只有无损模式需要：精确模式的切点由解码器保证）
            KeyframeIndex keyframes = new();
            double actualStart = options.StartSeconds;
            bool snapped = false;

            if (options.CopiesStreams)
            {
                Report(TrimStage.ScanningKeyframes, 6, "扫描关键帧…");
                keyframes = await KeyframeScanner.ScanAsync(_tools.Ffprobe!, options.Input,
                    cancellationToken).ConfigureAwait(false);
                diagnostics.Add("[关键帧] " + keyframes.Describe());

                if (keyframes.Ok && !keyframes.AllIntra)
                {
                    actualStart = keyframes.SnapToKeyframe(options.StartSeconds);
                    snapped = Math.Abs(actualStart - options.StartSeconds) > 1e-3;
                    if (snapped)
                        notices.Add(string.Format(CultureInfo.InvariantCulture,
                            "无损切割的切点必须落在关键帧上：请求起点 {0:0.###}s 已前移到关键帧 {1:0.###}s" +
                            "（多带 {2:0.###} 秒画面）。需要精确到帧请改用「精确切割」。",
                            options.StartSeconds, actualStart, options.StartSeconds - actualStart));
                }
                else if (keyframes.Ok && keyframes.AllIntra)
                {
                    notices.Add("素材是全关键帧，无损切割也能精确到帧，不会多带画面");
                }
                else
                {
                    // 扫描失败不阻止任务：让 ffmpeg 自己处理，退化成「不告诉用户吸附了多少」。
                    notices.Add("未能扫描关键帧，实际切点由 ffmpeg 决定，可能比请求的起点更早");
                    diagnostics.Add("[关键帧] 扫描失败：" + keyframes.Error);
                }
            }

            double actualDuration = requestedEnd - actualStart;
            if (actualDuration < 0.05)
                return Fail(taskId, TrimStage.Planning,
                    "吸附到关键帧后剩余时长不足 0.05 秒，请把起点或终点往前/往后调整",
                    diagnostics, startedAt);

            // 4) 旋转方案
            int? probedRotation = await RotationProbe.TryReadAsync(_tools.Ffprobe!, options.Input,
                cancellationToken).ConfigureAwait(false);
            if (probedRotation is null)
                notices.Add("未能读取源文件的显示方向，输出按无旋转处理；若素材是竖屏拍摄，请确认画面方向");
            int sourceRotation = probedRotation ?? 0;
            RotationPlan rotation = RotationPlan.Create(sourceRotation, options.Rotation, options.CopiesStreams);
            foreach (string note in rotation.Notes) notices.Add(note);
            diagnostics.Add("[旋转] " + rotation.Describe() +
                            "（源 " + RotationPlan.Normalize(sourceRotation) + "°，输出 " +
                            rotation.TargetRotation + "°）");

            // 5) 执行
            //
            // 绝不能直接写最终路径：ffmpeg 会先截断同名文件，一旦切割失败或用户取消，
            // 「校验失败就删半成品」的清理逻辑会把用户原本放在那里、与本次任务无关的文件
            // 一起删掉。先渲染到本任务专属临时文件，校验通过后再原子改名落盘，
            // 失败路径只会清理自己创建的那个文件。
            string renderPath = TempRenderPath(outputPath, options, taskId);
            _renderPath = renderPath;
            Report(TrimStage.Cutting, 10, options.CopiesStreams ? "无损切割…" : "精确切割（重编码所选区间）…");
            List<string> cmd = TrimCommandBuilder.Build(options, bundle, actualStart, rotation, _tools.Ffmpeg,
                renderPath);
            diagnostics.Add("[命令] " + Formatting.CommandText(cmd));

            (int code, string error) = await FfmpegRunner.RunFfmpegAsync(
                cmd, actualDuration,
                (percent, speed, eta) => Report(TrimStage.Cutting, 10 + percent / 100.0 * 80.0,
                    options.CopiesStreams ? "无损切割…" : "精确切割…", speed, eta),
                cancellationToken).ConfigureAwait(false);

            // 先判定取消：否则「用户点了取消」会被报成「切割失败」。
            cancellationToken.ThrowIfCancellationRequested();
            if (code != 0 || !File.Exists(renderPath))
            {
                TryDelete(renderPath);
                return Fail(taskId, TrimStage.Cutting,
                    FfmpegDiagnostics.Describe("切割失败：" + LastLines(error), error), diagnostics, startedAt);
            }

            // 6) 校验（仍校验临时文件，通过后才动最终路径）
            Report(TrimStage.Verifying, 92, "校验输出文件…");
            MediaBundle output = await MediaInspector.InspectAsync(_tools.Ffprobe!, renderPath,
                cancellationToken, options.ProbeTimeoutSeconds).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var issues = new List<string>();
            double outputDuration = output.Success ? output.Duration : 0;
            if (!output.Success) issues.Add("输出文件无法读取：" + output.Error);

            bool syncVerified = false;
            double sourceOffset = 0, outputOffset = 0, drift = 0;

            if (options.VerifyOutput)
            {
                issues.AddRange(VerifyDuration(actualDuration, outputDuration, video.EffectiveFrameRate));

                // 探测超时按用户设置透传：默认 30 秒对网络盘/大文件容易误判成「未能核实」。
                SyncSample sourceSync = await AvSyncProbe.ProbeAsync(_tools.Ffprobe!, options.Input,
                    cancellationToken, options.ProbeTimeoutSeconds).ConfigureAwait(false);
                SyncSample outputSync = await AvSyncProbe.ProbeAsync(_tools.Ffprobe!, renderPath,
                    cancellationToken, options.ProbeTimeoutSeconds).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                sourceOffset = sourceSync.Offset;
                outputOffset = outputSync.Offset;
                // 容差默认按素材帧长算：固定值要么误报无损切割，要么漏掉真错位。
                double syncTolerance = options.SyncToleranceSeconds > 0
                    ? options.SyncToleranceSeconds
                    : AvSyncProbe.ToleranceFor(bundle, output);
                diagnostics.Add(string.Format(CultureInfo.InvariantCulture,
                    "[同步] 容差 {0:0.###}s（按帧长自动计算）", syncTolerance));
                (bool syncOk, drift, string syncMessage) =
                    AvSyncProbe.Compare(sourceSync, outputSync, syncTolerance);
                // 只有「真的比较过两侧」才算核实。源侧探测失败时 Compare 会放行，
                // 但那不构成证据，界面上不能显示成「已核实」。
                syncVerified = syncOk && sourceSync.Ok && outputSync.Ok;
                diagnostics.Add("[同步] " + syncMessage);
                if (!syncOk) issues.Add(syncMessage);
                else if (!syncVerified) notices.Add("未能核实音画同步：" + syncMessage);

                // 首尾抽样解码：能拦住「文件被截断」这类时长断言看不出的问题。
                // CheckIntegrityAsync 不接受 token（内部 ffmpeg 最长 60s），这里用 WaitAsync
                // 把取消立刻传播出去，避免点停止后界面卡住一分钟（与 merge 引擎一致）。
                issues.AddRange(await OutputVerifier.CheckIntegrityAsync(_tools.Ffmpeg, renderPath)
                    .WaitAsync(cancellationToken).ConfigureAwait(false));
                cancellationToken.ThrowIfCancellationRequested();

                issues.AddRange(VerifyStreams(bundle, output));
            }
            else
            {
                notices.Add("已跳过输出校验：时长与音画同步未核实");
            }

            if (issues.Count > 0)
            {
                // 只删本任务渲染的临时文件。最终路径上可能有用户自己放的文件，
                // 本次任务从未写过它，绝不能因为「校验不通过」去动它。
                TryDelete(renderPath);
                return new TrimResult
                {
                    TaskId = taskId,
                    Status = TrimStatus.Failed,
                    Stage = TrimStage.Verifying,
                    OutputPath = outputPath,
                    RequestedStart = options.StartSeconds,
                    RequestedEnd = options.EndSeconds,
                    ActualStart = actualStart,
                    ActualEnd = requestedEnd,
                    Snapped = snapped,
                    OutputDuration = outputDuration,
                    VideoCopied = options.CopiesStreams,
                    AudioCopied = bundle.HasAudio && options.CopiesStreams,
                    SourceSyncOffset = sourceOffset,
                    OutputSyncOffset = outputOffset,
                    SyncDrift = drift,
                    SyncVerified = syncVerified,
                    Notices = notices,
                    Error = "输出校验未通过：" + string.Join("；", issues),
                    Diagnostics = diagnostics,
                    ElapsedSeconds = Formatting.UnixNow() - startedAt,
                };
            }

            Report(TrimStage.Completed, 100, "完成");
            if (rotation.Strategy == RotationStrategy.Metadata)
                notices.Add("已写入显示方向；像素数据未被重新编码");
            if (options.KeepSubtitles && bundle.SubtitleStreamCount > 0)
                notices.Add("字幕轨已复制，但字幕时间轴仍对应原片起点，裁掉前面后可能需要重新对齐");
            if (bundle.Audios.Count > 1)
                notices.Add(string.Format(CultureInfo.InvariantCulture,
                    "源文件有 {0} 条音轨，只保留了第一条", bundle.Audios.Count));

            // 校验全过之后才把临时文件换成最终路径：跨卷时是一次带进度的复制，
            // 同卷时只是改名。走到这里之前的所有失败路径都只碰过 renderPath，
            // 最终路径上用户原有的文件始终未被写过。
            Report(TrimStage.Completed, 99, "写入输出文件…");
            try
            {
                await FileMover.MoveAsync(renderPath, outputPath, null, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryDelete(renderPath);
                throw;
            }
            catch (Exception ex)
            {
                TryDelete(renderPath);
                return Fail(taskId, TrimStage.Completed, "写入输出文件失败：" + ex.Message, diagnostics, startedAt);
            }

            diagnostics.Add(string.Format(CultureInfo.InvariantCulture,
                "[完成] 输出 {0:0.###}s（请求 {1:0.###}s）", outputDuration, actualDuration));

            return new TrimResult
            {
                TaskId = taskId,
                Status = TrimStatus.Completed,
                Stage = TrimStage.Completed,
                OutputPath = outputPath,
                Output = output,
                RequestedStart = options.StartSeconds,
                RequestedEnd = options.EndSeconds,
                ActualStart = actualStart,
                ActualEnd = requestedEnd,
                Snapped = snapped,
                OutputDuration = outputDuration,
                VideoCopied = options.CopiesStreams,
                AudioCopied = bundle.HasAudio && options.CopiesStreams,
                SourceSyncOffset = sourceOffset,
                OutputSyncOffset = outputOffset,
                SyncDrift = drift,
                SyncVerified = syncVerified,
                Notices = notices,
                Diagnostics = diagnostics,
                ElapsedSeconds = Formatting.UnixNow() - startedAt,
            };
        }
        catch (OperationCanceledException)
        {
            // 取消只清理本任务渲染出的临时文件：最终路径走到这里时可能从未被写过。
            TryDelete(_renderPath ?? "");
            return new TrimResult
            {
                TaskId = taskId,
                Status = TrimStatus.Cancelled,
                Stage = TrimStage.Cancelled,
                OutputPath = options.OutputPath,
                RequestedStart = options.StartSeconds,
                RequestedEnd = options.EndSeconds,
                Notices = notices,
                Diagnostics = diagnostics,
                ElapsedSeconds = Formatting.UnixNow() - startedAt,
            };
        }
        catch (Exception ex)
        {
            return Fail(taskId, TrimStage.Failed, "裁剪时发生异常：" + ex.Message, diagnostics, startedAt);
        }
    }

    /// <summary>
    /// 渲染用的临时文件路径。
    ///
    /// 落在哪由 TempDirectory 决定：用户显式指定时先用它，否则与输出同目录
    /// （同卷时最后一步 File.Move 只是改名，瞬时完成且零额外空间）。
    /// 文件名带任务号前缀，避免同目录并发任务互相覆盖。
    /// </summary>
    private static string TempRenderPath(string outputPath, VideoTrimOptions options, string taskId)
    {
        string suffix = "subfusion_trim_" + taskId[..12] + Path.GetExtension(outputPath);
        if (!string.IsNullOrEmpty(options.TempDirectory))
        {
            string custom = Path.GetFullPath(options.TempDirectory!);
            Directory.CreateDirectory(custom);
            return Path.Combine(custom, suffix);
        }

        string dir = Path.GetDirectoryName(outputPath) ?? Path.GetTempPath();
        string beside = Path.Combine(dir, "." + suffix);
        try
        {
            Directory.CreateDirectory(dir);
            return beside;
        }
        catch
        {
            // 输出目录不可写：退回系统临时目录，最后由 FileMover 跨卷搬运。
            return Path.Combine(Path.GetTempPath(), suffix);
        }
    }

    /// <summary>
    /// 时长断言。容差同时容纳 AAC 的 priming/padding 与最后一帧取整，
    /// 但不足以容纳「切了 10 秒却给了 12 秒」这种量级的错误。
    /// </summary>
    private static List<string> VerifyDuration(double expected, double actual, double frameRate)
    {
        var issues = new List<string>();
        if (expected <= 0 || actual <= 0) return issues;

        double tolerance = Math.Max(0.35, expected * 0.02);
        if (frameRate > 0) tolerance = Math.Max(tolerance, 4.0 / frameRate);

        double delta = Math.Abs(actual - expected);
        if (delta > tolerance)
            issues.Add(string.Format(CultureInfo.InvariantCulture,
                "输出时长与请求不符（期望约 {0:F2}s，实际 {1:F2}s，相差 {2:F2}s，容差 {3:F2}s）",
                expected, actual, delta, tolerance));
        return issues;
    }

    /// <summary>输出必须仍然「有画面、应该有声音就有声音」。</summary>
    private static List<string> VerifyStreams(MediaBundle source, MediaBundle output)
    {
        var issues = new List<string>();
        if (!output.Success) return issues;
        if (output.PrimaryVideo is null) issues.Add("输出文件没有视频流");
        if (source.HasAudio && !output.HasAudio) issues.Add("源文件有音轨，输出却没有音轨");
        return issues;
    }

    private static TrimResult Fail(string taskId, TrimStage stage, string error,
                                   List<string> diagnostics, double startedAt)
    {
        diagnostics.Add("[错误] " + error);
        return new TrimResult
        {
            TaskId = taskId,
            Status = TrimStatus.Failed,
            Stage = stage,
            Error = error,
            Diagnostics = diagnostics,
            ElapsedSeconds = Formatting.UnixNow() - startedAt,
        };
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 删不掉也不影响结果 */ }
    }

    private static string LastLines(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "ffmpeg 未给出错误信息";
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join("｜", lines.TakeLast(3).Select(l => l.Trim()));
    }
}
