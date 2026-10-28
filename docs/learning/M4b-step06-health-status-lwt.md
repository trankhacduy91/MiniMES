# M4b · Bước 6 — Health: status máy, status gateway, LWT: kiến thức cần nắm

> Tài liệu học cho bước 6 của [module-04b](../../spec/module-04b-edge-gateway.md) (spec mục **1.2f, 2.8**; quy tắc G7).
> Bước trước: [M4b-step05](M4b-step05-store-and-forward.md). M4 đã có watchdog 30 s, LWT của máy MQTT, `status`.

---

## A. Nghiệp vụ

### A1. Ba sự cố, ba cách hiển thị, ba người xử lý

| Sự cố | Ai biết | Dashboard | Ai xử lý | OEE |
| --- | --- | --- | --- | --- |
| **Máy Down** (kẹt bo, hỏng đầu hút) | PLC báo state 3 | Ô **đỏ**, Andon tự bật sau 60 s | Bảo trì | Trừ Availability |
| **Mất máy** (PLC tắt, cáp đứt, firmware) | Gateway: không đọc được | Ô **xám Offline** trong ≤ 5 s; thẻ gateway "1/2 machines connected"; **không Andon** | IT / tự động hóa | Khoảng Offline xử lý riêng (M5) |
| **Gateway chết** | Broker (LWT) | Thẻ gateway **đỏ OFFLINE** ngay; máy của nó xám sau 30 s; máy MQTT trực tiếp không đổi | IT | Như trên |

Gateway **không bao giờ** tự đặt `Down` khi mất máy (G7): nó không biết máy có đang chạy hay không. Gọi nhầm bảo trì
và trừ Availability sai là hậu quả của việc "đoán".

### A2. Kết quả đo thật (28/10/2026)

| Kịch bản | Kết quả |
| --- | --- |
| `opcua stop` (3 lần) | Lò **Offline sau 4,1–4,6 s**; L1-GLUE-01, L1-SMT-01 không đổi; **0 Andon** |
| `opcua start` | Lò Running lại sau 2,5–4,6 s; state history Running → Offline → Running |
| Kill gateway (Task Manager) | Thẻ GW01 **offline ngay (0 s)**; L1-GLUE-01, L1-REFLOW-01 Offline sau ~28–30 s; L1-SMT-01 không bị ảnh hưởng |
| Chạy lại gateway | Online sau 1 s; 2 máy Running sau 1–4,6 s |

---

## B. Thiết kế

```
Gateway                                                  Broker                         MES
 driver mất PLC ─ConnectionChanged─▶ pipeline
   ├ outbox: .../{machine}/status = "offline" (retained)  ─────────────▶ ingest: ReportOffline → watchdog xác nhận 3 s → Offline
   ├ heartbeat của máy dừng
   └ GatewayStatusPublisher.Trigger()
 GatewayStatusPublisher (30 s + khi có thay đổi)
   └ minimes/{site}/_sys/gateway/{id}/status (retained) ─────────────▶ ingest nhánh _sys → GatewayStatusStore → SignalR GatewayStatusChanged
 MQTT connect: LWT = {"gateway":"GW01","state":"offline"}  ──(gateway chết)──▶ broker tự publish LWT ─▶ thẻ đỏ
```

| Phía | File | Việc |
| --- | --- | --- |
| Hợp đồng | [Contracts/Machines/GatewayContracts.cs](../../src/MiniMes.Contracts/Machines/GatewayContracts.cs) | `GatewayStatusMessage`, `GatewayTopic` |
| Gateway | [Health/GatewayStatusPublisher.cs](../../src/MiniMes.EdgeGateway/Health/GatewayStatusPublisher.cs) | Status gateway + "offline" khi dừng gọn |
| Gateway | [Mqtt/GatewayMqttClient.cs](../../src/MiniMes.EdgeGateway/Mqtt/GatewayMqttClient.cs) | LWT, sự kiện `Connected` |
| Gateway | [Pipeline/PipelineService.cs](../../src/MiniMes.EdgeGateway/Pipeline/PipelineService.cs) | `status` máy qua outbox |
| MES | [Application/Machines/GatewayStatusStore.cs](../../src/MiniMes.Application/Machines/GatewayStatusStore.cs) | Đọc payload (hàm thuần), lưu RAM, lọc theo line |
| MES | [Infrastructure/Machines/MqttIngestService.cs](../../src/MiniMes.Infrastructure/Machines/MqttIngestService.cs) | Nhánh `_sys` **trước** khi tra máy |
| Client | [ViewModels/Machines/GatewayCardViewModel.cs](../../src/MiniMes.Client.Core/ViewModels/Machines/GatewayCardViewModel.cs) | Thẻ gateway |

### B1. Phần MES phải sửa: chỉ một nhánh

Topic `minimes/VN-BN01/_sys/gateway/GW01/status` có 6 cấp, nên **khớp luôn** subscription `minimes/+/+/+/+/+` của ingest
M4; không cần subscribe thêm. Nhưng nếu đi đường cũ, ingest sẽ tra `_sys` như một area và `GW01` như một máy, rồi
bỏ với lý do "no active machine". Vì vậy nhánh `GatewayTopic.TryParseStatus` đứng **đầu** `OnMessageAsync`. Dữ liệu máy
vẫn đi đúng đường cũ.

### B2. Thứ tự là thứ cần giữ

- `status` của máy đi **qua outbox**, cùng hàng với dữ liệu: "offline" luôn đến **sau** giá trị cuối cùng đọc được.
  Gửi thẳng thì có thể vượt mặt dữ liệu còn trong hàng đợi.
- Status **gateway** thì gửi thẳng: status cũ gửi bù là thông tin sai.

---

## C. Kiến thức kỹ thuật

### C1. LWT (Last Will and Testament) của MQTT

- Khai báo **lúc kết nối**: topic, payload, QoS, retain. Broker giữ nó.
- Broker publish LWT khi client **mất kết nối không chào** (process bị kill, cáp đứt, keep-alive hết hạn).
  Disconnect gọn (gửi DISCONNECT) thì **không** có LWT. Vì vậy khi dừng gọn, gateway phải tự publish "offline"
  (`GatewayStatusPublisher.StopAsync`). Nó được đăng ký sau client MQTT, mà hosted service dừng theo thứ tự ngược, nên
  "offline" kịp đi trước khi client ngắt.
- Retained: dashboard mở sau đó (hoặc API restart) vẫn thấy "offline" ngay.
- Kill bằng Task Manager: hệ điều hành đóng socket (RST), broker thấy ngay, nên LWT đến sau 0 s. Rút cáp: broker phải
  chờ hết keep-alive (1,5 × 15 s) mới biết.

### C2. Status gateway: JSON cả khi offline

LWT chỉ mang `{"gateway":"GW01","state":"offline"}`, không có `devices`. MES giữ danh sách máy của lần status trước
và đánh dấu tất cả `disconnected`, nên thẻ vẫn biết gateway này phụ trách máy nào (để lọc theo line).

### C3. Offline ≤ 5 giây: cộng các khoảng chờ

```
server OPC UA dừng ──(keep-alive ≤ 1 s)──▶ gateway DISCONNECTED ──outbox ms──▶ MES ReportOffline ──(xác nhận 3 s)──▶ Offline
```

- Vì sao MES chờ 3 s xác nhận: khi broker tắt, nó publish LWT của **mọi** client; nếu tin ngay thì cả nhà máy
  "Offline" chỉ vì broker restart (M4). Xác nhận = không có message nào của máy sau báo cáo, và kết nối của ingest vẫn
  sống.
- Gateway ngừng heartbeat và ngừng gửi dữ liệu của máy đó, nên điều kiện xác nhận được thỏa.

### C4. OPC UA: server tắt có báo trước (sự cố gặp thật)

`StandardServer.StopAsync` mặc định chờ `ShutdownDelay = 5 s`: báo `ServerState.Shutdown` cho client rồi mới đóng, và
trong lúc đó **vẫn nhận session mới**. Kết quả lần đo đầu: Offline sau 7–9 s, và lò nháy Offline → online → Offline vì
gateway nối lại vào server đang tắt dần. Sửa:

1. Gateway: keep-alive có `CurrentState` là `Shutdown`/`Failed` thì coi là mất máy ngay. **Không** tính `Unknown`:
   ngay sau khi server khởi động, keep-alive có thể chưa có state.
2. Simulator: `ShutdownDelay = 0` (lệnh `opcua stop` = "PLC bị tắt").

Chi tiết trong [docs/incidents.md](../incidents.md). Bài học: lần theo từng mốc thời gian (lệnh → server dừng →
gateway → MES) trước khi chỉnh tham số. Giảm keep-alive một mình **không** giúp, vì nút thắt nằm ở chỗ khác.

### C5. Jitter cho backoff

`Backoff(1 s → 30 s, jitter 0,2)`: mỗi lần chờ lệch ngẫu nhiên ±20 %. 20 gateway cùng mất một server thì không cùng
gõ cửa lại trong một giây khi server vừa lên (*thundering herd*). Dùng cho cả driver lẫn client MQTT của gateway.

### C6. SignalR: đẩy tới mọi client

Máy được đẩy theo group của line. Gateway có thể đọc máy của nhiều line, nên status gateway đẩy tới **tất cả**
(`Clients.All`) và ViewModel chỉ giữ gateway nào đọc ít nhất một máy đang hiện trên dashboard. Snapshot REST
(`LineStatusDto.Gateways`) cũng lọc như vậy, để mở dashboard hoặc reconnect là thấy thẻ ngay.

---

## D. Test

- `GatewayStatusStoreTests`: topic nào vào nhánh `_sys`, đọc status online, LWT giữ máy và đánh `disconnected`,
  payload sai bị bỏ có lý do, lọc theo line.
- `GatewayCardTests` (ViewModel): thẻ từ snapshot, mất một PLC ("1/2"), LWT → OFFLINE, hàng đợi và dropped, status cũ
  không ghi đè status mới, gateway của line khác không hiện.
- `BackoffTests`: jitter trong ±20 % và dưới trần; hai gateway rải lần thử khác nhau.
- Integration `Gateway_status_and_its_lwt_reach_the_gateway_store` (Mosquitto thật).
- 812 unit + 54 integration (Machines/Andon/Line) xanh. Thẻ WPF chưa chụp màn hình; đã kiểm bằng snapshot REST và
  test ViewModel.

---

## E. Câu hỏi phỏng vấn

1. **"Máy Down và máy mất kết nối khác nhau thế nào trên hệ thống của anh?"** — Down là máy báo (đỏ, Andon, bảo trì,
   trừ Availability). Mất kết nối là gateway không đọc được (xám Offline, không Andon, IT xử lý). Gateway chết là LWT
   (thẻ gateway đỏ). Ba tình huống, ba người xử lý.
2. **"LWT là gì, khi nào không chạy?"** — Di chúc khai lúc connect, broker publish khi client chết không chào.
   Disconnect gọn thì không có, nên phải tự publish "offline" khi dừng.
3. **"Phát hiện mất PLC mất bao lâu?"** — Keep-alive 1 s + MES xác nhận 3 s: đo được 4,1–4,6 s. Không dùng watchdog
   30 s cho trường hợp này vì gateway **biết** chắc.
4. **"Vì sao MES không tin LWT ngay?"** — Broker tắt thì publish LWT của mọi client; xác nhận 3 s tránh báo động giả
   toàn nhà máy.
5. **"Jitter để làm gì?"** — Tránh nhiều client cùng thử lại một lúc khi server vừa lên.

---

## F. Đọc thêm

- HiveMQ MQTT Essentials: "Last Will and Testament", "Keep Alive and Client Take-Over", "Retained Messages".
- OPC UA Part 5: `ServerStatus`, `ServerState` (Running, Shutdown, Failed...), `SecondsTillShutdown`.
- AWS Architecture Blog: "Exponential Backoff And Jitter".
