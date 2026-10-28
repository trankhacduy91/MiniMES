# M4b step 7: the edge gateway as a container. Build from the repo root:
#   docker build -f docker/gateway.Dockerfile -t minimes-gateway .
# Volumes: /app/data (SQLite outbox: must survive a container restart) and /app/pki (OPC UA certificates).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/MiniMes.Contracts/MiniMes.Contracts.csproj src/MiniMes.Contracts/
COPY src/MiniMes.EdgeGateway/MiniMes.EdgeGateway.csproj src/MiniMes.EdgeGateway/
RUN dotnet restore src/MiniMes.EdgeGateway/MiniMes.EdgeGateway.csproj
COPY src/MiniMes.Contracts/ src/MiniMes.Contracts/
COPY src/MiniMes.EdgeGateway/ src/MiniMes.EdgeGateway/
RUN dotnet publish src/MiniMes.EdgeGateway/MiniMes.EdgeGateway.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
VOLUME ["/app/data", "/app/pki"]
ENTRYPOINT ["dotnet", "MiniMes.EdgeGateway.dll"]
