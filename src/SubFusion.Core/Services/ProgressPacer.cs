namespace SubFusion.Core.Services;

/// <summary>
/// 进度刷新节流。
///
/// 存在的理由不只是省 CPU，更是一个具体的界面故障：
/// 节流如果「超时就整条丢弃」，而被丢掉的恰好是任务的最后一条进度
/// （例如「合并完成 / 100%」），界面就永远停在前一条上——
/// 任务其实早已成功结束，进度条却卡在 99% 和上一句文案，
/// 用户只能认为程序卡死了。
///
/// 所以规则是：普通进度按最小间隔丢弃，<b>终态进度无条件送达</b>。
/// </summary>
public static class ProgressPacer
{
    /// <summary>两次界面刷新之间的最小间隔。</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// 本次进度是否应该刷新界面。
    /// </summary>
    /// <param name="sinceLastFlush">距离上次刷新的时长。</param>
    /// <param name="terminal">
    /// 是否是最后一条进度（进入 Completed / Failed / Cancelled，或已经到 100%）。
    /// 终态永远送达，不受间隔限制。
    /// </param>
    public static bool ShouldDeliver(TimeSpan sinceLastFlush, bool terminal)
        => terminal || sinceLastFlush >= MinInterval;

    /// <summary>进度值本身是否代表「已经收尾」（>= 100%）。</summary>
    public static bool IsTerminalPercent(double percent) => percent >= 100.0 - 1e-9;
}
