using System.Globalization;
using SubFusion.Core.Media;
using SubFusion.Core.Services;

namespace SubFusion.Core.Compress;

/// <summary>
/// 视频压制引擎（独立模块，不复用裁剪/合并的任何执行路径）。
///
/// 流程：校验参数 → 探测输入 → 选编码器（真实探测结果）→ 构造命令 → 编码 → 校验 → 原子落盘。
///
/// 三条来自实测的设计约束：
/// <list type="number">
/// <item>绝不直接写最终路径：ffmpeg 会先截断同名文件，失败或取消时清理半成品会把用户
/// 原本放在那里的文件一起删掉。先渲染到本任务专属临时文件，校验通过才改名。</item>
/// <item>取消判定要在「把子进程退出码当成失败」之前：被 Kill 的进程退出码没有意义，
/// 顺序反了会把用户点取消报成「编码失败」。</item>
/// <item>硬件编码可能首次初始化失败（显卡被占用 / 驱动抖动），自动模式下换 CPU 编码重试一次，
/// 并把这件事写进诊断；hardware 模式下则如实失败，不偷偷降级。</item>
/// </list>
/// </summary>
public sealed class VideoCompressEngine
{
    private readonly FfmpegTools _tools;
    private readonly IProgress<CompressProgress>? _progress;
    private string? _renderPath;

    public VideoCompressEngine(FfmpegTools tools, IProgress<CompressProgress>? progress = null)
    {
        _tools = tools;
        _progress = progress;
    }

    /// <summary>只探测不执行：返回输入媒体信息与编码器可用性。</summary>
    public async Task<(MediaBundle Bundle, IReadOnlyList<EncoderAvailability> Encoders)> AnalyzeAsync(
        string inputPath, CancellationToken cancellationToken = default)
    {
        MediaBundle bundle = await MediaInspector.InspectAsync(_tools.Ffprobe ?? "", inputPath,
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<EncoderAvailability> encoders = await HardwareEncoder
            .ProbeAsync(_tools.Ffmpeg, cancellationToken).ConfigureAwait(false);
        return (bundle, encoders);
    }

    /// <summary>执行压制。</summary>
    public async Task<CompressResult> RunAsync(VideoCompressOptions options,
                                               CancellationToken cancellationToken = default)
    {
        string taskId = Guid.NewGuid().ToString("N");
        double startedAt = Formatting.UnixNow();
        var diagnostics = new List<string>();
        var notices = new List<string>();
        _renderPath = null;

        void Report(CompressStage stage, double percent, string message,
                    double speed = 0, double? eta = null, string? encoder = null)
            => _progress?.Report(new CompressProgress(stage, Math.Clamp(percent, 0, 100), message,
                                                      speed, eta, encoder));

        try
        {
            // 1) 参数校验
            Report(CompressStage.Validating, 0, "校验参数…");
            List<string> errors = options.Validate();
            if (errors.Count > 0)
                return Fail(taskId, CompressStage.Validating, string.Join("；", errors), diagnostics, startedAt);

            if (!_tools.Available)
                return Fail(taskId, CompressStage.Validating, "未找到 ffmpeg，无法压制视频", diagnostics, startedAt);
            if (!_tools.ProbeAvailable)
                return Fail(taskId, CompressStage.Validating, "未找到 ffprobe，无法探测媒体信息", diagnostics, startedAt);

            string outputPath = Path.GetFullPath(options.OutputPath);
            string? outputDir = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrEmpty(outputDir))
                return Fail(taskId, CompressStage.Validating, "输出路径无效：" + options.OutputPath,
                    diagnostics, startedAt);
            Directory.CreateDirectory(outputDir);
            if (File.Exists(outputPath) && !options.Overwrite)
                return Fail(taskId, CompressStage.Validating,
                    "输出文件已存在且未允许覆盖：" + Path.GetFileName(outputPath), diagnostics, startedAt);

            // 2) 探测输入
            Report(CompressStage.Probing, 2, "读取输入媒体信息…");
            MediaBundle bundle = await MediaInspector.InspectAsync(_tools.Ffprobe!, options.Input,
                cancellationToken, options.ProbeTimeoutSeconds).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!bundle.Success)
                return Fail(taskId, CompressStage.Probing, "输入文件无法读取：" + bundle.Error,
                    diagnostics, startedAt);

            VideoStreamInfo? video = bundle.PrimaryVideo;
            if (video is null)
                return Fail(taskId, CompressStage.Probing, "输入文件没有视频流", diagnostics, startedAt);
            diagnostics.Add("[探测] " + bundle.Summary);

            if (bundle.Duration <= 0)
                return Fail(taskId, CompressStage.Probing, "无法确定输入文件时长", diagnostics, startedAt);

            if (options.ReencodeAudio)
            {
                // 重新编码音频：任何容器都能装，无需额外检查。
            }
            else
            {
                List<string> audioErrors = VideoCompressOptions.ValidateAudioCopy(
                    options.NormalizedContainer(), bundle.PrimaryAudio?.Codec ?? "", bundle.HasAudio);
                if (audioErrors.Count > 0)
                    return Fail(taskId, CompressStage.Validating, string.Join("；", audioErrors),
                        diagnostics, startedAt);
            }

            if (options.KeepSubtitles && options.NormalizedContainer() == "mp4" && bundle.BitmapSubtitleCount > 0)
                return Fail(taskId, CompressStage.Validating,
                    "MP4 容器无法保存图形字幕（PGS/VobSub）。请改选 MKV，或关闭「保留字幕轨」。",
                    diagnostics, startedAt);

            if (video.IsHdr)
                notices.Add("源是 HDR（" + video.HdrFormat + "），压制只保留原色彩标记，不做 HDR→SDR 转换；" +
                            "若目标设备不支持 HDR，画面会发灰。");

            // 3) 选编码器
            Report(CompressStage.Probing, 5, "检测可用编码器…");
            IReadOnlyList<EncoderAvailability> available = await HardwareEncoder
                .ProbeAsync(_tools.Ffmpeg, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            foreach (EncoderAvailability entry in available.Where(e => e.Usable))
                diagnostics.Add("[编码器] " + entry.Name + " 可用（" + entry.Label + "）");

            EncoderAvailability? encoder = ResolveEncoder(options, available, notices, diagnostics);
            if (encoder is null)
            {
                string wanted = options.Codec == VideoCodecKind.Hevc ? "H.265（HEVC）" : "H.264";
                return Fail(taskId, CompressStage.Validating,
                    "没有可用的 " + wanted + " 编码器。请在编码设置里改用「CPU 软件编码」，" +
                    "或在设置页指定一个带对应编码器的 ffmpeg。", diagnostics, startedAt);
            }

            // 4) 执行（硬件失败时按策略重试一次）
            Report(CompressStage.Encoding, 8, "准备编码…", encoder: encoder.Name);
            (CompressResult? result, EncoderAvailability used) =
                await EncodeWithFallbackAsync(options, bundle, encoder, available,
                    outputPath, taskId, diagnostics, notices, startedAt, Report, cancellationToken)
                    .ConfigureAwait(false);
            return result!;
        }
        catch (OperationCanceledException)
        {
            // 取消只清理本任务渲染出的临时文件；最终路径走到这里时可能从未被写过。
            TryDelete(_renderPath ?? "");
            return new CompressResult
            {
                TaskId = taskId,
                Status = CompressStatus.Cancelled,
                Stage = CompressStage.Cancelled,
                OutputPath = options.OutputPath,
                Notices = notices,
                Diagnostics = diagnostics,
                ElapsedSeconds = Formatting.UnixNow() - startedAt,
            };
        }
        catch (Exception ex)
        {
            TryDelete(_renderPath ?? "");
            return Fail(taskId, CompressStage.Failed, "压制时发生异常：" + ex.Message, diagnostics, startedAt);
        }
    }

    /// <summary>按偏好解析出实际使用的编码器，并把「为什么没用显卡」如实写进提示。</summary>
    private static EncoderAvailability? ResolveEncoder(VideoCompressOptions options,
                                                       IReadOnlyList<EncoderAvailability> available,
                                                       List<string> notices, List<string> diagnostics)
    {
        if (!string.IsNullOrEmpty(options.EncoderOverride))
        {
            EncoderAvailability? forced = available
                .FirstOrDefault(e => e.Usable && string.Equals(e.Name, options.EncoderOverride, StringComparison.Ordinal));
            if (forced is not null) return forced;
            diagnostics.Add("[编码器] 指定的 " + options.EncoderOverride + " 在本机不可用，回落到自动选择");
        }

        List<EncoderKind>? backends = options.AllowedBackends.Count > 0 ? options.AllowedBackends : null;
        EncoderAvailability? selected = HardwareEncoder.Select(available, options.Codec,
                                                               options.EncoderPreference, backends);

        if (selected is null && backends is not null &&
            !string.Equals(options.EncoderPreference, "software", StringComparison.OrdinalIgnoreCase))
        {
            // 限定后端但那个后端不可用：不该直接失败，应给出可解释的提示后回落到软件编码。
            string names = string.Join("/", backends.Select(BackendName));
            notices.Add("已限定的编码后端（" + names + "）在本机不可用，已改用 CPU 软件编码");
            selected = HardwareEncoder.Select(available, options.Codec, "software");
        }

        if (selected is null) return null;

        if (selected.Kind == EncoderKind.Software &&
            !string.Equals(options.EncoderPreference, "software", StringComparison.OrdinalIgnoreCase))
        {
            string unavailable = string.Join("、", available
                .Where(e => e.Codec == options.Codec && e.Kind != EncoderKind.Software)
                .Select(e => e.Name + "：" + e.Error));
            if (unavailable.Length > 0)
            {
                notices.Add("本机没有可用的硬件编码器（" + unavailable + "），本次使用 CPU 软件编码");
                diagnostics.Add("[编码器] 硬件不可用：" + unavailable);
            }
        }
        return selected;
    }

    private static string BackendName(EncoderKind kind) => kind switch
    {
        EncoderKind.Amf => "AMD",
        EncoderKind.Nvenc => "NVIDIA",
        EncoderKind.Qsv => "Intel",
        _ => "CPU",
    };

    /// <summary>执行编码；自动模式下硬件首次失败会换 CPU 编码重试一次。</summary>
    private async Task<(CompressResult? Result, EncoderAvailability Used)> EncodeWithFallbackAsync(
        VideoCompressOptions options, MediaBundle bundle, EncoderAvailability encoder,
        IReadOnlyList<EncoderAvailability> available, string outputPath, string taskId,
        List<string> diagnostics, List<string> notices, double startedAt,
        Action<CompressStage, double, string, double, double?, string?> report,
        CancellationToken cancellationToken)
    {
        EncoderAvailability current = encoder;
        while (true)
        {
            (bool ok, string error, bool hardwareDecoded, string renderPath) = await EncodeOnceAsync(
                options, bundle, current, outputPath, taskId, diagnostics, report, cancellationToken)
                .ConfigureAwait(false);

            if (ok)
            {
                CompressResult result = await FinalizeAsync(options, bundle, current, hardwareDecoded,
                    renderPath, outputPath, taskId, diagnostics, notices, startedAt, report, cancellationToken)
                    .ConfigureAwait(false);
                return (result, current);
            }

            // 硬件编码失败且是自动模式：换软件编码重试一次，把原因如实记下来。
            bool canFallback = current.Kind != EncoderKind.Software &&
                               string.Equals(options.EncoderPreference, "auto", StringComparison.OrdinalIgnoreCase);
            if (!canFallback)
                return (Fail(taskId, CompressStage.Encoding,
                    FfmpegDiagnostics.Describe("编码失败：" + error, error), diagnostics, startedAt), current);

            EncoderAvailability? fallback = HardwareEncoder.SoftwareFallback(available, options.Codec);
            if (fallback is null || fallback.Name == current.Name)
                return (Fail(taskId, CompressStage.Encoding,
                    FfmpegDiagnostics.Describe("编码失败：" + error, error), diagnostics, startedAt), current);

            notices.Add(current.Label + " 首次编码失败，已自动改用 " + fallback.Label + " 重试（原因：" + error + "）");
            diagnostics.Add("[回退] " + current.Name + " → " + fallback.Name + "，原因：" + error);
            current = fallback;
            TryDelete(_renderPath ?? "");
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <summary>跑一次 ffmpeg 编码；返回是否成功、错误摘要、是否硬解、渲染路径。</summary>
    private async Task<(bool Ok, string Error, bool HardwareDecoded, string RenderPath)> EncodeOnceAsync(
        VideoCompressOptions options, MediaBundle bundle, EncoderAvailability encoder, string outputPath,
        string taskId, List<string> diagnostics,
        Action<CompressStage, double, string, double, double?, string?> report,
        CancellationToken cancellationToken)
    {
        string renderPath = TempRenderPath(outputPath, options, taskId);
        _renderPath = renderPath;

        bool wantHardwareDecode = options.HardwareDecode && encoder.Kind != EncoderKind.Software;
        List<string> cmd = CompressCommandBuilder.Build(options, bundle, encoder, _tools.Ffmpeg, renderPath);
        bool hardwareDecoded = wantHardwareDecode && cmd.Contains("-hwaccel");

        string stageText = encoder.Kind == EncoderKind.Software
            ? "软件编码中（" + encoder.Name + "）…"
            : "显卡编码中（" + encoder.Name + "）…";
        report(CompressStage.Encoding, 10, stageText, 0, null, encoder.Name);
        diagnostics.Add("[命令] " + Formatting.CommandText(cmd));

        double duration = bundle.Duration;
        (int code, string error) = await FfmpegRunner.RunFfmpegAsync(
            cmd, duration,
            (percent, speed, eta) => report(CompressStage.Encoding, 10 + percent / 100.0 * 80.0,
                stageText, speed, eta, encoder.Name),
            cancellationToken).ConfigureAwait(false);

        // 先判取消：被 Kill 的进程退出码没有意义，顺序反了会把「用户点取消」报成「编码失败」。
        cancellationToken.ThrowIfCancellationRequested();

        if (code == 0 && File.Exists(renderPath))
            return (true, "", hardwareDecoded, renderPath);

        // 硬解可能在这个文件上不可用（不支持的编码/10bit/坏帧），退回软解再试一次同样的编码器。
        if (hardwareDecoded && code != 0)
        {
            diagnostics.Add("[硬解] d3d11va 解码失败，退回软件解码重试：" + LastLines(error));
            VideoCompressOptions softDecode = options.Clone();
            softDecode.HardwareDecode = false;
            TryDelete(renderPath);
            return await EncodeOnceAsync(softDecode, bundle, encoder, outputPath, taskId, diagnostics,
                report, cancellationToken).ConfigureAwait(false);
        }

        return (false, LastLines(error), hardwareDecoded, renderPath);
    }

    /// <summary>校验临时文件并把结果搬到最终路径。</summary>
    private async Task<CompressResult> FinalizeAsync(
        VideoCompressOptions options, MediaBundle bundle, EncoderAvailability encoder, bool hardwareDecoded,
        string renderPath, string outputPath, string taskId, List<string> diagnostics, List<string> notices,
        double startedAt,
        Action<CompressStage, double, string, double, double?, string?> report,
        CancellationToken cancellationToken)
    {
        report(CompressStage.Verifying, 92, "校验输出文件…", 0, null, encoder.Name);
        MediaBundle output = await MediaInspector.InspectAsync(_tools.Ffprobe!, renderPath,
            cancellationToken, options.ProbeTimeoutSeconds).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var issues = new List<string>();
        double outputDuration = output.Success ? output.Duration : 0;
        if (!output.Success) issues.Add("输出文件无法读取：" + output.Error);

        if (options.VerifyOutput)
        {
            issues.AddRange(OutputVerifier.CheckDuration(bundle.Duration, outputDuration,
                bundle.PrimaryVideo?.EffectiveFrameRate ?? 0));

            MediaBundle? outputBundle = output.Success ? output : null;
            if (outputBundle?.PrimaryVideo is null) issues.Add("输出文件没有视频流");
            if (bundle.HasAudio && outputBundle is not null && !outputBundle.HasAudio &&
                !options.ReencodeAudio)
                issues.Add("源文件有音轨，输出却没有音轨");

            // 首尾抽样解码：能拦住「文件被截断」这类时长断言看不出的问题。
            issues.AddRange(await OutputVerifier.CheckIntegrityAsync(_tools.Ffmpeg, renderPath)
                .WaitAsync(cancellationToken).ConfigureAwait(false));
            cancellationToken.ThrowIfCancellationRequested();
        }
        else
        {
            notices.Add("已跳过输出校验：时长与解码完整性未核实");
        }

        long sourceBytes = SafeLength(options.Input);
        long outputBytes = SafeLength(renderPath);

        if (issues.Count > 0)
        {
            TryDelete(renderPath);
            return new CompressResult
            {
                TaskId = taskId,
                Status = CompressStatus.Failed,
                Stage = CompressStage.Verifying,
                OutputPath = outputPath,
                Output = output,
                SourceBytes = sourceBytes,
                OutputBytes = outputBytes,
                SourceDuration = bundle.Duration,
                OutputDuration = outputDuration,
                Encoder = encoder.Name,
                EncoderLabel = encoder.Label,
                HardwareEncoded = encoder.Kind != EncoderKind.Software,
                HardwareDecoded = hardwareDecoded,
                AudioCopied = !options.ReencodeAudio && bundle.HasAudio,
                Notices = notices,
                Error = "输出校验未通过：" + string.Join("；", issues),
                Diagnostics = diagnostics,
                ElapsedSeconds = Formatting.UnixNow() - startedAt,
            };
        }

        report(CompressStage.Moving, 99, "写入输出文件…", 0, null, encoder.Name);
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
            throw new IOException("写入输出文件失败：" + ex.Message, ex);
        }

        if (outputBytes > 0 && sourceBytes > 0)
        {
            double ratio = 1.0 - (double)outputBytes / sourceBytes;
            diagnostics.Add(string.Format(CultureInfo.InvariantCulture,
                "[体积] {0:0.##} MB → {1:0.##} MB（{2}{3:0.#}%）",
                sourceBytes / 1048576.0, outputBytes / 1048576.0, ratio >= 0 ? "减少 " : "增加 ",
                Math.Abs(ratio) * 100));
        }
        diagnostics.Add(string.Format(CultureInfo.InvariantCulture,
            "[完成] 编码器 {0}（{1}），输出 {2:0.###}s（源 {3:0.###}s）",
            encoder.Name, encoder.Label, outputDuration, bundle.Duration));

        report(CompressStage.Completed, 100, "完成", 0, null, encoder.Name);

        if (output.Success && output.PrimaryVideo is { } outVideo && bundle.PrimaryVideo is { } srcVideo &&
            (outVideo.Width != srcVideo.Width || outVideo.Height != srcVideo.Height))
            notices.Add(string.Format(CultureInfo.InvariantCulture, "分辨率 {0}x{1} → {2}x{3}",
                srcVideo.Width, srcVideo.Height, outVideo.Width, outVideo.Height));
        if (bundle.Audios.Count > 1)
            notices.Add(string.Format(CultureInfo.InvariantCulture,
                "源文件有 {0} 条音轨，只保留了第一条", bundle.Audios.Count));

        return new CompressResult
        {
            TaskId = taskId,
            Status = CompressStatus.Completed,
            Stage = CompressStage.Completed,
            OutputPath = outputPath,
            Output = output,
            SourceBytes = sourceBytes,
            OutputBytes = outputBytes,
            SourceDuration = bundle.Duration,
            OutputDuration = outputDuration,
            Encoder = encoder.Name,
            EncoderLabel = encoder.Label,
            HardwareEncoded = encoder.Kind != EncoderKind.Software,
            HardwareDecoded = hardwareDecoded,
            AudioCopied = !options.ReencodeAudio && bundle.HasAudio,
            Notices = notices,
            Diagnostics = diagnostics,
            ElapsedSeconds = Formatting.UnixNow() - startedAt,
        };
    }

    /// <summary>
    /// 渲染用的临时文件路径。默认与输出同目录（同卷时最后一步只是改名，瞬时且零额外空间）；
    /// 文件名带任务号前缀，避免同目录并发任务互相覆盖。
    /// </summary>
    private static string TempRenderPath(string outputPath, VideoCompressOptions options, string taskId)
    {
        string suffix = "subfusion_comp_" + taskId[..12] + Path.GetExtension(outputPath);
        if (!string.IsNullOrEmpty(options.TempDirectory))
        {
            string custom = Path.GetFullPath(options.TempDirectory!);
            Directory.CreateDirectory(custom);
            return Path.Combine(custom, suffix);
        }

        string dir = Path.GetDirectoryName(outputPath) ?? Path.GetTempPath();
        try
        {
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "." + suffix);
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), suffix);
        }
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    private static CompressResult Fail(string taskId, CompressStage stage, string error,
                                       List<string> diagnostics, double startedAt)
    {
        diagnostics.Add("[错误] " + error);
        return new CompressResult
        {
            TaskId = taskId,
            Status = CompressStatus.Failed,
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
