using System.Globalization;
using System.Text.Json;
using SubFusion.Core.Services;

namespace SubFusion.Core.Trim;

/// <summary>
/// 读取源视频的显示矩阵角度。
///
/// 单独一件事：<see cref="Media.MediaInspector"/> 解析的是编码参数，
/// 而旋转藏在 stream 的 side_data_list 里，两者取数方式不同。
/// 手机竖拍素材普遍带 90° 显示方向，裁剪时必须原样保留，
/// 否则输出会从「竖屏」变成「横躺」，用户第一眼就会发现画面歪了。
/// </summary>
public static class RotationProbe
{
    /// <summary>读取第一路视频流的显示角度，归一化到 0/90/180/270。失败返回 0。</summary>
    public static async Task<int> ReadAsync(string ffprobe, string path,
                                            CancellationToken cancellationToken = default,
                                            int timeoutSeconds = 30)
        => await TryReadAsync(ffprobe, path, cancellationToken, timeoutSeconds).ConfigureAwait(false) ?? 0;

    /// <summary>
    /// 同 <see cref="ReadAsync"/>，但「读不出来」返回 null。
    ///
    /// 二者必须分开：0° 是「确实没有显示方向」，null 是「没能问出来」。
    /// 混为一谈时，竖屏素材在 ffprobe 超时后会被安静地当成正常横屏素材，
    /// 界面还会明确告诉用户「源文件没有方向」——用户裁完才发现画面躺倒。
    /// </summary>
    public static async Task<int?> TryReadAsync(string ffprobe, string path,
                                                CancellationToken cancellationToken = default,
                                                int timeoutSeconds = 30)
    {
        if (string.IsNullOrEmpty(ffprobe) || !File.Exists(path)) return null;

        string[] args =
        {
            "-v", "error",
            "-select_streams", "v:0",
            "-show_streams",
            "-of", "json",
            path,
        };

        ProcessCapture proc;
        try
        {
            proc = await ProcessUtil.RunAsync(ffprobe, args, timeoutSeconds,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }

        if (proc.TimedOut || proc.ExitCode != 0 || string.IsNullOrWhiteSpace(proc.Stdout)) return null;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(proc.Stdout);
            if (!doc.RootElement.TryGetProperty("streams", out JsonElement streams) ||
                streams.ValueKind != JsonValueKind.Array)
                return null;

            foreach (JsonElement stream in streams.EnumerateArray())
            {
                if (!stream.TryGetProperty("side_data_list", out JsonElement sides) ||
                    sides.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (JsonElement side in sides.EnumerateArray())
                {
                    string type = side.TryGetProperty("side_data_type", out JsonElement t)
                        ? t.ToString() : "";
                    if (!type.Contains("Display Matrix", StringComparison.OrdinalIgnoreCase) &&
                        !type.Contains("DisplayMatrix", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (side.TryGetProperty("rotation", out JsonElement rotation) &&
                        double.TryParse(rotation.ToString(), NumberStyles.Float,
                                        CultureInfo.InvariantCulture, out double degrees))
                        return RotationPlan.Normalize((int)Math.Round(degrees));
                }
            }
        }
        catch { /* 解析失败按「未能读取」处理，不从错误数据里推断方向 */ }
        // 能读到流与 side_data，只是没有显示矩阵 = 确实是 0°。
        return 0;
    }
}
