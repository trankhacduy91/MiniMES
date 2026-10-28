# Module 4 — Machine Connectivity: Machine Simulator + MQTT + SignalR + Andon

> Module này nối "máy" vào MES: Machine Simulator giả lập máy (và trạm test tự động) gửi trạng thái, bộ đếm tích lũy, alarm, heartbeat qua MQTT (Mosquitto); API nhận bằng MQTTnet trong một `BackgroundService`, ghi theo batch, gom lịch sử trạng thái, đẩy realtime lên WPF bằng SignalR, và cho phép gọi người xử lý qua Andon.
> Sau khi xong bạn có: dashboard line cập nhật trạng thái máy trong < 2 giây, quy trình Andon raise → acknowledge → resolve, và dữ liệu đếm/trạng thái không bị hỏng khi tắt/bật Mosquitto. Đó chính là dữ liệu nền cho OEE ở Module 5.
> Thời lượng ước tính: khoảng 26 giờ (14–16 buổi, mỗi buổi 1,5–2 giờ; 22 giờ của bản cũ + 4 giờ cho phần **[v2]**). Nếu thiếu thời gian, cắt phần `--auto-operator` ở Bước 9.
> Tiên quyết: M0 (Docker Compose, Serilog, JWT, khung WPF), M1 (Machine, DowntimeReason, MediatR), M2–M3 (Work Order, serial, API scan) vì simulator vai trạm test gọi API scan của M3.
>
> **Cập nhật theo [spec-v2](spec-v2.md):** (1) topic theo **Unified Namespace ISA-95**, thêm cấp `area`: `minimes/{site}/{area}/{line}/{machine}/{kind}` (mục 2.1, 2.3); (2) payload thêm `source` (`mqtt`/`opcua`/`modbus`) và `quality` (`Good`/`Bad`/`Uncertain`); (3) **tag thông số quy trình** `process/{tag}` → bảng `MachineTag`, `ProcessValue` (mục 2.3b), nguồn dữ liệu cho SPC tự động ở M7; (4) **Andon leo thang theo thời gian** (mục 2.9b); (5) máy OPC UA/Modbus được đọc bởi **Edge Gateway ở [M4b](module-04b-edge-gateway.md)** và đi vào đúng `MqttIngestService` của module này, không cần sửa ingest. Các chỗ đổi được đánh dấu **[v2]**.

## 1. Kiến thức nghiệp vụ MES

### 1.1 Bức tranh tổng thể

**Module này nằm ở đâu trong nhà máy.** Nhà máy tự động hóa thường được mô tả theo các tầng (mô hình ISA-95, bạn sẽ nghe nhắc trong phỏng vấn):

| Tầng | Thiết bị / hệ thống | Nhịp phản hồi | Ví dụ ở xưởng điện tử |
| --- | --- | --- | --- |
| Level 0 | Cảm biến, cơ cấu chấp hành | vi giây – ms | Cảm biến phát hiện bo mạch vào máy, xy-lanh khí nén |
| Level 1 | PLC / bộ điều khiển | ms | PLC điều khiển băng tải, đầu hút linh kiện |
| Level 2 | SCADA / HMI | giây | Màn hình cảm ứng cạnh máy hiện trạng thái |
| Level 3 | **MES** | giây – phút | Hệ thống bạn đang xây |
| Level 4 | ERP (SAP) | giờ – ngày | Kế hoạch tuần, tồn kho, kế toán |

Module 4 là cầu nối giữa Level 1–2 (máy) và Level 3 (MES). MES **không điều khiển máy** (việc của PLC, chu kỳ vài ms); MES chỉ **đọc** dữ liệu để biết máy đang làm gì. Đó là lý do "realtime" trong MES nghĩa là vài giây, không phải vài mili giây.

**Ai dùng:**

- Operator: bấm nút Andon trên kiosk khi gặp sự cố; nhìn trạng thái máy của mình.
- Supervisor: nhìn dashboard line, nhận Andon, xác nhận, chọn lý do dừng khi xong.
- Maintenance: được gọi qua Andon loại "hỏng máy", sửa máy.
- IT MES / kỹ sư tự động hóa: theo dõi kết nối; khi máy `Offline` thì họ là người xử lý, không phải bảo trì.
- Quản lý xưởng: xem OEE (Module 5) — con số đó dựa trên dữ liệu của module này.

**Vì sao nhà máy cần.** Trước khi có kết nối máy, operator ghi giấy: "máy dừng 40 phút, không rõ lý do". Ba vấn đề: (1) chậm — cuối ca mới biết; (2) sai — người ta làm tròn, quên ghi; (3) thiếu chi tiết — 22 lần dừng ngắn 1–2 phút (Nhật gọi là チョコ停, micro-stop) không ai ghi, nhưng cộng lại mất gần 40 phút. Kết nối máy tự động giải quyết cả ba, và cho phép gọi người xử lý ngay lúc máy dừng thay vì đọc báo cáo hôm sau.

### 1.2 Khái niệm & thuật ngữ

**a) Máy nói chuyện với MES như thế nào (chỉ giới thiệu; dự án dùng MQTT mô phỏng).**

Máy công nghiệp không có REST API. Dữ liệu nằm trong **PLC** (Programmable Logic Controller): máy tính công nghiệp chạy chương trình ladder theo vòng quét vài ms, giữ trạng thái và bộ đếm trong các thanh ghi (register). Hãng hay gặp ở Nhật: Mitsubishi (MELSEC, người Nhật hay gọi PLC là シーケンサ), Omron, Keyence; ở châu Âu: Siemens. Mỗi hãng có giao thức riêng, nên có các cách lấy dữ liệu:

| Cách | Là gì | Ưu / nhược |
| --- | --- | --- |
| **Modbus** (RTU/TCP, cổng 502) | Giao thức cũ, đơn giản: đọc/ghi thanh ghi 16-bit và coil theo địa chỉ số | Rất phổ biến, rẻ; nhưng chỉ là "địa chỉ 40001 = ?" (không có ngữ nghĩa), phải poll, không có bảo mật, thanh ghi 16-bit nên bộ đếm quay vòng ở 65535 |
| **OPC UA** (cổng 4840 mặc định) | Chuẩn mở, có mô hình thông tin (node có tên, kiểu, đơn vị), bảo mật bằng chứng chỉ, hỗ trợ subscribe | Chuẩn hiện đại nhất cho MES ↔ PLC; nhưng phức tạp, tốn công cấu hình chứng chỉ |
| **Giao thức hãng** (MC protocol của Mitsubishi, FINS của Omron, S7 của Siemens, EtherNet/IP của Rockwell) | Đọc thẳng PLC hãng đó | Nhanh, sẵn; nhưng mỗi hãng một driver |
| **Gateway / edge** (Kepware, Node-RED, Ignition, hoặc phần mềm tự viết) | Máy nhỏ đứng giữa: nói giao thức của PLC ở phía dưới, phát ra MQTT/OPC UA/HTTP ở phía trên | Tách MES khỏi độ lộn xộn của từng hãng; thêm một điểm có thể hỏng |
| **Chuẩn theo ngành** | SMT có IPC-CFX (theo mình biết dựa trên AMQP) và IPC-Hermes cho chuyển bo; bán dẫn có SECS/GEM | Nếu máy hỗ trợ thì dùng, khỏi tự map thanh ghi |

Đường đi điển hình ngoài đời: `cảm biến → PLC → (OPC UA server trong PLC hoặc gateway) → MES`. MQTT xuất hiện nhiều ở tầng gateway → MES vì nhẹ, chịu mạng chập chờn, tách rời hai đầu; Eclipse Sparkplug là một quy ước phổ biến đặt lên trên MQTT cho công nghiệp. **Trong dự án:** không có PLC thật. Ở module này, Machine Simulator đóng vai máy "thông minh" tự phát MQTT theo hợp đồng dữ liệu ở mục 2.3. **[v2]** Ở M4b, simulator đóng thêm vai PLC thật (một OPC UA server cho lò reflow, một Modbus TCP slave cho máy dán keo) và một **Edge Gateway** viết bằng .NET đọc hai máy đó rồi phát MQTT theo **đúng hợp đồng này**. Toàn bộ phần phía MES (ingest, lưu, realtime, Andon) không đổi: đó là bằng chứng hợp đồng dữ liệu tách được MES khỏi giao thức của từng máy.

**b) Trạng thái máy (machine state).**

| State | Nghĩa | Ví dụ ở xưởng | Ai gửi |
| --- | --- | --- | --- |
| `Running` | Đang sản xuất, bộ đếm tăng | Máy SMT gắp-đặt linh kiện, chu kỳ 12 giây/bo | Máy |
| `Idle` | Máy sẵn sàng, không có việc | Trạm Test chờ bo từ trạm trước (starved), hoặc chưa có WO | Máy |
| `Down` | Dừng ngoài kế hoạch do lỗi, cần người can thiệp | Alarm E205 "lỗi chân không đầu hút" | Máy |
| `Setup` | Đổi model, nạp feeder, hiệu chuẩn | Đổi từ `PCB-A` sang `PCB-B`, 25 phút | Máy (hoặc operator bấm) |
| `PlannedStop` | Dừng có kế hoạch | Nghỉ trưa, bảo trì định kỳ | Máy/operator |
| `Offline` | MES không nghe thấy máy nữa | Rút dây mạng, gateway treo | **Không ai gửi**: MES tự suy ra (mục c) |

Máy chỉ gửi 5 state đầu (đúng topic `state` trong spec). `Offline` là state **do server suy ra**. Chuẩn công nghiệp có bộ state riêng (SEMI E10 cho bán dẫn, PackML cho đóng gói); dự án gọn lại còn 6 cho dễ học. Ví dụ dòng thời gian một máy trong ca:

```
06:00        09:42  09:49        11:30         12:30      14:10   14:21
 |--Running----|-Down-|--Running---|-PlannedStop-|-Running--|Offline|--Running--->
```

**c) Vì sao phân biệt `Down` với `Offline`.**

| Khía cạnh | `Down` | `Offline` |
| --- | --- | --- |
| MES biết gì | Máy tự khai: "tôi hỏng, mã lỗi E205" | Không biết gì. Máy có thể đang chạy tốt, hoặc hỏng, hoặc tắt |
| Nguyên nhân thường gặp | Lỗi cơ khí, vật tư, cài đặt | Mạng, gateway, PLC mất điện, phần mềm treo, hoặc chính MES nhận tin có vấn đề |
| Ai xử lý | Maintenance | IT MES / tự động hóa |
| Có Andon tự động không | Có (Down > X giây) | Không mặc định; hiện ô xám + cảnh báo kết nối |
| Với OEE | Mất Availability, có lý do để Pareto | Là "lỗ hổng dữ liệu", quyết định ở M5 (đề xuất: tính là dừng ngoài kế hoạch nhưng gắn cờ "chưa xác minh") |
| Dữ liệu trong lúc đó | Đầy đủ | Thiếu state; bộ đếm có thể vẫn tăng (máy vẫn chạy) |

Nếu gộp hai loại vào một: hoặc bảo trì bị gọi đi kiểm tra một cái máy vẫn chạy bình thường (chỉ mất mạng), hoặc IT bị bỏ qua khi máy thật sự hỏng. Và OEE của máy bị "đổ tội" oan vì lỗi mạng.

**d) Bộ đếm tích lũy (cumulative counter).** PLC giữ một số đếm tổng, chỉ tăng: giống công-tơ-mét xe máy, không phải bộ đếm vòng bánh. Ví dụ `counter: 15230` nghĩa là "từ lúc bộ đếm được reset đến giờ máy đã làm 15.230 bo". Số sản phẩm trong một khoảng = `counter cuối − counter đầu`. Ưu điểm so với gửi "+1" mỗi sản phẩm: mất vài message vẫn tính lại đúng, vì message sau mang tổng mới. Nhược điểm: bộ đếm có thể **reset về 0** (PLC khởi động lại, operator reset đầu ca) hoặc quay vòng (thanh ghi 16-bit). Ví dụ: 15.230 → 15.290 sau 60 giây là 60 bo; 15.290 → 3 là reset, không phải −15.287 bo.

**e) Alarm.** Sự kiện máy báo lỗi/cảnh báo kèm mã: `E205` (đầu hút lỗi chân không), `E310` (kẹt băng tải), `W001` (áp suất khí thấp; cảnh báo, máy vẫn chạy). Alarm khác state: máy có thể có alarm mà vẫn `Running` (cảnh báo), hoặc chuyển `Down` vì alarm. Alarm cho biết **nguyên nhân kỹ thuật**; lý do dừng (DowntimeReason) là **phân loại quản lý** do người chọn. Hai thứ này không giống nhau, đừng gộp.

**f) Heartbeat.** Máy gửi một message nhỏ định kỳ (ở đây 5 giây) chỉ để nói "tôi còn sống". Nhật gọi là 死活監視 (giám sát sống/chết). Không có heartbeat thì không phân biệt được "máy `Idle` hai tiếng không gửi gì" với "máy đã chết". Không nhận heartbeat quá **30 giây** (6 nhịp) → `Offline`.

**g) Andon.** Từ tiếng Nhật 行灯 (andon, đèn lồng giấy). Nguồn gốc: Hệ thống sản xuất Toyota (TPS), nguyên tắc **jidoka** (自働化, "tự động hóa có chút bàn tay người"): máy hoặc người phải dừng lại khi phát hiện bất thường, thay vì cho sản phẩm lỗi chảy tiếp. Ý tưởng bắt nguồn từ máy dệt của Sakichi Toyoda tự dừng khi đứt sợi. Trên dây chuyền Toyota, công nhân kéo dây (andon cord) hoặc bấm nút, bảng Andon phía trên sáng lên chỉ đúng trạm có vấn đề, tổ trưởng chạy đến; theo mô tả phổ biến, nếu không xử lý kịp trong một takt thì line dừng. Điểm cốt lõi không phải cái đèn, mà là **quy ước: ai thấy bất thường được quyền gọi, và có người chịu trách nhiệm đáp lại trong thời gian quy định**.

Đèn tháp (積層表示灯 / stack light, hay gọi theo hãng là パトライト) đặt trên máy là dạng Andon vật lý: thường đỏ = lỗi/dừng, vàng = cảnh báo/chờ, xanh = chạy (màu chính xác tùy quy ước từng nhà máy). Trong dự án, "đèn" là ô màu trên dashboard và danh sách Andon trên WPF.

Quy trình trong MES: `Raised → Acknowledged → Resolved`:

| Bước | Ai | Làm gì | Ghi lại | Đồng hồ |
| --- | --- | --- | --- | --- |
| **Raise** | Operator bấm nút trên kiosk (chọn loại: Machine/Material/Quality/Other), hoặc hệ thống tự raise khi máy `Down` quá X giây | Gọi người | `RaisedBy` (hoặc `system`), `RaisedAt`, loại | Bắt đầu tính thời gian phản ứng |
| **Acknowledge** | Supervisor hoặc Maintenance | Bấm "tôi nhận, đang đến" | `AckBy`, `AckAt` | `AckAt − RaisedAt` = thời gian phản ứng (mục tiêu ví dụ < 2 phút) |
| **Resolve** | Người xử lý | Sau khi xong, chọn **lý do dừng** (DowntimeReason của M1) + ghi chú | `ResolvedAt`, `DowntimeReasonId` | `ResolvedAt − AckAt` = thời gian sửa |

Lý do dừng là thứ M5 dùng làm Pareto: nếu Resolve không bắt buộc chọn lý do, dữ liệu downtime sẽ toàn "không rõ".

**h) Vì sao cần realtime.** Ví dụ số: máy SMT chu kỳ 12 giây làm 300 bo/giờ. Dừng 5 phút mà không ai biết = mất 25 bo; biết sau 8 giờ (báo cáo cuối ca) thì không còn gì để cứu. Nhưng cũng không cần "quá realtime": PLC lo vòng điều khiển ms, MES chỉ cần supervisor thấy trong vài giây.

| Loại dữ liệu | Độ trễ chấp nhận | Lý do |
| --- | --- | --- |
| Đổi state, Andon | < 2 giây (DoD của module) | Có người phải hành động |
| Số đếm hiển thị | ~1 giây | Chỉ để nhìn, không cần từng số |
| OEE | phút – giờ (M5) | Là KPI, không phải cảnh báo |

**i) Machine Simulator.** Chương trình console giả lập N máy. Dạy đúng **luồng** (ingest, lưu, realtime, Andon) nhưng không dạy được độ bẩn của dữ liệu thật: giờ máy lệch, bộ đếm nhảy lung tung, alarm bắn liên tục, gateway tự khởi động lại. Vì vậy simulator có tùy chọn cố tình gây lỗi (mục 2.8) để bạn tự tập xử lý.

### 1.3 Một ngày ở xưởng

Nhân vật: **Hà** (Planner), **Tuấn** (operator, Line 1), **chị Lan** (supervisor), **anh Minh** (bảo trì), **Dũng** (IT MES trực). Giờ ghi theo giờ địa phương; trong DB mọi thời điểm là UTC (UTC+7 nên 09:42 là 02:42Z).

1. **06:00.** Ca sáng bắt đầu. WO 200 bo `PCB-A` đã release từ hôm trước (M2). Máy `L1-SMT-01` (chu kỳ 12 giây) chuyển `Running`; simulator/gateway bắt đầu gửi `count` mỗi giây và `heartbeat` mỗi 5 giây. Dashboard của chị Lan hiện ô xanh, sản lượng giờ hiện tại nhảy 5 bo/phút.
2. **09:42.** Máy báo alarm `E205` rồi chuyển `Down`. Ô trên dashboard chuyển đỏ trong dưới 2 giây. Tuấn thấy đèn tháp đỏ.
3. **09:42:20.** Tuấn bấm nút Andon trên kiosk, chọn "Machine", ghi "đầu hút số 3 rơi linh kiện". (Nếu anh không bấm, đến 09:43:00 — máy Down đủ 60 giây, X = 60 — hệ thống tự raise. Vì đã có Andon mở cho máy này, Andon tự động không tạo thêm bản thứ hai.)
4. **09:44:20.** Chị Lan thấy ô đỏ nhấp nháy, bấm **Acknowledge**. Thời gian phản ứng = 2 phút. Chị gọi anh Minh.
5. **09:49.** Anh Minh thay đầu hút; máy trở lại `Running`. Andon vẫn mở: máy chạy lại không tự đóng Andon, vì chưa ai nói **vì sao** nó dừng.
6. **09:52.** Chị Lan bấm **Resolve**, chọn lý do "Nozzle / feeder fault" (Unplanned). Tổng: dừng 7 phút, mất khoảng 35 bo.
7. **11:30–12:30.** Nghỉ trưa: máy chuyển `PlannedStop`; không có Andon.
8. **14:10.** Ô của `L1-TEST-01` chuyển xám `Offline` (30 giây không có heartbeat). Không Andon vì không phải máy hỏng; Dũng nhận cảnh báo kết nối, kiểm tra và thấy switch bị rút nguồn.
9. **14:21.** Có lại mạng. Máy thực ra vẫn chạy suốt: bộ đếm nhảy từ 15.230 lên 15.290. MES tính delta = 60 bo, trạng thái quay về `Running`. Khoảng 14:10–14:21 được gắn cờ "state không xác minh" để M5 xử lý.
10. **Chiều.** Chị Lan thấy 11 phút Offline trong danh sách "cần phân loại" (làm ở M5) và gán lý do "Network failure".

### 1.4 Quy tắc nghiệp vụ & lý do tồn tại

| Quy tắc | Lý do |
| --- | --- |
| Máy gửi **bộ đếm tích lũy**, MES tính delta | Mất message không làm sai tổng; tính lại được từ hai mốc bất kỳ |
| Delta âm (`counter < counter trước`) = reset, delta = `counter` hiện tại | Tránh sản lượng âm khi PLC restart |
| Lần đầu thấy máy chỉ đặt mốc (delta = 0) | Không tính "15.230 bo" vào giờ hiện tại |
| Message có `msgId` đã xử lý bị bỏ qua (idempotent) | QoS 1 có thể giao trùng; đếm trùng làm sai OEE |
| Lưu cả `ts` giờ máy lẫn `ReceivedAt` giờ server | Đồng hồ PLC hay lệch; so sánh hai giờ để phát hiện |
| Không heartbeat > 30 giây → `Offline` (khác `Down`) | Máy im lặng ≠ máy hỏng (mục 1.2c) |
| Mỗi máy đúng **một** bản ghi `MachineStateHistory` đang mở, các đoạn không chồng nhau | Tính thời gian theo state cho OEE mới đúng |
| Chỉ nhận topic/máy có trong master data (`Machine.MqttTopicKey`); `machine` trong payload phải khớp topic | Chống cấu hình sai; master data là nguồn sự thật (M1) |
| Máy `Down` > X giây → tự raise Andon; mỗi máy tối đa một Andon mở cho mỗi loại | Không ai quên gọi; không spam trùng (Andon fatigue) |
| Resolve bắt buộc chọn `DowntimeReason` | Pareto ở M5 cần lý do |
| **[v2]** Andon `Raised` quá thời gian quy định mà chưa ai Acknowledge thì **leo thang** lên cấp kế tiếp (theo `AndonEscalationRule`), mỗi cấp chỉ một lần | Andon không có chủ là Andon vô dụng; leo thang buộc có người chịu trách nhiệm |
| **[v2]** Tag thông số quy trình chỉ được nhận khi có trong master `MachineTag`; giá trị `quality = Bad` được lưu nhưng **không** đưa vào SPC | Giá trị đọc lỗi (cảm biến đứt dây, mất kết nối) không được làm sai biểu đồ kiểm soát |
| Total Count lấy từ bộ đếm máy; Good/NG lấy từ kết quả scan theo serial; `ngDelta` của máy chỉ để đối chiếu | Quyết định chốt của dự án: một nguồn sự thật cho chất lượng là serial |

### 1.5 Sai lầm / sự cố thường gặp ngoài đời

- **Đếm bằng "+1 mỗi message"**: mất mạng 10 giây là lệch mãi, không ai phát hiện.
- **Coi `Offline` là `Down`** (bảo trì chạy nhầm) **hoặc coi là `Running`** (dashboard xanh dù máy đã chết, đây là lỗi nguy hiểm hơn).
- **Đồng hồ PLC lệch hoặc lưu giờ địa phương**: sự kiện "trong tương lai", đoạn state âm thời gian. Xử lý: dùng giờ server làm mốc phụ, cảnh báo khi chênh lớn.
- **Alarm flood**: một lỗi bắn cùng mã 10 lần/giây làm đầy log và Andon. Cần debounce ở gateway hoặc ở ingest.
- **Bộ đếm reset không ai báo** làm sản lượng ca âm; hoặc quay vòng 16-bit làm mất 65.536 bo trên báo cáo.
- **Andon quá nhiều** (mọi lần dừng 5 giây cũng raise): người ta quen, không ai nhìn nữa. Ngưỡng X phải đủ lớn để lọc dừng ngắn.
- **Andon không có chủ**: bấm xong không ai nhận. Cần đồng hồ leo thang (quá 3–5 phút chưa ack thì báo lên cấp trên). **[v2]** Dự án làm phần này ở mục 2.9b.
- **Danh sách lý do dừng quá dài**: operator luôn chọn "Other" (xem M5).
- **MES tự mất kết nối rồi báo *tất cả* máy Offline**: MES không nhận được gì thì không được kết luận về máy (mục 2.5).
- **Message `retained` cũ bị phát lại** làm tưởng máy vừa đổi state (mục 2.1).
- **Cập nhật UI theo từng message**: 200 máy × 10 msg/giây làm treo WPF (mục 2.7).

### 1.6 Bảng thuật ngữ

| English | Tiếng Việt | 日本語 | Giải thích ngắn |
| --- | --- | --- | --- |
| Machine state | Trạng thái máy | 設備状態 | Running/Idle/Down/Setup/PlannedStop/Offline |
| Running | Đang chạy | 稼働中 | Đang sản xuất |
| Idle | Chờ | 待機 | Sẵn sàng nhưng không có việc |
| Down | Dừng sự cố | 故障停止 / 異常停止 | Dừng ngoài kế hoạch |
| Setup / Changeover | Đổi model / cài đặt | 段取り | Chuẩn bị chuyển sản phẩm |
| Planned stop | Dừng có kế hoạch | 計画停止 | Nghỉ, bảo trì định kỳ |
| Offline | Mất kết nối | 通信断 / オフライン | MES không nghe thấy máy |
| Micro-stop | Dừng ngắn | チョコ停 | Dừng vài giây đến vài phút, hay không ai ghi |
| Cumulative counter | Bộ đếm tích lũy | 積算カウンタ | Tổng đếm chỉ tăng |
| Counter reset | Reset bộ đếm | カウンタリセット | Về 0 |
| Alarm | Cảnh báo/lỗi máy | アラーム | Sự kiện lỗi kèm mã |
| Heartbeat | Nhịp sống | ハートビート / 死活監視 | Tín hiệu "còn sống" định kỳ |
| Andon | Andon (đèn gọi) | アンドン(行灯) | Hệ thống gọi người khi có bất thường |
| Jidoka | Tự động hóa có người | 自働化 | Dừng khi có bất thường |
| Acknowledge | Xác nhận nhận việc | 確認 / 受付 | Bước 2 của Andon |
| Resolve | Xử lý xong | 復旧 / 対応完了 | Bước 3 của Andon, kèm lý do |
| Downtime reason | Lý do dừng | 停止理由 | Phân loại quản lý (M1) |
| Stack light | Đèn tháp | 積層表示灯 (パトライト) | Đèn đỏ/vàng/xanh trên máy |
| Cycle time | Chu kỳ | サイクルタイム | Thời gian làm một sản phẩm |
| Takt time | Nhịp sản xuất | タクトタイム | Nhịp theo nhu cầu khách |
| Poka-yoke | Chống sai | ポカヨケ | Thiết kế để khó làm sai |
| PLC | PLC | PLC / シーケンサ | Bộ điều khiển trong máy |
| Gateway | Cổng dữ liệu | ゲートウェイ | Chuyển giao thức PLC sang MQTT/OPC UA |
| Broker | Broker | ブローカー | Máy chủ trung chuyển MQTT |
| Topic | Chủ đề | トピック | "Địa chỉ" của message MQTT |
| Publish / Subscribe | Phát / đăng ký | 発行 / 購読 | Hai đầu của MQTT |
| Retained message | Message giữ lại | 保持メッセージ | Broker giữ bản cuối của topic |
| Last Will (LWT) | Di chúc của client | Last Will(遺言メッセージ) | Message broker phát hộ khi client chết bất thường |

## 2. Kiến thức lập trình

Thứ tự đọc: 2.1–2.2 (MQTT, MQTTnet) → 2.3 (hợp đồng dữ liệu, bộ đếm) → 2.4–2.5 (ingest, lưu, state) → 2.6–2.7 (SignalR, throttle) → 2.8 (simulator) → 2.9 (Andon). Sơ đồ tổng ở cuối mục.

### 2.1 MQTT từ gốc

**Nó là gì.** MQTT là giao thức nhắn tin kiểu publish/subscribe, rất nhẹ, chạy trên TCP (cổng 1883; TLS thường là 8883). Thiết kế cho thiết bị yếu và mạng chập chờn nên rất hợp máy công nghiệp. Có hai phiên bản đang dùng: 3.1.1 và 5.0. Khóa học dùng ngữ nghĩa 3.1.1 (MQTT 5 thêm user properties, session expiry, shared subscription `$share/...`; biết là đủ).

**Vấn đề nó giải quyết.** Cách ngây thơ: MES gọi HTTP đến từng máy mỗi giây. Bốn vấn đề: (1) MES phải biết địa chỉ từng máy, thêm máy là sửa MES; (2) máy thường nằm sau firewall, không mở cổng vào; (3) poll 1 giây vẫn bỏ sót sự kiện giữa hai lần hỏi, và tốn băng thông khi không có gì đổi; (4) thiết bị đơn giản không chạy nổi web server. MQTT đảo chiều: máy **chủ động gửi ra** một điểm trung gian; ai quan tâm thì đến đó lấy.

**Khái niệm cốt lõi.**

- **Broker** (Mosquitto): máy chủ trung gian. Publisher và subscriber chỉ nói chuyện với broker, không biết nhau ("tách rời" cả về địa chỉ lẫn thời gian).
- **Topic**: chuỗi phân cấp bằng `/`, phân biệt hoa thường, ví dụ `minimes/HN1/SMT-ASSY/L1/L1-SMT-01/state`. Không bắt đầu bằng `/`, không nhét dữ liệu vào topic (chỉ định danh).
- **[v2] Unified Namespace (UNS).** Thay vì mỗi hệ thống tự đặt topic, cả nhà máy dùng **một cây topic duy nhất theo mô hình thiết bị ISA-95**: `enterprise/site/area/line/cell/...`. Ai cần dữ liệu (MES, SCADA, dashboard, AI) chỉ việc subscribe đúng nhánh; thêm máy mới là thêm một nhánh, không ai phải sửa tích hợp điểm-điểm. Đây là kiến trúc mà United Manufacturing Hub, HiveMQ, Ignition đang quảng bá. Dự án dùng `minimes/{site}/{area}/{line}/{machine}/{kind}`; `minimes` đứng ở vị trí enterprise.
- **Wildcard** (chỉ dùng khi *subscribe*):

| Filter | Khớp | Không khớp |
| --- | --- | --- |
| `minimes/HN1/SMT-ASSY/L1/L1-SMT-01/state` | đúng topic đó | mọi topic khác |
| `minimes/HN1/SMT-ASSY/L1/+/state` | `state` của mọi máy line L1 (`+` thay đúng **một** cấp) | `.../L1-SMT-01/count` |
| `minimes/HN1/#` | mọi thứ dưới site HN1 (`#` thay **nhiều** cấp, chỉ được đứng cuối) | site khác |
| `minimes/+/+/+/+/+` | mọi topic đúng 6 cấp (ingest dùng filter này) | topic 5 hoặc 7 cấp |
| `minimes/+/+/+/+/process/+` **[v2]** | mọi tag thông số quy trình (7 cấp) | topic 6 cấp |

Subscribe `#` không khớp topic bắt đầu bằng `$` (như `$SYS/...`, thống kê của broker).

- **QoS** (mức đảm bảo giao message):

| QoS | Tên | Nghĩa | Chi phí | Dùng trong dự án |
| --- | --- | --- | --- | --- |
| 0 | At most once | Gửi rồi thôi, có thể mất | Rẻ nhất | `heartbeat` (mất một nhịp không sao, còn 5 nhịp trong 30 giây) |
| 1 | At least once | Chắc chắn tới, **có thể tới nhiều lần** | Vừa (PUBLISH + PUBACK) | `state`, `count`, `alarm`, kèm chống trùng theo `msgId` |
| 2 | Exactly once | Tới đúng một lần | Đắt (bốn gói bắt tay), chậm | Không dùng: chống trùng ở tầng ứng dụng rẻ hơn và cần dù sao (broker/DB restart) |

QoS thực tế = **min(QoS publisher, QoS subscription)**. Publisher gửi QoS 1 mà subscriber đăng ký QoS 0 thì subscriber chỉ nhận QoS 0.

- **Retained message**: khi publish với cờ retain, broker giữ **bản cuối** của topic đó và gửi ngay cho subscriber mới. Dùng cho thứ có "giá trị hiện tại" (state, status online/offline). Không dùng cho sự kiện (alarm, count) vì subscriber mới sẽ nhận lại sự kiện cũ như thể vừa xảy ra.
- **Last Will and Testament (LWT)**: khi client kết nối, đăng ký sẵn một message. Nếu client biến mất **không qua DISCONNECT sạch** (rớt mạng, crash, kill process, hoặc broker không nhận keep-alive trong khoảng ~1,5 lần chu kỳ keep-alive), broker phát message đó hộ. Máy sẽ đặt LWT là `offline` (retained) trên topic `status`.
- **Keep-alive**: client gửi PINGREQ định kỳ; đó là cách broker biết client còn sống.
- **Session**: mỗi client có `ClientId` duy nhất. `CleanSession = true`: broker quên subscription và message chờ khi client ngắt. `false` (persistent session): broker giữ subscription và các message QoS 1 đang chờ trong lúc client vắng, giao lại khi client vào lại, với điều kiện `ClientId` cố định.

**Snippet: Mosquitto trong Docker Compose và thử bằng tay.**

```yaml
# docker-compose.yml (bổ sung vào file của M0)
  mosquitto:
    image: eclipse-mosquitto:2
    ports: ["1883:1883"]
    volumes:
      - ./docker/mosquitto/mosquitto.conf:/mosquitto/config/mosquitto.conf
      - mosquitto-data:/mosquitto/data      # persistence: giữ retained + QoS 1 chờ khi broker restart
```

```
# docker/mosquitto/mosquitto.conf  (chỉ cho dev; production: bật password/TLS, bỏ allow_anonymous)
listener 1883
allow_anonymous true
persistence true
persistence_location /mosquitto/data/
```

```bash
# Terminal 1: nghe mọi thứ (-v in kèm topic)
mosquitto_sub -h localhost -t "minimes/#" -v
# Terminal 2: giả làm máy, QoS 1, retained
mosquitto_pub -h localhost -q 1 -r -t "minimes/HN1/SMT-ASSY/L1/L1-SMT-01/state" \
  -m '{"msgId":"b3f1c2d4-0000-4000-8000-000000000001","machine":"L1-SMT-01","ts":"2026-10-01T01:23:45.123Z","state":"Running"}'
# Mở terminal 3 sau đó và subscribe: sẽ nhận ngay bản retained.
# Xóa retained: publish payload rỗng với -r
mosquitto_pub -h localhost -r -n -t "minimes/HN1/SMT-ASSY/L1/L1-SMT-01/state"
```

**Bẫy thường gặp.**

- Hai client cùng `ClientId` đá nhau: broker ngắt cái cũ, cái cũ tự reconnect, vòng lặp vô tận. Ingest và mỗi máy simulator phải có ClientId riêng và ổn định.
- Mosquitto 2.x mặc định chỉ nghe localhost, không có config thì từ ngoài container kết nối bị từ chối (log: "Starting in local only mode"). Vì vậy cần file `mosquitto.conf` ở trên.
- Từ container khác trong Compose, host là tên service `mosquitto`, không phải `localhost`.
- Retained trên `heartbeat`: broker giữ nhịp cuối, ingest khởi động lại thấy "máy vừa gửi", máy chết vẫn trông sống. **Không retain heartbeat.** Retained `state` thì ổn, nhưng payload phải có `ts` và `msgId` để ingest phân biệt bản cũ.
- Nhầm QoS 1 là "đúng một lần": nó là "ít nhất một lần"; phải tự chống trùng.
- Ghi dữ liệu vào topic (`.../count/15230`): không lọc/không truy vấn được, nhồi retained rác.

### 2.2 MQTTnet: client .NET

**Nó là gì.** Thư viện MQTT phổ biến nhất cho .NET (repo `dotnet/MQTTnet` trên GitHub), có client và server. Mình dùng client cho cả ingest (phía API) và simulator.

**Vấn đề nó giải quyết.** Tự viết MQTT trên `TcpClient` nghĩa là tự làm CONNECT/keep-alive/PUBACK/parse gói tin; sai một chỗ là broker ngắt. MQTTnet gói toàn bộ, bạn chỉ làm việc với: options → connect → subscribe → nhận event → publish.

**Khái niệm cốt lõi.** `MqttFactory` tạo `IMqttClient`; `MqttClientOptionsBuilder` dựng cấu hình; event async `ApplicationMessageReceivedAsync`, `ConnectedAsync`, `DisconnectedAsync`. **MQTTnet không tự reconnect ở lõi**: bạn tự viết vòng lặp giữ kết nối (hoặc dùng gói managed client tách riêng). Cách tự viết đơn giản, dễ test, dễ log, nên khóa học chọn cách đó.

> **Phiên bản.** Snippet viết theo **MQTTnet 4.3.x** (`MqttFactory`). MQTTnet 5 đổi một số API (theo mình biết: `MqttFactory` thành `MqttClientFactory`, cách đọc payload và một số builder cũng đổi). Nếu tên method khác, đối chiếu README/wiki của `dotnet/MQTTnet` trên GitHub, đừng sửa mò.

**Snippet: vòng giữ kết nối + subscribe lại mỗi lần connect (MQTTnet 4.x).**

```csharp
public sealed partial class MqttIngestService : BackgroundService
{
    private readonly IMqttClient _client = new MqttFactory().CreateMqttClient();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(_cfg.Host, _cfg.Port)
            .WithClientId("minimes-ingest")                 // cố định, không trùng ai
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(15))
            .WithCleanSession(false)                        // broker giữ QoS 1 chờ khi ta rớt ngắn
            .Build();

        _client.ApplicationMessageReceivedAsync += e => OnMessageAsync(e, ct);
        _client.ConnectedAsync += async _ =>                // subscribe MỖI LẦN connect, đừng giả định broker nhớ
        {
            var opts = new MqttClientSubscribeOptionsBuilder()                      // [v2] 2 filter: 6 cấp + tag process
                .WithTopicFilter(f => f.WithTopic("minimes/+/+/+/+/+").WithAtLeastOnceQoS())
                .WithTopicFilter(f => f.WithTopic("minimes/+/+/+/+/process/+").WithAtLeastOnceQoS())
                .Build();
            await _client.SubscribeAsync(opts, ct);
            _connectedSince = DateTime.UtcNow;              // watchdog dùng để có "thời gian ân hạn" (mục 2.5)
        };

        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            if (!_client.IsConnected)
            {
                try { await _client.ConnectAsync(options, ct); backoff = TimeSpan.FromSeconds(1); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "MQTT connect lỗi, thử lại sau {Backoff}", backoff);
                    backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));   // 1,2,4,...,30 giây
                }
            }
            await Task.Delay(_client.IsConnected ? TimeSpan.FromSeconds(2) : backoff, ct);
        }
    }
}
```

**Bẫy thường gặp.**

- Handler `ApplicationMessageReceivedAsync` ném exception: MQTTnet có thể nuốt/log rồi tiếp tục, nên lỗi biến mất im lặng. Luôn `try/catch` và log trong handler.
- Handler chạy lâu (ghi DB đồng bộ) chặn vòng nhận, broker dồn message, keep-alive có thể trễ. Handler chỉ nên parse + đẩy vào hàng đợi (mục 2.4).
- Mặc định khi handler trả về là client gửi PUBACK. Nghĩa là message đã "được nhận" dù chưa ghi DB. Với dự án chấp nhận được vì bộ đếm là tích lũy (mất một message chỉ mất độ mịn, không mất tổng). MQTTnet có cơ chế xác nhận thủ công cho ai cần chắc hơn (pseudo: tắt auto-acknowledge rồi tự ack sau khi commit; kiểm tra tên API trong tài liệu phiên bản bạn dùng).
- `ConnectAsync` chỉ ném lỗi khi không nối được; mất kết nối *sau đó* chỉ báo qua `DisconnectedAsync`. Vòng lặp ở trên bắt được bằng `IsConnected`.
- Đọc payload: v4 có `e.ApplicationMessage.ConvertPayloadToString()`; v5 kiểm tra README (kiểu của `Payload` đã đổi).

### 2.3 Hợp đồng dữ liệu và bộ đếm tích lũy

**Nó là gì.** "Hợp đồng" là thỏa thuận cứng giữa bên gửi (máy/simulator/gateway) và bên nhận (ingest): topic, JSON schema, ngữ nghĩa từng trường, QoS/retain. Đặt DTO trong `MiniMes.Contracts` để **simulator và API dùng chung một class**: đổi hợp đồng là hỏng compile ở cả hai đầu thay vì hỏng lúc chạy.

**Vấn đề nó giải quyết.** Không có hợp đồng thì mỗi máy gửi một kiểu ("state":"RUN" hay "Running"?), ingest thành đống `if`. Và nếu chỉ gửi "+1" thì không có cách sửa khi mất message.

**Khái niệm cốt lõi.** Một schema JSON chung, trường không liên quan để `null` (đúng spec). Ý nghĩa theo topic:

| Topic (`minimes/{site}/{area}/{line}/{machine}/...`) | QoS | Retain | Trường dùng | Tần suất |
| --- | --- | --- | --- | --- |
| `state` | 1 | có | `state` | khi đổi, và ngay sau khi (re)connect |
| `count` | 1 | không | `counter`, `goodDelta`, `ngDelta` | khi có sản phẩm, tối đa 1 msg/giây; ít nhất 30 giây một lần dù không đổi |
| `alarm` | 1 | không | `alarmCode` (`null` = alarm đã xóa) | khi xảy ra/xóa |
| `heartbeat` | 0 | không | `state` | 5 giây (lấy state để tự phục hồi sau `Offline`) |
| `status` (mở rộng, mục 2.5) | 1 | có | chuỗi thô `online` / `offline` | connect / LWT. **[v2]** Gateway M4b cũng publish `offline` cho máy khi mất kết nối OPC UA/Modbus tới máy đó |
| `process/{tag}` **[v2]** | 1 | không | `tag`, `value`, `quality` | Khi giá trị đổi vượt deadband, tối đa 1 msg/giây/tag; ít nhất 60 giây một lần |

`ts` luôn là UTC, kết thúc bằng `Z`. `machine` phải khớp phần `{machine}` trong topic. `goodDelta`/`ngDelta` là số máy tự phân loại từ lần gửi trước, **chỉ để đối chiếu**, không dùng cho OEE.

**[v2] Hai trường mới, mọi topic:**

- `source`: `mqtt` (máy tự phát), `opcua`, `modbus` (gateway đọc hộ). Chỉ để chẩn đoán: khi số liệu lạ, biết phải kiểm tra máy hay cấu hình gateway. Ingest không xử lý khác nhau theo `source`.
- `quality`: `Good` / `Bad` / `Uncertain`, lấy theo status code của OPC UA (Modbus: `Good` khi đọc được, `Bad` khi lỗi đọc). Máy MQTT trực tiếp luôn `Good`. Message `Bad` vẫn được lưu nhưng không dùng tính delta bộ đếm và không đưa vào SPC.

Ví dụ message tag process do gateway phát (topic `minimes/HN1/SMT-ASSY/L1/L1-REFLOW-01/process/peakTemp`):

```json
{ "msgId": "5f0c…", "machine": "L1-REFLOW-01", "ts": "2026-10-11T02:15:30.250Z",
  "tag": "peakTemp", "value": 245.3, "quality": "Good", "source": "opcua" }
```

**Snippet: DTO và tiện ích topic (dùng chung trong `MiniMes.Contracts`).**

```csharp
namespace MiniMes.Contracts.Machines;

public enum MachineState { Running, Idle, Down, Setup, PlannedStop, Offline }   // Offline: chỉ server suy ra

public enum DataQuality { Good, Bad, Uncertain }                              // [v2]

public sealed record MachineMessage(
    Guid MsgId, string Machine, DateTimeOffset Ts,
    MachineState? State, long? Counter, int? GoodDelta, int? NgDelta, string? AlarmCode,
    string? Tag = null, double? Value = null,                                  // [v2] chỉ dùng cho process/{tag}
    DataQuality Quality = DataQuality.Good, string Source = "mqtt");           // [v2]

public readonly record struct ParsedTopic(string Site, string Area, string Line, string Machine, string Kind, string? Tag);

public static class MachineTopic
{
    public static string Build(string site, string area, string line, string machine, string kind)
        => $"minimes/{site}/{area}/{line}/{machine}/{kind}";
    public static string BuildProcess(string site, string area, string line, string machine, string tag)   // [v2]
        => $"minimes/{site}/{area}/{line}/{machine}/process/{tag}";

    public static bool TryParse(string topic, out ParsedTopic parsed)
    {
        var p = topic.Split('/');
        parsed = default;
        if (p[0] != "minimes") return false;
        if (p.Length == 6) { parsed = new ParsedTopic(p[1], p[2], p[3], p[4], p[5], null); return true; }
        if (p.Length == 7 && p[5] == "process")                   // [v2] tag thông số quy trình
        { parsed = new ParsedTopic(p[1], p[2], p[3], p[4], "process", p[6]); return true; }
        return false;                                              // sai cấu trúc: ingest bỏ + log
    }
}

public static class MachineJson
{   // camelCase, enum dạng chuỗi ("Running"), đọc không phân biệt hoa thường
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
        { Converters = { new JsonStringEnumConverter() } };
}
```

**Snippet: tính delta từ bộ đếm tích lũy (hàm thuần, dễ test).**

```csharp
public readonly record struct CounterDelta(long Delta, bool IsReset, bool IsBaseline);

public static class CounterMath
{
    public static CounterDelta Compute(long? last, long current)
    {
        if (last is null) return new(0, false, true);          // lần đầu thấy máy: chỉ đặt mốc, không tính lịch sử vào giờ này
        if (current >= last) return new(current - last.Value, false, false);
        return new(current, true, false);                      // counter < last: máy reset về 0, đã đếm `current` từ lúc reset
    }
}
```

Bảng test (xUnit `[Theory]`) tối thiểu:

| last | current | Kết quả |
| --- | --- | --- |
| null | 15230 | delta 0, baseline |
| 100 | 105 | delta 5 |
| 100 | 100 | delta 0 (không đổi) |
| 15230 | 15290 | delta 60 (sau đoạn Offline vẫn đúng) |
| 15290 | 3 | delta 3, reset |
| 65535 | 2 | delta 2, reset (PLC 16-bit quay vòng bị xem là reset; nếu cần chính xác, cấu hình `rolloverAt` theo máy) |

**Bẫy thường gặp.**

- Reset mà đúng lúc đó có message đến trễ/sai thứ tự (counter thấp hơn một chút) sẽ bị nhận nhầm thành reset và cộng thừa. Giảm rủi ro: loại trùng `msgId` trước, xử lý theo thứ tự từng máy (mục 2.4), bỏ message có `ts` cũ hơn message đã xử lý.
- Trong lúc reset, số sản phẩm giữa "message cuối trước reset" và "lúc reset" mất; chấp nhận và ghi vào tài liệu.
- Sau restart ingest, mốc `last` phải nạp lại từ DB (bản ghi `count` cuối) thay vì để `null`, nếu không delta bị đánh mất một lần.
- Delta bất thường (ví dụ 5.000 bo trong 10 giây với máy chu kỳ 12 giây) nên cảnh báo thay vì tin: đó là bước "kiểm tra chéo" của M5.

### 2.3b [v2] Tag thông số quy trình: `MachineTag`, `ProcessValue`

**Nó là gì.** Ngoài trạng thái và bộ đếm, máy còn có **thông số quy trình** (process parameter): nhiệt độ các vùng lò reflow, tốc độ băng tải, áp suất khí, lực ép. Những giá trị này quyết định chất lượng sản phẩm: lò reflow chạy thấp nhiệt hơn profile thì mối hàn không chảy hết (hàn nguội), cao quá thì linh kiện hỏng.

**Vấn đề nó giải quyết.** Không có dữ liệu này thì SPC (M7) phải chờ QC đo tay vài lần mỗi ca và phát hiện trôi nhiệt rất muộn. Có tag process đọc tự động thì M7 vẽ được biểu đồ kiểm soát gần như liên tục và báo động ngay khi nhiệt bắt đầu trôi. Đây là chỗ nối IoT với Quality.

**Khái niệm cốt lõi.**

| Entity | Cột quan trọng | Ghi chú |
| --- | --- | --- |
| `MachineTag` (master) | Id, MachineId, TagName (`peakTemp`), Unit (`°C`), DataType, LowerPlausible, UpperPlausible, SpcCharacteristicId (nullable, FK tới đặc tính kiểm tra của M7), SpcSampleEverySec | Chỉ tag có trong bảng này mới được nhận. `Plausible` là khoảng **vật lý hợp lý** (ví dụ 0–400 °C), khác giới hạn kỹ thuật USL/LSL của M7 |
| `ProcessValue` (insert-only) | Id (bigint), MachineTagId, Value (`float`), Quality, SourceTs, ReceivedAt, MsgId (unique) | Bảng lớn nhất hệ thống nếu đọc dày; giữ 30 ngày (job xóa theo lô 5.000 dòng), hoặc thử partition theo ngày |

- Gateway (M4b) chỉ gửi khi giá trị đổi vượt **deadband** (ví dụ 0,5 °C) và tối thiểu 60 giây một lần dù không đổi. Không gửi theo chu kỳ cố định 100 ms: 99% message sẽ trùng giá trị.
- Ingest: đường nóng lưu giá trị mới nhất vào `MachineRuntimeStore` (dashboard hiển thị), đẩy SignalR có throttle 1 giây; đường lạnh ghi `ProcessValue` theo batch như `MachineEvent`.
- Giá trị ngoài khoảng `Plausible` → lưu với `Quality = Bad` và log cảnh báo (cảm biến đứt dây thường trả về giá trị cực trị như -32768 hoặc 0).
- Nối SPC: tag có `SpcCharacteristicId` thì M7 lấy mẫu mỗi `SpcSampleEverySec` giây (giá trị `Good` gần nhất) thành một `Measurement`. M4 chỉ lưu; quyết định lấy mẫu nằm ở M7.

**Bẫy.** (1) Ghi mọi giá trị vào bảng có index rộng: chậm dần. Chỉ index `(MachineTagId, SourceTs)`. (2) Dùng `decimal` cho giá trị cảm biến: tốn chỗ, không cần; `float` (8 byte trong SQL Server) là đủ. (3) Quên `quality`: giá trị `Bad` lọt vào biểu đồ kiểm soát tạo ra cảnh báo giả.

### 2.4 BackgroundService, Channel và ghi theo batch

**Nó là gì.** `BackgroundService` là lớp cơ sở cho tác vụ chạy nền trong process ASP.NET Core (cài `IHostedService`): override `ExecuteAsync(CancellationToken)`, đăng ký bằng `services.AddHostedService<T>()`, host tự start/stop cùng ứng dụng. `System.Threading.Channels.Channel<T>` là hàng đợi producer/consumer async trong bộ nhớ.

**Vấn đề nó giải quyết.** Cách ngây thơ: trong handler MQTT, mỗi message `new DbContext` + `SaveChanges`. Với 10 msg/giây là 10 round-trip/giây; khi Mosquitto giao dồn 5.000 message sau một đợt mất kết nối thì handler chạy chậm, keep-alive trễ, broker ngắt luôn client. Gom lại: handler chỉ đẩy vào Channel (vài micro giây), **một** consumer gom tối đa 200 dòng hoặc 1 giây rồi insert một lần.

**Khái niệm cốt lõi.**

- `Channel.CreateBounded<T>(capacity)` với `FullMode = Wait`: khi đầy, `WriteAsync` chờ, tức handler bị chặn, MQTT client ngừng nhận, broker giữ message QoS 1 hộ ta. Đây là **backpressure** (áp lực ngược). Kiểu `DropOldest` thì mất dữ liệu âm thầm, không chọn.
- `SingleReader = true`: một consumer duy nhất ⇒ ghi theo đúng thứ tự đến. Quan trọng cho state history (đóng bản ghi cũ trước khi mở bản ghi mới).
- `BackgroundService` là singleton; `DbContext` là scoped ⇒ tạo scope mỗi batch bằng `IServiceScopeFactory`.
- Exception không bắt trong `ExecuteAsync`: từ .NET 6 mặc định **dừng cả host** (API chết theo). Luôn `try/catch` quanh thân vòng lặp.
- EF Core `AddRange` + `SaveChanges` tự gộp nhiều INSERT vào ít round-trip; đủ cho ~vài trăm dòng/giây. `SqlBulkCopy` (Microsoft.Data.SqlClient) nhanh hơn ở hàng nghìn dòng/giây nhưng phải tự map cột và bỏ qua change tracking; chọn khi đo thấy EF là nút cổ chai.

**Snippet: đường vào (handler, pseudo-code phần gọi service) và consumer gom batch.**

```csharp
// (1) Handler: nhanh, không chạm DB. Pseudo: _machines, _seen, _store, _channel là service của bạn.
private async Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs e, CancellationToken ct)
{
    try
    {
        var topic = e.ApplicationMessage.Topic;
        if (!MachineTopic.TryParse(topic, out var t)) { _log.LogWarning("Topic lạ {Topic}", topic); return; }
        var payload = e.ApplicationMessage.ConvertPayloadToString();
        if (t.Kind == "status") { await HandleStatusAsync(t, payload, ct); return; }   // payload thô "online"/"offline", không phải JSON
        // [v2] t.Kind == "process": tra MachineTag theo (machine, t.Tag); không có → bỏ + log; có → _store.ApplyProcess(...)
        var msg = JsonSerializer.Deserialize<MachineMessage>(payload, MachineJson.Options);
        if (msg is null || msg.Machine != t.Machine || !_machines.TryResolve(t, out var machine)) { _log.LogWarning("Bỏ {Topic}", topic); return; }
        if (!_seen.TryAdd(msg.MsgId)) return;                       // trùng msgId (QoS 1 giao lại): bỏ
        var item = _store.Apply(machine, t.Kind, msg, DateTime.UtcNow);  // đường nóng: RAM, tính delta, phát hiện đổi state
        if (item.StateChange is not null)                           // đổi state: đẩy ngay, không throttle (mục 2.7)
            await _notifier.MachineStateChangedAsync(machine.Line, item.StateChange.ToDto());
        await _channel.Writer.WriteAsync(item, ct);                 // đường lạnh: xuống DB theo batch
    }
    catch (JsonException ex) { _log.LogWarning(ex, "JSON hỏng"); }   // message rác: bỏ, không làm sập kết nối
    catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogError(ex, "Xử lý message lỗi"); }
}
```

```csharp
// (2) Consumer: một reader, gom tối đa 200 dòng hoặc 1 giây
protected override async Task ExecuteAsync(CancellationToken ct)
{
    var reader = _channel.Reader;
    var batch = new List<IngestItem>(MaxBatch);
    try
    {
        while (await reader.WaitToReadAsync(ct))                    // chờ vô hạn cho message đầu tiên
        {
            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
            window.CancelAfter(TimeSpan.FromSeconds(1));            // từ đây có tối đa 1 giây để gom thêm
            try
            {
                do { while (batch.Count < MaxBatch && reader.TryRead(out var i)) batch.Add(i); }
                while (batch.Count < MaxBatch && await reader.WaitToReadAsync(window.Token));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }   // hết 1 giây: ghi luôn
            await FlushAsync(batch, ct);
            batch.Clear();
        }
    }
    catch (OperationCanceledException) { }                          // dừng ứng dụng
    while (reader.TryRead(out var i)) batch.Add(i);                 // xả nốt phần còn lại trước khi thoát
    if (batch.Count > 0) await FlushAsync(batch, CancellationToken.None);
}
```

```csharp
// (3) Ghi một batch: 1 scope, 1 transaction; DB chết thì giữ batch và thử lại; trùng msgId thì ghi từng dòng
private async Task FlushAsync(List<IngestItem> batch, CancellationToken ct)
{
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MesDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            db.MachineEvents.AddRange(batch.Where(i => i.Event is not null).Select(i => i.Event!));
            await db.SaveChangesAsync(ct);
            foreach (var i in batch.Where(i => i.StateChange is not null))
                await _history.CloseAndOpenAsync(db, i.StateChange!, ct);   // mục 2.5, giữ đúng thứ tự
            await tx.CommitAsync(ct);
            return;
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        { await InsertOneByOneSkippingDuplicatesAsync(batch, ct); return; }   // unique index MsgId là lưới an toàn cuối
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Ghi batch {Count} lỗi, lần {Attempt}", batch.Count, attempt);
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(attempt * 2, 30)), ct);
        }
    }
}
```

Đăng ký DI: `services.AddSingleton(Channel.CreateBounded<IngestItem>(new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true }));` rồi `AddHostedService<MqttIngestService>()` và `AddHostedService<MachineEventBatchWriter>()`.

Chống trùng có hai lớp: **cache ngắn hạn** trong RAM (`_seen`: `ConcurrentDictionary<Guid, DateTime>` dọn định kỳ, hoặc `MemoryCache`, TTL ~10 phút; nhanh, che gần như mọi trường hợp giao lại) và **unique index `MsgId`** trong DB (che phần còn lại, ví dụ sau khi restart ingest). Ước lượng khối lượng: 8 máy, chu kỳ 12 giây, 16 giờ/ngày ≈ 8 × 4.800 ≈ 38.000 dòng `count`/ngày (không ghi heartbeat, không ghi count không đổi). Nếu lên 200 máy chu kỳ 1–2 giây: hàng triệu dòng/ngày, lúc đó cần partition theo tháng và archive (đúng thứ nhà máy hỏi).

**Bẫy thường gặp.**

- Quên xả Channel lúc dừng ứng dụng: mất tối đa 1 giây dữ liệu mỗi lần deploy.
- Một message lỗi trong batch làm hỏng cả batch: vì vậy validate ở handler, và có nhánh ghi từng dòng.
- Consumer dùng `await Task.Delay` cố định thay vì cửa sổ 1 giây như trên: hoặc trễ khi ít message, hoặc batch quá nhỏ.
- Index `MsgId` là GUID ngẫu nhiên nên chèn phân mảnh index; chấp nhận vì bảng insert-only, giữ khóa cụm là `Id bigint` tăng dần, và nếu cần thì dọn/rebuild theo lịch.

### 2.5 Gom trạng thái, Offline và LWT

**Nó là gì.** `MachineStateHistory` là chuỗi đoạn `[StartAt, EndAt)` cho mỗi máy, mỗi đoạn một state. `MachineEvent` ghi *sự kiện*; `MachineStateHistory` ghi *khoảng thời gian*, là thứ M5 cần để cộng thời gian Running/Down.

**Vấn đề nó giải quyết.** Nếu chỉ có bảng sự kiện, mỗi lần tính OEE phải quét và ghép cặp "đổi sang Down" với "đổi sang Running" trên hàng triệu dòng, và sự kiện thiếu làm sai cả ngày. Gom một lần lúc ghi, có ràng buộc DB bảo vệ.

**Khái niệm cốt lõi.**

- Khi state đổi: đóng bản ghi đang mở (`EndAt = ts`) và mở bản ghi mới (`StartAt = ts`) trong **một transaction**.
- Ràng buộc: mỗi máy đúng một bản ghi `EndAt IS NULL` (filtered unique index).
- Mốc thời gian: dùng `ts` giờ máy khi `|ts − ReceivedAt|` ≤ 60 giây (đề xuất của giáo án, hãy chỉnh theo thực tế), ngoài ngưỡng thì dùng `ReceivedAt` và gắn cờ lệch giờ. Message có `ts` cũ hơn `StartAt` của bản ghi đang mở thì chỉ ghi vào `MachineEvent`, không sửa lịch sử.
  - **[M4b, 28/10/2026, user duyệt]** Ngoại lệ cho message từ edge gateway (`source` ≠ `mqtt`): `ts` ở **quá khứ** tới 7 ngày (`MaxStoreAndForwardDelay`, bằng `maxAgeDays` mặc định của outbox) là **gửi muộn** từ hàng đợi SQLite, giữ `ts`, không gắn cờ lệch giờ. Chỉ `ts` ở tương lai quá 60 giây hoặc cũ hơn 7 ngày mới là lệch giờ. Máy MQTT trực tiếp giữ luật cũ. Lý do: gửi bù sau khi mất broker > 60 giây, luật cũ dời message đầu sang giờ nhận rồi bỏ các count sau là "cũ hơn" (tổng đúng nhưng sản lượng theo giờ dồn vào lúc nối lại). Hàm thuần `MachineRuntimeStore.EventTime`, test `Late_gateway_messages_keep_their_time`, `A_replayed_outbox_keeps_every_count_at_its_own_time`.
- `Offline` không do máy gửi: một watchdog định kỳ so `LastSeenAt` (thời điểm **nhận** message/heartbeat cuối, giờ server) với 30 giây.
- Nhận lại tín hiệu từ máy đang `Offline` → chuyển về state máy báo (heartbeat có kèm `state` nên biết ngay).
- **Thời gian ân hạn**: nếu chính ingest vừa mất kết nối MQTT hoặc vừa khởi động, chưa được phán máy `Offline` cho đến khi đã kết nối đủ 30 giây (nếu không, cứ Mosquitto khởi động lại là mọi máy đỏ/xám).
- LWT + retained (tùy chọn, bước 5): mỗi máy simulator dùng **một MQTT client riêng** (mỗi client một LWT) đặt LWT `offline` retained trên `status`, và publish `online` retained khi nối. Ingest nhận `offline` là chuyển `Offline` ngay không cần chờ 30 giây. Heartbeat vẫn cần: LWT chỉ bắt được kết nối chết, không bắt được máy treo phần mềm mà TCP vẫn mở.

**Snippet: DDL cốt lõi (T-SQL).**

```sql
CREATE TABLE dbo.MachineEvent (
    Id          bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_MachineEvent PRIMARY KEY,
    MsgId       uniqueidentifier NOT NULL,
    MachineId   int NOT NULL,
    Kind        tinyint NOT NULL,          -- 1=State 2=Count 3=Alarm 4=Online/Offline (server suy ra)
    MachineTs   datetime2(3) NOT NULL,     -- giờ máy, UTC
    ReceivedAt  datetime2(3) NOT NULL,     -- giờ server, UTC
    State       tinyint NULL,
    Counter     bigint NULL,
    Delta       bigint NULL,               -- ingest tính lúc ghi (mục 2.3)
    IsReset     bit NOT NULL CONSTRAINT DF_MachineEvent_IsReset DEFAULT 0,
    GoodDelta   int NULL, NgDelta int NULL,   -- chỉ đối chiếu, KHÔNG dùng cho OEE
    AlarmCode   nvarchar(32) NULL
);
CREATE UNIQUE INDEX UX_MachineEvent_MsgId ON dbo.MachineEvent (MsgId);
CREATE INDEX IX_MachineEvent_Machine_Ts ON dbo.MachineEvent (MachineId, MachineTs) INCLUDE (Kind, Delta);

CREATE TABLE dbo.MachineStateHistory (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_MachineStateHistory PRIMARY KEY,
    MachineId int NOT NULL, State tinyint NOT NULL,
    StartAt datetime2(3) NOT NULL, EndAt datetime2(3) NULL
);
-- Mỗi máy đúng một đoạn đang mở: DB tự chặn nếu code có lỗi
CREATE UNIQUE INDEX UX_MSH_OpenPerMachine ON dbo.MachineStateHistory (MachineId) WHERE EndAt IS NULL;
```

**Snippet: watchdog Offline (`PeriodicTimer` sẽ được giải thích kỹ ở M5; ở đây chỉ cần biết nó "tick" mỗi 5 giây; `TimeProvider` là đồng hồ có thể thay bằng giả trong test).**

```csharp
protected override async Task ExecuteAsync(CancellationToken ct)
{
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
    while (await timer.WaitForNextTickAsync(ct))
    {
        try
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            if (!_mqtt.HasBeenConnectedFor(_opt.HeartbeatTimeout, now)) continue;   // ân hạn: MES tự mù thì không kết luận về máy
            foreach (var m in _store.All())
                if (m.State != MachineState.Offline && now - m.LastSeenAt > _opt.HeartbeatTimeout)   // 30 giây
                    await _states.ApplyAsync(m.Code, MachineState.Offline, at: now, source: "watchdog", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogError(ex, "Watchdog lỗi"); }
    }
}
```

**Bẫy thường gặp.**

- Tin `ts` máy tuyệt đối: đồng hồ PLC lệch vài phút làm đoạn state chồng lên nhau hoặc nằm trong tương lai.
- Đóng/mở đoạn ngoài transaction: crash giữa chừng để lại máy không có bản ghi mở (hoặc hai bản ghi).
- Watchdog thiếu ân hạn (xem trên) và "Offline flapping": máy chập chờn 29–31 giây liên tục đổi state. Có thể thêm độ trễ hồi phục (phải nhận 2 heartbeat mới chuyển về).
- Retained `status = offline` cũ được phát lại lúc ingest khởi động: đúng là "thông tin cuối cùng", nhưng phải cho phép heartbeat mới ghi đè ngay.
- Lưu `Offline` bằng chính enum của máy nhưng quên rằng máy không bao giờ gửi giá trị đó: validate và từ chối `state = Offline` từ topic `state`.

### 2.6 SignalR từ gốc: hub, group, client WPF, Dispatcher

**Nó là gì.** ASP.NET Core SignalR là thư viện đẩy dữ liệu realtime từ server xuống client qua kết nối giữ mở. Tự chọn transport tốt nhất (WebSocket, rồi Server-Sent Events, rồi Long Polling), lo tuần tự hóa JSON, reconnect, và cho phép **server gọi hàm trên client** như gọi hàm thường.

**Vấn đề nó giải quyết.** Cách ngây thơ: mỗi WPF gọi `GET /api/machines` mỗi giây. 20 màn hình ⇒ 20 request/giây suốt ngày dù không có gì đổi, độ trễ trung bình nửa chu kỳ poll, và mỗi lần là một truy vấn DB. Tự viết WebSocket thô thì bạn phải tự làm reconnect, nhóm client, giao thức. SignalR: có sự kiện mới thì đẩy đúng cho đúng người, rảnh thì gần như không tốn gì.

**Khái niệm cốt lõi.**

- **Hub**: lớp phía server, điểm gặp gỡ. Client gọi method của hub (`InvokeAsync`); server gọi method trên client (`Clients.X.SendAsync` hoặc gọi thẳng nếu dùng hub định kiểu).
- **Connection**: mỗi kết nối có `ConnectionId` riêng; kết nối mới (kể cả reconnect) là `ConnectionId` mới.
- **Group**: nhóm tên tự đặt, gán connection vào. Đẩy cho nhóm bằng `Clients.Group("line:L1")`. Dashboard line 1 chỉ nhận sự kiện line 1. Group **không được lưu** qua reconnect.
- **`IHubContext<THub>`**: cách gửi từ ngoài hub (từ `BackgroundService`, MediatR handler). Hub chỉ sống trong một lần gọi của client; ingest không phải client nên phải dùng `IHubContext`.
- **Hub định kiểu** (`Hub<IMachineClient>`): interface liệt kê các method client, hết gõ nhầm chuỗi tên sự kiện.
- **Phía WPF**: gói `Microsoft.AspNetCore.SignalR.Client`; `HubConnectionBuilder` → `On<T>(tên, handler)` → `StartAsync`.
- **Kiến trúc lớp**: Application chỉ biết `IRealtimeNotifier` (interface); Infrastructure cài đặt bằng `IHubContext`; DTO push nằm trong Contracts; endpoint `MapHub` ở Api. Nhờ vậy Domain/Application không phụ thuộc SignalR.
- **SignalR không đảm bảo giao message cho client đang rớt kết nối.** Mẫu đúng: **nạp snapshot bằng REST, sau đó nhận delta bằng SignalR**, và nạp lại snapshot sau mỗi lần reconnect.
- **Luồng UI**: callback `On` chạy trên thread nền. WPF chỉ cho thread UI đụng vào control/`ObservableCollection`. Sai sẽ ném `InvalidOperationException: The calling thread cannot access this object because a different thread owns it` (hoặc `NotSupportedException` khi sửa collection từ thread khác). Giải pháp: chuyển về thread UI qua `Dispatcher`. Đặt sau interface `IUiDispatcher` để `Client.Core` không phụ thuộc WPF và ViewModel test được.

**Snippet: hub, notifier và DI phía server.**

```csharp
// MiniMes.Contracts: hợp đồng push (tên method = tên sự kiện client nhận)
public interface IMachineClient
{
    Task MachineStateChanged(MachineStateChangedDto dto);
    Task MachineCounterUpdated(MachineCounterDto dto);
    Task AndonChanged(AndonDto dto);
}

// MiniMes.Infrastructure/Realtime
[Authorize]
public sealed class MachineHub : Hub<IMachineClient>
{
    public Task JoinLine(string lineCode)  => Groups.AddToGroupAsync(Context.ConnectionId, $"line:{lineCode}");
    public Task LeaveLine(string lineCode) => Groups.RemoveFromGroupAsync(Context.ConnectionId, $"line:{lineCode}");
}

public sealed class SignalRRealtimeNotifier(IHubContext<MachineHub, IMachineClient> hub) : IRealtimeNotifier
{   // IRealtimeNotifier khai báo ở Application; Domain/Application không biết SignalR
    public Task MachineStateChangedAsync(string line, MachineStateChangedDto dto) =>
        hub.Clients.Group($"line:{line}").MachineStateChanged(dto);
    public Task MachineCounterUpdatedAsync(string line, MachineCounterDto dto) =>
        hub.Clients.Group($"line:{line}").MachineCounterUpdated(dto);
    public Task AndonChangedAsync(string line, AndonDto dto) =>
        hub.Clients.Group($"line:{line}").AndonChanged(dto);
}

// Api/Program.cs
builder.Services.AddSignalR();
app.MapHub<MachineHub>("/hubs/machines");
```

**Snippet: JWT cho SignalR (đã có JWT từ M0; chỉ thêm đoạn đọc token từ query).**

```csharp
o.Events = new JwtBearerEvents
{
    OnMessageReceived = ctx =>
    {   // WebSocket không gắn được header Authorization, client gửi token qua query "access_token"
        var token = ctx.Request.Query["access_token"];
        if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments("/hubs"))
            ctx.Token = token;
        return Task.CompletedTask;
    }
};
```

**Snippet: client trong `MiniMes.Client.Core` (không phụ thuộc WPF).**

```csharp
public sealed class RealtimeClient(ITokenStore tokens, string baseUrl) : IAsyncDisposable
{
    private readonly HubConnection _conn = new HubConnectionBuilder()
        .WithUrl($"{baseUrl}/hubs/machines", o => o.AccessTokenProvider = () => Task.FromResult<string?>(tokens.Token))
        .WithAutomaticReconnect()       // mặc định thử lại sau 0, 2, 10, 30 giây rồi bỏ cuộc (có overload nhận mảng TimeSpan)
        .Build();
    private readonly HashSet<string> _lines = new();
    public event Action<MachineStateChangedDto>? MachineStateChanged;
    public event Action<MachineCounterDto>? MachineCounterUpdated;   // (đăng ký On<MachineCounterDto> tương tự bên dưới)
    public event Action<AndonDto>? AndonChanged;
    public event Action<bool>? ConnectionChanged;            // true = đang kết nối
    public event Func<Task>? NeedResync;                     // sau reconnect: VM tải lại snapshot qua REST

    public async Task StartAsync()
    {
        _conn.On<MachineStateChangedDto>("MachineStateChanged", d => MachineStateChanged?.Invoke(d));   // đăng ký TRƯỚC StartAsync
        _conn.On<AndonDto>("AndonChanged", d => AndonChanged?.Invoke(d));
        _conn.Reconnecting += _ => { ConnectionChanged?.Invoke(false); return Task.CompletedTask; };
        _conn.Reconnected += async _ =>
        {   // ConnectionId mới ⇒ đã rớt khỏi mọi group: phải join lại
            foreach (var l in _lines) await _conn.InvokeAsync("JoinLine", l);
            ConnectionChanged?.Invoke(true);
            if (NeedResync is not null) await NeedResync();
        };
        _conn.Closed += _ => { ConnectionChanged?.Invoke(false); return Task.CompletedTask; };   // hết lượt retry: cần vòng khởi động lại thủ công
        await _conn.StartAsync();
    }
    public async Task JoinLineAsync(string line) { _lines.Add(line); await _conn.InvokeAsync("JoinLine", line); }
    public ValueTask DisposeAsync() => _conn.DisposeAsync();
}
```

**Snippet: ViewModel, đưa cập nhật về thread UI.**

```csharp
public interface IUiDispatcher { void Post(Action action); }          // Client.Core: không nhắc tới WPF
public sealed class WpfUiDispatcher : IUiDispatcher                    // BackOffice/Kiosk: cài bằng Dispatcher
{
    public void Post(Action action) => Application.Current.Dispatcher.InvokeAsync(action);   // không chặn thread gọi
}

public partial class LineDashboardViewModel(RealtimeClient rt, IUiDispatcher ui, ApiClient api) : ObservableObject
{
    public ObservableCollection<MachineTileViewModel> Tiles { get; } = new();

    public async Task StartAsync(string line)
    {
        rt.MachineStateChanged += d => ui.Post(() => Tiles.FirstOrDefault(t => t.Code == d.Machine)?.Apply(d));  // luôn qua Dispatcher
        rt.NeedResync += () => LoadSnapshotAsync(line);
        await rt.StartAsync();
        await rt.JoinLineAsync(line);
        await LoadSnapshotAsync(line);                        // nghe trước, tải snapshot sau: không hở giữa hai nguồn
    }
    private async Task LoadSnapshotAsync(string line)
    {
        var snap = await api.GetLineStatusAsync(line);        // REST, thread nền
        ui.Post(() => { /* thay/cập nhật Tiles từ snap */ });
    }
}
// MachineTileViewModel.Apply(dto): bỏ qua nếu dto.Ts <= Ts đang hiển thị (event cũ đến muộn sau snapshot)
```

**Bẫy thường gặp.**

- Sửa `ObservableCollection` hoặc control từ callback `On`: exception, hoặc tệ hơn là lỗi ngẫu nhiên. Mọi thứ đi qua `IUiDispatcher`.
- Dùng `Dispatcher.Invoke` (đồng bộ) trong callback có thể gây deadlock khi thread UI đang chờ thứ gì đó; ưu tiên `InvokeAsync`/`BeginInvoke`.
- Quên join lại group sau `Reconnected`: dashboard "đứng yên" mà không báo lỗi.
- Đăng ký `On<T>` sau `StartAsync`: lỡ những event đầu tiên.
- Tên method chuỗi sai chính tả: chỉ lộ lúc chạy (đó là lý do hub định kiểu).
- Đẩy cho `Clients.All`: mọi màn hình nhận dữ liệu của mọi line. Dùng group.
- Chạy nhiều instance API: cần backplane (Redis) để các instance chia sẻ group; MVP chạy một instance nên chưa cần.

### 2.7 Throttle: không để UI bị dồn

**Nó là gì.** Throttle giới hạn tần suất: dù đầu vào dồn dập, đầu ra tối đa N lần/giây. Khác **debounce** (chờ yên lặng một lúc rồi mới phát một lần, hợp với ô tìm kiếm) và **batching** (gom nhiều mục thành một lượt ghi, mục 2.4).

**Vấn đề nó giải quyết.** Push mỗi message: 200 máy × 10 msg/giây = 2.000 event/giây × N client, mỗi event kích một lần binding/vẽ lại. WPF nghẽn hàng đợi Dispatcher, giao diện đơ, và người nhìn không đọc nổi số nhảy 10 lần/giây. Cộng dồn ở server rồi chỉ phát bản mới nhất mỗi giây thì mỗi client chỉ nhận ≤ 200 event/giây (một event/máy/giây), không mất thông tin vì mỗi snapshot đã chứa tổng mới nhất.

**Khái niệm cốt lõi.**

- Đường nóng lưu trạng thái mới nhất vào `MachineRuntimeStore` (RAM) và đặt cờ `CounterDirty`.
- Một service `RealtimePublisher` mỗi giây lấy các máy "dirty", gửi snapshot mới nhất, xóa cờ.
- **Chỉ throttle số đếm.** Đổi state và Andon là hiếm và quan trọng: đẩy **ngay** (đây là điều kiện để đạt "< 2 giây").
- Client cũng có thể gộp thêm (chỉ vẽ lại theo `DispatcherTimer`), nhưng nếu server đã throttle thì thường không cần.

**Snippet.**

```csharp
public sealed class MachineRuntimeStore    // singleton, thread-safe; nạp từ DB lúc khởi động (state mở + counter cuối)
{
    private readonly ConcurrentDictionary<string, MachineRuntime> _m = new();
    public IEnumerable<MachineRuntime> All() => _m.Values;

    public IReadOnlyList<CounterSnapshot> TakeDirty()      // trả snapshot của máy có số đếm mới, xóa cờ
    {
        var list = new List<CounterSnapshot>();
        foreach (var m in _m.Values)
            lock (m.Gate)                                  // đường nóng cũng lock m.Gate khi ghi
            {
                if (!m.CounterDirty) continue;
                m.CounterDirty = false;
                list.Add(new(m.Code, m.Line, m.TotalCount, m.State, m.LastSeenAt));
            }
        return list;
    }
}

public sealed class RealtimePublisher(MachineRuntimeStore store, IRealtimeNotifier notifier) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));     // throttle: tối đa 1 lần/giây/máy
        while (await timer.WaitForNextTickAsync(ct))
            foreach (var s in store.TakeDirty())
                await notifier.MachineCounterUpdatedAsync(s.Line, s.ToDto());
    }
}
```

**Bẫy thường gặp.**

- Throttle cả state/Andon: trễ thêm tới 1 giây, dễ vượt 2 giây khi cộng độ trễ khác.
- Chỉ throttle ở client: server và mạng vẫn gánh toàn bộ lưu lượng.
- Gửi "delta" thay vì "giá trị mới nhất" rồi throttle: gộp mất số. Với throttle, luôn gửi trạng thái tuyệt đối (tổng đếm, state), không gửi "+3".
- Đọc/ghi `MachineRuntime` từ hai thread không khóa: số đếm rách.

### 2.8 Thiết kế Machine Simulator (console app)

**Nó là gì.** `MiniMes.MachineSimulator`: console .NET 10 giả lập N máy theo file cấu hình. Dùng `Microsoft.Extensions.Hosting` (cấu hình, log, dừng gọn bằng Ctrl+C).

**Vấn đề nó giải quyết.** Không có máy thật, nhưng cần dữ liệu "sống" để dựng dashboard, test Andon, và **cố tình gây lỗi** (Down, NG, reset, mất mạng, lệch giờ) để kiểm tra ingest. Hai vai theo spec:

1. **Vai máy (MQTT)**: state, bộ đếm tích lũy, alarm, heartbeat, giống PLC/gateway.
2. **Vai trạm tự động (HTTP)**: với trạm có máy (SMT, Test), mỗi chu kỳ lấy serial kế tiếp đang chờ ở bước đó và gọi đúng `POST /stations/{id}/scan` mà kiosk dùng, kèm OK/NG + DefectCode. Trạm thủ công (Assembly, Repair, Pack): người scan trên Kiosk, hoặc bật `--auto-operator` để simulator scan thay.

**Khái niệm cốt lõi.**

- Mỗi máy: một `Task` chạy vòng chu kỳ (state machine nhỏ) + một `Task` heartbeat độc lập (giống tác vụ truyền thông riêng của PLC).
- **Mỗi máy một MQTT client** (`ClientId = sim-{machine}`) để có LWT riêng.
- Máy giữ **bộ đếm nội bộ** và tiếp tục đếm ngay cả khi không gửi được (như PLC thật). Khi nối lại, message kế tiếp mang tổng mới ⇒ MES không mất số (đây là cách đạt DoD "không mất dữ liệu đếm quá 1 chu kỳ").
- Cấu hình có `seed` cho `Random` để demo lặp lại được.
- Lệnh console qua hàng đợi lệnh cho từng máy; vòng lặp máy đọc lệnh ở đầu mỗi vòng (không sửa state từ thread khác).
- Vai 2 cần biết "serial nào đang chờ ở bước này": dùng endpoint `GET /stations/{id}/waiting-serials?take=1` (Module 3, bước 5b). Simulator đăng nhập bằng một user role Operator riêng (seed) qua endpoint JWT của M0, rồi mở `StationSession` cho trạm (Module 3, bước 5) trước khi scan. Nếu không có serial chờ, máy chuyển `Idle` (starved) rồi thử lại mỗi vài giây: tự nhiên tạo ra state Idle thật.

**Snippet: `machines.json`.**

```json
{
  "site": "HN1",
  "area": "SMT-ASSY",
  "seed": 42,
  "mqtt": { "host": "localhost", "port": 1883, "heartbeatSec": 5 },
  "api":  { "baseUrl": "https://localhost:5001", "user": "sim-station" },
  "machines": [
    { "code": "L1-SMT-01", "line": "L1", "cycleTimeSec": 12, "initialCounter": 15000,
      "downProbPerCycle": 0.002, "downDurationSec": [30, 180], "ngProb": 0.01 },
    { "code": "L1-TEST-01", "line": "L1", "cycleTimeSec": 8, "initialCounter": 0,
      "downProbPerCycle": 0.001, "downDurationSec": [20, 90], "ngProb": 0.03,
      "station": { "id": 3, "mode": "AutoTest" }, "clockSkewSec": 0 }
  ]
}
```

**Snippet: vòng chu kỳ của một máy (rút gọn).**

```csharp
public async Task RunAsync(CancellationToken ct)
{
    await PublishStateAsync(MachineState.Running, ct);                  // state retained
    while (!ct.IsCancellationRequested)
    {
        ApplyCommands();                                                // lệnh console: down/up/ng/reset/mute...
        switch (_state)
        {
            case MachineState.Running:
                await Task.Delay(_cfg.CycleTime, ct);
                if (_forceDown || _rng.NextDouble() < _cfg.DownProbPerCycle) { await GoDownAsync(ct); break; }
                bool ng;
                if (_forceNg > 0) { ng = true; _forceNg--; } else ng = _rng.NextDouble() < _cfg.NgProb;
                _counter++; if (ng) _ng++; else _good++;                // bộ đếm tích lũy, không bao giờ giảm (trừ lệnh reset)
                await PublishCountAsync(ct);                            // gộp: tối đa 1 message/giây
                if (_station is not null && !await _station.ScanNextAsync(ng, ct))   // vai 2: POST /stations/{id}/scan
                    await SetStateAsync(MachineState.Idle, ct);         // không có serial chờ: starved
                break;
            case MachineState.Down:
                if (!_forceDown && _clock.UtcNow >= _downUntil) await SetStateAsync(MachineState.Running, ct);
                else await Task.Delay(500, ct);
                break;
            default:                                                    // Idle/Setup/PlannedStop
                await Task.Delay(500, ct); break;
        }
    }
}
```

**Snippet: publisher có LWT (MQTTnet 4.x; tên method builder của phần Will nên đối chiếu README nếu bản bạn dùng khác).**

```csharp
var statusTopic = MachineTopic.Build(site, line, machine, "status");
var options = new MqttClientOptionsBuilder()
    .WithTcpServer(host, port).WithClientId($"sim-{machine}")
    .WithWillTopic(statusTopic).WithWillPayload("offline")           // broker phát hộ khi ta chết bất thường
    .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).WithWillRetain(true)
    .Build();
await client.ConnectAsync(options, ct);
await Publish(statusTopic, "online", MqttQualityOfServiceLevel.AtLeastOnce, retain: true);
// Publish(...) nội bộ: new MqttApplicationMessageBuilder().WithTopic(t).WithPayload(p)
//   .WithQualityOfServiceLevel(q).WithRetainFlag(retain).Build(); rồi client.PublishAsync(msg, ct)
```

Bảng lệnh console (gõ khi demo):

| Lệnh | Tác dụng | Kiểm tra gì |
| --- | --- | --- |
| `list` | Hiện state, counter từng máy | |
| `down <m> [giây]` / `up <m>` | Ép Down / Running | Đổi state, Andon tự động |
| `setup <m>`, `stop <m>` | `Setup` / `PlannedStop` | Phân loại state |
| `ng <m> <n>` | n sản phẩm tiếp theo là NG | Đường Good/NG qua scan |
| `reset <m>` | Counter về 0 | Xử lý reset |
| `mute <m>` / `unmute <m>` | Ngừng/tiếp tục gửi (kể cả heartbeat) | `Offline` sau 30 giây |
| `dup <m>` | Gửi message kế tiếp hai lần, cùng `msgId` | Chống trùng |
| `skew <m> <giây>` | Cộng lệch vào `ts` | Cảnh báo lệch giờ |
| Kill process (không Ctrl+C sạch) | TCP đóng không DISCONNECT | LWT phát `offline` |

**Bẫy thường gặp.**

- Dùng `Thread.Sleep`/`Task.Delay` không truyền `ct`: Ctrl+C không dừng được.
- Dùng chung một MQTT client cho mọi máy: chỉ có một LWT, không mô phỏng được máy rớt riêng lẻ.
- Random không seed: demo mỗi lần một khác, không tái hiện được lỗi.
- Sinh serial hoặc kết quả trong simulator thay vì lấy từ API: sẽ không kiểm thử được route check của M3. Simulator phải đi đúng đường của kiosk.
- Simulator quá "sạch": không bao giờ lệch giờ, reset, trùng. Dùng lệnh ở bảng trên để tự tạo dữ liệu bẩn.

### 2.9 Andon trong code: domain, chống trùng, quyền

**Nó là gì.** `AndonCall` là aggregate nhỏ có state machine ba trạng thái chính (`Raised → Acknowledged → Resolved`, thêm `Cancelled` cho báo nhầm), viết theo kiểu rich domain model như `WorkOrder` ở M2 (xem Module 2, mục state machine).

**Vấn đề nó giải quyết.** Nếu Andon chỉ là dòng trong bảng với cột `Status` sửa tự do, hai supervisor cùng bấm nhận, hoặc người ta resolve mà không chọn lý do, hoặc auto-Andon và nút tay tạo hai bản cho cùng một sự cố. Đặt luật trong domain và ràng buộc ở DB.

**Khái niệm cốt lõi.**

- Method `Acknowledge`/`Resolve` ném lỗi domain khi chuyển sai; `Resolve` bắt buộc `downtimeReasonId`.
- Resolve thẳng từ `Raised` được phép (người sửa xong luôn): tự điền `AckBy/AckAt` để số liệu phản ứng không bị null.
- **Chống trùng**: chỉ một Andon *mở* cho mỗi (máy, loại), bảo đảm bằng filtered unique index chứ không chỉ bằng `if` trong code (hai luồng chạy đua vẫn qua được `if`).
- **Đua nhau ack**: `RowVersion` (concurrency token) ⇒ người thứ hai nhận 409 (xem Module 2, mục optimistic concurrency).
- Auto-raise: một `AndonWatcherService` mỗi 5 giây duyệt `MachineRuntimeStore`; máy `Down` liên tục ≥ X giây (cấu hình, mặc định 60) và chưa có Andon mở loại `Machine` ⇒ gửi command `RaiseAndonCommand(Source = Auto)` (MediatR như M1).
- Phân quyền: Operator raise; Supervisor/Maintenance/Admin acknowledge và resolve.
- Sau mỗi thay đổi: ghi DB rồi `IRealtimeNotifier.AndonChangedAsync` (push ngay, không throttle).

**Snippet.**

```csharp
public sealed class AndonCall
{
    public long Id { get; private set; }
    public int MachineId { get; private set; }
    public AndonType Type { get; private set; }                 // Machine, Material, Quality, Other
    public AndonStatus Status { get; private set; }             // Raised=0, Acknowledged=1, Resolved=2, Cancelled=3
    public string RaisedBy { get; private set; } = default!;    // user hoặc "system" khi tự động
    public DateTime RaisedAt { get; private set; }
    public string? AckBy { get; private set; }
    public DateTime? AckAt { get; private set; }
    public DateTime? ResolvedAt { get; private set; }
    public int? DowntimeReasonId { get; private set; }
    public byte[] RowVersion { get; private set; } = default!;  // 2 supervisor ack cùng lúc: người sau nhận 409

    public void Acknowledge(string by, DateTime nowUtc)
    {
        if (Status != AndonStatus.Raised) throw new DomainException("Andon đã được nhận hoặc đã đóng.");
        Status = AndonStatus.Acknowledged; AckBy = by; AckAt = nowUtc;
    }

    public void Resolve(string by, int downtimeReasonId, DateTime nowUtc)
    {
        if (Status is AndonStatus.Resolved or AndonStatus.Cancelled) throw new DomainException("Andon đã đóng.");
        if (Status == AndonStatus.Raised) { AckBy = by; AckAt = nowUtc; }   // xử lý luôn, bỏ qua bước nhận
        Status = AndonStatus.Resolved; ResolvedAt = nowUtc; DowntimeReasonId = downtimeReasonId;
    }
}
```

```sql
-- Mỗi (máy, loại) tối đa một Andon đang mở (Raised=0 hoặc Acknowledged=1)
CREATE UNIQUE INDEX UX_AndonCall_OpenPerMachineType ON dbo.AndonCall (MachineId, Type) WHERE Status <= 1;
```

API gợi ý: `POST /api/andon` (raise), `POST /api/andon/{id}/acknowledge`, `POST /api/andon/{id}/resolve` (body có `downtimeReasonId`), `GET /api/andon?status=open`, và `GET /api/lines/{code}/status` (snapshot cho dashboard ở mục 2.6).

**Bẫy thường gặp.**

- Tự đóng Andon khi máy quay lại `Running`: mất lý do dừng. Nên hiển thị "máy đã chạy lại" nhưng vẫn chờ người resolve.
- Auto-Andon cho mọi lần `Down` (kể cả 5 giây): Andon fatigue. X phải đủ lớn.
- Bắt lỗi unique index của tình huống đua nhau như lỗi 500: xử lý thành "đã có Andon mở", trả bản hiện có.
- Không lưu `RaisedBy = system` cho bản tự động: về sau không phân biệt được ai gọi.
- ~~Leo thang chưa nằm trong MVP~~ **[v2]** Leo thang nằm trong phạm vi, xem mục 2.9b.

### 2.9b [v2] Andon leo thang theo thời gian (escalation)

**Nó là gì.** Andon `Raised` mà sau N phút chưa ai `Acknowledge` thì hệ thống tự báo lên cấp trên; quá M phút nữa thì báo lên cấp cao hơn. OpenMES có tính năng này; nhà máy Toyota và hầu hết nhà máy Nhật ở VN đều có quy định thời gian phản ứng theo cấp.

**Vấn đề nó giải quyết.** Supervisor đang họp, bảo trì đang ở line khác: Andon đỏ mà không ai đến. Không có leo thang thì máy dừng cả tiếng và không ai chịu trách nhiệm.

**Khái niệm cốt lõi.**

| `AndonEscalationRule` | Cột |
| --- | --- |
| Khóa | `AndonType`, `Level` |
| Dữ liệu | `AfterMinutes` (tính từ `RaisedAt`), `NotifyRole`, `Note` |

Seed:

| AndonType | Level | AfterMinutes | NotifyRole | Ý nghĩa |
| --- | --- | --- | --- | --- |
| Machine | 0 | 0 | Supervisor, Maintenance | Khi raise (hành vi cũ) |
| Machine | 1 | 3 | Admin (đại diện trưởng xưởng ở MVP) | 3 phút chưa ai nhận |
| Machine | 2 | 10 | Admin + ghi log cảnh báo mức Error | Ngoài đời: gửi email/Teams/Zalo cho giám đốc sản xuất |
| Quality | 1 | 5 | QC | |

- `AndonCall` thêm `EscalationLevel` (mặc định 0) và `LastEscalatedAt`. Method domain `Escalate(int toLevel, DateTime nowUtc)` chỉ cho **tăng** level và chỉ khi `Status = Raised`; gọi lại cùng level thì bỏ qua (idempotent).
- `AndonWatcherService` (đã chạy mỗi 5 giây để auto-raise) thêm việc: với mỗi Andon `Raised`, tính level đáng có = level lớn nhất có `AfterMinutes ≤ now − RaisedAt`; lớn hơn level hiện tại thì `Escalate` rồi push SignalR tới group `role:{NotifyRole}` (client join group theo role trong JWT khi kết nối).
- Acknowledge thì dừng leo thang. Ghi lại `EscalationLevel` cuối cùng để báo cáo "bao nhiêu Andon phải leo thang tới cấp 2 trong tháng": một KPI quản lý rất được quan tâm.

```csharp
public bool Escalate(int toLevel, DateTime nowUtc)
{
    if (Status != AndonStatus.Raised || toLevel <= EscalationLevel) return false;   // đã nhận, hoặc đã ở level này
    EscalationLevel = toLevel; LastEscalatedAt = nowUtc;
    return true;
}

// trong AndonWatcherService, mỗi 5 giây
var now = _time.GetUtcNow().UtcDateTime;
foreach (var a in openRaised)
{
    var due = rules.Where(r => r.AndonType == a.Type && r.AfterMinutes <= (now - a.RaisedAt).TotalMinutes)
                   .Select(r => r.Level).DefaultIfEmpty(0).Max();
    if (a.Escalate(due, now)) escalated.Add(a);
}
await db.SaveChangesAsync(ct);                                    // RowVersion: nếu ai vừa Ack thì xung đột → bỏ qua lần này
foreach (var a in escalated) await _notifier.AndonEscalatedAsync(a.ToDto(), RolesFor(a), ct);
```

**Bẫy.** (1) Dùng `DateTime.UtcNow` trực tiếp thì không test được "3 phút sau": dùng `TimeProvider` + `FakeTimeProvider` (như watchdog Offline). (2) Supervisor Ack đúng lúc watcher leo thang: `RowVersion` làm một bên thua; watcher thua thì bỏ qua (lần chạy sau thấy `Acknowledged`), không được ghi đè. (3) API restart: leo thang tính từ `RaisedAt` trong DB nên không mất; nhưng đừng gửi lại thông báo cho level đã gửi (nhờ `EscalationLevel` lưu DB).

### Sơ đồ luồng dữ liệu của module

```
 VAI MÁY (Simulator)                       Mosquitto            MES API (.NET 10, 1 process)
 ┌────────────────────┐  MQTT QoS 1/0     ┌────────┐   ┌──────────────────────────────────────────────────────────────┐
 │ SimulatedMachine   │ ── state/count ─▶ │ broker │─▶ │ MqttIngestService (MQTTnet)                                   │
 │  (1 MQTT client/   │    alarm/heartbt  │ retained│   │   parse topic → validate → machine lookup → dedupe(msgId)     │
 │   máy, có LWT)     │ ── status(LWT) ─▶ │  + LWT │   │            │ đường nóng                  │ đường lạnh         │
 └────────────────────┘                   └────────┘   │            ▼                             ▼                    │
 ┌────────────────────┐   HTTP (JWT)                   │   MachineRuntimeStore (RAM)      Channel<IngestItem> (bounded)│
 │ VAI TRẠM TỰ ĐỘNG   │ ── POST /stations/{id}/scan ─▶ │   tính delta, đổi state            │ 1 consumer, ≤200/1s      │
 │ (cùng process sim) │        (M3: route check)       │            │                        ▼                         │
 └────────────────────┘                                │   ┌────────┴────────┐      MachineEventBatchWriter           │
                                                       │   ▼                 ▼        (transaction)                    │
 ┌───────────────────┐                                 │ RealtimePublisher  AndonWatcher       │                       │
 │ WPF Kiosk         │ ── POST /andon ───────────────▶ │  (1 giây/máy)      Down > X giây      ▼                       │
 │  nút Andon        │                                 │   │                 │      SQL Server: MachineEvent,          │
 └───────────────────┘                                 │   │     OfflineWatchdog     MachineStateHistory, AndonCall      │
                                                       │   │   (heartbeat > 30 giây)  ▲                                  │
 ┌───────────────────┐   REST snapshot + SignalR       │   ▼                 ▼        │ Application: Raise/Ack/Resolve   │
 │ WPF Dashboard /   │ ◀───────────────────────────────│ IRealtimeNotifier ─▶ SignalR MachineHub (group "line:L1")    │
 │ Kiosk (Client.Core│   MachineStateChanged,          └──────────────────────────────────────────────────────────────┘
 │ RealtimeClient →  │   MachineCounterUpdated,
 │ IUiDispatcher →   │   AndonChanged
 │ ViewModel → View) │
 └───────────────────┘
```

## 3. Kế hoạch thực hiện

Tổng khoảng 26 giờ (**[v2]** +4 giờ: topic UNS và trường mới ở bước 1–3, tag process ở bước 4b, leo thang Andon ở bước 7b). Làm đúng thứ tự: mỗi bước cho ra thứ chạy được và kiểm tra được, bước sau dựa vào bước trước. Chỗ nào ghi "unit test" thì viết test trước hoặc ngay sau khi viết hàm (hàm thuần: `CounterMath`, `MachineTopic`, `AndonCall`).

- [x] **Bước 0 — Mosquitto và thử bằng tay** (1 giờ)
  - Mục tiêu: hiểu topic, wildcard, QoS, retained bằng tay trước khi viết một dòng C#.
  - Việc cần làm: thêm service `mosquitto` vào `docker-compose.yml` + `docker/mosquitto/mosquitto.conf` (mục 2.1); cài `mosquitto_pub/sub` (hoặc dùng `docker exec` vào container); chạy các lệnh ở 2.1; thử `+`, `#`, retained (subscribe sau khi publish), xóa retained.
  - File: `docker/mosquitto/mosquitto.conf`, sửa `docker-compose.yml`.
  - Kiểm tra: subscribe `minimes/#` thấy message publish từ terminal khác; subscriber mở sau vẫn nhận bản retained; `docker compose restart mosquitto` xong retained vẫn còn (nhờ `persistence true`).
  - **Ghi chú khi làm (17/10/2026).** Service `mosquitto` (`eclipse-mosquitto:2`, cổng 1883, volume `mosquitto-data`, healthcheck đọc `$SYS`) và file cấu hình đã có từ M0 ở **`docker/mosquitto.conf`** (spec ghi `docker/mosquitto/mosquitto.conf`; giữ vị trí cũ, nội dung đúng 2.1: `listener 1883`, `allow_anonymous true`, `persistence true`, `persistence_location /mosquitto/data/`). Không cài `mosquitto_pub/sub` trên Windows: dùng `docker exec mes-mosquitto mosquitto_pub|mosquitto_sub`. Topic theo site/area seed của M0: `minimes/VN-BN01/SMT-ASSY/L1/{machine}/{kind}`. Kiểm tra: `+` (`.../L1/+/state`) nhận `state` của L1-SMT-01 và L1-TEST-01, không nhận `count`; `#` (`minimes/VN-BN01/#`) nhận cả ba; publish `state` retained rồi mới subscribe vẫn nhận ngay bản cuối; `alarm` không retained thì subscriber đến sau không nhận gì; `docker compose restart mosquitto` xong retained vẫn còn; publish payload rỗng `-r -n` xóa retained.

- [x] **Bước 1 — Contracts, entity, migration** (2 giờ)
  - Mục tiêu: chốt hợp đồng dữ liệu và bảng.
  - Việc cần làm: `MachineMessage` (**[v2]** có `Tag`, `Value`, `Quality`, `Source`), `MachineState`, `MachineTopic` (**[v2]** 6 cấp có `area` + dạng 7 cấp `process/{tag}`; unit test cả hai), `MachineJson` (2.3); `CounterMath` + unit test theo bảng; entity `MachineEvent`, `MachineStateHistory`, `AndonCall` (**[v2]** có `EscalationLevel`, `LastEscalatedAt`), **[v2]** `MachineTag`, `ProcessValue`, `AndonEscalationRule` + EF configuration (filtered unique index viết bằng `HasIndex(...).HasFilter(...)`); migration; bổ sung cột `Machine.MqttTopicKey` nếu M1 chưa có.
  - File: `src/MiniMes.Contracts/Machines/*.cs`, `src/MiniMes.Domain/Machines/CounterMath.cs`, `src/MiniMes.Infrastructure/Persistence/Configurations/Machine*.cs`, `tests/MiniMes.UnitTests/Machines/CounterMathTests.cs`.
  - Kiểm tra: test xanh (đủ 6 dòng bảng 2.3); `dotnet ef database update` chạy; thử chèn hai bản ghi `MachineStateHistory` cùng máy cùng `EndAt = NULL` phải bị DB từ chối.
  - **Ghi chú khi làm (17/10/2026).** `Contracts/Machines/MachineContracts.cs`: `MachineState`, `DataQuality`, `MachineMessage` (**[v2]** `Tag`, `Value`, `Quality`, `Source`), `MachineSources`, `MachineKinds`, `ParsedTopic`, `MachineTopic` (`Build`, `BuildProcess`, `TryParse` 6 cấp và 7 cấp `process/{tag}`, từ chối cấp rỗng / gốc khác / 7 cấp không phải process; hằng `AllMachineKinds`, `AllProcessTags` cho ingest), `MachineJson`. Domain: `Machines/{CounterMath, MachineEvent, MachineStateHistory, MachineTag}.cs` (`ProcessValue` nằm cùng file `MachineTag`), `Andon/AndonCall.cs` (`AndonCall`, `AndonEscalationRule`). **Enum có hai bản:** Domain không tham chiếu Contracts, nên `MachineState`/`DataQuality` có ở cả hai (bản Domain là `byte`); unit test bắt hai bản cùng danh sách tên vì ingest map theo tên. `MachineEvent` tạo bằng factory theo loại (`StateChanged`, `Count`, `Alarm`, `Connection`). `MachineTag.Judge(value, reported)` trả `Bad` khi ngoài khoảng plausible (dùng ở Bước 4b). `AndonCall` mới có constructor + thuộc tính (**[v2]** `EscalationLevel`, `LastEscalatedAt`, `RowVersion`); các method chuyển trạng thái để Bước 7/7b cùng test. `AndonEscalationRule` khóa `(AndonType, Level)`, `NotifyRoles` là chuỗi phân tách bằng dấu phẩy (một cấp có thể báo hai role, như seed Level 0 "Supervisor, Maintenance"); seed ở Bước 7b. `Machine.MqttTopicKey` và `DataSource` đã có từ M1. EF: `Configurations/MachineConfigurations.cs`; enum của bảng lớn lưu `tinyint` như DDL 2.5; `AlarmCode` dùng `varchar(32)` (spec ghi `nvarchar`; mã lỗi máy là ASCII như các mã khác của dự án); `datetime2(3)`; filtered unique `UX_MachineStateHistory_OpenPerMachine` (`[EndAt] IS NULL`) và `UX_AndonCalls_OpenPerMachineType` (`[Status] <= 1`); unique `MsgId` ở `MachineEvents` và `ProcessValues`; index `(MachineId, MachineTs) INCLUDE (Kind, Delta)`, `(MachineTagId, SourceTs)`; check `LowerPlausible < UpperPlausible`; FK từ `MachineStateHistory`, `MachineTags`, `AndonCalls` (bảng ít dòng), không FK ở `MachineEvents`/`ProcessValues` (log ghi theo batch). Audit: `MachineTag`, `AndonCall`, `AndonEscalationRule`; không audit 3 bảng log. Migration `M4_MachineConnectivity` đã áp vào DB dev. Kiểm tra: unit `CounterMathTests` (đủ **6 dòng bảng 2.3** + mất message không mất sản lượng), `MachineContractTests` 22 (topic 6/7 cấp, 6 topic sai, JSON camelCase + enum chữ + round-trip, message gateway của spec đọc được (`"quality": "good"` chữ thường), hai bộ enum khớp, đoạn state đóng một lần và không đóng trước khi mở, `Judge` 6 trường hợp, validate tag 4 trường hợp, sự kiện count giữ delta reset); integration `MachineSchemaTests` 5: **hai đoạn mở cùng máy → DB từ chối (409)**, hai Andon mở cùng máy+loại → 409 (loại khác được; đóng bản cũ thì mở được bản mới), trùng `MsgId` ở `MachineEvents` và `ProcessValues` → 409, tag trùng tên cùng máy → 409, check constraint plausible, kiểu cột (`tinyint`, `varchar`, `nvarchar` cho Unit, `float`, `timestamp`).

- [x] **Bước 2 — Simulator vai máy (MQTT)** (2,5 giờ)
  - Mục tiêu: có nguồn dữ liệu sống.
  - Việc cần làm: project console dùng Generic Host; đọc `machines.json` (2.8; **[v2]** thêm `area` cấp site, mọi message có `source = "mqtt"`, `quality = "Good"`); mỗi máy một `Task` chu kỳ + một `Task` heartbeat, mỗi máy một MQTT client; publish `state` (retained), `count`, `alarm` khi vào `Down`, `heartbeat`; vòng lặp lệnh console (`list`, `down`, `up`, `ng`, `reset`, `mute`, `dup`, `skew`); dừng gọn bằng Ctrl+C; publisher bắt lỗi khi mất broker và tự nối lại, bộ đếm vẫn tiếp tục.
  - File: `src/MiniMes.MachineSimulator/{Program.cs, Config/SimulatorOptions.cs, Machines/SimulatedMachine.cs, Mqtt/MachinePublisher.cs, ConsoleCommands/CommandLoop.cs}`, `machines.json`.
  - Kiểm tra: `mosquitto_sub -t "minimes/#" -v` thấy đủ bốn loại message theo đúng tần suất; gõ `down L1-SMT-01 60` thì thấy state `Down` + alarm; `docker compose stop mosquitto` rồi `start`: simulator không crash, counter trong message tiếp theo cao hơn (không mất số).
  - **Ghi chú khi làm (17/10/2026).** Project mới `MiniMes.MachineSimulator` (chỉ tham chiếu `MiniMes.Contracts`; thêm luật vào `DependencyTests`, CI build thêm project này). **MQTTnet 5.2.0** (bản mới nhất, không dùng 4.3 của snippet): `new MqttClientFactory().CreateMqttClient()`; `MqttClientOptionsBuilder` vẫn có `WithTcpServer`, `WithClientId`, `WithKeepAlivePeriod`, `WithTimeout`, `WithWillTopic/Payload/QualityOfServiceLevel/Retain`; `DisconnectAsync` cần `MqttClientDisconnectOptions`. File: `Program.cs` (Generic Host, đọc `machines.json` cạnh exe hoặc `--config <file>`, validate cấu hình, sai thì in lỗi và thoát mã 1), `Config/SimulatorOptions.cs`, `Machines/{SimulatedMachine, MachineCommand, MachineFleet}.cs`, `Mqtt/MachinePublisher.cs`, `ConsoleCommands/CommandLoop.cs`. **`machines.json` 3 máy** theo mã đã seed: site `VN-BN01` (spec ghi `HN1`), area `SMT-ASSY`, line `L1`, máy `L1-SMT-01`, `L1-REFLOW-01`, `L1-TEST-01`. M1 seed chỉ có 2 máy `DataSource = Mqtt`, nên `L1-REFLOW-01` (OPC UA) tạm phát qua MQTT cho đủ 3 máy theo DoD; M4b sẽ chuyển sang OPC UA server + gateway. Thêm `alarmCodes` theo máy (mã chọn ngẫu nhiên khi máy tự Down; lệnh `down` dùng `E999`). Mỗi máy: một MQTT client `sim-{machine}` có LWT `status = offline` (retained, QoS 1), mỗi lần (re)connect phát `online` + state retained; một vòng tick 100 ms (đọc lệnh từ `Channel`, hết chu kỳ thì làm 1 sản phẩm, hết giờ Down thì về Running) nên lệnh có hiệu lực ngay, không phải chờ hết chu kỳ 12 giây; một task heartbeat (`PeriodicTimer`, QoS 0, kèm state). Count: tối đa 1 msg/giây khi có thay đổi, ít nhất 30 giây một lần; gửi không được thì giữ `goodDelta/ngDelta` để gửi cùng lần sau. Publisher không ném lỗi khi mất broker (trả `false`); vòng nối lại dùng backoff 1-2-4…30 giây. `mute` giữ nguyên kết nối TCP (không phát LWT, để watchdog Bước 5 phát hiện); `unmute` gửi lại state + count. `dup` gửi message kế tiếp (state/count/alarm) hai lần cùng `msgId`. Ctrl+C: dừng vòng lặp, mỗi máy tự phát `offline` rồi ngắt kết nối gọn (ngắt gọn thì broker không phát LWT). Lệnh thêm theo bảng 2.8: `setup`, `stop`, `help`. **Sửa hợp đồng của Bước 1:** `MachineJson` thêm converter ghi `ts` dạng `...123Z` (mặc định của System.Text.Json là `+00:00`, không đúng câu "kết thúc bằng Z" của 2.3); đọc thì nhận mọi offset rồi đổi về UTC (thêm 2 assert/test). **Sự cố:** configuration binder nối mảng JSON vào mảng mặc định (`[30,180]` thành 4 phần tử), ghi ở `docs/incidents.md`. Kiểm tra (chạy thật với Mosquitto, `mosquitto_sub`): đủ 6 loại message `status`/`state`/`count`/`alarm`/`heartbeat` đúng tần suất (heartbeat 5 giây, count mỗi chu kỳ 8/12 giây); subscriber mới nhận ngay `state` + `status` có cờ retained; `down L1-SMT-01 60` → state `Down` + alarm `E999`, 60 giây sau alarm `null` + `Running`; `dup` → hai message cùng `msgId`; `skew 120` → `ts` lệch 2 phút; `mute` 26 giây không có message nào của máy, `unmute` có ngay state + count; `reset` → counter 0; `ng 2` → 2 lần `ngDelta = 1`; **tắt Mosquitto 25 giây:** trước đó counter `L1-TEST-01` = 19, message đầu tiên sau khi nối lại counter = 22, `goodDelta` = 3 (không mất số), simulator không crash; kill process (`taskkill /F`) → broker phát `offline` retained cho cả 3 máy (LWT); Ctrl+C (`CTRL_C_EVENT`) → thoát mã 0, `status = offline`. Simulator không có unit test riêng (UnitTests chỉ được tham chiếu Domain/Application/Client.Core); integration test simulator + ingest ở Bước 9.

- [x] **Bước 3 — `MqttIngestService`: nhận, validate, chống trùng** (2 giờ)
  - Mục tiêu: nhận đúng, bỏ đúng.
  - Việc cần làm: `BackgroundService` + MQTTnet (2.2); handler theo 2.4; tra `MqttTopicKey` → `MachineId` (cache master data); từ chối topic lạ, JSON hỏng, `machine` không khớp topic, `state = Offline` từ máy; cache `msgId`; **chưa ghi DB**, chỉ log `Debug` những gì nhận được và bị bỏ.
  - File: `src/MiniMes.Infrastructure/Machines/{MqttIngestService.cs, MachineDirectory.cs, RecentIdCache.cs}`; unit test cho `RecentIdCache`.
  - Kiểm tra: chạy simulator, log API in từng message; lệnh `dup` cho ra đúng một log "xử lý" và một log "trùng"; publish tay JSON hỏng hoặc topic 4 cấp: có cảnh báo, API không crash.
  - **Ghi chú khi làm (17/10/2026).** **Đổi chỗ file:** `RecentIdCache` và các luật kiểm tra message (`MachineMessageReader`, hàm thuần `(topic, payload)` → input hợp lệ hoặc lý do bỏ) đặt ở `Application/Machines` thay vì Infrastructure, vì UnitTests chỉ được tham chiếu Domain/Application/Client.Core (CLAUDE.md) mà spec yêu cầu unit test. Infrastructure chỉ giữ phần MQTT và DB: `Machines/{MqttIngestService, MachineDirectory, MqttIngestOptions}.cs`; thêm package `MQTTnet` 5.2.0 và `Microsoft.Extensions.Hosting.Abstractions` (chứa `BackgroundService`) vào Infrastructure. Cấu hình `Mqtt` (`Host`, `Port`, `ClientId = minimes-ingest`, `Enabled`); `ApiFactory` đặt `Mqtt__Enabled=false` (Testing không có broker, Bước 9 dùng container Mosquitto riêng). Ingest: vòng giữ kết nối backoff 1→30 giây, `CleanSession(false)`, subscribe lại **mỗi lần connect** 2 filter (`AllMachineKinds`, `AllProcessTags`, QoS 1); handler không bao giờ ném lỗi ra ngoài. `MachineDirectory`: cache `FrozenDictionary` theo **cả đường dẫn** `(site, area, line, MqttTopicKey)` (máy đã chuyển line mà vẫn gửi topic cũ thì bị bỏ, không xếp nhầm), phân biệt hoa thường như MQTT, chỉ máy `IsActive`; không tìm thấy thì nạp lại từ DB, nhưng tối đa 30 giây một lần (máy mới thêm ở M1 tự xuất hiện; topic rác không làm DB quá tải). Luật bỏ message: topic sai cấu trúc / cấp rỗng, kind lạ, `status` không phải `online`/`offline`, JSON hỏng (kể cả enum lạ như `"RUN"`), thiếu `msgId`, `machine` khác topic, `state`/`heartbeat` thiếu state hoặc báo `Offline`, `count` thiếu hay âm `counter`, `process` sai tag hoặc thiếu `value`. Bị bỏ → log `Warning` kèm lý do; nhận / trùng → `Debug`. `status` và `process` mới được log; xử lý ở Bước 5 và 4b. Kiểm tra: unit `RecentIdCacheTests` 3 (trùng trong TTL, quá TTL thì quên và được dọn, 16 luồng cùng một id chỉ 1 qua), `MachineMessageReaderTests` 24; integration `MachineDirectoryTests` 5 (máy seed tìm được theo đúng đường dẫn; sai line, sai site, sai hoa thường, máy lạ → không tìm thấy). Chạy thật API (Development) + simulator: lúc kết nối, ingest nhận ngay `state`/`status` retained của 3 máy; `dup L1-TEST-01` → đúng một log `processed` và một log `duplicate` cùng `msgId`; publish tay JSON hỏng, `state = Offline`, `machine` khác topic, máy lạ ở `L2`, topic có cấp rỗng, kind `temperature`, `status = rebooting` → mỗi cái một cảnh báo kèm lý do, API vẫn chạy. Topic thiếu cấp (5 cấp, không có `area`) **không tới được ingest** vì không khớp filter subscribe (broker lọc trước), nên không có log; luật topic trong reader là lưới an toàn cho trường hợp khớp wildcard như cấp rỗng.

- [x] **Bước 4 — Đường nóng, batch writer, gom state** (2,5 giờ)
  - Mục tiêu: dữ liệu vào DB đúng và đủ.
  - Việc cần làm: `MachineRuntimeStore` (delta, phát hiện đổi state, `LastSeenAt`, nạp lại từ DB khi khởi động); `Channel` bounded; `MachineEventBatchWriter` (batch 200 dòng/1 giây, transaction, retry, nhánh trùng `MsgId`, xả khi dừng); `CloseAndOpenAsync` đóng/mở `MachineStateHistory`; không ghi heartbeat và count không đổi.
  - File: `src/MiniMes.Infrastructure/Machines/{MachineRuntimeStore.cs, IngestItem.cs, MachineEventBatchWriter.cs, MachineStateHistoryWriter.cs}`.
  - Kiểm tra: chạy 5 phút, `SELECT SUM(Delta) FROM MachineEvent WHERE MachineId=@m` bằng đúng (counter cuối − counter đầu) mà simulator in ra; lệnh `reset`: có một dòng `IsReset = 1` và tổng vẫn đúng; đổi state vài lần: mỗi máy đúng một đoạn `EndAt IS NULL`, các đoạn liền nhau không chồng; `docker stop` SQL Server 20 giây rồi bật lại: batch được ghi bù, không mất.
  - **Ghi chú khi làm (17/10/2026).** **Đổi chỗ file (như Bước 3):** `MachineRuntimeStore` cùng `IngestItem`, `StateChange`, `IngestResult` đặt ở `Application/Machines/MachineRuntimeStore.cs` (logic thuần, có unit test). Infrastructure: `Machines/{MachineEventBatchWriter, MachineStateHistoryWriter, MachineRuntimeLoader}.cs`. Store: delta theo `CounterMath`; bỏ count có `ts` cũ hơn count đã xử lý; bỏ count `quality = Bad`; mốc thời gian dùng `ts` của máy khi lệch ≤ 60 giây, quá thì dùng giờ nhận (ingest log cảnh báo **một lần** khi máy bắt đầu lệch và một lần khi hết lệch, không log mỗi message); state cũ hơn đoạn đang mở chỉ ghi event; `LastSeenAt` = giờ nhận. **Không ghi:** heartbeat, count không đổi, và **state trùng state hiện tại** (thêm so với spec: state retained được giao lại mỗi lần reconnect; ghi nó chỉ thêm dòng, và sau khi restart API thì đụng unique `MsgId`). Heartbeat báo state khác (message state bị mất) được xử lý như message state. `MachineRuntimeLoader` nạp đoạn đang mở và counter cuối của từng máy khi ingest khởi động (DB chết thì thử lại). Channel bounded 10.000, `Wait`, `SingleReader`. Writer: batch 200 dòng / cửa sổ 1 giây, một transaction; lỗi DB thì giữ batch và thử lại (2, 4, 6… tối đa 30 giây); trùng `MsgId` (`ConflictException` từ `AppDbContext`) thì ghi từng dòng và bỏ đúng dòng trùng; reset `Id` trước khi ghi lại (lần lỗi trước có thể đã gán identity). `CloseAndOpenAsync`: idempotent (state đã mở thì bỏ qua; cũ hơn đoạn mở thì bỏ qua); `SaveChanges` lần đóng trước rồi mới insert đoạn mới (filtered unique index). **Sự cố (ghi ở `docs/incidents.md`):** đoạn xả hàng đợi đặt ở cuối `ExecuteAsync` như snippet 2.4 không chạy được, vì từ .NET 10 `ExecuteAsync` chạy trên thread nền, start rồi stop ngay thì 19/20 lần nó chưa kịp chạy. Đã chuyển sang override `StopAsync`. Đăng ký writer **trước** ingest (host dừng theo thứ tự ngược: ingest dừng trước, writer xả sau). Kiểm tra: unit `MachineRuntimeStoreTests` 13 (baseline, delta, count không đổi, reset, count đến trễ không dời mốc, `Bad`, seed sau restart, đổi state / trùng state, heartbeat, state cũ hơn đoạn mở, lệch giờ ±60/61/3600 giây, process không đi đường event); integration `MachineEventWriterTests` 5 (đoạn liền nhau, đúng một đoạn mở, `SUM(Delta)` = counter cuối − đầu; trùng `MsgId` chỉ bỏ dòng đó; `CloseAndOpen` idempotent; dừng là xả hết 250 dòng; loader nạp lại state + counter), chạy lặp 5 lần đều xanh. **Chạy thật 5 phút** (API Development + simulator 3 máy, có `down 40`, `dup`, `reset`, `setup`/`up`, `docker stop mes-sqlserver` 20 giây): sau khi cho 3 máy Down để chốt số, `SUM(Delta)` = số sản phẩm simulator báo: `L1-SMT-01` 23 = 15023 − 15000; `L1-REFLOW-01` 27 = 8 (trước reset) + 19, có đúng 1 dòng `IsReset = 1`; `L1-TEST-01` 37 (lệnh `dup` không cộng đôi); 0 `MsgId` trùng; mỗi máy 1 đoạn mở, 0 chỗ gãy chuỗi (`LAG(EndAt) = StartAt`); lúc SQL Server tắt writer log lỗi, thử lại, "written after 5 attempts", không mất dòng. **Restart API** (Ctrl+C, tắt 20 giây trong khi máy vẫn chạy): loader nạp 3 máy, count đầu tiên sau khi bật lại có `delta = 3` (không phải baseline 0), tổng vẫn khớp simulator (29 / 33 / 47).

- [x] **Bước 4b — [v2] Tag thông số quy trình** (1,5 giờ)
  - Mục tiêu: nhận, lưu và hiển thị giá trị `process/{tag}`.
  - Việc cần làm: ingest nhánh `process` theo 2.3b (tra `MachineTag`, kiểm tra khoảng plausible, `Quality`); `MachineRuntimeStore` giữ giá trị mới nhất mỗi tag; batch writer ghi `ProcessValue`; job dọn dữ liệu > 30 ngày (xóa theo lô); seed `MachineTag` `L1-REFLOW-01/peakTemp` (°C, plausible 0–400). Để test trước khi có gateway M4b: simulator MQTT có lệnh `tag L1-REFLOW-01 peakTemp 245.3`, hoặc `mosquitto_pub` tay.
  - File: `src/MiniMes.Domain/Machines/MachineTag.cs`, `src/MiniMes.Infrastructure/Machines/{ProcessValueWriter.cs, ProcessValueRetentionJob.cs}`.
  - Kiểm tra: publish 10 giá trị → 10 dòng `ProcessValue`; publish tag không có trong master → bị bỏ, có log; publish `value = -32768` → lưu `Quality = Bad`; publish trùng `msgId` → 1 dòng.
  - **Ghi chú khi làm (17/10/2026).** `MachineTag.Judge` (Domain) đã có từ Bước 1. **Khác danh sách file:** không tách `ProcessValueWriter`; `ProcessValue` đi chung channel và batch với `MachineEvent` (`IngestItem` thêm trường `ProcessValue`), nên dùng chung một transaction, một logic thử lại, một nhánh trùng `MsgId`. `MachineDirectory` cache thêm `MachineTag` đang active theo `(MachineId, TagName)` (phân biệt hoa thường như topic, nạp lại cùng lúc với máy, tối đa 30 giây một lần). `MachineRuntimeStore.ApplyProcess`: chất lượng = `tag.Judge(value, quality máy báo)`; ngoài khoảng plausible thì `Bad` kèm cảnh báo (máy tự báo `Bad` thì không cảnh báo); giữ giá trị mới nhất của từng tag (`MachineRuntimeSnapshot.Process`, dùng cho dashboard Bước 6–8); giá trị đến trễ vẫn được lưu nhưng không thay giá trị mới nhất; `SourceTs` theo cùng luật lệch giờ 60 giây. Ingest: tag không có trong master → bỏ + `Warning`. Seed `L1-REFLOW-01/peakTemp` (°C, 0–400) trong `MasterDataSeeder`. `ProcessValueRetentionJob` (mỗi giờ): `DELETE TOP (5000)` lặp tới khi hết, theo **`ReceivedAt`** chứ không theo `SourceTs` (gateway lệch giờ không làm mất dữ liệu mới; `ReceivedAt` tăng cùng khóa cụm `Id` nên dòng cũ nằm đầu bảng, không cần thêm index). Simulator thêm lệnh `tag <m> <tag> <value>`. **Ghim `System.Security.Cryptography.Pkcs` 10.0.13** trong Infrastructure (bản cũ 9.0.11 do `Microsoft.Data.SqlClient` 6.1.6 kéo vào có CVE-2026-84008 / CVE-2026-70162, cảnh báo NU1902/NU1903). Kiểm tra: unit 4 (lưu + giá trị mới nhất, −32768 → `Bad` kèm ghi chú, máy báo `Bad` giữ `Bad`, giá trị trễ không thay giá trị mới nhất); integration `ProcessValueTests` 3 (tag seed tìm được, tag lạ / sai hoa thường thì không; 10 giá trị → 10 dòng `Good`, −32768 → `Bad`, cùng `msgId` ghi lại → vẫn 1 dòng; dọn 12.000 dòng cũ hơn 30 ngày bằng 3 lệnh, dòng mới hơn còn nguyên). Chạy thật API + simulator: 10 lệnh `tag` → 10 dòng; `tag … zone9Temp` và `tag L1-SMT-01 peakTemp` → bỏ kèm cảnh báo; `-32768` → `Bad` + cảnh báo; `dup` → log `duplicate`, 1 dòng; tổng 12 dòng, 12 `MsgId` khác nhau.

- [x] **Bước 5 — Reconnect, Offline watchdog, LWT** (2 giờ)
  - Mục tiêu: chịu được mất kết nối và phân biệt `Offline` với `Down`.
  - Việc cần làm: vòng giữ kết nối có backoff (đã có ở bước 3, kiểm tra kỹ); `OfflineWatchdog` với ân hạn (2.5) và `TimeProvider`; thêm LWT + `status` retained ở simulator; ingest xử lý `status`; validate rằng heartbeat từ máy `Offline` đưa máy về state thật.
  - File: `src/MiniMes.Infrastructure/Machines/OfflineWatchdog.cs`, `MachineStateService.cs`; sửa `MachinePublisher.cs`.
  - Kiểm tra: `mute L1-TEST-01` → sau 30 giây (± nhịp watchdog) máy sang `Offline`; `unmute` → về `Running`; kill simulator không sạch → `Offline` gần như ngay nhờ LWT; `docker compose stop mosquitto` 2 phút → **không** có máy nào bị ghi `Offline` lúc broker còn chết (ân hạn), sau khi bật lại máy về đúng state; unit test watchdog bằng `FakeTimeProvider` (gói `Microsoft.Extensions.TimeProvider.Testing`).
  - **Ghi chú khi làm (17/10/2026).** Luật Offline là hàm thuần `Application/Machines/OfflineDetector.cs` (để unit test bằng `FakeTimeProvider`); Infrastructure: `OfflineWatchdog` (chỉ tick), `MachineStateService` (Offline/online do server suy ra: store rồi channel, Bước 6 thêm SignalR ở đây), `MqttConnectionState` (ingest ghi lúc connect/disconnect). Store thêm `MarkOffline`, `MarkOnline` (event `Connection`, đoạn `Offline`) và `ReportOffline`; rời `Offline` thì dùng giờ server (đoạn Offline mở theo đồng hồ server, PLC chậm vài giây sẽ bị coi là "cũ hơn đoạn mở" và kẹt Offline). Luật: ingest không kết nối → không kết luận gì; im lặng tính từ mốc muộn hơn giữa `LastSeenAt` và lúc ingest kết nối, quá 30 giây → Offline (máy chưa nghe thấy từ lúc khởi động cũng bị tính sau 30 giây). **Status:** `online` → event + `LastSeenAt`; `offline` **retained** (phát lại mỗi lần subscribe, có thể cũ) → bỏ qua, để watchdog quyết; `offline` **live** (LWT) → chỉ là **báo cáo**, xác nhận sau 3 giây nếu ingest giữ **cùng phiên kết nối** từ trước lúc báo và máy không gửi gì thêm. **Sự cố (ghi ở `docs/incidents.md`):** bản đầu áp dụng LWT ngay; tắt Mosquitto thì broker phát LWT của mọi client trước khi ngắt ingest, nên 2/3 máy bị Offline oan. **Khác spec:** watchdog tick mỗi **1 giây** (snippet 5 giây) để LWT đã xác nhận hiện trong khoảng 3–4 giây; backoff tối đa của simulator giảm còn **10 giây** (máy phải kịp nối lại trong 30 giây ân hạn sau khi broker trở lại; ingest giữ tối đa 30 giây). LWT + `status` retained của simulator đã có từ Bước 2. Kiểm tra: unit `OfflineDetectorTests` 10 (đúng 30 giây chưa, 31 giây thì Offline; heartbeat giữ online; không kết nối thì không kết luận; sau reconnect có đủ 30 giây; máy chưa nghe thấy từ lúc khởi động; không báo lại máy đã Offline; heartbeat đưa về state thật theo giờ server dù PLC chậm 20 giây; LWT được xác nhận khi kết nối còn; LWT lúc broker shutdown không tính; LWT bị bỏ khi máy nói lại); integration 1 (Running → Offline → Running thành 3 đoạn liền nhau, event `Connection` offline/online). **Chạy thật** (API + simulator): `mute L1-TEST-01` 22:04:24 → Offline 22:04:55, `unmute` → Running ngay; kill simulator 22:05:14 → LWT báo 22:05:15, xác nhận Offline 22:05:18; `docker compose stop mosquitto` 2 phút → 2 LWT tới nhưng **không máy nào bị Offline**, cả lúc broker chết lẫn sau khi bật lại (máy nối lại trong 1–2 giây sau ingest); `MachineStateHistory` chỉ có đoạn Offline của `mute` và của lúc kill.

- [x] **Bước 6 — SignalR hub, notifier, throttle** (2 giờ)
  - Mục tiêu: đẩy realtime lên client.
  - Việc cần làm: `IMachineClient`, DTO push trong Contracts; `MachineHub` + `SignalRRealtimeNotifier`; `MapHub`; token qua query cho `/hubs` (M0 có JWT, chỉ thêm `OnMessageReceived`); `RealtimePublisher` throttle 1 giây; đổi state đẩy ngay; REST `GET /api/lines/{code}/status` làm snapshot.
  - File: `src/MiniMes.Contracts/Realtime/*.cs`, `src/MiniMes.Application/Realtime/IRealtimeNotifier.cs`, `src/MiniMes.Infrastructure/Realtime/{MachineHub.cs, SignalRRealtimeNotifier.cs}`, `.../Machines/RealtimePublisher.cs`.
  - Kiểm tra: dùng một console nhỏ (hoặc test) nối `HubConnection`, `JoinLine("L1")`: nhận `MachineStateChanged` ngay khi gõ `down`; `MachineCounterUpdated` không quá 1 lần/giây/máy dù máy gửi nhanh hơn (chỉnh `cycleTimeSec` xuống 0,2 để thử); client không join group L1 thì không nhận gì từ L1.
  - **Ghi chú khi làm (17/10/2026).** Contracts `Realtime/RealtimeContracts.cs`: `IMachineClient` (`MachineStateChanged`, `MachineCounterUpdated`, **[v2]** `MachineProcessUpdated`; `AndonChanged` thêm ở Bước 7 cùng `AndonDto`), `RealtimeHub` (đường dẫn `/hubs/machines`, tên method, group `line:{code}`), DTO push và snapshot `LineStatusDto`/`MachineStatusDto` (state, counter, `LastSeenAt`, giá trị tag mới nhất, `DataSource`). Mọi DTO mang **giá trị tuyệt đối** (tổng đếm, state), không mang "+n". Application: `Realtime/IRealtimeNotifier.cs`; `Machines/LineStatus.cs` (`GetLineStatusQuery`: master data từ DB + giá trị sống từ `MachineRuntimeStore`; line tìm theo code vì MVP có một area); store thêm cờ dirty + `TakeDirty()` (counter và tag mới nhất, lấy xong thì xóa cờ). Infrastructure: `Realtime/{MachineHub, SignalRRealtimeNotifier}.cs` (push lỗi thì log và nuốt, không bao giờ làm dừng ingest), `Machines/RealtimePublisher.cs` (mỗi giây đẩy phần dirty); `FrameworkReference Microsoft.AspNetCore.App` cho Infrastructure, nên gỡ 2 package đã có sẵn trong framework (`Hosting.Abstractions`, `Identity.Core`; cảnh báo NU1510). Đổi state đẩy **ngay** (`MachineStateService.PushStateChangeAsync`, gọi từ ingest trước khi ghi DB và từ Offline). Api: `MapHub`, `OnMessageReceived` đọc `?access_token=` chỉ trên `/hubs`; `GET /api/lines/{code}/status` (mọi user đã đăng nhập). **Ghi nhận:** client .NET (WPF) gửi token bằng **header** cả lúc bắt tay WebSocket; `?access_token=` là đường của trình duyệt (WebSocket của trình duyệt không đặt được header), có test riêng (mutation: bỏ `ctx.Token = token` thì đúng test đó đỏ). **Bảo mật:** `Microsoft.AspNetCore.SignalR.Common` 10.0.12 có CVE-2026-70159 (DoS khi bật *stateful reconnect*); test dùng `SignalR.Client` **10.0.13**; server chạy shared framework ASP.NET Core 10.0.12 đã cài trên máy, không bật stateful reconnect (mặc định tắt) nên không bị ảnh hưởng, nhưng nên cập nhật runtime lên 10.0.13. Kiểm tra: unit 3 (10 count giữa hai lần lấy → 1 snapshot với giá trị cuối, lấy lần hai rỗng, count không đổi không đánh dấu; tag; đổi state không nằm trong phần throttle); integration `RealtimeTests` 5 với `HubConnection` thật qua WebSocket (không token → 401; token qua query được nhận; đổi state tới group L1, group L2 không nhận; 10 count → đúng 1 push mang giá trị cuối; snapshot REST có máy của line, `DataSource`, tag mới nhất, line lạ 404, chưa đăng nhập 401). **Chạy thật** (API + simulator `cycleTimeSec = 0.2` + console client `JoinLine`): `mosquitto_pub` bắn 100 count của `L1-SMT-01` trong khoảng 10 giây → ingest xử lý đủ 100, client nhận 11 push (tối đa 1/giây/máy, push cuối = 20099, không mất số); hai máy chu kỳ 0,2 giây cũng tối đa 1 push/giây; `down L1-TEST-01 5` → client nhận `Down` ở 22:27:44.114, cùng giây simulator đổi state, và `Running` sau 5 giây; client chỉ join L2 không nhận event nào.

- [x] **Bước 7 — Andon backend** (2,5 giờ)
  - Mục tiêu: quy trình raise → acknowledge → resolve chạy trên API.
  - Việc cần làm: `AndonCall` (2.9) + unit test mọi chuyển trạng thái (nhận hai lần, resolve khi đã đóng, resolve thiếu lý do); command/handler `Raise`, `Acknowledge`, `Resolve` (MediatR + FluentValidation như M1); role theo mục 2.9; `AndonWatcherService` (X cấu hình, mặc định 60 giây); push `AndonChanged` sau mỗi thay đổi; xử lý va chạm unique index và `DbUpdateConcurrencyException` thành 409.
  - File: `src/MiniMes.Domain/Andon/AndonCall.cs`, `src/MiniMes.Application/Andon/*`, `src/MiniMes.Api/Controllers/AndonController.cs`, `.../Machines/AndonWatcherService.cs`.
  - Kiểm tra: `down L1-SMT-01 300`, sau X giây có đúng **một** Andon `Raised`, `RaisedBy = system`; Operator bấm tay khi đã có Andon mở nhận lại bản đang mở, không tạo bản thứ hai; hai request `acknowledge` song song: một 200, một 409; Operator gọi `acknowledge` bị 403; resolve không kèm `downtimeReasonId` bị 400.
  - **Ghi chú khi làm (27/10/2026).** Domain `AndonCall`: `Acknowledge`, `Resolve(by, reasonId, note, now)` (resolve thẳng từ `Raised` tự điền `AckBy/AckAt`; lưu `ResolvedBy`, `ResolutionNote` đã có từ Bước 1), **`Cancel`** (báo nhầm, mục 2.9 có trạng thái `Cancelled`; không cần lý do; endpoint `POST /api/andon/{id}/cancel`). Contracts `Andon/AndonContracts.cs` (`AndonDto` mang mã máy, mã line, tên lý do, `RowVersion` base64; `RaiseAndonResult(Andon, Created)`); `IMachineClient.AndonChanged` + `IRealtimeNotifier.AndonChangedAsync` (push ngay tới `line:{code}`, không throttle). Application `Andon/AndonUseCases.cs` (Raise, Acknowledge, Resolve, Cancel, danh sách `?status=open|<tên>&line=`, tối đa 200 dòng mới nhất; xem theo id) và `Andon/AndonAutoRaise.cs` (hàm thuần: máy `Down` ≥ X; Offline/PlannedStop/Setup/Idle không tính). **Khác spec / bổ sung:** (1) acknowledge/resolve/cancel nhận **`RowVersion` client đã thấy** (như WO ở M2) và so **trước** khi gọi Domain: người thứ hai luôn nhận **409** bất kể thứ tự (không có thì ai đọc sau khi người kia commit sẽ nhận 422 "đã nhận"); (2) Raise trả **201** khi tạo, **200** khi đã có bản mở (kể cả khi thua đua ở unique index: bắt `ConflictException`, đọc lại bản thắng); (3) cờ `Auto` không có trong request HTTP, chỉ watcher đặt được `RaisedBy = system`; (4) watcher chỉ raise **một lần mỗi đợt Down**: bỏ qua nếu đã có Andon loại Machine đang mở **hoặc** `RaisedAt >= StateSince` (resolve khi máy vẫn Down không sinh Andon mới 5 giây sau; API restart không raise lại); (5) role: raise = Operator, Admin; ack/resolve/cancel = Supervisor, Maintenance, Admin; đọc = mọi user. `AndonWatcherService` (Infrastructure/Machines, 5 giây, `Andon:AutoRaiseAfterSeconds` = 60) gửi `RaiseAndonCommand(Auto: true)` qua MediatR trong scope riêng; chỉ đăng ký khi `Mqtt:Enabled`. Không có migration mới. Kiến thức của bước: [docs/learning/M4-step07-andon-backend.md](../docs/learning/M4-step07-andon-backend.md). Kiểm tra: unit `AndonCallTests` (ma trận 4 trạng thái × 3 hành động = 12 ô viết tay + 9 case: tạo mới, ack ghi người/giờ, ack hai lần giữ người đầu, resolve thẳng điền AckAt, thiếu lý do 0/−1, note quá dài, cancel), `AndonAutoRaiseTests` 10 (59/60 giây, các state khác Down, máy chưa nghe thấy); integration `AndonTests` 10 (luồng HTTP đủ, raise lại → 200 cùng id, loại khác → bản mới; **2 raise song song × 5 vòng → 1 bản** (đường unique index); **2 ack song song × 5 vòng → 200 + 409**; 403 Operator ack/resolve/cancel, Planner raise; 401; 400 thiếu/sai lý do, type lạ, rowVersion hỏng, status lạ; 404; 422 ack/resolve bản đã đóng; watcher: 30 giây chưa raise, 61 giây đúng 1 bản `system` qua 2 tick, operator bấm nhận lại bản đó, resolve khi vẫn Down không raise lại, đợt Down mới → bản thứ hai), `RealtimeTests` +1 (`AndonChanged` Raised rồi Cancelled tới L1, L2 không nhận). Toàn bộ: unit 634, integration 265 xanh. **Chạy thật** (API Development + simulator): `down L1-SMT-01 300` lúc 14:38:28 → Andon `system` lúc 14:39:33 (60 giây + nhịp 5 giây), đúng 1 bản; operator `POST /api/andon` → 200, cùng id; operator acknowledge → 403; resolve thiếu lý do → 400; hai `acknowledge` song song bằng curl → 200 và 409; 14:41:18 máy về `Running`, Andon **vẫn** `Acknowledged`; resolve `MCH-FAULT` → 200 `Resolved`; DB: đúng 1 Andon cho `L1-SMT-01`. **Ghi nhận (chưa sửa, ngoài phạm vi bước):** `DateTime` đọc từ DB có `Kind = Unspecified`, nên JSON không có hậu tố `Z` (`raisedAt`, cũng như `plannedStart` của WO từ M2); client phải hiểu là UTC.

- [x] **Bước 7b — [v2] Andon leo thang** (1,5 giờ)
  - Mục tiêu: Andon không ai nhận thì tự báo cấp trên theo bảng quy tắc.
  - Việc cần làm: `AndonEscalationRule` + seed (2.9b); `AndonCall.Escalate`; mở rộng `AndonWatcherService`; client SignalR join group `role:{role}` lúc kết nối; event `AndonEscalated`; báo cáo đơn giản `GET /api/andon/stats?from=&to=` (số Andon theo level cuối, thời gian phản ứng trung bình).
  - File: `src/MiniMes.Domain/Andon/{AndonCall.cs, AndonEscalationRule.cs}`, `.../Machines/AndonWatcherService.cs`, `tests/MiniMes.UnitTests/Andon/EscalationTests.cs`.
  - Kiểm tra: unit test với `FakeTimeProvider`: raise → +2 phút level 0 → +3 phút level 1 → +10 phút level 2; Ack ở phút 4 → không lên level 2; gọi `Escalate` hai lần cùng level → lần hai trả `false`. Thử tay: `down L1-SMT-01 900`, không ai ack → sau 3 phút user Admin thấy thông báo leo thang.
  - **Ghi chú khi làm (27/10/2026).** **User chốt trước khi code** (spec chưa nói rõ): (a) stats bỏ Andon `Cancelled`, lọc `RaisedAt` trong `[from, to)` UTC, trả `Total`, `ByLevel` (level cuối), `Acknowledged`, `AvgReactionSeconds` = trung bình `AckAt − RaisedAt` của Andon đã nhận; role Supervisor, Maintenance, Admin; (b) **Level 0 không push `AndonEscalated`**: raise đã đẩy `AndonChanged` tới line (hành vi Bước 7); chỉ level ≥ 1 gửi tới role. Domain: `AndonEscalationRule` tách sang `Andon/AndonEscalationRule.cs` (đúng danh sách file) cùng hàm thuần `AndonEscalation.DueLevel(rules, type, elapsed)` (level lớn nhất có `AfterMinutes ≤ elapsed`, không khớp → 0); `AndonCall.Escalate(toLevel, now)` như snippet (chỉ tăng, chỉ khi `Raised`, cùng level → `false`; level giữ lại sau Ack/Resolve cho KPI). Application `Andon/AndonEscalations.cs`: `EscalateAndonsCommand` (MediatR, gọi từ `AndonWatcherService` mỗi tick sau auto-raise) **lưu từng Andon một**: `ConflictException` (supervisor vừa Ack, RowVersion) → detach Andon đó, log Information, không ghi đè, không chặn các Andon khác; sau khi lưu: log Warning (level 1) / **Error** (level ≥ 2), push `AndonEscalated` tới `role:{NotifyRole}` của quy tắc và `AndonChanged` tới line (dashboard hiện level); `GetAndonStatsQuery` (đọc 3 cột rồi tính trong C#, khoảng ≤ 366 ngày). Tick trễ (API restart) nhảy thẳng lên level đến hạn, chỉ báo role của level đó, không phát lại level dưới. Contracts: `IMachineClient.AndonEscalated`, `RealtimeHub.RoleGroup`, `AndonStatsDto`. `MachineHub.OnConnectedAsync`: **server** cho connection vào `role:{role}` theo claim `role` của JWT (client không tự join được role khác; reconnect là connection mới nên tự join lại). Seed 4 quy tắc của bảng 2.9b trong `MasterDataSeeder` (`NotifyRoles` "Supervisor,Maintenance"). Không migration. Kiến thức: [docs/learning/M4-step07b-andon-escalation.md](../docs/learning/M4-step07b-andon-escalation.md). Kiểm tra: unit `EscalationTests` 17 (`FakeTimeProvider`: +2 phút level 0, 3:00 level 1, 9:59 vẫn 1, 10:00 level 2; Ack phút 4 → không lên 2; `Escalate` cùng level hai lần → `false`; không giảm; tick trễ 15 phút → thẳng level 2; Acknowledged/Resolved/Cancelled không leo; `DueLevel` 8 dòng theo loại; tách role); integration `AndonTests` +4 (seed đúng 4 quy tắc; command trên DB thật: 2/4/11 phút → 0/1/2, Quality 6 phút → 1, Material 60 phút → 0, đã ack → 0, chạy lần hai không đổi; stats số liệu tính tay 4 Andon, level 0:2/1:1/2:1, 3 đã nhận, trung bình 340 giây, bỏ Cancelled, `to` không tính; 403 Operator, 400 khoảng ngược / quá 366 ngày / thiếu tham số), `RealtimeTests` +1 (level 1 Machine → `admin` nhận, `sup01`, `qc01` không, không cần `JoinLine`). Toàn bộ: unit 651, integration 270 xanh. **Chạy thật** (API + simulator + 2 client SignalR `admin`, `sup01`): `down L1-SMT-01 900` lúc 14:59:49 → Andon #2 `system` 15:00:50 → **15:03:55 `admin` nhận `AndonEscalated` level 1**, log Warning "not acknowledged after 3 min … notifying Admin"; `sup01` chỉ nhận `AndonChanged` level 0 rồi 1; resolve sau đó giữ `EscalationLevel = 1`; `GET /api/andon/stats` ngày 27/10 → total 2, level 0:1 / 1:1, trung bình 22,6 giây. Level 2 (10 phút) chỉ kiểm bằng test, không chờ tay.

- [x] **Bước 8 — WPF: dashboard line, Andon, resync** (3,5 giờ)
  - Mục tiêu: người dùng thấy và thao tác được.
  - Việc cần làm: `RealtimeClient` (2.6) trong Client.Core, đăng ký DI cùng `IUiDispatcher`; BackOffice: `LineDashboardView` — ô màu theo state (Running xanh, Idle vàng nhạt, Down đỏ, Setup xanh dương, PlannedStop xám đậm, Offline xám nhạt), số đếm giờ hiện tại, **[v2]** giá trị tag process mới nhất trên ô máy (ví dụ "Peak 245.3 °C", xám khi `Quality = Bad`) và nhãn nguồn (`MQTT`/`OPC UA`/`Modbus` theo `Machine.DataSource`), danh sách Andon mở với nút Acknowledge/Resolve (ComboBox lý do) **[v2]** và cột Level (Level ≥ 1 nền đỏ nhấp nháy); Kiosk: nút Andon to (chọn loại) + banner trạng thái máy của trạm; banner "Reconnecting..." khi `ConnectionChanged(false)`; sau reconnect tự tải lại snapshot. Chuỗi UI đặt trong `Strings.resx`.
  - File: `src/MiniMes.Client.Core/Realtime/{RealtimeClient.cs, IUiDispatcher.cs}`, `src/MiniMes.BackOffice.Wpf/{Views/LineDashboardView.xaml, ViewModels/LineDashboardViewModel.cs, ViewModels/MachineTileViewModel.cs}`, `src/MiniMes.Kiosk.Wpf/Views/AndonPanel.xaml`.
  - Kiểm tra: gõ `down` trong simulator, ô đỏ trong < 2 giây (đo bằng đồng hồ bấm giờ hoặc log giờ); tắt API 20 giây rồi bật lại: banner hiện rồi mất, tile đúng state hiện tại (không phải state cũ); mở dashboard trước, chạy simulator sau, và ngược lại: cả hai đều đúng; UI không giật khi để `cycleTimeSec = 0,2` trên 8 máy.
  - **Ghi chú khi làm (27/10/2026).** **User chốt trước khi code** (spec chưa nói rõ): (a) **"số đếm giờ hiện tại" do server tính**: `MachineRuntimeStore` giữ `HourStart`/`HourCount` (cộng `Delta`, nên reset giữa giờ không làm sai), giờ theo **giờ nhà máy** (`PlantTimeZone.HourStartUtc`, test cả Ấn Độ +5:30); `TakeDirty(now)` sang giờ mới thì đưa máy đang giữ số giờ cũ về 0 và đánh dấu dirty (đẩy 0 một lần); `MachineRuntimeLoader` nạp `SUM(Delta)` của giờ hiện tại khi khởi động; **thêm trường `CountThisHour`** vào `MachineCounterDto` và `MachineStatusDto` (sửa hợp đồng Bước 6); (b) **Kiosk: operator chọn máy** của work center của trạm (1 máy thì chọn sẵn, nhiều máy thì chọn, không có máy thì không có Andon). **Khác danh sách file:** ViewModel ở `Client.Core/ViewModels/{Machines,Kiosk}` (CLAUDE.md mục 4: ViewModel dùng chung, unit test trên Linux), không ở `BackOffice.Wpf/ViewModels`. Thêm gói `Microsoft.AspNetCore.SignalR.Client` 10.0.13 vào Client.Core (spec 2.6 chỉ định; cùng bản IntegrationTests). Client.Core: `Realtime/IUiDispatcher.cs`, `Realtime/RealtimeClient.cs` (`IRealtimeClient`: event C#, `On<T>` đăng ký trong constructor (trước `StartAsync`), tên event lấy từ `nameof(IMachineClient.X)`; `WithAutomaticReconnect` với `IRetryPolicy` **thử mãi** (0, 2, 5 rồi 10 giây; mặc định bỏ cuộc sau 30 giây); vòng thử lại cho **lần kết nối đầu** (automatic reconnect không áp dụng); mỗi lần (re)connect: join lại các line rồi bắn `Resync`; `IRealtimeClientFactory` thay vì đăng ký DI Transient, vì container giữ mọi Transient `IAsyncDisposable` tạo từ root tới khi app tắt), `Api/MachineApi.cs`, `ViewModels/Machines/{LineDashboardViewModel, MachineTileViewModel, AndonRowViewModel}.cs`, `ViewModels/Kiosk/AndonPanelViewModel.cs` (gắn vào `ScanViewModel.Andon`). **`IClosable`** (`Navigation/IPageNavigator.cs`): `PageNavigator` và `Navigation` của Kiosk gọi `CloseAsync` khi rời trang/logout để đóng kết nối (không dùng `IDisposable` cho ViewModel vì lý do container như trên). Push đến muộn không làm màn hình đi lùi: state so `Since`, counter so `LastSeenAt` (counter có thể reset), tag so `SourceTs`, Andon so **`RowVersion`** (8 byte big-endian → `ulong`), dashboard nhớ version của cả Andon đã đóng. 409 khi Acknowledge/Resolve: hiện thông báo và tải lại, **giữ** thông báo (unit test bắt được bản đầu xóa mất). Nút Acknowledge/Resolve chỉ hiện với Supervisor/Maintenance/Admin (UX; API vẫn trả 403). WPF: `WpfUiDispatcher` (`Dispatcher.InvokeAsync`) ở mỗi app; `Views/Machines/LineDashboardView.xaml` (menu "Line dashboard" đứng đầu; màu ô bằng `DataTrigger` + `TextElement.Foreground`; dòng Level ≥ 1 nền đỏ nhấp nháy bằng animation `Opacity` vì brush trong Style bị freeze; cột lý do là ComboBox); Kiosk `Views/AndonPanel.xaml` (chip trạng thái máy + Andon đang mở, nút ANDON) + overlay chọn máy/loại trong `ScanView` (mọi nút `Focusable=False`, chọn máy bằng nút chứ không dùng ListBox, để ô scan giữ focus). Strings: `Menu_LineDashboard`, `Dashboard_*`, `Andon_*`, `Source_*`. Kiến thức: [docs/learning/M4-step08-wpf-dashboard.md](../docs/learning/M4-step08-wpf-dashboard.md). Kiểm tra: unit `LineDashboardViewModelTests` 17, `AndonPanelViewModelTests` 7, `MachineRuntimeStoreTests` +4 (đếm giờ qua reset, sang giờ, máy im lặng về 0 một lần, seed sau restart), `PlantTimeZoneTests` +5; integration `MachineEventWriterTests` +1 (loader nạp số giờ hiện tại, bỏ giờ trước, cộng qua reset), `RealtimeTests` snapshot có `CountThisHour`. Toàn bộ: unit 684, integration 271 xanh. Chưa có test tự động cho `RealtimeClient` với server thật (IntegrationTests không được tham chiếu Client.Core theo CLAUDE.md); kiểm bằng chạy thật. **Chạy thật** (API + simulator + BackOffice + Kiosk, điều khiển bằng UI Automation, chụp màn hình): `down L1-SMT-01` → ô đỏ sau **370 ms** (tính từ lúc gửi lệnh cho simulator, đo bằng UIA dò chữ "Down" mỗi 50 ms); Andon tự động hiện trong lưới, Acknowledge bằng nút → "Acknowledged by sup01", chọn lý do + Resolve → dòng biến mất; **tắt API 25 giây** (trong lúc tắt cho máy `up`): banner "Reconnecting" hiện, ô vẫn Down (dữ liệu cũ, đúng), bật API → banner mất, ô **Running** (state hiện tại), số giờ giữ nguyên (loader); **kill simulator** (dashboard mở trước) → 3 ô Offline sau 3,3 giây (LWT); chạy lại simulator `cycleTimeSec = 0.2` → ô đổi đúng **1 lần/giây** (10 lần/10 giây), truy vấn UIA trung bình 62 ms, tối đa 118 ms (UI không nghẽn); Kiosk `--StationCode L1-SMT-01`: chip 2 máy theo state, ANDON → "Which machine?" (máy chọn có viền) → Material → "Andon sent…", chip "Material Andon: Raised", ô scan vẫn giữ focus; supervisor acknowledge qua API → chip "Acknowledged by sup01" ngay. BackOffice đăng nhập `admin`: 3 phút sau Andon tự động của `L1-SMT-01` → banner "Andon escalated to level 1…" và dòng Level 1 đỏ nhấp nháy (lấy mẫu một điểm ảnh của dòng mỗi 150 ms: màu dao động 84–161 ở kênh G); lưới Andon vừa khung sau khi cho cột "Acknowledged by" co giãn (`Width="*"`; bản đầu bị cuộn ngang che cột Machine). **Chưa làm/ghi nhận:** (1) "8 máy" chỉ thử được 3 (master data chỉ có 3 máy MQTT); (2) **giá trị tag process mới nhất mất sau khi API restart** (store chỉ giữ trong RAM, loader Bước 4b không nạp lại từ `ProcessValues`) → ô Reflow không hiện `peakTemp` tới lần đọc kế tiếp; đề xuất nạp giá trị mới nhất mỗi tag trong loader, chờ user duyệt; (3) simulator khởi động lại với `initialCounter` nhỏ hơn counter cuối trong DB bị `CounterMath` tính là **reset** (cộng nguyên giá trị) nên "This hour" phình lên (15000+); đúng luật Bước 1, chỉ là dữ liệu giả lập.

- [x] **Bước 9 — Simulator vai trạm, integration test, tài liệu** (3 giờ)
  - Mục tiêu: nối M3, đóng module.
  - Việc cần làm: `AutoStationClient` (login JWT bằng user Operator seed, lấy serial đang chờ, `POST /stations/{id}/scan` với OK/NG + DefectCode, hết serial thì `Idle`); tùy chọn `--auto-operator` cho trạm thủ công; integration test (Testcontainers SQL Server + Mosquitto): chuỗi counter `100, 105, 3` cho ra delta `5` và `3 (reset)`, message trùng `msgId` chỉ một dòng, ngừng heartbeat thì `Offline`, stop rồi start container Mosquitto ingest tự nối lại; viết mục "Nguồn dữ liệu OEE" vào `ARCHITECTURE.md`: Total Count từ bộ đếm máy (MQTT), Good/NG từ scan theo serial, `ngDelta` chỉ để đối chiếu.
  - File: `src/MiniMes.MachineSimulator/Stations/AutoStationClient.cs`, `tests/MiniMes.IntegrationTests/Machines/{MqttIngestTests.cs, MosquittoFixture.cs}`, `ARCHITECTURE.md`.
  - Kiểm tra: chạy kịch bản demo bước 4–6 (mục 0 spec): serial đi qua SMT/Test tự động, một con NG theo `ng L1-TEST-01 1`, máy Down 5 phút → Andon → resolve; DoD mục 5.1 đạt.
  - **Ghi chú khi làm (27/10/2026).** **User chốt trước khi code:** (a) tài khoản vai trạm là **badge `SIM-STATION`** (Operator, không mật khẩu, `DevSeeder.SimulatorBadge`, seed riêng nên DB đã có user cũng nhận được), đăng nhập bằng `POST /api/auth/login-operator` theo từng trạm như quẹt thẻ ở kiosk (snippet 2.8 ghi user `sim-station` qua endpoint JWT của M0: đổi để không có mật khẩu trong `machines.json`); (b) trạm mở session cho **WO đầu dispatch list** của line, **bỏ qua WO đang chờ FAI** (chỉ mẫu FAI được chạy, mẫu là việc của kiosk); hết bo của WO đó mà đầu danh sách đã đổi thì mở session WO mới; (c) **sửa luôn phát hiện của Bước 8**: `MachineRuntimeLoader` nạp giá trị **mới nhất mỗi tag** (theo `SourceTs`) từ `ProcessValues` vào store (`SeedProcess`, không đánh dấu dirty). Simulator: `Stations/AutoStationClient.cs` (login → tra station theo **mã**, không theo id như snippet → dispatch list → session → `waiting-serials?take=1` → `scan` `Source = Machine`; mode `Auto` luôn OK (máy dán không phán bo), `AutoTest` OK/NG theo `ngProb`/lệnh `ng`, NG kèm mã lỗi ngẫu nhiên trong `defectCodes`; 401 thì đăng nhập lại; mọi lỗi API = "không có bo", không bao giờ ném), `StationClients` (một `HttpClient` chung, timeout 5 giây), `Stations/AutoOperatorService.cs` (`--auto-operator`: trạm thủ công Assembly/Pack trong `autoOperators`, luôn OK; Repair vẫn thủ công vì cần form sửa). `SimulatedMachine`: máy có `station` **chỉ đếm khi scan được chấp nhận** (không có bo, API không trả lời hoặc `Blocked` → không làm ra sản phẩm, máy `Idle` "starved", 5 giây xem lại, có bo thì về `Running`; NG ép bằng lệnh được giữ cho bo thật kế tiếp); lệnh đổi state xóa cờ starved. `machines.json`: `api { baseUrl, badge }`, `station` cho `L1-SMT-01` (Auto) và `L1-TEST-01` (AutoTest), `autoOperators` cho `L1-ASM-01`, `L1-PACK-01`; `--auto-operator` là cờ không có giá trị nên được lấy ra khỏi `args` trước khi cấu hình đọc dòng lệnh. Integration: `Machines/MosquittoFixture.cs` (container `eclipse-mosquitto:2` với chính `docker/mosquitto.conf`, **cổng host cố định chọn lúc chạy** để stop/start container vẫn cùng địa chỉ; `MqttApiFixture` = SQL Server riêng + broker + `ApiFactory(sql, mqttPort)` bật ingest, đợi ingest kết nối; collection `Mqtt` **`DisableParallelization`** vì các factory cấu hình API bằng biến môi trường chung cả process), `Machines/MqttIngestTests.cs` 4 test đi đúng đường MQTT client → broker → ingest → DB: `100, 105, 3` → delta `0, 5, 3 (reset)`; `msgId` trùng → 1 dòng, tổng đúng; ngừng heartbeat → `Offline` (32 giây); stop rồi start container → ingest tự nối lại, count sau đó vào DB. Thêm `ProcessValueTests` +1 (loader nạp giá trị mới nhất theo `SourceTs`, giá trị đến trễ không thắng, không đánh dấu dirty), `AuthTests` +1 (badge `SIM-STATION`). `ARCHITECTURE.md`: **ADR-007** nguồn dữ liệu OEE, **ADR-008** vì sao MQTT. README: trạng thái M4, tài khoản `SIM-STATION`, mục "Machine connectivity (M4)" có số đo và 2 ảnh. Kiến thức: [docs/learning/M4-step09-simulator-station-and-tests.md](../docs/learning/M4-step09-simulator-station-and-tests.md). Toàn bộ: unit 684, integration 277 xanh (≈ 1 phút 36 giây). **Chạy thật (kịch bản demo 4–6, `--auto-operator`, chu kỳ 3 giây):** 4 trạm mở session trên `WO-2610-00003` bằng `SIM-STATION` (bỏ `WO-2610-00001` đang chờ FAI); bo đi SMT → ASM → TEST → PACK; DB: mọi scan của simulator `Source = Machine`, SMT 6, ASM 6, TEST 5 OK + 2 NG, PACK 5; `ng L1-TEST-01 1` → đúng một NG (`PCBA2610170002117`, `CMP-MIS`); `down L1-SMT-01 300` → TEST hết bo, tự `Idle` (starved); Andon tự động **không** tạo vì máy còn Andon mở từ lần thử Bước 8 (đúng luật), và khi API khởi động lại Andon đó đã chờ 40 phút nên **nhảy thẳng level 2** (log Error); hủy các Andon cũ → 5 giây sau Andon #7 tự raise cho đợt Down này; supervisor ack (11 giây) + resolve `MCH-FAULT`; Repair `PCBA2610170002117` qua API (check-in + form) → về bước TEST → TEST tự `Idle → Running` và test lại **OK**. Đo DoD: `RaisedAt` → client SignalR nhận `AndonChanged`, 10 lần: min 11 ms, TB 31 ms, max 186 ms.

**Testcontainers cho Mosquitto (pseudo-code, kiểm tra API trong tài liệu `testcontainers-dotnet` phiên bản bạn dùng):**

```csharp
// Không có module Mosquitto dựng sẵn theo mình biết, dùng container tổng quát.
var mosquitto = new ContainerBuilder()
    .WithImage("eclipse-mosquitto:2")
    .WithPortBinding(1883, true)                       // true = map sang cổng host ngẫu nhiên
    // Bản 2.x mặc định chỉ nghe localhost: phải nạp config cho phép kết nối từ ngoài.
    // Cách 1: map file mosquitto.conf của dự án vào /mosquitto/config/mosquitto.conf.
    // Cách 2 (chưa chắc, cần kiểm tra trong image): chạy với file config no-auth có sẵn trong image.
    .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(1883))
    .Build();
await mosquitto.StartAsync();
var port = mosquitto.GetMappedPublicPort(1883);      // truyền cho ingest qua cấu hình
```

### Nếu bị kẹt

| Triệu chứng | Kiểm tra theo thứ tự |
| --- | --- |
| Ingest không nhận gì | (1) `mosquitto_sub -t "#" -v` có thấy message không: nếu không, lỗi ở phía gửi/broker. (2) Log "local only mode" ⇒ thiếu `mosquitto.conf`. (3) Host: `localhost` từ máy host, `mosquitto` từ container khác. (4) Filter subscribe 6 cấp (và 7 cấp cho `process/+`) có khớp topic không; topic có cấp `area` chưa. (5) Subscribe đã chạy trong `ConnectedAsync` chưa. |
| Client cứ kết nối rồi bị ngắt liên tục | Trùng `ClientId` (ingest chạy hai bản, hoặc hai máy simulator cùng ClientId). |
| Message tới nhưng không vào DB | Handler ném exception bị nuốt: thêm try/catch + log; kiểm tra `machine` khớp topic, `MqttTopicKey` có trong master data; xem batch writer có đang retry vì lỗi SQL không. |
| Đếm bị nhân đôi | Cache `msgId`, unique index `MsgId`; hai instance ingest cùng `ClientId`; retained cũ phát lại sau restart. |
| Đếm âm hoặc nhảy vọt | Reset chưa xử lý, mốc `last` không nạp lại sau restart, message sai thứ tự. In `last`, `current`, `Delta` ra log Debug. |
| Batch ghi chậm | Đo bằng SQL Profiler/log EF; batch quá nhỏ (cửa sổ 1 giây có hoạt động không?); index thừa trên `MachineEvent`; chỉ khi đo thấy EF là nút cổ chai mới nghĩ tới `SqlBulkCopy`. |
| API tự tắt sau một lúc | Exception thoát khỏi `ExecuteAsync` (host dừng mặc định). Xem log lúc chết, bọc `try/catch` thân vòng lặp. |
| Client SignalR nhận 401 | Token qua query `access_token` chưa cấu hình cho path `/hubs`, hoặc token hết hạn. |
| Kết nối SignalR ổn nhưng không thấy event | Chưa `JoinLine`; tên group lệch (`line:L1` với `line:l1`); tên method chuỗi sai; `On<T>` đăng ký sau `StartAsync`; bật log `Microsoft.AspNetCore.SignalR` mức Debug. |
| "The calling thread cannot access this object" / lỗi `CollectionView` | Đang sửa UI/collection từ callback SignalR: bọc qua `IUiDispatcher`. |
| Dashboard đứng yên sau khi API restart | `Reconnected` chưa join lại group hoặc chưa tải lại snapshot; hoặc quá lượt retry (`Closed`) mà chưa có vòng khởi động lại. |
| Máy nhấp nháy `Offline`/`Running` | Ngưỡng 30 giây sát nhịp heartbeat, mạng mất gói; thêm điều kiện hồi phục (2 heartbeat) và kiểm tra ân hạn. |
| Toàn bộ máy `Offline` sau khi Mosquitto bật lại | Watchdog thiếu thời gian ân hạn. |
| Hai Andon cho một sự cố | Thiếu filtered unique index hoặc không bắt lỗi trùng; auto và tay chạy đua. |

## 4. Vì sao chọn giải pháp này

| Quyết định | Đã chọn | Phương án khác đã cân nhắc | Vì sao không chọn | Khi nào nên đổi |
| --- | --- | --- | --- | --- |
| Đường lấy dữ liệu máy | MQTT + Mosquitto theo hợp đồng; **[v2]** máy OPC UA/Modbus đi qua Edge Gateway (M4b) rồi vào cùng MQTT | MES làm OPC UA client/Modbus master trực tiếp; máy tự gọi HTTP POST | MES gọi thẳng PLC thì mỗi giao thức một driver trong MES, MES restart là mất kết nối tất cả máy, không có bộ đệm khi mạng đứt. HTTP POST mất message khi MES tắt, máy nào cũng phải biết địa chỉ MES | Nhà máy đã có SCADA/historian: subscribe từ đó thay vì gateway riêng, vẫn giữ hợp đồng |
| **[v2]** Cấu trúc topic | Unified Namespace theo ISA-95 (`site/area/line/machine`) | Topic phẳng theo máy; Sparkplug B | Topic phẳng không subscribe theo khu/line được; Sparkplug B dùng protobuf và quy ước birth/death phức tạp, khó đọc khi học | Khi tích hợp với hệ thống đã dùng Sparkplug: thêm lớp chuyển đổi ở gateway |
| **[v2]** Lưu thông số quy trình | Bảng `ProcessValue` trên SQL Server, chỉ ghi khi vượt deadband, giữ 30 ngày | Time-series DB (InfluxDB, TimescaleDB); historian thương mại | Thêm hạ tầng; spec đã chốt chỉ SQL Server; vài tag × 1 msg/giây vẫn nhỏ | Hàng nghìn tag tần suất cao hoặc cần lưu nhiều năm: historian/time-series DB, MES chỉ giữ giá trị đã lấy mẫu cho SPC |
| **[v2]** Leo thang Andon | Bảng quy tắc + watcher có sẵn, push SignalR theo role | Hard-code thời gian; gửi email/Zalo thật | Hard-code thì mỗi nhà máy phải sửa code; kênh ngoài cần tài khoản/dịch vụ | Thêm kênh ngoài như một `INotificationChannel` khi có yêu cầu |
| Cách gửi số đếm | Bộ đếm tích lũy | Gửi "+1" mỗi sản phẩm | Mất message ⇒ lệch không sửa được | Cần từng sự kiện có thời điểm chính xác (truy xuất cycle-by-cycle): gửi thêm sự kiện, vẫn giữ tích lũy làm đối chiếu |
| QoS | QoS 1 + chống trùng `msgId`; heartbeat QoS 0 | QoS 0 tất cả; QoS 2 | QoS 0 mất state/alarm; QoS 2 chậm, và vẫn cần chống trùng tầng ứng dụng | Luồng mà trùng gây hậu quả tài chính và không thể idempotent |
| Chống trùng | Cache RAM + unique index `MsgId` | Chỉ cache; chỉ DB | Chỉ cache: mất khi restart; chỉ DB: mỗi trùng là một exception/round-trip | Khối lượng lớn: bỏ cột `MsgId` khỏi bảng chính, dùng bảng dedupe có TTL |
| Nơi chạy ingest | `BackgroundService` trong API process | Worker Service riêng | Thêm một thứ để deploy/cấu hình trong MVP; hai process chưa cần | Cần scale ingest độc lập, hoặc ingest làm API chậm: tách process (dùng chung Infrastructure) và shared subscription (MQTT 5) |
| Ghi DB | `Channel` + batch EF `AddRange` | Insert từng message; `SqlBulkCopy` | Từng message quá nhiều round-trip; BulkCopy phải tự map, chưa cần | Đo thấy > vài nghìn dòng/giây: `SqlBulkCopy` |
| Trạng thái hiện tại | `MachineRuntimeStore` trong RAM, nạp từ DB lúc khởi động | Truy vấn DB mỗi lần; Redis | DB mỗi message là chậm; Redis thêm hạ tầng | Chạy nhiều instance API: Redis (kèm backplane SignalR) |
| Lịch sử state | Gom lúc ghi (`MachineStateHistory`) | Suy ra từ event lúc báo cáo | Truy vấn nặng, sai khi thiếu event | Cần dựng lại lịch sử từ event (sửa lỗi dữ liệu): script chạy lại từ `MachineEvent` |
| Phát hiện Offline | Heartbeat timeout 30 giây (+ LWT tùy chọn) | Chỉ LWT; chỉ "không có message" | LWT không bắt được máy treo mà TCP vẫn mở; máy `Idle` im lặng bị nhầm là chết | Mạng rất ổn định và cần phản ứng < 5 giây: rút ngắn heartbeat |
| Realtime lên client | SignalR + throttle server 1 giây | Polling REST; SSE; WebSocket thô; MQTT qua WebSocket xuống WPF | Polling tốn và trễ; WebSocket thô tự làm reconnect/group; MQTT xuống client thêm đường phân quyền thứ hai | Nhiều nghìn client: backplane Redis hoặc Azure SignalR Service |
| Đẩy sự kiện | State/Andon đẩy ngay, số đếm throttle | Throttle tất cả; không throttle | Throttle tất cả có thể vượt 2 giây; không throttle làm nghẽn UI | Cần mượt hơn: throttle theo nhóm máy hoặc client gộp bằng `DispatcherTimer` |
| Andon | Tự xây trong MES (DB + SignalR) | Chỉ báo Zalo/SMS/email; đèn tháp phần cứng | Không có lịch sử ack/resolve, không có lý do cho OEE | Thêm kênh ngoài (Teams/Zalo/đèn tháp) như phần mở rộng, vẫn giữ bản ghi trong DB |
| Simulator | Một process hai vai, mỗi máy một MQTT client | Hai process; một client chung | Hai process khó đồng bộ chu kỳ máy với scan; một client chỉ có một LWT | Cần mô phỏng hàng trăm máy: giảm còn một client + LWT theo nhóm |

**Ba quyết định đáng nói lại được trong phỏng vấn.**

1. *Bộ đếm tích lũy + QoS 1 + idempotent.* "Mạng giữa máy và MES không đáng tin, nên tôi không dựa vào việc từng message đến được. Máy gửi tổng đếm; MES tính delta giữa hai mốc, xử lý reset khi tổng giảm. MQTT QoS 1 đảm bảo ít nhất một lần, nên tôi chống trùng bằng `msgId` (cache và unique index). Mất mạng một lúc thì máy vẫn đếm, khi nối lại message kế tiếp mang tổng mới nên không mất sản lượng, chỉ mất độ mịn."
2. *Đường nóng và đường lạnh.* "Handler MQTT không chạm DB: nó cập nhật trạng thái trong RAM, đẩy realtime, rồi bỏ vào hàng đợi có giới hạn; một consumer ghi DB theo batch. Nhờ vậy UI thấy thay đổi trong vài trăm mili giây, DB không bị 10 lần ghi mỗi giây, và khi DB chậm thì áp lực ngược lên broker thay vì mất dữ liệu. Cái giá: trạng thái trong RAM có thể đi trước DB một chút, và chạy nhiều instance thì phải chuyển store ra Redis."
3. *Down khác Offline, Andon có chủ.* "Down là máy tự khai lỗi, gọi bảo trì; Offline là MES không nghe thấy máy, gọi IT, và không được tính như máy hỏng hay máy chạy. Andon là quy trình có người chịu trách nhiệm: raise, acknowledge, resolve kèm lý do; mỗi bước có thời gian để đo phản ứng, và lý do đi thẳng vào Pareto ở OEE."

## 5. Hoàn thành module

### 5.1 Definition of Done

- [x] `docker compose up` có SQL Server + Mosquitto; chạy API + Simulator + WPF BackOffice là thấy dashboard sống.
- [x] Simulator chạy ≥ 3 máy theo `machines.json`, có lệnh ép Down/NG/reset/mute/dup/skew; vai trạm test tự động scan qua API M3.
- [x] Hợp đồng dữ liệu: DTO chung trong `MiniMes.Contracts`; topic, QoS, retain đúng bảng mục 2.3.
- [x] **Tắt rồi bật Mosquitto: không mất số đếm quá một chu kỳ.** Kiểm tra bằng số: `SUM(Delta)` trong DB khớp (counter cuối − counter đầu) của simulator; không có máy nào bị `Offline` oan khi chỉ broker chết.
- [x] **Andon hiện trên dashboard < 2 giây** kể từ lúc Andon được tạo (`RaisedAt`); đổi state hiện < 2 giây kể từ lúc ingest nhận message. Đã đo và ghi số vào README.
- [x] Chống trùng: lệnh `dup` không tạo hai dòng, không cộng đôi số đếm; reset xử lý đúng.
- [x] `MachineStateHistory`: mỗi máy đúng một đoạn mở, không chồng (DB ép bằng filtered unique index).
- [x] `Offline` tách khỏi `Down`: 30 giây không heartbeat ⇒ `Offline`; có ân hạn khi ingest tự mất kết nối.
- [x] Andon: raise (tay và tự động) → acknowledge → resolve kèm lý do; mỗi (máy, loại) một Andon mở; 403 đúng role; 409 khi ack đua nhau.
- [ ] Throttle: số đếm ≤ 1 lần/giây/máy tới client; UI không giật với 8 máy chu kỳ 0,2 giây. *(Throttle đạt; UI chỉ thử được 3 máy vì master data có 3 máy MQTT: 1 lần/giây/máy, UI không nghẽn. 8 máy chờ M4b/thêm master data.)*
- [x] Client SignalR reconnect: join lại group, tải lại snapshot, banner trạng thái kết nối.
- [ ] Unit test: `CounterMath`, `MachineTopic`, `AndonCall`, `RecentIdCache`, watchdog (FakeTimeProvider). Integration test Testcontainers (SQL Server + Mosquitto) chạy trong CI. *(Đủ test, xanh ở máy dev; chưa push nên chưa thấy CI chạy test Mosquitto.)*
- [x] **[v2]** Topic theo UNS 6 cấp có `area`; payload có `source`, `quality`; ingest chấp nhận tag `process/{tag}` có trong `MachineTag`, bỏ tag lạ, đánh `Bad` giá trị ngoài khoảng hợp lý.
- [x] **[v2]** Andon leo thang đúng bảng quy tắc (unit test FakeTimeProvider); Ack dừng leo thang; dashboard hiện level.
- [x] **[v2]** Dashboard hiện giá trị tag process mới nhất và nguồn dữ liệu (MQTT/OPC UA/Modbus) của từng máy.
- [x] `ARCHITECTURE.md` ghi quyết định nguồn dữ liệu OEE (Total Count từ MQTT, Good/NG từ scan theo serial, `ngDelta` chỉ đối chiếu) và lý do chọn MQTT.

### 5.2 Tự kiểm tra

**1. Vì sao máy gửi bộ đếm tích lũy thay vì "+1"? Khi nào bộ đếm tích lũy gây rắc rối?**
<details><summary>Gợi ý</summary>
Mất message không sai tổng vì message sau mang tổng mới; tính lại được từ hai mốc. Rắc rối: reset về 0 (PLC restart), quay vòng 16-bit, message sai thứ tự bị nhận nhầm là reset. Delta = current − last, nếu current &lt; last thì delta = current và đánh dấu reset.
</details>

**2. QoS 1 giao "ít nhất một lần". Bạn xử lý bản trùng ở đâu, bằng gì? Vì sao không dùng QoS 2?**
<details><summary>Gợi ý</summary>
`msgId` duy nhất: cache RAM TTL ngắn ở ingest + unique index `MsgId` trong DB. QoS 2 chỉ đảm bảo trong một phiên giao nhận MQTT, không che được trùng ở tầng ứng dụng (restart, retry ghi DB), lại chậm hơn (bốn gói).
</details>

**3. Down khác Offline ở đâu? Máy `Offline` mà bộ đếm vẫn tăng thì khi nối lại xử lý thế nào?**
<details><summary>Gợi ý</summary>
Down: máy tự khai, có mã lỗi, bảo trì xử lý. Offline: MES không nghe thấy, IT xử lý, dữ liệu state thiếu. Khi nối lại, message `count` kế tiếp mang tổng mới nên delta bù đúng sản lượng; đoạn state trong lúc Offline gắn cờ chưa xác minh để M5 quyết định.
</details>

**4. Vì sao subscribe lại trong `ConnectedAsync` mỗi lần kết nối? Vai trò của `ClientId` và `CleanSession`?**
<details><summary>Gợi ý</summary>
Với clean session, broker quên subscription khi ngắt; kể cả khi không, nên đừng phụ thuộc. `ClientId` phải duy nhất và ổn định: trùng thì đá nhau; ổn định thì persistent session giữ được QoS 1 chờ. `CleanSession = false` chỉ có tác dụng nếu ClientId không đổi.
</details>

**5. Retained message dùng cho topic nào và không dùng cho topic nào? Vì sao không retain heartbeat?**
<details><summary>Gợi ý</summary>
Dùng cho giá trị hiện tại (`state`, `status`). Không dùng cho sự kiện (`count`, `alarm`) vì subscriber mới nhận lại như vừa xảy ra. Heartbeat retained: broker giữ nhịp cuối nên ingest mới khởi động tưởng máy còn sống dù nó đã chết.
</details>

**6. Vì sao handler MQTT chỉ đẩy vào `Channel` mà không ghi DB? Nếu DB chết 1 phút thì chuyện gì xảy ra?**
<details><summary>Gợi ý</summary>
Handler chậm sẽ chặn vòng nhận, trễ keep-alive, và ghi từng dòng tốn round-trip. Consumer retry giữ batch; Channel đầy (bounded, `Wait`) thì handler bị chặn, MQTT client ngừng nhận, broker giữ QoS 1 hộ (với persistent session). Mất dữ liệu nhiều nhất là phần chưa kịp ack.
</details>

**7. SignalR client rớt kết nối 10 giây rồi nối lại. Cần làm gì để dashboard đúng? Vì sao?**
<details><summary>Gợi ý</summary>
`ConnectionId` mới nên đã ra khỏi group ⇒ join lại; event trong lúc rớt không được phát lại ⇒ tải lại snapshot qua REST. Đăng ký `On` trước `StartAsync`; bỏ event có `Ts` cũ hơn snapshot.
</details>

**8. Vì sao phải đưa cập nhật UI qua Dispatcher? Lỗi nào xuất hiện nếu không?**
<details><summary>Gợi ý</summary>
Callback SignalR chạy trên thread nền; WPF chỉ cho thread UI sửa control/`ObservableCollection`. Lỗi: `InvalidOperationException` ("calling thread cannot access this object") hoặc `NotSupportedException` từ CollectionView. Dùng `Dispatcher.InvokeAsync` qua interface `IUiDispatcher`.
</details>

**9. Throttle khác debounce và batching thế nào? Vì sao state/Andon không throttle còn số đếm thì có?**
<details><summary>Gợi ý</summary>
Throttle: tối đa N lần/giây, phát bản mới nhất. Debounce: đợi yên lặng mới phát. Batching: gom nhiều mục vào một lần ghi. State/Andon hiếm và cần hành động ngay nên phải đẩy tức thì (điều kiện < 2 giây); số đếm dày và chỉ để nhìn nên gộp 1 giây/máy, gửi giá trị tuyệt đối để gộp không mất thông tin.
</details>

**10. Hai supervisor cùng bấm Acknowledge một Andon, và auto-Andon chạy đua với nút tay của operator. DB và code chặn thế nào?**
<details><summary>Gợi ý</summary>
Ack: `RowVersion` (concurrency token) ⇒ người sau nhận 409, và domain chặn `Acknowledge` khi không còn ở `Raised`. Trùng Andon: filtered unique index `(MachineId, Type) WHERE Status <= 1`; bắt lỗi unique và trả về bản đang mở.
</details>

**11. [v2] Unified Namespace là gì, khác gì cách tích hợp điểm-điểm?**
<details><summary>Gợi ý</summary>
Một cây topic duy nhất theo cấp ISA-95 (site/area/line/machine) mà mọi hệ thống publish vào và subscribe từ đó. Điểm-điểm: N hệ thống nối với nhau cần tới N×(N−1) tích hợp; UNS: mỗi hệ thống chỉ nối một lần với broker. Thêm máy là thêm nhánh, không sửa người dùng dữ liệu.
</details>

**12. [v2] Andon đang leo thang thì supervisor bấm Acknowledge đúng lúc watcher chạy. Chuyện gì xảy ra?**
<details><summary>Gợi ý</summary>
Hai bên cùng sửa một dòng `AndonCall`; `RowVersion` làm bên ghi sau nhận xung đột. Nếu watcher thua thì bỏ qua lần này, lần chạy sau thấy `Acknowledged` và không leo thang. Nếu supervisor thua thì nhận 409, tải lại và bấm lại (lúc đó Andon ở level cao hơn nhưng vẫn `Raised`, ack được bình thường).
</details>

### 5.3 Câu hỏi phỏng vấn liên quan

1. **"Đọc dữ liệu máy bằng cách nào?"** Gợi ý: nêu chuỗi cảm biến → PLC → (OPC UA / Modbus / giao thức hãng) → gateway → MES; phân biệt polling (Modbus) với subscribe (OPC UA, MQTT). **[v2]** Kể đúng những gì bạn đã làm: máy MQTT phát trực tiếp; lò reflow là OPC UA server, máy dán keo là Modbus slave, cả hai được Edge Gateway .NET của bạn đọc và đẩy lên MQTT theo cùng hợp đồng, MES không phải sửa dòng nào. Thừa nhận chưa nối PLC thật, nhưng đã gặp các vấn đề thật trên simulator: map thanh ghi, thứ tự byte, bộ đếm 16-bit, lệch giờ, mất kết nối.
2. **"Mất mạng giữa máy và MES thì sao?"** Gợi ý: máy vẫn đếm; heartbeat mất 30 giây ⇒ Offline (không phải Down); bộ đếm tích lũy nên nối lại không mất sản lượng; QoS 1 + persistent session cho message đang chờ; ingest tự reconnect có backoff và có thời gian ân hạn để không kết luận sai về máy; cái mất là độ mịn (không biết state trong khoảng đó), được gắn cờ để người phân loại.
3. **"QoS 0, 1, 2 khác nhau gì, bạn chọn gì và vì sao?"** Gợi ý: nêu ba mức, min(publisher, subscriber), chọn QoS 1 cho state/count/alarm và chống trùng bằng `msgId`, QoS 0 cho heartbeat.
4. **"Andon là gì? Bạn thiết kế thế nào?"** Gợi ý: Toyota/jidoka; ba bước raise/ack/resolve; ai làm gì; thời gian phản ứng và thời gian sửa; lý do dừng nối sang Pareto; chống trùng; ngưỡng auto X để tránh Andon fatigue; leo thang là bước mở rộng.
5. **"Vì sao SignalR mà không polling? 500 client thì sao?"** Gợi ý: push khi có thay đổi, group theo line, throttle ở server; scale bằng backplane Redis hoặc Azure SignalR Service; nhấn mạnh snapshot + delta và resync sau reconnect.
6. **"Bộ đếm reset về 0 giữa ca, bạn xử lý thế nào?"** Gợi ý: delta âm ⇒ reset, delta = tổng mới; đánh dấu `IsReset`; giới hạn: mất số giữa message cuối và lúc reset; quay vòng 16-bit cần cấu hình riêng; cảnh báo delta bất thường và đối chiếu với số serial đã scan (M5).
7. **[v2] "Andon không ai nhận thì sao?"** Gợi ý: bảng quy tắc leo thang theo loại Andon và thời gian; watcher dùng `TimeProvider` nên test được; dừng khi Ack; KPI "số Andon phải leo thang cấp 2" cho quản lý.
8. **[v2] "Dữ liệu nhiệt độ lò dùng vào việc gì trong MES?"** Gợi ý: tag process lưu theo deadband, có quality; M7 lấy mẫu thành số đo SPC, rule engine báo khi nhiệt trôi, QC Hold các serial đã qua lò trong khoảng đó.

### 5.4 Tài liệu đọc thêm

Chỉ liệt kê trang chính thức (tên trang/tài liệu + trang chủ). Nếu không tìm ra đúng trang, mở trang chủ và dùng ô tìm kiếm.

- MQTT: mqtt.org (mục Getting started, đặc tả MQTT 3.1.1 và 5.0 do OASIS công bố).
- Eclipse Mosquitto: mosquitto.org, tài liệu `mosquitto.conf` (man page), `mosquitto_pub`, `mosquitto_sub`.
- MQTTnet: github.com/dotnet/MQTTnet (README và Wiki: client options, Last Will, đọc payload theo từng phiên bản 4.x/5.x).
- Microsoft Learn: "Background tasks with hosted services in ASP.NET Core" (BackgroundService, hành vi khi exception).
- Microsoft Learn: tài liệu `System.Threading.Channels` và bài "An Introduction to System.Threading.Channels" (devblogs.microsoft.com/dotnet).
- Microsoft Learn: "Overview of ASP.NET Core SignalR", "ASP.NET Core SignalR .NET Client" (automatic reconnect, `On`, `InvokeAsync`), "Send messages from outside a hub" (`IHubContext`), "Authentication and authorization in ASP.NET Core SignalR" (token qua query), "Strongly typed hubs".
- Microsoft Learn: "Threading Model" (WPF) và tài liệu `Dispatcher.InvokeAsync`.
- Microsoft Learn: "Filtered indexes" (SQL Server), `SqlBulkCopy` (Microsoft.Data.SqlClient).
- Testcontainers for .NET: github.com/testcontainers/testcontainers-dotnet và dotnet.testcontainers.org (nếu trang chủ tài liệu khác, đi từ README).
- Bối cảnh công nghiệp: opcfoundation.org (OPC UA), modbus.org (đặc tả Modbus), Eclipse Sparkplug (trang thuộc eclipse.org), IPC-CFX và SEMI E10 (tra trên trang của IPC và SEMI).
- Andon/TPS: "Toyota Production System: Beyond Large-Scale Production" (Taiichi Ohno), "The Toyota Way" (Jeffrey Liker).
