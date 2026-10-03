using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SteamLuaManager.Services;
using SteamLuaManager.ViewModels;

namespace SteamLuaManager.Views;

public partial class ExtractionView : UserControl
{
    private ExtractionViewModel? _subscribedVm;

    public ExtractionView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            try
            {
                var settings = App.ServiceProvider?.GetService(typeof(ISettingsService)) is ISettingsService s
                    ? s.Load() : null;
                var showInSetting = settings is { ShowCopyLogButton: true };
                var vm = DataContext as ExtractionViewModel;
                // 切页每次都会进 Loaded；VM 是单例，先摘旧订阅否则 handler 越积越多
                if (!ReferenceEquals(_subscribedVm, vm))
                {
                    if (_subscribedVm != null)
                        _subscribedVm.LogLines.CollectionChanged -= OnLogLinesChanged;
                    _subscribedVm = vm;
                    if (_subscribedVm != null)
                        _subscribedVm.LogLines.CollectionChanged += OnLogLinesChanged;
                }
                if (showInSetting && vm != null)
                    CopyLogButton.Visibility = vm.LogLines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                else
                    CopyLogButton.Visibility = Visibility.Collapsed;
            }
            catch { CopyLogButton.Visibility = Visibility.Collapsed; }
        };
    }

    private void OnLogLinesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_subscribedVm == null) return;
        // 日志进来才冒出来是存量 bug：开关关闭时必须保持隐藏，不能只看条数
        var show = App.ServiceProvider?.GetService(typeof(ISettingsService)) is ISettingsService s
            && s.Load().ShowCopyLogButton;
        CopyLogButton.Visibility = show && _subscribedVm.LogLines.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CopyLogButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ExtractionViewModel vm && vm.LogLines.Count > 0)
        {
            try
            {
                var text = string.Join(Environment.NewLine, vm.LogLines);
                Clipboard.SetText(text);
            }
            catch { }
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

    private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        var parent = VisualTreeHelper.GetParent(child);
        while (parent != null && parent is not T)
            parent = VisualTreeHelper.GetParent(parent);
        return parent as T;
    }
}
