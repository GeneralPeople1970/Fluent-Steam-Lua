using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Diagnostics;
using iNKORE.UI.WPF.Modern.Controls;
using SteamLuaManager.Services;
using SteamLuaManager.ViewModels;

namespace SteamLuaManager.Views;

public partial class ScriptDownloadView : UserControl
{
    public ScriptDownloadView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            try
            {
                var settings = App.ServiceProvider?.GetService(typeof(ISettingsService)) is ISettingsService s
                    ? s.Load() : null;
                var showInSetting = settings is { ShowCopyLogButton: true };
                if (showInSetting && DataContext is ScriptDownloadViewModel vm)
                {
                    vm.LogLines.CollectionChanged += (_, _) =>
                        CopyLogButton.Visibility = vm.LogLines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                    CopyLogButton.Visibility = vm.LogLines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                }
                else
                    CopyLogButton.Visibility = Visibility.Collapsed;
            }
            catch { CopyLogButton.Visibility = Visibility.Collapsed; }
        };
    }

    private void CopyLogButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ScriptDownloadViewModel vm && vm.LogLines.Count > 0)
        {
            try
            {
                var text = string.Join(Environment.NewLine, vm.LogLines);
                Clipboard.SetText(text);
            }
            catch { }
        }
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs e)
    {
        if (DataContext is ScriptDownloadViewModel vm && !string.IsNullOrEmpty(e.QueryText))
        {
            vm.GameId = e.QueryText;
            vm.SearchCommand.Execute(null);
        }
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs e)
    {
        if (DataContext is ScriptDownloadViewModel vm && e.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            vm.GameId = sender.Text ?? string.Empty;
        }
    }

    private void LogScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var innerScroller = (ScrollViewer)sender;
        if ((e.Delta > 0 && innerScroller.VerticalOffset == 0) ||
            (e.Delta < 0 && innerScroller.VerticalOffset >= innerScroller.ScrollableHeight))
        {
            var parent = FindVisualParent<ScrollViewer>((DependencyObject)sender);
            if (parent != null)
            {
                e.Handled = true;
                var newArgs = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
                {
                    RoutedEvent = UIElement.MouseWheelEvent
                };
                parent.RaiseEvent(newArgs);
            }
        }
    }

    // 搜索结果封面失败切换：首选 404 时按候选序换下一张，耗尽即停，不循环；
    // URL 比对先解码（heybox 链接含 %3E 这类转义，Uri.ToString 会解码，直接比对永远对不上）
    private void SearchCover_ImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (sender is not Image img) return;
        if (img.DataContext is not ScriptDownloadViewModel.FoundGame game) return;
        var candidates = game.CoverCandidates;
        if (candidates == null || candidates.Count == 0) return;
        var current = NormUrl((img.Source as BitmapImage)?.UriSource?.ToString() ?? game.CoverUrl);
        var idx = candidates.FindIndex(u => string.Equals(NormUrl(u), current, StringComparison.OrdinalIgnoreCase));
        for (var i = idx + 1; i < candidates.Count; i++)
        {
            if (string.Equals(NormUrl(candidates[i]), current, StringComparison.OrdinalIgnoreCase)) continue;
            Uri? uri;
            try { uri = new Uri(candidates[i]); }
            catch { continue; }
            img.Source = new BitmapImage(uri);
            return;
        }
    }

    private static string NormUrl(string url)
    {
        try { return Uri.UnescapeDataString(url); }
        catch { return url; }
    }

    private void GameInfoButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ScriptDownloadViewModel.FoundGame game })
        {
            Process.Start(new ProcessStartInfo($"https://store.steampowered.com/app/{game.AppId}/")
            {
                UseShellExecute = true
            });
        }
    }

    private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        var parent = VisualTreeHelper.GetParent(child);
        while (parent != null && parent is not T)
            parent = VisualTreeHelper.GetParent(parent);
        return parent as T;
    }
}
