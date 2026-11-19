using System.Globalization;
using SubFusion.Core.Media;
using SubFusion.Core.Services;

namespace SubFusion.Core.Merge;

/// <summary>
/// 一个输入文件的完整性结论：声明的时长是否真有那么多数据支撑。
/// </summary>
/// <remarks>
/// 之所以要单独判断这件事：容器里写的时长（duration）只是**声明**，
/// 它来自文件头的索引，跟后面是否真有对应的数据包无关。
/// 下载中断的 mp4、只拷了一半的文件、录制时崩溃的 mkv，
/// 声明的时长照样是完整的（moov 写在前面时尤其如此），但实际数据在中途就断了。
///
/// 直接把这号文件丢给 concat 会同时制造两个看起来毫无关联的错误：
/// <list type="number">
/// <item><b>时长不符</b>：可读数据比声明短，拼接输出自然短一大截，
///   而校验是按「声明时长之和」算的，于是报「有片段被丢弃或时间轴错位」；</item>
/// <item><b>结尾无法解码</b>：文件尾部那个残缺的 AAC/视频包被原样搬进输出，
///   解码到结尾就报 <c>invalid band type</c> 之类。</item>
/// </list>
/// 两者叠在一起，用户看到的是「输出校验未通过」——听起来像是合并做坏了，
/// 实际上是喂进去的素材本来就是坏的。这里把判断提前，让报错指回真正的责任方。
/// </remarks>
public sealed record InputHealth(string Path, double DeclaredSeconds, double ReadableSeconds)
{
    /// <summary>数据终点超出声明时长的富余量（秒）。容器时长偶尔比最后一帧略短，不算问题。</summary>
    private const double Slackiness = 0.5;

    /// <summary>
    /// 判定为截断的门槛：至少缺 0.5 秒，长素材按 0.5% 放宽，但最多放宽到 2 秒。
    ///
    /// 按比例放宽是为了容纳「容器时长比最后一帧多算零点几秒」这种正常误差，
    /// 而它必须封顶：纯比例会让两小时的片子要到缺 36 秒才算坏，
    /// 可实际缺 15 秒就是明确的截断，也正是用户最想被提醒的量级。
    /// </summary>
    public double Tolerance => Math.Max(Slackiness, Math.Min(DeclaredSeconds * 0.005, MaxProportional));

    /// <summary>比例容差的上限（秒）。超过这个量级的缺失一定是数据没了，不是取整误差。</summary>
    private const double MaxProportional = 2.0;

    /// <summary>声明时长与可读终点之差（秒）；可读终点未知时为 0。</summary>
    public double MissingSeconds => ReadableSeconds > 0 ? Math.Max(0, DeclaredSeconds - ReadableSeconds) : 0;

    /// <summary>
    /// 可读终点是否拿到了确定结论。false 表示扫包本身没给出答案（超时/被杀/ffprobe 出错），
    /// 此时既不能判「完整」也不能判「截断」。
    /// </summary>
    public bool ReadableKnown => ReadableSeconds > 0;

    /// <summary>
    /// true 表示文件声明有的数据实际读不到——尾部被截断。
    ///
    /// ReadableSeconds &lt;= 0 表示**扫包没有给出结论**，不是「一个可读包都没有」。
    /// 这两件事必须分开：把「探测失败」当成截断，引擎会对完好的大文件执行
    /// 「按可读部分收尾」，KeepSeconds 归零后产出 0 秒片段，
    /// 最终报错还指向用户本来完好的素材。所以这里不下判断（宁可漏报）；
    /// 真正的「收尾后仍然读不出来」由引擎在收尾失败时单独上报。
    /// </summary>
    public bool Truncated => DeclaredSeconds > 0 && ReadableKnown && MissingSeconds > Tolerance;

    /// <summary>可读数据里真正能安全保留的长度（留出余量，避开残缺的尾包）。</summary>
    public double KeepSeconds
    {
        get
        {
            // 余量必须大于「一个音频包 + 一个视频包」的跨度，否则残缺的尾包会被原样带进输出。
            // 实测 0.12 秒对 48kHz AAC（21ms/包）与 24fps 视频（42ms/帧）都够。
            const double margin = 0.12;
            double keep = ReadableSeconds - margin;
            return Math.Max(0, Math.Min(keep, DeclaredSeconds));
        }
    }

    public string Describe()
    {
        if (!ReadableKnown)
            return string.Format(CultureInfo.InvariantCulture,
                "{0}：无法确定可读终点（声明 {1:F2}s，扫包未给出结论）",
                System.IO.Path.GetFileName(Path), DeclaredSeconds);
        if (!Truncated)
            return string.Format(CultureInfo.InvariantCulture, "{0}：完整（{1:F2}s）",
                System.IO.Path.GetFileName(Path), DeclaredSeconds);
        return string.Format(CultureInfo.InvariantCulture,
            "{0}：声明 {1:F2}s，实际只能读到 {2:F2}s，缺 {3:F2}s",
            System.IO.Path.GetFileName(Path), DeclaredSeconds, ReadableSeconds, MissingSeconds);
    }
}

/// <summary>
/// 输入完整性检查与「按可读部分收尾」的修复。
///
/// 检查策略是两段式，兼顾速度与准确：
/// 先在声明终点前开一个窗口（声明时长的 2%，限制在 2–10 秒）扫一遍包；
/// 这段时间内有包、且最后一个包抵到声明终点，就判为完整——一次 ffprobe 约 0.2–0.4 秒。
/// 只有窗口里空手而归时才做全量扫包拿真实终点，代价约 0.3–0.5 秒/文件。
/// 判「不完整」属于结论性判断，因此不用抽样代替全量。
/// </summary>
public static class InputIntegrity
{
    /// <summary>进程超时：扫包只读索引与包头，几分钟足够覆盖超长素材。</summary>
    private const int ProbeTimeoutSeconds = 180;

    /// <summary>检查单个输入。</summary>
    public static async Task<InputHealth> ProbeAsync(string ffprobe, MediaBundle bundle,
                                                     CancellationToken cancellationToken = default)
    {
        double declared = bundle.Duration;
        bool hasVideo = bundle.PrimaryVideo is not null;
        bool hasAudio = bundle.HasAudio;

        // 拿不到声明时长（或没有可用流）时不下判断：宁可漏报，也不能把好文件判成坏的。
        if (string.IsNullOrEmpty(ffprobe) || !bundle.Success || declared <= 0 || (!hasVideo && !hasAudio))
            return new InputHealth(bundle.Path, declared, declared);

        double window = Math.Clamp(declared * 0.02, 2.0, 10.0);
        double from = Math.Max(0, declared - window);

        double tail = await LastPacketEndAsync(ffprobe, bundle, from, cancellationToken).ConfigureAwait(false);
        if (tail >= declared - 0.5)
            return new InputHealth(bundle.Path, declared, Math.Max(tail, 0));

        // 尾部窗口是空的：要么被截断，要么容器的时长声明得离谱。
        // 两种情况都得靠真实扫描给结论。
        double full = await LastPacketEndAsync(ffprobe, bundle, null, cancellationToken).ConfigureAwait(false);
        return new InputHealth(bundle.Path, declared, Math.Max(full, 0));
    }

    /// <summary>批量检查（串行：这些进程本身就在抢磁盘，并发只会更慢）。</summary>
    public static async Task<List<InputHealth>> ProbeManyAsync(string ffprobe, IReadOnlyList<MediaBundle> bundles,
                                                               CancellationToken cancellationToken = default)
    {
        var list = new List<InputHealth>(bundles.Count);
        foreach (MediaBundle bundle in bundles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            list.Add(await ProbeAsync(ffprobe, bundle, cancellationToken).ConfigureAwait(false));
        }
        return list;
    }

    /// <summary>
    /// 生成「按可读部分收尾」的命令：全程 <c>-c copy</c>，画质零损失。
    ///
    /// 输出容器必须与输入同族。若一律落成 mkv，一批 mp4 里冒出一个 mkv 就会被
    /// 兼容性判定当成「容器不一致」而触发整体重新编码——为了一段残缺文件把全部素材重压一遍，
    /// 既费时又掉画质。同族封装则不进这条分支。
    /// </summary>
    public static List<string> BuildSalvageCommand(string ffmpeg, MediaBundle bundle, string outputPath,
                                                   double keepSeconds)
    {
        bool mp4Family = bundle.Container.Family == "mp4";
        var cmd = new List<string>
        {
            ffmpeg, "-y", "-hide_banner", "-loglevel", "error", "-nostdin",
            "-i", bundle.Path,
            // 问号 = 存在才映射：只有视频或只有音频的文件都不会因此失败。
            "-map", "0:v:0?", "-map", "0:a:0?",
            "-c", "copy",
            "-map_chapters", "-1",
            "-t", keepSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            // 截断的文件尾部常见负时间戳，mp4 无法表达，必须归零。
            "-avoid_negative_ts", "make_zero",
        };
        cmd.AddRange(mp4Family
            ? new[] { "-f", "mp4", "-movflags", "+faststart" }
            : new[] { "-f", "matroska" });
        cmd.Add(outputPath);
        return cmd;
    }

    /// <summary>文件在保留容器族的前提下应该用什么扩展名。</summary>
    public static string SalvagedExtension(MediaBundle bundle)
        => bundle.Container.Family == "mp4" ? ".mp4" : ".mkv";

    /// <summary>两条流里更靠后的那个包终点；空手时返回 -1。</summary>
    private static async Task<double> LastPacketEndAsync(string ffprobe, MediaBundle bundle,
                                                         double? fromSeconds, CancellationToken cancellationToken)
    {
        double best = -1;
        if (bundle.PrimaryVideo is not null)
            best = Math.Max(best, await StreamEndAsync(ffprobe, bundle.Path, "v:0", fromSeconds, cancellationToken)
                .ConfigureAwait(false));
        if (bundle.HasAudio)
            best = Math.Max(best, await StreamEndAsync(ffprobe, bundle.Path, "a:0", fromSeconds, cancellationToken)
                .ConfigureAwait(false));
        return best;
    }

    private static async Task<double> StreamEndAsync(string ffprobe, string path, string selector,
                                                     double? fromSeconds, CancellationToken cancellationToken)
    {
        var args = new List<string> { "-v", "error", "-select_streams", selector };
        if (fromSeconds is > 0)
            args.AddRange(new[]
            {
                "-read_intervals",
                fromSeconds.Value.ToString("0.###", CultureInfo.InvariantCulture) + "%+#4000",
            });
        args.AddRange(new[] { "-show_entries", "packet=pts_time,duration_time", "-of", "csv=p=0", path });

        ProcessCapture proc;
        try
        {
            proc = await ProcessUtil.RunAsync(ffprobe, args, ProbeTimeoutSeconds,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch { return -1; }
        if (proc.TimedOut) return -1;

        double best = -1;
        foreach (string raw in proc.Stdout.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            string[] parts = line.Split(',');
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double pts))
                continue;   // 无 pts 的包（N/A）不参与判断
            double duration = parts.Length > 1 &&
                              double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture,
                                  out double d) ? d : 0;
            double end = pts + duration;
            if (end > best) best = end;
        }
        return best;
    }
}
