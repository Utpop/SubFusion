using System.Globalization;
using System.Text;

namespace SubFusion.Core.Merge;

/// <summary>搬运进度。</summary>
public sealed record GroupFolderProgress(
    int FolderIndex,
    int FolderCount,
    string FolderName,
    int FileIndex,
    int FileCount,
    string FileName,
    double Percent);

/// <summary>搬运的结果。</summary>
public sealed class GroupFolderResult
{
    public bool Ok { get; init; }
    public string TargetRoot { get; init; } = "";
    public int FoldersCreated { get; init; }
    /// <summary>成功搬到目标文件夹的文件数。</summary>
    public int FilesMoved { get; init; }
    /// <summary>其中需要跨盘复制搬运（而不是瞬时改名）的文件数。</summary>
    public int FilesCopied { get; init; }
    public long BytesMoved { get; init; }
    public List<string> FolderPaths { get; init; } = new();
    /// <summary>失败原因（Ok 为 false 时）。</summary>
    public string Error { get; init; } = "";
    /// <summary>需要注意但不构成失败的事项（例如某个文件搬运失败被跳过）。</summary>
    public List<string> Notices { get; init; } = new();

    public string SummaryText => Ok
        ? string.Format(CultureInfo.InvariantCulture,
            "已把 {0} 个组合移动到各自的文件夹（共 {1} 个文件，{2}）",
            FoldersCreated, FilesMoved, Services.Formatting.FormatSize(BytesMoved))
        : "操作失败：" + Error;
}

/// <summary>
/// 按 <see cref="GroupFolderPlan"/> 把文件<b>移动</b>到各自的组文件夹。
///
/// 移动有两种代价，必须分开对待：
/// <list type="bullet">
/// <item><b>同盘</b>：只是改名，瞬时完成、零额外空间、失败也不会有中间态。</item>
/// <item><b>跨盘</b>：没有原子操作可用，只能「复制 → 核对字节数 → 删源」。</item>
/// </list>
///
/// 跨盘这一路的安全线是「先证明副本完整，再删源」。
/// 具体做法：复制完成后比对目标文件大小与源文件大小，一致才删除源文件；
/// 复制过程中出错或被取消，删掉目标侧的半成品、<b>源文件原封不动</b>。
/// 顺序反了就会在半路断电/取消时同时失去两份数据——那是不可接受的。
/// </summary>
public static class GroupFolderExporter
{
    private const int BufferBytes = 4 * 1024 * 1024;

    public static async Task<GroupFolderResult> ExecuteAsync(
        GroupFolderPlan plan,
        IProgress<GroupFolderProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(plan.SpaceProblem))
            return new GroupFolderResult { Ok = false, TargetRoot = plan.TargetRoot, Error = plan.SpaceProblem };
        if (plan.Folders.Count == 0)
            return new GroupFolderResult { Ok = false, TargetRoot = plan.TargetRoot, Error = "没有可处理的组合" };

        var notices = new List<string>();
        var created = new List<string>();
        // 每个已建目录里实际搬进去的文件数。取消时要据此清掉「一个文件都没有」的空目录：
        // 用户按了取消，却留下几个只含说明文件的文件夹，既占位置又让人以为搬过一部分。
        var movedPerFolder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int filesMoved = 0;
        int filesCopied = 0;
        long bytesMoved = 0;
        int fileTotal = plan.Folders.Sum(f => f.Files.Count);

        try
        {
            Directory.CreateDirectory(plan.TargetRoot);
        }
        catch (Exception ex)
        {
            return new GroupFolderResult
            {
                Ok = false, TargetRoot = plan.TargetRoot,
                Error = "无法创建目标目录：" + ex.Message,
            };
        }

        try
        {
        for (int fi = 0; fi < plan.Folders.Count; fi++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GroupFolderPlanItem item = plan.Folders[fi];

            try
            {
                Directory.CreateDirectory(item.FolderPath);
            }
            catch (Exception ex)
            {
                // 单个文件夹建不出来不该中断整批：其余组合仍然值得搬过去。
                notices.Add(item.FolderName + "：无法创建文件夹（" + ex.Message + "）");
                continue;
            }

            for (int i = 0; i < item.Files.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                GroupFolderFile file = item.Files[i];
                string destination = Path.Combine(item.FolderPath, file.FileName);

                bool cross = !Services.FileMover.SameVolume(file.SourcePath, destination);
                try
                {
                    if (cross)
                    {
                        await CopyThenDeleteAsync(file.SourcePath, destination,
                            ratio => progress?.Report(new GroupFolderProgress(
                                fi, plan.Folders.Count, item.FolderName, i, item.Files.Count, file.FileName,
                                Overall(fi, i, ratio, plan, fileTotal))),
                            cancellationToken).ConfigureAwait(false);
                        filesCopied++;
                    }
                    else
                    {
                        // 同盘：改名。没有中间态，也就不需要任何核对。
                        File.Move(file.SourcePath, destination, overwrite: false);
                        progress?.Report(new GroupFolderProgress(fi, plan.Folders.Count, item.FolderName,
                            i, item.Files.Count, file.FileName,
                            Overall(fi, i, 1.0, plan, fileTotal)));
                    }
                    filesMoved++;
                    bytesMoved += file.Bytes;
                    movedPerFolder[item.FolderPath] =
                        (movedPerFolder.TryGetValue(item.FolderPath, out int already) ? already : 0) + 1;
                }
                catch (OperationCanceledException)
                {
                    // 取消时源文件必须完好：跨盘路径已在内部删掉半成品。
                    throw;
                }
                catch (Exception ex)
                {
                    notices.Add(file.FileName + "：未能移动（" + ex.Message + "）");
                    continue;
                }
            }

            try
            {
                await File.WriteAllTextAsync(Path.Combine(item.FolderPath, GroupFolderPlanner.ReadmeFileName),
                    GroupFolderPlanner.BuildReadme(item), new UTF8Encoding(false), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                notices.Add(item.FolderName + "：写入组合说明失败（" + ex.Message + "）");
            }

            created.Add(item.FolderPath);
            progress?.Report(new GroupFolderProgress(fi + 1, plan.Folders.Count, item.FolderName,
                item.Files.Count, item.Files.Count, "", (fi + 1) * 100.0 / plan.Folders.Count));
        }
        }
        catch (OperationCanceledException)
        {
            // 取消时把「一个文件都没搬进去」的目录清掉；已经搬了文件的目录必须原样保留，
            // 否则用户会在源和目标之间两头找不到文件。清不掉的只记提示，不掩盖取消。
            var removedDirs = new List<string>();
            foreach (string dir in created)
            {
                int moved = movedPerFolder.TryGetValue(dir, out int n) ? n : 0;
                if (moved > 0) continue;
                try
                {
                    if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                    removedDirs.Add(Path.GetFileName(dir));
                }
                catch { /* 清不掉不影响取消结论 */ }
            }
            if (removedDirs.Count > 0)
                notices.Add("已取消，并清理了 " + removedDirs.Count + " 个空文件夹：" +
                            string.Join("、", removedDirs));
            throw;
        }

        // Ok 不能只看「建了几个文件夹」：文件夹建成功、里面一个文件都没搬过去时，
        // SummaryText 会输出「已把 N 个组合移动到各自的文件夹（共 0 个文件，0 B）」而 Error 为空，
        // 用户以为成功了，实际一个文件都没动。至少要有一个文件真的落位才算成功。
        bool ok = filesMoved > 0;
        string error = "";
        if (!ok)
        {
            error = created.Count == 0
                ? "没有任何组合被处理：" + string.Join("；", notices)
                : "已创建 " + created.Count + " 个文件夹，但没有任何文件被移动过去：" +
                  string.Join("；", notices);
        }
        return new GroupFolderResult
        {
            Ok = ok,
            TargetRoot = plan.TargetRoot,
            FoldersCreated = created.Count,
            FilesMoved = filesMoved,
            FilesCopied = filesCopied,
            BytesMoved = bytesMoved,
            FolderPaths = created,
            Error = error,
            Notices = notices,
        };
    }

    /// <summary>整体完成比例：已完成的文件 + 当前文件内部的复制进度。</summary>
    private static double Overall(int folderIndex, int fileIndex, double fileRatio,
                                  GroupFolderPlan plan, int fileTotal)
    {
        int done = 0;
        for (int i = 0; i < folderIndex && i < plan.Folders.Count; i++) done += plan.Folders[i].Files.Count;
        done += fileIndex;
        return Math.Clamp((done + Math.Clamp(fileRatio, 0, 1)) / Math.Max(1, fileTotal) * 100.0, 0, 100);
    }

    /// <summary>
    /// 跨盘移动：复制 → 核对大小 → 删源。
    ///
    /// 三步的顺序不能调换。先删源再复制，一旦中途失败两份都没了；
    /// 不核对大小就删源，"复制成功但字节少了"会被当成成功。
    /// </summary>
    public static async Task CopyThenDeleteAsync(string source, string destination,
                                                 Action<double> onProgress,
                                                 CancellationToken cancellationToken)
    {
        long copied = await CopyWithProgressAsync(source, destination, onProgress, cancellationToken)
            .ConfigureAwait(false);

        // 复制刚结束就被取消的情况也要拦住：副本虽然完整，
        // 但用户既然按了取消，就不该再执行不可逆的删源。
        // 这一行让「取消」在任何时刻都保证源文件还在。
        try { cancellationToken.ThrowIfCancellationRequested(); }
        catch (OperationCanceledException)
        {
            TryDelete(destination);
            throw;
        }

        long expected;
        try { expected = new FileInfo(source).Length; }
        catch (Exception ex)
        {
            TryDelete(destination);
            throw new IOException("无法读取源文件大小，已放弃删除源文件：" + ex.Message, ex);
        }

        if (copied != expected)
        {
            // 副本不完整就绝不能动源文件：宁可这一步失败，也不能出现「两份都缺一截」。
            TryDelete(destination);
            throw new IOException(string.Format(CultureInfo.InvariantCulture,
                "复制结果不完整（源 {0} 字节，目标 {1} 字节），已保留源文件", expected, copied));
        }

        try
        {
            File.Delete(source);
        }
        catch (Exception ex)
        {
            // 源删不掉：副本已经完整存在，数据没有丢，只是变成了两份。
            throw new IOException("副本已就位但源文件删除失败（数据未丢失）：" + ex.Message, ex);
        }
    }

    /// <summary>带进度的复制；出错或取消会删掉目标侧半成品，源文件不受影响。</summary>
    private static async Task<long> CopyWithProgressAsync(string source, string destination,
                                                          Action<double> onProgress,
                                                          CancellationToken cancellationToken)
    {
        long total = 0;
        try { total = new FileInfo(source).Length; }
        catch { total = 0; }
        long copied = 0;
        double last = -1;

        try
        {
            await using (FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                                                 BufferBytes, FileOptions.SequentialScan))
            await using (FileStream output = new(destination, FileMode.Create, FileAccess.Write, FileShare.None,
                                                  BufferBytes, FileOptions.SequentialScan))
            {
                byte[] buffer = new byte[BufferBytes];
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int read = await input.ReadAsync(buffer.AsMemory(0, BufferBytes), cancellationToken)
                        .ConfigureAwait(false);
                    if (read <= 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    copied += read;
                    double ratio = total > 0 ? Math.Min(1.0, (double)copied / total) : 1.0;
                    // 每 1% 才回调一次：一次处理有几十个文件，全量回调会把 UI 线程刷爆。
                    if (ratio - last >= 0.01 || ratio >= 1.0)
                    {
                        last = ratio;
                        onProgress(ratio);
                    }
                }
            }
        }
        catch
        {
            TryDelete(destination);
            throw;
        }

        onProgress(1.0);
        return copied;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 清理失败不影响结论 */ }
    }
}
