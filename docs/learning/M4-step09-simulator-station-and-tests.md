# M4 · Bước 9 — Simulator vai trạm, integration test với broker thật, tài liệu: kiến thức cần nắm

> Tài liệu học cho bước 9 (bước cuối) của [module-04](../../spec/module-04-machine-connectivity.md) (spec mục **2.8**, 1.4,
> mục 0 kịch bản demo; ADR-007, ADR-008 trong [ARCHITECTURE.md](../../ARCHITECTURE.md)).

---

## A. Nghiệp vụ

### A1. Hai nguồn sản lượng, một sự thật cho mỗi câu hỏi (ADR-007)

| Câu hỏi | Lấy từ đâu | Vì sao |
| --- | --- | --- |
| Máy làm ra **bao nhiêu** (Performance của OEE) | Bộ đếm tích lũy của máy qua MQTT (`MachineEvents.Delta`) | Nhịp thật của máy, có cả khi không ai scan |
| Bao nhiêu **tốt / hỏng** (Quality của OEE, FPY) | Kết quả **scan theo serial** ở trạm (M3) | Có serial, mã lỗi, người/máy scan, qua route check, truy xuất được |
| `goodDelta` / `ngDelta` máy tự báo | **Chỉ đối chiếu** | Máy test NG có thể do lỗi ở SMT; máy không biết serial |

Câu phỏng vấn: *"OEE của anh lấy Good từ đâu?"* → từ scan theo serial, vì một nguồn sự thật cho chất lượng là serial;
bộ đếm máy chỉ cho tổng số.

### A2. Trạm tự động vs trạm thủ công

- **Trạm có máy** (SMT, Test): máy tự scan bo nó vừa làm (máy test gửi kết quả OK/NG + mã lỗi theo serial). Ngoài đời là
  tích hợp máy test (ICT/FCT) với MES, hoặc đầu đọc mã vạch gắn trên băng tải.
- **Trạm thủ công** (Assembly, Pack): người cầm máy quét ở kiosk. Simulator có `--auto-operator` để demo không cần người.
- **Repair** luôn thủ công: kỹ thuật viên phải chọn hành động, linh kiện thay, quay về bước nào.

### A3. "Starved": Idle thật, không phải Idle giả

Máy Test không có bo nào chờ (vì SMT đang Down) thì **đứng chờ**: đó là `Idle`, không phải `Down`. Trong OEE, Idle vì
thiếu đầu vào là **tổn thất do line** (upstream), không phải lỗi của máy Test. Simulator tạo ra đúng hiện tượng này: SMT
Down → Test tự chuyển `Idle`, có bo trở lại thì tự `Running`. Đây là dữ liệu thật cho phân tích bottleneck ở M5.

---

## B. Thiết kế vai trạm trong simulator

```
SimulatedMachine (vòng chu kỳ, mỗi chu kỳ một bo)
   │ có station?
   ├─ AutoStationClient.NextSerialAsync()      GET waiting-serials?take=1  (lần đầu: login badge → session)
   │     null ─────────────────────────────▶ Idle (starved), 5 giây sau xem lại
   ├─ AutoStationClient.ScanAsync(serial, ng)  POST /api/stations/{id}/scan  Source=Machine
   │     null / Blocked ───────────────────▶ Idle (không làm ra sản phẩm)
   └─ counter++  → MQTT count                (chỉ đếm khi MES đã nhận bo)
```

| Quy tắc | Lý do |
| --- | --- |
| Serial và kết quả **lấy từ API**, không tự sinh | Đi đúng đường của kiosk → route check M3 được kiểm thử thật (spec 2.8, bẫy cuối) |
| Đăng nhập bằng **badge `SIM-STATION`**, không mật khẩu | Không có bí mật trong `machines.json`; scan ghi rõ là máy, không lẫn với tên công nhân |
| Session mở cho **WO đầu dispatch list, bỏ WO chờ FAI** | Giống operator chọn dòng đầu ở kiosk; WO chờ FAI chỉ cho mẫu chạy (việc của người) |
| Mọi lỗi API = "không có bo" | Simulator không bao giờ chết vì API; ngoài đời máy cũng chỉ đứng chờ |
| Tra trạm theo **mã**, không theo id | Id khác nhau giữa các DB; mã là khóa nghiệp vụ |

---

## C. Kiến thức kỹ thuật

### C1. HttpClient trong console app

- **Một `HttpClient` dùng chung** cho mọi trạm (đăng ký Singleton): tạo `HttpClient` mỗi request làm cạn socket
  (*socket exhaustion*, socket ở trạng thái TIME_WAIT). Trong ASP.NET Core dùng `IHttpClientFactory`; console nhỏ thì một
  instance sống suốt process là đủ.
- Token **khác nhau cho mỗi trạm** → gắn header `Authorization` **theo từng request** (`HttpRequestMessage`), không đặt
  vào `DefaultRequestHeaders` của client dùng chung.
- `Timeout = 5 giây`: API chậm thì máy chờ (Idle), không treo vòng chu kỳ.
- 401 → xóa token, lần sau đăng nhập lại (API restart với khóa JWT khác, token hết hạn).

### C2. Cờ dòng lệnh không có giá trị

`Host.CreateApplicationBuilder(args)` đọc dòng lệnh thành cấu hình dạng `--key value`. Cờ trơn `--auto-operator` sẽ "nuốt"
tham số kế tiếp làm giá trị (hoặc lỗi nếu đứng cuối). Cách làm: lấy cờ ra khỏi `args` trước khi tạo builder.

### C3. Integration test với broker thật (Testcontainers)

```csharp
new ContainerBuilder("eclipse-mosquitto:2")
    .WithPortBinding(Port, 1883)                                       // cổng host CỐ ĐỊNH, chọn lúc chạy
    .WithResourceMapping(new FileInfo("docker/mosquitto.conf"), "/mosquitto/config/")
    .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("mosquitto version .* running"))
```

- **Dùng chính file cấu hình của dự án**: Mosquitto 2 mặc định chỉ nghe localhost *bên trong container*, thiếu
  `listener 1883` + `allow_anonymous` là kết nối từ ngoài bị từ chối ("local only mode").
- **Cổng cố định**: test "stop rồi start broker" cần ingest tìm lại broker ở **cùng địa chỉ**. Cổng ngẫu nhiên
  (`WithPortBinding(1883, true)`) đổi sau mỗi lần start. Lấy một cổng trống bằng `TcpListener(IPAddress.Loopback, 0)`.
- **Wait strategy theo log**: image Mosquitto không có công cụ để kiểm tra cổng từ bên trong; đợi dòng log "running".
- Test đi **đúng đường thật**: client MQTT → broker → `MqttIngestService` → store → batch writer → SQL Server. Kiểm tra
  bằng cách **dò DB tới khi đạt điều kiện** (có timeout), không `Task.Delay` cố định: nhanh khi hệ thống nhanh, vẫn
  đúng khi CI chậm.

### C4. Biến môi trường dùng chung cả process và test song song

`ApiFactory` cấu hình API bằng `Environment.SetEnvironmentVariable` (phải có trước khi `Program.cs` đọc cấu hình). Biến
môi trường là **của cả process**: hai collection xUnit chạy **song song** mà mỗi bên đặt `Mqtt__Enabled` khác nhau thì
host này có thể đọc nhầm giá trị của host kia, gây test chập chờn ngẫu nhiên. Cách xử lý:

```csharp
[CollectionDefinition(Name, DisableParallelization = true)]   // chạy riêng, sau các collection song song
```

Bài học chung: **state toàn cục** (biến môi trường, static, file, cổng mạng) là kẻ thù của test song song.

### C5. Nạp lại "giá trị mới nhất" sau restart

```csharp
from t in db.MachineTags where t.IsActive
let v = db.ProcessValues.Where(p => p.MachineTagId == t.Id)
                        .OrderByDescending(p => p.SourceTs).ThenByDescending(p => p.Id).FirstOrDefault()
where v != null
select new { Tag = t, v.Value, v.Quality, v.SourceTs }
```

- EF dịch `let … FirstOrDefault()` thành `OUTER APPLY (SELECT TOP 1 …)` của SQL Server, tận dụng index
  `(MachineTagId, SourceTs)`: mỗi tag một lần seek, không quét bảng.
- **Mới nhất theo `SourceTs`** (giờ đo), không theo thứ tự ghi: giá trị đến trễ được lưu sau nhưng không được thắng.
- Không đánh dấu dirty: giá trị nạp lại không phải "mới", không cần push.

### C6. ADR (Architecture Decision Record)

Khung: **Bối cảnh → Quyết định → Phương án đã loại → Đánh đổi**. Viết ADR cho quyết định *khó đảo ngược* hoặc *người
mới sẽ hỏi "sao không làm X?"*. ADR-007 (nguồn OEE) và ADR-008 (MQTT) là hai câu hỏi phỏng vấn thường gặp nhất của
module này; phần "phương án đã loại" chính là câu trả lời cho "sao anh không gọi thẳng PLC / không dùng HTTP?".

---

## D. Test

| Loại | File | Nội dung |
| --- | --- | --- |
| Integration (Mosquitto thật) | [MqttIngestTests.cs](../../tests/MiniMes.IntegrationTests/Machines/MqttIngestTests.cs) | `100, 105, 3` → delta `0, 5, 3 (reset)`; `msgId` trùng → 1 dòng, tổng đúng; ngừng heartbeat → `Offline` (≈ 32 giây); stop/start broker → ingest tự nối lại, dữ liệu vào DB |
| Integration | [MosquittoFixture.cs](../../tests/MiniMes.IntegrationTests/Machines/MosquittoFixture.cs) | broker + SQL Server riêng + API bật ingest; collection không song song |
| Integration | `ProcessValueTests` +1, `AuthTests` +1 | loader nạp giá trị tag mới nhất; badge `SIM-STATION` đăng nhập được |

Simulator **không có unit test** (UnitTests chỉ được tham chiếu Domain/Application/Client.Core, CLAUDE.md mục 4). Vai trạm
được kiểm bằng chạy thật kịch bản demo (mục E).

---

## E. Chạy thật: kịch bản demo bước 4–6

| Bước | Quan sát |
| --- | --- |
| Simulator `--auto-operator`, chu kỳ 3 giây | 4 trạm mở session trên `WO-2610-00003` bằng `SIM-STATION`, bỏ `WO-2610-00001` (chờ FAI) |
| Bo chạy | SMT → ASM → TEST → PACK; DB: mọi scan `Source = Machine` |
| `ng L1-TEST-01 1` | đúng một NG (`CMP-MIS`), serial chuyển `Ng` |
| `down L1-SMT-01 300` | TEST hết bo → tự `Idle` (starved) |
| Andon | một Andon cũ còn mở từ lần thử trước nên không tạo bản thứ hai (đúng luật); API vừa restart thấy nó chờ 40 phút nên nhảy thẳng **level 2** (log Error); hủy bản cũ → 5 giây sau Andon mới tự raise; ack sau 11 giây, resolve `MCH-FAULT` |
| Repair | check-in + form sửa qua API → bo về TEST → TEST tự `Idle → Running` → test lại **OK** |
| Đo DoD | `RaisedAt` → client nhận `AndonChanged`: TB 31 ms, max 186 ms (yêu cầu < 2 giây) |

---

## F. Câu hỏi phỏng vấn

1. OEE lấy Total Count và Good/NG từ đâu? Vì sao không lấy tất cả từ máy? `ngDelta` dùng làm gì?
2. Vì sao simulator phải lấy serial từ API mà không tự sinh?
3. Máy Test `Idle` vì thiếu bo thì OEE tính thế nào, khác `Down` ra sao?
4. Vì sao chọn MQTT thay vì cho MES gọi thẳng PLC, hoặc cho máy POST HTTP vào MES?
5. Viết integration test với message broker thế nào? Vì sao cần cổng cố định khi test restart broker?
6. Vì sao test song song có thể chập chờn khi dùng biến môi trường? Cách xử lý?
7. Tạo `HttpClient` mới cho mỗi request thì có vấn đề gì?
8. ADR là gì, khi nào nên viết?

## G. Đọc thêm

- Spec module-04 mục 2.8 (simulator hai vai), mục 4 (bảng "Vì sao chọn giải pháp này").
- Testcontainers for .NET: *Wait strategies*, *Resource mapping*. Microsoft Docs: *HttpClient guidelines*, *xUnit: running tests in parallel*.
- Tiếp theo: **M4b** — Edge Gateway (OPC UA + Modbus → MQTT, đệm SQLite khi mất mạng), vào đúng ingest của M4.
