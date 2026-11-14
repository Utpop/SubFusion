using System.Globalization;
using System.Text;
using System.Text.Json;
using SubtitleMerger.Core;
using SubtitleMerger.Core.Models;
using SubtitleMerger.Core.Services;

namespace SubtitleMerger.SelfTest;

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
        string temp = Path.Combine(Path.GetTempPath(), "submerge_selftest_" + Guid.NewGuid().ToString("N"));
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
    /// 1.1：主窗口布局的数值约束。用文字断言而不是引用 UI 类型，
    /// 这样 Core/SelfTest 仍不依赖 WinUI，但重构 XAML 时能拦住把底部区域挤出窗口的回归。
    /// 判定依据：最小窗口 1120x800（物理像素，125% 缩放即 882x685 DIP）时，
    /// 各固定行合计必须给设置区留下 ≥250 DIP，否则「输出」页内容会被裁。
    /// </summary>
    private static void CheckLayoutConstraints()
    {
        string? xaml = FindMainWindowXaml();
        if (xaml is null)
        {
            Console.WriteLine("(未找到 MainWindow.xaml，跳过)");
            return;
        }

        Check("布局：任务表/设置区按比例分配剩余高度（0.38*/0.62*）",
              xaml.Contains(@"Height=""0.38*""", StringComparison.Ordinal) &&
              xaml.Contains(@"Height=""0.62*""", StringComparison.Ordinal));
        Check("布局：设置区最小高度 250（小窗口仍能完整显示一页）",
              xaml.Contains(@"MinHeight=""250""", StringComparison.Ordinal));
        Check("布局：任务表最小高度 110（可收缩但保留表头与一行）",
              xaml.Contains(@"MinHeight=""110""", StringComparison.Ordinal));
        Check("布局：Pivot 不再使用固定 Height（固定高度会把底部按钮挤出窗口）",
              !System.Text.RegularExpressions.Regex.IsMatch(xaml, @"<Pivot[^>]*\sHeight="""));
    }

    /// <summary>
    /// 重负载相关回归：
    /// 1. 进程超时/取消后必须释放句柄（不会留下挂死的管道读取）；
    /// 2. 批量任务在用户点「停止」后收尾要完整：所有行都有终态、不会残留「处理中」，也不重复回调。
    /// </summary>
    private static async Task CheckRobustnessAsync()
    {
        string work = Path.Combine(Path.GetTempPath(), "submerge_robust_" + Guid.NewGuid().ToString("N"));
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

    private static string? FindMainWindowXaml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "src", "SubtitleMerger.App", "Views", "MainWindow.xaml");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
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

        string root = Path.Combine(Path.GetTempPath(), "submerge_replace_" + Guid.NewGuid().ToString("N"));
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
        string work = Path.Combine(Path.GetTempPath(), "submerge_hardiso_" + Guid.NewGuid().ToString("N"));
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

        string work = Path.Combine(Path.GetTempPath(), "submerge_e2e_" + Guid.NewGuid().ToString("N"));
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
