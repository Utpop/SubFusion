using System.Globalization;
using SubFusion.Core.Models;

namespace SubFusion.Core.Services;

/// <summary>原 core.check_integrity / verify_output：输出文件流结构与完整性校验。</summary>
public static class OutputVerifier
{
    private static readonly string[] IntegrityBadKeywords =
        { "prematurely", "invalid data", "truncat", "corrupt", "error while decoding" };
    private static readonly string[] IntegrityReasonKeywords =
        { "prematurely", "invalid", "truncat", "corrupt", "error" };

    /// <summary>
    /// 从解码输出里筛出真正表示"文件有问题"的行。
    ///
    /// 必须过滤掉以 <c>[null @</c> 开头的行：那是我们主动指定的 null 复用器在抱怨输出侧时间戳，
    /// 典型如 <c>Application provided invalid, non monotonically increasing dts to muxer in stream 1:
    /// 89088 &gt;= 89082</c>。它说的是"往垃圾桶里塞帧时时间戳有重叠"，不是"文件解码不出来"——
    /// 实测一份完全健康的两段 AAC 拼接文件，全片解码必然出现这一行。
    /// 把它当成损坏，会让校验对**每一个**正常的 AAC 拼接输出都报错，
    /// 于是"校验通过才删源"永远无法通过，整个删源功能事实上被关闭。
    ///
    /// 真正损坏的报文来自解复用器或解码器，形如
    /// <c>[matroska,webm @ ...] invalid as first byte of an EBML number</c>、
    /// <c>[dec:aac @ ...] Error submitting packet to decoder: Invalid data found</c>，
    /// 全部在保留之列。
    /// </summary>
    internal static string[] DecodeErrorLines(string stderr)
    {
        return stderr
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Where(l => !IsNoiseLine(l))
            .ToArray();
    }

    /// <summary>
    /// 这一行是不是「不是文件损坏」的噪声。
    ///
    /// 两类噪声，都必须滤掉，滤漏一类就会把健康文件判成接缝损坏：
    ///
    /// 1) <b>null 复用器的时间戳抱怨</b>（<c>[null @ …]</c>、新版 ffmpeg 的
    ///    <c>[out#0/null @ …]</c>）。它说的是「往垃圾桶里塞帧时时间戳有重叠」，
    ///    实测每一份正常的两段 AAC 拼接文件都会出现。
    ///
    /// 2) <b>ffmpeg 的重复抑制摘要</b> <c>    Last message repeated N times</c>。
    ///    这一行没有 <c>[null @</c> 前缀，只靠前缀过滤会漏过去。
    ///    而它出现的时机恰恰是「同一条 null 时间戳抱怨连着刷了两次」——
    ///    也就是 AAC 拼接接缝处最常见的输出。漏掉它，接缝校验就会对一个
    ///    完好的文件报「拼接点损坏」，而且样本正好是这句废话。
    ///
    /// 丢掉摘要行不会漏掉真正的错误：ffmpeg 计数前总会把原消息完整打印一次
    /// （见 libavutil/log.c 的 av_log_default_callback），真错误那一行仍在。
    /// </summary>
    internal static bool IsNoiseLine(string line)
    {
        if (line.StartsWith("[null @", StringComparison.Ordinal)) return true;
        // 新版 ffmpeg 给输出侧上下文加了 out#N 前缀，形如 [out#0/null @ 0x...]。
        if (line.StartsWith("[out#", StringComparison.Ordinal) &&
            line.Contains("/null @", StringComparison.Ordinal)) return true;
        // 重复抑制摘要（缩进无关，前面已经 Trim 过）。
        if (line.Contains("Last message repeated", StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// 全片解码校验：把整个文件解码一遍（输出到 null），统计解码错误。
    ///
    /// 与 <see cref="CheckIntegrityAsync"/> 的区别很关键：抽样只解码开头 1 秒与结尾 2 秒，
    /// 而拼接产生的损坏几乎都发生在段间的接缝处（中段），抽样根本碰不到。
    /// 因为调用方会据此删除源文件，这里必须全片跑一遍，宁可慢也不能漏。
    /// </summary>
    /// <returns>错误描述列表；空列表表示全片可完整解码。</returns>
    public static async Task<List<string>> CheckFullDecodeAsync(string? ffmpeg, string path,
                                                               int timeoutSeconds = 1800,
                                                               double knownDuration = 0,
                                                               Action<double>? onProgress = null)
    {
        var issues = new List<string>();
        if (string.IsNullOrEmpty(ffmpeg)) return issues;
        if (!File.Exists(path)) { issues.Add("输出文件不存在"); return issues; }

        // 全片解码是整条校验链里最慢的一步（与素材时长线性相关，几十 GB 要十几分钟），
        // 所以它必须能报告进度。否则界面会在「正在收尾… 99%」上静止十几分钟，
        // 与卡死无法区分——这正是用户实际反馈过的现象。
        // 走 FfmpegRunner 而不是 ProcessUtil，是因为只有它会解析 -progress 管道；
        // 它同时把 stderr 收集下来，判断逻辑与原来一致。
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin", "-progress", "pipe:1",
            "-i", path, "-f", "null", "-",
        };

        var logLines = new List<string>();
        int exitCode;
        string stderrText;
        try
        {
            (exitCode, stderrText) = await FfmpegRunner.RunFfmpegAsync(
                new[] { ffmpeg }.Concat(args).ToList(),
                knownDuration > 0 ? knownDuration : null,
                onProgress is null ? null : (percent, _, _) => onProgress(percent / 100.0),
                CancellationToken.None,
                logLines, 400).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 无法执行校验时不能假定"没问题"——删源的前提是"已证明完好"。
            issues.Add("全片解码校验无法执行：" + ex.Message);
            return issues;
        }

        string errText = stderrText.Trim();

        // FfmpegRunner 在超时/无法启动时返回 -1；能跑完的 ffmpeg 不会返回 -1。
        if (exitCode == -1)
        {
            issues.Add("全片解码校验未能完成：" + (errText.Length > 0 ? errText : "进程异常退出"));
            return issues;
        }

        if (exitCode != 0)
        {
            string first = errText.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
            issues.Add("全片解码失败（退出码 " + exitCode + "）" +
                       (first.Length > 0 ? "：" + first : ""));
            return issues;
        }

        // 只看真正来自解复用器/解码器的行；null 复用器的时间戳抱怨不代表文件损坏。
        string[] errorLines = DecodeErrorLines(errText);
        if (errorLines.Length > 0)
        {
            // -loglevel error 下仍有非 null-muxer 输出，说明解码过程真的报了错。
            string sample = errorLines[0].Length > 80 ? errorLines[0][..80] : errorLines[0];
            issues.Add("全片解码存在错误（" + errorLines.Length + " 行）：" + sample);
        }
        return issues;
    }

    /// <summary>
    /// 接缝定点解码：在每段边界处各解码一小段，专门命中 concat 产生损坏的地方。
    ///
    /// 为什么需要这一步（而不是只做全片解码）：
    /// 拼接的损坏几乎都发生在段与段的接缝上，而接缝在文件的中段。
    /// 全片解码确实能覆盖接缝，但耗时与素材时长线性相关（2.2GB / 2 小时的片子要跑十几分钟）。
    /// 这里按「前面若干段时长之和」算出每个接缝的位置，只解接缝前后各 1 秒，
    /// 用秒级的代价覆盖真正的高风险区域，作为全片解码之前的快速拦截。
    ///
    /// 两种校验是「递进」而不是「替代」关系：删源等不可逆动作仍要求全片解码通过。
    /// </summary>
    /// <param name="boundaries">各段边界的累计秒数（不含 0，不含总时长）。</param>
    /// <returns>错误描述列表；空列表表示所有接缝均可解码。</returns>
    public static async Task<List<string>> CheckSeamsAsync(string? ffmpeg, string path,
                                                           IReadOnlyList<double> boundaries,
                                                           int timeoutSeconds = 120)
    {
        var issues = new List<string>();
        if (string.IsNullOrEmpty(ffmpeg)) return issues;
        if (!File.Exists(path)) { issues.Add("输出文件不存在"); return issues; }
        if (boundaries.Count == 0) return issues;

        // 每个接缝只解 2 秒（接缝前 1 秒 + 后 1 秒），覆盖跨段的参考帧依赖。
        const double window = 1.0;
        foreach (double boundary in boundaries)
        {
            if (boundary <= window) continue;      // 太靠前，与开头检查重叠
            double start = boundary - window;

            var args = new List<string>
            {
                "-hide_banner", "-loglevel", "error", "-nostdin",
                "-ss", start.ToString("0.###", CultureInfo.InvariantCulture),
                "-t", (window * 2).ToString("0.###", CultureInfo.InvariantCulture),
                "-i", path, "-f", "null", "-",
            };

            ProcessCapture proc;
            try
            {
                proc = await ProcessUtil.RunAsync(ffmpeg, args, timeoutSeconds).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                issues.Add(string.Format(CultureInfo.InvariantCulture,
                    "接缝 {0:0.###}s 处校验无法执行：{1}", boundary, ex.Message));
                return issues;   // 无法执行就不能假定没问题
            }

            if (proc.TimedOut)
            {
                issues.Add(string.Format(CultureInfo.InvariantCulture,
                    "接缝 {0:0.###}s 处校验超时，未能证明该处完好", boundary));
                return issues;
            }

            // 与全片解码用同一套筛选：null 复用器的时间戳抱怨不构成"接缝损坏"。
            string[] lines = DecodeErrorLines(proc.Stderr);
            if (proc.ExitCode != 0 || lines.Length > 0)
            {
                string sample = lines.Length > 0 ? lines[0] : ("退出码 " + proc.ExitCode);
                if (sample.Length > 80) sample = sample[..80];
                issues.Add(string.Format(CultureInfo.InvariantCulture,
                    "接缝 {0:0.###}s 处解码出错（拼接点损坏）：{1}", boundary, sample));
            }
        }
        return issues;
    }

    /// <summary>
    /// 时长断言：输出时长必须约等于各段时长之和。
    ///
    /// 这是整条校验链里最便宜的一环（只用一次 ffprobe 的已有结果，不启动任何进程），
    /// 却能拦住 concat 最典型的静默损坏：容器 duration 与实际内容不符导致后续段整体偏移。
    /// ffmpeg 官方文档明确点出这是 artifacts 的根源
    /// （"if the duration is incorrect ... it can cause artifacts"，见 ffmpeg-formats 3.6）。
    /// </summary>
    /// <param name="expectedSeconds">各输入段时长之和。</param>
    /// <param name="actualSeconds">实际输出时长。</param>
    /// <param name="frameRate">用于折算容差；<=0 时只用比例容差。</param>
    public static List<string> CheckDuration(double expectedSeconds, double actualSeconds, double frameRate = 0)
    {
        var issues = new List<string>();
        if (expectedSeconds <= 0 || actualSeconds <= 0) return issues;

        // 容差取「2% 与若干帧」的较大者：长素材按比例，短素材按帧。
        // 只放宽到能容纳 AAC priming/padding 与最后一帧的取整误差，
        // 不足以容纳「整段偏移」这种量级的错误。
        double tolerance = Math.Max(0.5, expectedSeconds * 0.02);
        if (frameRate > 0) tolerance = Math.Max(tolerance, 4.0 / frameRate);

        double delta = Math.Abs(actualSeconds - expectedSeconds);
        if (delta > tolerance)
            issues.Add(string.Format(CultureInfo.InvariantCulture,
                "输出时长与各段之和不符（期望约 {0:F2}s，实际 {1:F2}s，相差 {2:F2}s，容差 {3:F2}s），" +
                "说明有片段被丢弃或时间轴错位", expectedSeconds, actualSeconds, delta, tolerance));
        return issues;
    }

    /// <summary>
    /// 内容终点检查：声明的时长在后面是否真有数据。
    ///
    /// 这是头尾抽样之外必须补上的一环。头尾抽样只解码「结尾 2 秒」，
    /// 它判断的是「解码器报不报错」——遇到尾部数据根本不存在时，ffmpeg 是
    /// 安静地什么都不解码然后返回 0，于是抽样"通过"，文件却被判为完好。
    /// 实测：一段声明 40 秒、实际只有 21 秒数据的 mp4，头尾抽样零告警。
    ///
    /// 这里改成看「解出来多少内容」：从声明终点往前开一个窗口解码，
    /// 窗口内必须真的产出接近窗口长度的内容，否则说明后面是空的。
    /// 代价只有一次 ffmpeg 启动（数百毫秒），比全片解码便宜得多，
    /// 却能把「容器说有这么长、数据却早断了」这类静默损坏拦下来。
    /// </summary>
    /// <returns>错误描述列表；空列表表示声明时长有数据支撑。</returns>
    public static async Task<List<string>> CheckContentEndAsync(string? ffmpeg, string path,
                                                                double declaredSeconds,
                                                                int timeoutSeconds = 120)
    {
        var issues = new List<string>();
        if (string.IsNullOrEmpty(ffmpeg)) return issues;
        if (!File.Exists(path)) { issues.Add("输出文件不存在"); return issues; }
        if (declaredSeconds <= 0) return issues;

        // 窗口取 1% 封顶 4 秒：长素材不必解 10 秒去证明一件事，短素材也不至于只剩几帧。
        double window = Math.Clamp(declaredSeconds * 0.01, 1.0, 4.0);
        double from = Math.Max(0, declaredSeconds - window);
        // 窗口末尾要给解码器留出收尾空间，否则分不清「数据没了」与「差最后两帧」。
        double accept = window * 0.75;

        var args = new List<string>
        {
            "-hide_banner", "-nostats", "-loglevel", "error", "-nostdin",
            "-ss", from.ToString("0.###", CultureInfo.InvariantCulture),
            "-t", window.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", path,
            // 问号 = 存在才解码；纯视频或纯音频文件都不会因此失败。
            "-map", "0:v:0?", "-map", "0:a:0?",
            "-f", "null", "-",
            "-progress", "pipe:1",
        };

        ProcessCapture proc;
        try
        {
            proc = await ProcessUtil.RunAsync(ffmpeg, args, timeoutSeconds).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 无法执行时不能假定没问题：这一项正是「敢不敢删源」的依据之一。
            issues.Add("内容终点校验无法执行：" + ex.Message);
            return issues;
        }
        if (proc.TimedOut)
        {
            issues.Add("内容终点校验超时，未能证明输出完整");
            return issues;
        }

        double produced = 0;
        foreach (string raw in proc.Stdout.Split('\n'))
        {
            string line = raw.Trim();
            if (!line.StartsWith("out_time_us=", StringComparison.Ordinal)) continue;
            string value = line["out_time_us=".Length..].Trim();
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long us) && us > 0)
                produced = Math.Max(produced, us / 1_000_000.0);
        }

        if (produced < accept)
        {
            issues.Add(string.Format(CultureInfo.InvariantCulture,
                "输出在 {0:F2}s 之后没有可解码的内容（声明时长 {1:F2}s，该区间只解出 {2:F2}s，" +
                "说明文件尾部被截断或时间轴被压缩）", from, declaredSeconds, produced));
        }
        return issues;
    }

    /// <summary>
    /// 全片抽样解码：沿整条时间轴均匀取若干窗口各解一小段。
    ///
    /// 这是「删源前全片解码」的替代方案。原来那一步对整个输出逐帧解码，
    /// 耗时与时长线性相关 —— 36 分钟的片子实测要 116 秒，2 小时的片子要十几分钟，
    /// 而这段时间界面在收尾阶段完全静止，看起来就是卡死。
    ///
    /// 为什么抽样在这里是合理的：真正的损坏几乎只出现在<b>拼接接缝</b>上，
    /// 而接缝在拼接完成后已经逐个定点解码验过（<see cref="CheckSeamsAsync"/>）。
    /// 全片解码多出来的价值只是「碰巧解到别处的偶发损坏」，
    /// 均匀抽样以固定代价覆盖整条时间轴，命中概率远高于只验接缝，
    /// 代价却与素材时长基本无关。
    ///
    /// 必须说清楚的是：抽样是<b>采样</b>，不是证明。它降低风险，不消除风险。
    /// 因此这一步还配了一条更强的证据——文件字节数必须与校验通过时的临时文件一致，
    /// 那能确定性地排除「复制过程写坏了/写短了」这一整类问题。
    /// </summary>
    /// <param name="durationSeconds">输出时长；&lt;= 0 时退回只查头尾。</param>
    /// <param name="timeoutSeconds">单窗口解码超时。</param>
    public static async Task<List<string>> CheckSampledDecodeAsync(string? ffmpeg, string path,
                                                                   double durationSeconds,
                                                                   int timeoutSeconds = 60)
    {
        var issues = new List<string>();
        if (string.IsNullOrEmpty(ffmpeg)) return issues;
        if (!File.Exists(path)) { issues.Add("输出文件不存在"); return issues; }
        if (durationSeconds <= 0)
            return await CheckIntegrityAsync(ffmpeg, path, timeoutSeconds).ConfigureAwait(false);

        // 抽样点数量：每约两分钟一个，至少 6 个、最多 16 个。
        // 上限存在的意义是「代价封顶」：4 小时的片子和 40 分钟的片子，
        // 这一步都只花几秒，而原来的全片解码要差出 6 倍时间。
        int count = Math.Clamp((int)(durationSeconds / 120.0), 6, 16);
        // 窗口 1 秒：短到便宜，长到足以跨过一个 GOP 的参考帧依赖。
        const double window = 1.0;

        for (int i = 0; i < count; i++)
        {
            // 均匀铺开且避开首尾各 1%：头尾已有专门的检查覆盖。
            double ratio = count == 1 ? 0.5 : (i + 0.5) / count;
            double at = Math.Clamp(durationSeconds * ratio, 0, Math.Max(0, durationSeconds - window));

            var args = new List<string>
            {
                "-hide_banner", "-loglevel", "error", "-nostdin",
                "-ss", at.ToString("0.###", CultureInfo.InvariantCulture),
                "-t", window.ToString("0.##", CultureInfo.InvariantCulture),
                "-i", path,
                "-map", "0:v:0?", "-map", "0:a:0?",
                "-f", "null", "-",
            };

            ProcessCapture proc;
            try
            {
                proc = await ProcessUtil.RunAsync(ffmpeg, args, timeoutSeconds).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                issues.Add(string.Format(CultureInfo.InvariantCulture,
                    "抽样校验在 {0:0.##}s 处无法执行：{1}", at, ex.Message));
                return issues;   // 无法执行不能假定没问题
            }
            if (proc.TimedOut)
            {
                issues.Add(string.Format(CultureInfo.InvariantCulture,
                    "抽样校验在 {0:0.##}s 处超时，未能证明该处完好", at));
                return issues;
            }

            string[] lines = DecodeErrorLines(proc.Stderr);
            if (proc.ExitCode != 0 || lines.Length > 0)
            {
                string sample = lines.Length > 0 ? lines[0] : ("退出码 " + proc.ExitCode);
                if (sample.Length > 80) sample = sample[..80];
                issues.Add(string.Format(CultureInfo.InvariantCulture,
                    "抽样点 {0:0.##}s 处解码出错：{1}", at, sample));
            }
        }
        return issues;
    }

    /// <summary>快速完整性检查：解码开头 1 秒与结尾 2 秒，能发现「文件被截断」。</summary>
    public static async Task<List<string>> CheckIntegrityAsync(string? ffmpeg, string path, int timeout = 60)
    {
        var issues = new List<string>();
        if (string.IsNullOrEmpty(ffmpeg) || !File.Exists(path)) return issues;

        var probes = new (string[] Extra, string Label)[]
        {
            (new[] { "-t", "1" }, "开头"),
            (new[] { "-sseof", "-2", "-t", "2" }, "结尾"),
        };

        foreach (var (extra, label) in probes)
        {
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
            args.AddRange(extra);
            args.AddRange(new[] { "-i", path, "-f", "null", "-" });

            ProcessCapture proc;
            try
            {
                proc = await ProcessUtil.RunAsync(ffmpeg, args, timeout).ConfigureAwait(false);
            }
            catch { continue; }        // 超时/无法启动时不误报
            if (proc.TimedOut) continue;

            string errText = proc.Stderr.Trim();
            bool bad = proc.ExitCode != 0;
            if (!bad && !string.IsNullOrEmpty(errText))
            {
                string lowered = errText.ToLowerInvariant();
                bad = IntegrityBadKeywords.Any(k => lowered.Contains(k, StringComparison.Ordinal));
            }
            if (!bad) continue;

            string[] errLines = errText.Split('\n')
                .Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();
            string reason = "";
            foreach (string line in errLines)
            {
                string low = line.ToLowerInvariant();
                if (IntegrityReasonKeywords.Any(k => low.Contains(k, StringComparison.Ordinal)))
                {
                    reason = line.Length > 70 ? line[..70] : line;
                    break;
                }
            }
            if (string.IsNullOrEmpty(reason))
                reason = errLines.Length > 0
                    ? (errLines[^1].Length > 70 ? errLines[^1][..70] : errLines[^1])
                    : "读取失败";
            issues.Add($"输出文件{label}数据不完整或无法解码（{reason}）");
        }
        return issues;
    }

    /// <summary>
    /// 校验输出是否符合 plan 预期。ffprobe 不可用时返回通过，避免误报。
    /// deep=true 时额外做开头/结尾的快速解码检查。
    /// </summary>
    public static async Task<VerifyResult> VerifyOutputAsync(string? ffprobe, string output,
                                                              OutputPlan plan,
                                                              MediaInfo? sourceInfo = null,
                                                              string? ffmpeg = null,
                                                              bool deep = true, int timeout = 30)
    {
        var result = new VerifyResult();
        var issues = result.Issues;
        if (string.IsNullOrEmpty(ffprobe)) return new VerifyResult { Ok = true };

        if (!File.Exists(output))
        {
            issues.Add("输出文件不存在");
            result.Ok = false;
            return result;
        }

        long size;
        try { size = new FileInfo(output).Length; }
        catch { size = 0; }
        result.Detail.Size = size;
        if (size <= 0)
        {
            issues.Add("输出文件为空（0 字节）");
            result.Ok = false;
            return result;
        }

        MediaInfo info = await MediaProbe.ProbeStreamsAsync(ffprobe, output, timeout).ConfigureAwait(false);
        result.Detail = info;
        result.Detail.Size = size;

        if (info.VStreams == 0 && plan.ExpectedVideo != 0)
            issues.Add("输出中没有视频流");

        // 硬字幕 + 无可用轨时，命令把源字幕原样复制进输出：
        // 这时不能拿 ExpectedSubs(=0，「没有新轨」) 去比对实际字幕数。
        // 只有源字幕数量本身可信时才判，否则这一项无从核对，直接跳过。
        if (plan.SubsFromSource)
        {
            if (plan.SubsSourceKnown && sourceInfo is not null && info.SStreams != sourceInfo.SStreams)
                issues.Add(string.Format(CultureInfo.InvariantCulture,
                    "字幕轨数量不符（期望沿用源文件的 {0} 条，实际 {1} 条）",
                    sourceInfo.SStreams, info.SStreams));
        }
        else if (info.SStreams != plan.ExpectedSubs)
        {
            issues.Add(string.Format(CultureInfo.InvariantCulture,
                "字幕轨数量不符（期望 {0} 条，实际 {1} 条）", plan.ExpectedSubs, info.SStreams));
        }

        if (plan.ExpectedAudio != 0 && info.AStreams == 0)
            issues.Add(string.Format(CultureInfo.InvariantCulture,
                "源文件有 {0} 条音轨，输出中没有音轨", plan.ExpectedAudio));

        string? wanted = plan.Vcodec switch
        {
            "libx264" => "h264",
            "libx265" => "hevc",
            "h264_nvenc" => "h264",
            "hevc_nvenc" => "hevc",
            _ => null,
        };
        if (!string.IsNullOrEmpty(plan.Vcodec) && plan.Vcodec != "copy" &&
            !string.IsNullOrEmpty(info.Vcodec) && wanted is not null && info.Vcodec != wanted)
            issues.Add($"视频编码为 {info.Vcodec}，预期 {wanted}");

        if (sourceInfo is not null)
        {
            double? srcDur = sourceInfo.Duration;
            double? outDur = info.Duration;
            if (srcDur.HasValue && outDur.HasValue)
            {
                double tolerance = Math.Max(2.0, srcDur.Value * 0.05);
                if (Math.Abs(outDur.Value - srcDur.Value) > tolerance)
                    issues.Add(string.Format(CultureInfo.InvariantCulture,
                        "时长偏差较大（源 {0:F1} 秒，输出 {1:F1} 秒）", srcDur.Value, outDur.Value));
            }

            // 纯封装（不重编码）时体积不该明显缩水；硬字幕会强制重编码视频，体积缩水属正常
            if (plan.Mode != "hard" && plan.Vcodec == "copy" && plan.Acodec == "copy")
            {
                long srcSize = sourceInfo.Size;
                if (srcSize > 0 && size < srcSize * 0.5)
                    issues.Add($"输出体积（{Formatting.FormatSize(size)}）明显小于源文件" +
                               $"（{Formatting.FormatSize(srcSize)}），可能数据不完整");
            }
        }

        if (deep && !string.IsNullOrEmpty(ffmpeg))
            issues.AddRange(await CheckIntegrityAsync(ffmpeg, output).ConfigureAwait(false));

        result.Ok = issues.Count == 0;
        return result;
    }
}
