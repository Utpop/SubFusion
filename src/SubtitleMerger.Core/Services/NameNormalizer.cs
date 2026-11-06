using System.Globalization;

namespace SubtitleMerger.Core.Services;

/// <summary>
/// 文件名规范化与语言识别（原 core.strip_lang_tokens / tokenize / normalize_name /
/// detect_lang_from_name / similarity）。
/// </summary>
public static class NameNormalizer
{
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

    public static List<string> Tokenize(string path)
    {
        string baseName = Path.GetFileNameWithoutExtension(path);
        baseName = Constants.BracketRegex.Replace(baseName, " ");
        baseName = baseName.ToLowerInvariant();
        baseName = Constants.NoiseRegex.Replace(baseName, " ");
        baseName = Constants.KeepCharsRegex.Replace(baseName, " ");
        return baseName.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    public static string Normalize(string path)
        => string.Join(" ", StripLangTokens(Tokenize(path)).Tokens);

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
}
