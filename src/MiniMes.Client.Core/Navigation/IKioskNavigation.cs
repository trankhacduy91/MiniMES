namespace MiniMes.Client.Core.Navigation;

// Kiosk screens: badge login -> ShowHome (choose the WO at this station) -> ShowScan.
public interface IKioskNavigation : INavigation
{
    void ShowScan();
}
