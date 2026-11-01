namespace MiniMes.Client.Core;

// Bound from the root of each WPF app's appsettings.json.
public sealed class ClientOptions
{
    public string ApiBaseUrl { get; init; } = string.Empty;

    // Kiosk only: the station this PC stands at. Operators never choose it.
    public string? StationCode { get; init; }

    // Short on purpose: on the shop floor "Cannot reach server" after 10 s beats a frozen screen.
    public int TimeoutSeconds { get; init; } = 10;

    // [v2] M5 step 8b, Kiosk only: started with --mode=linetv --line=L1[,L2] the kiosk is a big screen at the line.
    // The command line is part of the configuration, so these bind like the JSON values.
    public string? Mode { get; init; }
    public string? Line { get; init; }
    public string LineTvBadge { get; init; } = "LINE-TV";
    public int LineTvRotateSeconds { get; init; } = 20;   // 0 = stay on the first line

    public const string LineTvMode = "linetv";
    public bool IsLineTv => string.Equals(Mode, LineTvMode, StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<string> LineTvLines => (Line ?? string.Empty)
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
