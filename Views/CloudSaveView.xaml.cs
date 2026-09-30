using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SteamLuaManager.ViewModels;

namespace SteamLuaManager.Views;

public partial class CloudSaveView : UserControl
{
    public CloudSaveView()
    {
        InitializeComponent();
    }

    // 名单内滚动到顶/底后把滚轮让给外层页面，避免鼠标进来就滚不出去
    private void GamesScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scroller) return;
        if ((e.Delta > 0 && scroller.VerticalOffset == 0) ||
            (e.Delta < 0 && scroller.VerticalOffset >= scroller.ScrollableHeight))
        {
            e.Handled = true;
            var newArgs = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent
            };
            FindVisualParent<ScrollViewer>(scroller)?.RaiseEvent(newArgs);
        }
    }

    private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        var parent = VisualTreeHelper.GetParent(child);
        while (parent != null && parent is not T)
            parent = VisualTreeHelper.GetParent(parent);
        return parent as T;
    }

    private void R2SecretBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is CloudSaveViewModel vm && sender is PasswordBox box)
            vm.R2SecretKey = box.Password;
    }

    private void S3SecretBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is CloudSaveViewModel vm && sender is PasswordBox box)
            vm.S3SecretKey = box.Password;
    }
}
