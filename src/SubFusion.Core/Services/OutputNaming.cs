namespace SubFusion.Core.Services;

/// <summary>
/// 输出文件名避让：目标已存在时改用「名称 (1)」「名称 (2)」…
///
/// 为什么默认避让而不是覆盖：覆盖是不可逆的。用户把输出目录设成素材目录、
/// 或反复试参数时，只要一次点错，上一次的成品就被静默替换掉了，
/// 界面事后也无法告诉他「刚才那份还在不在」。避让的代价只是多一个文件，
/// 而覆盖的代价是不可恢复的数据丢失——两者不对等。
///
/// 命名规则与资源管理器一致（`名称 (1).mkv`），用户一眼能看出是新的一份。
/// </summary>
public static class OutputNaming
{
    /// <summary>最多尝试到 (999)；再撞上就退化为带时间戳的名字，绝不返回已存在的路径。</summary>
    private const int MaxAttempts = 999;

    /// <summary>
    /// 返回一个当前不存在的路径。目标本来就不存在时原样返回。
    /// 目录不存在时也原样返回：是否创建目录由引擎决定，这里只负责命名。
    /// </summary>
    public static string Unique(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        if (!File.Exists(path)) return path;

        string directory = Path.GetDirectoryName(path) ?? "";
        string name = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        if (directory.Length == 0)
        {
            directory = ".";
        }

        for (int index = 1; index <= MaxAttempts; index++)
        {
            string candidate = Path.Combine(directory, name + " (" + index + ")" + extension);
            if (!File.Exists(candidate)) return candidate;
        }

        // 极端情况：同名文件已经有 999 个。用时间戳保证一定拿到一个没被占用的名字，
        // 而不是把已存在的文件当成自己的输出。
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        string fallback = Path.Combine(directory, name + "_" + stamp + extension);
        for (int index = 1; index <= MaxAttempts && File.Exists(fallback); index++)
            fallback = Path.Combine(directory, name + "_" + stamp + "_" + index + extension);
        return fallback;
    }
}
