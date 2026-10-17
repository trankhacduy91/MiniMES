# Mini MES

[![ci](https://github.com/trankhacduy91/MiniMES/actions/workflows/ci.yml/badge.svg)](https://github.com/trankhacduy91/MiniMES/actions/workflows/ci.yml)

Hệ thống điều hành sản xuất (MES) thu nhỏ cho một nhà máy lắp ráp điện tử: Work Order, truy xuất serial/lot, OEE, chất lượng, kết nối máy qua MQTT / OPC UA / Modbus. Đây là dự án học để chuyển sang vị trí **IT MES**; spec chi tiết ở [`spec/`](spec/), quyết định kiến trúc ở [`ARCHITECTURE.md`](ARCHITECTURE.md).

**Stack:** .NET 10 (LTS) · ASP.NET Core (Controller) · EF Core 10 + SQL Server 2022 · WPF (MVVM, CommunityToolkit.Mvvm) · Serilog · JWT · xUnit + Testcontainers · Docker Compose (SQL Server, Mosquitto) · GitHub Actions.

**License thư viện:** MediatR được ghim ở **12.x** (Apache 2.0); từ v13 MediatR chuyển sang license thương mại (có bản Community cần đăng ký key), nên không nâng cấp. Test `MediatR_stays_on_version_12` chặn việc vô tình nâng. FluentValidation, ClosedXML: Apache 2.0 / MIT.

**Trạng thái:**
- **M0 (nền tảng) xong:** solution Clean Architecture, Docker, đăng nhập theo role, audit trail không sửa được, API cấu hình nhà máy, CI, 2 app WPF đăng nhập được.
- **M1 (master data) xong:** Item, BOM đa cấp có version, routing, work center/máy, ca, lịch nhà máy, mã lỗi, lý do dừng; explode BOM bằng recursive CTE; import Excel; màn hình WPF Back-office. Xem [mục Master data](#master-data-m1).
- Các module tiếp theo (M2 → M7) làm theo [lộ trình](spec/spec-v2.md).

## Kiến trúc

Theo các tầng ISA-95; MES nằm ở Level 3, giữa ERP (Level 4) và máy (Level 0–2).

```
 L4  ERP (ngoài phạm vi)
 ──────────────────────────────────────────────────────────────────────────
 L3  MiniMes.Api (.NET 10, modular monolith) ─────────── SQL Server 2022
       ▲ REST + JWT (SignalR từ M4)                 ▲ MQTT subscribe (M4)
       │                                            │
   [WPF Back-office]  [WPF Kiosk]                   │
 ───────────────────────────────────────────────────┼──────────────────────
 L2  Mosquitto (MQTT broker)  ◀── Unified Namespace ┤
       [EdgeGateway] OPC UA + Modbus → MQTT (M4b) ──┘
 ──────────────────────────────────────────────────────────────────────────
 L1/L0  MachineSimulator: máy SMT (MQTT), lò reflow (OPC UA), máy dán keo (Modbus)
```

Bên trong backend (Clean Architecture, mũi tên = được tham chiếu tới):

```
Api ──► Application ──► Domain          Contracts: DTO dùng chung API ↔ WPF
 └──► Infrastructure ──┘                Client.Core ──► Contracts
      (EF Core, interceptor)            BackOffice.Wpf, Kiosk.Wpf ──► Client.Core
```

WPF chỉ thấy `Client.Core` nên không có cách nào nối thẳng DB. Chiều phụ thuộc được giữ bằng test ([`DependencyTests`](tests/MiniMes.UnitTests/Architecture/DependencyTests.cs)): sai là CI đỏ.

## Yêu cầu cài đặt

| Cần | Ghi chú |
| --- | --- |
| Windows 10/11 | App WPF chỉ chạy trên Windows; backend chạy được trên Linux/macOS |
| [.NET SDK 10](https://dotnet.microsoft.com/download/dotnet/10.0), bản `10.0.401` trở lên | Phiên bản ghim trong `global.json`; kiểm tra bằng `dotnet --version` ở thư mục repo |
| [Docker Desktop](https://www.docker.com/products/docker-desktop/) (bật WSL2) | Chạy SQL Server + Mosquitto, và cho integration test |
| IDE | Visual Studio 2026, Rider bản mới, hoặc VS Code + C# Dev Kit (cần mở được `.slnx`) |
| Git | |

Không cần cài SQL Server hay `dotnet-ef` riêng: SQL Server chạy trong Docker, `dotnet-ef` là local tool của repo.

## Chạy lần đầu

Các lệnh chạy ở thư mục gốc repo. Dùng PowerShell hoặc Git Bash.

**1. Cấu hình môi trường Docker**

```bash
cp .env.example .env
```

Mở `.env`:
- Đổi `MSSQL_SA_PASSWORD` (SQL Server yêu cầu ≥ 8 ký tự, có chữ hoa, chữ thường, số và ký tự đặc biệt; mật khẩu yếu thì container tự thoát).
- Nếu máy đã có SQL Server khác dùng cổng 1433 (ví dụ SQL Express), bỏ dấu `#` ở dòng `MSSQL_PORT` để container dùng cổng 14333. Kiểm tra: `netstat -ano | findstr :1433`.

**2. Bật SQL Server và Mosquitto**

```bash
docker compose up -d
docker compose ps        # chờ đến khi cả hai là "healthy" (~20-30 giây)
```

**3. Cấu hình bí mật cho API** (lưu ở user-secrets trên máy bạn, không nằm trong repo)

```bash
dotnet tool restore
dotnet user-secrets set "ConnectionStrings:Default" "Server=localhost,1433;Database=MiniMes;User Id=sa;Password=<MSSQL_SA_PASSWORD trong .env>;TrustServerCertificate=True" --project src/MiniMes.Api
dotnet user-secrets set "Jwt:Key" "<chuỗi ngẫu nhiên tối thiểu 32 ký tự>" --project src/MiniMes.Api
```

Nếu đã đặt `MSSQL_PORT=14333` ở bước 1 thì dùng `Server=localhost,14333`. Thiếu `Jwt:Key` (hoặc ngắn hơn 32 ký tự) thì API không khởi động.

**4. Build và tạo database**

```bash
dotnet build
dotnet ef database update --project src/MiniMes.Infrastructure --startup-project src/MiniMes.Api
```

Phải `dotnet build` (hoặc ít nhất `dotnet restore`) trước: trên bản clone mới, `dotnet ef` cần package đã được restore, nếu không sẽ báo `NETSDK1004: Assets file ... project.assets.json not found`.

**5. Chạy API**

```bash
dotnet run --project src/MiniMes.Api --launch-profile http
```

Lần chạy đầu, seeder tạo dữ liệu demo: 1 site, 1 area, 2 line, 10 station, 6 user, và master data của model `PCB-A` (xem [Master data](#master-data-m1)). Seeder chạy mỗi lần khởi động nhưng bỏ qua mã đã có, nên chạy lại không tạo trùng và không ghi đè dữ liệu bạn đã sửa. Mở **Swagger: http://localhost:5157/swagger** → `POST /api/auth/login` với `admin` / `Demo@12345` → copy `token` → nút **Authorize**.

**6. Chạy app WPF** (terminal khác, API phải đang chạy)

```bash
dotnet run --project src/MiniMes.BackOffice.Wpf   # văn phòng: mã + mật khẩu, vd. planner01 / Demo@12345
dotnet run --project src/MiniMes.Kiosk.Wpf        # trạm: gõ EMP00123 + Enter (như quẹt thẻ)
```

Địa chỉ API (`ApiBaseUrl`) và mã trạm của kiosk (`StationCode`, mặc định `L1-TEST-01`) nằm trong `appsettings.json` của từng app. Thử trạm khác: `dotnet run --project src/MiniMes.Kiosk.Wpf -- --StationCode=L2-PACK-01`.

## Tài khoản demo (chỉ Development)

| Mã | Mật khẩu | Role | Dùng ở |
| --- | --- | --- | --- |
| `admin` | `Demo@12345` | Admin | BackOffice, Swagger |
| `planner01` | `Demo@12345` | Planner | BackOffice |
| `sup01` | `Demo@12345` | Supervisor | BackOffice |
| `qc01` | `Demo@12345` | QC | BackOffice |
| `mt01` | `Demo@12345` | Maintenance | BackOffice |
| `EMP00123` | (không có, quẹt thẻ) | Operator | Kiosk |

Tài khoản có mật khẩu không đăng nhập được bằng "quẹt thẻ" ở kiosk, và token kiosk chỉ mang role `Operator`.

## Kịch bản demo (13 bước)

Kịch bản end-to-end mà toàn dự án hướng tới ([spec-v2 B2](spec/spec-v2.md)). Sẽ hoàn thiện theo từng module; M0 cung cấp nền cho mọi bước (đăng nhập theo role, audit, Docker, khung WPF).

| # | Bước | Module | Trạng thái |
| --- | --- | --- | --- |
| 1 | Admin import Excel Item/BOM/Routing của model mới (file mẫu `PCB-B`) | M1 | xong |
| 2 | Planner tạo WO 200 pcs → Release → sinh serial, in nhãn ZPL | M2 | chưa làm |
| 3 | Kho nhập reel → QC làm IQC → lot `Available` | M6 | chưa làm |
| 4 | Operator nạp reel lên máy SMT, MES kiểm tra đúng vật tư và FIFO | M6 | chưa làm |
| 5 | FAI: serial đầu tiên qua hết các bước → QC duyệt → chạy hàng loạt | M2, M3, M7 | chưa làm |
| 6 | Simulator: SMT qua MQTT, lò reflow OPC UA, máy dán keo Modbus (qua gateway) | M4, M4b | chưa làm |
| 7 | Operator scan trên kiosk, xem SOP; NG → Repair → Test lại → OK | M3 | chưa làm |
| 8 | Ngắt mạng gateway–broker 60 s → đệm → gửi bù, không mất bộ đếm | M4b | chưa làm |
| 9 | Máy Down → Andon → quá 3 phút chưa Ack thì leo thang → chọn lý do dừng | M4, M5 | chưa làm |
| 10 | Nhiệt độ reflow trôi → SPC báo vi phạm → NCR, Hold serial liên quan | M7 | chưa làm |
| 11 | Trạm Pack: serial vào thùng 10 pcs → nhãn thùng → pallet | M3b | chưa làm |
| 12 | Reel lỗi → truy xuất xuôi serial → thùng → pallet → Hold | M6, M7 | chưa làm |
| 13 | WO Completed → báo cáo sản lượng/FPY/OEE, xuất Excel | M2, M3, M5 | chưa làm |

## Master data (M1)

Dữ liệu seed (theo [`docs/erd-master-data.md`](docs/erd-master-data.md) mục 3):

| Nhóm | Dữ liệu |
| --- | --- |
| Item | `PCB-A` (Finished), `PCBA-SMT` (Semi), 8 NVL (`PCB-BARE`, `RES-10K-0402`, `CAP-100N-0402`, `IC-MCU-QFN`, `SOLDER-PASTE` (kg), `CONN-USBC`, `SHIELD-CAN`, `PACK-BAG` (`SkipIqc`)) |
| BOM | `PCB-A` → `PCBA-SMT` → NVL, 2 cấp, v1 Approved, hiệu lực từ ngày seed |
| Routing | `PCB-A` v1: 10 SMT → 20 ASSY → 30 TEST → 40 PACK, mỗi bước có SOP ([`docs/sop/PCB-A/`](docs/sop/PCB-A/)) |
| Line 1 | 4 work center (`WC-SMT`, `WC-ASSY`, `WC-TEST`, `WC-PACK`); máy `L1-SMT-01` (MQTT), `L1-REFLOW-01` (OPC UA), `L1-GLUE-01` (Modbus), `L1-TEST-01` (MQTT); trạm M0 gắn vào work center |
| Ca | `S1` 06–14, `S2` 14–22 (T2–T7), `S3` 22–06 qua nửa đêm (T2–T6); đều 420 phút kế hoạch |
| Lịch | Ngày lễ VN của năm hiện tại (Bộ luật Lao động 2019, Điều 112) + 1 Chủ nhật tăng ca (`S1`) |
| Danh mục | 10 mã lỗi, 9 lý do dừng máy |

**Explode BOM** (tổng NVL cho 200 `PCB-A`, đi xuống mọi cấp, cộng hao hụt): trong Swagger lấy `id` của `PCB-A` bằng `GET /api/items?search=PCB-A`, rồi gọi `GET /api/items/{id}/explosion?qty=200`. Hoặc bằng curl:

```bash
TOKEN=$(curl -s -X POST http://localhost:5157/api/auth/login -H "Content-Type: application/json"   -d '{"employeeCode":"planner01","password":"Demo@12345"}' | jq -r .token)
curl -s -H "Authorization: Bearer $TOKEN" "http://localhost:5157/api/items/{id}/explosion?qty=200" | jq '.lines[] | {code, requiredQty}'
```

Kết quả đúng: `PCBA-SMT` 202, `PCB-BARE` 204, `RES-10K-0402` 2473, `CAP-100N-0402` 4121, `IC-MCU-QFN` 204, `SOLDER-PASTE` 0,12726 kg (hiển thị 0,127), `CONN-USBC` 201, `SHIELD-CAN` 201, `PACK-BAG` 200. Cây có số lượng từng nút: `GET /api/items/{id}/bom-tree?qty=200`. Cách tính và lý do dùng recursive CTE: [ADR-005](ARCHITECTURE.md#adr-005-explode-bom-bằng-recursive-cte-cây-bom-dựng-trong-bộ-nhớ).

**Màn hình WPF Back-office** (thêm/sửa/import master data cần role Admin, đăng nhập `admin`; các role khác chỉ xem): Items (lọc, phân trang), BOM (version, cây, explode), Routing (Up/Down, SOP URL), danh mục (work center, máy, ca, lịch, mã lỗi, lý do dừng), Import.

**Import Excel:** màn hình Import → *Download template* để lấy file mẫu, hoặc dùng [`docs/samples/import-PCB-B.xlsx`](docs/samples/import-PCB-B.xlsx) (model `PCB-B`) → *Validate* (kiểm tra, không ghi) → *Import* (chỉ bật khi 0 lỗi; ghi tất cả trong 1 transaction, BOM/routing ở trạng thái Draft). Cùng chức năng qua API: `POST /api/import/master-data?dryRun=true|false` (multipart, field `file`).

**Version BOM/Routing:** bản Approved bị khóa; muốn đổi thì *New version* (Draft) → sửa → *Approve*, version cũ tự đóng hiệu lực đúng lúc version mới bắt đầu ([ADR-006](ARCHITECTURE.md#adr-006-bom-và-routing-có-version-draft--approved-hiệu-lực-nửa-mở)).

## Chạy test

```bash
dotnet test tests/MiniMes.UnitTests          # nhanh, không cần Docker: Domain, ViewModel, ApiClient, luật kiến trúc
dotnet test tests/MiniMes.IntegrationTests   # cần Docker: Testcontainers bật 1 SQL Server riêng, test xong tự xóa
```

Integration test chạy API ở môi trường `Testing`: tự `MigrateAsync()` + seed trên DB mới, secret lấy từ biến môi trường (không đọc user-secrets). Docker không chạy thì test **fail**, không bị bỏ qua. CI (GitHub Actions) chạy cả hai, cộng build 2 app WPF trên Windows.

## Audit trail

Mọi thay đổi trên bảng master (Site/Area/Line/Station, User, Role, Item, BOM, Routing và các danh mục M1) tự ghi vào `AuditLog` qua `AuditSaveChangesInterceptor`, cùng transaction với dữ liệu: ai, lúc nào (UTC), giá trị cũ → mới. Admin xem bằng `GET /api/audit-logs?tableName=Stations&recordKey=1`.

`AuditLog` chỉ được INSERT: trigger `TR_AuditLog_BlockModify` chặn UPDATE/DELETE, kể cả với `sa`. Ở môi trường thật, ứng dụng dùng tài khoản riêng (không phải sysadmin) và thêm:

```sql
DENY UPDATE, DELETE ON dbo.AuditLog TO [minimes_app];
```

Giới hạn: db_owner/sysadmin vẫn có thể `DISABLE TRIGGER` hoặc `TRUNCATE`; muốn chặn cả họ cần tách quyền DBA, SQL Server Audit hoặc Ledger table. Chi tiết: [`ARCHITECTURE.md`](ARCHITECTURE.md).

## Gặp lỗi khi chạy

| Triệu chứng | Nguyên nhân thường gặp |
| --- | --- |
| Container `mes-sqlserver` thoát ngay | Mật khẩu SA yếu: xem `docker compose logs sqlserver` |
| `Login failed for user 'sa'` dù mật khẩu đúng | Một SQL Server khác đang chiếm cổng 1433: dùng `MSSQL_PORT=14333` (bước 1, 3). Chi tiết ở [`docs/incidents.md`](docs/incidents.md) |
| API báo `ConnectionStrings:Default is not configured` hoặc `Jwt:Key must be...` | Chưa làm bước 3 (user-secrets chỉ được đọc ở môi trường `Development`) |
| `docker compose up` báo container name đã tồn tại | Một bản clone khác của repo đang chạy: `docker compose down` ở thư mục đó trước |
| Đổi `MSSQL_SA_PASSWORD` trong `.env` rồi `Login failed` | Mật khẩu SA được lưu vào volume ở lần chạy **đầu tiên**; sửa `.env` sau đó không có tác dụng. Dùng lại mật khẩu cũ, hoặc `docker compose down -v` (**xóa toàn bộ dữ liệu DB**) rồi chạy lại từ bước 2 |
| `dotnet ef` báo `NETSDK1004 ... project.assets.json not found` | Chưa build/restore: chạy `dotnet build` trước (bước 4) |
| Hai bản clone trên cùng máy "đụng" cấu hình | User-secrets gắn theo `UserSecretsId` trong `.csproj`, nên mọi bản clone trên một máy dùng chung connection string và `Jwt:Key` |
| App WPF: "Cannot reach server" | API chưa chạy, hoặc `ApiBaseUrl` trong `appsettings.json` sai cổng |

## Cấu trúc repo

```
src/MiniMes.Domain          entity, quy tắc nghiệp vụ (không phụ thuộc EF/ASP.NET)
src/MiniMes.Application     use case (MediatR + FluentValidation), IAppDbContext, ICurrentUser
src/MiniMes.Infrastructure  EF Core, migration, audit interceptor, JWT, seeder
src/MiniMes.Api             Controller, middleware (CorrelationId, ProblemDetails), auth
src/MiniMes.Contracts       DTO dùng chung API ↔ WPF
src/MiniMes.Client.Core     ApiClient, ViewModel dùng chung, Strings.resx
src/MiniMes.BackOffice.Wpf  app văn phòng
src/MiniMes.Kiosk.Wpf       app trạm
tests/                      unit test, integration test (Testcontainers)
spec/                       spec dự án và từng module
docs/                       ERD master data, SOP mẫu, file import mẫu, incidents.md (lỗi đáng nhớ và bài học)
```
