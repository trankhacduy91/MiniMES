# Architecture decisions

Mỗi quyết định ghi theo khung ADR (Architecture Decision Record): bối cảnh → quyết định → phương án đã loại → đánh đổi. Bảng đầy đủ các lựa chọn công nghệ ở [`spec/module-00-foundation.md`](spec/module-00-foundation.md) mục 4. Các ADR mới thêm vào cuối file theo từng module.

| # | Quyết định | Ngày |
| --- | --- | --- |
| [ADR-001](#adr-001-clean-architecture-modular-monolith) | Clean Architecture, modular monolith | 10/2026 |
| [ADR-002](#adr-002-wpf-gọi-api-không-nối-thẳng-database) | WPF gọi API, không nối thẳng database | 10/2026 |
| [ADR-003](#adr-003-audit-trail-ở-ef-core-interceptor) | Audit trail ở EF Core interceptor | 10/2026 |
| [ADR-004](#adr-004-auditlog-chặn-sửaxóa-bằng-trigger) | `AuditLog` chặn sửa/xóa bằng trigger | 10/2026 |

---

## ADR-001: Clean Architecture, modular monolith

**Bối cảnh.** Một người phát triển, một database SQL Server, khoảng 10 module nghiệp vụ (Master Data, Work Order, Execution, OEE, Quality...). Quy tắc nghiệp vụ của MES (route check, state machine của WO, công thức OEE, SPC rule) là phần quan trọng nhất và phải test được nhanh, không cần DB.

**Quyết định.** Một process duy nhất (monolith), chia layer theo Clean Architecture:

```
Api ──► Application ──► Domain
 └──► Infrastructure ──┘
```

- `Domain`: entity và quy tắc nghiệp vụ, **không tham chiếu gì** (không EF, không ASP.NET, không package nào).
- `Application`: use case và interface (`ICurrentUser`, từ M1 là `IAppDbContext`).
- `Infrastructure`: EF Core, migration, interceptor, MQTT, job nền; cài đặt các interface của Application.
- `Api`: Controller mỏng, middleware, auth, DI; không chứa logic nghiệp vụ.

Bên trong mỗi layer chia theo module bằng thư mục (`Factory/`, `Users/`, sau này `Planning/`, `Quality/`...). Chiều phụ thuộc được kiểm bằng [`DependencyTests`](tests/MiniMes.UnitTests/Architecture/DependencyTests.cs), đọc `ProjectReference`/`PackageReference` trong `.csproj` (không dùng `GetReferencedAssemblies()` vì compiler bỏ tham chiếu không dùng): khai báo sai là CI đỏ.

**Phương án đã loại.**
- *N-tier kiểu Facade → Business → DAO:* logic dễ dính vào DB, khó unit test.
- *Microservice:* một người, một DB, không có nhu cầu scale độc lập; tốn công triển khai, gọi mạng giữa service, transaction phân tán mà không đem lại lợi ích ở quy mô này.
- *Một project duy nhất:* nhanh lúc đầu, nhưng không có gì ngăn Controller gọi thẳng `DbContext` hay Domain phụ thuộc EF.

**Đánh đổi.** Nhiều project và interface hơn một CRUD đơn giản cần. Đổi lại: Domain test bằng unit test thuần (vài chục ms); hạ tầng (EF, MQTT, SignalR) thay được mà không đụng quy tắc nghiệp vụ; ranh giới module rõ, nếu sau này cần tách service thì tách theo module.

---

## ADR-002: WPF gọi API, không nối thẳng database

**Bối cảnh.** Nhiều MES in-house ở các nhà máy vừa và nhỏ cho app WinForms/WPF kết nối thẳng SQL Server bằng một tài khoản dùng chung (`sa` hoặc `mes_app`). Hệ quả: chuỗi kết nối nằm trên mọi máy trạm (ai cũng mở SSMS sửa được dữ liệu), DB không biết *người* nào đã ghi, đổi logic phải cài lại từng máy.

**Quyết định.** Hai app WPF (BackOffice, Kiosk) chỉ gọi REST API với JWT:

```
BackOffice.Wpf, Kiosk.Wpf ──► Client.Core ──► Contracts      (không thấy Domain/Application/Infrastructure)
```

- `Client.Core` (`net10.0` thuần): `ApiClient` (gắn JWT + `X-Correlation-Id`, đọc ProblemDetails), `TokenStore`, ViewModel dùng chung, `Strings.resx`.
- Mỗi request mang token của *người* đang dùng; quyền kiểm ở API (`[Authorize(Roles=...)]`), không chỉ ẩn nút ở UI.
- Kiosk đăng nhập bằng mã thẻ + mã trạm lấy từ `appsettings.json` của máy; token kiosk chỉ mang role `Operator`.

`DependencyTests` cấm app WPF và `Client.Core` tham chiếu EF Core / `Microsoft.Data.SqlClient`: về mặt biên dịch không có đường nào tới DB.

**Phương án đã loại.**
- *Client nối thẳng SQL Server:* như bối cảnh; không phân quyền theo người, không audit được "ai".
- *Web app thay WPF:* hợp lý, nhưng kiosk ở xưởng cần đầu đọc thẻ/máy in nhãn cục bộ và spec chọn WPF.

**Đánh đổi.** Thêm một tầng mạng: vài ms độ trễ, và client phải xử lý mất kết nối ("Cannot reach server" ở M0, banner Offline ở M3). Đổi lại: phân quyền và audit nằm ở một chỗ, cập nhật logic chỉ cần deploy API, ViewModel unit test được trên Linux CI vì `Client.Core` không có type WPF.

---

## ADR-003: Audit trail ở EF Core interceptor

**Bối cảnh.** Khách hàng và đoàn audit (IATF 16949, customer audit) hỏi "ai đổi BOM/cấu hình test, lúc nào, giá trị cũ là gì". Nếu mỗi use case tự gọi `audit.Write(...)`, sẽ có ngày ai đó quên, đúng lúc cần bằng chứng nhất.

**Quyết định.** `AuditSaveChangesInterceptor` móc vào mọi `SaveChanges` của EF Core:

- Chỉ entity đánh dấu `IAudited` (bảng master, trạng thái WO); bảng log lớn (`ScanLog`, `MachineEvent`) đã insert-only nên không audit.
- **Modified:** mỗi cột thật sự đổi một dòng (`OldValue` → `NewValue`). **Added/Deleted:** một dòng `ColumnName = "*"`, giá trị là JSON mọi cột.
- "Ai" lấy từ `ICurrentUser` (claim `sub` của JWT, `system` khi seeder/job nền); "lúc nào" từ `TimeProvider`, lưu UTC.
- Cột nhạy cảm (`[AuditIgnore]`, ví dụ `PasswordHash`) vẫn ghi là có đổi nhưng giá trị là `***`.
- **Cùng transaction với dữ liệu.** Bản ghi Added chỉ có Id sau khi lưu, nên interceptor tự mở transaction (hoặc dùng lại transaction của use case), lưu dữ liệu, rồi lưu dòng audit lần hai và commit; lần hai lỗi thì rollback cả hai. Integration test kiểm chứng bằng cách làm hỏng lần lưu audit: bản ghi gốc cũng không còn.

**Phương án đã loại.**
- *Ghi tay trong use case:* dễ quên, job nền không đi qua use case.
- *Trigger SQL ghi audit:* DB chỉ thấy login của ứng dụng, không biết người dùng nào.
- *Temporal table / CDC:* ghi được thay đổi nhưng cũng không biết người dùng ứng dụng; khó truy vấn "ai đổi cột X".

**Đánh đổi.** Thay đổi đi ngoài ChangeTracker (`ExecuteUpdate`, `SqlBulkCopy`, script SQL) không được audit, nên chỉ dùng chúng cho bảng không cần audit. Không bật `EnableRetryOnFailure` vì execution strategy có retry từ chối transaction do interceptor tự mở.

---

## ADR-004: `AuditLog` chặn sửa/xóa bằng trigger

**Bối cảnh.** Audit trail là bằng chứng; nếu sửa hoặc xóa được thì không còn là bằng chứng. "Code không có API sửa/xóa" là chưa đủ: người có SSMS (kể cả dev dùng `sa`) vẫn sửa được.

**Quyết định.** Hai lớp bảo vệ:

1. **Trigger** `TR_AuditLog_BlockModify` (`INSTEAD OF UPDATE, DELETE`) ném lỗi 51000 cho mọi lệnh sửa/xóa, kể cả từ `sa` (vì `sa` bỏ qua mọi `DENY`). Trigger tạo trong migration (`migrationBuilder.Sql`), nên dựng lại DB là có ngay.
2. **`DENY UPDATE, DELETE ON dbo.AuditLog`** cho tài khoản ứng dụng ở môi trường thật (tài khoản này không phải sysadmin).

API chỉ có `GET /api/audit-logs` (Admin). Integration test chạy `UPDATE`/`DELETE` thẳng vào DB và kiểm tra lỗi 51000, số dòng không đổi.

**Phương án đã loại.**
- *Chỉ dựa vào code/API:* như bối cảnh.
- *Ledger table (SQL Server 2022):* chống giả mạo mạnh hơn (hash chain), nhưng nặng hơn cần thiết ở MVP; là bước tiếp theo nếu cần.

**Đánh đổi / giới hạn (nói thật khi phỏng vấn).** Người có quyền `ALTER` bảng (db_owner, sysadmin) vẫn có thể `DISABLE TRIGGER` hoặc `TRUNCATE` (TRUNCATE không kích hoạt trigger DELETE). Muốn chặn cả họ cần tách quyền DBA và quyền ứng dụng, bật SQL Server Audit để ghi lại chính các lệnh đó, hoặc dùng Ledger table.

## ADR-005: Explode BOM bằng recursive CTE, cây BOM dựng trong bộ nhớ

**Bối cảnh.** Explode BOM (tổng nhu cầu NVL cho N thành phẩm, mọi cấp) là truy vấn lõi của M1, sẽ được M2 (snapshot khi release WO) và M6 (truy xuất) dùng lại. Cách ngây thơ là gọi DB cho từng cấp/từng Item: cây 3 cấp × 9 dòng đã là hàng chục round-trip (N+1), cây thật vài trăm dòng thì chậm theo độ sâu.

**Quyết định.**

1. `GET /api/items/{id}/explosion?qty=&asOfUtc=` chạy **một** recursive CTE (`Infrastructure/Persistence/Queries/BomExplosionQuery.cs`): anchor = con trực tiếp của gốc, phần đệ quy nhân dồn `QtyPer × (1 + ScrapRate)` xuống từng cấp, cuối cùng `GROUP BY` để **cộng các nhánh**. Chỉ dùng version Approved hiệu lực tại `asOf` (`[EffectiveFrom, EffectiveTo)`). `CAST(... AS decimal(28,9))` để không mất lẻ; `OPTION (MAXRECURSION 50)` để dữ liệu lỡ có vòng thì báo lỗi 530 (đổi thành 422) thay vì chạy mãi.
2. Application chỉ thấy interface `IBomExplosionQuery`; SQL thô là chi tiết của Infrastructure.
3. Làm tròn **một lần, trên tổng**, trong Domain (`Units.RoundRequirement`): `pcs` làm tròn lên; `kg/g/m` giữ giá trị chính xác (0,12726 kg, không làm tròn xuống 0,127 để khỏi cấp thiếu; UI hiển thị 3 số lẻ). API trả cả `ExactQty` và `RequiredQty`.
4. `GET /api/items/{id}/bom-tree` (cho WPF `TreeView`) tải mọi BOM hiệu lực trong **một** truy vấn rồi đệ quy trong bộ nhớ (`Domain/MasterData/Boms/BomExplosion.cs`, hàm thuần, có unit test với bảng PCB-A).
5. Semi không có BOM hiệu lực → 422 kèm danh sách mã (nếu không, cả nhánh biến mất khỏi kết quả mà không ai biết).

Integration test (Testcontainers SQL Server) so CTE với bảng của spec (202, 204, 2473, 4121, 204, 0,127 kg, 201, 201, 200) và **đối chiếu** tổng cộng từ cây với kết quả CTE: hai cách tính độc lập phải ra cùng số.

**Phương án đã loại.**
- *Đệ quy trong C# gọi DB mỗi cấp:* N+1 round-trip.
- *Tải hết BOM vào bộ nhớ rồi tính cho cả explode:* đơn giản và đủ nhanh với dữ liệu MVP (đã dùng cho `bom-tree`), nhưng kéo cả bảng BOM về ứng dụng khi chỉ cần một cây; CTE để DB lọc và tổng hợp.
- *Bảng tính sẵn (materialized/closure table):* nhanh khi đọc nhưng phải đồng bộ mỗi lần Approve; thừa cho MVP.

**Đánh đổi / giới hạn.** Có hai cài đặt cùng một phép tính (SQL và C#): phải giữ chúng khớp, nên có test đối chiếu. SQL thô không được compiler kiểm tra tên bảng/cột: test tích hợp chạy trên SQL Server thật là lưới an toàn. Dữ liệu lớn sẽ cần index `Boms(ItemId, Status, EffectiveFrom)` (đo ở M3).
