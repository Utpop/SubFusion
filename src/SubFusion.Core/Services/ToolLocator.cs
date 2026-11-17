using SubFusion.Core.Models;

namespace SubFusion.Core.Services;

/// <summary>
/// ffmpeg / ffprobe 定位与能力探测（原 core._locate / find_ffmpeg / find_ffprobe /
/// ffmpeg_capabilities / ffmpeg_version / bundled_dir）。
/// </summary>
public static class ToolLocator
{
    /// <summary>
    /// 原 bundled_dir()：PyInstaller 单文件模式用 sys._MEIPASS 临时解压目录。
    /// .NET 无 PyInstaller；单文件发布时 AppContext.BaseDirectory 即资源解包目录，
    /// 普通发布时则是 exe 所在目录，语义等价。
    /// </summary>
    public static string BundledDir() => AppContext.BaseDirectory;

    /// <summary>原 core.__file__ 所在目录；C# 中即主程序集所在目录。</summary>
    public static string HereDir() => AppContext.BaseDirectory;

    /// <summary>
    /// 候选顺序（与原版严格一致）：
    /// 配置值 → 环境变量 → 内置资源目录(bin) → 程序目录(bin / ffmpeg/bin) → PATH → 常见安装位置。
    /// </summary>
    private static string? Locate(string name, string cfgKey)
    {
        var cfg = AppConfig.Load();
        var candidates = new List<string>();

        string? fromCfg = cfg.Get(cfgKey);
        if (!string.IsNullOrEmpty(fromCfg)) candidates.Add(fromCfg);

        string envName = string.Equals(name, "ffmpeg", StringComparison.Ordinal) ? "FFMPEG_PATH" : "FFPROBE_PATH";
        string? env = Environment.GetEnvironmentVariable(envName);
        if (!string.IsNullOrEmpty(env)) candidates.Add(env);

        string exe = name + ".exe";
        string baseDir = BundledDir();
        candidates.Add(Path.Combine(baseDir, exe));
        candidates.Add(Path.Combine(baseDir, "bin", exe));

        string here = HereDir();
        candidates.Add(Path.Combine(here, exe));
        candidates.Add(Path.Combine(here, "bin", exe));
        candidates.Add(Path.Combine(here, "ffmpeg", "bin", exe));

        string? which = ProcessUtil.Which(name);
        if (!string.IsNullOrEmpty(which)) candidates.Add(which);

        foreach (string root in new[] { "C:/ffmpeg/bin", "D:/ffmpeg/bin", "C:/Program Files/ffmpeg/bin" })
            candidates.Add(Path.Combine(root, exe));

        foreach (string? path in candidates)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                return path;
        }
        return null;
    }

    public static string? FindFfmpeg() => Locate("ffmpeg", "ffmpeg");
    public static string? FindFfprobe() => Locate("ffprobe", "ffprobe");

    /// <summary>
    /// 原 ffmpeg_capabilities：分别执行 -filters / -encoders，超时 30 秒，异常时保持 false。
    /// Python 的 stderr=STDOUT 合并流在 .NET 用 stdout+stderr 拼接替代。
    /// </summary>
    public static async Task<FfmpegCapabilities> FfmpegCapabilitiesAsync(string? ffmpeg)
    {
        var caps = new FfmpegCapabilities();
        if (string.IsNullOrEmpty(ffmpeg)) return caps;
        try
        {
            ProcessCapture filters = await ProcessUtil.RunAsync(ffmpeg,
                new[] { "-hide_banner", "-filters" }, 30).ConfigureAwait(false);
            if (!filters.TimedOut)
            {
                string text = filters.Stdout + filters.Stderr;
                caps.Libass = text.Contains(" subtitles ") || text.Contains("\tsubtitles ");
            }
        }
        catch { /* 原实现吞掉所有异常 */ }

        try
        {
            ProcessCapture encoders = await ProcessUtil.RunAsync(ffmpeg,
                new[] { "-hide_banner", "-encoders" }, 30).ConfigureAwait(false);
            if (!encoders.TimedOut)
            {
                string text = encoders.Stdout + encoders.Stderr;
                caps.X264 = text.Contains("libx264");
                caps.X265 = text.Contains("libx265");
                caps.Nvenc = text.Contains("h264_nvenc");
            }
        }
        catch { /* 原实现吞掉所有异常 */ }

        return caps;
    }

    /// <summary>原 ffmpeg_version：-version 首行，超时 20 秒，异常返回空串。</summary>
    public static async Task<string> FfmpegVersionAsync(string? ffmpeg)
    {
        if (string.IsNullOrEmpty(ffmpeg)) return "";
        try
        {
            ProcessCapture result = await ProcessUtil.RunAsync(ffmpeg,
                new[] { "-version" }, 20).ConfigureAwait(false);
            if (result.TimedOut) return "";
            string text = result.Stdout + result.Stderr;
            using var reader = new StringReader(text);
            return (reader.ReadLine() ?? "").Trim();
        }
        catch { return ""; }
    }
}
