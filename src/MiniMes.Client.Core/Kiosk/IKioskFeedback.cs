namespace MiniMes.Client.Core.Kiosk;

// What the kiosk shows after a scan (spec M3 2.8). The ViewModel only sets the state; the view picks the
// colours (DataTrigger) and the app plays the sound, so Client.Core needs no WPF types.
public enum ScanState
{
    Idle,
    Ok,
    Ng,
    Blocked,
    Warning,   // M6: yellow, allowed after a supervisor confirms (FIFO)
}

// Sound of a result: operators wear ear protection and look at the board, so sound is the second channel.
public interface IKioskFeedback
{
    void Play(ScanState state);
}
