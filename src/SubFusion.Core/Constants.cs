using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace SubFusion.Core;

/// <summary>
/// 原 Python 版 core.py + subfusion.py 中的全部常量、语言表、字符集与默认值。
/// 除显式注明外，取值与顺序逐项保持一致。
/// </summary>
public static class Constants
{
    public const string AppName = "SubFusion 字幕与视频合并工具";
    public const string AppVersion = "2.3";

    /// <summary>并发探测媒体信息的线程数（原 PROBE_WORKERS）。</summary>
    public const int ProbeWorkers = 4;

    /// <summary>
    /// 文件名自动匹配的默认阈值（0~1）。
    /// 1.1 起打分改为「标题相似度 0.85 + 令牌重合 0.15」加权，0.72 对应的基本是
    /// 「除语言/格式标记外文件名完全一致」，与旧版「规范化后完全相等」同一语义。
    /// </summary>
    public const double DefaultThreshold = 0.72;

    /// <summary>配置文件路径：%USERPROFILE%\.subfusion.json（原 os.path.expanduser("~")）。</summary>
    public static string ConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     ".subfusion.json");

    public static readonly IReadOnlySet<string> VideoExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".m4v", ".avi", ".wmv", ".flv", ".f4v",
        ".webm", ".ts", ".m2ts", ".mpg", ".mpeg", ".vob",
    };

    public static readonly IReadOnlySet<string> TextSubExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".vtt",
    };

    public static readonly IReadOnlySet<string> BitmapSubExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".sup", ".sub",
    };

    public static readonly IReadOnlySet<string> SubExts =
        new HashSet<string>(TextSubExts.Concat(BitmapSubExts), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 语言后缀（文件名 token）→ ISO 639-2/B 三字母轨道语言码。
    /// 键顺序与原 core.LANG_HINTS 一致；含中文/繁体字形的键也保留。
    /// 注意：带连字符的键（如 "zh-cn"）在 tokenize 之后不可能命中，属于原实现的遗留项，
    /// 此处原样保留以保证行为完全一致。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> LangHints =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["zh"] = "chi", ["chs"] = "chi", ["cht"] = "chi", ["zh-cn"] = "chi",
            ["zh-hans"] = "chi", ["zh-hant"] = "chi", ["zh-tw"] = "chi", ["zh-hk"] = "chi",
            ["cn"] = "chi", ["sc"] = "chi", ["tc"] = "chi", ["gb"] = "chi",
            ["big5"] = "chi", ["chi"] = "chi", ["zho"] = "chi",
            ["简体"] = "chi", ["繁體"] = "chi", ["繁体"] = "chi", ["中文"] = "chi", ["中字"] = "chi",
            ["chs-eng"] = "chi", ["zh-eng"] = "chi",

            ["en"] = "eng", ["eng"] = "eng", ["english"] = "eng", ["en-us"] = "eng",
            ["en-gb"] = "eng", ["英文"] = "eng", ["英语"] = "eng", ["英文字幕"] = "eng",

            ["jp"] = "jpn", ["ja"] = "jpn", ["jpn"] = "jpn", ["japanese"] = "jpn",
            ["日文"] = "jpn", ["日语"] = "jpn", ["日語"] = "jpn",

            ["ko"] = "kor", ["kr"] = "kor", ["kor"] = "kor", ["korean"] = "kor",
            ["韩文"] = "kor", ["韓文"] = "kor", ["韩语"] = "kor",

            ["fr"] = "fre", ["fre"] = "fre", ["fra"] = "fre", ["french"] = "fre", ["法文"] = "fre",
            ["de"] = "ger", ["ger"] = "ger", ["deu"] = "ger", ["german"] = "ger", ["德文"] = "ger",
            ["es"] = "spa", ["spa"] = "spa", ["spanish"] = "spa", ["西文"] = "spa",
            ["ru"] = "rus", ["rus"] = "rus", ["russian"] = "rus", ["俄文"] = "rus",
            ["pt"] = "por", ["por"] = "por", ["pt-br"] = "por",
            ["it"] = "ita", ["ita"] = "ita", ["italian"] = "ita",
            ["ar"] = "ara", ["ara"] = "ara", ["arabic"] = "ara",
            ["th"] = "tha", ["tha"] = "tha", ["泰文"] = "tha",
            ["vi"] = "vie", ["vie"] = "vie",
            ["id"] = "ind", ["ind"] = "ind",
            ["tr"] = "tur", ["tur"] = "tur",
        });

    /// <summary>
    /// 字幕「类型」标记 → 语言码。与 <see cref="LangHints"/> 分开：
    /// 这些是「这是哪国语言的字幕」的标记，而不是「这是一条字幕」的标记，
    /// 去掉它们才能让 chs.ass 与 .mkv 规范化后完全一致。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> TypedLangHints =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["chs"] = "chi", ["cht"] = "chi", ["cn"] = "chi", ["sc"] = "chi", ["tc"] = "chi",
            ["gb"] = "chi", ["big5"] = "chi", ["zho"] = "chi", ["简"] = "chi", ["繁"] = "chi",
            ["简体"] = "chi", ["繁體"] = "chi", ["繁体"] = "chi", ["中字"] = "chi",
            ["eng"] = "eng", ["en"] = "eng", ["english"] = "eng", ["英文"] = "eng", ["英文字幕"] = "eng",
            ["jpn"] = "jpn", ["ja"] = "jpn", ["jp"] = "jpn", ["japanese"] = "jpn", ["日文"] = "jpn", ["日语"] = "jpn", ["日語"] = "jpn",
            ["kor"] = "kor", ["ko"] = "kor", ["kr"] = "kor", ["korean"] = "kor", ["韩文"] = "kor", ["韓文"] = "kor", ["韩语"] = "kor",
            ["chi"] = "chi", ["zh"] = "chi", ["fre"] = "fre", ["fra"] = "fre", ["fr"] = "fre", ["french"] = "fre", ["法文"] = "fre",
            ["ger"] = "ger", ["deu"] = "ger", ["de"] = "ger", ["german"] = "ger", ["德文"] = "ger",
            ["spa"] = "spa", ["es"] = "spa", ["spanish"] = "spa", ["西文"] = "spa",
            ["rus"] = "rus", ["ru"] = "rus", ["russian"] = "rus", ["俄文"] = "rus",
            ["por"] = "por", ["pt"] = "por", ["ita"] = "ita", ["it"] = "ita", ["italian"] = "ita",
            ["ara"] = "ara", ["ar"] = "ara", ["arabic"] = "ara", ["tha"] = "tha", ["th"] = "tha", ["泰文"] = "tha",
            ["vie"] = "vie", ["vi"] = "vie", ["ind"] = "ind", ["id"] = "ind", ["tur"] = "tur", ["tr"] = "tur",
        });

    /// <summary>
    /// <see cref="TypedLangHints"/> 的符号形式（.en / _chs / [cht] / .zh-CN. 等）。
    /// 开头或空白后必须带 . 或 _ 或 -，避免把 "eng" 从 "english" 里截掉；
    /// 结尾用 (?![a-z]) 而不是 \b，这样 "x.mkv.zh" 这种末尾标记同样命中。
    /// </summary>
    public static readonly Regex TypedLangTokenRegex = BuildTypedLangTokenRegex();

    /// <summary>
    /// 非 ASCII 语言标记（如 <c>.简体</c>、<c>[韩语]</c>、<c>.繁體</c>）。
    /// BuildTypedLangTokenRegex 只收录 ASCII 键，这些 Unicode 标记会在 Analyze 的
    /// 原始 token 阶段单独剥离；A 组 ASCII 输出不受影响。
    /// </summary>
    public static readonly Regex NonAsciiLangTokenRegex = BuildNonAsciiLangTokenRegex();

    private static Regex BuildNonAsciiLangTokenRegex()
    {
        // 只取 TypedLangHints 中含非 ASCII 字符的键（简/繁/简体/韩语/日语…），
        // 用分隔符边界包住，避免从「简报」这类正常词里误删。
        IEnumerable<string> keys = TypedLangHints.Keys
            .Where(k => k.Any(c => c >= 128))
            .Select(k => Regex.Escape(k))
            .Distinct()
            .OrderByDescending(k => k.Length);
        string alternation = string.Join("|", keys);
        return new Regex($@"(?:^|(?<=[\s._\-\[(]))(?:{alternation})(?![^\x00-\x7F])",
                         RegexOptions.Compiled | RegexOptions.CultureInvariant);
    }

    private static Regex BuildTypedLangTokenRegex()
    {
        IEnumerable<string> keys = TypedLangHints.Keys
            .Concat(new[] { "zh-cn", "zh-tw", "zh-hk", "zh-hans", "zh-hant", "pt-br", "en-us", "en-gb" })
            .Where(k => k.All(c => c < 128))
            .Select(k => Regex.Escape(k))
            .Distinct()
            .OrderByDescending(k => k.Length);
        string alternation = string.Join("|", keys);
        // 结尾用 (?![a-z\-])：chs-eng 这种组合标记整体命中，而不是只吃掉前面的 chs
        return new Regex($@"(?:^|(?<=[\s._\-\[(]))(?:{alternation})(?![a-z]|-?[a-z])",
                         RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    }

    /// <summary>语言码 → 显示名（原 LANG_NAMES）。</summary>
    public static readonly IReadOnlyDictionary<string, string> LangNames =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["chi"] = "中文", ["eng"] = "英语", ["jpn"] = "日语", ["kor"] = "韩语",
            ["fre"] = "法语", ["ger"] = "德语", ["spa"] = "西班牙语", ["rus"] = "俄语",
            ["por"] = "葡萄牙语", ["ita"] = "意大利语", ["ara"] = "阿拉伯语", ["tha"] = "泰语",
            ["vie"] = "越南语", ["ind"] = "印尼语", ["tur"] = "土耳其语", ["und"] = "未指定",
        });

    public readonly record struct LangChoice(string Code, string Name);

    /// <summary>下拉框语言选项，顺序与内容同原 LANG_CHOICES。</summary>
    public static readonly IReadOnlyList<LangChoice> LangChoices = new[]
    {
        new LangChoice("chi", "中文"), new LangChoice("eng", "英语"),
        new LangChoice("jpn", "日语"), new LangChoice("kor", "韩语"),
        new LangChoice("und", "未指定"), new LangChoice("fre", "法语"),
        new LangChoice("ger", "德语"), new LangChoice("spa", "西班牙语"),
        new LangChoice("rus", "俄语"), new LangChoice("por", "葡萄牙语"),
        new LangChoice("ita", "意大利语"), new LangChoice("ara", "阿拉伯语"),
        new LangChoice("tha", "泰语"), new LangChoice("vie", "越南语"),
        new LangChoice("ind", "印尼语"), new LangChoice("tur", "土耳其语"),
    };

    /// <summary>编码检测候选，顺序同原 CANDIDATE_ENCODINGS。</summary>
    public static readonly string[] CandidateEncodings =
    {
        "utf-8", "gb18030", "big5", "shift_jis", "euc-jp", "euc-kr", "cp1252",
    };

    #region 常见简/繁体汉字字频表（用于编码打分，逐字对应原 COMMON_SIMPLIFIED / COMMON_TRADITIONAL）

    public const string CommonSimplified =
        "的一是不了在人有我他这个们中来上大为和国地到以说时要就出会可也你对生能而子那得于着下" +
        "自之年过发后作里用道行所然家种事成方多经么去法学如都同现当没动面起看定天分还进好小" +
        "部其些主样理心她本前开但因只从想实日军者意无力它与长把机十民第公此已工使情明性知全" +
        "三又关点正业外将两高间由问很最重并物手应战向头文体政美相见被利什二等产或新己制身果" +
        "加西斯月话合回特代内信表化老给世位次度门任常先海通教儿原东声提立及比员解水名真论处" +
        "走义各入几口认条平系气题活尔更别打女变四神总何电数安少报才结反受目太量再感建务做接" +
        "必场件计管期市直德资命山金指克许统区保至队形社便空决治展马科司五基眼书非则听白却界" +
        "达光放强即像难且权思王象完设式色路记南品住告类求据程北边死张该交规万取拉格望觉术领" +
        "共确传师观清今切院让识候带导争运笑飞风步改收根干造言联持组每济车亲极林服快办议往元" +
        "英士证近失转夫令准布始怎呢存未远叫台单影具罗字爱击流备兵连调深商算质团集百需价花党" +
        "华城石级整府离况亚请技际约示复病息究线似官火断精满支视消越器容照须九增研写称企八功" +
        "吗包片史委乎查轻易早曾除农找装广显吧阿李标谈吃图念六引历首医局突专费号尽另周较注语" +
        "仅考落青随选列武红响虽推势参希古众构房半节土投某案黑维革划敌致陈律足态护七兴派孩验" +
        "责营星够章音跟志底站严例留调";

    public const string CommonTraditional =
        "的一是不了在人有我他這個們中來上大為和國地到以說時要就出會可也你對生能而子那得於著下" +
        "自之年過發後作裡用道行所然家種事成方多經麼去法學如都同現當沒動面起看定天分還進好小" +
        "部其些主樣理心她本前開但因只從想實日軍者意無力它與長把機十民第公此已工使情明性知全" +
        "三又關點正業外將兩高間由問很最重並物手應戰向頭文體政美相見被利什二等產或新己制身果" +
        "加西斯月話合回特代內信表化老給世位次度門任常先海通教兒原東聲提立及比員解水名真論處" +
        "走義各入幾口認條平係氣題活爾更別打女變四神總何電數安少報才結反受目太量再感建務做接" +
        "必場件計管期市直德資命山金指克許統區保至隊形社便空決治展馬科司五基眼書非則聽白卻界" +
        "達光放強即像難且權思王象完設式色路記南品住告類求據程北邊死張該交規萬取拉格望覺術領" +
        "共確傳師觀清今切院讓識候帶導爭運笑飛風步改收根幹造言聯持組每濟車親極林服快辦議往元" +
        "英士證近失轉夫令準布始怎呢存未遠叫臺單影具羅字愛擊流備兵連調深商算質團集百需價花黨" +
        "華城石級整府離況亞請技際約示復病息究線似官火斷精滿支視消越器容照須九增研寫稱企八功" +
        "嗎包片史委乎查輕易早曾除農找裝廣顯吧阿李標談吃圖念六引歷首醫局突專費號盡另周較注語" +
        "僅考落青隨選列武紅響雖推勢參希古眾構房半節土投某案黑維革劃敵致陳律足態護七興派孩驗" +
        "責營星夠章音跟志底站嚴例留調";

    #endregion

    /// <summary>
    /// 原 NOISE_RE：两侧用「非字母数字」断言而不是 \b，中文紧邻时也能命中，
    /// 同时不破坏包含这些片段的正常词（如 nosub 里的 sub、english 里的 eng）。
    /// </summary>
    public const string NoisePattern =
        @"(?<![a-z0-9])" +
        @"(480p|720p|1080p|1440p|2160p|4320p|4k|8k|bluray|blu-?ray|bdrip|brrip|" +
        @"webrip|web-?dl|webdl|hdrip|hdtv|hddvd|dvdrip|dvdscr|remux|x264|x265|" +
        @"h264|h265|hevc|xvid|divx|aac|ac3|eac3|dts|dd5|ddp5|truehd|atmos|" +
        @"10bit|8bit|hdr|hdr10|sdr|dolby|vision|dv|amzn|nf|dsnp|hmax|max|" +
        @"proper|repack|internal|extended|unrated|imax|directors|cut|" +
        @"mp4|mkv|avi|mov|webm|ts)" +
        @"(?![a-z0-9])";

    public static readonly Regex NoiseRegex =
        new(NoisePattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>括号类内容：[] () {}（原 tokenize 中的 re.sub）。</summary>
    public static readonly Regex BracketRegex =
        new(@"\[[^\]]*\]|\([^)]*\)|\{[^}]*\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>只保留中/日/韩文字、字母、数字，其余替换为空格。</summary>
    public static readonly Regex KeepCharsRegex =
        new(@"[^\u4e00-\u9fff\u3040-\u30ff\uac00-\ud7af0-9a-z]+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static readonly Regex KanaRegex = new(@"[\u3040-\u30ff]", RegexOptions.Compiled);
    public static readonly Regex HangulRegex = new(@"[\uac00-\ud7af]", RegexOptions.Compiled);
    public static readonly Regex HanRegex = new(@"[\u4e00-\u9fff]", RegexOptions.Compiled);

    public const string LogSeparator = "============================================================"; // 60 个 '='
}
