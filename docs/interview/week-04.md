# Tuần 4: Kết nối máy, IoT và Andon (M4, M4b)

> **Mục tiêu tuần:** đây là mảng IT MES ở KCN làm **hằng ngày** và cũng là mảng người phỏng vấn đánh giá cao nhất, vì ít ứng viên web làm được. Cuối tuần bạn phải:
> - nói được dữ liệu đi từ PLC tới dashboard qua những chặng nào;
> - với mỗi chặng, biết hỏng thì triệu chứng là gì và kiểm tra bằng công cụ gì.
>
> **Thời lượng:** ~10 giờ, gồm đọc 3 · lab 3 · drill 2 · nói 2.

## 0. Ngoài đời việc này diễn ra thế nào

- **Máy SMT đời mới** (Fuji, Panasonic, Yamaha…) thường có giao diện riêng của hãng, ví dụ chuẩn **IPC-CFX** (dựa trên AMQP) hoặc **IPC-HERMES** (giao tiếp giữa các máy). Nhiều nhà máy dùng thêm phần mềm trung gian hoặc tự viết bộ chuyển sang MQTT.
- **Lò reflow và máy có PLC Siemens/Omron đời mới:** thường đọc qua **OPC UA**.
- **Máy cũ, bộ đếm, cảm biến:** **Modbus TCP**, hoặc Modbus RTU qua bộ chuyển RS-485 sang Ethernet. Muốn biết đọc thanh ghi nào thì phải đọc **manual của máy**.
- **Edge gateway:** một PC công nghiệp trong tủ điện ở line, chạy dịch vụ đọc máy rồi đẩy lên server. Lý do: không để server MES mở kết nối trực tiếp xuống mạng máy (mạng OT tách với mạng IT, có firewall).
- **Ticket hay gặp:**
  - "dashboard máy xám";
  - "sản lượng máy khác sản lượng scan";
  - "số đếm nhảy lên mấy tỷ";
  - "sau khi bảo trì đổi PLC thì mất dữ liệu";
  - "mất mạng 10 phút thì OEE ca đó sai".
- **Người phối hợp:**
  - kỹ sư tự động hóa / bảo trì (biết PLC);
  - IT hạ tầng (mạng, VLAN, firewall);
  - vendor máy (đổi cấu hình máy, cấp tài liệu thanh ghi).

## 1. Đọc (3 giờ)

- [ ] `docs/learning/M4b-step00-opcua-modbus-basics.md`: đọc kỹ nhất.
- [ ] `docs/opcua-modbus-notes.md`.
- [ ] `spec/module-04-machine-connectivity.md`, phần mở đầu: các tầng ISA-95; khác nhau giữa Down và Offline; Andon.
- [ ] `spec/module-04b-edge-gateway.md`, phần mở đầu.
- [ ] `ARCHITECTURE.md`:
  - ADR-007: nguồn dữ liệu OEE;
  - ADR-008: mọi thứ đi qua MQTT;
  - ADR-009: gateway tự viết, subscription và polling, outbox SQLite.
- [ ] `docs/learning/M4-step07*` (Andon và leo thang), `M4b-step05-store-and-forward.md`, `M4b-step06-health-status-lwt.md`.

### Đường đi của dữ liệu (vẽ lại tay, rồi tự giải thích không nhìn)

```
PLC/máy ──(OPC UA subscribe | Modbus poll 1 s | MQTT trực tiếp)──► Edge Gateway ──► outbox SQLite
   ──(MQTT QoS 1, xóa khỏi outbox sau PUBACK)──► Mosquitto ──► MqttIngestService (API)
   ──► MachineEvents / MachineStateHistory (SQL) ──► SignalR ──► Dashboard WPF, Line TV
```

Topic theo Unified Namespace: `minimes/VN-BN01/SMT-ASSY/L1/L1-SMT-01/state | count | alarm | heartbeat | process/{tag}`.

### Bảng leo thang Andon (seed, `MasterDataSeeder.AndonEscalationRules`)

| Loại | Cấp | Sau | Gọi ai |
| --- | --- | --- | --- |
| Machine | 0 | lúc raise | Supervisor, Maintenance |
| Machine | 1 | 3 phút chưa Ack | Admin (quản đốc xưởng) |
| Machine | 2 | 10 phút | Admin (quản lý sản xuất), log Error |
| Quality | 1 | 5 phút chưa Ack | QC |

Máy Down ≥ 60 giây thì hệ thống tự raise Andon, nếu chưa có Andon mở cho máy đó.

## 2. Lab (3 giờ)

1. Chạy `docker compose up -d`, API, `dotnet run --project src/MiniMes.MachineSimulator`, `dotnet run --project src/MiniMes.EdgeGateway`. Mở BackOffice → **Line dashboard**.
2. Cài **MQTT Explorer** (miễn phí), nối `localhost:1883`, subscribe `minimes/#`:
   - xem `state` là **retained**, `count` là bộ đếm **tích lũy**, `heartbeat` 5 giây một lần;
   - xem payload có `source` (`mqtt`/`opcua`/`modbus`) và `quality`.
3. **OPC UA:** chạy

   ```powershell
   dotnet run docs/learning/labs/opcua-lab.cs -- opc.tcp://localhost:4840 0.5 20 urn:minimes:simulator:reflow Reflow.PeakTemp Reflow.Counter Reflow.State
   ```

   Hoặc dùng UaExpert. Ở simulator gõ `drift L1-REFLOW-01 1` và nhìn `PeakTemp` tăng.
4. **Modbus:** chạy

   ```powershell
   dotnet run docs/learning/labs/modbus-lab.cs -- 5021
   ```

   Lab này tự dựng **cả slave lẫn master** trong một process. Dùng cổng 5021 để không đụng slave của simulator ở 5020. Nó cố tình mắc các lỗi kinh điển: sai thứ tự word, lệch địa chỉ 1 (40001 là địa chỉ 0), đọc nhầm FC04 thay vì FC03. Đọc output, rồi đối chiếu với khối `L1-GLUE-01` trong `src/MiniMes.EdgeGateway/gateway.tags.json`: `startAddress`, `count`, `wordOrder: CDAB`.
5. **Andon:**
   - gõ `down L1-SMT-01 300` ở simulator;
   - xem ô máy đỏ, rồi Andon tự raise sau 60 giây, rồi leo thang cấp 1 sau 3 phút;
   - đăng nhập `sup01` Ack, Resolve với lý do `FDR-JAM`.
6. **SQL:**

Lưu ý: các bảng máy lưu enum dạng **số** (byte), khác với bảng WO/serial lưu chuỗi. Bảng mã:
- `State`: 0 Running, 1 Idle, 2 Down, 3 Setup, 4 PlannedStop, 5 Offline;
- `Kind`: 1 State, 2 Count, 3 Alarm, 4 Connection.

Xem `src/MiniMes.Domain/Machines/MachineEvent.cs`. Ngoài đời, đọc DB của MES vendor cũng hay gặp kiểu này: phải tìm bảng mã trước khi viết SQL.

```sql
-- Lịch sử trạng thái máy hôm nay
SELECT m.Code, CHOOSE(h.State + 1, 'Running','Idle','Down','Setup','PlannedStop','Offline') AS State, h.StartAt, h.EndAt, DATEDIFF(second, h.StartAt, ISNULL(h.EndAt, SYSUTCDATETIME())) AS Sec, r.Code AS Reason
FROM MachineStateHistory h JOIN Machines m ON m.Id = h.MachineId LEFT JOIN DowntimeReasons r ON r.Id = h.DowntimeReasonId
WHERE h.StartAt >= CAST(SYSUTCDATETIME() AS date) ORDER BY h.StartAt DESC;

-- Bộ đếm: delta từng message, có reset không
SELECT TOP 50 m.Code, e.MachineTs, e.ReceivedAt, e.Counter, e.Delta, e.IsReset
FROM MachineEvents e JOIN Machines m ON m.Id = e.MachineId WHERE e.Kind = 2 /* Count */ ORDER BY e.Id DESC;

-- Andon: thời gian phản hồi (phút)
SELECT m.Code, a.Type, a.Status, a.EscalationLevel, a.RaisedAt,   -- Type/Status cũng là số: tra enum trong Domain/Andon
       DATEDIFF(second, a.RaisedAt, a.AckAt) / 60.0 AS MinToAck, DATEDIFF(second, a.RaisedAt, a.ResolvedAt) / 60.0 AS MinToResolve
FROM AndonCalls a JOIN Machines m ON m.Id = a.MachineId ORDER BY a.Id DESC;
```

## 3. Drill sự cố (2 giờ)

### Drill A: "Mất mạng giữa gateway và server 1 phút. Có mất sản lượng không?"
**Cách tạo:** gateway đang chạy thì `docker stop mes-mosquitto`, chờ 60–90 giây, rồi `docker start mes-mosquitto`.

**Quan sát:**
- log gateway;
- file `data/outbox.db` (trong thư mục chạy gateway): mở bằng DB Browser for SQLite, xem bảng `Outbox` đầy lên rồi rỗng;
- `MachineEvents` của `L1-REFLOW-01` / `L1-GLUE-01`: các message gửi bù có `MachineTs` đúng giờ máy.

<details><summary>Đáp án gợi ý</summary>

- Gateway **ghi mọi message vào outbox trước**, chỉ xóa sau khi broker trả PUBACK (QoS 1). Mất broker thì message nằm lại; có mạng thì gửi bù theo thứ tự.
- **Không mất số đếm** vì bộ đếm là **tích lũy**: kể cả có message mất, message kế tiếp vẫn mang tổng; delta tính bằng hiệu hai giá trị.
- **Gửi trùng** (gửi rồi mà chưa nhận PUBACK) không nhân đôi, vì `msgId` là hash nội dung và ingest bỏ message trùng.
- **Bẫy đã gặp** (incident "Gửi bù đủ message nhưng sản lượng theo giờ vẫn sai"): message gửi muộn bị coi là "đồng hồ PLC sai" và bị dời giờ. Đã sửa: message từ gateway cũ tới 7 ngày (tuổi tối đa của outbox) được giữ đúng giờ máy.
- **Ngoài đời:** hỏi "outbox chứa được bao lâu?" Ở đây cấu hình `maxRows 500000`, `maxAgeDays 7`.
</details>

### Drill B: "Dashboard máy lò reflow xám (Offline), bảo trì nói máy vẫn chạy"
**Cách tạo:** ở simulator gõ `opcua stop`. Thử thêm: tắt gateway (Ctrl+C), hoặc `docker stop mes-mosquitto`.

<details><summary>Đáp án gợi ý</summary>

Offline là **mất kết nối**, không phải máy hỏng (máy hỏng là **Down**). Đi từng chặng:
1. **Máy ↔ gateway:** log gateway có lỗi kết nối OPC UA/Modbus không? `Test-NetConnection <ip-máy> -Port 4840` (Modbus là 502, ở lab là 5020).
2. **Gateway sống không:** topic `minimes/VN-BN01/_sys/gateway/GW01/status` (LWT) trên MQTT Explorer. Gateway chết thì broker phát `offline` thay nó.
3. **Gateway ↔ broker:** outbox có đang đầy lên không?
4. **Broker ↔ API:** log API (`MqttIngestService`). Các máy MQTT khác có online không?

**Bài học từ incident:** tắt broker **không** được làm máy Offline oan. Báo cáo `offline` qua LWT chỉ được xác nhận sau 3 giây, và chỉ khi ingest vẫn giữ cùng phiên kết nối.
</details>

### Drill C: "Máy dán keo báo 2,26 tỷ sản phẩm sau khi bảo trì đổi PLC"
**Cách tạo:** sửa `wordOrder` của `L1-GLUE-01` trong `gateway.tags.json` từ `CDAB` sang `ABCD`, rồi restart gateway. **Nhớ sửa lại sau khi xong.**

<details><summary>Đáp án gợi ý</summary>

- Bộ đếm 32-bit nằm trên **2 thanh ghi 16-bit**. Mỗi hãng PLC xếp word theo thứ tự khác nhau. Ghép sai thì giá trị nhân 65.536.
- **Cách tìm:** đọc thô hai thanh ghi rồi tự ghép tay theo hai cách; cách nào ra số hợp lý thì đó là thứ tự đúng.
  - Ngoài đời đọc thô bằng Modbus Poll hoặc `mbpoll`.
  - Trong dự án, output của `modbus-lab.cs` in đúng phép thử này cho cả 4 thứ tự.
  - Ví dụ: HR0 = `0x86A0`, HR1 = `0x0001`. Ghép CDAB ra `0x0001_86A0` = 100.000; ghép ABCD ra `0x86A0_0001` ≈ 2,26 tỷ.
- **Phòng ngừa:** ingest đánh `Bad` cho giá trị ngoài khoảng hợp lý; khi thay PLC thì có checklist đối chiếu với manual.
- Đây là incident "cố tình tái hiện" trong `docs/incidents.md`, một câu chuyện rất tốt để kể khi phỏng vấn.
</details>

## 4. Câu hỏi phỏng vấn tuần 4

<details><summary>1. OPC UA, Modbus, MQTT khác nhau thế nào? Khi nào dùng cái nào?</summary>

- **Modbus TCP:**
  - rất cũ, đơn giản, chỉ có thanh ghi 16-bit, không có tên tag, không bảo mật;
  - client phải **poll** liên tục;
  - dùng cho máy cũ và bộ đếm.
- **OPC UA:**
  - có mô hình dữ liệu (node, kiểu, tên), có bảo mật (chứng chỉ);
  - có **subscription**: server tự gửi khi giá trị đổi quá deadband;
  - dùng cho PLC đời mới.
- **MQTT:**
  - giao thức publish/subscribe qua broker, nhẹ, hợp để **chuyển tiếp** dữ liệu lên server và cho nhiều bên cùng nghe;
  - không định nghĩa nội dung, nên cần quy ước topic/payload (UNS).

Trong dự án: gateway đọc OPC UA/Modbus rồi publish MQTT.
</details>

<details><summary>2. Subscription và polling khác gì? Ưu nhược điểm?</summary>

- **Polling:** client hỏi định kỳ (Modbus 1 giây).
  - Đơn giản.
  - Tốn băng thông, và bỏ lỡ thay đổi ngắn hơn chu kỳ.
- **Subscription:** server tự báo khi đổi.
  - `samplingMs` là tần suất server lấy mẫu; `publishingMs` là tần suất gửi; `deadband` là ngưỡng thay đổi tối thiểu.
  - Ít tải, nhanh hơn.
  - Cần keep-alive để biết server còn sống.
</details>

<details><summary>3. Mất mạng thì dữ liệu máy đi đâu? Làm sao không mất?</summary>

- **Store-and-forward:** outbox SQLite trên gateway, gửi QoS 1, xóa sau PUBACK.
- **Bộ đếm tích lũy** thay vì gửi "+1".
- **Bỏ trùng** bằng `msgId`.

Kể drill A kèm số liệu: không mất số đếm, `SUM(Delta)` khớp hiệu bộ đếm đầu và cuối.
</details>

<details><summary>4. Vì sao dùng gateway mà không để MES đọc PLC trực tiếp?</summary>

1. **Bảo mật:** mạng OT tách khỏi mạng IT; chỉ gateway đứng ở biên.
2. **Mất mạng** giữa line và server không mất dữ liệu (đệm tại chỗ).
3. **Tách trách nhiệm:** đổi máy, đổi giao thức chỉ sửa cấu hình gateway, MES không biết.
4. **Tải:** server không phải giữ hàng trăm kết nối PLC.
</details>

<details><summary>5. Unified Namespace (UNS) là gì?</summary>

- Một **cây topic chung** đặt theo cấp ISA-95 (site/area/line/machine), là "nguồn sự thật" về trạng thái nhà máy hiện tại.
- Ai cần thì subscribe: MES, dashboard, data lake.
- Thay vì nối điểm–điểm giữa từng hệ thống.
</details>

<details><summary>6. Down và Offline khác nhau thế nào? Ai xử lý?</summary>

- **Down:** máy báo nó đang dừng (lỗi feeder, kẹt board…). Bảo trì xử lý; tính vào downtime OEE.
- **Offline:** MES **không nghe** được máy (mạng, gateway, PLC tắt). IT MES / tự động hóa xử lý; không tự raise Andon máy.
- Phát hiện Offline bằng heartbeat 5 giây và LWT (Last Will) của MQTT.
</details>

<details><summary>7. Andon là gì? Leo thang hoạt động thế nào?</summary>

- Andon (từ Toyota) là tín hiệu gọi người hỗ trợ ngay khi có vấn đề.
- **Trong dự án:**
  - operator bấm ANDON trên kiosk, hoặc máy Down ≥ 60 giây thì tự raise;
  - SignalR đẩy lên dashboard trong vài chục ms;
  - quá 3 phút chưa Ack thì lên quản đốc; 10 phút lên quản lý sản xuất;
  - Resolve phải chọn lý do dừng, để có dữ liệu cho Pareto tuần 5.
</details>

<details><summary>8. MQTT QoS 0/1/2 và retained message?</summary>

- **QoS 0:** gửi một lần, có thể mất.
- **QoS 1:** ít nhất một lần, có thể trùng, nên phải bỏ trùng. Dự án dùng cho count.
- **QoS 2:** đúng một lần, chậm, ít dùng.
- **Retained:** broker giữ message cuối của topic; client mới subscribe nhận ngay. Dùng cho `state`, để dashboard mở ra thấy trạng thái ngay.
- **LWT:** broker tự phát hộ khi client chết bất thường.
</details>

<details><summary>9. Đồng hồ PLC sai thì sao?</summary>

- Dùng timestamp của máy nếu hợp lý, lệch quá ngưỡng thì dùng giờ nhận.
- Nhưng phải phân biệt "gửi muộn sau mất mạng" với "đồng hồ sai" (incident M4b bước 5).
- **Ngoài đời:** đồng bộ NTP cho PLC/gateway là việc phải đề xuất.
</details>

<details><summary>10. Gateway chạy thế nào ngoài đời? Chết thì sao?</summary>

- Chạy như **Windows Service** (`sc.exe create … start= auto`), cấu hình tự khởi động lại khi chết (`sc.exe failure`). Lệnh có trong README phần M4b.
- Có LWT để MES biết gateway chết.
- Thư mục outbox cố định, không đặt trong thư mục tạm.
- Chứng chỉ OPC UA trong `pki`; **không** để `autoAccept` ở production.
</details>

## 5. Tự kiểm cuối tuần

- [ ] Vẽ lại đường đi dữ liệu và nói được triệu chứng khi hỏng ở **từng** chặng.
- [ ] Làm đủ 3 drill. Drill C: tự ghép tay 2 thanh ghi ra số đúng.
- [ ] Giải thích được cho người không biết IT vì sao "máy xám" không phải "máy hỏng".
- [ ] Trả lời ≥ 8/10 câu.
