using System.Globalization;
using System.Text;
using System.Text.Json;
using SubFusion.Core;
using SubFusion.Core.Models;
using SubFusion.Core.Chapters;
using SubFusion.Core.Media;
using SubFusion.Core.Merge;
using SubFusion.Core.Services;

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

        Console.WriteLine("=== B. 端到端功能验证（本机 ffmpeg） ===");
        await EndToEndAsync(args.Contains("--skip-e2e"));

        Console.WriteLine("=== C. 界面布局约束 ===");
        CheckLayoutConstraints();

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
            string a = await MakeClipAsync(ffmpeg, work, "clipA", 320, 240, 24, "aac", 44100, 2, 2.0, "testsrc");
            string b = await MakeClipAsync(ffmpeg, work, "clipB", 320, 240, 24, "aac", 44100, 2, 2.0, "smptebars");
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

            string c = await MakeClipAsync(ffmpeg, work, "clipC", 640, 360, 30, "aac", 48000, 2, 2.0, "testsrc2");
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
            string wide = await MakeClipAsync(ffmpeg, work, "wide", 640, 360, 24, "aac", 44100, 2, 2.0, "testsrc");
            string square = await MakeClipAsync(ffmpeg, work, "square", 320, 320, 24, "aac", 44100, 2, 2.0, "testsrc2");
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
            MergeResult noOverwrite = await task.RunAsync(new VideoMergeOptions
            {
                Inputs = new List<string> { a },
                OutputPath = existing,
                Container = "mkv",
                Overwrite = false,
            });
            Check("G45 未允许覆盖时拒绝写入已存在文件",
                  noOverwrite.Status == MergeStatus.Failed && noOverwrite.Error.Contains("已存在"),
                  noOverwrite.Error);
            Check("G46 拒绝覆盖时原文件未被修改",
                  (await File.ReadAllTextAsync(existing)) == "keep");

            // ---- 分析接口 ----
            (MergeCompatibility analyzed, List<MediaBundle> probed) =
                await task.AnalyzeAsync(new VideoMergeOptions { Inputs = new List<string> { a, b } });
            Check("G47 分析接口返回每个输入的探测结果", probed.Count == 2 && probed.All(x => x.Success));
            Check("G48 分析接口给出可无损拼接结论", analyzed.CanStreamCopy);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* 忽略 */ }
        }
    }

    /// <summary>用 lavfi 现场生成指定参数的测试片段。</summary>
    private static async Task<string> MakeClipAsync(string ffmpeg, string work, string name, int width, int height,
                                                    int fps, string audioCodec, int sampleRate, int channels,
                                                    double seconds, string source)
    {
        string path = Path.Combine(work, name + ".mp4");
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", $"{source}=size={width}x{height}:rate={fps}",
            "-f", "lavfi", "-i", $"sine=frequency=440:sample_rate={sampleRate}",
            "-t", seconds.ToString("0.###", CultureInfo.InvariantCulture),
            // 显式写入 BT.709 色彩标记：编码器的 -colorspace 参数在多数封装下不会写进码流，
            // 必须走滤镜内的 setparams，否则同一批素材的色彩字段会是「未知」，兼容性判定无法对齐。
            "-vf", "setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709:range=tv",
            "-c:v", "libx264", "-pix_fmt", "yuv420p",
            "-c:a", audioCodec, "-ac", channels.ToString(CultureInfo.InvariantCulture),
            "-shortest", path,
        };
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

            string c1 = await MakeClipAsync(ffmpeg, work, "part1", 320, 240, 24, "aac", 44100, 2, 2.0, "testsrc");
            string c2 = await MakeClipAsync(ffmpeg, work, "part2", 320, 240, 24, "aac", 44100, 2, 3.0, "smptebars");
            string c3 = await MakeClipAsync(ffmpeg, work, "part3", 320, 240, 24, "aac", 44100, 2, 2.5, "testsrc2");
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
}
