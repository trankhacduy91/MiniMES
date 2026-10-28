# CLAUDE.md — Mini MES (pet project)

Rule dự án cho Claude Code. Đọc file này trước mọi task; khi rule ở đây khác thói quen mặc định, theo rule ở đây.

## 1. Bối cảnh

- Chủ dự án: dev web C#/.NET/SQL Server, đang chuyển sang **IT MES** ở khu công nghiệp, chưa có kinh nghiệm nhà máy.
- Mục tiêu: hiểu nghiệp vụ MES + cách kết nối IoT (MQTT, OPC UA, Modbus) đủ để **đưa vào CV và tự tin phỏng vấn**. Đây là project học, không phải sản phẩm thật.
- Hệ quả: ưu tiên code dễ đọc, giải thích được trong phỏng vấn; không tối ưu sớm, không thêm tính năng ngoài spec.

## 2. Nguồn sự thật (theo thứ tự ưu tiên)

1. `spec/spec-v2.md` — phạm vi đã chốt (mục B10), giờ ước lượng, roadmap MVP-1…6.
2. `spec/module-XX-*.md` — spec chi tiết từng module (chỗ đổi theo v2 đánh dấu **[v2]**). Mỗi file có 5 phần; phần 3 là kế hoạch từng bước, làm theo đúng thứ tự bước.
3. `spec/spec.md` — bản gốc; chỉ dùng khi v2 và file module không nói gì.

- Spec mâu thuẫn hoặc thiếu → **hỏi user**, không tự đoán rồi code.
- Muốn làm khác spec (đổi thiết kế, thêm thư viện, thêm bảng) → đề xuất trước, user duyệt mới làm, rồi cập nhật lại file spec tương ứng.
- **Ngoài phạm vi, không làm** (B10): Gantt, M9, CAPA, calibration, kiosk offline. M8, M10 là P2, chưa có spec chi tiết.
- Thứ tự module: M0 → M1 → M2 → M3 → M4 → M4b → M5 → M6 → **M3b** → M7 (M3b làm sau M6).

## 3. Stack

- **.NET 10 (LTS)**: `net10.0` (backend, Client.Core, Simulator, EdgeGateway), `net10.0-windows` (2 app WPF). SDK ghim bằng `global.json`; solution là `MiniMes.slnx`. Không dùng .NET 8/9 (hết hỗ trợ 10/11/2026). Snippet spec ghi .NET 8 vẫn áp dụng; API khác phiên bản thì đối chiếu tài liệu .NET 10.
- ASP.NET Core dạng **Controller**, EF Core 10 + SQL Server 2022 (`UseCompatibilityLevel(160)`), xUnit + Testcontainers.
- OpenAPI: `Microsoft.AspNetCore.OpenApi` (có sẵn) + `Swashbuckle.AspNetCore.SwaggerUI` (chỉ phần giao diện). Không dùng `AddSwaggerGen`.
- MediatR **12.x** (Apache 2.0) — không nâng lên v13+ (license thương mại). FluentValidation, Serilog, JWT.
- WPF + MVVM (CommunityToolkit.Mvvm), OxyPlot (thư viện biểu đồ duy nhất), WebView2, ClosedXML.
- IoT: MQTTnet + Mosquitto, OPCFoundation.NetStandard.Opc.Ua, NModbus, Microsoft.Data.Sqlite, Polly.
- Thêm package ngoài danh sách trên → hỏi trước.

## 4. Cấu trúc solution

```
src/MiniMes.Domain           entity, value object, state machine, rule — không tham chiếu EF/ASP.NET/Serilog
src/MiniMes.Application      use case (MediatR), interface IAppDbContext/ICurrentUser, validator
src/MiniMes.Infrastructure   DbContext, migration, interceptor, MQTT, SignalR, job nền
src/MiniMes.Api              Controllers, auth, DI, middleware — không chứa logic nghiệp vụ
src/MiniMes.Contracts        DTO dùng chung API ↔ WPF
src/MiniMes.Client.Core      ApiClient, SignalR client, ViewModel dùng chung, Strings.resx (net10.0 thuần, không type WPF)
src/MiniMes.BackOffice.Wpf   app văn phòng (View + Generic Host)
src/MiniMes.Kiosk.Wpf        app trạm (+ chế độ --mode=linetv); appsettings có StationCode
src/MiniMes.MachineSimulator giả lập máy MQTT / OPC UA server / Modbus slave   (tạo ở M4)
src/MiniMes.EdgeGateway      Worker Service: OPC UA + Modbus → MQTT, SQLite outbox (tạo ở M4b)
tests/MiniMes.UnitTests, tests/MiniMes.IntegrationTests
```

- Chiều phụ thuộc (chỉ những tham chiếu này):
  - Domain, Contracts → không gì
  - Application → Domain, Contracts
  - Infrastructure → Application
  - Api → Application, Infrastructure (Infrastructure chỉ để đăng ký DI)
  - Client.Core → Contracts; BackOffice.Wpf, Kiosk.Wpf → Client.Core (WPF không bao giờ thấy Domain/Application/Infrastructure → không thể nối DB)
  - MachineSimulator, EdgeGateway → Contracts (chương trình riêng, chỉ chung hợp đồng MQTT với MES)
  - UnitTests → Domain, Application, Client.Core, EdgeGateway (hàm thuần của gateway, spec M4b); IntegrationTests → Api, Infrastructure, EdgeGateway + MachineSimulator (test đầu-cuối M4b, tham chiếu bằng extern alias vì mỗi project có `Program` riêng)
- Giữ chiều phụ thuộc bằng `tests/MiniMes.UnitTests/Architecture/DependencyTests.cs`. Không tạo project `Common`/`Shared`; không thêm project ngoài danh sách trên khi chưa hỏi.
- Khung `OpenMES.*` (net9.0, có RabbitMQ) là bản tạo trước khi chốt spec: xóa ở M0 bước 1, không sửa tiếp.

## 5. Quy ước code

- Bật `Nullable` mọi project; `TreatWarningsAsErrors` cho Domain.
- Thời gian: lưu UTC (`datetime2`), lấy qua `TimeProvider` (test dùng `FakeTimeProvider`), không gọi `DateTime.Now`/`UtcNow` trực tiếp trong Domain/Application. Hiển thị theo giờ nhà máy.
- API: tiền tố `/api`; chỉ dùng Controller (`Api/Controllers/XxxController.cs`, `[ApiController]`, `[Authorize(Roles=...)]`), không Minimal API. Controller mỏng (nhận HTTP → `ISender.Send` → trả HTTP). M0 gọi service thường ở Application; từ M1 mọi use case đi qua MediatR, một pattern duy nhất.
- Lỗi: `GlobalExceptionHandler` → ProblemDetails. 400 validation, 404 không tìm thấy, 409 `ConflictException` (trùng mã, concurrency), 422 `DomainException` (vi phạm quy tắc nghiệp vụ; đặt ở `Domain/Common`), 500 còn lại không lộ stack trace. Kết quả nghiệp vụ "bị chặn" (scan sai bước) không phải exception: 200 + `reasonCode`.
- Môi trường: `Development` đọc user-secrets, migrate bằng tay (`dotnet ef database update`), seeder chạy khi khởi động; `Testing` nhận secret qua biến môi trường, tự `MigrateAsync()` + seed.
- Log: Serilog message template có tham số (`{SerialNo}`), không nối chuỗi `$""`; mỗi request có `X-Correlation-Id`.
- Audit: ghi qua `AuditSaveChangesInterceptor` cùng transaction; Modified ghi mỗi cột một dòng, Added/Deleted ghi một dòng `ColumnName="*"` với JSON; Added ghi ở `SavedChanges` trong transaction interceptor tự mở (dùng lại transaction của use case nếu có). Không bật `EnableRetryOnFailure`. Bảng `AuditLog` chỉ được INSERT (trigger chặn sửa/xóa).
- Master data không xóa cứng: `IsActive=false`.
- Quy tắc nghiệp vụ (route check, state machine, OEE, SPC rule…) đặt trong Domain, viết thành hàm thuần để unit test không cần DB.
- Thao tác cạnh tranh (scan trùng, đóng thùng, gán mẫu FAI) dùng cập nhật có điều kiện / unique index như spec mô tả, không dùng lock trong bộ nhớ.
- Migration: `--project src/MiniMes.Infrastructure --startup-project src/MiniMes.Api`. Trigger/SQL đặc biệt viết trong migration (`migrationBuilder.Sql`). Không sửa migration đã commit; tạo migration mới.
- UI WPF: tiếng Anh, mọi chuỗi trong `Strings.resx` (key dạng `Screen_Element`). Không `.Result`/`.Wait()` trên thread UI. ViewModel đăng ký Transient/Scoped, không Singleton.
- MQTT topic theo Unified Namespace: `minimes/{site}/{area}/{line}/{machine}/{kind}`, tag quy trình `.../process/{tag}`, trạng thái gateway `minimes/{site}/_sys/gateway/{id}/status`.
- Bí mật (connection string, khóa JWT, mật khẩu SA) ở user-secrets hoặc `.env`; không commit.
- Comment trong code: ngắn, tiếng Anh đều được, chỉ giải thích "tại sao".

## 6. Test

- Mỗi bước có mục "Kiểm tra" trong spec → viết test tương ứng trước khi đánh dấu xong.
- Domain rule: unit test bảng (`[Theory]`) với đúng các test case liệt kê trong spec (ví dụ route check 19 case).
- Cần DB/MQTT thật → integration test bằng Testcontainers, không mock `DbContext`.
- CI: GitHub Actions (`.github/workflows/ci.yml`), chạy khi push `main` và PR; SDK lấy từ `global.json`; trên `ubuntu-latest` build/test từng project backend, không build cả solution (có WPF).
- Sau khi sửa code: chạy `dotnet build` và test liên quan; báo kết quả thật (kể cả fail). Docker không chạy thì nói rõ, không bỏ qua test.

## 7. Cách làm việc với user

- Trả lời bằng **tiếng Việt**; tên kỹ thuật, tên class, thuật ngữ MES giữ tiếng Anh.
- User đang học: khi dùng khái niệm mới (MES hoặc kỹ thuật), giải thích ngắn 1–2 câu và trỏ tới mục spec liên quan. Không giải thích lại thứ user đã biết (C#, ASP.NET Core, EF Core, SQL Server cơ bản).
- Làm từng bước nhỏ theo kế hoạch module; xong mỗi bước thì tóm tắt đã làm gì, kiểm tra ra sao, và đánh dấu `[x]` ở checklist trong file module.
- Không tự commit/push. Chỉ commit khi user yêu cầu; message tiếng Anh, ngắn, dạng `M1: add item CRUD`.
- Snippet trong spec chưa được biên dịch: khi API thư viện khác phiên bản, đối chiếu tài liệu chính thức của đúng phiên bản đang cài, rồi ghi chú lại vào spec.
- Gặp lỗi đáng nhớ (khó debug, có bài học) → ghi vào `docs/incidents.md` để dùng khi phỏng vấn. Xong mỗi mốc `v0.x` → cập nhật README.
