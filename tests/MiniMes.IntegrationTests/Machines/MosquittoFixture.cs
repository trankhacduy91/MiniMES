using System.Net;
using System.Net.Sockets;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.DependencyInjection;
using MiniMes.Infrastructure.Machines;
using MiniMes.IntegrationTests.Infrastructure;

namespace MiniMes.IntegrationTests.Machines;

// Spec M4 step 9: a real Mosquitto in a container, with the project's own mosquitto.conf (Mosquitto 2 listens on
// localhost only without it). Fixed host port, chosen free at start: the broker test stops and starts the container,
// and the ingest must find it again at the same address (a random port binding would change on restart).
public sealed class MosquittoFixture : IAsyncLifetime
{
    private readonly IContainer _broker;

    public MosquittoFixture()
    {
        Port = FreePort();
        _broker = new ContainerBuilder("eclipse-mosquitto:2")   // same image as docker-compose
            .WithPortBinding(Port, 1883)
            .WithResourceMapping(new FileInfo(ConfigFile()), "/mosquitto/config/")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("mosquitto version .* running"))
            .Build();
    }

    public int Port { get; }

    public Task InitializeAsync() => _broker.StartAsync();

    public Task DisposeAsync() => _broker.DisposeAsync().AsTask();

    // "docker compose stop mosquitto" of the manual check, inside a test.
    public Task StopAsync() => _broker.StopAsync();

    public Task StartAsync() => _broker.StartAsync();

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    // docker/mosquitto.conf of the repository, found from the test's bin folder.
    private static string ConfigFile()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var file = Path.Combine(dir.FullName, "docker", "mosquitto.conf");
            if (File.Exists(file)) return file;
        }
        throw new FileNotFoundException("docker/mosquitto.conf not found above " + AppContext.BaseDirectory);
    }
}

// SQL Server + Mosquitto + the API with the MQTT ingest ON (the "Api" collection runs it OFF).
public sealed class MqttApiFixture : IAsyncLifetime
{
    private readonly SqlServerFixture _sql = new();

    public MosquittoFixture Broker { get; } = new();
    public ApiFactory Factory { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_sql.InitializeAsync(), Broker.InitializeAsync());
        Factory = new ApiFactory(_sql, mqttPort: Broker.Port);
        var connection = Factory.Services.GetRequiredService<MqttConnectionState>();   // starts the host + ingest
        for (var i = 0; i < 100 && connection.ConnectedSince is null; i++) await Task.Delay(100);
        if (connection.ConnectedSince is null) throw new InvalidOperationException("The ingest did not connect to the test broker.");
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await Broker.DisposeAsync();
        await _sql.DisposeAsync();
    }
}

// The factories configure the API through process-wide environment variables (read when Program.cs builds the host):
// this collection must not build its host while the "Api" one builds its own. DisableParallelization runs it alone,
// after the parallel collections.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MqttCollection : ICollectionFixture<MqttApiFixture>
{
    public const string Name = "Mqtt";
}
