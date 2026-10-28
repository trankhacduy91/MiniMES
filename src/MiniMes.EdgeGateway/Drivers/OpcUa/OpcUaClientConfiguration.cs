using MiniMes.EdgeGateway.Config;
using Opc.Ua;
using Opc.Ua.Configuration;

namespace MiniMes.EdgeGateway.Drivers.OpcUa;

// One client identity (application URI + certificate) for the whole gateway, shared by every OPC UA driver.
// Built once, on first use: the first start creates the self-signed certificate in pki/own.
public sealed class OpcUaClientConfiguration(GatewayOptions options, bool autoAccept, ITelemetryContext telemetry)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ApplicationConfiguration? _config;

    public ITelemetryContext Telemetry => telemetry;
    public int KeepAliveMs => options.OpcUa.KeepAliveMs;

    public async Task<ApplicationConfiguration> GetAsync(CancellationToken ct)
    {
        if (_config is not null) return _config;
        await _gate.WaitAsync(ct);
        try
        {
            return _config ??= await CreateAsync(ct);
        }
        finally { _gate.Release(); }
    }

    private async Task<ApplicationConfiguration> CreateAsync(CancellationToken ct)
    {
        var pki = Path.Combine(AppContext.BaseDirectory, options.OpcUa.PkiPath);
        var id = options.Gateway.Id;
        var config = new ApplicationConfiguration(telemetry)
        {
            ApplicationName = $"MiniMes edge gateway {id}",
            ApplicationUri = $"urn:{System.Net.Dns.GetHostName()}:minimes:gateway:{id}",
            ProductUri = "urn:minimes:gateway",
            ApplicationType = ApplicationType.Client,
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(pki, "own"),
                    SubjectName = $"CN=MiniMes edge gateway {id}, O=MiniMes",
                },
                TrustedPeerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pki, "trusted") },
                TrustedIssuerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pki, "issuer") },
                // Refused server certificates land here: production moves them to trusted/ by hand.
                RejectedCertificateStore = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pki, "rejected") },
                AutoAcceptUntrustedCertificates = autoAccept,
            },
            TransportQuotas = new TransportQuotas { OperationTimeout = 15_000 },
            ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60_000 },
        };
        await config.ValidateAsync(ApplicationType.Client, ct);
        await new ApplicationInstance(config, telemetry).CheckApplicationInstanceCertificatesAsync(silent: true, ct: ct);
        return config;
    }
}
