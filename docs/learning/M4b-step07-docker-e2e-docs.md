# M4b · Bước 7 — Docker Compose, integration test đầu-cuối, tài liệu: kiến thức cần nắm

> Tài liệu học cho bước 7 (bước cuối) của [module-04b](../../spec/module-04b-edge-gateway.md) (spec mục **2.9**, 4, 5).
> Bước trước: [M4b-step06](M4b-step06-health-status-lwt.md). Quyết định kiến trúc tổng hợp:
> [ADR-009](../../ARCHITECTURE.md#adr-009-edge-gateway-tự-viết-opc-ua-subscription-modbus-polling-outbox-sqlite-luôn-bật).

---

## A. Nghiệp vụ: gateway chạy ở đâu ngoài nhà máy

- **PC công nghiệp (IPC) Windows** trong tủ điện của xưởng, chạy gateway như **Windows Service**: tự chạy khi bật
  máy, tự khởi động lại khi chết (`sc.exe failure`), không cần ai đăng nhập. Đây là cách phổ biến nhất ở nhà máy
  vừa và nhỏ tại VN.
- **Container** (Docker/Kubernetes ở edge): khi nhà máy có hạ tầng container. Hàng đợi SQLite và chứng chỉ OPC UA phải
  nằm trên **volume**: container bị tạo lại là mất mọi thứ không nằm trên volume.
- Chạy trên PC văn phòng, Windows Update tự restart: chính là lý do outbox nằm trên đĩa (spec 1.5).

---

## B. Docker

### B1. Dockerfile multi-stage

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build       # có SDK: restore, publish
COPY global.json Directory.Build.props ./              # cùng SDK và cùng cài đặt (Nullable...) như máy dev
COPY src/MiniMes.Contracts/*.csproj ... ; RUN dotnet restore   # chỉ csproj trước: đổi code không phải restore lại
COPY src/... ; RUN dotnet publish -c Release -o /app
FROM mcr.microsoft.com/dotnet/runtime:10.0             # image chạy nhỏ, không có SDK
VOLUME ["/app/data", "/app/pki"]
```

- **Copy csproj rồi restore trước, copy code sau**: Docker cache theo từng lớp; sửa code chỉ chạy lại lớp publish.
- Build context là thư mục gốc repo (cần `global.json`, `Directory.Build.props`, `Contracts`); `.dockerignore` bỏ
  `bin/`, `obj/`, `data/`, `pki/`, tests, docs để context nhỏ và không lọt file máy dev vào image.

### B2. Compose: profile, biến môi trường, tên service

| Chủ đề | Cách làm | Vì sao |
| --- | --- | --- |
| Profile `plant` | `docker compose --profile plant up -d --build` | `up -d` quen thuộc vẫn chỉ dựng SQL + Mosquitto; không đụng simulator chạy bằng `dotnet run` (trùng cổng 4840/5020, trùng MQTT client id `sim-*` thì broker đá nhau) |
| Ghi đè cấu hình | `mqtt__host: mosquitto`, `devices__0__endpoint: opc.tcp://simulator:4840` | `__` = dấu `:` của configuration .NET; `AddEnvironmentVariables()` gọi **sau** file JSON nên biến môi trường thắng |
| Tên service = hostname | `simulator`, `mosquitto` | Trong mạng Compose, container gọi nhau bằng tên service |
| OPC UA quảng bá endpoint | `machines__1__opcua__host: simulator` | Bẫy 1 của spec 2.2: URL server quảng bá phải phân giải được ở phía client |
| Gọi API trên máy host | `host.docker.internal:5157` + `extra_hosts: host-gateway` | API chưa đóng container; trên Linux cần dòng `extra_hosts` |
| Volume | `gateway-data:/app/data`, `gateway-pki:/app/pki` | Outbox và chứng chỉ sống qua `down`/`up` |
| Lệnh console simulator | `stdin_open`, `tty` + `docker attach mes-simulator` | Thoát bằng Ctrl+P, Ctrl+Q; Ctrl+C sẽ dừng container |

Kết quả thật: build 2 image 28 s; gateway trong container nối `simulator:4840` và `simulator:5020`; simulator gọi API
trên host được; snapshot dashboard có 4 máy + GW01 `online 2/2`. Client OPC UA trên host nối `localhost:4840` được dù
server quảng bá `simulator:4840`, vì thư viện .NET thay host của endpoint bằng host của URL discovery (UaExpert thì hỏi).

---

## C. Integration test đầu-cuối

### C1. Dựng cả "nhà máy" trong một process

```
Testcontainers: Mosquitto (fixture M4, cổng cố định để stop/start)
In-process:     ReflowServer (OPC UA, simulator) + GlueMachineSlave (Modbus, simulator)
                GatewayHost.CreateBuilder([--config temp.json, --environment Development]).Build()   ← host gateway THẬT
Subscriber:     MQTT client CleanSession=false, client id cố định = "MES"
```

Để test dùng được host thật, `Program.cs` của gateway được tách thành `GatewayHost.CreateBuilder(args)`: chương trình
và test chạy **cùng một** cấu hình DI. Cấu hình test chỉ khác ở `pollMs = 200`, outbox và `pki` trong thư mục tạm.

### C2. `extern alias`: ba class `Program` cùng tên

API, simulator và gateway đều dùng top-level statements, nên mỗi assembly có một class `Program` ở namespace gốc. Test
đang dùng `WebApplicationFactory<Program>` (của API). Tham chiếu thêm 2 project kia thì `Program` bị mơ hồ (lỗi CS0433).
Cách giải:

```xml
<ProjectReference Include="...\MiniMes.EdgeGateway.csproj" Aliases="gateway" />
```
```csharp
extern alias gateway;
using GatewayHost = gateway::MiniMes.EdgeGateway.GatewayHost;
```

Kiểu trong assembly có alias không còn ở namespace toàn cục, nên `Program` của API vẫn là `Program`.

### C3. Ba kiểm tra của spec

| Test | Cách kiểm |
| --- | --- |
| Đúng topic/schema | Đủ 9 loại topic của 2 máy; **mọi** message qua được `MachineMessageReader.Read` (chính hàm của ingest MES); `source` đúng; counter Modbus = 100.000 (CDAB giải mã đúng) |
| Dừng broker 20 s | Trong lúc dừng, bộ đếm Modbus tăng mỗi 500 ms; sau khi bật lại, subscriber có **đủ** `start..last`, mỗi giá trị một lần (bỏ bản trùng theo msgId như MES), **đúng thứ tự** |
| Tắt Modbus slave | `.../L1-GLUE-01/status` = `offline`, không có `state = Down`; bật lại → `online` |

### C4. Kiểm tra test bằng đột biến (mutation)

Test xanh ngay lần đầu chưa chứng minh được gì. Tôi cố tình làm hỏng `ForwarderService` (bỏ kiểm tra kết nối, xóa
message dù publish thất bại) rồi chạy lại: test **đỏ**, subscriber chỉ nhận `[200000, 200040]`, mất 39 giá trị ở
giữa. Vậy test thực sự bảo vệ quy tắc G4 "chỉ xóa sau PUBACK". Sau đó khôi phục file.

### C5. Persistent session của subscriber

Subscriber cũng mất kết nối khi broker dừng. Với `CleanSession = false` và client id cố định, broker (có
`persistence true`, dừng/chạy lại **cùng** container) giữ subscription và xếp hàng message QoS 1 cho nó. Đó cũng là
cách ingest M4 sống sót qua lần broker restart: chuỗi "không mất" gồm outbox gateway, persistent session và khử trùng
msgId.

---

## D. Windows Service

`builder.Services.AddWindowsService(o => o.ServiceName = "MiniMesGateway")` (gói
`Microsoft.Extensions.Hosting.WindowsServices`): chạy bằng `dotnet run` thì là console bình thường, cài bằng `sc.exe`
thì là service (log ra Event Log/console tùy cấu hình, thư mục làm việc là `C:\Windows\System32`). Vì vậy mọi đường
dẫn của gateway (`gateway.tags.json`, `data/`, `pki/`) đều tính từ **thư mục exe** (`AppContext.BaseDirectory`), không
phải thư mục làm việc. Lệnh cài/gỡ ở README. Chưa cài thử (cần quyền admin).

---

## E. Đã kiểm / chưa kiểm

| Hạng mục | Trạng thái |
| --- | --- |
| Integration test toàn bộ | 281/281 xanh (1 phút 43 s); unit test 812 xanh |
| Gateway E2E (3 test) | Xanh; đột biến outbox → đỏ |
| Docker Compose profile `plant` | Chạy thật trên repo dev |
| Clone sạch → `docker compose up` | **Chưa** |
| CI GitHub Actions | **Chưa thấy chạy** (chưa push) |
| Windows Service | Lệnh trong README, **chưa cài thử** |

---

## F. Câu hỏi phỏng vấn

1. **"Anh triển khai gateway thế nào?"** — Windows Service trên IPC trong tủ điện (tự khởi động lại khi chết), hoặc
   container với volume cho hàng đợi và chứng chỉ. Cấu hình theo môi trường bằng biến môi trường.
2. **"Làm sao test được 'mất broker không mất dữ liệu'?"** — Testcontainers Mosquitto, dừng container thật 20 giây
   trong lúc bộ đếm chạy, kiểm subscriber nhận đủ và đúng thứ tự. Thử đột biến để chắc test bắt được lỗi.
3. **"Container mất thì hàng đợi đi đâu?"** — Nằm trên volume; tạo lại container vẫn còn. Quên volume là mất hàng đợi
   và phải trust lại chứng chỉ OPC UA.
4. **"Vì sao OPC UA trong Docker hay lỗi kết nối?"** — Server quảng bá endpoint theo hostname của nó; client ngoài
   Docker không phân giải được. Đặt hostname quảng bá bằng tên mà client dùng.

---

## G. Đọc thêm

- learn.microsoft.com: "Create a Windows Service using BackgroundService", "Containerize a .NET app",
  "Configuration in .NET" (mục environment variables, `__`).
- docs.docker.com: "Multi-stage builds", "Compose profiles", "Networking in Compose".
- Microsoft Learn (C#): "extern alias".
- Testcontainers for .NET: "Getting started", "Wait strategies".
