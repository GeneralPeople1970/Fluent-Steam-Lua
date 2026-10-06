using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SteamLuaManager.ViewModels;

namespace SteamLuaManager.Views;

public partial class CrackToolView : UserControl
{
    private CrackToolViewModel? _logVm;

    public CrackToolView()
    {
        InitializeComponent();
        // 切页每次都会进 Loaded，先摘后挂；新日志进来沉底，保证最新行可见
        Loaded += (_, _) =>
        {
            var vm = DataContext as CrackToolViewModel;
            if (ReferenceEquals(_logVm, vm)) return;
            if (_logVm != null)
                _logVm.LogLines.CollectionChanged -= OnLogAdded;
            _logVm = vm;
            if (_logVm != null)
                _logVm.LogLines.CollectionChanged += OnLogAdded;
        };
        // Unloaded 必摘：VM 若被页面缓存复用，旧视图会借 CollectionChanged 一直活着
        Unloaded += (_, _) =>
        {
            if (_logVm != null)
                _logVm.LogLines.CollectionChanged -= OnLogAdded;
            _logVm = null;
        };
    }

    private void OnLogAdded(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.InvokeAsync(() => LogScrollViewer.ScrollToBottom(), DispatcherPriority.Background);
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
