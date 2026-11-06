namespace SubtitleMerger.Core.Services;

public sealed record CollectResult(List<string> Videos, List<string> Subtitles, List<string> Skipped);

/// <summary>原 core.collect_files：文件/文件夹收集、去重、过滤隐藏子目录、排序。</summary>
public static class FileCollector
{
    public static CollectResult Collect(IEnumerable<string> paths, bool recursive = true)
    {
        var videos = new List<string>();
        var subs = new List<string>();
        var skipped = new List<string>();
        var seenV = new HashSet<string>(StringComparer.Ordinal);
        var seenS = new HashSet<string>(StringComparer.Ordinal);

        void Add(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            string real = Path.GetFullPath(path);
            if (Constants.VideoExts.Contains(ext))
            {
                if (seenV.Add(real)) videos.Add(real);
            }
            else if (Constants.SubExts.Contains(ext))
            {
                if (seenS.Add(real)) subs.Add(real);
            }
            else
            {
                skipped.Add(real);
            }
        }

        foreach (string path in paths)
        {
            if (File.Exists(path))
            {
                Add(path);
            }
            else if (Directory.Exists(path))
            {
                if (recursive)
                {
                    Walk(path, Add);
                }
                else
                {
                    foreach (string name in SortedNames(Directory.GetFiles(path).Select(Path.GetFileName)!))
                    {
                        string full = Path.Combine(path, name);
                        if (File.Exists(full)) Add(full);
                    }
                }
            }
        }

        var lowerComparer = new LowerOrdinalComparer();
        List<string> sortedVideos = videos.OrderBy(p => p, lowerComparer).ToList();
        List<string> sortedSubs = subs.OrderBy(p => p, lowerComparer).ToList();
        return new CollectResult(sortedVideos, sortedSubs, skipped);
    }

    /// <summary>等价于 os.walk：跳过以 "." 开头的子目录；文件名按 sorted() 顺序处理。</summary>
    private static void Walk(string root, Action<string> add)
    {
        try
        {
            foreach (string name in SortedNames(Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName)!))
            {
                string full = Path.Combine(root, name);
                if (File.Exists(full)) add(full);
            }
            foreach (string dir in Directory.EnumerateDirectories(root))
            {
                string leaf = Path.GetFileName(dir);
                if (leaf.StartsWith('.')) continue;   // dirs[:] = [d for d in dirs if not d.startswith(".")]
                Walk(dir, add);
            }
        }
        catch
        {
            // os.walk 默认 onerror=None，遇到无权限目录静默跳过
        }
    }

    private static IEnumerable<string> SortedNames(IEnumerable<string?> names)
        => names.Where(n => n is not null).Select(n => n!).OrderBy(n => n, StringComparer.Ordinal);

    /// <summary>等价于 Python 的 sort(key=lambda p: p.lower())。</summary>
    private sealed class LowerOrdinalComparer : IComparer<string>
    {
        public int Compare(string? x, string? y)
            => StringComparer.Ordinal.Compare((x ?? "").ToLowerInvariant(), (y ?? "").ToLowerInvariant());
    }
}
