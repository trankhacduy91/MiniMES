# Mini MES — Spec & Roadmap (bản đã review)

> Bản sửa từ spec gốc. Thay đổi chính so với bản cũ được đánh dấu **[MỚI]** hoặc **[SỬA]**.

## 0. Mục tiêu, phạm vi và giả định

**Mục tiêu**: một MES thu nhỏ chạy được end-to-end, dùng làm portfolio khi ứng tuyển IT MES ở KCN (điện tử, lắp ráp). Mỗi module phải trả lời được một câu hỏi phỏng vấn cụ thể.

**Phạm vi đã chốt**:

| Hạng mục | Giả định |
| --- | --- |
| Loại sản xuất | **[CHỐT]** Rời rạc (discrete), lắp ráp điện tử: SMT → lắp ráp → test → đóng gói |
| Quy mô demo | 1 nhà máy, 2 line, mỗi line 3–4 trạm/máy, 2 ca/ngày |
| Đơn vị truy xuất | Thành phẩm: **serial** từng sản phẩm. NVL: **lot** (reel linh kiện, thùng vật tư) |
| Người dùng | Planner, Operator, Supervisor, QC, Maintenance, Admin |
| DB | SQL Server duy nhất (đúng stack nhà máy tuyển dụng). Không dùng TimescaleDB/InfluxDB ở MVP |
| Client | **[CHỐT]** Desktop **WPF (.NET 8, MVVM)** cho cả kiosk trạm và back-office. Không dùng React/Vue. *(Đã đổi sang .NET 10 — xem spec-v2 B10 #11)* |
| Ngôn ngữ UI | **[CHỐT]** Tiếng Anh. Chuỗi UI đặt trong file resource `.resx` ngay từ đầu (thêm ngôn ngữ sau không phải sửa code) |
| Tích hợp ERP | **[CHỐT]** Ngoài phạm vi MVP. WO do Planner tạo tay trong MES. Xem M8 (tùy chọn, làm sau) |
| Nguồn dữ liệu máy | **[CHỐT]** Chỉ dùng Machine Simulator (không có máy/PLC thật) |
| Kiến trúc | Modular monolith, Clean Architecture, feature folder |
| Thời gian | ~1,5–2 giờ/ngày (~10 giờ/tuần); M0–M7 khoảng 200 giờ ≈ 20 tuần cho người mới (xem mục 3) |

**Kịch bản demo end-to-end** — mọi module phục vụ kịch bản này, làm gì không phục vụ nó thì để sau:

1. Planner tạo Work Order: 200 bo mạch model `PCB-A` cho Line 1.
2. Planner release WO → hệ thống sinh 200 serial, in nhãn (ZPL preview).
3. Kho nhập 3 reel linh kiện (lot) → operator nạp reel vào máy, scan xác nhận đúng vật tư.
4. Simulator chạy: máy đếm sản lượng qua MQTT, trạm Test tự gửi kết quả OK/NG theo từng serial; dashboard cập nhật realtime.
5. Operator scan tay 1 serial trên kiosk; trạm Test báo NG 1 con → đi Repair → quay lại Test → OK.
6. Máy Down 5 phút → Andon → supervisor thấy cảnh báo, chọn lý do dừng.
7. OEE ca hiện tại hiển thị (A × P × Q).
8. QC phát hiện reel lỗi → truy xuất xuôi ra danh sách serial bị ảnh hưởng → Hold.
9. WO đủ sản lượng → Completed, báo cáo sản lượng/FPY/OEE theo WO.

## 1. Kết quả review spec gốc

| # | Vấn đề | Đề xuất |
| --- | --- | --- |
| 1 | Module 3 dùng Vue, các module khác dùng React | **[CHỐT]** Toàn bộ client là WPF |
| 2 | RabbitMQ có trong Docker nhưng không module nào dùng | **[CHỐT]** Bỏ khỏi MVP; chỉ thêm lại nếu làm M8 |
| 3 | Thiếu luồng **scan serial + route check (interlock) + NG/Repair** — chức năng MES cốt lõi ở nhà máy điện tử | Thêm M3 Shop Floor Execution, tách phần máy sang M4 |
| 4 | Chưa có tích hợp ERP | **[CHỐT]** Để sau, thành M8 tùy chọn |
| 5 | Thứ tự: Module 3 đã ghi `LotConsumption` nhưng Lot ở Module 6, Warehouse ở Module 7 | Gộp "nhập lot NVL tối thiểu" vào M6 Material & Traceability, làm trước Quality |
| 6 | Work Order dùng BOM/Routing "sống" — sửa master sau khi release sẽ làm sai WO | Snapshot BOM/Routing vào WO khi release; master có version/hiệu lực |
| 7 | OEE thiếu dữ liệu nền: thời gian kế hoạch (lịch ca, nghỉ giữa ca), ideal cycle time, nguồn đếm OK/NG | Thêm vào M1 (Shift, cycle time chuẩn) và định nghĩa rõ ở M5 |
| 8 | MQTT chưa định nghĩa topic, payload, trùng message (QoS 1), mất kết nối, lệch giờ | Thêm "hợp đồng dữ liệu" ở M4: topic, schema, idempotency, UTC |
| 9 | Chưa có phân quyền, audit trail (ai làm gì, lúc nào) | Thêm vào M0; operator login bằng scan thẻ |
| 10 | TimescaleDB/InfluxDB trộn với SQL Server | Giữ SQL Server; học partition/index/archive — đúng thứ nhà máy hỏi |
| 11 | Quality chưa nối với Traceability (NG → Hold lot/serial) | Thêm Hold/Release ở M7, dùng truy xuất của M6 |
| 12 | Chưa có Definition of Done, chưa có kịch bản demo | Thêm DoD cho từng module + kịch bản ở mục 0 |
| 13 | Chưa có test tích hợp với DB/MQTT thật | Testcontainers (SQL Server, Mosquitto) trong CI |

## 2. Kiến trúc tổng quan

```
                    MQTT (Mosquitto)            ┌──────────── MES API (.NET 10) ────────────┐
[Machine Simulator] ── state/count/alarm ──────▶│ MasterData | Planning | Execution         │
        │                                       │ Machine | OEE | Material/Trace | Quality  │
        └── HTTP: kết quả test theo serial ────▶│ Maintenance                               │
                                                └──────┬───────────────────┬────────────────┘
                                                   SQL Server         REST + SignalR
                                                                           │
                                          ┌────────────────────────────────┼────────────────────┐
                                  [WPF Kiosk trạm]              [WPF Back-office]      [WPF Dashboard
                                   scan, NG, Andon,              master data, WO,       (có thể là 1 view
                                   nạp vật tư                    truy xuất, QC          trong Back-office)]
```

Tại sao WPF gọi qua API thay vì kết nối thẳng DB: nhiều MES in-house cũ ở VN cho client WinForms kết nối thẳng SQL Server, rất khó kiểm soát phân quyền và cập nhật. Thiết kế qua API cho phép nói trong phỏng vấn: "tôi biết cả hai cách và vì sao chọn cách này". Ghi vào ARCHITECTURE.md.

Solution:

```
src/
  MiniMes.Domain/            entity, value object, domain rule, state machine
  MiniMes.Application/       use case (MediatR command/query), validation, DTO
  MiniMes.Infrastructure/    EF Core, MQTT client, SignalR hub, background jobs
  MiniMes.Api/               endpoint, auth, DI
  MiniMes.Contracts/         DTO dùng chung giữa API và client WPF
  MiniMes.MachineSimulator/  console app giả lập máy + trạm test
  MiniMes.Client.Core/       ApiClient (HttpClient), SignalR client, auth token, .resx chuỗi UI
  MiniMes.Kiosk.Wpf/         màn hình trạm: toàn màn hình, chữ to, tối ưu scan
  MiniMes.BackOffice.Wpf/    master data, planning, dashboard, truy xuất, QC
tests/
  MiniMes.UnitTests/
  MiniMes.IntegrationTests/  Testcontainers
docker-compose.yml           SQL Server + Mosquitto
```

Thư viện WPF: `CommunityToolkit.Mvvm` (MVVM), `Microsoft.Extensions.Hosting` (DI trong WPF), `Microsoft.AspNetCore.SignalR.Client`, **`OxyPlot.Wpf`** (một thư viện biểu đồ duy nhất cho OEE, Pareto, control chart). Cây BOM dùng `TreeView` + `HierarchicalDataTemplate`.

Quy ước chung: lưu thời gian **UTC** (`datetime2`), hiển thị theo giờ nhà máy; mọi bảng giao dịch có `CreatedAt`, `CreatedBy`; bảng log lớn (`ScanLog`, `MachineEvent`) là **insert-only**, khóa chính `bigint`.

---

## Module 0 — Nền tảng kỹ thuật

**Nghiệp vụ phụ trách**: không có nghiệp vụ sản xuất, nhưng quyết định hệ thống có "chạy như nhà máy" hay không: ai được làm gì (phân quyền), ai đã làm gì (audit), hệ thống có dựng lại được trên máy khác không.

**Các bước chi tiết**

1. Tạo solution theo cấu trúc mục 2; bật `Nullable`, `TreatWarningsAsErrors` cho Domain.
2. EF Core + SQL Server Developer; connection string qua `appsettings.json` + user-secrets. Migration đặt trong Infrastructure; script `dotnet ef database update` ghi vào README.
3. `docker-compose.yml`: SQL Server, Mosquitto. Healthcheck cho từng container. (RabbitMQ chỉ thêm khi làm M8.)
4. Serilog (console + file, có `CorrelationId`), middleware xử lý exception trả về `ProblemDetails`.
5. **[MỚI] Auth & phân quyền**: JWT đơn giản; role `Planner/Operator/Supervisor/QC/Maintenance/Admin`. Operator đăng nhập kiosk bằng mã nhân viên (scan thẻ = gõ chuỗi + Enter).
6. **[MỚI] Audit trail**: interceptor EF Core ghi `AuditLog` (bảng, khóa, cột thay đổi, giá trị cũ/mới, người, thời điểm) cho bảng master và trạng thái WO.
7. **[MỚI] Cấu hình nhà máy**: bảng `Site`, `Line`, `Station` ở mức tối thiểu (đủ để các module sau tham chiếu).
8. GitHub Actions: build + unit test; job integration test chạy Testcontainers.
9. **[MỚI] Khung client WPF**: `MiniMes.Client.Core` (ApiClient có gắn JWT, xử lý lỗi `ProblemDetails` thành thông báo tiếng Anh), host DI cho 2 app WPF, file `Strings.resx` chứa toàn bộ chuỗi UI (English). Màn hình Login dùng chung.
10. README khung: mục tiêu, kiến trúc, cách chạy, kịch bản demo.

**DoD**: clone → `docker compose up` → chạy API → Swagger mở được; mở app WPF Back-office, login được với user seed.

---

## Module 1 — Master Data

**Nghiệp vụ phụ trách**: định nghĩa "sản xuất cái gì, bằng gì, qua đâu, trên máy nào, trong bao lâu". Ở nhà máy thật, dữ liệu này thường đến từ ERP (Item, BOM) và kỹ sư công nghệ (Routing, cycle time). Sai master data là nguyên nhân số 1 của lỗi MES ngoài đời: thiếu mã vật tư, sai định mức, sai công đoạn.

**Thuật ngữ**

| Thuật ngữ | Nghĩa |
| --- | --- |
| Item | Mã hàng: NVL (raw), bán thành phẩm (semi), thành phẩm (finished) |
| UoM | Đơn vị tính (pcs, reel, kg, m) |
| BOM | Định mức vật tư: 1 đơn vị cha cần bao nhiêu đơn vị con. BOM đa cấp: bán thành phẩm có BOM riêng |
| Routing | Chuỗi công đoạn (operation) mà sản phẩm phải đi qua, theo thứ tự |
| Work Center / Station | Nơi thực hiện công đoạn; có thể gồm 1 hay nhiều máy |
| Ideal cycle time | Thời gian chuẩn làm 1 sản phẩm tại công đoạn — dùng cho OEE Performance |
| Shift / Calendar | Ca làm việc, giờ nghỉ — dùng cho OEE Availability |

**Entity chính**

| Entity | Cột quan trọng | Ghi chú |
| --- | --- | --- |
| `Item` | Code, Name, Type, UoM, IsSerialized, IsLotControlled, IsActive | IsSerialized = true cho thành phẩm |
| `Bom` | ItemId, Version, EffectiveFrom, EffectiveTo, Status (Draft/Approved) | **[SỬA]** có version |
| `BomLine` | BomId, ComponentItemId, QtyPer, ScrapRate, OperationSeq | OperationSeq: vật tư tiêu thụ ở công đoạn nào |
| `Routing` | ItemId, Version, Status | **[SỬA]** có version |
| `RoutingStep` | RoutingId, Seq, OperationCode, WorkCenterId, IdealCycleTimeSec, IsInspection, IsMandatory | |
| `WorkCenter` / `Machine` | Code, LineId, Type | Machine có `MqttTopicKey` |
| `Shift` / `ShiftBreak` | StartTime, EndTime, Days, CrossesMidnight | Ca đêm qua 0h |
| `DefectCode` **[MỚI]** | Code, Name, Category | Dùng cho NG ở M3, M7 |
| `DowntimeReason` **[CHUYỂN từ M5]** | Code, Name, Category (Planned/Unplanned), CountsAgainstAvailability | |

**Quy tắc nghiệp vụ**

- BOM không có vòng lặp (A dùng B, B dùng A) — kiểm tra bằng DFS khi lưu.
- Chỉ một version `Approved` có hiệu lực tại một thời điểm cho mỗi Item.
- Routing phải có ít nhất 1 bước; Seq tăng dần, không trùng.
- Không xóa cứng master đã được WO tham chiếu → `IsActive = false`.

**Các bước chi tiết**

1. ERD (vẽ bằng dbdiagram.io hoặc Mermaid, đặt trong `docs/`).
2. Entity + configuration EF Core; unique index trên `Item.Code`, `(Bom.ItemId, Version)`.
3. CRUD API theo một pattern duy nhất (MediatR command/query + FluentValidation).
4. Validate vòng lặp BOM + unit test (case: vòng trực tiếp, vòng gián tiếp 3 cấp, BOM hợp lệ nhiều cấp).
5. API "explode BOM" đa cấp trả về tổng nhu cầu NVL (recursive CTE hoặc đệ quy trong code — ghi lại lý do chọn).
6. Approve BOM/Routing → khóa sửa, muốn đổi phải tạo version mới.
7. WPF Back-office: danh sách Item (`DataGrid` có lọc), BOM dạng cây (`TreeView` + `HierarchicalDataTemplate`), Routing dạng lưới sắp xếp thứ tự bằng nút Up/Down.
8. Seed data: 1 thành phẩm `PCB-A`, 1 bán thành phẩm, 6–8 NVL, routing 4 bước (SMT → Assembly → Test → Pack).

**DoD**: explode BOM của `PCB-A` ra đúng số lượng NVL; không tạo được BOM vòng.

**Phỏng vấn**: "Vì sao cần version BOM?", "Master data sai thì MES ảnh hưởng gì?"

---

## Module 2 — Production Planning (Work Order)

**Nghiệp vụ phụ trách**: biến kế hoạch thành lệnh sản xuất cụ thể cho từng line. Ngoài đời kế hoạch tổng thường đến từ ERP ("tuần này làm 10.000 PCB-A"); trong dự án này Planner tạo WO trực tiếp trong MES, chia theo line, ca. Khi WO được **release** thì line mới được phép chạy.

**Thuật ngữ**: Work Order (WO), Release, Dispatch, WIP, Snapshot BOM/Routing.

**Entity chính**

| Entity | Cột quan trọng |
| --- | --- |
| `WorkOrder` | WoNo, ItemId, PlannedQty, LineId, PlannedStart/End, Status, BomVersion, RoutingVersion, ExternalRef (để trống, dành cho M8) |
| `WorkOrderMaterial` | WoId, ComponentItemId, RequiredQty, IssuedQty, ConsumedQty (**snapshot từ BOM**) |
| `WorkOrderOperation` | WoId, Seq, WorkCenterId, IdealCycleTimeSec, QtyGood, QtyNg, Status (**snapshot từ Routing**) |
| `Serial` **[MỚI]** | SerialNo, WoId, ItemId, CurrentSeq, Status (Created/InProcess/Ng/Repair/Completed/Scrapped/Hold) |

**State machine WO**

```
Draft → Released → InProgress → Completed → Closed
  │        │            │
  └────────┴────→ Cancelled (chỉ khi chưa có sản lượng)
InProgress ⇄ OnHold
```

**Quy tắc nghiệp vụ**

- Release chỉ khi BOM và Routing có version Approved.
- Khi Release: snapshot BOM → `WorkOrderMaterial`, Routing → `WorkOrderOperation`; sinh `Serial` (nếu Item IsSerialized).
- Sinh serial phải **không trùng khi nhiều người release cùng lúc**: dùng `SEQUENCE` SQL Server hoặc bảng counter + `UPDLOCK` trong transaction; unique index trên `SerialNo`.
- Không Cancel khi đã có sản lượng; Completed khi QtyGood của bước cuối ≥ PlannedQty (hoặc supervisor đóng thủ công có lý do).

**Các bước chi tiết**

1. Entity + state machine trong Domain (method `Release()`, `Start()`, `Complete()` ném lỗi nếu chuyển sai), unit test mọi chuyển trạng thái.
2. Use case Create WO: chọn Item + Qty → preview NVL (explode BOM) + công đoạn.
3. Use case Release: snapshot + sinh serial + ghi audit.
4. **[MỚI]** In nhãn: sinh chuỗi ZPL cho từng serial (mã Code128/DataMatrix), xem trước qua Labelary API hoặc render ảnh; lưu `LabelPrintLog`.
5. WPF Back-office: danh sách WO lọc theo trạng thái/line; màn hình chi tiết (NVL, công đoạn, tiến độ từng bước); nút Release/Hold/Complete bật/tắt theo state machine.
6. (Tùy chọn) Gantt đơn giản theo line.

**DoD**: release WO 200 pcs sinh đúng 200 serial không trùng (test đồng thời 2 request), sửa BOM sau release không làm thay đổi WO.

**Phỏng vấn**: "Tránh trùng serial khi nhiều PC cùng in?", "WO đang chạy mà kỹ sư đổi BOM thì sao?"

---

## Module 3 — Shop Floor Execution **[MỚI — tách từ Module 3 cũ]**

**Nghiệp vụ phụ trách**: đây là trái tim MES ở nhà máy điện tử. Ở mỗi trạm, operator scan serial; MES kiểm tra sản phẩm có được phép làm ở trạm này không (route check), ghi kết quả OK/NG, chuyển sản phẩm NG sang sửa chữa, và không cho sản phẩm "nhảy cóc" công đoạn. Đây là phần "poka-yoke bằng phần mềm".

**Thuật ngữ**

| Thuật ngữ | Nghĩa |
| --- | --- |
| Route check / Interlock | Chặn scan nếu công đoạn trước chưa OK |
| NG | Sản phẩm không đạt |
| Repair | Sửa sản phẩm NG, sau đó quay lại công đoạn kiểm tra |
| Rework | Làm lại theo quy trình (có thể nhiều bước) |
| Scrap | Hủy bỏ, không sửa được |
| WIP | Sản phẩm đang nằm giữa các công đoạn |
| FPY | First Pass Yield — tỷ lệ đạt ngay lần đầu |

**Entity chính**

| Entity | Cột quan trọng |
| --- | --- |
| `ScanLog` | Id (bigint), SerialNo, WoId, StationId, Seq, Result (OK/NG), DefectCode, OperatorId, ScannedAt |
| `RepairLog` | SerialNo, DefectCode, Action, ComponentReplaced, RepairedBy, RepairedAt |
| `StationSession` | StationId, OperatorId, WoId, LoginAt, LogoutAt |

**Quy tắc route check** (viết trong 1 hàm domain, sau này có thể chuyển thành stored procedure để so sánh hiệu năng):

1. Serial tồn tại và thuộc WO đang InProgress/Released.
2. Serial không ở trạng thái Hold/Scrapped/Completed.
3. Bước hiện tại của trạm = `CurrentSeq + 1` của serial (hoặc = bước kiểm tra sau khi Repair).
4. Serial đang NG thì chỉ trạm Repair được scan.
5. Scan lặp (cùng serial, cùng trạm, trong vài giây) → trả OK nhưng không ghi trùng (idempotent).

**Các bước chi tiết**

1. Domain service `RouteCheck` + bảng test case (tối thiểu 10 case: đúng bước, nhảy bước, scan lại, NG, sau repair, serial hold, WO sai…).
2. API `POST /stations/{id}/scan` trả `{ result, message, nextStep }` trong < 100 ms.
3. WPF Kiosk: login bằng mã nhân viên → chọn trạm + WO → `TextBox` scan luôn giữ focus (scanner keyboard wedge = gõ + Enter; bắt `KeyDown` Enter, sau đó xóa ô và focus lại) → nền xanh/đỏ toàn màn hình, `SystemSounds` báo. Chạy toàn màn hình (`WindowState=Maximized`, `WindowStyle=None`), nút to cho màn cảm ứng. Mất kết nối API → hiện banner đỏ "Offline", không cho scan (không lưu tạm cục bộ ở MVP).
4. Trạm Test: nút NG + chọn DefectCode. Trạm Repair: nhập hành động sửa, linh kiện thay.
5. Cập nhật `WorkOrderOperation.QtyGood/QtyNg`, WIP theo bước.
6. Báo cáo: FPY theo trạm, danh sách WIP theo bước.
7. **Hiệu năng**: seed 5–10 triệu dòng `ScanLog`, đo thời gian route check trước/sau index `(SerialNo, ScannedAt)`; ghi kết quả vào README.

**DoD**: kịch bản bước 5 của demo chạy đúng; không scan nhảy bước được.

**Phỏng vấn**: "Làm sao chặn công nhân bỏ qua công đoạn?", "Scan chậm thì kiểm tra gì?"

---

## Module 4 — Machine Connectivity: Simulator + MQTT + Andon **[SỬA]**

**Nghiệp vụ phụ trách**: thu thập dữ liệu **tự động** từ máy (trạng thái, số đếm, cảnh báo) thay cho ghi tay; phát hiện dừng máy và gọi người xử lý (Andon). Ngoài đời dữ liệu đến từ PLC qua OPC UA/Modbus/gateway; ở đây dùng MQTT để mô phỏng tầng đó.

**Hợp đồng dữ liệu [MỚI]**

Topic:

```
minimes/{site}/{line}/{machine}/state      # Running | Idle | Down | Setup | PlannedStop
minimes/{site}/{line}/{machine}/count      # bộ đếm tích lũy
minimes/{site}/{line}/{machine}/alarm
minimes/{site}/{line}/{machine}/heartbeat
```

Payload (JSON):

```json
{ "msgId": "b3f1…", "machine": "L1-SMT-01", "ts": "2026-10-01T01:23:45.123Z",
  "state": "Running", "counter": 15230, "goodDelta": 1, "ngDelta": 0, "alarmCode": null }
```

Quy tắc:

- Gửi **bộ đếm tích lũy** thay vì chỉ "+1" để mất message vẫn tính lại được; xử lý khi bộ đếm reset về 0.
- QoS 1 có thể nhận trùng → idempotent theo `msgId` (unique index hoặc cache ngắn hạn).
- `ts` là giờ máy; lưu thêm `ReceivedAt` giờ server để phát hiện lệch giờ.
- Không nhận heartbeat quá 30 giây → máy chuyển trạng thái `Offline` (khác với `Down`).

**Entity chính**: `MachineEvent` (insert-only), `MachineStateHistory` (StartAt, EndAt, State — gom từ event, dùng cho OEE), `AndonCall` (Machine, Type, RaisedBy, RaisedAt, AckBy, AckAt, ResolvedAt, DowntimeReasonId).

**Các bước chi tiết**

1. `MachineSimulator`: cấu hình N máy từ file JSON (cycle time, xác suất Down, xác suất NG); chạy song song bằng `Task`; có lệnh console để ép máy Down/NG khi demo. Simulator đóng **hai vai**:
    - **Vai máy (qua MQTT)**: gửi state, bộ đếm tích lũy, alarm, heartbeat — giống PLC/gateway ngoài đời.
    - **Vai trạm tự động (qua HTTP)**: với trạm có máy (SMT, Test), mở `StationSession` bằng user Operator riêng, lấy serial kế tiếp qua `GET /stations/{id}/waiting-serials` (M3) và gọi đúng API `POST /stations/{id}/scan` mà kiosk dùng, kèm OK/NG + DefectCode. Ngoài đời đây là máy test/AOI tự gửi kết quả theo serial.
    - Trạm thủ công (Assembly, Repair, Pack) vẫn do người scan trên WPF Kiosk, hoặc bật chế độ `--auto-operator` để simulator scan thay khi chạy demo dài.
2. Worker `MqttIngestService` (BackgroundService + MQTTnet): subscribe, validate, ghi `MachineEvent` theo batch (ví dụ 200 dòng hoặc 1 giây).
3. Gom trạng thái: khi state đổi → đóng bản ghi `MachineStateHistory` cũ, mở bản ghi mới.
4. Tự reconnect MQTT, log mất kết nối; test bằng cách tắt container Mosquitto.
5. SignalR hub: push thay đổi trạng thái máy, số đếm, Andon lên dashboard (throttle 1 lần/giây/máy để không quá tải UI).
6. Andon: nút trên kiosk hoặc tự động khi máy Down > X giây → supervisor Acknowledge → Resolve kèm lý do dừng.
7. Dashboard line: ô màu theo trạng thái máy, sản lượng giờ hiện tại, danh sách Andon mở.
8. **Liên kết với M3**: số đếm máy (MQTT) dùng cho Availability/Performance; kết quả OK/NG theo serial (API scan, dù do người hay simulator gửi) dùng cho Quality. Ghi rõ quyết định này trong ARCHITECTURE.md.

**DoD**: tắt/bật Mosquitto không mất dữ liệu đếm quá 1 chu kỳ; Andon hiện trên dashboard < 2 giây.

**Phỏng vấn**: "Đọc dữ liệu máy bằng cách nào?", "Mất mạng giữa máy và MES thì sao?"

---

## Module 5 — OEE & Downtime

**Nghiệp vụ phụ trách**: đo hiệu quả thiết bị và tìm nguyên nhân mất năng suất. OEE là KPI quản lý xưởng hỏi đầu tiên; bảng lý do dừng máy (Pareto) là thứ họ dùng để cải tiến.

**Định nghĩa [MỚI — phải chốt rõ]**

```
Planned Production Time = Thời gian ca − Nghỉ giữa ca − PlannedStop
Run Time               = Planned Production Time − Unplanned Downtime
Availability           = Run Time / Planned Production Time
Performance            = (IdealCycleTime × Total Count) / Run Time
Quality                = Good Count / Total Count
OEE                    = Availability × Performance × Quality
```

- **[CHỐT]** Nguồn Total Count: bộ đếm máy qua MQTT (M4). Nguồn Good/NG: kết quả theo serial qua API scan (M3) — do simulator vai trạm tự động gửi hoặc do người scan. Không dùng `ngDelta` của máy cho OEE (chỉ để đối chiếu).
- Lý do: một nguồn sự thật duy nhất cho chất lượng là serial; khi khách hàng hỏi "con NG nào, lỗi gì" thì trả lời được. Bộ đếm máy chỉ biết "bao nhiêu", không biết "con nào".
- Kiểm tra chéo: Total Count của máy và số serial đã scan ở trạm đó chênh nhau > 2% → cảnh báo (máy đếm sai hoặc bỏ scan).
- IdealCycleTime lấy từ snapshot `WorkOrderOperation` (sản phẩm khác nhau có cycle time khác nhau).
- Ca đêm qua 0h: tính theo "ngày sản xuất", không theo ngày lịch.
- Performance > 100% → cycle time chuẩn sai → cảnh báo master data.
- Setup/đổi model: tính là downtime có kế hoạch hay không — cấu hình theo `DowntimeReason.CountsAgainstAvailability`.

**Entity**: `OeeSnapshot` (Machine, ShiftDate, Shift, Hour, PlannedTime, RunTime, TotalCount, GoodCount, A, P, Q, OEE).

**Các bước chi tiết**

1. Hàm tính OEE thuần (pure function) trong Domain + unit test với số liệu tính tay (ví dụ trong ca 8 giờ, nghỉ 60 phút, dừng 45 phút…).
2. Background job tính theo giờ và chốt cuối ca → `OeeSnapshot`. Ghi trade-off realtime vs định kỳ vào README (realtime: chính xác tức thời nhưng tốn tài nguyên; định kỳ: dễ kiểm chứng, phù hợp báo cáo).
3. Downtime chưa có lý do → danh sách "cần phân loại" cho supervisor.
4. Dashboard: OEE theo máy/ca/ngày, 3 thành phần A/P/Q, Pareto top 5 lý do dừng, sản lượng theo giờ so với kế hoạch.

**DoD**: số OEE trên dashboard khớp với tính tay cho 1 ca mẫu.

**Phỏng vấn**: "OEE tính thế nào? Máy 83% thì cải thiện ở đâu trước?"

---

## Module 6 — Material & Traceability **[SỬA — gộp nhập lot tối thiểu, làm trước Quality]**

**Nghiệp vụ phụ trách**: biết chính xác **sản phẩm nào dùng vật tư nào, trên máy nào, bởi ai, lúc nào**. Khi khách hàng khiếu nại 1 serial, nhà máy phải trả lời trong vài giờ: lỗi do lô linh kiện nào, còn bao nhiêu sản phẩm khác dùng lô đó (để recall hoặc hold). Đây là yêu cầu audit của hầu hết khách hàng ngành điện tử, ô tô.

**Thuật ngữ**: Lot, Serial, Genealogy, Truy xuất ngược (backward: thành phẩm → NVL), truy xuất xuôi (forward: NVL → thành phẩm), Feeder/Setup verification, FIFO.

**Entity chính**

| Entity | Cột quan trọng |
| --- | --- |
| `MaterialLot` | LotNo, ItemId, Qty, RemainingQty, SupplierLot, ReceivedAt, ExpiryAt, Status (Available/Hold/Consumed) |
| `MaterialLoad` | MachineId/StationId, LotNo, WoId, LoadedAt, UnloadedAt, LoadedBy — lot nào đang nạp trên máy |
| `SerialComponent` | SerialNo, LotNo, ItemId, Qty, StationId, ConsumedAt — thành phẩm dùng lot nào |
| `LotGenealogy` | ParentLotNo, ChildLotNo, Qty — cho bán thành phẩm theo lô |

**Quy tắc nghiệp vụ**

- Nạp lot lên máy: MES kiểm tra lot đúng Item trong BOM của WO (verification) và còn hạn; sai → chặn, báo đỏ.
- Khi serial được scan OK ở trạm có tiêu thụ vật tư (theo `BomLine.OperationSeq`) → ghi `SerialComponent` với lot đang nạp tại trạm, trừ `RemainingQty`.
- FIFO: gợi ý lot nhập sớm nhất; nạp lot mới hơn khi còn lot cũ → cảnh báo (có quyền supervisor override, ghi audit).

**Các bước chi tiết**

1. Màn hình nhập kho NVL tối thiểu: tạo lot, in nhãn lot.
2. Kiosk "nạp vật tư": scan máy + scan lot → verification.
3. Hook vào M3: khi scan OK → ghi `SerialComponent`.
4. Query truy xuất ngược và xuôi bằng recursive CTE; index `(LotNo)`, `(SerialNo)`. Ví dụ truy xuất xuôi:

   ```sql
   WITH Affected AS (
       SELECT sc.SerialNo FROM SerialComponent sc WHERE sc.LotNo = @LotNo
       UNION
       SELECT sc2.SerialNo
       FROM LotGenealogy g
       JOIN SerialComponent sc2 ON sc2.LotNo = g.ChildLotNo
       WHERE g.ParentLotNo = @LotNo
   )
   SELECT DISTINCT a.SerialNo, s.WoId, s.Status FROM Affected a JOIN Serial s ON s.SerialNo = a.SerialNo;
   ```
   (Genealogy nhiều cấp thì viết CTE đệ quy trên `LotGenealogy` trước rồi join.)
5. UI: nhập serial hoặc lot → cây truy xuất (máy, operator, thời gian, kết quả từng trạm) + nút "Hold tất cả" (dùng ở M7).
6. Đo hiệu năng truy xuất với dữ liệu lớn; mục tiêu < 1 giây.

**DoD**: kịch bản bước 8 của demo chạy đúng, kết quả khớp với dữ liệu seed.

**Phỏng vấn**: "Thiết kế traceability thế nào?", "Khách báo lỗi serial X, bạn làm gì trong 2 giờ đầu?"

---

## Module 7 — Quality: Inspection, SPC, NCR, Hold

**Nghiệp vụ phụ trách**: phát hiện bất thường sớm (SPC), ghi nhận và xử lý lỗi có hệ thống (NCR), và **chặn hàng nghi ngờ** không cho đi tiếp (Hold/Release). QC là người dùng chính.

**Thuật ngữ**: Inspection point, UCL/LCL (giới hạn kiểm soát — tính từ dữ liệu), USL/LSL (giới hạn kỹ thuật — theo bản vẽ), Cpk, NCR (Non-Conformance Report), Hold/Release, MRB (hội đồng xử lý hàng lỗi: dùng, sửa, hủy).

**Entity chính**: `InspectionPlan` (RoutingStep, Characteristic, USL, LSL, SampleSize, Frequency), `Measurement` (SerialNo/LotNo, Value, MeasuredAt, By), `SpcAlert`, `Ncr` (NcrNo, Source, DefectCode, Qty, AffectedSerials/Lots, Disposition, Status), `HoldRecord` (Target type/id, Reason, HeldBy, ReleasedBy).

**Quy tắc nghiệp vụ**

- Giá trị ngoài USL/LSL → sản phẩm NG ngay.
- UCL/LCL = trung bình ± 3σ trên dữ liệu ổn định; cảnh báo theo Western Electric rules (1 điểm ngoài 3σ; 7–8 điểm liên tiếp cùng phía đường trung tâm…).
- Serial/Lot bị Hold → route check (M3) chặn; lot Hold → không nạp được lên máy (M6).
- NCR đóng chỉ khi có disposition và người phê duyệt.

**Các bước chi tiết**

1. Nhập đo lường tại trạm kiểm tra (kiosk) hoặc sinh tự động từ simulator.
2. Hàm tính UCL/LCL, Cpk + unit test với bộ dữ liệu mẫu.
3. Rule engine đơn giản (ngưỡng trước, rule chuỗi điểm sau).
4. Control chart (OxyPlot trong WPF): điểm đo, đường tâm, UCL/LCL, đánh dấu điểm vi phạm.
5. NCR: tạo từ SPC alert hoặc thủ công; liên kết serial/lot lấy từ truy xuất M6.
6. Hold/Release hàng loạt từ màn hình truy xuất.

**DoD**: reel lỗi → truy xuất → Hold → scan serial bị hold ở trạm bị chặn.

---

## Module 8 — ERP Integration **[TÙY CHỌN — ngoài MVP, làm sau nếu có thời gian]**

> Không nằm trong kịch bản demo. Giữ lại để biết cách mở rộng; khi làm thì thêm RabbitMQ vào Docker Compose.

**Nghiệp vụ phụ trách**: MES không tồn tại một mình. ERP (SAP) gửi xuống lệnh sản xuất và master data; MES báo lên sản lượng hoàn thành, tiêu hao vật tư (backflush), phế phẩm. Ở KCN, lỗi interface là loại sự cố IT MES xử lý nhiều nhất (thiếu mã vật tư, sai đơn vị, gửi trùng, mất kết nối).

**Thiết kế**

- `MockErp`: console/API nhỏ có danh sách "production order" và nhận "confirmation".
- Chiều xuống: MockErp publish `ProductionOrderCreated` (RabbitMQ) → MES tạo WO Draft; mã hàng không tồn tại → ghi lỗi, không tạo WO nửa vời.
- Chiều lên: **Transactional Outbox** — khi WO Completed, ghi `OutboxMessage` trong cùng transaction; worker gửi sang RabbitMQ, retry với backoff; `Status`: 0 chờ, 1 đã gửi, 9 lỗi (cần người xử lý).
- Idempotency: mỗi message có `MessageId`; MockErp bỏ qua message đã nhận.
- Màn hình "Interface monitor": danh sách message lỗi, nút gửi lại.

**Các bước chi tiết**

1. Định nghĩa message contract (JSON) cho 3 loại: `ProductionOrder`, `ProductionConfirmation`, `GoodsIssue` (tiêu hao).
2. Consumer + outbox worker + retry policy (Polly).
3. Test: tắt RabbitMQ khi WO hoàn thành → bật lại → message được gửi đúng 1 lần.
4. Interface monitor UI.

**DoD** (khi làm): WO tạo từ MockErp qua queue; WO Completed gửi confirmation về MockErp đúng 1 lần.

**Phỏng vấn**: "Interface ERP lỗi giữa đêm thì bạn làm gì?", "Làm sao không gửi trùng confirmation?"

---

## Module 9 — Warehouse mở rộng

**Nghiệp vụ phụ trách**: quản lý tồn kho theo vị trí, cấp phát vật tư cho line (kitting), nhập thành phẩm. Ở nhà máy lớn phần này thường thuộc WMS riêng; MES chỉ cần biết vật tư đã xuống line.

**Các bước**: `Inventory` (Item, Lot, Location, Qty), phiếu xuất cho WO (issue), nhập thành phẩm (receipt) khi đóng gói, FIFO khi xuất, lịch sử giao dịch, kiểm kê đơn giản.

---

## Module 10 — Maintenance (CMMS rút gọn)

**Nghiệp vụ phụ trách**: bảo trì định kỳ (PM) để giảm dừng máy đột xuất, ghi nhận sửa chữa sự cố (BM), đo MTBF/MTTR. Liên kết trực tiếp với downtime và OEE.

**Các bước**:

1. `MaintenancePlan` theo giờ chạy (lấy từ `MachineStateHistory`) hoặc theo lịch.
2. Job sinh `MaintenanceOrder` khi tới hạn; máy quá hạn PM → cảnh báo trên dashboard (tùy chọn: chặn release WO vào máy đó).
3. Andon loại "hỏng máy" → tự tạo `MaintenanceOrder` BM.
4. Báo cáo MTBF, MTTR theo máy.

---

## 3. Thứ tự và ước lượng

| Giai đoạn | Module | Ước lượng cho người mới (giờ) | Mốc demo |
| --- | --- | --- | --- |
| MVP-1 | M0, M1 | 25 + 26 | Master data + explode BOM, 2 app WPF chạy |
| MVP-2 | M2, M3 | 26 + 27 | Release WO, in nhãn, scan qua trạm, NG/Repair |
| MVP-3 | M4, M5 | 22 + 22 | Máy giả lập realtime, Andon, OEE |
| MVP-4 | M6 | 25 | Truy xuất ngược/xuôi |
| Mở rộng | M7 | 26 | SPC, NCR, Hold từ truy xuất |
| Tùy chọn | M10, M9 | ~20 | Maintenance, Warehouse mở rộng |
| Sau này | M8 | ~20 | Interface ERP qua queue |
| Polish | README, ARCHITECTURE.md, video | 10 | Video demo 5 phút theo kịch bản mục 0 |

M0–M7 ≈ 200 giờ ≈ 20 tuần ở nhịp 10 giờ/tuần. Ước lượng đã tính thời gian học WPF/MVVM, MQTT, SignalR, thống kê SPC từ đầu. Nếu thiếu thời gian, dừng ở MVP-4 (≈ 175 giờ) vẫn đủ để phỏng vấn tốt.

## 4. Polish

- README: kiến trúc, ERD, `docker compose up`, tài khoản demo, GIF/video theo kịch bản.
- `ARCHITECTURE.md`: các quyết định kèm lý do — modular monolith, WPF gọi API thay vì nối thẳng DB, MQTT, nguồn đếm OEE, snapshot BOM/Routing, SQL Server thay vì time-series DB.
- `docs/perf.md`: số liệu đo route check, truy xuất trước/sau tối ưu.
- `docs/incidents.md`: 3–5 "sự cố" bạn gặp khi phát triển và cách xử lý — dùng kể chuyện khi phỏng vấn.
- Seed data + script `demo.ps1` chạy kịch bản tự động.

## 5. Quyết định đã chốt

| # | Câu hỏi | Mặc định nếu không đổi |
| --- | --- | --- |
| # | Câu hỏi | Đã chốt |
| --- | --- | --- |
| 1 | Ngành mô phỏng | Điện tử, truy xuất theo serial |
| 2 | ERP Integration | Không trong MVP; M8 làm sau nếu có thời gian. Tập trung phần mềm MES |
| 3 | Client | WPF (.NET 10, MVVM) cho Kiosk và Back-office *(đổi từ .NET 8, spec-v2 B10 #11)* |
| 4 | Nguồn dữ liệu OEE (chỉ có simulator) | Total Count từ bộ đếm máy (MQTT); Good/NG từ kết quả theo serial qua API scan (simulator vai trạm tự động hoặc người scan) |
| 5 | Ngôn ngữ UI | Tiếng Anh, chuỗi trong `.resx` |
