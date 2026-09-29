using System.Windows;
using System.Windows.Controls;
using SteamLuaManager.ViewModels;

namespace SteamLuaManager.Views;

public partial class CloudSaveView : UserControl
{
    public CloudSaveView()
    {
        InitializeComponent();
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
