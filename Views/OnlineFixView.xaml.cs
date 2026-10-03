using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using iNKORE.UI.WPF.Modern.Controls;
using SteamLuaManager.Services;
using SteamLuaManager.ViewModels;

namespace SteamLuaManager.Views;

public partial class OnlineFixView : UserControl
{
    private OnlineFixViewModel? _subscribedVm;

    public OnlineFixView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Loaded += (_, _) =>
        {
            // VM 常驻单例，先摘旧订阅；复制按钮跟随设置开关（与提取/入库页一致）
            var vm = DataContext as OnlineFixViewModel;
            if (!ReferenceEquals(_subscribedVm, vm))
            {
                if (_subscribedVm != null)
                    _subscribedVm.LogLines.CollectionChanged -= OnLogLinesChanged;
                _subscribedVm = vm;
                if (_subscribedVm != null)
                    _subscribedVm.LogLines.CollectionChanged += OnLogLinesChanged;
            }
            RefreshCopyLogButtonVisibility();
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 与修改器页一致：内容块变为可见时播一次短淡入；切页每次都会进 Loaded，先摘后挂
        Content480Panel.IsVisibleChanged -= OnContentPanelIsVisibleChanged;
        ContentPatchPanel.IsVisibleChanged -= OnContentPanelIsVisibleChanged;
        Content480Panel.IsVisibleChanged += OnContentPanelIsVisibleChanged;
        ContentPatchPanel.IsVisibleChanged += OnContentPanelIsVisibleChanged;
    }

    private static void OnContentPanelIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue && sender is FrameworkElement element)
        {
            element.Opacity = 0;
            var animation = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromSeconds(0.2),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            element.BeginAnimation(FrameworkElement.OpacityProperty, animation);
        }
    }

    private void OnLogLinesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => RefreshCopyLogButtonVisibility();

    private void RefreshCopyLogButtonVisibility()
    {
        try
        {
            var show = App.ServiceProvider?.GetService(typeof(ISettingsService)) is ISettingsService s
                && s.Load().ShowCopyLogButton;
            CopyLogButton.Visibility = show && _subscribedVm != null && _subscribedVm.LogLines.Count > 0
                ? Visibility.Visible : Visibility.Collapsed;
        }
        catch { CopyLogButton.Visibility = Visibility.Collapsed; }
    }

    private void CopyLogButton_Click(object sender, RoutedEventArgs e)
    {
        if (_subscribedVm != null && _subscribedVm.LogLines.Count > 0)
        {
            try
            {
                var text = string.Join(Environment.NewLine, _subscribedVm.LogLines);
                Clipboard.SetText(text);
            }
            catch { }
        }
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (DataContext is OnlineFixViewModel vm && vm.SearchCommand.CanExecute(null))
            vm.SearchCommand.Execute(null);
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (DataContext is OnlineFixViewModel vm)
            vm.SearchText = sender.Text ?? string.Empty;
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
