# M4b · Bước 3 — Modbus slave + master + giải mã thanh ghi: kiến thức cần nắm

> Tài liệu học cho bước 3 của [module-04b](../../spec/module-04b-edge-gateway.md) (spec mục **1.2b, 1.2c, 2.4, 2.5**;
> quy tắc G8, G9). Bước trước: [M4b-step02](M4b-step02-gateway-opcua-client.md) (OPC UA client).
> Lab nền: [labs/modbus-lab.cs](labs/modbus-lab.cs) ở bước 0.

---

## A. Nghiệp vụ

### A1. Máy dán keo đời cũ: chỉ có Modbus

`L1-GLUE-01` là máy dán keo (glue dispenser) đời cũ: không có MQTT, không có OPC UA, chỉ có PLC với **Modbus TCP**.
Ngoài nhà máy loại máy này rất nhiều, cùng với đồng hồ đo, biến tần, cân. MES chỉ "thấy" máy này qua gateway. Vì vậy
simulator chạy nó với `"mqtt": false`; tới bước 4, gateway sẽ publish thay.

Tài liệu máy (giả lập) ghi bảng thanh ghi:

| Tài liệu | Trên dây (0-based) | Nội dung | Kiểu |
| --- | --- | --- | --- |
| 40001–40002 | 0–1 | Bộ đếm tích lũy | uint32, **CDAB** (word thấp trước) |
| 40003 | 2 | Trạng thái 1 Running, 2 Idle, 3 Down, 4 Setup, 5 PlannedStop | uint16 |
| 40004 | 3 | Mã alarm, 0 = không có (`G301` → 301, `E999` → 999) | uint16 |

### A2. Sự cố "2 tỷ sản phẩm" (đã tái hiện)

| Cấu hình | Bộ đếm thật 100.000 | Sau 1 bo |
| --- | --- | --- |
| `wordOrder: CDAB` (đúng) | 100.000 | 100.001 |
| `wordOrder: ABCD` (sai) | **2.258.632.705** | 2.258.698.241 (**+65.536**) |

Nhảy **65.536 = 2^16** mỗi bo là dấu hiệu chắc chắn của đảo word: word thấp (tăng 1) bị hiểu là word cao. Ở M5 lỗi
này thành "Performance 1.000.000 %". Câu chuyện đầy đủ trong [docs/incidents.md](../incidents.md). Đây là câu chuyện
phỏng vấn đáng kể nhất của module.

### A3. Polling: bỏ lỡ được gì

Gateway hỏi 1 lần/giây. Bộ đếm **tích lũy** nên bỏ lỡ một lần hỏi không mất sản lượng; lần sau vẫn ra tổng đúng.
Alarm bật 300 ms rồi tắt giữa hai lần hỏi thì **không thấy**. Muốn chắc thì PLC phải chốt (latch) alarm tới khi có
người xác nhận (spec 5.2 câu 7).

---

## B. Thiết kế

```
Simulator                                           Gateway
SimulatedMachine (100 ms)                           ModbusDriver (mỗi 1000 ms)
  └ PublishPlc → GlueMachineSlave.Publish             └ ReadHoldingRegistersAsync(unit 1, start 0, count 4)   ← 1 request (FC03)
       WritePoints(0, [lo, hi, state, alarm])            └ RegisterDecoder.Decode(block, offset, type, wordOrder) mỗi tag
       (1 lần ghi cả 4: không bao giờ đọc "rách")        └ RawValue(Good, ts = giờ gateway) → Channel → Pipeline (log khi đổi)
```

| File | Việc |
| --- | --- |
| [Modbus/GlueMachineSlave.cs](../../src/MiniMes.MachineSimulator/Modbus/GlueMachineSlave.cs) | Slave NModbus, cổng 5020, start/stop, giữ thanh ghi cuối |
| [Drivers/Modbus/RegisterDecoder.cs](../../src/MiniMes.EdgeGateway/Drivers/Modbus/RegisterDecoder.cs) | Hàm thuần: 4 thứ tự, uint16/int16/uint32/int32/float32 |
| [Drivers/Modbus/ModbusDriver.cs](../../src/MiniMes.EdgeGateway/Drivers/Modbus/ModbusDriver.cs) | Master: kết nối, poll một khối, phân loại lỗi, backoff |
| [gateway.tags.json](../../src/MiniMes.EdgeGateway/gateway.tags.json) | Thiết bị `L1-GLUE-01`: `startAddress 0`, `count 4`, 3 tag |
| [machines.json](../../src/MiniMes.MachineSimulator/machines.json) | Máy `L1-GLUE-01`: `"mqtt": false`, `"modbus": { "port": 5020, "unitId": 1 }` |

### B1. Hai loại lỗi, hai cách xử lý

| Lỗi | Ví dụ | Kết nối | Gateway làm gì |
| --- | --- | --- | --- |
| Slave **trả lời** bằng exception code | Firmware mới, địa chỉ không còn (`IllegalDataAddress`) | Còn | Mọi tag `quality = Bad`, value null (G8); log Error một lần; vẫn poll |
| Slave **không trả lời** | Tắt máy, rút cáp, timeout | Mất | `ConnectionChanged(false)`, backoff 1-2-4… s, kết nối lại |

### B2. Ba chi tiết khác snippet của spec

1. `Transport.Retries = 0`: NModbus mặc định thử lại 3 lần. Với slave chết, mỗi lần poll treo 4 × timeout = 8 giây.
   Ở đây lần poll kế tiếp chính là lần thử lại.
2. Timeout **kết nối** riêng (`CancelAfter(timeoutMs)`): không để hệ điều hành chờ 20 giây khi host không trả lời.
3. `WaitAsync(2 × timeout)` bọc lời gọi đọc: chốt chặn cuối nếu timeout của socket không áp dụng cho đường async.

---

## C. Kiến thức kỹ thuật

### C1. Ghép 2 thanh ghi thành 32-bit

A, B, C, D là 4 byte của số, A lớn nhất. 100.000 = `0x00_01_86_A0` → A=00, B=01, C=86, D=A0.

| Thứ tự | r0 | r1 | Hay gặp ở |
| --- | --- | --- | --- |
| ABCD | `0x0001` | `0x86A0` | "Chuẩn" Modbus (big-endian) |
| CDAB | `0x86A0` | `0x0001` | Rất nhiều PLC (word thấp trước) |
| BADC | `0x0100` | `0xA086` | Ít gặp |
| DCBA | `0xA086` | `0x0100` | Thiết bị lưu little-endian thuần |

Code: [RegisterDecoder.ToUInt32](../../src/MiniMes.EdgeGateway/Drivers/Modbus/RegisterDecoder.cs), unit test theo đúng bảng
2.4 của spec, thêm float `123,456` ở cả 4 thứ tự.

### C2. Bẫy C#: kiểu của switch expression (lỗi gặp thật, test bắt được)

```csharp
object Decode(...) => type switch { "uint16" => block[i], "uint32" => ToUInt32(...), "float32" => ToFloat(...) };
```

Mọi nhánh bị đổi sang **`float`** (kiểu chung tốt nhất của `ushort/short/uint/int/float`) **trước** khi boxing thành
`object`. Test fail với thông báo khó hiểu `Expected: 100000, Actual: 100000`. Ngoài đời, bộ đếm trên 16.777.216 mất
chữ số cuối. Sửa bằng `(object)` ở từng nhánh, cùng test hồi quy 2^24 + 1 kèm `Assert.IsType<uint>`.

### C3. NModbus

- **Slave**: `ModbusFactory().CreateSlaveNetwork(TcpListener)` → `CreateSlave(unitId)` → `AddSlave` → `ListenAsync(ct)`.
  Dữ liệu nằm trong `slave.DataStore.HoldingRegisters`; `DefaultPointSource` có khóa (`_syncRoot`), nên **một lần**
  `WritePoints(0, [lo, hi, state, alarm])` là nguyên khối so với một lần đọc của master: không đọc được bộ đếm "rách"
  (word thấp mới, word cao cũ).
- **Dừng slave**: `network.Dispose()` đóng cả các kết nối master đang mở, nên gateway thấy lỗi ngay ở lần đọc sau
  (`An established connection was aborted`).
- **Master**: `CreateMaster(TcpClient)`, `ReadHoldingRegistersAsync(unitId, start, count)` = FC03.
  `ReadInputRegistersAsync` = FC04 (vùng khác, lab bước 0 đọc ra toàn 0).

### C4. `localhost` không phải `127.0.0.1` (lỗi gặp thật)

Slave nghe `IPAddress.Any` (chỉ IPv4); gateway nối `localhost` → thử `::1` (IPv6) trước. Windows mất khoảng 2 giây
mới từ chối kết nối loopback, đúng bằng timeout kết nối của gateway. Sửa: `TcpListener.Create(port)` nghe cả IPv6 lẫn
IPv4. Dấu hiệu nhận biết: lỗi "timeout" mà con số **đúng bằng** timeout mình cấu hình.

### C5. Timestamp Modbus

Modbus không mang timestamp. `ts` = giờ gateway lúc đọc (quy tắc G9). Đồng hồ PC gateway lệch thì `ts` lệch, nên
gateway phải đồng bộ NTP (spec 1.5).

---

## D. Kiểm tra (đã chạy 28/10/2026)

```
dotnet run --project src/MiniMes.MachineSimulator     # list: L1-GLUE-01 ... no MQTT / Modbus ... running
dotnet run --project src/MiniMes.EdgeGateway
```

| Kiểm tra | Kết quả |
| --- | --- |
| Đọc khối 4 thanh ghi | `connected to localhost:5020 unit 1, reading 4 registers from 0 every 1000 ms` → `counter = 100000`, `state = 1`, `alarm = 0` |
| `down L1-GLUE-01 6` | `state = 3`, `alarm = 999`; 6 giây sau `state = 1`, `alarm = 0` |
| `reset L1-GLUE-01` | `counter = 0` |
| `modbus stop` | `DISCONNECTED: ... connection was aborted`, thử lại 1, 2, 4 s |
| `modbus start` | Nối lại, `counter` khớp `list` của simulator |
| `wordOrder: ABCD` (cố tình) | `counter = 2258632705`, rồi `2258698241` (+65.536) |
| `wordOrder: CDAB` | `counter = 100001` |

Unit test: [RegisterDecoderTests](../../tests/MiniMes.UnitTests/Gateway/RegisterDecoderTests.cs) (uint32 4 thứ tự + đọc
sai, int32 có dấu, float 4 thứ tự + đọc sai, khối máy dán keo, lệch 1 địa chỉ, int16, độ rộng kiểu, hồi quy kiểu) và
10 trường hợp cấu hình Modbus sai trong `GatewayOptionsValidatorTests`. Tổng 743 test xanh.

---

## E. Câu hỏi phỏng vấn

1. **"Đọc 2 thanh ghi ra số sai, anh kiểm tra gì?"** — Theo thứ tự: địa chỉ lệch 1 (40001 = 0), FC03 hay FC04, unit
   ID, thứ tự word (thử 4 kiểu với giá trị đã biết trên HMI), có dấu/không dấu, hệ số scale (2453 = 245,3 °C). Kể sự cố
   2 tỷ: bước nhảy 65.536 là dấu hiệu đảo word.
2. **"Sao không đọc từng thanh ghi cho dễ?"** — 4 request thay vì 1: chậm, tải PLC, và 4 giá trị không cùng một thời
   điểm (bộ đếm có thể bị đọc "rách" giữa hai request).
3. **"PLC trả lỗi thì gateway làm gì?"** — Phân biệt: trả exception code (kết nối vẫn tốt) thì giá trị Bad và poll
   tiếp; không trả lời thì coi như mất kết nối, kết nối lại có backoff.
4. **"Modbus có bảo mật không?"** — Không. Ai vào được mạng là đọc/ghi được; phải cô lập mạng OT (VLAN, firewall) và
   gateway chỉ đọc (FC03), không ghi.
5. **"Poll bao lâu một lần?"** — 500–1000 ms là đủ cho MES; nhanh hơn chỉ làm PLC chậm. Bộ đếm tích lũy nên không mất
   sản lượng; alarm ngắn thì cần PLC latch.

---

## F. Đọc thêm

- Modbus Organization: "Modbus Application Protocol Specification" (FC03 tối đa 125 thanh ghi, exception codes),
  "Modbus Messaging on TCP/IP Implementation Guide" (MBAP header, unit ID).
- Repo `NModbus/NModbus`: mẫu `ModbusTcpMasterReadInputs`, `StartModbusTcpSlave`.
- C# language reference: "switch expression", mục *natural type* / best common type.
