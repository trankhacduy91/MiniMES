using System.Media;
using MiniMes.Client.Core.Kiosk;

namespace MiniMes.Kiosk.Wpf;

// Windows system sounds: enough for the MVP (spec M3 2.8). A different sound for OK and for any problem,
// so the result is heard without looking.
public sealed class SystemSoundFeedback : IKioskFeedback
{
    public void Play(ScanState state)
    {
        switch (state)
        {
            case ScanState.Ok: SystemSounds.Asterisk.Play(); break;
            case ScanState.Ng or ScanState.Blocked: SystemSounds.Hand.Play(); break;
        }
    }
}
