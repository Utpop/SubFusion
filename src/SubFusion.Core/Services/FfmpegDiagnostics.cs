namespace SubFusion.Core.Services;

/// <summary>
/// 把 ffmpeg / ffprobe 的报错翻译成「下一步该做什么」。
///
/// 调研结论（见 docs/视频合并方案调研报告.md §4.2-⑤）：直接把这几个英文短语抛给用户
/// 没有任何指导意义——"Non-monotonically increasing dts" 对使用者而言不构成信息。
/// LosslessCut 的做法是弹一个列了若干条可操作建议的对话框，这里把它压缩成
/// 「关键词 → 一句可操作建议」的映射，在失败信息里附带。
///
/// 注意：这里只做措辞翻译，绝不据此改变引擎行为。是否重编码、是否重封装
/// 仍然只由 <see cref="SubFusion.Core.Merge.CompatibilityAnalyzer"/> 决定。
/// </summary>
public static class FfmpegDiagnostics
{
    private sealed record Rule(string[] Keywords, string Suggestion);

    // 顺序即优先级：越靠前的模式越能解释根因。
    private static readonly Rule[] Rules =
    {
        new(new[]
            {
                "non-monotonous", "non monotonically", "non-monotonically",
                "dts out of order", "monotonically increasing",
            },
            "各段时间戳不连续（时间基准不一致）。可尝试：让程序用「无损重封装」对齐时间基准，" +
            "或先允许重新编码再合并。"),

        new(new[]
            {
                "invalid data found when processing input",
                "could not find codec parameters",
                "decoder not found",
                "unsupported codec",
            },
            "某些输入的编码参数不一致或无法解析。可尝试：开启「允许重新编码」，" +
            "让程序把这些片段统一到同一编码后再拼接。"),

        new(new[] { "mcbpc damaged", "marker does not match", "error while decoding", "concealing errors", "slice mismatch" },
            "码流本身有损坏，或帧率被按错误的间隔解释。可尝试：开启「允许重新编码」重做这一段。"),

        new(new[] { "moov atom not found", "prematurely", "truncated", "invalid nal size" },
            "文件不完整（复制或下载可能中途中断），建议重新获取源文件。"),

        new(new[] { "unsafe file name", "protocol not on whitelist", "invalid argument" },
            "路径或清单格式被拒绝。可尝试：把文件移到不含引号、换行等特殊字符的目录后再试。"),

        new(new[] { "impossible to open", "no such file or directory", "error opening input" },
            "无法打开输入文件。可尝试：确认文件仍存在，且没有被播放器或同步盘占用。"),

        new(new[] { "no space left", "disk full", "not enough space" },
            "磁盘空间不足，请清理后重试。"),

        new(new[] { "permission denied", "access is denied" },
            "没有权限读写目标位置。可尝试：换一个输出目录，或关闭占用该文件的程序。"),

        new(new[] { "unknown encoder", "encoder not found", "unrecognized option" },
            "当前 ffmpeg 构建不支持所需的编码器或参数。可尝试：在设置里指定功能更完整的 ffmpeg。"),
    };

    /// <summary>根据报错文本给出一条可操作建议；无法归类时返回 null（不编造建议）。</summary>
    public static string? Suggest(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return null;
        string lowered = stderr.ToLowerInvariant();
        foreach (Rule rule in Rules)
        {
            foreach (string keyword in rule.Keywords)
            {
                if (lowered.Contains(keyword, StringComparison.Ordinal)) return rule.Suggestion;
            }
        }
        return null;
    }

    /// <summary>
    /// 把「原始原因」与「可操作建议」拼成一条面向用户的失败信息。
    /// 没有匹配到建议时只返回原因，不追加空洞的通用话术。
    /// </summary>
    public static string Describe(string reason, string? stderr)
    {
        string? suggestion = Suggest(stderr);
        return suggestion is null ? reason : reason + "\n\n建议：" + suggestion;
    }
}
