# Module 2 — Production Planning (Work Order)

> **Module này làm gì**: biến "kế hoạch" thành **lệnh sản xuất (Work Order, WO)** cụ thể cho một line, kiểm soát vòng đời của lệnh bằng state machine, và khi **release** thì chụp lại (snapshot) BOM/Routing, sinh **serial number** không trùng cho từng sản phẩm, rồi in nhãn (ZPL, Code128/DataMatrix).
> **Sau khi xong bạn có**: API + màn hình WPF Back-office để tạo, release, hold, complete WO; bảng `Serial` với 200 serial duy nhất cho WO 200 pcs; chuỗi ZPL xem trước qua Labelary; test chứng minh 2 request release đồng thời không sinh trùng serial.
> **Thời lượng ước tính**: ~27 giờ (khoảng 15–17 buổi, 1,5–2 giờ/buổi; 26 giờ của bản cũ + 1 giờ cho phần **[v2]**, Gantt đã bỏ). Nếu thiếu thời gian, cắt phần WPF trước (Bước 8) và làm sau khi M3 chạy.
>
> **Cập nhật theo [spec-v2](spec-v2.md):** (1) **bỏ Gantt**; điều độ (dispatch) bằng `WorkOrder.Priority` + danh sách WO theo line (`GET /lines/{id}/dispatch-list`) mà kiosk M3 dùng; (2) thêm **`WorkOrder.RequireFai` / `FaiStatus` / `FaiSerialNo`**: cổng FAI do M3 kiểm tra và M7 duyệt; (3) snapshot thêm `SopUrl` vào `WorkOrderOperation`. Các chỗ đổi được đánh dấu **[v2]**.
> **Module tiên quyết**: M0 (nền tảng, audit interceptor, Testcontainers), M1 (Item, BOM, Routing có version + Approved, API explode BOM).

---

## 1. Kiến thức nghiệp vụ MES

### 1.1 Bức tranh tổng thể

M1 trả lời "sản xuất cái gì, bằng gì, qua đâu". M2 trả lời câu hỏi kế tiếp: **"làm bao nhiêu, ở line nào, khi nào, và ai cho phép bắt đầu?"**

```
Nhu cầu (đơn hàng khách, dự báo)
   │   [ngoài MVP: ERP / họp S&OP]
   ▼
Kế hoạch sản xuất (Production Plan)   "Tuần 40: 10.000 PCB-A"      (thô, theo tuần/tháng)
   │   Planner chia nhỏ theo line, ca, năng lực
   ▼
Work Order (WO)                       "200 PCB-A, Line 1, 01/10"   <-- MODULE 2 (chi tiết, thực thi được)
   │   Draft ──► Release
   ▼
Line chạy: scan serial (M3), máy đếm (M4), nạp lot (M6)
   ▼
Completed ──► Closed                  báo cáo sản lượng / FPY / OEE theo WO
```

Ai dùng:

| Vai trò | Làm gì với WO |
| --- | --- |
| Planner | Tạo WO, xem preview NVL/công đoạn, release, cancel WO chưa chạy |
| Supervisor | Hold/Resume WO khi có sự cố, Complete thủ công (kèm lý do), Close |
| Operator | Không thao tác WO, nhưng cầm nhãn serial do WO sinh ra và scan ở trạm |
| QC | Xem WO để biết serial nào thuộc lệnh nào (dùng nhiều ở M6, M7) |

Vì sao nhà máy cần WO: nếu không có lệnh, line chạy "tự do", không ai trả lời được các câu hỏi sống còn: con bo này làm theo BOM phiên bản nào? Lệnh này đã xong bao nhiêu? Vì sao dùng hết 5.000 con IC trong khi kế hoạch cần 4.800? Khách hỏi "lô 200 cái giao tuần trước" thì lô đó là những serial nào? WO là **đơn vị quản lý** gom toàn bộ câu trả lời đó vào một chỗ.

Ghi chú cho người từng làm SAP: WO ở đây tương đương **production order** của SAP (製造指図). Trạng thái SAP như CRTD (created), REL (released), TECO (technically completed), CLSD (closed) tương ứng gần đúng Draft, Released, Completed, Closed của module này. Chỉ để dễ hình dung; đừng sao chép nguyên cơ chế status của SAP.

### 1.2 Khái niệm & thuật ngữ

#### a) Kế hoạch sản xuất vs lệnh sản xuất

Đây là hai thứ hay bị nhầm vì đều "nói về sản xuất bao nhiêu".

| Tiêu chí | Kế hoạch sản xuất (Production Plan) | Lệnh sản xuất (Work Order) |
| --- | --- | --- |
| Câu hỏi | "Cần làm gì trong tuần/tháng tới?" | "Line 1 làm 200 PCB-A vào ca sáng 01/10" |
| Độ chi tiết | Thô: mã hàng × kỳ (tuần, tháng) × số lượng | Cụ thể: 1 mã hàng, 1 line, 1 khoảng thời gian, 1 số lượng |
| Tính ràng buộc | Mềm, sửa thường xuyên (đơn hàng đổi, thiếu vật tư) | Cứng sau khi release: kỹ thuật, kho, line đều làm theo |
| Có BOM/Routing gắn chặt không | Không (chỉ ước lượng năng lực) | Có, và bị đóng băng (snapshot) khi release |
| Có định danh để truy xuất không | Không | Có: `WoNo`, mọi serial/lot gắn vào WoNo |
| Nguồn ngoài đời | ERP (SAP: kế hoạch tổng, MRP) | Planner phát hành từ kế hoạch |

Ví dụ số: kế hoạch tuần 40 là 10.000 PCB-A. Planner nhìn năng lực: mỗi line làm ~1.000 con/ca, 2 line, 2 ca/ngày. Ông ta chia thành 50 WO × 200 con, gắn vào Line 1/Line 2, ca sáng/ca chiều. Mỗi WO là một đơn vị nhỏ để theo dõi, đổi ưu tiên, hold hoặc hủy mà không làm hỏng cả kế hoạch tuần.

Trong dự án này **không có ERP**: Planner tạo WO trực tiếp trong MES, không có bảng "Production Plan". Cột `ExternalRef` của `WorkOrder` **để trống** (nullable), chỉ để dành chỗ cho M8 (khi ERP gửi lệnh xuống thì lưu số production order của ERP vào đây). Đừng viết logic nào dùng `ExternalRef` ở M2.

#### b) Work Order (WO)

Một lệnh sản xuất **một mã hàng** (thành phẩm) với **số lượng kế hoạch** (`PlannedQty`) trên **một line** trong khoảng `PlannedStart`–`PlannedEnd`. WO có mã `WoNo` duy nhất (ví dụ `WO-2610-00001`).

WO mang ba lớp thông tin:
1. **Đầu bài**: Item, PlannedQty, Line, thời gian kế hoạch.
2. **Công thức đã chốt**: `WorkOrderMaterial` (cần bao nhiêu NVL) và `WorkOrderOperation` (đi qua công đoạn nào, cycle time bao nhiêu). Chép từ BOM/Routing khi release.
3. **Kết quả**: sản lượng tốt/NG từng bước (`QtyGood`, `QtyNg`), NVL đã cấp/đã dùng, trạng thái từng serial.

#### c) Vòng đời WO và ý nghĩa từng trạng thái ngoài đời

```
Draft → Released → InProgress → Completed → Closed
  │        │            │
  └────────┴────→ Cancelled (chỉ khi chưa có sản lượng)
InProgress ⇄ OnHold
```

| Trạng thái | Ngoài đời nghĩa là gì | Ai chuyển | Cho phép | Cấm |
| --- | --- | --- | --- | --- |
| **Draft** (nháp) | Planner đang soạn, chưa cam kết. Giống bản nháp email | Planner tạo | Sửa mọi trường (qty, line, ngày), xem preview NVL | Scan, in nhãn, nạp vật tư |
| **Released** (đã phát lệnh) | Lệnh chính thức gửi xuống line. Kỹ thuật đã duyệt BOM/Routing, serial đã sinh, nhãn sẵn sàng in. Chưa ai làm gì | Planner (`Release()`) | Kho cấp vật tư, in nhãn, operator scan bước đầu | Sửa qty, item, line (muốn đổi phải cancel rồi tạo WO mới) |
| **InProgress** (đang chạy) | Đã có ít nhất một sản phẩm được scan vào bước đầu. Line đang sản xuất | Hệ thống tự chuyển khi có scan đầu tiên (`Start()`, hook từ M3) | Scan mọi bước, nạp lot, Andon | Cancel nếu đã có sản lượng |
| **OnHold** (tạm dừng) | Supervisor tạm dừng cả lệnh: thiếu vật tư, khách đổi ý, nghi lỗi chất lượng, chờ kỹ thuật. Line không làm lệnh này, nhưng lệnh **không** bị hủy | Supervisor | Xem, ghi lý do, Resume | Scan (route check M3 chặn), in nhãn mới |
| **Completed** (hoàn thành sản xuất) | Đủ sản lượng: `QtyGood` bước cuối ≥ `PlannedQty`, hoặc Supervisor đóng thủ công có lý do (ví dụ hết vật tư, khách chấp nhận thiếu) | Hệ thống hoặc Supervisor | Xem báo cáo, đối chiếu vật tư còn thừa, xử lý serial NG còn tồn | Scan sản xuất thêm |
| **Closed** (khóa sổ) | Đã đối chiếu xong (sản lượng, vật tư thừa/thiếu, phế phẩm). Dữ liệu chốt để tính giá thành, báo cáo tháng, gửi ERP | Supervisor | Chỉ đọc | Mọi thay đổi |
| **Cancelled** (hủy) | Lệnh không bao giờ chạy hoặc bị hủy khi chưa có sản lượng. Giữ lại để truy vết "lệnh này từng có và bị hủy vì sao" | Planner/Supervisor | Chỉ đọc | Khôi phục (tạo WO mới nếu cần) |

Hai điểm cần nhớ:
- **Completed khác Closed.** Completed là "line xong việc", Closed là "sổ sách xong việc". Khoảng cách giữa hai trạng thái là nơi nhà máy đối chiếu vật tư thừa, phế phẩm, serial NG chưa xử lý. Nếu gộp một trạng thái, bạn mất cơ hội chặn "sản xuất thêm sau khi báo hoàn thành".
- **OnHold không phải Cancelled.** Hold có thể quay lại (Resume), mọi serial và snapshot giữ nguyên. Cancel là kết thúc.

#### d) Release là gì và vì sao line chỉ chạy khi WO đã release

**Release (phát lệnh)** là hành động chính thức của Planner: "lệnh này đủ điều kiện, line được phép làm". Nó là một **cổng kiểm soát (gate)**, không chỉ đổi cờ trạng thái. Khi release, hệ thống:

1. Kiểm tra điều kiện: Item còn hoạt động, có BOM và Routing ở trạng thái **Approved** đang có hiệu lực, line hợp lệ, `PlannedQty > 0`.
2. Chụp snapshot BOM/Routing vào `WorkOrderMaterial`/`WorkOrderOperation` và ghi `BomVersion`, `RoutingVersion`.
3. Sinh `Serial` cho từng sản phẩm (nếu Item `IsSerialized`).
4. Đổi trạng thái sang Released và ghi audit (ai release, lúc nào).

Vì sao line **chỉ chạy khi đã release**:
- **Chặn sản xuất không có phép**: không có WO release thì không có serial hợp lệ để scan, route check của M3 từ chối. Ngăn tình huống "leader thấy rảnh nên tự chạy thêm 500 con" rồi không ai biết dùng BOM nào.
- **Đảm bảo công thức đã được duyệt**: release buộc BOM/Routing phải Approved. Bản Draft chưa được kỹ thuật ký thì không được lên line.
- **Đảm bảo truy xuất được**: mọi thứ sản xuất ra đều thuộc một WO có snapshot cố định. Khách hỏi sau 2 năm thì vẫn dựng lại được "con bo này làm theo BOM v3, routing v2".
- **Tách quyền**: người soạn (Planner) và người thực thi (Operator) là hai vai. Release là điểm bàn giao trách nhiệm.

Ngoài đời, release thường còn kiểm tra vật tư đã sẵn sàng (kitting đủ chưa). MVP không kiểm tồn kho (đó là M9), nên chỉ kiểm tra master data.

#### e) Dispatch

**Dispatch** là bước xếp lệnh vào line theo thứ tự và thời điểm cụ thể ("Line 1 làm WO-A trước 8h–12h, rồi WO-B"). Ở dự án này, dispatch được thể hiện đơn giản bằng `LineId` + `PlannedStart/PlannedEnd` + **[v2] `Priority`**. Không tạo thuật toán xếp lịch tự động: đó là chủ đề APS (Advanced Planning and Scheduling), nằm ngoài phạm vi. **[v2] Bỏ Gantt** (spec v2, mục A2 #5): Gantt chỉ là cách vẽ lại cùng dữ liệu, tốn 3 giờ mà không thêm nghiệp vụ nào để nói khi phỏng vấn.

**[v2] Danh sách điều độ (dispatch list).** Thứ operator thật sự cần không phải biểu đồ, mà là câu trả lời "line của tôi làm WO nào tiếp theo". API `GET /lines/{id}/dispatch-list` trả các WO `Released`/`InProgress`/`OnHold` của line, sắp theo `Priority` (1 = gấp nhất) rồi `PlannedStart`. Kiosk M3 hiển thị danh sách này khi operator chọn WO, WO đầu danh sách được chọn sẵn.

- `Priority` là số nguyên 1–9, mặc định 5. Planner/Supervisor được đổi `Priority` ở mọi trạng thái chưa kết thúc (Draft → OnHold), khác với `PlannedQty`/`LineId` bị khóa sau Release (R6): đổi thứ tự ưu tiên không làm sai snapshot hay serial.
- Ví dụ thực tế: khách gọi lúc 9h xin giao gấp 50 `PCB-B`. Supervisor đổi WO `PCB-B` sang `Priority = 1`; kiosk của Line 1 đưa WO đó lên đầu. Line có thể chạy song song nhiều WO (MVP không cấm), nhưng danh sách cho operator biết nên làm cái nào trước.

#### f) Snapshot BOM/Routing

Master data (M1) có thể đổi: kỹ sư thay tụ 10uF bằng loại khác, thêm một bước kiểm tra. Nếu WO chỉ tham chiếu "BOM hiện tại" thì WO đang chạy dở sẽ đổi định mức ngay giữa chừng: báo cáo sai, truy xuất sai, operator bối rối.

**Snapshot** = lúc release, **chép giá trị** (không chỉ chép khóa ngoại) từ BOM/Routing sang các bảng riêng của WO. Sau đó WO chỉ đọc bản chép của mình. Master đổi tùy ý, WO không bị ảnh hưởng. Đây cũng là lý do M1 có version và trạng thái Approved: WO biết mình chụp từ version nào. Chi tiết kỹ thuật ở mục 2.3.

Ví dụ: BOM `PCB-A` v2 có 24 con điện trở `R-10K`/board. WO-2610-00001 (200 con) release lúc 8h, snapshot cần 200 × 24 × (1 + 2% hao hụt) = 4.896 con. 10h kỹ sư approve BOM v3 đổi còn 20 con/board. WO-2610-00001 vẫn cần 4.896 (theo v2). WO-2610-00002 release sau 10h sẽ chụp v3.

#### f2) [v2] FAI — kiểm tra sản phẩm đầu tiên (First Article Inspection)

**FAI** là quy tắc: khi bắt đầu một WO, **sản phẩm đầu tiên** phải đi hết các bước rồi được QC kiểm tra kỹ (kích thước, ngoại quan, chức năng, đối chiếu BOM thực tế với BOM chuẩn). QC duyệt thì line mới được chạy hàng loạt. Ngoài đời FAI làm ở đầu WO, đầu ca, và sau khi đổi model hoặc sửa máy. MVP chỉ làm FAI theo **đầu WO**.

Vì sao nhà máy cần: một lỗi setup (nạp nhầm reel, sai chương trình máy gắp) sẽ lặp lại trên **mọi** sản phẩm. Phát hiện ở con thứ 1 thì mất 1 con; phát hiện ở con thứ 200 thì mất 200 con.

Phần M2 lo là **trạng thái** trên WO; M3 **chặn** theo trạng thái; M7 **ghi kết quả** và duyệt:

| Cột trên `WorkOrder` | Ý nghĩa |
| --- | --- |
| `RequireFai` | Planner chọn khi tạo WO (mặc định `true`) |
| `FaiStatus` | `NotRequired` (RequireFai = false) · `Pending` (chờ serial FAI đi hết line và QC duyệt) · `Passed` |
| `FaiSerialNo` | Serial được chọn làm mẫu FAI: M3 gán bằng serial **đầu tiên** được scan ở bước đầu khi `FaiStatus = Pending` |

Release đặt `FaiStatus = Pending` (hoặc `NotRequired`). QC duyệt **đạt** (M7) → `Passed`. QC đánh giá **không đạt** → WO vẫn `Pending`, `FaiSerialNo` bị xóa, serial cũ đi Repair hoặc Scrap, serial tiếp theo vào line sẽ thành mẫu FAI mới. Vì vậy **không có** giá trị `Failed` trên WO: kết quả từng lần FAI (đạt/không đạt, người duyệt, checklist) lưu ở `FaiRecord` của M7.

#### g) Serial number

**Serial number** là mã định danh **duy nhất cho từng sản phẩm** (từng con bo mạch), khác với **mã hàng** (Item code `PCB-A`, chung cho mọi con) và **lot** (mã chung cho một lô, ví dụ cuộn linh kiện). Ở điện tử, serial là chìa khóa truy xuất: mọi scan (M3), vật tư đã dùng (M6), kết quả đo (M7) đều gắn vào serial.

Cấu trúc mã đề xuất cho dự án (ví dụ minh họa, bạn có thể đổi):

```
P C B A 2 6 1 0 0 1 0 0 0 1 2 3 2
└──┬──┘ └───┬───┘ └────┬────┘ └┘
Model   Ngày sinh   Số thứ tự    Check
(4 ký tự, YYMMDD    (6 chữ số,   digit
 từ Item             lấy từ       (1 số)
 code bỏ ký tự       SEQUENCE)
 không phải chữ/số)
```

- **Model** (`PCBA`): nhìn serial biết ngay là con gì, không cần tra bảng. Bỏ dấu `-` vì bộ đọc/keyboard wedge đôi khi gõ sai ký tự đặc biệt tùy bố cục bàn phím.
- **Ngày sinh** (`261001` = 01/10/2026): thuận tiện tra cứu bằng mắt. Lưu ý đây là ngày **sinh serial** (lúc release), không phải ngày sản xuất thực tế.
- **Số thứ tự** (`000123`): lấy từ một `SEQUENCE` của SQL Server. **Đây mới là thứ đảm bảo không trùng**, phần còn lại chỉ để đọc cho dễ. Sequence là **toàn cục**, không reset theo ngày, nên hai WO cùng ngày không bao giờ đụng nhau.
- **Check digit**: 1 chữ số kiểm tra tính từ các ký tự trước (dự án dùng tổng mã ASCII với trọng số 3,1 xen kẽ, lấy phần bù về bội của 10), bắt lỗi gõ tay/mờ nhãn. Không bắt buộc, nhưng rẻ và hữu ích khi operator phải gõ tay serial.

Vì sao **không được trùng**:
- Serial là khóa truy xuất. Hai con bo cùng serial thì `ScanLog` lẫn lộn: con A báo NG nhưng con B (cùng số) bị chặn ở Repair; truy xuất ngược ra lot linh kiện của con nào?
- Route check (M3) dựa vào `CurrentSeq` của serial: trùng serial thì một con "nhảy bước" thành công nhờ tiến độ của con kia.
- Khi recall, nhà máy gửi danh sách serial cho khách. Trùng serial nghĩa là thu hồi nhầm hoặc sót.
- Ngoài đời, khách hàng OEM thường quy định định dạng và **dải serial** riêng, trùng là lỗi nghiêm trọng (có thể bị phạt hợp đồng).

Cần phân biệt: serial **được phép có lỗ hổng** (000123 rồi 000125, vì transaction rollback làm mất số), nhưng **không được trùng**. Serial là định danh, không phải bộ đếm số lượng.

#### h) Nhãn sản phẩm & barcode

**Nhãn** là tờ tem dán (hoặc khắc laser) lên sản phẩm, in serial ở dạng đọc được bằng mắt và bằng máy quét. Operator scan nhãn ở mỗi trạm để MES biết "con nào đang ở đây".

Nhãn in bằng máy in nhiệt (thermal transfer, ví dụ Zebra). Máy in nhận lệnh ở ngôn ngữ **ZPL** (Zebra Programming Language): một chuỗi text mô tả vị trí chữ, barcode. MES chỉ cần sinh chuỗi ZPL và gửi cho máy in (hoặc cho Labelary để xem trước). Chi tiết ở mục 2.5.

Hai loại mã vạch dùng cho serial:

| | **Code128** (1D, vạch) | **DataMatrix** (2D, ô vuông) |
| --- | --- | --- |
| Hình dạng | Dãy vạch đen trắng | Ma trận ô vuông, có viền chữ L |
| Dung lượng | Dài theo chiều ngang, mỗi ký tự ~11 module | Nhỏ gọn: 17 ký tự vẫn vừa ô ~16×16 hoặc 18×18 |
| Sửa lỗi | Chỉ có checksum để phát hiện lỗi | Reed-Solomon (ECC 200) sửa được vết xước/bẩn tới mức nhất định |
| Bộ đọc | Laser 1D hoặc imager 2D đều đọc | **Cần imager 2D** (laser 1D không đọc được) |
| Chỗ dùng | Nhãn thùng, nhãn thành phẩm, dán lên vỏ | Bo mạch nhỏ, linh kiện, khắc laser lên PCB |

Code128 nhận toàn bộ 128 ký tự ASCII, chiều dài thay đổi, có subset A/B/C (subset C nén hai chữ số vào một ký hiệu nên chuỗi nhiều số ngắn hơn). Chi tiết chuẩn không cần thuộc; điều cần biết khi làm nghiệp vụ: **bo mạch nhỏ thường dùng DataMatrix vì không còn chỗ cho vạch dài**, còn nhãn thùng dùng Code128. Dự án in cả hai trên cùng tem để demo.

Chi tiết thực tế hay bị bỏ qua:
- Nhãn dán lên PCB **trước** lò reflow (SMT) phải chịu ~245°C, tem giấy thường bị cháy. Người ta dùng tem polyimide chịu nhiệt hoặc khắc laser trực tiếp.
- Cần vùng trống quanh mã (quiet zone), in mờ hoặc dán nhăn là bộ đọc không đọc được. Nhà máy nghiêm ngặt dùng máy verifier chấm điểm chất lượng mã (chuẩn ISO/IEC 15416 cho 1D, 15415 cho 2D).
- In lại nhãn (reprint) phải ghi log: ai in, lúc nào, lần thứ mấy. Nếu không, có thể có hai con bo mang cùng nhãn.

#### i) WIP (Work In Process / 仕掛品)

**WIP** là các sản phẩm đã bắt đầu sản xuất nhưng chưa xong: nằm giữa các công đoạn hoặc đang chờ trước một trạm. Ví dụ WO 200 con: 80 con đang ở giữa SMT và Assembly, 30 con ở Test, 10 con NG chờ Repair, 40 con đã hoàn thành, 40 con chưa vào line. WIP lúc này là 80 + 30 + 10 = 120 con. Dự án định nghĩa **WIP của WO = số serial ở trạng thái InProcess, Ng, Repair** (đã vào line, chưa Completed/Scrapped). Con chưa vào line (Created) không tính WIP; con Hold thống kê riêng.

Vì sao nhà máy quan tâm WIP:
- WIP là tiền nằm chết trên line (đã trả tiền linh kiện, chưa thu tiền thành phẩm).
- Định luật Little: `WIP = năng suất × thời gian lưu trên line`. WIP tăng mà năng suất không đổi nghĩa là thời gian chờ tăng: có nút thắt cổ chai ở đâu đó.
- Khi WO Completed mà vẫn còn WIP (serial chưa xong) thì phải xử lý (sửa, hủy) trước khi Close.

Ở M2 bạn chỉ định nghĩa WIP và chuẩn bị `Serial.Status`, `CurrentSeq`. M3 mới tính WIP theo từng bước (báo cáo "WIP theo bước").

### 1.3 Một ngày ở xưởng: từ kế hoạch đến khóa sổ WO

Nhân vật: **Hương** (Planner), **Dũng** (kỹ sư công nghệ, duyệt BOM), **Tuấn** (Supervisor Line 1), **Lan** (Operator trạm SMT), **Minh** (QC), **Hùng** (thủ kho). Số liệu là ví dụ.

**07:30, Hương tạo WO.** Kế hoạch tuần 40 là 10.000 PCB-A. Hương mở Back-office, tạo `WO-2610-00001`: Item `PCB-A`, 200 con, Line 1, 01/10 từ 08:00 đến 12:00. Hệ thống hiện **preview** (gọi explode BOM của M1): cần 4.896 con điện trở `R-10K`, 202 con IC `U1`... và 4 công đoạn SMT → Assembly → Test → Pack. WO ở trạng thái **Draft**. Hương đổi PlannedQty thành 220 vì nghĩ sẽ hao, rồi đổi lại 200: Draft sửa thoải mái, không ai bị ảnh hưởng.

**07:45, Release bị chặn.** Hương bấm Release. Hệ thống báo: "BOM of PCB-A has no approved version effective now". BOM v3 mới soạn, Dũng chưa duyệt. Đây là gate hoạt động đúng. Dũng approve BOM v3 lúc 07:50 (M1).

**07:52, Release thành công.** Trong một transaction, hệ thống: ghi `BomVersion = 3`, `RoutingVersion = 2`; chép 8 dòng vào `WorkOrderMaterial`, 4 dòng vào `WorkOrderOperation`; sinh 200 serial có số thứ tự 000001 đến 000200, ví dụ `PCBA2610010000011` (số thứ tự lấy từ SEQUENCE, chữ số cuối là check digit); đổi trạng thái **Released**; ghi audit. Cùng lúc, Hương ở máy khác bấm Release lần hai (vì tưởng chưa được): request thứ hai bị từ chối "WO is not in Draft" hoặc lỗi concurrency, **không sinh thêm 200 serial**.

**08:05, in nhãn.** Hương chọn "Print labels" cho WO. Hệ thống sinh ZPL cho 200 serial, xem trước một tem qua Labelary, gửi máy in, ghi 200 dòng `LabelPrintLog`. Lan dán nhãn (polyimide chịu nhiệt) lên bo trước khi vào SMT.

**08:30, chạy.** Lan scan serial đầu tiên ở trạm SMT (M3). Hệ thống tự gọi `Start()`: WO chuyển **InProgress**. Từ giờ WO không được Cancel nữa vì đã có sản lượng.

**10:00, kỹ thuật đổi BOM.** Dũng approve BOM v4 (giảm 24 xuống 20 điện trở/board). `WO-2610-00001` **không đổi**: vẫn theo v3. Hương tạo `WO-2610-00002` chiều nay sẽ chụp v4.

**10:15, nghi lỗi.** Minh (QC) nghi reel IC lô X bị ẩm. Tuấn bấm **Hold** WO, ghi lý do "Suspect IC lot X moisture". Lan scan serial tiếp theo: kiosk đỏ, "WO is on hold". 10:45 kết quả kiểm tra ổn, Tuấn **Resume**: WO về InProgress.

**11:50, kết thúc.** Line làm được 197 con tốt, 3 con Scrapped (không sửa được). Số tốt bước cuối chỉ 197 < 200, hệ thống không tự Complete. Khách chấp nhận giao 197, Tuấn bấm **Complete** thủ công kèm lý do "3 pcs scrapped, customer accepts 197". WO **Completed**.

**Ngày hôm sau, khóa sổ.** Hùng đối chiếu vật tư thừa trả kho, Tuấn kiểm tra không còn serial nào NG chưa xử lý, rồi bấm **Close**. WO **Closed**, chỉ đọc.

Cùng ngày, Hương phát hiện `WO-2610-00003` tạo nhầm sang Line 2, còn ở Draft nên **Cancel** không cần lý do gì đặc biệt (ghi vẫn tốt hơn).

### 1.4 Quy tắc nghiệp vụ & lý do tồn tại

| # | Quy tắc | Lý do |
| --- | --- | --- |
| R1 | Chỉ Release khi Item còn hoạt động, có BOM và Routing **Approved đang hiệu lực** | Không cho công thức nháp lên line; kỹ thuật chịu trách nhiệm về công thức |
| R2 | Khi Release: snapshot BOM → `WorkOrderMaterial`, Routing → `WorkOrderOperation`, ghi `BomVersion`/`RoutingVersion` | WO ổn định dù master đổi; truy xuất biết công thức đã dùng |
| R3 | Sinh serial (nếu `IsSerialized`) đúng bằng `PlannedQty`, cùng transaction với Release | Release thành công thì serial phải có đủ; lỗi giữa chừng thì không còn WO "nửa release" |
| R4 | Serial duy nhất toàn hệ thống: SEQUENCE + unique index `SerialNo` | Xem 1.2g; nhiều người release cùng lúc không được trùng |
| R5 | Một WO chỉ Release được một lần (kiểm tra trạng thái trong transaction + rowversion) | Chống double click, hai Planner cùng bấm |
| R6 | Draft sửa được; từ Released trở đi khóa `ItemId`, `PlannedQty`, `LineId` | Số serial đã sinh bằng PlannedQty; đổi qty làm lệch serial và snapshot. Muốn đổi: Cancel rồi tạo WO mới |
| R7 | Không Cancel khi đã có sản lượng (`QtyGood + QtyNg > 0` ở bất kỳ bước nào) | Đã có sản phẩm thật thuộc WO; hủy WO sẽ làm mồ côi dữ liệu truy xuất. Xử lý bằng Complete/Close hoặc Scrap từng serial |
| R8 | Cancel: tất cả serial `Created` chuyển `Scrapped` (kèm lý do "WO cancelled"), tem chưa dán coi như hủy | Serial của WO hủy không được dùng lại nhưng phải còn vết |
| R9 | Hold chỉ từ InProgress; Resume về InProgress | Hold trước khi chạy không có ý nghĩa (Released chưa làm gì thì Cancel hoặc để đó) |
| R10 | Completed khi `QtyGood` của **bước cuối** ≥ `PlannedQty`, hoặc Supervisor Complete thủ công **bắt buộc có lý do** | Không dùng tổng số scan mọi bước (đếm trùng); ép lý do để audit "vì sao thiếu 3 con" |
| R11 | Closed chỉ từ Completed; sau Closed không sửa gì | Chốt số liệu cho giá thành và báo cáo |
| R12 | Mọi chuyển trạng thái ghi audit (M0) và ném lỗi nghiệp vụ nếu chuyển sai | Chuyển trạng thái là quyết định quan trọng nhất của WO; phải có người chịu trách nhiệm |
| R13 | In lại nhãn phải ghi `LabelPrintLog` (lần in thứ mấy, ai in) | Chống hai con mang cùng nhãn; biết ai in lại và vì sao |
| R14 | `ExternalRef` để trống, không kiểm tra | Chỉ dành cho M8 |
| R15 **[v2]** | `Priority` 1–9, đổi được đến trước Completed (có audit); dispatch list sắp theo `Priority`, rồi `PlannedStart` | Đổi thứ tự ưu tiên là việc hằng ngày của supervisor, không ảnh hưởng snapshot/serial |
| R16 **[v2]** | Release đặt `FaiStatus = Pending` nếu `RequireFai`, ngược lại `NotRequired`; chỉ use case duyệt FAI của M7 được đặt `Passed`; `RequireFai` khóa sau Release | Cổng FAI chỉ có nghĩa nếu không ai "tắt" được nó khi line đang chạy |
| R17 **[v2]** | Snapshot `SopUrl` cùng các cột routing khác | WO đang chạy luôn mở đúng SOP của version routing đã release |

Ghi chú R6: đây là quyết định thiết kế của dự án, không phải luật vũ trụ. Một số nhà máy cho phép đổi qty sau release (xem thêm serial hoặc hủy bớt). Dự án chọn cấm vì đơn giản và an toàn.

### 1.5 Sai lầm/sự cố thường gặp ngoài đời

1. **Sinh serial bằng `SELECT MAX(...) + 1`.** Hai người release cùng lúc đọc cùng MAX, cùng ra serial 000201. Đây là lỗi kinh điển, xảy ra khi số người dùng vượt một. Cách đúng: SEQUENCE hoặc bảng counter có khóa (mục 2.2, 2.4).
2. **WO tham chiếu BOM "sống".** Kỹ sư sửa BOM, WO đang chạy đổi theo, báo cáo tiêu hao lệch với thực tế đã lắp. Lỗi lộ ra sau vài tuần khi kho kiểm kê không khớp. Cách đúng: snapshot.
3. **Sửa số lượng WO sau khi in nhãn.** Đã in 200 nhãn, ai đó sửa thành 180: còn 20 nhãn "ma" ngoài hiện trường và 20 serial không thuộc ai. Vì vậy R6.
4. **Không có trạng thái Closed.** WO mãi ở Completed, vật tư thừa không ai đối chiếu, có người scan thêm vào lệnh đã xong. Số liệu tháng sai.
5. **Cancel WO đã chạy.** Bo mạch thật nằm trên line nhưng WO biến mất. Ngoài xưởng gọi là "hàng mồ côi".
6. **Serial gắn ngày theo giờ UTC.** Serial sinh lúc 00:30 giờ VN (17:30 UTC ngày hôm trước) mang ngày lệch một hôm. Nếu đưa ngày vào serial, dùng **ngày theo múi giờ nhà máy**, còn cột `CreatedAt` vẫn lưu UTC.
7. **Máy quét 1D nhưng in nhãn DataMatrix.** Nhãn đẹp, không ai đọc được. Kiểm tra máy quét (imager 2D) trước khi chọn loại mã.
8. **Nhãn giấy đi qua lò reflow.** Cháy nâu, mất mã, phải in lại và dán lại bằng tay, sinh ra trùng nhãn nếu không log reprint.
9. **Tưởng serial liên tục.** Release lỗi, rollback, SEQUENCE vẫn nhảy số. Có người báo bug "thiếu serial 000150". Đó không phải bug, cần giải thích rõ với người dùng.
10. **Thiếu serial vì scrap.** WO 200 con, 3 con bị hủy, không đủ 200 tốt. Cần quy trình Complete thủ công có lý do hoặc tạo WO bổ sung; nếu không line sẽ bị treo chờ đủ 200.
11. **Release khi kỹ thuật vừa đổi BOM nhưng chưa Approved.** Nhờ R1, hệ thống chặn. Nếu bỏ R1 vì "thuận tiện", lỗi sẽ về lại thành khiếu nại khách.

### 1.6 Bảng thuật ngữ

| English | Tiếng Việt | 日本語 | Giải thích ngắn |
| --- | --- | --- | --- |
| Production Plan | Kế hoạch sản xuất | 生産計画 | Kế hoạch thô theo tuần/tháng, chưa phải lệnh |
| Work Order (WO) | Lệnh sản xuất | 製造指図 / 製造オーダー | Một mã hàng, một số lượng, một line, một khoảng thời gian |
| Release | Phát lệnh | 指図発行 / リリース | Cho phép line chạy; snapshot + sinh serial |
| Dispatch | Điều phối/xếp lệnh vào line | 割付 | Sắp thứ tự và thời điểm chạy trên line |
| Draft | Nháp | 作成中 / 下書き | Đang soạn, sửa tự do |
| Released | Đã phát lệnh | 発行済 | Sẵn sàng chạy |
| InProgress | Đang sản xuất | 生産中 | Đã có sản phẩm được scan |
| OnHold | Tạm dừng | 保留 | Dừng lệnh, còn khả năng Resume |
| Completed | Hoàn thành sản xuất | 生産完了 | Đủ sản lượng hoặc đóng thủ công |
| Closed | Khóa sổ | 締め / クローズ | Đối chiếu xong, chỉ đọc |
| Cancelled | Hủy | 取消 | Không chạy nữa (chỉ khi chưa có sản lượng) |
| Snapshot | Bản chụp (đóng băng) | スナップショット | Chép giá trị BOM/Routing vào WO lúc release |
| BOM | Định mức vật tư | 部品表 (BOM) | Xem M1 |
| Routing | Quy trình công đoạn | 工順 / 工程表 | Xem M1 |
| Serial number | Số serial | シリアル番号 / 製造番号 | Mã duy nhất cho từng sản phẩm |
| Numbering (sequence) | Cấp số | 採番 | Cấp số tự động, không trùng |
| Check digit | Số kiểm tra | チェックデジット | Ký tự cuối bắt lỗi gõ sai |
| Label | Nhãn/tem | ラベル | Tem dán lên sản phẩm |
| Barcode | Mã vạch | バーコード | Mã 1D dạng vạch |
| Code128 | Mã vạch Code128 | コード128 | Mã 1D, nhận ASCII, chiều dài thay đổi |
| DataMatrix | Mã ma trận DataMatrix | データマトリックス | Mã 2D nhỏ gọn, có sửa lỗi |
| Quiet zone | Vùng trống quanh mã | クワイエットゾーン | Lề trắng bắt buộc để bộ đọc nhận ra mã |
| Reprint | In lại nhãn | 再印刷 | Phải có log |
| WIP | Bán thành phẩm đang làm dở | 仕掛品 | Sản phẩm đã vào line chưa xong |
| Planned Qty | Số lượng kế hoạch | 計画数量 | Số lượng WO cần làm |
| Scrap | Phế phẩm/hủy | 廃棄 / スクラップ | Sản phẩm không sửa được |
| ExternalRef | Tham chiếu hệ thống ngoài | 外部参照 | Số lệnh của ERP (M8), để trống ở MVP |

---

## 2. Kiến thức lập trình

Module này dùng lại MediatR/FluentValidation/EF Core configuration từ M1 và audit interceptor từ M0; không dạy lại. Phần mới: state machine trong domain (2.1), transaction & concurrency (2.2), snapshot pattern (2.3), sinh serial (2.4), ZPL & Labelary (2.5), WPF navigation & CanExecute theo trạng thái (2.6).

### 2.1 State machine trong domain (rich domain model)

**Nó là gì.** State machine là mô hình "đối tượng ở đúng một trạng thái tại một thời điểm, và chỉ được chuyển sang một số trạng thái nhất định qua các hành động cho phép". WO có 7 trạng thái, 9 chuyển hợp lệ (bảng bên dưới). **Rich domain model** nghĩa là các quy tắc chuyển nằm **trong chính entity** (`wo.Release()`), không nằm rải rác ở controller hay handler.

**Vấn đề nó giải quyết.** Cách ngây thơ: entity chỉ có property `Status` public setter, handler tự viết `wo.Status = WorkOrderStatus.Released` sau vài `if`. Hậu quả: ba handler khác nhau (API, job, màn hình) mỗi nơi kiểm tra một kiểu; một chỗ quên kiểm tra là WO Closed bị Release lại. Muốn biết "WO được chuyển thế nào" phải đọc cả codebase.

**Khái niệm cốt lõi.**
- `Status` có **private setter**: chỉ đổi được qua method của entity.
- Mỗi method (`Release`, `Start`, `Hold`, `Resume`, `Complete`, `Close`, `Cancel`) tự kiểm tra trạng thái hiện tại và ném `DomainException` nếu sai.
- Có thêm `AllowedActions` (tính từ Status) để UI biết nút nào bật; UI không tự lặp lại luật.
- Không cần thư viện. Nếu máy trạng thái lớn hơn (nhiều guard, sự kiện), có thể xét thư viện `Stateless` hoặc State pattern bằng class; với 7 trạng thái, method thường là đủ.

Bảng chuyển hợp lệ (mọi ô khác đều bị từ chối):

| Hành động | Từ | Sang | Điều kiện thêm |
| --- | --- | --- | --- |
| `Release` | Draft | Released | Có snapshot BOM/Routing (mục 2.3) |
| `Start` | Released | InProgress | Gọi bởi M3 khi scan đầu tiên |
| `Hold` | InProgress | OnHold | Bắt buộc lý do |
| `Resume` | OnHold | InProgress | |
| `Complete` | InProgress | Completed | Đủ QtyGood bước cuối, hoặc có lý do thủ công |
| `Close` | Completed | Closed | |
| `Cancel` | Draft, Released, InProgress | Cancelled | Không có sản lượng; bắt buộc lý do |

**Snippet** (rút gọn, bỏ phần snapshot; .NET 10):

```csharp
public enum WorkOrderStatus { Draft, Released, InProgress, OnHold, Completed, Closed, Cancelled }

public class DomainException(string message) : Exception(message);   // primary constructor, C# 12

public class WorkOrder
{
    private readonly List<WorkOrderOperation> _operations = new();
    public IReadOnlyList<WorkOrderOperation> Operations => _operations;   // chỉ đọc từ bên ngoài

    public WorkOrderStatus Status { get; private set; } = WorkOrderStatus.Draft;
    public decimal PlannedQty { get; private set; }
    public string? HoldReason { get; private set; }
    public string? ExternalRef { get; private set; }          // để trống, dành cho M8
    public int Priority { get; private set; } = 5;             // [v2] 1 = gấp nhất
    public bool RequireFai { get; private set; } = true;       // [v2]
    public FaiStatus FaiStatus { get; private set; }           // [v2] NotRequired | Pending | Passed
    public string? FaiSerialNo { get; private set; }           // [v2] M3 gán khi scan serial đầu tiên
    public byte[] RowVersion { get; private set; } = default!; // xem 2.2

    // Có sản lượng khi bất kỳ bước nào đã ghi tốt/NG. PHẢI Include(Operations) khi load.
    public bool HasOutput => _operations.Any(o => o.QtyGood + o.QtyNg > 0);

    public void Hold(string reason)
    {
        Ensure(WorkOrderStatus.InProgress);
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("Hold reason is required.");
        Status = WorkOrderStatus.OnHold; HoldReason = reason;
    }

    public void Cancel(string reason)
    {
        Ensure(WorkOrderStatus.Draft, WorkOrderStatus.Released, WorkOrderStatus.InProgress);
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("Cancel reason is required.");
        if (HasOutput) throw new DomainException("Cannot cancel a work order that already has output.");
        Status = WorkOrderStatus.Cancelled;   // (serial Created của WO chuyển Scrapped: làm ở handler, xem 2.3 và Bước 7)
    }

    // [v2] M7 gọi khi QC duyệt FAI; M3 gọi AssignFaiSerial ở scan đầu tiên
    public void AssignFaiSerial(string serialNo)
    {
        if (FaiStatus != FaiStatus.Pending) throw new DomainException("FAI is not pending.");
        FaiSerialNo ??= serialNo;                                 // chỉ gán lần đầu
    }
    public void RecordFaiResult(bool passed)
    {
        if (FaiStatus != FaiStatus.Pending || FaiSerialNo is null) throw new DomainException("No FAI sample to judge.");
        if (passed) FaiStatus = FaiStatus.Passed; else FaiSerialNo = null;   // không đạt: chờ mẫu mới
    }

    private void Ensure(params WorkOrderStatus[] allowed)
    {
        if (!allowed.Contains(Status))
            throw new DomainException($"Work order {Status} cannot perform this action.");
    }
}
```

**Test mọi chuyển trạng thái** (Bước 1): lập bảng (trạng thái xuất phát × hành động) → mong đợi thành công hay ném lỗi. `[Theory]` chạy cả ma trận 7 × 7 chỉ bằng vài dòng:

```csharp
[Theory]
[InlineData(WorkOrderStatus.Draft,      "Hold",   false)]   // Draft không Hold được
[InlineData(WorkOrderStatus.InProgress, "Hold",   true)]
[InlineData(WorkOrderStatus.Closed,     "Cancel", false)]   // Closed là cuối đường
public void Transition_matrix(WorkOrderStatus from, string action, bool ok)
{
    var wo = WorkOrderTestBuilder.InStatus(from);   // helper tự viết: dựng WO ở trạng thái mong muốn
    var act = () => Apply(wo, action);              // switch nhỏ gọi wo.Hold("x") / wo.Cancel("x")...
    if (ok) act();                                   // không được ném
    else Assert.Throws<DomainException>(act);
}
```

**Bẫy thường gặp.**
- EF Core cần dựng entity dù setter private: dùng constructor riêng (`private WorkOrder() {}`) cho EF, và constructor/factory công khai cho code nghiệp vụ. Collection dùng backing field (`_operations`) cần cấu hình `builder.Navigation(x => x.Operations).UsePropertyAccessMode(PropertyAccessMode.Field)`.
- `HasOutput` chỉ đúng khi collection `Operations` đã được load. Nếu handler quên `Include`, `HasOutput` luôn `false` và bạn Cancel được WO đã chạy. Test riêng case này (dùng SQL Server thật qua Testcontainers, không chỉ test in-memory).
- Lưu enum thành chuỗi (`HasConversion<string>()`) để đọc DB bằng mắt được; giá trị số sẽ rất khó debug khi có người thêm trạng thái ở giữa enum.
- Đừng để UI/handler tự nhớ "trạng thái nào được làm gì". Trả `AllowedActions` từ API (xem 2.6).

### 2.2 Transaction & concurrency: SEQUENCE, UPDLOCK, rowversion

**Nó là gì.** Khi nhiều request chạm cùng dữ liệu cùng lúc, cần cơ chế để kết quả vẫn đúng như khi chạy lần lượt. Có ba công cụ dùng trong module này: **optimistic concurrency bằng rowversion** (cho dòng WO), **SEQUENCE** (cấp số duy nhất), **UPDLOCK** (khóa dòng khi đọc, dùng với bảng counter).

**Vấn đề nó giải quyết.** Hai tình huống race của module:

1. Hương và Tuấn cùng bấm Release một WO. Cả hai request đọc `Status = Draft`, đều qua kiểm tra, đều sinh 200 serial. Kết quả: 400 serial cho WO 200 con.
2. Hai WO khác nhau release cùng lúc, cùng sinh serial. Nếu cấp số bằng `MAX + 1`, cả hai đọc cùng MAX và sinh trùng số.

Cách ngây thơ "kiểm tra rồi ghi" (`if (wo.Status == Draft) {...}`) không đủ, vì giữa lúc kiểm tra và lúc ghi có khoảng hở (check-then-act).

**Khái niệm cốt lõi.**

*Optimistic concurrency (lạc quan)*: giả định hiếm khi đụng nhau, không khóa gì cả; chỉ phát hiện khi ghi. Thêm cột `rowversion` (SQL Server tự tăng mỗi khi dòng bị UPDATE). EF Core thêm `WHERE Id = @id AND RowVersion = @giá_trị_lúc_đọc` vào câu UPDATE. Nếu ai đó đã sửa dòng trước, UPDATE tác động 0 dòng và EF ném `DbUpdateConcurrencyException`. Request đến sau thua, và **toàn bộ SaveChanges (kể cả 200 serial insert) rollback**.

*Pessimistic (bi quan)*: khóa dòng ngay khi đọc để người khác phải chờ. `UPDLOCK` là hint nói "tôi đọc để sắp cập nhật, giữ khóa cập nhật, đừng cho ai khác lấy khóa cập nhật cùng dòng". Dùng khi đụng nhau **thường xuyên** hoặc hậu quả thua rất tốn kém.

*SEQUENCE*: đối tượng của SQL Server, cấp số bằng `NEXT VALUE FOR dbo.SerialSeq`. Mỗi lần gọi nhận một số khác nhau, kể cả hàng nghìn kết nối cùng lúc, không cần khóa bảng. Đặc điểm quan trọng: **nằm ngoài transaction**: rollback không trả số lại, và nếu khai báo `CACHE` thì restart server cũng có thể mất một khoảng số. Nghĩa là số **có lỗ hổng, nhưng không trùng**.

| | SEQUENCE | Bảng counter + UPDLOCK |
| --- | --- | --- |
| Cấp được số liên tục không lỗ hổng | Không (rollback vẫn mất số) | Có, nếu cập nhật trong cùng transaction |
| Tốc độ / tranh chấp | Rất tốt, không khóa | Mọi người xếp hàng chờ một dòng |
| Cấp nhiều số một lần (200 serial) | Dễ: `sp_sequence_get_range` hoặc `NEXT VALUE FOR ... OVER` | Dễ: cộng 200 rồi trả khoảng |
| Khi nào chọn | Cần định danh duy nhất (serial) | Cần liên tục có ý nghĩa pháp lý (số chứng từ) |

Với serial, không ai đòi liên tục, nên dự án chọn SEQUENCE. Bảng counter được ghi lại vì spec nêu hai phương án và phỏng vấn hay hỏi.

**Snippet 1**: cấu hình EF Core cho sequence, rowversion, unique index.

```csharp
// Infrastructure/Persistence/Configurations/PlanningConfigurations.cs
public void Configure(ModelBuilder modelBuilder)
{
    // Hai sequence: mã WO và serial. Đều bigint, bắt đầu từ 1.
    modelBuilder.HasSequence<long>("SerialSeq").StartsAt(1).IncrementsBy(1);
    modelBuilder.HasSequence<long>("WoNoSeq").StartsAt(1).IncrementsBy(1);

    modelBuilder.Entity<WorkOrder>(b =>
    {
        b.Property(x => x.RowVersion).IsRowVersion();          // SQL Server rowversion, EF tự kèm vào WHERE
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.HasIndex(x => x.WoNo).IsUnique();
        b.Property(x => x.ExternalRef).HasMaxLength(50);       // nullable, để trống
    });

    modelBuilder.Entity<Serial>(b =>
    {
        b.HasIndex(x => x.SerialNo).IsUnique();                // lớp bảo vệ thứ hai ngoài SEQUENCE
        b.HasIndex(x => new { x.WoId, x.Status });             // đếm WIP/tiến độ theo WO
        b.Property(x => x.SerialNo).HasMaxLength(30).IsUnicode(false);
    });
}
```

**Snippet 2**: bảng counter với UPDLOCK (SQL, phương án thay thế).

```sql
-- Bảng counter: mỗi dòng là một "bộ đếm" (ví dụ tên = 'SERIAL')
CREATE TABLE dbo.NumberCounter (Name varchar(30) PRIMARY KEY, LastValue bigint NOT NULL);

BEGIN TRAN;
    DECLARE @first bigint;
    -- UPDLOCK + HOLDLOCK: khóa dòng ngay lúc đọc và giữ tới hết transaction,
    -- người thứ hai phải chờ ở đây thay vì đọc cùng giá trị.
    SELECT @first = LastValue + 1
    FROM dbo.NumberCounter WITH (UPDLOCK, HOLDLOCK)
    WHERE Name = 'SERIAL';

    UPDATE dbo.NumberCounter SET LastValue = @first + 200 - 1 WHERE Name = 'SERIAL';  -- xin 200 số một lần
    -- ... insert 200 Serial dùng khoảng [@first, @first + 199] ...
COMMIT;   -- rollback thì LastValue quay về, không mất số (khác SEQUENCE)
```

**Snippet 3**: bắt xung đột trong handler và trả 409.

```csharp
// Application/Planning/ReleaseWorkOrder/ReleaseWorkOrderHandler.cs (đoạn cuối)
try
{
    await _db.SaveChangesAsync(ct);   // 1 transaction ngầm: update WO + insert snapshot + insert 200 serial
}
catch (DbUpdateConcurrencyException)
{
    // Có người sửa/release WO này giữa lúc ta đọc và ghi
    throw new ConflictException("Work order was changed by another user. Reload and try again.");
}
// ConflictException được exception middleware của M0 map thành ProblemDetails 409.
```

**Hai tầng phòng thủ cho Release.**
1. **Rowversion** bắt request thứ hai nếu hai request chồng lấn thời gian rất sát.
2. **Kiểm tra trạng thái trong `Release()`** (`Ensure(Draft)`) bắt request thứ hai nếu nó đến *sau khi* request thứ nhất đã commit (lúc đó WO đã Released; request sau đọc Released, bị `DomainException`).

Ngoài ra: **client gửi lên `RowVersion` mà nó thấy** (dạng base64 trong DTO) để bắt cả trường hợp Hương mở màn hình lúc 8:00 rồi bấm Release lúc 8:30 sau khi WO đã bị người khác Hold/Cancel. Phía server gán nó vào `OriginalValue`:

```csharp
_db.Entry(wo).Property(x => x.RowVersion).OriginalValue = Convert.FromBase64String(cmd.RowVersion);
```

**Kiểm chứng bằng test** (Bước 5, dùng Testcontainers của M0): chạy hai release đồng thời và khẳng định đúng một thành công, 200 serial khác nhau.

```csharp
[Fact]
public async Task Two_concurrent_releases_generate_serials_once()
{
    var woId = await Fixture.CreateDraftWorkOrderAsync(qty: 200);       // helper của bạn
    // Mỗi task dùng scope (DbContext) riêng, y như hai request HTTP riêng
    var results = await Task.WhenAll(
        Fixture.TrySendAsync(new ReleaseWorkOrderCommand(woId)),        // trả true nếu thành công, false nếu bị từ chối
        Fixture.TrySendAsync(new ReleaseWorkOrderCommand(woId)));

    Assert.Single(results.Where(r => r));                                // đúng 1 thành công
    var serials = await Fixture.GetSerialNosAsync(woId);
    Assert.Equal(200, serials.Count);
    Assert.Equal(200, serials.Distinct().Count());                       // không trùng
}
```

**Bẫy thường gặp.**
- Test đồng thời **không** thay thế unique index. Test chỉ chứng minh trong một lần chạy; index bảo vệ mãi mãi.
- `rowversion` không phải là thời gian. Đừng so sánh lớn/nhỏ, chỉ so bằng.
- `SaveChanges` đã bọc mọi thay đổi trong một transaction; **đừng** gọi `SaveChanges` hai lần (ví dụ một lần cho WO, một lần cho serial) mà không bọc bằng transaction tường minh, vì bạn sẽ có WO Released nhưng thiếu serial khi lỗi giữa chừng.
- SEQUENCE bị "tiêu" ngay cả khi request thua và rollback: đó là lý do serial có lỗ hổng. Đừng cố "sửa" bằng cách reset sequence; sẽ sinh trùng.
- `UPDLOCK` mà không có transaction thì khóa được nhả ngay sau câu SELECT, vô tác dụng. Luôn đặt trong `BEGIN TRAN`.
- Nếu hai request xin cả `WoNoSeq` lẫn `SerialSeq`, không có deadlock vì sequence không khóa; còn với counter table, luôn khóa các bảng theo cùng một thứ tự.

### 2.3 Snapshot pattern

**Nó là gì.** Khi tạo một bản ghi, **chép giá trị** cần dùng từ nguồn (master) sang bản ghi đó, thay vì chỉ giữ khóa ngoại trỏ tới nguồn. Sau đó nguồn đổi thì bản chép không đổi.

**Vấn đề nó giải quyết.** Cách ngây thơ: `WorkOrderMaterial` chỉ lưu `BomLineId`, khi cần số lượng thì JOIN tới `BomLine`. Rủi ro: ai đó sửa `BomLine.QtyPer` (nếu master không bị khóa), hoặc xóa mềm dòng, hoặc chuyển version; WO đang chạy đổi số theo. Ngay cả khi M1 khóa BOM Approved (nên về lý thuyết version đã duyệt không đổi), snapshot vẫn có ích vì:
- WO cần cột **riêng của nó** (`IssuedQty`, `ConsumedQty`, `QtyGood`, `QtyNg`, `Status`) nên dù sao cũng phải có dòng riêng cho mỗi WO.
- Đọc WO không phải JOIN qua hai ba bảng master theo version.
- Master có thể bị vô hiệu (`IsActive = false`), thay đổi cấu trúc, hoặc sau này bị ERP ghi đè (M8); WO đã release không nên phụ thuộc.
- Chụp cả `BomVersion`/`RoutingVersion` để truy xuất "con này làm theo phiên bản nào".

**Khái niệm cốt lõi.**
- Thời điểm chụp = lúc **Release** (không phải lúc tạo Draft). Draft chỉ có preview, chưa chụp: preview luôn tính theo master hiện tại; sau Release mới đóng băng.
- Chụp **giá trị đã tính** (`RequiredQty = PlannedQty × QtyPer × (1 + ScrapRate)`) chứ không chỉ `QtyPer`, và chụp cả `QtyPer`/`ScrapRate` gốc để đối chiếu.
- BOM đa cấp (M1): dùng kết quả **explode** ra cấp NVL lá (đúng cái M1 trả về), kèm `OperationSeq` để biết vật tư dùng ở bước nào.
- Snapshot và ghi trạng thái phải nằm **cùng một transaction** (2.2), tránh WO Released mà thiếu snapshot.

Ví dụ với số liệu 1.2f: `R-10K`, `QtyPer = 24`, `ScrapRate = 0.02`, `PlannedQty = 200` → `RequiredQty = 200 × 24 × 1,02 = 4.896`.

**Snippet** (đoạn Release trong entity; `BomLineSnapshot` và `RoutingStepSnapshot` là record DTO trong Application do handler dựng từ query M1):

```csharp
public sealed record BomLineSnapshot(int ComponentItemId, decimal QtyPer, decimal ScrapRate, int? OperationSeq);
public sealed record RoutingStepSnapshot(int Seq, string OperationCode, int WorkCenterId, decimal IdealCycleTimeSec,
                                         bool IsInspection, string? SopUrl);   // [v2] SopUrl

public void Release(int bomVersion, int routingVersion,
                    IReadOnlyCollection<BomLineSnapshot> bomLines,
                    IReadOnlyCollection<RoutingStepSnapshot> steps, DateTime utcNow)
{
    Ensure(WorkOrderStatus.Draft);                                    // chỉ Release một lần
    if (bomLines.Count == 0) throw new DomainException("BOM has no lines.");
    if (steps.Count == 0)    throw new DomainException("Routing has no steps.");

    BomVersion = bomVersion;
    RoutingVersion = routingVersion;

    foreach (var l in bomLines)                                       // chép GIÁ TRỊ, không chỉ khóa ngoại
        _materials.Add(new WorkOrderMaterial(l.ComponentItemId, l.QtyPer, l.ScrapRate, l.OperationSeq,
            requiredQty: PlannedQty * l.QtyPer * (1 + l.ScrapRate)));

    foreach (var s in steps.OrderBy(x => x.Seq))
        _operations.Add(new WorkOrderOperation(s.Seq, s.OperationCode, s.WorkCenterId, s.IdealCycleTimeSec,
                                               s.IsInspection, s.SopUrl));

    FaiStatus = RequireFai ? FaiStatus.Pending : FaiStatus.NotRequired;   // [v2]
    Status = WorkOrderStatus.Released;
    ReleasedAt = utcNow;                                              // UTC
}
```

**Vì sao `Serial` không nằm trong aggregate `WorkOrder`.** WO 10.000 con có 10.000 serial; nạp toàn bộ vào bộ nhớ mỗi lần đổi trạng thái là lãng phí. `Serial` là aggregate riêng, tham chiếu `WoId`. Handler Release: (1) nạp WO + BOM/Routing snapshot qua interface đọc M1; (2) gọi `wo.Release(...)`; (3) sinh serial (2.4); (4) một `SaveChanges`. Cancel làm tương tự: handler gọi `wo.Cancel(reason)` rồi cập nhật hàng loạt serial `Created` thành `Scrapped` bằng một câu lệnh set-based (ví dụ `ExecuteUpdateAsync` của EF Core 7+) trong cùng transaction.

**Bẫy thường gặp.**
- Chụp trước khi kiểm tra Approved: viết bước kiểm tra **trước** khi chụp; lấy version Approved **đang hiệu lực tại `utcNow`** (M1 có `EffectiveFrom/To`).
- Quên kiểm tra BOM rỗng: WO Released mà không cần vật tư nào là dấu hiệu master data sai.
- Chụp `IdealCycleTimeSec` rồi vẫn JOIN routing "sống" ở M5: mất ý nghĩa snapshot. Nhắc lại M5 dùng số trong `WorkOrderOperation`.
- Sửa dòng snapshot bằng tay để "fix nhanh": mất tính đóng băng. Sửa chỉ qua use case có audit.
- Sai số làm tròn: `decimal(18,4)` cho `RequiredQty`; nếu hiển thị theo đơn vị đếm được (pcs), làm tròn **lên** khi hiển thị, không lưu làm tròn.

### 2.4 Sinh serial: SEQUENCE + hàm định dạng

**Nó là gì.** Hai phần tách biệt: (a) **cấp số** duy nhất (SQL Server SEQUENCE); (b) **định dạng** số đó thành chuỗi serial (hàm thuần trong Domain, dễ test).

**Vấn đề nó giải quyết.** Cách ngây thơ: tự `MAX(SerialNo)` rồi `+1` (race, 1.5.1), hoặc dùng `Guid` (duy nhất nhưng dài, không đọc được, không in vừa nhãn nhỏ). Cần mã ngắn, đọc được, duy nhất, có thể tạo hàng trăm mã một lần.

**Khái niệm cốt lõi.**
- Xin **cả khoảng** số trong một lần gọi DB (200 số một round trip), không gọi 200 lần.
- Hàm định dạng nhận `(itemCode, ngày theo giờ nhà máy, số)` và trả chuỗi: dễ unit test không cần DB.
- Hai lớp bảo vệ: SEQUENCE (không trùng số) + unique index `SerialNo` (nếu có lỗi lập trình sinh trùng chuỗi, DB từ chối ngay thay vì âm thầm nhận).

**Snippet 1**: hàm định dạng (Domain, không phụ thuộc DB).

```csharp
public static class SerialFormat
{
    public const long MaxSequence = 999_999;                      // 6 chữ số; hết dải thì đổi sang D8 hoặc sequence theo model

    public static string Build(string itemCode, DateOnly factoryDate, long seq)
    {
        if (seq is < 1 or > MaxSequence) throw new DomainException("Serial sequence out of range.");
        var model = new string(itemCode.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        model = model.Length >= 4 ? model[..4] : model.PadRight(4, 'X');    // PCB-A -> PCBA
        var body = $"{model}{factoryDate:yyMMdd}{seq:D6}";                   // PCBA261001000123
        return body + CheckDigit(body);                                      // PCBA2610010001232
    }

    // Trọng số 3,1 xen kẽ trên mã ASCII, lấy phần bù về bội của 10
    private static char CheckDigit(string s)
    {
        var sum = 0;
        for (var i = 0; i < s.Length; i++) sum += s[i] * (i % 2 == 0 ? 3 : 1);
        return (char)('0' + (10 - sum % 10) % 10);
    }
}
```

Unit test: `Build("PCB-A", new DateOnly(2026,10,1), 123)` phải trả `PCBA2610010001232`; `seq = 0` và `seq = 1_000_000` phải ném lỗi. (Giá trị `...2` được tính bằng một script tương đương với hàm trên; hãy tự chạy unit test để xác nhận.)

**Snippet 2**: xin một khoảng số từ SEQUENCE (Infrastructure; nằm trong cùng `DbContext` với transaction release).

```csharp
// Xin `count` số liên tiếp trong 1 câu SQL.
// LƯU Ý: đây là kỹ thuật viết theo hiểu biết của tài liệu, hãy chạy thử trên SQL Server của bạn.
public async Task<IReadOnlyList<long>> ReserveSerialNumbersAsync(int count, CancellationToken ct)
{
    // ORDER BY trong OVER quyết định thứ tự cấp số; bảng dãy số tạm sinh bằng ROW_NUMBER
    var sql = """
        SELECT NEXT VALUE FOR dbo.SerialSeq OVER (ORDER BY n.rn) AS [Value]
        FROM (SELECT TOP (@count) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS rn
              FROM sys.all_objects a CROSS JOIN sys.all_objects b) n
        """;
    return await _db.Database
        .SqlQueryRaw<long>(sql, new SqlParameter("@count", count))   // EF Core 7+; cột phải tên "Value"
        .ToListAsync(ct);
}
```

Phương án khác: gọi stored procedure hệ thống `sp_sequence_get_range` (trả số đầu của khoảng và bạn tự cộng), hoặc gọi `SELECT NEXT VALUE FOR dbo.SerialSeq` 200 lần trong một vòng lặp (chậm hơn nhưng đơn giản nhất, chấp nhận được với WO vài trăm con). Nếu cách đầu tiên (`OVER`) làm bạn kẹt, dùng vòng lặp trước rồi tối ưu sau.

**Snippet 3**: handler ghép lại.

```csharp
var seqs = await _serials.ReserveSerialNumbersAsync((int)wo.PlannedQty, ct);
var factoryDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(_clock.UtcNow, _factoryTz)); // giờ nhà máy, không phải UTC
var serials = seqs.Select(n => Serial.Create(
    SerialNo: SerialFormat.Build(item.Code, factoryDate, n), woId: wo.Id, itemId: wo.ItemId)).ToList();
_db.Serials.AddRange(serials);       // trạng thái ban đầu Created, CurrentSeq = 0
await _db.SaveChangesAsync(ct);      // cùng transaction với wo.Release()
```

**Bẫy thường gặp.**
- Ngày trong serial dùng UTC thay vì giờ nhà máy (1.5.6). Dùng `TimeProvider` (có sẵn từ .NET 8) để test được; M5 sẽ dạy kỹ. Không tự viết `IClock`.
- Đếm ký tự sai: check digit thêm một ký tự, nhãn dài hơn. Tính lại chiều rộng barcode (2.5).
- Model 4 ký tự có thể đụng nhau giữa hai mã hàng (`PCBA-1` và `PCBA-2` đều thành `PCBA`). Không sao vì số thứ tự toàn cục, nhưng nhìn bằng mắt sẽ nhầm. Nếu vấn đề, thêm cột `Item.SerialPrefix` (ngoài M1, làm khi cần).
- Serial nhập tay sai một ký tự: kiểm tra check digit ở API scan (M3) trước khi truy vấn DB, báo "invalid serial format" ngay.

### 2.5 ZPL & Labelary: sinh nhãn và xem trước

**Nó là gì.** **ZPL** (Zebra Programming Language) là ngôn ngữ text để ra lệnh cho máy in nhãn Zebra: "đặt chữ ở tọa độ này, vẽ Code128 ở tọa độ kia". **Labelary** là dịch vụ web (labelary.com) nhận ZPL và trả lại ảnh (PNG) hoặc PDF của nhãn, giúp xem trước mà **không cần máy in thật**. Dự án không có máy in thật, nên Labelary là cách duy nhất "nhìn thấy" nhãn.

**Vấn đề nó giải quyết.** Cách ngây thơ: vẽ nhãn bằng in tài liệu qua driver Windows (GDI): phụ thuộc driver, lệch kích thước giữa máy, khó tự động hóa. ZPL gửi thẳng tới máy in nên nhãn giống nhau trên mọi máy; và vì ZPL chỉ là chuỗi, bạn **unit test** được ("chuỗi ZPL có chứa serial này không").

**Khái niệm cốt lõi.**
- Nhãn = chuỗi lệnh bắt đầu `^XA`, kết thúc `^XZ`. Mỗi trường: `^FO x,y` (đặt vị trí, đơn vị dot) → lệnh vẽ → `^FD dữ liệu ^FS` (field data / field separator).
- **Dot và mật độ in**: máy 203 dpi ≈ 8 dot/mm (Labelary gọi là `8dpmm`). Nhãn 60 × 30 mm = 480 × 240 dot.
- Lệnh dùng trong dự án (kiểm tra lại tham số trong ZPL Programming Guide của Zebra):

| Lệnh | Ý nghĩa |
| --- | --- |
| `^XA` / `^XZ` | Bắt đầu / kết thúc một nhãn |
| `^PW480`, `^LL240` | Chiều rộng in, chiều dài nhãn (dot) |
| `^FO20,15` | Đặt góc trên trái của phần tử kế tiếp tại (20, 15) |
| `^A0N,26,26` | Font mặc định scalable, hướng thường, cao/rộng 26 dot |
| `^BY2` | Độ rộng module hẹp nhất của vạch = 2 dot |
| `^BCN,60,Y,N,N` | Code128: hướng thường, cao 60 dot, in dòng chữ dưới vạch |
| `^BXN,3,200` | DataMatrix: hướng thường, mỗi ô 3 dot, ECC 200 |
| `^FD...^FS` | Dữ liệu của trường; kết thúc trường |

**Snippet 1**: ZPL cho một nhãn 60 × 30 mm (ví dụ, chưa chạy thử trên máy in; hãy xem trước bằng Labelary).

```
^XA
^PW480
^LL240
^FO20,15^A0N,26,26^FDPCB-A   WO-2610-00001^FS
^FO20,50^BY2^BCN,60,Y,N,N^FDPCBA2610010001232^FS
^FO390,150^BXN,3,200^FDPCBA2610010001232^FS
^FO20,190^A0N,22,22^FDBOM v3   RT v2^FS
^XZ
```

Tính nhanh chiều rộng Code128 để không tràn nhãn: 17 ký tự dạng "4 chữ + 13 số" cần khoảng 15–17 ký hiệu × 11 module + start/stop/check, tổng ~200 module; với `^BY2` là ~400 dot, từ x = 20 tới ~420, còn 60 dot bên phải nên DataMatrix (~18×18 ô × 3 dot ≈ 54 dot) đặt ở hàng dưới cho không đè lên vạch. Đây là **ước lượng**; Labelary preview sẽ cho biết thực tế.

**Snippet 2**: builder sinh ZPL (Infrastructure hoặc Application; là hàm thuần, dễ test).

```csharp
public static class ZplLabelBuilder
{
    public static string Build(string itemCode, string woNo, string serialNo, int bomVer, int routingVer)
    {
        // ZPL dùng '^' và '~' làm ký tự lệnh; serial chỉ có chữ/số nên an toàn, nhưng tên/mã khác thì kiểm tra
        foreach (var s in new[] { itemCode, woNo, serialNo })
            if (s.Contains('^') || s.Contains('~')) throw new DomainException("Invalid character in label data.");

        return $"""
            ^XA
            ^PW480
            ^LL240
            ^FO20,15^A0N,26,26^FD{itemCode}   {woNo}^FS
            ^FO20,50^BY2^BCN,60,Y,N,N^FD{serialNo}^FS
            ^FO390,150^BXN,3,200^FD{serialNo}^FS
            ^FO20,190^A0N,22,22^FDBOM v{bomVer}   RT v{routingVer}^FS
            ^XZ
            """;
    }
}
```

**Snippet 3**: lấy ảnh xem trước từ Labelary (Infrastructure). **Phần API này viết theo hiểu biết của tài liệu, hãy đối chiếu với trang tài liệu chính thức của Labelary (labelary.com/service.html): đường dẫn, header, giới hạn số nhãn/lượt gọi.**

```csharp
public async Task<byte[]> PreviewAsync(string zpl, CancellationToken ct)
{
    // Dạng URL theo tài liệu Labelary: /v1/printers/{dpmm}/labels/{rộng}x{cao inch}/{chỉ số nhãn}/
    // 60 x 30 mm ~ 2.36 x 1.18 inch. Nếu URL/tham số khác, sửa theo tài liệu.
    using var req = new HttpRequestMessage(HttpMethod.Post,
        "http://api.labelary.com/v1/printers/8dpmm/labels/2.36x1.18/0/")
    {
        Content = new StringContent(zpl)               // thân request là chuỗi ZPL
    };
    req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/png"));   // xin ảnh PNG

    using var res = await _http.SendAsync(req, ct);    // _http: HttpClient từ IHttpClientFactory
    res.EnsureSuccessStatusCode();
    return await res.Content.ReadAsByteArrayAsync(ct);
}
```

**In thật ra máy in mạng** (không làm trong MVP, chỉ để biết): nhiều máy Zebra nhận ZPL thô qua TCP cổng 9100. Vì vậy bạn có thể thay `LabelaryPreviewClient` bằng `ZebraTcpPrinter` mà không đổi phần sinh ZPL: tách interface `ILabelPreview` và `ILabelPrinter`.

**`LabelPrintLog`**: mỗi lần in một serial ghi một dòng: `SerialNo`, `PrintedAt` (UTC), `PrintedBy`, `PrintNo` (lần in thứ mấy; lần > 1 là reprint), `Reason` (bắt buộc khi reprint). Tại thời điểm Release chưa in gì; in là hành động riêng sau Release.

**Bẫy thường gặp.**
- Labelary là dịch vụ ngoài: **giới hạn tốc độ và số nhãn mỗi lần gọi**; có thể lỗi hoặc chậm. Dùng nó để xem **một** nhãn mẫu, không dùng gọi 200 lần cho 200 serial. Tránh gửi dữ liệu thật của khách vào dịch vụ công cộng.
- Preview đẹp không có nghĩa là in đẹp: khác biệt độ tương phản, font, độ dày mực. Với mã 2D, nhà máy thật dùng verifier.
- DataMatrix cần máy quét imager 2D; đã nêu ở 1.2h.
- Font ZPL mặc định không hỗ trợ tiếng Việt/Nhật; nhãn chỉ nên chứa ASCII. Nếu buộc phải in ký tự Unicode, cần cấu hình encoding (`^CI`) và font phù hợp, đọc tài liệu Zebra.
- Chuỗi C# interpolated raw string (`"""`) cần C# 11 trở lên: dự án dùng .NET 10 nên có sẵn; nhưng chú ý dấu xuống dòng, ZPL chấp nhận khoảng trắng/xuống dòng giữa các lệnh.

### 2.6 WPF: điều hướng giữa các view và CanExecute theo trạng thái

Nền WPF/MVVM/CommunityToolkit và `AsyncRelayCommand` đã học ở M0, M1 (xem Module 0, mục WPF; Module 1, mục ICommand/RelayCommand). Ở đây chỉ thêm hai thứ mới: điều hướng và bật/tắt nút theo state machine.

**a) Navigation giữa các view.**

*Vấn đề*: Back-office có Danh sách WO → Chi tiết WO → Preview nhãn. Cách ngây thơ: `new WorkOrderDetailWindow().Show()` từ code-behind: mở cả đống cửa sổ, ViewModel trực tiếp biết View, khó test.

*Khái niệm*: **ViewModel-first navigation**. Cửa sổ chính có một `ContentControl` gắn với property `CurrentViewModel`. Mỗi ViewModel có `DataTemplate` ánh xạ sang View tương ứng. Đổi trang = gán `CurrentViewModel` mới. Một `INavigationService` (DI) làm việc đó để ViewModel không biết View.

```xml
<!-- MainWindow.xaml: ánh xạ ViewModel -> View, rồi 1 chỗ hiển thị -->
<Window.Resources>
  <DataTemplate DataType="{x:Type vm:WorkOrderListViewModel}"><views:WorkOrderListView/></DataTemplate>
  <DataTemplate DataType="{x:Type vm:WorkOrderDetailViewModel}"><views:WorkOrderDetailView/></DataTemplate>
</Window.Resources>
<ContentControl Content="{Binding Navigation.Current}"/>   <!-- đổi Current là đổi màn hình -->
```

```csharp
public interface INavigationService { ObservableObject? Current { get; } Task NavigateToAsync<T>(object? arg = null) where T : ObservableObject; }

public partial class NavigationService(IServiceProvider sp) : ObservableObject, INavigationService
{
    [ObservableProperty] private ObservableObject? _current;         // CommunityToolkit sinh property Current + INotifyPropertyChanged

    public async Task NavigateToAsync<T>(object? arg = null) where T : ObservableObject
    {
        var vm = sp.GetRequiredService<T>();                          // lấy ViewModel từ DI (M0: Generic Host)
        if (vm is INavigationAware aware) await aware.OnNavigatedToAsync(arg);   // interface tự định nghĩa: truyền WoId, nạp dữ liệu
        Current = vm;
    }
}
```

**b) Command CanExecute theo trạng thái WO.**

*Vấn đề*: Nút Release chỉ được bật khi WO là Draft; Hold khi InProgress... Cách ngây thơ: viết `if (Status == Draft)` ở nhiều nơi trong code-behind, và **lặp lại luật** đã có ở Domain: hai nơi lệch nhau thì UI cho bấm nhưng server từ chối, hoặc ngược lại.

*Khái niệm*: **server là nguồn sự thật**. `GET /work-orders/{id}` trả thêm `allowedActions: ["Release","Cancel"]` (tính từ `AllowedActions` của entity). ViewModel chỉ hỏi "action này có trong danh sách không". Khi trạng thái đổi, ViewModel nạp lại chi tiết và báo cho command đánh giá lại `CanExecute`.

```csharp
public partial class WorkOrderDetailViewModel(IApiClient api, INavigationService nav) : ObservableObject, INavigationAware
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReleaseCommand))]      // Detail đổi -> Release đánh giá lại CanExecute
    [NotifyCanExecuteChangedFor(nameof(HoldCommand))]
    private WorkOrderDetailDto? _detail;

    private bool Can(string action) => Detail?.AllowedActions.Contains(action) == true;
    private bool CanRelease() => Can("Release");
    private bool CanHold() => Can("Hold");

    [RelayCommand(CanExecute = nameof(CanRelease))]           // async command tự tắt nút khi đang chạy (mặc định không chạy đồng thời)
    private async Task ReleaseAsync()
    {
        try { await api.ReleaseWorkOrderAsync(Detail!.Id, Detail.RowVersion); }
        catch (ConflictException) { /* hiện thông báo tiếng Anh từ Strings.resx: "changed by another user" */ }
        await LoadAsync(Detail!.Id);                          // luôn nạp lại để lấy trạng thái + RowVersion mới
    }

    [RelayCommand(CanExecute = nameof(CanHold))] private async Task HoldAsync() { /* hỏi lý do rồi gọi API */ }
}
```

**Bẫy thường gặp.**
- Quên `NotifyCanExecuteChangedFor`: nút không bật/tắt khi `Detail` đổi; lỗi này rất hay gặp.
- Gọi API xong không nạp lại: UI dùng `RowVersion` cũ, lần bấm sau bị 409 oan.
- Cập nhật property từ thread nền (không phải UI thread): xử lý bằng `await` tiếp diễn trên UI thread; tránh `Task.Run` bao cả lệnh gọi API rồi gán property trong đó. (Dispatcher sẽ học kỹ ở M4.)
- Chuỗi trong thông báo, tiêu đề nút, tên trạng thái: đều lấy từ `Strings.resx` (quyết định chung của dự án), không hard-code trong XAML/C#.
- `NavigateToAsync` truyền tham số kiểu `object`: dễ sai kiểu ở runtime; với dự án nhỏ chấp nhận, ghi chú trong code.

### Sơ đồ luồng dữ liệu của module (use case Release + in nhãn)

```
[WPF BackOffice]                                                        [SQL Server]
 WorkOrderDetailView ──(Button Release)── WorkOrderDetailViewModel
        │ ReleaseCommand (CanExecute từ allowedActions)
        ▼
 MiniMes.Client.Core: ApiClient (JWT)  ── POST /work-orders/{id}/release {rowVersion} ──┐
                                                                                        ▼
 MiniMes.Api:  WorkOrdersController ── [Authorize(Roles="Planner,Admin")]
        │  Send(ReleaseWorkOrderCommand)  (MediatR)
        ▼
 MiniMes.Application: ValidationBehavior (FluentValidation) ─► ReleaseWorkOrderHandler
        │  1. nạp WorkOrder (+Operations)                          ───────────────────►  SELECT WorkOrder
        │  2. đọc BOM/Routing Approved hiệu lực (interface M1)      ───────────────────►  SELECT Bom/Routing
        │  3. wo.Release(...)  ──► MiniMes.Domain: state machine + snapshot
        │  4. ReserveSerialNumbers (NEXT VALUE FOR SerialSeq)      ───────────────────►  SEQUENCE
        │  5. SerialFormat.Build ×200 → Serial.Create               (Domain, hàm thuần)
        │  6. SaveChanges (1 transaction; audit interceptor M0 ghi AuditLog)
        ▼                                                          ───────────────────►  UPDATE WO (kèm RowVersion)
   DbUpdateConcurrencyException?  ─► ConflictException ─► 409 ProblemDetails            INSERT Material/Operation/Serial
        │ OK → 200 + WorkOrderDetailDto(status=Released, allowedActions, rowVersion mới)
        ▼
 WPF: nạp lại chi tiết → CanExecute đánh giá lại (Release tắt, Print Labels bật)

 In nhãn:  POST /work-orders/{id}/labels/preview?serial=...  ► ZplLabelBuilder ► LabelaryPreviewClient ► PNG ► WPF Image
           POST /work-orders/{id}/labels/print               ► ZplLabelBuilder ×N ► (ILabelPrinter) + INSERT LabelPrintLog
```

Không có MQTT/SignalR trong M2. (Nếu muốn danh sách WO tự cập nhật khi Supervisor Hold ở máy khác, làm ở M4 khi đã học SignalR.)

---

## 3. Kế hoạch thực hiện

Tổng ~27 giờ (**[v2]** bỏ Bước 9 Gantt; +1 giờ cho Priority, dispatch list và FAI). Làm theo thứ tự; mỗi bước có cách kiểm tra riêng để bạn biết đã xong chưa. Đường dẫn giả định theo solution ở spec mục 2; nếu M1 của bạn đặt thư mục khác (ví dụ `Planning` viết khác), theo pattern của M1.

- [x] **Bước 1 — Domain: WorkOrder, Serial và state machine**
  - Mục tiêu: entity có luật chuyển trạng thái, test đủ ma trận.
  - Việc cần làm: enum `WorkOrderStatus`; entity `WorkOrder`, `WorkOrderMaterial`, `WorkOrderOperation`, `Serial` (status Created/InProcess/Ng/Repair/Completed/Scrapped/Hold); method `Release`, `Start`, `Hold`, `Resume`, `Complete`, `Close`, `Cancel`; **[v2]** `ChangePriority`, `AssignFaiSerial`, `RecordFaiResult`, enum `FaiStatus`; thuộc tính `AllowedActions`; `HasOutput`; `DomainException` (nếu M0/M1 chưa có).
  - File: `src/MiniMes.Domain/Planning/WorkOrder.cs`, `WorkOrderMaterial.cs`, `WorkOrderOperation.cs`, `Serial.cs`, `WorkOrderStatus.cs`; `tests/MiniMes.UnitTests/Planning/WorkOrderStateMachineTests.cs`.
  - Kiểm tra: `dotnet test` xanh; ma trận (trạng thái × hành động) có ít nhất một case cho mỗi ô; case `Cancel` khi `HasOutput` ném lỗi; case `Complete` thiếu lý do thủ công khi QtyGood chưa đủ ném lỗi; **[v2]** Release với `RequireFai=true` cho `Pending`, `false` cho `NotRequired`; `AssignFaiSerial` hai lần giữ serial đầu; `RecordFaiResult(false)` xóa `FaiSerialNo` và giữ `Pending`; `ChangePriority(0)` và `ChangePriority` khi Closed ném lỗi.
  - Ước lượng: 3,5 giờ.
  - **Ghi chú khi làm (17/10/2026).** `Domain/Planning/`: `WorkOrder` (state machine bằng method + `Ensure`, `AllowedActions`, `HasOutput`, `QtyGoodLastStep`), `WorkOrderMaterial`, `WorkOrderOperation`, `Serial` + `SerialStatus`, `WorkOrderStatus`, `WorkOrderAction`, `FaiStatus`. Khác snippet (user đã duyệt 17/10/2026): (1) `PlannedQty` là `int` (mỗi đơn vị một serial; WO chỉ cho Semi/Finished, đơn vị `pcs`); (2) input snapshot NVL là `MaterialSnapshot(ComponentItemId, OperationSeq, QtyPerUnit)` với `QtyPerUnit` = lượng cho **1 thành phẩm** sau explode đa cấp (đã gồm scrap mọi cấp), `RequiredQty = PlannedQty × QtyPerUnit` giữ số chính xác, làm tròn khi hiển thị theo ADR-005 (snippet `QtyPer × (1 + ScrapRate)` chỉ đúng với BOM 1 cấp); `BomVersion` là version BOM của Item WO, version BOM Semi suy ra từ `ReleasedAt`; (3) `FaiStatus` nullable: Draft = `null` (chưa quyết), Release mới đặt; (4) thêm `StartedAt`, `CompletedAt`, `CompleteReason`, `CancelReason`, `Serial.ScrapReason` (R8 cần lý do); (5) `Release` còn chặn Seq trùng và NVL tiêu thụ ở bước không có trong routing; (6) `Materials`/`Operations` là `List` property như `Bom.Lines` của M1; `Serial` không audit (bảng lớn, M3 cập nhật liên tục). `WorkOrderOperation.RecordOutput` là `internal` (+ `InternalsVisibleTo` cho UnitTests) để dựng trạng thái có sản lượng; M3 tăng bộ đếm bằng `ExecuteUpdate`. Kiểm tra: `WorkOrderStateMachineTests` 91 test: ma trận đủ 7 × 9 = 63 ô (bảng chuyển viết tay, không suy từ `AllowedActions`; mỗi ô còn kiểm `AllowedActions` khớp với method), Cancel khi có sản lượng (cả chỉ NG), Complete thiếu lý do khi 197/200, sản lượng bước giữa không tính, snapshot (RequiredQty 2472,48, sắp Seq, SopUrl), FAI (Pending/NotRequired, gán 2 lần giữ serial đầu, fail xóa mẫu giữ Pending, Passed khóa cổng), Priority 0/10 và khi Closed. Mutation: bỏ chặn `HasOutput`, cho Hold từ Released, ghi đè `FaiSerialNo`, đếm bước đầu thay bước cuối → đều đỏ.

- [x] **Bước 2 — EF Core configuration, migration, sequence**
  - Mục tiêu: DB có bảng WO/Serial, 2 sequence, unique index, rowversion.
  - Việc cần làm: configuration theo snippet 2.2; `HasSequence` cho `SerialSeq`, `WoNoSeq`; enum lưu chuỗi; navigation dùng backing field; unique index `WoNo`, `Serial.SerialNo`; index `(WoId, Status)`; **[v2]** index `(LineId, Status, Priority, PlannedStart)` cho dispatch list; cột `WorkOrderOperation.SopUrl`, `IsInspection`; migration; đảm bảo bảng `WorkOrder` nằm trong danh sách được audit interceptor (M0) theo dõi.
  - File: `src/MiniMes.Infrastructure/Persistence/Configurations/PlanningConfigurations.cs`, thư mục `Migrations/`.
  - Kiểm tra: `dotnet ef database update` chạy; mở SSMS/Azure Data Studio thấy `SELECT NEXT VALUE FOR dbo.SerialSeq` trả 1, 2, 3; thử insert hai serial trùng, DB báo vi phạm unique index.
  - Ước lượng: 2 giờ.
  - **Ghi chú khi làm (17/10/2026).** `Configurations/PlanningConfigurations.cs` (4 bảng `WorkOrders`, `WorkOrderMaterials`, `WorkOrderOperations`, `Serials`), migration `M2_WorkOrders`. Sequence `WoNoSeq`, `SerialSeq` (bigint, từ 1) khai báo ở `AppDbContext.OnModelCreating` (cấu hình mức model, không thuộc entity nào). Unique: `WoNo`, `Serial.SerialNo`, `(WorkOrderId, Seq)` của operation, `(WorkOrderId, ComponentItemId, OperationSeq)` của material; index `(WorkOrderId, Status)` cho serial, **[v2]** `(LineId, Status, Priority, PlannedStart)` cho dispatch list. `RowVersion` là `rowversion`. Thêm CHECK: `PlannedQty > 0`, `PlannedEnd > PlannedStart`, `Priority BETWEEN 1 AND 9`, `IdealCycleTimeSec > 0`, bộ đếm `QtyGood/QtyNg >= 0`, `QtyPerUnit > 0`. Số lượng NVL `decimal(28,9)` như CTE explode của M1 (snippet 2.3 gợi ý `decimal(18,4)` sẽ làm tròn 0,12726 kg thành 0,1273). FK `Serial → WorkOrder` là **Restrict** (serial là lịch sử truy xuất, không xóa theo WO); material/operation cascade theo WO. `WorkOrder`/material/operation có `IAudited` nên interceptor M0 tự ghi audit; `Serial` không. **Bẫy gặp khi viết test:** `SqlQueryRaw("SELECT NEXT VALUE FOR ...").SingleAsync()` lỗi "NEXT VALUE FOR function is not allowed in ... sub-queries" vì EF bọc câu SQL vào subquery khi ghép thêm LINQ; phải `ToListAsync()` rồi lấy trong bộ nhớ (Bước 4 xin dải số cũng phải tránh ghép LINQ). Kiểm tra: `dotnet ef database update` trên DB dev; `sqlcmd` trong container: `NEXT VALUE FOR dbo.SerialSeq` trả 1, 2, 3; integration `PlanningSchemaTests` 8 test: sequence cấp số liên tiếp, serial trùng và `WoNo` trùng bị unique index từ chối (→ `ConflictException`, không dòng nào được ghi), hai người cùng sửa một WO thì người lưu sau nhận `DbUpdateConcurrencyException` và giá trị của người trước được giữ, 3 CHECK constraint từ chối giá trị ghi thẳng bằng SQL (lỗi 547), tạo WO ghi `AuditLog` (`WorkOrders`, `*`).

- [x] **Bước 3 — Use case Create WO + preview**
  - Mục tiêu: Planner chọn Item + Qty, thấy trước NVL và công đoạn, rồi lưu Draft.
  - Việc cần làm: `PreviewWorkOrderQuery` (gọi explode BOM của M1, nhân với qty và scrap rate; lấy Routing Approved); `CreateWorkOrderCommand` + FluentValidation (qty > 0, ngày kết thúc sau ngày bắt đầu, line và item tồn tại và active, **[v2]** `Priority` 1–9, `RequireFai`); sinh `WoNo` từ `WoNoSeq` (`WO-yyMM-#####`); Draft chưa chụp version; API `POST /work-orders/preview`, `POST /work-orders`, `GET /work-orders`, `GET /work-orders/{id}`, `PUT /work-orders/{id}` (chỉ Draft).
  - File: `src/MiniMes.Application/Planning/CreateWorkOrder/*`, `PreviewWorkOrder/*`; `src/MiniMes.Contracts/Planning/*`; endpoint WO trong `MiniMes.Api` theo pattern M1.
  - Kiểm tra: gọi preview cho `PCB-A` 200 con, số NVL khớp tính tay (ví dụ `R-10K`: 4.896 nếu dùng số ở 1.2f, hoặc số trong seed M1 của bạn); tạo WO ra Draft; sửa qty khi Draft OK; validation chặn qty = 0.
  - Ước lượng: 3 giờ.
  - **Ghi chú khi làm (17/10/2026).** `Application/Planning/WorkOrderUseCases.cs` (Preview, Create, Update, GetById, List) + `WorkOrderQueries.cs`, `Contracts/Planning/WorkOrderDtos.cs`, `Api/Controllers/WorkOrdersController.cs` (đọc: mọi role; preview/create/update: Planner, Admin). **Đọc BOM/Routing:** `LoadRecipeAsync` dùng `IAppDbContext` ngay trong Application (nạp mọi BOM hiệu lực bằng một query rồi explode trong bộ nhớ, như `bom-tree` của M1), không qua interface Infrastructure như gợi ý Bước 4, vì không cần SQL đặc thù; snapshot cần NVL **theo từng bước tiêu thụ** mà CTE (tổng theo Item) không giữ. Thêm `BomExplosion.LeafMaterials` (Domain): chỉ lá (NVL mua), gom theo (Item, OperationSeq); Semi `PCBA-SMT` không phải NVL cấp phát. Explode cho **1 đơn vị**, Release nhân `PlannedQty`. **Preview = Release thử** trên một WO tạm không lưu: mọi luật Release (NVL ở bước không có trong routing, Seq trùng) hiện ra ngay ở preview, một nguồn luật. Thiếu BOM/routing Approved → 422 nêu tên. **Create:** Draft không kiểm tra BOM/routing (cổng là Release, R1); Item phải tồn tại, active, không phải Raw, Line active → 400 trên ô `ItemId`/`LineId`; `PlannedQty` 1–10.000 (Release sinh mọi serial trong một transaction), `Priority` 1–9, `PlannedEnd > PlannedStart`; giờ không có `Z` coi là UTC. `WoNo = WO-yyMM-#####` (`WorkOrderNumber`, Domain) từ `WoNoSeq` qua `ISequenceNumbers` (Infrastructure, `SqlQueryRaw` + `ToListAsync`), tháng theo **giờ nhà máy**: cấu hình mới `Plant:TimeZone = Asia/Ho_Chi_Minh` → `PlantTimeZone` (Bước 4 dùng cho ngày trong serial). **Update:** chỉ Draft (Domain → 422), client gửi `RowVersion` (base64) đã nạp, handler gán làm `OriginalValue` qua `IAppDbContext.Entry` (thêm vào interface). `DbUpdateConcurrencyException` đổi thành `ConflictException` (409) **một chỗ** trong `AppDbContext.SaveChangesAsync`, cạnh lỗi unique index (thay vì từng handler bắt như snippet 2.2). **List:** lọc `status`, `lineId`, `search` (WoNo/mã Item), sắp Priority → PlannedStart, cột `QtyGoodLastStep`. Kiểm tra: unit 7 test (`LeafMaterials`: 8 NVL không có Semi, theo bước, per-unit × 200 = explode 200, cùng NVL ở 2 bước tách dòng / cùng bước ở 2 nhánh cộng lại; `WorkOrderNumber`), sửa số liệu test Bước 1 cho đúng 12 × 1,02 × 1,01 = 12,3624; integration `WorkOrderApiTests` 20 test (preview PCB-A × 200 khớp tính tay 204/2473/4121/204/0,12726 kg/201/201/200, đối chiếu với explode CTE; thiếu routing → 422; Draft có `WoNo`, chưa snapshot, `allowedActions`; số tăng dần; 6 lỗi 400 đúng ô; Raw → 400; QC/Supervisor 403, Operator đọc được; sửa Draft; RowVersion cũ → 409 và giá trị trước được giữ; RowVersion sai → 400; sửa sau Draft → 422; lọc/sắp danh sách). Mutation: bỏ gán `OriginalValue`, bỏ chặn Raw → test đỏ. API dev: preview đúng bảng, tạo `WO-2610-00001` Draft.

- [x] **Bước 4 — Use case Release (snapshot + serial + audit)**
  - Mục tiêu: một request Release làm toàn bộ việc trong một transaction.
  - Việc cần làm: `ReleaseWorkOrderCommand` + handler theo luồng ở sơ đồ; interface đọc BOM/Routing Approved hiệu lực (do Application định nghĩa, Infrastructure hiện thực bằng query M1); `SerialFormat`; `ReserveSerialNumbersAsync`; gán `OriginalValue` cho `RowVersion`; map `DbUpdateConcurrencyException` thành 409; audit ghi chuyển trạng thái (kiểm tra interceptor M0 thấy được).
  - File: `src/MiniMes.Application/Planning/ReleaseWorkOrder/*`, `src/MiniMes.Domain/Planning/SerialFormat.cs`, `src/MiniMes.Infrastructure/Planning/SerialSequenceService.cs`; `tests/MiniMes.UnitTests/Planning/SerialFormatTests.cs`.
  - Kiểm tra: Release WO 200 pcs: bảng `Serial` có đúng 200 dòng duy nhất, `WorkOrderMaterial` và `WorkOrderOperation` có số dòng bằng BOM/Routing; `BomVersion`/`RoutingVersion` đúng; **[v2]** `WorkOrderOperation.SopUrl` chép đúng, `FaiStatus = Pending`; `AuditLog` có dòng chuyển Draft → Released; BOM chưa Approved thì Release bị chặn; sau Release, **đổi BOM ở M1 (tạo version mới và approve)** rồi mở WO: số liệu không đổi.
  - Ước lượng: 4 giờ.
  - **Ghi chú khi làm (17/10/2026).** `Application/Planning/ReleaseWorkOrder.cs` (`ReleaseWorkOrderCommand` + `GetWorkOrderSerialsQuery`), `Domain/Planning/SerialFormat.cs`, `ISequenceNumbers.NextSerialNumbersAsync` (Infrastructure `SequenceNumbers`, tên khác `SerialSequenceService` của spec vì dùng chung cho cả `WoNoSeq`); API `POST /api/work-orders/{id}/release` (body `WorkOrderTransitionRequest { rowVersion, reason? }`, dùng lại cho các chuyển trạng thái ở Bước 7; Planner, Admin) và `GET /api/work-orders/{id}/serials` (phân trang, lọc trạng thái, sắp theo SerialNo = thứ tự sequence). Luồng handler: nạp WO (+Operations) → gán `RowVersion` client gửi làm `OriginalValue` → Item và Line còn active (422) → `LoadRecipeAsync` (BOM + routing Approved hiệu lực lúc này, Bước 3) → `wo.Release(...)` → **chỉ sau khi mọi kiểm tra qua** mới xin dải số (không phí số khi bị từ chối) → `Serial` chỉ khi `Item.IsSerialized` (Semi theo serial của thành phẩm nên không có serial riêng) → **một** `SaveChanges`. Xin 200 số bằng **một** câu `NEXT VALUE FOR ... OVER (ORDER BY rn)` (snippet 2.4 chạy được trên SQL Server 2022, không cần `sp_sequence_get_range`); `ToListAsync` để EF không bọc subquery. Ngày trong serial theo `PlantTimeZone`. `SerialFormat`: ví dụ spec `PCBA2610010001232` đúng (đã tính tay check digit = 2), thêm `HasValidCheckDigit` cho M3 kiểm tra serial gõ tay trước khi hỏi DB. **Hai tầng chống Release hai lần:** request đến **sau** khi request đầu đã commit bị `Release()` chặn (422, dù mang RowVersion nào); hai request **chồng lấn** thì RowVersion cho request sau 409 (test ở Bước 5). Kiểm tra: unit `SerialFormatTests` 12 (ví dụ spec, seq 0/-1/1.000.000 bị từ chối, model 4 ký tự, check digit bắt gõ sai/đảo số), `PlantTimeZoneTests` 3 (17:30 UTC = 00:30 ngày hôm sau ở VN); integration `ReleaseWorkOrderTests` 11: PCB-A × 200 → 200 serial duy nhất, liên tiếp trong một lần xin, đúng check digit, 8 dòng NVL, 4 công đoạn, `SopUrl`, `FaiStatus = Pending`, `allowedActions`; ngày serial = ngày VN; `AuditLog` có `Status Draft → Released` bởi `planner01`; `RequireFai=false` → `NotRequired`; **approve BOM v2 (20 → 22 tụ) và routing v2 sau Release: WO giữ nguyên v1 và số cũ, preview WO mới thấy v2**; BOM chỉ có Draft → 422, WO vẫn Draft, 0 serial; Release lần hai → 422, vẫn đúng số serial; Semi → Released, 0 serial; QC/Supervisor 403; danh sách serial phân trang. API dev: `WO-2610-00001` Released, 200 serial, bắt đầu từ số 4 (số 1–3 đã dùng khi thử sequence ở Bước 2: có lỗ hổng, không trùng), Release lại → 422.

- [x] **Bước 5 — Test đồng thời (bằng chứng cho DoD)**
  - Mục tiêu: chứng minh không trùng serial và không release hai lần.
  - Việc cần làm: test Testcontainers (tái dùng fixture M0); test hai Release cùng lúc trên cùng WO; test hai WO khác nhau Release cùng lúc (serial của hai WO không giao nhau); test unique index từ chối trùng khi cố tình insert.
  - File: `tests/MiniMes.IntegrationTests/Planning/ReleaseConcurrencyTests.cs`.
  - Kiểm tra: chạy test 20 lần liên tiếp đều xanh (`dotnet test --filter ...` trong vòng lặp shell); tạm thời bỏ unique index, thay sequence bằng `MAX+1` để thấy test **đỏ** (chứng minh test có tác dụng), rồi hoàn lại.
  - Ước lượng: 2,5 giờ.
  - **Ghi chú khi làm (17/10/2026).** `ReleaseConcurrencyTests` 3 test, mỗi request là một HTTP call thật (client, scope, DbContext riêng), hai request chờ chung một "cổng" (`TaskCompletionSource`) rồi chạy cùng lúc: (1) 2 Release cùng một WO, lặp 5 vòng: đúng một 200, bên kia 409/422, đúng 200 serial không trùng, 8 dòng vật tư; (2) 2 WO khác nhau Release cùng lúc: cả hai 200, hai tập serial không giao nhau (400 serial khác nhau); (3) nhánh thua làm cho xác định: RowVersion cũ hơn DB → 409, rollback toàn bộ (vẫn Draft, 0 vật tư/công đoạn/serial). Unique index từ chối serial trùng đã có ở `PlanningSchemaTests` (Bước 2). **Phát hiện:** in mã trạng thái từng vòng thì bên thua luôn 409, nhưng đọc inner exception thấy 409 đến từ **unique index của `WorkOrderMaterials` (lỗi 2601)**, không phải RowVersion: trong một `SaveChanges`, EF gửi INSERT bảng con trước UPDATE `WorkOrders`. Kết quả vẫn đúng vì mọi lớp (index vật tư, index công đoạn, RowVersion) nằm chung một transaction; chỉ sửa thông báo 409 của lỗi unique cho trung tính. Ghi ở `docs/incidents.md`. Kiểm tra: chạy 20 lần liên tiếp đều xanh (100 vòng tranh cùng WO + 20 lần hai WO); mutation `MAX+1` (giữ index) → test hai WO đỏ (một bên 409 vô lý); `MAX+1` + bỏ unique index `SerialNo` → đỏ vì serial trùng được lưu thật; đã hoàn lại.

- [x] **Bước 6 — Nhãn: ZPL, Labelary, LabelPrintLog**
  - Mục tiêu: sinh ZPL cho từng serial, xem trước một nhãn, ghi log in.
  - Việc cần làm: `ZplLabelBuilder` + unit test (chuỗi chứa serial, Code128 và DataMatrix, chặn ký tự `^`/`~`); `ILabelPreview` + `LabelaryPreviewClient` (đăng ký bằng `AddHttpClient`); entity `LabelPrintLog` + migration; API `POST /work-orders/{id}/labels/preview` (một serial), `POST /work-orders/{id}/labels/print` (ghi log, trả về chuỗi ZPL hoặc file `.zpl`); reprint yêu cầu `Reason`, tăng `PrintNo`; chỉ cho in khi WO ở Released/InProgress.
  - File: `src/MiniMes.Application/Labels/*`, `src/MiniMes.Infrastructure/Labels/LabelaryPreviewClient.cs`, `LabelPrintLog.cs` trong Domain.
  - Kiểm tra: dán chuỗi ZPL vào trình xem trực tuyến của Labelary (hoặc gọi API) thấy vạch Code128 và ô DataMatrix, không tràn mép nhãn; in hai lần cùng serial cho `PrintNo = 1, 2`; in WO Draft bị chặn.
  - Ước lượng: 3,5 giờ.
  - **Ghi chú khi làm (17/10/2026).** `Application/Labels/` (`ZplLabelBuilder`, `ILabelPreview`, `LabelUseCases`: `PreviewLabelQuery`, `PrintLabelsCommand`), `Domain/Planning/LabelPrintLog.cs` (SerialId, SerialNo, WorkOrderId, PrintNo, PrintedAt UTC, PrintedBy, Reason; không audit vì là log chỉ INSERT), `Infrastructure/Labels/LabelaryPreviewClient.cs`, migration `M2_LabelPrintLog` (unique `(SerialId, PrintNo)`: hai lần in lại cùng lúc không thể cùng số → 409). `HttpClient` có kiểu đăng ký bằng `AddHttpClient` ở **Api** (IHttpClientFactory có sẵn trong ASP.NET Core) thay vì thêm gói `Microsoft.Extensions.Http` vào Infrastructure. API: `POST /api/work-orders/{id}/labels/preview` (`{serialNo?}`, mặc định serial đầu; trả `image/png`; không ghi log), `POST /api/work-orders/{id}/labels/print` (`{serialNos?, reason?}`; mặc định mọi serial trừ Scrapped; trả `{printed, reprinted, zpl}`); quyền Planner, **Supervisor** (in lại tem hỏng ngoài line), Admin. Domain: `WorkOrder.EnsureCanPrintLabels()` (Released/InProgress) + hành động `PrintLabels` trong `AllowedActions` (ma trận Bước 1 thêm cột này); `LabelPrintLog` chặn in lại không lý do và serial Scrapped. ZPL chỉ nhận ASCII in được, không `^`/`~`. **Đối chiếu snippet với dịch vụ thật:** (1) snippet 3 gửi `StringContent(zpl)` = `text/plain` → Labelary trả **415**; phải khai `application/x-www-form-urlencoded` (như `curl --data` trong tài liệu Labelary), thân vẫn là ZPL thô; dùng `https`; (2) Code128 dùng `^BCN,60,Y,N,N,A` (mode A tự chọn subset C cho cặp số) và DataMatrix `^BXN,4,200` tại `^FO380,140`: preview thật cho thấy vạch kết thúc ~x 440/480, còn ~5 mm lề phải, DataMatrix không đè vạch (ảnh `docs/images/m2-label-preview.png`). Integration test không gọi Labelary: `ApiFactory` thay `ILabelPreview` bằng `FakeLabelPreview`. Kiểm tra: unit `LabelTests` 9 (ZPL có `^PW480`/`^LL240`, serial trong trường `^BC` và `^BX`, chặn `^`, `~`, tiếng Việt, xuống dòng; in lần đầu không cần lý do, in lại thiếu lý do và PrintNo 0 bị chặn); integration `LabelApiTests` 8 (preview PNG từ serial đầu, không ghi log; serial của WO khác → 400; in tất cả → mỗi nhãn một dòng log PrintNo 1 và đủ ZPL; in lại không lý do → 422 không ghi gì, có lý do → PrintNo 2 do `sup01` kèm lý do; WO Draft → 422; danh sách có serial lạ → 400 không ghi gì; QC/Maintenance 403). API dev: preview thật qua Labelary 200 `image/png`; in 200 nhãn → 200 dòng log; in lại `PCBA2610170000049` không lý do 422, có lý do → PrintNo 2.

- [ ] **Bước 7 — Các chuyển trạng thái còn lại + quyền**
  - Mục tiêu: Hold/Resume/Complete/Close/Cancel qua API, đúng quyền, đúng luật.
  - Việc cần làm: command tương ứng; `Complete` tự động khi QtyGood bước cuối ≥ PlannedQty (hàm nhận số liệu, M3 sẽ gọi; ở M2 kiểm tra bằng cách tự sửa số liệu trong test); `Complete` thủ công yêu cầu lý do; `Cancel` cập nhật serial `Created` thành `Scrapped` bằng `ExecuteUpdateAsync` trong transaction; `Start()` để sẵn cho M3 gọi; **[v2]** `PUT /work-orders/{id}/priority` (Planner, Supervisor) và `GET /lines/{id}/dispatch-list` (mọi role, Operator dùng ở kiosk) trả `WoNo, Item, PlannedQty, QtyGoodLastStep, Priority, PlannedStart, Status, FaiStatus`; role: Planner (create/release/cancel), Supervisor (hold/resume/complete/close), Admin (tất cả).
  - Kiểm tra: mỗi API trả 403 với role sai, 422 với trạng thái sai (`DomainException`), 409 khi xung đột `RowVersion`, ProblemDetails rõ ràng; Cancel WO Released làm 200 serial thành Scrapped; Cancel WO có sản lượng bị từ chối; **[v2]** 3 WO trên Line 1 (Priority 5, 5, 1) → dispatch list trả WO Priority 1 đầu tiên, hai WO còn lại theo `PlannedStart`; WO Closed/Cancelled không xuất hiện.
  - Ước lượng: 2,5 giờ.

- [ ] **Bước 8 — WPF Back-office: danh sách và chi tiết WO**
  - Mục tiêu: dùng được từ UI.
  - Việc cần làm: `INavigationService` + `MainViewModel`; `WorkOrderListView` (DataGrid, lọc trạng thái/line, sắp theo **[v2]** `Priority` rồi `PlannedStart`, cột `FaiStatus`, nút đổi Priority); `WorkOrderDetailView` (thông tin, tab NVL, tab công đoạn có QtyGood/QtyNg, tab Serial phân trang nhẹ); nút Release/Hold/Resume/Complete/Close/Cancel bật tắt theo `allowedActions`; hộp thoại nhập lý do; nút Preview Label hiển thị PNG; form tạo WO với preview NVL; xử lý 409; toàn bộ chuỗi trong `Strings.resx`.
  - File: `src/MiniMes.BackOffice.Wpf/Views/Planning/*`, `ViewModels/Planning/*`, `Services/NavigationService.cs`; `src/MiniMes.Client.Core/Api/WorkOrderApi.cs`; `Strings.resx`.
  - Kiểm tra: kịch bản demo bước 1–2 chạy hoàn toàn từ UI: tạo WO 200 PCB-A Line 1, Release, thấy 200 serial, xem trước nhãn; mở hai cửa sổ app, Release cùng lúc: một bên thành công, bên kia hiện thông báo xung đột.
  - Ước lượng: 6 giờ.

- ~~Bước 9 (tùy chọn) — Gantt đơn giản theo line~~ **[v2] Bỏ** (spec v2 mục A2 #5). Danh sách điều độ ở Bước 7 thay thế.

### Nếu bị kẹt

- **`DbUpdateConcurrencyException` xảy ra ngay cả khi chỉ có một người dùng.** Kiểm tra bạn có `SaveChanges` hai lần trong một request, hoặc có gán `OriginalValue` bằng `RowVersion` của DTO cũ (client chưa nạp lại sau lần thao tác trước). Bật log EF (`Microsoft.EntityFrameworkCore.Database.Command` ở mức Information) để xem câu `UPDATE ... WHERE ... RowVersion = ...` và `@p` nào.
- **Vẫn sinh trùng serial trong test.** Test có thực sự dùng hai DbContext/scope riêng không? Nếu dùng cùng một context, hai task chạy tuần tự trong logic của EF, không phản ánh thực tế. Kiểm tra `SerialSeq` có `INCREMENT BY 1` và bạn không cache số trong biến static.
- **`SqlQueryRaw<long>` báo lỗi cột.** Đặt tên cột kết quả là `Value` (như snippet). Nếu vẫn lỗi, dùng vòng lặp `NEXT VALUE FOR` từng số để chạy được trước, tối ưu sau.
- **`HasOutput` luôn false.** Quên `Include(x => x.Operations)` khi nạp WO.
- **Nút không bật/tắt.** Thiếu `[NotifyCanExecuteChangedFor]`, hoặc `Detail` được gán từ thread nền.
- **Labelary không trả ảnh.** Đọc mã lỗi HTTP và nội dung phản hồi; kiểm tra URL, kích thước nhãn, giới hạn tốc độ; thử dán ZPL vào trang xem trực tuyến của Labelary để tách lỗi ZPL khỏi lỗi code gọi API. Không có mạng thì bỏ qua preview, kiểm tra chuỗi ZPL bằng unit test.
- **Mã vạch tràn nhãn trong preview.** Giảm `^BY` xuống 1 hoặc tăng chiều rộng nhãn, hoặc dời DataMatrix.

---

## 4. Vì sao chọn giải pháp này

| Quyết định | Đã chọn | Phương án khác đã cân nhắc | Vì sao không chọn | Khi nào nên đổi |
| --- | --- | --- | --- | --- |
| Cấp serial | SEQUENCE + unique index | `MAX+1`; GUID; bảng counter + UPDLOCK | `MAX+1` race; GUID dài, không đọc được, không vừa nhãn nhỏ; counter phải xếp hàng | Khách yêu cầu dải serial liên tục có kiểm soát: counter table (hoặc bảng cấp phát dải riêng theo khách) |
| Chống Release đồng thời | Rowversion (optimistic) + kiểm tra trạng thái trong `Release()` | UPDLOCK trên dòng WO; `sp_getapplock`; khóa phân tán | Release hiếm khi đụng nhau, khóa làm người thứ hai chờ vô ích; khóa phân tán quá nặng cho một DB | Nếu thường xuyên nhiều người thao tác cùng WO và không chấp nhận báo lỗi xung đột: khóa bi quan |
| Cách lưu công thức của WO | Snapshot (chép giá trị) + ghi version | Chỉ lưu `BomId`/version, JOIN khi đọc; event sourcing | Vẫn cần dòng riêng cho `IssuedQty`... và phụ thuộc master; event sourcing quá nặng | Nếu master bảo đảm bất biến tuyệt đối và bạn muốn tiết kiệm dung lượng: chỉ lưu tham chiếu |
| Máy trạng thái | Method trong entity, `Ensure(...)` | Thư viện `Stateless`; State pattern bằng class; workflow engine | 7 trạng thái không đáng thêm thư viện; workflow engine quá nặng | Khi trạng thái nhiều, có guard/hành động phụ phức tạp, hoặc quy trình do người dùng cấu hình |
| Ranh giới aggregate | `Serial` là aggregate riêng, không nằm trong `WorkOrder` | Serial là collection của WO | Nạp 10.000 serial mỗi lần đổi trạng thái | Nếu luật WO cần đọc mọi serial trong một lần và số lượng nhỏ |
| Thời điểm sinh serial | Sinh hết lúc Release | Sinh lúc in nhãn; sinh từng cái khi scan đầu | Release là điểm chốt, in nhãn hoặc scan có thể lặp và trùng | Nếu WO cực lớn (hàng trăm nghìn) hoặc khách cần dải serial cấp phát dần |
| Định dạng serial | Model + ngày (giờ nhà máy) + số SEQUENCE + check digit | Toàn số thuần; GUID; theo chuẩn khách/GS1 | Mã có ý nghĩa vừa đủ giúp đọc bằng mắt; check digit giảm lỗi gõ | Khách bắt buộc định dạng riêng: tách `SerialFormat` để thay |
| Nhãn | ZPL thô + Labelary preview | Driver in Windows (GDI); thư viện barcode local render ảnh; phần mềm BarTender/NiceLabel | Phụ thuộc driver, khó test; phần mềm thương mại ngoài phạm vi | Khi có máy in thật và nhiều mẫu nhãn: thiết kế nhãn bằng phần mềm chuyên dụng; hoặc tự render preview local để khỏi phụ thuộc dịch vụ ngoài |
| Nguồn WO | Planner tạo trong MES, `ExternalRef` nullable, trống | Bảng ánh xạ ngoài riêng; bỏ cột | Giữ chỗ chi phí thấp cho M8 mà không ảnh hưởng luồng | Khi làm M8: có thể cần thêm `Source`, filtered unique index trên `ExternalRef` |
| **[v2]** Điều độ | `Priority` + dispatch list theo line | Gantt kéo thả; APS tự xếp lịch theo năng lực | Gantt chỉ vẽ lại dữ liệu, tốn công WPF; APS là sản phẩm riêng (Preactor, Asprova) | Khi nhiều line, nhiều WO/ngày và cần cân bằng năng lực: tích hợp APS, MES chỉ nhận thứ tự |
| **[v2]** Lưu trạng thái FAI | 3 cột trên `WorkOrder` (`RequireFai`, `FaiStatus`, `FaiSerialNo`), chi tiết kết quả ở `FaiRecord` (M7) | Thêm trạng thái `FaiPending` vào state machine WO | Trộn hai trục độc lập (vòng đời WO và cổng chất lượng) làm ma trận chuyển trạng thái phình to | Khi FAI theo ca/đổi model: chuyển sang bảng `FaiRequirement` (WO, ca, lý do) |
| Bật/tắt nút trên UI | Server trả `allowedActions` | Client tự lặp luật theo Status | Hai nơi giữ luật sẽ lệch nhau | Nếu client offline cần tự quyết: chấp nhận lặp luật và có test đối chiếu |
| Cancel WO có serial | Serial Created → Scrapped, kèm lý do | Thêm trạng thái Voided/Cancelled cho Serial; xóa cứng serial | Không thêm trạng thái ngoài spec; xóa cứng mất dấu vết | Nếu báo cáo scrap bị lẫn serial hủy: thêm trạng thái riêng cho serial |

**Vì sao SEQUENCE + unique index + rowversion, mỗi thứ một việc.** Có ba nguy cơ khác nhau và mỗi công cụ chặn đúng một nguy cơ. SEQUENCE bảo đảm hai request khác nhau luôn nhận **số khác nhau** mà không cần khóa; nó ngoài transaction nên số có thể có lỗ hổng, nhưng serial là định danh nên chấp nhận được. Unique index là **lưới an toàn cuối cùng** ở tầng dữ liệu: nếu code có lỗi, DB vẫn từ chối serial trùng thay vì âm thầm nhận. Rowversion chặn **hai người cùng release một WO**. Khi phỏng vấn, bạn nên nói được rằng test đồng thời không chứng minh tuyệt đối; nó là bằng chứng bổ sung cho các ràng buộc ở DB.

**Vì sao snapshot.** WO là hợp đồng giữa Planner và line: "làm cái này, theo công thức này". Nếu công thức thay đổi sau lưng, hợp đồng vô nghĩa và truy xuất sai. Snapshot chi phí thêm ít dòng dữ liệu nhưng cho ba thứ: WO ổn định, truy xuất biết công thức đã dùng, và nơi để ghi số liệu riêng của WO (đã cấp, đã dùng). Lý do "master có version rồi nên chỉ cần lưu version" đúng một phần: bạn vẫn nên chụp vì cần dòng riêng cho số liệu thực tế và không muốn WO phụ thuộc thay đổi của master hay ERP sau này.

**Vì sao release là cổng.** Release gom mọi kiểm tra "được phép chạy" vào một điểm duy nhất: công thức đã duyệt, serial đã sinh, người chịu trách nhiệm đã ghi lại. M3 chỉ cần hỏi "serial này thuộc WO đã release và đang chạy không?" là chặn được sản xuất ngoài kế hoạch. Nếu để mỗi chỗ tự kiểm tra, sẽ có chỗ quên.

---

## 5. Hoàn thành module

### 5.1 Definition of Done

- [ ] Release WO 200 pcs sinh đúng 200 serial, không trùng; **test đồng thời 2 request Release** chạy xanh (một thành công, một bị từ chối, vẫn chỉ 200 serial).
- [ ] Sửa BOM/Routing ở M1 (version mới, Approved) **sau khi** WO đã Released: mở lại WO, `WorkOrderMaterial`/`WorkOrderOperation`/`BomVersion`/`RoutingVersion` không đổi (có test).
- [ ] Không Release được khi BOM hoặc Routing chưa Approved/hết hiệu lực.
- [ ] Unit test phủ mọi cặp (trạng thái × hành động) của state machine; Cancel khi đã có sản lượng bị từ chối.
- [ ] Unique index `Serial.SerialNo` và `WorkOrder.WoNo` tồn tại trong migration; thử insert trùng bị DB từ chối.
- [ ] Chuyển trạng thái WO ghi `AuditLog` (ai, lúc nào, cũ → mới).
- [ ] `SerialFormat` có unit test (ví dụ `PCBA2610010001232`, seq ngoài dải bị từ chối).
- [ ] ZPL của một serial hiển thị được (Labelary hoặc tương đương) với Code128 và DataMatrix; `LabelPrintLog` ghi mỗi lần in, reprint có `PrintNo > 1` và lý do.
- [ ] WPF Back-office: danh sách WO lọc theo trạng thái/line; chi tiết WO có NVL, công đoạn, tiến độ từng bước; nút Release/Hold/Complete bật/tắt theo `allowedActions`; xung đột 409 hiện thông báo tiếng Anh từ `Strings.resx`.
- [ ] `ExternalRef` tồn tại, nullable, chưa có logic dùng nó.
- [ ] **[v2]** `Priority` đổi được (có audit); `GET /lines/{id}/dispatch-list` sắp đúng theo Priority rồi PlannedStart; không còn Gantt trong phạm vi.
- [ ] **[v2]** Release đặt `FaiStatus` đúng theo `RequireFai`; unit test `AssignFaiSerial`/`RecordFaiResult`; `WorkOrderOperation.SopUrl` được snapshot.
- [ ] Ghi vào README hoặc `docs/incidents.md` ít nhất một "sự cố" bạn gặp ở module này (ví dụ test đồng thời đỏ khi thử `MAX+1`), phục vụ kể chuyện khi phỏng vấn.

### 5.2 Tự kiểm tra

**1. Kế hoạch sản xuất khác lệnh sản xuất ở những điểm nào? Vì sao không dùng luôn kế hoạch để chạy line?**
<details><summary>Gợi ý</summary>Kế hoạch: thô (mã hàng × kỳ × số lượng), mềm, chưa gắn công thức cố định, chưa có định danh truy xuất. WO: một mã hàng, một line, một khoảng thời gian, công thức đã đóng băng, có `WoNo` để gom serial/lot. Line cần đơn vị nhỏ, cụ thể, kiểm soát được để scan, đo, hold, hủy.</details>

**2. Vì sao có cả Completed và Closed?**
<details><summary>Gợi ý</summary>Completed: line xong việc (đủ sản lượng hoặc đóng thủ công có lý do). Closed: đối chiếu xong (vật tư thừa/thiếu, serial NG/WIP tồn), số liệu chốt, chỉ đọc. Khoảng giữa dành cho đối chiếu; nếu gộp, dễ có scan/ghi thêm vào lệnh đã "xong".</details>

**3. Vì sao line chỉ được chạy khi WO đã Released?**
<details><summary>Gợi ý</summary>Release là cổng: BOM/Routing đã Approved, snapshot đã chụp, serial đã sinh, có người chịu trách nhiệm (audit). Không Release thì không có serial hợp lệ để scan, route check chặn. Ngăn sản xuất ngoài kế hoạch, dùng công thức nháp, mất truy xuất.</details>

**4. WO đang chạy mà kỹ sư đổi BOM thì sao? Vì sao?**
<details><summary>Gợi ý</summary>WO không đổi vì đã snapshot giá trị và ghi `BomVersion` lúc Release. WO mới sau đó sẽ chụp version mới. Nếu bắt buộc áp version mới cho WO đang chạy thì phải có quy trình riêng (Hold, Cancel và tạo WO mới, hoặc change order có phê duyệt), không tự động.</details>

**5. Vì sao serial không được trùng? Serial có lỗ hổng (000150 rồi 000152) có phải lỗi không?**
<details><summary>Gợi ý</summary>Serial là khóa truy xuất, trùng làm lẫn scan, route check, truy xuất, recall. Lỗ hổng không phải lỗi: SEQUENCE ngoài transaction, rollback vẫn tiêu số; serial là định danh chứ không phải bộ đếm.</details>

**6. Hai người bấm Release cùng một WO cùng lúc: điều gì xảy ra ở từng lớp bảo vệ?**
<details><summary>Gợi ý</summary>Rowversion: request đến sau nhận `DbUpdateConcurrencyException`, cả transaction rollback (serial insert cũng mất), trả 409. Nếu request sau đến sau khi request trước đã commit: `Ensure(Draft)` ném `DomainException`. SEQUENCE bảo đảm số hai request nhận không trùng, unique index là lưới cuối. Số đã cấp cho request thua bị bỏ, để lại lỗ hổng.</details>

**7. Khi nào dùng SEQUENCE, khi nào dùng bảng counter + UPDLOCK?**
<details><summary>Gợi ý</summary>SEQUENCE: cần định danh duy nhất, tốc độ cao, chấp nhận lỗ hổng. Counter + UPDLOCK: cần số liên tục không lỗ hổng (chứng từ pháp lý) chấp nhận xếp hàng chờ và chỉ đúng trong transaction.</details>

**8. Chọn Code128 hay DataMatrix cho nhãn bo mạch nhỏ? Cần lưu ý gì về máy quét và quy trình?**
<details><summary>Gợi ý</summary>DataMatrix: nhỏ gọn, sửa lỗi (Reed-Solomon), hợp với bo nhỏ hoặc khắc laser; cần imager 2D. Code128: vạch dài, laser 1D đọc được, hợp nhãn thùng/vỏ. Lưu ý nhãn chịu nhiệt nếu dán trước reflow, quiet zone, log reprint.</details>

**9. WIP của một WO được định nghĩa thế nào trong dự án? Vì sao nhà máy quan tâm?**
<details><summary>Gợi ý</summary>Số serial ở trạng thái InProcess, Ng, Repair (đã vào line, chưa Completed/Scrapped). WIP là vốn nằm chết; theo định luật Little, WIP tăng khi thời gian lưu tăng, chỉ ra nút thắt; và WIP còn tồn phải xử lý trước khi Close.</details>

**10. Vì sao UI nhận `allowedActions` từ server thay vì tự kiểm tra `Status`? Và vì sao `HasOutput` phụ thuộc `Include`?**
<details><summary>Gợi ý</summary>Tránh lặp luật ở hai nơi dẫn tới lệch. `HasOutput` duyệt collection `Operations`; nếu chưa load, collection rỗng và trả false, cho phép Cancel WO đã chạy.</details>

**11. [v2] Vì sao `Priority` được đổi sau Release còn `PlannedQty` thì không?**
<details><summary>Gợi ý</summary>`PlannedQty` gắn với số serial đã sinh và `RequiredQty` đã snapshot; đổi sẽ làm lệch. `Priority` chỉ quyết định thứ tự hiển thị trong dispatch list, không ảnh hưởng dữ liệu nào đã chốt.</details>

**12. [v2] Vì sao FAI không là một trạng thái trong state machine của WO?**
<details><summary>Gợi ý</summary>Vòng đời WO (Draft → Closed) và cổng FAI là hai trục độc lập: WO đang InProgress có thể có FAI Pending hoặc Passed, đang OnHold cũng vậy. Gộp vào một enum thì số trạng thái nhân lên và ma trận test phình to.</details>

### 5.3 Câu hỏi phỏng vấn liên quan + gợi ý trả lời

**"Làm sao tránh trùng serial khi nhiều PC cùng in/release?"**
Gợi ý: cấp số bằng SEQUENCE của SQL Server (không khóa, không trùng dù đồng thời), ghép thành chuỗi bằng hàm thuần có check digit, thêm unique index làm lưới an toàn. Nói rõ serial có thể có lỗ hổng và vì sao chấp nhận. Nếu bị hỏi phương án khác: bảng counter + UPDLOCK (liên tục nhưng xếp hàng). Nêu bạn đã viết test đồng thời với Testcontainers và **cố tình phá** (dùng `MAX+1`) để thấy test đỏ.

**"WO đang chạy mà kỹ sư đổi BOM thì sao?"**
Gợi ý: WO đã snapshot BOM/Routing lúc release và ghi version, nên không đổi. WO mới sẽ lấy version Approved mới. Nếu cần áp dụng cho WO đang chạy thì đó là quyết định nghiệp vụ (change control): Hold, đánh giá, có thể Cancel/tạo WO mới; không tự động vì sản phẩm đã lắp theo công thức cũ. Nhấn mạnh truy xuất cần biết "con này theo BOM nào".

**"Release làm những gì và vì sao là bước riêng?"**
Gợi ý: kiểm tra Approved, snapshot, sinh serial, đổi trạng thái, audit, trong một transaction. Là điểm bàn giao trách nhiệm từ Planner sang xưởng; M3 dựa vào nó để chặn sản xuất ngoài kế hoạch.

**"MES work order khác ERP production order thế nào?"**
Gợi ý: ERP quản lý kế hoạch và tài chính (nhu cầu, giá thành, tồn kho tổng); MES quản lý thực thi (từng serial, từng bước, từng máy, realtime). Ngoài đời ERP tạo lệnh gửi xuống MES, MES báo lại sản lượng và tiêu hao; trong dự án này tôi cho Planner tạo WO trong MES và để `ExternalRef` cho tích hợp sau (M8).

**[v2] "Line có 3 WO đang Released, operator biết làm cái nào trước bằng cách nào?"**
Gợi ý: dispatch list theo line, sắp theo `Priority` rồi `PlannedStart`; supervisor đổi Priority khi khách yêu cầu gấp, có audit. Nói rõ không làm APS/Gantt và vì sao: MES lo thực thi, xếp lịch tối ưu là việc của APS.

**[v2] "FAI là gì, hệ thống của bạn chặn thế nào?"**
Gợi ý: kiểm tra sản phẩm đầu tiên để bắt lỗi setup trước khi nhân lên cả lô. Release đặt `FaiStatus = Pending`; route check (M3) chỉ cho đúng 1 serial mẫu đi qua; QC duyệt ở M7 mới mở cho cả lô. Không đạt thì chọn mẫu mới.

**"WO 200 con nhưng chỉ ra được 197 tốt, xử lý thế nào?"**
Gợi ý: hệ thống không tự Complete vì QtyGood < PlannedQty; Supervisor Complete thủ công bắt buộc lý do (audit); serial scrap ghi rõ; sau đối chiếu vật tư thì Close. Hoặc tạo WO bổ sung nếu khách cần đủ 200.

### 5.4 Tài liệu đọc thêm

Chỉ liệt kê tên tài liệu và nơi tìm; hãy tự mở trang chủ để lấy liên kết hiện hành.

- **learn.microsoft.com**: EF Core, mục "Handling Concurrency Conflicts" (optimistic concurrency, `IsRowVersion`, `DbUpdateConcurrencyException`); EF Core, mục "Sequences" (`HasSequence`); EF Core, mục "SQL Queries" (`SqlQueryRaw`, `ExecuteUpdateAsync` trong phần bulk update); SQL Server, mục "Sequence numbers" và `CREATE SEQUENCE`, `sp_sequence_get_range`; SQL Server, mục "Table hints" (`UPDLOCK`, `HOLDLOCK`); tài liệu "MVVM Toolkit" (`RelayCommand`, `ObservableProperty`, `NotifyCanExecuteChangedFor`).
- **labelary.com**: trang "Web Service" (API xem trước ZPL) và trình xem ZPL trực tuyến.
- **zebra.com**: "ZPL Programming Guide" (tham số chính xác của `^BC`, `^BX`, `^BY`, `^FO`, `^A`).
- **gs1.org**: tài liệu về Code128 và DataMatrix (khi cần đối chiếu chuẩn).
- **Sách**: Eric Evans, "Domain-Driven Design" (aggregate, entity); Vaughn Vernon, "Effective Aggregate Design" (bài viết, giải thích vì sao aggregate nên nhỏ, liên quan việc `Serial` tách khỏi `WorkOrder`).
- **Trong dự án**: spec mục Module 2; M1 (BOM/Routing version, explode BOM); M3 (route check dùng `Serial.CurrentSeq` và trạng thái WO); M6 (`SerialComponent` gắn serial với lot).

