# Module 3 — Shop Floor Execution (scan serial, route check, OK/NG, Repair, WIP, FPY)

> Module này làm phần "trái tim" của MES ở xưởng điện tử: operator scan serial ở từng trạm, hệ thống kiểm tra sản phẩm có được phép làm ở trạm đó không (route check / interlock), ghi kết quả OK/NG, đưa hàng NG sang Repair và tính WIP, FPY.
> Sau khi xong bạn có: API `POST /stations/{id}/scan` chạy < 100 ms (dùng chung cho WPF Kiosk và cho Machine Simulator vai trạm test tự động), domain service `RouteCheck` có bảng test, màn hình Kiosk toàn màn hình tối ưu cho máy quét, báo cáo FPY theo trạm và WIP theo bước, và số đo hiệu năng trên 5–10 triệu dòng `ScanLog`.
> Thời lượng ước tính: 27–33 giờ (khoảng 2,5–3 tuần ở nhịp 1,5–2 giờ/ngày; bản cũ 24–30 giờ + 3 giờ cho phần **[v2]**). Đây là module nặng nhất của MVP-2; phần tùy chọn được đánh dấu để cắt khi trễ.
> Module tiên quyết: Module 0 (khung solution, JWT, WPF host, .resx), Module 1 (Routing, DefectCode, MediatR/FluentValidation), Module 2 (WO, snapshot `WorkOrderOperation`, `Serial`, state machine, rowversion).
>
> **Cập nhật theo [spec-v2](spec-v2.md):** (1) **quy tắc route check số 6 — cổng FAI**: khi WO có `FaiStatus = Pending`, chỉ đúng 1 serial mẫu được đi qua line (mục 1.4, 2.1, 2.2); (2) kiosk có nút **SOP** mở hướng dẫn công việc của bước hiện tại (mục 2.8b); (3) kiosk chọn WO từ **dispatch list** của M2; (4) trạm **Pack** chuyển sang module **[M3b Packing](module-03b-packing.md)**, M3b gọi lại đúng use case Scan của module này. Các chỗ đổi được đánh dấu **[v2]**.

---

## 1. Kiến thức nghiệp vụ MES

### 1.1 Bức tranh tổng thể

Ở Module 2, Planner release Work Order (WO) `WO-0001`: 200 bo mạch `PCB-A`, hệ thống sinh 200 serial ở trạng thái `Created`. Từ lúc đó đến lúc thùng hàng được đóng, mỗi bo mạch đi qua bốn công đoạn:

```
 Line 1 (routing snapshot của WO-0001)

  Seq 1            Seq 2             Seq 3              Seq 4
 [ SMT ] ───────▶ [ ASSEMBLY ] ───▶ [ TEST ] ────OK───▶ [ PACK ] ──▶ Completed
 L1-SMT           L1-ASM            L1-TEST     │        L1-PACK
 (có máy,         (thủ công,        (có máy,    NG
  tự động)         operator scan)    tự động)   ▼
                                           [ REPAIR ]  L1-REPAIR (thủ công, ngoài routing)
                                                │
                                                └── sửa xong ──▶ quay lại TEST
```

Module 3 nằm ở chính giữa sơ đồ này: mọi lần một bo mạch "đi qua" một trạm đều tạo ra một lần scan. Từ chuỗi scan đó, MES biết:

- Bo mạch nào đang ở đâu (`Serial.CurrentSeq`, `Serial.Status`).
- Đã làm được bao nhiêu ở mỗi công đoạn (`QtyGood`, `QtyNg`).
- Bao nhiêu bo đang nằm dở giữa các công đoạn (WIP).
- Bao nhiêu bo đạt ngay lần đầu (FPY).
- Ai làm, ở trạm nào, lúc nào (truy xuất, Module 6).

Ai dùng module này:

| Người | Làm gì với module này |
| --- | --- |
| Operator | Scan serial ở trạm của mình; xem đèn xanh/đỏ; ở Test bấm NG + chọn mã lỗi; ở Repair nhập hành động sửa |
| Supervisor | Xem WIP, FPY; quyết định scrap; xử lý các trường hợp bị chặn |
| QC | Xem FPY, Pareto lỗi; sau này Hold hàng (Module 7) |
| Machine Simulator (vai trạm test tự động) | Gọi đúng API scan như Kiosk, kèm OK/NG + DefectCode |
| Planner | Xem tiến độ WO qua `QtyGood` của bước cuối |

Vì sao nhà máy cần: nếu không có MES, công nhân điền phiếu giấy đi kèm bo mạch. Phiếu mất, ghi sai, ghi sau. Khi khách hỏi "bo serial X đã qua test chưa?" thì không ai trả lời chắc chắn. Với module này, câu trả lời có trong 1 giây và bo mạch chưa qua Test thì không thể đóng gói được.

### 1.2 Khái niệm & thuật ngữ

**Trạm (station) và công đoạn (operation).**
Công đoạn là một bước công nghệ trong routing: "dán linh kiện SMT", "lắp ráp", "test chức năng", "đóng gói". Trạm là nơi vật lý thực hiện công đoạn đó: một cái bàn, một máy, một cụm máy, có một PC/tablet chạy Kiosk. Ở dự án này mỗi `Station` thuộc một `WorkCenter` (Module 0/1), và `WorkOrderOperation` của WO (snapshot từ Routing) nói "bước Seq 3 làm ở WorkCenter Test". Khi trạm `L1-TEST` scan, MES biết trạm này đang làm bước nào của WO nhờ `WorkCenterId`.

Ví dụ seed dùng xuyên suốt tài liệu này:

| Seq | Công đoạn | Trạm | Có máy? | Ai scan |
| --- | --- | --- | --- | --- |
| 1 | SMT | `L1-SMT` | Có (`L1-SMT-01`) | Simulator vai trạm tự động, hoặc operator khi demo |
| 2 | Assembly | `L1-ASM` | Không | Operator trên Kiosk |
| 3 | Test | `L1-TEST` | Có (`L1-TEST-01`) | Simulator vai trạm test; hoặc operator + nút NG |
| 4 | Pack | `L1-PACK` | Không | Operator trên Kiosk |
| (ngoài routing) | Repair | `L1-REPAIR` | Không | Kỹ thuật viên sửa chữa trên Kiosk |

Serial ví dụ trong tài liệu: `PCBA-000001` … `PCBA-000200` (định dạng thật do Module 2 quy định; ở đây chỉ để dễ đọc).

**Operator làm gì trong một ca** (ví dụ operator ở trạm Assembly, ca 07:00–15:00, nghỉ 60 phút):

1. Đến trạm, đăng nhập Kiosk bằng thẻ nhân viên (máy quét đọc mã thẻ, giống gõ chuỗi + Enter). Hệ thống mở một `StationSession` (trạm, người, WO, giờ vào).
2. Chọn trạm và WO đang chạy (thường supervisor đã dặn: "hôm nay chạy `WO-0001`").
3. Lấy một bo từ băng tải/khay đầu vào, quét nhãn serial dán trên bo.
4. Đợi màn hình: xanh nghĩa là được làm; đỏ nghĩa là dừng, đọc lý do.
5. Làm công đoạn (lắp connector, bắt vít...), đặt bo sang khay đầu ra.
6. Lặp lại ở nhịp cycle time (ví dụ 20 giây/bo). Một ca làm khoảng 1.200 bo.
7. Gặp đỏ mà không hiểu, hoặc bo lạ: gọi supervisor (Andon ở Module 4).
8. Cuối ca: đăng xuất (đóng `StationSession`), bàn giao bo dở dang cho ca sau.

Khoảnh khắc "quét → chờ màu" xảy ra hàng nghìn lần mỗi ca. Đó là lý do phần scan phải nhanh và không bao giờ được rối (mục 1.4).

**Serial và bước hiện tại.**
Mỗi bo có một `Serial` với `CurrentSeq` = số thứ tự bước đã hoàn thành OK gần nhất. Bo mới sinh có `CurrentSeq = 0`. Bo vừa qua SMT có `CurrentSeq = 1`. Bo qua đủ 4 bước có `CurrentSeq = 4` và `Status = Completed`. Trong seed của dự án `Seq` là 1, 2, 3, 4 liên tục, nên "bước kế tiếp = `CurrentSeq + 1`" đúng như spec.

Các trạng thái của serial (spec, Module 2): `Created`, `InProcess`, `Ng`, `Repair`, `Completed`, `Scrapped`, `Hold`.

**Poka-yoke.**
Từ tiếng Nhật ポカヨケ (poka = lỗi vô ý, yoke = tránh), do Shigeo Shingo đề xuất trong Toyota Production System: thiết kế sao cho lỗi của con người không thể xảy ra hoặc bị phát hiện ngay. Ví dụ đời thường: phích cắm USB chỉ cắm được một chiều; xe số tự động không nổ máy được khi chưa về P. Ở xưởng điện tử: connector có rãnh chống cắm ngược, jig test chỉ nhận bo đúng hướng. Poka-yoke bằng phần mềm là route check: phần mềm không để bo chưa qua Assembly được scan ở Test. Có hai kiểu:

- Ngăn ngừa (prevention): chặn hành động sai, như route check.
- Phát hiện (detection): cho làm nhưng báo ngay, như cảnh báo "scan quá nhanh bất thường".

Module này chủ yếu làm kiểu ngăn ngừa.

**Route check / Interlock.**
Kiểm tra lộ trình: trước khi ghi nhận scan, hệ thống kiểm tra "bo này có được phép làm ở trạm này lúc này không". Interlock (インターロック, khóa liên động) là tên gọi kỹ thuật cho cơ chế "điều kiện A chưa thỏa thì hành động B bị khóa". Nếu không được phép, MES trả về lý do và không ghi gì vào trạng thái.

**OK / NG.**
OK: bo đạt ở công đoạn này. NG (không đạt, 不良): bo không đạt, kèm defect code. Ở trạm thủ công như Assembly, scan mặc định là "tôi đã làm xong, OK". Ở trạm Test, kết quả OK/NG đến từ máy test (Simulator) hoặc do operator chọn.

**Defect code.**
Mã lỗi chuẩn hóa (bảng `DefectCode` ở Module 1: Code, Name, Category). Không cho gõ tự do vì không thống kê được. Ví dụ seed:

| Code | Tên | Category |
| --- | --- | --- |
| `SOL-BRIDGE` | Solder bridge (cầu thiếc) | Soldering |
| `SOL-OPEN` | Open / cold solder joint (mối hàn hở/nguội) | Soldering |
| `CMP-MISSING` | Component missing (thiếu linh kiện) | Component |
| `CMP-REVERSE` | Component reversed (ngược cực) | Component |
| `FUNC-NOBOOT` | Board does not boot (không khởi động) | Functional |
| `FUNC-VOLT` | Voltage out of range (điện áp ngoài ngưỡng) | Functional |
| `COS-SCRATCH` | Cosmetic scratch (xước ngoại quan) | Cosmetic |

Vì sao cần: Pareto lỗi (Module 5/7) chỉ có nghĩa nếu mã lỗi chuẩn. Nếu 60% NG là `SOL-BRIDGE`, kỹ sư sẽ xem lại stencil/nhiệt độ lò reflow ở SMT, chứ không đi sửa bo từng cái mãi.

**NG vs Repair vs Rework vs Scrap.**
Bốn thuật ngữ hay bị nhầm:

| Thuật ngữ | Nghĩa | Ví dụ với `PCBA-000042` | Kết quả với serial |
| --- | --- | --- | --- |
| NG | Sự kiện: lần kiểm tra này không đạt | Test báo `SOL-BRIDGE` | `Status = Ng`, `CurrentSeq` giữ nguyên (2) |
| Repair | Hành động sửa lỗi tại chỗ rồi kiểm tra lại | Thợ hàn lại cầu thiếc, quét lại | `Ng → Repair → InProcess`; phải qua Test lại |
| Rework | Làm lại theo quy trình, có thể quay lại nhiều bước | Lỗi do linh kiện dán lệch: cho chạy lại SMT rồi các bước sau | `CurrentSeq` lùi về trước bước cần làm lại |
| Scrap | Hủy, không sửa được hoặc sửa không kinh tế | PCB cháy, tróc pad | `Status = Scrapped`, kết thúc |

Repair thường là sửa nhỏ và tốn ít thời gian (10–30 phút). Rework là chạy lại một phần quy trình, tốn hơn. Scrap mất luôn nguyên liệu, công đã bỏ ra, và phải ghi nhận để tính hao hụt (`ScrapRate` ở BOM, Module 1).

Quy ước cho MVP: NG không làm `CurrentSeq` tăng. Sau Repair, mặc định bo quay lại đúng bước đã NG (nên "bước kế tiếp = `CurrentSeq + 1`" lại là Test, khớp câu "hoặc = bước kiểm tra sau khi Repair" trong spec). Rework nhiều bước được hỗ trợ bằng tham số tùy chọn `returnToSeq` của thao tác hoàn tất Repair: `CurrentSeq = returnToSeq - 1`.

**WIP (work in process, 仕掛品).**
Hàng dở dang: bo đã bắt đầu nhưng chưa xong. Trong dự án: serial có `Status` là `InProcess`, `Ng`, `Repair` (và `Hold` nếu đang bị giữ trên line). Không tính `Created` (chưa vào line) và `Completed`/`Scrapped` (đã kết thúc). WIP thường được nhóm theo bước để thấy chỗ tắc nghẽn: nhiều bo dồn ở "sau Assembly, trước Test" nghĩa là trạm Test đang là nút cổ chai.

Ví dụ WO 200 bo tại một thời điểm:

| Nhóm | Số bo |
| --- | --- |
| Created (chưa vào line) | 60 |
| Sau SMT, chờ Assembly (`CurrentSeq = 1`) | 30 |
| Sau Assembly, chờ Test (`CurrentSeq = 2`, InProcess) | 40 |
| Sau Test, chờ Pack (`CurrentSeq = 3`) | 25 |
| Completed | 40 |
| Ng (chờ vào Repair) | 3 |
| Repair (đang sửa) | 2 |
| Tổng | 200 |

WIP = 30 + 40 + 25 + 3 + 2 = 100 bo. Định luật Little: WIP = throughput × lead time. Nếu throughput là 180 bo/giờ thì bo nằm trong line trung bình 100/180 = 0,56 giờ (khoảng 33 phút). WIP cao mà throughput không đổi nghĩa là bo đợi lâu hơn.

**FPY và final yield.**
FPY (First Pass Yield, 直行率) = tỷ lệ bo đạt ngay lần kiểm tra đầu tiên, không qua sửa. Final yield (最終歩留まり) = tỷ lệ bo cuối cùng đạt, kể cả bo đã sửa xong và đạt lại.

Công thức (tính theo trạm):

```
FPY_trạm      = số serial OK ở lần scan ĐẦU TIÊN tại trạm / số serial vào trạm lần đầu
Final yield   = số serial Completed / (số serial Completed + số serial Scrapped)
RTY (tùy chọn)= FPY_SMT × FPY_ASM × FPY_TEST × FPY_PACK
```

Ví dụ số: 200 bo vào Test lần đầu. 184 bo OK ngay. 16 bo NG, đưa sang Repair: 12 sửa xong và retest OK, 4 bo scrap.

- FPY_TEST = 184 / 200 = 92,0%.
- Final yield = (184 + 12) / (184 + 12 + 4) = 196 / 200 = 98,0%.
- Số thợ sửa phải xử lý: 16 bo. Nếu mỗi bo tốn 20 phút thì 16 × 20 = 320 phút, hơn 5 giờ công, để cứu 12 bo. Đây là "nhà máy ẩn" (hidden factory): nếu chỉ nhìn final yield 98% thì tưởng ổn, FPY 92% mới lộ ra chi phí thật.

Một cách tính sai hay gặp: lấy `QtyGood / (QtyGood + QtyNg)` của trạm Test. Trong ví dụ trên `QtyGood = 196` (184 lần đầu + 12 lần retest), `QtyNg = 16`, tỷ lệ = 196 / 212 = 92,5%. Gần 92% nhưng sai về bản chất: mẫu số đếm cả lượt retest. Chỉ cần vài bo NG hai lần là lệch. FPY phải tính theo serial, lần đầu tiên tại trạm, không tính theo lượt scan.

RTY (rolled throughput yield) cho thấy xác suất một bo đi qua toàn line mà không bị NG lần nào. Ví dụ FPY từng trạm: SMT 98%, Assembly 99%, Test 92%, Pack 99,5%:

RTY = 0,98 × 0,99 × 0,92 × 0,995 = 0,888 (88,8%). Cứ 1.000 bo bắt đầu, khoảng 112 bo phải sửa ít nhất một lần dù mỗi trạm nhìn riêng đều "trên 92%". Spec chỉ yêu cầu FPY theo trạm; RTY là phần thêm vì chỉ là phép nhân.

Lưu ý mẫu số của final yield: bo còn dở dang (WIP) chưa phải đạt cũng chưa phải hỏng. Nếu đặt mẫu số là tổng số bo đã bắt đầu, final yield bị kéo thấp vô lý ở giữa ca. Vì vậy ở công thức trên mẫu số chỉ gồm bo đã kết thúc (Completed + Scrapped).

**Idempotent scan.**
Scanner đôi khi bắn hai lần (nhấn cò nhanh, hoặc phím kích hoạt bị dính). Nếu hệ thống không phân biệt, bo bị đếm hai lần hoặc lần thứ hai bị báo đỏ "đã qua bước rồi", làm operator hoang mang. Idempotent nghĩa là gửi cùng một yêu cầu nhiều lần cho kết quả giống gửi một lần. Với scan: cùng serial, cùng trạm, trong vài giây thì trả OK và không ghi trùng.

**Hai loại "người gọi" API scan.**
Cùng một endpoint `POST /stations/{id}/scan`, hai loại client:

| | WPF Kiosk (người scan) | Machine Simulator, vai trạm test tự động |
| --- | --- | --- |
| Ngoài đời tương ứng | Operator + máy quét mã vạch | Máy test/AOI tự gửi kết quả theo serial |
| Tốc độ | 1 scan mỗi 10–30 giây/trạm | Có thể 1 scan mỗi 1–3 giây |
| Danh tính | Operator đăng nhập bằng thẻ (JWT của operator) | Tài khoản dịch vụ (ví dụ `SIM-TEST-01`, role Operator) |
| Kết quả OK/NG | Trạm thủ công: mặc định OK; trạm Test: bấm nút | Luôn gửi kèm OK/NG (+ DefectCode nếu NG) |
| Khi lỗi mạng | Hiện banner đỏ, chặn scan | Retry; vì thế API phải idempotent |

Hệ quả thiết kế: API không được "nghĩ" là chỉ có người gọi. Không được dựa vào việc người sẽ quét chậm, nhìn màn hình rồi mới quét tiếp. Simulator gọi liên tục, song song nhiều trạm, có thể retry cùng một request sau timeout.

**Scan có nhanh không: vì sao < 100 ms.**
Xem chi tiết ở 1.4 (quy tắc) và 2.5–2.6 (cách đạt và đo).

### 1.3 Một ngày ở xưởng

Nhân vật: Hùng (Planner), Lan (operator trạm Assembly), Minh (kỹ thuật viên Repair), Thảo (Supervisor), Nam (QC). Hôm nay Line 1 chạy `WO-0001`: 200 bo `PCB-A`.

**07:00 — Vào ca.** Lan đến `L1-ASM`, quét thẻ nhân viên. Kiosk mở `StationSession`: trạm `L1-ASM`, Lan, `WO-0001`, giờ vào 00:00 UTC (07:00 giờ nhà máy). Lan thấy chữ "READY" trên nền xám đậm.

**07:10 — Nhịp bình thường.** Lan quét `PCBA-000001`. Bo này đã qua SMT sáng sớm (`CurrentSeq = 1`). Màn hình xanh, chữ "OK - next: Test", âm "ding". Lan lắp connector, đặt bo sang khay Test. Cứ 20 giây một bo.

**07:35 — Bo nhảy cóc.** Một bo vừa từ SMT ra, Lan chưa quét bao giờ, bị ai đó đặt nhầm vào khay Test. Ở trạm Test, `PCBA-000031` được quét: đỏ, "Blocked: expected step 2 Assembly, board is at step 1". Bo được trả lại Assembly. Nếu không có route check, bo này sẽ được test (có thể pass vì test chức năng không kiểm tra connector chưa lắp?) và đóng gói, khách nhận bo thiếu công đoạn.

**08:02 — Scanner bắn đúp.** Lan bóp cò, máy quét kêu hai lần, Kiosk nhận `PCBA-000047` hai lần cách nhau 0,3 giây. Lần 1: OK. Lần 2: vẫn xanh, message "Duplicate scan ignored", không ghi thêm. Số đếm không tăng 2.

**09:15 — Bo NG.** Trạm Test tự động (Simulator đóng vai máy test) gửi kết quả `PCBA-000042` NG, `SOL-BRIDGE`. Bo được đánh dấu `Ng`. Cùng lúc, ai đó thử đưa lại bo này vào khay Test và quét: đỏ, "Serial is NG: send to Repair". Minh nhận bo ở `L1-REPAIR`, quét: xanh, trạng thái chuyển `Repair`. Minh hàn lại cầu thiếc, nhập hành động "Reflow solder joint U3", không thay linh kiện, bấm hoàn tất. Bo quay lại Test.

**09:40 — Retest.** `PCBA-000042` được quét ở Test: xanh, Test báo OK lần này. `Serial` thành `InProcess`, `CurrentSeq = 3`. FPY của Test sẽ không tính lần retest này (nó đã "mất" lần đầu).

**10:20 — Mạng chập chờn.** Switch ở gần trạm Assembly reset. Kiosk của Lan hiện banner đỏ "OFFLINE - scanning disabled". Lan báo Thảo. 40 giây sau banner biến mất. Lúc mạng rớt, Lan vừa quét `PCBA-000090`: Kiosk chờ 3 giây rồi báo lỗi "Không rõ kết quả". Lan quét lại: server trả "Duplicate scan ignored" vì yêu cầu trước thực ra đã tới server. Không mất, không trùng.

**11:00 — Hàng bị hold.** Nam (QC) nghi ngờ một lô linh kiện, đặt `PCBA-000120` vào Hold (Module 7). Lan vô tình cầm đúng bo này quét ở Assembly: đỏ, "Serial is on HOLD". Bo được đặt riêng sang khu cách ly.

**14:45 — Sắp hết ca.** Thảo mở báo cáo WIP: 31 bo nằm giữa Assembly và Test, chỉ 8 bo giữa Test và Pack. Test đang là nút cổ chai (ví dụ do 12 bo NG đang đợi sửa). Thảo điều thêm một kỹ thuật viên sang Repair.

**15:00 — Bàn giao.** Lan đăng xuất, `StationSession` đóng. Ca sau nhìn báo cáo WIP để biết bo dở dang đang nằm ở đâu.

**Cuối ngày.** Hùng xem `WO-0001`: `QtyGood` bước cuối = 137/200, FPY Test = 91,4%, FPY Assembly 99,3%. Anh gọi kỹ sư SMT vì số lỗi cầu thiếc cao.

### 1.4 Quy tắc nghiệp vụ & lý do tồn tại

**Route check: 5 quy tắc của spec.** Viết trong một hàm domain, chạy theo thứ tự dưới đây (thứ tự quan trọng, xem lý do sau bảng ví dụ).

Giả định cho các ví dụ: `WO-0001` đang `InProgress`, routing 1 SMT, 2 Assembly, 3 Test, 4 Pack; `Repair` ngoài routing; cửa sổ chống scan lặp 5 giây.

| # | Quy tắc | Ví dụ serial | Kết quả | Lý do tồn tại |
| --- | --- | --- | --- | --- |
| 1a | Serial tồn tại | `PCBA-999999` (không có trong DB) | Chặn `SerialNotFound` | Bo lạ hoặc nhãn in sai/gõ nhầm; không được tự tạo serial ở trạm |
| 1b | WO phải `Released` hoặc `InProgress` | Serial của WO đã `Completed`/`OnHold`/`Cancelled` | Chặn `WoNotActive` | Line không được chạy WO chưa cho phép hoặc đã đóng, sẽ sai số liệu WO |
| 1c | Serial thuộc đúng WO của phiên trạm | `PCBA-900001` thuộc `WO-0002`, trạm đang chạy `WO-0001` | Chặn `WrongWo` | Nhầm bo giữa hai lệnh sản xuất (khác model/khách hàng) |
| 2 | Serial không ở `Hold`, `Scrapped`, `Completed` | `PCBA-000120` Hold; `PCBA-000011` Scrapped; `PCBA-000012` Completed | Chặn `SerialHold` / `SerialScrapped` / `SerialCompleted` | Hàng nghi ngờ không được đi tiếp; hàng đã hủy/đã xong không được tái sinh |
| 3a | Trạm = bước `CurrentSeq + 1` | `PCBA-000002` (Cur=1) scan ở Assembly (Seq 2) | Cho phép; `CurrentSeq = 2` | Đúng thứ tự công nghệ |
| 3b | Nhảy bước | `PCBA-000003` (Cur=1) scan ở Test (Seq 3) | Chặn `SkippedStep`, "expected Assembly (step 2)" | Chống bỏ công đoạn (mục đích chính của poka-yoke) |
| 3c | Quay lại bước đã qua (sau khoảng scan lặp) | `PCBA-000004` (Cur=3) scan ở Assembly, 40 giây sau lần scan trước | Chặn `AlreadyPassed` | Bo đã qua bước này; ghi lần hai sẽ làm sai số lượng |
| 3d | Trạm không thuộc routing của WO | Trạm Pack scan serial của WO có routing 3 bước (không có Pack) | Chặn `StationNotInRoute` | Sai trạm/sai cấu hình |
| 4a | Serial `Ng`/`Repair` chỉ trạm Repair được scan | `PCBA-000007` (Ng) scan ở Test | Chặn `MustGoRepair` | Bo lỗi không được lẫn vào bo tốt |
| 4b | Trạm Repair chỉ nhận bo `Ng`/`Repair` | `PCBA-000008` (InProcess) scan ở Repair | Chặn `NotDefective` | Không ai "sửa" bo tốt rồi làm mất trạng thái |
| 4c | Sau Repair, bo quay lại bước kiểm tra | `PCBA-000009` Repair xong, Cur=2 | Scan ở Test được phép (Seq 3 = Cur + 1) | Chốt lại chất lượng bằng đúng bài test đã fail |
| 4d | Rework | `PCBA-000013` Repair xong với `returnToSeq = 1` | `CurrentSeq = 0`; SMT được phép | Lỗi cần làm lại từ SMT |
| 5 | Scan lặp (cùng serial, cùng trạm, trong cửa sổ) | `PCBA-000047` scan Assembly hai lần cách 0,3 giây | Trả OK, message "Duplicate scan ignored", không ghi lần 2 | Scanner bắn đúp; retry của máy sau timeout |
| 6a **[v2]** | WO có `FaiStatus = Pending`, chưa có mẫu FAI: serial scan ở **bước đầu** được chọn làm mẫu | `PCBA-000001` scan SMT, `FaiSerialNo` đang trống | Allow; `WorkOrder.FaiSerialNo = PCBA-000001` | Line cần chạy đúng 1 con để QC kiểm tra (xem M2 mục f2) |
| 6b **[v2]** | WO `FaiStatus = Pending`, serial **không phải** mẫu FAI | `PCBA-000002` scan SMT khi mẫu là `…001` | Chặn `FaiNotApproved`, "First article not approved yet" | Không chạy hàng loạt trước khi QC xác nhận setup đúng |
| 6c **[v2]** | Serial mẫu FAI đi tiếp các bước | `PCBA-000001` scan Assembly | Allow (theo quy tắc 3) | Mẫu phải đi hết line mới kiểm tra được |
| 6d **[v2]** | `FaiStatus = Passed` hoặc `NotRequired` | mọi serial | Bỏ qua quy tắc 6 | Cổng đã mở |

Thứ tự kiểm tra và vì sao:

1. Tồn tại, WO, đúng WO (rule 1).
2. Scan lặp (rule 5) đến TRƯỚC status và step. Lý do: sau khi scan OK lần đầu, `CurrentSeq` đã tăng và với bước Pack thì serial đã `Completed`. Nếu kiểm tra step trước, lần scan lặp sẽ bị báo `AlreadyPassed`/`SerialCompleted` thay vì "duplicate OK".
3. Status Hold/Scrapped/Completed (rule 2).
4. Ng/Repair (rule 4). Trạm Repair không bị cổng FAI chặn: mẫu FAI bị NG vẫn phải vào Repair được.
5. **[v2]** FAI (rule 6). Đặt sau Ng/Repair và trước step: bo NG thì báo "đi Repair" quan trọng hơn; còn nếu đặt sau step, serial thứ 2 scan sai bước sẽ nhận lý do `SkippedStep` trong khi nguyên nhân thật là line chưa được phép chạy.
6. Step (rule 3).

Một tinh chỉnh so với chữ trong spec: spec nói "trả OK nhưng không ghi trùng". Ở dự án này scan lặp được định nghĩa là: cùng serial, cùng trạm, cùng kết quả yêu cầu, trong 5 giây so với bản ghi gần nhất tại trạm đó, thì trả về kết quả của bản ghi cũ và cờ `duplicate = true`. Với scan OK đó đúng là "trả OK". Với máy test gửi NG rồi retry cùng NG sau timeout, trả lại NG cũ (không ghi thêm defect). Thời lượng cửa sổ là cấu hình, mặc định 5 giây.

**Các quy tắc khác của module.**

| Quy tắc | Lý do |
| --- | --- |
| Kết quả NG bắt buộc kèm `DefectCode` hợp lệ | Không có mã lỗi thì không thống kê, không truy nguyên nhân |
| `ScanLog` insert-only, không sửa/xóa. Sửa sai bằng bản ghi mới | Nhật ký là chứng cứ audit. Khách hàng/ISO có thể đòi xem |
| Một trạm có tối đa một `StationSession` đang mở; scan cần phiên đang mở | Biết "ai" đã scan; ca sau không dùng phiên của ca trước |
| Ghi `ScanLog`, đổi trạng thái `Serial`, tăng `QtyGood/QtyNg` trong CÙNG một transaction | Nếu ghi được nửa chừng, số liệu lệch nhau và không ai biết số nào đúng |
| Scan OK ở bước cuối → `Serial.Status = Completed` | Kết thúc vòng đời sản phẩm |
| Scan đầu tiên thành công của WO chuyển WO từ `Released` sang `InProgress` (gọi `WorkOrder.Start()` của Module 2) | Trạng thái WO phản ánh thực tế đã chạy |
| **[v2]** Gán mẫu FAI bằng cập nhật có điều kiện (`... WHERE FaiStatus = 'Pending' AND FaiSerialNo IS NULL`), cùng transaction với scan | Hai trạm SMT scan 2 serial khác nhau cùng lúc: chỉ một serial được làm mẫu, serial kia bị chặn `FaiNotApproved` |
| **[v2]** Kiosk chỉ hiển thị nút SOP khi bước có `SopUrl`; mở SOP không làm mất phiên hoặc focus scan sau khi đóng | Operator cần xem hướng dẫn ngay tại trạm, nhất là với model mới |
| Repair bắt buộc nhập `Action`; `ComponentReplaced` khi có thay linh kiện | Nguyên nhân gốc và truy xuất linh kiện thay thế |
| Scrap chỉ Supervisor làm, có lý do (thao tác tùy chọn của module) | Hủy là mất tiền, cần người có thẩm quyền |
| Mất kết nối API: Kiosk chặn scan, không lưu tạm | Quyết định spec MVP. Lưu tạm cục bộ gây xung đột trạng thái khi nối lại |

**Vì sao scan phải nhanh (< 100 ms) và hậu quả khi chậm.**

100 ms là ngưỡng người dùng cảm nhận "tức thì" (giới hạn nổi tiếng trong nghiên cứu về thời gian phản hồi giao diện: 0,1 giây tức thì; 1 giây vẫn giữ được mạch suy nghĩ; hơn 10 giây thì mất tập trung). Ở xưởng, hậu quả cụ thể hơn:

1. Mất năng suất. Trạm Test có cycle time 12 giây/bo. Một ca 8 giờ, nghỉ 60 phút: 420 phút = 25.200 giây, làm được 2.100 bo. Nếu operator phải đợi thêm 1,5 giây cho mỗi lần scan thì cycle 13,5 giây, làm được 25.200 / 13,5 = 1.866 bo. Mất 234 bo (11%) chỉ vì phần mềm chậm. (Số liệu minh họa; chỉ đúng khi operator không làm việc song song trong lúc chờ.)
2. Operator không đợi. Nếu màn hình đỏ đến sau 1–2 giây, công nhân đã đặt bo sang trạm sau mất rồi. Poka-yoke mất tác dụng vì cảnh báo đến muộn.
3. Scan lặp và nhầm lẫn. Không thấy phản hồi, operator quét lại lần hai, ba. Nhiều lần scan lặp làm sinh nhiễu và lỗi khó hiểu.
4. Hàng đợi tại DB. Scan chậm giữ khóa lâu (lock, transaction dài). Các scan khác cùng bước cũng phải đợi, timeout dây chuyền. Simulator gọi nhiều trạm song song làm sự cố nặng hơn.
5. Máy test tự động timeout và retry. Nếu máy chờ 2 giây không thấy trả lời thì gửi lại; khi API không idempotent sẽ sinh bản ghi trùng.
6. Mất niềm tin. Công nhân bỏ dùng hệ thống, quay lại phiếu giấy. Đây là kết cục tệ nhất với một dự án MES.

Ngân sách thời gian gợi ý (ước lượng để bạn đối chiếu số đo thật, không phải số đo):

| Thành phần | Khoảng |
| --- | --- |
| Mạng LAN xưởng, Kiosk → API | 1–5 ms |
| Xác thực JWT (kiểm chữ ký cục bộ) | < 1 ms |
| Đọc DB (Serial, WO, scan gần nhất) | 3–10 ms |
| Route check trong bộ nhớ | < 1 ms |
| Ghi DB trong transaction (flush log) | 5–20 ms |
| Serialize JSON | 1 ms |
| Tổng | 15–40 ms, còn dư cho p95/p99 |

Mục tiêu đo bằng p95 (95% lần scan dưới 100 ms) chứ không bằng trung bình; xem 2.6.

### 1.5 Sai lầm/sự cố thường gặp ngoài đời

| Sự cố | Chuyện gì xảy ra | Cách MES phòng |
| --- | --- | --- |
| Bỏ qua công đoạn để "đạt số" | Công nhân cho bo đi thẳng Pack, bỏ Test vì bị hối | Route check chặn cứng, không có "bỏ qua" ở Kiosk (chỉ Supervisor xử lý ngoài luồng, có audit) |
| Scan bù hàng loạt | Cuối giờ scan cả chồng bo cùng lúc để kịp báo cáo | Thời gian scan sai làm cycle time và OEE sai. Ghi `ScannedAt` giờ server, cảnh báo nếu khoảng cách giữa hai scan cùng trạm quá ngắn (mở rộng) |
| Scan hai lần cùng bo để "tăng số" | Số đếm ảo | Idempotent + `ScanLog` insert-only để đối chiếu |
| Đưa bo NG trở lại line | Bo lỗi lẫn vào bo tốt | Quy tắc 4a. Bo NG cần khu vực và nhãn riêng ngoài phần mềm |
| Nhãn serial in mờ / dán lệch | Scanner đọc sai, đọc rỗng | Hiển thị "serial không tìm thấy" rõ ràng; cho phép gõ tay |
| Máy quét cấu hình sai layout bàn phím | Serial có ký tự `-` bị đọc thành `_` hoặc chữ hoa/thường lẫn lộn | Chuẩn hóa serial (Trim, ToUpperInvariant) và dùng mã vạch Code128 chỉ chữ số/chữ hoa; kiểm tra cấu hình scanner khi lắp |
| Thợ Repair thay linh kiện nhưng không ghi | Truy xuất sau này thiếu linh kiện thay thế | Trường `ComponentReplaced` bắt buộc khi Action là "Replace" |
| Cập nhật trùng khi hai trạm cùng scan một bo | Race condition; bo nhảy hai bước | rowversion trên `Serial` (2.2) |
| Chờ Test quá lâu, WIP dồn | Không ai nhận ra vì không có báo cáo theo bước | Báo cáo WIP theo bước (1.2, 2.10) |
| Đèn xanh/đỏ không phân biệt được | Khoảng 8% nam giới khó phân biệt đỏ/xanh | Luôn kèm chữ to ("OK"/"BLOCKED"), âm báo khác nhau, không chỉ màu |
| Mạng lỗi, công nhân ghi tay, nhập bù sau | Dữ liệu sai giờ, thiếu | Quyết định MVP: chặn scan khi offline. Ngoài đời có phương án store-and-forward, cân nhắc ở mục 4 |

### 1.6 Bảng thuật ngữ

| English | Tiếng Việt | 日本語 | Giải thích ngắn |
| --- | --- | --- | --- |
| Station | Trạm | ステーション | Nơi vật lý làm một công đoạn, có một Kiosk |
| Operation / Process step | Công đoạn | 工程 (こうてい) | Bước công nghệ trong routing |
| Route check / Interlock | Kiểm tra lộ trình / khóa liên động | 工程チェック / インターロック | Chặn scan nếu chưa đủ điều kiện |
| Poka-yoke | Chống lỗi | ポカヨケ | Thiết kế để lỗi vô ý không xảy ra |
| Serial number | Số serial | シリアル番号 | Định danh từng sản phẩm |
| Barcode / Code128 | Mã vạch | バーコード | Chuỗi mã trên nhãn, máy quét đọc |
| Keyboard wedge | Máy quét giả lập bàn phím | キーボードウェッジ | Scanner gõ chuỗi + Enter như bàn phím |
| OK | Đạt | 良品 (りょうひん) | Sản phẩm đạt |
| NG | Không đạt | 不良 (ふりょう) / NG | Sản phẩm không đạt |
| Defect code | Mã lỗi | 不良コード | Mã chuẩn hóa cho loại lỗi |
| Repair | Sửa chữa | 修理 (しゅうり) | Sửa bo NG rồi kiểm tra lại |
| Rework | Làm lại | 手直し (てなおし) / リワーク | Chạy lại một phần quy trình |
| Scrap | Phế / hủy | 廃棄 (はいき) / スクラップ | Bỏ sản phẩm, không dùng nữa |
| WIP | Hàng dở dang | 仕掛品 (しかかりひん) | Hàng đã bắt đầu, chưa xong |
| FPY | Tỷ lệ đạt lần đầu | 直行率 (ちょっこうりつ) | Bo đạt ngay lần kiểm tra đầu |
| Final yield | Tỷ lệ đạt cuối cùng | 最終歩留まり (さいしゅうぶどまり) | Bo Completed / (Completed + Scrapped) |
| RTY | Tỷ lệ đạt tích lũy qua các công đoạn | 通算直行率 (cách gọi có thể khác theo công ty) | Tích các FPY |
| Cycle time | Thời gian chu kỳ | サイクルタイム | Thời gian làm một sản phẩm ở một công đoạn |
| Idempotent | Idempotent (gửi lại không đổi kết quả) | 冪等 (べきとう) | Gửi N lần = gửi 1 lần |
| Hold | Giữ hàng | ホールド / 保留 | Chặn hàng nghi ngờ đi tiếp |
| Operator | Công nhân vận hành | 作業者 (さぎょうしゃ) / オペレーター | Người thao tác tại trạm |

---

## 2. Kiến thức lập trình

Bạn đã biết C#, ASP.NET Core Web API và EF Core, nên các mục dưới chỉ đi sâu phần mới hoặc liên quan trực tiếp đến module. Phần khái niệm đã học ở module trước được tham chiếu, không nhắc lại.

### 2.1 Domain service `RouteCheck`: hàm thuần, kết quả có lý do

**Nó là gì.** Một hàm (static class hoặc domain service) nhận toàn bộ dữ liệu cần thiết đã đọc sẵn và trả về quyết định: cho phép, trùng lặp, hay chặn kèm lý do. Nó không gọi DB, không đọc giờ hệ thống, không ném exception cho tình huống nghiệp vụ bình thường.

**Vấn đề nó giải quyết.** Cách "ngây thơ": viết `if` rải rác trong controller, vừa truy vấn DB vừa kiểm tra, trả `BadRequest("sai bước")` bằng chuỗi. Hậu quả: không test được nếu không dựng DB, không đổi được nơi triển khai (spec muốn có thể chuyển thành stored procedure để so sánh hiệu năng), lý do là chuỗi tự do nên client không xử lý theo mã được.

**Khái niệm cốt lõi.**
- Tách "đọc dữ liệu" (Application/Infrastructure) khỏi "quyết định" (Domain). Domain nhận `ScanContext` (bản chụp dữ liệu), trả `RouteDecision`.
- Kết quả là giá trị (record) với `Verdict` + `BlockReason` (enum, ổn định, dùng làm mã cho client) + `Message` (cho người đọc).
- Thời gian hiện tại truyền vào (`NowUtc`) để test được. (Chi tiết `TimeProvider` ở Module 5.)
- Thứ tự kiểm tra là một phần của hợp đồng; xem 1.4.

```csharp
// MiniMes.Domain/Execution/RouteCheck.cs  (rút gọn; các enum/record khai báo ở file khác)
public static class RouteCheck
{
    public static RouteDecision Evaluate(ScanContext c)
    {
        static RouteDecision Block(BlockReason r, string m) => new(RouteVerdict.Block, r, m);

        if (c.Serial is null)                           return Block(BlockReason.SerialNotFound, "Serial not found");
        if (c.WoStatus is not (WoStatus.Released or WoStatus.InProgress))
                                                        return Block(BlockReason.WoNotActive, "Work order is not active");
        if (c.Serial.WoId != c.SessionWoId)             return Block(BlockReason.WrongWo, "Serial belongs to another work order");

        // Scan lặp phải kiểm tra trước status/step (xem 1.4)
        if (c.LastScanHere is { } last && c.NowUtc - last.At <= c.DuplicateWindow && last.Result == c.RequestedResult)
            return new(RouteVerdict.Duplicate, BlockReason.None, "Duplicate scan ignored");

        switch (c.Serial.Status)
        {
            case SerialStatus.Hold:      return Block(BlockReason.SerialHold, "Serial is on HOLD");
            case SerialStatus.Scrapped:  return Block(BlockReason.SerialScrapped, "Serial is scrapped");
            case SerialStatus.Completed: return Block(BlockReason.SerialCompleted, "Serial already completed");
        }

        var defective = c.Serial.Status is SerialStatus.Ng or SerialStatus.Repair;
        if (c.Station.IsRepair)
            return defective ? new(RouteVerdict.Allow, BlockReason.None, "Repair check-in")
                             : Block(BlockReason.NotDefective, "Serial is not defective");
        if (defective)                                  return Block(BlockReason.MustGoRepair, "Serial is NG: send to Repair");

        var stepHere = c.Operations.FirstOrDefault(o => o.WorkCenterId == c.Station.WorkCenterId);
        if (stepHere is null)                           return Block(BlockReason.StationNotInRoute, "Station is not in this routing");

        // [v2] Cổng FAI: khi Pending chỉ 1 serial mẫu được đi
        var assignFai = false;
        if (c.FaiStatus == FaiStatus.Pending)
        {
            var isFirstStep = stepHere.Seq == c.Operations.Min(o => o.Seq);
            if (c.FaiSerialNo is null && isFirstStep && c.Serial.CurrentSeq == 0) assignFai = true;   // 6a
            else if (c.FaiSerialNo != c.Serial.SerialNo)
                return Block(BlockReason.FaiNotApproved, "First article not approved yet");          // 6b
        }
        var expected = c.Serial.CurrentSeq + 1;
        if (stepHere.Seq > expected)                    return Block(BlockReason.SkippedStep, $"Expected step {expected}, this is step {stepHere.Seq}");
        if (stepHere.Seq < expected)                    return Block(BlockReason.AlreadyPassed, $"Step {stepHere.Seq} already passed");
        return new(RouteVerdict.Allow, BlockReason.None, "OK") { AssignFaiSample = assignFai };   // [v2]
    }
}
```

Bảng test (xUnit `[Theory]` + `[MemberData]`, hoặc `[InlineData]` với builder nhỏ). Ít nhất 12 case (**[v2]**, bản cũ 10); bảng dưới có 19:

| # | Tình huống | Scan tại | Kỳ vọng |
| --- | --- | --- | --- |
| 1 | `PCBA-000001` Created (Cur 0) | SMT | Allow |
| 2 | `PCBA-000002` InProcess Cur 1 | ASM | Allow |
| 3 | `PCBA-000003` InProcess Cur 1 | TEST | Block `SkippedStep` |
| 4 | `PCBA-000004` InProcess Cur 3 | ASM, scan cuối 40 giây trước | Block `AlreadyPassed` |
| 5 | `PCBA-000005` Cur 2, vừa scan ASM 2 giây trước | ASM | Duplicate |
| 6 | Như 5 nhưng vừa scan 30 giây trước | ASM | Block `AlreadyPassed` |
| 7 | `PCBA-000007` Ng | TEST | Block `MustGoRepair` |
| 8 | `PCBA-000007` Ng | REPAIR | Allow (check-in) |
| 9 | `PCBA-000008` InProcess | REPAIR | Block `NotDefective` |
| 10 | `PCBA-000009` sau Repair (InProcess, Cur 2) | TEST | Allow |
| 11 | `PCBA-000010` Hold | ASM | Block `SerialHold` |
| 12 | `PCBA-000011` Scrapped | bất kỳ | Block `SerialScrapped` |
| 13 | `PCBA-000012` Completed, scan Pack 30 giây sau | PACK | Block `SerialCompleted` |
| 14 | `PCBA-900001` thuộc `WO-0002`, phiên `WO-0001` | ASM | Block `WrongWo` |
| 15 | WO `Completed` | ASM | Block `WoNotActive` |
| 16 **[v2]** | WO FAI Pending, chưa có mẫu; `PCBA-000001` Created | SMT | Allow, `AssignFaiSample = true` |
| 17 **[v2]** | WO FAI Pending, mẫu là `…001`; `PCBA-000002` Created | SMT | Block `FaiNotApproved` |
| 18 **[v2]** | WO FAI Pending, mẫu là `…001`; `PCBA-000001` Cur 1 | ASM | Allow, `AssignFaiSample = false` |
| 19 **[v2]** | WO FAI Passed; `PCBA-000002` Created | SMT | Allow |

**Bẫy thường gặp.**
- Kiểm tra scan lặp sau step/status: scan lặp sẽ bị báo lỗi thay vì OK.
- Dùng `DateTime.Now` trong domain: test lúc pass lúc fail; phải truyền thời gian vào.
- Trả `string` làm mã lỗi: client so sánh chuỗi, dễ gãy khi sửa câu chữ. Dùng enum.
- Hai bước trong routing cùng WorkCenter (ví dụ Test hai lần): `FirstOrDefault` ở trên cho kết quả mơ hồ. MVP giả định mỗi WorkCenter xuất hiện một lần; khi cần, đổi thành: tìm bước `Seq == CurrentSeq + 1` rồi so `WorkCenterId` với trạm.
- Nếu routing có bước `IsMandatory = false` (Module 1): mở rộng bằng cách bỏ qua bước không bắt buộc khi tìm bước kế tiếp. Seed MVP đặt tất cả bước bắt buộc.
- **[v2]** Hàm thuần chỉ **quyết định** "serial này được làm mẫu FAI" (`AssignFaiSample`), không tự ghi. Việc ghi `FaiSerialNo` nằm ở handler (2.2), có điều kiện ở DB để chống hai trạm cùng chọn mẫu.

### 2.2 Use case Scan: transaction, concurrency, đếm sản lượng (hot path)

**Nó là gì.** Handler của lệnh `ScanCommand` (MediatR, xem Module 1) điều phối: đọc dữ liệu, gọi `RouteCheck`, và nếu được phép thì ghi 3 thứ trong một transaction: dòng `ScanLog`, trạng thái `Serial`, bộ đếm `WorkOrderOperation`.

**Vấn đề nó giải quyết.** Cách "ngây thơ": đọc-tăng-ghi bộ đếm: `op.QtyGood++; SaveChanges()`. Hai scan đồng thời cùng đọc `QtyGood = 100`, cùng ghi 101, mất một lần đếm (lost update). Ngoài ra nếu ghi `ScanLog` thành công mà cập nhật `Serial` lỗi thì hai bảng lệch nhau.

**Khái niệm cốt lõi.**
- Concurrency của cùng một serial: dùng rowversion trên `Serial` (đã học ở Module 2). Hai scan đồng thời cùng serial: một cái thắng, cái kia nhận `DbUpdateConcurrencyException`. Xử lý: thử lại toàn bộ handler một lần. Lần thử hai đọc lại và thấy bản ghi vừa được ghi, trả `Duplicate` hoặc `Block` đúng.
- Bộ đếm: dùng cập nhật nguyên tử ở phía DB `SET QtyGood = QtyGood + 1` (EF Core 7+: `ExecuteUpdateAsync`) thay vì đọc-tăng-ghi. Đây cũng là "hot row": mọi scan cùng một bước của cùng WO cùng ghi một dòng `WorkOrderOperation`. Ở quy mô demo (vài chục scan/phút) hoàn toàn ổn; nếu nghẽn, chuyển sang tính bộ đếm từ `ScanLog` theo chu kỳ.
- Số round-trip tới DB ít (mục tiêu 5–6): đọc `Serial` + trạng thái WO, đọc scan gần nhất tại trạm, ghi. Cấu hình trạm và snapshot routing của WO gần như bất biến sau khi release nên cache trong bộ nhớ (`IMemoryCache`) theo `WoId`; chỉ trạng thái WO cần đọc mới.

```csharp
// MiniMes.Application/Execution/Scan/ScanCommandHandler.cs (rút gọn, không bao gồm DTO/mapping)
public async Task<ScanResponse> Handle(ScanCommand cmd, CancellationToken ct)
{
    for (var attempt = 0; attempt < 2; attempt++)                       // thử lại 1 lần khi đụng concurrency
    {
        var ctx = await _reader.LoadAsync(cmd, _clock.GetUtcNow().UtcDateTime, ct); // đọc Serial+WO+scan gần nhất (+cache routing)
        var decision = RouteCheck.Evaluate(ctx);

        if (decision.Verdict == RouteVerdict.Block)     return ScanResponse.Blocked(decision);
        if (decision.Verdict == RouteVerdict.Duplicate) return ScanResponse.DuplicateOf(ctx.LastScanHere!);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var serial = await _db.Serials.SingleAsync(s => s.SerialNo == cmd.SerialNo, ct); // tracked, có rowversion
            var outcome = serial.ApplyScan(ctx.Station, ctx.StepHere, cmd.Result, cmd.DefectCode); // đổi CurrentSeq/Status trong Domain

            if (decision.AssignFaiSample)                                 // [v2] chọn mẫu FAI, có điều kiện ở DB
            {
                var taken = await _db.WorkOrders
                    .Where(w => w.Id == serial.WoId && w.FaiStatus == FaiStatus.Pending && w.FaiSerialNo == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(w => w.FaiSerialNo, serial.SerialNo), ct);
                if (taken == 0)                                           // trạm khác vừa chọn mẫu trước
                {
                    await tx.RollbackAsync(ct);
                    return ScanResponse.Blocked(RouteDecision.FaiNotApproved);
                }
            }
            _db.ScanLogs.Add(ScanLog.Create(serial, ctx.Station, cmd, outcome, ctx.NowUtc));
            await _db.SaveChangesAsync(ct);                              // kiểm tra rowversion ở đây

            // Cập nhật nguyên tử ở DB: không đọc-tăng-ghi
            await _db.WorkOrderOperations
                .Where(o => o.WoId == serial.WoId && o.Seq == ctx.StepHere.Seq)
                .ExecuteUpdateAsync(s => outcome.IsOk
                    ? s.SetProperty(o => o.QtyGood, o => o.QtyGood + 1)
                    : s.SetProperty(o => o.QtyNg,   o => o.QtyNg + 1), ct);

            await tx.CommitAsync(ct);
            return ScanResponse.From(decision, outcome);
        }
        catch (DbUpdateConcurrencyException) when (attempt == 0) { /* vòng lặp đọc lại */ }
    }
    return ScanResponse.Busy();                                          // hiếm: báo "quét lại"
}
```

Snippet ghi chú: `ExecuteUpdateAsync` với biểu thức điều kiện `outcome.IsOk ? ... : ...` viết được vì `outcome` là biến C# ngoài truy vấn, nhưng cú pháp `SetProperty` khác nhau giữa các phiên bản EF Core (EF Core 10 thêm dạng lambda thường). Đây là chỗ cần kiểm tra lại với phiên bản EF Core bạn dùng; nếu không chắc, tách thành hai nhánh `if/else`, mỗi nhánh một `ExecuteUpdateAsync`.

**Bẫy thường gặp.**
- `ExecuteUpdate` bỏ qua change tracker và interceptor `SaveChanges` (audit của Module 0 không thấy thay đổi). Với bộ đếm thì chấp nhận được (audit chỉ áp cho bảng master và trạng thái WO); nếu cần audit, ghi rõ.
- Đặt `ScanLog` insert ngoài transaction, hoặc `SaveChanges` rồi mới `BeginTransaction`: không bảo vệ được cả ba.
- Load cả `Include` WO + tất cả operations + tất cả serial: nặng vô ích ở hot path. Truy vấn theo `SerialNo` chính xác, `AsNoTracking()` cho phần đọc thuần.
- Tính bộ đếm bằng `COUNT(*)` trên `ScanLog` mỗi lần scan: chậm dần khi log lớn.
- Quên thử lại khi `DbUpdateConcurrencyException`: operator thấy lỗi ngẫu nhiên khi scanner bắn đúp.
- **[v2]** Gán `FaiSerialNo` bằng cách load `WorkOrder` rồi `wo.AssignFaiSerial()` + `SaveChanges`: hai trạm cùng đọc `FaiSerialNo = null` và cùng ghi; serial ghi sau thắng, serial ghi trước đã được cho qua mà không phải mẫu. Cập nhật có điều kiện `WHERE FaiSerialNo IS NULL` và kiểm tra số dòng bị ảnh hưởng giải quyết việc này mà không cần khóa. (Rowversion trên `WorkOrder` cũng làm được, nhưng sẽ xung đột với mọi cập nhật khác của WO.)
- Bắt exception rộng `catch (Exception)` và nuốt: mất chẩn đoán. Serilog (Module 0) ghi `SerialNo`, `StationId`, `CorrelationId`.

### 2.3 Idempotency cho scan (retry an toàn)

**Nó là gì.** Tính chất "gửi cùng một yêu cầu nhiều lần thì hệ thống chỉ thực hiện một lần". Ở API, gọi tắt: at-least-once delivery + idempotent handler = effectively-once.

**Vấn đề nó giải quyết.** Mạng có thể mất phản hồi dù server đã xử lý xong. Client không biết nên coi là đã gửi hay chưa; gửi lại thì có nguy cơ ghi hai lần. Máy test tự động, Kiosk đều đối mặt tình huống này.

**Khái niệm cốt lõi.** Hai cách phổ biến:

| Cách | Cơ chế | Ưu | Nhược |
| --- | --- | --- | --- |
| Cửa sổ thời gian tự nhiên (spec chọn) | Cùng serial + trạm + kết quả, trong N giây so với bản ghi gần nhất → coi là lặp | Không cần client sinh khóa; scanner bắn đúp cũng được che | Client gửi lại sau N giây sẽ không được nhận ra |
| Idempotency key | Client sinh GUID cho mỗi yêu cầu, server nhớ | Chính xác tuyệt đối cho retry | Scanner bắn đúp tạo hai GUID khác nhau, vẫn cần cửa sổ; cần bảng/cache lưu key |

Dự án dùng cửa sổ thời gian vì nguồn gây lặp chính là scanner (không thể gắn key). Cho retry của máy, N mặc định 5 giây đủ vì máy retry ngay sau timeout ngắn. Trường hợp hai request giống hệt đến cùng lúc (chưa cái nào kịp ghi): cả hai qua bước kiểm tra "scan gần nhất", cả hai vào transaction; rowversion khiến một cái thất bại, handler thử lại, lần này thấy bản ghi và trả `Duplicate`. Không cần khóa riêng.

**Bẫy thường gặp.**
- Ghi bản ghi trùng vì kiểm tra "scan gần nhất" đọc trước khi transaction bắt đầu mà không có rowversion làm chốt: thiếu chốt chặn cuối.
- Cửa sổ quá dài (ví dụ 60 giây) che mất scan hợp lệ (ví dụ Test NG, Repair siêu nhanh, retest). Giữ ở mức vài giây.
- Trả lỗi 409 cho scan lặp: client máy coi là thất bại và retry mãi. Trả 200 kèm `duplicate = true`.
- Không lưu dấu "kết quả yêu cầu" khi so sánh: máy gửi OK rồi NG cùng bo cách 1 giây (bug máy) bị coi là lặp. Nên so cả `Result`.

### 2.4 Hợp đồng API dùng chung cho Kiosk và Simulator

**Nó là gì.** Một endpoint, một cặp DTO nằm ở `MiniMes.Contracts`, hai client cùng dùng: WPF Kiosk (qua `MiniMes.Client.Core`) và Machine Simulator vai trạm test.

**Vấn đề nó giải quyết.** Nếu làm endpoint riêng cho máy, hai đường code có thể lệch nhau (một bên quên route check). Một đường duy nhất buộc mọi nguồn dữ liệu đi qua cùng poka-yoke và cùng nhật ký. Đây cũng là quyết định đã chốt: kết quả OK/NG theo serial (dù người hay máy gửi) là nguồn duy nhất cho chất lượng (Module 5).

**Khái niệm cốt lõi.**
- Response có ba nhóm kết quả rõ ràng: `Ok` (chấp nhận, sản phẩm đạt), `Ng` (chấp nhận, sản phẩm không đạt đã được ghi), `Blocked` (route check từ chối, chưa ghi gì).
- Blocked trả HTTP 200 kèm `reasonCode`, không phải 4xx. Lý do: đó là kết quả nghiệp vụ bình thường, xảy ra hàng ngày, không phải lỗi giao thức. 4xx dành cho request sai định dạng (400), chưa đăng nhập (401), thiếu quyền (403), trạm không tồn tại (404). Phương án dùng 409/422 cho Blocked cũng hợp lý; xem mục 4.
- Danh tính máy: Simulator đăng nhập bằng tài khoản dịch vụ (role Operator) lấy JWT như operator. Không tạo cơ chế xác thực riêng ở MVP. Ghi thêm `Source` (`Kiosk` hoặc `Machine`) vào `ScanLog` giúp phân biệt sau này (cột thêm, không trong spec gốc).
- Simulator có thể tái dùng `ApiClient` của `MiniMes.Client.Core` nếu project đó không phụ thuộc WPF; nếu có phụ thuộc, dùng `HttpClient` + Contracts trực tiếp.

```csharp
// MiniMes.Contracts/Execution/ScanContracts.cs
public sealed record ScanRequest(
    string SerialNo,
    string Result = "OK",          // "OK" | "NG"; trạm thủ công bỏ trống = OK
    string? DefectCode = null);    // bắt buộc khi Result = "NG"

public sealed record ScanResponse(
    string Result,                 // "Ok" | "Ng" | "Blocked"
    string Message,                // câu hiển thị cho operator (tiếng Anh; chuỗi UI thật lấy từ .resx phía client theo ReasonCode)
    string? ReasonCode,            // null khi thành công; ví dụ "SkippedStep", "MustGoRepair"
    int? NextStep,                 // Seq bước kế tiếp, null nếu đã Completed
    string? NextStepName,          // "Test"
    bool Duplicate);               // true nếu là scan lặp, không ghi thêm

// Endpoint: POST /stations/{id}/scan -> 200 ScanResponse
// Khác: 400 (định dạng), 401, 403, 404 (trạm), 409 chỉ dùng khi hết lần retry concurrency
```

**Bẫy thường gặp.**
- Đưa chuỗi hiển thị vào server: UI đa ngôn ngữ (.resx) không dịch được. Server trả `ReasonCode`, client map sang chuỗi `.resx`; `Message` chỉ là dự phòng.
- Kiosk gửi `operatorId` trong body: giả mạo được. Lấy từ JWT.
- Không kiểm tra `DefectCode` tồn tại: FluentValidation (Module 1) rule "NG thì phải có mã hợp lệ".
- Simulator gọi API tuần tự: không thấy được vấn đề đồng thời. Khi đo hiệu năng cho chạy song song nhiều trạm.
- (Tùy chọn) Ở Test thủ công, operator muốn biết bị chặn TRƯỚC khi đi test. Thêm tham số `dryRun` (chỉ kiểm tra, không ghi) là phần mở rộng ngoài spec; nếu không làm, chấp nhận rằng ở Test thủ công lỗi route chỉ lộ khi bấm nút OK/NG.

### 2.5 Index & execution plan

**Nó là gì.** Index là cấu trúc B-tree giúp tìm dòng theo cột nhanh mà không đọc cả bảng. Execution plan là kế hoạch SQL Server chọn để chạy truy vấn: đọc bảng bằng Index Seek hay Scan, có Key Lookup không, bao nhiêu dòng ước tính so với thực tế.

**Vấn đề nó giải quyết.** `ScanLog` là bảng log lớn (spec dự kiến 5–10 triệu dòng để đo). Truy vấn "scan gần nhất của serial X tại trạm Y" trên bảng không index phải quét cả 10 triệu dòng: hàng trăm ms đến vài giây, đúng bằng "scan chậm".

**Khái niệm cốt lõi.**
- Khóa cụm (clustered index) là `Id` (bigint identity, chèn tuần tự cuối bảng, không tách trang: hợp bảng insert-only).
- Index không cụm `(SerialNo, ScannedAt)` theo spec. Có thể thêm `INCLUDE (StationId, Result)` để truy vấn route check được "phủ" (covering): đọc đủ dữ liệu ngay trong index, không phải Key Lookup về bảng chính.
- Trade-off: mỗi lần insert ghi thêm vào index. Log insert-only nên chấp nhận được, nhưng đừng thêm index thừa.
- Đọc plan: Index Seek + số logical reads nhỏ = tốt. Clustered Index Scan trên bảng lớn = xấu. Với `SET STATISTICS IO, TIME ON` xem `logical reads` và `elapsed time`.
- `Serial.SerialNo` có unique index: đọc theo serial là seek, độc lập với kích thước `ScanLog`. Vì vậy phần đọc trạng thái nhanh ngay cả khi log rất lớn; chỉ truy vấn tìm scan lặp phụ thuộc index của `ScanLog`.

```sql
-- Truy vấn cần tối ưu: scan gần nhất của serial tại trạm
SET STATISTICS IO, TIME ON;   -- xem logical reads, thời gian

SELECT TOP (1) ScannedAt, Result
FROM   dbo.ScanLog
WHERE  SerialNo = @SerialNo AND StationId = @StationId
ORDER  BY ScannedAt DESC;

-- Trước: không index => Clustered Index Scan, ~ hàng chục nghìn logical reads với vài triệu dòng
-- Sau: index theo spec, thêm INCLUDE để phủ truy vấn
CREATE NONCLUSTERED INDEX IX_ScanLog_SerialNo_ScannedAt
ON dbo.ScanLog (SerialNo, ScannedAt DESC)
INCLUDE (StationId, Result);

-- Chạy lại, so sánh: Index Seek, logical reads còn vài trang
-- Xem plan thực tế trong SSMS/Azure Data Studio: bật "Include Actual Execution Plan" rồi chạy
```

Kịch bản đo (bước 10 của kế hoạch): chạy ba phương án và ghi bảng vào README/`docs/perf.md`:
1. Không index.
2. Index `(SerialNo, ScannedAt)` theo spec.
3. Index có `INCLUDE`; hoặc phi chuẩn hóa: lưu `LastScanStationId`, `LastScanAt`, `LastScanResult` ngay trên `Serial` để kiểm tra scan lặp không phải chạm `ScanLog` (đọc theo unique index của `Serial` đủ). Kết quả này giúp trả lời câu phỏng vấn "vì sao/không phi chuẩn hóa".
4. (Tùy chọn, spec gợi ý) Viết route check dạng stored procedure để so sánh.

**Bẫy thường gặp.**
- Đo trên bảng vài nghìn dòng rồi kết luận "nhanh": phải seed đủ lớn.
- Parameter sniffing và kiểu dữ liệu không khớp: `SerialNo` cột là `varchar` mà EF gửi `nvarchar` (mặc định string → `nvarchar`) khiến SQL Server chuyển kiểu trên cột và bỏ qua index (implicit conversion). Cấu hình `.IsUnicode(false)` và `HasMaxLength(...)` cho cột serial. Đây là nguyên nhân "có index mà vẫn scan" rất hay gặp; nhìn plan có cảnh báo `CONVERT_IMPLICIT`.
- Tối ưu index nhưng thứ tự cột sai: `(ScannedAt, SerialNo)` không giúp truy vấn tìm theo `SerialNo`.
- Index trên cột rộng (`SerialNo` dài): tăng kích thước index. Serial ngắn (20–30 ký tự).

### 2.6 Đo hiệu năng với dữ liệu lớn

**Nó là gì.** Quy trình: tạo dữ liệu giả cỡ triệu dòng, chạy tải lặp lại, đo phân phối thời gian (p50/p95/p99), so sánh trước và sau khi tối ưu.

**Vấn đề nó giải quyết.** "Chạy trên máy tôi nhanh" không chứng minh gì. Số đo trước/sau là thứ bạn kể được trong phỏng vấn ("giảm từ 480 ms xuống 6 ms nhờ index có INCLUDE": số của bạn, không phải số trong tài liệu này).

**Khái niệm cốt lõi.**
- Dùng percentile, không dùng trung bình: trung bình 30 ms vẫn có thể có 1% lần 800 ms, chính những lần đó làm operator đỏ mặt.
- Seed bằng T-SQL sinh số (`ROW_NUMBER` trên cross join hệ thống) nhanh hơn insert qua EF. (Batching bằng `SqlBulkCopy` học ở Module 4.)
- Khởi động nóng (warm-up): lần gọi đầu chậm do JIT, tạo model EF, mở kết nối. Bỏ 100 lần đầu ra khỏi số đo.
- Đo ở hai tầng: server-side (`SET STATISTICS TIME`, log thời gian handler) và client-side (Stopwatch cả vòng HTTP).
- Chạy API ở cấu hình Release (`dotnet run -c Release`), DB cùng máy hoặc cùng mạng như dự kiến.

```sql
-- Seed 1 triệu dòng ScanLog mỗi lần (chạy nhiều lần đổi @Offset để có 5–10 triệu)
DECLARE @Offset int = 0;   -- lần 2: 1000000, lần 3: 2000000 ...
;WITH n AS (
    SELECT TOP (1000000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) + @Offset AS i
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
)
INSERT INTO dbo.ScanLog (SerialNo, WoId, StationId, Seq, Result, DefectCode, OperatorId, ScannedAt)
SELECT CONCAT('SEED-', RIGHT(CONCAT('00000000', i / 4), 8)),   -- mỗi serial 4 scan
       1,
       (i % 4) + 1,                                            -- trạm 1..4
       (i % 4) + 1,
       CASE WHEN i % 50 = 0 AND i % 4 = 2 THEN 'NG' ELSE 'OK' END,
       CASE WHEN i % 50 = 0 AND i % 4 = 2 THEN 'SOL-BRIDGE' END,
       1,
       DATEADD(SECOND, i, '2026-01-01')
FROM n;
-- Lưu ý: tạo index SAU khi seed sẽ nhanh hơn; thay đổi tên cột/kiểu cho khớp migration của bạn.
```

```csharp
// tools hoặc integration test: đo p50/p95/p99 của API scan (chạy song song 8 luồng)
var latencies = new ConcurrentBag<double>();
await Parallel.ForEachAsync(serials, new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (sn, ct) =>
{
    var sw = Stopwatch.StartNew();
    using var res = await http.PostAsJsonAsync($"/stations/{stationId}/scan", new ScanRequest(sn), ct);
    sw.Stop();
    latencies.Add(sw.Elapsed.TotalMilliseconds);
});
var sorted = latencies.OrderBy(x => x).ToArray();
double P(double p) => sorted[(int)Math.Ceiling(p * sorted.Length) - 1];
Console.WriteLine($"n={sorted.Length} p50={P(0.50):F1} p95={P(0.95):F1} p99={P(0.99):F1} ms");
```

**Bẫy thường gặp.**
- Chỉ đo bước SMT với serial `Created`: chưa đại diện; đo cả bước Test có NG và Repair.
- Dữ liệu seed phân bố quá đều: thực tế một serial có ít dòng, còn bảng có hàng triệu serial khác nhau. Phân bố như trong seed ở trên (mỗi serial 4 dòng) là hợp lý.
- Log nhiều (Serilog Debug, ghi từng câu SQL) làm chậm số đo. Đặt mức Information khi đo.
- Không ghi cấu hình máy (CPU, RAM, ổ đĩa) vào README: số liệu mất ý nghĩa.

### 2.7 WPF Kiosk: máy quét mã vạch (keyboard wedge), focus và phím Enter

**Nó là gì.** Máy quét mã vạch phổ biến hoạt động như bàn phím (keyboard wedge): quét xong thì "gõ" chuỗi mã kèm phím Enter (đôi khi Tab, tùy cấu hình) vào control đang có focus. Kiosk không cần thư viện quét, chỉ cần một `TextBox` luôn có focus và bắt Enter.

**Vấn đề nó giải quyết.** Cách ngây thơ: ô nhập bình thường, operator phải click vào ô trước khi quét. Trong ca 1.200 lần quét, vài lần focus lạc (bấm nút, popup đóng, Alt-Tab) là quét vào hư không: không có phản hồi, operator quét lại, sinh scan lặp. Kiosk phải đảm bảo mọi ký tự từ scanner đều đi vào ô scan.

**Khái niệm cốt lõi.**
- Focus trong WPF: `Keyboard.Focus(element)`, `element.Focus()`, attached property `FocusManager.FocusedElement`.
- Nút bấm cảm ứng đặt `Focusable="False"` để click không cướp focus khỏi ô scan.
- `KeyBinding Key="Return"` trên `TextBox` gắn với `ICommand`. Bẫy nổi tiếng: mặc định `TextBox.Text` cập nhật nguồn binding khi mất focus (`LostFocus`), nên khi Enter đến, ViewModel còn giữ chuỗi cũ. Phải đặt `UpdateSourceTrigger=PropertyChanged`.
- Sau khi scan: xóa ô (`ScanInput = ""`); vì focus chưa từng rời ô nên không cần focus lại. Focus chỉ mất khi cửa sổ mất kích hoạt: hook sự kiện `Activated` để lấy lại.
- MessageBox/dialog modal cướp focus và chặn scan: dùng lớp phủ (overlay) trong cùng cửa sổ thay cho `MessageBox`.
- Chuẩn hóa chuỗi: `Trim()` và `ToUpperInvariant()` trước khi gửi, vì scanner cấu hình khác nhau có thể thêm ký tự cuối hoặc đổi hoa/thường.

```xml
<!-- MiniMes.Kiosk.Wpf/Views/ScanView.xaml (rút gọn) -->
<TextBox x:Name="ScanBox"
         Text="{Binding ScanInput, UpdateSourceTrigger=PropertyChanged}"
         FontSize="36" HorizontalContentAlignment="Center"
         IsReadOnly="{Binding IsOffline}"
         AutomationProperties.Name="Scan serial">
    <TextBox.InputBindings>
        <!-- Scanner gõ chuỗi + Enter: Enter kích hoạt lệnh -->
        <KeyBinding Key="Return" Command="{Binding ScanCommand}" />
    </TextBox.InputBindings>
</TextBox>

<!-- Nút bấm cảm ứng không cướp focus -->
<Button Content="NG" Focusable="False" Command="{Binding OpenNgPanelCommand}"
        MinHeight="90" MinWidth="160" FontSize="28"/>
```

```csharp
// MiniMes.Kiosk.Wpf/MainWindow.xaml.cs — logic THUẦN VIEW (focus), không phải nghiệp vụ, nên đặt ở code-behind là chấp nhận được
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded    += (_, _) => ScanBox.Focus();
        Activated += (_, _) => ScanBox.Focus();     // Alt-Tab quay lại thì lấy lại focus
        ScanBox.LostKeyboardFocus += (_, e) =>
        {
            // Focus rời ScanBox sang thứ không phải control nhập liệu (vd panel chọn DefectCode): giữ nguyên.
            // Ngược lại kéo focus về sau khi các sự kiện hiện tại xử lý xong.
            if (e.NewFocus is null)
                Dispatcher.BeginInvoke(new Action(() => ScanBox.Focus()), DispatcherPriority.Input);
        };
    }
}
```

**Bẫy thường gặp.**
- `UpdateSourceTrigger` mặc định: lệnh scan luôn nhận chuỗi rỗng hoặc chuỗi của lần trước.
- Phím Enter mặc định của một nút (`IsDefault="True"`) chặn `KeyBinding`: đừng đặt `IsDefault` ở view scan.
- Scanner ở chế độ gửi thêm Tab hoặc CR+LF: cấu hình scanner "suffix Enter". Nếu Tab được gửi, focus nhảy sang control khác; dùng `KeyboardNavigation.TabNavigation="None"` trên container để chặn.
- Layout bàn phím trên PC của trạm (ví dụ bộ gõ tiếng Việt Telex) làm biến dạng chuỗi scan. PC Kiosk nên dùng layout `en-US` và tắt bộ gõ; hoặc chỉ dùng mã vạch chữ số/chữ hoa.
- Người dùng gõ tay chậm: có thể phân biệt scanner (chuỗi tới trong < 50 ms) với gõ tay để cảnh báo, nhưng chỉ nên làm nếu có yêu cầu, không cần cho MVP.
- Scanner nhanh hơn phản hồi API: hai chuỗi tới liên tiếp. Với CommunityToolkit.Mvvm, `AsyncRelayCommand` mặc định không cho chạy song song: lần Enter thứ hai bị bỏ rơi im lặng trong khi lệnh đầu chưa xong. Đặt `[RelayCommand(AllowConcurrentExecutions = true)]` cho lệnh scan (kiểm tra tên thuộc tính trong tài liệu thư viện phiên bản bạn dùng), hoặc dùng hàng đợi nhỏ.

### 2.8 WPF Kiosk: toàn màn hình, nền đổi màu theo trạng thái, âm báo

**Nó là gì.** Cửa sổ không viền, chiếm toàn màn hình; màu nền và chữ lớn phản ánh kết quả scan gần nhất; kèm âm thanh.

**Vấn đề nó giải quyết.** Operator nhìn màn hình từ 1–2 mét, tay đang bận. Thông báo nhỏ trong dialog sẽ bị bỏ lỡ. Phản hồi phải hiểu được trong 0,3 giây, kể cả khi không đọc chữ.

**Khái niệm cốt lõi.**
- Cửa sổ: `WindowStyle="None"`, `WindowState="Maximized"`, `ResizeMode="NoResize"`. Thoát ra chỉ bằng tổ hợp phím ẩn cho supervisor. Với một số cấu hình, cửa sổ vẫn để lộ taskbar (hoặc ngược lại); nếu gặp, thử `Topmost="True"` hoặc dùng chế độ kiosk của Windows (Assigned Access). Kiểm tra lại trên máy thật, tôi không khẳng định hành vi này chắc chắn trên mọi phiên bản Windows.
- Trạng thái UI dạng enum `ScanState { Idle, Ok, Ng, Blocked, Offline }` trong ViewModel; XAML dùng `DataTrigger` để đổi nền. ViewModel không biết màu.
- Sau 1,5–2 giây tự về `Idle`. Vì có thể có scan mới trong lúc đó, dùng số phiên bản (`_flashVersion`) để bản hẹn giờ cũ không xóa trạng thái mới.
- Âm thanh: `System.Media.SystemSounds` (`Asterisk`, `Hand`, `Exclamation`, `Beep`). Đơn giản, đủ cho MVP. Chọn âm khác nhau cho OK và cho lỗi, vì nhiều operator đeo tai nghe bảo hộ, âm là kênh phụ.
- Màu không được là kênh duy nhất: kèm chữ lớn "OK", "BLOCKED", biểu tượng, và lý do ngắn.
- Chuỗi hiển thị lấy từ `.resx` theo `ReasonCode` (xem Module 0).

```xml
<!-- Nền đổi theo ScanState -->
<Grid>
  <Grid.Style>
    <Style TargetType="Grid">
      <Setter Property="Background" Value="#202124"/>
      <Style.Triggers>
        <DataTrigger Binding="{Binding State}" Value="Ok">
          <Setter Property="Background" Value="#1B8F3A"/>
        </DataTrigger>
        <DataTrigger Binding="{Binding State}" Value="Ng">
          <Setter Property="Background" Value="#C62828"/>
        </DataTrigger>
        <DataTrigger Binding="{Binding State}" Value="Blocked">
          <Setter Property="Background" Value="#C62828"/>
        </DataTrigger>
      </Style.Triggers>
    </Style>
  </Grid.Style>
  <TextBlock Text="{Binding Headline}" FontSize="96" Foreground="White"
             HorizontalAlignment="Center" VerticalAlignment="Center"/>
</Grid>
```

```csharp
// ScanViewModel (CommunityToolkit.Mvvm 8.x, rút gọn): hiển thị kết quả rồi tự về Idle
private int _flashVersion;

private async Task ShowAsync(ScanState state, string headline, string message)
{
    var v = ++_flashVersion;
    State = state; Headline = headline; Message = message;
    (state == ScanState.Ok ? SystemSounds.Asterisk : SystemSounds.Hand).Play();

    await Task.Delay(TimeSpan.FromMilliseconds(1800));    // tiếp tục trên UI thread vì đang ở trong lệnh async của UI
    if (v == _flashVersion) { State = ScanState.Idle; Headline = "READY"; Message = ""; }
}
```

**Bẫy thường gặp.**
- Đặt logic màu ở code-behind hoặc ViewModel trả `Brush`: khó đổi giao diện, khó test.
- Quên kiểm tra `_flashVersion`: scan nhanh liên tiếp bị xóa màu quá sớm.
- Dùng `MessageBox.Show` cho lỗi scan: cướp focus, chặn scan tiếp (xem 2.7).
- Chữ nhỏ, độ tương phản thấp trên màn hình công nghiệp chói sáng. Thử ở khoảng cách thực.

### 2.8b [v2] WPF Kiosk: xem SOP tại trạm

**Nó là gì.** Nút "SOP" trên màn hình scan mở tài liệu hướng dẫn công việc (PDF/ảnh) của bước mà trạm đang làm, lấy từ `WorkOrderOperation.SopUrl` (M2 snapshot từ `RoutingStep.SopUrl` của M1).

**Vấn đề nó giải quyết.** Ngoài đời SOP in giấy dán ở trạm; đổi model hoặc đổi version quy trình thì tờ giấy cũ hay bị quên thay. Hiện SOP từ MES đảm bảo operator luôn xem đúng version gắn với WO đang chạy (OpenMES và Carbon đều có tính năng này).

**Khái niệm cốt lõi.**

- Hiển thị trong **lớp phủ (overlay)** của chính cửa sổ kiosk bằng control `WebView2` (Microsoft Edge WebView2, có sẵn trình xem PDF). Không dùng `Process.Start` mở trình đọc PDF ngoài: kiosk toàn màn hình sẽ bị che, operator phải Alt-Tab, và focus ô scan bị mất.
- API phục vụ file: `GET /sop/{**path}` (static file từ thư mục cấu hình, chỉ đọc, cần JWT). URL trong DB là đường dẫn tương đối, không phải đường dẫn ổ đĩa.
- Đóng overlay → trả focus về `ScanBox` (mục 2.7). Trong lúc overlay mở, scan vẫn hoạt động: nếu có chuỗi scan tới thì tự đóng overlay và xử lý scan.
- Nút SOP ẩn khi bước không có `SopUrl`.

**Bẫy.** WebView2 cần Runtime cài trên máy (Windows 11 có sẵn); khởi tạo bất đồng bộ (`EnsureCoreWebView2Async`) nên phải chờ trước khi gán `Source`. WebView2 cũng nhận focus bàn phím: phải kéo focus về ô scan khi đóng.

### 2.9 WPF Kiosk: mất kết nối, timeout

**Nó là gì.** Cách Kiosk phát hiện API không tới được, báo rõ cho operator và không để scan rơi vào khoảng mờ. Quyết định MVP: hiện banner đỏ "OFFLINE", không cho scan, không lưu tạm cục bộ.

**Vấn đề nó giải quyết.** `HttpClient` mặc định timeout 100 giây: operator nhìn màn hình đứng hình cả phút. Ngoài ra, nếu request đã tới server mà phản hồi rớt, Kiosk không biết scan đã ghi hay chưa.

**Khái niệm cốt lõi.**
- Timeout ngắn cho scan (ví dụ 3 giây; `HttpClient.Timeout`). Khi timeout: hiện "Result unknown - scan again". Nhờ idempotency (2.3) quét lại là an toàn: hoặc ghi mới, hoặc trả `Duplicate`.
- Thăm dò sức khỏe định kỳ (ví dụ mỗi 3 giây gọi `/health` của API, endpoint health check học ở Module 0). Cách này đáng tin hơn `NetworkChange.NetworkAvailabilityChanged`, vì sự kiện đó chỉ nói card mạng còn nối chứ không nói API còn sống.
- Khi offline: `ScanCommand.CanExecute` = false, ô nhập vẫn nhận ký tự nhưng bị bỏ (`IsReadOnly`), banner đỏ hiện. Không để `TextBox` bị `IsEnabled=false` vì nó sẽ mất focus và sau khi online phải lấy lại.
- Vì sao không lưu tạm cục bộ (store-and-forward): khi nối lại, trạng thái serial trên server có thể đã khác (ai đó đã scan trạm khác, serial bị Hold). Xử lý xung đột cần thiết kế riêng, quá phức tạp cho MVP. Xem mục 4.

```csharp
// ScanViewModel: thăm dò health mỗi 3 giây bằng DispatcherTimer (tick chạy trên UI thread)
_probe = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
_probe.Tick += async (_, _) =>
{
    var online = await _api.PingAsync();            // GET /health, timeout 1 giây, trả false nếu lỗi
    if (online == IsOffline)                        // trạng thái đổi
    {
        IsOffline = !online;                        // [NotifyCanExecuteChangedFor] cập nhật CanExecute của ScanCommand
        if (IsOffline) State = ScanState.Offline;   // banner đỏ "OFFLINE - scanning disabled"
        else           State = ScanState.Idle;
    }
};
_probe.Start();

// Trong ScanAsync: bắt lỗi mạng => xác định trạng thái không rõ
catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
{
    IsOffline = true;
    await ShowAsync(ScanState.Blocked, "NO RESPONSE", "Result unknown - scan again when online");
}
```

**Bẫy thường gặp.**
- Không đặt timeout: treo giao diện 100 giây.
- Ngắt kết nối giữa lúc quét, sau đó quét lại mà không hiểu: hệ thống báo `Duplicate`, operator tưởng lỗi. Message rõ: "Already recorded".
- Ping làm quá thường xuyên (mỗi 100 ms) với hàng chục Kiosk gây tải.
- Cập nhật giao diện từ luồng nền (ví dụ `Task.Run` + gán property gắn UI): dùng `Dispatcher`. `DispatcherTimer` như trên tránh phải nghĩ tới việc đó (Module 4 dạy sâu về Dispatcher khi làm SignalR).

### 2.10 Báo cáo FPY và WIP bằng SQL

**Nó là gì.** Hai truy vấn tổng hợp: FPY theo trạm cho một WO (chỉ lần scan đầu tiên của mỗi serial tại mỗi trạm) và WIP theo bước.

**Vấn đề nó giải quyết.** `WorkOrderOperation.QtyGood/QtyNg` đếm lượt, không đủ để tính FPY: một bo NG rồi retest OK làm `QtyGood` tăng, nhưng nó không phải "đạt lần đầu" (xem ví dụ 196/212 ở 1.2).

**Khái niệm cốt lõi.**
- Window function `ROW_NUMBER() OVER (PARTITION BY ... ORDER BY ...)` đánh số các lần scan của mỗi serial tại mỗi trạm; lấy `rn = 1` là lần đầu. (Window function được đào sâu ở Module 5.)
- Loại bản ghi của trạm ngoài routing (Repair có `Seq = 0`) khỏi FPY.
- WIP: đếm `Serial` theo `CurrentSeq` và `Status`, đọc trực tiếp từ bảng `Serial` (không phải log), cần index `Serial (WoId, Status) INCLUDE (CurrentSeq)`.

```sql
-- FPY theo trạm cho 1 WO
WITH FirstAttempt AS (
    SELECT StationId, SerialNo, Result,
           ROW_NUMBER() OVER (PARTITION BY SerialNo, StationId ORDER BY ScannedAt, Id) AS rn
    FROM   dbo.ScanLog
    WHERE  WoId = @WoId AND Seq > 0            -- Seq = 0: trạm ngoài routing (Repair)
)
SELECT StationId,
       COUNT(*)                                                        AS Entered,
       SUM(CASE WHEN Result = 'OK' THEN 1 ELSE 0 END)                  AS PassedFirstTime,
       CAST(100.0 * SUM(CASE WHEN Result = 'OK' THEN 1 ELSE 0 END)
            / COUNT(*) AS decimal(5,2))                                AS FpyPct
FROM   FirstAttempt
WHERE  rn = 1
GROUP  BY StationId;

-- WIP theo bước (Status lưu dạng chuỗi qua HasConversion<string>() để đọc được trong SQL)
SELECT CurrentSeq, Status, COUNT(*) AS Qty
FROM   dbo.Serial
WHERE  WoId = @WoId AND Status IN ('InProcess', 'Ng', 'Repair', 'Hold')
GROUP  BY CurrentSeq, Status
ORDER  BY CurrentSeq, Status;
```

**Bẫy thường gặp.**
- FPY tính từ `QtyGood/(QtyGood+QtyNg)` (mục 1.2): sai vì lẫn lượt retest.
- Quên loại bản ghi Repair check-in (`Result = OK`): FPY bị kéo lên.
- Quên lọc theo WO: trộn số liệu của lệnh khác.
- Báo cáo FPY chạy trên `ScanLog` hàng triệu dòng toàn hệ thống: bắt buộc lọc `WoId` (cần index `WoId`) hoặc theo khoảng thời gian.
- Định nghĩa "bo vào trạm lần đầu" bằng `CurrentSeq` thay vì `ScanLog`: các bo NG không tăng `CurrentSeq`, sẽ bị bỏ khỏi mẫu số.

### 2.11 Sơ đồ luồng dữ liệu của module

Luồng scan (dùng chung cho Kiosk và Simulator):

```
 WPF Kiosk (người scan)                    Machine Simulator (vai trạm test)
 ┌──────────────────────┐                  ┌───────────────────────────────┐
 │ ScanBox (keyboard    │                  │ lấy serial kế tiếp cần test   │
 │ wedge) + Enter       │                  │ chạy "test" giả, sinh OK/NG   │
 │ ScanViewModel        │                  └───────────────┬───────────────┘
 └──────────┬───────────┘                                  │
            │  ApiClient (Client.Core)                     │  HttpClient + Contracts
            └───────────────┬──────────────────────────────┘
                            ▼   POST /stations/{id}/scan  (JWT)   ScanRequest
                 ┌──────────────────────────┐
                 │ MiniMes.Api  (endpoint)  │  xác thực JWT, validate (FluentValidation)
                 └───────────┬──────────────┘
                             ▼
                 ┌──────────────────────────┐
                 │ Application: ScanCommand │  đọc Serial + WO + scan gần nhất (cache routing)
                 │  Handler (MediatR)       │
                 └───────────┬──────────────┘
                             ▼
                 ┌──────────────────────────┐
                 │ Domain: RouteCheck       │  hàm thuần: Allow / Duplicate / Block(+reason)
                 │  + Serial.ApplyScan      │  đổi CurrentSeq / Status
                 └───────────┬──────────────┘
          Block ─────────────┤ Allow
   (không ghi gì)            ▼
                 ┌─────────────────────────────────────────────┐
                 │ SQL Server, 1 transaction                   │
                 │  INSERT ScanLog (bigint, insert-only)       │
                 │  UPDATE Serial (rowversion)                 │
                 │  UPDATE WorkOrderOperation Qty = Qty + 1    │
                 └───────────┬─────────────────────────────────┘
                             ▼
              ScanResponse { result, message, reasonCode, nextStep, duplicate }
                             ▼
      Kiosk: đổi nền xanh/đỏ + âm thanh          Simulator: ghi log, chuyển serial kế tiếp

 Repair:  scan tại L1-REPAIR (Ng → Repair)  →  POST /stations/{id}/repair (RepairLog, Repair → InProcess)
 Báo cáo: GET /reports/fpy?woId=..   GET /reports/wip?woId=..   → BackOffice.Wpf (DataGrid)
 Ngoài phạm vi module này: MQTT (Module 4) và SignalR (Module 4). Kiosk chỉ dùng HTTP.
```

Vòng đời serial (state):

```
Created ──scan step1 OK──▶ InProcess(Cur=1) ──▶ ... ──▶ InProcess(Cur=N-1) ──scan step N OK──▶ Completed
                              │  ▲
                        scan NG│  │ Repair xong (Cur = returnToSeq - 1, mặc định giữ nguyên)
                              ▼  │
                             Ng ──scan tại Repair──▶ Repair ──complete──┘
                              │                        │
                              └──────── scrap ─────────┴──▶ Scrapped
          (Hold / Release ở Module 7: chặn ở mọi trạng thái, quay lại trạng thái trước đó)
```

---

## 3. Kế hoạch thực hiện

Tổng ước lượng khoảng 27–33 giờ (**[v2]** +3 giờ: bước 3b cổng FAI, SOP ở bước 7, dispatch list ở bước 6). Module này nặng, dành riêng khoảng 2,5 tuần. Nếu trễ, cắt các phần đánh dấu (tùy chọn) trước.

- [x] **Bước 1 — Mô hình dữ liệu và migration (2 giờ)**
  - Mục tiêu: có bảng để ghi vết scan và sửa chữa.
  - Việc cần làm: thêm entity `ScanLog` (bigint identity, `SerialNo` `varchar` `IsUnicode(false)`, `Seq`, `Result`, `DefectCode`, `OperatorId`, `ScannedAt` UTC, cột tùy chọn `Source`), `RepairLog`, `StationSession`; bổ sung nếu chưa có trên `Serial`: rowversion, unique index `SerialNo`, index `(WoId, Status) INCLUDE (CurrentSeq)`; lưu `Status` dạng chuỗi (`HasConversion<string>()`). Index `ScanLog (SerialNo, ScannedAt)` theo spec (và biến thể có INCLUDE ở bước 10). Index `ScanLog (WoId)` cho báo cáo.
  - File/class: `src/MiniMes.Domain/Execution/{ScanLog,RepairLog,StationSession}.cs`; `src/MiniMes.Infrastructure/Persistence/Configurations/*Configuration.cs`; migration `AddShopFloorExecution`.
  - Kiểm tra: `dotnet ef database update` chạy sạch; xem schema bằng SSMS thấy đúng khóa, index, kiểu `varchar`.
  - **Ghi chú khi làm (17/10/2026).** `Domain/Execution/{ScanLog,RepairLog,StationSession}.cs`, `Configurations/ExecutionConfigurations.cs`, migration `M3_ShopFloorExecution` (tên theo quy ước của dự án). **Lệch so với spec phải nói rõ:** routing của M1 đánh `Seq` 10, 20, 30, 40 (đúng gợi ý M1), không phải 1, 2, 3, 4 như ví dụ ở 1.2; vì vậy `CurrentSeq` = `Seq` của bước OK gần nhất (0 = chưa vào line) và **bước kế tiếp = bước có `Seq` nhỏ nhất lớn hơn `CurrentSeq`** (vẫn đúng với 1, 2, 3, 4); rework `returnToSeq` đặt `CurrentSeq` về `Seq` của bước liền trước (Bước 2, 4 làm theo quy ước này). `ScanLog`: `SerialNo` + `SerialId`, `WorkOrderId`, `StationId`, `Seq` (0 ở trạm Repair), `Result` (Ok/Ng), `DefectCode` (mã, snapshot), **`OperatorCode`** (mã nhân viên từ JWT, thay cho `OperatorId` vì `ICurrentUser` chỉ có mã), `Source` (Kiosk/Machine), `ScannedAt` UTC; không FK (log chỉ INSERT, mỗi FK là thêm một lần kiểm tra trên INSERT nóng nhất); không audit. `RepairLog`: `DefectCode`, `Action` (bắt buộc), `ComponentReplaced`, `ReturnToSeq`, `RepairedBy`, `RepairedAt`. `StationSession`: `StationId`, `WorkOrderId`, `OperatorCode`, `OpenedAt`, `ClosedAt`; **unique có filter `[ClosedAt] IS NULL`** giữ quy tắc "mỗi trạm tối đa một phiên mở" ngay cả khi hai kiosk đăng nhập cùng lúc. `Serial`: thêm `RowVersion`; index `(WorkOrderId, Status) INCLUDE (CurrentSeq)`. Cột chuỗi đều `varchar` (`IsUnicode(false)`). **Thêm:** trigger `TR_ScanLogs_BlockModify`, `TR_RepairLogs_BlockModify` chặn UPDATE/DELETE như `AuditLog` (ADR-004), để quy tắc "ScanLog insert-only" được DB giữ chứ không chỉ là quy ước. Kiểm tra: `dotnet ef database update` trên DB dev; `sqlcmd` xem `INFORMATION_SCHEMA`/`sys.indexes`: cột `varchar`, index `(SerialNo, ScannedAt)`, `INCLUDE (CurrentSeq)`, filter phiên mở, 2 trigger, `RowVersion`; unit `ExecutionEntityTests` 5 (NG cần mã lỗi, OK không có mã, Repair cần Action, phiên đóng một lần); integration `ExecutionSchemaTests` 5 (UPDATE/DELETE `ScanLogs` và DELETE `RepairLogs` → lỗi 51000, dữ liệu không đổi; phiên mở thứ hai cùng trạm → 409; `SerialNo` là `varchar` ở cả 3 bảng).

- [x] **Bước 2 — Domain service `RouteCheck` + bảng test (3 giờ)**
  - Mục tiêu: hàm quyết định hoàn chỉnh, được bao bởi test, chưa cần DB.
  - Việc cần làm: định nghĩa `ScanContext` (**[v2]** gồm `FaiStatus`, `FaiSerialNo` của WO), `RouteDecision` (**[v2]** có `AssignFaiSample`), `RouteVerdict`, `BlockReason` (**[v2]** thêm `FaiNotApproved`); viết `RouteCheck.Evaluate` theo thứ tự ở 1.4; viết `Serial.ApplyScan(...)` (OK tăng `CurrentSeq`, NG giữ nguyên và đặt `Ng`, bước cuối OK thành `Completed`); viết test bảng 19 case ở 2.1.
  - File: `src/MiniMes.Domain/Execution/{RouteCheck,ScanContext,RouteDecision}.cs`, `tests/MiniMes.UnitTests/Execution/RouteCheckTests.cs`.
  - Kiểm tra: `dotnet test` xanh; đổi thứ tự hai dòng trong `Evaluate` (đưa scan lặp xuống dưới) → case 5 phải đỏ (chứng minh test bắt được thứ tự).
  - **Ghi chú khi làm (17/10/2026).** `Domain/Execution/{ScanContext,RouteCheck}.cs` (`ScanContext`, `SerialState`, `StationInfo`, `RouteStep`, `LastScan`, `RouteVerdict`, `BlockReason`, `RouteDecision` có `Step` và **[v2]** `AssignFaiSample`), `Serial.ApplyScan(stepSeq, isLastStep, result)` (OK → `CurrentSeq = stepSeq`, bước cuối → `Completed`; NG → `Ng`, giữ `CurrentSeq`; chỉ nhận Created/InProcess và bước chưa qua). Khác snippet 2.1: (1) bước kế tiếp = `Seq` nhỏ nhất lớn hơn `CurrentSeq` (routing 10/20/30/40, xem Bước 1); (2) **một work center xuất hiện hai lần** trong routing (bẫy spec nêu) được xử lý ngay: trạm hợp lệ nếu một trong các bước của nó là bước kế tiếp; trạm có bước sau bước kế tiếp → `SkippedStep`, chỉ có bước trước → `AlreadyPassed`; (3) trạm sản xuất chưa gắn work center → `StationNotInRoute`; (4) message nêu tên bước ("Expected step 20 ASSY…"). Thứ tự kiểm tra đúng 1.4. Kiểm tra: `RouteCheckTests` 34 test: **19 case của bảng 2.1, mỗi case một dòng test** (routing seed 10 SMT, 20 ASSY, 30 TEST, 40 PACK) + serial không có, WO Released, trạm ngoài routing/chưa gắn work center, message bước, scan lặp thắng trạng thái Completed ở Pack, OK rồi NG trong 1 giây không phải lặp, đúng 5 giây vẫn là lặp, mẫu FAI bị QC đánh giá không đạt (`FaiSerialNo` đã bị xóa) vẫn vào Repair được, lý do FAI thắng lý do sai bước, work center dùng hai lần; 4 test `ApplyScan`. **Mutation thứ tự:** dời kiểm tra lặp xuống sau trạng thái, hoặc xuống ngay trước kiểm tra bước → `Duplicate_wins_over_completed_status` đỏ (case 5 của spec vẫn xanh vì lặp vẫn đứng trước kiểm tra bước; case cần để lộ thứ tự là lặp ở bước cuối, khi serial đã Completed); đưa cổng FAI lên trước NG/Repair → `Failed_fai_sample_can_go_to_repair` đỏ. Lần đầu mutation FAI **không** đỏ: test cũ dùng mẫu FAI vẫn còn là mẫu nên không phân biệt được thứ tự; sửa test theo tình huống thật (FAI không đạt xóa mẫu, bo vẫn NG).

- [x] **Bước 3 — Use case Scan + endpoint (4 giờ)**
  - Mục tiêu: `POST /stations/{id}/scan` chạy đúng nghiệp vụ và an toàn đồng thời.
  - Việc cần làm: `ScanCommand` + validator (NG cần DefectCode hợp lệ), `ScanContext` reader (đọc Serial + WO + scan gần nhất, cache routing/trạm bằng `IMemoryCache`), handler theo 2.2 (transaction, rowversion retry, `ExecuteUpdate`), tự chuyển WO `Released → InProgress` khi scan OK đầu tiên; `ScanRequest/ScanResponse` trong Contracts; endpoint; lấy operator từ JWT.
  - File: `src/MiniMes.Application/Execution/Scan/*`, `src/MiniMes.Contracts/Execution/ScanContracts.cs`, `src/MiniMes.Api/Controllers/StationsController.cs`, `tests/MiniMes.IntegrationTests/Execution/ScanTests.cs` (Testcontainers).
  - Kiểm tra: integration test chạy kịch bản: serial đi qua đủ 4 trạm → `Completed`; scan nhảy bước bị `Blocked`; scan lặp trả `Duplicate` và `ScanLog` chỉ một dòng; hai request cùng serial đồng thời (`Task.WhenAll`) chỉ tạo đúng một bản ghi; `QtyGood` bước 1 đúng bằng số scan OK.
  - **Ghi chú khi làm (17/10/2026).** `Application/Execution/{Scan,ScanContextReader}.cs`, `Contracts/Execution/ScanContracts.cs`, `POST /api/stations/{id}/scan` trong `StationsController` (Operator, Admin). Request `{ serialNo, result = "OK", defectCode?, source = "Kiosk" }`; response `{ result: Ok|Ng|Blocked, message, reasonCode, nextStep, nextStepName, duplicate }`, Blocked luôn 200. Luồng: chuẩn hóa serial (`Trim` + `ToUpperInvariant`, spec 1.5) → trạm active (404) → `DefectCode` tồn tại và active (400) → **trạm phải có phiên mở** (`NoOpenSession`, mã mới; API mở phiên ở Bước 5, test mở thẳng trong DB) → **check digit** của M2 (`SerialFormat.HasValidCheckDigit`, sai thì chặn `SerialNotFound` mà không hỏi bảng serial) → đọc context (`AsNoTracking`, theo khóa: serial + trạng thái/FAI của WO, routing đã chụp, scan gần nhất ở trạm) → `RouteCheck` → Block/Duplicate không ghi gì; Allow: **một transaction** gồm `Serial.ApplyScan` (hoặc `CheckInRepair` ở trạm Repair, `Seq` 0), **[v2]** gán mẫu FAI bằng `ExecuteUpdate ... WHERE FaiStatus='Pending' AND FaiSerialNo IS NULL` (0 dòng → chặn `FaiNotApproved`, rollback), `WorkOrder.Start()` ở scan sản xuất đầu tiên (nạp tracked để audit ghi `Released → InProgress`), dòng `ScanLog`, `SaveChanges` (kiểm `RowVersion` của serial), bộ đếm `QtyGood/QtyNg` bằng `ExecuteUpdate ... = x + 1` (hai nhánh `if`, như snippet gợi ý), và **`WorkOrder.TryAutoComplete`** của M2 khi OK ở bước cuối. Xung đột (`ConflictException` từ `AppDbContext`) → `ChangeTracker.Clear()` (thêm vào `IAppDbContext`) rồi đánh giá lại một lần; lần hai thấy scan trước và trả `Duplicate`. Repair chỉ nhận OK (check-in), NG ở trạm Repair → 400. Serilog: `Scan {SerialNo} at station {StationId}: {ScanResult} {ReasonCode} duplicate={Duplicate} in {ElapsedMs} ms`. **Chưa cache** trạm/routing bằng `IMemoryCache` như snippet: đo trước ở Bước 10 rồi mới tối ưu. Kiểm tra: integration `ScanTests` 18: một bo qua đủ 4 trạm → `Completed`, `ScanLog` 4 dòng (10/20/30/40), mỗi bước `QtyGood` 1, WO 1 cái tự `Completed`, audit `Released>InProgress` và `InProgress>Completed` bởi `EMP00123`; nhảy bước → `SkippedStep` không ghi gì, WO vẫn Released; bấm cò đúp → `duplicate`, 1 dòng log; **hai request cùng bo đồng thời** → cả hai OK, đúng một `duplicate`, 1 dòng log, `QtyGood` 1; hai bo đầu tiên đồng thời → WO bắt đầu một lần, `QtyGood` 2; 5 scan OK = `QtyGood` 5; NG ở Test → `Ng`, scan lại Test → `MustGoRepair`, Repair check-in → `Repair`, `QtyNg` 1; 5 lỗi 400 đúng ô; không có phiên → `NoOpenSession`; bo của WO khác → `WrongWo`; sai check digit và serial không tồn tại → `SerialNotFound`; chữ thường + khoảng trắng vẫn nhận; trạm lạ 404, Planner 403. Mutation: bỏ thử lại khi xung đột → test đồng thời đỏ 5/5 lần. API dev: scan → OK next ASSY, scan lại → duplicate, sai check digit → chặn; log Serilog có đủ trường (lần đầu 363 ms do API vừa khởi động, lần sau 20 ms).

- [ ] **Bước 3b — [v2] Cổng FAI trong use case Scan (1,5 giờ)**
  - Mục tiêu: WO có `FaiStatus = Pending` chỉ cho đúng 1 serial mẫu đi qua line, kể cả khi 2 trạm scan cùng lúc.
  - Việc cần làm: reader nạp `FaiStatus`, `FaiSerialNo` (có thể cache theo `WoId`, nhưng phải xóa cache khi M7 duyệt FAI: dùng key có version hoặc không cache 2 cột này); handler ghi `FaiSerialNo` bằng cập nhật có điều kiện như 2.2; map `FaiNotApproved` sang chuỗi `.resx` "Waiting for first article approval (QC)". Endpoint tạm `POST /work-orders/{id}/fai/result` (role QC) gọi `RecordFaiResult` để test trước khi có M7; M7 sẽ thay bằng use case có checklist.
  - File: `Application/Execution/Scan/*` (sửa), `tests/MiniMes.IntegrationTests/Execution/FaiGateTests.cs`.
  - Kiểm tra: release WO `RequireFai = true` → serial `…001` qua SMT OK → `…002` bị `FaiNotApproved` → `…001` đi hết Pack → gọi `fai/result` passed → `…002` scan SMT được. Test đồng thời: `Task.WhenAll` scan `…001` và `…002` ở hai trạm SMT khác nhau → đúng một cái được làm mẫu.

- [ ] **Bước 4 — Repair, Rework, Scrap (3 giờ)**
  - Mục tiêu: xử lý đầy đủ vòng đời NG.
  - Việc cần làm: scan tại trạm Repair chuyển `Ng → Repair` (check-in); `POST /stations/{id}/repair` nhận `serialNo, defectCode, action, componentReplaced, returnToSeq?` → ghi `RepairLog`, `Repair → InProcess`, `CurrentSeq = returnToSeq - 1` (mặc định giữ nguyên); (tùy chọn) `POST /serials/{no}/scrap` cho Supervisor kèm lý do. Đây là API bổ sung do tài liệu chi tiết hóa; spec chỉ định nghĩa `RepairLog` và scan.
  - File: `Application/Execution/Repair/*`, `Domain/Execution/Serial.cs` (thêm `CheckInRepair`, `CompleteRepair`, `Scrap`), test tương ứng.
  - Kiểm tra: kịch bản demo bước 5: `PCBA-000042` NG → Test bị chặn → Repair check-in → hoàn tất → Test OK → Pack → `Completed`. Test rework với `returnToSeq = 1`: SMT được phép, Test bị chặn `SkippedStep`.

- [ ] **Bước 5 — Phiên trạm (StationSession) (1,5 giờ)**
  - Mục tiêu: biết ai đang làm ở trạm nào với WO nào.
  - Việc cần làm: `POST /stations/{id}/sessions` (login bằng mã nhân viên đã có JWT → mở phiên với WO), `DELETE` để đóng; scan yêu cầu phiên mở; đóng phiên cũ của trạm khi mở phiên mới.
  - File: `Application/Execution/Sessions/*`, `Api/Controllers/StationsController.cs`.
  - Kiểm tra: scan khi chưa có phiên bị từ chối rõ ràng; mở phiên mới tự đóng phiên cũ.

- [ ] **Bước 5b — Endpoint serial đang chờ cho Machine Simulator (1 giờ)**
  - Mục tiêu: simulator vai trạm tự động (Module 4) biết serial nào đang chờ ở trạm để gửi kết quả.
  - Việc cần làm: `GET /stations/{id}/waiting-serials?take=1` trả serial của WO trong phiên đang mở, trạng thái `Created`/`InProcess`, có `CurrentSeq + 1` = bước của trạm, sắp theo `SerialNo` (FIFO). Dùng lại logic của báo cáo WIP ở bước 8. Simulator phải mở `StationSession` (bước 5) bằng user Operator riêng trước khi scan, giống người thật.
  - File: `Application/Execution/Wip/GetWaitingSerialsQuery.cs`, `Api/Controllers/StationsController.cs`.
  - Kiểm tra: release WO 5 serial → gọi endpoint ở trạm SMT trả `…001`; scan OK `…001` → gọi lại trả `…002`; ở trạm Test trả `…001`.

- [ ] **Bước 6 — Kiosk: đăng nhập, chọn trạm và WO (3,5 giờ)**
  - Mục tiêu: khung Kiosk chạy được đến màn hình scan.
  - Việc cần làm: dùng khung WPF của Module 0; màn hình chọn trạm/WO (**[v2]** danh sách WO lấy từ `GET /lines/{id}/dispatch-list` của M2, WO đầu danh sách chọn sẵn, hiện cột Priority và FAI); điều hướng sang `ScanView` (xem cách điều hướng ở Module 2); toàn màn hình (`WindowStyle=None`, `WindowState=Maximized`).
  - File: `src/MiniMes.Kiosk.Wpf/Views/{StationSelectView,ScanView}.xaml`, `ViewModels/*`.
  - Kiểm tra: đăng nhập bằng mã user seed, chọn `L1-ASM` + `WO-0001`, thấy màn hình READY.

- [ ] **Bước 7 — Kiosk: màn hình scan (6 giờ)**
  - Mục tiêu: scan tay end-to-end với phản hồi rõ ràng.
  - Việc cần làm: `ScanBox` luôn focus theo 2.7 (`UpdateSourceTrigger=PropertyChanged`, `Focusable=False` cho nút, `Activated`); `ScanViewModel` + `ScanCommand` (cho phép chạy đồng thời); nền xanh/đỏ và âm báo theo 2.8; map `ReasonCode` → chuỗi `.resx`; banner Offline + timeout theo 2.9; lỗi hiển thị bằng overlay, không dùng `MessageBox`; **[v2]** nút SOP + overlay `WebView2` theo 2.8b, endpoint `GET /sop/{**path}`; banner vàng "FAI pending" khi WO đang chờ duyệt FAI.
  - File: `Views/ScanView.xaml(.cs)`, `ViewModels/ScanViewModel.cs`, `Strings.resx` (khóa như `Scan_Ready`, `Block_SkippedStep`...), `Converters/*` nếu cần.
  - Kiểm tra: (1) dùng bàn phím giả lập scanner: gõ nhanh `PCBA-000001` + Enter → xanh; (2) scan lại ngay → xanh "Duplicate"; (3) scan bo nhảy bước → đỏ có lý do; (4) tắt API → trong 3–5 giây thấy banner Offline, scan bị chặn; bật lại → tự hết; (5) nếu có máy quét thật, thử thật; (6) **[v2]** bấm SOP → PDF hiện trong kiosk, đóng → scan tiếp được ngay không cần click; scan trong lúc SOP mở → overlay tự đóng, scan được xử lý.

- [ ] **Bước 8 — Kiosk: trạm Test và trạm Repair (3 giờ)**
  - Mục tiêu: nút NG + mã lỗi, biểu mẫu sửa chữa.
  - Việc cần làm: `TestView` có nút NG lớn → panel chọn `DefectCode` (nút to cho màn cảm ứng, lấy từ API) → gửi scan `Result = NG`; `RepairView`: sau khi scan bo (check-in) hiện form (Action, ComponentReplaced, returnToSeq tùy chọn) → gọi `repair`.
  - File: `Kiosk.Wpf/Views/{TestView,RepairView}.xaml`, ViewModels tương ứng.
  - Kiểm tra: kịch bản demo bước 5 chạy hoàn toàn bằng UI.

- [ ] **Bước 9 — Báo cáo FPY và WIP (3 giờ)**
  - Mục tiêu: hai báo cáo đúng với số tính tay.
  - Việc cần làm: hai query ở 2.10 qua Dapper hoặc EF `FromSql` (ghi lý do chọn); endpoint `GET /reports/fpy`, `GET /reports/wip`; màn hình `DataGrid` đơn giản trong BackOffice; thêm RTY và final yield nếu còn thời gian.
  - File: `Application/Execution/Reports/*`, `BackOffice.Wpf/Views/ExecutionReportView.xaml`.
  - Kiểm tra: dựng test với dữ liệu 10 serial (có 2 NG lần đầu, 1 NG hai lần) và so FPY tính tay; WIP cộng đúng bằng tổng bo đã bắt đầu và chưa kết thúc.

- [ ] **Bước 10 — Hiệu năng: seed, đo, tối ưu, ghi README (4 giờ)**
  - Mục tiêu: chứng minh scan < 100 ms (p95) trên 5–10 triệu dòng `ScanLog`.
  - Việc cần làm: script seed (2.6); đo route check trước/sau index; thử biến thể INCLUDE và phi chuẩn hóa `LastScan*` trên `Serial`; chạy benchmark 8 luồng; chụp execution plan trước/sau; (tùy chọn) so sánh với stored procedure.
  - File: `scripts/seed-scanlog.sql`, `tools/` hoặc test benchmark, `docs/perf.md`, mục Performance trong README.
  - Kiểm tra: bảng số liệu p50/p95/p99 trước/sau, cấu hình máy, có ảnh plan; p95 < 100 ms.

### Nếu bị kẹt

| Triệu chứng | Nghĩ tới | Cách kiểm tra |
| --- | --- | --- |
| Lệnh scan trong Kiosk luôn nhận chuỗi cũ/rỗng | `UpdateSourceTrigger` | Đặt `PropertyChanged`; đặt breakpoint ở setter `ScanInput` |
| Enter không kích hoạt lệnh | `KeyBinding` không có focus; hoặc có nút `IsDefault` chặn | Bỏ `IsDefault`; xác nhận `ScanBox` đang có focus (`Keyboard.FocusedElement`) |
| Sau khi bấm nút, scanner "không gõ được" | Nút cướp focus | `Focusable="False"` cho nút |
| Scan thứ hai bị bỏ qua khi scan nhanh | `AsyncRelayCommand` không cho chạy song song | `AllowConcurrentExecutions = true` (kiểm tra tài liệu phiên bản) |
| Có index mà vẫn Clustered Index Scan | Kiểu dữ liệu tham số (`nvarchar` vs `varchar`), thứ tự cột, ước lượng sai | Xem plan tìm `CONVERT_IMPLICIT`; `IsUnicode(false)` |
| Scan chậm lâu lâu (spike) | Lock/blocking, autogrowth file log DB, GC, cache miss | `sp_who2`/`sys.dm_tran_locks` khi chậm; xem Serilog thời gian handler; tăng kích thước file DB trước |
| `DbUpdateConcurrencyException` xuất hiện nhiều | Hai luồng cùng scan một serial (Simulator + Kiosk cùng chạy) | Xem log `SerialNo`, `StationId`; bảo đảm handler retry; kiểm tra Simulator không quét lại quá nhanh |
| `QtyGood` lệch số dòng `ScanLog` | Ghi ngoài transaction, hoặc dùng `++` thay vì cập nhật nguyên tử | So `COUNT` từ `ScanLog` với bộ đếm; xem lại transaction trong handler |
| FPY > 100% hoặc số kỳ lạ | Lẫn lượt retest hoặc bản ghi Repair | Kiểm tra `rn = 1` và `Seq > 0` |
| Test route check chập chờn (lúc xanh lúc đỏ) | Thời gian hiện tại chưa truyền vào | Không dùng `DateTime.UtcNow` trong Domain |
| Kiosk không hiện taskbar/vẫn hiện taskbar | Hành vi cửa sổ không viền khác nhau | Thử `Topmost`, hoặc Assigned Access của Windows |

---

## 4. Vì sao chọn giải pháp này

| Quyết định | Đã chọn | Phương án khác đã cân nhắc | Vì sao không chọn | Khi nào nên đổi |
| --- | --- | --- | --- | --- |
| Nơi đặt route check | Domain service thuần C# | Stored procedure; trigger DB; kiểm tra ở client | SP khó test đơn vị và version; trigger ẩn logic; client kiểm tra không tin được | Khi đo cho thấy round-trip tới DB là điểm nghẽn và có thể gom vào một SP (spec gợi ý để so sánh) |
| Nguồn sự thật trạng thái bo | `Serial.CurrentSeq/Status` (denormalized), `ScanLog` là nhật ký | Tính trạng thái từ `ScanLog` mỗi lần (event sourcing) | Đọc chậm dần theo kích thước log; phức tạp | Khi cần "tua lại" trạng thái tại thời điểm bất kỳ và có hạ tầng event store |
| Chống scan lặp | Cửa sổ thời gian 5 giây theo (serial, trạm, kết quả) + rowversion làm chốt | Idempotency key do client sinh; unique index (serial, trạm, giây) | Scanner bắn đúp không mang key; unique theo giây gây lỗi biên | Khi có nhiều client máy retry lâu (hàng phút) → thêm idempotency key |
| Concurrency trên serial | Optimistic (rowversion) + retry 1 lần | `UPDLOCK` (pessimistic); khóa ứng dụng | Khóa giữ lâu khi scan chậm; khó mở rộng nhiều instance API | Khi đụng độ rất thường xuyên (>5%) trên cùng serial |
| Bộ đếm sản lượng | `UPDATE ... = Qty + 1` nguyên tử trong transaction | Đọc-tăng-ghi; tính từ `ScanLog` theo chu kỳ | Đọc-tăng-ghi mất cập nhật; tính từ log chậm và trễ | Khi hot row nghẽn (nhiều trạm cùng một bước) → tính theo chu kỳ hoặc tách bộ đếm |
| Trả kết quả bị chặn | HTTP 200 + `result = Blocked` + `reasonCode` | 409/422 | Blocked là kết quả nghiệp vụ hằng ngày; client máy không nên coi là lỗi để retry | Nếu hệ thống bên thứ ba cần dựa vào mã HTTP để phân loại |
| API scan | Một endpoint cho Kiosk và Simulator | Endpoint riêng cho máy | Hai đường code dễ lệch; máy có thể qua mặt route check | Khi máy thật cần giao thức khác (ví dụ OPC UA gateway) → thêm adapter phía ngoài vẫn gọi cùng use case |
| **[v2]** Cổng FAI | Quy tắc trong `RouteCheck` + gán mẫu bằng cập nhật có điều kiện | Kiểm tra FAI ở màn hình chọn WO (không cho mở phiên); khóa ứng dụng khi gán mẫu | Kiểm tra ở UI thì Simulator/API trực tiếp qua mặt được; khóa ứng dụng không chạy được khi có nhiều instance API | Khi FAI theo ca/đổi model: thêm nguồn "cần FAI" khác nhưng vẫn giữ quy tắc trong `RouteCheck` |
| **[v2]** Hiển thị SOP | `WebView2` overlay trong kiosk, file phục vụ qua API | Mở trình đọc PDF ngoài; nhúng ảnh tĩnh | Trình ngoài phá chế độ kiosk và focus; ảnh tĩnh không xem được PDF nhiều trang | Khi có hệ thống quản lý tài liệu (DMS): lấy URL từ DMS, kiosk vẫn hiển thị như cũ |
| Mất kết nối ở Kiosk | Chặn scan, banner Offline | Store-and-forward cục bộ | Xung đột trạng thái khi nối lại; phức tạp | Khi nhà máy mạng yếu thường xuyên và chấp nhận thiết kế phối hợp xung đột (kèm hàng đợi bền) |
| NG và `CurrentSeq` | NG không tăng `CurrentSeq`; Repair trả về bước NG | NG tăng `CurrentSeq` + cờ riêng | Cần thêm cờ, quy tắc "bước kế tiếp" phức tạp hơn | Khi routing có nhánh sửa chữa là một công đoạn chính thức trong routing |
| Index `ScanLog` | `(SerialNo, ScannedAt DESC) INCLUDE (StationId, Result)` | Chỉ `(SerialNo, ScannedAt)`; phi chuẩn hóa `LastScan*` trên `Serial` | Không INCLUDE thì phải Key Lookup; phi chuẩn hóa thêm dữ liệu cần đồng bộ | Khi số đo cho thấy phi chuẩn hóa nhanh hơn đáng kể và chấp nhận chi phí duy trì |

**Ba quyết định bạn nên nói lại được trong phỏng vấn.**

Thứ nhất, route check là hàm thuần trong domain. Câu trả lời cho "làm sao chặn công nhân bỏ qua công đoạn": mọi lần scan, phần mềm so bước hiện tại của serial với bước của trạm; chỉ khi khớp mới ghi nhận. Logic nằm trong một hàm không phụ thuộc DB nên test được bằng bảng case (nhảy bước, scan lại, NG, sau Repair, Hold, sai WO), và có thể đổi sang stored procedure mà không sửa client. Thứ tự kiểm tra là một phần thiết kế: scan lặp kiểm tra trước step để không biến scan bắn đúp thành lỗi.

Thứ hai, một API cho cả người và máy, kèm idempotency. Nguồn dữ liệu chất lượng duy nhất là kết quả theo serial nên mọi nguồn phải đi qua cùng poka-yoke. Vì máy retry sau timeout và scanner bắn đúp, API idempotent bằng cửa sổ thời gian, rowversion làm chốt chặn cuối khi hai request đến đồng thời. Kết quả "bị chặn" trả 200 vì đó là kết quả nghiệp vụ, không phải lỗi.

Thứ ba, "scan chậm thì kiểm tra gì?": đo trước, không đoán. Kiểm tra p95 chứ không phải trung bình; xem execution plan của truy vấn scan gần nhất (Index Seek hay Scan, có CONVERT_IMPLICIT do `nvarchar` vs `varchar` không); đếm round-trip DB; xem blocking/lock và độ dài transaction; xem kích thước log DB và autogrowth; cuối cùng mới tới mạng và client. Trong dự án bạn có số đo trước/sau trên 5–10 triệu dòng để minh họa.

---

## 5. Hoàn thành module

### 5.1 Definition of Done

- [ ] Kịch bản demo (spec v2, bước 5 và 7) chạy đúng: serial FAI đi hết line, QC duyệt; operator scan tay một serial trên Kiosk; trạm Test (Simulator hoặc UI) báo NG một bo → Repair → quay lại Test → OK → Pack → `Completed`.
- [ ] Không thể scan nhảy bước bằng bất kỳ đường nào (UI, API trực tiếp, Simulator); có test chứng minh.
- [ ] `RouteCheck` có ít nhất 12 case (dự án này: 19, gồm 4 case FAI) và pass; thứ tự kiểm tra được bảo vệ bằng test.
- [ ] **[v2]** Cổng FAI: WO `RequireFai` chỉ cho 1 serial mẫu đi qua đến khi QC duyệt; hai trạm scan đồng thời không tạo hai mẫu (integration test).
- [ ] **[v2]** Kiosk chọn WO từ dispatch list (M2); nút SOP mở đúng tài liệu của bước, đóng xong scan tiếp được ngay.
- [ ] `POST /stations/{id}/scan` trả `{ result, message, nextStep }` (cùng `reasonCode`, `duplicate`) và Simulator vai trạm test gọi được đúng endpoint đó.
- [ ] Scan lặp không ghi trùng; hai request đồng thời cùng serial chỉ ghi một bản ghi (integration test).
- [ ] `ScanLog` ghi cùng transaction với thay đổi `Serial` và bộ đếm `QtyGood/QtyNg`.
- [ ] Kiosk: ô scan luôn giữ focus, nền xanh/đỏ + âm báo + chữ to, toàn màn hình, banner Offline chặn scan khi API mất, chuỗi UI trong `.resx`.
- [ ] Repair ghi `RepairLog` (Action, ComponentReplaced) và đưa bo về đúng bước; rework với `returnToSeq` hoạt động.
- [ ] Báo cáo FPY theo trạm và WIP theo bước đúng với số tính tay (test có dữ liệu mẫu).
- [ ] `docs/perf.md` (và mục trong README) có số đo p50/p95/p99 trước/sau index trên 5–10 triệu dòng, có cấu hình máy; p95 < 100 ms.
- [ ] Serilog ghi `SerialNo`, `StationId`, kết quả route check và thời gian xử lý cho mỗi scan.

### 5.2 Tự kiểm tra

1. Vì sao scan lặp phải kiểm tra trước bước và trạng thái?
<details><summary>Đáp án gợi ý</summary>
Sau lần scan đầu, `CurrentSeq` đã tăng (hoặc bo đã `Completed` ở Pack). Nếu kiểm tra step/status trước, lần scan lặp bị báo `AlreadyPassed`/`SerialCompleted` thay vì "Duplicate OK". Operator thấy đỏ dù lần đầu đã thành công.
</details>

2. Bo `PCBA-000003` có `CurrentSeq = 1` được scan ở Test. Kết quả và mã lý do là gì? Nếu scan ở Assembly thì sao?
<details><summary>Đáp án gợi ý</summary>
Ở Test (Seq 3): bước kế tiếp phải là 2 nên Blocked `SkippedStep`, "expected step 2". Ở Assembly (Seq 2): Allow, `CurrentSeq = 2`.
</details>

3. Khác nhau giữa NG, Repair, Rework, Scrap là gì? Trạng thái và `CurrentSeq` của serial thay đổi thế nào trong mỗi trường hợp?
<details><summary>Đáp án gợi ý</summary>
NG là sự kiện (`Status = Ng`, `CurrentSeq` giữ nguyên). Repair là sửa tại chỗ rồi quay lại bước NG (`Ng → Repair → InProcess`, `CurrentSeq` giữ nguyên). Rework là chạy lại nhiều bước (`CurrentSeq = returnToSeq - 1`). Scrap là kết thúc (`Scrapped`), không đi tiếp.
</details>

4. 200 bo vào Test lần đầu, 184 OK ngay, 16 NG; 12 sửa xong retest OK, 4 scrap. Tính FPY, final yield. Vì sao `QtyGood/(QtyGood+QtyNg)` cho số khác?
<details><summary>Đáp án gợi ý</summary>
FPY = 184/200 = 92%. Final yield = 196/(196+4) = 98%. `QtyGood = 196`, `QtyNg = 16` → 196/212 = 92,5%: mẫu số đếm cả 12 lượt retest, và tính theo lượt thay vì theo serial lần đầu.
</details>

5. Nếu FPY từng trạm là 98%, 99%, 92%, 99,5%, RTY bằng bao nhiêu, nói lên điều gì?
<details><summary>Đáp án gợi ý</summary>
0,98 × 0,99 × 0,92 × 0,995 ≈ 0,888. Khoảng 11% bo phải sửa ít nhất một lần dù mỗi trạm nhìn có vẻ tốt. Nhà máy ẩn (hidden factory) của sửa chữa.
</details>

6. Vì sao cập nhật `QtyGood` bằng `UPDATE ... SET QtyGood = QtyGood + 1` thay vì đọc rồi ghi? Hạn chế của `ExecuteUpdate` là gì?
<details><summary>Đáp án gợi ý</summary>
Đọc-tăng-ghi mất cập nhật khi hai scan đồng thời. Cập nhật nguyên tử được DB tuần tự hóa bằng khóa dòng. `ExecuteUpdate` bỏ qua change tracker và interceptor `SaveChanges` (audit Module 0 không thấy), và đây là hot row nếu nhiều trạm cùng bước.
</details>

7. Hai request scan giống hệt đến cùng lúc: điều gì ngăn ghi hai bản ghi?
<details><summary>Đáp án gợi ý</summary>
Rowversion trên `Serial`: một transaction thắng, transaction kia nhận `DbUpdateConcurrencyException`. Handler đọc lại, thấy scan gần nhất vừa ghi và trả `Duplicate`.
</details>

8. Ở p50 scan mất 20 ms nhưng p99 mất 900 ms. Bạn kiểm tra theo thứ tự nào?
<details><summary>Đáp án gợi ý</summary>
Tìm khác biệt của các request chậm: có trùng thời điểm với autogrowth/checkpoint không (log DB), có blocking/lock không (`sys.dm_tran_locks`), cache miss ở routing, warm-up/JIT, GC; xem execution plan (parameter sniffing, `CONVERT_IMPLICIT`); chỉ sau đó tới mạng và client.
</details>

9. Vì sao `TextBox` scan trong Kiosk cần `UpdateSourceTrigger=PropertyChanged` và nút bấm cần `Focusable="False"`?
<details><summary>Đáp án gợi ý</summary>
Mặc định `TextBox.Text` chỉ đẩy giá trị về ViewModel khi mất focus, nên khi Enter đến ViewModel còn chuỗi cũ. Nút cảm ứng nếu focusable sẽ cướp focus khỏi ô scan, các lần quét sau đi vào hư không.
</details>

10. Mạng rớt đúng lúc Kiosk gửi scan (timeout 3 giây). Operator quét lại. Điều gì xảy ra ở server và vì sao không sai dữ liệu?
<details><summary>Đáp án gợi ý</summary>
Nếu request lần đầu chưa tới server: lần quét lại được xử lý bình thường. Nếu đã tới và đã ghi: quét lại nằm trong cửa sổ 5 giây, server trả `Duplicate` (OK, không ghi). Nếu quá cửa sổ: rơi vào `AlreadyPassed`, message cần rõ để supervisor hiểu. At-least-once + idempotent.
</details>

11. **[v2]** Vì sao quy tắc FAI nằm sau Ng/Repair nhưng trước kiểm tra bước?
<details><summary>Đáp án gợi ý</summary>
Trạm Repair phải nhận được mẫu FAI bị NG, nên Ng/Repair xử lý trước. Đặt FAI trước step để lý do chặn phản ánh đúng nguyên nhân: line chưa được phép chạy hàng loạt, chứ không phải serial sai bước.
</details>

12. **[v2]** Hai trạm SMT cùng scan hai serial khác nhau đúng lúc WO vừa Release (FAI Pending). Điều gì ngăn cả hai thành mẫu FAI?
<details><summary>Đáp án gợi ý</summary>
Cập nhật có điều kiện `UPDATE WorkOrder SET FaiSerialNo = @s WHERE Id = @id AND FaiStatus = 'Pending' AND FaiSerialNo IS NULL` trong transaction của scan. SQL Server tuần tự hóa hai câu UPDATE trên cùng dòng; câu thứ hai thấy `FaiSerialNo` đã có giá trị, ảnh hưởng 0 dòng, handler rollback và trả `FaiNotApproved`.
</details>

### 5.3 Câu hỏi phỏng vấn liên quan

1. **"Làm sao chặn công nhân bỏ qua công đoạn?"**
   Gợi ý: route check khi scan (bước hiện tại của serial so với bước của trạm), không có nút bỏ qua ở Kiosk; ngoại lệ do Supervisor xử lý ngoài luồng có audit. Nêu các quy tắc chính (nhảy bước, đã qua, Hold, NG chỉ vào Repair) và test bảng. Nói thêm poka-yoke ngăn ngừa vs phát hiện.
2. **"Scan chậm thì kiểm tra gì?"**
   Gợi ý: đo p95/p99; execution plan và index của truy vấn scan gần nhất; kiểu dữ liệu tham số; số round-trip; blocking và thời gian transaction; autogrowth; cache routing; sau cùng mạng. Kể số đo trước/sau của bạn.
3. **"FPY khác final yield thế nào? Vì sao nhà máy quan tâm FPY?"**
   Gợi ý: FPY tính theo serial lần đầu, final yield tính bo cuối cùng đạt; FPY thấp nghĩa là nhiều công sửa và WIP tắc ở Repair dù final yield tốt; nêu ví dụ 92%/98%, RTY.
4. **"Máy test tự động và người cùng gọi một API; điều đó ảnh hưởng thiết kế thế nào?"**
   Gợi ý: idempotent theo retry của máy; blocked trả 200 kèm mã; danh tính tài khoản dịch vụ; kiểm thử song song; một đường duy nhất qua poka-yoke.
5. **"Mất mạng giữa trạm và MES thì sao?"**
   Gợi ý: MVP chặn scan và hiển thị offline; timeout ngắn + idempotency giải quyết kết quả không rõ; nêu store-and-forward là phương án khác cùng chi phí xung đột.
6. **"Hai người cùng scan một bo cùng lúc thì sao?"**
   Gợi ý: rowversion + retry; kết quả cuối vẫn nhất quán; hoặc `UPDLOCK` nếu chọn pessimistic và vì sao không chọn.
7. **"Bạn tính WIP thế nào và nó cho bạn biết điều gì?"**
   Gợi ý: đếm serial đã bắt đầu, chưa kết thúc theo bước; chỗ dồn WIP là nút cổ chai; định luật Little liên hệ WIP, throughput, lead time.
8. **[v2] "Đổi model mới, làm sao chắc line setup đúng trước khi chạy cả lô?"**
   Gợi ý: FAI. Hệ thống chỉ cho 1 serial mẫu đi hết line; QC kiểm tra và duyệt thì route check mới mở cho cả lô. Chặn nằm trong route check nên Simulator hay API trực tiếp cũng không qua mặt được. Kể thêm chỗ xử lý tranh chấp khi hai trạm cùng chọn mẫu.
9. **[v2] "Operator xem hướng dẫn công việc ở đâu?"**
   Gợi ý: SOP gắn theo version routing, được snapshot vào WO, kiosk mở trong overlay WebView2 nên luôn đúng version, không phá chế độ kiosk.

### 5.4 Tài liệu đọc thêm

Chỉ liệt kê tên trang chính thức; tìm theo tên trên trang chủ tương ứng (không cung cấp đường dẫn sâu để tránh sai):

- learn.microsoft.com, EF Core: "ExecuteUpdate and ExecuteDelete", "Handling Concurrency Conflicts", "Efficient Querying".
- learn.microsoft.com, ASP.NET Core: "Health checks in ASP.NET Core".
- **[v2]** learn.microsoft.com: "Get started with WebView2 in WPF apps".
- learn.microsoft.com, WPF: "Focus Overview", "Input Overview", "Data binding overview" (`UpdateSourceTrigger`), "Styles and templates" (trigger).
- learn.microsoft.com, SQL Server: "Display an Actual Execution Plan", "Create indexes with included columns", "SET STATISTICS IO/TIME".
- Tài liệu CommunityToolkit.Mvvm (Microsoft Learn: "MVVM Toolkit"), mục "RelayCommand attribute" và "AsyncRelayCommand".
- Đọc về poka-yoke (Shigeo Shingo) và First Pass Yield / Rolled Throughput Yield trong tài liệu Lean/Six Sigma (sách hoặc bài viết của tổ chức chuyên ngành như ASQ, asq.org).
- Tài liệu hướng dẫn cấu hình máy quét mã vạch của hãng bạn dùng: mục "keyboard wedge", "suffix Enter", "keyboard layout".
