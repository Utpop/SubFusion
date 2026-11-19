namespace SubFusion.Core.Trim;

/// <summary>视频旋转方向。数值是顺时针角度，可直接用于 ffmpeg。</summary>
public enum RotationMode
{
    /// <summary>不改变方向。</summary>
    None = 0,

    /// <summary>顺时针 90°（竖屏素材摆正）。</summary>
    Clockwise90 = 90,

    /// <summary>180°（画面上下颠倒时用）。</summary>
    Half = 180,

    /// <summary>逆时针 90°（等价于顺时针 270°）。</summary>
    CounterClockwise90 = 270,
}

/// <summary>旋转在两种裁剪模式下的实现方式。</summary>
public enum RotationStrategy
{
    /// <summary>未要求旋转。</summary>
    None = 0,

    /// <summary>只写显示矩阵（display matrix），像素不动，随 -c copy 无损写入。</summary>
    Metadata,

    /// <summary>用 transpose 滤镜真正把像素转过来，画面被烘焙进码流。</summary>
    Baked,
}

/// <summary>
/// 旋转请求的最终结论：目标角度、实现方式，以及需要如实告诉用户的取舍。
///
/// 两种方式的差别是**播放器兼容性**而不是画质：
/// Metadata 方式像素完全没动，凡支持旋转元数据的播放器（绝大多数现代播放器、手机、剪辑软件）
/// 都会正确显示；但个别老播放器、以及某些网站的转码管线会忽略元数据，于是画面仍是躺着的。
/// Baked 方式把方向写死进像素，任何播放器都正确，代价是视频必须重编码。
/// </summary>
public sealed class RotationPlan
{
    public RotationMode Requested { get; init; } = RotationMode.None;

    /// <summary>源文件原有的显示矩阵角度（0 / 90 / 180 / 270），无旋转时为 0。</summary>
    public int SourceRotation { get; init; }

    /// <summary>要写入输出的绝对角度（源角度 + 请求角度，归一化到 0/90/180/270）。</summary>
    public int TargetRotation { get; init; }

    public RotationStrategy Strategy { get; init; } = RotationStrategy.None;

    /// <summary>需要如实告知的内容；为空表示无需额外说明。</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    public bool ChangesPixels => Strategy == RotationStrategy.Baked;
    public bool ChangesMetadata => Strategy == RotationStrategy.Metadata;

    public static RotationPlan None => new();

    public string Describe() => Strategy switch
    {
        RotationStrategy.None => "不旋转",
        // 用 TargetRotation 而非 Requested：请求 None 但源带方向时实际输出是源角度，
        // 报告 0° 会让用户以为没旋转。
        RotationStrategy.Metadata => "旋转 " + TargetRotation.ToString(System.Globalization.CultureInfo.InvariantCulture) + "°（只写显示方向，不重新编码）",
        RotationStrategy.Baked => "旋转 " + TargetRotation.ToString(System.Globalization.CultureInfo.InvariantCulture) + "°（把方向烘焙进画面）",
        _ => "不旋转",
    };

    /// <summary>
    /// 归一化到 0 / 90 / 180 / 270 的标准角。
    /// ffmpeg 的 display matrix 允许负角（-90 等价 270），这里统一成正值便于书写与比较。
    /// </summary>
    public static int Normalize(int degrees)
    {
        int normalized = ((degrees % 360) + 360) % 360;
        // 只接受直角；其它角度（如 45°）在裁剪场景没有意义，按最近的直角处理。
        int quarter = (int)Math.Round(normalized / 90.0);
        return (quarter * 90) % 360;
    }

    /// <summary>
    /// 由源角度与请求角度推出最终角度。
    ///
    /// 关键事实：ffmpeg 的 <c>-display_rotation</c> 是**绝对覆盖**而不是叠加
    /// （实测对已有 90° 的文件再传 90，输出仍是 90 而不是 180）。
    /// 因此叠加运算必须由我们自己完成，再把这个绝对值传给 ffmpeg。
    /// </summary>
    public static int Compose(int sourceRotation, RotationMode requested)
        => Normalize(sourceRotation + requested.ToDegrees());

    public static RotationPlan Create(int sourceRotation, RotationMode requested, bool copying)
    {
        if (requested == RotationMode.None && Normalize(sourceRotation) == 0)
            return None;

        int target = Compose(sourceRotation, requested);
        var notes = new List<string>();

        if (requested == RotationMode.None)
        {
            // 用户没要求旋转，但源文件本身带方向：必须原样保留，否则竖屏素材会躺倒。
            notes.Add("源文件自带 " + Normalize(sourceRotation) + "° 显示方向，已原样保留");
            return new RotationPlan
            {
                Requested = RotationMode.None,
                SourceRotation = Normalize(sourceRotation),
                TargetRotation = target,
                Strategy = copying ? RotationStrategy.Metadata : RotationStrategy.Baked,
                Notes = notes,
            };
        }

        if (copying)
        {
            notes.Add("无损模式下只写入显示方向，像素未被重新编码；绝大多数播放器会正确竖放画面");
            notes.Add("个别老播放器或网站转码管线会忽略显示方向，画面仍可能横躺——" +
                      "需要处处都正确请改用「精确切割」，它会真正旋转像素");
            return new RotationPlan
            {
                Requested = requested,
                SourceRotation = Normalize(sourceRotation),
                TargetRotation = target,
                Strategy = RotationStrategy.Metadata,
                Notes = notes,
            };
        }

        notes.Add("精确模式下画面会被真正旋转并重新编码，任何播放器都显示正确");
        return new RotationPlan
        {
            Requested = requested,
            SourceRotation = Normalize(sourceRotation),
            TargetRotation = target,
            Strategy = RotationStrategy.Baked,
            Notes = notes,
        };
    }
}

/// <summary>旋转枚举 → 角度 / 中文名，供 UI 与命令拼装共用。</summary>
public static class RotationModeExtensions
{
    public static int ToDegrees(this RotationMode mode) => (int)mode;

    public static string ToLabel(this RotationMode mode) => mode switch
    {
        RotationMode.Clockwise90 => "顺时针 90°",
        RotationMode.Half => "旋转 180°",
        RotationMode.CounterClockwise90 => "逆时针 90°",
        _ => "不旋转",
    };
}
