namespace SubtitleMerger.Core.Services;

public sealed record MatchResult(Dictionary<string, List<string>> Mapping, List<string> Unmatched);

/// <summary>原 core.match_subtitles：字幕按文件名自动匹配到最佳视频。</summary>
public static class SubtitleMatcher
{
    public static MatchResult Match(IReadOnlyList<string> videos,
                                    IReadOnlyList<string> subtitles,
                                    double threshold = Constants.DefaultThreshold,
                                    bool oneSubPerVideo = false)
    {
        var mapping = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (string v in videos)
            mapping[v] = new List<string>();

        var normVideos = videos.Select(v => (Video: v, Norm: NameNormalizer.Normalize(v))).ToList();

        foreach (string sub in subtitles)
        {
            string subNorm = NameNormalizer.Normalize(sub);
            string? bestVideo = null;
            double bestScore = 0.0;
            foreach (var (video, vNorm) in normVideos)
            {
                double score = NameNormalizer.Similarity(vNorm, subNorm);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestVideo = video;
                }
            }
            if (bestVideo is not null && bestScore >= threshold)
            {
                if (oneSubPerVideo && mapping[bestVideo].Count > 0) continue;
                mapping[bestVideo].Add(sub);
            }
        }

        foreach (string video in mapping.Keys.ToList())
        {
            List<string> sorted = mapping[video]
                .OrderBy(p => p.ToLowerInvariant(), StringComparer.Ordinal)
                .ToList();
            mapping[video] = sorted;
        }

        var matched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var list in mapping.Values)
            foreach (string s in list) matched.Add(s);
        List<string> unmatched = subtitles.Where(s => !matched.Contains(s)).ToList();

        return new MatchResult(mapping, unmatched);
    }
}
