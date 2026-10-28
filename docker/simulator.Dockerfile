# M4b step 7: the machine simulator (MQTT machines, OPC UA reflow oven on 4840, Modbus glue dispenser on 5020).
#   docker build -f docker/simulator.Dockerfile -t minimes-simulator .
# Console commands: docker attach mes-simulator (detach with Ctrl+P, Ctrl+Q; Ctrl+C would stop it).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/MiniMes.Contracts/MiniMes.Contracts.csproj src/MiniMes.Contracts/
COPY src/MiniMes.MachineSimulator/MiniMes.MachineSimulator.csproj src/MiniMes.MachineSimulator/
RUN dotnet restore src/MiniMes.MachineSimulator/MiniMes.MachineSimulator.csproj
COPY src/MiniMes.Contracts/ src/MiniMes.Contracts/
COPY src/MiniMes.MachineSimulator/ src/MiniMes.MachineSimulator/
RUN dotnet publish src/MiniMes.MachineSimulator/MiniMes.MachineSimulator.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 4840 5020
ENTRYPOINT ["dotnet", "MiniMes.MachineSimulator.dll"]
