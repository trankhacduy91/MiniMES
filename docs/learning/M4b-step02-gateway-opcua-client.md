# M4b · Bước 2 — Khung Edge Gateway + OPC UA client: kiến thức cần nắm

> Tài liệu học cho bước 2 của [module-04b](../../spec/module-04b-edge-gateway.md) (spec mục **2.1, 2.3, 2.6** phần
> cấu hình; quy tắc G2, G3, G7–G9). Bước trước: [M4b-step01](M4b-step01-opcua-server.md) (server lò reflow).

---

## A. Nghiệp vụ

### A1. Gateway là một chương trình riêng, đứng cạnh máy

```
 Xưởng (mạng OT)                                     Phòng server (mạng IT)
 ┌──────────────┐  OPC UA   ┌────────────────────┐  MQTT (bước 4)   ┌──────────┐
 │ Lò reflow    │ ◀───────▶ │ MiniMes.EdgeGateway│ ───────────────▶ │ Broker   │ ─▶ MES
 │ (PLC/server) │           │ (PC công nghiệp)   │                  └──────────┘
 └──────────────┘           └────────────────────┘
```

- **Chỉ chung hợp đồng với MES** (`MiniMes.Contracts`), không chung DB, không gọi API MES. `DependencyTests` giữ luật này.
- **Cấu hình nằm ở gateway** (`gateway.tags.json`), không nằm trong DB MES (quy tắc G2): MES chết hoặc mạng đứt, gateway
  vẫn khởi động và vẫn đọc máy.
- **Mã máy phải trùng `Machine.MqttTopicKey`** (G3): ingest của MES bỏ message của máy lạ, mất dữ liệu mà không báo lỗi.

### A2. "Mất kết nối máy" khác "máy dừng"

Khi `opcua stop`, gateway ghi `DISCONNECTED` chứ **không** suy ra máy `Down`. Gateway chỉ biết là không đọc được, còn
máy có thể vẫn đang chạy (quy tắc G7). Bước 6 sẽ biến sự kiện này thành `status = offline` trên MQTT.

---

## B. Thiết kế

```
gateway.tags.json ──▶ GatewayOptions ──(ValidateOnStart)──▶ sai = không khởi động, exit 1
                                │
                 1 driver / 1 máy (DriverHost chạy song song)
                                │
 OpcUaDriver ── session ── subscription ── MonitoredItem.Notification ──TryWrite──┐
     │                                                                              ▼
     └── keep-alive Bad / lỗi ──▶ ConnectionChanged(false) ──TryWrite──▶ Channel<DeviceEvent> ──▶ PipelineService
         backoff 1→2→4…30 s, tạo session mới                                (1 reader, đúng thứ tự)  bước 2: log
                                                                                                    bước 4: Normalizer
```

| File | Việc |
| --- | --- |
| [Config/GatewayOptions.cs](../../src/MiniMes.EdgeGateway/Config/GatewayOptions.cs) | Cấu trúc file cấu hình |
| [Config/GatewayOptionsValidator.cs](../../src/MiniMes.EdgeGateway/Config/GatewayOptionsValidator.cs) | `IValidateOptions` + hàm thuần `Errors(...)` để unit test |
| [Drivers/IDeviceDriver.cs](../../src/MiniMes.EdgeGateway/Drivers/IDeviceDriver.cs) | `IDeviceDriver`, `DeviceEvent` = `RawValue` \| `ConnectionChanged` |
| [Drivers/OpcUa/OpcUaDriver.cs](../../src/MiniMes.EdgeGateway/Drivers/OpcUa/OpcUaDriver.cs) | Session, subscription, keep-alive, reconnect |
| [Drivers/OpcUa/OpcUaClientConfiguration.cs](../../src/MiniMes.EdgeGateway/Drivers/OpcUa/OpcUaClientConfiguration.cs) | Danh tính client + chứng chỉ, tạo một lần |
| [Drivers/Backoff.cs](../../src/MiniMes.EdgeGateway/Drivers/Backoff.cs) | 1, 2, 4… 30 s |
| [Pipeline/PipelineService.cs](../../src/MiniMes.EdgeGateway/Pipeline/PipelineService.cs) | Đọc channel; bước này chỉ log |
| [gateway.tags.json](../../src/MiniMes.EdgeGateway/gateway.tags.json) | GW01, lò reflow, 3 tag |

### B1. Vì sao một Channel thay vì hai event (khác spec)

Spec gợi ý `event ValueReceived` và `event ConnectionChanged`. Tôi gộp thành **một `Channel<DeviceEvent>`**:

1. **Thứ tự**: giá trị cuối cùng đọc được trước khi mất kết nối phải tới pipeline **trước** sự kiện "mất kết nối".
   Hai event gọi kiểu `_ = RaiseAsync(...)` (fire-and-forget) không đảm bảo điều đó.
2. **Thread của thư viện**: callback `Notification` chạy trên thread của stack OPC UA. Chỉ `TryWrite` vào channel (gần
   như tức thì), không `await` gì, nên không chặn notification khác (bẫy 1 của spec 2.3).
3. **Một reader**: pipeline xử lý từng sự kiện một, không cần khóa.

### B2. Reconnect: tạo lại session từ đầu

Mỗi lỗi (server tắt, keep-alive hỏng, mạng) → bỏ session, báo `ConnectionChanged(false)`, chờ backoff, tạo **session
mới và subscription mới**. Đơn giản, dễ giải thích. Cách tối ưu hơn là `SessionReconnectHandler` của thư viện: giữ
subscription qua lần mất mạng ngắn, server gửi bù notification đã xếp hàng. Spec để đó làm thử sau.

---

## C. Kiến thức kỹ thuật

### C1. Options pattern + `ValidateOnStart`

```csharp
builder.Services.AddSingleton<IValidateOptions<GatewayOptions>, GatewayOptionsValidator>();
builder.Services.AddOptions<GatewayOptions>().Bind(builder.Configuration).ValidateOnStart();
```

- `Bind` đọc JSON vào class (key `"1"` trong `map` vào `Dictionary<string,string>` được).
- `IValidateOptions` trả về **tất cả** lỗi cùng lúc, không phải lỗi đầu tiên. Người sửa file cấu hình sửa một lần.
- `ValidateOnStart`: kiểm tra lúc host khởi động, không đợi lần đầu dùng. `Program.cs` bắt `OptionsValidationException`,
  in danh sách lỗi, trả exit code 1 (Windows Service / Docker thấy service không lên được).
- Logic kiểm tra để trong hàm thuần `Errors(GatewayOptions)`, nên test không cần host.

### C2. OPC UA client: các bước (API async của 1.5.378)

```
config (1 lần) → SelectEndpointAsync(useSecurity: false) → DefaultSessionFactory.CreateAsync
  → session.KeepAliveInterval = 2000; session.KeepAlive += (Bad → mất kết nối)
  → new Subscription { PublishingInterval, MinLifetimeInterval }
  → new MonitoredItem(telemetry) { StartNodeId, SamplingInterval, QueueSize, Filter = DataChangeFilter }
  → session.AddSubscription(sub); await sub.CreateAsync()        // đúng thứ tự này
  → kiểm tra item.Status.Error (NodeId sai không làm hỏng cả subscription, chỉ item đó Bad)
```

| Thuộc tính | Giá trị | Vì sao |
| --- | --- | --- |
| `KeepAliveInterval` | 2000 ms | Server biến mất mà không đóng TCP thì chỉ keep-alive phát hiện; 2 s để bước 6 đạt "Offline ≤ 5 s" |
| `PublishingInterval` | 1000 ms | Gom thay đổi gửi 1 lần/giây |
| `SamplingInterval` | 500 ms (nhiệt độ) | ≤ publishing; validator chặn sampling > publishing |
| `QueueSize` | 10 | Mẫu giữa hai lần publish chờ trong hàng, không mất |
| `DataChangeTrigger.StatusValue` | | Đổi chất lượng (Good → BadSensorFailure) cũng báo, dù value không đổi |
| `DeadbandType.Absolute` | 0,5 °C | Server lọc, gateway khỏi lọc (G10) |
| `MinLifetimeInterval` | 60 000 ms | Subscription sống ít nhất bằng thời gian session có thể khôi phục; thiếu thì thư viện cảnh báo |

### C3. NodeId theo namespace URI

Cấu hình dùng `nsu=urn:minimes:simulator:reflow;s=Reflow.PeakTemp`. Lúc kết nối, driver đổi sang index thật:

```csharp
var nodeId = ExpandedNodeId.ToNodeId(ExpandedNodeId.Parse(tag.NodeId), session.NamespaceUris);   // null = server không có URI đó
```

Bước 0 đã thấy cùng loại server mà namespace là `ns=3`, còn ở lò của ta là `ns=2`. Firmware mới có thể đổi thứ tự
namespace. Cấu hình theo URI thì không phải sửa.

### C4. Timestamp và chất lượng

- `SourceTimestamp` (lúc đo) → nếu trống thì `ServerTimestamp` → nếu vẫn trống thì giờ gateway (bẫy 3, quy tắc G9).
- `StatusCode` → `DataQuality`: Good / Uncertain / Bad (G8). Bad thì value `null`, vẫn chuyển đi để MES lưu chẩn đoán.

### C5. Chứng chỉ và môi trường

- Gateway có **chứng chỉ client riêng** (`pki/own`, tự tạo lần đầu). Server thấy client là ai, kể cả khi security None.
- `AutoAcceptUntrustedCertificates`: không ghi trong cấu hình thì **chỉ bật ở Development**. `dotnet run` dùng
  `Properties/launchSettings.json` (`DOTNET_ENVIRONMENT=Development`). Chạy file exe hoặc Windows Service thì là
  Production, tắt auto-accept: chứng chỉ server lạ nằm ở `pki/rejected`, người vận hành chuyển sang `pki/trusted`.

### C6. Log có cấu trúc, bớt nhiễu

- Template có tham số `{Machine}`, `{Tag}`, `{Endpoint}` (CLAUDE.md mục 5).
- Stack OPC UA log mỗi lần socket bị từ chối kèm cả stack trace. Driver đã log một dòng có lý do, nên `Opc.Ua.Bindings`
  hạ xuống Critical. Tương tự, "Hosting failed to start" của Host bị ẩn vì `Program.cs` đã in lỗi cấu hình gọn hơn.

---

## D. Kiểm tra (đã chạy 28/10/2026)

```
dotnet run --project src/MiniMes.MachineSimulator        # cửa sổ 1 (gõ lệnh ở đây)
dotnet run --project src/MiniMes.EdgeGateway             # cửa sổ 2
```

| Kiểm tra spec | Log gateway |
| --- | --- |
| Subscribe 3 node | `connected to opc.tcp://localhost:4840/, 3 monitored items` → `counter = 8001`, `state = 1`, `peakTemp = 245.7` |
| Deadband 0,5 °C | `peakTemp` liên tiếp: 245.25 → 244.46 → 245.78 → 245 → 245.61 (mọi bước > 0,5); thay đổi nhỏ không hiện |
| `opcua stop` | Ngay lập tức: `DISCONNECTED: keep-alive failed (BadSecureChannelClosed)`, rồi `retry in 1s`, `2s`, `4s`, `8s` |
| `opcua start` | `connected ...` và giá trị chảy lại (lần thử sớm gặp `BadServerHalted` lúc server đang khởi động) |
| `sensor bad` | `peakTemp = (null) Bad` |
| NodeId sai | `tag peakTemp (ns=2;s=Reflow.PeakTemperature) refused by the server: BadNodeIdUnknown`; URI sai: `uses namespace ... that the server does not have`; tag đúng vẫn chạy |
| Cấu hình sai | 3 lỗi in cùng lúc (tag trùng, endpoint rỗng, protocol `s7`), exit code 1 |

Unit test (`tests/MiniMes.UnitTests/Gateway/`): `GatewayOptionsValidatorTests` (cấu hình mẫu hợp lệ, 19 kiểu cấu hình
sai, hai dạng NodeId, gom đủ lỗi) và `BackoffTests` (1-2-4-8-16-30-30, reset). Tổng 709 test xanh.
`DependencyTests` có luật mới: EdgeGateway chỉ tham chiếu Contracts.

---

## E. Câu hỏi phỏng vấn

1. **"Gateway của anh phát hiện mất kết nối PLC thế nào?"** — Server đóng kênh thì publish/keep-alive lỗi ngay
   (`BadSecureChannelClosed`). Server treo hoặc mạng đứt im lặng thì keep-alive mỗi 2 s không có trả lời. Cả hai đều
   dẫn tới: báo mất kết nối, backoff, tạo lại session.
2. **"Sai NodeId thì sao?"** — Subscription vẫn tạo được, chỉ item đó có status `BadNodeIdUnknown`. Phải kiểm tra
   `Status.Error` của từng item và log Error, nếu không thì tag đó im lặng mãi.
3. **"Vì sao cấu hình tag không để trong DB MES?"** — Gateway phải chạy khi MES/mạng hỏng (G2). Nhiều gateway thì quản
   lý tập trung, nhưng gateway vẫn giữ bản cục bộ.
4. **"Callback của thư viện thì nên làm gì?"** — Chỉ đẩy dữ liệu vào hàng đợi trong RAM (`Channel`), xử lý ở thread
   khác. Chặn callback là chặn mọi notification.
5. **"Backoff để làm gì?"** — Không dồn dập vào một server đang khởi động lại; vẫn nối lại nhanh khi lỗi ngắn. Bước 6
   thêm jitter để 20 gateway không thử cùng một lúc.

---

## F. Đọc thêm

- learn.microsoft.com: "Worker services in .NET", "Options pattern" (mục *Options validation*, `ValidateOnStart`),
  "System.Threading.Channels".
- Repo `OPCFoundation/UA-.NETStandard`: `Applications/ConsoleReferenceClient` (session, subscription, reconnect handler).
- OPC UA Part 4 (Services): mục Subscription (KeepAliveCount, LifetimeCount), MonitoredItem (DataChangeFilter, Deadband).
