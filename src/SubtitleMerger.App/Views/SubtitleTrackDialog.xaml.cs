using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SubtitleMerger.App.Services;
using SubtitleMerger.Core;
using SubtitleMerger.Core.Models;
using SubtitleMerger.Core.Services;

namespace SubtitleMerger.App.Views;

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

        LangCombo.ItemsSource = Constants.LangChoices
            .Select(c => $"{c.Code}  {c.Name}").ToList();

        foreach (SubTrack track in current) _tracks.Add(track.Clone());
        RefreshSelected();
    }

    private void AddPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (_tracks.Any(t => string.Equals(t.Path, path, StringComparison.Ordinal))) return;
        string kind = Constants.BitmapSubExts.Contains(Path.GetExtension(path).ToLowerInvariant())
            ? "bitmap" : "text";
        _tracks.Add(new SubTrack(path,
            lang: NameNormalizer.DetectLangFromName(path),
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
        foreach (object? value in LangCombo.Items)
        {
            string text = value as string ?? "";
            if (text.StartsWith(track.Lang + " ", StringComparison.Ordinal)) { label = text; break; }
        }
        LangCombo.SelectedItem = label;
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

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        int idx = SelectedList.SelectedIndex;
        if (idx < 0 || idx >= _tracks.Count) return;
        _tracks.RemoveAt(idx);
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
        => Result = _tracks;

    #endregion
}
