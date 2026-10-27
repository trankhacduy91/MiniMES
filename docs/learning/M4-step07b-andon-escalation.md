# M4 · Bước 7b — [v2] Andon leo thang (escalation): kiến thức cần nắm

> Tài liệu học cho bước 7b của [module-04](../../spec/module-04-machine-connectivity.md) (spec mục 1.4, 1.5, **2.9b**;
> spec-v2 mục M4). Nên đọc [bước 7](M4-step07-andon-backend.md) trước: bước này dựng trên `AndonCall`, `RowVersion`
> và `AndonWatcherService` của bước 7.

---

## A. Nghiệp vụ

### A1. Vấn đề: "Andon không có chủ là Andon vô dụng"

Operator đã bấm Andon, đèn đỏ đã sáng, nhưng supervisor đang họp và bảo trì đang ở line khác. Không ai tới, máy dừng
cả tiếng, cuối ca không ai chịu trách nhiệm. Bấm nút chỉ là nửa đầu của quy ước Andon. Nửa sau là **có người đáp lại
trong thời gian quy định**, và nếu không ai đáp thì **báo lên cấp cao hơn**.

### A2. Leo thang theo thời gian

Nhà máy Nhật ở VN thường quy định thời gian phản ứng theo cấp, ví dụ 3 phút chưa ai nhận thì báo trưởng xưởng,
10 phút thì báo giám đốc sản xuất. Dự án cấu hình bằng bảng `AndonEscalationRule` (seed theo spec 2.9b):

| AndonType | Level | AfterMinutes (tính từ `RaisedAt`) | NotifyRoles | Ngoài đời |
| --- | --- | --- | --- | --- |
| Machine | 0 | 0 | Supervisor, Maintenance | Lúc raise: dashboard của line (hành vi bước 7) |
| Machine | 1 | 3 | Admin | Trưởng xưởng |
| Machine | 2 | 10 | Admin + log mức **Error** | Giám đốc sản xuất (email/Teams/Zalo) |
| Quality | 1 | 5 | QC | Trưởng QC |
| Material, Other | (không có quy tắc) | | | Không leo thang |

Quy tắc nghiệp vụ (spec 1.4):

- Chỉ Andon **`Raised`** mới leo thang. **Acknowledge là dừng leo thang**: đã có người nhận việc thì không cần báo sếp.
- **Mỗi cấp chỉ báo một lần**: watcher chạy 5 giây/lần, không được gửi lại thông báo cấp 1 mỗi 5 giây.
- **Giữ lại `EscalationLevel` cuối cùng** kể cả sau khi resolve, để làm KPI "tháng này bao nhiêu Andon phải leo thang tới
  cấp 2". Đây là KPI mà quản lý rất quan tâm, vì nó đo **kỷ luật phản ứng** của xưởng chứ không đo máy.

### A3. Báo cáo (`GET /api/andon/stats?from=&to=`)

| Chỉ số | Cách tính | Ý nghĩa |
| --- | --- | --- |
| `Total` | số Andon có `RaisedAt` trong `[from, to)`, **bỏ `Cancelled`** | báo nhầm không phải sự cố thật |
| `ByLevel` | đếm theo `EscalationLevel` cuối | level 2 nhiều = phản ứng chậm, cần xem lại phân công |
| `AvgReactionSeconds` | trung bình `AckAt − RaisedAt` của các Andon đã có người nhận | "trung bình bao lâu mới có người tới" |

> Hai lựa chọn này (bỏ Cancelled; Level 0 không gửi thông báo theo role) do bạn chốt trước khi code,
> vì spec chưa nói rõ (CLAUDE.md mục 2: spec thiếu thì hỏi).

---

## B. Thiết kế

```
AndonWatcherService (mỗi 5 giây, 1 scope)
  ├─ auto-raise (bước 7)
  └─ ISender.Send(EscalateAndonsCommand)
        │ 1. đọc Andon Status = Raised (tracked) + toàn bộ AndonEscalationRule
        │ 2. với mỗi Andon: due = AndonEscalation.DueLevel(rules, type, now − RaisedAt)   ← hàm thuần Domain
        │                   call.Escalate(due, now)  → false: không làm gì
        │ 3. SaveChanges TỪNG Andon (RowVersion; thua thì bỏ qua Andon đó)
        │ 4. log Warning (level 1) / Error (level ≥ 2)
        │ 5. push AndonEscalated → group role:{NotifyRole}       (người cần biết)
        │    push AndonChanged   → group line:{code}             (dashboard hiện level)
        ▼
MachineHub.OnConnectedAsync: server tự cho connection vào role:{role} theo claim "role" trong JWT
```

| File | Thay đổi |
| --- | --- |
| [Domain/Andon/AndonEscalationRule.cs](../../src/MiniMes.Domain/Andon/AndonEscalationRule.cs) | tách ra file riêng + `AndonEscalation.DueLevel` (hàm thuần) |
| [Domain/Andon/AndonCall.cs](../../src/MiniMes.Domain/Andon/AndonCall.cs) | `bool Escalate(int toLevel, DateTime nowUtc)` |
| [Application/Andon/AndonEscalations.cs](../../src/MiniMes.Application/Andon/AndonEscalations.cs) | `EscalateAndonsCommand`, `GetAndonStatsQuery` |
| [Infrastructure/Machines/AndonWatcherService.cs](../../src/MiniMes.Infrastructure/Machines/AndonWatcherService.cs) | thêm bước leo thang vào mỗi tick |
| [Infrastructure/Realtime/MachineHub.cs](../../src/MiniMes.Infrastructure/Realtime/MachineHub.cs) | join `role:{role}` lúc kết nối |
| `IMachineClient.AndonEscalated`, `IRealtimeNotifier.AndonEscalatedAsync` | event mới, gửi tới nhiều group role |
| `MasterDataSeeder` | seed 4 quy tắc |
| [Api/Controllers/AndonController.cs](../../src/MiniMes.Api/Controllers/AndonController.cs) | `GET /api/andon/stats` (Supervisor, Maintenance, Admin) |

Không cần migration: bảng `AndonEscalationRules` và cột `EscalationLevel`, `LastEscalatedAt` đã tạo ở Bước 1.

---

## C. Kiến thức kỹ thuật

### C1. Tách "luật" khỏi "thời gian" bằng hàm thuần

```csharp
public static int DueLevel(IEnumerable<AndonEscalationRule> rules, AndonType type, TimeSpan elapsed)
    => rules.Where(r => r.AndonType == type && TimeSpan.FromMinutes(r.AfterMinutes) <= elapsed)
            .Select(r => r.Level).DefaultIfEmpty(0).Max();
```

- Hàm không biết "bây giờ là mấy giờ", chỉ nhận `elapsed`. Câu hỏi "sau 3 phút thì sao?" trở thành gọi
  `DueLevel(rules, Machine, 3 phút)`, test trong 1 ms.
- `DefaultIfEmpty(0).Max()`: không có quy tắc nào khớp (Material, hoặc mới 1 phút) thì level 0. Thiếu
  `DefaultIfEmpty`, `Max()` trên dãy rỗng sẽ ném `InvalidOperationException`.
- Đặt ở **Domain** vì đây là quy tắc nghiệp vụ (CLAUDE.md mục 5).

### C2. Idempotent: gọi nhiều lần, kết quả như gọi một lần

```csharp
public bool Escalate(int toLevel, DateTime nowUtc)
{
    if (Status != AndonStatus.Raised || toLevel <= EscalationLevel) return false;
    EscalationLevel = toLevel; LastEscalatedAt = nowUtc;
    return true;
}
```

- **Idempotent** = thao tác lặp lại không gây thêm tác dụng. Watcher chạy 5 giây/lần nên phút 3:00, 3:05, 3:10… đều
  tính ra level 1. Chỉ lần đầu trả `true`; các lần sau trả `false` nên không gửi thông báo lại.
- Trả **`bool`** chứ không ném exception: "đã ở level này rồi" là chuyện bình thường, không phải lỗi.
  (Khác với `Acknowledge` hai lần ở bước 7: đó là người dùng làm sai, nên ném `DomainException` → 422.)
- **Chỉ tăng**: `toLevel <= EscalationLevel` thì bỏ qua. Nhờ vậy nếu API tắt 15 phút rồi bật lại, watcher nhảy
  thẳng lên level 2 (không phát lại level 1). Level lưu trong DB nên restart không làm mất, cũng không làm lặp.

### C3. Trạng thái nằm trong DB, không nằm trong RAM

Không có `Dictionary<long, int> lastNotifiedLevel` trong watcher. Mọi thứ tính lại từ `RaisedAt` và `EscalationLevel`
trong DB. Đây là thiết kế **stateless worker**:

- API restart: không mất, không lặp (spec 2.9b, bẫy 3).
- Sau này chạy 2 instance API: cái nào lưu trước thì thắng (RowVersion), cái kia nhận 409 và bỏ qua.

### C4. Đua nhau giữa watcher và người bấm Acknowledge

```
 Watcher                                  Supervisor
 đọc Andon #5 (Raised, RowVersion=A)
                                          POST acknowledge (RowVersion=A) → UPDATE … WHERE RowVersion=A ✔ → B
 Escalate(1) → UPDATE … WHERE RowVersion=A  ✘ 0 dòng → ConflictException
 → Detach Andon #5, log Information, đi tiếp Andon khác
```

- Bên thua phải là **watcher**: nó **không được ghi đè** việc người đã nhận (spec 2.9b, bẫy 2). Lần chạy sau nó thấy
  `Acknowledged`, nên `Escalate` trả `false`.
- **Lưu từng Andon một**: nếu lưu cả lô một lần, một xung đột sẽ làm rollback mọi Andon khác trong lô.
- `db.Entry(call).State = EntityState.Detached`: bỏ entity lỗi khỏi change tracker, để lần `SaveChanges` sau không
  cố UPDATE nó lần nữa. (Bước 7 dùng `ChangeTracker.Clear()` vì chỉ có một entity; ở đây còn các Andon khác đang
  được theo dõi.)

### C5. SignalR group theo role, do **server** quyết định

```csharp
public override async Task OnConnectedAsync()
{
    foreach (var role in Context.User?.FindAll(JwtTokenService.RoleClaim).Select(c => c.Value) ?? [])
        await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeHub.RoleGroup(role));
    await base.OnConnectedAsync();
}
```

- `OnConnectedAsync` chạy mỗi khi một client kết nối (kể cả **reconnect**, vì reconnect là connection mới, group cũ mất).
- Role lấy từ **JWT đã được xác thực** (`Context.User`). Nếu để client tự gọi `JoinRole("Admin")` thì Operator cũng
  nghe được thông báo của Admin. Quy tắc chung: **quyền do server cấp, không do client khai**.
- Gửi tới nhiều group một lúc: `Clients.Groups(["role:Admin", "role:QC"])`. Một connection ở cả hai group vẫn chỉ nhận
  **một** lần.
- So sánh với group `line:{code}`: line là **thứ người dùng muốn xem** (client tự join, ai cũng được xem); role là
  **thứ người dùng có quyền nhận** (server join).

### C6. Log có mức độ (log level) là một kênh cảnh báo

```csharp
log.Log(call.EscalationLevel >= ErrorLogLevel ? LogLevel.Error : LogLevel.Warning,
    "Andon {AndonId} on {Machine} ({Type}) not acknowledged after {Minutes:F0} min: escalated to level {Level}, notifying {Roles}", ...);
```

- Ngoài đời, log `Error` thường được hệ thống giám sát (Seq, ELK, Grafana Loki…) bắt và đẩy thành email hoặc tin nhắn.
  Ở MVP, "báo giám đốc" là một dòng Error. Đây là cách nối với hạ tầng có sẵn mà không phải viết code gửi email.
- Message template có tham số (`{AndonId}`), không dùng `$""` (CLAUDE.md mục 5): Serilog lưu từng tham số thành
  trường riêng để lọc được, ví dụ "mọi escalation của L1-SMT-01".

### C7. Báo cáo: tính trong SQL hay trong C#?

```csharp
var calls = await db.AndonCalls.AsNoTracking()
    .Where(a => a.Status != AndonStatus.Cancelled && a.RaisedAt >= from && a.RaisedAt < to)
    .Select(a => new { a.EscalationLevel, a.RaisedAt, a.AckAt })   // chỉ 3 cột
    .ToListAsync(ct);
```

- Một tháng chỉ vài trăm Andon nên đọc 3 cột rồi `GroupBy`/`Average` trong C#: dễ đọc, không phụ thuộc `DATEDIFF` riêng của
  SQL Server. Nếu là bảng triệu dòng (`MachineEvents`) thì phải gộp trong SQL. **Chọn theo khối lượng dữ liệu**, và
  nói được lý do khi phỏng vấn.
- Khoảng **nửa mở `[from, to)`**: hai tháng liền nhau không đếm trùng Andon raise đúng lúc 00:00.
- Query string `?from=...Z`: model binding của ASP.NET Core đổi `Z` thành giờ **Local** của server; `AsUtc()` đưa về
  UTC trước khi so sánh với cột `datetime2` lưu UTC.
- Validator giới hạn khoảng ≤ 366 ngày: không cho một request quét toàn bộ lịch sử.

### C8. `FakeTimeProvider`: test "10 phút sau" trong 1 ms

```csharp
private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 27, 2, 0, 0, TimeSpan.Zero));
_clock.Advance(TimeSpan.FromMinutes(3));   // thời gian "trôi" mà không cần chờ
```

- Gói `Microsoft.Extensions.TimeProvider.Testing`. Code thật nhận `TimeProvider` qua DI (`TimeProvider.System`);
  test truyền đồng hồ giả.
- Test **biên**: 9:59 chưa lên level 2, 10:00 thì lên. Lỗi thường gặp nhất trong luật thời gian là `<` với `<=`.
- Bẫy 1 của spec 2.9b: dùng `DateTime.UtcNow` trực tiếp thì không test được, chỉ còn cách ngồi chờ 10 phút.

---

## D. Test

| Loại | File | Nội dung |
| --- | --- | --- |
| Unit | [EscalationTests.cs](../../tests/MiniMes.UnitTests/Andon/EscalationTests.cs) | `FakeTimeProvider`: +2 phút level 0 → 3:00 level 1 → 9:59 vẫn 1 → 10:00 level 2; **Ack ở phút 4 → không lên level 2**; Escalate cùng level hai lần → lần hai `false`; không bao giờ giảm; tick trễ 15 phút nhảy thẳng level 2; Acknowledged/Resolved/Cancelled không leo; bảng `DueLevel` theo loại (Quality 5 phút, Material/Other không bao giờ) |
| Integration | [AndonTests.cs](../../tests/MiniMes.IntegrationTests/Machines/AndonTests.cs) | seed đúng 4 quy tắc; `EscalateAndonsCommand` trên DB thật: 2/4/11 phút → 0/1/2, Quality 6 phút → 1, Material 60 phút → 0, đã ack → 0; chạy lần hai không đổi gì; stats: số liệu tính tay (4 Andon, level 0:2 / 1:1 / 2:1, 3 đã nhận, trung bình (240+60+720)/3 = **340 giây**), bỏ Cancelled, `to` không tính; 403 Operator; 400 khoảng sai |
| Integration | [RealtimeTests.cs](../../tests/MiniMes.IntegrationTests/Machines/RealtimeTests.cs) | Andon level 1 (Machine) → `admin` nhận `AndonEscalated`, `sup01` và `qc01` không nhận (không cần `JoinLine`) |

Bài học khi viết test: `EscalateAndonsCommand` leo thang **mọi** Andon `Raised` trong DB dùng chung, nên test push chỉ
xét event có **đúng Id của mình**. Nếu chỉ lấy "event đầu tiên nhận được", test sẽ chập chờn tùy thứ tự chạy.

---

## E. Câu hỏi phỏng vấn

1. Vì sao cần leo thang Andon? Ai được báo ở mỗi cấp, dựa vào đâu (bảng quy tắc, không hard-code)?
2. Watcher chạy mỗi 5 giây, làm sao không gửi thông báo cấp 1 tới 12 lần mỗi phút? (idempotent, level lưu DB)
3. API restart lúc Andon đã chờ 15 phút thì sao? (tính từ `RaisedAt`, nhảy thẳng level 2, không phát lại level 1)
4. Supervisor bấm "Nhận" đúng lúc watcher leo thang: bên nào thắng, vì sao phải là supervisor?
5. Vì sao role group do server join chứ không cho client tự join?
6. Test "10 phút sau" thế nào mà không phải chờ 10 phút?
7. KPI "Andon phải leo thang tới cấp 2" nói gì về nhà máy? Vì sao loại Andon `Cancelled` khỏi báo cáo?
8. Khi nào gộp số liệu trong SQL, khi nào trong C#?

## F. Đọc thêm

- Spec module-04 mục 1.4 (quy tắc leo thang), 1.5 ("Andon không có chủ"), 2.9b; spec-v2 mục M4.
- Microsoft Docs: *Manage users and groups in SignalR* (`Groups.AddToGroupAsync`, `OnConnectedAsync`),
  *TimeProvider* và *FakeTimeProvider*, *Handling Concurrency Conflicts* (EF Core).
- Bước tiếp theo **8**: WPF dashboard. Cột Level (≥ 1 nền đỏ nhấp nháy) sẽ dùng `AndonChanged` có `EscalationLevel`.
