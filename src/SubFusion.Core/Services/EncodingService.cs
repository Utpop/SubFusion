using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using SubFusion.Core.Models;

namespace SubFusion.Core.Services;

public sealed record DetectedEncoding(string Encoding, bool HasBom);
public sealed record ConvertResult(string Path, bool Created, string Encoding);
public sealed record HardSubFailure(string Name, string Reason);
public sealed record PrepareResult(List<SubTrack> Prepared, List<HardSubFailure> Failed);

/// <summary>
/// 文本字幕编码检测、UTF-8 转换、转码缓存、硬字幕工作副本
/// （原 core._score_text / _looks_like_utf16 / detect_encoding / is_text_subtitle /
/// ensure_utf8 / clear_utf8_cache / _convert_to_utf8 / prepare_hard_subs）。
///
/// 依赖：System.Text.Encoding.CodePages（微软官方包）。.NET Core 移除了内置的
/// GB18030/Big5/Shift-JIS/EUC 代码页，必须注册 CodePagesEncodingProvider 才能与 Python 等价。
/// </summary>
public static class EncodingService
{
    static EncodingService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private static readonly Dictionary<string, int> CodePageMap = new(StringComparer.Ordinal)
    {
        ["gb18030"] = 54936,
        ["big5"] = 950,
        ["shift_jis"] = 932,
        ["euc-jp"] = 51932,
        ["euc-kr"] = 51949,
        ["cp1252"] = 1252,
    };

    private static readonly ConcurrentDictionary<(string Path, string TempDir, long LastWriteTicks, long Length), ConvertResult> Utf8Cache = new();

    public static bool IsTextSubtitle(string path)
        => Constants.TextSubExts.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>清空转码缓存（每轮批量处理结束后调用）。</summary>
    public static void ClearUtf8Cache() => Utf8Cache.Clear();

    #region 编码检测

    private static double ScoreText(string text, string encoding)
    {
        if (string.IsNullOrEmpty(text)) return -1.0;
        string sample = text.Length > 4000 ? text[..4000] : text;
        int total = sample.Length;
        if (total == 0) return -1.0;

        int bad = 0, cjk = 0, kana = 0, hangul = 0;
        foreach (char ch in sample)
        {
            int code = ch;
            if (code < 0x20 && ch != '\r' && ch != '\n' && ch != '\t') bad++;
            else if (code >= 0x7F && code <= 0x9F) bad++;
            else if (code == 0xFFFD) bad += 10;
            else if (code >= 0x4E00 && code <= 0x9FFF) cjk++;
            else if (code >= 0x3040 && code <= 0x30FF) kana++;
            else if (code >= 0xAC00 && code <= 0xD7AF) hangul++;
        }

        double score = -bad * 5.0;
        if (encoding == "utf-8") score += 30.0;
        else score += cjk * 0.35 + kana * 0.4 + hangul * 0.4;

        if (cjk != 0)
        {
            // 原写法对每个候选编码(约 6 种)都做 4000 字符 × 上千字符表的 O(N*M) 扫描；
            // 用预构建的 HashSet 把每次查询降到 O(1)。
            var present = new HashSet<char>(sample);
            int simp = Constants.CommonSimplified.Count(present.Contains);
            int trad = Constants.CommonTraditional.Count(present.Contains);
            if (encoding == "gb18030") score += simp * 0.6 - trad * 0.25;
            else if (encoding == "big5") score += trad * 0.6 - simp * 0.25;
            else if (encoding is "shift_jis" or "euc-jp") score += kana * 0.5;
            else if (encoding == "euc-kr") score += hangul * 0.5;
        }
        return score;
    }

    /// <summary>无 BOM 的 UTF-16 启发式判断（ASCII 字幕里总有一个字节为 0）。</summary>
    private static string? LooksLikeUtf16(byte[] raw)
    {
        int len = Math.Min(raw.Length, 4000);
        if (len < 8) return null;
        int pairs = len / 2;
        if (pairs < 4) return null;
        int zerosEven = 0, zerosOdd = 0;
        for (int i = 0; i < pairs * 2 - 1; i += 2)
            if (raw[i] == 0) zerosEven++;
        for (int i = 1; i < pairs * 2; i += 2)
            if (raw[i] == 0) zerosOdd++;
        double ratioEven = zerosEven / (double)pairs;
        double ratioOdd = zerosOdd / (double)pairs;
        if (ratioEven > 0.55 && ratioOdd < 0.15) return "utf-16-be";
        if (ratioOdd > 0.55 && ratioEven < 0.15) return "utf-16-le";
        return null;
    }

    private static bool StartsWith(byte[] raw, params byte[] bom)
    {
        if (raw.Length < bom.Length) return false;
        for (int i = 0; i < bom.Length; i++)
            if (raw[i] != bom[i]) return false;
        return true;
    }

    /// <summary>严格解码（对应 Python raw.decode(enc, errors="strict")）。</summary>
    private static bool TryStrictDecode(byte[] raw, string encoding, int codePage, out string text)
    {
        try
        {
            Encoding enc = Encoding.GetEncoding(codePage,
                EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            text = enc.GetString(raw);
            return true;
        }
        catch
        {
            text = "";
            return false;
        }
    }

    /// <summary>取绝对路径 SHA256 的前 10 位十六进制，作为转换文件名的唯一短哈希。</summary>
    private static string ShortPathHash(string fullPath)
    {
        byte[] bytes;
        using (var sha = SHA256.Create())
            bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(fullPath));
        return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant()[..10];
    }

    /// <summary>原 detect_encoding：BOM → 无 BOM UTF-16 → 严格 UTF-8 → 候选编码打分。</summary>
    public static DetectedEncoding DetectEncoding(string path, int sampleSize = 262144)
    {
        byte[] raw;
        long fileLength;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fileLength = fs.Length;
            var buffer = new byte[sampleSize];
            int read = fs.Read(buffer, 0, buffer.Length);
            if (read != buffer.Length)
                Array.Resize(ref buffer, read);
            raw = buffer;
        }
        catch
        {
            return new DetectedEncoding("utf-8", false);   // OSError → ("utf-8", False)
        }

        // 有些 legacy 编码文件开头整段都是纯 ASCII，仅看前 256KB 采样会通过严格 UTF-8
        // 判断而被误判成 utf-8；真正的非 ASCII 字节出现在更靠后的位置。
        // 当采样全是 ASCII 且文件还有更多内容时，读取更大范围（必要时整个文件）重新检测。
        if (raw.Length > 0 && raw.Length < fileLength && raw.All(b => b < 0x80))
        {
            try
            {
                // 分块有界扫描：只在遇到第一个非 ASCII 字节时停止，避免为大字幕整文件分配 byte[]。
                // 上限 4MB——足以越过字幕头部大段 ASCII 说明，又不会让内存随文件大小增长。
                const int maxExtended = 4 * 1024 * 1024;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var extended = new List<byte>(Math.Min((int)Math.Min(fileLength, maxExtended), 1 << 20));
                extended.AddRange(raw);
                var chunk = new byte[64 * 1024];
                while (extended.Count < maxExtended)
                {
                    int n = fs.Read(chunk, 0, (int)Math.Min(chunk.Length, maxExtended - extended.Count));
                    if (n == 0) break;
                    bool nonAscii = false;
                    for (int k = 0; k < n; k++) if (chunk[k] >= 0x80) { nonAscii = true; break; }
                    extended.AddRange(new ArraySegment<byte>(chunk, 0, n));
                    if (nonAscii) break;   // 找到非 ASCII 即可交给 DetectFromBytes 判定
                }
                if (extended.Count > raw.Length) raw = extended.ToArray();
            }
            catch { /* 扩大采样失败则沿用原采样 */ }
        }

        return DetectFromBytes(raw);
    }

    /// <summary>对给定字节采样执行检测；BOM 顺序与打分系数与 Python v1.1 完全一致。</summary>
    private static DetectedEncoding DetectFromBytes(byte[] raw)
    {
        // codecs.BOM_* 顺序：UTF-32 的 LE/BE BOM 必须以 4 字节判断，否则会被 UTF-16 BOM 抢先
        if (StartsWith(raw, 0xEF, 0xBB, 0xBF)) return new DetectedEncoding("utf-8-sig", true);
        if (StartsWith(raw, 0xFF, 0xFE, 0x00, 0x00)) return new DetectedEncoding("utf-32", true);
        if (StartsWith(raw, 0x00, 0x00, 0xFE, 0xFF)) return new DetectedEncoding("utf-32", true);
        if (StartsWith(raw, 0xFF, 0xFE)) return new DetectedEncoding("utf-16", true);
        if (StartsWith(raw, 0xFE, 0xFF)) return new DetectedEncoding("utf-16", true);

        int zeroCount = raw.Count(b => b == 0);
        if (zeroCount > Math.Max(2, raw.Length / 8))
        {
            string? guess = LooksLikeUtf16(raw);
            if (guess is not null) return new DetectedEncoding(guess, false);
        }

        try
        {
            var strictUtf8 = new UTF8Encoding(false, true);
            _ = strictUtf8.GetString(raw);
            return new DetectedEncoding("utf-8", false);
        }
        catch (DecoderFallbackException) { /* 继续候选编码 */ }

        string best = "utf-8";
        double bestScore = -1e9;
        foreach (string enc in Constants.CandidateEncodings)
        {
            if (enc == "utf-8") continue;
            if (!CodePageMap.TryGetValue(enc, out int codePage)) continue;
            if (!TryStrictDecode(raw, enc, codePage, out string text)) continue;
            double score = ScoreText(text, enc);
            if (score > bestScore)
            {
                bestScore = score;
                best = enc;
            }
        }
        return new DetectedEncoding(best, false);
    }

    #endregion

    #region 解码

    /// <summary>
    /// 按 Python codec 名称解码（errors="replace"）。BOM 感知的 codec 会自动去掉 BOM，
    /// 行为与 Python 的 utf-8-sig / utf-16 / utf-32 一致。
    /// </summary>
    private static string Decode(byte[] raw, string encoding)
    {
        try
        {
            switch (encoding)
            {
                case "utf-8-sig":
                {
                    int skip = StartsWith(raw, 0xEF, 0xBB, 0xBF) ? 3 : 0;
                    return ProcessUtil.Utf8Replace.GetString(raw, skip, raw.Length - skip);
                }
                case "utf-8":
                    return ProcessUtil.Utf8Replace.GetString(raw);
                case "utf-16":
                {
                    Encoding enc = StartsWith(raw, 0xFE, 0xFF)
                        ? new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: false)
                        : new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: false);
                    int skip = (StartsWith(raw, 0xFF, 0xFE) || StartsWith(raw, 0xFE, 0xFF)) ? 2 : 0;
                    return enc.GetString(raw, skip, raw.Length - skip);
                }
                case "utf-16-be":
                    return new UnicodeEncoding(true, false, false).GetString(raw);
                case "utf-16-le":
                    return new UnicodeEncoding(false, false, false).GetString(raw);
                case "utf-32":
                {
                    bool bigEndian = StartsWith(raw, 0x00, 0x00, 0xFE, 0xFF);
                    var enc = new UTF32Encoding(bigEndian, false, false);
                    int skip = (StartsWith(raw, 0xFF, 0xFE, 0x00, 0x00) || bigEndian) ? 4 : 0;
                    return enc.GetString(raw, skip, raw.Length - skip);
                }
                default:
                {
                    int codePage = CodePageMap.TryGetValue(encoding, out int cp) ? cp : 65001;
                    return Encoding.GetEncoding(codePage,
                        EncoderFallback.ReplacementFallback,
                        DecoderFallback.ReplacementFallback).GetString(raw);
                }
            }
        }
        catch
        {
            // Python 的 LookupError 兜底：raw.decode("utf-8", errors="replace")
            return ProcessUtil.Utf8Replace.GetString(raw);
        }
    }

    private static string StripBomChar(string text)
        => text.StartsWith('\uFEFF') ? text[1..] : text;

    #endregion

    #region UTF-8 转换

    /// <summary>原 ensure_utf8：已是 UTF-8 无 BOM 则原样返回；否则转存临时文件并缓存。</summary>
    public static ConvertResult EnsureUtf8(string path, string tempDir, bool useCache = true)
    {
        if (!IsTextSubtitle(path))
            return new ConvertResult(path, false, "");

        string keyPath, keyTemp;
        long keyTicks = 0, keyLength = -1;
        try
        {
            keyPath = Path.GetFullPath(path);
            keyTemp = Path.GetFullPath(tempDir);
            var fileInfo = new FileInfo(path);
            keyTicks = fileInfo.LastWriteTimeUtc.Ticks;
            keyLength = fileInfo.Length;
        }
        catch
        {
            keyPath = path; keyTemp = tempDir;
        }
        var key = (keyPath, keyTemp, keyTicks, keyLength);

        if (useCache && Utf8Cache.TryGetValue(key, out var cached))
            return cached;

        ConvertResult result = ConvertToUtf8(path, tempDir);
        if (useCache && result.Created)
            Utf8Cache[key] = result;
        return result;
    }

    private static ConvertResult ConvertToUtf8(string path, string tempDir)
    {
        DetectedEncoding detected = DetectEncoding(path);
        string encoding = detected.Encoding;

        byte[] raw;
        try { raw = File.ReadAllBytes(path); }
        catch { return new ConvertResult(path, false, encoding); }

        string text = Decode(raw, encoding);
        if (detected.HasBom) text = StripBomChar(text);

        // 即便已是 UTF-8，ASS/SSA 仍可能需要补段头（见 NormalizeAssScriptInfo），
        // 因此只有「无需改编码 且 无需修复结构」时才原样返回。
        bool repair = NeedsScriptInfoRepair(path, text);
        if (encoding == "utf-8" && !detected.HasBom && !repair)
            return new ConvertResult(path, false, encoding);

        if (repair) text = NormalizeAssScriptInfo(text);

        try { Directory.CreateDirectory(tempDir); }
        catch { return new ConvertResult(path, false, encoding); }

        string name = Path.GetFileName(path);
        string stem = Path.GetFileNameWithoutExtension(name);
        string ext = Path.GetExtension(name);
        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch { fullPath = path; }
        string outPath = Path.Combine(tempDir, $"{stem}_{ShortPathHash(fullPath)}_utf8{ext}");
        try
        {
            // Python: open(out_path, "w", encoding="utf-8", newline="") —— 无 BOM、换行不转换
            File.WriteAllText(outPath, text, new UTF8Encoding(false));
            return new ConvertResult(outPath, true, encoding);
        }
        catch { return new ConvertResult(path, false, encoding); }
    }

    #endregion

    #region ASS/SSA 结构修复

    /// <summary>
    /// 判断 ASS/SSA 是否缺少 <c>[Script Info]</c> 段头。
    ///
    /// 有些字幕站/工具导出的 .ass 会把 <c>[Script Info]</c> 这一行本身丢掉，
    /// 文件直接从 <c>Title:</c> 开始。libavformat 的 ass 解析器要求第一行必须是段头，
    /// 于是整个文件被判为 <c>Invalid data found</c>，进而被当成「无效字幕」剔除——
    /// 表现为界面上明明是有效字幕，开始处理却全部「没有可用字幕轨」。
    /// </summary>
    public static bool NeedsScriptInfoRepair(string path, string? text = null)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".ass" or ".ssa")) return false;
        if (text is null)
        {
            try
            {
                DetectedEncoding detected = DetectEncoding(path);
                byte[] raw = File.ReadAllBytes(path);
                text = Decode(raw, detected.Encoding);
                if (detected.HasBom) text = StripBomChar(text);
            }
            catch { return false; }
        }

        // 取第一行非空内容判断
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim().Trim('\uFEFF', '\r');
            if (trimmed.Length == 0) continue;
            return !trimmed.StartsWith("[Script Info]", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    /// <summary>在文件开头补上缺失的 <c>[Script Info]</c> 段头（幂等：已存在时原样返回）。</summary>
    public static string NormalizeAssScriptInfo(string text)
    {
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim().Trim('\uFEFF', '\r');
            if (trimmed.Length == 0) continue;
            if (trimmed.StartsWith("[Script Info]", StringComparison.OrdinalIgnoreCase)) return text;
            break;
        }
        return "[Script Info]\r\n" + text.TrimStart('\uFEFF');
    }

    #endregion

    #region 硬字幕工作副本

    /// <summary>
    /// 原 prepare_hard_subs：复制到工作目录并改成 ASCII 文件名（libass 对 Windows 路径里的
    /// 冒号/反斜杠转义很挑剔）。文本字幕顺带转 UTF-8。
    /// </summary>
    public static PrepareResult PrepareHardSubs(IReadOnlyList<SubTrack> tracks, string workDir,
                                                bool convert = true)
    {
        var prepared = new List<SubTrack>();
        var failed = new List<HardSubFailure>();
        try { Directory.CreateDirectory(workDir); }
        catch (Exception ex)
        {
            return new PrepareResult(prepared,
                tracks.Select(t => new HardSubFailure(t.Name, $"无法创建工作目录：{ex.Message}")).ToList());
        }

        for (int index = 0; index < tracks.Count; index++)
        {
            SubTrack track = tracks[index];
            string ext = Path.GetExtension(track.Path).ToLowerInvariant();
            if (string.IsNullOrEmpty(ext)) ext = ".srt";
            string target = Path.Combine(workDir, $"hardsub_{index}{ext}");
            try
            {
                // 结构修复（缺 [Script Info]）与转编码独立判断：
                // 硬字幕走 libass，没有段头同样无法解析，所以即使 convert=false 也要修。
                if (IsTextSubtitle(track.Path) && (convert || NeedsScriptInfoRepair(track.Path)))
                {
                    DetectedEncoding detected = DetectEncoding(track.Path);
                    byte[] raw = File.ReadAllBytes(track.Path);
                    string text = Decode(raw, detected.Encoding);
                    text = StripBomChar(text);     // 原实现无条件去 BOM 字符
                    text = NormalizeAssScriptInfo(text);   // 缺 [Script Info] 时补段头，否则 libass 不认
                    File.WriteAllText(target, text, new UTF8Encoding(false));
                }
                else
                {
                    File.Copy(track.Path, target, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                failed.Add(new HardSubFailure(track.Name, ex.Message));
                continue;
            }

            SubTrack clone = track.Clone();
            clone.Path = target;
            prepared.Add(clone);
        }
        return new PrepareResult(prepared, failed);
    }

    #endregion
}
