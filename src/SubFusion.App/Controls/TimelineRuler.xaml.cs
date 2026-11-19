using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Shapes;
using SubFusion.Core.Trim;

namespace SubFusion.App.Controls;

/// <summary>
/// 时间轴区间选择控件。
///
/// 这里只做三件事：把指针位置交给 Core 的 <see cref="TimelineGeometry"/> 换算、
/// 把结果画出来、在变化时发一个事件。
/// 所有夹紧/吸附/命中判定都在 Core 里，因此那些逻辑有自检覆盖。
/// </summary>
public sealed partial class TimelineRuler : UserControl
{
    private double _duration;
    private double _start;
    private double _end;

    /// <summary>无损切割时为 true：拖动起点时吸附到关键帧。</summary>
    public bool SnapEnabled { get; set; } = true;

    /// <summary>吸附容差（秒）。约等于 1% 的可见宽度，肉眼不可见但贴合手感。</summary>
    private double SnapTolerance => Math.Clamp(_duration * 0.01, 0.05, 1.0);

    private IReadOnlyList<double> _keyframes = Array.Empty<double>();

    private TimelineHit _dragging = TimelineHit.None;
    private double _dragPointerStartX;
    private double _dragStart;
    private double _dragEnd;

    /// <summary>区间变化（秒）。拖动过程中连续触发，便于实时刷新预计结果。</summary>
    public event EventHandler<(double Start, double End)>? RangeChanged;

    /// <summary>拖动结束（松手或取消）。用它把最终值回写输入框。</summary>
    public event EventHandler<(double Start, double End)>? RangeCommitted;

    public TimelineRuler()
    {
        InitializeComponent();
        UpdateHandles();
    }

    /// <summary>设置总时长与关键帧；tickCount 过多时不画，避免遮挡。</summary>
    public void Configure(double duration, IReadOnlyList<double> keyframes)
    {
        _duration = Math.Max(0, double.IsNaN(duration) ? 0 : duration);
        _keyframes = keyframes ?? Array.Empty<double>();
        if (_end <= 0 || _end > _duration) _end = _duration;
        _start = TimelineGeometry.ClampSeconds(_start, _duration);
        if (_end <= _start) _start = 0;
        DrawTicks();
        UpdateHandles();
    }

    /// <summary>设置当前区间（不触发事件，供外部输入框回写）。</summary>
    public void SetRange(double start, double end, bool notify = false)
    {
        (double s, double e) = TimelineGeometry.MoveStart(start, Math.Max(end, TimelineGeometry.MinDurationSeconds), _duration);
        e = Math.Min(Math.Max(e, s + TimelineGeometry.MinDurationSeconds), Math.Max(_duration, s + TimelineGeometry.MinDurationSeconds));
        bool changed = Math.Abs(s - _start) > 1e-6 || Math.Abs(e - _end) > 1e-6;
        _start = s;
        _end = e;
        UpdateHandles();
        if (changed && notify)
        {
            RangeChanged?.Invoke(this, (_start, _end));
        }
    }

    public (double Start, double End) Range => (_start, _end);

    private double TrackWidth => Math.Max(1, RootGrid.ActualWidth);

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        DrawTicks();
        UpdateHandles();
    }

    private void UpdateHandles()
    {
        double width = TrackWidth;
        double startX = TimelineGeometry.XFromSeconds(_start, width, _duration);
        double endX = TimelineGeometry.XFromSeconds(_end, width, _duration);
        if (endX < startX) (startX, endX) = (endX, startX);

        StartHandle.Margin = new Thickness(Math.Max(0, startX - StartHandle.Width / 2), 0, 0, 0);
        EndHandle.Margin = new Thickness(Math.Max(0, endX - EndHandle.Width / 2), 0, 0, 0);

        SelectionBar.Width = Math.Max(0, endX - startX);
        SelectionBar.Margin = new Thickness(startX, 0, 0, 0);

        StartLabel.Text = TimelineGeometry.ShortLabel(_start);
        EndLabel.Text = _duration > 0 ? TimelineGeometry.ShortLabel(_end - _start) : "";
        TotalLabel.Text = _duration > 0 ? "/ " + TimelineGeometry.ShortLabel(_duration) : "";
    }

    /// <summary>
    /// 画关键帧刻度。
    /// 上限 400 条：全关键帧素材可能有几万个关键帧，逐条建元素会把 UI 线程拖死，
    /// 这种情况改为等距抽稀——刻度只是提示，不需要精确到每一帧。
    /// </summary>
    private void DrawTicks()
    {
        TickLayer.Children.Clear();
        if (_duration <= 0 || _keyframes.Count == 0) return;

        double width = TrackWidth;
        if (width <= 2) return;

        const int MaxTicks = 400;
        int step = Math.Max(1, (int)Math.Ceiling(_keyframes.Count / (double)MaxTicks));

        var brush = Application.Current.Resources.TryGetValue("TextFillColorTertiaryBrush", out object? b) &&
                    b is Microsoft.UI.Xaml.Media.Brush found
            ? found
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray);

        for (int i = 0; i < _keyframes.Count; i += step)
        {
            double x = TimelineGeometry.XFromSeconds(_keyframes[i], width, _duration);
            var tick = new Rectangle
            {
                Width = 1,
                Height = 10,
                Fill = brush,
                Opacity = 0.6,
            };
            Canvas.SetLeft(tick, x);
            Canvas.SetTop(tick, 0);
            TickLayer.Children.Add(tick);
        }
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_duration <= 0) return;
        double x = e.GetCurrentPoint(RootGrid).Position.X;
        double width = TrackWidth;
        double startX = TimelineGeometry.XFromSeconds(_start, width, _duration);
        double endX = TimelineGeometry.XFromSeconds(_end, width, _duration);

        _dragging = TimelineGeometry.PickHandle(x, startX, endX);
        _dragPointerStartX = x;
        _dragStart = _start;
        _dragEnd = _end;

        RootGrid.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging == TimelineHit.None) return;
        double x = e.GetCurrentPoint(RootGrid).Position.X;
        double width = TrackWidth;
        double seconds = TimelineGeometry.SecondsFromX(x, width, _duration);

        (double s, double en) = _dragging switch
        {
            TimelineHit.Start => TimelineGeometry.MoveStart(Snap(seconds), _dragEnd, _duration),
            TimelineHit.End => TimelineGeometry.MoveEnd(seconds, _dragStart, _duration),
            TimelineHit.Range => TimelineGeometry.MoveRange(
                seconds - TimelineGeometry.SecondsFromX(_dragPointerStartX, width, _duration),
                _dragStart, _dragEnd, _duration),
            _ => (_start, _end),
        };

        if (Math.Abs(s - _start) < 1e-9 && Math.Abs(en - _end) < 1e-9) return;
        _start = s;
        _end = en;
        UpdateHandles();
        RangeChanged?.Invoke(this, (_start, _end));
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging == TimelineHit.None) return;
        _dragging = TimelineHit.None;
        RootGrid.ReleasePointerCapture(e.Pointer);
        RangeCommitted?.Invoke(this, (_start, _end));
        e.Handled = true;
    }

    /// <summary>
    /// 拖动起点时吸附到最近关键帧，让拖动位置与实际切点一致。
    ///
    /// 容差传 double.MaxValue = 不论多远都吸附。这不是"手感"取舍：
    /// 引擎的 KeyframeIndex.SnapToKeyframe 本身就没有容差，永远回退到上一个关键帧。
    /// 界面若在容差外不吸附，就会出现「标尺显示 5.3s、实际从 2.0s 开始」的所见非所得，
    /// 而那正是这个控件存在的理由。
    /// </summary>
    private double Snap(double seconds)
    {
        if (!SnapEnabled || _keyframes.Count == 0) return seconds;
        return TimelineGeometry.SnapToPreviousKeyframe(_keyframes, seconds, double.MaxValue);
    }

    /// <summary>指针形状反馈：可拖的地方给手型光标。</summary>
    private void UpdateCursor(PointerRoutedEventArgs e)
    {
        try { ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand); }
        catch { /* 某些环境不支持自定义光标，不影响拖动 */ }
    }
}
