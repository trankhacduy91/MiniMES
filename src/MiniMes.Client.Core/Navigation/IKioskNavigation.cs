namespace MiniMes.Client.Core.Navigation;

// Kiosk screens: badge login -> ShowHome (choose the WO at this station) -> ShowScan.
public interface IKioskNavigation : INavigation
{
    void ShowScan();

    // M6 step 4: the station's reels (load, replace, take off); back to ShowScan.
    void ShowMaterial();
}
