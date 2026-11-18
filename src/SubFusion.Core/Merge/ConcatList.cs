using System.Text;

namespace SubFusion.Core.Merge;

/// <summary>
/// concat demuxer 清单的生成。
///
/// 这个类存在的唯一理由：清单格式有三个互相独立、但都会静默失败的坑，
/// 分散在调用点写一定会漏掉其中一个。
///
/// <list type="number">
/// <item><b>反斜杠</b>：concat 解析器把 <c>\</c> 当转义符，Windows 原始路径
///   <c>C:\videos\a.mp4</c> 会被吃掉字符，报成"文件不存在"。</item>
/// <item><b>单引号</b>：路径里出现 <c>'</c> 会提前结束引号，必须写成 <c>'\''</c>。</item>
/// <item><b>file: 前缀</b>：新版 ffmpeg 在部分路径形态下会把它当成协议名去解析，
///   加 <c>file:</c> 能稳定落到本地文件协议上。</item>
/// </list>
///
/// 这三条都是实测踩过的：indent 前两条会让拼接直接失败，第三条的表现是
/// <c>Impossible to open 'pipe:xyz.mp4'</c> 这类误导性报错。
/// </summary>
public static class ConcatList
{
    /// <summary>
    /// 把绝对路径转成清单里可安全使用的一行路径（不含 <c>file</c> 关键字）。
    /// </summary>
    public static string Escape(string path)
    {
        // 先统一成正斜杠：ffmpeg 在 Windows 上接受正斜杠，
        // 而反斜杠在清单里是转义符，必须先消掉歧义。
        string normalized = path.Replace('\\', '/');
        // 单引号是唯一需要转义的字符；其余字符（含空格、中文、括号）在单引号内安全。
        normalized = normalized.Replace("'", "'\\''");
        // 只在确实还没有 file: 前缀时补，避免出现 file:file:。
        return normalized.StartsWith("file:", StringComparison.Ordinal) ? normalized : "file:" + normalized;
    }

    /// <summary>生成完整的清单文本（每行一条 <c>file '...'</c>）。</summary>
    public static string Build(IEnumerable<string> paths)
    {
        var text = new StringBuilder();
        foreach (string path in paths)
            text.Append("file '").Append(Escape(path)).Append("'\n");
        return text.ToString();
    }

    /// <summary>写出清单文件；UTF-8 无 BOM（带 BOM 会让第一行的 file 指令失效）。</summary>
    public static async Task WriteAsync(string listPath, IEnumerable<string> paths,
                                        CancellationToken cancellationToken = default)
    {
        await File.WriteAllTextAsync(listPath, Build(paths), new UTF8Encoding(false), cancellationToken)
            .ConfigureAwait(false);
    }
}
