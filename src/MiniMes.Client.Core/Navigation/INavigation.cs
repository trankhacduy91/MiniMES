namespace MiniMes.Client.Core.Navigation;

// Implemented by each WPF app (it owns the windows). ViewModels only see this interface,
// so they never reference Window/MessageBox and stay unit-testable.
public interface INavigation
{
    void ShowLogin();
    void ShowHome();
}
