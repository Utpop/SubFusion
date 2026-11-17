using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SubFusion.Core.Services;

/// <summary>
/// 配置读写，逐项对应原 core.load_config / save_config。
/// 路径：%USERPROFILE%\.subfusion.json；UTF-8 无 BOM、ensure_ascii=False（不转义中文）、
/// indent=2；读取失败返回空配置，写入失败静默忽略。
/// 与 Python 版一致：读入的未知键会原样保留并写回，不丢失用户手工添加的字段。
/// </summary>
public sealed class AppConfig
{
    private readonly JsonObject _root;
    private readonly object _saveLock = new();

    private AppConfig(JsonObject root) => _root = root;

    public static string ConfigPath => Constants.ConfigPath;

    public static AppConfig Load()
    {
        try
        {
            string text = File.ReadAllText(ConfigPath, Encoding.UTF8);
            JsonNode? node = JsonNode.Parse(text);
            return new AppConfig(node as JsonObject ?? new JsonObject());
        }
        catch
        {
            return new AppConfig(new JsonObject());
        }
    }

    public void Save()
    {
        // 串行化写入，避免并发 Save 互相踩踏；临时文件 + 原子替换防止崩溃时截断配置。
        lock (_saveLock)
        {
            string? tempPath = null;
            try
            {
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                };
                string text = _root.ToJsonString(options);

                string dir = Path.GetDirectoryName(ConfigPath) ?? "";
                tempPath = Path.Combine(dir, $".subfusion.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
                File.WriteAllText(tempPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                File.Move(tempPath, ConfigPath, overwrite: true);
                tempPath = null;   // 替换成功，临时文件已不存在
            }
            catch
            {
                // 原实现：写入失败完全静默
            }
            finally
            {
                // 只有写入/替换失败时才会留下临时文件，清理掉，不碰已存在的配置。
                if (tempPath is not null)
                {
                    try { File.Delete(tempPath); } catch { /* 静默 */ }
                }
            }
        }
    }

    public bool Has(string key) => _root.ContainsKey(key);

    /// <summary>取字符串：数字/布尔节点返回其 JSON 文本，等价于 Python 的 str(cfg[k])。</summary>
    public string? Get(string key)
    {
        try
        {
            JsonNode? node = _root[key];
            if (node is null) return null;
            try { return node.GetValue<string>(); }
            catch { return node.ToJsonString().Trim('"'); }
        }
        catch { return null; }
    }

    public bool? GetBool(string key)
    {
        try
        {
            JsonNode? node = _root[key];
            if (node is null) return null;
            try { return node.GetValue<bool>(); }
            catch
            {
                string raw = node.ToJsonString().Trim('"');
                if (bool.TryParse(raw, out bool b)) return b;
                return raw is "0" or "" ? false : raw is "1" ? true : null;
            }
        }
        catch { return null; }
    }

    public void Set(string key, string? value) => _root[key] = JsonValue.Create(value ?? "");
    public void Set(string key, bool value) => _root[key] = JsonValue.Create(value);
    public void Set(string key, int value) => _root[key] = JsonValue.Create(value);
}
