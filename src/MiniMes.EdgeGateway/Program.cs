using Microsoft.Extensions.Options;
using MiniMes.EdgeGateway;

// Edge gateway (spec M4b): reads the PLCs (OPC UA, Modbus TCP), normalises the readings and publishes the M4 MQTT
// contract. A program on its own: it shares only MiniMes.Contracts with the MES, never its database.
//   dotnet run --project src/MiniMes.EdgeGateway [-- --config other.tags.json]
// The host itself is built in GatewayHost (also used by the end-to-end test).
var (builder, configFile) = GatewayHost.CreateBuilder(args);
try
{
    await builder.Build().RunAsync();
    return 0;
}
catch (OptionsValidationException ex)
{
    Console.Error.WriteLine($"{configFile} is not valid:{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", ex.Failures)}");
    return 1;
}
