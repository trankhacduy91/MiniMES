# M4b · Bước 0 — Học OPC UA và Modbus bằng công cụ có sẵn: kiến thức cần nắm

> Tài liệu học cho bước 0 của [module-04b](../../spec/module-04b-edge-gateway.md) (spec mục **1.1, 1.2a–c, 2.4**).
> Bước này không viết code sản phẩm: chỉ chạm tay vào hai giao thức trước khi tự viết server/client ở bước 1–3.
> Ghi chú 10 dòng (phần "Kiểm tra" của spec): [docs/opcua-modbus-notes.md](../opcua-modbus-notes.md).

---

## A. Nghiệp vụ: vì sao MES cần biết hai giao thức này

### A1. Máy ngoài đời không nói MQTT

Ở M4, simulator tự phát MQTT. Ngoài đời, phần lớn máy trong nhà máy điện tử chỉ có **PLC**:
máy đời mới có **OPC UA**, máy đời cũ, đồng hồ đo và biến tần có **Modbus**. Ngoài ra còn giao thức riêng của từng hãng
(Siemens S7, Mitsubishi MC, Omron FINS). Một lớp **edge gateway** đọc các giao thức đó rồi dịch sang một hợp đồng MQTT
chung, nên MES chỉ phải hiểu một thứ. Đó là toàn bộ module M4b.

```
MES ◀── MQTT (hợp đồng M4) ── Gateway ──┬── OPC UA (subscribe) ── lò reflow
                                         └── Modbus TCP (poll)  ── máy dán keo đời cũ
```

### A2. Hai giao thức, hai triết lý

| | OPC UA | Modbus TCP |
| --- | --- | --- |
| Dữ liệu | Có **ngữ nghĩa**: node có tên, kiểu, đơn vị, chất lượng, timestamp | Chỉ là **bảng số 16-bit** đánh địa chỉ; ý nghĩa nằm trong tài liệu máy |
| Ai chủ động | Server **đẩy** khi đổi (subscription) | Master **hỏi** theo chu kỳ (polling) |
| Bảo mật | Chứng chỉ X.509 hai chiều, ký/mã hóa | Không có; phải cô lập mạng OT |
| Số 32-bit | Có sẵn kiểu `UInt32`, `Double` | Ghép **2 register**, tự lo thứ tự word |
| Độ khó | Phức tạp (đặc tả hàng nghìn trang) | Rất đơn giản (từ 1979) |

---

## B. Lab đã chạy (thay cho UaExpert / mbpoll)

Spec gợi ý UaExpert và mbpoll (công cụ GUI/CLI). Để học được ngay trên máy dev không cần cài thêm, bước này có hai
**file-based app** C# (.NET 10 chạy thẳng file `.cs`, không cần `.csproj`). Chúng làm đúng những thao tác mà UaExpert
làm, và in ra kết quả để đối chiếu với lý thuyết.

| Lab | Chạy | Làm gì |
| --- | --- | --- |
| [labs/opcua-lab.cs](labs/opcua-lab.cs) | `docker run -d --name opcplc-lab -p 50000:50000 mcr.microsoft.com/iotedge/opc-plc:latest --pn=50000 --autoaccept --unsecuretransport`<br>`dotnet run docs/learning/labs/opcua-lab.cs -- opc.tcp://localhost:50000 60 15` | Chọn endpoint, tạo session, in bảng namespace, Browse 2 cấp, Read 2 node, Subscribe 1 node hai lần (có và không deadband) rồi đếm notification |
| [labs/modbus-lab.cs](labs/modbus-lab.cs) | `dotnet run docs/learning/labs/modbus-lab.cs` | Dựng slave "máy dán keo" (bộ đếm CDAB) và master trong cùng process; đọc FC03 rồi giải mã 4 thứ tự; cố tình đọc lệch địa chỉ và đọc nhầm FC04 |

Vẫn nên thử **UaExpert** (Unified Automation, miễn phí, cần đăng ký) một lần: nối `opc.tcp://localhost:50000`, mở cây
`Objects/OpcPlc/Telemetry`, kéo `SpikeData` vào *Data Access View*. Ở phỏng vấn, người ta hay hỏi "anh dùng công cụ gì để
kiểm tra node", và UaExpert là câu trả lời chuẩn. Dọn lab: `docker rm -f opcplc-lab`.

### B1. Kết quả OPC UA (28/10/2026)

```
Endpoint  opc.tcp://localhost:50000/  security=None/None
Namespace table:   ns=0 http://opcfoundation.org/UA/ ... ns=3 http://microsoft.com/Opc/OpcPlc/ ...
Browse:            OpcPlc  ns=3;s=OpcPlc  →  Telemetry, Methods, SimulatorConfiguration
Read:              ns=3;s=SpikeData  value=58.77  type=Double  status=Good  source=11:28:11.171  server=11:28:11.242
Subscribe 15 s:    không deadband = 43 notification, deadband tuyệt đối 60 = 17
```

Đọc kết quả:

- **`ns=3` chứ không phải `ns=2`**: index phụ thuộc server. Spec của lò reflow ghi `ns=2;s=Reflow.PeakTemp` vì server
  của ta sẽ chỉ có một namespace riêng. Gateway thật nên cấu hình **URI** của namespace, hoặc ít nhất kiểm tra
  index khi kết nối, vì firmware mới có thể đổi thứ tự (bảng "Nếu bị kẹt" của spec: `BadNodeIdUnknown`).
- **SourceTimestamp < ServerTimestamp** vài chục ms: thời điểm đo và thời điểm server ghi nhận khác nhau. Gateway dùng
  `SourceTimestamp` làm `ts` (quy tắc G9).
- **Deadband giảm 60 %** message mà không cần client lọc gì. Với nhiệt độ lò (dao động ±0,8 °C, deadband 0,5 °C) mức
  giảm còn lớn hơn khi nhiệt ổn định. Đây là quy tắc G10.

### B2. Kết quả Modbus

```
Slave: HR0..3 = 0x86A0 0x0001 0x0001 0x0000      (bộ đếm 100.000 lưu CDAB, state 1, alarm 0)
FC03 đọc 0..3, giải mã bộ đếm:
  ABCD    2,258,632,705     ← "sản lượng 2 tỷ"
  CDAB          100,000     ← thứ tự thật của máy
  BADC    2,693,136,640
  DCBA       16,818,310
Float 0x42F6 0xE979: ABCD 123.456, CDAB -1.88E+25
Lệch 1 địa chỉ (đọc 1..2): 0x0001 0x0001 → 65,537
FC04 thay vì FC03:          0x0000 0x0000 0x0000 0x0000
```

Bài học: **mọi lỗi Modbus đều trả về một con số**, không có exception nào. 65.537 trông vẫn giống một bộ đếm thật. Cách
duy nhất để biết đúng là so với một giá trị đã biết (màn hình HMI của máy), và có unit test cho hàm giải mã.

---

## C. Kiến thức kỹ thuật

### C1. OPC UA: các khái niệm đã chạm tay

| Khái niệm | Gặp ở đâu trong lab |
| --- | --- |
| **ApplicationConfiguration** | Tên, `ApplicationUri`, 4 thư mục chứng chỉ (`own`, `trusted`, `issuer`, `rejected`) |
| **Chứng chỉ ứng dụng** | `CheckApplicationInstanceCertificatesAsync` tự tạo chứng chỉ tự ký lần đầu, kể cả khi dùng security `None` (server vẫn thấy client là ai) |
| **Endpoint** | `SelectEndpointAsync(..., useSecurity: false)`: hỏi server danh sách endpoint, chọn cái `None/None` |
| **Session** | Một kết nối logic có timeout (60 s). Spec mục 1.5: một session cho mỗi server, đừng mở nhiều |
| **Browse** | `Browser.BrowseAsync(ObjectIds.ObjectsFolder)` theo `HierarchicalReferences` |
| **Subscription / MonitoredItem** | Một subscription chứa nhiều item; mỗi item có `SamplingInterval`, `QueueSize`, `Filter` |
| **DataChangeFilter** | `Trigger = StatusValue` (đổi giá trị **hoặc** đổi chất lượng đều báo), `DeadbandType.Absolute` |
| **Thứ tự** | `session.AddSubscription(sub)` **trước**, `sub.CreateAsync()` **sau** |
| **Notification callback** | Chạy trên thread của thư viện: chỉ làm việc nhẹ (gateway sẽ đẩy vào `Channel`) |

### C2. Thư viện OPC UA .NET: đang ở giữa hai phiên bản

`OPCFoundation.NetStandard.Opc.Ua` hiện có hai dòng ổn định:

- **1.5.378.x** (bản 1.5.378.182, 04/10/2026): lab dùng bản này. Các API đồng bộ (`Session.Create`, `SelectEndpoint`,
  `Browse`, `ReadValue`, `Subscription.Create`) đã bị đánh dấu `[Obsolete]`, gợi ý bản async (`DefaultSessionFactory.CreateAsync`,
  `SelectEndpointAsync`, `BrowseAsync`, `ReadValueAsync`, `CreateAsync`). Mọi thành phần nhận một **`ITelemetryContext`**
  (logging + metrics), tạo bằng `DefaultTelemetry.Create(...)`.
- **2.0.x** (2.0.0 ngày 07/10/2026): bỏ hẳn API cũ.

Snippet trong spec viết theo kiểu đồng bộ cũ; lab đã viết lại theo kiểu async nên chuyển sang 2.0 sau này sẽ ít sửa.
Đây đúng là tình huống CLAUDE.md nói: snippet spec chưa được biên dịch, phải đối chiếu tài liệu của đúng phiên bản
đã cài. Cách tra nhanh: mở file XML doc trong `~/.nuget/packages/opcfoundation.netstandard.opc.ua.client/<ver>/lib/net10.0/`.

### C3. Modbus: bốn thứ cần đúng cùng lúc

Một lần đọc Modbus đúng cần đúng cả 4 thứ; sai bất kỳ cái nào cũng ra một con số trông bình thường:

1. **Unit ID** (1–247): slave nào.
2. **Function code / vùng**: FC03 holding register (`4xxxx`), FC04 input register (`3xxxx`).
3. **Địa chỉ trên dây** = số trong tài liệu − 40001 (0-based).
4. **Kiểu và thứ tự**: uint16 / int32 / float; ABCD / CDAB / BADC / DCBA.

Đọc **một khối** 4 register trong một request (spec 2.5): một lần hỏi PLC thay vì bốn. Và luôn đặt
`Transport.ReadTimeout`: slave chết mà không có timeout thì vòng poll treo im lặng.

### C4. File-based app của .NET 10

`dotnet run file.cs` biên dịch và chạy một file không cần project. Chỉ thị ở đầu file:
`#:package NModbus@3.0.83` (thêm gói NuGet), `#:property PublishAot=false` (file-based app mặc định bật Native AOT,
tắt phản chiếu; công cụ dev không cần AOT). Dự án đã dùng cách này cho `scripts/scan-bench.cs`.

---

## D. Kiểm tra (spec "Kiểm tra" của Bước 0)

- [x] Có [docs/opcua-modbus-notes.md](../opcua-modbus-notes.md) 10 dòng: NodeId, Sampling vs Publishing, 40001 là địa chỉ 0
  trên dây, CDAB là gì, kèm số đo thật từ lab.
- [x] OPC UA: Browse, Read, Subscribe, Deadband đã chạy với server thật (OPC PLC trong Docker).
- [x] Modbus: đọc/ghi holding register, ghép int32 với 4 thứ tự, thấy được "số 2 tỷ".
- [ ] (Tự làm) Mở UaExpert một lần với cùng container để quen giao diện.

---

## E. Câu hỏi phỏng vấn

1. **"NodeId `ns=2;s=Reflow.PeakTemp` nghĩa là gì?"** — namespace index 2 (một namespace riêng của server, index tra từ
   bảng namespace), định danh kiểu chuỗi `Reflow.PeakTemp`. Index có thể đổi giữa các server, URI thì không.
2. **"SamplingInterval khác PublishingInterval thế nào?"** — Sampling: server đọc nguồn bao lâu một lần. Publishing:
   server gửi gộp cho client bao lâu một lần. 500/1000 ms: lấy mẫu 2 lần/giây, gửi 1 gói/giây.
3. **"Deadband ai lọc?"** — Server lọc (`DataChangeFilter`), nên tiết kiệm cả mạng. Tôi đo: 43 → 17 notification/15 giây.
4. **"Tài liệu ghi 40001, anh đọc thế nào?"** — FC03, địa chỉ 0, đọc 2 register nếu là 32-bit; thử 4 thứ tự với giá
   trị đã biết trên HMI.
5. **"Đọc ra 2 tỷ sản phẩm, lỗi gì?"** — gần như chắc chắn sai thứ tự word: 100.000 lưu CDAB đọc nhầm ABCD ra 2.258.632.705.

---

## F. Đọc thêm

- Spec mục 5.4 (OPC Foundation Part 4 Services; repo `OPCFoundation/UA-.NETStandard`, thư mục `Applications/ConsoleReferenceClient`).
- README image OPC PLC: repo `Azure-Samples/iot-edge-opc-plc` (danh sách node giả lập, tham số `--pn`, `--autoaccept`, `--unsecuretransport`).
- Modbus Organization: "Modbus Application Protocol Specification" (bảng function code).
