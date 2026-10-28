# M4b · Bước 4 — Chuẩn hóa và publish lên MQTT: kiến thức cần nắm

> Tài liệu học cho bước 4 của [module-04b](../../spec/module-04b-edge-gateway.md) (spec mục **2.6**; quy tắc G1, G6,
> G8–G10). Bước trước: [M4b-step03](M4b-step03-modbus.md). Hợp đồng MQTT là của M4 (spec module-04 mục 2.3).

---

## A. Nghiệp vụ

### A1. Mục tiêu: MES không biết máy nói giao thức gì

Sau bước này dashboard có 4 máy. MES nhận cả 4 qua **cùng một** `MqttIngestService` của M4, và ingest **không sửa dòng
nào** (quy tắc G1):

| Máy | Giao thức của máy | Đường tới MES | `source` trong message |
| --- | --- | --- | --- |
| L1-SMT-01, L1-TEST-01 | MQTT (máy "thông minh") | Máy → broker | `mqtt` |
| L1-REFLOW-01 | OPC UA | Lò → **gateway** → broker | `opcua` |
| L1-GLUE-01 | Modbus TCP | Máy dán keo → **gateway** → broker | `modbus` |

`source` chỉ để chẩn đoán. Ingest xử lý mọi nguồn như nhau. Thêm một loại PLC mới (S7, MC Protocol) chỉ là thêm một
driver trong gateway; MES không đổi.

### A2. Gateway nói thay cho máy, nhưng không nói dối

- Gateway gửi **heartbeat thay máy**, mỗi 5 giây, **chỉ khi đang đọc được máy**. Mất máy thì im lặng; sau 30 giây
  watchdog M4 cho máy Offline. Gateway không bao giờ tự đặt `Down` (G7).
- Cảm biến hỏng (OPC UA `BadSensorFailure`): gateway gửi **một lần** giá trị tốt gần nhất kèm `quality = Bad`. MES lưu
  lại để chẩn đoán, SPC (M7) bỏ qua giá trị Bad.

### A3. Đã kiểm tra trên hệ thống thật

| Kịch bản | Kết quả |
| --- | --- |
| `mosquitto_sub -t "minimes/#" -v` | `.../L1-GLUE-01/count`, `/state` (retained), `/heartbeat`; `.../L1-REFLOW-01/process/peakTemp`, `source` đúng |
| `ProcessValues` | Có dòng `L1-REFLOW-01 peakTemp 244,45 ...` |
| `down L1-GLUE-01 75` | State history Running → **Down** → Running; Andon `RaisedBy = system` sau ~63 s (X = 60 s + chu kỳ watcher 5 s); alarm `999` |
| `reset L1-GLUE-01` | `MachineEvents`: `Counter 0, Delta 0, IsReset 1`, bo sau `Delta 1` |
| Snapshot dashboard `GET /api/lines/L1/status` | 4 máy, `dataSource` Mqtt / OpcUa / Modbus, lò `peakTemp 244,66 °C` |

---

## B. Thiết kế

```
Drivers ─RawValue/ConnectionChanged─┐
PipelineService.TickAsync ─Tick 1s─┤
                                    ▼
                    Channel<DeviceEvent> (1 reader: PipelineService)
                       │ RawValue         → Normalizer.Accept(raw, now) → 0..1 Outgoing ─┐
                       │ ConnectionChanged→ HealthPublisher (bật/tắt), Normalizer.Disconnected │
                       │ Tick             → Normalizer.Refresh(now) (30/60 s)        ─────────┤
                       │                    HealthPublisher.Due(now) (heartbeat 5 s, QoS 0) ──┤
                       ▼                                                                       ▼
                                                                   GatewayMqttClient.PublishAsync (QoS 1, PUBACK)
```

| File | Việc |
| --- | --- |
| [Pipeline/Normalizer.cs](../../src/MiniMes.EdgeGateway/Pipeline/Normalizer.cs) | Bảng 2.6: gửi gì, lên topic nào, khi nào |
| [Pipeline/MessageIdFactory.cs](../../src/MiniMes.EdgeGateway/Pipeline/MessageIdFactory.cs) | msgId = SHA-256(machine, tag, ts, value) |
| [Pipeline/PipelineService.cs](../../src/MiniMes.EdgeGateway/Pipeline/PipelineService.cs) | Reader duy nhất của channel, tick 1 giây |
| [Health/HealthPublisher.cs](../../src/MiniMes.EdgeGateway/Health/HealthPublisher.cs) | Heartbeat 5 giây cho máy đang kết nối |
| [Mqtt/GatewayMqttClient.cs](../../src/MiniMes.EdgeGateway/Mqtt/GatewayMqttClient.cs) | Kết nối broker, tự reconnect, publish |

### B1. Bảng chuẩn hóa (spec 2.6)

| `kind` | Topic | Trường | Gửi khi | Retain |
| --- | --- | --- | --- | --- |
| `count` | `.../count` | `counter` | Đổi; và ≥ 30 s/lần | Không |
| `state` | `.../state` | `state` qua `map` (`3` → `Down`) | Chỉ khi đổi | **Có** |
| `alarm` | `.../alarm` | `alarmCode` (`0` → `null` = hết alarm) | Chỉ khi đổi | Không |
| `process` | `.../process/{tag}` | `tag`, `value`, `quality` | Vượt deadband; và ≥ 60 s/lần | Không |
| heartbeat | `.../heartbeat` | `state` | 5 s, chỉ máy đang kết nối | Không, QoS 0 |

### B2. Vì sao một channel cho cả tick

Normalizer giữ trạng thái "đã gửi gì, lúc nào" trong `Dictionary`. Nếu timer gọi `Refresh` trên thread khác trong khi
pipeline đang gọi `Accept`, hai bên ghi cùng một dictionary thì phải khóa. Thay vào đó, timer chỉ **viết một
`PipelineTick` vào channel**; reader duy nhất xử lý tuần tự giá trị, sự kiện kết nối và tick. Không có khóa nào. Đây là
mô hình "actor" đơn giản.

---

## C. Kiến thức kỹ thuật

### C1. msgId cố định theo nội dung (G6)

```csharp
SHA256.HashData(UTF8("{machine}|{tag}|{ts:O}|{value}")) → 16 byte đầu → Guid
```

- OPC UA phát lại notification cũ sau reconnect → cùng nội dung → cùng msgId → MES bỏ (M4 có cache + unique index).
- Bước 5: gateway chết sau khi broker nhận nhưng trước PUBACK → gửi lại đúng payload trong outbox → cùng msgId.
- "Ít nhất một lần" trên đường truyền, cộng với MES idempotent, cho ra "đúng một lần" trong DB.

### C2. Hai đồng hồ: của máy và của gateway (lỗi bắt được khi tự rà code)

- `ts` trong message = **giờ nguồn** (OPC UA `SourceTimestamp`, Modbus = giờ gateway lúc đọc) theo G9.
- Mốc "đã gửi lúc nào" cho luật 30/60 s = **giờ gateway**. Bản nháp đầu dùng giờ PLC: PLC lệch 2 giờ thì refresh hoặc
  không bao giờ chạy, hoặc chạy ở mọi tick. Có test `A_skewed_plc_clock_does_not_change_the_refresh_rhythm`.
- Bản refresh định kỳ (OPC UA không gửi gì khi giá trị đứng yên) có `ts` = giờ gateway lúc xác nhận, nên msgId mới, MES
  không coi là trùng.

### C3. Quy tắc G8 trong giới hạn của hợp đồng M4

Spec nói "Bad vẫn gửi", nhưng ingest M4 **từ chối** `process` thiếu `value` và **bỏ** `count` Bad. Không sửa ingest (mục
tiêu của bước này), nên:

| Kind | Bad thì |
| --- | --- |
| `process` | Gửi **một lần** giá trị tốt gần nhất + `quality = Bad`; Good trở lại thì gửi ngay (dù trong deadband) |
| `count`, `state`, `alarm` | Không gửi: giá trị không xác định, và ingest cũng bỏ |

### C4. Chuyển kiểu giá trị

- `RawValue.Value` giữ kiểu của thiết bị (`uint`, `ushort`, `int`, `float`, `double`). Normalizer đổi sang `long`
  (counter), `MachineState` (state), chuỗi (alarm), `double` (process).
- `float` → `double` đi qua **chuỗi ngắn nhất** (`"R"`): `45.1f` thành `45.1`, không phải `45.099998474121094`.

### C5. Mất máy rồi có lại

`Normalizer.Disconnected(machine)` xóa mọi "đã gửi" của máy đó:

- Không refresh "vẫn 8002" cho một máy không đọc được.
- `CurrentState` = null nên không có heartbeat.
- Nối lại thì giá trị đầu tiên của mỗi tag đi ra ngay. Trùng thì MES tự bỏ nhờ msgId.

### C6. Còn thiếu (các bước sau)

- Broker vắng mặt: message bị log `NOT SENT` và **mất**. Bước 5 thêm outbox SQLite (ghi trước, xóa sau PUBACK).
- Mất máy: hiện chỉ dựa vào watchdog 30 giây. Bước 6 publish `status = offline` để MES biết trong ≤ 5 giây, và thêm LWT
  cho chính gateway.

---

## D. Test

`tests/MiniMes.UnitTests/Gateway/`:

- `NormalizerTests`: topic, retain, map state, alarm bật/tắt, deadband (biên 0,5), refresh 30/60 s, PLC lệch giờ,
  Bad sensor, float, msgId lặp lại được, mất kết nối, tag lạ.
- `HealthAndMessageIdTests`: cùng nội dung cùng msgId, khác bất kỳ phần nào thì khác msgId; heartbeat 5 giây, bỏ máy
  chưa có state và máy mất kết nối.

Tổng 777 test xanh.

---

## E. Câu hỏi phỏng vấn

1. **"Thêm một máy Siemens S7 thì MES phải sửa gì?"** — Không sửa gì. Gateway thêm một driver; mọi máy ra cùng một hợp
   đồng MQTT. Bằng chứng: ở bước này, máy OPC UA và Modbus vào MES mà ingest không đổi dòng nào.
2. **"Gửi lại thì sao MES không đếm hai lần?"** — msgId từ nội dung; MES khử trùng theo msgId. At-least-once kèm
   idempotent.
3. **"Nhiệt độ thay đổi liên tục, gửi hết à?"** — Deadband: chỉ gửi khi đổi quá 0,5 °C, và vẫn gửi ít nhất mỗi 60
   giây để MES biết giá trị còn đúng. Giảm 90–99 % message (G10).
4. **"Gateway biết máy dừng không?"** — Nó chỉ biết đọc được hay không. Không đọc được thì ngừng heartbeat, không đoán
   `Down`.
5. **"Có hai đồng hồ, anh dùng cái nào?"** — Giờ nguồn cho `ts` (đo lúc nào); giờ gateway cho logic nhịp gửi. Lẫn hai
   cái thì PLC lệch giờ làm hỏng nhịp gửi.

---

## F. Đọc thêm

- Spec module-04 mục 2.3 (hợp đồng MQTT), 2.4 (khử trùng msgId), 2.5 (Offline watchdog).
- HiveMQ "MQTT Essentials" (QoS, retained message, LWT).
- learn.microsoft.com: `System.Security.Cryptography.SHA256.HashData`, "Standard numeric format strings" (định dạng `R`).
