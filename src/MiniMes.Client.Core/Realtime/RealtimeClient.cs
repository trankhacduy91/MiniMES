using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using MiniMes.Client.Core.Auth;
using MiniMes.Contracts.Andon;
using MiniMes.Contracts.Realtime;

namespace MiniMes.Client.Core.Realtime;

// Spec M4 2.6: the SignalR connection of one screen (dashboard, kiosk Andon panel). Transient: the screen owns it
// and disposes it when it closes. Events fire on a background thread: subscribers post to the UI (IUiDispatcher).
public interface IRealtimeClient : IAsyncDisposable
{
    event Action<MachineStateChangedDto>? MachineStateChanged;
    event Action<MachineCounterDto>? MachineCounterUpdated;
    event Action<MachineProcessDto>? MachineProcessUpdated;
    event Action<AndonDto>? AndonChanged;
    event Action<AndonDto>? AndonEscalated;

    // false = "Reconnecting..." banner; true = live again.
    event Action<bool>? ConnectionChanged;

    // After every (re)connection, once the line groups are joined again: reload the REST snapshot, because SignalR
    // does not replay what was pushed while the client was away.
    event Func<Task>? Resync;

    bool IsConnected { get; }

    // Never throws. Not reachable: reports ConnectionChanged(false) and keeps trying in the background.
    Task StartAsync();

    Task JoinLineAsync(string lineCode);

    Task LeaveLineAsync(string lineCode);
}

public sealed class RealtimeClient : IRealtimeClient
{
    // The default automatic reconnect gives up after 0, 2, 10, 30 s; a dashboard on a wall must never give up.
    private sealed class ForeverRetry : IRetryPolicy
    {
        private static readonly TimeSpan[] Delays = [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

        public TimeSpan? NextRetryDelay(RetryContext context)
            => context.PreviousRetryCount < Delays.Length ? Delays[context.PreviousRetryCount] : TimeSpan.FromSeconds(10);
    }

    public static readonly TimeSpan FirstConnectRetry = TimeSpan.FromSeconds(5);

    private readonly HubConnection _conn;
    private readonly HashSet<string> _lines = [];
    private readonly CancellationTokenSource _stop = new();
    private bool _started;

    public event Action<MachineStateChangedDto>? MachineStateChanged;
    public event Action<MachineCounterDto>? MachineCounterUpdated;
    public event Action<MachineProcessDto>? MachineProcessUpdated;
    public event Action<AndonDto>? AndonChanged;
    public event Action<AndonDto>? AndonEscalated;
    public event Action<bool>? ConnectionChanged;
    public event Func<Task>? Resync;

    public RealtimeClient(ITokenStore tokens, IOptions<ClientOptions> options)
    {
        _conn = new HubConnectionBuilder()
            // The .NET client sends the token as an Authorization header, also on the WebSocket handshake.
            .WithUrl(options.Value.ApiBaseUrl.TrimEnd('/') + RealtimeHub.Path,
                o => o.AccessTokenProvider = () => Task.FromResult(tokens.AccessToken))
            .WithAutomaticReconnect(new ForeverRetry())
            .Build();

        // Handlers before StartAsync, or the first pushes are lost. Names from the typed hub interface: a typo is
        // a compile error, not a silent "nothing ever arrives".
        _conn.On<MachineStateChangedDto>(nameof(IMachineClient.MachineStateChanged), d => MachineStateChanged?.Invoke(d));
        _conn.On<MachineCounterDto>(nameof(IMachineClient.MachineCounterUpdated), d => MachineCounterUpdated?.Invoke(d));
        _conn.On<MachineProcessDto>(nameof(IMachineClient.MachineProcessUpdated), d => MachineProcessUpdated?.Invoke(d));
        _conn.On<AndonDto>(nameof(IMachineClient.AndonChanged), d => AndonChanged?.Invoke(d));
        _conn.On<AndonDto>(nameof(IMachineClient.AndonEscalated), d => AndonEscalated?.Invoke(d));

        _conn.Reconnecting += _ => { ConnectionChanged?.Invoke(false); return Task.CompletedTask; };
        // New ConnectionId: the server forgot our groups (role groups it re-adds itself, line groups we must join).
        _conn.Reconnected += _ => OnConnectedAsync();
        // Only when stopped or disposed: the retry policy never gives up.
        _conn.Closed += _ => { ConnectionChanged?.Invoke(false); return Task.CompletedTask; };
    }

    public bool IsConnected => _conn.State == HubConnectionState.Connected;

    public async Task StartAsync()
    {
        if (_started) return;
        _started = true;
        // WithAutomaticReconnect only covers a connection that was up once; the first one is retried here.
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await _conn.StartAsync(_stop.Token);
                await OnConnectedAsync();
                return;
            }
            catch (Exception) when (!_stop.IsCancellationRequested)
            {
                ConnectionChanged?.Invoke(false);
                try { await Task.Delay(FirstConnectRetry, _stop.Token); }
                catch (OperationCanceledException) { return; }
            }
            catch (OperationCanceledException) { return; }
        }
    }

    public async Task JoinLineAsync(string lineCode)
    {
        lock (_lines) _lines.Add(lineCode);
        if (IsConnected) await TryInvokeAsync(RealtimeHub.JoinLine, lineCode);
    }

    public async Task LeaveLineAsync(string lineCode)
    {
        lock (_lines) _lines.Remove(lineCode);
        if (IsConnected) await TryInvokeAsync(RealtimeHub.LeaveLine, lineCode);
    }

    private async Task OnConnectedAsync()
    {
        string[] lines;
        lock (_lines) lines = [.. _lines];
        foreach (var line in lines) await TryInvokeAsync(RealtimeHub.JoinLine, line);
        ConnectionChanged?.Invoke(true);
        if (Resync is { } resync) await resync();
    }

    // The connection can drop between the IsConnected check and the call; the reconnect joins again anyway.
    private async Task TryInvokeAsync(string method, string lineCode)
    {
        try { await _conn.InvokeAsync(method, lineCode, _stop.Token); }
        catch (Exception) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _conn.DisposeAsync();
        _stop.Dispose();
    }
}

// Screens get their connection from here instead of from DI: the container keeps every IAsyncDisposable Transient
// it creates from the root provider alive until the app exits, so each dashboard visit would leak one connection
// object. A screen creates it, owns it and disposes it in IClosable.CloseAsync.
public interface IRealtimeClientFactory
{
    IRealtimeClient Create();
}

public sealed class RealtimeClientFactory(ITokenStore tokens, IOptions<ClientOptions> options) : IRealtimeClientFactory
{
    public IRealtimeClient Create() => new RealtimeClient(tokens, options);
}
