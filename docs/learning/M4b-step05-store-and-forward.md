# M4b · Bước 5 — Store-and-forward bằng SQLite: kiến thức cần nắm

> Tài liệu học cho bước 5 của [module-04b](../../spec/module-04b-edge-gateway.md) (spec mục **1.2e, 2.7**; quy tắc
> G4, G5, G6, G11). Bước trước: [M4b-step04](M4b-step04-normalize-and-publish.md). Bước này có một sửa đổi nhỏ ở ingest
> M4 (đã được duyệt), xem mục C5.

---

## A. Nghiệp vụ

### A1. Mạng xưởng sẽ đứt

Switch khởi động lại, cáp bị chuột cắn, Windows Update restart PC gateway, broker nâng cấp. Không có bộ đệm thì mọi
thay đổi trong lúc đứt **mất hẳn**: bộ đếm tích lũy cứu được tổng, nhưng lịch sử state, alarm, nhiệt độ lò cho SPC
trong khoảng đó thì không. **Store-and-forward**: gateway ghi xuống đĩa trước, có mạng thì gửi bù **đúng thứ tự**, và
chỉ xóa khi broker xác nhận.

### A2. Ba mắt xích của "không mất, không trùng"

```
Gateway: outbox SQLite      → không mất khi broker/mạng/gateway chết (xóa chỉ sau PUBACK)
Broker:  persistent session  → message cho ingest được giữ khi ingest chưa kịp kết nối lại
         (mosquitto persistence true + ingest CleanSession=false, đã có từ M4)
MES:     khử trùng theo msgId → gửi lại (sau crash) không đếm hai lần
```

Thiếu một mắt xích là mất hoặc trùng dữ liệu. Câu trả lời phỏng vấn tốt là kể đủ cả ba.

### A3. Kết quả đo thật (28/10/2026)

| Kịch bản | Kết quả |
| --- | --- |
| `docker compose stop mosquitto` | Log `Outbox: QueueLength 18 → 23 → 29 → 36 → … → 61, dropped 0` |
| `start` | **66 message gửi bù trong 2,9 s** sau khi gateway nối lại broker |
| `SUM(Delta)` trên MES | Máy dán keo 26 = 100.033 − 100.007; lò 22 = 8.027 − 8.005; **không có `IsReset`**, bộ đếm không giảm |
| Kill gateway khi có 16 dòng chờ | 16 dòng vẫn trên đĩa; chạy lại, dòng mới nối Id tiếp theo; 47 message gửi trong 2,1 s; **0 msgId trùng** |
| Sau khi sửa ingest (C5) | 9 count trong 70 s mất broker, **mỗi count đúng giờ máy làm ra** (cách 10 s), `Delta = 1` |

Lưu ý: broker lên rồi mà gateway còn chờ thêm tới khoảng 25 giây mới nối lại, vì backoff của client MQTT đã lên 16–30 s.
Đó là cái giá của backoff (không dồn dập vào broker đang khởi động); bước 6 thêm jitter.

---

## B. Thiết kế

```
PipelineService (1 reader)                 SqliteOutbox (data/outbox.db, WAL)            ForwarderService (1 vòng)
  drain mọi sự kiện đang chờ ─EnqueueAsync─▶ INSERT ... (1 transaction / lô)               │
                                             Id AUTOINCREMENT = thứ tự gửi ◀──PeekAsync(200)─┤ ORDER BY Id
                                                                                           │ PublishRawAsync QoS 1
                                             DELETE WHERE Id = ? ◀────DeleteAsync──────────┤ (sau PUBACK)
OutboxMaintenanceService: TrimAsync mỗi phút (maxRows, maxAgeDays), log QueueLength mỗi 10 s khi > 0
Heartbeat: đi thẳng QoS 0, KHÔNG qua outbox
```

| File | Việc |
| --- | --- |
| [Queue/SqliteOutbox.cs](../../src/MiniMes.EdgeGateway/Queue/SqliteOutbox.cs) | Bảng `Outbox`, WAL, ghi theo lô, peek, delete, trim, stats |
| [Queue/ForwarderService.cs](../../src/MiniMes.EdgeGateway/Queue/ForwarderService.cs) | Gửi tuần tự, xóa sau PUBACK, đo thời gian gửi bù |
| [Queue/OutboxMaintenanceService.cs](../../src/MiniMes.EdgeGateway/Queue/OutboxMaintenanceService.cs) | Trần hàng đợi + log chỉ số |
| [Pipeline/PipelineService.cs](../../src/MiniMes.EdgeGateway/Pipeline/PipelineService.cs) | Ghi vào outbox thay vì publish thẳng |

### B1. Luôn đi qua outbox, kể cả khi có mạng

Không có hai đường "có mạng thì gửi thẳng, mất mạng thì ghi đĩa". Hai đường là hai chỗ có thể sai thứ tự (message mới
đi thẳng vượt mặt message cũ còn trong hàng đợi), và lúc "chuyển chế độ" luôn có lỗi biên. Giá phải trả là một lần ghi
đĩa mỗi message, không đáng kể với WAL và ghi theo lô.

### B2. Gửi tuần tự, xóa sau PUBACK

| Làm sai | Hậu quả |
| --- | --- |
| Xóa trước khi có PUBACK | Broker chết giữa chừng → mất message |
| Gửi song song để nhanh | Sai thứ tự → state history lộn, count cũ đến sau count mới (MES có thể hiểu là reset) |
| Message lỗi thì bỏ qua, gửi cái sau | Sai thứ tự; phải dừng và thử lại **đúng message đó** |

Crash giữa PUBACK và DELETE: lần chạy sau gửi lại một message, cùng payload và cùng msgId, MES bỏ. Đây là
**at-least-once + idempotent**.

### B3. Payload lưu nguyên văn

Normalizer tạo `MachineMessage`, serialize thành JSON **một lần** rồi lưu text. Forwarder gửi đúng chuỗi đó
(`PublishRawAsync`). Gửi lại thì giống từng byte, msgId không thể đổi.

---

## C. Kiến thức kỹ thuật

### C1. SQLite cho hàng đợi

```sql
PRAGMA journal_mode = WAL;     -- người đọc và người ghi không chặn nhau
PRAGMA synchronous = NORMAL;   -- với WAL: an toàn khi process crash; mất điện có thể mất vài commit cuối
CREATE TABLE IF NOT EXISTS Outbox (Id INTEGER PRIMARY KEY AUTOINCREMENT, Topic, Payload, Retain, CreatedAt);
```

- `AUTOINCREMENT`: Id **không bao giờ tái dùng**, kể cả khi bảng rỗng. Thứ tự gửi đúng qua cả lần restart (test
  `The_queue_survives_a_restart_and_keeps_its_order`).
- `Microsoft.Data.Sqlite`: một `SqliteConnection` dùng cho cả ghi (pipeline) lẫn đọc/xóa (forwarder). Connection không
  thread-safe, nên một `SemaphoreSlim` xếp hàng các lệnh. `Pooling = false` để đóng là nhả file ngay.
- Ghi theo lô: pipeline xử lý **mọi sự kiện đang chờ** trong channel rồi ghi một transaction. Lúc ít dữ liệu, lô 1–3
  message; lúc dồn, lô tới 500. Không cần timer 50 ms như spec gợi ý.
- File ở `data/outbox.db` **cạnh exe**, không ở thư mục tạm (bị dọn = mất hàng đợi). Trong Docker là một volume.

### C2. Trần hàng đợi (G11)

```sql
DELETE FROM Outbox WHERE Id IN (SELECT Id FROM Outbox ORDER BY Id LIMIT max(COUNT(*) - @maxRows, 0));
DELETE FROM Outbox WHERE CreatedAt < @now - maxAgeDays;
```

Bỏ bản **cũ nhất**, log Error, tăng `DroppedCount`. Bộ đếm tích lũy nên tổng sản lượng không mất; chỉ mất chi tiết
của khoảng bị bỏ. Đĩa đầy làm treo cả máy gateway, nên trần là bắt buộc.

### C3. Chờ có tín hiệu thay vì vòng lặp bận

`WaitForNewAsync(1 s)`: forwarder ngủ tới khi có `EnqueueAsync` (một `TaskCompletionSource` được "bắn") hoặc hết 1
giây. Hàng đợi rỗng thì CPU gần 0, có message mới thì gửi ngay.

### C4. Heartbeat không qua outbox

Heartbeat cũ gửi bù là **nói dối**: báo máy "còn sống lúc 10:15" khi MES đã đánh Offline. Heartbeat đi thẳng QoS 0;
broker vắng thì bỏ.

### C5. Bài học lớn: "đồng hồ lệch" hay "đến muộn"? (đã sửa ingest M4)

M4 có luật: `ts` cách giờ nhận quá 60 s nghĩa là đồng hồ PLC sai, dùng giờ nhận. Với hàng đợi, luật này hỏng:

```
Hàng đợi gửi bù:  count A (ts cũ 87 s)   → "lệch" → ghi ở giờ NHẬN (now)
                  count B (ts cũ 50 s)   → trong ngưỡng → ts thật = now−50 s < mốc của A → BỎ ("older than last")
                  count C (ts cũ 20 s)   → BỎ ...
Kết quả: tổng đúng (bộ đếm tích lũy), nhưng 11 count còn 3, sản lượng theo giờ dồn vào lúc nối lại.
```

Chỉ nhìn `ts` và giờ nhận thì "PLC chậm 2 phút" và "message chờ 2 phút trong hàng đợi" **giống hệt nhau**. Phân biệt
bằng **đường đi**: message từ gateway (`source` = `opcua`/`modbus`) có `ts` ở **quá khứ** tới 7 ngày (= tuổi tối đa
của outbox) là đến muộn, giữ `ts`. Tương lai > 60 s, hoặc cũ hơn 7 ngày, mới là lệch giờ. Máy MQTT trực tiếp giữ luật
cũ. Code: hàm thuần [`MachineRuntimeStore.EventTime`](../../src/MiniMes.Application/Machines/MachineRuntimeStore.cs).

Trong lúc mất broker, state history của MES vẫn là **Offline** (watchdog M4 không nghe thấy gì). State gửi bù chỉ được
lưu thành event, không viết lại history. Đúng như câu chuyện 1.3 #3 của spec: dashboard Offline trong lúc đứt, bộ đếm
giờ vẫn khớp PLC.

---

## D. Test

- [SqliteOutboxTests](../../tests/MiniMes.UnitTests/Gateway/SqliteOutboxTests.cs) (file SQLite thật trong thư mục tạm):
  thứ tự, peek không xóa, **maxRows 100 + ghi 150 → còn 100, `DroppedCount = 50`, giữ 100 bản mới nhất**, quá 7 ngày bị
  bỏ, dưới trần không bỏ, sống sót qua restart và giữ thứ tự Id, stats, `WaitForNewAsync`.
- `MachineRuntimeStoreTests` (ingest M4): gateway gửi muộn giữ `ts`; quá 7 ngày, `ts` tương lai và máy MQTT trực tiếp
  vẫn là lệch giờ; hàng đợi gửi bù giữ từng count đúng giờ; process gửi muộn giữ `ts`.
- 792 unit test + 48 integration test (Machines, Andon) xanh.

---

## E. Câu hỏi phỏng vấn

1. **"Mất mạng giữa xưởng và server thì dữ liệu đi đâu?"** — Outbox SQLite ở gateway, chỉ xóa sau PUBACK, gửi bù tuần
   tự, msgId cố định để MES khử trùng, trần hàng đợi có cảnh báo. Số đo: mất broker 87 giây, 66 message gửi bù trong
   2,9 giây, tổng sản lượng khớp PLC, 0 trùng.
2. **"Vì sao outbox luôn bật?"** — Một đường duy nhất nên thứ tự luôn theo Id; restart không mất; không có lúc chuyển
   chế độ.
3. **"Gateway chết đúng lúc broker đã nhận nhưng chưa trả PUBACK?"** — Message còn trong outbox, gửi lại cùng msgId,
   MES bỏ bản trùng.
4. **"Hàng đợi đầy sau 7 ngày thì sao?"** — Bỏ bản cũ nhất, log Error, đếm `DroppedCount`. Tổng sản lượng vẫn đúng nhờ
   bộ đếm tích lũy, chỉ mất chi tiết.
5. **"Anh từng gặp lỗi gì khi ghép hai tính năng?"** — Chống lệch giờ của MES và gửi bù của gateway: message gửi bù bị
   coi là đồng hồ lệch, tổng đúng nhưng chi tiết theo giờ sai. Phát hiện vì kiểm cả số dòng và `MachineTs`, không chỉ
   tổng. Sửa bằng cách phân biệt theo đường đi của message.

---

## F. Đọc thêm

- sqlite.org: "Write-Ahead Logging", "PRAGMA synchronous", "AUTOINCREMENT" (khác `ROWID` thường ở chỗ không tái dùng Id).
- learn.microsoft.com: "Microsoft.Data.Sqlite overview", "Transactions" (Microsoft.Data.Sqlite).
- Mẫu Transactional Outbox (microservices.io): cùng ý tưởng "ghi trước, gửi sau, xóa khi chắc chắn".
- Mosquitto: `persistence`, `persistent_client_expiration`; MQTT 3.1.1 "Clean Session".
