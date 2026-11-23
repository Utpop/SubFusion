using System.Globalization;
using System.Text;

namespace SubFusion.Core.Merge;

/// <summary>计划里的一个文件：从哪来、在组文件夹里叫什么名字。</summary>
public sealed record GroupFolderFile(string SourcePath, string FileName, long Bytes);

/// <summary>计划里的一个组文件夹。</summary>
public sealed class GroupFolderPlanItem
{
    /// <summary>文件夹名（已消毒，保证是合法目录名）。</summary>
    public string FolderName { get; init; } = "";

    /// <summary>文件夹的完整路径。</summary>
    public string FolderPath { get; init; } = "";

    /// <summary>组标题，如「组合 1」。</summary>
    public string Title { get; init; } = "";

    /// <summary>「直接拼接」或「重封装后拼接」。</summary>
    public string KindText { get; init; } = "";

    /// <summary>该组共同的参数摘要。</summary>
    public string Signature { get; init; } = "";

    /// <summary>为什么需要重封装（直接拼接时为空）。</summary>
    public string ReasonText { get; init; } = "";

    /// <summary>组内合计时长（秒）。</summary>
    public double TotalDuration { get; init; }

    public List<GroupFolderFile> Files { get; init; } = new();

    public long TotalBytes => Files.Sum(f => f.Bytes);

    public bool DirectCopy => KindText.StartsWith("直接", StringComparison.Ordinal);

    public string TotalSizeText => Services.Formatting.FormatSize(TotalBytes);

    public string DurationText => TimeSpan.FromSeconds(TotalDuration).ToString(@"hh\:mm\:ss");
}

/// <summary>
/// 导出计划：把识别出来的无损组合各分配到一个独立文件夹。
/// </summary>
public sealed class GroupFolderPlan
{
    public string TargetRoot { get; init; } = "";
    public List<GroupFolderPlanItem> Folders { get; init; } = new();
    /// <summary>未能进入任何组合的文件数（仅供参考，不参与导出）。</summary>
    public int RejectedCount { get; init; }

    public long TotalBytes => Folders.Sum(f => f.TotalBytes);
    public int FileCount => Folders.Sum(f => f.Files.Count);
    public string TotalSizeText => Services.Formatting.FormatSize(TotalBytes);

    /// <summary>
    /// 需要真正跨盘复制的字节数。
    ///
    /// 同盘移动只是改名，一个字节都不用额外占；跨盘才需要目标盘有同等空间。
    /// 把这两个量分开算，用户才不会看到「空间不足」的假告警。
    /// </summary>
    public long CrossVolumeBytes { get; init; }

    /// <summary>true 表示本次导出至少有一个文件要跨盘搬运（会慢，且需要目标盘空间）。</summary>
    public bool HasCrossVolumeMove => CrossVolumeBytes > 0;

    /// <summary>目标盘可用空间不足时的问题描述；空表示空间充足。</summary>
    public string SpaceProblem { get; init; } = "";
}

/// <summary>
/// 「把无损组合分配到各自文件夹」的计划生成器。
///
/// 为什么先出计划再执行：这件事会在用户目录里一次性建出几十个文件夹、复制几个 GB，
/// 且目标位置可能是用户随手选的（甚至就是素材所在目录）。把它拆成「先算清楚要做什么」
/// 和「照做」两步，用户能先看见会发生什么，也能在动手之前发现空间不够。
///
/// 三条约束，全部写在计划里而不是执行时临时判断：
/// <list type="number">
/// <item><b>移动是跨盘还是同盘，决定代价与安全策略</b>。同盘只是改名（瞬时、零风险）；
///   跨盘必须「先复制、校验大小、再删源」，任何一步失败源文件都还在。</item>
/// <item><b>绝不复用已有文件夹</b>。目标位置已有同名目录时自动改用「_2」「_3」，
///   而不是把新文件塞进别人的目录里混在一起——那会让「每组一个文件夹」这件事失去意义。</item>
/// <item><b>绝不把文件挪进它自己所在的目录之外的意外位置</b>。计划里逐条写明
///   每个文件从哪来、到哪去，执行时照单执行，不做任何临场推断。</item>
/// </list>
/// </summary>
public static class GroupFolderPlanner
{
    /// <summary>Windows 目录名不允许出现的字符。</summary>
    private static readonly char[] InvalidNameChars =
        { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

    /// <summary>为一次导出生成本地计划（只读磁盘的目录列表与文件大小，不写任何东西）。</summary>
    public static GroupFolderPlan Build(MergeGroupingResult grouping, string targetRoot)
    {
        string root = string.IsNullOrWhiteSpace(targetRoot)
            ? throw new ArgumentException("未指定导出位置", nameof(targetRoot))
            : Path.GetFullPath(targetRoot);

        var items = new List<GroupFolderPlanItem>();
        var usedFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 已在磁盘上存在的目录同样要避开，否则会把新文件混进旧目录。
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Directory.Exists(root))
                foreach (string dir in Directory.GetDirectories(root))
                    existing.Add(Path.GetFileName(dir));
        }
        catch
        {
            // 目录列不出来（权限/不存在）不等于出错：执行时 Directory.CreateDirectory 会给出真实原因。
        }

        int index = 1;
        foreach (MergeGroup group in grouping.Groups)
        {
            if (group.Items.Count < 2) continue;

            string baseName = string.Format(CultureInfo.InvariantCulture, "组合{0:D2}_{1}",
                index, group.DirectCopy ? "直接拼接" : "重封装");
            string folderName = MakeUnique(Sanitize(baseName), usedFolderNames, existing);
            usedFolderNames.Add(folderName);

            var files = new List<GroupFolderFile>();
            var usedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Media.MediaBundle bundle in group.Items)
            {
                long bytes = 0;
                try { bytes = new FileInfo(bundle.Path).Length; }
                catch { bytes = 0; }
                string name = MakeUnique(Sanitize(Path.GetFileName(bundle.Path)), usedFileNames, null);
                usedFileNames.Add(name);
                files.Add(new GroupFolderFile(bundle.Path, name, bytes));
            }

            items.Add(new GroupFolderPlanItem
            {
                FolderName = folderName,
                FolderPath = Path.Combine(root, folderName),
                Title = "组合 " + index.ToString(CultureInfo.InvariantCulture),
                KindText = group.KindText,
                Signature = group.Signature,
                ReasonText = group.ReasonText,
                TotalDuration = group.TotalDuration,
                Files = files,
            });
            index++;
        }

        // 只有跨盘的那些文件才需要目标盘空间；同盘移动是改名，不额外占用。
        long crossBytes = 0;
        foreach (GroupFolderPlanItem item in items)
            foreach (GroupFolderFile file in item.Files)
                if (!Services.FileMover.SameVolume(file.SourcePath, Path.Combine(item.FolderPath, file.FileName)))
                    crossBytes += file.Bytes;

        return new GroupFolderPlan
        {
            TargetRoot = root,
            Folders = items,
            RejectedCount = grouping.Rejected.Count,
            CrossVolumeBytes = crossBytes,
            SpaceProblem = CheckSpace(root, crossBytes),
        };
    }

    /// <summary>
    /// 检查目标盘剩余空间。
    ///
    /// 必须留余量：磁盘写满会让复制在中途失败，留下几十个半成品文件夹，
    /// 比一开始就拒绝要糟得多。因此要求可用空间 &gt; 总大小 + 64 MB。
    /// </summary>
    private static string CheckSpace(string root, long neededBytes)
    {
        if (neededBytes <= 0) return "";
        try
        {
            string? driveRoot = Path.GetPathRoot(root);
            if (string.IsNullOrEmpty(driveRoot)) return "";
            var drive = new DriveInfo(driveRoot);
            if (!drive.IsReady) return "";
            const long reserve = 64L * 1024 * 1024;
            if (drive.AvailableFreeSpace >= neededBytes + reserve) return "";
            return string.Format(CultureInfo.InvariantCulture,
                "目标位置所在磁盘空间不足：需要 {0}，可用 {1}",
                Services.Formatting.FormatSize(neededBytes + reserve),
                Services.Formatting.FormatSize(drive.AvailableFreeSpace));
        }
        catch
        {
            return "";   // 拿不到空间信息时不做判断，执行阶段仍会失败并如实报告
        }
    }

    /// <summary>
    /// 目录/文件名消毒。非法字符与首尾空白替换掉，结尾的点与空格必须去掉
    /// （Windows 会静默吞掉它们，导致「建出来的名字」与「计划里的名字」不一致）。
    /// </summary>
    public static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "未命名";
        var sb = new StringBuilder(name.Length);
        foreach (char ch in name)
        {
            if (ch < 32 || Array.IndexOf(InvalidNameChars, ch) >= 0) sb.Append('_');
            else sb.Append(ch);
        }
        string cleaned = sb.ToString().Trim().TrimEnd('.', ' ');
        if (cleaned.Length == 0) cleaned = "未命名";
        // 超长名字在 Windows 上会直接失败；给后缀（_2 之类）留出空间。
        if (cleaned.Length <= 96) return cleaned;
        // 不能直接 Substring(0, 96)：UTF-16 的下标可能正好落在代理对中间，
        // 切出来的孤立半代理是不合法字符串，后面 CreateDirectory / File.Move
        // 会抛 ArgumentException，整组被静默跳过（emoji 文件名就会踩到）。
        int cut = 96;
        if (char.IsHighSurrogate(cleaned[cut - 1])) cut--;
        return cleaned[..cut].TrimEnd('.', ' ');
    }

    /// <summary>
    /// 生成不冲突的名字：占用集合里已有同名（忽略大小写）或磁盘上已存在同名目录时，
    /// 依次尝试「名字_2」「名字_3」。用下划线而不是括号，避免与已有括号编号混淆。
    /// </summary>
    private static string MakeUnique(string name, HashSet<string> used, HashSet<string>? existing)
    {
        if (!used.Contains(name) && (existing is null || !existing.Contains(name))) return name;
        string stem = name;
        string ext = "";
        if (existing is null)
        {
            // 只有文件名需要拆扩展名；目录名整串当主干。
            ext = Path.GetExtension(name);
            if (ext.Length > 0) stem = name[..^ext.Length];
        }
        for (int i = 2; i < 1000; i++)
        {
            string candidate = string.Format(CultureInfo.InvariantCulture, "{0}_{1}{2}", stem, i, ext);
            if (!used.Contains(candidate) && (existing is null || !existing.Contains(candidate)))
                return candidate;
        }
        // 1000 次都撞名时绝不能返回已知冲突的 name：调用方会把它当成唯一名，
        // 结果要么计划里出现两个同名目录，要么两个源文件映射到同一个目标文件名，
        // 后者在 File.Move(overwrite:false) 上必然失败并被吞成一条提示。
        // 这里改用 GUID 后缀，保证「一定唯一」这个契约。
        return string.Format(CultureInfo.InvariantCulture, "{0}_{1}{2}",
            stem, Guid.NewGuid().ToString("N")[..8], ext);
    }

    /// <summary>
    /// 每个文件夹里放一份说明。检查组合时最想知道的是
    /// 「这组为什么能拼」「应该按什么顺序拼」，写在文件夹里比记在别处可靠。
    /// </summary>
    public static string BuildReadme(GroupFolderPlanItem item)
    {
        var sb = new StringBuilder();
        sb.Append(item.Title).Append(" —— ").Append(item.KindText).AppendLine();
        sb.Append("共同参数：").AppendLine(item.Signature);
        sb.Append("处理方式：").Append(item.KindText).AppendLine(
            item.DirectCopy ? "（不重新编码，画质逐比特不变）" : "（先统一时间基准，仍不重新编码）");
        if (item.ReasonText.Length > 0)
            sb.Append("为什么需要多一步：").AppendLine(item.ReasonText);
        sb.Append("合计时长：").Append(item.DurationText).AppendLine();
        sb.Append("合计大小：").AppendLine(item.TotalSizeText);
        sb.AppendLine();
        sb.AppendLine("文件顺序（即建议的合并顺序）：");
        for (int i = 0; i < item.Files.Count; i++)
            sb.AppendFormat(CultureInfo.InvariantCulture, "  {0}. {1}", i + 1, item.Files[i].FileName).AppendLine();
        sb.AppendLine();
        sb.AppendLine("说明：这些文件已从原位置<b>移动</b>到这里，原位置不再保留副本。");
        return sb.ToString();
    }

    /// <summary>说明文件在文件夹里的名字。</summary>
    public const string ReadmeFileName = "组合说明.txt";
}
