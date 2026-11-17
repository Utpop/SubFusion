using System.Text;
using System.Text.RegularExpressions;

namespace SubFusion.Core.Services;

/// <summary>
/// 文件名规范化与语言识别（原 core.strip_lang_tokens / tokenize / normalize_name /
/// detect_lang_from_name / similarity）。
///
/// 1.1 起修正了两处让「只有扩展名不同的视频/字幕」匹配不上的问题：
///  1. tokenize 先移除 <c>.chs</c> / <c>_en</c> / <c>[cht]</c> 这类语言标记再切分，
///     不再把它当作普通词导致 AS≠ASS、SRT≠MKV；
///  2. tokenize 只去掉末尾的括号内容（语言/编码标记），保留开头的发布组
///     <c>[POPGO]</c>，也不再吞掉 <c>(2021)</c> 这类年份。
/// 原有的 <see cref="Tokenize(string)"/> / <see cref="Normalize(string)"/> /
/// <see cref="Similarity(string, string)"/> 保持旧语义，供自检与旧调用方使用。
/// </summary>
public static class NameNormalizer
{
    /// <summary>
    /// 「这是一条字幕」的任务词。中文词只在它与分隔符相邻时才算任务词：
    /// 标题里本来就含这些字（中文电影、特效师、中字头）时不能删。
    /// 英文词还要排除 sub 出现在 subtitle / subs 这类更长单词里的情况。
    /// </summary>
    private static readonly Regex TaskMarkerRegex = new(
        @"(?<![A-Za-z])(?:subtitles?|subs?|captions?|closedcaption)(?![A-Za-z])" +
        @"|(?:^|(?<=[\s._\-\[(]))(?:字幕組|字幕组|字幕|中字|双语|雙語|中文|简繁|簡繁|特效)(?=$|[\s._\-\])])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>标题末尾最常见的「这不是标题」的词（原版 NOISE_RE 中的发行/编码词）。</summary>
    private static readonly string[] ReleaseNoise =
    {
        "480p", "720p", "1080p", "1440p", "2160p", "4320p", "4k", "8k",
        "bluray", "bdrip", "brrip", "webrip", "webdl", "web", "hdrip", "hdtv", "hddvd",
        "dvdrip", "dvdscr", "remux", "h264", "h265", "x264", "x265", "hevc", "avc", "av1",
        "xvid", "divx", "10bit", "8bit", "hdr", "sdr", "dv",
        "aac", "ac3", "eac3", "dts", "flac", "flacx2", "flacx3", "opus", "truehd", "atmos",
        "amzn", "nf", "dsnp", "hmax", "proper", "repack", "internal", "extended", "unrated",
        "imax", "directors", "cut", "rip", "bd", "dvd", "tv", "v2", "rev",
    };

    private static readonly HashSet<string> ReleaseNoiseSet =
        new(ReleaseNoise, StringComparer.Ordinal);

    /// <summary>切词用的空白字符：必须包含全角空格 \u3000（中文文件名里很常见）。</summary>
    private static readonly char[] SpaceChars = { ' ', '\u3000', '\t' };

    /// <summary>EP03 / E03 / S01E03（只取集数部分）。</summary>
    private static readonly Regex EpisodeShortRegex = new(
        @"^(?:s\d{1,2})?e(?:p)?(\d{1,4})$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>数字后带这两个单位时它不是「集数」，不能在集数匹配上当作序号用。</summary>
    private static readonly HashSet<string> NonEpisodeUnits =
        new(StringComparer.Ordinal)
        {
            "bit", "kbps", "k", "mbps", "fps", "hz", "khz", "ch", "channel", "sec", "s", "min", "ms",
        };

    private static readonly Regex CjkEpisodeRegex = new(
        @"^第\s*([0-9]{1,4})\s*[集话話期回]$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly char[] SepChars =
        { ' ', '\u3000', '\t', '.', '_', '-', '+', '&', '·', '—', '–', ',', '\'', '!', '?', '~', '@', '#', '[', '-' };

    private static readonly Regex ResolutionRegex = new(
        @"^(?:\d{3,4}p|[248]k|uhd|fhd)$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static bool IsResolutionToken(string lower) => ResolutionRegex.IsMatch(lower);

    private static readonly Regex RipRegex = new(
        "^[a-z]{2,12}rip$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>分辨率 / 来源 / 编码这类「版本标记」词（发布组不算：它很少出现在字幕名里）。</summary>
    /// <summary>源 / 编码的缩写（WEB-DL 的 DL、BD、HD、DD 这类），不是标题词。</summary>
    private static readonly HashSet<string> TechShortWords =
        new(new[] { "dl", "bd", "hd", "sd", "dd", "ddp" }, StringComparer.Ordinal);

    private static bool IsTechWord(string lower)
        => ReleaseNoiseSet.Contains(lower) || TechShortWords.Contains(lower) ||
           IsResolutionToken(lower) || RipRegex.IsMatch(lower) ||
           Regex.IsMatch(lower, @"^s\d{1,2}e\d{1,4}$|^e\d{1,4}$|^ep\d{1,4}$|^\d{3,4}x\d{3,4}$");

    /// <summary>纯分隔符切出来的 token（"Anime - 01" 的 "-"）既不是标题也不是词，直接丢掉。</summary>
    private static bool IsSeparatorOnly(string token) => token.All(c => SepChars.Contains(c));

    /// <summary>移除语言标记，返回剩余 token 与检测到的语言码（取第一个命中）。</summary>
    public static (List<string> Tokens, string? Lang) StripLangTokens(IEnumerable<string> tokens)
    {
        string? lang = null;
        var rest = new List<string>();
        foreach (string tok in tokens)
        {
            string key = tok.ToLowerInvariant();
            if (Constants.LangHints.TryGetValue(key, out var hit))
            {
                lang ??= hit;
                continue;
            }
            rest.Add(tok);
        }
        return (rest, lang);
    }

    /// <summary>旧版切分：去扩展名 → 删括号 → 小写 → 去噪声词 → 只留中英数字。</summary>
    public static List<string> Tokenize(string path)
    {
        string baseName = Path.GetFileNameWithoutExtension(path);
        baseName = Constants.BracketRegex.Replace(baseName, " ");
        baseName = baseName.ToLowerInvariant();
        baseName = Constants.NoiseRegex.Replace(baseName, " ");
        baseName = Constants.KeepCharsRegex.Replace(baseName, " ");
        return baseName.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    /// <summary>旧版规范化：tokenize 后去掉语言标记。</summary>
    public static string Normalize(string path)
        => string.Join(" ", StripLangTokens(Tokenize(path)).Tokens);

    #region 1.1 匹配用切分

    /// <summary>去掉中文「字幕」「中字」等任务词，避免标题里本来就有「字幕」时被误删。</summary>
    private static string StripTaskMarkers(string text) => TaskMarkerRegex.Replace(text, " ");

    private static bool IsNoiseToken(string lower, string nextLower)
    {
        if (ReleaseNoiseSet.Contains(lower)) return true;
        if ((lower == "web" || lower == "ddp") && nextLower.Length > 0 && char.IsAsciiDigit(nextLower[0])) return true;
        return false;
    }

    /// <summary>
    /// 按括号把名字切成若干段：括号外的部分、以及每个括号的内容（相邻的 [A][B] 视为一组，
    /// 方便整体判断 [1080P] 这种"纯噪声"括号）。括号外的部分无论有没有括号都要参与切词。
    /// </summary>
    private static List<string> SplitTopLevel(string text)
    {
        var chunks = new List<string>();
        var buffer = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c is '[' or '(' or '{')
            {
                if (buffer.Length > 0) { chunks.Add(buffer.ToString()); buffer.Clear(); }
                char close = c switch { '[' => ']', '(' => ')', _ => '}' };
                int end = text.IndexOf(close, i + 1);
                if (end < 0)
                {
                    buffer.Append(text, i + 1, text.Length - i - 1);   // 没有闭合：按普通文字处理
                    i = text.Length;
                    break;
                }
                chunks.Add(text[i..(end + 1)]);
                i = end + 1;
                continue;
            }
            buffer.Append(c);
            i++;
        }
        if (buffer.Length > 0) chunks.Add(buffer.ToString());
        return chunks;
    }

    private static List<string> RawTokens(string text)
    {
        var raw = text.Split(SepChars, StringSplitOptions.RemoveEmptyEntries);
        var tokens = new List<string>(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            if (IsSeparatorOnly(raw[i])) continue;
            string next = i + 1 < raw.Length ? raw[i + 1].ToLowerInvariant() : "";
            if (IsNoiseToken(raw[i].ToLowerInvariant(), next)) continue;
            tokens.Add(raw[i]);
        }
        return tokens;
    }

    /// <summary>
    /// 1.1 起匹配用的结构化切分：
    /// <list type="bullet">
    /// <item>去掉扩展名后，末尾的点分语言段（<c>.chs</c>、<c>.zh-CN</c>、<c>.简体</c>）整段剥离；</item>
    /// <item>开头的括注（发布组 <c>[POPGO]</c>）、文件名里的 <c>(2021)</c> 年份、
    /// <c>[1080P]</c> / <c>[BDrip]</c> / <c>[01]</c> 等保留为 token；</item>
    /// <item>连续的纯分隔符（<c>AACx4</c>、<c>8bit</c>、<c>1080p</c>）作为一个 token 保留，'
    /// 这样 AAC 与 AACx4 只差一个特征，而不是整段被删掉导致「没变化」；</item>
    /// <item>标题 = 日语假名/韩文/汉字，或字母开头的词（排除 at1080p / 1080p 这类分辨率）。</item>
    /// </list>
    /// </summary>
    public static NameSignature Analyze(string path)
    {
        // 匹配只关心「视频/字幕自己的名字」：路径里就算带 "[Anime]" 也不能干扰
        string name = Path.GetFileName(path) ?? "";
        /* 人工核对点：扩展名 */
        int extDot = name.LastIndexOf('.');
        if (extDot > 0) name = name[..extDot];
        string typedLang = "und";
        bool stripped = false;

        // 1) 先剥末尾的点分语言段（Name.mkv.zh.srt → "Name.mkv.zh" → "Name.mkv"），最多 3 段
        bool peeled;
        do
        {
            peeled = false;
            int dot = name.LastIndexOf('.');
            if (dot <= 0) break;
            string key = name[(dot + 1)..].Trim();
            if (Constants.TypedLangHints.TryGetValue(key.ToLowerInvariant(), out string? lang) ||
                TrySplitRegion(key, out lang))
            {
                if (!stripped) typedLang = lang;
                stripped = true;
                name = name[..dot];
                peeled = true;
            }
        }
        while (peeled);

        // 2) 去掉不带语言后缀时残留的容器扩展名（a.mkv.zh.srt → a.zh 之后再无后缀），
        //    以及末尾的「纯集数」点分段（Anime.Episode.01.mkv 的 ".01"）：
        //    它们只是第几集；年份（Movie.2021）与作品名里的数字（Blade.Runner.2049）必须保留，
        //    所以只剥 1~2 位、且带 v2 之类版本后缀的纯数字段。
        for (int guard = 0; guard < 4; guard++)
        {
            int dot = name.LastIndexOf('.');
            if (dot <= 0) break;
            string key = name[(dot + 1)..].Trim();
            // 容器扩展名（Show.mkv.zh）与源名（Show.WEB-DL/Show.WEB.DL）都要去掉
            string extKey = "." + key.ToLowerInvariant();
            if (Constants.VideoExts.Contains(extKey) || Constants.SubExts.Contains(extKey))
            {
                name = name[..dot];
                continue;
            }
            string digits = new(key.Where(char.IsAsciiDigit).ToArray());
            // 「前置词 + 数字」的点分段（Anime.Episode.01 → "Episode.01"）：把序号留在同一段里。
            // 单字母 e/s 必须整词匹配（上面的 [a-z] 前瞻），否则 "BluRay.x264-GRP" 里的 e 会被当成集数。
            const string EpisodeWords =
                "(?<![a-z])(?:episode|volume|season|chapter|part|ova|ep|vol|pt|ch|sp|e|s)(?![a-z])";
            var pair = Regex.Match(name, $@"(?:^|\.)({EpisodeWords})\.([0-9]{{1,3}})$", RegexOptions.IgnoreCase);
            bool pureEpisode = digits.Length > 0 && key.Length <= 5 &&
                               Regex.IsMatch(key, @"^[0-9]{1,2}(v[0-9]+)?$", RegexOptions.IgnoreCase);
            if (pair.Success)
            {
                // 统一切成".Num"，交给 TryEpisode 决定它是不是集数（S01 这类季号会原样保留）
                name = name[..(name.Length - pair.Length)] + "." + pair.Groups[1].Value + pair.Groups[2].Value;
            }
            else if (pureEpisode)
            {
                name = name[..dot];
            }
            else
            {
                break;
            }
        }

        name = StripTaskMarkers(name);
        name = Constants.TypedLangTokenRegex.Replace(name, " ");
        // 非 ASCII 语言标记（.简体 / [韩语] / .繁體 …）在 BuildTypedLangTokenRegex 中
        // 被排除（那里只收 ASCII 键），这里单独剥离，避免它们混进标题 token
        // 导致「Movie.简体.srt」与「Movie.mkv」的标题不一致。ASCII 输出不变。
        name = Constants.NonAsciiLangTokenRegex.Replace(name, " ");

        // 括号内的内容按顺序参与切词（[POPGO]、[01]、[HEVC_10bit_AAC]），
        // 但如果一组括号里全是噪声词（[1080P][BluRay]），整组丢掉
        var tokens = new List<string>();
        var trailer = new List<string>();
        foreach (string chunk in SplitTopLevel(name))
        {
            bool bracketed = chunk.Length > 0 && (chunk[0] is '[' or '(' or '{');
            var inner = RawTokens(chunk.Replace("[", " ").Replace("]", " ")
                                         .Replace("(", " ").Replace(")", " ")
                                         .Replace("{", " ").Replace("}", " "));
            if (bracketed && inner.Count == 0) continue;       // [1080P][BluRay] 这种纯噪声括号

            // 这一段的噪音标记分成两截：前半截（1080p.BluRay.x264）后面还有正常词，
            // 结尾连续的那一截（.x264-GRP 前的部分）才是「版本尾巴」。
            var rawWords = chunk.Split(SepChars, StringSplitOptions.RemoveEmptyEntries)
                                .Where(w => !IsSeparatorOnly(w)).ToList();
            int lastWord = rawWords.FindLastIndex(w => !IsTechWord(w.ToLowerInvariant()));
            var run = rawWords.Skip(lastWord + 1).Select(w => w.ToLowerInvariant()).ToList();
            if (run.Count > 0) trailer = run;

            tokens.AddRange(inner);
        }

        var title = new List<string>();
        string? episode = null;
        for (int i = 0; i < tokens.Count; i++)
        {
            string tok = tokens[i];
            if (episode is null && TryEpisode(tok, tokens, i, out string? eps, out int consumed))
            {
                episode = eps;
                if (consumed == 2) { title.Add(tok + tokens[i + 1]); i++; continue; }
                continue;
            }
            // 版本/发布标记不能进标题（Show.S01E01.720p.WEB-DL.x264-GRP 的 DL、GRP）
            if (IsTechWord(tok.Trim('[', ']', '{', '}', '(', ')').ToLowerInvariant())) continue;
            // 发布组式全大写缩写（2~12 位，如 GRP / RARBG / NTb）：标题名里也基本不会出现。
            // 纯字母才判断：带数字的缩写（AACx4、x265、8bit）是音频/编码标记，不是发布组。
            string bare = new(tok.Where(char.IsAsciiLetterOrDigit).ToArray());
            if (bare.Length is >= 2 and <= 12 && bare.All(char.IsAsciiLetter) &&
                bare.All(c => char.IsUpper(c))) continue;
            if (IsTitleToken(tok)) title.Add(tok);
        }

        // 「技术尾巴」：标题名末尾、字幕名也可能写出的版本标记（分辨率、来源、编码等）。
        // 切词阶段它们被当作噪声丢掉了，这里在同一个循环里顺手记下最后一段连续出现的。
        // 多个版本同时存在时（Show.S01E01.1080p.zh.srt 对 Show.S01E01.2160p.mkv），
        // 尾巴对不上就说明视频带的是字幕没提过的版本。
        string techTail = trailer.Count > 0 ? string.Join(" ", trailer) : "";

        return new NameSignature(
            Path.GetFileName(path) ?? path,
            tokens,
            string.Join(" ", title),
            typedLang,
            episode,
            techTail);
    }

    /// <summary>.zh-CN / .pt-BR 这类「语言-地区」。</summary>
    private static bool TrySplitRegion(string key, out string lang)
    {
        lang = "und";
        int dash = key.IndexOf('-');
        if (dash <= 0) return false;
        if (!Constants.TypedLangHints.TryGetValue(key[..dash].ToLowerInvariant(), out string? hit)) return false;
        lang = hit;
        return true;
    }

    /// <summary>
    /// 判断一个 token 是否集数：[01] / [01v2] / 1080p 后的裸数字 / S01E02 / EP03 / 01v2。
    /// consumed=2 表示集数由相邻的两个 token 组成（EP + 03）。
    /// </summary>
    private static bool TryEpisode(string token, IReadOnlyList<string> tokens, int index,
                                   out string? episode, out int consumed)
    {
        episode = null;
        consumed = 1;

        string core = token.Trim('[', ']', '{', '}', '(', ')').ToLowerInvariant();
        string prev = index > 0 ? tokens[index - 1].ToLowerInvariant() : "";
        string next = index + 1 < tokens.Count ? tokens[index + 1].ToLowerInvariant() : "";

        // EP03 / E03 / 第03集 这种整体形式
        var shortForm = EpisodeShortRegex.Match(core);
        if (shortForm.Success) { episode = shortForm.Groups[1].Value; return true; }
        var cjkForm = CjkEpisodeRegex.Match(core);
        if (cjkForm.Success) { episode = cjkForm.Groups[1].Value; return true; }

        if (IsDigits(core) || Regex.IsMatch(core, "^[0-9]{1,4}v[0-9]+$"))
        {
            string number = Regex.Match(core, "^[0-9]{1,4}").Value;
            if (number.Length > 3) return false;                       // 年份不是集数
            if (NonEpisodeUnits.Contains(prev)) return false;          // 10bit / 2000k 之类
            if (NonEpisodeUnits.Contains(next)) return false;
            if (Regex.IsMatch(prev, @"^\d+[xX]$")) return false;       // 1920x1080 的后半段
            // 紧跟在分辨率/年份后面的裸数字（Movie.2021.1080p.02）算集数
            episode = number;
            return true;
        }

        if (index + 1 < tokens.Count && IsDigits(next) && IsEpisodePrefix(core))
        {
            string lowerNext = next.ToLowerInvariant();
            if (core is "s" or "season")
            {
                // 季号不是集数：S01.E02 → 集数取 02，季号留进标题以便区分不同季
                episode = lowerNext;
                consumed = 2;
                return false;
            }
            episode = next;
            consumed = 2;
            return true;
        }
        return false;
    }

    private static bool IsDigits(string s) => s.Length > 0 && s.All(char.IsAsciiDigit);

    private static bool IsEpisodePrefix(string token)
    {
        string lower = token.ToLowerInvariant();
        if (lower is "ep" or "episode" or "e" or "no" or "vol" or "volume" or "part" or "pt") return true;
        return Regex.IsMatch(lower, "^s[0-9]+$") || Regex.IsMatch(lower, "^e[0-9]+$");
    }

    /// <summary>字母开头（at1080p 这类以分辨率开头的噪声词不算）或 CJK；纯数字单独判断。</summary>
    private static bool IsTitleToken(string token)
    {
        char first = token[0];
        while (!char.IsLetterOrDigit(first) && token.Length > 1)
        {
            token = token[1..];
            first = token[0];
        }
        if (IsCjk(first)) return true;
        if (!char.IsAsciiLetter(first)) return false;
        return !Regex.IsMatch(token, @"(1080|720|2160|480|4k|8k)", RegexOptions.IgnoreCase);
    }

    private static bool IsCjk(char c) =>
        (c >= '\u4e00' && c <= '\u9fff') ||   // 汉字
        (c >= '\u3040' && c <= '\u30ff') ||   // 假名
        (c >= '\uac00' && c <= '\ud7af');     // 谚文

    #endregion

    /// <summary>
    /// 先从 token 里的语言标记判断；否则按文件名中的日文假名 / 韩文 / 汉字判断。
    /// </summary>
    public static string DetectLangFromName(string path)
    {
        var (_, lang) = StripLangTokens(Tokenize(path));
        if (!string.IsNullOrEmpty(lang)) return lang;

        string baseName = Path.GetFileName(path);
        if (Constants.KanaRegex.IsMatch(baseName)) return "jpn";
        if (Constants.HangulRegex.IsMatch(baseName)) return "kor";
        if (Constants.HanRegex.IsMatch(baseName)) return "chi";
        return "und";
    }

    /// <summary>
    /// 1.1 起用于轨道语言的检测：先看 .chs / _en / [cht] 这类明确标记，
    /// 没有标记时再用 <see cref="DetectLangFromName"/> 的字符集启发式。
    /// </summary>
    public static string DetectTypedLang(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path) ?? "";
        var marks = Constants.TypedLangTokenRegex.Matches(name);
        if (marks.Count > 0)
        {
            string key = marks[^1].Value.ToLowerInvariant();
            if (Constants.TypedLangHints.TryGetValue(key, out string? lang)) return lang;
        }
        return DetectLangFromName(path);
    }

    /// <summary>原 core.similarity：相等 1.0；包含关系 0.72~0.94；否则 difflib 比率。</summary>
    public static double Similarity(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0.0;
        if (a == b) return 1.0;
        if (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal))
        {
            string shortName, longName;
            if (a.Length <= b.Length) { shortName = a; longName = b; }
            else { shortName = b; longName = a; }
            if (shortName.Length < 3) return 0.0;
            double ratio = shortName.Length / (double)longName.Length;
            return 0.72 + 0.22 * Math.Min(1.0, ratio);
        }
        return SequenceMatcher.Ratio(a, b);
    }

    /// <summary>1.1 起的字符串相似度：包含关系的高分只给较短一方仍有长度时，其余走 difflib。</summary>
    public static double StringSimilarity(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0.0;
        if (a == b) return 1.0;
        return SequenceMatcher.Ratio(a, b);
    }
}

/// <summary>一个文件名的匹配特征（见 <see cref="NameNormalizer.Analyze"/>）。</summary>
public sealed record NameSignature(
    string FileName,
    IReadOnlyList<string> Tokens,
    string Title,
    string TypedLang,
    string? Episode,
    string Tail = "");
