using System.Globalization;
using SubtitleMerger.Core.Models;

namespace SubtitleMerger.Core.Services;

public sealed class BatchRequest
{
    public required Options Options { get; init; }
    public required string Ffmpeg { get; init; }
    public string? Ffprobe { get; init; }
    public required IReadOnlyList<Job> Jobs { get; init; }
    public required string TempDir { get; init; }
}

public sealed record BatchSummary(int Done, int Failed, int Skipped, int Cancelled,
                                  int Warned, double TotalSeconds, string OutputDir);

/// <summary>批量处理事件；实现类负责把回调投递到 UI 线程（原 Python 的 msg_queue + _drain_queue）。</summary>
public interface IBatchEvents
{
    void Log(string text);
    void JobUpdate(Job job);
    void Progress(Job job);
    void JobDone(Job job);
    void AllDone(BatchSummary summary);
    void Fatal(string text);
}

/// <summary>
/// 批量任务编排（原 App.start / _dispatch / _run_job 的非 UI 部分）。
/// 保留：并发数 1..4、失败隔离、随时停止、临时字幕文件、输出校验、日志文本。
/// </summary>
public sealed class BatchProcessor
{
    private readonly IBatchEvents _events;

    public BatchProcessor(IBatchEvents events) => _events = events;

    public async Task RunAsync(BatchRequest request, CancellationToken cancellationToken)
    {
        double startedAt = Formatting.UnixNow();
        Options options = request.Options;
        IReadOnlyList<Job> jobs = request.Jobs;

        try
        {
            foreach (Job job in jobs)
            {
                ResetJob(job, request);
                _events.JobUpdate(job);
            }

            _events.Log(Constants.LogSeparator);
            _events.Log(string.Format(CultureInfo.InvariantCulture,
                "开始处理：{0} 个视频，容器={1}，方式={2}，输出={3}",
                jobs.Count, options.Container,
                options.Mode == "hard" ? "硬字幕" : "软字幕", options.OutputDir));

            await DispatchAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _events.Fatal($"{ex.Message}\n{ex}");
        }

        EncodingService.ClearUtf8Cache();

        int done = jobs.Count(j => j.State == Job.StateDone);
        int failed = jobs.Count(j => j.State == Job.StateFailed);
        int skipped = jobs.Count(j => j.State == Job.StateSkipped);
        int cancelled = jobs.Count(j => j.State == Job.StateCancelled);
        int warned = jobs.Count(j => j.State == Job.StateDone && j.VerifyOk == false);
        var summary = new BatchSummary(done, failed, skipped, cancelled, warned,
                                       Formatting.UnixNow() - startedAt, options.OutputDir);
        _events.AllDone(summary);
    }

    private static void ResetJob(Job job, BatchRequest request)
    {
        job.State = Job.StatePending;
        job.Progress = 0.0;
        job.Speed = "";
        job.Eta = "";
        job.Message = "";
        job.Output = CommandBuilder.BuildOutputPath(job.Video, request.Options, request.Options.OutputDir);
    }

    /// <summary>
    /// 原 _dispatch：ThreadPoolExecutor(max_workers=workers) + as_completed。
    /// 这里用 SemaphoreSlim 限制并发，Task.WhenAny 按完成顺序回调 job_done；
    /// 取消时停止派发剩余 job_done（与原实现 break 一致）。
    /// </summary>
    private async Task DispatchAsync(BatchRequest request, CancellationToken cancellationToken)
    {
        int workers = Math.Max(1, request.Options.Workers);
        using var semaphore = new SemaphoreSlim(workers);
        // 完成通知用 Channel 而不是「每完成一个就扫一遍所有 Task」：
        // 旧实现在几百个任务时每秒要做上千次 Task.WhenAny，且为每个任务额外起了��个 Task，
        // 重负载下 UI/线程池都会被拖慢。
        var completed = System.Threading.Channels.Channel.CreateUnbounded<Job>();

        var tasks = new List<Task>(request.Jobs.Count);
        foreach (Job job in request.Jobs)
        {
            Job captured = job;
            tasks.Add(Task.Run(async () =>
            {
                bool entered = false;
                try
                {
                    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    entered = true;
                    await RunJobAsync(captured, request, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 还没轮到就取消了：也要给一个终态，界面行才不会一直停在「待处理」
                    captured.State = Job.StateCancelled;
                    captured.FinishedAt ??= Formatting.UnixNow();
                }
                catch (Exception ex)
                {
                    captured.State = Job.StateFailed;
                    captured.Message = Formatting.Truncate(ex.Message, 200);
                }
                finally
                {
                    if (entered) semaphore.Release();
                    completed.Writer.TryWrite(captured);
                }
            }, CancellationToken.None));
        }

        // 逐个「按完成顺序」回调 job_done。用户点停止后，每个任务（含被取消的）
        // 仍然回调一次：界面行必须有终态，总进度条也要能走完，否则会残留「处理中」的行。
        for (int i = 0; i < request.Jobs.Count; i++)
        {
            Job finished;
            try
            {
                finished = await completed.Reader.ReadAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                break;
            }
            _events.JobDone(finished);
        }

        try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { /* 已在处理中记录 */ }
    }

    private async Task RunJobAsync(Job job, BatchRequest request, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            job.State = Job.StateCancelled;
            return;
        }

        Options options = request.Options;
        job.State = Job.StateRunning;
        job.StartedAt = Formatting.UnixNow();
        job.FinishedAt = null;          // 重置上一轮的完成时间，避免停止后误报「已完成」
        job.VerifyOk = null;
        job.VerifyIssues = new List<string>();
        job.Progress = 0.0;
        _events.JobUpdate(job);

        List<SubTrack> enabled = job.Subs.Where(t => t.Enabled).ToList();
        string? cwd = null;
        if (enabled.Count == 0)
        {
            job.State = Job.StateSkipped;
            job.Message = "没有可用字幕轨，已跳过";
            _events.Log($"[跳过] {job.Name}：{job.Message}");
            return;
        }

        List<SubTrack> prepared;
        if (options.Mode == "hard")
        {
            string hardDir = Path.Combine(request.TempDir, "hard");
            PrepareResult pr = EncodingService.PrepareHardSubs(enabled, hardDir, options.ConvertEncoding);
            cwd = hardDir;
            foreach (HardSubFailure failure in pr.Failed)
                _events.Log($"[跳过] {job.Name}：字幕 {failure.Name} 预处理失败（{failure.Reason}）");
            if (pr.Prepared.Count == 0)
            {
                job.State = Job.StateFailed;
                job.Message = "字幕文件无法读取";
                return;
            }
            prepared = pr.Prepared;
        }
        else
        {
            prepared = new List<SubTrack>();
            foreach (SubTrack track in enabled)
            {
                // 结构修复（补 [Script Info] 段头）与「转编码」是两件事：
                // 缺少段头的 ass/ssa 即使不转码也必然让 ffmpeg 报 Invalid data，
                // 因此这里无条件先修一遍；只有确实需要改动时才使用副本。
                if (track.Kind == "text")
                {
                    bool needsRepair = EncodingService.NeedsScriptInfoRepair(track.Path);
                    if (needsRepair || options.ConvertEncoding)
                    {
                        ConvertResult converted = EncodingService.EnsureUtf8(track.Path, request.TempDir);
                        if (!string.IsNullOrEmpty(converted.Encoding))
                            track.Encoding = converted.Encoding;
                        if (converted.Created)
                        {
                            if (needsRepair)
                                _events.Log($"[修复] {job.Name}：字幕 {track.Name} 缺少 [Script Info] 段头，已自动补全");
                            SubTrack clone = track.Clone();
                            clone.Path = converted.Path;
                            prepared.Add(clone);
                            continue;
                        }
                    }
                }
                prepared.Add(track);
            }
        }

        if (prepared.Count == 0)
        {
            job.State = Job.StateSkipped;
            job.Message = "没有可用字幕轨，已跳过";
            _events.Log($"[跳过] {job.Name}：{job.Message}");
            return;
        }

        var tempJob = new Job(job.Video, prepared, job.Output, job.Duration, job.Info) { Uid = job.Uid };
        BuildCommandResult built = CommandBuilder.BuildCommand(tempJob, options, request.Ffmpeg);
        foreach (string note in built.Notes)
            _events.Log($"[提示] {job.Name}：{note}");
        _events.Log($"[命令] {Formatting.CommandText(built.Command)}");

        void OnProgress(double percent, double speed, double? eta)
        {
            job.Progress = percent;
            job.Speed = speed > 0 ? $"{Formatting.F2(speed)}x" : "";
            job.Eta = eta.HasValue ? Formatting.FormatDuration(eta) : "--:--";
            _events.Progress(job);
        }

        var logLines = new List<string>();
        (int code, string error) = await FfmpegRunner.RunFfmpegAsync(
            built.Command, job.Duration, OnProgress, cancellationToken, logLines, cwd: cwd)
            .ConfigureAwait(false);

        job.FinishedAt = Formatting.UnixNow();

        if (cancellationToken.IsCancellationRequested)
        {
            job.State = Job.StateCancelled;
            job.Message = "已取消";
        }
        else if (code == 0 && File.Exists(job.Output))
        {
            job.State = Job.StateDone;
            job.Progress = 100.0;
            job.Message = "";
            try { job.Size = new FileInfo(job.Output).Length; }
            catch { job.Size = null; }

            if (options.Verify)
            {
                VerifyResult verified = await OutputVerifier.VerifyOutputAsync(
                    request.Ffprobe, job.Output, built.Plan, job.Info, ffmpeg: request.Ffmpeg)
                    .ConfigureAwait(false);
                job.VerifyOk = verified.Ok;
                job.VerifyIssues = verified.Issues;
                if (verified.Ok)
                    _events.Log($"[校验] {job.Name}：输出正常（{Formatting.PlanText(built.Plan)}）");
                else
                    foreach (string issue in verified.Issues)
                        _events.Log($"[校验] {job.Name}：{issue}");
            }

            // 替换源文件：只有「合并成功 + 校验通过」才删除原件。
            if (options.ReplaceSource)
                ApplySourceReplacement(job, options, request, built);
        }
        else
        {
            job.State = Job.StateFailed;
            string reason = string.IsNullOrEmpty(error)
                ? $"ffmpeg 返回码 {code.ToString(CultureInfo.InvariantCulture)}" : error;
            job.Message = Formatting.Truncate(reason, 200);
            // 失败时把 ffmpeg 的完整错误（最多 maxLog 行）写进日志：
            // 旧实现只取最后 6 行，遇到多行报错时会丢掉真正的根因。
            _events.Log($"[失败] {job.Name}：ffmpeg 返回码 {code.ToString(CultureInfo.InvariantCulture)}");
            foreach (string line in logLines)
                _events.Log($"[错误] {job.Name}：{line}");
            if (logLines.Count == 0)
                _events.Log($"[错误] {job.Name}：ffmpeg 没有输出错误信息");
        }
    }

    /// <summary>
    /// 「合并成功后替换源文件」：删除原视频与已合并的字幕，把结果改名为原视频文件名（不加 _sub）。
    ///
    /// 这是破坏性操作，按下面的顺序做，任何一步不满足就放弃删除、保留全部原件：
    /// 1. 输出目录必须就是源目录（否则不是替换场景）；
    /// 2. 合并结果必须存在且非空；
    /// 3. 校验必须通过（开启校验时 Ok 必须为 true；未开启校验时要求体积达标）；
    /// 4. 先把结果改名到最终路径，成功后再删原件；删源失败则回滚改名。
    /// </summary>
    private void ApplySourceReplacement(Job job, Options options, BatchRequest request, BuildCommandResult built)
    {
        string? finalPath = CommandBuilder.BuildFinalReplacementPath(job.Video, options, options.OutputDir);
        if (finalPath is null)
        {
            _events.Log($"[保留] {job.Name}：输出目录与源目录不同，不执行替换删除");
            return;
        }

        long size;
        try { size = new FileInfo(job.Output).Length; }
        catch { size = 0; }
        if (size <= 0)
        {
            _events.Log($"[保留] {job.Name}：合并结果为空，未删除原件");
            return;
        }

        // 校验门禁：开启校验时必须明确通过；未开启时用「体积不小于源文件 50%」兜底
        if (options.Verify)
        {
            if (job.VerifyOk != true)
            {
                _events.Log($"[保留] {job.Name}：输出校验未通过，未删除原件");
                return;
            }
        }
        else if (job.Info.Size > 0 && size < job.Info.Size * 0.5)
        {
            _events.Log($"[保留] {job.Name}：输出体积明显偏小，未删除原件");
            return;
        }

        // 结果与源文件同名时无需改名（BuildOutputPath 已保证不会写进源文件路径）。
        // 注意顺序：必须先删除原视频，再把合并结果改名到原路径；
        // 反过来（先改名覆盖、再删源）会把刚生成的结果当成源文件删掉。
        bool samePath = string.Equals(Path.GetFullPath(finalPath), Path.GetFullPath(job.Output),
                                      StringComparison.OrdinalIgnoreCase);

        // 收集要删除的字幕：只删「本次确实合并进去」的原始字幕文件（不是临时副本）
        var subToDelete = new List<string>();
        foreach (SubTrack track in job.Subs.Where(t => t.Enabled))
        {
            string path = track.Path;
            if (path.EndsWith("_utf8" + Path.GetExtension(path), StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(path)) continue;
            // 只删与视频同目录的字幕，避免误删其它位置的文件
            string? dir = Path.GetDirectoryName(path);
            string? videoDir = Path.GetDirectoryName(job.Video);
            if (dir is null || videoDir is null) continue;
            if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)),
                               Path.TrimEndingDirectorySeparator(Path.GetFullPath(videoDir)),
                               StringComparison.OrdinalIgnoreCase)) continue;
            subToDelete.Add(path);
        }

        var failures = new List<string>();

        // 1) 先删原视频（若与最终路径不同名，它此刻仍独立存在）
        if (!samePath)
        {
            try { File.Delete(job.Video); }
            catch (Exception ex) { failures.Add($"视频：{ex.Message}"); }
        }

        // 2) 删源失败就整体放弃，保留 _sub 结果，不动原件
        if (failures.Count > 0)
        {
            _events.Log($"[保留] {job.Name}：删除原视频失败（{string.Join("；", failures)}），已保留原件与 _sub 输出");
            return;
        }

        // 3) 把合并结果改名到最终路径
        if (!samePath)
        {
            try
            {
                File.Move(job.Output, finalPath, overwrite: true);
            }
            catch (Exception ex)
            {
                _events.Log($"[保留] {job.Name}：改名失败（{ex.Message}），结果仍为 {Path.GetFileName(job.Output)}");
                return;
            }
        }

        // 4) 删除已合并的原始字幕
        foreach (string path in subToDelete)
        {
            try { File.Delete(path); }
            catch (Exception ex) { failures.Add($"{Path.GetFileName(path)}：{ex.Message}"); }
        }

        if (failures.Count > 0)
            _events.Log($"[提示] {job.Name}：以下字幕未能删除（{string.Join("；", failures)}），需要时请手动清理");

        job.Output = finalPath;
        if (subToDelete.Count > 0)
            _events.Log($"[替换] {job.Name}：已删除原视频与 {subToDelete.Count} 个字幕，保留 {Path.GetFileName(finalPath)}");
        else
            _events.Log($"[替换] {job.Name}：已删除原视频，保留 {Path.GetFileName(finalPath)}");
    }
}
