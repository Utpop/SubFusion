using System.Text.RegularExpressions;

namespace SubFusion.Core.Services;

/// <summary>
/// 落在「模糊区间」的推测对应（同名视频明显更合适却没能挂上，例如视频已挂满或只允许一条字幕）。
/// 1.1 起这类结果不自动挂载，只交给界面提示，避免多集连续剧里把字幕错挂到别人身上。
/// </summary>
public sealed record FuzzyMatch(string Subtitle, string Video, double Score);

public sealed record MatchResult(
    Dictionary<string, List<string>> Mapping,
    List<string> Unmatched,
    IReadOnlyList<FuzzyMatch>? Fuzzy = null,
    int ConflictCount = 0)
{
    /// <summary>有推测对应但没自动挂载的字幕（界面用来提示，不计入「未匹配」）。</summary>
    public IReadOnlyList<FuzzyMatch> FuzzyMatches => Fuzzy ?? (IReadOnlyList<FuzzyMatch>)Array.Empty<FuzzyMatch>();
}

/// <summary>
/// 原 core.match_subtitles：字幕按文件名自动匹配到最佳视频。
/// 1.1 起打分改为可解释的加权模型，并加入目录、集数与竞争约束，专门修好
/// 「同目录、只有扩展名不同」的字幕匹配不上的问题：
///
/// <list type="number">
/// <item>字符：标题相似度（difflib 比率）·0.85；</item>
/// <item>词级：其它 token 的重合度（Jaccard）·0.15，用来区分 01/02 这种标题相近的集数；</item>
/// <item>集数：两边都识别出集数但不一致 → 直接判定不匹配；</item>
/// <item>目录：字幕与视频目录不同 → 文本字幕判定不匹配，图形字幕（sup/sub）权重减半；</item>
/// <item>同目录、标题与集数都一致（只是扩展名/语言不同）→ 分数记为 1.0，优先分配；</item>
/// <item>一对一：一份字幕只挂一个视频；同一集在一个视频下只留分数最高的一份字幕。</item>
/// </list>
/// </summary>
public static class SubtitleMatcher
{
    private const double TitleWeight = 0.85;
    private const double TokenWeight = 0.15;
    private const double BitmapScopeFactor = 0.5;

    private sealed class Candidate
    {
        public required string Sub { get; init; }
        public string SubEpisode { get; init; } = "";
        public required string Video { get; init; }
        public double Score { get; init; }
        public bool InScope { get; init; }
        public bool Exact { get; init; }
    }

    public static MatchResult Match(IReadOnlyList<string> videos,
                                    IReadOnlyList<string> subtitles,
                                    double threshold = Constants.DefaultThreshold,
                                    bool oneSubPerVideo = false)
    {
        var mapping = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (string v in videos) mapping[v] = new List<string>();

        var videoSigs = new Dictionary<string, (NameSignature Sig, string Dir)>(StringComparer.Ordinal);
        foreach (string v in videos)
            videoSigs[v] = (NameNormalizer.Analyze(v), NormalizeDir(Path.GetDirectoryName(v)));

        // 1) 每份字幕 × 每个视频打分（只看目录内的视频：跨目录本来就判定不匹配）
        var unmatched = new List<string>();
        var bySub = new Dictionary<string, List<Candidate>>(StringComparer.Ordinal);
        foreach (string sub in subtitles)
        {
            NameSignature subSig = NameNormalizer.Analyze(sub);
            string subDir = NormalizeDir(Path.GetDirectoryName(sub));
            bool bitmap = Constants.BitmapSubExts.Contains(Path.GetExtension(sub).ToLowerInvariant());
            string titleKey = Key(subSig.Title);

            var list = new List<Candidate>();
            foreach (string video in videos)
            {
                (NameSignature vSig, string vDir) = videoSigs[video];
                bool sameDir = string.Equals(subDir, vDir, StringComparison.OrdinalIgnoreCase);

                double score = Score(subSig, vSig, sameDir, bitmap);
                if (score <= 0.0 || score < threshold) continue;       // 0 表示「明确不匹配」，与阈值无关

                bool exact = sameDir && titleKey.Length > 0 && titleKey == Key(vSig.Title) &&
                             string.Equals(subSig.Episode ?? "", vSig.Episode ?? "", StringComparison.Ordinal);
                if (exact)
                {
                    // 只是扩展名/语言/集数标记不同：直接配对。视频名比字幕名多带的尾巴
                    // （Movie.2021.2160p.mkv 对 Movie.2021.zh.srt）只按 0.90 算，
                    // 免得它盖过真正同名的那份视频（Movie.2021.1080p.…）。
                    // 视频只是比字幕多了「技术尾巴」（Show.S01E01.zh.srt 对
                    // Show.S01E01.1080p.BluRay.mkv）→ 几乎确定是同一部；
                    // 尾巴里出现了字幕没提过的特征（Show.S01E01.zh.srt 对 Show.S01E01.2160p.mkv）
                    // → 只算近似（0.90），让真正带同名标记的那份视频以 1.0 胜出。
                    string subTail = subSig.Tail;
                    bool onlyExtra = vSig.Tail.Length > 0 && IsContained(subTail, vSig.Tail);
                    score = onlyExtra ? 0.98 : 0.90;

                    // 尾巴互相矛盾（Show.1080p.zh.srt 对 Show.2160p.mkv）→ 不是这一版
                    if (!IsContained(subTail, vSig.Tail) && !IsContained(vSig.Tail, subTail))
                    {
                        score = 0.0;
                        continue;
                    }
                }

                list.Add(new Candidate
                {
                    Sub = sub, SubEpisode = subSig.Episode ?? "", Video = video,
                    Score = score, InScope = sameDir, Exact = exact,
                });
            }
            bySub[sub] = list;
            if (list.Count == 0) unmatched.Add(sub);
        }

        // 2) 分配：同目录且精确匹配的优先；其余按分数从高到低，同一分数时同目录优先、
        //    目录层级更近优先（Frieren/ vs Frieren/S2/ 这种嵌套目录）。
        //    约束只有两条：一份字幕只挂一个视频；同一集在一个视频下只留分数最高的一份字幕
        //    （Anime.01.zh.srt + Anime.01.en.srt 这类同集多语言仍然都保留）。
        var chosen = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        var episodesTaken = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        int conflicts = 0;

        foreach (bool exactPass in new[] { true, false })
        {
            IEnumerable<Candidate> ordered = bySub.Values.SelectMany(x => x)
                .Where(c => c.Exact == exactPass)
                .OrderByDescending(c => c.Score)
                .ThenByDescending(c => c.InScope)
                .ThenBy(c => DirDepth(c.Sub, c.Video))
                .ThenBy(c => c.Sub, StringComparer.Ordinal)
                .ThenBy(c => c.Video, StringComparer.Ordinal);

            foreach (Candidate c in ordered)
            {
                if (chosen.ContainsKey(c.Sub)) continue;   // 同一份字幕只挂一个视频

                List<string> list = mapping[c.Video];
                if (oneSubPerVideo && list.Count > 0) { conflicts++; continue; }
                // 精确匹配（同目录、标题与集数都一致）是确定性的配对，可以多份同时挂；
                // 只有近似候选才需要「一个目录 + 一个集数只留一份最高分」来防止 01/02 错挂。
                // 同一个视频下的多语言字幕（同集 .chs.srt + .eng.srt）都允许。
                if (!c.Exact && c.SubEpisode.Length > 0)
                {
                    string slot = SlotKey(c.Video, c.SubEpisode);
                    if (!episodesTaken.TryGetValue(slot, out var owners)) episodesTaken[slot] = owners = new HashSet<string>(StringComparer.Ordinal);
                    if (owners.Count > 0 && !owners.Contains(c.Sub))
                    {
                        conflicts++;
                        continue;
                    }
                    owners!.Add(c.Sub);
                }
                list.Add(c.Sub);
                chosen[c.Sub] = c;
            }
        }

        // 3) 输出排序：同一视频下按文件名
        foreach (string video in mapping.Keys.ToList())
        {
            mapping[video] = mapping[video]
                .OrderBy(p => p.ToLowerInvariant(), StringComparer.Ordinal)
                .ToList();
        }

        // 3.5) 一次性剔除已匹配项：逐个 List.Remove 是 O(n) 的，几百份字幕就要搬移上万次，
        // 匹配阶段会明显卡顿。先收集到集合里再统一过滤。
        var matchedSubs = new HashSet<string>(StringComparer.Ordinal);
        foreach (List<string> selected in mapping.Values)
            foreach (string s in selected) matchedSubs.Add(s);
        if (matchedSubs.Count > 0) unmatched.RemoveAll(matchedSubs.Contains);

        // 4) 模糊提示：能对上某个同目录视频、但没分到（同集已有一份 / 已挂满 / 只允许一条）
        var fuzzy = new List<FuzzyMatch>();
        foreach (string sub in subtitles)
        {
            if (chosen.ContainsKey(sub)) continue;
            if (!bySub.TryGetValue(sub, out var list) || list.Count == 0) continue;

            Candidate? best = list.Where(c => c.Score >= threshold)
                                  .OrderByDescending(c => c.InScope)
                                  .ThenByDescending(c => c.Score)
                                  .ThenBy(c => c.Video, StringComparer.Ordinal)
                                  .FirstOrDefault();
            if (best is null) continue;

            fuzzy.Add(new FuzzyMatch(sub, best.Video, best.Score));
            unmatched.Remove(sub);
        }

        fuzzy.Sort((a, b) => string.CompareOrdinal(a.Subtitle, b.Subtitle));
        return new MatchResult(mapping, unmatched, fuzzy, conflicts);
    }

    /// <summary>单个视频与单份字幕的打分，供界面和自检直接查看。</summary>
    public static double Score(string video, string subtitle)
    {
        string subDir = NormalizeDir(Path.GetDirectoryName(subtitle));
        string vidDir = NormalizeDir(Path.GetDirectoryName(video));
        bool bitmap = Constants.BitmapSubExts.Contains(Path.GetExtension(subtitle).ToLowerInvariant());
        return Score(NameNormalizer.Analyze(subtitle), NameNormalizer.Analyze(video),
                     string.Equals(subDir, vidDir, StringComparison.OrdinalIgnoreCase), bitmap);
    }

    /// <summary>有可比较的「身份」：有标题词，或者整体就是一个数字（20240212.ass 这种日期命名）。</summary>
    private static bool HasIdentity(NameSignature sig)
    {
        if (sig.Title.Length > 0) return true;
        string tokens = Key(string.Join("", sig.Tokens));
        return tokens.Length > 0 && tokens.All(char.IsAsciiDigit);
    }

    private static double Score(NameSignature sub, NameSignature video, bool sameDir, bool bitmap)
    {
        bool subKnown = HasIdentity(sub);
        bool vidKnown = HasIdentity(video);
        if (!subKnown || !vidKnown) return 0.0;                         // 纯噪声名（如只有 [1080P]）不与任何视频配对

        // 大小写不应影响「同名」判定：视频/字幕分别分析时保留各自原始大小写，
        // 跨目录的字幕常因大小写不同（Show.S01E01 vs show.s01e01）被判死，改用
        // 不区分大小写的比较（与 Key() 归一化后的结果一致）。
        bool exactTitle = subKnown && vidKnown && string.Equals(sub.Title, video.Title, StringComparison.OrdinalIgnoreCase);
        // 跨目录的文本字幕原本被判死；1.1 改成仍然打分，但按半权，
        // 这样「Season/B/Show.S02E01.mkv ← Show.S02E01.zh.srt」这类排版也能配上，
        // 而同名的本地视频总能凭 1.0 压过它。
        if (!sameDir && !bitmap && !exactTitle) return 0.0;
        bool subHasEps = !string.IsNullOrEmpty(sub.Episode);
        bool vidHasEps = !string.IsNullOrEmpty(video.Episode);
        if (subHasEps && vidHasEps && !string.Equals(sub.Episode, video.Episode, StringComparison.Ordinal))
            return 0.0;                                                 // 01 与 02 是不同集

        double titleScore = exactTitle ? 1.0 : NameNormalizer.StringSimilarity(sub.Title, video.Title);
        double tokenScore = Jaccard(sub.Tokens, video.Tokens);
        double score = TitleWeight * titleScore + TokenWeight * tokenScore;
        if (!sameDir && (bitmap || !exactTitle)) score *= BitmapScopeFactor;
        return score;
    }

    /// <summary>a 的每个 token 是否都出现在 b 里（空的 a 视为「什么都没提」，永远满足）。</summary>
    private static bool IsContained(string a, string b)
    {
        if (a.Length == 0) return true;
        var has = new HashSet<string>(b.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
        return a.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(has.Contains);
    }

    /// <summary>「目录 + 集数」的互斥键。必须先把 '/' 统一成 '\'，否则 Path.GetDirectoryName
    /// 对两种写法给出不同结果，同一视频会被当成两个位置。</summary>
    private static string SlotKey(string video, string episode)
    {
        if (episode.Length == 0) return "";
        string dir = NormalizeDir(Path.GetDirectoryName(video.Replace('/', '\\')));
        return dir + "|" + episode;
    }

    /// <summary>字幕所在目录到视频所在目录的距离；跨目录时返回 0（由分数本身决定先后）。</summary>
    private static int DirDepth(string sub, string video)
    {
        string a = NormalizeDir(Path.GetDirectoryName(sub));
        string b = NormalizeDir(Path.GetDirectoryName(video));
        if (a.Length == 0 || b.Length == 0) return 0;
        int depth = 0;
        while (!string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
        {
            string? up = Path.GetDirectoryName(a);
            if (string.IsNullOrEmpty(up) || up == a) return 0;
            a = up;
            depth++;
        }
        return depth;
    }

    private static string Key(string title)
        => string.Join("", title.Where(c => !char.IsWhiteSpace(c))).ToLowerInvariant();

    private static double Jaccard(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count == 0 && b.Count == 0) return 1.0;
        var sa = new HashSet<string>(a, StringComparer.OrdinalIgnoreCase);
        var sb = new HashSet<string>(b, StringComparer.OrdinalIgnoreCase);
        int inter = sa.Count(sb.Contains);
        int union = sa.Count + sb.Count - inter;
        return union == 0 ? 1.0 : inter / (double)union;
    }

    private static string NormalizeDir(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return "";
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir.Replace('/', '\\'))); }
        catch { return dir; }
    }
}
