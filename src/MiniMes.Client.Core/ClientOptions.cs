namespace MiniMes.Client.Core;

// Bound from the root of each WPF app's appsettings.json.
public sealed class ClientOptions
{
    public string ApiBaseUrl { get; init; } = string.Empty;

    // Kiosk only: the station this PC stands at. Operators never choose it.
    public string? StationCode { get; init; }

    // Short on purpose: on the shop floor "Cannot reach server" after 10 s beats a frozen screen.
    public int TimeoutSeconds { get; init; } = 10;
}
