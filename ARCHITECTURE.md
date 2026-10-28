# Architecture decisions

Mỗi quyết định ghi theo khung ADR (Architecture Decision Record): bối cảnh → quyết định → phương án đã loại → đánh đổi. Bảng đầy đủ các lựa chọn công nghệ ở [`spec/module-00-foundation.md`](spec/module-00-foundation.md) mục 4. Các ADR mới thêm vào cuối file theo từng module.

| # | Quyết định | Ngày |
| --- | --- | --- |
| [ADR-001](#adr-001-clean-architecture-modular-monolith) | Clean Architecture, modular monolith | 10/2026 |
| [ADR-002](#adr-002-wpf-gọi-api-không-nối-thẳng-database) | WPF gọi API, không nối thẳng database | 10/2026 |
| [ADR-003](#adr-003-audit-trail-ở-ef-core-interceptor) | Audit trail ở EF Core interceptor | 10/2026 |
| [ADR-004](#adr-004-auditlog-chặn-sửaxóa-bằng-trigger) | `AuditLog` chặn sửa/xóa bằng trigger | 10/2026 |
| [ADR-005](#adr-005-explode-bom-bằng-recursive-cte-cây-bom-dựng-trong-bộ-nhớ) | Explode BOM bằng recursive CTE, cây BOM dựng trong bộ nhớ | 10/2026 |
| [ADR-006](#adr-006-bom-và-routing-có-version-draft--approved-hiệu-lực-nửa-mở) | BOM và Routing có version: Draft → Approved, hiệu lực nửa mở | 10/2026 |
| [ADR-007](#adr-007-nguồn-dữ-liệu-oee-total-count-từ-máy-goodng-từ-scan-theo-serial) | Nguồn dữ liệu OEE: Total Count từ máy, Good/NG từ scan theo serial | 10/2026 |
| [ADR-008](#adr-008-dữ-liệu-máy-đi-qua-mqtt-hợp-đồng-chung-cho-mọi-giao-thức) | Dữ liệu máy đi qua MQTT, hợp đồng chung cho mọi giao thức | 10/2026 |
| [ADR-009](#adr-009-edge-gateway-tự-viết-opc-ua-subscription-modbus-polling-outbox-sqlite-luôn-bật) | Edge Gateway tự viết: OPC UA subscription, Modbus polling, outbox SQLite luôn bật | 10/2026 |

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

## ADR-006: BOM và Routing có version: Draft → Approved, hiệu lực nửa mở

**Bối cảnh.** BOM và routing đổi theo thời gian (BOM v2 dùng 22 tụ thay vì 20, routing thêm bước dán keo). Nếu sửa đè, WO đang chạy đổi định mức giữa chừng, và câu hỏi "lô tháng trước làm theo định mức nào" không còn trả lời được. Audit log (ADR-003) cho biết *ai sửa gì*, nhưng không cho biết *định mức nào có hiệu lực* tại một thời điểm.

**Quyết định.**

1. Mỗi lần đổi là một **version mới** (`Bom`/`Routing` có `Version`, unique `(ItemId, Version)`). Version mới tạo ở trạng thái **Draft** (copy bản mới nhất), chỉ Draft được sửa; **Approve** khóa lại vĩnh viễn (`Domain/MasterData/Boms/Bom.cs`, `Routings/Routing.cs`).
2. Hiệu lực là khoảng **nửa mở `[EffectiveFrom, EffectiveTo)`**, UTC. `Approve(from, previous)` đóng version đang mở đúng tại `from` của version mới: tại mọi thời điểm có đúng một version hiệu lực, không chồng, không hở.
3. Hai **filtered unique index** giữ quy tắc ngay cả khi hai request đến cùng lúc: `UX_Boms_OneDraftPerItem` (`[Status]='Draft'`) và `UX_Boms_OneOpenApprovedPerItem` (`[Status]='Approved' AND [EffectiveTo] IS NULL`); routing có cặp tương tự. Approve v2 cập nhật v1 và v2 trong cùng `SaveChanges`; EF sắp các UPDATE theo khóa nên v1 được đóng trước khi v2 mở, index không bị vi phạm giữa chừng.
4. Kiểm tra cần DB (component còn active, không tạo vòng, Semi có BOM) nằm ở use case Approve; quy tắc trạng thái/hiệu lực là hàm thuần trong Domain, unit test không cần DB.
5. M2 sẽ **snapshot** version đang hiệu lực vào WO lúc release, nên Approve v3 không ảnh hưởng WO đang chạy.

**Phương án đã loại.**
- *Sửa đè + dựa vào AuditLog:* biết ai sửa, không biết định mức nào đúng lúc sản xuất; dựng lại trạng thái cũ từ log rất mong manh.
- *Chỉ lưu bản sao trong WO:* WO có lịch sử, nhưng master không có, không so sánh được v1/v2 và không có quy trình phê duyệt.
- *Hiệu lực theo ngày (`date`, giờ địa phương):* đơn giản hơn nhưng không đúng với ca đêm qua nửa đêm và lệch với quy ước UTC toàn dự án.

**Đánh đổi / giới hạn.** Thêm thao tác cho người dùng (New version → sửa → Approve thay vì sửa trực tiếp), đúng với yêu cầu kiểm soát thay đổi ở nhà máy. MVP chưa có phê duyệt hai người (người tạo khác người duyệt) và chưa có "alternate BOM" theo khách hàng; cả hai thêm được trên khung này.

---

## ADR-007: Nguồn dữ liệu OEE: Total Count từ máy, Good/NG từ scan theo serial

**Bối cảnh.** OEE = Availability × Performance × Quality (M5). Performance cần **tổng số sản phẩm máy làm ra**; Quality cần **số tốt / số hỏng**. Trong dự án có hai nguồn cùng nói về sản lượng: (1) bộ đếm của máy gửi qua MQTT (`counter`, kèm `goodDelta`/`ngDelta` do máy tự phân loại), (2) kết quả scan từng serial ở trạm (M3: OK/NG + mã lỗi, qua route check). Hai nguồn **không bao giờ khớp tuyệt đối**: máy đếm cả bo chạy thử, bo bị gạt ra trước khi scan; scan đi trễ hơn máy; một bo NG ở Test là do lỗi ở SMT chứ không phải ở máy test. Nếu mỗi báo cáo lấy một nguồn, OEE, FPY và báo cáo chất lượng sẽ tự mâu thuẫn.

**Quyết định.**

1. **Total Count = bộ đếm tích lũy của máy (MQTT).** `MachineEvents.Delta` tính bằng `CounterMath` (xử lý reset, mất message không mất số, ADR-008). Đây là nhịp thật của máy: Performance = Total Count × Ideal Cycle Time / Run Time.
2. **Good/NG = kết quả scan theo serial (M3).** Một nguồn sự thật cho chất lượng là **serial**: cùng dữ liệu cho Quality của OEE, FPY/RTY, truy xuất và Repair. NG có mã lỗi, có người/máy scan, có route check.
3. **`goodDelta`/`ngDelta` của máy chỉ để đối chiếu** (cảnh báo khi lệch nhiều: máy báo NG mà không có scan NG nào → trạm bỏ sót scan), **không bao giờ** vào công thức OEE.
4. Simulator đi đúng đường đó (M4 bước 9): máy **có trạm** (SMT, Test) chỉ tăng bộ đếm khi bo đã được MES chấp nhận scan (`AutoStationClient`), không tự bịa serial hay kết quả; máy không có trạm (lò reflow) chỉ đếm.

**Phương án đã loại.**
- *Tất cả từ máy (counter + good/ng của máy):* không có serial, không có mã lỗi, không truy xuất được; máy test NG nhưng lỗi do SMT thì quy sai cho trạm.
- *Tất cả từ scan:* trạm thủ công scan trễ hoặc gom, Performance theo phút sai; máy không có trạm scan (lò reflow) thì không có số.
- *Lấy trung bình/ưu tiên tùy báo cáo:* mỗi màn hình một con số, mất niềm tin vào hệ thống.

**Đánh đổi / giới hạn.** Quality của OEE và Performance tính từ hai nguồn khác nhau, nên Good có thể > Total trong một khoảng ngắn (scan đến trước count) → M5 phải tính trên khoảng thời gian đủ dài (ca) và kẹp tỉ lệ ≤ 100 %. Máy không có trạm scan chỉ có Total Count; Quality lấy theo serial ở trạm kiểm tra cuối của line.

---

## ADR-008: Dữ liệu máy đi qua MQTT, hợp đồng chung cho mọi giao thức

**Bối cảnh.** Máy nói nhiều giao thức (PLC Mitsubishi/Omron/Siemens, OPC UA, Modbus TCP, giao thức hãng). Mạng xưởng chập chờn; MES phải restart để cập nhật; một máy chết không được kéo theo cả hệ thống. Cần đường dữ liệu chịu được mất kết nối, không mất sản lượng, và MES không phải biết từng giao thức.

**Quyết định.**

1. Máy (hoặc **Edge Gateway**, M4b, cho máy OPC UA/Modbus) phát **MQTT** lên **Mosquitto** theo **một hợp đồng** (`MiniMes.Contracts/Machines`): topic Unified Namespace ISA-95 `minimes/{site}/{area}/{line}/{machine}/{kind}`, JSON có `msgId`, `ts` UTC, `source`, `quality`.
2. **Bộ đếm tích lũy**, không gửi "+1": mất message chỉ mất độ mịn, message kế tiếp mang tổng mới. **QoS 1** cho state/count/alarm, QoS 0 cho heartbeat; chống trùng bằng cache `msgId` + unique index (QoS 1 = ít nhất một lần).
3. `state` **retained**: ingest khởi động lại nhận ngay trạng thái hiện tại. **LWT** `status = offline` + heartbeat 30 giây để phân biệt **Offline** (MES không nghe thấy máy) với **Down** (máy báo lỗi).
4. Ingest là `BackgroundService` trong API: đường nóng (RAM, SignalR, < 2 giây) tách đường lạnh (`Channel` + batch writer vào SQL Server).

**Phương án đã loại.**
- *MES gọi thẳng PLC (OPC UA client / Modbus master trong MES):* mỗi giao thức một driver trong MES; MES restart là mất kết nối tất cả máy; không có bộ đệm khi mạng đứt.
- *Máy tự gọi HTTP POST vào MES:* MES tắt là mất message; máy nào cũng phải biết địa chỉ và token của MES; không có retained/LWT.
- *Sparkplug B:* chuẩn công nghiệp trên MQTT nhưng payload protobuf và quy ước birth/death khó đọc khi học; giữ được hợp đồng hiện tại và thêm lớp chuyển đổi ở gateway khi cần.

**Đánh đổi / giới hạn.** Thêm một thành phần phải vận hành (broker) và một điểm có thể hỏng (gateway). Dev chạy `allow_anonymous`; production cần user/password hoặc chứng chỉ + TLS và ACL theo topic. Một instance ingest (`ClientId` cố định); scale ngang cần shared subscription (MQTT 5) và chuyển runtime store sang Redis.

---

## ADR-009: Edge Gateway tự viết: OPC UA subscription, Modbus polling, outbox SQLite luôn bật

**Bối cảnh.** Phần lớn máy trong nhà máy điện tử không nói MQTT: lò reflow đời mới có **OPC UA**, máy dán keo đời cũ chỉ có **Modbus TCP**. MES (Level 3) không nên nói chuyện trực tiếp với từng PLC (Level 1) (ADR-008). Mạng giữa xưởng và phòng server có thể đứt vài phút; gateway chạy trên một PC công nghiệp có thể bị restart.

**Quyết định.** Một chương trình riêng `MiniMes.EdgeGateway` (.NET Worker, chạy như Windows Service hoặc container), chỉ chung `MiniMes.Contracts` với MES:

```
PLC ──OPC UA subscription / Modbus FC03 polling──▶ driver ─▶ Channel ─▶ Normalizer ─▶ outbox SQLite ─▶ forwarder ─QoS 1─▶ Mosquitto ─▶ ingest M4
                                                                   (msgId = hash nội dung)  (xóa sau PUBACK, tuần tự)
```

1. **Vì sao có gateway**: dịch giao thức, chuẩn hóa tên/ý nghĩa (thanh ghi `40001-40002` CDAB hay `ns=2;s=Reflow.Counter` đều thành `count` của máy trong UNS), đệm khi mạng đứt. Cấu hình tag ở file `gateway.tags.json` của gateway, không ở DB MES: gateway phải chạy khi MES hỏng. Thêm giao thức (S7, MC Protocol) = thêm một `IDeviceDriver`.
2. **Subscription cho OPC UA, polling cho Modbus.** OPC UA: một session, một subscription, deadband ở server (giảm 60–90 % message), `SourceTimestamp` làm `ts`. Modbus không đẩy được: đọc **một khối** thanh ghi mỗi giây, giải mã bằng `RegisterDecoder` (4 thứ tự word, unit test); bộ đếm tích lũy nên bỏ lỡ một lần hỏi không mất sản lượng.
3. **Outbox luôn bật.** Mọi message dữ liệu ghi SQLite (WAL) trước, forwarder gửi **tuần tự** theo `Id`, **xóa sau PUBACK**. Một đường duy nhất nên thứ tự luôn đúng; restart gateway không mất hàng đợi; có trần (500.000 dòng / 7 ngày, bỏ bản cũ nhất, log Error). Heartbeat và status gateway thì gửi thẳng (bản cũ gửi bù là thông tin sai).
4. **`msgId` cố định theo nội dung** (SHA-256 của máy, tag, ts, giá trị): OPC UA phát lại sau reconnect hay gateway gửi lại sau crash đều ra cùng `msgId`, MES khử trùng. Ít nhất một lần trên đường truyền + idempotent ở MES = đúng một lần trong DB.
5. **Offline ≠ Down.** Mất PLC: gateway publish `status = offline` (retained) cho máy, ngừng heartbeat, **không** đoán `Down` (MES Offline trong ≤ 5 s, không Andon). Gateway chết: **LWT** trên `minimes/{site}/_sys/gateway/{id}/status`, thẻ gateway đỏ ngay, máy của nó Offline sau 30 s (watchdog M4). Ba sự cố, ba cách hiển thị, ba người xử lý.
6. **Phía MES chỉ sửa hai chỗ**: nhánh `_sys/gateway` đầu ingest (thẻ gateway), và luật thời gian: message từ gateway có `ts` quá khứ ≤ 7 ngày là **gửi muộn** từ outbox, giữ `ts` (không coi là PLC lệch giờ), để sản lượng theo giờ vẫn đúng sau khi mất mạng.

**Phương án đã loại.**
- *MES làm OPC UA client / Modbus master:* như ADR-008; thêm nữa mạng IT phải mở đường vào mạng OT.
- *Kepware, Ignition Edge, Node-RED, United Manufacturing Hub:* đúng lựa chọn khi có ngân sách và nhiều loại PLC; ở dự án học, tự viết để hiểu cơ chế (subscription, giải mã thanh ghi, store-and-forward). MES chỉ cần đúng hợp đồng MQTT nên đổi sang sản phẩm thương mại không phải sửa MES.
- *Polling OPC UA bằng `Read` mỗi giây:* tốn tải, bỏ lỡ thay đổi ngắn, bỏ phí deadband ở server.
- *Hàng đợi trong RAM / chỉ ghi đĩa khi mất mạng:* RAM mất khi restart; hai đường gửi dễ sai thứ tự.
- *`Guid.NewGuid()` làm msgId:* bản phát lại thành message "mới", đếm trùng.

**Đánh đổi / giới hạn.** Một lần ghi đĩa mỗi message (không đáng kể với WAL và ghi theo lô). Bảo mật OPC UA ở dev là `None` + auto-accept chứng chỉ; production phải `SignAndEncrypt` (Basic256Sha256) và trust chứng chỉ thủ công, theo dõi ngày hết hạn. Modbus không có bảo mật: cô lập mạng OT, gateway chỉ đọc. Một gateway cho một nhóm máy; hai gateway đọc cùng một máy sẽ gửi đôi. Hàng đợi tràn mất chi tiết của khoảng bị bỏ (tổng sản lượng vẫn đúng nhờ bộ đếm tích lũy).
