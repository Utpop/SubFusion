namespace SubFusion.Core.Models;

/// <summary>一条字幕轨（原 core.SubTrack）。</summary>
public sealed class SubTrack
{
    public string Path { get; set; } = "";
    public string Lang { get; set; } = "und";
    public string Title { get; set; } = "";
    public bool Default { get; set; }
    public bool Forced { get; set; }
    public string Kind { get; set; } = "text";   // text / bitmap
    public string Encoding { get; set; } = "";
    public bool Enabled { get; set; } = true;

    public string Name => System.IO.Path.GetFileName(Path);

    public SubTrack() { }

    public SubTrack(string path, string lang = "und", string title = "", bool @default = false,
                    bool forced = false, string kind = "text", string encoding = "", bool enabled = true)
    {
        Path = path; Lang = lang; Title = title; Default = @default; Forced = forced;
        Kind = kind; Encoding = encoding; Enabled = enabled;
    }

    /// <summary>列表显示文本：文件名（语言） [默认] [强制]。</summary>
    public string Label()
    {
        string lang = Constants.LangNames.TryGetValue(Lang, out var name) ? name : (Lang ?? "");
        string text = Name;
        if (!string.IsNullOrEmpty(lang))
            text = $"{text}（{lang}）";
        if (Default) text += " [默认]";
        if (Forced) text += " [强制]";
        return text;
    }

    public SubTrack Clone() => new(Path, Lang, Title, Default, Forced, Kind, Encoding, Enabled);
}
