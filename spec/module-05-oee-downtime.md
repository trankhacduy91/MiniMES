# Module 5 — OEE & Downtime

> Module này biến dữ liệu máy (M4) và kết quả scan theo serial (M3) thành con số mà quản lý xưởng hỏi đầu tiên: **OEE = Availability × Performance × Quality**, kèm bảng **Pareto lý do dừng máy** để biết sửa cái gì trước.
> Sau khi xong bạn có: hàm tính OEE thuần (pure function) có unit test khớp số tính tay, bộ xử lý ca/ngày sản xuất kể cả ca qua nửa đêm, job chốt `OeeSnapshot` theo giờ và cuối ca, danh sách downtime "cần phân loại", và dashboard WPF (A/P/Q, Pareto top 5, sản lượng theo giờ so với kế hoạch).
> Thời lượng ước tính: khoảng 25 giờ (14–16 buổi, 1,5–2 giờ/buổi; 22 giờ của bản cũ + 3 giờ cho phần **[v2]**). Bạn sẽ tự học OEE từ gốc, nên buổi đầu chỉ cầm giấy bút, chưa viết code.
> Module tiên quyết: M1 (`Shift`, `ShiftBreak`, **[v2]** `PlantCalendar`, `DowntimeReason`, `IdealCycleTimeSec`), M2 (snapshot `WorkOrderOperation`), M3 (`ScanLog`), M4 (`MachineEvent`, `MachineStateHistory`, `AndonCall`, SignalR). Máy OPC UA/Modbus của M4b đi vào cùng `MachineEvent` nên công thức không đổi.
>
> **Cập nhật theo [spec-v2](spec-v2.md):** (1) Planned Production Time **bỏ qua ngày nghỉ** khai trong `PlantCalendar` (mục 2.2); (2) **xuất Excel** OEE theo ca và Pareto bằng ClosedXML (mục 2.7); (3) view **Line TV** toàn màn hình cho màn hình lớn ở line (mục 2.8). Các chỗ đổi được đánh dấu **[v2]**.

---

## 1. Kiến thức nghiệp vụ MES

### 1.1 Bức tranh tổng thể

**Module này nằm ở đâu.** Trong sơ đồ kiến trúc của dự án, M4 là "giác quan": nó nghe máy nói gì (đang chạy, đang dừng, đã đếm bao nhiêu). M3 là "sổ ghi chép theo từng sản phẩm": con nào qua trạm nào, OK hay NG. M5 là "bộ não phân tích" đứng trên hai nguồn đó và trả lời câu hỏi: **máy của tôi đang được dùng tốt đến mức nào, và phần thất thoát nằm ở đâu?**

```
M3 ScanLog (OK/NG theo serial) ──┐
                                 ├──▶ M5: OEE + Downtime analysis ──▶ Dashboard, báo cáo ca, họp cải tiến
M4 MachineEvent / StateHistory ──┘
M1 Shift, IdealCycleTime, DowntimeReason (dữ liệu nền)
```

**Ai dùng.**

| Vai trò | Dùng để làm gì |
| --- | --- |
| Operator | Nhìn OEE ca hiện tại, biết line đang đúng nhịp hay chậm |
| Supervisor | Phân loại downtime chưa có lý do, xem Pareto cuối ca, báo cáo bàn giao ca |
| Quản lý xưởng / kỹ sư cải tiến | So sánh máy/ca/ngày, chọn dự án cải tiến từ Pareto |
| Maintenance | Xem máy nào dừng đột xuất nhiều nhất (liên kết M10 sau này) |
| Kỹ sư công nghệ | Nhận cảnh báo "Performance > 100%" để sửa cycle time chuẩn |

**Vì sao nhà máy cần.** Một nhà máy điện tử đầu tư máy SMT vài trăm nghìn đến vài triệu USD. Nếu máy đó chỉ tạo ra giá trị 60% thời gian mà không ai đo, thì mua thêm máy (đắt) trông như giải pháp duy nhất. OEE cho thấy: "Máy hiện có đang bị lãng phí 40%, và 25 điểm trong đó là do 3 lý do dừng máy cụ thể." Đó là lý do OEE là KPI số 1 của ngành sản xuất rời rạc, và là câu hỏi phỏng vấn IT MES phổ biến nhất.

**Phạm vi trong dự án.** OEE tính cho các máy có bộ đếm (SMT, Test) — tức các trạm mà Machine Simulator vai "máy" gửi `counter` qua MQTT. Các trạm thủ công (Assembly, Pack) không có bộ đếm máy nên **không có OEE ở MVP**; chúng vẫn có sản lượng/FPY từ M3.

### 1.2 Khái niệm & thuật ngữ

Các ví dụ dưới đây dùng chung một máy: **`L1-SMT-01`**, ca **S1 06:00–14:00**, sản phẩm `PCB-A`, ideal cycle time **20 giây/bo mạch**.

#### 1.2.1 OEE là gì

**OEE (Overall Equipment Effectiveness)** = tỷ lệ thời gian *có giá trị thật sự* trên tổng thời gian *đáng lẽ máy phải làm việc*. Nó trả lời bằng một phần trăm duy nhất cho câu hỏi: "Trong khoảng thời gian tôi định dùng máy, bao nhiêu phần trăm máy đã chạy **đủ nhanh**, **không dừng**, và **ra sản phẩm tốt**?"

OEE chia ba câu hỏi nhỏ:

| Thành phần | Câu hỏi | Mất mát nó đo |
| --- | --- | --- |
| **Availability (A)** | Máy có chạy khi đáng lẽ phải chạy không? | Dừng máy (hỏng, thiếu vật tư, đổi model…) |
| **Performance (P)** | Khi chạy, máy có chạy đủ nhanh không? | Chạy chậm hơn chuẩn, dừng ngắn lặt vặt |
| **Quality (Q)** | Sản phẩm làm ra có đạt không? | NG, phải sửa, phải hủy |

Công thức của dự án (đã chốt ở spec, không đổi):

```
Planned Production Time = Thời gian ca − Nghỉ giữa ca − PlannedStop
Run Time               = Planned Production Time − Unplanned Downtime
Availability           = Run Time / Planned Production Time
Performance            = (IdealCycleTime × Total Count) / Run Time
Quality                = Good Count / Total Count
OEE                    = Availability × Performance × Quality
```

Lưu ý quan trọng: nhân ba tỷ lệ cho ra một biểu thức rút gọn rất đẹp, và bạn sẽ dùng nó để kiểm tra chéo:

```
OEE = (IdealCycleTime × Good Count) / Planned Production Time
```

Tức: **OEE = thời gian lý tưởng để làm ra đúng số sản phẩm tốt, chia cho thời gian đáng lẽ được dùng**. Các `Run Time` và `Total Count` triệt tiêu nhau.

#### 1.2.2 Ví dụ tính tay đầy đủ (bạn phải làm lại bằng giấy trước khi viết code)

**Đề bài — máy `L1-SMT-01`, ca S1, thứ Năm 01/10/2026.**

| Dữ kiện | Giá trị |
| --- | --- |
| Ca | 06:00–14:00 = **480 phút** |
| Nghỉ giữa ca | 08:00–08:15 (giải lao) + 10:00–10:45 (ăn giữa ca) = **60 phút** |
| PlannedStop | 06:00–06:15 họp đầu ca + kiểm tra máy = **15 phút** (lý do `PLN-MEETING`, Planned, không tính vào Availability) |
| Dừng ngoài kế hoạch | 07:10–07:35 kẹt feeder (25 phút, `BRK-FEEDER`); 09:20–09:32 hết reel chờ kho (12 phút, `MAT-WAIT`); 12:40–12:48 chờ QC duyệt bo đầu tiên sau đổi reel (8 phút, `QC-WAIT`) → **45 phút** |
| Dừng ngắn (micro stop) | 6 lần × 20 giây = 2 phút. Ngắn hơn ngưỡng 60 giây nên **không** tính là downtime (xem 1.2.5), nằm trong Run Time |
| Ideal cycle time | **20 giây**/bo (lấy từ snapshot `WorkOrderOperation`) |
| Total Count | Bộ đếm máy: **1.000** bo (tổng các delta của `counter` trong ca) |
| NG | Từ `ScanLog` theo serial: **30** kết quả NG → Good Count = **970** |

**Bước 1 — Planned Production Time.**

```
480 − 60 − 15 = 405 phút
```

**Bước 2 — Run Time.**

```
405 − 45 = 360 phút
```

**Bước 3 — Availability.**

```
A = 360 / 405 = 0,8889  (88,89%)
```

**Bước 4 — Performance.** Đổi hết về giây cho khỏi nhầm đơn vị: Run Time = 360 × 60 = 21.600 giây.

```
Thời gian lý tưởng để làm 1.000 bo = 1.000 × 20 = 20.000 giây
P = 20.000 / 21.600 = 0,9259  (92,59%)
```

**Bước 5 — Quality.**

```
Q = 970 / 1.000 = 0,97  (97,00%)
```

**Bước 6 — OEE.**

```
OEE = 0,8889 × 0,9259 × 0,97 ≈ 0,7983  (79,8%)
(0,7984 nếu giữ nguyên phân số; 0,0001 chênh lệch chỉ do làm tròn các bước trung gian)
```

**Kiểm tra bằng công thức rút gọn** (nếu hai kết quả lệch nhau thì bạn tính sai ở đâu đó):

```
OEE = (20 s × 970) / (405 × 60 s) = 19.400 / 24.300 = 0,7984  ✓
```

**Biểu đồ "thác nước thời gian"** — cách nhìn quan trọng nhất, vì nó cho thấy mỗi thành phần OEE là *một loại mất mát thời gian*:

```
480,00 ┤████████████████████████████████████████  Thời gian ca
       │ − 60,00  nghỉ giữa ca                      (không tính vào OEE)
       │ − 15,00  PlannedStop                       (không tính vào OEE)
405,00 ┤████████████████████████████████████  Planned Production Time (100%)
       │ − 45,00  Unplanned downtime                → mất mát Availability
360,00 ┤████████████████████████████████      Run Time (A = 88,89%)
       │ − 26,67  chạy chậm hơn chuẩn               → mất mát Performance
333,33 ┤██████████████████████████████        Thời gian "lý tưởng" cho 1.000 bo
       │ − 10,00  30 bo NG × 20 s                   → mất mát Quality
323,33 ┤█████████████████████████████         Thời gian có giá trị (79,84%)
```

Cách đọc: trong 405 phút đáng lẽ dùng máy, chỉ **323,33 phút** thực sự tạo ra bo đạt chuẩn. Mất 81,67 phút, trong đó lớn nhất là 45 phút dừng máy (55% tổng mất mát), rồi chạy chậm (26,67 phút), rồi NG (10 phút). Nếu quản lý hỏi "cải thiện ở đâu trước?", câu trả lời bắt đầu từ đây (xem thêm mục 5.3).

Cuối cùng, so sánh với ngưỡng tham khảo hay gặp: A 90% × P 95% × Q 99% = 84,6%. Con số "85% là đẳng cấp thế giới" chỉ là quy ước tham khảo; mục tiêu thật của từng line phụ thuộc loại máy và mức độ đổi model. Đừng đem 85% ra làm chỉ tiêu cứng cho mọi máy.

**Ví dụ thứ hai, ngược chiều.** Máy `L1-TEST-01` (trạm test) chạy đủ 405 phút không dừng (A = 100%), nhưng test mỗi bo mất 30 giây trong khi chuẩn là 20 giây, Q = 99%. P = 20/30 = 66,7%, OEE ≈ 66%. Máy "không bao giờ hỏng" vẫn có OEE thấp. Đó là lý do phải có cả ba thành phần.


#### 1.2.3 Six Big Losses (sáu tổn thất lớn)

Đây là khung phân loại mất mát của TPM (Total Productive Maintenance, xuất phát từ Nhật Bản). Mỗi tổn thất rơi vào đúng một thành phần OEE, nên khi OEE thấp bạn biết đi tìm ở đâu.

| # | Tổn thất | Thuộc | Ví dụ ở xưởng SMT | Dữ liệu MES bắt được nó |
| --- | --- | --- | --- | --- |
| 1 | **Breakdown** (hỏng máy) | A | Kẹt feeder, hỏng nozzle, lỗi servo | State `Down` + lý do `BRK-*` |
| 2 | **Setup & Adjustment** (đổi model, chỉnh máy) | A | Đổi sang `PCB-B`, đổi reel, hiệu chỉnh camera | State `Setup`, lý do `SET-*` |
| 3 | **Minor Stop / Idling** (dừng ngắn) | P | Máy kẹt linh kiện 15 giây rồi tự chạy lại, sensor báo nhầm | Nhiều đoạn `Down` rất ngắn, hoặc không thấy trong state nhưng bộ đếm chậm lại |
| 4 | **Reduced Speed** (chạy chậm) | P | Operator chỉnh tốc độ băng tải xuống cho "êm", máy cũ chạy chậm | P < 100% mà không có dừng máy nào tương ứng |
| 5 | **Process Defects** (sản phẩm lỗi khi đang chạy ổn định) | Q | Lệch linh kiện, thiếu thiếc, NG tại AOI/Test | `ScanLog` NG + `DefectCode` |
| 6 | **Reduced Yield / Startup Rejects** (lỗi giai đoạn khởi động) | Q | 10 bo đầu tiên sau khi đổi model luôn NG vì chưa ổn định | NG tập trung ngay sau `Setup` |

Hai điều hay bị nhầm:

- Tổn thất 3 và 4 thường **không** làm máy "Down" trên đồng hồ trạng thái. Chúng chỉ lộ ra vì số đếm thấp hơn kỳ vọng. Vì vậy P là thành phần khó nhìn thấy nhất, và cũng là chỗ "giấu" nhiều mất mát nhất.
- Trong Six Big Losses, "hỏng máy" và "setup" là hai tổn thất riêng vì **cách chữa khác nhau**: hỏng máy chữa bằng bảo trì phòng ngừa, setup chữa bằng SMED (rút ngắn thời gian đổi model).

#### 1.2.4 Planned vs Unplanned downtime

**Planned downtime** là lần dừng được quyết định *trước*, có lý do kinh doanh, nằm trong lịch: nghỉ giữa ca, họp đầu ca, bảo trì định kỳ (PM), hiệu chuẩn theo kế hoạch. Chúng bị **trừ khỏi Planned Production Time** — máy không "phải" chạy lúc đó, nên không bị phạt.

**Unplanned downtime** là lần dừng xảy ra ngoài kế hoạch: hỏng máy, thiếu vật tư, chờ QC, mất điện. Chúng **làm giảm Run Time** và bị phạt vào Availability.

Trong dữ liệu dự án, `DowntimeReason` có hai cột quyết định cách tính:

| Cột | Ý nghĩa | Dùng để |
| --- | --- | --- |
| `Category` (Planned / Unplanned) | Nhóm để báo cáo, lọc Pareto | Hiển thị, nhóm |
| `CountsAgainstAvailability` (bool) | `true` = trừ vào Run Time (phạt A); `false` = trừ khỏi Planned Production Time (không phạt) | **Toán học của OEE** |

Quy ước của tài liệu này: **phép tính chỉ nhìn `CountsAgainstAvailability`**, còn `Category` chỉ để báo cáo. Nhờ vậy một nhà máy có thể chọn: "đổi model (`SET-CHANGEOVER`) là Planned nhưng vẫn phạt Availability" — vì họ muốn nhìn thấy nó trong Pareto và có mục tiêu giảm. Nhà máy khác chọn không phạt. Cùng một code, khác cấu hình. Đây chính là câu hỏi "Setup/đổi model có tính là downtime kế hoạch không" trong spec.

Bảng lý do mẫu để seed (đủ dùng cho demo, đừng làm quá 15–20 mã):

| Code | Tên | Category | CountsAgainstAvailability |
| --- | --- | --- | --- |
| `PLN-BREAK` | Nghỉ giữa ca | Planned | false |
| `PLN-MEETING` | Họp đầu ca / 5S | Planned | false |
| `PLN-PM` | Bảo trì định kỳ | Planned | false |
| `SET-CHANGEOVER` | Đổi model | Planned | true |
| `BRK-FEEDER` | Kẹt feeder | Unplanned | true |
| `BRK-NOZZLE` | Hỏng nozzle | Unplanned | true |
| `MAT-WAIT` | Chờ vật tư | Unplanned | true |
| `QC-WAIT` | Chờ QC duyệt | Unplanned | true |
| `PWR-OUT` | Mất điện | Unplanned | true |
| `OTHER` | Khác (bắt buộc ghi chú) | Unplanned | true |

Trạng thái máy nào cũng phải đi về một trong hai cách tính. Quy ước cho từng `State` của M4:

| State | Cách tính |
| --- | --- |
| `Running` | Tính vào Run Time |
| `PlannedStop` | Trừ khỏi Planned Production Time |
| `Setup` | Theo `CountsAgainstAvailability` của lý do gắn với đoạn đó (mặc định: phạt A) |
| `Down` | Unplanned; cần lý do (nếu chưa có → "cần phân loại", vẫn tính là phạt A) |
| `Idle` | Có WO đang chạy trên máy mà máy chờ (thiếu bo từ công đoạn trước, chờ hàng): tính unplanned, lý do `MAT-WAIT`/`UPSTREAM-WAIT`. Không có WO nào giao cho máy: máy không thuộc kế hoạch, không tính OEE cho khoảng đó |
| `Offline` | Mất heartbeat > 30 s (M4). Không có dữ liệu để nói máy làm gì. Quy ước: tính unplanned kèm nhãn "không có dữ liệu", chờ supervisor xác nhận |

Đây là các quy ước bạn chọn và ghi vào ARCHITECTURE.md. Điều quan trọng là **chọn một cách, viết ra, và giữ nhất quán**, vì nếu Idle/Offline tùy hứng thì hai người tính OEE ra hai con số.

#### 1.2.5 Micro stop (dừng ngắn)

Máy kẹt 15 giây, operator gạt tay, máy chạy lại. Không ai bấm Andon, không ai chọn lý do. Một ca có thể xảy ra 40 lần như thế = 10 phút, nhưng nếu buộc mọi lần dừng phải có lý do thì operator sẽ phát ngấy và chọn bừa.

Thực hành phổ biến: dừng ngắn hơn một **ngưỡng** (thường vài phút) coi là *micro stop*, **không tính vào Availability** mà để nó tự lộ ra ở Performance (vì Run Time vẫn dài nhưng số đếm thấp).

Đề xuất cho dự án (cấu hình được, ghi vào ARCHITECTURE.md):

- `MicroStopThresholdSec` mặc định **60 giây**. Đoạn `Down` ngắn hơn ngưỡng: không trừ vào Run Time, không cần lý do, không vào Pareto lý do dừng.
- Ngưỡng Andon tự động (M4, "máy Down > X giây") đặt bằng ngưỡng này, để không gọi supervisor vì một lần kẹt 20 giây.
- Đặt ngưỡng = 0 nếu muốn mọi lần dừng đều tính (khớp nghiêm ngặt công thức spec).

Ví dụ với số trong 1.2.2: 6 lần × 20 giây = 2 phút micro stop. Nếu ngưỡng 60 s, chúng nằm trong Run Time (360 phút) và chỉ làm P thấp đi một chút (2/360 ≈ 0,6 điểm). Nếu ngưỡng 0, Run Time còn 358 phút, A = 88,4%. Khác nhau nhỏ, nhưng ở xưởng thật 40 lần/ca thì khác nhiều. Vì thế cần một quy ước rõ ràng và thống nhất giữa các máy.

#### 1.2.6 TEEP (chỉ nhắc)

**TEEP (Total Effective Equipment Performance)** = OEE × Utilization, trong đó Utilization = Planned Production Time / Thời gian lịch (24 giờ × 7 ngày). OEE hỏi "trong thời gian tôi định dùng máy thì tốt đến đâu", TEEP hỏi "so với toàn bộ thời gian tồn tại, tôi khai thác máy bao nhiêu". Ví dụ OEE 80% nhưng máy chỉ chạy 1 ca/ngày, 5 ngày/tuần (Utilization ≈ 24%) thì TEEP ≈ 19%. TEEP dùng khi quyết định có nên mua máy mới hay chạy thêm ca. MVP không làm TEEP; nếu sau này cần, nó chỉ là phép nhân thêm một hệ số từ lịch ca.

#### 1.2.7 Vì sao Performance > 100% là dấu hiệu master data sai

P = (IdealCycleTime × Total Count) / Run Time. Nếu P > 100% nghĩa là máy làm được nhiều sản phẩm hơn mức lý tưởng — nhưng "lý tưởng" là mức nhanh nhất có thể, nên không có chuyện đó. Chỉ có ba cách giải thích:

1. **Ideal cycle time nhập sai (quá chậm).** Kỹ sư nhập 20 giây trong khi máy thật chạy 15 giây. Ví dụ: Run Time 360 phút, đếm 1.440 bo → ideal = 1.440 × 20 = 28.800 s, P = 28.800 / 21.600 = **133%**. Nguyên nhân số 1.
2. **Đơn vị sai.** Máy dập 1 lần ra 2 bo (panel 2-up) nhưng bộ đếm đếm số lần dập, còn cycle time tính theo bo, hoặc ngược lại.
3. **Bộ đếm sai** (nhảy số, đếm cả chạy không tải) hoặc Run Time bị tính thiếu (ví dụ không trừ đúng nghỉ giữa ca vào lúc bộ đếm vẫn tăng).

Vì thế P > 100% **không phải "kết quả tốt"** mà là **cảnh báo dữ liệu**. Quy ước của dự án: giữ nguyên giá trị P thô (không cắt về 100% âm thầm), gắn cờ `PerformanceOver100` trên `OeeSnapshot`, và hiện huy hiệu "check master data" ở dashboard. Cắt về 100% sẽ che mất lỗi: OEE trông đẹp và không ai sửa cycle time. (Ngoài đời có nơi cắt về 100% để báo cáo cho khách; nếu làm vậy phải vẫn giữ cờ cảnh báo.)

#### 1.2.8 Ngày sản xuất (production date) vs ngày lịch

Ca đêm S3 chạy **22:00 hôm nay → 06:00 hôm sau**. Nếu gộp theo ngày lịch, ca đêm bị xé làm đôi: 2 giờ vào ngày 01/10, 6 giờ vào ngày 02/10, và số liệu "ngày 02/10" bị lẫn sản lượng của hai ca khác nhau. Quản lý hỏi "ca đêm thứ Năm làm tốt không?" thì không ai trả lời được.

**Quy tắc:** mỗi ca thuộc về **ngày sản xuất (`ShiftDate`) = ngày lịch (giờ nhà máy) tại thời điểm ca bắt đầu**. Mọi sự kiện trong ca, dù xảy ra sau nửa đêm, đều mang `ShiftDate` của ngày ca bắt đầu.

Nhà máy ở UTC+7. Dữ liệu lưu UTC, nên phải đổi sang giờ nhà máy trước khi quyết định ngày:

| Thời điểm UTC (lưu DB) | Giờ nhà máy (UTC+7) | Ca | `ShiftDate` |
| --- | --- | --- | --- |
| 2026-10-01 15:00 | 01/10 22:00 | S3 (đêm) bắt đầu | 2026-10-01 |
| 2026-10-01 18:30 | **02/10** 01:30 | S3 | **2026-10-01** (không phải 02/10) |
| 2026-10-01 22:59 | 02/10 05:59 | S3 (cuối ca) | 2026-10-01 |
| 2026-10-01 23:00 | 02/10 06:00 | S1 (sáng) bắt đầu | 2026-10-02 |

Hai cách gộp sai:

- **Gộp theo ngày UTC**: ca S1 ngày 02/10 bắt đầu 06:00 giờ nhà máy = 23:00 UTC ngày 01/10, nên bị đẩy vào ngày 01/10 (dòng 4 của bảng). Ca đêm S3 trong ví dụ này lại tình cờ nằm trọn trong ngày UTC 01/10, nên lỗi không lộ ra ở dữ liệu đêm; bạn chỉ thấy khi nhìn ca sáng. Đây là kiểu lỗi chỉ xuất hiện ở một số giờ nên rất khó tìm.
- **Gộp theo ngày lịch giờ nhà máy**: xé ca đêm làm đôi (dòng 1 và dòng 2 rơi vào hai ngày lịch khác nhau).

Chỉ có "ngày bắt đầu ca (giờ nhà máy)" là đúng.

Cấu hình demo: S1 06:00–14:00, S2 14:00–22:00 (2 ca/ngày như phạm vi spec), thêm S3 22:00–06:00 (qua nửa đêm) để có thể chạy được test case và chuyển sang mô hình 2 ca kiểu ca ngày/ca đêm khi cần. `Shift.CrossesMidnight` (M1) được suy ra: `End <= Start`.

#### 1.2.9 Pareto lý do dừng và cách quản lý xưởng dùng nó

**Pareto** (nguyên lý 80/20): một số ít nguyên nhân gây ra phần lớn hậu quả. Biểu đồ Pareto = cột sắp xếp giảm dần theo thời gian mất (phút) + đường tích lũy %.

Ví dụ tuần 40, `L1-SMT-01` (tổng downtime 700 phút):

| Hạng | Lý do | Phút | % | % tích lũy |
| --- | --- | --- | --- | --- |
| 1 | `BRK-FEEDER` Kẹt feeder | 280 | 40,0 | 40,0 |
| 2 | `MAT-WAIT` Chờ vật tư | 175 | 25,0 | 65,0 |
| 3 | `SET-CHANGEOVER` Đổi model | 105 | 15,0 | 80,0 |
| 4 | `QC-WAIT` Chờ QC | 70 | 10,0 | 90,0 |
| 5 | `OTHER` | 70 | 10,0 | 100,0 |

Cách quản lý xưởng đọc và hành động:

- **Họp cải tiến hàng tuần**: mở Pareto, chọn cột đầu (40% tổng downtime), giao kỹ sư một dự án ("feeder kẹt vì reel cong, đổi nhà cung cấp/đổi cách bảo quản"). Không ai họp để bàn 10 nguyên nhân cùng lúc.
- **Hai cách sắp xếp cho hai loại hành động**: theo *tổng thời gian* thì thấy cái đau nhất (breakdown lớn); theo *số lần* thì thấy cái lặp nhiều (dừng ngắn, lỗi nhỏ). Nguyên nhân dừng 3 lần × 60 phút cần bảo trì; nguyên nhân dừng 90 lần × 2 phút cần chuẩn hóa thao tác. Dashboard nên cho đổi giữa hai chế độ.
- **Nếu `OTHER` > 20%**, danh sách lý do quá nghèo hoặc operator lười chọn. Pareto chỉ hữu ích khi dữ liệu phân loại tốt; đó là lý do có màn hình "cần phân loại" cho supervisor (bước 3 của spec).
- **Đo hiệu quả cải tiến**: sau khi sửa feeder, tuần sau cột 1 phải giảm. Pareto vừa là chẩn đoán vừa là công cụ kiểm chứng.
- Chỉ tính các lý do **thực sự làm mất thời gian** (mặc định lọc `CountsAgainstAvailability = true`); lý do Planned có thể bật thêm bằng bộ lọc.

#### 1.2.10 Nguồn dữ liệu OEE đã chốt và kiểm tra chéo bộ đếm máy vs serial scan

Spec đã chốt và bạn không được đổi:

| Đại lượng | Nguồn | Lý do |
| --- | --- | --- |
| Total Count | **Bộ đếm máy** qua MQTT (M4), tính bằng tổng delta của `counter` trong cửa sổ | Máy đếm mọi lần nó xử lý, kể cả khi chưa ai scan |
| Good Count / NG | **Kết quả scan theo serial** (M3 `ScanLog`, dù do người hay simulator vai trạm tự động gửi) | Một nguồn sự thật duy nhất cho chất lượng; trả lời được "con nào NG, lỗi gì" |
| `ngDelta` của máy | Chỉ để đối chiếu, **không** dùng cho OEE | Máy chỉ biết "bao nhiêu", không biết "con nào" |
| Ideal cycle time | Snapshot `WorkOrderOperation` (M2) | Mỗi sản phẩm một cycle time khác nhau |

Hai nguồn độc lập nên có thể **chênh nhau**. **Kiểm tra chéo:** nếu `|Total Count máy − số lần scan tại trạm| / Total Count máy > 2%` thì cảnh báo. Ví dụ trong ca mẫu: máy đếm 1.000, trạm ghi 985 scan → chênh 1,5%, bình thường (một vài bo đang giữa trạm khi ca kết thúc). Nếu ghi 940 scan → chênh 6%: hoặc máy đếm nhầm (đếm cả bo chạy không), hoặc operator bỏ qua scan, hoặc simulator lỗi.

Khi chênh lớn thì OEE **không đáng tin**, vì Good Count lấy từ scan còn Total Count lấy từ máy: thiếu scan làm Q thấp giả (ví dụ 940 scan có 910 OK → Q = 91% trong khi thực tế NG chỉ 3%). Vì thế cảnh báo này gắn vào snapshot dưới dạng cờ `CounterMismatch`, không "sửa" số.

Chú ý khi đối chiếu ở trạm Test: một bo NG được sửa xong sẽ **test lại**, nên máy đếm lượt thứ hai và `ScanLog` cũng có dòng thứ hai. Hai bên đều tính theo *lượt xử lý*, không phải theo số serial khác nhau. Vì vậy so sánh **số dòng scan** (đã loại trùng do idempotent M3) với **số đếm máy**, đừng so `COUNT(DISTINCT SerialNo)`.

### 1.3 Một ngày ở xưởng: một ca trên Line 1

Nhân vật: **chị Hạnh** (Planner), **anh Tuấn** (Operator SMT), **chị Mai** (Supervisor ca S1), **anh Long** (Maintenance), **anh Dũng** (quản lý xưởng). Máy: `L1-SMT-01`. Số liệu khớp ví dụ ở mục 1.2.2.

1. **05:50 — Chuẩn bị.** Chị Hạnh đã release WO 1.000 bo `PCB-A` cho Line 1 từ hôm trước (M2). Snapshot `WorkOrderOperation` của bước SMT ghi `IdealCycleTimeSec = 20`. Hệ thống dùng đúng con số này cho OEE của ca, kể cả nếu kỹ sư sửa routing master trong ngày.
2. **06:00–06:15 — Họp đầu ca.** Chị Mai chuyển máy sang trạng thái `PlannedStop` (lý do `PLN-MEETING`). Máy không chạy, nhưng 15 phút này bị **trừ khỏi thời gian kế hoạch**, không phạt máy.
3. **06:15 — Chạy.** Simulator (vai máy) gửi `state=Running`, bộ đếm tăng mỗi 20 giây. Vai trạm tự động gọi API scan cho từng serial với kết quả OK/NG. Dashboard hiện OEE ca hiện tại, lúc này chưa nhiều dữ liệu nên hơi dao động.
4. **07:10 — Sự cố đầu tiên.** Feeder kẹt. State chuyển `Down`, sau 60 giây hệ thống tự bật Andon (M4). Anh Tuấn gọi anh Long. **07:35** máy chạy lại; chị Mai mở Andon, chọn lý do `BRK-FEEDER`. Lần dừng 25 phút được gắn lý do.
5. **08:00–08:15, 10:00–10:45 — Nghỉ giữa ca.** Máy `Idle`. Vì thời gian này nằm trong `ShiftBreak`, nó **không** tính vào downtime, dù trạng thái máy ghi `Idle`. (Nếu code tính nhầm, Availability bị hạ oan 60 phút, đây là sai lầm hay gặp nhất, xem 1.5.)
6. **09:20 — Sự cố thứ hai.** Hết reel, phải chờ kho. Anh Tuấn bấm Andon trên kiosk. **09:32** reel về, chị Mai gắn `MAT-WAIT` (12 phút).
7. **Suốt ca — dừng ngắn.** 6 lần máy kẹt khoảng 20 giây rồi tự chạy. Không ai bấm gì, và cũng không cần: dưới ngưỡng 60 giây, chúng không thành downtime mà làm Performance thấp đi chút ít.
8. **12:40 — Sự cố thứ ba.** Sau lần đổi reel, QC phải duyệt bo đầu tiên. Máy chờ 8 phút. Chị Mai ghi `QC-WAIT`. Lần này chị lỡ quên chọn lý do nên downtime nằm ở danh sách "cần phân loại".
9. **13:00 — Job theo giờ.** Job chốt `OeeSnapshot` cho giờ 12:00–13:00, ghi `TotalCount`, `GoodCount`, A/P/Q. Ở dashboard, cột giờ 12:00 thấp hơn đường kế hoạch, đúng vào giờ có lần chờ QC.
10. **14:00 — Hết ca.** Sau 5 phút chờ dữ liệu về muộn (QoS 1 có thể trễ), job chốt snapshot cuối ca: Planned 405 phút, Run 360 phút, Total 1.000, Good 970, **A 88,9% / P 92,6% / Q 97,0% / OEE 79,8%**. Hệ thống kiểm tra chéo: máy đếm 1.000, `ScanLog` có 993 dòng, chênh 0,7% < 2%, không có cờ mismatch.
11. **14:10 — Bàn giao ca.** Chị Mai mở danh sách "cần phân loại", chọn `QC-WAIT` cho đoạn 12:40–12:48 và lưu. Vì `QC-WAIT` có `CountsAgainstAvailability = true` nên OEE không đổi; nếu chị chọn nhầm `PLN-PM` (không phạt) thì hệ thống phải tính lại snapshot của ca (xem 2.4).
12. **Thứ Sáu — họp cải tiến.** Anh Dũng mở Pareto tuần: kẹt feeder chiếm 40% downtime. Anh giao anh Long điều tra reel. Tuần sau anh mở lại Pareto để kiểm chứng.

Điểm rút ra: mỗi con số OEE truy ngược được về từng dòng dữ liệu (đoạn `Down`, lý do, dòng scan). Một hệ thống chỉ cho ra số OEE mà không giải thích được nó đến từ đâu sẽ không thuyết phục được quản lý xưởng.

### 1.4 Quy tắc nghiệp vụ và lý do tồn tại

| # | Quy tắc | Lý do |
| --- | --- | --- |
| 1 | Công thức A/P/Q/OEE đúng như mục 1.2.1, viết **một chỗ duy nhất** (pure function trong Domain) | Nếu dashboard, báo cáo, job mỗi nơi tính một kiểu thì ba con số khác nhau cho cùng một ca |
| 2 | Total Count = tổng delta bộ đếm máy; Good/NG = từ `ScanLog`; không dùng `ngDelta` | Nguồn sự thật chất lượng là serial (đã chốt) |
| 3 | Ideal cycle time lấy từ snapshot `WorkOrderOperation`; ca có nhiều WO thì cộng theo từng WO: `Σ (count_wo × ict_wo)` | Sản phẩm khác nhau, cycle time khác nhau; sửa master không làm sai lịch sử |
| 4 | Nghỉ giữa ca và PlannedStop loại khỏi Planned Production Time; nếu hai khoảng chồng nhau (họp đúng giờ nghỉ) thì chỉ trừ **phần hợp**, không trừ hai lần | Trừ hai lần làm Planned Time nhỏ hơn thực tế, A/P tăng ảo |
| 5 | Downtime rơi vào nghỉ giữa ca hoặc PlannedStop không tính là unplanned | Máy được phép dừng lúc đó |
| 6 | Đoạn `Down` ngắn hơn `MicroStopThresholdSec` không trừ vào Run Time | Tránh bắt operator giải thích từng lần kẹt 20 giây; P tự phản ánh |
| 7 | Downtime chưa có lý do vẫn tính là unplanned, và nằm trong danh sách "cần phân loại" | Nếu mặc định "không phạt" thì mọi người có động cơ để trống lý do |
| 8 | Đổi lý do của một đoạn downtime trong ca đã chốt phải tính lại snapshot của ca đó | Đổi từ "phạt" sang "không phạt" làm A thay đổi |
| 9 | P > 100% hoặc Good > Total: giữ giá trị thô, gắn cờ cảnh báo; không cắt im lặng | Che lỗi master data/bộ đếm là tệ hơn số xấu |
| 10 | Kiểm tra chéo: chênh > 2% giữa Total Count máy và số dòng scan → cờ `CounterMismatch` | Hai nguồn độc lập nên đối chiếu được; chênh lớn nghĩa là một trong hai sai |
| 11 | Mọi tính toán thời gian dùng UTC trong DB; `ShiftDate` = ngày (giờ nhà máy) lúc ca bắt đầu | Tránh xé ca đêm và lệch múi giờ |
| 12 | Tổng hợp nhiều giờ/nhiều máy: **cộng thời gian và số đếm rồi chia**, không lấy trung bình cộng A/P/Q | Trung bình các tỷ lệ cho kết quả sai khi mẫu số khác nhau (xem 1.5) |
| 13 | Snapshot idempotent: tính lại cùng khoảng thời gian cho cùng kết quả và ghi đè, không nhân đôi dòng | Job chạy lại, server khởi động lại, dữ liệu về muộn |
| 14 | Chia cho 0 (Planned = 0, Total = 0) trả về "không xác định" chứ không phải 0% hay ngoại lệ | Giờ nghỉ trưa có Planned Time = 0; hiện "0%" là sai, ném lỗi thì job chết |

### 1.5 Sai lầm và sự cố thường gặp ngoài đời

1. **Nghỉ giữa ca bị tính là downtime.** Máy `Idle` lúc nghỉ trưa, code cộng vào downtime, Availability tụt 12 điểm. Cách kiểm: viết test "máy Idle trọn giờ nghỉ → A không đổi".
2. **Trừ nghỉ giữa ca hai lần** khi PlannedStop trùng giờ nghỉ (họp trong giờ giải lao). Xử lý bằng phép hợp khoảng thời gian.
3. **Ideal cycle time sai** (nhập theo giây/panel nhưng đếm theo bo, hoặc lấy thời gian chu kỳ đo được thay vì thiết kế). Triệu chứng: P > 100% hoặc luôn đúng 100% (vì ai đó chỉnh cycle time để "cho đẹp").
4. **Bộ đếm reset** sau khi máy khởi động lại, code lấy `count_cuối − count_đầu` ra số âm hoặc nhỏ bất thường. Cần xử lý theo quy tắc reset của M4 (delta khi counter giảm = giá trị mới).
5. **Ngày lịch thay vì ngày sản xuất**: báo cáo "sản lượng ngày 02/10" chứa 2 giờ đêm của ca hôm trước. Sau đó ai đó "vá" bằng cách trừ thủ công trên Excel và không ai còn tin báo cáo.
6. **Trung bình cộng các OEE giờ** để ra OEE ca. Ví dụ: giờ 1 chạy 60 phút được 90%, giờ 2 chỉ chạy 6 phút được 40%; trung bình cộng = 65%, nhưng OEE thật (theo thời gian) khoảng 85%. Phải cộng thời gian và số đếm rồi chia.
7. **NG không được nhập** (operator bỏ qua scan NG) khiến Q = 100% giả. Kiểm tra chéo với bộ đếm là một trong những cách phát hiện.
8. **Danh sách lý do quá dài** (60 mã), operator chọn mã đầu tiên hoặc `OTHER`. Pareto vô dụng. Giữ 10–20 mã, mỗi mã một câu mô tả rõ, xem lại hàng quý.
9. **Lạm dụng OEE để chấm điểm operator.** OEE là chỉ số của *hệ thống* (máy, vật tư, kế hoạch). Khi nó thành công cụ phạt, người ta bắt đầu "làm đẹp" số: chọn lý do Planned, hạ Planned Time, sửa cycle time. Thiết kế MES phải hạn chế gian lận (audit khi sửa lý do, ghi ai sửa) nhưng quan trọng hơn là cách quản lý xưởng dùng nó.
10. **So sánh OEE giữa các máy khác loại** (SMT tốc độ cao với trạm test thủ công). OEE hợp để so một máy với chính nó theo thời gian và để tìm điểm nghẽn, không hợp để xếp hạng máy khác bản chất.
11. **Tính OEE cho máy không phải nút cổ chai.** Cải thiện OEE máy phía trước nút cổ chai chỉ tạo thêm WIP. Cải tiến nên tập trung vào máy quyết định sản lượng của cả line.
12. **Dữ liệu về muộn/đến trùng** (QoS 1 trùng `msgId`, mất mạng) làm snapshot theo giờ chốt quá sớm, sai. Cần thời gian đệm trước khi chốt và khả năng tính lại.

### 1.6 Bảng thuật ngữ

| English | Tiếng Việt | 日本語 | Giải thích ngắn |
| --- | --- | --- | --- |
| OEE (Overall Equipment Effectiveness) | Hiệu suất thiết bị tổng thể | 設備総合効率 | A × P × Q |
| Availability | Tỷ lệ sẵn sàng / thời gian vận hành | 時間稼働率 | Run Time / Planned Production Time |
| Performance | Hiệu suất tốc độ | 性能稼働率 | Thời gian lý tưởng / Run Time |
| Quality | Tỷ lệ đạt | 良品率 | Good / Total |
| Planned Production Time | Thời gian sản xuất theo kế hoạch | 負荷時間 | Thời gian ca trừ nghỉ và dừng có kế hoạch |
| Run Time | Thời gian máy chạy | 稼働時間 | Planned − Unplanned downtime |
| Ideal cycle time | Thời gian chu kỳ chuẩn | 基準サイクルタイム（理論サイクルタイム） | Thời gian nhanh nhất để làm 1 sản phẩm |
| Total Count | Tổng số lượng (máy đếm) | 総生産数 | Từ bộ đếm máy |
| Good Count | Số lượng đạt | 良品数 | Từ kết quả scan theo serial |
| Downtime | Thời gian dừng máy | 停止時間 | Khoảng máy không sản xuất |
| Planned downtime | Dừng có kế hoạch | 計画停止 | Nghỉ, họp, PM |
| Unplanned downtime | Dừng ngoài kế hoạch | 突発停止（非計画停止） | Hỏng máy, thiếu vật tư |
| Micro stop / Minor stop | Dừng ngắn | チョコ停 | Dừng vài giây đến vài phút, tự hết hoặc gạt tay |
| Six Big Losses | Sáu tổn thất lớn | 六大ロス | Khung phân loại mất mát của TPM |
| Breakdown | Hỏng máy | 故障ロス | Tổn thất Availability |
| Setup / Changeover | Đổi model / chỉnh máy | 段取り替え・調整ロス | Tổn thất Availability |
| Reduced speed | Chạy chậm | 速度低下ロス | Tổn thất Performance |
| Defects & rework | Lỗi và làm lại | 不良・手直しロス | Tổn thất Quality |
| Startup loss | Lỗi giai đoạn khởi động | 立上げロス | NG tập trung sau khi bật máy/đổi model |
| TEEP | Hiệu suất thiết bị theo thời gian lịch | TEEP（カレンダー時間基準） | OEE × Utilization |
| Pareto chart | Biểu đồ Pareto | パレート図 | Cột giảm dần + % tích lũy |
| Production date | Ngày sản xuất | 生産日 | Ngày ca bắt đầu, giờ nhà máy |
| Shift | Ca làm việc | 直（シフト） | S1/S2/S3 |
| Night shift | Ca đêm | 夜勤 | Ca qua nửa đêm |
| Break | Nghỉ giữa ca | 休憩 | Loại khỏi thời gian kế hoạch |
| Counter | Bộ đếm | カウンタ | Tích lũy trên máy, có thể reset |
| Andon | Đèn/cảnh báo gọi hỗ trợ | アンドン | Gọi supervisor khi máy dừng |

---

## 2. Kiến thức lập trình

Các chủ đề đã dạy ở module trước không nhắc lại: MQTT/counter/reset và SignalR (Module 4), CQRS handler và FluentValidation (Module 1), EF Core interceptor/Testcontainers (Module 0), state machine (Module 2). Module này tập trung vào: hàm tính thuần, thời gian, job theo lịch, SQL tổng hợp, và biểu đồ WPF.

### 2.1 Hàm tính OEE thuần (pure function) và unit test bằng số tính tay

**Nó là gì.** Hàm thuần là hàm chỉ phụ thuộc đầu vào, không đọc DB, không gọi `DateTime.UtcNow`, không ghi log, không thay đổi trạng thái. Cùng đầu vào luôn cùng đầu ra.

**Vấn đề nó giải quyết.** Cách "ngây thơ": tính OEE trong controller, trong SQL view, và một lần nữa trong ViewModel WPF. Sau vài tuần ba nơi cho ba kết quả, và bạn không thể chứng minh nơi nào đúng vì test được nó cần dựng cả DB. Với hàm thuần, toàn bộ luật nghiệp vụ nằm trong một hàm 30 dòng có thể test trong vài mili giây, và mọi nơi khác (job, API realtime, báo cáo) chỉ *gọi* nó.

**Khái niệm cốt lõi.**

- Tách hai bước: (1) **gom dữ liệu**: đọc DB, cắt đoạn thời gian vào cửa sổ, tính tổng phút nghỉ/dừng/số đếm (phần có I/O); (2) **tính**: đưa các con số đó vào hàm thuần. Bước 1 test bằng integration test, bước 2 test bằng số tính tay.
- Đầu vào là `record` gồm các *tổng* (TimeSpan, số đếm), không phải danh sách sự kiện thô.
- Kết quả dùng `double?` cho các tỷ lệ: `null` nghĩa là "không xác định" (mẫu số 0), khác với 0%. Kèm cờ cảnh báo (`[Flags] enum`).
- Ca có nhiều WO: đầu vào nhận danh sách `ProductionSegment(IdealCycleSec, TotalCount)`, ideal time = Σ (cycle × count).
- Dùng `double` cho tỷ lệ là đủ (đây là số thống kê, không phải tiền); thời gian dùng `TimeSpan`, số đếm dùng `long`.

**Snippet — hàm tính (C#, .NET 10):**

```csharp
namespace MiniMes.Domain.Oee;

public readonly record struct ProductionSegment(double IdealCycleSec, long TotalCount);

public sealed record OeeInput(TimeSpan ShiftTime, TimeSpan BreakTime, TimeSpan PlannedStop,
    TimeSpan UnplannedDowntime, IReadOnlyList<ProductionSegment> Segments, long GoodCount);

[Flags] public enum OeeWarning { None = 0, PerformanceOver100 = 1, GoodExceedsTotal = 2, DowntimeExceedsPlanned = 4 }

public sealed record OeeResult(TimeSpan PlannedTime, TimeSpan RunTime, long TotalCount, long GoodCount,
    double IdealSeconds, double? Availability, double? Performance, double? Quality, double? Oee, OeeWarning Warnings);

public static class OeeCalculator
{
    public static OeeResult Calculate(OeeInput x)
    {
        var planned = x.ShiftTime - x.BreakTime - x.PlannedStop;
        long total = x.Segments.Sum(s => s.TotalCount);
        double ideal = x.Segments.Sum(s => s.IdealCycleSec * s.TotalCount);   // Σ cycle × count
        if (planned <= TimeSpan.Zero)   // giờ nghỉ trưa: không có thời gian kế hoạch -> "không xác định"
            return new(TimeSpan.Zero, TimeSpan.Zero, total, x.GoodCount, ideal, null, null, null, null, OeeWarning.None);

        var w = OeeWarning.None;
        var run = planned - x.UnplannedDowntime;
        if (run < TimeSpan.Zero) { run = TimeSpan.Zero; w |= OeeWarning.DowntimeExceedsPlanned; }

        double a = run / planned;                                          // TimeSpan / TimeSpan = double
        double? p = run > TimeSpan.Zero ? ideal / run.TotalSeconds : null;  // Run = 0 -> không xác định
        double? q = total > 0 ? (double)x.GoodCount / total : null;
        if (p > 1.0) w |= OeeWarning.PerformanceOver100;                   // KHÔNG cắt về 100%, chỉ gắn cờ
        if (x.GoodCount > total) w |= OeeWarning.GoodExceedsTotal;
        double? oee = p is { } pp && q is { } qq ? a * pp * qq : 0.0;      // không sản xuất gì -> OEE = 0
        return new(planned, run, total, x.GoodCount, ideal, a, p, q, oee, w);
    }
}
```

**Snippet — test khớp số tính tay (xUnit):**

```csharp
[Fact]
public void Ca_8_gio_mau_khop_so_tinh_tay()
{
    var input = new OeeInput(
        ShiftTime: TimeSpan.FromMinutes(480), BreakTime: TimeSpan.FromMinutes(60),
        PlannedStop: TimeSpan.FromMinutes(15), UnplannedDowntime: TimeSpan.FromMinutes(45),
        Segments: [new ProductionSegment(20, 1000)], GoodCount: 970);

    var r = OeeCalculator.Calculate(input);

    Assert.Equal(405, r.PlannedTime.TotalMinutes);
    Assert.Equal(360, r.RunTime.TotalMinutes);
    Assert.Equal(0.8889, r.Availability!.Value, 4);            // 360 / 405
    Assert.Equal(0.9259, r.Performance!.Value, 4);             // 20.000 / 21.600
    Assert.Equal(0.97,   r.Quality!.Value, 4);
    Assert.Equal(19400.0 / 24300.0, r.Oee!.Value, 6);          // công thức rút gọn ideal × good / planned
    Assert.Equal(OeeWarning.None, r.Warnings);
}
```

**Bảng test tối thiểu** (mỗi dòng một `[Fact]` hoặc một `[InlineData]`):

| Case | Đầu vào chính | Kỳ vọng |
| --- | --- | --- |
| Ca mẫu (ở trên) | 480/60/15/45, 1.000 bo, ct 20 s, good 970 | A 0,8889, P 0,9259, Q 0,97, OEE 0,7984 |
| Không dừng, chạy đúng chuẩn | planned 405, down 0, count = 405×60/20 = 1.215, good = 1.215 | A = P = Q = 1, OEE = 1 |
| Performance > 100% | Run 360 phút, count 1.440, ct 20 s | P = 1,3333, cờ `PerformanceOver100` |
| Giờ nghỉ trưa | shift 60, break 60 | Mọi tỷ lệ `null`, không ném lỗi |
| Máy chưa đếm gì | Run > 0, total 0 | P = 0, Q = `null`, OEE = 0 |
| Downtime lớn hơn Planned | planned 60, down 90 | Run = 0, cờ `DowntimeExceedsPlanned`, OEE = 0 |
| Nhiều WO trong ca | 2 segment: (20 s × 600) + (30 s × 200) | ideal = 12.000 + 6.000 = 18.000 s |
| Good > Total | good 1.010, total 1.000 | Q = 1,01, cờ `GoodExceedsTotal` |

Ngoài ra thêm một **test tính chất (invariant)**: với dữ liệu ngẫu nhiên hợp lệ, `A × P × Q` phải bằng `ideal × good / planned` (sai số 1e-9). Nếu công thức bị sửa sai một chỗ, test này bắt được dù bạn chưa nghĩ ra ca test nào cụ thể.

**Bẫy thường gặp.**

- Trộn phút và giây trong P: dùng `run.TotalSeconds` và cycle time bằng giây, đừng để một chỗ dùng phút.
- Chia số nguyên: `good / total` với hai `long` cho 0. Ép kiểu `(double)` trước khi chia.
- So sánh `double` bằng `Assert.Equal(0.8889, x)` sẽ fail vì làm tròn; dùng overload có `precision`.
- Đưa `DateTime.UtcNow` vào hàm này "cho tiện" (để tính ca đang chạy). Đừng: nó phá tính thuần; ca đang chạy thì bên ngoài cắt cửa sổ đến "bây giờ" rồi đưa số vào.
- Tổng hợp nhiều giờ: cộng `PlannedTime`, `RunTime`, `TotalCount`, `GoodCount`, `IdealSeconds` từ các snapshot rồi tính lại bằng cùng công thức. Vì vậy `OeeSnapshot` nên lưu thêm `IdealSeconds` (bổ sung nhỏ so với spec, không mâu thuẫn); suy ngược từ `P × RunTime` cũng được nhưng mất chính xác vì làm tròn.

### 2.2 Xử lý thời gian: UTC, giờ nhà máy, ngày sản xuất, ca qua nửa đêm

**Nó là gì.** Quy ước của dự án: DB lưu UTC (`datetime2`); nhà máy có một múi giờ cấu hình (`Factory:TimeZoneId`); ca làm việc định nghĩa bằng giờ địa phương (06:00, 22:00) vì công nhân không nghĩ bằng UTC.

**Vấn đề nó giải quyết.** Cách ngây thơ: `GROUP BY CAST(Ts AS date)` hoặc `DateTime.Now.Date`. Kết quả: ca đêm bị xé đôi (mục 1.2.8), server chạy múi giờ khác máy dev cho số liệu khác, và báo cáo lệch 7 tiếng.

**Khái niệm cốt lõi.**

- `TimeZoneInfo.ConvertTimeFromUtc` / `ConvertTimeToUtc` để chuyển qua lại. Id múi giờ khác nhau theo hệ điều hành (Windows: `"SE Asia Standard Time"`, Linux/macOS: `"Asia/Ho_Chi_Minh"`). Từ .NET 6 có thể dùng cả hai dạng trên cả hai hệ điều hành nếu ICU khả dụng (nên kiểm tra trên máy bạn); an toàn nhất là đặt id trong cấu hình và ghi chú.
- `DateOnly`, `TimeOnly` (từ .NET 6) cho ngày sản xuất và giờ bắt đầu/kết thúc ca. EF Core 8 ánh xạ chúng sang `date`/`time` của SQL Server.
- Ca qua nửa đêm: `CrossesMidnight = End <= Start`, thời lượng = `End − Start` cộng 24 giờ nếu âm. Khi tìm ca của một thời điểm, thử cả **ngày hôm nay** và **ngày hôm qua** làm ngày bắt đầu ca.
- `ShiftDate` = ngày bắt đầu ca (giờ nhà máy). Cửa sổ ca lưu ở dạng cặp UTC `[StartUtc, EndUtc)`, nửa mở để không tính trùng giây ranh giới.
- Nghỉ giữa ca nên tính bằng **độ lệch từ đầu ca** (`StartOffset`, `Duration`) chứ không phải giờ tuyệt đối; nghỉ lúc 02:00 của ca đêm không cần xử lý đặc biệt.
- Buckets theo giờ căn theo *đầu ca*. MVP giả định ca bắt đầu đúng giờ chẵn để `Hour` trùng giờ đồng hồ.
- **[v2] Ngày nghỉ.** Ca không được lên lịch (ngày lễ trong `PlantCalendar`) thì **không có cửa sổ ca**, nên Planned Production Time = 0 và không sinh `OeeSnapshot`. Máy vẫn có thể gửi dữ liệu hôm đó (chạy thử, bảo trì): thời gian đó nằm **ngoài** mọi ca, giống giờ giữa hai ca, không làm OEE xấu đi. Ngược lại Chủ nhật tăng ca (`IsWorkingDay = true`) thì có cửa sổ ca bình thường. Quy tắc tra cứu nằm ở một chỗ (`IsScheduled`, cùng logic với `IsShiftScheduled` của M1) để M1 và M5 không lệch nhau.

**Snippet — tìm ca và ngày sản xuất từ một thời điểm UTC (C#):**

```csharp
public sealed record ShiftDef(string Code, TimeOnly Start, TimeOnly End, DayOfWeek[] Days)
{
    public TimeSpan Duration
    {
        get { var d = End.ToTimeSpan() - Start.ToTimeSpan();
              return d <= TimeSpan.Zero ? d + TimeSpan.FromHours(24) : d; }   // ca qua nửa đêm
    }
    public bool RunsOn(DayOfWeek startDay) => Days.Contains(startDay);        // theo ngày BẮT ĐẦU ca
}

// [v2] Ngoại lệ lịch (M1 PlantCalendar): ngày lễ / tăng ca
public sealed record CalendarException(DateOnly Date, bool IsWorkingDay, string[]? ShiftCodes);

public sealed record ShiftWindow(DateOnly ShiftDate, string ShiftCode, DateTime StartUtc, DateTime EndUtc);

public sealed class ShiftCalendar(TimeZoneInfo factoryTz, IReadOnlyList<ShiftDef> shifts,
                                  IReadOnlyDictionary<DateOnly, CalendarException> exceptions)   // [v2]
{
    // [v2] Cùng quy tắc với IsShiftScheduled của M1: có ngoại lệ thì ngoại lệ quyết định
    public bool IsScheduled(ShiftDef s, DateOnly date) =>
        exceptions.TryGetValue(date, out var ex)
            ? ex.IsWorkingDay && (ex.ShiftCodes is null || ex.ShiftCodes.Contains(s.Code))
            : s.RunsOn(date.DayOfWeek);

    public ShiftWindow? Resolve(DateTime utc)   // utc phải có Kind = Utc (xem bẫy)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, factoryTz);
        var today = DateOnly.FromDateTime(local);
        // Ca đêm bắt đầu hôm qua vẫn có thể đang chạy: thử cả hôm nay và hôm qua
        foreach (var date in new[] { today, today.AddDays(-1) })
        foreach (var s in shifts.Where(s => IsScheduled(s, date)))          // [v2] thay cho RunsOn
        {
            var startLocal = date.ToDateTime(s.Start);          // Kind = Unspecified
            var endLocal = startLocal + s.Duration;
            if (local >= startLocal && local < endLocal)
                return new ShiftWindow(date, s.Code,
                    TimeZoneInfo.ConvertTimeToUtc(startLocal, factoryTz),
                    TimeZoneInfo.ConvertTimeToUtc(endLocal, factoryTz));
        }
        return null;                                            // ngoài mọi ca
    }
}
```

**Snippet — test ca qua nửa đêm (dùng đúng bảng ở mục 1.2.8):**

```csharp
[Theory]
[InlineData("2026-10-01T15:00:00Z", "2026-10-01", "S3")]   // 22:00 local: đầu ca đêm
[InlineData("2026-10-01T18:30:00Z", "2026-10-01", "S3")]   // 01:30 ngày 02/10 local: vẫn ShiftDate 01/10
[InlineData("2026-10-01T22:59:59Z", "2026-10-01", "S3")]   // 05:59:59 local: giây cuối
[InlineData("2026-10-01T23:00:00Z", "2026-10-02", "S1")]   // 06:00 local: sang ca sáng ngày 02/10
public void Resolve_dung_ShiftDate(string utcText, string expectedDate, string expectedShift)
{
    var utc = DateTime.Parse(utcText, null, DateTimeStyles.RoundtripKind);   // Kind = Utc
    var w = _calendar.Resolve(utc);
    Assert.Equal(DateOnly.Parse(expectedDate), w!.ShiftDate);
    Assert.Equal(expectedShift, w.ShiftCode);
}
```

**Snippet — phép hợp khoảng thời gian, để không trừ nghỉ giữa ca và PlannedStop hai lần:**

```csharp
public readonly record struct Interval(DateTime StartUtc, DateTime EndUtc)
{
    public TimeSpan Length => EndUtc > StartUtc ? EndUtc - StartUtc : TimeSpan.Zero;
    public Interval Clip(Interval w) =>                       // cắt vào cửa sổ [w.Start, w.End)
        new(StartUtc > w.StartUtc ? StartUtc : w.StartUtc, EndUtc < w.EndUtc ? EndUtc : w.EndUtc);
}

public static class IntervalMath
{
    public static TimeSpan UnionLength(IEnumerable<Interval> items)   // phần chồng nhau chỉ tính một lần
    {
        var total = TimeSpan.Zero; DateTime? s = null, e = null;
        foreach (var i in items.Where(x => x.Length > TimeSpan.Zero).OrderBy(x => x.StartUtc))
        {
            if (e is null || i.StartUtc > e)                      // khoảng mới, rời khoảng trước
            {
                if (e is not null) total += e.Value - s!.Value;
                s = i.StartUtc; e = i.EndUtc;
            }
            else if (i.EndUtc > e) e = i.EndUtc;                  // nối dài khoảng hiện tại
        }
        return e is null ? total : total + (e.Value - s!.Value);
    }
    // Downtime tính thật = |D \ E| = |D ∪ E| − |E|, với E = nghỉ ∪ PlannedStop, D = đoạn dừng bị phạt.
    // Planned Production Time = Thời gian ca − |E|.  (D, E đều đã Clip vào cửa sổ tính.)
}
```

**Bẫy thường gặp.**

- **`DateTimeKind` mất khi đọc từ DB.** EF Core đọc `datetime2` ra `DateTime` với `Kind = Unspecified`. `ConvertTimeFromUtc` coi `Unspecified` là UTC (chạy được), nhưng `ToLocalTime()` coi là giờ local của server (sai). Khắc phục: value converter EF gắn `DateTimeKind.Utc` khi đọc, hoặc dùng `DateTimeOffset` cho API và giữ `DateTime` UTC cho DB.
- **Giờ chuyển mùa (DST)**: Việt Nam không có, nên MVP bỏ qua; nhưng nếu đổi múi giờ sang nơi có DST thì ca 22:00–06:00 có thể dài 7 hoặc 9 giờ và `ConvertTimeToUtc` ném lỗi với giờ không tồn tại. Đây là lý do cửa sổ ca lưu ở UTC sau khi tính.
- **Giờ máy vs giờ server**: dùng `Ts` (giờ máy) để xếp thứ tự và chia cửa sổ; nếu M4 báo lệch giờ vượt ngưỡng thì dùng `ReceivedAt`. Đừng trộn hai thứ trong cùng một phép tính.
- **Cửa sổ đóng/mở**: dùng `Ts >= from AND Ts < to`, không dùng `BETWEEN` (tính trùng sự kiện đúng ranh giới giờ).

### 2.3 TimeProvider và FakeTimeProvider (test theo thời gian)

**Nó là gì.** `TimeProvider` là lớp trừu tượng có sẵn từ .NET 8 (`System.TimeProvider`, dự án dùng .NET 10) cung cấp "bây giờ" và bộ hẹn giờ. `TimeProvider.System` là bản thật. Gói `Microsoft.Extensions.TimeProvider.Testing` cung cấp `FakeTimeProvider` để test điều khiển thời gian bằng tay.

**Vấn đề nó giải quyết.** Cách ngây thơ: gọi `DateTime.UtcNow` trong handler/job. Test "job chốt snapshot lúc 14:05" phải `Thread.Sleep` hoặc phụ thuộc giờ máy chạy test, nên chạy lúc 3 giờ sáng thì fail, hoặc phải tự viết `IClock`. `TimeProvider` là chuẩn chung nên thư viện khác (như `PeriodicTimer`) cũng nhận được.

**Khái niệm cốt lõi.**

- Đăng ký DI: `services.AddSingleton(TimeProvider.System);`. Trong test: `new FakeTimeProvider(startTime)`.
- Đọc giờ: `clock.GetUtcNow()` (trả `DateTimeOffset`). Không dùng `DateTime.UtcNow` ở Application/Domain/Infrastructure.
- Test: `fake.Advance(TimeSpan.FromMinutes(5))` hoặc `fake.SetUtcNow(...)`.
- Áp dụng trong module này: (1) cắt đoạn `EndAt = null` (state hiện tại) đến "bây giờ"; (2) job chốt snapshot; (3) quyết định giờ đã đủ trễ để chốt (grace).

**Snippet — handler OEE ca hiện tại dùng TimeProvider, và test:**

```csharp
public sealed class GetCurrentShiftOeeHandler(ShiftCalendar cal, IOeeInputReader reader, TimeProvider clock)
{
    public async Task<OeeResult?> Handle(string machineCode, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;              // không dùng DateTime.UtcNow
        var shift = cal.Resolve(now);
        if (shift is null) return null;
        // Ca chưa kết thúc: cắt cửa sổ đến "bây giờ" để Planned Time không tính cả phần tương lai
        var window = new Interval(shift.StartUtc, now < shift.EndUtc ? now : shift.EndUtc);
        var input = await reader.ReadAsync(machineCode, window, ct);   // I/O nằm ở đây
        return OeeCalculator.Calculate(input);                          // hàm thuần
    }
}

[Fact]
public async Task Giua_ca_chi_tinh_den_bay_gio()
{
    // 2026-10-01 03:00Z = 10:00 local, đang giữa ca S1 (06:00–14:00)
    var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 3, 0, 0, TimeSpan.Zero));
    var handler = new GetCurrentShiftOeeHandler(_calendar, _fakeReader, clock);
    var r = await handler.Handle("L1-SMT-01", CancellationToken.None);
    Assert.Equal(_fakeReader.LastWindow.EndUtc, clock.GetUtcNow().UtcDateTime);
}
```

**Bẫy thường gặp.**

- Quên: `Planned Production Time` của ca đang chạy phải tính đến "bây giờ", không phải hết ca; nếu không, lúc 07:00 máy mới chạy được 45 phút nhưng mẫu số là 405 phút, A = 45/405 = 11%.
- `FakeTimeProvider` không tự tiến; test loop phải gọi `Advance`.
- Tên method/constructor của `FakeTimeProvider` và `PeriodicTimer` nhận `TimeProvider` (xem 2.4): mình tin là đúng từ .NET 8 trở lên, nhưng nên kiểm tra tài liệu gói `Microsoft.Extensions.TimeProvider.Testing` nếu compiler báo không có.

### 2.4 Job theo lịch: BackgroundService + PeriodicTimer, thiết kế "bắt kịp" (catch-up)

**Nó là gì.** `BackgroundService` (đã học ở Module 4) là chỗ chạy tác vụ nền trong host ASP.NET Core. `PeriodicTimer` (từ .NET 6) là bộ hẹn giờ theo kiểu `await`: `await timer.WaitForNextTickAsync(ct)` trả `true` mỗi chu kỳ và không chồng lượt (nếu lượt trước chưa xong thì tick sau chờ).

**Vấn đề nó giải quyết.** Cách ngây thơ: `System.Threading.Timer` hoặc `while(true){ ...; await Task.Delay(1h); }` rồi "cứ đúng phút 0 thì chốt". Server khởi động lại lúc 13:30 thì snapshot 13:00 không bao giờ được tạo. `Task.Delay` trôi dần và không tôn trọng giờ ca. Cách đúng: job chạy **thường xuyên (mỗi phút)** và mỗi lần **tự hỏi "còn khoảng nào đã đến hạn chốt mà chưa chốt?"**. Bị tắt 3 giờ thì lần chạy kế tiếp chốt bù cả 3 giờ.

**Khái niệm cốt lõi.**

- **Bucket** = một cặp (máy, ca, giờ). Bucket đến hạn chốt khi `EndUtc + Grace <= now`. `Grace` (ví dụ 5 phút) chờ dữ liệu MQTT về muộn/trùng.
- **Idempotent upsert:** tìm dòng theo khóa `(MachineId, ShiftDate, ShiftId, Hour)`; có thì ghi đè, không thì thêm. Unique index trên bốn cột này bảo vệ chống hai lần chạy song song. Dòng tổng ca dùng `Hour = NULL` (unique index của SQL Server coi NULL bằng nhau nên chỉ có một dòng tổng ca cho mỗi khóa).
- **Cờ chốt:** `IsFinal` cho biết đã chờ đủ grace. Dòng chưa final có thể tính lại; dòng final chỉ tính lại khi bị "làm bẩn" (dirty) bởi việc đổi lý do downtime (mục 1.4 quy tắc 8): đặt `IsFinal = false` cho snapshot của ca đó, lượt job kế tiếp sẽ tính lại.
- **Scope:** `BackgroundService` là singleton còn `DbContext` là scoped, nên phải tạo scope mỗi lượt (`IServiceScopeFactory`).
- **Một instance:** MVP chạy một API. Nếu sau này chạy nhiều instance thì cần khóa phân tán hoặc chỉ một node chạy job; unique index chỉ bảo vệ dữ liệu, không ngăn tính toán trùng.
- **Realtime vs định kỳ** (ghi vào README theo yêu cầu spec): OEE ca hiện tại tính *theo yêu cầu* bằng chính `OeeCalculator` (có thể cache 10–15 giây); snapshot theo giờ/ca dành cho lịch sử và báo cáo. Realtime cho số tức thời nhưng tốn truy vấn mỗi lần mở dashboard và số nhảy khi dữ liệu về muộn; định kỳ dễ kiểm chứng (số đã chốt không đổi trừ khi có lý do) và đủ cho báo cáo. Dùng cả hai vì chúng phục vụ hai nhu cầu khác nhau, nhưng chỉ có **một** hàm tính.

**Snippet — worker (C#, phác thảo; kiểm tra chữ ký `PeriodicTimer` nhận `TimeProvider` ở .NET 10):**

```csharp
public sealed class OeeSnapshotWorker(IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<OeeSnapshotWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Chạy mỗi phút; truyền TimeProvider để test có thể tua thời gian bằng FakeTimeProvider
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<OeeSnapshotService>();
                await svc.CloseDueBucketsAsync(ct);          // chốt mọi bucket đến hạn, kể cả bù
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "OEE snapshot run failed; will retry next tick");   // không để job chết
            }
        }
    }
}
```

**Snippet — chốt các bucket đến hạn, idempotent (phác thảo):**

```csharp
public async Task CloseDueBucketsAsync(CancellationToken ct)
{
    var now = clock.GetUtcNow().UtcDateTime;
    var due = await FindDueBucketsAsync(now - Grace, ct);     // (máy, ca, giờ): End + Grace <= now, chưa IsFinal
    foreach (var b in due)
    {
        var input  = await reader.ReadAsync(b.MachineCode, b.Window, ct);   // gom dữ liệu (I/O)
        var result = OeeCalculator.Calculate(input);                        // tính (thuần)
        var row = await db.OeeSnapshots.SingleOrDefaultAsync(x =>
                      x.MachineId == b.MachineId && x.ShiftDate == b.ShiftDate &&
                      x.ShiftId == b.ShiftId && x.Hour == b.Hour, ct)
                  ?? db.OeeSnapshots.Add(OeeSnapshot.CreateFor(b)).Entity;
        row.Apply(result, calculatedAt: now, isFinal: true);                // ghi đè = idempotent
    }
    await db.SaveChangesAsync(ct);
}
```

**Bẫy thường gặp.**

- Chốt đúng phút 0 mà không có grace: sự kiện đến muộn 20 giây làm snapshot thiếu.
- Tính lại nhưng quên cập nhật `CalculatedAt`: sau này không biết snapshot cũ hay mới.
- Exception không được bắt trong `ExecuteAsync` sẽ làm dừng cả host (mặc định từ .NET 6). Luôn `try/catch` bên trong vòng lặp, chỉ cho `OperationCanceledException` đi qua.
- Test job bằng `Thread.Sleep`. Dùng `FakeTimeProvider` + gọi trực tiếp `CloseDueBucketsAsync` để test logic; chỉ test worker bằng một integration test mỏng.
- Ca vừa kết thúc lúc 14:00 nhưng snapshot giờ cuối 13:00–14:00 và snapshot tổng ca cần chốt cùng lúc; tính tổng ca bằng cách **cộng** các snapshot giờ hoặc tính lại từ dữ liệu thô? Chọn tính lại từ dữ liệu thô (qua cùng `ReadAsync`) để không phụ thuộc thứ tự chốt, đồng thời viết một test bảo đảm hai cách cho cùng kết quả.

### 2.5 SQL tổng hợp: window function, cắt đoạn vào cửa sổ, Pareto

**Nó là gì.** Window function (`LAG`, `SUM() OVER`, `ROW_NUMBER`) tính trên "cửa sổ" các dòng liên quan mà không gom mất dòng như `GROUP BY`. Ở module này bạn dùng ba việc: (1) `LAG` để lấy delta của bộ đếm tích lũy; (2) cắt đoạn thời gian vào cửa sổ để đếm đúng phần giao; (3) `SUM() OVER` để tính % và % tích lũy của Pareto.

**Vấn đề nó giải quyết.** Cách ngây thơ: kéo hàng trăm nghìn `MachineEvent` về C# rồi vòng lặp so từng cặp. Chậm, tốn RAM, và lặp logic reset bộ đếm ở nhiều nơi. Với bảng insert-only hàng triệu dòng, để SQL Server làm phần nặng và chỉ trả về vài dòng tổng hợp. Ngược lại, danh sách đoạn trạng thái (`MachineStateHistory`) chỉ vài chục đoạn mỗi ca, nên phần *hợp/trừ khoảng* làm bằng C# (mục 2.2) cho dễ test.

**Khái niệm cốt lõi.**

- Delta có xử lý reset: `Counter >= Prev` thì `Counter − Prev`, ngược lại (máy reset về 0) thì `Counter` (quy tắc của M4). Dòng đầu tiên không có `Prev` cho 0 (hoặc lấy dư một khoảng lookback để có `Prev`).
- Cắt đoạn vào cửa sổ `[from, to)`: phần giao của `[StartAt, EndAt)` với cửa sổ = `min(EndAt, to) − max(StartAt, from)`. SQL Server 2022 có `GREATEST/LEAST` nhưng để chạy được trên bản cũ hơn, dùng `CASE`.
- Đoạn đang mở (`EndAt IS NULL`): thay bằng `@now` (lấy từ `TimeProvider`, truyền vào làm tham số, không dùng `SYSUTCDATETIME()` để test được).
- `DATEDIFF(HOUR, @start, Ts)` đếm số **ranh giới giờ** đã đi qua, không phải số giờ đã trôi. Chỉ đúng như "chỉ số bucket" khi `@start` tròn giờ (giả định MVP ở 2.2).

**Snippet A — Total Count theo giờ từ bộ đếm tích lũy (T-SQL; tên cột theo M4 của bạn):**

```sql
-- Bộ đếm tích lũy -> số lượng theo giờ trong ca, có xử lý reset
DECLARE @machine int = 1, @start datetime2 = '2026-10-01T23:00:00', @to datetime2 = '2026-10-02T07:00:00';
WITH c AS (
    SELECT Ts, Counter,
           LAG(Counter) OVER (ORDER BY Ts, Id) AS PrevCounter          -- Id (bigint) phá thế hòa khi trùng Ts
    FROM MachineEvent
    WHERE MachineId = @machine AND EventType = 'count'
      AND Ts >= DATEADD(HOUR, -1, @start) AND Ts < @to                 -- lấy dư 1 giờ để dòng đầu ca có PrevCounter
)
SELECT DATEDIFF(HOUR, @start, Ts) AS HourIdx,
       SUM(CASE WHEN PrevCounter IS NULL     THEN 0
                WHEN Counter >= PrevCounter  THEN Counter - PrevCounter
                ELSE Counter END) AS TotalCount                        -- Counter < Prev: máy reset, delta = giá trị mới
FROM c
WHERE Ts >= @start                                                     -- lọc SAU khi đã có LAG
GROUP BY DATEDIFF(HOUR, @start, Ts)
ORDER BY HourIdx;
```

**Snippet B — downtime chưa/đã có lý do, cắt vào cửa sổ:**

```sql
DECLARE @from datetime2 = '2026-10-01T23:00:00', @to datetime2 = '2026-10-02T07:00:00', @now datetime2 = SYSUTCDATETIME();
SELECT h.Id, h.MachineId, h.DowntimeReasonId,                          -- NULL = cần phân loại
       DATEDIFF(SECOND,
           CASE WHEN h.StartAt < @from THEN @from ELSE h.StartAt END,
           CASE WHEN COALESCE(h.EndAt, @now) > @to THEN @to ELSE COALESCE(h.EndAt, @now) END) AS Sec
FROM MachineStateHistory h
WHERE h.State = 'Down'                                                  -- giả định State lưu dạng chuỗi (HasConversion<string>)
  AND h.StartAt < @to AND COALESCE(h.EndAt, @now) > @from;             -- điều kiện giao nhau
```

**Snippet C — Pareto có % và % tích lũy bằng window function:**

```sql
WITH d AS (   -- lấy Snippet B làm CTE, gom theo lý do, chỉ lý do phạt Availability
    SELECT x.DowntimeReasonId, SUM(x.Sec) / 60.0 AS Minutes
    FROM ( /* dán Snippet B vào đây */ ) x
    LEFT JOIN DowntimeReason r ON r.Id = x.DowntimeReasonId
    WHERE r.CountsAgainstAvailability = 1 OR r.Id IS NULL              -- chưa phân loại vẫn phạt (quy tắc 7)
    GROUP BY x.DowntimeReasonId
)
SELECT COALESCE(r.Code, 'UNCLASSIFIED') AS Reason, d.Minutes,
       100.0 * d.Minutes / SUM(d.Minutes) OVER ()                                              AS Pct,
       100.0 * SUM(d.Minutes) OVER (ORDER BY d.Minutes DESC, r.Code ROWS UNBOUNDED PRECEDING)
             / SUM(d.Minutes) OVER ()                                                          AS CumPct
FROM d LEFT JOIN DowntimeReason r ON r.Id = d.DowntimeReasonId
ORDER BY d.Minutes DESC, r.Code;
```

**Bẫy thường gặp.**

- Quên `ROWS UNBOUNDED PRECEDING`: mặc định `RANGE` cho kết quả sai khi có hai dòng cùng `Minutes` (cả hai nhận cùng tổng tích lũy).
- Đặt điều kiện `WHERE Ts >= @start` **trong** CTE có `LAG`: dòng đầu ca mất `PrevCounter`. Lọc phải nằm ngoài CTE.
- Pareto SQL không trừ phần downtime rơi vào giờ nghỉ hoặc PlannedStop, nên có thể lệch nhẹ so với phần phạt Availability tính ở C#. Với dashboard cần khớp tuyệt đối: dựng Pareto từ **cùng danh sách đoạn** mà `IOeeInputReader` dùng để tính A, và thêm test "tổng phút Pareto = phút phạt Availability". SQL C phù hợp cho báo cáo nhiều ngày/nhiều máy và để luyện window function.
- Chỉ mục: `MachineEvent (MachineId, Ts) INCLUDE (Counter, EventType)`, `MachineStateHistory (MachineId, StartAt) INCLUDE (EndAt, State, DowntimeReasonId)`, `ScanLog (StationId, ScannedAt) INCLUDE (Result)`. Mỗi chỉ mục thêm làm chậm insert; với bảng insert-only lớn hãy đo (xem cách đo ở Module 3) trước khi chốt.
- `State` và `EventType` lưu chuỗi hay số phụ thuộc cấu hình EF ở M4; sửa `'Down'` cho khớp.

### 2.6 Biểu đồ trong WPF với OxyPlot: Pareto, sản lượng theo giờ, A/P/Q

**Nó là gì.** OxyPlot là thư viện biểu đồ mã nguồn mở cho .NET. Dự án dùng **một** thư viện biểu đồ duy nhất là OxyPlot 2.x (gói NuGet `OxyPlot.Wpf`) cho cả dashboard OEE ở module này và control chart SPC ở Module 7 (cần `LineAnnotation` vẽ đường UCL/LCL), để bạn chỉ học một API. Nó tách biểu đồ làm hai phần:

- **`PlotModel`** (thuần dữ liệu, không phải control WPF): chứa tiêu đề, danh sách trục (`Axes`), danh sách chuỗi dữ liệu (`Series`), chú thích (`Annotations`). Bạn dựng và sửa nó trong ViewModel.
- **`PlotView`** (control WPF, xmlns `http://oxyplot.org/wpf`): chỉ có nhiệm vụ vẽ một `PlotModel` lên màn hình. Bạn **binding** `PlotView.Model` tới thuộc tính `PlotModel` của ViewModel.

Ba khái niệm dùng hàng ngày: **Series** quyết định dữ liệu vẽ thành hình gì (cột, đường, điểm); **Axis** quy đổi giá trị dữ liệu ra vị trí trên màn hình (`Position` = trái/phải/dưới/trên, mỗi trục có thể có `Key` riêng); **`InvalidatePlot(true)`** báo cho model biết "dữ liệu đã đổi, đọc lại và vẽ lại" (tham số `true` = đọc lại dữ liệu từ series; `false` chỉ vẽ lại hình từ dữ liệu đã đọc, dùng khi chỉ đổi màu, phóng to).

**Vấn đề nó giải quyết.** Cách ngây thơ: tự vẽ `Rectangle` trong `Canvas` cho từng cột, tự tính tỷ lệ, tự làm trục và nhãn. Mất nhiều ngày, không có tooltip, không có trục phụ, và mỗi biểu đồ mới viết lại. OxyPlot cho một biểu đồ Pareto (cột + đường tích lũy trên trục phụ) trong vài chục dòng, và cùng bộ khái niệm dùng lại được cho control chart ở Module 7.

**Khái niệm cốt lõi.**

- **Series**: `ColumnSeries` (cột dọc, phần tử là `ColumnItem`), `BarSeries` (cột ngang, phần tử là `BarItem`), `LineSeries` (đường, phần tử là `DataPoint(x, y)`). Cột dọc và cột ngang là **hai lớp khác nhau**: cột dọc đi với `CategoryAxis` ở dưới, cột ngang đi với `CategoryAxis` ở bên trái.
- **Axes**: `CategoryAxis` chứa nhãn (mã lý do dừng); `LinearAxis` cho giá trị số. **Trục phụ**: thêm một `LinearAxis` thứ hai với `Position = Right` và `Key = "pct"`, rồi đặt `YAxisKey = "pct"` cho series cần dùng trục đó. Series nào không ghi `YAxisKey` dùng trục mặc định (trục đầu tiên cùng hướng).
- **Pareto** = cột giảm dần (phút) trên trục trái + đường % tích lũy trên trục phải cố định 0–100. Đường `LineSeries` dùng chung `CategoryAxis` với cột; điểm thứ `i` có `x = i` (chỉ số nhóm), nên đường đi qua giữa mỗi cột.
- **Sản lượng theo giờ so với kế hoạch** = cột sản lượng thực (Good Count mỗi giờ) + đường mục tiêu `PlannedTimeTrongGiờ / IdealCycleTime`. Ví dụ ct 20 s: giờ đủ 60 phút kế hoạch mục tiêu 180 bo; giờ có nghỉ 15 phút chỉ còn 45 phút nên mục tiêu 135 bo; giờ nghỉ trưa mục tiêu 0. Đây là mục tiêu "lý tưởng theo cycle time"; chọn cách này thay vì chia đều `PlannedQty` của WO vì nó luôn tính được và khớp định nghĩa Performance. (Cách khác là chia đều `PlannedQty` theo giờ kế hoạch của WO; hợp khi WO có hạn giao hàng.)
- **A/P/Q**: ba thẻ số lớn kèm `ProgressBar`, đơn giản và đọc nhanh hơn biểu đồ. Số `null` hiện "—". Cờ cảnh báo hiện huy hiệu vàng "Check master data" (`PerformanceOver100`) hoặc "Counter mismatch".
- **Cập nhật số liệu**: dashboard gọi API OEE ca hiện tại mỗi 10–15 giây (đủ nhanh vì OEE không đổi từng giây), đồng thời nhận SignalR (Module 4) cho sự kiện "downtime cần phân loại" và "snapshot đã chốt" để làm mới ngay. Cập nhật `PlotModel` (thêm/sửa series, gọi `InvalidatePlot(true)`) nên chạy trên UI thread (xem Dispatcher, Module 4). Với biểu đồ sản lượng theo giờ: `ColumnSeries` cho Good Count mỗi giờ (chỉ số nhóm = giờ trong ca) + `LineSeries` cho mục tiêu, đặt `Color = OxyColors.Red` (namespace `OxyPlot`) để phân biệt.

**Snippet — ViewModel Pareto (viết theo OxyPlot 2.x, cột dọc; xem lưu ý về tên API bên dưới):**

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;

public partial class ParetoViewModel : ObservableObject
{
    // PlotView.Model binding tới thuộc tính PlotModel sinh ra từ field này
    [ObservableProperty] private PlotModel _plotModel = new();

    // items đã sắp xếp giảm dần theo Minutes (API/SQL trả sẵn, CumPct tính ở server)
    public void Load(IReadOnlyList<ParetoItem> items)
    {
        var m = new PlotModel { Title = "Downtime Pareto" };
        var cat = new CategoryAxis { Position = AxisPosition.Bottom, Key = "cat" };   // nhãn = mã lý do
        foreach (var i in items) cat.Labels.Add(i.ReasonCode);
        m.Axes.Add(cat);
        m.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Key = "min", Minimum = 0, Title = "Minutes" });
        m.Axes.Add(new LinearAxis { Position = AxisPosition.Right, Key = "pct", Minimum = 0, Maximum = 100,
                                    Title = "Cumulative %" });                       // trục phụ, khóa 0–100

        var bars = new ColumnSeries { XAxisKey = "cat", YAxisKey = "min", Title = "Minutes" };
        foreach (var i in items) bars.Items.Add(new ColumnItem(i.Minutes));           // 1 cột / lý do
        var line = new LineSeries { XAxisKey = "cat", YAxisKey = "pct", Title = "Cumulative %" };
        for (var k = 0; k < items.Count; k++) line.Points.Add(new DataPoint(k, items[k].CumPct));
        m.Series.Add(bars); m.Series.Add(line);

        PlotModel = m;   // đổi cả model: PlotView tự vẽ lại qua binding
        // Nếu thay vì tạo model mới bạn sửa Items/Points của model đang hiển thị: gọi PlotModel.InvalidatePlot(true)
    }
}
```

**Snippet — XAML:**

```xml
<Window ... xmlns:oxy="http://oxyplot.org/wpf">
    <oxy:PlotView Model="{Binding PlotModel}" Height="320" />
</Window>
```

**Lưu ý độ chính xác.** Các lớp và thuộc tính sau tôi tin là đúng cho OxyPlot 2.x: `PlotModel`, `CategoryAxis` (+ `Labels`), `LinearAxis` (`Position`, `Key`, `Minimum`, `Maximum`), `LineSeries` (`Points`, `DataPoint`), `XAxisKey`/`YAxisKey`, `InvalidatePlot(bool)`, `PlotView.Model`. Phần **chưa chắc** là họ series cột: `ColumnSeries` + `ColumnItem` (cột dọc) và `BarSeries` + `BarItem` (cột ngang, `CategoryAxis` ở `Position = Left`) tồn tại ở 2.0/2.1, nhưng các bản 2.2 trở lên có thay đổi ở nhóm series cột, còn `RectangleBarSeries` là lớp khác (khoảng x0–x1, y0–y1 tự chỉ định). Nếu compile lỗi, kiểm tra README/tài liệu OxyPlot theo đúng phiên bản NuGet bạn cài, hoặc chuyển sang `BarSeries` ngang (đổi trục cho khớp), vẫn dùng được cho Pareto. Nếu không chắc tên API, coi các đoạn trên là pseudo-code.

**Bẫy thường gặp.**

- **Quên `InvalidatePlot(true)`** sau khi sửa `Items`/`Points` của model đang hiển thị: biểu đồ đứng yên dù dữ liệu đã đổi. Tạo `PlotModel` mới rồi gán (như snippet) thì không cần, nhưng tạo model mới mỗi 10 giây làm mất trạng thái zoom/pan của người dùng.
- Sửa `PlotModel` từ thread nền (callback SignalR) dễ gây lỗi hoặc vẽ sai; chuyển về UI thread bằng `Dispatcher`/`SynchronizationContext` như đã học ở Module 4.
- Nhãn `CategoryAxis` và số cột lệch nhau khi lọc dữ liệu: dựng lại `Labels` và `Items` từ cùng một danh sách trong cùng một lần.
- Series tham chiếu `YAxisKey`/`XAxisKey` không tồn tại: series không hiện hoặc ném lỗi khi vẽ. Kiểm tra chính tả `Key` của trục.
- Nhầm cột dọc và cột ngang: `ColumnSeries` (dọc) với `CategoryAxis` ở dưới; `BarSeries` (ngang) với `CategoryAxis` bên trái. Đặt sai vị trí trục thì biểu đồ trống hoặc méo.
- Vẽ Pareto không sắp xếp giảm dần: đường tích lũy không còn là đường cong lõm, và mất ý nghĩa 80/20.
- Để trục % tích lũy tự co giãn: phải khóa `Minimum = 0`, `Maximum = 100`, nếu không đường trông giống nhau mọi lúc.

### 2.7 [v2] Xuất Excel bằng ClosedXML

**Nó là gì.** Endpoint trả file `.xlsx` cho các báo cáo: OEE theo máy/ca trong khoảng ngày, Pareto lý do dừng, (M3) FPY/WIP, (M6) danh sách truy xuất. Dùng lại thư viện `ClosedXML` đã dùng để import ở M1.

**Vấn đề nó giải quyết.** Quản lý xưởng ở VN gần như luôn hỏi "xuất Excel được không?" để gửi báo cáo tuần, dán vào slide, làm pivot. Mọi MES mã nguồn mở được khảo sát đều có tính năng xuất.

**Khái niệm cốt lõi.**

- Sinh file **ở server** (API trả `application/vnd.openxmlformats-officedocument.spreadsheetml.sheet`), WPF chỉ tải về và mở bằng `SaveFileDialog`. Cùng một query với dashboard nên số trong Excel khớp số trên màn hình.
- Ghi **giá trị số thật** (không phải chuỗi đã định dạng) và đặt định dạng ô: `0.0%` cho A/P/Q/OEE, `#,##0` cho số đếm, ngày giờ theo giờ nhà máy (đổi từ UTC trước khi ghi, ghi rõ múi giờ ở tiêu đề).
- Sheet thứ hai `Definitions` ghi định nghĩa và quy ước (Planned Time trừ những gì, micro stop bao nhiêu giây, nguồn Total/Good). Người nhận file sau 3 tháng vẫn hiểu số.
- Giới hạn khoảng ngày (ví dụ tối đa 31 ngày) để không sinh file khổng lồ.

```csharp
public static byte[] BuildOeeWorkbook(IReadOnlyList<OeeRow> rows, string tzLabel)
{
    using var wb = new XLWorkbook();
    var ws = wb.AddWorksheet("OEE");
    ws.Cell(1, 1).Value = $"OEE by machine and shift (times in {tzLabel})";
    var t = ws.Cell(3, 1).InsertTable(rows.Select(r => new
    {
        r.ShiftDate, r.Shift, r.Machine, PlannedMin = r.PlannedSec / 60.0, RunMin = r.RunSec / 60.0,
        r.TotalCount, r.GoodCount, r.Availability, r.Performance, r.Quality, r.Oee, r.Flags
    }), "OeeTable");
    foreach (var col in new[] { "Availability", "Performance", "Quality", "Oee" })
        t.DataRange.Column(t.FindColumn(c => c.FirstCell().Value.ToString() == col).RangeAddress.FirstAddress.ColumnNumber)
         .Style.NumberFormat.Format = "0.0%";
    ws.Columns().AdjustToContents();
    wb.AddWorksheet("Definitions").Cell(1, 1).Value = OeeDefinitions.Text;
    using var ms = new MemoryStream(); wb.SaveAs(ms); return ms.ToArray();
}
```

(Tên method tìm cột trong table có thể khác theo phiên bản ClosedXML; cách đơn giản hơn là định dạng theo số thứ tự cột.)

**Bẫy.** (1) Ghi `0.8889` dưới dạng chuỗi "88.9%": người nhận không tính toán được. (2) Ghi giờ UTC mà không ghi chú: báo cáo lệch 7 tiếng. (3) Sinh file lớn trong request đồng bộ, giữ hết trong RAM: giới hạn khoảng ngày; với dữ liệu thật lớn thì sinh nền và cho tải sau.

### 2.8 [v2] Line TV: màn hình lớn tại line

**Nó là gì.** Một view toàn màn hình, chữ rất lớn, đặt trên TV 43–55 inch treo ở đầu line: OEE ca hiện tại, sản lượng thực/kế hoạch theo giờ, các Andon đang mở (kèm cấp leo thang của M4), trạng thái từng máy. ktg-mes gọi là "màn hình lớn" (大屏), OpenMES có "supervisor dashboard"; nhà máy Nhật gọi là 生産管理板 hay アンドン表示板.

**Vấn đề nó giải quyết.** Dashboard BackOffice dành cho người ngồi trước PC. Công nhân và tổ trưởng đứng trên line cần nhìn từ 5–10 mét: "giờ này đang chậm bao nhiêu so với kế hoạch" để tự điều chỉnh.

**Khái niệm cốt lõi.**

- Chạy trong app **Kiosk** (đã có chế độ toàn màn hình, M3) với tham số `--mode=linetv --line=L1`, đăng nhập bằng tài khoản chỉ đọc (role Operator). Không cần app thứ ba.
- Dữ liệu: dùng lại API `GET /oee/current` và snapshot line của M4 + SignalR (Andon, state). Không có query mới.
- Bố cục cố định 16:9: hàng trên 3 ô số lớn (OEE %, Good/Plan, Andon mở); giữa là bảng giờ (giờ, kế hoạch, thực tế, chênh lệch tô đỏ khi âm); dưới là dải ô máy. Font tối thiểu ~48 px cho số chính. Dùng `Viewbox` để tự co theo độ phân giải TV.
- Nhiều line trên một TV: tự xoay vòng mỗi 20 giây (`DispatcherTimer`), có thể tắt.
- Không có nút bấm, không cần chuột. Mất kết nối API thì hiện dải "Offline — data at hh:mm", không để số cũ trông như số mới.

**Bẫy.** (1) Màu chỉ đỏ/xanh: người mù màu không phân biệt; kèm mũi tên/ký hiệu. (2) Cập nhật quá dày làm số nhảy liên tục khó đọc: 10–15 giây là đủ, Andon thì ngay lập tức. (3) Để Windows tự tắt màn hình/ngủ: cấu hình power plan của PC gắn TV (ghi vào README triển khai).

### Sơ đồ luồng dữ liệu của module

```
[M4 MQTT]  Machine Simulator (vai máy) ──▶ MqttIngestService ──▶ MachineEvent (counter) + MachineStateHistory
[M3 HTTP]  Machine Simulator (vai trạm) ──▶ POST /stations/{id}/scan ──▶ ScanLog (OK/NG theo serial)
                                                  │
                                                  ▼
 ┌─────────────────────────── Infrastructure ───────────────────────────────┐
 │ IOeeInputReader: SQL (LAG delta counter) + đoạn downtime (cắt cửa sổ)     │
 │ + ScanLog (Good/NG) + ShiftBreak + snapshot ideal cycle time (WO)         │
 │ OeeSnapshotWorker (PeriodicTimer 1 phút, TimeProvider) ──▶ OeeSnapshot    │
 └───────────────────────────────────┬───────────────────────────────────────┘
                                     ▼  OeeInput (các tổng)
 Domain:  ShiftCalendar ─▶ Interval/IntervalMath ─▶ OeeCalculator (pure) ─▶ OeeResult + cờ cảnh báo
                                     │
 Application (queries):  GetCurrentShiftOee | GetOeeHistory | GetDowntimePareto | GetUnclassifiedDowntime
                         AssignDowntimeReason (command) ─▶ đánh dấu snapshot của ca là dirty
                                     │
 Api:  GET /oee/current?machine=…  |  GET /oee/history  |  GET /downtime/pareto  |  PUT /downtime/{id}/reason
                                     │  REST (poll 10–15 s)          SignalR: DowntimeNeedsClassification, OeeSnapshotClosed
                                     ▼
 WPF BackOffice (Client.Core ApiClient + SignalR client) ─▶ OeeDashboardViewModel ─▶ PlotModel ─▶ OxyPlot PlotView
        [thẻ A/P/Q + cờ cảnh báo]  [Pareto top 5]  [sản lượng theo giờ vs mục tiêu]  [danh sách "cần phân loại"]
```

---

## 3. Kế hoạch thực hiện

Thứ tự cố ý đi từ "không cần máy chạy" (giấy, hàm thuần, thời gian) đến "cần cả hệ thống" (dashboard). Bạn có thể làm bước 1–3 khi Module 4 chưa xong.

- [ ] **Bước 1 — Tính tay và chốt bảng test** (1,5 giờ)
  - Mục tiêu: hiểu OEE đến mức không cần công thức để nhớ.
  - Việc cần làm: làm lại ví dụ 1.2.2 trên giấy hoặc Excel; tự bịa thêm ba ca: (a) P > 100%, (b) PlannedStop trùng giờ nghỉ, (c) ca có hai WO cycle time khác nhau. Viết ra kết quả A/P/Q/OEE và phép rút gọn `ideal × good / planned` cho từng ca.
  - File: `docs/oee-hand-calcs.md` (hoặc `.xlsx`).
  - Kiểm tra: với mọi ca, `A × P × Q` bằng phép rút gọn; bạn giải thích được "thác nước thời gian" của từng ca bằng lời.

- [ ] **Bước 2 — `OeeCalculator` và unit test** (2 giờ)
  - Mục tiêu: một hàm thuần duy nhất cho toàn hệ thống.
  - Việc cần làm: `OeeInput`, `ProductionSegment`, `OeeResult`, `OeeWarning`, `OeeCalculator.Calculate` (mục 2.1); chuyển bảng test ở 2.1 thành `[Fact]`/`[Theory]`; thêm test tính chất.
  - File: `src/MiniMes.Domain/Oee/OeeCalculator.cs` (+ các record), `tests/MiniMes.UnitTests/Oee/OeeCalculatorTests.cs`.
  - Kiểm tra: `dotnet test` xanh; ca mẫu ra A 0,8889 / P 0,9259 / Q 0,97 / OEE 0,7984; phá thử công thức (đổi `*` thành `+`) thì ít nhất 3 test đỏ.

- [ ] **Bước 3 — Thời gian: `ShiftCalendar`, ngày sản xuất, `IntervalMath`** (3,5 giờ)
  - Mục tiêu: xác định đúng ca/ngày sản xuất và tính đúng thời gian kế hoạch/downtime thật.
  - Việc cần làm: `ShiftDef`, `ShiftCalendar.Resolve` (kể cả ca qua nửa đêm); `Interval`, `IntervalMath.UnionLength`; hàm dựng `OeeInput` từ danh sách khoảng (nghỉ, PlannedStop, downtime bị phạt, micro stop threshold) — thuần, chưa đụng DB; đăng ký `TimeProvider.System` trong DI; test với `FakeTimeProvider`.
  - File: `src/MiniMes.Domain/Time/ShiftCalendar.cs`, `IntervalMath.cs`, `src/MiniMes.Domain/Oee/OeeInputBuilder.cs`, `tests/MiniMes.UnitTests/Time/*`.
  - **[v2]** `ShiftCalendar` nhận ngoại lệ `PlantCalendar` (mục 2.2), reader nạp bảng này.
  - Kiểm tra: 4 dòng test ở 2.2 xanh; test "họp trùng giờ nghỉ thì Planned Time không bị trừ hai lần"; test "máy Down trong giờ nghỉ không thành downtime"; **[v2]** test ngày 30/4 có `IsWorkingDay = false` → `Resolve` trả `null` cho mọi giờ trong ngày; test Chủ nhật tăng ca chỉ `S1` → 08:00 ra `S1`, 15:00 ra `null`.

- [ ] **Bước 4 — Đọc dữ liệu đầu vào từ DB** (4 giờ)
  - Mục tiêu: `IOeeInputReader` trả `OeeInput` từ dữ liệu thật của M3/M4.
  - Việc cần làm: (1) `MachineEvent` -> Total Count theo delta có xử lý reset (Snippet A, 2.5); (2) `MachineStateHistory` -> danh sách đoạn bị phạt/không phạt theo `DowntimeReason`; (3) `ScanLog` -> Good/NG theo trạm của máy; (4) `ShiftBreak`; (5) `IdealCycleTimeSec` từ `WorkOrderOperation` theo WO đang chạy; thêm chỉ mục 2.5; nếu M4 chưa có, thêm cột `DowntimeReasonId` (nullable) cho `MachineStateHistory` bằng migration.
  - File: `src/MiniMes.Application/Oee/IOeeInputReader.cs`, `src/MiniMes.Infrastructure/Oee/OeeInputReader.cs`, migration mới, `tests/MiniMes.IntegrationTests/Oee/OeeInputReaderTests.cs`.
  - Kiểm tra: integration test (Testcontainers) **dựng lại đúng ca mẫu** bằng cách insert trực tiếp event/đoạn trạng thái/scan, kỳ vọng OEE 79,84%; thêm test bộ đếm reset giữa ca; thêm test đoạn `EndAt = NULL`.

- [ ] **Bước 5 — `OeeSnapshot`, service chốt và worker** (3 giờ)
  - Mục tiêu: chốt theo giờ và cuối ca, idempotent, bắt kịp khi bị tắt.
  - Việc cần làm: entity `OeeSnapshot` (cột của spec + `IdealSeconds`, `IsFinal`, `CalculatedAt`, cờ cảnh báo), unique index `(MachineId, ShiftDate, ShiftId, Hour)`; `OeeSnapshotService.CloseDueBucketsAsync`; `OeeSnapshotWorker` (`PeriodicTimer`); ghi trade-off realtime vs định kỳ vào README.
  - File: `src/MiniMes.Domain/Oee/OeeSnapshot.cs`, `src/MiniMes.Infrastructure/Oee/OeeSnapshotService.cs`, `OeeSnapshotWorker.cs`, configuration EF, migration.
  - Kiểm tra: test với `FakeTimeProvider`: tua 5 phút sau 14:00 thì có snapshot tổng ca; chạy job hai lần không nhân đôi dòng; "tắt" job 3 giờ rồi chạy lại thì đủ 3 snapshot giờ; tổng ca tính từ dữ liệu thô = tổng hợp (cộng thời gian/đếm) từ các snapshot giờ.

- [ ] **Bước 6 — Downtime "cần phân loại" và đổi lý do** (2 giờ)
  - Mục tiêu: supervisor gán lý do cho đoạn dừng còn trống, số OEE cập nhật đúng.
  - Việc cần làm: query danh sách đoạn `Down` chưa có lý do (dài hơn ngưỡng micro stop); command `AssignDowntimeReason` (validation: lý do tồn tại, đoạn đã đóng, `OTHER` bắt buộc ghi chú); đánh dấu snapshot ca liên quan là chưa final; audit người sửa (M0 interceptor).
  - File: `src/MiniMes.Application/Oee/Downtime/*`.
  - Kiểm tra: gán `PLN-PM` (không phạt) cho một đoạn thì snapshot ca tính lại và A tăng; gán lý do phạt thì A không đổi; AuditLog ghi lại.

- [ ] **Bước 7 — API, Contracts và SignalR** (2,5 giờ)
  - Mục tiêu: client WPF lấy được OEE, Pareto, danh sách phân loại.
  - Việc cần làm: `GET /oee/current`, `GET /oee/history`, `GET /downtime/pareto?from&to&machine&by=minutes|count`, `GET /downtime/unclassified`, `PUT /downtime/{id}/reason`; **[v2]** `GET /reports/oee.xlsx?from&to&line` và `GET /reports/downtime-pareto.xlsx?from&to&line` theo mục 2.7 (giới hạn 31 ngày, sheet `Definitions`); DTO trong `MiniMes.Contracts`; phát sự kiện SignalR `DowntimeNeedsClassification`, `OeeSnapshotClosed` (throttle như M4).
  - File: `src/MiniMes.Api/Controllers/OeeController.cs`, `src/MiniMes.Contracts/Oee/*`.
  - Kiểm tra: Swagger trả OEE ca mẫu; phân quyền: Supervisor được đổi lý do, Operator chỉ xem; **[v2]** mở file Excel tải về: ô A/P/Q là số có định dạng %, giá trị khớp dashboard, cột thời gian là giờ nhà máy.

- [ ] **Bước 8 — Dashboard WPF** (4 giờ)
  - Mục tiêu: màn hình quản lý xưởng nhìn được.
  - Việc cần làm: `OeeDashboardViewModel` (chọn máy/ca/ngày, poll 10–15 giây, nhận SignalR); thẻ A/P/Q + huy hiệu cảnh báo; Pareto top 5 (2.6); cột sản lượng theo giờ + đường mục tiêu; danh sách "cần phân loại" với ComboBox lý do; mọi chuỗi trong `Strings.resx`.
  - File: `src/MiniMes.BackOffice.Wpf/Views/OeeDashboardView.xaml`, `ViewModels/OeeDashboardViewModel.cs`, `ParetoViewModel.cs`.
  - Kiểm tra: chọn ca mẫu (seed) thấy đúng 79,8%; đổi lý do thì Pareto và OEE cập nhật; kéo chuột lên cột thấy tooltip phút.

- [ ] **Bước 8b — [v2] Line TV** (1,5 giờ)
  - Mục tiêu: màn hình lớn tại line đọc được từ xa.
  - Việc cần làm: `LineTvView` trong `MiniMes.Kiosk.Wpf` theo mục 2.8 (tham số dòng lệnh `--mode=linetv --line=L1[,L2]`), `Viewbox`, xoay vòng line, dải Offline; dùng lại API và SignalR có sẵn; nút "Export to Excel" trên `OeeDashboardView` của BackOffice gọi endpoint ở Bước 7.
  - File: `src/MiniMes.Kiosk.Wpf/Views/LineTvView.xaml`, `ViewModels/LineTvViewModel.cs`.
  - Kiểm tra: chạy ở 1920×1080 và 3840×2160 bố cục không vỡ; đứng cách màn hình 3 mét vẫn đọc được số OEE và chênh lệch giờ; ép Andon → hiện ngay; tắt API → dải Offline.

- [ ] **Bước 9 — Kiểm tra chéo, cờ cảnh báo, chạy demo** (1,5 giờ)
  - Mục tiêu: chạm đủ DoD và kịch bản demo bước 6–7.
  - Việc cần làm: `CounterCrossCheck` (chênh > 2% -> `CounterMismatch`); thêm ca có dữ liệu sai (cycle time 15 s thay vì 20 s) để thấy cờ P > 100%; chạy simulator với cấu hình cố định (seed) trong một ca ngắn; ép máy Down 5 phút bằng lệnh console, chọn lý do, xem OEE.
  - File: `src/MiniMes.Domain/Oee/CounterCrossCheck.cs`, cấu hình simulator `sim-oee-demo.json`.
  - Kiểm tra: xem DoD 5.1.

**Nếu bị kẹt**

| Triệu chứng | Kiểm tra theo thứ tự |
| --- | --- |
| OEE ra khác số tính tay | In ra bốn đại lượng: Planned, Run, Total, Good. So từng con số với giấy; lỗi thường ở Planned (nghỉ tính hai lần) hoặc Total (delta bộ đếm) chứ hiếm khi ở công thức |
| A quá thấp bất thường | Có đang tính `Idle` lúc nghỉ giữa ca là downtime không? Có đoạn `Offline` dài không? |
| P > 100% ngay ở demo | Cycle time trong snapshot WO (M2) có khớp cấu hình simulator không? Simulator gửi bộ đếm theo bo hay theo panel? |
| Total Count âm hoặc quá lớn | Bộ đếm reset: xem điều kiện `Counter >= PrevCounter`; có nhận trùng `msgId` không (M4 phải loại) |
| Ca đêm lệch ngày | In `Resolve(...)` cho 3 thời điểm 22:00, 01:30, 06:00 local; kiểm tra `DateTimeKind` của giá trị đọc từ DB |
| Snapshot bị nhân đôi | Thiếu unique index hoặc `Hour = NULL` không được xử lý nhất quán khi upsert |
| Snapshot theo giờ không đổi sau khi đổi lý do | Đã đánh dấu chưa final chưa? Job có lọc `IsFinal = false` không? |
| Pareto không khớp A | Pareto đang lấy đoạn chưa trừ nghỉ giữa ca; dùng cùng danh sách đoạn với reader (2.5) |
| Biểu đồ trống hoặc không đổi | Đã gán `PlotModel` mới hoặc gọi `InvalidatePlot(true)` chưa? `XAxisKey`/`YAxisKey` của series có khớp `Key` của trục không? Có sửa model từ thread nền không? |
| Worker không chạy | Đã `AddHostedService<OeeSnapshotWorker>()` chưa? Có exception bị nuốt ở scope tạo DbContext không? Kiểm tra log Serilog |

---

## 4. Vì sao chọn giải pháp này

| Quyết định | Đã chọn | Phương án khác đã cân nhắc | Vì sao không chọn | Khi nào nên đổi |
| --- | --- | --- | --- | --- |
| Nơi tính OEE | Pure function trong Domain, mọi nơi gọi lại | Tính trong SQL view/stored procedure; tính trong ViewModel | Khó unit test bằng số tay; logic lặp ở nhiều nơi rồi lệch nhau | Cần báo cáo ad-hoc hàng loạt trên hàng trăm triệu dòng: đưa vào SP nhưng giữ test đối chiếu với hàm thuần |
| Nguồn Total/Good | Total từ bộ đếm máy, Good từ scan theo serial (đã chốt) | Chỉ dùng `ngDelta` của máy; chỉ dùng scan | `ngDelta` không truy được con nào NG; chỉ dùng scan không biết máy đã xử lý bao nhiêu | Có máy thật đếm không tin cậy, hoặc mọi trạm đều scan đủ: dùng scan cho cả hai |
| Lưu thời gian & ngày | UTC + `ShiftDate` = ngày bắt đầu ca (giờ nhà máy) | Ngày lịch local; ngày UTC | Xé ca đêm; lệch múi giờ | Nhà máy nhiều múi giờ: lưu `SiteTimeZone` theo từng Site |
| Tần suất tính | Realtime cho ca hiện tại + snapshot theo giờ/ca cho lịch sử | Chỉ realtime; chỉ snapshot | Chỉ realtime: nặng, số nhảy, lịch sử không cố định; chỉ snapshot: dashboard trễ tới một giờ | Số máy lớn: chỉ snapshot + cache; cần độ trễ thấp hơn: rút bucket còn 15 phút |
| Lập lịch job | `BackgroundService` + `PeriodicTimer`, chạy mỗi phút và chốt bù | Hangfire/Quartz; SQL Agent; cron | Thêm phụ thuộc và hạ tầng không cần cho một job đơn giản | Nhiều job, nhiều instance, cần retry/dashboard job: Hangfire/Quartz |
| Tổng hợp nhiều giờ/máy | Cộng thời gian & số đếm rồi chia | Trung bình cộng các tỷ lệ | Sai khi mẫu số khác nhau (ví dụ 1.5 mục 6) | Không có; trung bình cộng chỉ đúng khi mẫu số bằng nhau |
| Performance > 100% | Giữ giá trị thô + cờ cảnh báo | Cắt về 100% | Che lỗi cycle time | Báo cáo cho khách bên ngoài: hiển thị cắt 100% nhưng vẫn giữ cờ trong hệ thống |
| Micro stop | Ngưỡng cấu hình, mặc định 60 s | Mọi lần dừng đều tính; ngưỡng cố định trong code | Mọi lần dừng: operator chọn bừa lý do; hard-code: mỗi nhà máy một chuẩn | Ngưỡng 0 nếu nhà máy muốn khớp định nghĩa nghiêm ngặt |
| Nơi giữ lý do dừng | Cột `DowntimeReasonId` trên `MachineStateHistory` (một lý do/đoạn) | Bảng `DowntimeEvent` riêng cho phép tách đoạn; suy ra từ `AndonCall` lúc truy vấn | Bảng riêng và join Andon đều phức tạp hơn nhu cầu MVP | Supervisor cần tách một lần dừng dài thành nhiều lý do: dùng bảng riêng |
| Phân chia tính toán | SQL lo bộ đếm (nhiều dòng), C# lo hợp/trừ khoảng (ít dòng) | Tất cả SQL; tất cả C# | Tất cả C#: kéo triệu dòng về; tất cả SQL: khó test phép hợp khoảng | Khối lượng đoạn trạng thái lớn: chuyển phép cắt đoạn sang SQL |
| Thư viện biểu đồ | OxyPlot | LiveCharts2, tự vẽ | LiveCharts2 đẹp và có animation nhưng API thay đổi nhiều giữa các bản; dùng 1 thư viện cho cả M5 và M7 | Nếu cần dashboard hiệu ứng đẹp cho demo, hoặc hiệu năng render dữ liệu rất lớn |
| **[v2]** Ngày nghỉ | Ngoại lệ `PlantCalendar` được `ShiftCalendar` dùng khi dựng cửa sổ ca | Để supervisor phân loại cả ngày lễ là PlannedStop | Phải thao tác tay mỗi ngày lễ cho mọi máy; quên là OEE tháng sai | Lịch khác nhau theo line: thêm `LineId` vào `PlantCalendar` |
| **[v2]** Xuất báo cáo | File `.xlsx` sinh ở server bằng ClosedXML, số thật + định dạng | Xuất CSV; sinh ở WPF từ dữ liệu đang hiển thị; Power BI/SSRS | CSV mất định dạng và hỏng tiếng Việt khi mở bằng Excel; sinh ở client thì số có thể lệch với server; BI cần hạ tầng riêng | Nhiều báo cáo, nhiều người dùng: kết nối Power BI vào view SQL chỉ đọc |
| **[v2]** Màn hình lớn | View `LineTv` trong app Kiosk | App riêng; trang web | Thêm một app phải triển khai; web cần thêm stack front-end (spec chốt WPF) | Nhiều TV ở nhiều xưởng, không muốn cài app: chuyển sang trang web chỉ đọc |
| Đọc "bây giờ" | `TimeProvider` (có từ .NET 8) | Interface `IClock` tự viết; `DateTime.UtcNow` | Tự viết không tích hợp với `PeriodicTimer`; `UtcNow` không test được | Không có (chuẩn của .NET) |

**Ba quyết định bạn phải nói lại được trong phỏng vấn.**

*Một, hàm tính OEE là hàm thuần và chỉ có một.* "Tôi tách việc thu thập dữ liệu (có I/O, test bằng integration test) khỏi việc tính (thuần, test bằng số tính tay). Dashboard realtime, job snapshot và báo cáo đều gọi cùng một hàm, nên một ca không thể cho hai con số OEE. Khi khách hỏi vì sao 79,8% thì tôi truy được ra từng đoạn dừng và từng dòng scan." Điểm cộng là bạn nói được bốn con số trung gian của ca mẫu (405, 360, 1.000, 970) thay vì chỉ nói công thức.

*Hai, nguồn dữ liệu và kiểm tra chéo.* "Bộ đếm máy cho biết máy xử lý bao nhiêu nhưng không biết con nào; kết quả scan theo serial cho biết con nào OK/NG và lỗi gì. Tôi dùng máy cho Total và serial cho Good, vì chất lượng cần truy được đến từng sản phẩm. Hai nguồn độc lập nên tôi đối chiếu: chênh quá 2% thì gắn cờ vì OEE lúc đó không đáng tin." Điểm cộng là bạn biết một hệ thống MES không nên tin một nguồn duy nhất mà không tự kiểm chứng.

*Ba, thời gian và độ bền của job.* "Dữ liệu UTC, ngày sản xuất là ngày bắt đầu ca theo giờ nhà máy, nên ca đêm không bị xé đôi. Job chốt snapshot chạy mỗi phút và hỏi 'bucket nào đến hạn mà chưa chốt', nên server tắt vài giờ vẫn chốt bù và chạy lại không sinh dữ liệu trùng. Có thời gian đệm để chờ dữ liệu MQTT về muộn, và đổi lý do dừng sẽ đánh dấu tính lại." Đây là dạng câu trả lời cho câu hỏi "hệ thống của bạn xử lý mất mạng/dữ liệu trễ thế nào".

---

## 5. Hoàn thành module

### 5.1 Definition of Done

- [ ] Số OEE trên dashboard **khớp tính tay** cho ca mẫu (A 88,9% / P 92,6% / Q 97,0% / OEE 79,8%) — DoD của spec.
- [ ] Unit test `OeeCalculator` pass, gồm ca mẫu, P > 100%, giờ nghỉ (Planned = 0), downtime > Planned, nhiều WO, và test tính chất `A×P×Q = ideal×good/planned`.
- [ ] Unit test `ShiftCalendar` pass cho ca qua nửa đêm (bốn mốc ở 2.2), test không trừ hai lần khi nghỉ trùng PlannedStop, test Down trong giờ nghỉ không tính downtime.
- [ ] Integration test (Testcontainers) dựng lại ca mẫu từ `MachineEvent`/`MachineStateHistory`/`ScanLog` và cho OEE 79,84%; có test bộ đếm reset giữa ca.
- [ ] Không còn `DateTime.UtcNow`/`DateTime.Now` trong Domain/Application/Infrastructure của module (đã thay bằng `TimeProvider`).
- [ ] `OeeSnapshot` có unique index; chạy job hai lần không nhân đôi; tắt job rồi bật lại thì chốt bù đủ.
- [ ] Danh sách "cần phân loại" hoạt động; đổi lý do làm snapshot của ca được tính lại; thao tác được ghi audit.
- [ ] Dashboard: OEE theo máy/ca/ngày, ba thẻ A/P/Q, Pareto top 5, sản lượng theo giờ so với mục tiêu; cờ cảnh báo P > 100% và `CounterMismatch` hiện đúng.
- [ ] Kiểm tra chéo bộ đếm máy vs số scan chạy; cấu hình sai cycle time (15 s) hiện cờ.
- [ ] Kịch bản demo bước 6–7: ép máy Down 5 phút, chọn lý do, OEE ca hiện tại thay đổi hợp lý.
- [ ] README có mục "OEE: realtime vs định kỳ" và các quy ước đã chọn (Idle/Offline, micro stop, P > 100%, ShiftDate); ARCHITECTURE.md ghi nguồn đếm OEE.
- [ ] Mọi chuỗi UI mới nằm trong `Strings.resx`.
- [ ] **[v2]** Ngày lễ trong `PlantCalendar` không có cửa sổ ca, không sinh snapshot, không làm OEE tháng giảm; Chủ nhật tăng ca tính đúng (unit test).
- [ ] **[v2]** Xuất Excel OEE và Pareto: số khớp dashboard, định dạng %, có sheet `Definitions`, giờ nhà máy.
- [ ] **[v2]** Line TV chạy toàn màn hình, xoay vòng line, hiện Andon ngay, có dải Offline.

### 5.2 Tự kiểm tra

**1. Ca 480 phút, nghỉ 30 phút, PlannedStop 10 phút, dừng ngoài kế hoạch 50 phút, ideal cycle time 30 giây, đếm 700, NG 14. Tính A, P, Q, OEE.**
<details><summary>Đáp án gợi ý</summary>
Planned = 480 − 30 − 10 = 440 phút. Run = 440 − 50 = 390 phút. A = 390/440 = 0,8864. Ideal = 700 × 30 = 21.000 giây; Run = 23.400 giây; P = 0,8974. Good = 686; Q = 686/700 = 0,98. OEE = 0,8864 × 0,8974 × 0,98 ≈ 0,7795. Kiểm: 686 × 30 / (440 × 60) = 20.580/26.400 = 0,7795.
</details>

**2. Vì sao Performance > 100% không phải tin tốt?**
<details><summary>Đáp án gợi ý</summary>
Cycle time chuẩn là mức nhanh nhất lý tưởng nên không thể vượt. P > 100% nghĩa là cycle time nhập quá chậm, sai đơn vị (bo vs panel), bộ đếm sai, hoặc Run Time bị tính thiếu. Đây là cảnh báo master data; giữ giá trị thô và gắn cờ thay vì cắt về 100%.
</details>

**3. Máy báo `Idle` suốt giờ nghỉ trưa. Có tính là downtime không? Vì sao?**
<details><summary>Đáp án gợi ý</summary>
Không. Nghỉ giữa ca đã bị trừ khỏi Planned Production Time; downtime chỉ tính phần nằm ngoài (nghỉ ∪ PlannedStop). Nếu tính, Availability bị hạ oan.
</details>

**4. Sự kiện lúc 2026-10-01T18:30Z (nhà máy UTC+7, ca S3 22:00–06:00). Thuộc ngày sản xuất nào? Vì sao không phải 02/10?**
<details><summary>Đáp án gợi ý</summary>
18:30Z = 01:30 ngày 02/10 giờ nhà máy, nhưng nằm trong ca S3 bắt đầu 22:00 ngày 01/10, nên `ShiftDate` = 2026-10-01. Ngày sản xuất là ngày bắt đầu ca, để ca đêm không bị chia làm hai.
</details>

**5. Vì sao Total Count lấy từ bộ đếm máy còn Good Count lấy từ scan theo serial? Nếu hai nguồn chênh 6% thì OEE có đáng tin không?**
<details><summary>Đáp án gợi ý</summary>
Máy biết "bao nhiêu" nhưng không biết "con nào"; serial là nguồn sự thật duy nhất về chất lượng. Chênh 6% > 2% nghĩa là máy đếm sai hoặc thiếu scan, khi đó Q và OEE không đáng tin; gắn cờ `CounterMismatch` và không âm thầm sửa số.
</details>

**6. Có thể lấy OEE ca bằng trung bình cộng của 8 OEE theo giờ không?**
<details><summary>Đáp án gợi ý</summary>
Không, vì mẫu số (Planned Time) mỗi giờ khác nhau (giờ nghỉ trưa gần 0). Phải cộng Planned, Run, Ideal seconds, Good, Total rồi tính lại bằng công thức. Đó là lý do snapshot lưu `IdealSeconds`.
</details>

**7. Đổi model (setup) thuộc tổn thất nào trong Six Big Losses? Nó ảnh hưởng A, P hay Q? Cấu hình thế nào để có/không tính vào Availability?**
<details><summary>Đáp án gợi ý</summary>
Setup & Adjustment, tổn thất Availability. Cấu hình bằng `DowntimeReason.CountsAgainstAvailability`: `true` thì trừ vào Run Time (phạt A), `false` thì trừ khỏi Planned Production Time.
</details>

**8. Vì sao job chốt snapshot chạy mỗi phút và hỏi "bucket nào đến hạn", thay vì hẹn giờ đúng đầu mỗi giờ?**
<details><summary>Đáp án gợi ý</summary>
Nếu server tắt đúng lúc thì snapshot đó mất mãi. Chạy đều và chốt bù cho mọi bucket đến hạn (kèm grace chờ dữ liệu muộn), kết hợp upsert idempotent, làm job chịu được khởi động lại và chạy trùng.
</details>

**9. Vì sao dùng `TimeProvider` thay vì `DateTime.UtcNow` trong handler và job?**
<details><summary>Đáp án gợi ý</summary>
Để test điều khiển được thời gian (`FakeTimeProvider.Advance`) mà không `Sleep` hay phụ thuộc giờ máy; `PeriodicTimer` cũng nhận `TimeProvider` nên tua thời gian cho cả job.
</details>

**10. Bộ đếm tích lũy đang 15.230 rồi nhận 12. Delta là bao nhiêu và cần cẩn thận điều gì?**
<details><summary>Đáp án gợi ý</summary>
Theo quy tắc reset của M4: counter giảm nghĩa là máy reset về 0 nên delta = 12. Cẩn thận: message đến lộn thứ tự hoặc trùng cũng có thể làm counter "giảm"; sắp theo `Ts`, loại trùng theo `msgId` trước khi tính delta.
</details>

**11. [v2] Ngày Tết máy vẫn gửi dữ liệu vì kỹ thuật chạy thử. OEE ngày đó tính thế nào?**
<details><summary>Đáp án gợi ý</summary>
`PlantCalendar` khai ngày nghỉ nên không có cửa sổ ca, Planned Time = 0, không có snapshot. Dữ liệu chạy thử nằm ngoài mọi ca, giống giờ giữa hai ca: vẫn lưu trong `MachineEvent` nhưng không vào OEE.
</details>

### 5.3 Câu hỏi phỏng vấn liên quan

**1. OEE tính thế nào? Máy 83% thì cải thiện ở đâu trước?**
<details><summary>Gợi ý trả lời</summary>
Nêu công thức A×P×Q và ý nghĩa từng phần (thời gian ca trừ nghỉ và dừng có kế hoạch = Planned Time). 83% có thể là A 90 × P 95 × Q 97. Không đoán: tách ra ba thành phần rồi xem tổn thất theo thời gian (thác nước). Thường bắt đầu từ cột Pareto lớn nhất của downtime nếu A thấp nhất, vì mỗi phút dừng là mất trực tiếp; nhưng cân nhắc chi phí/khả năng làm được (đổi setup có thể rẻ hơn thay máy). Lưu ý P khó thấy: kiểm tra dừng ngắn và cycle time có đúng không trước khi kết luận. Chỉ cải thiện máy là nút cổ chai mới tăng sản lượng cả line.
</details>

**2. Planned và unplanned downtime khác gì? Đổi model tính thế nào?**
<details><summary>Gợi ý trả lời</summary>
Planned bị trừ khỏi thời gian kế hoạch nên không phạt; unplanned trừ vào Run Time nên phạt Availability. Đổi model do nhà máy quyết định; tôi cấu hình bằng cờ `CountsAgainstAvailability` trên lý do dừng để cùng một code chạy cho cả hai chính sách.
</details>

**3. Số OEE của bạn và của khách/ca trưởng khác nhau. Kiểm tra gì trước?**
<details><summary>Gợi ý trả lời</summary>
Định nghĩa Planned Production Time (nghỉ, họp có bị trừ không), nguồn ideal cycle time, quy ước micro stop và Idle/Offline, nguồn Total/Good, và cách gộp (cộng rồi chia hay trung bình). Hầu hết khác biệt nằm ở định nghĩa, không phải phép tính.
</details>

**4. Ca đêm qua nửa đêm xử lý thế nào?**
<details><summary>Gợi ý trả lời</summary>
Lưu UTC; ngày sản xuất là ngày bắt đầu ca theo giờ nhà máy; khi tìm ca của một thời điểm thử cả hôm nay và hôm qua; cửa sổ ca lưu cặp UTC nửa mở; nghỉ giữa ca tính theo độ lệch từ đầu ca. Có unit test cho 22:00, 01:30, 05:59:59, 06:00.
</details>

**5. Làm sao biết dữ liệu OEE đáng tin?**
<details><summary>Gợi ý trả lời</summary>
Kiểm tra chéo bộ đếm máy với số scan (ngưỡng 2%), cờ P > 100% cho master data sai, cờ Good > Total, danh sách downtime chưa phân loại, audit khi sửa lý do, và số liệu truy ngược được tới từng đoạn dừng/dòng scan.
</details>

**6. Vì sao có cả snapshot lẫn tính realtime?**
<details><summary>Gợi ý trả lời</summary>
Realtime cho ca hiện tại (số tức thời, chỉ tính khi có người xem, nhưng số có thể nhảy khi dữ liệu về muộn); snapshot đã chốt cho lịch sử và báo cáo (kiểm chứng được, rẻ khi truy vấn, đổi chỉ khi có lý do và được tính lại có kiểm soát). Cùng một hàm tính nên hai bên không lệch.
</details>

**7. [v2] Quản lý muốn báo cáo OEE tuần bằng Excel. Bạn làm thế nào để số trong file khớp số trên dashboard?**
<details><summary>Gợi ý trả lời</summary>
Sinh file ở server từ cùng query/snapshot mà dashboard dùng; ghi số thật kèm định dạng chứ không ghi chuỗi; đổi giờ UTC sang giờ nhà máy và ghi rõ; thêm sheet định nghĩa (Planned Time trừ gì, micro stop, nguồn Total/Good) để người đọc sau không hiểu sai.
</details>

### 5.4 Tài liệu đọc thêm

Chỉ liệt kê tên và nơi tìm; hãy tự tìm đúng trang vì URL chi tiết có thể thay đổi.

- Seiichi Nakajima, *Introduction to TPM: Total Productive Maintenance* (Productivity Press, 1988): nguồn gốc của OEE và Six Big Losses.
- ISO 22400 (Automation systems and integration — Key performance indicators for manufacturing operations management): tiêu chuẩn có định nghĩa các KPI vận hành như OEE; chỉ cần biết nó tồn tại và tham chiếu được khi khách hỏi định nghĩa "chuẩn".
- Eliyahu Goldratt, *The Goal*: hiểu vì sao OEE của nút cổ chai quan trọng hơn OEE của máy khác.
- Vorne Industries, trang giáo dục về OEE (oee.com): giải thích OEE và Six Big Losses bằng tiếng Anh, dễ đọc.
- learn.microsoft.com (tìm theo tên trang): `TimeProvider`, `PeriodicTimer`, `TimeZoneInfo`, "Background tasks with hosted services in ASP.NET Core", "OVER clause (Transact-SQL)", "LAG (Transact-SQL)".
- **[v2]** ClosedXML: repo GitHub `ClosedXML/ClosedXML` và docs.closedxml.io (Tables, Number formats).
- Gói NuGet `Microsoft.Extensions.TimeProvider.Testing` (nuget.org): tài liệu của `FakeTimeProvider`.
- OxyPlot: trang chủ oxyplot.org (mục Documentation: PlotModel, Axes, Series, "Getting started with WPF") và README/repo `oxyplot/oxyplot` trên GitHub; kiểm tra README theo đúng phiên bản NuGet `OxyPlot.Wpf` bạn cài.
- ARCHITECTURE.md của chính dự án: thêm mục "Nguồn dữ liệu OEE và các quy ước tính OEE" trước khi sang Module 6.
