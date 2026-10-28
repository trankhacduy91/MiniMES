using Opc.Ua;

namespace MiniMes.EdgeGateway;

// The OPC UA stack takes its logging from an ITelemetryContext: hand it the host's logger factory.
public sealed class HostTelemetry(ILoggerFactory loggers) : TelemetryContextBase(loggers);
