# M4 · Bước 7 — Andon backend: kiến thức cần nắm

> Tài liệu học cho bước 7 của [module-04](../../spec/module-04-machine-connectivity.md) (spec mục 1.2g, 1.3, 1.4, 2.9).
> Đọc theo thứ tự: phần A (nghiệp vụ) → B (thiết kế) → C (code) → D (test) → E (câu hỏi phỏng vấn).

---

## A. Nghiệp vụ: Andon là gì, vì sao nhà máy cần nó

### A1. Nguồn gốc

- **Andon** (行灯, "đèn lồng giấy") đến từ **Toyota Production System (TPS)**, gắn với nguyên tắc **jidoka** (自働化):
  khi thấy bất thường thì *dừng lại và gọi người*, không để sản phẩm lỗi chảy tiếp.
- Cái đèn không phải là điểm chính. Điểm chính là **quy ước**:
  1. **Ai thấy bất thường cũng được gọi** (operator kéo dây, bấm nút).
  2. **Phải có người chịu trách nhiệm đáp lại** trong thời gian quy định.
  3. **Phải ghi lại vì sao dừng**, để còn cải tiến.

### A2. Vòng đời một Andon

```
            Operator bấm / hệ thống tự raise
                        │
                        ▼
                    ┌────────┐  Acknowledge (Supervisor/Maintenance: "tôi nhận, tôi đang tới")
                    │ Raised │──────────────────────────┐
                    └────────┘                          ▼
                     │   │                     ┌──────────────┐
      Resolve luôn   │   │ Cancel (báo nhầm)   │ Acknowledged │
      (sửa xong ngay)│   │                     └──────────────┘
                     │   ▼                       │          │
                     │ ┌───────────┐  Cancel     │          │ Resolve + lý do dừng
                     │ │ Cancelled │◀────────────┘          │
                     │ └───────────┘                        ▼
                     └─────────────────────────────▶ ┌──────────┐
                                                     │ Resolved │
                                                     └──────────┘
```

| Thời điểm | Cột | Ý nghĩa KPI |
| --- | --- | --- |
| `RaisedAt` | lúc gọi | |
| `AckAt` | lúc có người nhận | **Reaction time** = `AckAt − RaisedAt` (bao lâu mới có người tới) |
| `ResolvedAt` | lúc xử lý xong | **Repair time** = `ResolvedAt − AckAt` (sửa mất bao lâu) |
| `DowntimeReasonId` | lý do dừng | Dữ liệu cho **Pareto lý do dừng** ở M5 (OEE & Downtime) |

### A3. Các quy tắc nghiệp vụ và lý do tồn tại (spec 1.4, 2.9)

| Quy tắc | Vì sao |
| --- | --- |
| Resolve **bắt buộc** chọn lý do dừng | Không có lý do thì không làm được Pareto, không biết cải tiến cái gì |
| Resolve thẳng từ `Raised` được phép, hệ thống tự điền `AckBy/AckAt` | Ngoài đời người sửa thường tới và sửa luôn; nếu để `AckAt = null` thì báo cáo reaction time bị thủng |
| Máy chạy lại **không** tự đóng Andon | Máy chạy lại nhưng chưa ai nói *vì sao* nó dừng → mất dữ liệu lý do dừng |
| Mỗi (máy, loại) tối đa **một** Andon mở | Auto-raise + operator bấm tay cho cùng một sự cố không được thành 2 bản (Andon spam) |
| Auto-raise chỉ khi máy `Down` liên tục ≥ X giây (mặc định 60) | Dừng ngắn 5 giây cũng raise thì người ta quen, không ai nhìn nữa: **Andon fatigue** |
| `Offline` **không** sinh Andon | Offline là mất kết nối (switch rút điện…), không phải máy hỏng (spec 1.3, 14:10) |
| Bản tự động lưu `RaisedBy = "system"` | Về sau phân biệt được hệ thống gọi hay người gọi |
| Operator raise; Supervisor/Maintenance/Admin acknowledge, resolve, cancel | Người thấy vấn đề ≠ người chịu trách nhiệm xử lý |
| `Cancelled` cho báo nhầm, không cần lý do | Báo nhầm không được lọt vào Pareto lý do dừng |

### A4. Thuật ngữ (có tiếng Nhật vì hay gặp ở nhà máy Nhật tại VN)

| Thuật ngữ | Nghĩa | Tiếng Nhật |
| --- | --- | --- |
| Andon | Hệ thống gọi người khi có bất thường | アンドン (行灯) |
| Acknowledge | Xác nhận nhận việc | 確認 / 受付 |
| Resolve | Xử lý xong, kèm lý do | 復旧 / 対応完了 |
| Jidoka | Dừng khi có bất thường | 自働化 |
| Downtime reason | Lý do dừng máy (master data M1) | 停止理由 |
| Andon fatigue | Gọi quá nhiều nên không ai phản ứng nữa | |

---

## B. Thiết kế: bước 7 đã làm những gì

```
 Kiosk / Dashboard ──HTTP──▶ AndonController ──ISender──▶ Raise/Acknowledge/Resolve/Cancel handler
                                                            │  1. load AndonCall
                                                            │  2. so RowVersion
                                                            │  3. gọi method Domain (luật nằm ở đây)
                                                            │  4. SaveChanges (unique index + RowVersion)
                                                            │  5. IRealtimeNotifier.AndonChangedAsync
                                                            ▼
                                                SignalR group "line:L1" ──▶ mọi dashboard của line

 MachineRuntimeStore (RAM) ◀── AndonWatcherService (mỗi 5 giây)
                                  máy Down ≥ X giây, chưa có Andon trong đợt Down này
                                  → ISender.Send(RaiseAndonCommand(Auto: true))   ← cùng use case với nút bấm
```

| File | Vai trò |
| --- | --- |
| [Domain/Andon/AndonCall.cs](../../src/MiniMes.Domain/Andon/AndonCall.cs) | State machine: `Acknowledge`, `Resolve`, `Cancel` ném `DomainException` khi chuyển sai |
| [Contracts/Andon/AndonContracts.cs](../../src/MiniMes.Contracts/Andon/AndonContracts.cs) | `AndonDto`, request, `RaiseAndonResult(Andon, Created)` |
| [Application/Andon/AndonUseCases.cs](../../src/MiniMes.Application/Andon/AndonUseCases.cs) | Command/handler/validator MediatR + query danh sách |
| [Application/Andon/AndonAutoRaise.cs](../../src/MiniMes.Application/Andon/AndonAutoRaise.cs) | Hàm thuần: máy nào đến hạn auto-raise |
| [Infrastructure/Machines/AndonWatcherService.cs](../../src/MiniMes.Infrastructure/Machines/AndonWatcherService.cs) | `BackgroundService` 5 giây, cấu hình `Andon:AutoRaiseAfterSeconds` |
| [Api/Controllers/AndonController.cs](../../src/MiniMes.Api/Controllers/AndonController.cs) | Controller mỏng + role |
| `IMachineClient.AndonChanged`, `IRealtimeNotifier.AndonChangedAsync` | Push realtime ngay, không throttle |

API:

| Method | Route | Role | Kết quả |
| --- | --- | --- | --- |
| GET | `/api/andon?status=open&line=L1` | mọi user đã đăng nhập | danh sách (mới nhất trước, tối đa 200) |
| GET | `/api/andon/{id}` | mọi user | 1 Andon |
| POST | `/api/andon` `{machineId, type}` | Operator, Admin | **201** tạo mới / **200** trả bản đang mở |
| POST | `/api/andon/{id}/acknowledge` `{rowVersion}` | Supervisor, Maintenance, Admin | 200 / **409** người khác đã nhận / 422 đã đóng |
| POST | `/api/andon/{id}/resolve` `{downtimeReasonId, note, rowVersion}` | như trên | 200 / **400** thiếu lý do / 409 / 422 |
| POST | `/api/andon/{id}/cancel` `{rowVersion}` | như trên | 200 / 409 / 422 |

---

## C. Kiến thức kỹ thuật

### C1. Rich domain model và state machine

**Là gì.** Entity tự giữ luật của nó: thuộc tính `private set`, chỉ đổi được qua method có kiểm tra
(`call.Acknowledge(by, now)`), thay vì `call.Status = 1` ở bất cứ đâu.

**Vì sao.** Luật nằm một chỗ, unit test được không cần DB, không ai "lỡ tay" chuyển `Resolved → Acknowledged`.
Cùng kiểu với `WorkOrder` ở M2.

```csharp
public void Acknowledge(string by, DateTime nowUtc)
{
    if (Status != AndonStatus.Raised)
        throw new DomainException($"Andon {Id} is {Status}; only a Raised call can be acknowledged.");
    Status = AndonStatus.Acknowledged; AckBy = by; AckAt = nowUtc;
}
```

- `DomainException` → `GlobalExceptionHandler` → **422** (vi phạm quy tắc nghiệp vụ).
- Thời gian truyền vào (`nowUtc`), Domain không gọi `DateTime.UtcNow` → test chủ động được thời gian.

### C2. Chống trùng bằng **filtered unique index**, không chỉ bằng `if`

```sql
CREATE UNIQUE INDEX UX_AndonCalls_OpenPerMachineType
    ON AndonCalls (MachineId, Type) WHERE Status <= 1;   -- Raised=0, Acknowledged=1
```

- **Filtered index** chỉ chứa các dòng thỏa `WHERE`. Andon đã đóng (`Status` 2, 3) không nằm trong index, nên một máy
  có thể có nhiều Andon đã đóng nhưng chỉ **một** Andon mở mỗi loại.
- **Vì sao `if` là không đủ (race condition):**

```
 Request A (auto-raise)            Request B (operator bấm)
 SELECT open → không có             SELECT open → không có     ← cả hai cùng qua được if
 INSERT ✔                           INSERT ✘ 2601 duplicate key ← DB chặn
```

- Handler xử lý bên thua **thành kết quả bình thường**, không phải lỗi 500:

```csharp
try { await db.SaveChangesAsync(ct); }
catch (ConflictException)                      // AppDbContext đã dịch SqlException 2601/2627 → ConflictException
{
    db.ChangeTracker.Clear();                  // quên entity INSERT hỏng
    var winner = await db.FindOpenAsync(machine.Id, type, ct);
    if (winner is null) throw;                 // không phải index này → để 409 đi tiếp
    return new RaiseAndonResult(await db.GetAndonDtoAsync(winner.Id, ct), Created: false);
}
```

- `if` vẫn giữ lại: nó trả lời trường hợp thường gặp (bấm sau khi auto-raise đã có) mà không cần gây lỗi SQL.
  Index là **lưới an toàn** cho trường hợp đua nhau.

### C3. Optimistic concurrency với `RowVersion` (hai supervisor cùng bấm Acknowledge)

- `rowversion` (SQL Server) tự đổi mỗi lần dòng bị UPDATE. EF cấu hình `.IsRowVersion()`, nên lệnh UPDATE có
  `WHERE Id = @id AND RowVersion = @original`. Khớp 0 dòng → `DbUpdateConcurrencyException` → `ConflictException` → **409**.
- Client gửi lại `RowVersion` nó **đã thấy trên màn hình** (base64 trong `AndonDto`).
- Handler so sánh **trước** khi gọi Domain:

```csharp
if (!call.RowVersion.AsSpan().SequenceEqual(seen))
    throw new ConflictException("... was changed by someone else ...");   // 409
db.Entry(call).Property(a => a.RowVersion).OriginalValue = seen;           // cho lệnh UPDATE
```

Hai tình huống, cả hai đều ra 409:

| Thứ tự | Điều gì xảy ra |
| --- | --- |
| B đọc **sau** khi A commit | `RowVersion` trong DB đã khác bản B thấy → 409 ngay ở bước so sánh |
| A và B cùng đọc **trước** khi ai lưu | Cả hai qua bước so sánh; UPDATE của bên sau khớp 0 dòng → 409 |

**Vì sao phải so sánh trước?** Nếu không, B đọc được bản đã `Acknowledged`, gọi `Acknowledge()` → Domain ném
"already acknowledged" → **422**. Với người dùng, "người khác vừa nhận rồi" là **xung đột** (409, tải lại),
không phải "bạn làm sai luật" (422). Còn nếu user mở màn hình đúng bản mới nhất mà Andon đã đóng → 422 là đúng.

> Phân biệt trong phỏng vấn: **pessimistic** lock = khóa dòng khi đọc (người khác phải chờ);
> **optimistic** = không khóa, lúc ghi mới kiểm tra "có ai sửa chưa". Optimistic hợp với web/WPF vì người dùng
> có thể mở màn hình hàng phút mà không giữ lock.

### C4. CQRS nhẹ với MediatR + FluentValidation

- Mỗi use case là một `record` command/query + một handler. Controller chỉ `sender.Send(...)`.
- `ValidationBehavior` (pipeline MediatR) chạy validator trước handler → lỗi → **400** ProblemDetails.
- Ví dụ: `ResolveAndonValidator` bắt `DowntimeReasonId` `NotNull().GreaterThan(0)`. Request dùng `int?` để
  "không gửi" khác "gửi 0".
- Lý do dừng có trong DB không, còn active không → kiểm trong handler (cần DB), ném `ValidationException` → 400.

**Phân lớp lỗi của dự án (CLAUDE.md mục 5):**

| Mã | Khi nào | Ví dụ ở bước này |
| --- | --- | --- |
| 400 | Dữ liệu vào sai | thiếu `downtimeReasonId`, `type = "Fire"`, `rowVersion` không phải base64 |
| 401 | Chưa đăng nhập | gọi `/api/andon` không token |
| 403 | Sai role | Operator gọi `acknowledge` |
| 404 | Không tìm thấy | máy / Andon không tồn tại |
| 409 | Xung đột đồng thời | hai supervisor cùng ack |
| 422 | Vi phạm luật nghiệp vụ | ack một Andon đã Resolved |

### C5. Phân quyền bằng `[Authorize(Roles = ...)]`

```csharp
private const string Callers    = $"{nameof(Role.Operator)},{nameof(Role.Admin)}";
private const string Responders = $"{nameof(Role.Supervisor)},{nameof(Role.Maintenance)},{nameof(Role.Admin)}";
```

- Chuỗi roles phân tách bằng dấu phẩy = **OR**. Dùng `nameof` để đổi tên enum thì compile báo lỗi.
- Cờ `Auto` của `RaiseAndonCommand` **không** có trong request HTTP: controller luôn gửi `Auto = false`, nên client
  không thể giả danh `"system"`. Đây là một dạng chống **over-posting / mass assignment**.

### C6. `BackgroundService` + `PeriodicTimer` + gọi lại use case

```csharp
using var timer = new PeriodicTimer(Every, clock);          // clock = TimeProvider → test được
while (await timer.WaitForNextTickAsync(stoppingToken))
{
    try { await TickAsync(stoppingToken); }
    catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "..."); }
}
```

- **Bọc try/catch thân vòng lặp**: exception thoát khỏi `ExecuteAsync` sẽ làm host dừng (mặc định .NET 6+).
- `BackgroundService` là **Singleton**, còn `DbContext`/`ISender` là **Scoped** → phải tạo scope mỗi lần:
  `using var scope = scopes.CreateScope();`. Inject thẳng `DbContext` vào singleton là lỗi kinh điển
  (captive dependency).
- Watcher **không tự INSERT** mà gửi `RaiseAndonCommand(Auto: true)`: cùng một đường đi với nút bấm (validator, chống
  trùng, push SignalR, audit), không có hai bản logic.
- Đọc `MachineRuntimeStore` (RAM) trước; chỉ khi có máy đến hạn mới chạm DB. 5 giây/lần × N máy mà query DB mỗi lần
  là tốn vô ích.
- **Một Andon tự động cho mỗi đợt Down**: watcher bỏ qua nếu đã có Andon loại Machine của máy đó đang mở
  **hoặc** được raise kể từ lúc đợt Down bắt đầu (`RaisedAt >= DownSince`). Thiếu điều kiện thứ hai thì
  supervisor resolve khi máy vẫn Down sẽ nhận ngay Andon mới 5 giây sau; và sau khi API restart, watcher dựa vào DB
  nên không raise lại.
- Luật "máy nào đến hạn" tách thành hàm thuần `AndonAutoRaise.Due(snapshots, now, X)` → unit test bằng `[Theory]`.

### C7. SignalR: push ngay sau khi lưu

- Thứ tự: **ghi DB rồi mới push**. Push trước mà lưu lỗi thì dashboard hiện thứ không tồn tại.
- Gửi tới group `line:{code}` của máy, không gửi mọi client.
- **Không throttle** Andon (khác số đếm): hiếm và quan trọng, cần < 2 giây (DoD).
- DTO mang **trạng thái đầy đủ** (status, ackBy, rowVersion…), không phải "đã ack": client chỉ cần thay dòng cũ.
- Push lỗi bị log và nuốt trong `SignalRRealtimeNotifier`: dashboard là "view", không được làm hỏng use case
  (dữ liệu đã nằm trong DB; client tự sửa bằng snapshot khi reconnect).

### C8. EF Core: projection và lỗi dịch truy vấn

```csharp
from a in calls join m in db.Machines ... join l in db.Lines ...
where line == null || l.Code == line          // lọc TRƯỚC khi select
orderby a.RaisedAt descending
select new AndonDto(...)                       // project cuối cùng
```

- EF **không dịch được** `.Where(d => d.Line == ...)` trên một record tạo bằng constructor (`new AndonDto(...)`):
  nó không biết tham số constructor nào ứng với property `Line`. Quy tắc: lọc/sắp xếp trên entity, project sau cùng.
- `join ... into reasons from r in reasons.DefaultIfEmpty()` = **LEFT JOIN** (Andon chưa resolve không có lý do).
- `AsNoTracking()` cho truy vấn chỉ đọc.

---

## D. Test đã viết và cách nghĩ khi test

| Loại | File | Kiểm tra gì |
| --- | --- | --- |
| Unit | [AndonCallTests.cs](../../tests/MiniMes.UnitTests/Andon/AndonCallTests.cs) | **Ma trận 4 trạng thái × 3 hành động** (12 ô, viết tay từ spec, không suy ra từ code); ack hai lần giữ người nhận đầu; resolve thiếu lý do; resolve thẳng từ Raised điền AckAt; cancel |
| Unit | [AndonAutoRaiseTests.cs](../../tests/MiniMes.UnitTests/Andon/AndonAutoRaiseTests.cs) | Đúng 60 giây là đến hạn, 59 giây chưa; Offline/PlannedStop/Setup/Idle/Running không bao giờ |
| Integration | [AndonTests.cs](../../tests/MiniMes.IntegrationTests/Machines/AndonTests.cs) | Luồng raise → ack → resolve qua HTTP; raise lại trả bản đang mở; **2 raise song song** → 1 bản (5 vòng); **2 ack song song** → một 200, một 409 (5 vòng); 403 theo role; 400 thiếu/sai lý do; 404; 422; watcher: đúng 1 Andon `system`, không raise lại trong cùng đợt Down |
| Integration | [RealtimeTests.cs](../../tests/MiniMes.IntegrationTests/Machines/RealtimeTests.cs) | `AndonChanged` tới group L1 (Raised rồi Cancelled), group L2 không nhận |

Ý chính:

- **Test race condition**: hai `HttpClient` chờ cùng một `TaskCompletionSource` rồi bắn cùng lúc, chạy nhiều vòng;
  khẳng định **bất biến** (luôn đúng 1 bản ghi; một 200 + một 409) chứ không khẳng định thứ tự.
- **Mỗi test một máy mới** (tạo qua API): test dùng chung DB nhưng không giẫm chân nhau vì unique index theo máy.
- **Không mock DbContext**: unique index, RowVersion chỉ có thật trên SQL Server (Testcontainers).
- Bài học khi viết test watcher: lần đầu test đỏ vì kịch bản test sai (đợt Down "mới" bắt đầu *trước* Andon cũ,
  nên đúng ra vẫn là đợt cũ). Khi test đỏ, kiểm tra cả test lẫn code; đừng sửa code cho xanh.

---

### Chạy thật (API + simulator, 27/10/2026)

| Giờ (UTC) | Việc | Kết quả |
| --- | --- | --- |
| 14:38:28 | `down L1-SMT-01 300` | máy `Down` |
| 14:39:33 | watcher | Andon #1 `Raised`, `RaisedBy = system` (60 giây + nhịp 5 giây) |
| | operator `POST /api/andon` | **200**, cùng id #1, `created = false` |
| | operator `acknowledge` | **403** |
| | `resolve` thiếu lý do | **400** |
| | 2 `acknowledge` song song (curl) | **200** và **409** |
| 14:41:18 | `up L1-SMT-01` | máy `Running`, Andon **vẫn `Acknowledged`** |
| | `resolve` với `MCH-FAULT` | **200**, `Resolved`; DB đúng 1 Andon |

## E. Câu hỏi phỏng vấn nên tự trả lời được

1. Andon là gì? Vì sao Resolve bắt buộc lý do dừng? Dữ liệu đó dùng vào đâu (Pareto, OEE Availability ở M5)?
2. Tại sao máy chạy lại không tự đóng Andon?
3. Andon fatigue là gì, hệ thống của bạn chống nó thế nào? (ngưỡng X, một Andon mở mỗi máy/loại, một auto mỗi đợt Down)
4. Auto-raise và operator bấm cùng một giây: làm sao không ra hai bản ghi? Vì sao `if (exists)` không đủ?
5. Hai supervisor cùng bấm "Nhận": chuyện gì xảy ra? Optimistic khác pessimistic concurrency thế nào?
6. 409 khác 422 thế nào trong API của bạn?
7. Tại sao `BackgroundService` không inject thẳng `DbContext`? Exception trong `ExecuteAsync` gây ra gì?
8. Tại sao ghi DB rồi mới push SignalR? Push lỗi thì sao?
9. Làm sao test được "sau 60 giây"? (hàm thuần nhận `now`, `TimeProvider`/`FakeTimeProvider`)
10. Reaction time và repair time tính từ cột nào?

## F. Đọc thêm

- Spec: module-04 mục 1.2g (nguồn gốc Andon), 1.3 (một ngày ở xưởng), 1.4, 2.9; Module 2 mục optimistic concurrency.
- Microsoft Docs: *Handling Concurrency Conflicts* (EF Core), *Filtered indexes* (SQL Server),
  *Background tasks with hosted services in ASP.NET Core*, *Role-based authorization*.
- Bước tiếp theo **7b**: leo thang Andon theo thời gian (`AndonEscalationRule`, `Escalate`, group `role:{role}`).
