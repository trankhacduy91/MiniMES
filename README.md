# Mini MES

Dự án học MES (.NET 10, SQL Server, MQTT, WPF). Chi tiết: `spec/`.

## Chạy nhanh (dev)

```bash
cp .env.example .env                  # đổi mật khẩu; đặt MSSQL_PORT nếu 1433 đã bị chiếm
docker compose up -d                  # SQL Server + Mosquitto
dotnet tool restore                   # dotnet-ef (ghim trong dotnet-tools.json)

# connection string lưu ở user-secrets (không commit); thay <password> và <port>
dotnet user-secrets set "ConnectionStrings:Default" "Server=localhost,<port>;Database=MiniMes;User Id=sa;Password=<password>;TrustServerCertificate=True" --project src/MiniMes.Api
# khóa ký JWT: chuỗi ngẫu nhiên >= 32 ký tự (thiếu thì API không khởi động)
dotnet user-secrets set "Jwt:Key" "<random-string-at-least-32-chars>" --project src/MiniMes.Api

dotnet ef database update --project src/MiniMes.Infrastructure --startup-project src/MiniMes.Api
dotnet run --project src/MiniMes.Api  # Development: seed factory (10 station) + user demo; Swagger: http://localhost:5157/swagger
```

## Audit trail

Mọi thay đổi trên bảng master (Site/Area/Line/Station, User, Role) tự ghi vào `AuditLog` qua `AuditSaveChangesInterceptor`; Admin xem bằng `GET /api/audit-logs?tableName=Stations&recordKey=1`.

`AuditLog` chỉ được INSERT: trigger `TR_AuditLog_BlockModify` chặn UPDATE/DELETE, kể cả với `sa`. Ở môi trường thật, ứng dụng dùng tài khoản riêng (không phải sysadmin) và thêm:

```sql
DENY UPDATE, DELETE ON dbo.AuditLog TO [minimes_app];
```

Giới hạn: db_owner/sysadmin vẫn có thể `DISABLE TRIGGER` hoặc `TRUNCATE`; muốn chặn cả họ cần tách quyền DBA, SQL Server Audit hoặc Ledger table.

## Tài khoản demo (chỉ Development)

| Mã | Mật khẩu | Role |
| --- | --- | --- |
| `admin` | `Demo@12345` | Admin |
| `planner01` | `Demo@12345` | Planner |
| `sup01` | `Demo@12345` | Supervisor |
| `qc01` | `Demo@12345` | QC |
| `mt01` | `Demo@12345` | Maintenance |
| `EMP00123` | (không có, quẹt thẻ) | Operator — `POST /api/auth/login-operator` kèm `stationCode`, ví dụ `L1-TEST-01` |

README đầy đủ sẽ hoàn thiện ở M0 bước 11.
