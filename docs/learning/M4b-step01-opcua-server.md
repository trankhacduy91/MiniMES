# M4b · Bước 1 — OPC UA server trong simulator (vai lò reflow): kiến thức cần nắm

> Tài liệu học cho bước 1 của [module-04b](../../spec/module-04b-edge-gateway.md) (spec mục **2.2**, 1.2a, 1.3 bước 4–5).
> Bước trước: [M4b-step00](M4b-step00-opcua-modbus-basics.md) (đã dùng client với server có sẵn). Bước này tự **làm server**.

---

## A. Nghiệp vụ

### A1. Lò reflow là gì và vì sao nó nói OPC UA

Line SMT: in kem hàn → gắn linh kiện (SMT) → **lò reflow** (nung chảy kem hàn để gắn chặt linh kiện) → kiểm tra.
Nhiệt độ **vùng peak** (thường 235–250 °C với kem hàn không chì) quyết định mối hàn tốt hay xấu: thấp quá thì hàn
nguội, cao quá thì cháy linh kiện. Vì vậy nhiệt độ lò là **thông số quy trình** quan trọng nhất của line SMT, và là
dữ liệu cho SPC ở M7.

Lò đời mới (Heller, BTU, Rehm) thường có **OPC UA server** sẵn trong bộ điều khiển. Ở dự án, simulator đóng vai bộ
điều khiển đó: một OPC UA server với 3 node.

| Node | Kiểu | Ý nghĩa |
| --- | --- | --- |
| `ns=2;s=Reflow.PeakTemp` | Double | Nhiệt độ vùng peak, °C (245 ± 0,8, có thể trôi) |
| `ns=2;s=Reflow.Counter` | UInt32 | Bộ đếm tích lũy số bo đã qua lò (giống M4) |
| `ns=2;s=Reflow.State` | Int32 | Mã trạng thái **theo cách đánh số của PLC**: 1 Running, 2 Idle, 3 Down, 4 Setup, 5 PlannedStop |

`State` cố ý là số chứ không phải chữ: PLC thật trả mã số, và gateway (bước 4) phải **map** sang `Running/Idle/...`
của hợp đồng M4. Đây là việc "chuẩn hóa ý nghĩa" của gateway (spec 1.1).

### A2. Các tình huống demo lệnh console tạo ra

| Lệnh | Ngoài đời là | Bước sau dùng để kiểm |
| --- | --- | --- |
| `drift L1-REFLOW-01 0.3` | Quạt làm mát yếu, nhiệt độ peak trôi lên 0,3 °C/phút | SPC rule "7 điểm trên đường tâm" (M7) |
| `drift L1-REFLOW-01 off` | Đã sửa quạt, về set point | |
| `sensor L1-REFLOW-01 bad` | Đứt dây cặp nhiệt (thermocouple) | Quy tắc G8: gateway gửi `quality = Bad`, MES không dùng cho SPC |
| `opcua stop` / `opcua start` | Lò cập nhật firmware, tắt bộ điều khiển 2 phút | Quy tắc G7: máy `Offline`, **không** `Down`, không Andon (bước 6) |

---

## B. Thiết kế

```
SimulatedMachine (vòng 100 ms, M4)
  │  mỗi tick: OvenTemperature.Update(now)   ← 1 giá trị/giây = 245 + nhiễu ±0,8 + drift
  │            ReflowServer.Publish(values, giờ PLC)
  ▼
ReflowServer  ── giữ giá trị cuối; start/stop được nhiều lần
  │
  └─ OvenServer : StandardServer
        └─ ReflowNodeManager : CustomNodeManager2   ← Objects/Reflow/{PeakTemp, Counter, State}
              Set(node): chỉ khi value/status đổi → Value, StatusCode, Timestamp, ClearChangeMasks
```

| File | Việc |
| --- | --- |
| [OpcUa/ReflowNodeManager.cs](../../src/MiniMes.MachineSimulator/OpcUa/ReflowNodeManager.cs) | Tạo address space, cập nhật node dưới `lock (Lock)` |
| [OpcUa/ReflowServer.cs](../../src/MiniMes.MachineSimulator/OpcUa/ReflowServer.cs) | `ApplicationConfiguration` bằng code, chứng chỉ tự ký, start/stop, giữ giá trị cuối |
| [Machines/OvenTemperature.cs](../../src/MiniMes.MachineSimulator/Machines/OvenTemperature.cs) | Mô hình nhiệt độ: set point + nhiễu + drift, Random riêng có seed |
| [machines.json](../../src/MiniMes.MachineSimulator/machines.json) | `"opcua": { "host": "localhost", "port": 4840, "peakTempC": 245, "noiseC": 0.8 }` cho lò |

Quyết định đáng nói:

1. **Lò vẫn phát MQTT** cho tới bước 4. Nếu tắt MQTT ngay, dashboard M4 mất lò suốt bước 2–3 (gateway chưa publish
   gì). Ở bước 4, gateway thay vào và MQTT trực tiếp của lò sẽ tắt.
2. **Node chỉ được cập nhật khi giá trị đổi.** Vòng máy gọi `Publish` mỗi 100 ms, nhưng `Set` bỏ qua nếu value và
   status như cũ. Nhờ vậy `SourceTimestamp` = lúc giá trị **thực sự đổi** (State có timestamp lúc máy chuyển
   Running, không phải 100 ms trước).
3. **Timestamp theo đồng hồ PLC** (có tính `skew` của M4): PLC lệch giờ thì `SourceTimestamp` lệch theo, như ngoài đời.
4. **Random riêng cho nhiệt độ** (`seed + 100`): nếu dùng chung Random của máy, mỗi lần rút nhiễu sẽ làm lệch chuỗi
   Down/NG của lò so với M4, và demo "cùng seed cùng kịch bản" không còn đúng.
5. **`opcua stop` hủy server, `start` tạo server mới.** `StandardServer` đã dừng không khởi động lại được. Giá trị cuối
   nằm ở `ReflowServer` nên server mới có dữ liệu ngay, giống PLC bật lại vẫn nhớ bộ đếm.

---

## C. Kiến thức kỹ thuật

### C1. Ba lớp của một OPC UA server .NET

| Lớp | Vai trò |
| --- | --- |
| `ApplicationInstance` | Vỏ ứng dụng: kiểm tra/tạo chứng chỉ (`CheckApplicationInstanceCertificatesAsync`), `StartAsync(server)` |
| `StandardServer` | Server chuẩn: session, subscription, node chuẩn ns=0 (Objects, Server...). Override `CreateMasterNodeManager` để gắn node manager của mình |
| `CustomNodeManager2` | Quản lý **một namespace**: tạo node trong `CreateAddressSpace`, giữ `Lock` và `SystemContext` |

### C2. Tạo address space

- `FolderState` cho thư mục `Reflow`, `BaseDataVariableState` cho từng biến (DataType, ValueRank Scalar, AccessLevel).
- Treo folder vào `Objects` cần **tham chiếu hai chiều**: folder có reference ngược (`isInverse: true`) tới
  `ObjectsFolder`, và `externalReferences[ObjectsFolder]` nhận reference xuôi tới folder. Node `Objects` thuộc node
  manager khác (ns=0), nên phải đi qua `externalReferences`.
- `AddPredefinedNode(SystemContext, folder)` đăng ký folder và mọi con của nó.
- **Namespace index**: ns=0 chuẩn, ns=1 là `ApplicationUri` của chính server, ns=2 là namespace đầu tiên ta thêm
  (`urn:minimes:simulator:reflow`). Đó là lý do NodeId của spec là `ns=2;...`. Trên OPC PLC ở bước 0 là `ns=3`, vì
  server đó có thêm namespace DI.

### C3. Cập nhật giá trị để subscription nhận được

```csharp
node.Value = value; node.StatusCode = quality; node.Timestamp = ts;
node.ClearChangeMasks(SystemContext, includeChildren: false);   // thiếu dòng này: Read thấy giá trị mới, subscription thì không
```

Luôn làm dưới `lock (Lock)` của node manager: thread của server đọc node cùng lúc với vòng máy ghi.

**StatusCode Bad thì không có value**: khi `PeakTemp` là `BadSensorFailure`, client nhận value rỗng. Ngoài đời, cảm
biến hỏng thì con số không có nghĩa, nên quy tắc G8 bảo gateway gửi `quality = Bad` để MES lưu lại mà không dùng.

### C4. Cấu hình server bằng code (dev)

- `SecurityPolicies = { None }`, `UserTokenPolicies = { Anonymous }`, `AutoAcceptUntrustedCertificates = true`:
  **chỉ cho dev**. Production: `Basic256Sha256` + `SignAndEncrypt`, trust chứng chỉ bằng tay (spec mục 4).
- `BaseAddresses = { "opc.tcp://localhost:4840/" }`: đây là URL server **quảng bá** cho client. Chạy trong Docker
  phải đổi `host` thành tên service (bẫy 1 của spec 2.2), nên `host` nằm trong `machines.json`.
- Chứng chỉ để ở `AppContext.BaseDirectory/pki` (cạnh file exe), không phải thư mục làm việc. Nếu để theo thư mục làm
  việc, chạy từ thư mục khác sẽ sinh chứng chỉ mới và client phải trust lại (bẫy 3).
- **`MaxRegistrationInterval = 0`**: tắt đăng ký với Local Discovery Server. Xem C5.

### C5. Sự cố gặp thật: server tự gọi chính nó

Khi mới chạy, log cứ vài giây lại báo `RegisterServer2 failed for opc.tcp://localhost:4840/`. Mặc định server đăng ký
với **LDS** (Local Discovery Server, "danh bạ" các server OPC UA trên một máy) ở `opc.tcp://localhost:4840`, cổng
chuẩn dành cho LDS. Server của ta chiếm đúng cổng đó nên nó gọi chính nó. Sửa bằng `MaxRegistrationInterval = 0`.
Chi tiết ở [docs/incidents.md](../incidents.md).

### C6. API của thư viện 1.5.378: telemetry thay cho logger toàn cục

Mọi thành phần nhận `ITelemetryContext`. Simulator đưa logger của Generic Host vào bằng một lớp 1 dòng:

```csharp
public sealed class HostTelemetry(ILoggerFactory loggers) : TelemetryContextBase(loggers);
```

Log của stack rất nhiều ở mức Information (mỗi bước khởi động, mỗi session). `Program.cs` lọc xuống Warning cho cả
`Opc.Ua` lẫn category `ReflowServer+OvenServer`, vì logger của server lấy tên theo class con của ta.

---

## D. Kiểm tra (đã chạy 28/10/2026)

Client: [labs/opcua-lab.cs](labs/opcua-lab.cs), dùng thay UaExpert.

```
dotnet run --project src/MiniMes.MachineSimulator
dotnet run docs/learning/labs/opcua-lab.cs -- opc.tcp://localhost:4840 0.5 15 urn:minimes:simulator:reflow Reflow.PeakTemp Reflow.Counter Reflow.State
```

| Kiểm tra | Kết quả |
| --- | --- |
| Browse | `Reflow ns=2;s=Reflow` → `PeakTemp`, `Counter`, `State` (ns=2;s=Reflow.*) |
| Read | PeakTemp 245,78 Double Good; Counter 8002 UInt32; State 1 Int32 (timestamp = lúc máy vào Running) |
| Deadband 0,5 °C | 15 giây: không deadband 13 notification, deadband 8 |
| `drift L1-REFLOW-01 30` | `list` mỗi 5 giây: 246,69 → 248,92 → 252,79 → 254,02 °C |
| `sensor L1-REFLOW-01 bad` | Client nhận `BadSensorFailure [0x808C0000]`, value rỗng; `good` → Good trở lại |
| `opcua stop` | Client mới: `SocketException 10061 ... actively refused` (như PLC tắt) |
| `opcua start` | Client đọc ngay Counter 8008 = đúng số trong `list` |
| MQTT của 3 máy | Vẫn chạy như M4 (counter lò tăng trên cả hai phía) |

Chưa có unit test: simulator không nằm trong danh sách project được `UnitTests` tham chiếu (CLAUDE.md mục 4).
Integration test đầu-cuối (simulator OPC UA chạy trong process test + gateway) là việc của bước 7.

Bạn nên tự mở **UaExpert** một lần với `opc.tcp://localhost:4840` khi simulator đang chạy: kéo `PeakTemp` vào Data
Access View, gõ `drift L1-REFLOW-01 1` và nhìn cột Value tăng dần.

---

## E. Câu hỏi phỏng vấn

1. **"Làm sao server OPC UA báo cho client khi giá trị đổi?"** — Client tạo subscription + monitored item; server lấy
   mẫu theo SamplingInterval, so với giá trị trước (và deadband), gửi gộp theo PublishingInterval. Phía server, code
   cập nhật node phải gọi `ClearChangeMasks` để stack biết node đã đổi.
2. **"ns=2 lấy ở đâu ra?"** — Bảng namespace của server: 0 chuẩn, 1 của chính server, 2 là namespace riêng đầu tiên.
   Index phụ thuộc thứ tự đăng ký, nên client chắc ăn thì tra theo URI.
3. **"Cảm biến hỏng thì OPC UA báo thế nào?"** — Bằng StatusCode trên chính giá trị (`BadSensorFailure`), không phải một
   tag lỗi riêng. Gateway map sang `quality = Bad`, MES lưu nhưng không dùng cho SPC/OEE.
4. **"Bảo mật OPC UA của anh?"** — Dev: None + auto-accept, có ghi rõ. Production: SignAndEncrypt + Basic256Sha256,
   trust chứng chỉ thủ công, theo dõi hạn chứng chỉ.
5. **"Cổng 4840 là gì?"** — Cổng IANA của OPC UA, dùng cho cả LDS. Kể sự cố server tự đăng ký vào chính nó.

---

## F. Đọc thêm

- Repo `OPCFoundation/UA-.NETStandard`: `Applications/ConsoleReferenceServer`, `Applications/Quickstarts.Servers`
  (ReferenceServer, `ReferenceNodeManager` có đủ kiểu node mẫu).
- OPC UA Online Reference, Part 3 (Address Space Model: Node, Reference, NodeClass), Part 12 (Discovery, LDS).
