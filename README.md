# Mini MES

Dự án học MES (.NET 10, SQL Server, MQTT, WPF). Chi tiết: `spec/`.

## Chạy nhanh (dev)

```bash
cp .env.example .env                  # đổi mật khẩu; đặt MSSQL_PORT nếu 1433 đã bị chiếm
docker compose up -d                  # SQL Server + Mosquitto
dotnet tool restore                   # dotnet-ef (ghim trong dotnet-tools.json)

# connection string lưu ở user-secrets (không commit); thay <password> và <port>
dotnet user-secrets set "ConnectionStrings:Default" "Server=localhost,<port>;Database=MiniMes;User Id=sa;Password=<password>;TrustServerCertificate=True" --project src/MiniMes.Api

dotnet ef database update --project src/MiniMes.Infrastructure --startup-project src/MiniMes.Api
dotnet run --project src/MiniMes.Api  # Development: seeder tạo 1 site, 1 area, 2 line, 10 station
```

README đầy đủ sẽ hoàn thiện ở M0 bước 11.
