using System.Windows;
using System.Windows.Controls;
using MiniMes.Client.Core.ViewModels;

namespace MiniMes.BackOffice.Wpf.Views;

public partial class LoginView : UserControl
{
    public LoginView() => InitializeComponent();

    // The one piece of code-behind: PasswordBox cannot be data-bound, so push the value to the ViewModel.
    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is UserLoginViewModel vm)
            vm.Password = PasswordBox.Password;
    }
}
