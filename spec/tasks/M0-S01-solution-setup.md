# Task M0-S01 — Dựng solution và quy ước

> Phiếu giao việc cho **một session Claude Code mới**. Tự đủ để làm Bước 1 của Module 0 mà không cần đọc lịch sử chat.
> Ước lượng: 1,5 giờ. Kết quả: solution `MiniMes.slnx` build sạch, đúng chiều phụ thuộc, có test chứng minh.

## 0. Đọc trước khi làm

| Thứ tự | File | Đọc phần nào | Để làm gì |
| --- | --- | --- | --- |
| 1 | [CLAUDE.md](../../CLAUDE.md) | Toàn bộ | Rule dự án: stack .NET 10, cấu trúc, chiều phụ thuộc, không tự commit |
| 2 | File này | Toàn bộ | Phạm vi, lệnh, nghiệm thu của bước 1 |
| 3 | [module-00-foundation.md](../module-00-foundation.md) | Mục 2.1 (Clean Architecture), Mục 3 "Bước 1" | Lý do thiết kế; checklist cần đánh dấu khi xong |
| 4 | [spec-v2.md](../spec-v2.md) | B10 dòng #11–#15 | Các quyết định đã chốt 10/2026 |

Không cần đọc các module khác. Khi file này và spec khác nhau, **theo file này** (file này viết sau, đã gộp các quyết định); báo lại cho user chỗ khác nhau.

## 1. Phạm vi

**Làm:**

- Xóa khung cũ `OpenMES.*` (tạo trước khi chốt spec).
- Tạo `global.json`, `Directory.Build.props`, `MiniMes.slnx`.
- Tạo 5 project backend + 2 project test, đặt tham chiếu đúng chiều.
- Bổ sung `.gitignore`.
- Viết `DependencyTests` kiểm tra chiều phụ thuộc.

**Không làm (thuộc bước sau, đừng làm trước):**

| Việc | Bước |
| --- | --- |
| Sửa `docker-compose.yml`, `mosquitto.conf`, `.env.example` | Bước 2 |
| Cài EF Core, tạo entity, DbContext, migration | Bước 3 |
| Serilog, ProblemDetails, Swagger UI, `/health` | Bước 4 |
| Cài Testcontainers, `Microsoft.AspNetCore.Mvc.Testing` | Bước 8 |
| Tạo `Client.Core`, 2 app WPF | Bước 10 |
| Tạo `MachineSimulator`, `EdgeGateway` | M4, M4b |
| `git commit`, `git push` | Chỉ khi user yêu cầu |

## 2. Kiểm tra điều kiện trước khi làm

Chạy ở thư mục gốc repo (`d:\Programming\Tools_ARCH\demo_project`). Điều kiện nào không đạt thì **dừng và báo user**, không tự cài.

| Kiểm tra | Lệnh | Yêu cầu |
| --- | --- | --- |
| SDK .NET 10 | `dotnet --list-sdks` | Có dòng `10.0.xxx`. Ghi lại số chính xác để đặt trong `global.json` |
| Trạng thái git | `git status --short` | Không có thay đổi chưa commit ngoài `spec/`, `CLAUDE.md`, `README.md`. Có thay đổi khác thì hỏi user |
| Branch | `git branch --show-current` | Hỏi user muốn làm thẳng trên `master` hay tạo branch (gợi ý `m0/s01-solution`) |

Docker **không** cần cho bước này.

## 3. Hiện trạng repo (lúc viết phiếu, 11/10/2026)

```
OpenMESSystem.sln                 ← xóa
src/OpenMES.Api|Application|Domain|Infrastructure/   ← xóa cả thư mục (gồm bin/obj)
tests/OpenMES.Domain.Tests/       ← xóa
command/init.txt                  ← xóa (lệnh cũ cho OpenMES, user đã đồng ý)
README.md, CLAUDE.md, spec/       ← giữ
docker-compose.yml, mosquitto.conf, .env, .env.example   ← giữ nguyên, bước 2 xử lý
.gitignore                        ← giữ, bổ sung
.vs/                              ← cache của Visual Studio, bị ignore; để nguyên
```

## 4. Kết quả cần có

```
global.json
Directory.Build.props
MiniMes.slnx
src/
  MiniMes.Domain/          net10.0, TreatWarningsAsErrors, KHÔNG có ProjectReference/PackageReference
  MiniMes.Contracts/       net10.0, không tham chiếu gì
  MiniMes.Application/     net10.0 → Domain, Contracts
  MiniMes.Infrastructure/  net10.0 → Application
  MiniMes.Api/             net10.0, Web SDK, dạng Controller → Application, Infrastructure
tests/
  MiniMes.UnitTests/         → Domain, Application, Contracts   (+ Client.Core từ bước 10)
    Architecture/DependencyTests.cs
  MiniMes.IntegrationTests/  → Api, Infrastructure
```

## 5. Các bước

### 5.1 Xóa khung cũ

Xóa đúng các mục ở mục 3. Sau đó `git grep -n OpenMES -- . ':!spec' ':!CLAUDE.md'` không còn kết quả. Tài liệu trong `spec/` và `CLAUDE.md` có nhắc chữ `OpenMES` để giải thích lịch sử; đừng sửa chúng.

### 5.2 `global.json`

```bash
dotnet new globaljson --sdk-version <10.0.xxx đã ghi ở mục 2> --roll-forward latestFeature
```

`latestFeature`: máy có SDK 10 bản mới hơn vẫn dùng được, nhưng không nhảy sang major 11.

Kiểm tra: `dotnet --version` ở gốc repo ra `10.0.x`.

### 5.3 `Directory.Build.props` (gốc repo)

```xml
<Project>
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
```

`TargetFramework` **không** đặt ở đây; mỗi project tự khai báo, vì WPF ở bước 10 dùng `net10.0-windows`. Các `.csproj` tạo từ template có sẵn `Nullable`/`ImplicitUsings`; xóa hai dòng đó khỏi `.csproj` để chỉ còn một nơi khai báo.

### 5.4 Tạo solution và project

```bash
dotnet new sln -n MiniMes                    # SDK 10 tạo MiniMes.slnx
dotnet new classlib -n MiniMes.Domain         -o src/MiniMes.Domain
dotnet new classlib -n MiniMes.Contracts      -o src/MiniMes.Contracts
dotnet new classlib -n MiniMes.Application    -o src/MiniMes.Application
dotnet new classlib -n MiniMes.Infrastructure -o src/MiniMes.Infrastructure
dotnet new webapi --use-controllers -n MiniMes.Api -o src/MiniMes.Api
dotnet new xunit -n MiniMes.UnitTests        -o tests/MiniMes.UnitTests
dotnet new xunit -n MiniMes.IntegrationTests -o tests/MiniMes.IntegrationTests
```

- Nếu `dotnet new sln` tạo ra `.sln` thay vì `.slnx` thì SDK đang dùng không phải bản 10: quay lại mục 5.2.
- **xUnit:** dùng phiên bản template `xunit` của SDK sinh ra. Ghi lại đó là xUnit v2 hay v3 vào phần báo cáo, vì snippet `IAsyncLifetime` ở M0 mục 2.8 viết cho v2. Không tự đổi sang template `xunit3`.

### 5.5 Dọn code template

| Project | Việc |
| --- | --- |
| Domain, Contracts, Application, Infrastructure | Xóa `Class1.cs` |
| Domain | Thêm `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` vào `.csproj` |
| Api | Xóa `WeatherForecast.cs` và `Controllers/WeatherForecastController.cs`; xóa request mẫu trong `MiniMes.Api.http`. `Program.cs` giữ tối thiểu: `AddControllers`, `AddOpenApi`, `MapOpenApi` (Development), `MapControllers`. Giữ package `Microsoft.AspNetCore.OpenApi` của template. Thư mục `Controllers/` để trống; tạo file `.gitkeep` nếu cần giữ trong git |
| UnitTests, IntegrationTests | Xóa `UnitTest1.cs` |

Ghi lại cổng HTTP/HTTPS trong `src/MiniMes.Api/Properties/launchSettings.json` vào phần báo cáo; bước 10 dùng làm `ApiBaseUrl` của WPF.

### 5.6 Tham chiếu và thêm vào solution

```bash
dotnet add src/MiniMes.Application    reference src/MiniMes.Domain src/MiniMes.Contracts
dotnet add src/MiniMes.Infrastructure reference src/MiniMes.Application
dotnet add src/MiniMes.Api            reference src/MiniMes.Application src/MiniMes.Infrastructure
dotnet add tests/MiniMes.UnitTests        reference src/MiniMes.Domain src/MiniMes.Application src/MiniMes.Contracts
dotnet add tests/MiniMes.IntegrationTests reference src/MiniMes.Api src/MiniMes.Infrastructure
dotnet sln MiniMes.slnx add src/MiniMes.Domain src/MiniMes.Contracts src/MiniMes.Application src/MiniMes.Infrastructure src/MiniMes.Api
dotnet sln MiniMes.slnx add tests/MiniMes.UnitTests tests/MiniMes.IntegrationTests
```

Không thêm tham chiếu nào khác. Chiều phụ thuộc đầy đủ của cả dự án nằm ở `CLAUDE.md` mục 4.

### 5.7 `.gitignore`

Thêm `logs/` (Serilog ghi file từ bước 4). Giữ các dòng đang có. Thêm `*.db`, `*.db-wal`, `*.db-shm` (SQLite outbox của EdgeGateway ở M4b) để khỏi quên.

### 5.8 `DependencyTests`

File: `tests/MiniMes.UnitTests/Architecture/DependencyTests.cs`.

**Cách kiểm tra: đọc file `.csproj`, không dùng `Assembly.GetReferencedAssemblies()`.** Lý do: trình biên dịch bỏ tham chiếu tới assembly không được dùng trong code. Ở bước 1 các project chưa có code, nên một `ProjectReference` sai sẽ không hiện trong `GetReferencedAssemblies()` và test vẫn xanh. Đọc `.csproj` bắt được ngay khi khai báo sai. Không cần thêm package nào: dùng `System.Xml.Linq`.

Cách làm:

1. Tìm gốc repo: đi ngược từ `AppContext.BaseDirectory` lên tới thư mục có `MiniMes.slnx`.
2. Với mỗi project trong bảng dưới, đọc `.csproj`, lấy tên project từ mọi `ProjectReference` (`Path.GetFileNameWithoutExtension(Include)`) và mọi `PackageReference`.
3. So với bảng luật. Dùng `[Theory]` một dòng mỗi project, thông báo lỗi ghi rõ project nào tham chiếu sai cái gì.

| Project | ProjectReference được phép (đúng danh sách, không hơn) | PackageReference |
| --- | --- | --- |
| `MiniMes.Domain` | (không có) | Không được có package nào |
| `MiniMes.Contracts` | (không có) | Không được có package nào |
| `MiniMes.Application` | `MiniMes.Domain`, `MiniMes.Contracts` | Cấm `Microsoft.AspNetCore.*`, `Microsoft.EntityFrameworkCore.SqlServer`, `Serilog.*` |
| `MiniMes.Infrastructure` | `MiniMes.Application` | — |
| `MiniMes.Api` | `MiniMes.Application`, `MiniMes.Infrastructure` | — |

Ghi chú cho người làm sau: Application **được** dùng gói `Microsoft.EntityFrameworkCore` (bản lõi) từ M1, vì `IAppDbContext` có `DbSet<>` (M1 mục 2.1) và M7 dùng `ExecuteSqlInterpolatedAsync`. Chỉ cấm provider SQL Server. Từ bước 10 thêm luật cho `Client.Core` (chỉ được tham chiếu `MiniMes.Contracts`) và 2 app WPF (chỉ `MiniMes.Client.Core`); để sẵn bảng luật dạng dữ liệu cho dễ thêm dòng.

Thêm một test: Domain `.csproj` có `TreatWarningsAsErrors = true`.

## 6. Nghiệm thu

Làm theo thứ tự; dán output thật vào báo cáo.

| # | Kiểm tra | Kết quả mong đợi |
| --- | --- | --- |
| 1 | `dotnet --version` (gốc repo) | `10.0.x` |
| 2 | `dotnet build MiniMes.slnx` | Thành công, **0 warning, 0 error** |
| 3 | `dotnet test tests/MiniMes.UnitTests` | Mọi test xanh (≥ 6: 5 dòng luật + `TreatWarningsAsErrors`) |
| 4 | Thêm tạm `using Microsoft.EntityFrameworkCore;` vào một file trong Domain, build | **Lỗi** biên dịch (Domain không có EF). Hoàn tác |
| 5 | Thêm tạm `ProjectReference` từ Domain tới Infrastructure, chạy lại test | `DependencyTests` **đỏ**, thông báo nêu đúng Domain → Infrastructure. Hoàn tác, test xanh lại |
| 6 | `git grep -n OpenMES -- . ':!spec' ':!CLAUDE.md'` | Không có kết quả |
| 7 | `git status --short` | Chỉ có file thuộc phạm vi mục 1; không có `bin/`, `obj/` |
| 8 | Mở `MiniMes.slnx` trong IDE của user | Thấy 7 project (user tự kiểm, ghi nhắc trong báo cáo) |

## 7. Khi xong

1. Đánh dấu `[x]` cho "Bước 1 — Dựng solution và quy ước" trong [module-00-foundation.md](../module-00-foundation.md) mục 3.
2. Báo cáo cho user (tiếng Việt):
   - Danh sách file tạo/xóa.
   - Output của các lệnh nghiệm thu 1–7.
   - Phiên bản SDK, phiên bản xUnit, cổng API.
   - Những chỗ phải làm khác phiếu này và lý do.
3. Nếu gặp lỗi đáng nhớ (khó tìm nguyên nhân, có bài học), ghi vào `docs/incidents.md` (tạo file nếu chưa có).
4. **Không commit.** Gợi ý message nếu user yêu cầu: `M0: scaffold MiniMes solution (.NET 10)`.

## 8. Kiến thức user cần hiểu sau bước này

Giải thích ngắn khi báo cáo, trỏ tới M0 mục 2.1:

- Quy tắc phụ thuộc của Clean Architecture; vì sao Domain không tham chiếu gì.
- Vì sao kiểm tra kiến trúc bằng test (luật bị phá thì CI đỏ, không dựa vào trí nhớ).
- `global.json` và `rollForward`; `Directory.Build.props`; định dạng `.slnx` mới.
- Câu tự kiểm tra: "Vẽ quy tắc phụ thuộc; Domain được tham chiếu tới project nào?" (M0 mục 5.2 câu 4).
