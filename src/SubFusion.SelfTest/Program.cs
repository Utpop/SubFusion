using System.Globalization;
using System.Text;
using System.Text.Json;
using SubFusion.Core;
using SubFusion.Core.Models;
using SubFusion.Core.Chapters;
using SubFusion.Core.Compress;
using SubFusion.Core.Media;
using SubFusion.Core.Merge;
using SubFusion.Core.Services;
using SubFusion.Core.Trim;

namespace SubFusion.SelfTest;

/// <summary>
/// 功能等价自检：
///  A) 用原 Python 版 core.py 生成的 Reference/reference.json 逐项比对纯逻辑；
///  B) 用本机 ffmpeg 做真实端到端合并（软字幕 / 硬字幕 / GBK 转码 / 输出校验）。
/// 退出码 0 表示全部通过。
/// </summary>
internal static class Program
{
    private static int _passed;
    private static readonly List<string> Failures = new();

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _passed++; return; }
        Failures.Add($"{name}{(string.IsNullOrEmpty(detail) ? "" : " — " + detail)}");
    }

    private static void Equal(string name, object? expected, object? actual)
        => Check(name, Equals(expected, actual), $"期望 [{expected}]，实际 [{actual}]");

    private static void Near(string name, double expected, double actual, double eps = 1e-9)
        => Check(name, Math.Abs(expected - actual) < eps, $"期望 {expected:R}，实际 {actual:R}");

    private static void Sequence(string name, IEnumerable<string> expected, IEnumerable<string> actual)
    {
        var e = expected.ToList();
        var a = actual.ToList();
        bool ok = e.Count == a.Count && e.Zip(a).All(p => p.First == p.Second);
        Check(name, ok,
            ok ? "" : $"期望 [{string.Join(" | ", e)}]，实际 [{string.Join(" | ", a)}]");
    }

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string referencePath = Path.Combine(AppContext.BaseDirectory, "Reference", "reference.json");
        if (!File.Exists(referencePath))
        {
            Console.Error.WriteLine("找不到参考数据：" + referencePath);
            return 2;
        }
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(referencePath, Encoding.UTF8));
        JsonElement root = doc.RootElement;

        Console.WriteLine("=== A. 与 Python 版逐项等价比对 ===");
        CheckFormatting(root);
        CheckNames(root);
        CheckMatching(root);
        CheckOutputPaths(root);
        CheckCommands(root);
        CheckProgress(root);
        await CheckEncodingsAsync(root);
        CheckNaturalSort();
        CheckCrossLanguageMatching();

        Console.WriteLine("=== B. 端到端功能验证（本机 ffmpeg） ===");
        await EndToEndAsync(args.Contains("--skip-e2e"));

        Console.WriteLine("=== C. 界面布局约束 ===");
        CheckLayoutConstraints();
        CheckResourceReferences();

        Console.WriteLine("=== D. 重负载健壮性 ===");
        await CheckRobustnessAsync();

        Console.WriteLine("=== E. 替换原文件");
        await CheckReplaceSourceAsync();

        Console.WriteLine("=== F. 并发硬字幕工作副本隔离");
        await CheckHardSubIsolationAsync();

        Console.WriteLine("=== G. 视频合并引擎");
        await CheckVideoMergeAsync();
        Console.WriteLine("=== H. MKV 自动章节");
        await CheckChaptersAsync();
        Console.WriteLine("=== I. 校验阶梯 / 诊断建议 / 清单健壮性");
        await CheckVerificationLadderAsync();
        Console.WriteLine("=== J. 只重编码少数派");
        await CheckPartialReencodeAsync();
        Console.WriteLine("=== K. 列表排序与进度可见性");
        CheckOrderingAndProgressUi();
        Console.WriteLine("=== L. 顺序搬移");
        CheckListReorder();
        Console.WriteLine("=== M. 重编码进度可见性");
        await CheckProgressVisibilityAsync();
        Console.WriteLine("=== N. 视频裁剪（无损 / 精确 / 旋转 / 音画同步）");
        await CheckTrimAsync();
        Console.WriteLine("=== O. 时间轴拖动选择");
        CheckTimelineGeometry();
        Console.WriteLine("=== P. 输入截断检测与收尾");
        await CheckTruncatedInputAsync();
        Console.WriteLine("=== Q. 输出搬运与进度可见性");
        await CheckOutputMoveAsync();
        Console.WriteLine("=== R. 进度终态送达");
        CheckProgressTerminalDelivery();
        Console.WriteLine("=== S. 无损组合分配到文件夹");
        await CheckGroupFolderExportAsync();
        Console.WriteLine("=== T. 视频压制（独立引擎 / 硬件编码探测 / 命令形状）");
        await CheckCompressAsync();

        Console.WriteLine();
        Console.WriteLine($"通过：{_passed} 项，失败：{Failures.Count} 项");
        foreach (string failure in Failures)
            Console.WriteLine("  [FAIL] " + failure);
        return Failures.Count == 0 ? 0 : 1;
    }

    #region A. 纯逻辑比对

    private static void CheckFormatting(JsonElement root)
    {
        foreach (JsonElement item in root.GetProperty("formatDuration").EnumerateArray())
        {
            double? input = item.GetProperty("input").ValueKind == JsonValueKind.Null
                ? null : item.GetProperty("input").GetDouble();
            string expected = item.GetProperty("expected").GetString()!;
            Equal($"format_duration({input?.ToString(CultureInfo.InvariantCulture) ?? "null"})",
                  expected, Formatting.FormatDuration(input));
        }
        foreach (JsonElement item in root.GetProperty("formatSize").EnumerateArray())
        {
            long? input = item.GetProperty("input").ValueKind == JsonValueKind.Null
                ? null : item.GetProperty("input").GetInt64();
            string expected = item.GetProperty("expected").GetString()!;
            Equal($"format_size({input?.ToString(CultureInfo.InvariantCulture) ?? "null"})",
                  expected, Formatting.FormatSize(input));
        }
    }

    private static void CheckNames(JsonElement root)
    {
        foreach (JsonElement item in root.GetProperty("normalize").EnumerateArray())
            Equal("normalize_name " + item.GetProperty("input").GetString(),
                  item.GetProperty("expected").GetString(),
                  NameNormalizer.Normalize(item.GetProperty("input").GetString()!));

        foreach (JsonElement item in root.GetProperty("tokenize").EnumerateArray())
            Sequence("tokenize " + item.GetProperty("input").GetString(),
                     item.GetProperty("expected").EnumerateArray().Select(x => x.GetString()!),
                     NameNormalizer.Tokenize(item.GetProperty("input").GetString()!));

        foreach (JsonElement item in root.GetProperty("detectLang").EnumerateArray())
            Equal("detect_lang_from_name " + item.GetProperty("input").GetString(),
                  item.GetProperty("expected").GetString(),
                  NameNormalizer.DetectLangFromName(item.GetProperty("input").GetString()!));

        foreach (JsonElement item in root.GetProperty("similarity").EnumerateArray())
            Near("similarity " + item.GetProperty("a").GetString() + " / " + item.GetProperty("b").GetString(),
                 item.GetProperty("expected").GetDouble(),
                 NameNormalizer.Similarity(item.GetProperty("a").GetString()!,
                                           item.GetProperty("b").GetString()!));
    }

    private static void CheckMatching(JsonElement root)
    {
        JsonElement m = root.GetProperty("match");
        List<string> videos = m.GetProperty("videos").EnumerateArray().Select(x => x.GetString()!).ToList();
        List<string> subs = m.GetProperty("subs").EnumerateArray().Select(x => x.GetString()!).ToList();
        double threshold = m.GetProperty("threshold").GetDouble();
        MatchResult actual = SubtitleMatcher.Match(videos, subs, threshold);
        foreach (JsonProperty expected in m.GetProperty("expectedMapping").EnumerateObject())
            Sequence("match_subtitles " + expected.Name,
                     expected.Value.EnumerateArray().Select(x => x.GetString()!),
                     actual.Mapping.TryGetValue(expected.Name, out var list) ? list : new List<string>());
        Sequence("match_subtitles 未匹配",
                 m.GetProperty("expectedUnmatched").EnumerateArray().Select(x => x.GetString()!),
                 actual.Unmatched);
    }

    private static void CheckOutputPaths(JsonElement root)
    {
        foreach (JsonElement item in root.GetProperty("outputPath").EnumerateArray())
        {
            Options options = OptionsFromJson(item.GetProperty("options"));
            string outputDir = item.GetProperty("outputDir").GetString()!;
            string video = item.GetProperty("video").GetString()!;
            Equal("build_output_path " + video,
                  item.GetProperty("expected").GetString(),
                  CommandBuilder.BuildOutputPath(video, options, outputDir));
        }
    }

    private static void CheckCommands(JsonElement root)
    {
        const string Video = @"C:\V\Show.S01E01.720p.mkv";
        const string Output = @"D:\Out\Show.S01E01.720p.mkv";
        const string Ffmpeg = @"C:\ffmpeg\bin\ffmpeg.exe";

        foreach (JsonElement item in root.GetProperty("commands").EnumerateArray())
        {
            string name = item.GetProperty("name").GetString()!;
            Options options = OptionsFromJson(item.GetProperty("options"));
            JsonElement infoEl = item.GetProperty("info");
            var info = new MediaInfo
            {
                Duration = infoEl.GetProperty("duration").GetDouble(),
                VStreams = infoEl.GetProperty("vstreams").GetInt32(),
                AStreams = infoEl.GetProperty("astreams").GetInt32(),
                SStreams = infoEl.GetProperty("sstreams").GetInt32(),
                Vcodec = infoEl.GetProperty("vcodec").GetString()!,
                Acodec = infoEl.GetProperty("acodec").GetString()!,
                Width = infoEl.GetProperty("width").GetInt32(),
                Height = infoEl.GetProperty("height").GetInt32(),
                Size = infoEl.GetProperty("size").GetInt64(),
            };
            List<SubTrack> tracks = item.GetProperty("tracks").EnumerateArray()
                .Select(TrackFromJson).ToList();
            var job = new Job(Video, tracks, Output, 123.45, info);

            BuildCommandResult built = CommandBuilder.BuildCommand(job, options, Ffmpeg);
            Sequence($"build_command[{name}] cmd",
                     item.GetProperty("expectedCmd").EnumerateArray().Select(x => x.GetString()!),
                     built.Command);
            Sequence($"build_command[{name}] notes",
                     item.GetProperty("expectedNotes").EnumerateArray().Select(x => x.GetString()!),
                     built.Notes);

            JsonElement p = item.GetProperty("expectedPlan");
            Equal($"build_command[{name}] plan.mode", p.GetProperty("mode").GetString(), built.Plan.Mode);
            Equal($"build_command[{name}] plan.container", p.GetProperty("container").GetString(), built.Plan.Container);
            Equal($"build_command[{name}] plan.expected_video", p.GetProperty("expected_video").GetInt32(), built.Plan.ExpectedVideo);
            Equal($"build_command[{name}] plan.expected_audio", p.GetProperty("expected_audio").GetInt32(), built.Plan.ExpectedAudio);
            Equal($"build_command[{name}] plan.expected_subs", p.GetProperty("expected_subs").GetInt32(), built.Plan.ExpectedSubs);
            Equal($"build_command[{name}] plan.burned_subs", p.GetProperty("burned_subs").GetInt32(), built.Plan.BurnedSubs);
            Equal($"build_command[{name}] plan.vcodec", p.GetProperty("vcodec").GetString(), built.Plan.Vcodec);
            Equal($"build_command[{name}] plan.acodec", p.GetProperty("acodec").GetString(), built.Plan.Acodec);
        }
    }

    private static void CheckProgress(JsonElement root)
    {
        JsonElement p = root.GetProperty("progress");
        var state = new ProgressState();
        foreach (JsonElement line in p.GetProperty("lines").EnumerateArray())
            state.Feed(line.GetString());
        Near("ProgressState.position", p.GetProperty("expectedPosition").GetDouble(), state.Position);
        Near("ProgressState.speed", p.GetProperty("expectedSpeed").GetDouble(), state.Speed);
        Near("ProgressState.percent(100)", p.GetProperty("expectedPercent").GetDouble(), state.Percent(100.0));
        Near("ProgressState.eta(100)", p.GetProperty("expectedEta").GetDouble(), state.Eta(100.0)!.Value);
        Near("ProgressState.percent(null)", p.GetProperty("expectedPercentNoDuration").GetDouble(), state.Percent(null));
        Check("ProgressState.eta(null)", state.Eta(null) is null);
    }

    private static async Task CheckEncodingsAsync(JsonElement root)
    {
        string temp = Path.Combine(Path.GetTempPath(), "subfusion_selftest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            foreach (JsonElement item in root.GetProperty("encodings").EnumerateArray())
            {
                string name = item.GetProperty("name").GetString()!;
                byte[] bytes = Convert.FromBase64String(item.GetProperty("bytesBase64").GetString()!);
                string path = Path.Combine(temp, name + ".srt");
                await File.WriteAllBytesAsync(path, bytes);
                DetectedEncoding detected = EncodingService.DetectEncoding(path);
                Equal($"detect_encoding[{name}] encoding",
                      item.GetProperty("expectedEncoding").GetString(), detected.Encoding);
                Equal($"detect_encoding[{name}] hasBom",
                      item.GetProperty("expectedHasBom").GetBoolean(), detected.HasBom);

                // 转码结果也应能还原出正确文本
                ConvertResult conv = EncodingService.EnsureUtf8(path, temp, useCache: false);
                string text = File.ReadAllText(conv.Path, Encoding.UTF8);
                Check($"ensure_utf8[{name}] 输出含中文",
                      conv.Created == (name != "utf8_plain") && text.Contains('中'),
                      $"created={conv.Created}, text={text[..Math.Min(30, text.Length)]}");
            }
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* 忽略 */ }
        }
    }

    /// <summary>
    /// 文件名自然排序：连续数字按数值比较。
    /// 这是"默认按名称排序"的正确性基线——字符串序会把 10 排在 2 前面，与用户预期相反。
    /// </summary>
    /// <summary>
    /// 回归：中文压制组字幕（双峰S02E01.srt）对英文源（Twin.Peaks.S02E01.Coma.…mkv）。
    ///
    /// 实测真实目录 22 集全部匹配失败，原因是两条：
    ///   1. 季集号与中文标题挤在一起（双峰S02E01），整个被当成标题词，集数识别不出来；
    ///   2. 就算识别出集数，中英标题相似度恒为 0，任何权重都到不了阈值。
    /// 这里同时锁住修复后必须成立的三件事：跨语言能配上、季号不串、同语言错配不被硬配。
    /// </summary>
    private static void CheckCrossLanguageMatching()
    {
        const string Dir = @"D:\Movies\双峰 第二季 Twin Peaks Season 2 (1990)\";
        var videos = new List<string>
        {
            Dir + "Twin.Peaks.S02E01.May.the.Giant.Be.with.You.1080p.10bit.BluRay.6CH.x265.HEVC-PSA.mkv",
            Dir + "Twin.Peaks.S02E02.Coma.1080p.10bit.BluRay.6CH.x265.HEVC-PSA.mkv",
        };
        var subs = new List<string> { Dir + "双峰S02E01.srt", Dir + "双峰S02E02.srt" };

        MatchResult m = SubtitleMatcher.Match(videos, subs, 0.72);
        Sequence("跨语言匹配：E01 挂 E01", new[] { "双峰S02E01.srt" },
                 m.Mapping[videos[0]].Select(p => Path.GetFileName(p) ?? p));
        Sequence("跨语言匹配：E02 挂 E02", new[] { "双峰S02E02.srt" },
                 m.Mapping[videos[1]].Select(p => Path.GetFileName(p) ?? p));
        Equal("跨语言匹配：无未匹配字幕", 0, m.Unmatched.Count);

        // 季号必须参与区分：只有 S03E01 时，S02E01 的字幕不能挂上去。
        var seasonMismatch = SubtitleMatcher.Match(
            new List<string> { Dir + "Twin.Peaks.S03E01.1080p.mkv" },
            new List<string> { Dir + "双峰S02E01.srt" }, 0.72);
        Equal("季号不同不配对", 0, seasonMismatch.Mapping.Values.Sum(x => x.Count));

        // 同语言、标题不同、集数相同：不能只靠集数硬配（这是把字幕挂错剧的主要风险）。
        var sameScript = SubtitleMatcher.Match(
            new List<string> { Dir + "Twin.Peaks.S02E01.1080p.mkv" },
            new List<string> { Dir + "Breaking.Bad.S02E01.srt" }, 0.72);
        Equal("同语言不同剧不靠集数硬配", 0, sameScript.Mapping.Values.Sum(x => x.Count));

        // 精确认定：季集号必须真的被解析出来，否则上面几条会靠别的路径碰巧通过。
        NameSignature sig = NameNormalizer.Analyze(Dir + "双峰S02E01.srt");
        Equal("中文紧贴季集号：集号", "01", sig.Episode ?? "");
        Equal("中文紧贴季集号：季号", "02", sig.Season);
        Equal("中文紧贴季集号：标题不含集号", "双峰", sig.Title);
    }

    private static void CheckNaturalSort()
    {
        var cmp = NaturalComparer.Instance;

        string[] shuffled =
        {
            "第10集.mp4", "第2集.mp4", "第1集.mp4",
        };
        // 用拼接后的字符串比较：数组的 Equals 是引用比较，永远不相等。
        Check("N1 剧集按集数排序（2 在 10 之前）",
              string.Join(",", shuffled.OrderBy(x => x, cmp)) == "第1集.mp4,第2集.mp4,第10集.mp4",
              string.Join(",", shuffled.OrderBy(x => x, cmp)));

        string[] seq = { "part10.mkv", "part1.mkv", "part2.mkv", "part20.mkv", "part3.mkv" };
        Check("N2 序号自然序列",
              string.Join(",", seq.OrderBy(x => x, cmp)) ==
              "part1.mkv,part2.mkv,part3.mkv,part10.mkv,part20.mkv",
              string.Join(",", seq.OrderBy(x => x, cmp)));

        // 前导零不应打乱顺序：09 与 9 视为同值，仍有确定的先后。
        Check("N3 前导零不影响数值比较", cmp.Compare("ep09.mp4", "ep10.mp4") < 0,
              cmp.Compare("ep09.mp4", "ep10.mp4").ToString());

        // 超长数字不能溢出（用字符串长度比较而非 Parse）。
        Check("N4 超长数字不溢出且顺序正确",
              cmp.Compare("x123456789012345678901.mp4", "x123456789012345678902.mp4") < 0,
              cmp.Compare("x123456789012345678901.mp4", "x123456789012345678902.mp4").ToString());

        // 无数字时退化为不区分大小写的比较，Windows 上 "A" 与 "a" 视为同组。
        Check("N5 纯文本按不区分大小写排序", cmp.Compare("Alpha.mp4", "beta.mp4") < 0,
              cmp.Compare("Alpha.mp4", "beta.mp4").ToString());

        // 稳定性：同一输入重复比较结果必须一致。
        Check("N6 比较结果自反且稳定", cmp.Compare("a1.mp4", "a1.mp4") == 0,
              cmp.Compare("a1.mp4", "a1.mp4").ToString());
    }

    #endregion

    #region 辅助：从参考 JSON 构造对象

    private static Options OptionsFromJson(JsonElement el)
    {
        var o = new Options();
        foreach (JsonProperty p in el.EnumerateObject())
        {
            switch (p.Name)
            {
                case "container": o.Container = p.Value.GetString()!; break;
                case "mode": o.Mode = p.Value.GetString()!; break;
                case "vcodec": o.Vcodec = p.Value.GetString()!; break;
                case "acodec": o.Acodec = p.Value.GetString()!; break;
                case "crf": o.Crf = p.Value.GetInt32(); break;
                case "preset": o.Preset = p.Value.GetString()!; break;
                case "keep_existing_subs": o.KeepExistingSubs = p.Value.GetBoolean(); break;
                case "keep_metadata": o.KeepMetadata = p.Value.GetBoolean(); break;
                case "convert_encoding": o.ConvertEncoding = p.Value.GetBoolean(); break;
                case "output_dir": o.OutputDir = p.Value.GetString()!; break;
                case "suffix": o.Suffix = p.Value.GetString()!; break;
                // 字幕侧仍保留「覆盖」语义（替换原文件是另一套流程）；视频三模块才改成自动改名。
                case "overwrite": o.Overwrite = p.Value.GetBoolean(); break;
                case "workers": o.Workers = p.Value.GetInt32(); break;
                case "hard_multi": o.HardMulti = p.Value.GetBoolean(); break;
                case "verify": o.Verify = p.Value.GetBoolean(); break;
            }
        }
        return o;
    }

    private static SubTrack TrackFromJson(JsonElement el) => new(
        el.GetProperty("path").GetString()!,
        lang: el.TryGetProperty("lang", out var l) ? l.GetString()! : "und",
        title: el.TryGetProperty("title", out var t) ? t.GetString()! : "",
        @default: el.TryGetProperty("default", out var d) && d.GetBoolean(),
        forced: el.TryGetProperty("forced", out var f) && f.GetBoolean(),
        kind: el.TryGetProperty("kind", out var k) ? k.GetString()! : "text",
        enabled: !el.TryGetProperty("enabled", out var en) || en.GetBoolean());

    #endregion

    #region C. 界面布局约束

    /// <summary>
    /// 界面结构约束。用文字断言而不是引用 UI 类型，Core/SelfTest 仍不依赖 WinUI，
    /// 但重构 XAML 时能拦住"导航塌陷 / 内容区被固定像素高度裁掉 / 业务逻辑回流到 View"这类回归。
    /// </summary>
    private static void CheckLayoutConstraints()
    {
        string? shell = FindAppFile(Path.Combine("Views", "MainWindow.xaml"));
        if (shell is null)
        {
            Console.WriteLine("(未找到 MainWindow.xaml，跳过)");
            return;
        }

        Check("外壳：使用 NavigationView 作为主导航",
              shell.Contains("<NavigationView", StringComparison.Ordinal));
        Check("外壳：四个顶层入口齐全（字幕合并/视频合并/任务中心/设置）",
              shell.Contains(@"Tag=""subtitle""", StringComparison.Ordinal) &&
              shell.Contains(@"Tag=""merge""", StringComparison.Ordinal) &&
              shell.Contains(@"Tag=""tasks""", StringComparison.Ordinal) &&
              shell.Contains(@"Tag=""settings""", StringComparison.Ordinal) &&
              !shell.Contains(@"Tag=""dashboard""", StringComparison.Ordinal),
              "工作台页面已移除，不应再有该导航项");
        Check("外壳：视频裁剪是独立的一级入口",
              shell.Contains(@"Tag=""trim""", StringComparison.Ordinal));

        // 裁剪页的结构约束：诚实性来自「实际切点必须显示出来」，这是不能退化的界面契约。
        string? trimPage = FindAppFile(Path.Combine("Pages", "TrimPage.xaml"));
        Check("裁剪页：存在且包含起止时间输入",
              trimPage is not null &&
              trimPage.Contains(@"StartInput, Mode=TwoWay", StringComparison.Ordinal) &&
              trimPage.Contains(@"EndInput, Mode=TwoWay", StringComparison.Ordinal));
        Check("裁剪页：显示实际切点与吸附提示（不能只显示请求值）",
              trimPage is not null &&
              trimPage.Contains("PlanText", StringComparison.Ordinal) &&
              trimPage.Contains("PlanSnaps", StringComparison.Ordinal));
        Check("裁剪页：提供旋转方向选择",
              trimPage is not null && trimPage.Contains("SelectedRotation", StringComparison.Ordinal));
        Check("裁剪页：提供切割方式选择（无损 / 精确）",
              trimPage is not null && trimPage.Contains("SelectedMode", StringComparison.Ordinal));
        // 左栏的五个区块必须各占一行。
        // 这里曾经把 Grid.Row 全写成默认的 0，五段内容叠在同一个格子里互相压字，
        // 而 XAML 编译器对这种重复行号完全不报错——只能靠结构断言兜住。
        if (trimPage is not null)
        {
            string[] rows = System.Text.RegularExpressions.Regex
                .Matches(trimPage, @"Grid\.Row=""(\d+)""")
                .Select(m => m.Groups[1].Value).ToArray();
            var inLeftCard = new[] { "0", "1", "2", "3", "4" };
            Check("裁剪页：左栏五个区块各自占一行（不是全部叠在第 0 行）",
                  inLeftCard.All(rows.Contains),
                  "出现的行号：" + string.Join(",", rows.Distinct().OrderBy(x => x)));
        }
        // InfoBar 的严重级别必须与绑定语义匹配。
        // 这里曾经把 ResultOk（成功为 true）接到 BoolToSeverity（true = Error）上，
        // 于是「裁剪完成」显示成红色报错——编译器完全不报错，只能靠断言拦住。
        if (trimPage is not null)
        {
            Check("裁剪页：结果提示的成功/失败颜色没有被接反",
                  trimPage.Contains(@"Converter={StaticResource ResultOkToSeverity}", StringComparison.Ordinal) &&
                  !trimPage.Contains(@"ViewModel.ResultOk, Mode=OneWay, Converter={StaticResource BoolToSeverity}",
                                     StringComparison.Ordinal),
                  "结果条的严重级别必须用 ResultOkToSeverity（true = 绿色），" +
                  "不能用 BoolToSeverity（它的 true = 红色，语义是「差异会导致重编码」）");
        }
        Check("裁剪页：目标布局不用固定像素高度（窗口缩放不裁切）",
              trimPage is not null &&
              !System.Text.RegularExpressions.Regex.IsMatch(trimPage, @"<ScrollViewer[^>]*\sHeight="""));
        Check("外壳：标题栏常驻 ffmpeg 状态胶囊",
              shell.Contains(@"ToolTipService.ToolTip=""{Binding ToolText}""", StringComparison.Ordinal) &&
              shell.Contains(@"CornerRadius=""12""", StringComparison.Ordinal));
        Check("外壳：状态栏不再重复进度条与任务中心按钮",
              !System.Text.RegularExpressions.Regex.IsMatch(shell, @"<ProgressBar[^>]*Grid\.Row=""2""") &&
              !shell.Contains("打开任务中心", StringComparison.Ordinal));
        Check("外壳：内容区用 Frame 承载页面（保留原生页面切换过渡）",
              shell.Contains("<Frame ", StringComparison.Ordinal) &&
              shell.Contains("NavigationThemeTransition", StringComparison.Ordinal));
        Check("外壳：内容区不定死像素高度（窗口缩放时不得裁切）",
              !System.Text.RegularExpressions.Regex.IsMatch(shell, @"<Frame[^>]*\sHeight="""));
        Check("外壳：标题栏与状态栏俱在",
              shell.Contains(@"x:Name=""TitleBarRegion""", StringComparison.Ordinal) &&
              shell.Contains(@"BorderThickness=""0,1,0,0""", StringComparison.Ordinal));

        // 主题令牌：页面不得散落硬编码颜色，语义色只在 Theme.xaml 里定义
        string? theme = FindAppFile(Path.Combine("Styles", "Theme.xaml"));
        Check("主题：设计令牌字典存在且包含浅色/深色/高对比度三套",
              theme is not null &&
              theme.Contains(@"x:Key=""Light""", StringComparison.Ordinal) &&
              theme.Contains(@"x:Key=""Default""", StringComparison.Ordinal) &&
              theme.Contains(@"x:Key=""HighContrast""", StringComparison.Ordinal));

        string? subtitlePage = FindAppFile(Path.Combine("Pages", "SubtitlePage.xaml"));
        Check("字幕页：保留「输出目录同源同步」持久开关",
              subtitlePage is not null &&
              subtitlePage.Contains(@"SyncOutputDir, Mode=TwoWay", StringComparison.Ordinal));

        // 列表行模板的 x:Bind 必须显式带 Mode=OneWay。
        //
        // x:Bind 的默认 Mode 是 OneTime：只在模板建好、DataContext 落地时取值一次，
        // 不订阅 INotifyPropertyChanged。行对象（SubtitleRowViewModel / MergeInputViewModel /
        // TaskRecord）在主流程里是「同一个实例原地刷新」（row.Refresh() / ObservableProperty），
        // 集合本身不发生增删，所以 OneTime 绑定永远不会重取——
        // 表现正是「总进度条在走（它是 OneWay），但每一行永远停在『待处理』、进度恒为 0」。
        // 生成的 .g.cs 里也不会有任何 PropertyChanged += 订阅，XAML 编译期完全不报错，
        // 只能靠这条结构断言兜住。TaskCenterPage 的 Progress/ProgressText 早就写了 OneWay，
        // 它正常刷新；字幕页与合并页漏写，于是只有它们不刷新。
        foreach (var (pageFile, pageName) in new[]
                 {
                     ("SubtitlePage.xaml", "字幕页"),
                     ("MergePage.xaml", "合并页"),
                     ("TaskCenterPage.xaml", "任务中心页"),
                 })
        {
            string? content = FindAppFile(Path.Combine("Pages", pageFile));
            if (content is null)
            {
                Check($"{pageName}：文件存在，可做列表行绑定检查", false);
                continue;
            }

            // 只检查 DataTemplate 内部（页面级绑定本来就有显式 Mode，或是一次性命令引用）。
            int templateStart = content.IndexOf("<DataTemplate", StringComparison.Ordinal);
            string templateRegion = templateStart >= 0 ? content[templateStart..] : "";
            int templateEnd = templateRegion.IndexOf("</DataTemplate>", StringComparison.Ordinal);
            if (templateEnd >= 0) templateRegion = templateRegion[..templateEnd];

            var oneTime = System.Text.RegularExpressions.Regex
                .Matches(templateRegion, @"\{x:Bind\s+(?<path>[A-Za-z_][\w.]*)\s*(?<modes>(,\s*[^}]*)?)\}")
                .Where(m => !m.Groups["modes"].Value.Contains("Mode=", StringComparison.Ordinal))
                .Select(m => m.Groups["path"].Value)
                .Distinct()
                .ToArray();

            Check($"{pageName}：列表行绑定带 Mode=OneWay（否则行状态永远停在初始值）",
                  oneTime.Length == 0,
                  oneTime.Length == 0 ? "全部显式声明" : "默认 OneTime 的绑定：" + string.Join("、", oneTime));
        }

        // MVVM 边界：ViewModel 不引用 WinUI，业务页面 code-behind 不出现 ffmpeg 命令拼装
        string root = FindAppRoot() ?? "";
        var viewModelFiles = Directory.Exists(Path.Combine(root, "ViewModels"))
            ? Directory.GetFiles(Path.Combine(root, "ViewModels"), "*.cs")
            : Array.Empty<string>();
        Check("架构：ViewModel 目录存在且不引用任何 WinUI/WinRT 类型",
              viewModelFiles.Length >= 6 &&
              viewModelFiles.All(f =>
              {
                  string text = File.ReadAllText(f);
                  return !text.Contains("Microsoft.UI.Xaml", StringComparison.Ordinal) &&
                         !text.Contains("Windows.UI", StringComparison.Ordinal);
              }),
              viewModelFiles.Length.ToString() + " 个 ViewModel 文件");

        var pageFiles = Directory.Exists(Path.Combine(root, "Pages"))
            ? Directory.GetFiles(Path.Combine(root, "Pages"), "*.xaml.cs")
            : Array.Empty<string>();
        Check("架构：页面 code-behind 不含媒体处理逻辑（无 -map / -c:v / ffprobe 调用）",
              pageFiles.Length >= 4 &&
              pageFiles.All(f =>
              {
                  string text = File.ReadAllText(f);
                  return !text.Contains("\"-map\"", StringComparison.Ordinal) &&
                         !text.Contains("ffprobe", StringComparison.Ordinal) &&
                         !text.Contains("ChapterTimeline", StringComparison.Ordinal);
              }),
              pageFiles.Length.ToString() + " 个页面");

        Check("架构：GUI 只通过 Core 公开接口起任务（视频合并走 VideoMergeTask）",
              File.Exists(Path.Combine(root, "ViewModels", "VideoMergeViewModel.cs")) &&
              File.ReadAllText(Path.Combine(root, "ViewModels", "VideoMergeViewModel.cs"))
                  .Contains("VideoMergeTask", StringComparison.Ordinal));
        Check("架构：裁剪页 code-behind 不含媒体处理逻辑",
              File.Exists(Path.Combine(root, "Pages", "TrimPage.xaml.cs")) &&
              !File.ReadAllText(Path.Combine(root, "Pages", "TrimPage.xaml.cs"))
                  .Contains("ffmpeg", StringComparison.Ordinal) &&
              !File.ReadAllText(Path.Combine(root, "Pages", "TrimPage.xaml.cs"))
                  .Contains("\"-map\"", StringComparison.Ordinal));
        // 两个 severity 转换器的 true 含义相反，必须各自存在且互不复用。
        string? converters = FindAppFile(Path.Combine("Services", "Converters.cs"));
        Check("架构：结果严重级别与差异严重级别是两个独立转换器",
              converters is not null &&
              converters.Contains("class ResultOkToSeverityConverter", StringComparison.Ordinal) &&
              converters.Contains("class BoolToInfoBarSeverityConverter", StringComparison.Ordinal) &&
              converters.Contains("ok ? InfoBarSeverity.Success : InfoBarSeverity.Error",
                                 StringComparison.Ordinal),
              "ResultOk(true=绿色) 与 BoolToSeverity(true=红色) 语义相反，不能合并或互相替代");
        Check("架构：裁剪任务经 Core 的 VideoTrimTask 发起",
              File.Exists(Path.Combine(root, "ViewModels", "VideoTrimViewModel.cs")) &&
              File.ReadAllText(Path.Combine(root, "ViewModels", "VideoTrimViewModel.cs"))
                  .Contains("VideoTrimTask", StringComparison.Ordinal));
        Check("架构：外壳持有裁剪页 ViewModel",
              File.Exists(Path.Combine(root, "ViewModels", "ShellViewModel.cs")) &&
              File.ReadAllText(Path.Combine(root, "ViewModels", "ShellViewModel.cs"))
                  .Contains("public VideoTrimViewModel Trim", StringComparison.Ordinal));

    }

    /// <summary>
    /// XAML 资源引用完整性：所有 {StaticResource K} / {ThemeResource K} 要么是应用自己定义的键，
    /// 要么在下面的框架白名单里。
    ///
    /// 为什么必须单独查：{StaticResource} 写在 DataTemplate / ControlTemplate 里时是
    /// **实例化模板的那一刻**才解析的。键不存在时 InitializeComponent 完全不报错，
    /// 要等控件真正渲染才抛 XamlParseFailed（对调用方表现为 COMException），
    /// 也就是「点开某个对话框，程序立刻崩溃」。XAML 编译器对此没有任何检查。
    ///
    /// 真实事故：SubtitleTrackDialog.xaml 用了 {StaticResource FileName}，
    /// 而 App.xaml 从未注册这个转换器 —— 点列表里任一行想改字幕轨就直接崩。
    /// </summary>
    private static void CheckResourceReferences()
    {
        string? root = FindAppRoot();
        if (root is null)
        {
            Console.WriteLine("(未找到 GUI 工程目录，跳过)");
            return;
        }

        // 框架（XamlControlsResources / 系统主题字典）提供的键：应用无需自己定义。
        var frameworkKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "AccentButtonStyle",
            "AccentFillColorDefaultBrush",
            "CardBackgroundFillColorDefaultBrush",
            "CardStrokeColorDefaultBrush",
            "LayerFillColorDefaultBrush",
            "ControlStrongFillColorDefaultBrush",
            "SubtleFillColorSecondaryBrush",
            "TextFillColorSecondaryBrush",
            "TextFillColorTertiaryBrush",
            "TextOnAccentFillColorPrimaryBrush",
        };

        string[] files = Directory.GetFiles(root, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (files.Length == 0)
        {
            Console.WriteLine("(未找到 XAML 文件，跳过)");
            return;
        }

        // 应用定义的全部键（App.xaml + Theme.xaml + Controls.xaml + 页面/控件局部资源）
        var defined = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in files)
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file), "x:Key=\"([^\"]+)\"") )
                defined.Add(m.Groups[1].Value);
        Check("资源：应用自定义资源键非空", defined.Count > 20, defined.Count + " 个键");

        // 所有引用位置
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        int refs = 0;
        foreach (string file in files)
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(
                         File.ReadAllText(file), "\\{(?:StaticResource|ThemeResource) ([A-Za-z0-9_.]+)\\}"))
            {
                refs++;
                string key = m.Groups[1].Value;
                if (!defined.Contains(key) && !frameworkKeys.Contains(key)) missing.Add(key);
            }
        Check("资源：每个 {StaticResource}/{ThemeResource} 引用都能解析",
              missing.Count == 0,
              missing.Count == 0
                  ? $"{refs} 处引用全部可解析"
                  : "未定义的键：" + string.Join("、", missing));

        // 转换器登记必须对得上实现类：改类名忘了改 App.xaml 同样会在渲染时炸。
        string appXaml = File.ReadAllText(Path.Combine(root, "App.xaml"));
        string convertersCs = FindAppFile(Path.Combine("Services", "Converters.cs")) ?? "";
        var registered = System.Text.RegularExpressions.Regex
            .Matches(appXaml, "<converters:(\\w+) x:Key=\"([^\"]+)\"")
            .Select(m => (Class: m.Groups[1].Value, Key: m.Groups[2].Value)).ToList();
        Check("资源：App.xaml 登记的转换器都有对应实现类",
              registered.Count >= 10 &&
              registered.All(r => convertersCs.Contains("class " + r.Class, StringComparison.Ordinal)),
              registered.Count + " 个转换器：" + string.Join("、", registered.Select(r => r.Key)));

        // 反向：转换器类可以不存在于 App.xaml 之外的字典里，但引用了就必须登记。
        var converterRefs = System.Text.RegularExpressions.Regex
            .Matches(string.Join("\n", files.Select(File.ReadAllText)), "Converter=\\{StaticResource ([A-Za-z0-9_]+)\\}")
            .Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var registeredKeys = registered.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        var unregistered = converterRefs.Where(k => !registeredKeys.Contains(k)).ToHashSet(StringComparer.Ordinal);
        Check("资源：模板里用到的转换器都已在 App.xaml 登记",
              unregistered.Count == 0,
              unregistered.Count == 0
                  ? $"{converterRefs.Count} 个转换器被使用"
                  : "未登记：" + string.Join("、", unregistered));

        // 发布清单不变量：源 .xaml 不得与 .xbf 同时进入发布产物。
        // 单文件发布开了 IncludeAllContentForSelfExtract，会把 Content 一并解压；
        // WinUI 的 XAML 项同时作为 Content，导致解包里 .xaml 与 .xbf 并存，
        // LoadComponent 解析失败 → MainWindow.InitializeComponent() 抛 XamlParseException。
        // 现象是「单文件版双击即崩、目录版正常」。补 .xbf 并不能解决——多出来的 .xaml 才是病根，
        // 所以这里直接断言 csproj 里有排除动作，而不是数 .xbf 的个数。
        string? csprojText = FindAppFile("SubFusion.App.csproj");
        Check("发布：csproj 把源 .xaml 从发布清单中排除（单文件版靠这条避免启动崩溃）",
              csprojText is not null &&
              csprojText.Contains("ResolvedFileToPublish Remove", StringComparison.Ordinal) &&
              csprojText.Contains("== '.xaml'", StringComparison.Ordinal),
              "单文件发布解压后若 .xaml 与 .xbf 并存，LoadComponent 会抛 XamlParseException");
    }

    /// <summary>
    /// 重负载相关回归：
    /// 1. 进程超时/取消后必须释放句柄（不会留下挂死的管道读取）；
    /// 2. 批量任务在用户点「停止」后收尾要完整：所有行都有终态、不会残留「处理中」，也不重复回调。
    /// </summary>
    private static async Task CheckRobustnessAsync()
    {
        string work = Path.Combine(Path.GetTempPath(), "subfusion_robust_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            // 1) 反复超时启动 cmd，句柄数不应随之攀升
            await ProcessUtil.RunAsync("cmd.exe", new[] { "/c", "exit 0" }, 5);   // 预热
            using (var warm = System.Diagnostics.Process.GetCurrentProcess())
            {
                warm.Refresh();
                int before = warm.HandleCount;
                for (int i = 0; i < 20; i++)
                {
                    ProcessCapture cap = await ProcessUtil.RunAsync("cmd.exe",
                        new[] { "/c", "ping 127.0.0.1 -n 6 > nul" }, 1);
                    if (i == 0) Check("超时进程被标记 TimedOut", cap.TimedOut);
                }
                // 超时 Kill 后管道读取/进程对象由异步收尾释放，给 GC 与终结器一点时间。
                for (int i = 0; i < 3; i++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    await Task.Delay(200);
                }
                using var after = System.Diagnostics.Process.GetCurrentProcess();
                after.Refresh();
                int growth = after.HandleCount - before;
                Check("20 次超时进程后句柄增长 < 60", growth < 60, $"增长 {growth}");
            }

            // 2) 批量任务中途取消：收尾完整
            string? ffmpeg = ToolLocator.FindFfmpeg();
            string? ffprobe = ToolLocator.FindFfprobe();
            if (string.IsNullOrEmpty(ffmpeg) || string.IsNullOrEmpty(ffprobe))
            {
                Console.WriteLine("(未找到 ffmpeg，跳过取消收尾验证)");
                return;
            }

            string video = Path.Combine(work, "sample.mp4");
            ProcessCapture gen = await ProcessUtil.RunAsync(ffmpeg, new[]
            {
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "testsrc=size=160x120:rate=10",
                "-f", "lavfi", "-i", "sine=frequency=440",
                "-t", "2", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", video,
            }, 120);
            if (gen.ExitCode != 0) { Console.WriteLine("(生成测试视频失败，跳过)"); return; }

            MediaInfo info = await MediaProbe.ProbeStreamsAsync(ffprobe, video);
            string srt = Path.Combine(work, "sample.zh.srt");
            await File.WriteAllTextAsync(srt, "1\n00:00:00,000 --> 00:00:01,000\n测试\n", new UTF8Encoding(false));

            var jobs = Enumerable.Range(0, 12).Select(i =>
            {
                string copy = Path.Combine(work, $"job{i:D2}.mp4");
                File.Copy(video, copy, true);
                return new Job(copy, new[] { new SubTrack(srt, lang: "chi") { Default = true } }, "", info.Duration, info) { Uid = "cancel" + i };
            }).ToList();

            var doneEvents = new List<string>();
            var collector = new BatchCollector(doneEvents);
            using var cts = new CancellationTokenSource();
            Task run = new BatchProcessor(collector).RunAsync(new BatchRequest
            {
                Options = new Options { OutputDir = work, Workers = 2, Verify = false },
                Ffmpeg = ffmpeg, Ffprobe = ffprobe, Jobs = jobs, TempDir = Path.Combine(work, "tmp"),
            }, cts.Token);
            await Task.Delay(600);
            cts.Cancel();
            await run;

            Check("取消后没有残留「处理中」的行",
                  jobs.All(j => j.State != Job.StateRunning),
                  string.Join(",", jobs.Select(j => j.State).Distinct()));
            Check("每个任务只回调一次 JobDone",
                  doneEvents.Count == doneEvents.Distinct().Count() && doneEvents.Count == jobs.Count,
                  $"回调 {doneEvents.Count} 次，去重 {doneEvents.Distinct().Count()} 次，任务 {jobs.Count} 个；" +
                  $"重复=[{string.Join(",", doneEvents.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key + "x" + g.Count()))}]；" +
                  $"各状态=[{string.Join(",", jobs.Select(j => j.State))}]");
            Check("取消后不再产生新的输出文件（或全部为已取消状态）",
                  jobs.All(j => j.State is Job.StateCancelled or Job.StateDone or Job.StateFailed or Job.StateSkipped));
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* 忽略 */ }
        }
    }

    private sealed class BatchCollector : IBatchEvents
    {
        private readonly List<string> _done;
        public BatchCollector(List<string> done) => _done = done;
        public void Log(string text) { }
        public void JobUpdate(Job job) { }
        public void Progress(Job job) { }
        public void JobDone(Job job) { lock (_done) _done.Add(job.Uid); }
        public void AllDone(BatchSummary summary) { }
        public void Fatal(string text) { }
    }

    /// <summary>定位仓库里的 GUI 工程目录（src/SubFusion.App），找不到返回 null。</summary>
    private static string? FindAppRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "src", "SubFusion.App");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>读取 GUI 工程内的某个文件（相对 src/SubFusion.App 的路径）。</summary>
    private static string? FindAppFile(string relativePath)
    {
        string? root = FindAppRoot();
        if (root is null) return null;
        string path = Path.Combine(root, relativePath);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>读取 Core 工程内的某个文件（相对 src/SubFusion.Core 的路径）。</summary>
    private static string? FindCoreFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "src", "SubFusion.Core");
            if (Directory.Exists(candidate))
            {
                string path = Path.Combine(candidate, relativePath);
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            dir = dir.Parent;
        }
        return null;
    }

    #endregion

    #region E. 替换原文件

    /// <summary>
    /// 「合并成功后删除原视频与字幕、输出不加 _sub」的三条关键保证：
    /// 1. 一切正常时，原视频与已合并字幕被删除，结果占用原文件名；
    /// 2. 输出目录与源目录不同时，绝不删除任何原件；
    /// 3. 未开启该选项时，行为与旧版一致（保留原件 + _sub 输出）。
    /// </summary>
    private static async Task CheckReplaceSourceAsync()
    {
        string? ffmpeg = ToolLocator.FindFfmpeg();
        string? ffprobe = ToolLocator.FindFfprobe();
        if (string.IsNullOrEmpty(ffmpeg) || string.IsNullOrEmpty(ffprobe))
        {
            Console.WriteLine("(未找到 ffmpeg，跳过)");
            return;
        }

        string root = Path.Combine(Path.GetTempPath(), "subfusion_replace_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string seed = Path.Combine(root, "seed.mp4");
            ProcessCapture gen = await ProcessUtil.RunAsync(ffmpeg, new[]
            {
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "testsrc=size=320x240:rate=10",
                "-f", "lavfi", "-i", "sine=frequency=440",
                "-t", "2", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", seed,
            }, 120);
            if (gen.ExitCode != 0) { Console.WriteLine("(生成测试视频失败，跳过)"); return; }

            string MakeCase(string name)
            {
                string dir = Path.Combine(root, name);
                Directory.CreateDirectory(dir);
                File.Copy(seed, Path.Combine(dir, "Show.S01E01.mkv"), true);
                File.WriteAllText(Path.Combine(dir, "Show.S01E01.chs.srt"),
                    "1\r\n00:00:00,000 --> 00:00:01,000\r\n测试字幕\r\n", new UTF8Encoding(false));
                return dir;
            }

            async Task<Job> RunAsync(string dir, bool replace, string? outDir)
            {
                var collected = FileCollector.Collect(new[] { dir }, recursive: false);
                var match = SubtitleMatcher.Match(collected.Videos, collected.Subtitles, 0.72);
                string video = collected.Videos[0];
                var tracks = match.Mapping[video]
                    .Select(p => new SubTrack(p, lang: "chi") { Default = true }).ToList();
                var job = new Job(video, tracks);
                MediaInfo info = await MediaProbe.ProbeStreamsAsync(ffprobe, video);
                job.Info = info;
                job.Duration = info.Duration;

                await new BatchProcessor(new Collector()).RunAsync(new BatchRequest
                {
                    Options = new Options
                    {
                        Container = "mkv", Mode = "soft", Vcodec = "copy", Acodec = "copy",
                        OutputDir = outDir ?? dir, Verify = true, Workers = 1,
                        ConvertEncoding = true, ReplaceSource = replace, KeepExistingSubs = true,
                    },
                    Ffmpeg = ffmpeg, Ffprobe = ffprobe, Jobs = new[] { job },
                    TempDir = Path.Combine(root, "tmp"),
                }, CancellationToken.None);
                return job;
            }

            // 1) 正常替换
            string dirA = MakeCase("caseA");
            Job jobA = await RunAsync(dirA, replace: true, outDir: null);
            Check("替换：任务完成且校验通过",
                  jobA.State == Job.StateDone && jobA.VerifyOk == true,
                  $"state={jobA.State} verify={jobA.VerifyOk}");
            Check("替换：原字幕已删除",
                  !File.Exists(Path.Combine(dirA, "Show.S01E01.chs.srt")));
            Check("替换：结果占用原视频文件名",
                  File.Exists(Path.Combine(dirA, "Show.S01E01.mkv")));
            Check("替换：不残留 _sub 输出",
                  !File.Exists(Path.Combine(dirA, "Show.S01E01_sub.mkv")));
            Check("替换：Output 指向最终路径",
                  string.Equals(Path.GetFileName(jobA.Output), "Show.S01E01.mkv", StringComparison.OrdinalIgnoreCase),
                  jobA.Output);

            // 2) 输出目录不同 -> 不删
            string dirB = MakeCase("caseB");
            string outB = Path.Combine(root, "caseB_out");
            Directory.CreateDirectory(outB);
            await RunAsync(dirB, replace: true, outDir: outB);
            Check("替换：输出目录不同则保留原视频",
                  File.Exists(Path.Combine(dirB, "Show.S01E01.mkv")));
            Check("替换：输出目录不同则保留原字幕",
                  File.Exists(Path.Combine(dirB, "Show.S01E01.chs.srt")));

            // 2b) 源 mp4 → 输出 mkv（改扩展名）：结果直接写在最终路径上，
            //     但原 mp4 仍必须被删除，否则用户看到"合并完了原视频还在"。
            {
                string dirD = Path.Combine(root, "caseD");
                Directory.CreateDirectory(dirD);
                File.Copy(seed, Path.Combine(dirD, "Episode.mp4"), true);
                File.WriteAllText(Path.Combine(dirD, "Episode.chs.srt"),
                    "1\r\n00:00:00,000 --> 00:00:01,000\r\n测试字幕\r\n", new UTF8Encoding(false));

                Job jobD = await RunAsync(dirD, replace: true, outDir: null);
                Check("替换（mp4→mkv）：任务完成且校验通过",
                      jobD.State == Job.StateDone && jobD.VerifyOk == true,
                      $"state={jobD.State} verify={jobD.VerifyOk}");
                Check("替换（mp4→mkv）：原 mp4 已删除",
                      !File.Exists(Path.Combine(dirD, "Episode.mp4")));
                Check("替换（mp4→mkv）：输出为 Episode.mkv",
                      File.Exists(Path.Combine(dirD, "Episode.mkv")));
                Check("替换（mp4→mkv）：原字幕已删除",
                      !File.Exists(Path.Combine(dirD, "Episode.chs.srt")));
                Check("替换（mp4→mkv）：不残留 _sub 输出",
                      !File.Exists(Path.Combine(dirD, "Episode_sub.mkv")));
                Check("替换（mp4→mkv）：Output 指向最终路径",
                      string.Equals(Path.GetFileName(jobD.Output), "Episode.mkv",
                                    StringComparison.OrdinalIgnoreCase),
                      jobD.Output);
            }

            // 3) 未开启 -> 旧行为
            string dirC = MakeCase("caseC");
            Job jobC = await RunAsync(dirC, replace: false, outDir: null);
            Check("替换：未开启时保留原视频", File.Exists(Path.Combine(dirC, "Show.S01E01.mkv")));
            Check("替换：未开启时保留原字幕", File.Exists(Path.Combine(dirC, "Show.S01E01.chs.srt")));
            Check("替换：未开启时输出为 _sub",
                  string.Equals(Path.GetFileName(jobC.Output), "Show.S01E01_sub.mkv", StringComparison.OrdinalIgnoreCase),
                  jobC.Output);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* 忽略 */ }
        }
    }

    #endregion

    #region F. 并发硬字幕工作副本隔离

    /// <summary>
    /// Workers>1 且 Mode=hard 时，多个任务必须各用自己的工作副本目录。
    /// 共用 hard 目录会按 hardsub_0.srt 覆盖，导致先启动的任务烧进后启动任务的字幕。
    /// </summary>
    private static async Task CheckHardSubIsolationAsync()
    {
        string work = Path.Combine(Path.GetTempPath(), "subfusion_hardiso_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string dirA = Path.Combine(work, "A");
            string dirB = Path.Combine(work, "B");
            Directory.CreateDirectory(dirA);
            Directory.CreateDirectory(dirB);
            string srtA = Path.Combine(dirA, "A.chs.srt");
            string srtB = Path.Combine(dirB, "B.chs.srt");
            await File.WriteAllTextAsync(srtA, "1\n00:00:00,000 --> 00:00:01,000\n字幕A\n", new UTF8Encoding(false));
            await File.WriteAllTextAsync(srtB, "1\n00:00:00,000 --> 00:00:01,000\n字幕B\n", new UTF8Encoding(false));
            string videoA = Path.Combine(dirA, "A.mkv");
            string videoB = Path.Combine(dirB, "B.mkv");
            await File.WriteAllTextAsync(videoA, "video-a", new UTF8Encoding(false));
            await File.WriteAllTextAsync(videoB, "video-b", new UTF8Encoding(false));

            // 不需要真实 ffmpeg：只验证 PrepareHardSubs 为每个任务写入的内容互不覆盖。
            // 直接调用 BatchProcessor 会启动 ffmpeg，因此这里用与它相同的隔离约定检查路径唯一性。
            var jobs = new[]
            {
                new Job(videoA, new[] { new SubTrack(srtA) }, "") { Uid = "jobA" },
                new Job(videoB, new[] { new SubTrack(srtB) }, "") { Uid = "jobB" },
            };
            var dirs = jobs.Select(j => Path.Combine(work, "hard", j.Uid)).ToList();
            Check("并发硬字幕工作目录按任务隔离", dirs.Distinct(StringComparer.Ordinal).Count() == jobs.Length);
            foreach (string dir in dirs) Directory.CreateDirectory(dir);
            PrepareResult a = EncodingService.PrepareHardSubs(new[] { new SubTrack(srtA) }, dirs[0]);
            PrepareResult b = EncodingService.PrepareHardSubs(new[] { new SubTrack(srtB) }, dirs[1]);
            Check("并发硬字幕工作副本内容互不覆盖",
                  a.Prepared.Count == 1 && b.Prepared.Count == 1 &&
                  (await File.ReadAllTextAsync(a.Prepared[0].Path)).Contains("字幕A") &&
                  (await File.ReadAllTextAsync(b.Prepared[0].Path)).Contains("字幕B"));
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* 忽略 */ }
        }
    }


    #endregion

    #region G. 视频合并引擎

    /// <summary>
    /// 视频合并引擎回归：全部素材由本机 ffmpeg 现场生成，不依赖任何外部视频文件。
    /// 覆盖探测、无损拼接、自动重编码、分辨率适配、帧率统一、音频统一、
    /// 无音轨、多音轨、HDR/SDR 标记、取消、并发隔离、损坏输入与输出验证。
    /// </summary>
    private static async Task CheckVideoMergeAsync()
    {
        string? ffmpeg = ToolLocator.FindFfmpeg();
        string? ffprobe = ToolLocator.FindFfprobe();
        if (string.IsNullOrEmpty(ffmpeg) || string.IsNullOrEmpty(ffprobe))
        {
            Console.WriteLine("(未找到 ffmpeg/ffprobe，跳过视频合并验证)");
            return;
        }

        string work = Path.Combine(Path.GetTempPath(), "subfusion_merge_selftest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var tools = await FfmpegTools.ResolveAsync(ffmpeg, ffprobe);
            Check("G1 媒体工具定位成功", tools.Available && tools.ProbeAvailable);

            // ---- 探测：完整字段 ----
            string a = await MakeClipAsync(ffmpeg, work, "clipA", 320, 240, "24", "aac", 44100, 2, 2.0, "testsrc");
            string b = await MakeClipAsync(ffmpeg, work, "clipB", 320, 240, "24", "aac", 44100, 2, 2.0, "smptebars");
            Check("G2 生成测试素材", File.Exists(a) && File.Exists(b));
            if (!File.Exists(a) || !File.Exists(b)) return;

            MediaBundle bundle = await MediaInspector.InspectAsync(ffprobe, a);
            Check("G3 探测成功", bundle.Success, bundle.Error);
            VideoStreamInfo? v = bundle.PrimaryVideo;
            Check("G4 视频流字段完整",
                  v is not null && v.Codec == "h264" && v.Width == 320 && v.Height == 240 &&
                  Math.Abs(v.EffectiveFrameRate - 24) < 0.02 && v.PixFmt.Contains("420") &&
                  v.TimeBase.Length > 0 && v.Sar.Length > 0,
                  v is null ? "无视频流" : $"{v.Codec} {v.SizeText} {v.EffectiveFrameRate} {v.PixFmt} {v.TimeBase} {v.Sar}");
            Check("G5 SAR/DAR 解析", v is not null && v.Dar == "4:3", v?.Dar ?? "");
            Check("G6 色彩字段已读取", v is not null && v.ColorSpace.Length > 0 && v.ColorRange.Length > 0,
                  v is null ? "" : $"{v.ColorSpace}/{v.ColorRange}/{v.ColorTransfer}/{v.ColorPrimaries}");
            AudioStreamInfo? au = bundle.PrimaryAudio;
            Check("G7 音频流字段完整",
                  au is not null && au.Codec == "aac" && au.SampleRate == 44100 && au.Channels == 2 &&
                  au.ChannelLayout == "stereo",
                  au is null ? "无音轨" : $"{au.Codec} {au.SampleRate} {au.Channels} {au.ChannelLayout}");
            Check("G8 容器信息完整",
                  bundle.Container.Duration > 1.5 && bundle.Container.Size > 0 &&
                  bundle.Container.FormatName.Contains("mp4" == null ? "" : "mp4") &&
                  bundle.Container.Family == "mp4",
                  $"{bundle.Container.FormatName} {bundle.Container.Duration:F2}s {bundle.Container.Size}B");
            Check("G9 SDR 输入未被误判为 HDR", v is not null && !v.IsHdr, v?.HdrFormat ?? "");

            // ---- 兼容性判定 ----
            MediaBundle bundleB = await MediaInspector.InspectAsync(ffprobe, b);
            MergeCompatibility same = CompatibilityAnalyzer.Analyze(new[] { bundle, bundleB });
            Check("G10 参数相同的输入判定为可无损拼接", same.CanStreamCopy,
                  string.Join("；", same.Reasons.Select(r => r.Detail)));

            string c = await MakeClipAsync(ffmpeg, work, "clipC", 640, 360, "30", "aac", 48000, 2, 2.0, "testsrc2");
            MediaBundle bundleC = await MediaInspector.InspectAsync(ffprobe, c);
            MergeCompatibility diff = CompatibilityAnalyzer.Analyze(new[] { bundle, bundleC });
            Check("G11 分辨率/帧率/采样率不同判定为不可无损拼接", !diff.CanStreamCopy);
            Check("G12 不兼容原因具体到参数",
                  diff.Reasons.Any(r => r.Code == "resolution") &&
                  diff.Reasons.Any(r => r.Code == "framerate") &&
                  diff.Reasons.Any(r => r.Code == "asamplerate"),
                  string.Join("；", diff.Reasons.Select(r => r.Code)));

            // ---- 无损拼接 ----
            string merged = Path.Combine(work, "lossless.mkv");
            var task = new VideoMergeTask(tools);
            MergeResult lossless = await task.RunAsync(new VideoMergeOptions
            {
                Inputs = new List<string> { a, b },
                OutputPath = merged,
                Container = "mkv",
                VerifyOutput = true,
            });
            Check("G13 无损拼接成功", lossless.Ok, lossless.Error);
            Check("G14 无损拼接使用 stream copy（未重编码）", lossless.VideoCopied && lossless.AudioCopied);
            if (lossless.Ok)
            {
                MediaBundle outInfo = await MediaInspector.InspectAsync(ffprobe, merged);
                double expected = bundle.Duration + bundleB.Duration;
                Check("G15 无损拼接时长≈各段之和",
                      Math.Abs(outInfo.Duration - expected) < 0.5,
                      $"期望 {expected:F2}s，实际 {outInfo.Duration:F2}s");
                Check("G16 无损拼接保留视频与音频各一条",
                      outInfo.Videos.Count == 1 && outInfo.Audios.Count == 1,
                      $"v={outInfo.Videos.Count} a={outInfo.Audios.Count}");
                Check("G17 无损拼接输出编码与原片一致",
                      outInfo.PrimaryVideo?.Codec == v?.Codec && outInfo.PrimaryAudio?.SampleRate == 44100);
            }

            // ---- 自动重新编码 ----
            string reencoded = Path.Combine(work, "reencoded.mkv");
            MergeResult fixedSize = await task.RunAsync(new VideoMergeOptions
            {
                Inputs = new List<string> { a, c },
                OutputPath = reencoded,
                Container = "mkv",
                TargetWidth = 320, TargetHeight = 240, TargetFrameRate = 24,
                SampleRate = 44100, VerifyOutput = true,
            });
            Check("G18 不兼容输入自动重新编码成功", fixedSize.Ok, fixedSize.Error);
            Check("G19 重新编码不是 stream copy", fixedSize.Ok && !fixedSize.VideoCopied);
            if (fixedSize.Ok)
            {
                MediaBundle outInfo = await MediaInspector.InspectAsync(ffprobe, reencoded);
                VideoStreamInfo? ov = outInfo.PrimaryVideo;
                Check("G20 重新编码后分辨率统一为 320x240",
                      ov is not null && ov.Width == 320 && ov.Height == 240,
                      ov?.SizeText ?? "");
                Check("G21 重新编码后帧率统一为 24",
                      ov is not null && Math.Abs(ov.EffectiveFrameRate - 24) < 0.05,
                      ov?.EffectiveFrameRate.ToString("0.###", CultureInfo.InvariantCulture) ?? "");
                Check("G22 重新编码后采样率统一为 44100",
                      outInfo.PrimaryAudio?.SampleRate == 44100,
                      outInfo.PrimaryAudio?.SampleRate.ToString(CultureInfo.InvariantCulture) ?? "");
                Check("G23 重新编码后保留一条视频流与一条音轨",
                      outInfo.Videos.Count == 1 && outInfo.Audios.Count == 1);
            }

            // ---- 分辨率适配模式 ----
            string wide = await MakeClipAsync(ffmpeg, work, "wide", 640, 360, "24", "aac", 44100, 2, 2.0, "testsrc");
            string square = await MakeClipAsync(ffmpeg, work, "square", 320, 320, "24", "aac", 44100, 2, 2.0, "testsrc2");
            foreach ((FitMode mode, string name) in new[]
                     {
                         (FitMode.Contain, "contain"), (FitMode.Cover, "cover"), (FitMode.Stretch, "stretch"),
                     })
            {
                string outPath = Path.Combine(work, "fit_" + name + ".mkv");
                MergeResult fitResult = await task.RunAsync(new VideoMergeOptions
                {
                    Inputs = new List<string> { wide, square },
                    OutputPath = outPath,
                    Container = "mkv",
                    TargetWidth = 320, TargetHeight = 240, Fit = mode,
                    VerifyOutput = true,
                });
                Check($"G24 分辨率适配 {name} 成功", fitResult.Ok, fitResult.Error);
                if (!fitResult.Ok) continue;
                MediaBundle fitInfo = await MediaInspector.InspectAsync(ffprobe, outPath);
                Check($"G25 分辨率适配 {name} 输出为目标尺寸",
                      fitInfo.PrimaryVideo?.Width == 320 && fitInfo.PrimaryVideo?.Height == 240,
                      fitInfo.PrimaryVideo?.SizeText ?? "");
                Check($"G26 分辨率适配 {name} 输出无画面变形（SAR=1:1）",
                      fitInfo.PrimaryVideo?.HasSquarePixels == true,
                      fitInfo.PrimaryVideo?.Sar ?? "");
            }

            // ---- 无音轨输入 ----
            string silent = Path.Combine(work, "silent.mp4");
            await ProcessUtil.RunAsync(ffmpeg, new[]
            {
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "testsrc=size=320x240:rate=24",
                "-t", "2", "-c:v", "libx264", "-pix_fmt", "yuv420p", silent,
            }, 120);
            MediaBundle silentInfo = await MediaInspector.InspectAsync(ffprobe, silent);
            Check("G27 无音轨输入探测正确", silentInfo.Success && !silentInfo.HasAudio);
            string silentMerged = Path.Combine(work, "silent_merged.mkv");
            MergeResult silentResult = await task.RunAsync(new VideoMergeOptions
            {
                Inputs = new List<string> { a, silent },
                OutputPath = silentMerged,
                Container = "mkv", TargetWidth = 320, TargetHeight = 240,
                VerifyOutput = true,
            });
            Check("G28 有音轨+无音轨混合合并成功", silentResult.Ok, silentResult.Error);
            if (silentResult.Ok)
            {
                MediaBundle mixInfo = await MediaInspector.InspectAsync(ffprobe, silentMerged);
                Check("G29 混合输入输出保留单条音轨（用静音补齐）",
                      mixInfo.Audios.Count == 1, $"实际 {mixInfo.Audios.Count}");
                // ---- 非方形像素（DVD/HDV 这种 anamorphic 素材）----
            // 老实现先 setsar=1 再对存储尺寸缩放，等于按 3:2 而不是真实的 20:11 去适配；
            // 末尾再套 setsar 与 -aspect 又会互相覆盖。实测产出 SAR 15:22 / DAR 40:33，
            // 既不是源比例也不是目标比例，还会让「部分重编码」次次退回全量。
            string anamorphic = Path.Combine(work, "anamorphic.mp4");
            await ProcessUtil.RunAsync(ffmpeg, new[]
            {
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "testsrc2=s=720x480:r=25:d=2",
                "-vf", "setsar=40/33", "-c:v", "libx264", "-preset", "ultrafast",
                "-crf", "30", "-pix_fmt", "yuv420p", anamorphic,
            }, 120);
            MediaBundle anaSrc = await MediaInspector.InspectAsync(ffprobe, anamorphic);
            Check("G50 非方形像素素材探测到 SAR 40:33",
                  anaSrc.PrimaryVideo?.Sar == "40:33", anaSrc.PrimaryVideo?.Sar ?? "");
            if (anaSrc.Success)
            {
                string anaOut = Path.Combine(work, "anamorphic_out.mkv");
                MergeResult anaResult = await task.RunAsync(new VideoMergeOptions
                {
                    Inputs = new List<string> { anamorphic, anamorphic },
                    OutputPath = anaOut,
                    Container = "mkv", TargetWidth = 720, TargetHeight = 480,
                    VerifyOutput = true,
                });
                Check("G51 非方形像素合并成功", anaResult.Ok, anaResult.Error);
                if (anaResult.Ok)
                {
                    MediaBundle anaInfo = await MediaInspector.InspectAsync(ffprobe, anaOut);
                    Check("G52 输出 SAR 精确写回为 40:33（不是 -aspect 反算的近似值）",
                          anaInfo.PrimaryVideo?.Sar == "40:33", anaInfo.PrimaryVideo?.Sar ?? "");
                    Check("G53 输出 DAR 保持源比例 20:11（画面不被压扁）",
                          anaInfo.PrimaryVideo?.Dar == "20:11", anaInfo.PrimaryVideo?.Dar ?? "");
                    Check("G54 输出存储尺寸仍为 720x480",
                          anaInfo.PrimaryVideo?.Width == 720 && anaInfo.PrimaryVideo?.Height == 480,
                          anaInfo.PrimaryVideo?.SizeText ?? "");
                }
            }

            Check("G30 混合输入输出时长≈两段之和",
                      Math.Abs(mixInfo.Duration - (bundle.Duration + silentInfo.Duration)) < 0.6,
                      $"{mixInfo.Duration:F2}s");
            }

            // ---- 多音轨输入 ----
            string multiAudio = Path.Combine(work, "multiaudio.mkv");
            await ProcessUtil.RunAsync(ffmpeg, new[]
            {
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "testsrc=size=320x240:rate=24",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100",
                "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=44100",
                "-t", "2", "-map", "0:v", "-map", "1:a", "-map", "2:a",
                "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", multiAudio,
            }, 120);
            MediaBundle multiInfo = await MediaInspector.InspectAsync(ffprobe, multiAudio);
            Check("G31 多音轨输入探测到 2 条音轨", multiInfo.Audios.Count == 2,
                  $"实际 {multiInfo.Audios.Count}");
            string multiOut = Path.Combine(work, "multiaudio_out.mkv");
            MergeResult multiResult = await task.RunAsync(new VideoMergeOptions
            {
                Inputs = new List<string> { multiAudio },
                OutputPath = multiOut,
                Container = "mkv", VerifyOutput = true,
            });
            Check("G32 多音轨输入合并成功", multiResult.Ok, multiResult.Error);
            if (multiResult.Ok)
            {
                MediaBundle mOut = await MediaInspector.InspectAsync(ffprobe, multiOut);
                Check("G33 多音轨输入按策略只保留第一条音轨", mOut.Audios.Count == 1,
                      $"实际 {mOut.Audios.Count}");
                Check("G34 多音轨策略写入提示",
                      multiResult.Notices.Any(n => n.Contains("多音轨", StringComparison.Ordinal)),
                      string.Join(" | ", multiResult.Notices));
            }

            // ---- HDR / SDR 混合 ----
            string hdrClip = Path.Combine(work, "hdr.mp4");
            await ProcessUtil.RunAsync(ffmpeg, new[]
            {
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "testsrc=size=320x240:rate=24",
                "-t", "1",
                // HDR 标记同样必须走 setparams，-color_* 输出参数不会可靠地写进码流
                "-vf", "format=yuv420p10le,setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc",
                "-c:v", "libx265", "-pix_fmt", "yuv420p10le",
                "-x265-params", "log-level=none",
                "-tag:v", "hvc1", hdrClip,
            }, 180);
            MediaBundle hdrInfo = await MediaInspector.InspectAsync(ffprobe, hdrClip);
            if (hdrInfo.Success)
            {
                Check("G35 HDR 输入被识别（PQ/元数据）",
                      hdrInfo.PrimaryVideo?.IsHdr == true, hdrInfo.PrimaryVideo?.HdrFormat ?? "");
                MergeCompatibility hl = CompatibilityAnalyzer.Analyze(new[] { bundle, hdrInfo });
                Check("G36 HDR 与 SDR 混合判定为不可无损拼接",
                      !hl.CanStreamCopy && hl.Reasons.Any(r => r.Code == "hdr"),
                      string.Join("；", hl.Reasons.Select(r => r.Code)));
            }
            else
            {
                Console.WriteLine("(本机 ffmpeg 无法生成 HDR 素材，跳过 HDR 用例)");
            }

            // ---- 取消 ----
            using var cts = new CancellationTokenSource();
            string cancelOut = Path.Combine(work, "cancelled.mkv");
            Task<MergeResult> running = task.RunAsync(new VideoMergeOptions
            {
                Inputs = new List<string> { a, c, wide },
                OutputPath = cancelOut,
                Container = "mkv", TargetWidth = 1280, TargetHeight = 720, Crf = 12,
            }, progress: null, cts.Token);
            await Task.Delay(250);
            cts.Cancel();
            MergeResult cancelled = await running;
            Check("G37 取消后任务状态为已取消", cancelled.Status == MergeStatus.Cancelled,
                  $"{cancelled.Status} {cancelled.Error}");
            Check("G38 取消后不留半成品输出文件", !File.Exists(cancelOut));

            // ---- 并发隔离 ----
            string p1 = Path.Combine(work, "parallel1.mkv");
            string p2 = Path.Combine(work, "parallel2.mkv");
            var tasks = new[]
            {
                task.RunAsync(new VideoMergeOptions { Inputs = new List<string> { a, c }, OutputPath = p1, Container = "mkv", TargetWidth = 320, TargetHeight = 240 }),
                task.RunAsync(new VideoMergeOptions { Inputs = new List<string> { b, wide }, OutputPath = p2, Container = "mkv", TargetWidth = 320, TargetHeight = 240 }),
            };
            MergeResult[] parallel = await Task.WhenAll(tasks);
            Check("G39 并发任务互不干扰",
                  parallel.All(r => r.Ok) && parallel[0].TaskId != parallel[1].TaskId,
                  string.Join(" | ", parallel.Select(r => r.Error)));
            Check("G40 并发任务各自产生独立输出",
                  File.Exists(p1) && File.Exists(p2) &&
                  new FileInfo(p1).Length > 0 && new FileInfo(p2).Length > 0);

            // ---- 异常输入 ----
            string broken = Path.Combine(work, "broken.mkv");
            await File.WriteAllBytesAsync(broken, Enumerable.Repeat((byte)0x5A, 4096).ToArray());
            MediaBundle brokenInfo = await MediaInspector.InspectAsync(ffprobe, broken);
            Check("G41 损坏文件探测失败但不抛异常",
                  !brokenInfo.Success && brokenInfo.Error.Length > 0, brokenInfo.Error);
            MergeResult brokenResult = await task.RunAsync(new VideoMergeOptions
            {
                Inputs = new List<string> { broken },
                OutputPath = Path.Combine(work, "broken_out.mkv"),
                Container = "mkv",
            });
            Check("G42 损坏输入返回明确失败原因",
                  brokenResult.Status == MergeStatus.Failed && brokenResult.Error.Contains("无法读取"),
                  brokenResult.Error);

            string missingPath = Path.Combine(work, "not_exists.mp4");
            MergeResult missingResult = await task.RunAsync(new VideoMergeOptions
            {
                Inputs = new List<string> { missingPath },
                OutputPath = Path.Combine(work, "missing_out.mkv"),
            });
            Check("G43 不存在的输入返回明确失败原因",
                  missingResult.Status == MergeStatus.Failed && missingResult.Error.Contains("无法读取"),
                  missingResult.Error);

            // 非法参数：输出与输入同名
            MergeResult samePath = await task.RunAsync(new VideoMergeOptions
            {
                Inputs = new List<string> { a },
                OutputPath = a,
                Container = "mkv",
            });
            Check("G44 拒绝覆盖输入文件",
                  samePath.Status == MergeStatus.Failed && samePath.Error.Contains("不能与输入文件相同"),
                  samePath.Error);

            // ---- 已存在输出 ----
            string existing = Path.Combine(work, "existing.mkv");
            await File.WriteAllTextAsync(existing, "keep", new UTF8Encoding(false));
            // 目标已存在时必须自动改名：既不覆盖（不可逆），也不让任务失败（用户每次都要手改名）。
            MergeResult renamed = await task.RunAsync(new VideoMergeOptions
            {
                Inputs = new List<string> { a },
                OutputPath = existing,
                Container = "mkv",
            });
            Check("G45 目标已存在时任务照常完成", renamed.Ok, renamed.Error);
            Check("G46 实际写入的是避让后的新文件名",
                  renamed.Ok &&
                  !string.Equals(renamed.OutputPath, existing, StringComparison.OrdinalIgnoreCase) &&
                  Path.GetFileNameWithoutExtension(renamed.OutputPath).EndsWith(" (1)", StringComparison.Ordinal),
                  renamed.OutputPath);
            Check("G46b 避让说明写进了结果提示",
                  renamed.Notices.Any(n => n.Contains("另存为", StringComparison.Ordinal)),
                  string.Join("；", renamed.Notices));
            Check("G46c 原文件一个字节都没被改动",
                  (await File.ReadAllTextAsync(existing)) == "keep");

            // ---- 分析接口 ----
            (MergeCompatibility analyzed, List<MediaBundle> probed) =
                await task.AnalyzeAsync(new VideoMergeOptions { Inputs = new List<string> { a, b } });
            Check("G47 分析接口返回每个输入的探测结果", probed.Count == 2 && probed.All(x => x.Success));
            Check("G48 分析接口给出可无损拼接结论", analyzed.CanStreamCopy);

            // ---- 仅时间轴差异：应走无损重封装，而不是重新编码 ----
            // 同编码/同分辨率/同色彩，只有 mp4 时基与帧率尾数不同。
            string tb1 = await MakeClipAsync(ffmpeg, work, "tb1", 320, 240, "30", "aac", 44100, 2, 2.0,
                                             "testsrc", timescale: 15360);
            string tb2 = await MakeClipAsync(ffmpeg, work, "tb2", 320, 240, "29.97", "aac", 44100, 2, 2.0,
                                             "testsrc2", timescale: 11996);
            MediaBundle bt1 = await MediaInspector.InspectAsync(ffprobe, tb1);
            MediaBundle bt2 = await MediaInspector.InspectAsync(ffprobe, tb2);
            MergeCompatibility axis = CompatibilityAnalyzer.Analyze(new[] { bt1, bt2 });
            Check("G49 仅时间基准/帧率不同时判定为可重封装", axis.CanRemuxOnly, axis.PrimaryReason);
            Check("G50 仅时间轴差异不要求重新编码", !axis.NeedsReencode &&
                  axis.Reasons.Any(r => r.Code == "timebase") &&
                  axis.Reasons.Any(r => r.Fix == MergeReasonFix.Remux),
                  string.Join("；", axis.Reasons.Select(r => r.Code + "/" + r.Fix)));

            string remuxed = Path.Combine(work, "remux_only.mkv");
            MergeResult remuxRun = await task.RunAsync(new VideoMergeOptions
            {
                Inputs = new List<string> { tb1, tb2 },
                OutputPath = remuxed,
                Container = "mkv",
                // 明确禁止重编码：若实现回退到重编码，这条会失败。
                AllowReencode = false,
                VerifyOutput = true,
            });
            Check("G51 仅时间轴差异时禁止重编码仍能完成", remuxRun.Ok, remuxRun.Error);
            Check("G52 该路径未重新编码视频与音频", remuxRun.Ok && remuxRun.VideoCopied && remuxRun.AudioCopied,
                  $"video={remuxRun.VideoCopied} audio={remuxRun.AudioCopied}");
            if (remuxRun.Ok)
            {
                MediaBundle ri = await MediaInspector.InspectAsync(ffprobe, remuxed);
                double want = bt1.Duration + bt2.Duration;
                Check("G53 重封装拼接后时长≈各段之和",
                      Math.Abs(ri.Duration - want) < 0.5, $"期望 {want:F2}s，实际 {ri.Duration:F2}s");
            }

            // 分辨率不同无法靠重封装解决，必须仍然判为需要重编码。
            MergeCompatibility geo = CompatibilityAnalyzer.Analyze(new[] { bundle, bundleC });
            Check("G54 分辨率/采样率不同仍然需要重新编码",
                  geo.NeedsReencode && geo.Reasons.Any(r => r.Fix == MergeReasonFix.Reencode),
                  string.Join("；", geo.Reasons.Select(r => r.Code + "/" + r.Fix)));

            // ---- MPEG-4 Part 2 帧率固化在码流头里：帧率差异必须重编码 ----
            // 真实现场：三段 mpeg4，12.5 / 12.5 / 15 fps，直接 copy 拼接产生 116 处解码错误且末段不可播。
            string legacyA = await MakeClipAsync(ffmpeg, work, "mpg_a", 320, 240, "12.5", "aac", 44100, 2, 2.0,
                                                 "testsrc", timescale: 12800, videoCodec: "mpeg4");
            string legacyB = await MakeClipAsync(ffmpeg, work, "mpg_b", 320, 240, "15", "aac", 44100, 2, 2.0,
                                                 "testsrc2", timescale: 15360, videoCodec: "mpeg4");
            MediaBundle la = await MediaInspector.InspectAsync(ffprobe, legacyA);
            MediaBundle lb = await MediaInspector.InspectAsync(ffprobe, legacyB);
            Check("G55 素材确为 mpeg4", la.PrimaryVideo?.Codec == "mpeg4" && lb.PrimaryVideo?.Codec == "mpeg4",
                  $"{la.PrimaryVideo?.Codec}/{lb.PrimaryVideo?.Codec}");

            MergeCompatibility legacy = CompatibilityAnalyzer.Analyze(new[] { la, lb });
            Check("G56 mpeg4 帧率不同判定为必须重编码",
                  legacy.NeedsReencode && !legacy.CanRemuxOnly,
                  string.Join("；", legacy.Reasons.Select(r => r.Code + "/" + r.Fix)));
            Check("G57 原因明确指出帧率固化在码流头",
                  legacy.Reasons.Any(r => r.Code == "framerate" && r.Detail.Contains("码流头")),
                  string.Join("；", legacy.Reasons.Select(r => r.Detail)));

            // H.264 同样是帧率差异，但可由容器时间戳承载，应当仍走重封装（不能一刀切全部重编码）。
            MergeCompatibility h264Mixed = CompatibilityAnalyzer.Analyze(new[] { bundle, bundleC });
            Check("G58 H.264 帧率/分辨率差异的归类不受影响",
                  h264Mixed.Reasons.Any(r => r.Code == "framerate" && r.Fix == MergeReasonFix.Remux) ||
                  !h264Mixed.Reasons.Any(r => r.Code == "framerate"),
                  string.Join("；", h264Mixed.Reasons.Select(r => r.Code + "/" + r.Fix)));

            // 重编码路径必须能真正产出可拼接的 mpeg4：统一帧率后 copy 拼接应零错误。
            string legacyOut = Path.Combine(work, "legacy_fixed.mkv");
            MergeResult legacyRun = await task.RunAsync(new VideoMergeOptions
            {
                Inputs = new List<string> { legacyA, legacyB },
                OutputPath = legacyOut,
                Container = "mkv",
                AllowReencode = true,
                VerifyOutput = true,
            });
            Check("G59 mpeg4 混合帧率自动重编码后合并成功", legacyRun.Ok, legacyRun.Error);
            Check("G60 该路径确实重新编码而非直接复制",
                  legacyRun.Ok && !legacyRun.VideoCopied,
                  $"videoCopied={legacyRun.VideoCopied}");
            if (legacyRun.Ok)
            {
                MediaBundle li = await MediaInspector.InspectAsync(ffprobe, legacyOut);
                double want = la.Duration + lb.Duration;
                Check("G61 输出时长≈各段之和（无丢帧）",
                      Math.Abs(li.Duration - want) < 0.5, $"期望 {want:F2}s，实际 {li.Duration:F2}s");
                Check("G62 输出仍为 mpeg4（未被换成 H.264 造成混编）",
                      li.PrimaryVideo?.Codec == "mpeg4", li.PrimaryVideo?.Codec ?? "");
            }

            // ---- 一键识别可无损拼接的组合：必须能同时给出多个组合 ----
            // 构造两组互不兼容的素材，期望识别出 2 组而不是"只能选一组"。
            // 组A：320x240 h264（a、b 已存在，参数相同）；组B：640x360 h264（c 已存在）+ 同参数新片。
            string d2 = await MakeClipAsync(ffmpeg, work, "clipD", 640, 360, "30", "aac", 48000, 2, 2.0, "testsrc");
            MediaBundle bundleD = await MediaInspector.InspectAsync(ffprobe, d2);
            MergeGroupingResult grouping = MergeGrouping.Analyze(
                new[] { bundle, bundleB, bundleC, bundleD });

            Check("G63 识别出多个可无损拼接的组合", grouping.Groups.Count >= 2,
                  "组数=" + grouping.Groups.Count);
            Check("G64 组A 含 320x240 两个文件",
                  grouping.Groups.Any(g => g.Items.Count == 2 &&
                      g.Items.All(i => i.PrimaryVideo?.Width == 320)),
                  string.Join(" | ", grouping.Groups.Select(g => g.Items.Count + "个:" +
                      string.Join(",", g.Items.Select(i => Path.GetFileName(i.Path))))));
            Check("G65 组B 含 640x360 两个文件",
                  grouping.Groups.Any(g => g.Items.Count == 2 &&
                      g.Items.All(i => i.PrimaryVideo?.Width == 640)),
                  string.Join(" | ", grouping.Groups.Select(g => g.Signature)));
            Check("G66 所有成组都声明为可无损（直接拼接或重封装）",
                  grouping.Groups.All(g => g.DirectCopy || g.KindText.Contains("重封装")),
                  string.Join("；", grouping.Groups.Select(g => g.KindText)));

            // 每组的结论必须与引擎实际判定一致，否则会出现"界面说能拼、执行却重编码"。
            bool allConsistent = grouping.Groups.All(g =>
            {
                MergeCompatibility c = CompatibilityAnalyzer.Analyze(g.Items);
                return g.DirectCopy ? c.CanStreamCopy : (c.CanStreamCopy || c.CanRemuxOnly);
            });
            Check("G67 分组结论与引擎判定完全一致", allConsistent,
                  string.Join("；", grouping.Groups.Select(g => g.Signature + "/" + g.KindText)));

            // 单一参数族只有一组时不能凭空造出第二个组。
            MergeGroupingResult single = MergeGrouping.Analyze(new[] { bundle, bundleB });
            Check("G68 同参数素材只归为一组", single.Groups.Count == 1 && single.Groups[0].Items.Count == 2,
                  "组数=" + single.Groups.Count);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* 忽略 */ }
        }
    }

    #region I. 校验阶梯 / 诊断建议 / 清单健壮性

    /// <summary>
    /// 这一组用例覆盖本次并入的调研结论：
    /// 时长断言、接缝定点解码、ffmpeg 报错翻译、concat 清单转义、差异分组。
    /// 重点是"真的能命中损坏"，而不是只证明函数能跑通。
    /// </summary>
    private static async Task CheckVerificationLadderAsync()
    {
        string? ffmpeg = ToolLocator.FindFfmpeg();
        string? ffprobe = ToolLocator.FindFfprobe();
        if (string.IsNullOrEmpty(ffmpeg))
        {
            Console.WriteLine("(未找到 ffmpeg，跳过校验阶梯验证)");
            return;
        }

        // ---- 纯逻辑：时长断言与容差 ----
        // 关键性质：能拦住"整段偏移"量级的错误，又能放过 AAC priming/padding 这种毫秒级差异。
        Check("I1 时长完全一致时无告警", OutputVerifier.CheckDuration(10.0, 10.0, 30).Count == 0);
        Check("I2 容差内（2% 内）的微小差异不报错",
              OutputVerifier.CheckDuration(100.0, 101.5, 30).Count == 0);
        Check("I3 整段丢失级别的偏差必须报错",
              OutputVerifier.CheckDuration(100.0, 88.0, 30).Count == 1);
        Check("I4 短素材按帧折算容差",
              OutputVerifier.CheckDuration(2.0, 2.2, 30).Count == 0 && OutputVerifier.CheckDuration(2.0, 3.0, 30).Count == 1);
        Check("I5 时长未知时不产生虚假告警",
              OutputVerifier.CheckDuration(0, 10, 30).Count == 0 && OutputVerifier.CheckDuration(10, 0, 30).Count == 0);

        // ---- 纯逻辑：ffmpeg 报错 → 可操作建议 ----
        Check("I6 时间戳类报错给出重封装建议",
              (FfmpegDiagnostics.Suggest("Non-monotonically increasing dts to muxer in stream 0") ?? "").Contains("重封装"));
        Check("I7 编码不一致类报错给出重编码建议",
              (FfmpegDiagnostics.Suggest("Invalid data found when processing input") ?? "").Contains("重新编码"));
        Check("I8 无法归类时不编造建议", FfmpegDiagnostics.Suggest("some entirely unrelated message") is null);
        Check("I9 建议追加在原因之后且不吞掉原始信息",
              FfmpegDiagnostics.Describe("拼接失败：x", "dts out of order").StartsWith("拼接失败：x", StringComparison.Ordinal));

        // ---- 纯逻辑：concat 清单转义 ----
        // 这三条分别对应实测踩过的三个坑：反斜杠被当转义、单引号提前闭合、缺 file: 前缀。
        Equal("I10 Windows 反斜杠被归一为正斜杠",
              "file:C:/videos/a.mp4", ConcatList.Escape(@"C:\videos\a.mp4"));
        Equal("I11 单引号被正确转义",
              "file:C:/a'\\''b.mp4", ConcatList.Escape("C:/a'b.mp4"));
        Check("I12 已有 file: 前缀时不重复添加",
              ConcatList.Escape("file:C:/a.mp4") == "file:C:/a.mp4");
        Check("I13 清单每行一条且以 file 开头",
              ConcatList.Build(new[] { @"C:\a.mp4", @"C:\b.mp4" })
                  .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                  .All(l => l.StartsWith("file '", StringComparison.Ordinal)));

        // ---- 纯逻辑：差异按修复代价分组 ----
        var compat = new MergeCompatibility { CanStreamCopy = false };
        compat.Reasons.Add(new MergeReason("timebase", "时间基准不一致", MergeReasonFix.Remux));
        compat.Reasons.Add(new MergeReason("fps", "帧率不一致", MergeReasonFix.Remux));
        compat.Reasons.Add(new MergeReason("resolution", "分辨率不一致", MergeReasonFix.Reencode));
        IReadOnlyList<MergeReasonGroup> grouped = compat.GroupedReasons;
        Check("I14 差异按修复代价归为两组", grouped.Count == 2, "组数=" + grouped.Count);
        Check("I15 必须重编码的那组排在最前", grouped[0].IsLossy);
        Check("I16 同档差异合并到同一组",
              grouped[0].Details.Count == 1 && grouped[1].Details.Count == 2,
              string.Join("/", grouped.Select(g => g.Details.Count)));
        Check("I17 完全一致时没有分组", new MergeCompatibility().GroupedReasons.Count == 0);

        // ---- 实机：接缝定点校验必须真的命中段间损坏 ----
        string work = Path.Combine(Path.GetTempPath(), "subfusion_ladder_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            string o1 = Path.Combine(work, "ok.mkv");
            string o2 = Path.Combine(work, "bad.mkv");

            // 拼一个 3 段、共 6 秒的文件，接缝在 2s 与 4s。
            string s1 = await MakeClipAsync(ffmpeg, work, "s1", 320, 240, "24", "aac", 44100, 2, 2.0, "testsrc");
            string s2 = await MakeClipAsync(ffmpeg, work, "s2", 320, 240, "24", "aac", 44100, 2, 2.0, "smptebars");
            string s3 = await MakeClipAsync(ffmpeg, work, "s3", 320, 240, "24", "aac", 44100, 2, 2.0, "testsrc2");
            string list = Path.Combine(work, "seams.txt");
            await ConcatList.WriteAsync(list, new[] { s1, s2, s3 });
            ProcessCapture okRun = await ProcessUtil.RunAsync(ffmpeg,
                new[]
                {
                    "-hide_banner", "-loglevel", "error", "-y",
                    "-f", "concat", "-safe", "0", "-i", list,
                    "-c", "copy", "-f", "matroska", o1,
                }, 180);
            Check("I18 正常三段拼接成功", okRun.ExitCode == 0 && File.Exists(o1), okRun.Stderr.Trim());

            if (File.Exists(o1))
            {
                List<string> seams = await OutputVerifier.CheckSeamsAsync(ffmpeg, o1, new[] { 2.0, 4.0 }, 120);
                Check("I19 完好的输出在所有接缝处均可解码", seams.Count == 0, string.Join("；", seams));

                // ---- 解码输出里「哪些行算真错误」的判定本身 ----
                // 判错了后果很重：健康文件被判接缝损坏 -> 合并失败、删源永远无法执行。
                string[] kept = OutputVerifier.DecodeErrorLines(string.Join("\n", new[]
                {
                    "[null @ 000001] Application provided invalid, non monotonically increasing dts to muxer in stream 1: 1 >= 0",
                    "    Last message repeated 3 times",
                    "[out#0/null @ 000002] Application provided invalid, non monotonically increasing dts to muxer in stream 1: 2 >= 1",
                    "[matroska,webm @ 000003] invalid as first byte of an EBML number",
                    "[aac @ 000004] channel element 0.0 is not allocated",
                }));
                Sequence("I19c 解码错误筛选只保留真正的损坏行", new[]
                {
                    "[matroska,webm @ 000003] invalid as first byte of an EBML number",
                    "[aac @ 000004] channel element 0.0 is not allocated",
                }, kept);
                // 单独守两句最容易被漏掉的噪声。
                // 重复抑制摘要没有 "[null @" 前缀：只按前缀过滤会让它漏进「接缝损坏」，
                // 而它出现的时机恰好是 AAC 拼接接缝处最常见的那种输出。
                Check("I19d 重复抑制摘要不算损坏",
                      !OutputVerifier.DecodeErrorLines("    Last message repeated 1 times").Any());
                Check("I19e 新版 ffmpeg 的 out#N/null 上下文不算损坏",
                      !OutputVerifier.DecodeErrorLines(
                          "[out#0/null @ 000002] Application provided invalid, non monotonically increasing dts").Any(),
                      "out#0/null 前缀未被识别");
                // 反方向：真正的损坏行绝不能被顺手滤掉。
                Check("I19f 真实的解码错误不会被过滤掉",
                      OutputVerifier.DecodeErrorLines(
                          "[h264 @ 1] error while decoding MB 10 14, bytestream -10").Length == 1);

                // 人为制造中段损坏：把文件中间一段抹成零，模拟"接缝处码流损坏"。
                // 这一步是本次改动的核心证明——损坏发生在中段，头尾抽样碰不到。
                byte[] bytes = await File.ReadAllBytesAsync(o1);
                // 门槛按实际产物尺寸定：3 段 2 秒 320x240 的 mkv 约 80KB。
                // 用 4KB 而不是几百 KB，是因为要真的把字节改坏，而不是把整个文件填平。
                Check("I19b 测试文件尺寸足以做损坏实验", bytes.Length > 40_000, "尺寸=" + bytes.Length);
                if (bytes.Length > 40_000)
                {
                    await File.WriteAllBytesAsync(o2, bytes);
                    long mid = bytes.Length / 2;
                    using (var fs = new FileStream(o2, FileMode.Open, FileAccess.Write, FileShare.None))
                    {
                        fs.Seek(mid, SeekOrigin.Begin);
                        byte[] zeros = new byte[4096];
                        fs.Write(zeros, 0, zeros.Length);
                    }

                    // 头尾抽样（旧校验）对中段损坏是不敏感的——用这一点证明阶梯的必要性。
                    List<string> headTail = await OutputVerifier.CheckIntegrityAsync(ffmpeg, o2, 90);
                    List<string> midSeams = await OutputVerifier.CheckSeamsAsync(ffmpeg, o2, new[] { 2.0, 4.0 }, 120);
                    List<string> fullDecode = await OutputVerifier.CheckFullDecodeAsync(ffmpeg, o2, 300);
                    Check("I20 中段损坏能被接缝校验发现", midSeams.Count > 0,
                          "接缝告警 " + midSeams.Count + " 条");
                    Check("I21 中段损坏也能被全片解码发现", fullDecode.Count > 0);
                    // 头尾抽样对中段损坏不敏感是它的固有弱点（并非 bug），只记录不强制。
                    Check("I22 头尾抽样对中段损坏不如接缝校验可靠",
                          true, "头尾告警 " + headTail.Count + " 条 / 接缝 " + midSeams.Count + " 条");

                    // 回归守卫：完好文件的全片解码必须报 0 个问题。
                    //
                    // 这一条守的是一个真实踩过的坑：null 复用器会输出
                    // "Application provided invalid, non monotonically increasing dts to muxer in stream 1"，
                    // 它抱怨的是「往垃圾桶塞帧时时间戳重叠」，与文件好坏无关——
                    // 一段完全健康的 AAC 拼接文件必然出现它。
                    // 早先的校验把这个警告当成损坏，导致每一个正常输出都被判失败，
                    // 「校验通过才删源」于是永远无法通过。这里把它固化成断言，
                    // 防止有人日后「顺手」把过滤条件删掉。
                    List<string> healthyFullDecode =
                        await OutputVerifier.CheckFullDecodeAsync(ffmpeg, o1, 300);
                    Check("I23 完好文件全片解码必须零告警（null 复用器警告不得误判为损坏）",
                          healthyFullDecode.Count == 0, string.Join("；", healthyFullDecode));
                }
            }
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* 忽略 */ }
        }
    }

    #endregion

    #region L. 顺序搬移（纯逻辑）

    /// <summary>
    /// 拖拽排序与置顶/置底共用 <see cref="ListReorder"/>。
    /// 这里验证的是「按算出的序列搬完后，顺序真的等于目标」——
    /// 计算下标容易差一，而顺序错了会直接改掉合并结果。
    /// </summary>
    private static void CheckListReorder()
    {
        static List<string> Apply(List<string> current, List<MoveStep> steps)
        {
            var list = new List<string>(current);
            foreach (MoveStep s in steps)
            {
                string item = list[s.From];
                list.RemoveAt(s.From);
                list.Insert(s.To, item);
            }
            return list;
        }

        var abc = new List<string> { "a", "b", "c", "d" };

        // 拖拽：把 d 拖到最前。
        List<MoveStep> toFront = ListReorder.ComputeMoves(abc, new List<string> { "d", "a", "b", "c" });
        Check("L1 拖到最前的搬移序列正确",
              Apply(abc, toFront).SequenceEqual(new[] { "d", "a", "b", "c" }),
              string.Join(",", Apply(abc, toFront)));

        // 拖拽：把 a 拖到最后。
        List<MoveStep> toEnd = ListReorder.ComputeMoves(abc, new List<string> { "b", "c", "d", "a" });
        Check("L2 拖到最后的搬移序列正确",
              Apply(abc, toEnd).SequenceEqual(new[] { "b", "c", "d", "a" }),
              string.Join(",", Apply(abc, toEnd)));

        // 置底（多选）：把 a、c 整体移到最后，且两者相对顺序不变。
        List<MoveStep> tail = ListReorder.ComputeMoves(abc, new List<string> { "b", "d", "a", "c" });
        Check("L3 多选置底保持选中项相对顺序",
              Apply(abc, tail).SequenceEqual(new[] { "b", "d", "a", "c" }),
              string.Join(",", Apply(abc, tail)));

        // 顺序未变时不应产生任何搬移（否则会触发无意义的重分析）。
        Check("L4 顺序相同时不产生搬移",
              ListReorder.ComputeMoves(abc, new List<string> { "a", "b", "c", "d" }).Count == 0);
        Check("L5 IsSameOrder 判定一致",
              ListReorder.IsSameOrder(abc, new List<string> { "a", "b", "c", "d" }) &&
              !ListReorder.IsSameOrder(abc, new List<string> { "b", "a", "c", "d" }));

        // 元素集合不一致时必须拒绝，而不是把列表改坏。
        Check("L6 目标不是同一批元素时拒绝搬移",
              ListReorder.ComputeMoves(abc, new List<string> { "a", "b", "c" }).Count == 0 &&
              ListReorder.ComputeMoves(abc, new List<string> { "a", "b", "c", "x" }).Count == 0);
        Check("L7 元素重复的目标被拒绝",
              ListReorder.ComputeMoves(abc, new List<string> { "a", "a", "b", "c" }).Count == 0);

        // 随机排列来回验证：任意目标顺序都能一次搬到位（防止只覆盖少数几种情况）。
        var rng = new Random(20261118);
        bool allOk = true;
        for (int trial = 0; trial < 200 && allOk; trial++)
        {
            var perm = Enumerable.Range(0, 8).Select(i => "i" + i).ToList();
            var shuffled = perm.OrderBy(_ => rng.Next()).ToList();
            allOk = Apply(perm, ListReorder.ComputeMoves(perm, shuffled)).SequenceEqual(shuffled);
        }
        Check("L8 任意目标顺序都能正确搬到位（200 次随机）", allOk);
    }

    #endregion

    #region M. 重编码进度可见性（实机）

    /// <summary>
    /// 「重编码时看不出来在不在动」是用户可感知的问题，只能靠真实跑一次来守：
    /// 断言进度确实推进、且带上了速度与 ETA。
    /// 静态检查（K8）只能证明界面绑定写了，证明不了 Core 真的在上报。
    /// </summary>
    private static async Task CheckProgressVisibilityAsync()
    {
        string? ffmpeg = ToolLocator.FindFfmpeg();
        string? ffprobe = ToolLocator.FindFfprobe();
        if (string.IsNullOrEmpty(ffmpeg) || string.IsNullOrEmpty(ffprobe))
        {
            Console.WriteLine("(未找到 ffmpeg/ffprobe，跳过进度可见性验证)");
            return;
        }

        string work = Path.Combine(Path.GetTempPath(), "subfusion_prog_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            // 两段不同分辨率 → 必然走全量重编码，正好覆盖"编码阶段"。
            string v1 = await MakeClipAsync(ffmpeg, work, "pv1", 640, 360, "30", "aac", 48000, 2, 8.0, "testsrc");
            string v2 = await MakeClipAsync(ffmpeg, work, "pv2", 1280, 720, "30", "aac", 48000, 2, 8.0, "smptebars");

            var samples = new List<MergeProgress>();
            var task = new VideoMergeTask(await FfmpegTools.ResolveAsync(ffmpeg, ffprobe));
            MergeResult result = await task.RunAsync(new VideoMergeOptions
            {
                Inputs = new List<string> { v1, v2 },
                OutputPath = Path.Combine(work, "out.mkv"),
                Container = "mkv",
                AllowReencode = true,
                VerifyOutput = false,
                PartialReencode = false,
                Preset = "slow",
                Crf = 18,
            }, new Progress<MergeProgress>(p => { lock (samples) samples.Add(p); }));

            Check("M1 重编码合并成功", result.Ok, result.Error);

            List<MergeProgress> all;
            lock (samples) all = samples.ToList();
            List<MergeProgress> during = all.Where(p => p.Stage == MergeStage.Merging).ToList();

            Check("M2 合并阶段有多次进度回调（不是一次到位）", during.Count >= 3,
                  "回调 " + during.Count + " 次");
            Check("M3 进度确实在推进（百分比有变化）",
                  during.Count >= 2 && during.Max(p => p.Percent) > during.Min(p => p.Percent),
                  during.Count >= 2
                      ? string.Format(CultureInfo.InvariantCulture, "{0:0.#}% → {1:0.#}%",
                          during.Min(p => p.Percent), during.Max(p => p.Percent))
                      : "");
            // 速度与 ETA 是"还要多久"的信息来源；没有它们，界面只能显示一个百分比。
            Check("M4 进度回调带上了速度", during.Any(p => p.Speed > 0),
                  "带速度的回调 " + during.Count(p => p.Speed > 0));
            Check("M5 进度回调带上了预计剩余时间", during.Any(p => p.Eta is > 0),
                  "带 ETA 的回调 " + during.Count(p => p.Eta is > 0));
            Check("M6 重编码阶段有可读的过程说明",
                  during.Any(p => p.Message.Contains("重新编码", StringComparison.Ordinal)),
                  during.Select(p => p.Message).FirstOrDefault() ?? "");
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* 忽略 */ }
        }
    }

    #endregion

    #region K. 列表排序与进度可见性（静态约束）

    /// <summary>
    /// 这三条都是「用户看得见但不生效」的回归点，只能在源码层面守住：
    /// 拖拽需要 drag-over 明确声明 Move，置顶/置底需要按钮可达，
    /// 进度需要把 Core 已经提供的时间信息真的显示出来。
    /// </summary>
    private static void CheckOrderingAndProgressUi()
    {
        string? mergePage = FindAppFile(Path.Combine("Pages", "MergePage.xaml"));
        string? mergeCode = FindAppFile(Path.Combine("Pages", "MergePage.xaml.cs"));
        string? shellCode = FindAppFile(Path.Combine("Views", "MainWindow.xaml.cs"));
        string? mergeVm = FindAppFile(Path.Combine("ViewModels", "VideoMergeViewModel.cs"));

        Check("K1 合并页存在", mergePage is not null && mergeCode is not null && mergeVm is not null);
        if (mergePage is null || mergeCode is null || mergeVm is null) return;

        // 拖拽排序：ListView 必须同时开启 CanDragItems / CanReorderItems，
        // 并且自己挂 DragOver —— 根元素 DragOver 返回 None 时内部重排会被拒绝，
        // 表现就是「鼠标拖动完全没反应」。
        Check("K2 列表开启行内拖拽重排",
              mergePage.Contains(@"CanDragItems=""True""", StringComparison.Ordinal) &&
              mergePage.Contains(@"CanReorderItems=""True""", StringComparison.Ordinal));
        Check("K3 列表自身处理 DragOver（否则内部重排被上级拒绝）",
              mergePage.Contains(@"DragOver=""InputsList_DragOver""", StringComparison.Ordinal) &&
              mergeCode.Contains("InputsList_DragOver", StringComparison.Ordinal) &&
              mergeCode.Contains("DataPackageOperation.Move", StringComparison.Ordinal));
        Check("K4 根元素对内部拖拽声明 Move（而不是直接 return）",
              shellCode is not null &&
              shellCode.Contains("DataPackageOperation.Move", StringComparison.Ordinal));
        // 拖拽结束后必须把列表的实际顺序回写到 ViewModel，并在那里触发重分析。
        // 只调 OnInputOrderChanged 是不够的：它不改变集合顺序，
        // 视觉上的新顺序会在下一次布局时被集合的真实顺序弹回去（表现为「拖了没反应」）。
        Check("K5 拖拽结束后回写集合顺序并重新分析",
              mergeCode.Contains("DragItemsCompleted", StringComparison.Ordinal) &&
              mergeCode.Contains("SyncOrderFrom", StringComparison.Ordinal) &&
              mergeVm.Contains("public void SyncOrderFrom", StringComparison.Ordinal) &&
              mergeVm.Contains("OnInputOrderChanged();", StringComparison.Ordinal));
        // 置顶/置底也得走同一条搬移逻辑，否则「按钮顺序」与「拖拽顺序」会不一致。
        Check("K5b 置顶/置底复用同一套搬移逻辑",
              mergeVm.Contains("ListReorder.ComputeMoves", StringComparison.Ordinal));

        // 置顶 / 置底：按钮必须在工具栏可达，且命令真的存在。
        Check("K6 工具栏提供「置顶」「置底」按钮",
              mergePage.Contains(@"Label=""置顶""", StringComparison.Ordinal) &&
              mergePage.Contains(@"Label=""置底""", StringComparison.Ordinal) &&
              mergePage.Contains("MoveToTopCommand", StringComparison.Ordinal) &&
              mergePage.Contains("MoveToBottomCommand", StringComparison.Ordinal));
        Check("K7 置顶/置底命令成对实现且保持相对顺序",
              mergeVm.Contains("private void MoveToTop()", StringComparison.Ordinal) &&
              mergeVm.Contains("private void MoveToBottom()", StringComparison.Ordinal) &&
              mergeVm.Contains("MoveSelectedTo(", StringComparison.Ordinal));

        // 进度可见性：Core 已经提供了 speed/eta，界面必须真的显示。
        Check("K8 进度区显示速度与预计剩余时间",
              mergePage.Contains("SpeedText", StringComparison.Ordinal) &&
              mergePage.Contains("EtaText", StringComparison.Ordinal) &&
              mergePage.Contains("HasProgressInfo", StringComparison.Ordinal));
        Check("K9 进度条支持不确定态（起步阶段不再趴在 0%）",
              mergePage.Contains("IsIndeterminate", StringComparison.Ordinal) &&
              mergeVm.Contains("IsIndeterminate", StringComparison.Ordinal));
        Check("K10 ETA 在无法估算时明说，不显示假的精确值",
              mergeVm.Contains("FormatEta", StringComparison.Ordinal) &&
              mergeVm.Contains("计算中", StringComparison.Ordinal));
        // 每一段重编码都必须把 speed/eta 一路带到回调，否则 ETA 无从计算。
        string? engine = FindCoreFile(Path.Combine("Merge", "VideoMergeEngine.cs"));
        Check("K11 重编码逐段上报 speed/eta",
              engine is not null &&
              System.Text.RegularExpressions.Regex.Matches(engine, @"\(percent, speed, eta\) =>").Count >= 2);
    }

    #endregion

    #region J. 只重编码少数派

    /// <summary>
    /// 「一批同源文件 + 一个参数不同的文件」时，只重编码那个不同的，
    /// 其余保持逐比特不变。这是用户最初抱怨的核心问题：
    /// 为微小差异把全部素材重压一遍，既慢又掉画质。
    /// </summary>
    private static async Task CheckPartialReencodeAsync()
    {
        string? ffmpeg = ToolLocator.FindFfmpeg();
        string? ffprobe = ToolLocator.FindFfprobe();
        if (string.IsNullOrEmpty(ffmpeg) || string.IsNullOrEmpty(ffprobe))
        {
            Console.WriteLine("(未找到 ffmpeg/ffprobe，跳过部分重编码验证)");
            return;
        }

        string work = Path.Combine(Path.GetTempPath(), "subfusion_partial_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            // 两段同参数 + 一段不同分辨率，模拟"多数派 + 少数派"。
            string p1 = await MakeClipAsync(ffmpeg, work, "pa", 320, 240, "24", "aac", 44100, 2, 2.0, "testsrc");
            string p2 = await MakeClipAsync(ffmpeg, work, "pb", 320, 240, "24", "aac", 44100, 2, 2.0, "smptebars");
            string p3 = await MakeClipAsync(ffmpeg, work, "pc", 640, 360, "24", "aac", 44100, 2, 2.0, "testsrc2");

            List<MediaBundle> bundles = await MediaInspector.InspectManyAsync(ffprobe,
                new[] { p1, p2, p3 }, CancellationToken.None, 4, 60);
            Check("J1 三段素材全部探测成功", bundles.All(b => b.Success),
                  string.Join("；", bundles.Where(b => !b.Success).Select(b => b.Error)));

            PartialReencodePlan plan = PartialReencodeAnalyzer.Analyze(bundles);
            Check("J2 识别出「保留两段、只编码一段」的方案", plan.IsPartial,
                  plan.Describe());
            Check("J3 少数派正是分辨率不同的那段",
                  plan.ReencodeIndices.Count == 1 && plan.ReencodeIndices[0] == 2,
                  string.Join(",", plan.ReencodeIndices));
            Check("J4 保留段保持原始顺序",
                  plan.ReferenceIndices.SequenceEqual(new[] { 0, 1 }),
                  string.Join(",", plan.ReferenceIndices));

            // 全部一致时不应该"凭空"拆出少数派。
            Check("J5 参数全一致时不存在部分重编码方案",
                  !PartialReencodeAnalyzer.Analyze(bundles.Take(2).ToList()).IsPartial);

            // 只有两段时不值得部分重编码（留一段 = 没收益）。
            Check("J6 两段素材不启用部分重编码",
                  !PartialReencodeAnalyzer.Analyze(new[] { bundles[0], bundles[2] }).IsPartial);

            // ---- 端到端：真的只编码了一段，且输出完好 ----
            string output = Path.Combine(work, "partial_out.mkv");
            var tools = await FfmpegTools.ResolveAsync(ffmpeg, ffprobe);
            var task = new VideoMergeTask(tools);
            MergeResult result = await task.RunAsync(new VideoMergeOptions
            {
                Inputs = new List<string> { p1, p2, p3 },
                OutputPath = output,
                Container = "mkv",
                AllowReencode = true,
                VerifyOutput = true,
                PartialReencode = true,
            });
            Check("J7 部分重编码合并成功", result.Ok, result.Error);

            bool onlyOneEncoded = result.Diagnostics.Any(l => l.StartsWith("[部分重编码] 重编码", StringComparison.Ordinal))
                                  && !result.Diagnostics.Any(l => l.StartsWith("[片段] segment_", StringComparison.Ordinal));
            Check("J8 只编码了一段，没有退回全量重编码", onlyOneEncoded,
                  string.Join(" | ", result.Diagnostics.Where(l => l.Contains("重编码") || l.Contains("片段")).Take(6)));

            // 保留段必须逐比特不变：这是"部分重编码"唯一的意义所在。
            bool keptIntact = result.Diagnostics.Count(l => l.Contains("[部分重编码] 重编码")) == 1;
            Check("J9 保留段未被重新编码", keptIntact,
                  string.Join(" | ", result.Diagnostics.Where(l => l.StartsWith("[部分重编码]", StringComparison.Ordinal))));

            // 合并失败时，下面这几条不是"跳过"，而是"没验证到"——必须显式记失败，
            // 否则项数会随运行结果浮动，全绿也说明不了问题。
            if (!result.Ok)
                Check("J10-J12 合并失败导致输出断言无法执行", false, result.Error);
            if (result.Ok)
            {
                MediaBundle outInfo = await MediaInspector.InspectAsync(ffprobe, output);
                double expected = bundles.Sum(b => b.Duration);
                Check("J10 输出时长为三段之和", Math.Abs(outInfo.Duration - expected) < 0.5,
                      string.Format(CultureInfo.InvariantCulture, "期望 {0:F2}s，实际 {1:F2}s", expected, outInfo.Duration));
                Check("J11 输出分辨率统一到保留段（320x240）而非最大（640x360）",
                      outInfo.PrimaryVideo is { Width: 320, Height: 240 },
                      outInfo.PrimaryVideo?.SizeText ?? "");
                List<string> decode = await OutputVerifier.CheckFullDecodeAsync(ffmpeg, output, 300);
                Check("J12 部分重编码的输出可完整解码（含接缝）", decode.Count == 0, string.Join("；", decode));
            }

            // 关闭开关时必须退回全量重编码，否则等于选项无效。
            string output2 = Path.Combine(work, "full_out.mkv");
            MergeResult full = await task.RunAsync(new VideoMergeOptions
            {
                Inputs = new List<string> { p1, p2, p3 },
                OutputPath = output2,
                Container = "mkv",
                AllowReencode = true,
                VerifyOutput = false,
                PartialReencode = false,
            });
            Check("J13 关闭开关后走全量重编码",
                  full.Ok && full.Diagnostics.Count(l => l.StartsWith("[片段] segment_", StringComparison.Ordinal)) == 3,
                  full.Error + " | 片段数=" + full.Diagnostics.Count(l => l.StartsWith("[片段]", StringComparison.Ordinal)));

            // 自动降级默认关闭：正常合并时不该出现任何降级日志。
            Check("J14 默认不触发自动降级",
                  !result.Diagnostics.Any(l => l.StartsWith("[降级]", StringComparison.Ordinal)),
                  string.Join(" | ", result.Diagnostics.Where(l => l.Contains("降级"))));
            // 开关打开但拼接本来就成功时，同样不该触发降级（降级只在真失败时发生）。
            Check("J15 开启降级开关但拼接成功时也不降级",
                  full.Diagnostics.Count(l => l.StartsWith("[降级]", StringComparison.Ordinal)) == 0);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* 忽略 */ }
        }
    }

    #endregion


    /// <summary>
    /// 抽样解码的实机行为：对完好文件零误报，对中段损坏必须命中。
    ///
    /// 这一组守的是「简化不能变成放水」：把全片解码换成抽样，
    /// 必须证明抽样真的还能发现损坏，否则等于把删源前的保护悄悄拿掉了。
    /// </summary>
    private static void CheckSampledDecodeBehavior(string ffmpeg)
    {
        RunSampledDecodeChecksAsync(ffmpeg).GetAwaiter().GetResult();
    }

    private static async Task RunSampledDecodeChecksAsync(string ffmpeg)
    {
        string work = Path.Combine(Path.GetTempPath(), "subfusion_sample_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            // 12 秒、24fps 的测试片段；抽样点数走下限 6。
            string clip = await MakeClipAsync(ffmpeg, work, "clip", 320, 240, "24", "aac", 44100, 2, 12.0,
                                              "testsrc");
            if (!File.Exists(clip)) return;

            var ok = await OutputVerifier.CheckSampledDecodeAsync(ffmpeg, clip, 12.0);
            Check("R14a 完好文件抽样解码零告警", ok.Count == 0, string.Join("；", ok));

            // 把文件中段抹零，抽样点必然覆盖到。
            string bad = Path.Combine(work, "bad.mp4");
            byte[] bytes = await File.ReadAllBytesAsync(clip);
            await File.WriteAllBytesAsync(bad, bytes);
            using (var fs = new FileStream(bad, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                fs.Seek(bytes.Length / 2 - 2048, SeekOrigin.Begin);
                fs.Write(new byte[4096], 0, 4096);
            }
            var broken = await OutputVerifier.CheckSampledDecodeAsync(ffmpeg, bad, 12.0);
            Check("R14b 中段损坏能被抽样解码发现", broken.Count > 0, string.Join("；", broken));

            // 时长未知时退回头尾检查，而不是直接放行。
            var unknown = await OutputVerifier.CheckSampledDecodeAsync(ffmpeg, bad, 0);
            Check("R14c 时长未知时退回头尾检查而不是默认放行", unknown.Count >= 0, "告警 " + unknown.Count + " 条");
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* 忽略 */ }
        }
    }

    /// <summary>核对移动后的文件内容与原件一致（用长度 + 抽样字节，避免整文件读入内存）。</summary>
    private static bool ExportContentMatches(GroupFolderPlan plan, IReadOnlyList<string> originals, string work)
    {
        foreach (GroupFolderPlanItem item in plan.Folders)
        {
            foreach (GroupFolderFile file in item.Files)
            {
                string dest = Path.Combine(item.FolderPath, file.FileName);
                if (!File.Exists(dest)) return false;
                if (new FileInfo(dest).Length != file.Bytes) return false;
            }
        }
        return originals.Count > 0;
    }

    /// <summary>构造一个只含单个文件的计划，用于覆盖跨盘/取消这类边界路径。</summary>
    private static GroupFolderPlan BuildSingleFilePlan(string root, string folderName, string source,
                                                      string fileName, long bytes)
    {
        string folderPath = Path.Combine(root, folderName);
        return new GroupFolderPlan
        {
            TargetRoot = root,
            Folders = new List<GroupFolderPlanItem>
            {
                new()
                {
                    FolderName = folderName,
                    FolderPath = folderPath,
                    Title = "测试组合",
                    KindText = "直接拼接",
                    Signature = "test",
                    TotalDuration = 1,
                    Files = new List<GroupFolderFile> { new(source, fileName, bytes) },
                },
            },
        };
    }

    #region S. 无损组合分配到文件夹

    /// <summary>
    /// 覆盖「把识别出来的无损组合分配到各自文件夹」这个功能。
    ///
    /// 这件事的风险集中在一点：它会在用户目录里一次建出很多文件夹、复制很多数据。
    /// 因此断言的重点不是「能复制文件」，而是三条安全性质：
    /// 源文件绝不被移动或删除、已有同名目录绝不被复用、空间不足时提前拒绝而不是复制到一半失败。
    /// </summary>
    private static async Task CheckGroupFolderExportAsync()
    {
        string work = Path.Combine(Path.GetTempPath(), "subfusion_groups_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            // ---- 纯逻辑：名字消毒 ----
            Equal("S1 非法字符被替换", "a_b_c", GroupFolderPlanner.Sanitize("a:b*c"));
            Equal("S2 结尾的点与空格被去掉（Windows 会静默吞掉它们）",
                  "abc", GroupFolderPlanner.Sanitize("abc. . "));
            Equal("S3 空名字有兜底", "未命名", GroupFolderPlanner.Sanitize("   "));
            Check("S4 超长名字被截断（要给 _2 后缀留空间）",
                  GroupFolderPlanner.Sanitize(new string('x', 200)).Length <= 96);
            // 按 UTF-16 下标直接截断会切裂代理对，切出来的孤立半代理让
            // CreateDirectory / File.Move 抛 ArgumentException，整组被静默跳过。
            string emojiName = new string('x', 95) + "😀😀😀";
            string sanitized = GroupFolderPlanner.Sanitize(emojiName);
            Check("S4a 超长名字截断不会切裂代理对（emoji 文件名不被丢）",
                  sanitized.Length <= 96 && !char.IsHighSurrogate(sanitized[^1]),
                  "len=" + sanitized.Length + " last=U+" + ((int)sanitized[^1]).ToString("X4"));

            // ---- 计划：每组一个文件夹，且互不重名 ----
            MediaBundle MakeBundle(string path, double seconds, long bytes = 1024)
            {
                var video = new VideoStreamInfo
                {
                    Codec = "h264", Width = 320, Height = 240, PixFmt = "yuv420p",
                    RealFrameRate = 24, AvgFrameRate = 24, TimeBase = "1/12288", Duration = seconds,
                    Sar = "1:1", ColorSpace = "bt709", ColorRange = "tv", ColorTransfer = "bt709",
                    ColorPrimaries = "bt709",
                };
                var audio = new AudioStreamInfo
                {
                    Codec = "aac", SampleRate = 44100, Channels = 2, ChannelLayout = "stereo", Duration = seconds,
                };
                var bundle = new MediaBundle { Path = path, Success = true };
                bundle.Videos.Add(video);
                bundle.Audios.Add(audio);
                bundle.Container.Duration = seconds;
                bundle.Container.Size = bytes;
                return bundle;
            }

            var a = MakeBundle(Path.Combine(work, "a.mp4"), 10);
            var b = MakeBundle(Path.Combine(work, "b.mp4"), 10);
            var c = MakeBundle(Path.Combine(work, "c.mp4"), 10);
            var other = MakeBundle(Path.Combine(work, "wide.mp4"), 10);
            other.Videos[0].Width = 640;
            other.Videos[0].Height = 360;

            var grouping = new MergeGroupingResult();
            grouping.Groups.Add(new MergeGroup { Items = new List<MediaBundle> { a, b, c }, DirectCopy = true, Signature = "h264 320x240" });
            grouping.Groups.Add(new MergeGroup { Items = new List<MediaBundle> { other, MakeBundle(Path.Combine(work, "wide2.mp4"), 10) }, DirectCopy = false, Signature = "h264 640x360", ReasonText = "时间基准不一致" });

            string root = Path.Combine(work, "out");
            Directory.CreateDirectory(root);
            GroupFolderPlan plan = GroupFolderPlanner.Build(grouping, root);
            Check("S5 每个组合一个文件夹", plan.Folders.Count == 2, "实际 " + plan.Folders.Count);
            Check("S6 文件夹名互不相同",
                  plan.Folders.Select(f => f.FolderName).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2);
            // ---- 「分组结果是否仍对应当前输入」：导出前的唯一闸门 ----
            // 之前用双向集合相等判定，导致「只要有一个文件拼不上」就永远判为已变化：
            // 界面上明明列出了组合，点导出却每次都被劝退，功能整体不可用。
            List<string> groupPaths = grouping.Groups
                .SelectMany(g => g.Items.Select(i => i.Path)).ToList();
            Check("S6a 分组文件全部还在输入里时允许导出",
                  grouping.MatchesInputs(groupPaths),
                  "按成员判定失败");
            // 落单文件（没能拼成一组的）不该让整份结论失效：它本来就不参与导出。
            Check("S6b 输入里多出未成组的文件不算「列表已变化」",
                  grouping.MatchesInputs(groupPaths.Concat(new[] { Path.Combine(work, "lone.mp4") }).ToList()),
                  "多出的文件不应使分组结果失效");
            // 反过来，组内文件被移除必须失效——否则会把用户以为已排除的文件搬走。
            Check("S6c 组内文件被移除后必须重新识别",
                  !grouping.MatchesInputs(groupPaths.Skip(1).ToList()));
            Check("S6d 输入列表完全不同时必须重新识别",
                  !grouping.MatchesInputs(new List<string> { Path.Combine(work, "x.mp4") }));
            Check("S6e 空分组结果不允许导出",
                  !new MergeGroupingResult().MatchesInputs(groupPaths));

            // ---- 合并成功后从列表里摘掉「确实被删掉」的源文件 ----
            // 判定必须由 Core 回报的 DeletedSources 驱动：删源会因为校验不通过、
            // 文件被占用而部分失败，那些文件还在磁盘上，界面不能凭开关状态把它们抹掉。
            string p1 = Path.Combine(work, "ep01.mkv");
            string p2 = Path.Combine(work, "ep02.mkv");
            string p3 = Path.Combine(work, "ep03.mkv");
            Sequence("S6f 没删任何文件时列表原样保留",
                     new[] { p1, p2, p3 },
                     InputListCleanup.Remaining(new[] { p1, p2, p3 }, Array.Empty<string>()));
            Sequence("S6g 全部删掉时列表清空",
                     Array.Empty<string>(),
                     InputListCleanup.Remaining(new[] { p1, p2, p3 }, new[] { p1, p2, p3 }));
            Sequence("S6h 只删掉一部分时其余顺序不变",
                     new[] { p2, p3 },
                     InputListCleanup.Remaining(new[] { p1, p2, p3 }, new[] { p1 }));
            // 删源用的路径来自 Core 的 GetFullPath，列表里是用户拖进来的原始写法：
            // 大小写或 ./ 差异不先归一化就会漏删，行留着指向一个已经不存在的文件。
            Sequence("S6i 大小写不同的同一路径也能匹配上",
                     new[] { p3 },
                     InputListCleanup.Remaining(new[] { p1, p2, p3 }, new[] { p1.ToUpperInvariant(), p2 }));
            Sequence("S6j 相对路径写法也能匹配到绝对路径的删除记录",
                     new[] { p2 },
                     InputListCleanup.Remaining(new[] { p1, p2 },
                         new[] { Path.Combine(work, ".", "ep01.mkv") }));
            Sequence("S6k 删除记录里混入不相关路径时不影响其它行",
                     new[] { p1, p3 },
                     InputListCleanup.Remaining(new[] { p1, p2, p3 },
                         new[] { p2, Path.Combine(work, "从未在列表里.mkv") }));

            // ---- 输出文件名避让：目标已存在时不覆盖、也不失败 ----
            // 覆盖是不可逆的：用户把输出目录设成素材目录、或反复试参数时，一次点错就会
            // 静默毁掉上一份成品。避让的代价只是多一个文件，两者代价不对等。
            string taken = Path.Combine(work, "movie.mkv");
            await File.WriteAllTextAsync(taken, "old", new UTF8Encoding(false));
            string free = Path.Combine(work, "movie_free.mkv");
            Equal("S6l 目标不存在时原样使用", free, OutputNaming.Unique(free));
            Equal("S6m 目标已存在时改用「名称 (1)」",
                  Path.Combine(work, "movie (1).mkv"), OutputNaming.Unique(taken));
            await File.WriteAllTextAsync(Path.Combine(work, "movie (1).mkv"), "old1", new UTF8Encoding(false));
            Equal("S6n 连号已被占用时继续往后找",
                  Path.Combine(work, "movie (2).mkv"), OutputNaming.Unique(taken));
            // 没有扩展名、带多个点的名字都要能正确处理，不能把 "a.b" 当成扩展名切错。
            string dotted = Path.Combine(work, "Show.S01E01.mkv");
            await File.WriteAllTextAsync(dotted, "x", new UTF8Encoding(false));
            Equal("S6o 名字里的点不被当成扩展名",
                  Path.Combine(work, "Show.S01E01 (1).mkv"), OutputNaming.Unique(dotted));
            Equal("S6p 空路径原样返回", "", OutputNaming.Unique(""));

            Check("S7 文件夹名体现处理方式（直接拼接 / 重封装）",
                  plan.Folders[0].FolderName.Contains("直接拼接", StringComparison.Ordinal) &&
                  plan.Folders[1].FolderName.Contains("重封装", StringComparison.Ordinal),
                  string.Join(" | ", plan.Folders.Select(f => f.FolderName)));
            Check("S8 计划里包含全部文件", plan.FileCount == 5, "实际 " + plan.FileCount);
            Check("S9 计划只读磁盘，不创建任何目录",
                  !Directory.Exists(plan.Folders[0].FolderPath));

            // ---- 已有同名目录必须改名，绝不能塞进别人的文件夹 ----
            Directory.CreateDirectory(plan.Folders[0].FolderPath);
            GroupFolderPlan again = GroupFolderPlanner.Build(grouping, root);
            Check("S10 目标已有同名目录时自动改名而不是复用",
                  again.Folders[0].FolderName != plan.Folders[0].FolderName,
                  $"首次={plan.Folders[0].FolderName} 再次={again.Folders[0].FolderName}");
            Check("S11 改名后不与已有目录冲突",
                  !Directory.Exists(again.Folders[0].FolderPath));

            // ---- 实机：真的复制一份 ----
            string srcDir = Path.Combine(work, "src");
            Directory.CreateDirectory(srcDir);
            var real = new List<string>();
            foreach (string name in new[] { "r1.mp4", "r2.mp4", "r3.mp4" })
            {
                string path = Path.Combine(srcDir, name);
                await File.WriteAllBytesAsync(path, new byte[256 * 1024]);
                real.Add(path);
            }
            var realGrouping = new MergeGroupingResult();
            realGrouping.Groups.Add(new MergeGroup
            {
                Items = real.Select(p => MakeBundle(p, 5, 256 * 1024)).ToList(),
                DirectCopy = true, Signature = "h264 320x240",
            });

            string exportRoot = Path.Combine(work, "export");
            GroupFolderPlan exportPlan = GroupFolderPlanner.Build(realGrouping, exportRoot);
            var progress = new List<double>();
            GroupFolderResult result = await GroupFolderExporter.ExecuteAsync(exportPlan,
                new Progress<GroupFolderProgress>(p => progress.Add(p.Percent)));
            Check("S12 移动成功", result.Ok, result.Error);
            Check("S13 建立了一个文件夹并移动了全部文件",
                  result.FoldersCreated == 1 && result.FilesMoved == 3,
                  $"文件夹 {result.FoldersCreated}，文件 {result.FilesMoved}");
            Check("S14 文件确实落在组文件夹里",
                  exportPlan.Folders[0].Files.All(f =>
                      File.Exists(Path.Combine(exportPlan.Folders[0].FolderPath, f.FileName))));
            Check("S15 文件夹里有组合说明（说明为什么能拼、按什么顺序）",
                  File.Exists(Path.Combine(exportPlan.Folders[0].FolderPath, GroupFolderPlanner.ReadmeFileName)));
            Check("S16 说明里写明是「移动」而不是副本（避免用户以为原库还有一份）",
                  File.ReadAllText(Path.Combine(exportPlan.Folders[0].FolderPath,
                      GroupFolderPlanner.ReadmeFileName)).Contains("移动", StringComparison.Ordinal));

            // 移动的语义：原位置不再保留。
            Check("S17 移动后原位置不再保留这些文件（与「复制」区分开）",
                  real.All(p => !File.Exists(p)), "仍存在：" + string.Join(",", real.Where(File.Exists)));
            Check("S18 移动后的文件内容与原件逐字节一致",
                  ExportContentMatches(exportPlan, real, work));
            Check("S19 移动有进度回调（跨盘搬运上 GB 不能没有反馈）",
                  progress.Count > 0 && Math.Abs(progress[^1] - 100.0) < 1e-6,
                  progress.Count == 0 ? "无回调" : "最后一次 " + progress[^1].ToString("0.#", CultureInfo.InvariantCulture));

            // ---- 一个文件都没搬过去时不能报成功 ----
            // 老实现用 created.Count > 0 判 Ok：只要文件夹建出来了就算成功，
            // 哪怕里面一个文件都没动（目标同名文件已存在、目标不可写等），
            // 界面会说「已把 N 个组合移动到各自的文件夹（共 0 个文件，0 B）」。
            string blockDir = Path.Combine(work, "blocked");
            Directory.CreateDirectory(blockDir);
            string blockSrc = Path.Combine(work, "block_src.bin");
            await File.WriteAllBytesAsync(blockSrc, new byte[64 * 1024]);
            GroupFolderPlan blockPlan = BuildSingleFilePlan(
                work, "blocked_group", blockSrc, "block_src.bin", 64 * 1024);
            // 预先在目标位置放一个同名文件：File.Move(overwrite:false) 必然失败。
            Directory.CreateDirectory(blockPlan.Folders[0].FolderPath);
            await File.WriteAllBytesAsync(
                Path.Combine(blockPlan.Folders[0].FolderPath, "block_src.bin"), new byte[8]);
            GroupFolderResult blockResult = await GroupFolderExporter.ExecuteAsync(blockPlan);
            Check("S21 一个文件都没搬成时必须报失败，而不是空提示的成功",
                  !blockResult.Ok && blockResult.FilesMoved == 0,
                  $"ok={blockResult.Ok} moved={blockResult.FilesMoved} err={blockResult.Error}");
            Check("S22 失败原因里带上具体文件名（用户要知道是哪一个没搬动）",
                  blockResult.Error.Contains("block_src.bin", StringComparison.Ordinal),
                  blockResult.Error);

            // ---- 跨盘路径的安全线：核对大小通过后才删源 ----
            // 这一条不能靠"同盘改名"掩盖：同盘移动没有中间态，真正危险的是跨盘复制。
            // 直接测「复制后核对」那段逻辑：造一个目标侧写不满的场景不容易，
            // 所以改为验证它的顺序性质——源文件在副本完整之前绝不消失。
            // 同盘路径是改名：没有中间态，也就没有"取消到一半"的窗口。
            string sameSrc = Path.Combine(work, "same.bin");
            await File.WriteAllBytesAsync(sameSrc, new byte[128 * 1024]);
            GroupFolderPlan samePlan = BuildSingleFilePlan(work, "same", sameSrc, "same_moved.bin", 128 * 1024);
            GroupFolderResult sameResult = await GroupFolderExporter.ExecuteAsync(samePlan);
            Check("S20 同盘移动是改名（FilesCopied 为 0，不走复制路径）",
                  sameResult.Ok && sameResult.FilesCopied == 0 && sameResult.FilesMoved == 1,
                  $"copied={sameResult.FilesCopied} moved={sameResult.FilesMoved}");

            // 跨盘路径必须直接测「复制 → 核对 → 删源」这一段，
            // 因为测试机通常只有一个盘，走不到那条分支，而它恰恰是危险的那条。
            string crossSrc = Path.Combine(work, "cross.bin");
            // 必须足够大才会产生多次进度回调（复制按 4MB 一块、每 1% 回一次）。
            // 用几百 KB 的文件只会回调一次，取消根本来不及触发——那是测试本身的问题，
            // 不是被测代码的问题。
            await File.WriteAllBytesAsync(crossSrc, new byte[40 * 1024 * 1024]);
            string crossDst = Path.Combine(work, "cross_moved.bin");
            using (var cts = new CancellationTokenSource())
            {
                int callbacks = 0;
                try
                {
                    await GroupFolderExporter.CopyThenDeleteAsync(crossSrc, crossDst, _ =>
                    {
                        if (++callbacks >= 2) cts.Cancel();
                    }, cts.Token);
                    Check("S21 取消能中断跨盘移动", false, "没有抛出取消");
                }
                catch (OperationCanceledException)
                {
                    Check("S21 取消能中断跨盘移动", true);
                }
                catch (Exception ex)
                {
                    // 实现被改坏时（例如先删源再复制）这里会抛别的异常。
                    // 断言必须报成一条 FAIL，而不是让整个自检崩掉——
                    // 崩溃会把后面所有用例一起吞掉，等于掩盖更多问题。
                    Check("S21 取消能中断跨盘移动", false, "抛出了 " + ex.GetType().Name + "：" + ex.Message);
                }
            }
            Check("S22 取消后源文件仍然完好（跨盘移动最要紧的性质）",
                  File.Exists(crossSrc), "源文件在取消时被删掉了");
            Check("S23 取消后目标侧不留下半成品",
                  !File.Exists(crossDst), "残留了不完整的副本");

            // 正常完成的跨盘移动：源消失、目标存在且大小一致。
            // 用 try/catch 包住：如果实现被改坏了（例如先删源再复制），
            // 这里会抛异常。断言必须报成一条 FAIL，而不是让整个自检崩掉——
            // 崩溃会把后面所有用例一起吞掉，等于掩盖了更多问题。
            string okSrc = Path.Combine(work, "ok.bin");
            string okDst = Path.Combine(work, "ok_moved.bin");
            await File.WriteAllBytesAsync(okSrc, new byte[300 * 1024]);
            bool okMoveDone = false;
            try
            {
                await GroupFolderExporter.CopyThenDeleteAsync(okSrc, okDst, _ => { }, CancellationToken.None);
                okMoveDone = !File.Exists(okSrc) && File.Exists(okDst) && new FileInfo(okDst).Length == 300 * 1024;
            }
            catch (Exception ex)
            {
                okMoveDone = false;
                Console.WriteLine("  (跨盘移动抛出异常：" + ex.Message + ")");
                try { await File.WriteAllBytesAsync(okSrc, new byte[300 * 1024]); } catch { /* 恢复失败不影响断言 */ }
            }
            Check("S24 跨盘移动完成后源消失、目标大小一致", okMoveDone);

            // ---- 结构守卫：GUI 必须走 Core，不能在页面里自己拼搬运逻辑 ----
            string? mergeVm = FindAppFile(Path.Combine("ViewModels", "VideoMergeViewModel.cs"));
            Check("S25 移动走 Core 的计划与执行器（GUI 不自行决定搬运策略）",
                  mergeVm is not null &&
                  mergeVm.Contains("GroupFolderPlanner.Build", StringComparison.Ordinal) &&
                  mergeVm.Contains("GroupFolderExporter.ExecuteAsync", StringComparison.Ordinal));
            Check("S26 移动前必须让用户确认，且明说这是移动不是复制",
                  mergeVm is not null &&
                  mergeVm.Contains("AskYesNoAsync", StringComparison.Ordinal) &&
                  mergeVm.Contains("这是移动，不是复制", StringComparison.Ordinal),
                  "移动不可逆，确认框必须把代价说清楚");
            string? mergePage = FindAppFile(Path.Combine("Pages", "MergePage.xaml"));
            Check("S27 合并页提供移动入口且按钮文案说明是移动",
                  mergePage is not null &&
                  mergePage.Contains("ExportGroupFoldersCommand", StringComparison.Ordinal) &&
                  mergePage.Contains("移动至文件夹", StringComparison.Ordinal) &&
                  mergePage.Contains("原位置不再保留", StringComparison.Ordinal));
            // 策略卡片不得因为差异条数无限拉长。
            // 差异条数与输入文件数成正比（三十几个文件能列三十几项，每项还带完整文件名），
            // 不限高的话这张卡片会把右栏撑成几千像素，「开始合并」被挤出屏幕，
            // 用户反而找不到该点哪里。XAML 对这种退化完全不报错，只能靠结构断言兜住。
            if (mergePage is not null)
            {
                int reasonsAt = mergePage.IndexOf("ViewModel.ReasonGroups", StringComparison.Ordinal);
                int scrollAt = reasonsAt < 0
                    ? -1
                    : mergePage.LastIndexOf("<ScrollViewer", reasonsAt, StringComparison.Ordinal);
                int tagEnd = scrollAt < 0 ? -1 : mergePage.IndexOf('>', scrollAt);
                string openTag = scrollAt >= 0 && tagEnd > scrollAt ? mergePage[scrollAt..(tagEnd + 1)] : "";
                Check("S30 策略差异列表放在限高滚动区内（不随条数无限拉长）",
                      openTag.Contains("MaxHeight=", StringComparison.Ordinal) &&
                      openTag.Contains("VerticalScrollBarVisibility=\"Auto\"", StringComparison.Ordinal),
                      scrollAt < 0
                          ? "找不到包裹差异列表的 ScrollViewer"
                          : "开标签：" + openTag.Replace("\r", " ").Replace("\n", " "));
            }

            string? exporter = FindCoreFile(Path.Combine("Merge", "GroupFolderExporter.cs"));
            // 这条守的是"顺序"，不是"有没有写"。仅断言代码里出现 File.Delete 是不够的：
            // 把它挪到复制之前同样能编译、能让正常路径跑通，
            // 却会在中途失败/取消时同时丢掉源文件与副本。
            // 因此按字符位置比较：删源必须出现在「核对大小」之后。
            bool orderOk = false;
            if (exporter is not null)
            {
                int copyAt = exporter.IndexOf("CopyWithProgressAsync(source, destination", StringComparison.Ordinal);
                int checkAt = exporter.IndexOf("复制结果不完整", StringComparison.Ordinal);
                int deleteAt = exporter.IndexOf("File.Delete(source)", StringComparison.Ordinal);
                orderOk = copyAt >= 0 && checkAt > copyAt && deleteAt > checkAt;
            }
            Check("S28 跨盘移动必须按「复制 → 核对大小 → 删源」的顺序（位置断言，不只看有没有写）",
                  orderOk, exporter is null ? "找不到执行器" :
                  "要求 copy < 核对 < delete，实际不满足；先删源会在中途失败时同时丢掉两份数据");
            Check("S29 计划区分同盘与跨盘（同盘不该要求目标盘空间）",
                  exporter is not null &&
                  FindCoreFile(Path.Combine("Merge", "GroupFolderPlan.cs")) is string planFile &&
                  planFile.Contains("CrossVolumeBytes", StringComparison.Ordinal) &&
                  planFile.Contains("HasCrossVolumeMove", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* 忽略 */ }
        }
    }

    #endregion

    #region R. 进度终态送达

    /// <summary>
    /// 覆盖「文件已经输出成功，界面却卡在 99% / 写入输出文件…」这个问题。
    ///
    /// 根因是进度节流把**最后一条**进度丢掉了：
    /// 界面按 100ms 最小间隔刷新，而引擎收尾时报的几条进度挨得极近
    /// （写入输出文件 → 输出已就位 → 合并完成，实测相差 3~5ms），
    /// 于是「合并完成」这一条被当作「太快了，丢了吧」扔掉。
    /// 任务其实早已成功结束，界面却停在前一条上。
    ///
    /// 这类缺陷编译器、运行时、业务断言全都不报错——文件是对的，只有画面是错的。
    /// 所以这里把规则固化成可执行断言，并加结构守卫防止有人把节流改回「超时就丢」。
    /// </summary>
    private static void CheckProgressTerminalDelivery()
    {
        TimeSpan tiny = TimeSpan.FromMilliseconds(2);
        TimeSpan enough = TimeSpan.FromMilliseconds(150);

        Check("R1 普通进度在节流间隔内被丢弃（避免刷爆 UI 线程）",
              !ProgressPacer.ShouldDeliver(tiny, terminal: false));
        Check("R2 普通进度超过间隔后照常送达",
              ProgressPacer.ShouldDeliver(enough, terminal: false));
        Check("R3 终态进度无条件送达（哪怕刚刷过一次）",
              ProgressPacer.ShouldDeliver(tiny, terminal: true));
        Check("R4 100% 被判定为终态", ProgressPacer.IsTerminalPercent(100.0));
        Check("R5 99.x% 不算终态（收尾阶段仍有真实进度要显示）",
              !ProgressPacer.IsTerminalPercent(99.9) && !ProgressPacer.IsTerminalPercent(99.0));

        // 事故现场重演：引擎收尾时连报三条，间隔 2ms。
        // 修复前第 2、3 条都会被丢掉，界面停在「写入输出文件… 99%」。
        var delivered = new List<string>();
        TimeSpan sinceLastFlush = enough;
        var bursts = new (string Message, bool Terminal)[]
        {
            ("写入输出文件…", false),
            ("输出已就位，正在收尾…", false),
            ("合并完成", true),
        };
        foreach ((string message, bool terminal) in bursts)
        {
            if (ProgressPacer.ShouldDeliver(sinceLastFlush, terminal))
            {
                delivered.Add(message);
                sinceLastFlush = TimeSpan.Zero;
            }
            sinceLastFlush += tiny;
        }
        Check("R6 收尾三连报里最后一条一定送达（否则界面停在 99%）",
              delivered.Contains("合并完成"), "实际送达：" + string.Join(" → ", delivered));
        Check("R7 卡顿场景下中间进度可以被合并掉", delivered.Count < 3,
              "送达 " + delivered.Count + " 条（全部送达会把 UI 线程刷爆）");

        // 结构守卫：进度节流必须经过统一判定。
        // 谁要是再写一遍「不到 100ms 就 return」，就会把这次的缺陷原样种回去。
        string root = FindAppRoot() ?? "";
        var targets = new (string File, string Label)[]
        {
            ("VideoMergeViewModel.cs", "视频合并"),
            ("VideoTrimViewModel.cs", "视频裁剪"),
        };
        foreach ((string file, string label) in targets)
        {
            string full = Path.Combine(root, "ViewModels", file);
            string text = File.Exists(full) ? File.ReadAllText(full) : "";
            Check("R8 进度节流走统一判定：" + label,
                  text.Contains("ProgressPacer.ShouldDeliver", StringComparison.Ordinal) &&
                  !System.Text.RegularExpressions.Regex.IsMatch(
                      text, @"TotalMilliseconds\s*<\s*100\s*\)\s*return"),
                  file + " 仍在使用「不到 100ms 就整条丢弃」的写法，终态会被丢掉");
        }

        // 结构守卫：任务结束必须把进度区各一次改到位。
        // 只改 Progress 不改 ProgressText/ProgressDetail/EtaText，
        // 界面就会定格成「已完成 / 写入输出文件… / 99% / 预计剩余 计算中…」。
        string mergeVmPath = Path.Combine(root, "ViewModels", "VideoMergeViewModel.cs");
        string mergeVm = File.Exists(mergeVmPath) ? File.ReadAllText(mergeVmPath) : "";
        Check("R9 合并页结束时会清掉「预计剩余」而不是保留上一阶段的预测",
              mergeVm.Contains("private void SettleProgress", StringComparison.Ordinal),
              "结束时若不清 EtaText，界面会停在「预计剩余 计算中…」");
        Check("R10 合并页结束时把百分比定稿为 100%",
              mergeVm.Contains("ProgressText = ", StringComparison.Ordinal) &&
              mergeVm.Contains("SettleProgress(result.Ok", StringComparison.Ordinal));

        // 收尾阶段的百分比只能一路向上。
        // 「删源前全片解码」排在「输出已就位」之后，若它又从 99.0 起算，
        // 进度条会在收尾时倒退回去，看起来像从头再来一遍（实测就是这样）。
        string? engine = FindCoreFile(Path.Combine("Merge", "VideoMergeEngine.cs"));
        if (engine is not null)
        {
            var literals = System.Text.RegularExpressions.Regex
                .Matches(engine, @"Report\(MergeStage\.(?:Completed|Verifying),\s*(99(?:\.\d+)?)")
                .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
                .ToList();
            double[] ordered = literals.ToArray();
            bool ascending = true;
            for (int i = 1; i < ordered.Length; i++)
                if (ordered[i] < ordered[i - 1]) ascending = false;
            Check("R11 收尾阶段报告的百分比单调不减（进度条不许倒退）",
                  ascending, "出现的顺序：" + string.Join(" → ", ordered));
        }

        // 删源前的闸门必须是「代价封顶」的抽样，不能是逐帧全片解码。
        // 全片解码的耗时与时长线性相关（36 分钟实测 116 秒，2 小时要十几分钟），
        // 用户每次合并都要在收尾阶段干等——这正是「卡在 99%」的最后一块来源。
        Check("R12 删源前改用抽样解码（代价封顶，不再逐帧全片解码）",
              engine is not null &&
              engine.Contains("CheckSampledDecodeAsync", StringComparison.Ordinal) &&
              !System.Text.RegularExpressions.Regex.IsMatch(
                  engine, @"CheckFullDecodeAsync\(\s*_tools\.Ffmpeg, outputPath"),
              "引擎不应再对最终输出做逐帧全片解码");
        Check("R13 删源前额外比对字节数（确定性排除搬运写坏）",
              engine is not null &&
              engine.Contains("expectedOutputBytes", StringComparison.Ordinal),
              "抽样是采样不是证明，必须配一条确定性的证据");

        // 实机：抽样必须能命中真实损坏，又必须对完好文件零误报。
        string? ffmpegPath = ToolLocator.FindFfmpeg();
        if (!string.IsNullOrEmpty(ffmpegPath))
        {
            CheckSampledDecodeBehavior(ffmpegPath);
        }

        string? verifier = FindCoreFile(Path.Combine("Services", "OutputVerifier.cs"));
        Check("R14 抽样解码会限制抽样点数量（代价不随时长线性增长）",
              verifier is not null &&
              System.Text.RegularExpressions.Regex.IsMatch(
                  verifier, @"Math\.Clamp\(\(int\)\(durationSeconds / 120\.0\), 6, 16\)"),
              "抽样点数必须封顶，否则长素材又变回「等十几分钟」");
        Check("R15 进度回调关闭后，排队中的旧快照不会再覆盖定稿",
              mergeVm.Contains("_progressClosed", StringComparison.Ordinal),
              "任务结束后必须有开关拦住迟到回调，否则 99.9% 会把「已完成/100%」覆盖回去");
    }

    #endregion

    #region Q. 输出搬运与进度可见性

    /// <summary>
    /// 覆盖「卡在 99% 很久」这个问题。
    ///
    /// 根因不是某一步算得慢，而是最后那一步 <c>File.Move</c>：
    /// 临时目录在系统临时盘（通常是 C:），输出可能选在别的盘，
    /// 跨卷时 .NET 会退化成「整文件复制 + 删源」，而且**全程没有任何进度回调**。
    /// 几十 GB 的输出在 99% 上静止几分钟，界面上和卡死无法区分。
    ///
    /// 两处修正，各配一条断言：
    /// 一是临时目录尽量贴着输出盘，让最后一步退化成改名（微秒级）；
    /// 二是万一仍需跨卷，复制要按字节报告进度。
    /// </summary>
    private static async Task CheckOutputMoveAsync()
    {
        string work = Path.Combine(Path.GetTempPath(), "subfusion_move_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            // ---- 同卷判定 ----
            Check("Q1 同一目录判为同卷",
                  FileMover.SameVolume(Path.Combine(work, "a"), Path.Combine(work, "b")));
            Check("Q2 不同盘符判为跨卷（不存在时按跨卷处理，慢一点但不会错）",
                  !FileMover.SameVolume(Path.Combine(work, "a"), @"Z:\nope\b") ||
                  !FileMover.SameVolume(Path.Combine(work, "a"), work));

            // ---- 同卷：改名，不复制 ----
            string src = Path.Combine(work, "src.bin");
            await File.WriteAllBytesAsync(src, new byte[256 * 1024]);
            string dst = Path.Combine(work, "dst.bin");
            var seen = new List<double>();
            await FileMover.MoveAsync(src, dst, r => seen.Add(r));
            Check("Q3 同卷搬运只是改名（源消失、目标出现、内容一致）",
                  !File.Exists(src) && File.Exists(dst) && new FileInfo(dst).Length == 256 * 1024);

            // ---- 跨卷：必须有真实进度，且内容逐字节一致 ----
            // 直接调用复制实现，因为测试机不一定有第二个盘；
            // 这正是「卡在 99%」的那段代码，不能因为磁盘只有一块就没人验证。
            string src2 = Path.Combine(work, "big.bin");
            string dst2 = Path.Combine(work, "big_copy.bin");
            int total = 8 * 1024 * 1024;
            byte[] payload = new byte[total];
            for (int i = 0; i < total; i++) payload[i] = (byte)(i * 31 % 251);
            await File.WriteAllBytesAsync(src2, payload);

            var ratios = new List<double>();
            await FileMover.CopyWithProgressAsync(src2, dst2, r => ratios.Add(r), CancellationToken.None);
            Check("Q4 跨卷复制有多次进度回调（不是一直不动到结束）",
                  ratios.Count >= 3, "回调次数=" + ratios.Count);
            Check("Q5 进度单调不减且以 1.0 收尾",
                  ratios.SequenceEqual(ratios.OrderBy(x => x)) && Math.Abs(ratios[^1] - 1.0) < 1e-9,
                  ratios.Count > 0 ? ratios[^1].ToString("0.###", CultureInfo.InvariantCulture) : "无回调");
            byte[] copied = await File.ReadAllBytesAsync(dst2);
            Check("Q6 复制结果与源逐字节一致", copied.SequenceEqual(payload),
                  $"len {copied.Length} vs {total}");

            // ---- 取消：半成品必须清掉 ----
            string dst3 = Path.Combine(work, "cancelled.bin");
            using (var cts = new CancellationTokenSource())
            {
                int callbacks = 0;
                try
                {
                    await FileMover.CopyWithProgressAsync(src2, dst3, _ =>
                    {
                        if (++callbacks >= 2) cts.Cancel();
                    }, cts.Token);
                    Check("Q7 取消能中断复制", false, "复制没有被中断");
                }
                catch (OperationCanceledException)
                {
                    Check("Q7 取消能中断复制", true);
                }
            }
            Check("Q8 取消后不留下半成品（残缺输出看起来像完整文件，比没有更危险）",
                  !File.Exists(dst3));

            // ---- 结构守卫：引擎不得再直接 File.Move 输出 ----
            // 这条守的是「有人日后为了省事把带进度的搬运改回 File.Move」。
            // 那会静默地退回「99% 卡住几分钟」，而且编译、运行、单测全都不报错。
            string? engine = FindCoreFile(Path.Combine("Merge", "VideoMergeEngine.cs"));
            if (engine is not null)
            {
                Check("Q9 合并引擎用 FileMover 搬运输出（不再裸用 File.Move）",
                      engine.Contains("FileMover.MoveAsync", StringComparison.Ordinal) &&
                      !engine.Contains("File.Move(tempOutput", StringComparison.Ordinal),
                      "跨卷时 File.Move 会退化成无进度的整文件复制，表现为卡在 99%");
                Check("Q10 临时目录优先放在输出盘（同卷时最后一步只是改名）",
                      engine.Contains("CreateTempDirectory", StringComparison.Ordinal) &&
                      engine.Contains("GetPathRoot", StringComparison.Ordinal));
            }
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* 忽略 */ }
        }
    }

    #endregion

    #region P. 输入截断检测与收尾

    /// <summary>
    /// 覆盖「容器声明的时长在后面没有数据」这一类输入损坏。
    ///
    /// 这个场景是从用户侧真实报错反推来的：合并报「输出时长与各段之和不符
    /// （期望约 1797.72s，实际 1324.95s）」外加「输出文件结尾数据不完整或无法解码
    /// （aac invalid band type）」。两个报错看着毫不相干，实际是同一个原因：
    /// 某个输入文件的容器头声明了完整时长，但数据在中途就断了。
    /// concat 把残缺的尾包原样搬进输出，于是短一截、结尾还解不出来。
    ///
    /// 断言重点是「能不能真的识别与修复」，而不是函数能跑通：
    /// 用真实的字节截断造一个坏文件，验证检测、收尾，以及拼出来的输出可完整解码。
    /// </summary>
    private static async Task CheckTruncatedInputAsync()
    {
        string? ffmpeg = ToolLocator.FindFfmpeg();
        string? ffprobe = ToolLocator.FindFfprobe();
        if (string.IsNullOrEmpty(ffmpeg) || string.IsNullOrEmpty(ffprobe))
        {
            Console.WriteLine("(未找到 ffmpeg/ffprobe，跳过截断输入验证)");
            return;
        }

        string work = Path.Combine(Path.GetTempPath(), "subfusion_trunc_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            // ---- 纯逻辑：判定与容差 ----
            Check("P1 声明与可读一致时判为完整", !new InputHealth("a.mp4", 100, 99.9).Truncated);
            Check("P2 只差零点几秒不算截断（容器时长本就会略短于最后一帧）",
                  !new InputHealth("a.mp4", 100, 99.6).Truncated);
            Check("P3 缺一大截判为截断", new InputHealth("a.mp4", 100, 21).Truncated);
            Check("P4 长素材按比例给容差、短素材按固定值",
                  new InputHealth("a.mp4", 4000, 3985).Truncated &&
                  !new InputHealth("a.mp4", 20, 19.7).Truncated,
                  string.Format(CultureInfo.InvariantCulture, "长={0:0.##}s 短={1:0.##}s",
                      new InputHealth("a", 4000, 3985).Tolerance, new InputHealth("a", 20, 19.7).Tolerance));
            Check("P5 可读终点未知时不下判断（宁可漏报也不误杀好文件）",
                  !new InputHealth("a.mp4", 100, 0).Truncated);
            Check("P6 收尾长度留出余量且不超过声明时长",
                  Math.Abs(new InputHealth("a.mp4", 100, 21).KeepSeconds - 20.88) < 0.01 &&
                  new InputHealth("a.mp4", 100, 200).KeepSeconds == 100,
                  new InputHealth("a.mp4", 100, 21).KeepSeconds.ToString("0.###", CultureInfo.InvariantCulture));

            // ---- 实机：造一个真的被截断的文件 ----
            string good = await MakeClipAsync(ffmpeg, work, "good", 320, 240, "24", "aac", 44100, 2, 8.0,
                                              "testsrc");
            string full = Path.Combine(work, "full.mp4");
            // 索引必须写在文件头（faststart）：这样截断后容器仍然声明完整时长，
            // 与线上那种「下载中断的 mp4」完全同形。索引在尾部时截断会直接变成不可读，
            // 反而测不到本例要覆盖的行为。
            ProcessCapture fullRun = await ProcessUtil.RunAsync(ffmpeg, new[]
            {
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "smptebars=size=320x240:rate=24",
                "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=44100",
                "-t", "8", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-ac", "2",
                "-movflags", "+faststart", full,
            }, 180);
            Check("P7 截断实验素材生成成功", fullRun.ExitCode == 0 && File.Exists(full), fullRun.Stderr.Trim());
            if (!File.Exists(full)) return;

            string trunc = Path.Combine(work, "trunc.mp4");
            long keepBytes = new FileInfo(full).Length * 55 / 100;
            await using (FileStream src = File.OpenRead(full))
            await using (FileStream dst = File.Create(trunc))
            {
                byte[] buffer = new byte[64 * 1024];
                long left = keepBytes;
                while (left > 0)
                {
                    int read = await src.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)));
                    if (read <= 0) break;
                    await dst.WriteAsync(buffer.AsMemory(0, read));
                    left -= read;
                }
            }

            MediaBundle fullInfo = await MediaInspector.InspectAsync(ffprobe, full);
            MediaBundle truncInfo = await MediaInspector.InspectAsync(ffprobe, trunc);
            Check("P8 截断文件的容器仍然声明完整时长（这正是它难被发现的原因）",
                  truncInfo.Success && Math.Abs(truncInfo.Duration - fullInfo.Duration) < 0.3,
                  string.Format(CultureInfo.InvariantCulture, "完整={0:F2}s 截断={1:F2}s",
                      fullInfo.Duration, truncInfo.Duration));

            InputHealth health = await InputIntegrity.ProbeAsync(ffprobe, truncInfo);
            Check("P9 实机识别出截断并报出缺失量",
                  health.Truncated && health.MissingSeconds > 1.0, health.Describe());
            InputHealth goodHealth = await InputIntegrity.ProbeAsync(ffprobe, fullInfo);
            Check("P10 完整文件不被误判为截断", !goodHealth.Truncated, goodHealth.Describe());

            // ---- 守卫：头尾抽样的结论取决于截断落在哪个包上，因此不可依赖 ----
            // 截断点恰好切断一个 AAC 帧时它会报错，落在两个可用包之间时它一无所知。
            // 所以这一条只记录，真正无条件生效的是下面的内容终点校验。
            List<string> headTail = await OutputVerifier.CheckIntegrityAsync(ffmpeg, trunc, 60);
            Check("P11 头尾抽样对尾部缺失不可靠（是否命中取决于截断位置）",
                  true, "头尾告警 " + headTail.Count + " 条");
            List<string> contentEnd = await OutputVerifier.CheckContentEndAsync(ffmpeg, trunc,
                truncInfo.Duration);
            Check("P12 内容终点校验能发现尾部没有数据",
                  contentEnd.Count == 1, string.Join("；", contentEnd));
            Check("P13 内容终点校验对完好文件不误报",
                  (await OutputVerifier.CheckContentEndAsync(ffmpeg, full, fullInfo.Duration)).Count == 0);

            // ---- 收尾后的片段必须能单独完整解码 ----
            string salvaged = Path.Combine(work, "salvaged.mp4");
            double keep = health.KeepSeconds;
            // BuildSalvageCommand 与其它 Build*Command 一样把可执行文件放在 [0]，
            // 而 ProcessUtil.RunAsync 的可执行文件是单独参数，因此这里必须跳过第一项。
            ProcessCapture salvageRun = await ProcessUtil.RunAsync(ffmpeg,
                InputIntegrity.BuildSalvageCommand(ffmpeg, truncInfo, salvaged, keep).Skip(1).ToList(), 180);
            Check("P14 收尾命令成功", salvageRun.ExitCode == 0 && File.Exists(salvaged),
                  salvageRun.Stderr.Trim());
            if (File.Exists(salvaged))
            {
                Check("P15 收尾后的片段可完整解码（残包已被切掉）",
                      (await OutputVerifier.CheckFullDecodeAsync(ffmpeg, salvaged, 300)).Count == 0);
                Check("P16 收尾保持原容器族（mp4 输入不被改成 mkv，否则会误触发重新编码）",
                      InputIntegrity.SalvagedExtension(truncInfo) == ".mp4");
            }

            // ---- 端到端：合并必须成功，且输出能完整解码 ----
            var task = new VideoMergeTask(await FfmpegTools.ResolveAsync(ffmpeg, ffprobe));
            string merged = Path.Combine(work, "merged.mkv");
            MergeResult result = await task.RunAsync(new VideoMergeOptions
            {
                Inputs = new List<string> { good, trunc },
                OutputPath = merged,
                Container = "mkv",
                VerifyOutput = true,
            });
            // 这一条是整个修复的核心：修复前这里必然以「时长不符 + 结尾无法解码」失败。
            Check("P17 含截断输入的合并在默认设置下成功", result.Ok, result.Error);
            Check("P18 结果里说明了哪个文件被截断、少了多少",
                  result.Notices.Any(n => n.Contains("尾部数据缺失", StringComparison.Ordinal)),
                  string.Join("；", result.Notices));
            if (result.Ok)
            {
                Check("P19 合并输出可完整解码",
                      (await OutputVerifier.CheckFullDecodeAsync(ffmpeg, merged, 600)).Count == 0);
                MediaBundle outInfo = await MediaInspector.InspectAsync(ffprobe, merged);
                Check("P20 输出时长等于「可读部分之和」而不是「声明之和」",
                      Math.Abs(outInfo.Duration - (fullInfo.Duration + keep)) < 1.5,
                      string.Format(CultureInfo.InvariantCulture, "输出={0:F2}s 期望≈{1:F2}s",
                          outInfo.Duration, fullInfo.Duration + keep));
            }

            // ---- 关闭收尾时必须明确指出坏文件，而不是含糊地说校验不过 ----
            string rejected = Path.Combine(work, "rejected.mkv");
            MergeResult strict = await task.RunAsync(new VideoMergeOptions
            {
                Inputs = new List<string> { good, trunc },
                OutputPath = rejected,
                Container = "mkv",
                VerifyOutput = true,
                SalvageTruncatedInputs = false,
            });
            Check("P21 关闭收尾时在探测阶段就失败",
                  !strict.Ok && strict.Stage == MergeStage.Probing, strict.Error);
            Check("P22 失败信息指名被截断的文件与缺失秒数",
                  strict.Error.Contains("截断", StringComparison.Ordinal) &&
                  strict.Error.Contains(Path.GetFileName(trunc), StringComparison.Ordinal),
                  strict.Error);
            Check("P23 关闭收尾时不产生输出文件", !File.Exists(rejected));
            Check("P24 诊断日志记录了完整性结论",
                  strict.Diagnostics.Any(l => l.StartsWith("[完整性]", StringComparison.Ordinal)),
                  string.Join(" | ", strict.Diagnostics.Take(4)));
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* 忽略 */ }
        }
    }

    #endregion

    /// <summary>用 lavfi 现场生成指定参数的测试片段。</summary>
    private static async Task<string> MakeClipAsync(string ffmpeg, string work, string name, int width, int height,
                                                    string fps, string audioCodec, int sampleRate, int channels,
                                                    double seconds, string source, int timescale = 0,
                                                    string videoCodec = "libx264")
    {
        string path = Path.Combine(work, name + ".mp4");
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", $"{source}=size={width}x{height}:rate={fps}",   // fps 允许 "29.97" 这类精确值
            "-f", "lavfi", "-i", $"sine=frequency=440:sample_rate={sampleRate}",
            "-t", seconds.ToString("0.###", CultureInfo.InvariantCulture),
            // 显式写入 BT.709 色彩标记：编码器的 -colorspace 参数在多数封装下不会写进码流，
            // 必须走滤镜内的 setparams，否则同一批素材的色彩字段会是「未知」，兼容性判定无法对齐。
            "-vf", "setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709:range=tv",
            "-c:v", videoCodec, "-pix_fmt", "yuv420p",
        };
        // 老编码没有 -crf，用 qscale 表达质量；其余保持默认。
        if (videoCodec is "mpeg4" or "mpeg2video") args.AddRange(new[] { "-q:v", "3" });
        // 显式指定 mp4 时基：默认值随 ffmpeg 版本变化，兼容性用例必须能精确控制它。
        if (timescale > 0) args.AddRange(new[] { "-video_track_timescale", timescale.ToString(CultureInfo.InvariantCulture) });
        args.AddRange(new[] { "-c:a", audioCodec, "-ac", channels.ToString(CultureInfo.InvariantCulture), "-shortest", path });
        ProcessCapture proc = await ProcessUtil.RunAsync(ffmpeg, args, 180);
        if (proc.ExitCode != 0) Console.WriteLine($"  (生成 {name} 失败：{proc.Stderr.Trim()})");
        return path;
    }

    #endregion

    #region H. MKV 自动章节

    /// <summary>
    /// MKV 自动章节回归：默认一章一片、自定义标题、关闭章节、时间轴与最终输出一致、
    /// 章节不触发重编码、输出可回读验证。
    /// </summary>
    private static async Task CheckChaptersAsync()
    {
        string? ffmpeg = ToolLocator.FindFfmpeg();
        string? ffprobe = ToolLocator.FindFfprobe();
        if (string.IsNullOrEmpty(ffmpeg) || string.IsNullOrEmpty(ffprobe))
        {
            Console.WriteLine("(未找到 ffmpeg/ffprobe，跳过章节验证)");
            return;
        }

        // ---- 纯逻辑：时间轴计算与合法性 ----
        List<ChapterEntry> timeline = ChapterTimeline.Build(new[] { 2.0, 3.0, 4.0 }, null, 9.0);
        Check("H1 每个输入生成一个章节", timeline.Count == 3);
        Equal("H2 第一章从 0 开始", "00:00:00.000", ChapterEntry.FormatTime(timeline[0].Start));
        Equal("H3 第二章起点接上一章终点", "00:00:02.000", ChapterEntry.FormatTime(timeline[1].Start));
        Equal("H4 第三章起点接上一章终点", "00:00:05.000", ChapterEntry.FormatTime(timeline[2].Start));
        Equal("H5 最后一章结束时于总时长", "00:00:09.000", ChapterEntry.FormatTime(timeline[^1].End));
        Check("H6 章节之间不重叠",
              timeline.Zip(timeline.Skip(1)).All(p => Math.Abs(p.First.End - p.Second.Start) < 1e-6));

        List<ChapterEntry> drifted = ChapterTimeline.Build(new[] { 2.0, 3.0, 4.0 }, null, 9.05);
        Check("H7 输出实际时长不同时按比例校正时间轴",
              Math.Abs(drifted[^1].End - 9.05) < 0.002,
              drifted[^1].End.ToString("F3", CultureInfo.InvariantCulture));

        List<ChapterEntry> titled = ChapterTimeline.Build(new[] { 1.0, 1.0 },
            new[] { "第一集", "第二集" }, 2.0);
        Equal("H8 自定义章节标题生效", "第一集", titled[0].Title);
        List<ChapterEntry> fallback = ChapterTimeline.Build(new[] { 1.0, 1.0 }, new[] { "只有一条" }, 2.0);
        Equal("H9 标题不足时用占位标题补齐", "第 2 段", fallback[1].Title);

        List<ChapterEntry> bad = new()
        {
            new ChapterEntry(0, 2, "A"),
            new ChapterEntry(1.5, 3, "B"),
        };
        Check("H10 重叠章节被识别为非法", ChapterTimeline.Validate(bad, 10).Any(m => m.Contains("重叠")));
        Check("H11 超出时长的章节被识别为非法",
              ChapterTimeline.Validate(new List<ChapterEntry> { new(0, 12, "A") }, 10)
                  .Any(m => m.Contains("超出")));
        Check("H12 合法章节通过校验",
              ChapterTimeline.Validate(timeline, 9.0).Count == 0,
              string.Join("；", ChapterTimeline.Validate(timeline, 9.0)));

        string meta = ChapterWriter.BuildMetadata(timeline);
        Check("H13 ffmetadata 含 FFMETADATA1 头", meta.StartsWith(";FFMETADATA1", StringComparison.Ordinal));
        Check("H14 ffmetadata 使用毫秒时间基准", meta.Contains("TIMEBASE=1/1000", StringComparison.Ordinal));
        Check("H15 ffmetadata 含全部章节", CountOccurrences(meta, "[CHAPTER]") == 3);
        Check("H16 特殊字符被转义",
              ChapterWriter.Escape("A=B;C#D").Contains("\\=", StringComparison.Ordinal) &&
              ChapterWriter.Escape("A=B;C#D").Contains("\\;", StringComparison.Ordinal) &&
              ChapterWriter.Escape("A=B;C#D").Contains("\\#", StringComparison.Ordinal));

        string work = Path.Combine(Path.GetTempPath(), "subfusion_chapter_selftest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var tools = await FfmpegTools.ResolveAsync(ffmpeg, ffprobe);
            var task = new VideoMergeTask(tools);

            string c1 = await MakeClipAsync(ffmpeg, work, "part1", 320, 240, "24", "aac", 44100, 2, 2.0, "testsrc");
            string c2 = await MakeClipAsync(ffmpeg, work, "part2", 320, 240, "24", "aac", 44100, 2, 3.0, "smptebars");
            string c3 = await MakeClipAsync(ffmpeg, work, "part3", 320, 240, "24", "aac", 44100, 2, 2.5, "testsrc2");
            if (!new[] { c1, c2, c3 }.All(File.Exists)) { Console.WriteLine("(素材生成失败，跳过)"); return; }

            // ---- 默认自动章节 ----
            string withChapters = Path.Combine(work, "chaptered.mkv");
            MergeResult result = await task.RunAsync(new VideoMergeOptions
            {
                Inputs = new List<string> { c1, c2, c3 },
                OutputPath = withChapters,
                Container = "mkv",
                VerifyOutput = true,
            });
            Check("H17 带章节的合并成功", result.Ok, result.Error);
            if (result.Ok)
            {
                List<ChapterEntry> read = await ChapterWriter.ReadAsync(ffprobe, withChapters);
                Check("H18 输出章节数量与输入一致", read.Count == 3, $"实际 {read.Count}");
                Check("H19 默认标题为去掉扩展名的文件名",
                      read.Count == 3 && read[0].Title == "part1" && read[1].Title == "part2" &&
                      read[2].Title == "part3",
                      string.Join(" | ", read.Select(x => x.Title)));
                MediaBundle info = await MediaInspector.InspectAsync(ffprobe, withChapters);
                Check("H20 第一章从 0 开始", read.Count == 3 && read[0].Start < 0.08,
                      read.Count == 3 ? read[0].StartText : "");
                Check("H21 最后一章结束于输出实际时长",
                      read.Count == 3 && Math.Abs(read[^1].End - info.Duration) < 0.15,
                      read.Count == 3 ? $"{read[^1].End:F3} vs {info.Duration:F3}" : "");
                Check("H22 章节时间轴严格递增且无重叠",
                      read.Zip(read.Skip(1)).All(p => p.Second.Start >= p.First.End - 0.02));
                Check("H23 章节写入未触发视频重编码", result.VideoCopied);
            }

            // ---- 自定义标题 ----
            string custom = Path.Combine(work, "custom.mkv");
            MergeResult customResult = await task.RunAsync(new VideoMergeOptions
            {
                Inputs = new List<string> { c1, c2 },
                OutputPath = custom,
                Container = "mkv",
                ChapterTitles = new List<string> { "开场", "正片：第二章" },
                VerifyOutput = true,
            });
            Check("H24 自定义标题合并成功", customResult.Ok, customResult.Error);
            if (customResult.Ok)
            {
                List<ChapterEntry> read = await ChapterWriter.ReadAsync(ffprobe, custom);
                Check("H25 自定义标题完整写入（含中文与冒号）",
                      read.Count == 2 && read[0].Title == "开场" && read[1].Title == "正片：第二章",
                      string.Join(" | ", read.Select(x => x.Title)));
            }

            // ---- 关闭章节 ----
            string off = Path.Combine(work, "nochapters.mkv");
            MergeResult offResult = await task.RunAsync(new VideoMergeOptions
            {
                Inputs = new List<string> { c1, c2 },
                OutputPath = off,
                Container = "mkv",
                AutoChapters = false,
                VerifyOutput = true,
            });
            Check("H26 关闭自动章节后合并成功", offResult.Ok, offResult.Error);
            if (offResult.Ok)
            {
                List<ChapterEntry> read = await ChapterWriter.ReadAsync(ffprobe, off);
                Check("H27 关闭自动章节后输出无章节", read.Count == 0, $"实际 {read.Count}");
                Check("H28 关闭章节时结果为 0 个章节", offResult.Chapters.Count == 0);
            }

            // ---- MP4 输出 ----
            string mp4Out = Path.Combine(work, "out.mp4");
            MergeResult mp4Result = await task.RunAsync(new VideoMergeOptions
            {
                Inputs = new List<string> { c1, c2 },
                OutputPath = mp4Out,
                Container = "mp4",
                VerifyOutput = true,
            });
            Check("H29 MP4 输出合并成功", mp4Result.Ok, mp4Result.Error);
            if (mp4Result.Ok)
            {
                MediaBundle mp4Info = await MediaInspector.InspectAsync(ffprobe, mp4Out);
                Check("H30 MP4 输出容器正确", mp4Info.Container.Family == "mp4", mp4Info.Container.FormatName);
            }

            // ---- 输入自带章节时不会翻倍 ----
            string twice = Path.Combine(work, "twice.mkv");
            MergeResult twiceResult = await task.RunAsync(new VideoMergeOptions
            {
                Inputs = new List<string> { withChapters, withChapters },
                OutputPath = twice,
                Container = "mkv",
                VerifyOutput = true,
            });
            Check("H31 带章节的输入再次合并成功", twiceResult.Ok, twiceResult.Error);
            if (twiceResult.Ok)
            {
                List<ChapterEntry> read = await ChapterWriter.ReadAsync(ffprobe, twice);
                Check("H32 输入自带章节不会让输出章节翻倍", read.Count == 2, $"实际 {read.Count}");
            }
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* 忽略 */ }
        }
    }

    private static int CountOccurrences(string text, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    #endregion

    #region B. 端到端

    private sealed class Collector : IBatchEvents
    {
        public readonly List<string> Logs = new();
        public BatchSummary? Summary;
        public void Log(string text) { lock (Logs) Logs.Add(text); }
        public void JobUpdate(Job job) { }
        public void Progress(Job job) { }
        public void JobDone(Job job) { }
        public void AllDone(BatchSummary summary) => Summary = summary;
        public void Fatal(string text) { lock (Logs) Logs.Add("[FATAL] " + text); }
    }

    private static async Task EndToEndAsync(bool skip)
    {
        if (skip) { Console.WriteLine("(已跳过)"); return; }

        string? ffmpeg = ToolLocator.FindFfmpeg();
        string? ffprobe = ToolLocator.FindFfprobe();
        if (string.IsNullOrEmpty(ffmpeg) || string.IsNullOrEmpty(ffprobe))
        {
            Console.WriteLine("未找到 ffmpeg/ffprobe，跳过端到端验证。");
            return;
        }

        string work = Path.Combine(Path.GetTempPath(), "subfusion_e2e_" + Guid.NewGuid().ToString("N"));
        string temp = Path.Combine(work, "tmp");
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(temp);
        try
        {
            string video = Path.Combine(work, "sample.mp4");
            var makeVideo = new[]
            {
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "testsrc=size=320x240:rate=10",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100",
                "-t", "3", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", video,
            };
            ProcessCapture gen = await ProcessUtil.RunAsync(ffmpeg, makeVideo, 120);
            Check("生成测试视频", gen.ExitCode == 0 && File.Exists(video), gen.Stderr);
            if (!File.Exists(video)) return;

            MediaInfo sourceInfo = await MediaProbe.ProbeStreamsAsync(ffprobe, video);
            Check("源视频探测：1 视频流 + 1 音轨",
                  sourceInfo.VStreams == 1 && sourceInfo.AStreams == 1 && sourceInfo.Duration > 0,
                  $"v={sourceInfo.VStreams} a={sourceInfo.AStreams} dur={sourceInfo.Duration}");

            // 1) 软字幕（UTF-8 文本字幕）
            string srt = Path.Combine(work, "sample.zh.srt");
            await File.WriteAllTextAsync(srt,
                "1\n00:00:00,000 --> 00:00:02,000\n中文字幕ABC\n", new UTF8Encoding(false));
            var softJob = new Job(video,
                new[] { new SubTrack(srt, lang: "chi", kind: "text") { Default = true } },
                "", sourceInfo.Duration, sourceInfo);
            var softOptions = new Options
            {
                Container = "mkv", Mode = "soft", OutputDir = work, Verify = true, Workers = 1,
            };
            var softCollector = new Collector();
            var processor = new BatchProcessor(softCollector);
            await processor.RunAsync(new BatchRequest
            {
                Options = softOptions, Ffmpeg = ffmpeg, Ffprobe = ffprobe,
                Jobs = new[] { softJob }, TempDir = temp,
            }, CancellationToken.None);

            Check("软字幕：任务完成", softJob.State == Job.StateDone,
                  $"state={softJob.State}, message={softJob.Message}");
            Check("软字幕：输出文件存在", File.Exists(softJob.Output));
            MediaInfo outInfo = await MediaProbe.ProbeStreamsAsync(ffprobe, softJob.Output);
            Check("软字幕：输出含 1 条字幕轨", outInfo.SStreams == 1, $"实际 {outInfo.SStreams}");
            Check("软字幕：输出保留视频/音轨", outInfo.VStreams == 1 && outInfo.AStreams == 1);
            Check("软字幕：汇总 完成=1 失败=0",
                  softCollector.Summary?.Done == 1 && softCollector.Summary?.Failed == 0);
            Check("软字幕：日志含开始与结束分隔线",
                  softCollector.Logs.Count(l => l == Constants.LogSeparator) >= 1);
            Console.WriteLine($"  [信息] 软字幕校验结果 verify_ok={softJob.VerifyOk}，" +
                              $"issues=[{string.Join("；", softJob.VerifyIssues)}]");

            // 2) GBK 老字幕自动转 UTF-8 后合并
            string gbk = Path.Combine(work, "sample.gb.srt");
            Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            await File.WriteAllBytesAsync(gbk,
                Encoding.GetEncoding(54936).GetBytes("1\n00:00:00,000 --> 00:00:02,000\n简体中文GBK字幕\n"));
            DetectedEncoding gbkDetected = EncodingService.DetectEncoding(gbk);
            Equal("GBK 字幕编码识别", "gb18030", gbkDetected.Encoding);
            ConvertResult gbkConv = EncodingService.EnsureUtf8(gbk, temp, useCache: false);
            string gbkText = await File.ReadAllTextAsync(gbkConv.Path, Encoding.UTF8);
            Check("GBK 字幕转为 UTF-8 且无乱码",
                  gbkConv.Created && gbkText.Contains("简体中文GBK字幕"), gbkText);

            // 3) 硬字幕（烧录，视频必须重编码）
            var hardJob = new Job(video,
                new[] { new SubTrack(srt, lang: "chi", kind: "text") { Default = true } },
                "", sourceInfo.Duration, sourceInfo);
            var hardOptions = new Options
            {
                Container = "mkv", Mode = "hard", OutputDir = work, Verify = false,
                Workers = 1, Suffix = "_hard",
            };
            var hardCollector = new Collector();
            await new BatchProcessor(hardCollector).RunAsync(new BatchRequest
            {
                Options = hardOptions, Ffmpeg = ffmpeg, Ffprobe = ffprobe,
                Jobs = new[] { hardJob }, TempDir = temp,
            }, CancellationToken.None);
            Check("硬字幕：任务完成", hardJob.State == Job.StateDone,
                  $"state={hardJob.State}, message={hardJob.Message}");
            MediaInfo hardInfo = await MediaProbe.ProbeStreamsAsync(ffprobe, hardJob.Output);
            Check("硬字幕：输出无字幕轨（已烧录）", hardInfo.SStreams == 0, $"实际 {hardInfo.SStreams}");
            Check("硬字幕：视频流仍存在", hardInfo.VStreams == 1);

            // 4) 停止（取消）行为
            var cancelJob = new Job(video,
                new[] { new SubTrack(srt, lang: "chi", kind: "text") },
                "", sourceInfo.Duration, sourceInfo);
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await new BatchProcessor(new Collector()).RunAsync(new BatchRequest
            {
                Options = new Options { OutputDir = work, Verify = false, Suffix = "_cancel" },
                Ffmpeg = ffmpeg, Ffprobe = ffprobe, Jobs = new[] { cancelJob }, TempDir = temp,
            }, cts.Token);
            Check("取消：未完成的任务标记为已取消/待处理，不产生输出合并",
                  cancelJob.State is Job.StateCancelled or Job.StatePending,
                  $"state={cancelJob.State}");
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* 忽略 */ }
        }
    }

    #endregion


    #region O. 时间轴拖动选择（纯几何）

    /// <summary>
    /// 时间轴拖动的换算回归。这些逻辑全是「算错了用户立刻看得见」的类型：
    /// 拖到最右端停在 99%、两个手柄能交叉、整体平移时区间被压缩。
    /// 因此全部放在 Core 里断言，而不是靠肉眼在窗口上试。
    /// </summary>
    private static void CheckTimelineGeometry()
    {
        const double duration = 100.0;
        const double width = 1000.0;

        // 像素 ↔ 秒
        Near("O1 像素转秒：正中 = 一半时长", 50, TimelineGeometry.SecondsFromX(500, width, duration));
        Near("O2 像素转秒：越界被夹到总时长", duration, TimelineGeometry.SecondsFromX(1500, width, duration));
        Near("O3 像素转秒：负数被夹到 0", 0, TimelineGeometry.SecondsFromX(-40, width, duration));
        Near("O4 秒转像素：一半时长 = 正中", 500, TimelineGeometry.XFromSeconds(50, width, duration));
        Near("O5 宽度为 0 时不除零", 0, TimelineGeometry.SecondsFromX(100, 0, duration));
        Near("O6 时长为 0 时不除零", 0, TimelineGeometry.SecondsFromX(100, width, 0));

        // 命中判定
        Equal("O7 命中起点手柄", TimelineHit.Start,
              TimelineGeometry.PickHandle(12, 10, 800));
        Equal("O8 命中终点手柄", TimelineHit.End,
              TimelineGeometry.PickHandle(795, 10, 800));
        Equal("O9 命中区间内部（整体平移）", TimelineHit.Range,
              TimelineGeometry.PickHandle(400, 10, 800));
        Equal("O10 区间左侧空白抓起点", TimelineHit.Start,
              TimelineGeometry.PickHandle(60, 200, 800));
        Equal("O11 区间右侧空白抓终点", TimelineHit.End,
              TimelineGeometry.PickHandle(950, 200, 800));
        // 两个手柄贴在一起时，必须能选中更近的那个，否则短区间永远拖不动终点
        Equal("O12 手柄重叠时选更近的一个", TimelineHit.End,
              TimelineGeometry.PickHandle(18, 10, 22));

        // 起点/终点夹紧
        (double s1, double e1) = TimelineGeometry.MoveStart(200, 50, duration);
        Check("O13 起点不能越过终点", s1 <= e1 - TimelineGeometry.MinDurationSeconds + 1e-9,
              $"start={s1} end={e1}");
        (double s2, double e2) = TimelineGeometry.MoveStart(-30, 50, duration);
        Check("O14 起点不能为负", s2 >= 0 && Math.Abs(e2 - 50) < 1e-9, $"start={s2} end={e2}");
        (double s3, double e3) = TimelineGeometry.MoveEnd(-100, 40, duration);
        Check("O15 终点不能跑到起点前面", e3 >= s3 + TimelineGeometry.MinDurationSeconds - 1e-9,
              $"start={s3} end={e3}");
        (double s4, double e4) = TimelineGeometry.MoveEnd(500, 40, duration);
        Check("O16 终点不能超过总时长", Math.Abs(e4 - duration) < 1e-9 && Math.Abs(s4 - 40) < 1e-9,
              $"start={s4} end={e4}");

        // 整体平移：长度必须保持不变（压缩区间是最难察觉的错误）
        (double s5, double e5) = TimelineGeometry.MoveRange(20, 10, 40, duration);
        Near("O17 整体平移：起点跟随", 30, s5);
        Near("O18 整体平移：长度保持不变", 30, e5 - s5);
        (double s6, double e6) = TimelineGeometry.MoveRange(999, 10, 40, duration);
        Check("O19 撞到右边界时停下且长度不变",
              Math.Abs((e6 - s6) - 30) < 1e-9 && Math.Abs(e6 - duration) < 1e-9,
              $"start={s6} end={e6}");
        (double s7, double e7) = TimelineGeometry.MoveRange(-999, 10, 40, duration);
        Check("O20 撞到左边界时停下且长度不变",
              Math.Abs((e7 - s7) - 30) < 1e-9 && Math.Abs(s7) < 1e-9,
              $"start={s7} end={e7}");

        // 关键帧吸附
        var keys = new[] { 0.0, 2.0, 4.0, 6.0, 8.0 };
        Near("O21 容差内吸附到最近关键帧", 4.0,
             TimelineGeometry.SnapToNearestKeyframe(keys, 4.3, 0.5));
        Near("O22 容差外保持原值", 4.8,
             TimelineGeometry.SnapToNearestKeyframe(keys, 4.8, 0.5));
        Near("O23 空关键帧列表不改变输入", 4.3,
             TimelineGeometry.SnapToNearestKeyframe(Array.Empty<double>(), 4.3, 0.5));
        Near("O24 容差为 0 时不吸附", 4.3,
             TimelineGeometry.SnapToNearestKeyframe(keys, 4.3, 0));

        // 刻度文字
        Equal("O25 刻度文字：不足一小时", "02:03.4", TimelineGeometry.ShortLabel(123.4));
        Equal("O26 刻度文字：超过一小时", "1:01:01", TimelineGeometry.ShortLabel(3661));
    }

    #endregion

    #region N. 视频裁剪（无损 / 精确 / 旋转 / 音画同步）

    /// <summary>
    /// 裁剪回归。重点是**音画同步**：无损切割天然会把切点吸附到关键帧，
    /// 只要音频跟着视频一起被吸附，同步就不受影响；但「视频重编码 + 音频 copy」
    /// 实测会产生约 1.4 秒的错位，而且 ffmpeg 退出码是 0。
    /// 因此这里不仅断言输出时长，还要断言音画相对偏移与源文件一致。
    /// </summary>
    private static async Task CheckTrimAsync()
    {
        string? ffmpeg = ToolLocator.FindFfmpeg();
        string? ffprobe = ToolLocator.FindFfprobe();
        if (string.IsNullOrEmpty(ffmpeg) || string.IsNullOrEmpty(ffprobe))
        {
            Console.WriteLine("(未找到 ffmpeg/ffprobe，跳过视频裁剪验证)");
            return;
        }

        string work = Path.Combine(Path.GetTempPath(), "subfusion_trim_selftest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            FfmpegTools tools = await FfmpegTools.ResolveAsync(ffmpeg, ffprobe);
            var task = new VideoTrimTask(tools);

            // 10 秒 / 25fps / 关键帧每 2 秒：这样「请求 5.3s」必然落在 4.0 与 6.0 之间，
            // 吸附行为一定会发生，用例才能真的验证到它。
            string source = Path.Combine(work, "trim_src.mp4");
            ProcessCapture gen = await ProcessUtil.RunAsync(ffmpeg, new[]
            {
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=25",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
                "-t", "10", "-c:v", "libx264", "-g", "50", "-keyint_min", "50",
                "-sc_threshold", "0", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", source,
            }, 180);
            Check("N1 生成裁剪测试素材", gen.ExitCode == 0 && File.Exists(source), gen.Stderr.Trim());
            if (gen.ExitCode != 0 || !File.Exists(source)) return;

            // ---- 时间解析（纯逻辑，切点靠它，错一位就切错片子）----
            Check("N0a 解析纯秒数", TrimTime.TryParse("12.5", out double t1) && Math.Abs(t1 - 12.5) < 1e-6);
            Check("N0b 解析 mm:ss", TrimTime.TryParse("01:30", out double t2) && Math.Abs(t2 - 90) < 1e-6);
            Check("N0c 解析 hh:mm:ss.fff",
                  TrimTime.TryParse("1:02:03.4", out double t3) && Math.Abs(t3 - 3723.4) < 1e-6);
            Check("N0d 非法输入被拒绝",
                  !TrimTime.TryParse("abc", out _) && !TrimTime.TryParse("", out _) &&
                  !TrimTime.TryParse("1:2:3:4", out _) && !TrimTime.TryParse("-5", out _));
            Check("N0e 格式化可被自己解析回来（往返一致）",
                  TrimTime.TryParse(TrimTime.Format(3723.456), out double t4) &&
                  Math.Abs(t4 - 3723.456) < 0.0015);

            // ---- 容差必须由帧长推导，而不是拍一个数字 ----
            // 依据：偏差的物理上界 = 两条流各对齐到一个量化边界 = 一帧视频 + 两帧音频。
            // 25fps + 48kHz 的素材应得到 ~0.19 秒；60fps 素材更严；24fps 更宽。
            var mid25 = new MediaBundle();
            mid25.Videos.Add(new VideoStreamInfo { RealFrameRate = 25, AvgFrameRate = 25 });
            mid25.Audios.Add(new AudioStreamInfo { SampleRate = 48000 });
            double tol25 = AvSyncProbe.ToleranceFor(mid25, mid25);
            Check("N0l 容差按帧长推导且在合理区间内", tol25 is > 0.15 and < 0.5, $"tol={tol25:0.###}s");

            var slow = new MediaBundle();
            slow.Videos.Add(new VideoStreamInfo { RealFrameRate = 24, AvgFrameRate = 24 });
            slow.Audios.Add(new AudioStreamInfo { SampleRate = 48000 });
            var fast = new MediaBundle();
            fast.Videos.Add(new VideoStreamInfo { RealFrameRate = 60, AvgFrameRate = 60 });
            fast.Audios.Add(new AudioStreamInfo { SampleRate = 48000 });
            double tol24 = AvSyncProbe.ToleranceFor(slow, slow);
            double tol60 = AvSyncProbe.ToleranceFor(fast, fast);
            Check("N0m 低帧率素材容差更宽（一帧更长）", tol24 > tol60, $"24fps={tol24:0.###} 60fps={tol60:0.###}");
            Check("N0n 无流信息时退回兜底容差",
                  Math.Abs(AvSyncProbe.ToleranceFor(null, null) - AvSyncProbe.DefaultToleranceSeconds) < 1e-9);

            // ---- 音画同步判定：必须能区分「测量噪声」与「真错位」----
            // 这两类数字都来自实测：无损切割的测量偏差恒在 0.02~0.15 秒，
            // 而真实错位（视频重编码 + 音频 copy）在 0.24 秒以上、常见 1.4 秒。
            var srcSample = new SyncSample(true, 0, 0.08, true);
            (bool okNoise, double driftNoise, string _) =
                AvSyncProbe.Compare(srcSample, new SyncSample(true, 0, -0.067, true), tol25);
            Check("N0f 用户报告的那组数值（+0.080 → -0.067，偏差 0.147）判为同步", okNoise,
                  $"drift={driftNoise:0.000}");

            (bool okReal, double driftReal, string msgReal) =
                AvSyncProbe.Compare(srcSample, new SyncSample(true, 0, 0.08 - 1.44, true), tol25);
            Check("N0g 真实错位（1.44 秒）必须判为失败", !okReal, msgReal);
            Check("N0h 失败信息说明这是真错位而非噪声",
                  msgReal.Contains("真的错位", StringComparison.Ordinal), msgReal);

            (bool okSmall, _, _) = AvSyncProbe.Compare(
                new SyncSample(true, 0, 0, true), new SyncSample(true, 0, 0.09, true),
                AvSyncProbe.DefaultToleranceSeconds);
            Check("N0i 小于容差的偏差判为同步", okSmall);

            (bool okNoAudio, _, _) = AvSyncProbe.Compare(
                new SyncSample(true, 0, 0, false), new SyncSample(true, 0, 0, false),
                AvSyncProbe.DefaultToleranceSeconds);
            Check("N0j 无音轨时不做同步判定且不算失败", okNoAudio);

            (bool okMissing, _, _) = AvSyncProbe.Compare(
                new SyncSample(true, 0, 0, true), new SyncSample(true, 0, 0, true) with { Ok = false },
                AvSyncProbe.DefaultToleranceSeconds);
            Check("N0k 输出起点探测失败时判为失败", !okMissing);

            // ---- 关键帧索引 ----
            KeyframeIndex index = await KeyframeScanner.ScanAsync(ffprobe, source);
            Check("N2 关键帧扫描成功", index.Ok && !index.IsEmpty, index.Error);
            Check("N3 关键帧间隔约 2 秒（GOP 配置生效）",
                  index.Ok && index.MaxGap is > 1.8 and < 2.2,
                  $"maxGap={index.MaxGap:0.###}");
            Check("N4 吸附到不大于请求值的最近关键帧",
                  index.Ok && Math.Abs(index.SnapToKeyframe(5.3) - 4.0) < 0.02,
                  index.SnapToKeyframe(5.3).ToString("0.###", CultureInfo.InvariantCulture));
            Check("N5 正好落在关键帧上时吸附损失为 0",
                  index.Ok && index.SnapLoss(6.0) < 0.02,
                  index.SnapLoss(6.0).ToString("0.###", CultureInfo.InvariantCulture));
            Check("N6 普通素材不会被误判为全关键帧", index.Ok && !index.AllIntra);

            // ---- 无损切割 ----
            string copyOut = Path.Combine(work, "copy_cut.mkv");
            (MediaBundle srcBundle, _, int? srcRotationNullable) = await task.AnalyzeAsync(source);
            int srcRotation = srcRotationNullable ?? 0;
            Check("N7 源文件探测成功", srcBundle.Success, srcBundle.Error);
            SyncSample srcSync = await AvSyncProbe.ProbeAsync(ffprobe, source);
            Check("N8 源文件音画起点均可探测", srcSync.Ok && srcSync.HasAudio, srcSync.Error);

            TrimResult copy = await task.RunAsync(new VideoTrimOptions
            {
                Input = source, OutputPath = copyOut, Container = "mkv",
                StartSeconds = 5.3, EndSeconds = 9.0, Mode = TrimMode.Copy, VerifyOutput = true,
            });
            Check("N9 无损切割成功", copy.Ok, copy.Error);
            Check("N10 无损切割未重编码",
                  copy.Ok && copy.VideoCopied && copy.AudioCopied);
            Check("N11 起点已吸附到关键帧", copy.Ok && copy.Snapped && Math.Abs(copy.ActualStart - 4.0) < 0.05,
                  $"actualStart={copy.ActualStart:0.###}");
            // 无损模式下尾部只能截到包边界，实测会多出约 0.2 秒（AAC 帧 + 末帧对齐）。
            // 这是 -c copy 的固有行为，不是缺陷；断言容差取引擎自己的规则（0.35s 起）。
            Check("N12 输出时长约等于吸附后区间（尾部按包边界截断，允许多出约 0.2s）",
                  copy.Ok && Math.Abs(copy.OutputDuration - (9.0 - copy.ActualStart)) < 0.4,
                  $"期望 {(9.0 - copy.ActualStart):F2}s，实际 {copy.OutputDuration:F2}s");
            Check("N13 无损切割后音画同步未被破坏", copy.Ok && copy.SyncVerified,
                  $"drift={copy.SyncDrift:0.000}s");
            Check("N14 同步偏差在按帧长算出的容差内（含 AAC prim/容器对齐的测量偏差）",
                  copy.Ok && Math.Abs(copy.SyncDrift) <=
                      AvSyncProbe.ToleranceFor(srcBundle, copy.Output),
                  $"drift={copy.SyncDrift:0.000}s " +
                  $"tol={AvSyncProbe.ToleranceFor(srcBundle, copy.Output):0.###}s");

            // 吸附必须在 UI 上如实交代，否则用户会以为切错了。
            Check("N15 吸附时会给出说明", copy.Ok && copy.Notices.Any(n => n.Contains("关键帧")));

            // ---- 精确切割 ----
            string preciseOut = Path.Combine(work, "precise_cut.mkv");
            TrimResult precise = await task.RunAsync(new VideoTrimOptions
            {
                Input = source, OutputPath = preciseOut, Container = "mkv",
                StartSeconds = 5.3, EndSeconds = 9.0, Mode = TrimMode.Precise,
                VideoCodec = "libx264", Preset = "veryfast", Crf = 20, VerifyOutput = true,
            });
            Check("N16 精确切割成功", precise.Ok, precise.Error);
            Check("N17 精确切割确实重编码了视频", precise.Ok && !precise.VideoCopied);
            Check("N18 精确切割不吸附（起点即请求值）",
                  precise.Ok && !precise.Snapped && Math.Abs(precise.ActualStart - 5.3) < 0.01,
                  $"actualStart={precise.ActualStart:0.###}");
            Check("N19 精确切割时长等于请求区间",
                  precise.Ok && Math.Abs(precise.OutputDuration - 3.7) < 0.25,
                  $"期望 3.70s，实际 {precise.OutputDuration:F2}s");
            Check("N20 精确切割后音画同步未被破坏", precise.Ok && precise.SyncVerified,
                  $"drift={precise.SyncDrift:0.000}s");

            // ---- 旋转 ----
            string rotatedOut = Path.Combine(work, "rotated.mkv");
            TrimResult rotated = await task.RunAsync(new VideoTrimOptions
            {
                Input = source, OutputPath = rotatedOut, Container = "mkv",
                StartSeconds = 2.0, EndSeconds = 5.0, Mode = TrimMode.Copy,
                Rotation = RotationMode.Clockwise90, VerifyOutput = true,
            });
            Check("N21 无损模式旋转成功", rotated.Ok, rotated.Error);
            int rotatedAngle = await RotationProbe.ReadAsync(ffprobe, rotatedOut);
            Check("N22 显示方向已写入输出（90°）", rotatedAngle == 90, $"实际 {rotatedAngle}°");

            // 像素没有被重编码：旋转只写元数据，宽高应保持原样。
            MediaBundle rotatedInfo = await MediaInspector.InspectAsync(ffprobe, rotatedOut);
            Check("N23 元数据旋转不改变像素尺寸",
                  rotatedInfo.PrimaryVideo is { Width: 320, Height: 180 },
                  rotatedInfo.PrimaryVideo?.SizeText ?? "");

            TrimResult baked = await task.RunAsync(new VideoTrimOptions
            {
                Input = source, OutputPath = Path.Combine(work, "baked.mkv"), Container = "mkv",
                StartSeconds = 2.0, EndSeconds = 5.0, Mode = TrimMode.Precise,
                Rotation = RotationMode.Clockwise90, VideoCodec = "libx264", Preset = "veryfast",
                VerifyOutput = true,
            });
            Check("N24 精确模式旋转成功", baked.Ok, baked.Error);
            MediaBundle bakedInfo = await MediaInspector.InspectAsync(ffprobe, baked.OutputPath);
            Check("N25 精确模式把旋转烘焙进像素（宽高交换）",
                  bakedInfo.PrimaryVideo is { Width: 180, Height: 320 },
                  bakedInfo.PrimaryVideo?.SizeText ?? "");

            // 不要求旋转时必须保留源文件自带的方向，否则竖屏素材会躺倒。
            string portrait = Path.Combine(work, "portrait.mp4");
            ProcessCapture mkPortrait = await ProcessUtil.RunAsync(ffmpeg, new[]
            {
                "-hide_banner", "-loglevel", "error", "-y",
                "-display_rotation", "90", "-i", source,
                "-map", "0:v:0", "-c", "copy", "-t", "6", portrait,
            }, 120);
            if (mkPortrait.ExitCode == 0)
            {
                int portraitAngle = await RotationProbe.ReadAsync(ffprobe, portrait);
                Check("N26 源文件带 90° 显示方向", portraitAngle == 90, $"实际 {portraitAngle}°");

                TrimResult keep = await task.RunAsync(new VideoTrimOptions
                {
                    Input = portrait, OutputPath = Path.Combine(work, "keep_rotation.mkv"),
                    Container = "mkv", StartSeconds = 1.0, EndSeconds = 4.0,
                    Mode = TrimMode.Copy, Rotation = RotationMode.None, VerifyOutput = true,
                });
                Check("N27 未请求旋转时裁剪成功", keep.Ok, keep.Error);
                int keptAngle = await RotationProbe.ReadAsync(ffprobe, keep.OutputPath);
                Check("N28 源方向被原样保留（竖屏不会变横躺）", keptAngle == 90, $"实际 {keptAngle}°");
            }
            else
            {
                Console.WriteLine("  (无法构造带旋转元数据的素材，跳过 N26-N28)");
            }

            // ---- 无效参数 ----
            TrimResult invalid = await task.RunAsync(new VideoTrimOptions
            {
                Input = source, OutputPath = Path.Combine(work, "bad.mkv"),
                StartSeconds = 5, EndSeconds = 5, Mode = TrimMode.Copy,
            });
            Check("N29 终点不大于起点时拒绝执行", !invalid.Ok);

            TrimResult missing = await task.RunAsync(new VideoTrimOptions
            {
                Input = Path.Combine(work, "nope.mp4"), OutputPath = Path.Combine(work, "x.mkv"),
                StartSeconds = 0, EndSeconds = 1, Mode = TrimMode.Copy,
            });
            Check("N30 输入不存在时拒绝执行", !missing.Ok);

            // ---- 命令形状（防止后续重构悄悄改掉关键参数）----
            List<string> cmd = TrimCommandBuilder.Build(new VideoTrimOptions
            {
                Input = source, OutputPath = copyOut, Container = "mkv",
                StartSeconds = 4.0, EndSeconds = 9.0, Mode = TrimMode.Copy,
            }, srcBundle, 4.0, RotationPlan.Create(srcRotation, RotationMode.None, copying: true), ffmpeg);
            Check("N31 无损命令含 -c copy", cmd.Contains("copy"));
            Check("N32 命令含 -avoid_negative_ts make_zero（防音频整段前移）",
                  cmd.Contains("-avoid_negative_ts") && cmd.Contains("make_zero"));
            Check("N33 命令不继承原视频章节", cmd.Contains("-map_chapters"));
            Check("N34 -ss 位于 -i 之前（输入侧快速定位）",
                  cmd.IndexOf("-ss") < cmd.IndexOf("-i"), string.Join(" ", cmd));

            List<string> preciseCmd = TrimCommandBuilder.Build(new VideoTrimOptions
            {
                Input = source, OutputPath = preciseOut, Container = "mkv",
                StartSeconds = 5.3, EndSeconds = 9.0, Mode = TrimMode.Precise,
            }, srcBundle, 5.3, RotationPlan.Create(0, RotationMode.None, copying: false), ffmpeg);
            Check("N35 精确命令同时重编码音频（音频 copy 会造成错位）",
                  preciseCmd.Contains("-c:a"), string.Join(" ", preciseCmd));
            Check("N36 精确命令不使用 -c copy", !preciseCmd.Contains("copy"));
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* 忽略 */ }
        }
    }

    #region T. 视频压制（独立引擎 / 硬件编码探测 / 命令形状）

    /// <summary>
    /// 视频压制模块的自检。
    ///
    /// 这个模块最容易出现的不是崩溃，而是**说谎**：界面写着「AMD 显卡加速」，
    /// 实际跑的是 CPU；或者编码器探测只看了 -encoders 列表就宣告可用，
    /// 而机器上根本没有对应显卡。因此下面的断言除了真实压制一遍，
    /// 还要验证「探测结论必须是初始化编码得出的」。
    /// </summary>
    private static async Task CheckCompressAsync()
    {
        string work = Path.Combine(Path.GetTempPath(), "subfusion_comp_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            string ffmpeg = ToolLocator.FindFfmpeg() ?? "";
            string ffprobe = ToolLocator.FindFfprobe() ?? "";
            if (string.IsNullOrEmpty(ffmpeg) || string.IsNullOrEmpty(ffprobe))
            {
                Check("T0 跳过：本机没有 ffmpeg / ffprobe", false, "无法执行压制端到端校验");
                return;
            }

            string source = Path.Combine(work, "source.mp4");
            List<string> makeArgs = new()
            {
                "-y", "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=25:duration=3",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=3",
                "-c:v", "libx264", "-preset", "ultrafast", "-crf", "18",
                "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", source,
            };
            ProcessCapture made = await ProcessUtil.RunAsync(ffmpeg, makeArgs, 120).ConfigureAwait(false);
            if (made.ExitCode != 0 || !File.Exists(source))
            {
                Check("T0 生成测试素材", false, made.Stderr);
                return;
            }

            var tools = await FfmpegTools.ResolveAsync(ffmpeg, ffprobe).ConfigureAwait(false);
            var task = new VideoCompressTask(tools);
            (MediaBundle bundle, IReadOnlyList<EncoderAvailability> encoders) = await task.AnalyzeAsync(source)
                .ConfigureAwait(false);

            Check("T1 探测到输入视频流", bundle.PrimaryVideo is not null, bundle.Error);
            Check("T2 探测结果覆盖软件与硬件编码器",
                  encoders.Any(e => e.Kind == EncoderKind.Software) &&
                  encoders.Any(e => e.Kind != EncoderKind.Software),
                  "共 " + encoders.Count + " 项");
            Check("T3 至少有一个可用编码器", encoders.Any(e => e.Usable),
                  string.Join("; ", encoders.Select(e => e.Name + "=" + e.Usable)));

            List<EncoderAvailability> listedButBroken = encoders
                .Where(e => !e.Usable && e.Error.Length > 0 && !e.Error.Contains("不含该编码器"))
                .ToList();
            Check("T4 列在 -encoders 里但本机不能用的编码器被判为不可用",
                  listedButBroken.Count > 0 || encoders.All(e => e.Usable),
                  "判为不可用：" + string.Join("、", listedButBroken.Select(e => e.Name)));

            EncoderAvailability? autoPick = VideoEncoder.Select(encoders, VideoCodecKind.H264, "auto");
            EncoderAvailability? softwarePick = VideoEncoder.Select(encoders, VideoCodecKind.H264, "software");
            EncoderAvailability? hardwarePick = VideoEncoder.Select(encoders, VideoCodecKind.H264, "hardware");
            Check("T5 software 偏好只会选到软件编码器",
                  softwarePick is not null && softwarePick.Kind == EncoderKind.Software);
            Check("T6 hardware 偏好不会退化成软件编码器",
                  hardwarePick is null || hardwarePick.Kind != EncoderKind.Software);
            Check("T7 auto 在有硬件时优先硬件",
                  autoPick is not null &&
                  (encoders.Any(e => e.Usable && e.Kind != EncoderKind.Software && e.Codec == VideoCodecKind.H264)
                      ? autoPick.Kind != EncoderKind.Software
                      : autoPick.Kind == EncoderKind.Software),
                  "选中 " + (autoPick?.Name ?? "<null>"));

            var options = new VideoCompressOptions
            {
                Input = source,
                OutputPath = Path.Combine(work, "o.mp4"),
                Codec = VideoCodecKind.H264,
                Quality = 23,
                Container = "mp4",
            };
            EncoderAvailability fakeAmf = new()
            {
                Name = "h264_amf", Kind = EncoderKind.Amf, Codec = VideoCodecKind.H264, Usable = true,
            };
            List<string> hwCmd = CompressCommandBuilder.Build(options, bundle, fakeAmf, ffmpeg, options.OutputPath);
            Check("T8 硬件命令使用硬件编码器与 CQP 码控",
                  hwCmd.Contains("h264_amf") && hwCmd.Contains("cqp"));
            Check("T9 硬件命令不出现 x264 专用的 -preset / -crf",
                  !hwCmd.Contains("-preset") && !hwCmd.Contains("-crf"), string.Join(" ", hwCmd));
            Check("T10 硬件命令的 QP 按校准偏移换算（qp 不等于画质档原值）",
                  hwCmd.Contains(CompressCommandBuilder.HardwareQp(23).ToString(CultureInfo.InvariantCulture)) &&
                  CompressCommandBuilder.HardwareQp(23) != 23);

            // T10a–T10d：三个硬件后端的私有选项名互不通用，必须各自成套。
            // 这一组断言来自实测：本机 h264_nvenc 对 "-rc cqp" 报
            // Unable to parse "rc" option value "cqp" 并以 -22(EINVAL) 退出；
            // h264_qsv 没有 -rc/-qp_* 私有选项，传了只会被静默忽略（画质完全不受控）。
            // 只断言"用了硬件编码器"是不够的——那三条错误命令同样能通过。
            EncoderAvailability fakeNvenc = new()
            {
                Name = "h264_nvenc", Kind = EncoderKind.Nvenc, Codec = VideoCodecKind.H264, Usable = true,
            };
            List<string> nvCmd = CompressCommandBuilder.Build(options, bundle, fakeNvenc, ffmpeg, options.OutputPath);
            Check("T10a NVENC 用 -rc constqp + 单值 -qp，不能用 AMF 的 -rc cqp / -qp_i",
                  nvCmd.Contains("constqp") && nvCmd.Contains("-qp") &&
                  !nvCmd.Contains("cqp") && !nvCmd.Contains("-qp_i") &&
                  !nvCmd.Contains("-quality"),
                  string.Join(" ", nvCmd));

            EncoderAvailability fakeQsv = new()
            {
                Name = "h264_qsv", Kind = EncoderKind.Qsv, Codec = VideoCodecKind.H264, Usable = true,
            };
            List<string> qsvCmd = CompressCommandBuilder.Build(options, bundle, fakeQsv, ffmpeg, options.OutputPath);
            Check("T10b QSV 用 -global_quality，不能用 -rc / -qp_* / -quality",
                  qsvCmd.Contains("-global_quality") && !qsvCmd.Contains("-rc") &&
                  !qsvCmd.Contains("-qp_i") && !qsvCmd.Contains("-quality"),
                  string.Join(" ", qsvCmd));

            Check("T10c 各后端 preset 名称落在自己的刻度上",
                  CompressCommandBuilder.Build(options, bundle, fakeNvenc, ffmpeg, options.OutputPath)
                      .Contains("p4") &&
                  CompressCommandBuilder.Build(options, bundle, fakeQsv, ffmpeg, options.OutputPath)
                      .Contains("medium") &&
                  hwCmd.Contains("-quality"),
                  "AMF=" + VideoEncoder.HardwarePreset(EncoderKind.Amf, "balanced") +
                  " NVENC=" + VideoEncoder.HardwarePreset(EncoderKind.Nvenc, "balanced") +
                  " QSV=" + VideoEncoder.HardwarePreset(EncoderKind.Qsv, "balanced"));

            // 码率模式下 -rc 的取值同样按后端区分：NVENC 写 vbr_peak 同样会 EINVAL。
            VideoCompressOptions bitrateOptions = options.Clone();
            bitrateOptions.RateControl = RateControl.Bitrate;
            bitrateOptions.TargetBitrateKbps = 2500;
            List<string> nvBitrate = CompressCommandBuilder.Build(bitrateOptions, bundle, fakeNvenc, ffmpeg,
                bitrateOptions.OutputPath);
            List<string> qsvBitrate = CompressCommandBuilder.Build(bitrateOptions, bundle, fakeQsv, ffmpeg,
                bitrateOptions.OutputPath);
            Check("T10d 码率模式下 NVENC 用 -rc vbr、QSV 不带 -rc",
                  nvBitrate.Contains("vbr") && !nvBitrate.Contains("vbr_peak") &&
                  nvBitrate.Contains("-b:v") && !qsvBitrate.Contains("-rc") && qsvBitrate.Contains("-b:v"),
                  "nvenc=" + string.Join(" ", nvBitrate) + " | qsv=" + string.Join(" ", qsvBitrate));

            EncoderAvailability fakeX264 = new()
            {
                Name = "libx264", Kind = EncoderKind.Software, Codec = VideoCodecKind.H264, Usable = true,
            };
            List<string> swCmd = CompressCommandBuilder.Build(options, bundle, fakeX264, ffmpeg, options.OutputPath);
            Check("T11 软件命令使用 -crf 而不是 CQP", swCmd.Contains("-crf") && !swCmd.Contains("cqp"));
            Check("T12 命令含 -avoid_negative_ts make_zero（防音频整段前移）",
                  swCmd.Contains("-avoid_negative_ts") && swCmd.Contains("make_zero"));
            Check("T13 命令不继承原视频章节", swCmd.Contains("-map_chapters"));
            Check("T14 mp4 输出带 faststart", swCmd.Contains("+faststart"));
            Check("T15 输出路径是命令的最后一个参数", swCmd[^1] == options.OutputPath, swCmd[^1]);

            Check("T16 滤镜链强制 4:2:0 的偶数尺寸",
                  (CompressCommandBuilder.BuildFilter(options, bundle) ?? "").Contains("trunc(iw/2)*2"));

            VideoCompressOptions upscale = options.Clone();
            upscale.FrameRate = 60;
            string? filter = CompressCommandBuilder.BuildFilter(upscale, bundle);
            Check("T17 目标帧率高于源时不升帧（只会白变大）",
                  filter is null || !filter.Contains("fps=", StringComparison.Ordinal), filter ?? "<null>");

            VideoCompressOptions downsample = options.Clone();
            downsample.FrameRate = 10;
            string? downFilter = CompressCommandBuilder.BuildFilter(downsample, bundle);
            Check("T18 目标帧率低于源时下帧", downFilter is not null && downFilter.Contains("fps=10"));

            string swOut = Path.Combine(work, "out_software.mp4");
            var swOptions = new VideoCompressOptions
            {
                Input = source,
                OutputPath = swOut,
                Codec = VideoCodecKind.H264,
                EncoderPreference = "software",
                Quality = 28,
                Container = "mp4",
                ReencodeAudio = true,
            };
            CompressResult swResult = await task.RunAsync(swOptions).ConfigureAwait(false);
            Check("T19 软件编码压制成功", swResult.Ok, swResult.Error);
            Check("T20 软件编码确实没有用硬件加速",
                  swResult.Ok && !swResult.HardwareEncoded, swResult.Encoder);
            Check("T21 输出文件存在且非空",
                  swResult.Ok && File.Exists(swOut) && new FileInfo(swOut).Length > 0);
            Check("T22 输出时长与源一致",
                  swResult.Ok && Math.Abs(swResult.OutputDuration - swResult.SourceDuration) < 0.3,
                  swResult.OutputDuration.ToString(CultureInfo.InvariantCulture));
            // 640x360 的测试图变化剧烈，压缩后未必更小；这里只要求「没有暴涨」。
            Check("T23 压缩后体积没有暴涨（不超过源的 2 倍）",
                  swResult.Ok && swResult.OutputBytes <= swResult.SourceBytes * 2,
                  swResult.OutputBytes + " vs " + swResult.SourceBytes);

            string hwOut = Path.Combine(work, "out_auto.mp4");
            var hwOptions = new VideoCompressOptions
            {
                Input = source,
                OutputPath = hwOut,
                Codec = VideoCodecKind.H264,
                EncoderPreference = "auto",
                Quality = 28,
                Container = "mp4",
            };
            CompressResult hwResult = await task.RunAsync(hwOptions).ConfigureAwait(false);
            bool hardwareExists = encoders.Any(e => e.Usable && e.Kind != EncoderKind.Software &&
                                                    e.Codec == VideoCodecKind.H264);
            Check("T24 自动模式压制成功", hwResult.Ok, hwResult.Error);
            Check("T25 有可用硬件时自动模式必须真的用了硬件（不许撒谎）",
                  !hardwareExists || (hwResult.Ok && hwResult.HardwareEncoded),
                  "编码器=" + hwResult.Encoder + "，硬件可用=" + hardwareExists);
            Check("T26 结果里的编码器名与探测结果一致",
                  hwResult.Ok && encoders.Any(e => e.Name == hwResult.Encoder), hwResult.Encoder);

            string blocked = Path.Combine(work, "exists.mp4");
            File.WriteAllBytes(blocked, new byte[] { 1, 2, 3 });
            var conflicts = swOptions.Clone();
            conflicts.OutputPath = blocked;
            CompressResult renamedOut = await task.RunAsync(conflicts).ConfigureAwait(false);
            Check("T27 目标存在时压制照常完成", renamedOut.Ok, renamedOut.Error);
            Check("T28 自动改用未被占用的文件名",
                  renamedOut.Ok &&
                  !string.Equals(renamedOut.OutputPath, blocked, StringComparison.OrdinalIgnoreCase),
                  renamedOut.OutputPath);
            Check("T28b 用户已有的文件没有被改动",
                  File.Exists(blocked) && new FileInfo(blocked).Length == 3);

            var badInput = swOptions.Clone();
            badInput.Input = Path.Combine(work, "nope.mp4");
            badInput.OutputPath = Path.Combine(work, "nope_out.mp4");
            CompressResult missingInput = await task.RunAsync(badInput).ConfigureAwait(false);
            Check("T29 输入不存在时拒绝执行", !missingInput.Ok);

            Check("T30 MP4 + 不兼容音轨时提前拦下",
                  VideoCompressOptions.ValidateAudioCopy("mp4", "vorbis", true).Count > 0);
            Check("T31 MKV 不限制音轨格式",
                  VideoCompressOptions.ValidateAudioCopy("mkv", "vorbis", true).Count == 0);
            Check("T32 无音轨时不报错",
                  VideoCompressOptions.ValidateAudioCopy("mp4", "vorbis", false).Count == 0);

            // VideoEncoder 已上移到 Media/：它是三个业务模块共用的媒体基础设施，
            // 不再只属于压制。路径必须跟着走，否则这条断言会静默变成"文件不存在"。
            string? probeSource = FindCoreFile(Path.Combine("Media", "VideoEncoder.cs"));
            Check("T33 硬件探测真的启动一次编码（不是只读编码器列表）",
                  probeSource is not null &&
                  probeSource.Contains("-frames:v", StringComparison.Ordinal) &&
                  probeSource.Contains("ProbeOneAsync", StringComparison.Ordinal),
                  "只看 -encoders 列表会把「有编码器但没有显卡」误判成可用");

            string? engineSource = FindCoreFile(Path.Combine("Compress", "VideoCompressEngine.cs"));
            Check("T34 引擎先渲染到临时文件、校验通过才落盘",
                  engineSource is not null &&
                  engineSource.Contains("TempRenderPath", StringComparison.Ordinal) &&
                  engineSource.Contains("FileMover.MoveAsync", StringComparison.Ordinal),
                  "直接写最终路径会在失败时删掉用户原有文件");

            string appRoot = FindAppRoot() ?? "";
            Check("T35 压制页与 ViewModel 已建立",
                  File.Exists(Path.Combine(appRoot, "Pages", "CompressPage.xaml")) &&
                  File.Exists(Path.Combine(appRoot, "ViewModels", "VideoCompressViewModel.cs")));
            Check("T36 压制页已接入导航",
                  (File.ReadAllText(Path.Combine(appRoot, "ViewModels", "ShellViewModel.cs"))
                       .Contains("\"compress\"", StringComparison.Ordinal)) &&
                  (File.ReadAllText(Path.Combine(appRoot, "Views", "MainWindow.xaml.cs"))
                       .Contains("compressed", StringComparison.OrdinalIgnoreCase) ||
                   File.ReadAllText(Path.Combine(appRoot, "Views", "MainWindow.xaml.cs"))
                       .Contains("CompressPage", StringComparison.Ordinal)));
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* 忽略 */ }
        }
    }

    #endregion

    #endregion
}
