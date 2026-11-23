using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SubFusion.App.Services;
using SubFusion.Core;
using SubFusion.Core.Models;
using SubFusion.Core.Services;

namespace SubFusion.App.Views;

/// <summary>
/// 字幕轨编辑对话框（原 SubtitleDialog 的 WinUI 3 版本）。
/// 交互入口：候选双击/添加、浏览、移除、上移/下移、语言/轨道名/默认轨/强制显示、确定/取消。
/// </summary>
public sealed partial class SubtitleTrackDialog : ContentDialog
{
    private readonly Func<Task<IReadOnlyList<string>>> _browse;
    private readonly ObservableCollection<string> _candidatePaths = new();
    private readonly ObservableCollection<string> _trackLabels = new();
    private readonly List<SubTrack> _tracks = new();
    private bool _suppress;

    /// <summary>
    /// 候选列表 ↔ 已选列表 的同步守卫。
    ///
    /// 候选列表里的「选中」本身就是「这份字幕要合并」——提示语写的就是勾选。
    /// 之前只有按「添加 →」或双击才写入 _tracks，用户按提示点选后直接点确定，
    /// 交回去的是空列表：行仍显示（无字幕），状态栏却照样报「已更新」，看不出问题。
    /// 现在选中即写入、取消选中即移除；这个标志防止回写时递归触发。
    /// </summary>
    private bool _candidateSync;

    /// <summary>确定后返回编辑结果；取消时为 null。</summary>
    public List<SubTrack>? Result { get; private set; }

    public SubtitleTrackDialog(string video,
                               IReadOnlyList<string> candidates,
                               IReadOnlyList<string> allSubs,
                               IReadOnlyList<SubTrack> current,
                               Func<Task<IReadOnlyList<string>>> browse)
    {
        InitializeComponent();
        _browse = browse;

        Title = "字幕轨设置 —— " + Path.GetFileName(video);
        VideoLabel.Text = "视频：" + video;

        IReadOnlyList<string> source = candidates.Count > 0 ? candidates : allSubs;
        foreach (string path in source) _candidatePaths.Add(path);
        CandidateList.ItemsSource = _candidatePaths;
        SelectedList.ItemsSource = _trackLabels;

        LangCombo.Items.Clear();
        foreach (string choice in Constants.LangChoices.Select(c => $"{c.Code}  {c.Name}"))
            LangCombo.Items.Add(choice);

        foreach (SubTrack track in current) _tracks.Add(track.Clone());
        RefreshSelected();
        SyncCandidateSelection();
    }

    private void AddPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (_tracks.Any(t => string.Equals(t.Path, path, StringComparison.Ordinal))) return;
        string kind = Constants.BitmapSubExts.Contains(Path.GetExtension(path).ToLowerInvariant())
            ? "bitmap" : "text";
        _tracks.Add(new SubTrack(path,
            lang: NameNormalizer.DetectTypedLang(path),
            kind: kind,
            @default: _tracks.Count == 0));
    }

    private void RefreshSelected(int? select = null)
    {
        _suppress = true;
        _trackLabels.Clear();
        foreach (SubTrack track in _tracks) _trackLabels.Add(track.Label());
        if (_tracks.Count > 0)
        {
            int idx = Math.Clamp(select ?? 0, 0, _tracks.Count - 1);
            SelectedList.SelectedIndex = idx;
        }
        else
        {
            SelectedList.SelectedIndex = -1;
        }
        _suppress = false;
        ShowProps();
    }

    private void ShowProps()
    {
        int idx = SelectedList.SelectedIndex;
        if (idx < 0 || idx >= _tracks.Count) return;
        SubTrack track = _tracks[idx];

        bool old = _suppress;
        _suppress = true;
        string label = $"{track.Lang}  {(Constants.LangNames.TryGetValue(track.Lang, out var n) ? n : track.Lang)}";
        int comboIndex = -1;
        for (int i = 0; i < LangCombo.Items.Count; i++)
        {
            string text = LangCombo.Items[i] as string ?? "";
            if (text.StartsWith(track.Lang + " ", StringComparison.Ordinal))
            {
                comboIndex = i;
                break;
            }
        }
        if (comboIndex < 0)
        {
            LangCombo.Items.Add(label);
            comboIndex = LangCombo.Items.Count - 1;
        }
        LangCombo.SelectedIndex = comboIndex;
        TitleBox.Text = track.Title;
        DefaultCheck.IsChecked = track.Default;
        ForcedCheck.IsChecked = track.Forced;
        _suppress = old;
    }

    private void ApplyProps()
    {
        if (_suppress) return;
        int idx = SelectedList.SelectedIndex;
        if (idx < 0 || idx >= _tracks.Count) return;
        SubTrack track = _tracks[idx];

        string value = LangCombo.SelectedItem as string ?? "";
        string[] parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        track.Lang = parts.Length > 0 ? parts[0] : "und";
        track.Title = TitleBox.Text.Trim();
        if (DefaultCheck.IsChecked == true)
            foreach (SubTrack other in _tracks) other.Default = false;
        track.Default = DefaultCheck.IsChecked == true;
        track.Forced = ForcedCheck.IsChecked == true;
        RefreshSelected(select: idx);
    }

    #region 事件

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        foreach (string path in CandidateList.SelectedItems.OfType<string>().ToList())
            AddPath(path);
        RefreshSelected();
    }

    private void CandidateList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => OnAdd(sender, e);

    /// <summary>
    /// 候选列表的选中状态直接映射到已选轨道：选中=加入，取消=移除。
    /// 这样「点选字幕 → 确定」就真的会合并，不需要再按一次「添加 →」。
    /// </summary>
    private void CandidateList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_candidateSync) return;
        _candidateSync = true;
        try
        {
            foreach (object item in e.RemovedItems)
                if (item is string removed)
                    _tracks.RemoveAll(t => string.Equals(t.Path, removed, StringComparison.Ordinal));
            foreach (object item in e.AddedItems)
                if (item is string added)
                    AddPath(added);
            // 勾选候选时不要打乱右侧当前选中的那一条，否则每点一次左侧，
            // 右下角的语言/轨道名编辑区就跳回第一条。
            RefreshSelected(select: SelectedList.SelectedIndex >= 0 ? SelectedList.SelectedIndex : null);
        }
        finally
        {
            _candidateSync = false;
        }
    }

    /// <summary>把 _tracks 的现状回填到候选列表的选中集合（不触发同步）。</summary>
    private void SyncCandidateSelection()
    {
        _candidateSync = true;
        try
        {
            CandidateList.SelectedItems.Clear();
            foreach (SubTrack track in _tracks)
                if (_candidatePaths.Contains(track.Path, StringComparer.Ordinal))
                    CandidateList.SelectedItems.Add(track.Path);
        }
        finally
        {
            _candidateSync = false;
        }
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        int idx = SelectedList.SelectedIndex;
        if (idx < 0 || idx >= _tracks.Count) return;
        string path = _tracks[idx].Path;
        _tracks.RemoveAt(idx);
        // 不同步取消候选列表的选中，下一次选择变更会按理把它又加回来。
        _candidateSync = true;
        try
        {
            foreach (object item in CandidateList.SelectedItems.Cast<object>().ToList())
                if (item is string s && string.Equals(s, path, StringComparison.Ordinal))
                    CandidateList.SelectedItems.Remove(item);
        }
        finally
        {
            _candidateSync = false;
        }
        RefreshSelected();
    }

    private void SelectedList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => OnRemove(sender, e);

    private void OnBrowse(object sender, RoutedEventArgs e)
        => CrashGuard.FireAndForget(BrowseAsync, "SubtitleTrackDialog.OnBrowse");

    private async Task BrowseAsync()
    {
        IReadOnlyList<string> paths = await _browse();
        foreach (string path in paths) AddPath(path);
        RefreshSelected();
        SyncCandidateSelection();
    }

    private void OnMoveUp(object sender, RoutedEventArgs e) => Move(-1);
    private void OnMoveDown(object sender, RoutedEventArgs e) => Move(1);

    private void Move(int delta)
    {
        int idx = SelectedList.SelectedIndex;
        if (idx < 0 || idx >= _tracks.Count) return;
        int next = idx + delta;
        if (next < 0 || next >= _tracks.Count) return;
        (_tracks[idx], _tracks[next]) = (_tracks[next], _tracks[idx]);
        RefreshSelected(select: next);
    }

    private void SelectedList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppress) ShowProps();
    }

    private void OnLangChanged(object sender, SelectionChangedEventArgs e) => ApplyProps();
    private void OnTitleChanged(object sender, TextChangedEventArgs e) => ApplyProps();
    private void OnCheckChanged(object sender, RoutedEventArgs e) => ApplyProps();

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // 兜底：提交瞬间再把候选列表里仍处于选中状态的字幕补进结果。
        // 正常路径由 CandidateList_SelectionChanged 已经写好，这里只防事件时序差异，
        // AddPath 对已存在项是幂等的，重复不会产生第二条轨道。
        foreach (string path in CandidateList.SelectedItems.OfType<string>().ToList()) AddPath(path);
        Result = _tracks;
    }

    /// <summary>自动化自检用：数据源条目数（不代表模板渲染成功）。</summary>
    internal int CandidateCountForTest => CandidateList.Items.Count;

    /// <summary>自动化自检用：已选字幕轨列表的条目数。</summary>
    internal int SelectedCountForTest => SelectedList.Items.Count;

    /// <summary>
    /// 自动化自检用：强制走一遍布局，并返回**已实现的容器**数量。
    ///
    /// 只读 Items.Count 是数据源计数，即使模板完全解析失败也照样是 2，
    /// 证明不了任何东西。必须真正 UpdateLayout 并去可视树里数 ListViewItem，
    /// 才能暴露「模板实例化时抛 XamlParseFailed」这类错误。
    /// </summary>
    internal int RealizeForTest()
    {
        CandidateList.UpdateLayout();
        SelectedList.UpdateLayout();
        UpdateLayout();
        return CountContainers(CandidateList) + CountContainers(SelectedList);
    }

    private static int CountContainers(DependencyObject root)
    {
        int count = 0;
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            DependencyObject node = stack.Pop();
            int n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < n; i++)
            {
                DependencyObject child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, i);
                if (child is ListViewItem) count++;
                stack.Push(child);
            }
        }
        return count;
    }

    #endregion
}
