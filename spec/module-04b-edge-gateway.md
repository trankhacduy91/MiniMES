# Module 4b — Edge Gateway: OPC UA + Modbus TCP → MQTT (store-and-forward)

> Module này dựng lớp **gateway/edge** nằm giữa máy (PLC) và MES, đúng như kiến trúc ngoài nhà máy. Simulator đóng thêm hai vai "PLC thật": một **OPC UA server** (lò reflow `L1-REFLOW-01`) và một **Modbus TCP slave** (máy dán keo đời cũ `L1-GLUE-01`). Project mới **`MiniMes.EdgeGateway`** (.NET Worker Service) đọc hai máy đó: OPC UA bằng **subscription**, Modbus bằng **polling**. Gateway chuẩn hóa dữ liệu thành message theo **hợp đồng MQTT của M4** (Unified Namespace) và **đệm vào SQLite** khi mất kết nối tới broker.
> Sau khi xong bạn có: dashboard M4 hiện cả 3 loại máy (MQTT, OPC UA, Modbus) giống nhau mà `MqttIngestService` gần như không phải sửa; ngắt broker 60 giây không mất message nào; tắt OPC UA server thì máy hiện `Offline` (không phải `Down`) và tự kết nối lại; nhiệt độ lò chảy vào `ProcessValue` cho SPC ở M7.
> Thời lượng ước tính: ~24 giờ (khoảng 13–15 buổi 1,5–2 giờ). Phần khó nhất là OPC UA (khái niệm mới, thư viện lớn).
> Module tiên quyết: M4 (hợp đồng dữ liệu, `MqttIngestService`, `MachineTag`/`ProcessValue`, Offline watchdog, Testcontainers Mosquitto), M1 (`Machine.DataSource`, máy `L1-REFLOW-01`, `L1-GLUE-01`).
>
> Module mới theo [spec-v2](spec-v2.md) (mục A2 #14–17, B5 M4b, B10 #6–8: gateway tự viết bằng .NET, bộ đệm SQLite).

---

## 1. Kiến thức nghiệp vụ MES

### 1.1 Bức tranh tổng thể

Ở M4, máy "thông minh" tự phát MQTT. Ngoài đời, phần lớn máy trong nhà máy điện tử ở VN **không** nói MQTT. Bên trong là PLC (Mitsubishi, Omron, Keyence, Siemens) với giao thức riêng của hãng, hoặc OPC UA (máy đời mới), hoặc Modbus (máy đời cũ, thiết bị đo, biến tần). MES (Level 3) cũng **không nên** nói chuyện trực tiếp với từng PLC (Level 1). Ở giữa có một lớp gọi là **gateway** hay **edge**:

```
 Level 3   MES (MqttIngestService)          ← chỉ hiểu 1 hợp đồng: MQTT + JSON (M4)
              ▲
 Level 2.5 Broker MQTT (Mosquitto)          ← Unified Namespace
              ▲  MQTT, QoS 1
           ┌──┴───────────────────────────────────────────┐
           │ EDGE GATEWAY (MiniMes.EdgeGateway)            │  ← module này
           │  • dịch giao thức  • chuẩn hóa tag  • đệm     │
           └──┬───────────────────────┬────────────────────┘
              │ OPC UA (subscribe)    │ Modbus TCP (poll)
 Level 1   [Lò reflow: OPC UA server] [Máy dán keo: Modbus slave]   ← simulator đóng vai
```

Gateway làm 3 việc:

1. **Dịch giao thức**: đọc OPC UA, Modbus (ngoài đời còn S7, MC Protocol, FINS, EtherNet/IP...) và phát ra một dạng chung.
2. **Chuẩn hóa tên và ý nghĩa**: thanh ghi `40001–40002` của máy dán keo hay node `ns=2;s=Reflow.Counter` của lò reflow đều thành `count` của máy tương ứng trong UNS. MES không cần biết thanh ghi nào là gì.
3. **Đệm khi mạng đứt (store-and-forward)**: mạng giữa xưởng và phòng server đứt 5 phút thì gateway ghi dữ liệu xuống đĩa, có mạng lại thì gửi bù theo đúng thứ tự.

**Sản phẩm thật làm việc này**: Kepware KEPServerEX (rất phổ biến ở nhà máy Nhật/Hàn tại VN), Ignition Edge, Node-RED, HighByte, United Manufacturing Hub (mã nguồn mở), Cogent DataHub. Nhiều nhà máy vừa và nhỏ tự viết gateway bằng C# vì đội IT MES đã quen C# và cần đọc vài loại PLC cụ thể. Đây chính là việc dự án này mô phỏng.

**Vì sao nhà tuyển dụng hỏi nhiều.** Câu "Anh kết nối PLC thế nào?" gần như luôn có trong phỏng vấn IT MES ở KCN, vì đây là nơi phát sinh nhiều sự cố nhất: sai địa chỉ thanh ghi, sai thứ tự byte, chứng chỉ OPC UA hết hạn, gateway treo, mạng xưởng chập chờn. Module này cho bạn trải nghiệm trên simulator những lỗi đó.

### 1.2 Khái niệm & thuật ngữ

#### a) OPC UA

**OPC UA** (Open Platform Communications Unified Architecture, IEC 62541) là chuẩn mở để trao đổi dữ liệu công nghiệp, do OPC Foundation quản lý. Cổng mặc định 4840, endpoint dạng `opc.tcp://host:4840`.

| Khái niệm | Nghĩa | Ví dụ trong dự án |
| --- | --- | --- |
| **Server / Client** | Máy (hoặc PLC) chạy OPC UA server; gateway là client | Simulator chạy server cho lò reflow; gateway là client |
| **Address space** | Cây các node mà server công bố, giống cây thư mục | `Objects/Reflow/PeakTemp`, `Objects/Reflow/Counter`, `Objects/Reflow/State` |
| **Node / NodeId** | Mỗi điểm dữ liệu là một node có định danh `ns=<namespace>;s=<chuỗi>` hoặc `i=<số>` | `ns=2;s=Reflow.PeakTemp` |
| **Variable node** | Node có giá trị, **kiểu dữ liệu**, đơn vị, timestamp | `PeakTemp`: Double, °C |
| **Browse** | Duyệt cây để tìm node | Dùng UaExpert để xem server có gì |
| **Read / Write** | Đọc/ghi một lần | Đọc cấu hình máy khi khởi động |
| **Subscription** | Client đăng ký; **server tự gửi** khi giá trị đổi | Gateway subscribe 3 node của lò |
| **MonitoredItem** | Một node trong subscription, có `SamplingInterval` (server lấy mẫu bao lâu một lần) và bộ lọc | PeakTemp lấy mẫu 500 ms |
| **PublishingInterval** | Server gom thay đổi và gửi cho client bao lâu một lần | 1000 ms |
| **Deadband** | Bỏ qua thay đổi nhỏ hơn ngưỡng | Absolute 0,5 °C |
| **StatusCode** | Mỗi giá trị đi kèm chất lượng: `Good`, `Uncertain`, `Bad...` | Cảm biến đứt dây → `BadSensorFailure` |
| **SourceTimestamp / ServerTimestamp** | Thời điểm giá trị được đo / server ghi nhận | Dùng `SourceTimestamp` làm `ts` |
| **Security** | `SecurityPolicy` (None, Basic256Sha256...), `MessageSecurityMode` (None/Sign/SignAndEncrypt), xác thực bằng **chứng chỉ X.509** hai chiều; user/password tùy chọn | Dev: `None` + tự chấp nhận chứng chỉ. Production: bắt buộc Sign/Encrypt |

**Vì sao OPC UA là chuẩn được ưa chuộng**: có **ngữ nghĩa** (tên, kiểu, đơn vị nằm trên node, không phải "thanh ghi 40001"), có **subscription** (không phải hỏi liên tục), có **bảo mật** thật, độc lập hãng PLC. Nhược điểm: phức tạp (đặc tả hàng nghìn trang), quản lý chứng chỉ phiền, PLC đời cũ không có.

#### b) Modbus TCP

**Modbus** là giao thức từ năm 1979 (Modicon), cực kỳ phổ biến vì đơn giản. Modbus TCP chạy cổng 502.

| Khái niệm | Nghĩa |
| --- | --- |
| **Master / Slave** (tài liệu mới gọi Client / Server) | Master (gateway) hỏi, slave (máy) trả lời. Slave không tự gửi gì |
| **Unit ID** | Số hiệu slave (1–247), cần khi qua bộ chuyển Modbus TCP ↔ RTU |
| **4 vùng dữ liệu** | Coil (bit, đọc/ghi), Discrete Input (bit, chỉ đọc), Input Register (16-bit, chỉ đọc), **Holding Register** (16-bit, đọc/ghi) |
| **Function code** | `01` đọc coil, `02` đọc discrete input, `03` **đọc holding register**, `04` đọc input register, `05`/`06` ghi 1 coil/register, `15`/`16` ghi nhiều |
| **Địa chỉ** | Tài liệu máy hay ghi `40001` (quy ước: `4xxxx` = holding register, bắt đầu từ 1). Trên đường truyền là **địa chỉ 0** của vùng holding register. Lệch 1 là lỗi kinh điển |
| **Thanh ghi 16-bit** | Mỗi register chỉ 16-bit. Số 32-bit (int32, float) nằm trên **2 register liên tiếp** |
| **Thứ tự byte / word** | Ghép 2 register thành 32-bit có 4 kiểu: `ABCD` (big-endian, chuẩn), `CDAB` (đảo word, rất hay gặp), `BADC`, `DCBA`. Tài liệu máy thường **không ghi rõ** |
| **Polling** | Master hỏi theo chu kỳ (ví dụ 1 giây). Thay đổi giữa hai lần hỏi bị bỏ lỡ |
| **Không có bảo mật** | Ai vào được mạng là đọc/ghi được. Phải cô lập mạng OT |

Ví dụ thanh ghi của máy dán keo (simulator):

| Địa chỉ tài liệu | Địa chỉ trên dây (0-based) | Nội dung | Kiểu |
| --- | --- | --- | --- |
| 40001–40002 | 0–1 | Bộ đếm tích lũy | int32, word order `CDAB` (cố tình, để bạn gặp lỗi thật) |
| 40003 | 2 | Trạng thái: 1 Running, 2 Idle, 3 Down, 4 Setup, 5 PlannedStop | uint16 |
| 40004 | 3 | Mã alarm (0 = không có) | uint16 |

Bộ đếm `100.000` lưu `CDAB` là register[0] = `0x86A0`, register[1] = `0x0001`. Đọc nhầm thành `ABCD` ra `0x86A00001` = 2.258.632.705. Đây là sự cố "sản lượng 2 tỷ" mà ai làm Modbus cũng từng gặp.

#### c) Polling hay subscription

| | Polling (Modbus) | Subscription (OPC UA, MQTT) |
| --- | --- | --- |
| Ai khởi động | Client hỏi liên tục | Server/publisher đẩy khi đổi |
| Tải mạng khi không đổi | Vẫn tốn | Gần như không |
| Bỏ lỡ thay đổi ngắn | Có (giữa 2 lần hỏi) | Không, nếu sampling đủ nhanh |
| Độ trễ | Tới 1 chu kỳ poll | Theo sampling + publishing interval |
| Phát hiện mất kết nối | Ngay khi request lỗi | Cần keep-alive |
| Hợp với | Thiết bị đơn giản, ít điểm | Nhiều điểm, cần phản ứng nhanh |

Bộ đếm tích lũy (M4) làm polling an toàn: bỏ lỡ một lần hỏi không mất sản lượng, chỉ mất độ mịn.

#### d) Các giao thức chỉ cần biết để trả lời phỏng vấn (không code)

| Giao thức | Dùng ở đâu | Thư viện .NET bạn có thể nhắc tên |
| --- | --- | --- |
| **Siemens S7** (S7comm, cổng 102) | PLC Siemens S7-300/400/1200/1500, phổ biến ở nhà máy châu Âu | `S7netplus` (mã nguồn mở) |
| **Mitsubishi MC Protocol** (SLMP) | PLC MELSEC iQ-R/Q/FX, rất phổ biến ở nhà máy Nhật tại VN | `HslCommunication` (có bản thương mại), hoặc tự viết theo tài liệu SLMP |
| **Omron FINS** | PLC Omron | `HslCommunication` |
| **EtherNet/IP** | PLC Rockwell/Allen-Bradley | `libplctag` (có wrapper .NET) |
| **SECS/GEM** (SEMI E5/E30/E37) | Thiết bị bán dẫn ở fab/OSAT (Intel, Amkor, Hana Micron tại VN): máy gửi sự kiện, MES gửi lệnh (remote command), tải recipe | Sản phẩm thương mại (Cimetrix, PEER Group) |
| **IPC-CFX / IPC-Hermes-9852** | Chuẩn ngành SMT: máy gửi sự kiện qua AMQP; Hermes cho chuyển bo giữa máy | SDK CFX của IPC (.NET) |

Câu trả lời mẫu: "Tôi đã code OPC UA và Modbus TCP. Với S7 hay MC Protocol, nguyên lý giống Modbus (đọc vùng nhớ theo địa chỉ, ghép byte), chỉ khác giao thức gói tin; tôi sẽ dùng thư viện có sẵn và kiến trúc gateway y như dự án, chỉ thêm một `IDeviceDriver` mới."

#### e) Store-and-forward

Gateway ghi mọi message xuống **hàng đợi cục bộ trên đĩa** (SQLite) trước, rồi một tác vụ riêng gửi lên broker theo thứ tự và **chỉ xóa khi broker xác nhận** (PUBACK của QoS 1). Mất broker 10 phút: hàng đợi dài ra; có broker lại: gửi bù hết. Gateway khởi động lại: hàng đợi vẫn còn trên đĩa.

Giới hạn: đĩa không vô hạn. Đặt trần (ví dụ 500.000 message hoặc 7 ngày). Đầy thì **bỏ message cũ nhất** và ghi log mức Error. Vì bộ đếm là tích lũy, bỏ message cũ chỉ mất độ mịn chứ không mất tổng sản lượng.

#### f) Offline ở tầng gateway

Ở M4, `Offline` = MES không nhận heartbeat 30 giây. Với máy đi qua gateway có thêm một trường hợp: **gateway còn sống, nhưng mất kết nối tới máy** (OPC UA session đứt, Modbus timeout). Gateway phải nói rõ điều đó bằng cách publish `status = offline` (retained) cho máy đó và ngừng heartbeat của máy, chứ **không** được tự suy ra `state = Down`. Gateway không biết máy có đang chạy hay không; nó chỉ biết là không đọc được.

Ngược lại, khi **chính gateway** chết, LWT của gateway (`minimes/{site}/_sys/gateway/{id}/status = offline`) báo cho MES. Dashboard hiện "Gateway GW01 offline" và tất cả máy của nó chuyển Offline sau 30 giây (watchdog M4).

### 1.3 Một ngày ở xưởng

Nhân vật: **Dũng** (IT MES trực), **chị Lan** (supervisor), **Minh** (QC).

1. **07:00.** Dũng xem dashboard: `L1-SMT-01` nhãn `MQTT`, `L1-REFLOW-01` nhãn `OPC UA` (Peak 245,1 °C), `L1-GLUE-01` nhãn `Modbus`. Cả ba xanh. Thẻ "Gateway GW01: online, queue 0".
2. **08:30.** Kỹ sư thiết bị thay PLC máy dán keo bằng PLC mới, tài liệu ghi bộ đếm ở `40001`. Dashboard báo sản lượng máy dán keo giờ 8 là **2.258 triệu**. M5 cảnh báo "Performance 1.000.000%". Dũng biết ngay: sai thứ tự word. Anh sửa `byteOrder` trong `gateway.tags.json` từ `ABCD` thành `CDAB`, restart gateway. Delta giờ trở lại bình thường. Anh ghi vào `docs/incidents.md`.
3. **10:15.** Switch giữa xưởng và phòng server bị khởi động lại, mất mạng 4 phút. Gateway mất broker, ghi vào SQLite: 1.900 message. Dashboard (phía MES) thấy cả 3 máy Offline (watchdog 30 giây) vì không có heartbeat. 10:19 có mạng lại: gateway gửi bù 1.900 message trong khoảng 20 giây theo đúng thứ tự. Bộ đếm giờ 10 khớp với PLC. Không ai phải nhập tay.
4. **13:40.** Lò reflow cập nhật firmware, OPC UA server tắt 2 phút. Gateway publish `status = offline` cho riêng `L1-REFLOW-01`; ô lò chuyển xám **Offline**, không có Andon (vì không phải Down). Hai máy còn lại vẫn xanh. Server lên lại, gateway tự tạo session mới, subscribe lại, ô lò xanh.
5. **14:00–14:20.** Nhiệt độ vùng peak trôi từ 245 lên 251 °C (quạt làm mát yếu). Gateway gửi mỗi khi đổi > 0,5 °C. M7 lấy mẫu, vẽ biểu đồ kiểm soát, rule "7 điểm liên tiếp trên đường tâm" bật lúc 14:12. Minh nhận cảnh báo SPC (M7).
6. **16:00.** Dũng mở UaExpert, kết nối thử `opc.tcp://reflow:4840` để xác nhận node vẫn đúng tên sau firmware. Có, `ns=2;s=Reflow.PeakTemp`.

### 1.4 Quy tắc nghiệp vụ & lý do tồn tại

| # | Quy tắc | Lý do |
| --- | --- | --- |
| G1 | Gateway publish **đúng hợp đồng M4** (topic UNS, JSON, QoS, retain) | MES chỉ hiểu một hợp đồng; thêm loại máy không đụng MES |
| G2 | Cấu hình tag (endpoint, node, địa chỉ thanh ghi, byte order, map trạng thái) nằm trong **file cấu hình của gateway**, không nằm trong DB MES | Gateway phải khởi động và chạy được khi MES/mạng hỏng (M1 mục 4) |
| G3 | Mã máy trong cấu hình gateway phải trùng `Machine.MqttTopicKey` (M1); tag process phải có trong `MachineTag` (M4) | MES bỏ message của máy/tag lạ; sai mã là mất dữ liệu im lặng |
| G4 | Mọi message đi qua hàng đợi SQLite; chỉ xóa sau khi broker xác nhận (QoS 1 PUBACK) | Không mất dữ liệu khi broker/mạng/gateway lỗi |
| G5 | Gửi bù theo **đúng thứ tự** đã ghi | Đổi thứ tự làm sai lịch sử state và có thể làm bộ đếm bị hiểu nhầm là reset |
| G6 | `msgId` cố định theo (máy, tag, `SourceTimestamp`, giá trị); gửi lại giữ nguyên `msgId` | MES (M4) khử trùng được khi gateway gửi lại hoặc OPC UA phát lại sau reconnect |
| G7 | Mất kết nối tới máy → publish `status = offline` (retained) cho máy đó, ngừng heartbeat; **không** tự đặt `Down` | Gateway không biết máy có chạy hay không (mục 1.2f) |
| G8 | Giá trị có `StatusCode` không phải Good (OPC UA) hoặc lỗi đọc (Modbus) → `quality = Bad/Uncertain`, vẫn gửi | MES lưu để chẩn đoán nhưng không dùng cho delta bộ đếm hay SPC |
| G9 | `ts` = `SourceTimestamp` của OPC UA; Modbus không có timestamp nên `ts` = giờ gateway lúc đọc (UTC) | Giữ thời điểm gần với lúc đo nhất có thể |
| G10 | Tag process chỉ gửi khi vượt deadband hoặc đã quá 60 giây | Giảm 90–99% message mà không mất thông tin |
| G11 | Hàng đợi có trần; đầy thì bỏ bản cũ nhất + log Error + chỉ số `DroppedCount` | Không để đầy đĩa làm treo cả máy gateway |

### 1.5 Sai lầm/sự cố thường gặp ngoài đời

| Sự cố | Triệu chứng | Phòng/xử lý |
| --- | --- | --- |
| Lệch địa chỉ 1 (40001 vs 0) | Đọc ra giá trị của thanh ghi bên cạnh | Cấu hình ghi rõ địa chỉ 0-based; test đọc thử bằng công cụ (Modbus Poll, `mbpoll`) |
| Sai thứ tự byte/word | Số khổng lồ hoặc âm | Hàm giải mã có 4 kiểu, unit test, thử từng kiểu với giá trị đã biết |
| Chứng chỉ OPC UA không được tin | `BadSecurityChecksFailed`, `BadCertificateUntrusted` | Copy chứng chỉ client vào thư mục trusted của server (và ngược lại); dev mới dùng auto-accept |
| Chứng chỉ hết hạn | Một sáng mọi máy OPC UA mất kết nối | Theo dõi ngày hết hạn, cảnh báo trước 30 ngày |
| Poll quá dày | PLC chậm, máy báo lỗi giao tiếp | Modbus: 500–1000 ms là đủ cho MES; gom nhiều register trong 1 request |
| Quá nhiều session OPC UA | Server từ chối (`BadTooManySessions`) | Một session/server, nhiều monitored item trong một subscription |
| Gateway chạy trên PC văn phòng, Windows Update tự restart | Mất dữ liệu nếu không có store-and-forward | Chạy như Windows Service, hàng đợi trên đĩa, máy riêng cho gateway |
| Đồng hồ PC gateway lệch | `ts` sai, OEE theo giờ lệch | Đồng bộ NTP; MES so `ts` với `ReceivedAt` (M4) |
| Đọc được thì gửi hết, không deadband | Hàng chục nghìn message/phút, DB phình | Deadband + chỉ gửi khi đổi |

### 1.6 Bảng thuật ngữ

| English | Tiếng Việt | 日本語 | Giải thích ngắn |
| --- | --- | --- | --- |
| Edge / Gateway | Cổng biên | エッジ / ゲートウェイ | Máy đứng giữa thiết bị và hệ thống trên |
| OPC UA | OPC UA | OPC UA | Chuẩn trao đổi dữ liệu công nghiệp có ngữ nghĩa và bảo mật |
| Address space | Không gian địa chỉ | アドレス空間 | Cây node của OPC UA server |
| Node / NodeId | Nút / định danh nút | ノード | Một điểm dữ liệu |
| Subscription | Đăng ký nhận thay đổi | サブスクリプション | Server tự gửi khi đổi |
| Monitored item | Mục giám sát | モニタードアイテム | Node trong subscription |
| Deadband | Dải chết | 不感帯 | Bỏ qua thay đổi nhỏ |
| Modbus master/slave | Chủ/tớ Modbus | マスタ/スレーブ | Bên hỏi/bên trả lời |
| Holding register | Thanh ghi giữ | 保持レジスタ | Vùng 16-bit đọc/ghi |
| Function code | Mã chức năng | ファンクションコード | Loại thao tác Modbus |
| Byte order / Endianness | Thứ tự byte | バイトオーダー / エンディアン | Cách ghép byte thành số |
| Polling | Hỏi vòng | ポーリング | Hỏi theo chu kỳ |
| Store-and-forward | Lưu rồi chuyển | ストア＆フォワード | Đệm khi mất kết nối |
| PLC | PLC | PLC / シーケンサ | Bộ điều khiển trong máy |
| Unified Namespace | Không gian tên thống nhất | 統合ネームスペース | Một cây topic chung theo ISA-95 |
| Certificate trust list | Danh sách chứng chỉ tin cậy | 信頼リスト | Chứng chỉ nào được kết nối |

---

## 2. Kiến thức lập trình

Thứ tự đọc: 2.1 (cấu trúc project) → 2.2–2.3 (OPC UA server/client) → 2.4–2.5 (Modbus) → 2.6 (chuẩn hóa, `msgId`) → 2.7 (store-and-forward) → 2.8 (health, reconnect) → 2.9 (chạy như dịch vụ).

> **Phiên bản.** OPC UA dùng gói chính thức `OPCFoundation.NetStandard.Opc.Ua` (server: `...Opc.Ua.Server`, client: `...Opc.Ua.Client`, cấu hình: `...Opc.Ua.Configuration`). Snippet viết theo dòng 1.5.x; nhánh 2.0 mới có thay đổi API (nhiều method chuyển sang async). Modbus dùng `NModbus` 3.x. **Khi compile lỗi, đối chiếu README và thư mục `Applications/` (mẫu ConsoleReferenceServer, ConsoleReferenceClient) của repo `OPCFoundation/UA-.NETStandard` đúng phiên bản bạn cài**, đừng sửa mò.
>
> **Ghi chú khi làm (Bước 0, 28/10/2026).** Bản 1.5.378.182 đã đánh dấu `[Obsolete]` các API đồng bộ trong snippet (`Session.Create`, `CoreClientUtils.SelectEndpoint`, `Subscription.Create`, `Browser.Browse`, `ReadValue`, `new ApplicationInstance(config)`). Dùng bản async: `new DefaultSessionFactory(telemetry).CreateAsync(...)`, `SelectEndpointAsync(config, url, false, telemetry)`, `sub.CreateAsync()`, `BrowseAsync`, `ReadValueAsync`, `new ApplicationInstance(config, telemetry)`; `telemetry = DefaultTelemetry.Create(...)` (`ITelemetryContext`). Dòng 2.0.x (07/10/2026) bỏ hẳn API cũ. Mẫu chạy được: `docs/learning/labs/opcua-lab.cs`. Namespace index trên OPC PLC là `ns=3`, không phải `ns=2`.

### 2.1 Cấu trúc project và trừu tượng `IDeviceDriver`

```
src/MiniMes.EdgeGateway/                  Worker Service (.NET 10)
  Program.cs                              Host, UseWindowsService(), options, DI
  Config/GatewayOptions.cs                site, area, gatewayId, mqtt, queue
  Config/DeviceOptions.cs                 đọc gateway.tags.json (mục 2.6)
  Drivers/IDeviceDriver.cs                StartAsync / StopAsync / event OnValue / OnConnectionChanged
  Drivers/OpcUa/OpcUaDriver.cs            mục 2.3
  Drivers/Modbus/ModbusDriver.cs          mục 2.5
  Drivers/Modbus/RegisterDecoder.cs       hàm thuần giải mã byte order (mục 2.4)
  Pipeline/Normalizer.cs                  giá trị thô → MachineMessage + topic (mục 2.6)
  Pipeline/MessageIdFactory.cs            msgId cố định (mục 2.6)
  Queue/SqliteOutbox.cs                   hàng đợi trên đĩa (mục 2.7)
  Queue/ForwarderService.cs               BackgroundService gửi lên MQTT (mục 2.7)
  Health/HealthPublisher.cs               heartbeat máy, status máy/gateway (mục 2.8)
src/MiniMes.MachineSimulator/
  OpcUa/ReflowServer.cs                   OPC UA server (mục 2.2)
  Modbus/GlueMachineSlave.cs              Modbus slave (mục 2.4)
```

Gateway **dùng lại** `MachineMessage`, `MachineTopic`, `MachineJson` trong `MiniMes.Contracts` (M4). Gateway không tham chiếu Domain, Application hay Infrastructure của MES: nó là một chương trình độc lập, chỉ chung hợp đồng.

```csharp
public sealed record RawValue(string Machine, string Tag, object? Value, DataQuality Quality,
                              DateTime SourceTsUtc, string Source);

public interface IDeviceDriver : IAsyncDisposable
{
    string Machine { get; }
    event Func<RawValue, Task>? ValueReceived;
    event Func<string /*machine*/, bool /*connected*/, Task>? ConnectionChanged;
    Task RunAsync(CancellationToken ct);    // tự kết nối lại bên trong, chỉ thoát khi ct bị hủy
}
```

Mỗi máy trong cấu hình tạo một driver. Thêm giao thức mới (S7, MC) = thêm một class hiện thực `IDeviceDriver`, không sửa pipeline.

### 2.2 OPC UA server trong simulator (vai lò reflow)

**Nó là gì.** Một OPC UA server nhỏ chạy trong `MiniMes.MachineSimulator`, công bố 3 node và cập nhật giá trị theo vòng chu kỳ của máy.

**Vì sao tự viết thay vì dùng server có sẵn.** Container `mcr.microsoft.com/iotedge/opc-plc` (OPC PLC của Microsoft) có sẵn node giả lập và đủ để **học** ở Bước 0. Nhưng demo cần **điều khiển kịch bản**: ép nhiệt độ trôi đúng lúc, ép máy Down, tắt server 2 phút. Tự viết trong simulator thì dùng chung vòng lệnh console của M4.

**Khái niệm cốt lõi.**

- `ApplicationConfiguration`: tên ứng dụng, URI, nơi lưu chứng chỉ (thư mục `pki/own`, `pki/trusted`, `pki/rejected`), endpoint, security policy. Có thể tạo từ file XML hoặc bằng code.
- `ApplicationInstance.CheckApplicationInstanceCertificate(...)`: lần đầu chạy tự tạo chứng chỉ tự ký.
- `StandardServer` + một **NodeManager** kế thừa `CustomNodeManager2`: trong `CreateAddressSpace` tạo folder `Reflow` và các `BaseDataVariableState`.
- Cập nhật giá trị: gán `Value`, `Timestamp`, `StatusCode`, rồi gọi `ClearChangeMasks` để server biết có thay đổi và đẩy cho subscription.

**Snippet** (rút gọn, kiểm tra tên API với mẫu `ConsoleReferenceServer` của đúng phiên bản):

```csharp
public sealed class ReflowNodeManager : CustomNodeManager2
{
    private BaseDataVariableState<double>? _peakTemp;
    private BaseDataVariableState<uint>? _counter;
    private BaseDataVariableState<int>? _state;

    public ReflowNodeManager(IServerInternal server, ApplicationConfiguration cfg)
        : base(server, cfg, "urn:minimes:simulator:reflow") { }

    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        lock (Lock)
        {
            var folder = CreateFolder(null, "Reflow");                         // helper tự viết: FolderState + AddReference tới ObjectsFolder
            AddRootNotifier(folder);
            _peakTemp = CreateVariable<double>(folder, "PeakTemp", DataTypeIds.Double);   // NodeId: ns=2;s=Reflow.PeakTemp
            _counter  = CreateVariable<uint>(folder, "Counter", DataTypeIds.UInt32);
            _state    = CreateVariable<int>(folder, "State", DataTypeIds.Int32);          // 1 Running, 2 Idle, 3 Down...
            AddPredefinedNode(SystemContext, folder);
        }
    }

    // Vòng chu kỳ của SimulatedMachine gọi hàm này (đừng sửa node từ nhiều thread không khóa)
    public void Publish(double peakTemp, uint counter, int state, StatusCode quality)
    {
        lock (Lock)
        {
            var now = DateTime.UtcNow;
            Set(_peakTemp!, peakTemp, now, quality);
            Set(_counter!, counter, now, StatusCodes.Good);
            Set(_state!, state, now, StatusCodes.Good);
        }
    }

    private void Set<T>(BaseDataVariableState<T> v, T value, DateTime ts, StatusCode q)
    {
        v.Value = value; v.Timestamp = ts; v.StatusCode = q;
        v.ClearChangeMasks(SystemContext, false);                              // báo cho subscription
    }
}
```

Lệnh console thêm cho simulator: `drift L1-REFLOW-01 0.3` (nhiệt độ tăng 0,3 °C/phút), `sensor L1-REFLOW-01 bad` (gửi `BadSensorFailure`), `opcua stop|start` (tắt/bật server).

**Bẫy.** (1) Endpoint phải dùng hostname mà client với tới được (`opc.tcp://simulator:4840` trong Docker, `opc.tcp://localhost:4840` khi chạy trên máy); server quảng bá endpoint theo hostname của nó, client chạy ngoài Docker có thể nhận URL không phân giải được. (2) Quên `ClearChangeMasks` thì client subscribe không nhận gì dù `Read` thấy giá trị mới. (3) Chứng chỉ lưu theo thư mục làm việc: chạy từ thư mục khác là tạo chứng chỉ mới, client phải trust lại.

### 2.3 OPC UA client trong gateway: session, subscription, reconnect

**Nó là gì.** `OpcUaDriver` tạo **một session** tới server, **một subscription** chứa mọi tag của máy, nhận notification và đẩy `RawValue` vào pipeline.

**Khái niệm cốt lõi.**

- Chọn endpoint: `CoreClientUtils.SelectEndpoint(config, url, useSecurity)`. Dev: `useSecurity: false`. Ghi rõ trong README rằng production phải bật `SignAndEncrypt` và trust chứng chỉ thủ công.
- `SamplingInterval` (server lấy mẫu) ≤ `PublishingInterval` (server gửi gộp). Ví dụ 500 ms / 1000 ms.
- `DataChangeFilter` với `DeadbandType.Absolute`: server tự lọc thay đổi nhỏ. Gateway khỏi phải lọc.
- `KeepAlive` của session báo mất kết nối; `SessionReconnectHandler` của thư viện thử nối lại và **chuyển subscription** (transfer) sang session mới; không được thì tạo session mới và subscription mới.
- Khi reconnect, server có thể **phát lại** notification cũ (republish). Đó là lý do `msgId` phải cố định theo nội dung (mục 2.6).

```csharp
public async Task RunAsync(CancellationToken ct)
{
    var backoff = Backoff.Exponential(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
    while (!ct.IsCancellationRequested)
    {
        try
        {
            var endpoint = CoreClientUtils.SelectEndpoint(_appConfig, _device.Endpoint, useSecurity: false);
            using var session = await Session.Create(_appConfig,
                new ConfiguredEndpoint(null, endpoint, EndpointConfiguration.Create(_appConfig)),
                updateBeforeConnect: false, sessionName: $"minimes-gw-{_device.Machine}",
                sessionTimeout: 60_000, identity: new UserIdentity(), preferredLocales: null);

            var sub = new Subscription(session.DefaultSubscription) { PublishingInterval = 1000 };
            foreach (var tag in _device.Tags)
            {
                var item = new MonitoredItem(sub.DefaultItem)
                {
                    StartNodeId = tag.NodeId, SamplingInterval = tag.SamplingMs, QueueSize = 10,
                    Filter = tag.Deadband is { } db
                        ? new DataChangeFilter { Trigger = DataChangeTrigger.StatusValue,
                                                 DeadbandType = (uint)DeadbandType.Absolute, DeadbandValue = db }
                        : null
                };
                item.Notification += (mi, _) =>
                {
                    foreach (var v in mi.DequeueValues())
                        _ = RaiseValueAsync(new RawValue(_device.Machine, tag.Name, v.Value,
                                 MapQuality(v.StatusCode), v.SourceTimestamp, "opcua"));
                };
                sub.AddItem(item);
            }
            session.AddSubscription(sub);
            sub.Create();

            await RaiseConnectionAsync(true);
            backoff.Reset();
            await WaitUntilBrokenAsync(session, ct);        // chờ KeepAlive báo lỗi (session.KeepAlive += ...) hoặc ct
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "OPC UA {Machine} lỗi kết nối", _device.Machine);
        }
        await RaiseConnectionAsync(false);                  // → status offline cho máy (mục 2.8)
        await Task.Delay(backoff.Next(), ct);
    }
}

static DataQuality MapQuality(StatusCode s) =>
    StatusCode.IsGood(s) ? DataQuality.Good : StatusCode.IsUncertain(s) ? DataQuality.Uncertain : DataQuality.Bad;
```

Snippet trên tạo lại session mỗi lần lỗi (đơn giản, dễ hiểu). Khi đã chạy được, thử thay bằng `SessionReconnectHandler` để giữ subscription qua lần mất kết nối ngắn; ghi lại khác biệt vào `docs/incidents.md`.

**Bẫy.** (1) Xử lý nặng trong `Notification`: callback chạy trên thread của thư viện, chặn nó là chặn mọi notification. Chỉ tạo `RawValue` và đẩy vào `Channel`. (2) Quên `sub.Create()` hoặc gọi trước `AddSubscription`. (3) `SourceTimestamp` có thể là `DateTime.MinValue` với một số server: khi đó dùng `ServerTimestamp`, rồi đến giờ gateway. (4) `AutoAcceptUntrustedCertificates = true` để ở production là lỗ hổng bảo mật; đặt nó theo môi trường.

### 2.4 Modbus slave trong simulator và hàm giải mã thanh ghi

**Modbus slave (simulator, vai máy dán keo).** NModbus có sẵn slave TCP với kho dữ liệu trong RAM:

```csharp
var listener = new TcpListener(IPAddress.Any, 502);       // cổng 502 trên Linux cần quyền; chạy Docker hoặc dùng 5020
listener.Start();
var factory = new ModbusFactory();
var network = factory.CreateSlaveNetwork(listener);
var slave = factory.CreateSlave(unitId: 1);
network.AddSlave(slave);
_ = network.ListenAsync(ct);

// Vòng chu kỳ máy cập nhật thanh ghi (địa chỉ 0-based)
void WriteRegisters(uint counter, ushort state, ushort alarm)
{
    var (hi, lo) = ((ushort)(counter >> 16), (ushort)(counter & 0xFFFF));
    slave.DataStore.HoldingRegisters.WritePoints(0, new[] { lo, hi, state, alarm });   // CDAB: word thấp trước (cố tình)
}
```

Lệnh console: `modbus stop|start`, `glue down 60`, `glue reset` (bộ đếm về 0).

**Hàm giải mã (gateway).** Hàm thuần, unit test kỹ, vì đây là chỗ hay sai nhất:

```csharp
public enum WordOrder { ABCD, CDAB, BADC, DCBA }

public static class RegisterDecoder
{
    static ushort Swap(ushort v) => (ushort)((v << 8) | (v >> 8));

    public static uint ToUInt32(ushort r0, ushort r1, WordOrder order) => order switch
    {
        WordOrder.ABCD => ((uint)r0 << 16) | r1,
        WordOrder.CDAB => ((uint)r1 << 16) | r0,
        WordOrder.BADC => ((uint)Swap(r0) << 16) | Swap(r1),
        WordOrder.DCBA => ((uint)Swap(r1) << 16) | Swap(r0),
        _ => throw new ArgumentOutOfRangeException(nameof(order))
    };

    public static int   ToInt32(ushort r0, ushort r1, WordOrder o) => unchecked((int)ToUInt32(r0, r1, o));
    public static float ToFloat(ushort r0, ushort r1, WordOrder o) => BitConverter.Int32BitsToSingle(ToInt32(r0, r1, o));
}
```

| r0 | r1 | Order | Kết quả |
| --- | --- | --- | --- |
| `0x0001` | `0x86A0` | ABCD | 100.000 |
| `0x86A0` | `0x0001` | CDAB | 100.000 |
| `0x86A0` | `0x0001` | ABCD | 2.258.632.705 (đọc sai) |
| `0x0100` | `0xA086` | BADC | 100.000 |
| `0xA086` | `0x0100` | DCBA | 100.000 |
| `0x42F6` | `0xE979` | ABCD (float) | ≈ 123,456 |

### 2.5 Modbus master trong gateway: polling

```csharp
public async Task RunAsync(CancellationToken ct)
{
    var backoff = Backoff.Exponential(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
    while (!ct.IsCancellationRequested)
    {
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(_device.Host, _device.Port, ct);
            using var master = new ModbusFactory().CreateMaster(tcp);
            master.Transport.ReadTimeout = 2000;                          // không để treo vô hạn
            await RaiseConnectionAsync(true); backoff.Reset();

            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_device.PollMs));
            while (await timer.WaitForNextTickAsync(ct))
            {
                var regs = await master.ReadHoldingRegistersAsync(_device.UnitId, _device.StartAddress, _device.Count); // 1 request cho cả khối
                var now = DateTime.UtcNow;
                foreach (var tag in _device.Tags)
                    await RaiseValueAsync(new RawValue(_device.Machine, tag.Name,
                        tag.Decode(regs), DataQuality.Good, now, "modbus"));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Modbus {Machine} lỗi", _device.Machine);
        }
        await RaiseConnectionAsync(false);
        await Task.Delay(backoff.Next(), ct);
    }
}
```

**Bẫy.** (1) Đọc từng register một: 4 request thay vì 1, chậm và tải PLC. Gom các tag liền nhau thành một khối đọc. (2) Không đặt timeout: socket treo, vòng poll đứng im, không ai biết. (3) Tag `state` gửi mỗi lần poll: dù giá trị không đổi vẫn sinh message `state`. Normalizer chỉ phát `state` khi **đổi** (mục 2.6). (4) Modbus không cho biết máy reset bộ đếm: `CounterMath` của M4 đã xử lý.

### 2.6 Chuẩn hóa sang hợp đồng M4 và `msgId` cố định

**Cấu hình tag** (`gateway.tags.json`, đọc bằng `IOptions`, kiểm tra hợp lệ lúc khởi động bằng `ValidateOnStart`):

```json
{
  "gateway": { "id": "GW01", "site": "HN1", "area": "SMT-ASSY", "line": "L1" },
  "mqtt":    { "host": "mosquitto", "port": 1883, "clientId": "minimes-gw-GW01" },
  "queue":   { "path": "data/outbox.db", "maxRows": 500000, "maxAgeDays": 7 },
  "devices": [
    { "machine": "L1-REFLOW-01", "protocol": "opcua", "endpoint": "opc.tcp://simulator:4840",
      "tags": [
        { "name": "peakTemp", "nodeId": "ns=2;s=Reflow.PeakTemp", "kind": "process", "samplingMs": 500, "deadband": 0.5 },
        { "name": "counter",  "nodeId": "ns=2;s=Reflow.Counter",  "kind": "count",   "samplingMs": 1000 },
        { "name": "state",    "nodeId": "ns=2;s=Reflow.State",    "kind": "state",
          "map": { "1": "Running", "2": "Idle", "3": "Down", "4": "Setup", "5": "PlannedStop" } } ] },
    { "machine": "L1-GLUE-01", "protocol": "modbus", "host": "simulator", "port": 5020, "unitId": 1,
      "pollMs": 1000, "startAddress": 0, "count": 4,
      "tags": [
        { "name": "counter", "offset": 0, "type": "uint32", "wordOrder": "CDAB", "kind": "count" },
        { "name": "state",   "offset": 2, "type": "uint16", "kind": "state",
          "map": { "1": "Running", "2": "Idle", "3": "Down", "4": "Setup", "5": "PlannedStop" } },
        { "name": "alarm",   "offset": 3, "type": "uint16", "kind": "alarm", "zeroMeansNone": true } ] }
  ]
}
```

**Normalizer** đổi `RawValue` thành `(topic, MachineMessage, retain)` theo `kind`:

| `kind` | Topic M4 | Trường | Khi nào phát |
| --- | --- | --- | --- |
| `count` | `.../count` | `counter` | Khi đổi; và ít nhất 30 giây một lần |
| `state` | `.../state` (retained) | `state` theo `map` | Chỉ khi đổi (so với giá trị đã phát gần nhất) |
| `alarm` | `.../alarm` | `alarmCode` (`0` → `null`) | Chỉ khi đổi |
| `process` | `.../process/{name}` | `tag`, `value` | Khi vượt deadband (OPC UA server đã lọc; Modbus thì normalizer tự lọc); và ít nhất 60 giây một lần |

Giá trị trong `map` không có (ví dụ PLC trả 9) → bỏ, log Warning một lần mỗi phút (tránh lụt log).

**`msgId` cố định.** Sinh từ nội dung thay vì `Guid.NewGuid()`:

```csharp
public static Guid MessageId(string machine, string tag, DateTime sourceTsUtc, string valueText)
{
    var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{machine}|{tag}|{sourceTsUtc:O}|{valueText}"));
    return new Guid(bytes.AsSpan(0, 16));
}
```

Vì sao: (1) OPC UA phát lại notification cũ sau reconnect → cùng nội dung → cùng `msgId` → MES bỏ (M4). (2) Gateway gửi message lên broker, broker đã nhận nhưng PUBACK chưa về thì gateway chết → khởi động lại gửi lại bản trong hàng đợi, vẫn cùng `msgId` vì payload lưu nguyên trong SQLite. Hai lớp cùng dẫn tới "gửi ít nhất một lần, MES xử lý đúng một lần".

**Heartbeat.** Gateway phát `heartbeat` cho **mỗi máy đang kết nối được**, mỗi 5 giây (không retain, QoS 0, **không** đi qua hàng đợi: heartbeat cũ gửi bù là vô nghĩa). Máy mất kết nối thì ngừng heartbeat của máy đó.

### 2.7 Store-and-forward bằng SQLite

**Thiết kế: luôn đi qua hàng đợi (outbox).** Mọi message (trừ heartbeat) được **ghi vào SQLite trước**, sau đó `ForwarderService` đọc theo thứ tự `Id`, publish QoS 1, chờ PUBACK, rồi xóa. Không có hai đường "có mạng thì gửi thẳng, mất mạng thì ghi đĩa": hai đường là hai chỗ có thể sai thứ tự.

```sql
CREATE TABLE IF NOT EXISTS Outbox (
  Id        INTEGER PRIMARY KEY AUTOINCREMENT,   -- thứ tự gửi
  Topic     TEXT    NOT NULL,
  Payload   TEXT    NOT NULL,                    -- JSON đã có msgId
  Retain    INTEGER NOT NULL,
  CreatedAt TEXT    NOT NULL
);
PRAGMA journal_mode = WAL;                        -- ghi và đọc đồng thời tốt hơn
PRAGMA synchronous = NORMAL;                      -- đủ an toàn với WAL, nhanh hơn FULL
```

```csharp
// ForwarderService (BackgroundService)
protected override async Task ExecuteAsync(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        if (!_mqtt.IsConnected) { await Task.Delay(1000, ct); continue; }       // MQTT client tự reconnect (như M4)
        var batch = await _outbox.PeekAsync(take: 200, ct);                     // ORDER BY Id
        if (batch.Count == 0) { await _outbox.WaitForNewAsync(TimeSpan.FromSeconds(1), ct); continue; }
        foreach (var m in batch)
        {
            var r = await _mqtt.PublishAsync(new MqttApplicationMessageBuilder()
                        .WithTopic(m.Topic).WithPayload(m.Payload)
                        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                        .WithRetainFlag(m.Retain).Build(), ct);                 // QoS 1: trả về sau PUBACK
            if (!r.IsSuccess) break;                                            // dừng, giữ thứ tự, thử lại vòng sau
            await _outbox.DeleteAsync(m.Id, ct);
        }
    }
}
```

**Trần hàng đợi.** Job mỗi phút: `SELECT COUNT(*)`; vượt `maxRows` hoặc có bản cũ hơn `maxAgeDays` → `DELETE ... WHERE Id IN (SELECT Id FROM Outbox ORDER BY Id LIMIT @n)`, tăng `DroppedCount`, log Error. Chỉ số `QueueLength`, `DroppedCount`, `OldestAgeSec` được publish trong status của gateway (mục 2.8) để dashboard hiển thị.

**Bẫy.** (1) Xóa trước khi có PUBACK: mất message khi broker chết giữa chừng. (2) Gửi song song nhiều message để nhanh: mất thứ tự. Một luồng gửi tuần tự là đủ nhanh (vài nghìn message/giây trên LAN). Nếu cần nhanh hơn, có thể giữ nhiều message "đang bay" nhưng chỉ xóa theo thứ tự. (3) Một transaction SQLite cho mỗi message lúc ghi vào: chậm. Gom ghi theo lô nhỏ (ví dụ 50 ms) trong một transaction. (4) Đặt file DB trong thư mục tạm bị dọn: mất hàng đợi. Dùng thư mục dữ liệu cố định, mount volume khi chạy Docker.

### 2.8 Health: trạng thái kết nối máy, LWT của gateway, reconnect

| Sự kiện | Gateway làm gì | MES thấy gì (M4, không sửa) |
| --- | --- | --- |
| Driver kết nối được máy | Publish `.../{machine}/status = online` (retained, qua hàng đợi), bắt đầu heartbeat máy | Máy rời `Offline` khi có heartbeat/state |
| Driver mất kết nối máy | Publish `.../{machine}/status = offline` (retained), ngừng heartbeat máy | Máy `Offline` ngay (nhánh `status` của M4) |
| Gateway khởi động | Kết nối MQTT với **LWT** `minimes/{site}/_sys/gateway/{id}/status = offline` (retained); publish `online` + chỉ số hàng đợi mỗi 30 giây | **[thêm nhỏ ở M4]** ingest nhận area `_sys`: cập nhật thẻ gateway trên dashboard, không tra bảng `Machine` |
| Gateway chết đột ngột | Broker phát LWT | Thẻ gateway đỏ; các máy của nó `Offline` sau 30 giây (watchdog) |

Payload status gateway: `{"state":"online","queueLength":0,"droppedCount":0,"oldestAgeSec":0,"devices":{"L1-REFLOW-01":"connected","L1-GLUE-01":"connected"},"ts":"..."}`.

**Reconnect có backoff.** Cả hai driver dùng một helper `Backoff.Exponential(1s → 30s)` (hoặc pipeline Polly `ResiliencePipeline` với `RetryStrategyOptions { BackoffType = Exponential, MaxDelay = 30s, UseJitter = true }`). Jitter để 20 gateway không cùng lúc dồn vào một server vừa khởi động lại.

**Thay đổi cần làm ở M4 (nhỏ).** Trong `MqttIngestService`, trước khi tra `MachineDirectory`: nếu `t.Area == "_sys"` và `t.Line == "gateway"` thì chuyển cho `GatewayStatusStore` và đẩy SignalR `GatewayStatusChanged`. Đây là phần duy nhất của MES phải sửa trong module này; dữ liệu máy đi qua đúng đường cũ.

### 2.9 Chạy gateway như dịch vụ

- `Host.CreateApplicationBuilder(args)` + `builder.Services.AddWindowsService()` (gói `Microsoft.Extensions.Hosting.WindowsServices`): chạy được bằng `dotnet run` khi dev, và cài thành Windows Service bằng `sc.exe create MiniMesGateway binPath= "...\MiniMes.EdgeGateway.exe"`. Ngoài đời gateway thường chạy trên một IPC (PC công nghiệp) Windows đặt trong tủ điện của xưởng.
- Docker: thêm service `simulator` và `gateway` vào `docker-compose.yml`, volume cho `data/` (SQLite) và `pki/` (chứng chỉ OPC UA). Như vậy `docker compose up` dựng cả chuỗi.
- Log Serilog ra file xoay vòng; ghi `Machine`, `Protocol`, `QueueLength` vào mọi dòng liên quan.

### Sơ đồ luồng dữ liệu của module

```
 MachineSimulator                                   MiniMes.EdgeGateway                                       Broker / MES
 ┌──────────────────────┐   OPC UA subscription   ┌─────────────────────────────────────────────────────┐
 │ ReflowNodeManager    │ ──── data change ─────▶ │ OpcUaDriver ─┐                                       │
 │ PeakTemp/Counter/State│                        │              │ RawValue                              │
 └──────────────────────┘                         │              ▼                                       │
 ┌──────────────────────┐   Modbus FC03 (poll)    │ ModbusDriver ─▶ Channel ─▶ Normalizer                 │
 │ GlueMachineSlave     │ ◀──── read 4 regs ───── │   RegisterDecoder         (kind → topic, msgId,       │
 │ HR0-1 counter CDAB   │ ────── values ────────▶ │                            chỉ phát khi đổi/deadband) │
 │ HR2 state, HR3 alarm │                         │                                │                     │
 └──────────────────────┘                         │                                ▼                     │
                                                  │                     SqliteOutbox (WAL, theo Id)      │
                                                  │                                │                     │      ┌───────────┐
                                                  │                     ForwarderService ── QoS 1 ───────┼────▶ │ Mosquitto │
                                                  │                       (xóa sau PUBACK)                │      └─────┬─────┘
                                                  │ HealthPublisher ── heartbeat (QoS 0, không qua outbox)┼────▶       │
                                                  │                 ── status máy / gateway (LWT) ───────┼────▶       ▼
                                                  └─────────────────────────────────────────────────────┘   MqttIngestService (M4)
                                                                                                             → MachineEvent, ProcessValue,
                                                                                                               MachineStateHistory, SignalR
```

---

## 3. Kế hoạch thực hiện

Tổng khoảng 24 giờ. Commit sau mỗi bước.

- [x] **Bước 0 — Học OPC UA và Modbus bằng công cụ có sẵn (2 giờ)**
  - Mục tiêu: hiểu khái niệm trước khi viết code.
  - Việc cần làm: chạy `docker run -p 50000:50000 mcr.microsoft.com/iotedge/opc-plc --autoaccept --unsecuretransport` (kiểm tra tham số trong README của image); cài **UaExpert** (Unified Automation, miễn phí, cần đăng ký) → kết nối, Browse, Read, kéo node vào Data Access View (subscription), thử Deadband; cài công cụ Modbus (ví dụ `mbpoll` dòng lệnh, hoặc Modbus Poll/Modbus Slave bản dùng thử) → đọc/ghi holding register, thử đọc 2 register thành int32 với các thứ tự byte.
  - Kiểm tra: tự viết vào `docs/opcua-modbus-notes.md` 10 dòng: NodeId là gì, SamplingInterval khác PublishingInterval thế nào, 40001 là địa chỉ mấy trên dây, CDAB nghĩa là gì.
  - Đã làm (28/10/2026): UaExpert/mbpoll được thay bằng hai lab C# chạy thẳng file (`docs/learning/labs/opcua-lab.cs` với container OPC PLC, `docs/learning/labs/modbus-lab.cs`); kết quả và ghi chú ở `docs/opcua-modbus-notes.md`, `docs/learning/M4b-step00-opcua-modbus-basics.md`.

- [x] **Bước 1 — OPC UA server trong simulator (4 giờ)**
  - Mục tiêu: lò reflow `L1-REFLOW-01` là OPC UA server có 3 node cập nhật theo chu kỳ.
  - Việc cần làm: thêm gói `OPCFoundation.NetStandard.Opc.Ua.Server`; `ApplicationConfiguration` bằng code (security `None` cho dev), chứng chỉ tự ký; `ReflowNodeManager` (2.2); nối vòng chu kỳ của `SimulatedMachine` (M4) để gọi `Publish`; nhiệt độ = 245 + nhiễu ±0,8 °C (seed cố định); lệnh `drift`, `sensor bad`, `opcua stop|start`.
  - File: `src/MiniMes.MachineSimulator/OpcUa/{ReflowServer.cs, ReflowNodeManager.cs}`, cấu hình trong `machines.json` (`"opcua": { "port": 4840 }` cho máy reflow).
  - Kiểm tra: UaExpert kết nối `opc.tcp://localhost:4840`, thấy `Reflow/PeakTemp` thay đổi; `drift L1-REFLOW-01 1` → giá trị tăng dần; `sensor bad` → UaExpert hiện status `Bad...`. **Nếu kẹt quá 2 giờ** ở phần server: tạm dùng container `opc-plc` với file node tùy chỉnh để làm tiếp Bước 2–7, quay lại sau.
  - Đã làm (28/10/2026): `OpcUa/ReflowNodeManager.cs`, `OpcUa/ReflowServer.cs`, `Machines/OvenTemperature.cs`; kiểm tra bằng `docs/learning/labs/opcua-lab.cs` (thay UaExpert): đúng `ns=2;s=Reflow.PeakTemp|Counter|State`, deadband 0,5 °C, drift 30 °C/phút 246,7 → 254,0 °C trong 15 giây, `sensor bad` → `BadSensorFailure`, `opcua stop` → client bị từ chối, `opcua start` → giá trị cuối có ngay. Khác snippet: (1) cấu hình `"opcua": { "host", "port", "peakTempC", "noiseC" }`; (2) `drift <m> off` để tắt drift và về set point; `sensor <m> bad|good`; `opcua stop|start` áp cho mọi server OPC UA; (3) `opcua stop` hủy server, `start` tạo server mới (StandardServer đã dừng không chạy lại được); (4) phải đặt `MaxRegistrationInterval = 0`, nếu không server tự đăng ký LDS ở chính cổng 4840 (xem `docs/incidents.md`); (5) lò **vẫn phát MQTT** cho tới Bước 4 (khi gateway thay vào), để dashboard không mất lò; (6) node chỉ được cập nhật khi giá trị đổi, timestamp = giờ PLC (tính cả `skew`); nhiễu nhiệt độ dùng Random riêng (seed + 100) để chuỗi Down/NG của lò giữ như M4.

- [x] **Bước 2 — Khung gateway + OPC UA client (3 giờ)**
  - Mục tiêu: gateway subscribe 3 node và log giá trị.
  - Việc cần làm: tạo project Worker Service `MiniMes.EdgeGateway` (tham chiếu `MiniMes.Contracts`); `GatewayOptions`, `DeviceOptions` + `ValidateOnStart` (endpoint rỗng, protocol lạ, tag trùng tên → không khởi động); `IDeviceDriver`, `OpcUaDriver` (2.3); `Channel<RawValue>`; log từng giá trị.
  - File: `src/MiniMes.EdgeGateway/**`, `gateway.tags.json`.
  - Kiểm tra: chạy simulator + gateway, log in `PeakTemp` chỉ khi đổi > 0,5 °C (deadband hoạt động); `opcua stop` → log mất kết nối, thử lại 1, 2, 4... giây; `opcua start` → tự kết nối, giá trị chảy lại.
  - Đã làm (28/10/2026): đạt cả 3 kiểm tra (mất kết nối phát hiện ngay bằng `BadSecureChannelClosed`, thử lại 1-2-4-8 s, nối lại sau khi server lên); thêm `sensor bad` → `quality = Bad`, NodeId sai → log Error `BadNodeIdUnknown` / namespace không có, cấu hình sai → không khởi động, exit 1. Unit test: `tests/MiniMes.UnitTests/Gateway/{GatewayOptionsValidatorTests, BackoffTests}.cs` (UnitTests tham chiếu thêm EdgeGateway, như spec Bước 3; CLAUDE.md đã cập nhật). Khác spec: (1) driver không có 2 event `ValueReceived`/`ConnectionChanged` mà ghi cả giá trị lẫn đổi kết nối vào **một** `Channel<DeviceEvent>`: giữ thứ tự "giá trị cuối → mất kết nối", thread của thư viện chỉ `TryWrite`; (2) NodeId cấu hình dạng `nsu=<URI>;s=...` (index tra lúc kết nối; `ns=2;s=...` vẫn dùng được); (3) `opcUa.autoAcceptUntrustedCertificates` mặc định chỉ bật ở Development (`launchSettings.json`); (4) `keepAliveMs = 2000` để phát hiện server biến mất trong vài giây; (5) API async của 1.5.378 (`DefaultSessionFactory.CreateAsync`, `new MonitoredItem(telemetry)`, `MinLifetimeInterval`); (6) chưa có `mqtt`/`queue` trong cấu hình: thêm ở Bước 4, 5.

- [x] **Bước 3 — Modbus slave + master + giải mã (3 giờ)**
  - Mục tiêu: máy dán keo đọc được qua Modbus, đúng số.
  - Việc cần làm: `GlueMachineSlave` (2.4) cổng 5020; `RegisterDecoder` + unit test bảng 2.4 (cả 4 thứ tự, int32 và float); `ModbusDriver` (2.5) đọc một khối 4 register mỗi 1 giây; lệnh `modbus stop|start`, `glue down`, `glue reset`.
  - File: `src/MiniMes.MachineSimulator/Modbus/GlueMachineSlave.cs`, `src/MiniMes.EdgeGateway/Drivers/Modbus/*`, `tests/MiniMes.UnitTests/Gateway/RegisterDecoderTests.cs`.
  - Kiểm tra: test xanh; **cố tình** đặt `wordOrder: ABCD` → log ra số tỷ; sửa thành `CDAB` → đúng. Ghi vào `docs/incidents.md` (đây là câu chuyện phỏng vấn tốt).
  - Đã làm (28/10/2026): `RegisterDecoderTests` (4 thứ tự, int32 có dấu, float 4 thứ tự, đọc lệch 1, khối 4 thanh ghi, hồi quy kiểu) + test validator Modbus, 743 test xanh. Chạy thật: CDAB đọc 100.000; ABCD đọc **2.258.632.705** và +65.536 mỗi bo (ghi `docs/incidents.md`); `down L1-GLUE-01 6` → state 3, alarm 999; `reset` → 0; `modbus stop` → DISCONNECTED ngay, thử lại 1-2-4 s; `start` → nối lại. Khác spec: (1) lệnh `glue down 60` / `glue reset` chính là `down L1-GLUE-01 60` / `reset L1-GLUE-01` đã có; (2) máy `L1-GLUE-01` thêm vào `machines.json` với `"mqtt": false` (máy cũ chỉ có Modbus; MES chỉ thấy nó qua gateway từ Bước 4) và `alarmCodes` có số (`G301` → thanh ghi 301, `E999` → 999); (3) slave nghe dual mode IPv6+IPv4 (`TcpListener.Create`), xem incidents; (4) driver: `Transport.Retries = 0` (lần poll sau là lần thử lại), timeout kết nối = `timeoutMs`; slave trả exception code → giá trị `Bad`, vẫn poll (G8), không trả lời → mất kết nối, backoff; (5) cấu hình tag Modbus: `offset`, `type` (uint16/int16/uint32/int32/float32), `wordOrder`, `zeroMeansNone`; thiết bị: `host`, `port`, `unitId`, `pollMs`, `timeoutMs`, `startAddress`, `count` (≤ 125).

- [x] **Bước 4 — Chuẩn hóa và publish lên MQTT (2,5 giờ)**
  - Mục tiêu: MES nhận dữ liệu 3 loại máy **không sửa ingest**.
  - Việc cần làm: `Normalizer` theo bảng 2.6 (state/alarm chỉ khi đổi, process theo deadband + 60 giây, count khi đổi + 30 giây); `MessageIdFactory` + unit test (cùng input → cùng Guid); tạm thời publish thẳng (chưa có outbox) bằng MQTTnet client có reconnect như M4; `HealthPublisher` heartbeat 5 giây cho máy đang kết nối.
  - File: `src/MiniMes.EdgeGateway/Pipeline/*`, `Health/HealthPublisher.cs`.
  - Kiểm tra: `mosquitto_sub -t "minimes/#" -v` thấy topic đúng UNS của `L1-REFLOW-01` và `L1-GLUE-01`, có `source`; dashboard M4 hiện 3 máy, ô lò có "Peak 245,x °C"; `SELECT TOP 10 * FROM ProcessValue ORDER BY Id DESC` có dữ liệu; `glue down 60` → ô máy dán keo đỏ, Andon tự raise sau X giây (M4); `glue reset` → MES ghi `IsReset` đúng.
  - Đã làm (28/10/2026): `Pipeline/{Normalizer, MessageIdFactory, PipelineService}.cs`, `Mqtt/GatewayMqttClient.cs`, `Health/HealthPublisher.cs`; **ingest MES không sửa dòng nào**. Kiểm tra thật (API + simulator + gateway + `mosquitto_sub`): topic `minimes/VN-BN01/SMT-ASSY/L1/{L1-REFLOW-01|L1-GLUE-01}/{count|state|heartbeat|process/peakTemp}`, `source` = `opcua`/`modbus`, ingest không bỏ message nào; `ProcessValues` có `peakTemp`; `down L1-GLUE-01 75` → history Down, Andon `system` tự raise sau ~63 s, alarm `999`; `reset` → `Counter 0, Delta 0, IsReset 1`; snapshot `GET /api/lines/L1/status` (nguồn của dashboard) có 4 máy với `dataSource` Mqtt/OpcUa/Modbus, lò `peakTemp 244,66 °C`. Unit test `NormalizerTests`, `HealthAndMessageIdTests` (777 test xanh). Quyết định: (1) **G8 trong giới hạn hợp đồng M4**: ingest từ chối `process` không có `value` và bỏ `count` Bad, nên `process` Bad đi **một lần** với giá trị tốt gần nhất + `quality = Bad`; `count/state/alarm` Bad không gửi; (2) mốc "đã gửi lúc nào" cho luật 30/60 s dùng **đồng hồ gateway**, `ts` trong message vẫn là giờ nguồn (G9); bản refresh định kỳ có `ts` = giờ gateway lúc xác nhận (msgId mới, không bị MES coi là trùng); (3) giá trị, đổi kết nối và tick 1 giây đi chung **một** channel, normalizer không cần khóa; (4) mất kết nối máy → normalizer quên trạng thái máy đó (không refresh "vẫn 8002", không heartbeat); nối lại → gửi lại tất cả (msgId theo nội dung, MES khử trùng); (5) alarm `0` lúc đầu không gửi (MES bắt đầu không có alarm); mã alarm Modbus gửi dạng số (`"999"`); (6) lò reflow chuyển `"mqtt": false` trong `machines.json`: MES chỉ nghe lò qua gateway; (7) float đổi sang double qua chuỗi ngắn nhất (`45.1f` → `45.1`).

- [x] **Bước 5 — Store-and-forward bằng SQLite (3,5 giờ)**
  - Mục tiêu: mất broker không mất message, giữ thứ tự.
  - Việc cần làm: gói `Microsoft.Data.Sqlite`; `SqliteOutbox` (tạo bảng, WAL, ghi theo lô nhỏ, `PeekAsync`, `DeleteAsync`, trần + `DroppedCount`); `ForwarderService` (2.7); chuyển Bước 4 sang đi qua outbox (trừ heartbeat).
  - File: `src/MiniMes.EdgeGateway/Queue/*`, `tests/MiniMes.UnitTests/Gateway/SqliteOutboxTests.cs` (SQLite file tạm).
  - Kiểm tra: `docker compose stop mosquitto` 60 giây → log `QueueLength` tăng; `start` → về 0 trong vài giây; trên MES: `SUM(Delta)` của `L1-GLUE-01` khớp bộ đếm simulator; không có `IsReset` giả; state history không có đoạn sai thứ tự. Kill gateway (Task Manager / `docker kill`) lúc đang có hàng đợi → chạy lại → hàng đợi được gửi tiếp, MES không có dòng trùng (nhờ `msgId`). Unit test: đặt `maxRows = 100`, ghi 150 → còn 100, `DroppedCount = 50`, giữ 100 bản mới nhất.
  - Đã làm (28/10/2026): `Queue/{SqliteOutbox, ForwarderService, OutboxMaintenanceService}.cs`, cấu hình `"queue": { "path": "data/outbox.db", "maxRows": 500000, "maxAgeDays": 7 }` (đường dẫn tương đối theo thư mục exe); heartbeat vẫn đi thẳng QoS 0. `SqliteOutboxTests` 8 test (gồm bài 150 → 100, `DroppedCount = 50`), 792 unit test + 48 integration test (Machines/Andon) xanh. Chạy thật: dừng Mosquitto → `QueueLength` 18 → 36 → 61, `dropped 0`; start → **66 message gửi bù trong 2,9 s**; `SUM(Delta)` khớp simulator (máy dán keo 26 = 100.033 − 100.007; lò 22 = 8.027 − 8.005), không `IsReset`, bộ đếm không giảm theo giờ máy. Kill gateway khi có 16 dòng chờ → dòng còn trên đĩa, chạy lại nối Id tiếp theo, broker lên gửi 47 message trong 2,1 s, **0 msgId trùng** ở MES. **Phát hiện và sửa ở M4 (user duyệt):** luật lệch giờ 60 s của ingest coi message gửi bù là "đồng hồ lệch" → dồn chi tiết vào lúc nối lại; nay message từ gateway có `ts` quá khứ ≤ 7 ngày giữ `ts` (ghi ở spec M4 mục 2.5 và `docs/incidents.md`); chạy lại: 9 count trong 70 s mất broker mỗi cái đúng giờ máy (cách 10 s), `Delta = 1`. Khác spec: (1) gom lô không dùng timer 50 ms mà gom mọi sự kiện đang chờ trong channel thành một transaction; (2) `DroppedCount` đếm từ lúc process chạy (lịch sử ở log Error); (3) log `QueueLength` mỗi 10 s khi > 0, trim mỗi phút; log "Outbox caught up: N messages in X s" sau mỗi lần mất broker; (4) state history trong lúc mất broker vẫn là Offline (watchdog M4), state gửi bù chỉ thành event, như câu chuyện 1.3 #3.

- [x] **Bước 6 — Health, status máy và gateway, LWT (2,5 giờ)**
  - Mục tiêu: phân biệt rõ "mất máy", "mất gateway", "máy Down".
  - Việc cần làm: `ConnectionChanged` → status máy `online/offline` (retained); LWT + status gateway có chỉ số hàng đợi mỗi 30 giây; sửa nhỏ M4: ingest nhánh `_sys/gateway` + `GatewayStatusStore` + event SignalR; dashboard M4 thêm thẻ gateway (online/offline, hàng đợi, số máy kết nối); jitter cho backoff.
  - File: `src/MiniMes.EdgeGateway/Health/*`, `src/MiniMes.Infrastructure/Machines/{MqttIngestService.cs (sửa), GatewayStatusStore.cs}`, `src/MiniMes.BackOffice.Wpf/Views/LineDashboardView.xaml (sửa)`.
  - Kiểm tra: `opcua stop` → chỉ ô lò Offline trong ≤ 5 giây (nhờ status, không phải chờ watchdog 30 giây), không có Andon; `opcua start` → xanh lại; kill gateway → thẻ gateway đỏ ngay (LWT), 2 máy của nó Offline sau ~30 giây, `L1-SMT-01` (MQTT trực tiếp) vẫn xanh.
  - Đã làm (28/10/2026): gateway `Health/GatewayStatusPublisher.cs` (status JSON retained mỗi 30 s + ngay khi kết nối broker/đổi thiết bị; "offline" khi dừng gọn), LWT trong `Mqtt/GatewayMqttClient.cs`, `status online/offline` của máy (retained, qua outbox để giữ thứ tự), jitter ±20 % cho `Backoff` (driver + client MQTT); hợp đồng `Contracts/Machines/GatewayContracts.cs` (`GatewayStatusMessage`, `GatewayTopic`); MES: `Application/Machines/GatewayStatusStore.cs` (**đặt ở Application**, không phải Infrastructure như spec, vì query snapshot line ở Application cần đọc nó, giống `MachineRuntimeStore`), nhánh `_sys/gateway` đầu `MqttIngestService.OnMessageAsync`, SignalR `GatewayStatusChanged` (tới mọi client: một gateway có thể đọc máy nhiều line), `LineStatusDto.Gateways` (gateway đọc ít nhất một máy của line); WPF: dải thẻ gateway trên `LineDashboardView` (`GatewayCardViewModel`: online/OFFLINE, "x/y machines connected", queue, dropped). Đo thật: `opcua stop` → lò Offline sau **4,1–4,6 s** (3 lần), Running lại sau 2,5–4,6 s, **0 Andon**, state history Running/Offline xen kẽ; kill gateway → thẻ `offline` **ngay (0 s)**, `L1-GLUE-01`/`L1-REFLOW-01` Offline sau ~28–30 s ("no message for 30 s"), `L1-SMT-01` không bị ảnh hưởng; chạy lại gateway → online sau 1 s, 2 máy Running sau 1–4,6 s. Test: `GatewayStatusStoreTests`, `GatewayCardTests`, `BackoffTests` (jitter), integration `Gateway_status_and_its_lwt_reach_the_gateway_store`; 812 unit + 54 integration (Machines/Andon/Line) xanh. Khác/thêm so với spec: (1) payload status gateway là JSON cả với LWT (`{"gateway":"GW01","state":"offline"}`); LWT không có `devices` nên MES giữ danh sách cũ và đánh dấu tất cả `disconnected`; (2) gateway coi keep-alive báo `ServerState.Shutdown/Failed` là mất máy ngay (server tắt có báo trước), `keepAliveMs` 1000; (3) simulator `ShutdownDelay = 0` (xem `docs/incidents.md`: với mặc định 5 s, gateway nối lại vào server đang tắt dần và lò bật tắt Offline/online); (4) thẻ gateway chưa chụp màn hình WPF, kiểm bằng snapshot REST và test ViewModel.

- [x] **Bước 7 — Docker Compose, integration test, tài liệu (3,5 giờ)**
  - Mục tiêu: dựng lại được bằng một lệnh, có test tự động, có tài liệu để kể khi phỏng vấn.
  - Việc cần làm: Dockerfile cho simulator và gateway, thêm vào `docker-compose.yml` (volume `gateway-data`, `gateway-pki`); integration test: Testcontainers Mosquitto (fixture của M4) + simulator OPC UA/Modbus chạy trong process test + gateway host trong process → kiểm tra (1) message đúng topic/schema, (2) dừng container Mosquitto 20 giây rồi chạy lại → đủ message, đúng thứ tự, (3) tắt Modbus slave → status offline; `ARCHITECTURE.md` thêm mục "Edge Gateway": vì sao có gateway, subscription vs polling, outbox luôn bật, `msgId` cố định, Offline ≠ Down; README thêm sơ đồ và hướng dẫn mở UaExpert.
  - File: `docker/simulator.Dockerfile`, `docker/gateway.Dockerfile`, `tests/MiniMes.IntegrationTests/Gateway/GatewayEndToEndTests.cs`, `ARCHITECTURE.md`, `README.md`.
  - Kiểm tra: clone sạch → `docker compose up` → dashboard có 3 máy chạy; CI chạy integration test xanh; DoD 5.1 đạt.
  - Đã làm (28/10/2026): `docker/{gateway,simulator}.Dockerfile` (multi-stage SDK → runtime 10.0), `.dockerignore`; `docker-compose.yml` thêm `simulator`, `gateway` trong **profile `plant`** (`docker compose --profile plant up -d --build`; để `up -d` quen thuộc không trùng cổng 4840/5020 và MQTT client id với simulator chạy bằng `dotnet run`), volume `gateway-data`, `gateway-pki`; cấu hình trong container ghi đè bằng biến môi trường (simulator và gateway thêm `AddEnvironmentVariables()` sau file JSON). Gateway tách `GatewayHost.CreateBuilder` (Program.cs và test dùng chung) + `AddWindowsService` (gói `Microsoft.Extensions.Hosting.WindowsServices` 10.0.12, spec 2.9). `tests/MiniMes.IntegrationTests/Gateway/{GatewayFixture, GatewayEndToEndTests}.cs`: Mosquitto Testcontainers + `ReflowServer`/`GlueMachineSlave` của simulator + host gateway thật trong process (tham chiếu project bằng **extern alias** `gateway`/`simulator` vì cả hai có `Program` riêng như API; CLAUDE.md đã cập nhật); 3 test: đúng topic/schema (đọc bằng `MachineMessageReader` của MES), dừng broker 20 s → đủ giá trị, đúng thứ tự (thử đột biến: forwarder xóa dù publish lỗi → test đỏ, nhận `[200000, 200040]`), tắt Modbus slave → `status = offline`, không `Down`. Toàn bộ integration test 281/281 xanh (1 phút 43 s), unit test 812 xanh. Chạy thật bằng Docker: build 2 image 28 s, gateway trong container nối `simulator:4840` và `simulator:5020`, simulator gọi được API trên host (`host.docker.internal:5157`), snapshot dashboard có 4 máy + GW01 `online 2/2`; client OPC UA trên host nối `opc.tcp://localhost:4840` được dù server quảng bá `simulator:4840`. `ARCHITECTURE.md` ADR-009; README mục "Edge Gateway (M4b)" (sơ đồ, Docker, UaExpert, lệnh `sc.exe`, số đo). **Chưa làm được:** clone sạch rồi `docker compose up` (đã chạy trên repo dev); CI chưa thấy chạy (chưa push); chưa cài thử Windows Service (cần quyền admin, lệnh ghi trong README); dashboard WPF chưa chụp màn hình (kiểm bằng snapshot REST + test ViewModel).

### Nếu bị kẹt

| Triệu chứng | Kiểm tra theo thứ tự |
| --- | --- |
| Client OPC UA báo `BadCertificateUntrusted` / `BadSecurityChecksFailed` | Dev: bật auto-accept ở **cả** server và client; xem thư mục `pki/rejected` có chứng chỉ không → chuyển sang `pki/trusted` |
| Kết nối được nhưng `BadNodeIdUnknown` | NodeId sai namespace index (`ns=2` hay `ns=3`?): Browse bằng UaExpert để copy chính xác |
| Subscribe không nhận thay đổi | Server quên `ClearChangeMasks`; deadband quá lớn; `sub.Create()` chưa gọi |
| Endpoint trả về URL không kết nối được | Server quảng bá hostname nội bộ Docker; dùng cùng hostname ở cả hai phía hoặc cấu hình `BaseAddresses` |
| Modbus đọc ra số khổng lồ/âm | Thứ tự word; địa chỉ lệch 1; đọc nhầm input register (FC04) thay vì holding (FC03) |
| Modbus timeout liên tục | Sai cổng (502 vs 5020), sai unit ID, firewall; thử bằng `mbpoll` trước khi nghi code |
| MES bỏ message của gateway | `machine` không trùng `MqttTopicKey`; tag process chưa có trong `MachineTag`; topic thiếu cấp `area` |
| Sau khi bật lại broker, MES có `IsReset` giả | Outbox gửi sai thứ tự (gửi song song?), hoặc có hai gateway cùng đọc một máy |
| Hàng đợi không giảm dù broker đã lên | `ForwarderService` chỉ kiểm tra `IsConnected` mà MQTT client chưa reconnect; xem log reconnect |
| Hai gateway đá nhau liên tục | Trùng `clientId` MQTT |

---

## 4. Vì sao chọn giải pháp này

| Quyết định | Đã chọn | Phương án khác | Vì sao không chọn | Khi nào nên đổi |
| --- | --- | --- | --- | --- |
| Có lớp gateway | Có, process riêng | MES làm OPC UA client/Modbus master trực tiếp | MES thêm driver cho từng giao thức; MES restart là mất kết nối mọi máy; không có bộ đệm khi mạng đứt; MES ở phòng server phải mở đường mạng vào mạng OT | Nhà máy rất nhỏ, 2–3 máy cùng một giao thức, cùng mạng: có thể đọc trực tiếp |
| Gateway tự viết hay sản phẩm có sẵn | Tự viết bằng .NET Worker (spec v2 B10 #7) | Node-RED; Kepware; UMH | Mục tiêu là thể hiện kỹ năng C# và hiểu bên trong; Node-RED nhanh nhưng không học được cơ chế | Dự án thật có ngân sách và nhiều loại PLC: Kepware/Ignition, MES chỉ cần đúng hợp đồng MQTT |
| Đọc OPC UA | Subscription + deadband ở server | Polling bằng `Read` mỗi giây | Polling tốn tải, bỏ lỡ thay đổi ngắn, không dùng được ưu điểm của OPC UA | Server cũ không hỗ trợ subscription tốt: polling theo lô |
| Bộ đệm | SQLite, outbox luôn bật (spec v2 B10 #8) | Hàng đợi trong RAM; file append-only; chỉ ghi đĩa khi mất mạng | RAM mất khi gateway restart; file append-only phải tự lo xóa/nén/khóa; hai đường gửi dễ sai thứ tự | Lưu lượng rất lớn (hàng chục nghìn msg/giây): hàng đợi chuyên dụng hoặc MQTT broker cục bộ ở edge (bridge) |
| `msgId` | Hash nội dung | `Guid.NewGuid()` | Phát lại sau reconnect OPC UA sẽ thành message "mới", đếm trùng | Không cần đổi |
| Mất kết nối máy | Publish `status = offline`, không suy ra state | Đặt `Down`; im lặng chờ watchdog | `Down` gọi nhầm bảo trì, sai OEE; im lặng thì MES biết chậm 30 giây | Không cần đổi |
| Cấu hình tag | File JSON trong gateway | Bảng trong DB MES, gateway gọi API lấy | Gateway phụ thuộc MES để khởi động | Nhiều gateway: quản lý tập trung nhưng gateway vẫn cache cấu hình cục bộ |
| Bảo mật OPC UA | `None` + auto-accept ở dev, tài liệu hóa cấu hình production | Bật SignAndEncrypt ngay từ đầu | Tăng thời gian học mà chưa thêm giá trị nghiệp vụ; nhưng phải **nói được** cách bật | Bất kỳ môi trường thật nào |

**Ba điều nên nói lại được khi phỏng vấn.**

1. *Vì sao có gateway*: tách MES khỏi giao thức từng hãng, có bộ đệm khi mạng đứt, MES chỉ hiểu một hợp đồng. Bằng chứng: thêm máy OPC UA và Modbus mà ingest của MES chỉ sửa đúng phần nhận trạng thái gateway.
2. *Không mất dữ liệu*: outbox SQLite luôn bật, chỉ xóa sau PUBACK, gửi tuần tự giữ thứ tự, `msgId` cố định để MES khử trùng, bộ đếm tích lũy nên kể cả khi hàng đợi tràn cũng không mất tổng sản lượng.
3. *Phân biệt lỗi*: máy Down (bảo trì), máy mất kết nối (IT/tự động hóa, `status = offline`), gateway chết (LWT). Ba tình huống ba cách hiển thị, ba người xử lý.

---

## 5. Hoàn thành module

### 5.1 Definition of Done

- [x] `docker compose up` dựng SQL Server, Mosquitto, simulator (MQTT + OPC UA + Modbus), gateway; API + WPF hiện dashboard 3 loại máy, mỗi ô có nhãn nguồn dữ liệu. *(Lệnh: `docker compose --profile plant up -d --build`; API chạy trên máy; dashboard kiểm qua snapshot REST, WPF có nhãn nguồn từ M4.)*
- [x] OPC UA: gateway subscribe (không polling) 3 node của lò; deadband 0,5 °C hoạt động; `PeakTemp` vào `ProcessValue` với `source = opcua`. *(`source` có trong message; bảng `ProcessValues` của M4 không có cột source.)*
- [x] Modbus: gateway poll 1 giây một khối 4 register; `RegisterDecoder` có unit test 4 thứ tự byte; cấu hình sai thứ tự cho ra số sai (đã thử và ghi vào `docs/incidents.md`).
- [x] MES nhận cả 3 loại máy qua đúng `MqttIngestService` của M4; phần MES sửa chỉ là nhánh trạng thái gateway (`_sys`). *(Thêm một sửa đã duyệt ở Bước 5: message từ gateway có `ts` quá khứ ≤ 7 ngày giữ `ts`.)*
- [x] **Mất broker 60 giây: không mất message, không trùng bộ đếm, không reset giả** (integration test + kiểm tra tay bằng `SUM(Delta)`).
- [x] Kill gateway khi đang có hàng đợi → chạy lại → gửi tiếp, MES không có dòng trùng.
- [x] Tắt OPC UA server → `L1-REFLOW-01` Offline trong ≤ 30 giây (thực tế ≤ 5 giây nhờ status), không có Andon; bật lại → tự kết nối.
- [x] Gateway chết → LWT → thẻ gateway đỏ; máy MQTT trực tiếp không bị ảnh hưởng.
- [x] Hàng đợi có trần, đếm `DroppedCount`; chỉ số hàng đợi hiện trên dashboard.
- [x] Gateway chạy được như Windows Service (đã thử `sc create` hoặc ghi rõ lệnh trong README) và như container. *(Lệnh `sc.exe` ghi trong README, chưa cài thử; container đã chạy.)*
- [x] `ARCHITECTURE.md` có mục Edge Gateway; README có hướng dẫn xem node bằng UaExpert.

### 5.2 Tự kiểm tra

**1. SamplingInterval và PublishingInterval khác nhau thế nào? Đặt Sampling 100 ms, Publishing 1000 ms nghĩa là gì?**
<details><summary>Gợi ý</summary>Sampling: server đọc giá trị nguồn bao lâu một lần. Publishing: server gửi gộp các thay đổi cho client bao lâu một lần. 100/1000: server lấy mẫu 10 lần/giây, xếp hàng (QueueSize) và gửi một gói mỗi giây; không mất thay đổi ngắn nếu QueueSize đủ.</details>

**2. Tài liệu máy ghi bộ đếm ở 40001–40002. Bạn cấu hình gateway thế nào và kiểm tra ra sao?**
<details><summary>Gợi ý</summary>Holding register (FC03), địa chỉ trên dây 0, count 2, kiểu uint32. Thứ tự word chưa biết: đọc thử khi biết giá trị thật trên màn hình máy (HMI), thử ABCD/CDAB tới khi khớp. Dùng `mbpoll`/Modbus Poll trước khi sửa code.</details>

**3. Vì sao outbox luôn bật, không chỉ khi mất mạng?**
<details><summary>Gợi ý</summary>Một đường duy nhất, thứ tự luôn theo `Id`; gateway restart cũng không mất message đang chờ; không có lúc "chuyển chế độ" dễ sinh lỗi. Đánh đổi: thêm một lần ghi đĩa mỗi message, không đáng kể với WAL và ghi theo lô.</details>

**4. Gateway gửi message, broker đã nhận nhưng gateway chết trước khi có PUBACK. Điều gì xảy ra và vì sao MES không đếm trùng?**
<details><summary>Gợi ý</summary>Message chưa bị xóa khỏi outbox; khi chạy lại gateway gửi lại đúng payload (cùng `msgId`). MES khử trùng theo `msgId` (cache + unique index ở M4). Đây là at-least-once + idempotent.</details>

**5. Vì sao gateway không đặt `state = Down` khi mất kết nối tới máy?**
<details><summary>Gợi ý</summary>Gateway không biết máy có chạy hay không. `Down` gọi bảo trì và trừ Availability; sai nguyên nhân thì gọi nhầm người và OEE sai. `status = offline` để IT xử lý; M5 xử lý khoảng Offline riêng.</details>

**6. Hàng đợi đầy sau 7 ngày mất mạng. Bỏ bản cũ nhất có làm mất sản lượng không?**
<details><summary>Gợi ý</summary>Không mất tổng: bộ đếm tích lũy, message `count` mới nhất vẫn mang tổng đúng; chỉ mất độ mịn (sản lượng theo giờ trong khoảng bị bỏ) và lịch sử state chi tiết. Phải log Error và cảnh báo vì đây là sự cố cần người biết.</details>

**7. Polling 1 giây trên Modbus có thể bỏ lỡ gì? Vì sao chấp nhận được với bộ đếm nhưng không với alarm ngắn?**
<details><summary>Gợi ý</summary>Thay đổi xảy ra và trở lại giữa hai lần hỏi. Bộ đếm tích lũy thì lần hỏi sau vẫn ra tổng đúng. Alarm chỉ bật 300 ms rồi tắt thì có thể không thấy; nếu quan trọng thì PLC phải chốt (latch) alarm cho đến khi được xác nhận, hoặc có bộ đếm số lần alarm.</details>

**8. OPC UA bảo mật bằng gì? Bạn bật trong production thế nào?**
<details><summary>Gợi ý</summary>Chứng chỉ X.509 hai chiều + SecurityPolicy (ví dụ Basic256Sha256) + MessageSecurityMode Sign/SignAndEncrypt; tùy chọn user/password. Production: tắt auto-accept, trao đổi chứng chỉ thủ công vào trusted list, theo dõi hạn chứng chỉ, chỉ mở endpoint có bảo mật.</details>

### 5.3 Câu hỏi phỏng vấn liên quan

1. **"Anh kết nối PLC với MES thế nào?"** — Vẽ chuỗi PLC → gateway → MQTT (UNS) → MES. Kể cụ thể: lò reflow OPC UA subscription có deadband, máy cũ Modbus polling 1 giây đọc một khối register, gateway .NET tự viết chuẩn hóa thành một hợp đồng, MES không biết giao thức máy. Thêm S7/MC Protocol = thêm một driver.
2. **"OPC UA khác Modbus thế nào?"** — Ngữ nghĩa (node có tên, kiểu, đơn vị) vs địa chỉ số; subscription vs polling; bảo mật chứng chỉ vs không có; 32-bit native vs ghép 2 register 16-bit; phức tạp vs đơn giản.
3. **"Mất mạng giữa xưởng và server thì dữ liệu đi đâu?"** — Outbox SQLite ở gateway, chỉ xóa sau PUBACK, gửi bù đúng thứ tự, `msgId` cố định để không trùng, trần hàng đợi và cảnh báo khi phải bỏ; MES hiển thị Offline trong lúc đó. Kể số liệu bạn đã đo (ví dụ 60 giây mất broker, N message gửi bù trong M giây).
4. **"Đọc 2 thanh ghi ra số sai, anh kiểm tra gì?"** — Địa chỉ lệch 1, FC03 hay FC04, unit ID, thứ tự word/byte (thử 4 kiểu với giá trị đã biết trên HMI), kiểu có dấu/không dấu, hệ số scale (PLC lưu 2453 nghĩa là 245,3 °C). Kể sự cố "2 tỷ sản phẩm" bạn đã gặp.
5. **"Polling hay subscription, khi nào dùng cái nào?"** — Bảng mục 1.2c; bộ đếm tích lũy làm polling an toàn; alarm ngắn cần latch.
6. **"Gateway của anh chết thì sao?"** — LWT báo ngay, máy Offline sau 30 giây, hàng đợi trên đĩa không mất; chạy như Windows Service tự khởi động lại; nếu cần cao hơn: hai gateway active/standby, nhưng phải tránh hai cái cùng đọc một máy.
7. **"Anh biết SECS/GEM không?"** — Chuẩn kết nối thiết bị bán dẫn (SEMI E5/E30/E37 HSMS qua TCP): thiết bị gửi sự kiện/alarm/dữ liệu, host (MES) gửi lệnh và recipe. Chưa code nhưng nguyên lý giống: driver riêng, chuẩn hóa về hợp đồng chung.

### 5.4 Tài liệu đọc thêm

- OPC Foundation: opcfoundation.org (mục "OPC UA Online Reference", phần Part 4 Services: Subscription, MonitoredItem; Part 2 Security), repo GitHub `OPCFoundation/UA-.NETStandard` (thư mục `Applications/ConsoleReferenceServer`, `ConsoleReferenceClient`, và `Docs/`).
- Unified Automation: UaExpert (công cụ client OPC UA miễn phí) và tài liệu hướng dẫn trên trang của họ.
- Microsoft: image `mcr.microsoft.com/iotedge/opc-plc` (repo GitHub `Azure-Samples/iot-edge-opc-plc`, README liệt kê tham số và node giả lập).
- Modbus Organization: modbus.org ("Modbus Application Protocol Specification", "Modbus Messaging on TCP/IP Implementation Guide").
- NModbus: repo GitHub `NModbus/NModbus` (README, mẫu master/slave TCP).
- learn.microsoft.com: "Worker services in .NET", "Create a Windows Service using BackgroundService", "Microsoft.Data.Sqlite overview", "Options pattern" (`ValidateOnStart`).
- SQLite: sqlite.org ("Write-Ahead Logging", "PRAGMA synchronous").
- Polly: repo GitHub `App-vNext/Polly` (resilience pipeline, retry với backoff và jitter).
- United Manufacturing Hub: tài liệu Unified Namespace (umh.docs.umh.app) để so sánh kiến trúc.
- Tham khảo thêm (chỉ đọc): `S7netplus` (Siemens S7), tài liệu SLMP/MC Protocol của Mitsubishi, tổng quan SECS/GEM của SEMI.
