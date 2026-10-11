# Module 6 — Material & Traceability (Lot NVL, nạp vật tư, genealogy, truy xuất)

> Module này trả lời câu hỏi: "sản phẩm nào đã dùng vật tư nào, trên máy nào, bởi ai, lúc nào" và ngược lại "vật tư này đã đi vào những sản phẩm nào". Sau khi xong bạn có: nhập lot NVL + in nhãn lot, màn hình kiosk nạp reel lên máy có kiểm tra đúng vật tư/còn hạn/FIFO, tự ghi `SerialComponent` mỗi khi scan OK, truy vấn truy xuất ngược/xuôi bằng recursive CTE (< 1 giây với hàng triệu dòng) và cây truy xuất trên WPF Back-office.
> Thời lượng ước tính: 2,5–3 tuần (~28 giờ, xem mục 3; 25 giờ của bản cũ + 3 giờ cho phần **[v2]**).
> Module tiên quyết: M1 (Item, BOM, `IsLotControlled`, **[v2]** `SkipIqc`), M2 (snapshot BOM vào WO, `Serial`), M3 (scan, `ScanLog`, route check), **[v2]** M3b (`CartonSerial`, `Carton`, `Pallet`). Module này cung cấp dữ liệu cho M7 (Hold theo lot/serial/pallet).
>
> **Cập nhật theo [spec-v2](spec-v2.md):** (1) **cổng IQC**: lot mới nhập ở trạng thái `PendingIqc`, QC kiểm tra đầu vào theo supplier lot, chưa đạt thì không nạp được lên máy (mục 1.2b, 2.4b); (2) truy xuất xuôi trả thêm **thùng, pallet, đã giao chưa** (mục 2.2b); (3) danh sách truy xuất **xuất Excel** (dùng lại M5 mục 2.7) thay cho CSV; (4) tùy chọn MSL (linh kiện nhạy ẩm) ở bước 10. Các chỗ đổi được đánh dấu **[v2]**.

---

## 1. Kiến thức nghiệp vụ MES

### 1.1 Bức tranh tổng thể

**Module này nằm ở đâu.** Trong nhà máy điện tử có hai dòng chảy song song:

```
DÒNG SẢN PHẨM   (M2, M3):  Serial  SMT ──▶ Assembly ──▶ Test ──▶ Pack ──▶ Giao khách
                                     ▲          ▲
DÒNG VẬT TƯ     (M6)    :  Kho ──▶ Lot/Reel ──▶ Nạp lên máy/trạm (feeder, khay, thùng)
                           (nhập lot)   (setup + verification)
```

M3 biết "serial nào đã qua trạm nào". M6 nối hai dòng lại: tại thời điểm serial được scan OK ở trạm SMT, **lot nào đang nạp trên máy đó**, và ghi lại thành một dòng `SerialComponent`. Từ đó về sau, mọi câu hỏi truy xuất chỉ là truy vấn trên các dòng này.

**Ai dùng.**

| Người dùng | Làm gì với module này |
| --- | --- |
| Nhân viên kho (Warehouse) | Nhận hàng, tạo lot, dán nhãn lot |
| Operator | Nạp/đổi reel trên kiosk, scan xác nhận |
| Supervisor | Duyệt ngoại lệ (nạp lot mới hơn khi còn lot cũ), xem lot còn lại |
| QC | Khi có khiếu nại/lô lỗi: truy xuất, xác định phạm vi ảnh hưởng, Hold |
| Kỹ sư chất lượng, đại diện khách hàng | Đến audit: yêu cầu "cho tôi xem hồ sơ truy xuất của serial này" |

**Vì sao nhà máy cần.** Ba thời điểm, ba câu hỏi:

1. **Trước khi chạy** (verification): reel đang gắn trên máy có đúng loại linh kiện không? Có hết hạn không? Chặn ngay nếu sai — rẻ nhất.
2. **Trong khi chạy** (consumption record): mỗi sản phẩm làm ra đã dùng lot nào? Ghi lại tự động, không nhờ operator ghi tay.
3. **Sau khi giao hàng** (traceability): khách trả lại một serial hỏng, hoặc nhà cung cấp báo một lô linh kiện lỗi. Trả lời trong vài giờ: chạm vào những sản phẩm nào, đang ở đâu.

Không có 3 thứ này, khi có sự cố nhà máy chỉ còn cách "thu hồi/hold cả tuần sản xuất cho chắc" — chi phí gấp hàng chục lần (ví dụ số liệu ở mục 1.3).

### 1.2 Khái niệm & thuật ngữ

**Item, Lot, Serial — ba tầng định danh.**

| Khái niệm | Định nghĩa dễ hiểu | Ví dụ ở xưởng điện tử |
| --- | --- | --- |
| Item (mã hàng) | Loại hàng, không phải một vật thể cụ thể | `CAP-100NF-0603` (tụ 100 nF, kích thước 0603) |
| Lot (lô) | Một nhóm đơn vị hàng cùng được sản xuất/nhập cùng đợt, coi là đồng nhất | Reel `L-CAP-260915-03` chứa 10.000 tụ từ cùng một mẻ của nhà cung cấp |
| Serial (số sê-ri) | Định danh **riêng cho từng đơn vị** | Bo mạch `PCB-A-2610-000137` |

**Vì sao NVL theo lot, thành phẩm theo serial.** Do chi phí và khả thi:

- Một reel tụ 0603 có 10.000 con, giá cả reel khoảng vài USD. Không ai dán mã cho từng con tụ (nhỏ 1,6 × 0,8 mm, còn không có chữ để in). Truy xuất ở mức "reel/lot" là đủ: nếu lot lỗi thì cả lot đáng ngờ.
- Một bo `PCB-A` thành phẩm có giá trị hàng chục USD, khách hàng có thể trả về từng cái. Phải biết chính xác cái nào → serial.
- Cầu nối giữa hai tầng: **`SerialComponent`** = "serial X đã dùng lot Y". Đây là dòng dữ liệu quan trọng nhất của module.

Trong Module 1, cột `Item.IsLotControlled` quyết định mã hàng nào phải theo lot. Vật tư phụ như vít, băng keo thường **không** quản lý theo lot (tốn công hơn giá trị) → module này bỏ qua chúng khi verification và ghi consumption.

**Lot number của nhà máy vs Supplier lot.** Hai số khác nhau, cả hai đều phải lưu:

- `SupplierLot` (còn gọi lot/batch/date code của nhà cung cấp): ví dụ `MUR-2609-A17`. Nhà cung cấp báo "lô A17 bị lỗi" thì họ nói bằng số này.
- `LotNo` của MES: ví dụ `L-CAP-260915-03`, do MES sinh khi kho nhập, in thành nhãn dán lên reel.
- Quan hệ: **một supplier lot có thể thành nhiều reel**, mỗi reel là một `MaterialLot` riêng trong MES (quy ước của dự án này: 1 reel = 1 lot). Ví dụ supplier lot `MUR-2609-A17` giao 5 reel → 5 `MaterialLot`, cùng `SupplierLot`. Đây là lý do truy xuất xuôi phải cho phép bắt đầu từ `SupplierLot`, không chỉ từ `LotNo`.

**Reel, feeder, slot — cách linh kiện SMT lên máy.** SMT (Surface Mount Technology) là công đoạn đầu của line: máy in kem hàn lên bo trần → **máy gắp đặt (mounter / pick-and-place)** gắp từng linh kiện đặt lên bo → lò reflow hàn → AOI kiểm tra quang học.

- **Reel**: cuộn băng nhựa có các ô nhỏ, mỗi ô chứa một linh kiện. Một reel chứa vài nghìn đến vài chục nghìn con.
- **Feeder**: bộ cấp liệu gắn trên máy. Kẹp reel, mỗi lần đầu gắp lấy một con thì feeder đẩy băng tiến một ô.
- **Slot**: vị trí trên bàn máy để gắn feeder (ví dụ slot 12). Chương trình máy (program) quy định "slot 12 = tụ 100 nF", "slot 13 = điện trở 10 kΩ"...
- Con số minh họa: máy 30.000 cph (components per hour), bo `PCB-A` có 120 linh kiện → mỗi bo mất 120 / 30.000 × 3600 = 14,4 giây. Tụ 100 nF dùng 4 con/bo → reel 10.000 con đủ cho 2.500 bo ≈ 10 giờ chạy. Tức là **cứ khoảng một ca, operator phải đổi vài reel** — nạp/đổi reel là việc lặp lại hàng chục lần mỗi ngày, mỗi lần là một cơ hội nạp nhầm.

Dự án này **không** có máy gắp thật, nên "nạp reel lên feeder" được mô phỏng thành: scan mã trạm/máy + (tùy chọn) số slot + scan nhãn lot → MES ghi `MaterialLoad`.

**Setup và Material verification.**

- **Setup**: chuẩn bị máy cho một WO/model: lắp feeder, nạp reel theo danh sách. Trạng thái máy `Setup` (M4) chính là thời gian này.
- **Material verification (setup verification)**: đối chiếu vật tư thực tế đang nạp với thứ **được phép** theo BOM/WO. Cách làm phổ biến: operator scan mã slot và mã reel; hệ thống so với BOM snapshot của WO. Sai thì báo đỏ, không cho chạy.

**FIFO / FEFO và hạn dùng.**

- **FIFO** (First In First Out): lot nhập trước dùng trước, để tồn kho không "già" đi. **FEFO** (First Expired First Out): lot hết hạn trước dùng trước — chính xác hơn khi vật tư có hạn dùng.
- Vì sao có hạn dùng: kem hàn, keo, flux hết hạn thì chất lượng mối hàn giảm; linh kiện nhạy ẩm (MSL) có thời gian cho phép để hở ngoài túi hút ẩm (floor life). Hai thứ sau (kem hàn có hồ sơ nhiệt độ/thời gian rã đông, MSL floor life) **ngoài phạm vi MVP**; MVP chỉ kiểm tra `ExpiryAt`.

**Genealogy (phả hệ / lineage).** Đồ thị quan hệ "cái này được tạo từ cái kia". Nút là lot và serial; cạnh là "đã dùng". Có hai loại cạnh trong dự án:

- `SerialComponent`: serial ← lot (thành phẩm dùng lot NVL).
- `LotGenealogy`: lot cha → lot con (lot này sinh ra từ lot kia): chia reel (một reel 10.000 con tách thành hai túi 4.000 và 6.000 cho hai line), gộp lot, hoặc **bán thành phẩm theo lô** (một mẻ 500 module con làm từ 1 lot IC + 1 lot bo trần, sau đó serial thành phẩm dùng lot module).
- "Nhiều cấp" = chuỗi cạnh dài hơn 1: `Reel gốc → túi chia → lot module con → serial`. Cấp càng nhiều thì càng cần truy vấn đệ quy.

**Backward và forward traceability.**

| | Truy xuất ngược (backward) | Truy xuất xuôi (forward) |
| --- | --- | --- |
| Hướng | Thành phẩm → NVL | NVL → thành phẩm |
| Điểm bắt đầu | Một serial (khách khiếu nại) | Một lot hoặc supplier lot (nhà cung cấp báo lỗi, QC phát hiện reel lỗi) |
| Câu hỏi | "Serial này dùng lot nào? Máy nào làm, ai làm, lúc nào?" | "Lot này đã vào những serial nào? Giờ chúng ở đâu?" |
| Kết quả | Danh sách nhỏ (vài chục lot) | Danh sách có thể lớn (hàng nghìn serial) |
| Dùng để | Tìm nguyên nhân gốc (root cause) | Xác định phạm vi ảnh hưởng, Hold/recall |

Thường làm **hai bước liền nhau**: ngược để tìm lot nghi ngờ, rồi xuôi từ lot đó để tìm mọi serial khác bị ảnh hưởng (ví dụ số liệu mục 1.3).

**Containment (khoanh vùng/ngăn chặn) và phạm vi ảnh hưởng.** Containment là hành động ngăn hàng nghi ngờ đi tiếp *trong lúc điều tra*: Hold lot còn trong kho/trên máy, Hold serial đang WIP hoặc nằm kho thành phẩm, thông báo khách nếu đã giao. Mục tiêu: phạm vi ảnh hưởng **đủ rộng để không sót, đủ hẹp để không phải bỏ cả tuần sản xuất**. Truy xuất tốt cho phép thu hẹp phạm vi từ "mọi thứ trong 3 tuần" xuống "6.200 bo dùng đúng 3 reel đó".

**Audit của khách hàng điện tử/ô tô.** Khách hàng lớn (nhất là ô tô, y tế, viễn thông) đến nhà máy kiểm tra hệ thống chất lượng. Điều họ hay làm: chọn ngẫu nhiên một serial giao cách đây vài tháng và yêu cầu: "cho tôi thấy lot linh kiện, máy, ca, người thao tác, kết quả test của từng công đoạn, trong vòng vài giờ". Cũng có thể yêu cầu "mô phỏng recall" (mock recall): cho một lot linh kiện, nhà máy phải liệt kê hết serial bị ảnh hưởng trong thời gian quy định.

- Yêu cầu truy xuất và định danh nằm trong các tiêu chuẩn hệ thống chất lượng như ISO 9001 và IATF 16949 (ô tô; điều khoản về nhận dạng và truy xuất — bạn nên đối chiếu số điều khoản trong bản tiêu chuẩn, xem mục 5.4). Trong điện tử còn có IPC-1782 về truy xuất sản phẩm điện tử.
- Thời gian lưu hồ sơ thường quy định trong hợp đồng: từ vài năm đến hàng chục năm (ô tô thường dài). Hệ quả kỹ thuật: bảng `SerialComponent` chỉ tăng, cần chiến lược index/archive (mục 2.3).

#### 1.2b [v2] IQC — kiểm tra vật tư đầu vào (Incoming Quality Control)

**IQC** là bước QC kiểm tra hàng mua vào **trước khi** cho phép dùng: đếm số lượng, đối chiếu mã trên nhãn nhà cung cấp, kiểm ngoại quan, đo mẫu (ví dụ đo giá trị điện trở của 5 con trên reel), kiểm tra chứng từ (CoC, báo cáo xuất xưởng). Ở nhà máy điện tử VN, kho nhận hàng xong đặt vào **khu chờ kiểm (quarantine)**, dán nhãn vàng; IQC đạt mới chuyển sang kệ dùng được.

Vì sao MES phải biết: nếu IQC chỉ là tờ giấy, operator vẫn lấy được reel "chờ kiểm" mang lên máy (nhất là khi line đang thiếu hàng). Khi trạng thái IQC nằm trong MES, **verification lúc nạp chặn luôn** (cùng cơ chế với lot Hold).

Mô hình trong dự án:

- Nhập kho → `MaterialLot.Status = PendingIqc` (trừ Item có `SkipIqc = true`, M1: vào thẳng `Available`).
- IQC kiểm theo **supplier lot** (một lần giao của một lô nhà cung cấp, gồm nhiều reel), lấy mẫu vài reel. Kết quả áp cho **mọi reel** cùng `(ItemId, SupplierLot)` đang `PendingIqc`.
- Đạt → `Available`. Không đạt → `IqcRejected` (không bao giờ nạp được; xử lý trả hàng nằm ở NCR của M7).
- Lấy mẫu: ngoài đời theo bảng AQL (ISO 2859-1 / ANSI Z1.4: lô N reel → kiểm n mẫu, chấp nhận nếu ≤ c lỗi). MVP: QC nhập số mẫu đã kiểm và số lỗi; có thể hiện gợi ý cỡ mẫu theo bảng AQL đơn giản. Biết tên chuẩn là đủ để trả lời phỏng vấn.

### 1.3 Một ngày ở xưởng: tình huống thực tế

Nhân vật: **Lan** (kho), **Tuấn** (operator Line 1 SMT), **Minh** (supervisor), **Hương** (QC). Sản phẩm: `PCB-A`, mỗi bo dùng 4 tụ `CAP-100NF-0603` (QtyPer = 4) và 1 điện trở `RES-100K-0603` (BOM của `PCB-A` chỉ có loại 100 kΩ, không có 10 kΩ).

**Tình huống A — Buổi sáng: nhập lot, nạp reel**

1. **07:30 — Lan nhập kho.** Nhà cung cấp giao 5 reel tụ, mỗi reel 10.000 con, cùng supplier lot `MUR-2609-A17`, hạn dùng 2 năm. Lan mở màn hình nhập kho, chọn Item, nhập `SupplierLot`, số reel = 5, `ExpiryAt`. MES tạo 5 lot `L-CAP-260915-01` … `-05`, mỗi lot Qty = RemainingQty = 10.000, Status = Available, in 5 nhãn lot (mã vạch = `LotNo`). Lan dán nhãn lên từng reel. Kho vẫn còn một reel cũ `L-CAP-260901-02` (nhập 15 ngày trước, còn 3.200 con).
2. **08:00 — Tuấn chuẩn bị chạy WO.** Planner đã release WO 200 pcs (M2). Máy `L1-SMT-01` chuyển trạng thái `Setup`. Trên kiosk Tuấn chọn "Load material", scan mã máy, rồi scan reel mới `L-CAP-260915-01`.
3. **MES phát hiện lot cũ hơn còn hàng.** Kiosk hiện **vàng**: "Older lot L-CAP-260901-02 (3,200 left) is available. Load it first." Tuấn gọi Minh. Minh bảo dùng reel cũ trước; Tuấn scan lại `L-CAP-260901-02` → xanh, `MaterialLoad` được ghi (LoadedBy = Tuấn).
4. **08:10 — Nạp nhầm điện trở.** Tuấn lấy nhầm một reel `RES-10K-0603` (từ kệ của model khác). Điện trở 10 kΩ và 100 kΩ nhìn giống hệt nhau, chỉ khác mã in trên thân (`103` và `104`) mà mắt thường khó đọc. Tuấn scan nhãn lot: kiosk hiện **đỏ** "Item RES-10K-0603 is not in the BOM of WO-2610-001", âm báo lỗi, không ghi gì vào `MaterialLoad`, chỉ ghi `MaterialVerificationLog` (Result = Rejected, Reason = ItemNotInBom). Tuấn đổi sang đúng reel `RES-100K-0603`.
5. **08:20 — Chạy.** Simulator/operator bắt đầu scan serial ở trạm SMT. Mỗi serial OK, MES ghi 2 dòng `SerialComponent` (một cho lot tụ, một cho lot điện trở) và trừ `RemainingQty` (tụ −4, điện trở −1).
6. **16:40 — Hết reel giữa ca.** Reel cũ hết 3.200 con sau 800 bo. Tuấn nạp reel mới `L-CAP-260915-03` (Minh duyệt vì hai reel `-01`, `-02` đã được dùng trước). MES tự đóng `MaterialLoad` của lot cũ (`UnloadedAt`), đổi lot cũ sang Consumed.

**Tình huống B — Hai tuần sau: khiếu nại và truy xuất (số liệu minh họa)**

7. **Thứ Hai 12/10, 09:00.** Khách hàng (kỹ sư chất lượng của hãng ô tô) gửi email: serial `PCB-A-2610-000137` trả về từ thị trường, tụ C12 sai dung sai. Họ hỏi: "Lô linh kiện nào? Còn bao nhiêu bo khác bị ảnh hưởng? Trả lời trong 2 giờ."
8. **09:10 — Hương truy xuất ngược** từ serial 137: MES trả về 8 dòng thành phần; dòng C12 cho thấy lot `L-CAP-260915-03`, `SupplierLot` = `MUR-2609-A17`, nạp lên `L1-SMT-01` bởi Tuấn lúc 16:41 ngày 03/10, đặt bo lúc 02:14 ngày 04/10, ca đêm. Hương gọi nhà cung cấp; họ xác nhận supplier lot `MUR-2609-A17` có nghi vấn.
9. **09:25 — Containment tại nguồn.** Hương truy vấn: lot `-03` **đang nạp trên `L1-SMT-01`, máy vẫn đang chạy**. Line làm ~250 bo/giờ (3.600 / 14,4). Mỗi giờ chậm là thêm ~250 bo dính lot lỗi. Hương gọi Minh: dừng, tháo reel `-03`, Hold cả 5 lot `MUR-2609-A17` (`-04`, `-05` còn nguyên trong kho).
10. **09:40 — Truy xuất xuôi** từ supplier lot `MUR-2609-A17`: 5 lot, 3 lot đã dùng. Kết quả **6.200 serial**:

| Lot | Số bo đã dùng | Ghi chú |
| --- | --- | --- |
| `-01` | 2.500 | đã hết (Consumed) |
| `-02` | 2.500 | đã hết (Consumed) |
| `-03` | 1.200 | tháo khỏi máy lúc 09:30, còn 5.200 con |
| `-04`, `-05` | 0 | còn nguyên trong kho, Hold |

Phân loại 6.200 serial theo `Serial.Status`: Completed 4.900, InProcess 900, Ng/Repair 250, Scrapped 150.

11. **10:00 — Hành động theo từng nhóm.** WIP 900 bo → Hold (M7) để route check chặn ở các trạm sau. 250 bo NG/Repair → Hold, **không** sửa rồi cho đi tiếp. Completed 4.900 bo: 1.300 còn trong kho thành phẩm → Hold; 3.600 đã giao (theo hệ thống kho/ERP — ngoài MVP) → gửi khách danh sách serial. 150 bo Scrapped → không cần hành động, giữ hồ sơ.
12. **10:45 — Gửi khách**: danh sách 3.600 serial đã giao, lot nghi vấn, hành động đã làm, hạn báo cáo nguyên nhân. Còn dưới 2 giờ tính từ email đầu.

**Vì sao con số này đáng giá.** Nếu **không có truy xuất**, nhà máy chỉ biết "nhà cung cấp giao ngày 15/09", nên phải Hold mọi bo `PCB-A` làm từ 15/09 đến 12/10, giả sử 40.000 bo. Có truy xuất: 6.200 bo, giảm 84,5%. Với chi phí xử lý (kiểm lại, đổi, vận chuyển) giả định 8 USD/bo: 320.000 USD so với 49.600 USD. Con số chỉ để minh họa, tỉ lệ mới là điều cần nhớ.

### 1.4 Quy tắc nghiệp vụ và lý do tồn tại

| # | Quy tắc | Vì sao có |
| --- | --- | --- |
| R1 | `LotNo` duy nhất toàn hệ thống; mọi nhãn lot phải scan được | Nhãn là cầu nối giữa vật thể thật và dữ liệu. Trùng mã = truy xuất sai âm thầm |
| R2 | Khi nạp: item của lot phải có trong **BOM snapshot của WO** (`WorkOrderMaterial`), và được tiêu thụ tại trạm/máy này (theo `OperationSeq`) | Nạp nhầm loại linh kiện là lỗi không phát hiện được bằng mắt và nhiều khi qua cả AOI/test (tụ không có chữ). Chặn ngay lúc nạp là rẻ nhất |
| R3 | Lot hết hạn (`ExpiryAt` ≤ thời điểm hiện tại UTC) bị chặn. Không có override | Vật tư hết hạn có thể gây hàn kém, độ tin cậy giảm ngoài thị trường. Override cho hạn dùng là rủi ro chất lượng, không phải rủi ro tồn kho |
| R4 | Chỉ lot `Available` và `RemainingQty` > 0 được nạp. Lot `Hold` bị chặn (M7 dùng quy tắc này) | Hold chỉ có ý nghĩa nếu hệ thống thực sự chặn được |
| R5 | FIFO: nếu còn lot cùng Item, nhập sớm hơn, Available, còn hàng → cảnh báo vàng. Supervisor được override, phải có lý do, ghi audit | FIFO là chính sách tồn kho, thường có ngoại lệ hợp lý (lot cũ dính nghi vấn, khác nhà cung cấp theo yêu cầu khách). Cảnh báo cứng sẽ khiến người dùng tìm cách lách |
| R6 | Mỗi (trạm/máy, item) chỉ có **một** `MaterialLoad` đang hoạt động. Nạp lot mới cùng item → đóng lot cũ (`UnloadedAt`) | Để mọi lần scan OK biết chính xác "lot nào đang dùng". MVP không hỗ trợ hai feeder cùng item chạy song song (xem 1.5) |
| R7 | Scan OK ở trạm có tiêu thụ vật tư → ghi `SerialComponent` cho từng BomLine tiêu thụ ở trạm đó, trừ `RemainingQty` | Ghi tự động tại thời điểm thật, không dựa vào việc người nhớ ghi |
| R8 | Nếu trạm có BomLine tiêu thụ lot-controlled mà chưa có lot nào đang nạp → **chặn scan** (`MATERIAL_NOT_LOADED`). Quyết định của tài liệu này, bật/tắt bằng cấu hình | Nếu vẫn cho scan OK, serial thiếu hồ sơ truy xuất — sau này không dựng lại được |
| R9 | Scan lặp (idempotent, quy tắc M3) không ghi `SerialComponent` lần hai. Unique index `(SerialNo, StationId, ItemId, LotNo)` | Tránh trừ kho hai lần, tránh trace ra 2 dòng cho một sự kiện |
| R10 | `RemainingQty` là **số lý thuyết**, không bao giờ âm. Khi unload, operator nhập số còn lại thực tế để hiệu chỉnh | Máy gắp hao hụt 0,5–2% (rơi, hút trượt) nên số lý thuyết lệch dần. Truy xuất quan trọng hơn khớp tồn kho tuyệt đối |
| R11 | `SerialComponent`, `MaterialVerificationLog` là insert-only. Không sửa, không xóa | Hồ sơ audit phải chứng minh được không bị chỉnh sau sự kiện. Phát hiện ghi sai → mở NCR (M7), không sửa dòng cũ |
| R12 | `LotGenealogy` không có vòng; lot cha phải tồn tại trước lot con | Truy vấn đệ quy sẽ lặp vô hạn nếu có vòng |
| R13 | Mọi lần nạp, kể cả bị từ chối và override, ghi `MaterialVerificationLog` (ai, máy, lot, kết quả, lý do) | Chứng minh với khách: hệ thống đã chặn bao nhiêu lần nạp sai. Cũng là dữ liệu cải tiến (nhân viên nào hay nạp nhầm → đào tạo lại) |
| R14 | Thời gian lưu UTC (`datetime2`); hạn dùng so với UTC | Quy ước chung của dự án (xem Module 5, mục xử lý thời gian) |
| R15 **[v2]** | Lot mới nhập có `Status = PendingIqc` (trừ Item `SkipIqc`); lot `PendingIqc` hoặc `IqcRejected` **không nạp được** (`LotNotIqcPassed`), không có override | Vật tư chưa được QC xác nhận có thể sai mã, sai giá trị, hàng giả. Override cho IQC là bỏ qua cả mục đích của IQC |
| R16 **[v2]** | Kết quả IQC ghi theo `(ItemId, SupplierLot)` và áp cho mọi reel `PendingIqc` của supplier lot đó trong **một transaction**; ghi `IqcRecord` insert-only | Một lần kiểm cho một lô giao hàng là thực tế ngoài đời; ghi một nơi để auditor xem "lô này ai kiểm, kiểm bao nhiêu mẫu" |
| R17 **[v2]** | Lot `IqcRejected` không quay lại `Available` bằng thao tác thường; muốn dùng phải qua quyết định NCR (M7, disposition `UseAsIs`) có người duyệt | Đảo kết quả IQC là quyết định chất lượng, phải có trách nhiệm rõ |
| R18 **[v2]** | Truy xuất xuôi luôn trả **vị trí hiện tại** của từng serial: trong line (WIP), đã vào thùng, đã lên pallet, đã giao | Containment khác nhau theo vị trí: WIP thì Hold, trong kho thì cách ly, đã giao thì báo khách |

### 1.5 Sai lầm/sự cố thường gặp ngoài đời

- **Nạp nhầm reel giống nhau.** Hai reel nhìn y hệt, khác mã hàng. Không kiểm tra ở lúc nạp thì chỉ phát hiện khi đến ICT/functional test hoặc, tệ nhất, ngoài thị trường. Chi phí sửa lỗi tăng theo cấp số nhân qua từng công đoạn: quy tắc kinh nghiệm "1 – 10 – 100" (sửa ở nguồn = 1, ở cuối line = 10, ở khách = 100).
- **Chỉ kiểm tra item có trong BOM là chưa đủ.** Nếu BOM chứa cả `RES-10K` và `RES-100K` cùng ở SMT, nạp reel 10K vào slot của 100K vẫn qua kiểm tra R2. Cách phòng đúng là kiểm tra theo **slot** (bảng slot → item lấy từ chương trình máy). MVP không có bảng này; ghi rõ là giới hạn và thêm như bước mở rộng (mục 3, bước 10).
- **Dán nhãn nhầm.** Nhãn của reel A dán lên reel B. Verification qua mã lot sẽ tin nhãn. Phòng ngừa: quy trình một-người-một-reel-một-nhãn tại kho, kiểm nhãn với nhãn nhà cung cấp; ngoài phạm vi phần mềm nhưng nên nêu khi phỏng vấn.
- **Đổi/nối reel mà không scan.** Operator nối băng reel mới vào reel cũ (splice) cho đỡ dừng máy, không scan. MES tiếp tục ghi lot cũ dù linh kiện đã là lot mới. Truy xuất đẹp nhưng sai. Nghiêm trọng hơn cả thiếu dữ liệu.
- **Nạp lot theo thời gian thay vì theo sự kiện scan.** Thiết kế "serial làm lúc 10:00 → lot nào đang nạp lúc 10:00" bằng cách join thời gian nghe hấp dẫn nhưng vỡ khi đồng hồ lệch, khi sửa `LoadedAt` tay, khi có hai lot cùng item nạp sát nhau. Vì vậy ghi `SerialComponent` tại lúc scan OK (mục 4).
- **Hao hụt làm `RemainingQty` âm hoặc còn dư ảo.** Số lý thuyết lệch với thực tế. Nếu kiosk chặn khi số dư ≤ 0 thì máy còn linh kiện vẫn bị chặn; nếu không kiểm soát thì lot "Consumed" mà vật lý còn. Cách xử lý: floor về 0, cảnh báo, hiệu chỉnh khi unload.
- **Sửa chữa thay linh kiện không ghi lot.** Ở Repair (M3, `RepairLog.ComponentReplaced`) thợ hàn tay thay một tụ bằng con lấy từ reel khác. Nếu không ghi lot thì hồ sơ truy xuất của serial đó sai đúng tại linh kiện từng hỏng. Mở rộng: ghi thêm `SerialComponent` tại trạm Repair.
- **Nhập ngày hết hạn sai định dạng** (ngày/tháng đảo). Lot 03/09/2028 bị hiểu thành 09/03/2028. Dùng date picker thay cho text, hiển thị `dd MMM yyyy`.
- **Hold quá rộng hoặc quá hẹp.** Quá rộng: Hold toàn tuần sản xuất. Quá hẹp: chỉ Hold serial mà quên lot còn trên máy/kho và lot con sinh ra từ lot lỗi (genealogy). Luôn truy xuất **xuôi qua toàn bộ đồ thị**, không chỉ cấp 1.
- **Truy xuất chậm đúng lúc cần.** Audit hoặc recall là lúc căng thẳng nhất, truy vấn 40 giây vì thiếu index `(LotNo)`. Ta đo hiệu năng ngay trong module (mục 3, bước 9).
- **Xóa lot "cho gọn".** Lot đã dùng xóa khỏi bảng thì mất khả năng truy xuất. Lot không bao giờ bị xóa cứng; kết thúc vòng đời là trạng thái `Consumed`.

### 1.6 Bảng thuật ngữ

| English | Tiếng Việt | 日本語 | Giải thích ngắn |
| --- | --- | --- | --- |
| Lot / Batch | Lô | ロット | Nhóm hàng cùng mẻ, truy xuất theo nhóm |
| Serial number | Số sê-ri | シリアル番号 | Định danh từng đơn vị sản phẩm |
| Supplier lot | Lô của nhà cung cấp | サプライヤロット | Số lô do nhà cung cấp gán, một supplier lot có thể thành nhiều reel |
| Reel | Cuộn linh kiện | リール（テープリール） | Cuộn băng chứa linh kiện SMT |
| Feeder | Bộ cấp liệu | フィーダ | Gắn reel lên máy gắp, đẩy băng từng bước |
| Mounter / Pick-and-place | Máy gắp đặt linh kiện | マウンター（実装機） | Máy đặt linh kiện SMT lên bo |
| Setup | Chuẩn bị/đổi máy cho WO | 段取り | Lắp feeder, nạp vật tư theo model |
| Material verification | Kiểm tra đối chiếu vật tư | 部品照合 | Kiểm reel thực tế với BOM/WO trước khi chạy |
| FIFO / FEFO | Nhập trước xuất trước / hết hạn trước xuất trước | 先入れ先出し / 期限先出し | Chính sách chọn lot để dùng |
| Expiry date | Hạn dùng | 有効期限・使用期限 | Quá hạn thì không được dùng |
| Genealogy | Phả hệ lot | ロット系譜（ジェネオロジー） | Đồ thị "lot/serial này sinh từ đâu" |
| Traceability | Truy xuất nguồn gốc | トレーサビリティ | Khả năng dựng lại lịch sử sản phẩm/vật tư |
| Backward trace | Truy xuất ngược | 逆トレース（バックワード） | Từ thành phẩm ra NVL |
| Forward trace | Truy xuất xuôi | 順トレース（フォワード） | Từ NVL ra thành phẩm |
| Containment | Khoanh vùng, ngăn chặn | 封じ込め | Ngăn hàng nghi ngờ đi tiếp khi đang điều tra |
| Affected scope | Phạm vi ảnh hưởng | 影響範囲 | Tập serial/lot bị ảnh hưởng |
| Recall | Thu hồi | リコール | Yêu cầu trả lại hàng đã giao |
| Hold / Quarantine | Giữ lại, cách ly | ホールド／保留・隔離 | Trạng thái chặn sử dụng |
| Audit | Đánh giá/kiểm tra (của khách hàng) | 監査 | Khách đến kiểm tra hệ thống chất lượng |
| Consumption | Tiêu hao vật tư | 消費・実績 | Ghi vật tư đã dùng cho sản phẩm |
| Root cause | Nguyên nhân gốc | 根本原因 | Thứ cần tìm sau khi truy xuất ngược |
| NCR | Báo cáo không phù hợp | 不適合報告書 | Xem Module 7 |

---

## 2. Kiến thức lập trình

Các chủ đề kỹ thuật của module này (theo phân công của brief): mô hình dữ liệu genealogy (2.1), recursive CTE ngược/xuôi (2.2), index cho truy vấn đệ quy (2.3), logic verification và FIFO trong Domain (2.4), hook sự kiện giữa module và trừ tồn kho an toàn (2.5), WPF TreeView truy xuất lazy-load (2.6), đo hiệu năng truy xuất (2.7). Không dạy lại: MediatR/CQRS (Module 1), rowversion/UPDLOCK (Module 2), idempotency và cách seed dữ liệu lớn (Module 3), TimeProvider (Module 5).

### 2.1 Mô hình dữ liệu genealogy (đồ thị trong SQL Server)

**Nó là gì.** Genealogy là một **đồ thị có hướng không vòng (DAG)**: nút = lot hoặc serial, cạnh = "được dùng để làm ra". Một lot có thể đi vào rất nhiều serial (1 reel → 2.500 bo), một serial có nhiều lot (mỗi bo ~8 lot). Đó là quan hệ nhiều-nhiều, kèm chuỗi lot sinh lot.

**Cách "ngây thơ".** (a) Thêm cột `LotNo` vào bảng `Serial`: một serial có 8 lot, không chứa được. (b) Lưu chuỗi `"L1,L2,L3"` trong một cột: không tìm ngược được (`LIKE '%L2%'` quét toàn bảng, khớp nhầm `L21`). (c) Đoán lot từ thời gian: vỡ khi lệch giờ (mục 1.5). (d) Chỉ lưu lot ở cấp thành phẩm, quên lot con sinh từ lot cha: tìm thiếu.

**Khái niệm cốt lõi.**

- Mỗi cạnh là **một dòng** trong bảng nối. Hai bảng cạnh ứng với hai loại quan hệ: `SerialComponent` (serial ← lot) và `LotGenealogy` (lot cha → lot con).
- **Quy ước hướng cạnh** (dễ nhầm nhất): `LotGenealogy(ParentLotNo, ChildLotNo)` = "vật liệu của Parent đã được dùng để tạo Child". Ví dụ chia reel: Parent = reel gốc `L-100`, Child = túi chia `L-100-A`. Truy xuất **xuôi** đi Parent → Child; truy xuất **ngược** đi Child → Parent.
- Ghi cạnh **tại lúc sự kiện xảy ra** (write-time), không suy ra lúc truy vấn (mục 4).
- Truy xuất ở mức lot cho kết quả **bao phủ rộng hơn thực tế** (conservative): nếu `L-100-A` sinh từ `L-100` và `L-100-B` cũng sinh từ `L-100`, đi xuôi từ `L-100` sẽ lấy cả serial dùng `-A` lẫn `-B`. Với containment đây là điều mong muốn (thà dư còn hơn sót).

Ví dụ hai cấp (bán thành phẩm theo lô):

```
IC reel L-IC-01 ─┐
                 ├─(LotGenealogy)─▶ Lot module con SM-0107 (500 module) ─(SerialComponent)─▶ PCB-A-...-000137
Bo trần L-PCB-09 ┘                                                        ─(SerialComponent)─▶ PCB-A-...-000138 ...
Tụ L-CAP-...-03 ───────────────────────────────────(SerialComponent)─────▶ PCB-A-...-000137   (cạnh trực tiếp, 1 cấp)
```

**Snippet — bảng và index (SQL Server).**

```sql
-- Lot NVL: 1 reel = 1 lot. PK tự nhiên là LotNo (chuỗi ngắn, in trên nhãn).
CREATE TABLE MaterialLot (
  LotNo        nvarchar(30)  NOT NULL PRIMARY KEY,
  ItemId       int           NOT NULL,
  Qty          decimal(18,3) NOT NULL,
  RemainingQty decimal(18,3) NOT NULL,           -- số LÝ THUYẾT, xem R10
  SupplierLot  nvarchar(50)  NULL,
  ReceivedAt   datetime2     NOT NULL,           -- UTC
  ExpiryAt     datetime2     NULL,
  Status       tinyint       NOT NULL,           -- 0 Available, 1 Hold, 2 Consumed, [v2] 3 PendingIqc, 4 IqcRejected
  CONSTRAINT CK_MaterialLot_Qty CHECK (RemainingQty >= 0 AND RemainingQty <= Qty)
);
-- Bảng log lớn: insert-only, khóa bigint (quy ước chung của dự án)
CREATE TABLE SerialComponent (
  Id         bigint IDENTITY PRIMARY KEY,
  SerialNo   nvarchar(30)  NOT NULL,
  LotNo      nvarchar(30)  NOT NULL REFERENCES MaterialLot(LotNo),
  ItemId     int           NOT NULL,
  Qty        decimal(18,3) NOT NULL,
  StationId  int           NOT NULL,
  ScanLogId  bigint        NOT NULL,             -- scan nào đã sinh ra dòng này (để lấy operator)
  ConsumedAt datetime2     NOT NULL,
  CONSTRAINT UQ_SerialComponent UNIQUE (SerialNo, StationId, ItemId, LotNo)  -- R9 + phục vụ truy xuất ngược
);
CREATE TABLE LotGenealogy (
  Id bigint IDENTITY PRIMARY KEY,
  ParentLotNo nvarchar(30) NOT NULL REFERENCES MaterialLot(LotNo),
  ChildLotNo  nvarchar(30) NOT NULL REFERENCES MaterialLot(LotNo),
  Qty decimal(18,3) NOT NULL, CreatedAt datetime2 NOT NULL,
  CONSTRAINT UQ_LotGenealogy UNIQUE (ParentLotNo, ChildLotNo)
);
```

**[v2] Bảng IQC** (insert-only, mỗi lần kiểm một dòng):

```sql
CREATE TABLE IqcRecord (
  Id          bigint IDENTITY PRIMARY KEY,
  ItemId      int           NOT NULL,
  SupplierLot nvarchar(50)  NOT NULL,
  LotCount    int           NOT NULL,           -- số reel PendingIqc tại lúc kiểm
  SampleQty   int           NOT NULL,           -- số mẫu đã kiểm
  DefectQty   int           NOT NULL,
  Result      tinyint       NOT NULL,           -- 0 Pass, 1 Fail
  DefectCode  nvarchar(20)  NULL,               -- bắt buộc khi Fail (M1 DefectCode)
  Note        nvarchar(500) NULL,
  InspectedBy nvarchar(50)  NOT NULL,
  InspectedAt datetime2     NOT NULL
);
CREATE INDEX IX_IqcRecord_SupplierLot ON IqcRecord (ItemId, SupplierLot);
```

Cột `ScanLogId` và bảng `MaterialLoad`, `MaterialVerificationLog` là phần **chi tiết hóa thêm** so với spec (spec chỉ liệt kê cột quan trọng). `MaterialVerificationLog` (Id bigint, StationId, LotNo, ItemId, WoId, Result, ReasonCode, By, At, OverrideReason) ghi mọi lần nạp kể cả bị từ chối (R13). Trong `MaterialLoad` thêm cột `SlotNo` (nullable) và `FifoOverrideBy`, `FifoOverrideReason`.

**Bẫy thường gặp.**

- Nhầm hướng `Parent`/`Child` khi viết CTE ngược và xuôi: viết chú thích hướng ngay trên entity và test bằng seed hai cấp.
- Dùng khóa tự nhiên `LotNo` (chuỗi) làm FK ở bảng vài chục triệu dòng làm index to hơn `int`. Ở quy mô của dự án này chấp nhận được (mục 4); nếu bảng lên hàng trăm triệu dòng, cân nhắc khóa thay thế `LotId int`.
- Cạnh bị **xóa hoặc sửa** thì truy xuất sai âm thầm. Đặt quyền DB: ứng dụng chỉ `INSERT/SELECT` trên `SerialComponent`.

### 2.2 Recursive CTE truy xuất ngược và xuôi

**Nó là gì.** CTE (Common Table Expression, `WITH ...`) là truy vấn có tên dùng ngay trong câu lệnh sau nó. **Recursive CTE** tự tham chiếu chính nó để đi dọc một chuỗi quan hệ (cha → con → cháu). Module 1 đã dùng recursive CTE để explode BOM; ở đây dùng lại cùng ý tưởng cho đồ thị lot.

**Cách "ngây thơ".** Viết vòng lặp trong C#: mỗi cấp một truy vấn, mỗi lot một truy vấn (N+1). Với recall có hàng chục lot con, mỗi lot 1 lần round-trip; và không kiểm soát được khi có vòng.

**Khái niệm cốt lõi (SQL Server).**

- Cấu trúc: `anchor member` (điểm xuất phát) `UNION ALL` `recursive member` (join lại chính CTE). SQL Server **chỉ chấp nhận `UNION ALL`** giữa hai phần này, không dùng được `UNION`/`DISTINCT` bên trong phần đệ quy, nên trùng lặp (đồ thị hình thoi: hai đường dẫn đến cùng một lot) phải lọc ở truy vấn ngoài bằng `DISTINCT`.
- **Không có mệnh đề CYCLE** (khác một số CSDL khác). Chống vòng bằng ba lớp: (1) ràng buộc lúc ghi (R12); (2) cột `Depth` giới hạn độ sâu, ví dụ `WHERE t.Depth < 10`; (3) `OPTION (MAXRECURSION n)`. Mặc định là 100; `0` = không giới hạn (đừng dùng).
- **Thiết kế hai giai đoạn**: đệ quy chỉ chạy trên `LotGenealogy` (bảng nhỏ, vài nghìn dòng), rồi **join một lần** sang `SerialComponent` (bảng khổng lồ). Không đưa bảng lớn vào phần đệ quy.
- Nếu điểm xuất phát là supplier lot, anchor chọn nhiều lot cùng lúc.

**Snippet — truy xuất xuôi (đa cấp).** Mở rộng ví dụ trong spec (chỉ 1 cấp) thành đệ quy:

```sql
DECLARE @SupplierLot nvarchar(50) = N'MUR-2609-A17', @ItemId int = 101;

WITH LotTree AS (
    -- anchor: các lot xuất phát (một supplier lot có thể là nhiều reel)
    SELECT l.LotNo, 0 AS Depth
    FROM MaterialLot l
    WHERE l.SupplierLot = @SupplierLot AND l.ItemId = @ItemId
    UNION ALL
    -- recursive: đi xuôi Parent -> Child, giới hạn độ sâu
    SELECT g.ChildLotNo, t.Depth + 1
    FROM LotTree t
    JOIN LotGenealogy g ON g.ParentLotNo = t.LotNo
    WHERE t.Depth < 10
)
SELECT s.SerialNo, s.WoId, s.Status, MIN(t.Depth) AS Depth
FROM LotTree t
JOIN SerialComponent sc ON sc.LotNo = t.LotNo      -- join bảng lớn đúng 1 lần
JOIN Serial s           ON s.SerialNo = sc.SerialNo
GROUP BY s.SerialNo, s.WoId, s.Status               -- lọc trùng do nhiều đường dẫn
OPTION (MAXRECURSION 20);
```

**Snippet — truy xuất ngược.** Từ serial ra mọi lot, kể cả tổ tiên:

```sql
DECLARE @SerialNo nvarchar(30) = N'PCB-A-2610-000137';

WITH Anc AS (
    SELECT sc.LotNo, sc.StationId, sc.ScanLogId, sc.ConsumedAt, 0 AS Depth
    FROM SerialComponent sc
    WHERE sc.SerialNo = @SerialNo                   -- seek theo index UQ_SerialComponent
    UNION ALL
    SELECT g.ParentLotNo, a.StationId, a.ScanLogId, a.ConsumedAt, a.Depth + 1
    FROM Anc a
    JOIN LotGenealogy g ON g.ChildLotNo = a.LotNo   -- đi ngược Child -> Parent
    WHERE a.Depth < 10
)
SELECT a.Depth, a.LotNo, l.ItemId, l.SupplierLot, a.StationId, a.ConsumedAt
FROM Anc a JOIN MaterialLot l ON l.LotNo = a.LotNo
ORDER BY a.Depth, a.ConsumedAt
OPTION (MAXRECURSION 20);
-- Operator, kết quả từng trạm: join thêm ScanLog theo a.ScanLogId (M3).
-- Người nạp reel: join MaterialLoad theo (StationId, LotNo, khoảng thời gian).
```

**Gọi từ C# và bẫy với EF Core.** Truy vấn bắt đầu bằng `WITH` **không thể bọc thêm LINQ** lên trên trong EF Core với SQL Server (EF bọc SQL của bạn thành subquery, mà SQL Server không cho phép CTE trong subquery). Cách chắc chắn nhất, và quen thuộc với bạn từ ADO.NET ở WebForms: dùng `SqlConnection`/`SqlCommand` (Microsoft.Data.SqlClient) trong một lớp `TraceQueries` ở Infrastructure, tham số hóa đầy đủ.

```csharp
// Infrastructure/Traceability/TraceQueries.cs  (đã lược bỏ using)
public async Task<List<AffectedSerialRow>> ForwardAsync(string supplierLot, int itemId, CancellationToken ct)
{
    const string sql = ForwardSql.Text;               // chuỗi SQL ở trên, để trong file riêng cho dễ test
    var rows = new List<AffectedSerialRow>();
    await using var conn = new SqlConnection(_connectionString);
    await conn.OpenAsync(ct);
    await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
    cmd.Parameters.AddWithValue("@SupplierLot", supplierLot);   // KHÔNG nối chuỗi: tránh SQL injection
    cmd.Parameters.AddWithValue("@ItemId", itemId);
    await using var rd = await cmd.ExecuteReaderAsync(ct);
    while (await rd.ReadAsync(ct))
        rows.Add(new AffectedSerialRow(rd.GetString(0), rd.GetInt32(1), rd.GetByte(2), rd.GetInt32(3)));
    return rows;
}
```

Ghi chú không chắc chắn: EF Core 8 có `Database.SqlQuery<T>(FormattableString)` để ánh xạ kết quả raw SQL sang kiểu bất kỳ; nếu dùng cho CTE thì cần `.AsEnumerable()`/`ToListAsync()` ngay và có thể vẫn gặp lỗi bọc subquery. Kiểm tra tài liệu "SQL Queries" của EF Core trên learn.microsoft.com trước khi chọn hướng này. Với Query (đọc) của CQRS, dùng ADO.NET/Dapper là chuyện bình thường; không bắt buộc EF cho mọi thứ.

**Bẫy thường gặp.**

- Truy xuất xuôi dừng ở cấp 1 (đúng như ví dụ trong spec) khi có lot con: sót phạm vi. Luôn dùng bản đệ quy.
- Quên `GROUP BY`/`DISTINCT` cuối: cùng một serial xuất hiện nhiều lần khi nó dùng nhiều lot cùng nằm trong cây.
- Quên `MAXRECURSION` → dữ liệu lỗi có vòng làm truy vấn bị hủy ở 100 tầng với thông báo khó hiểu; hoặc đặt 0 thì treo.
- Trả 6.200 (hoặc 60.000) dòng lên UI tree một lần. Xem 2.6.

### 2.2b [v2] Truy xuất xuôi tới thùng, pallet và trạng thái giao hàng

Sau khi có tập serial bị ảnh hưởng, câu hỏi kế tiếp luôn là "chúng đang ở đâu". M3b đã ghi quan hệ serial → thùng → pallet; truy vấn xuôi chỉ cần **LEFT JOIN** thêm ở bước cuối (sau đệ quy, như nguyên tắc ở 2.2):

```sql
-- nối tiếp CTE LotTree ở trên
SELECT s.SerialNo, s.WoId, s.Status,
       c.CartonNo, p.PalletNo, p.Status AS PalletStatus, p.ShippedAt, p.ShipmentRef,
       Location = CASE
            WHEN p.Status = 'Shipped'  THEN 'Shipped'
            WHEN p.PalletNo IS NOT NULL THEN 'OnPallet'
            WHEN c.CartonNo IS NOT NULL THEN 'InCarton'
            WHEN s.Status = 'Completed' THEN 'CompletedUnpacked'
            ELSE 'Wip' END
FROM (SELECT DISTINCT sc.SerialNo FROM LotTree t JOIN SerialComponent sc ON sc.LotNo = t.LotNo) a
JOIN      Serial s        ON s.SerialNo = a.SerialNo
LEFT JOIN CartonSerial cs ON cs.SerialNo = s.SerialNo AND cs.UnpackedAt IS NULL   -- chỉ thùng hiện tại
LEFT JOIN Carton c        ON c.CartonNo = cs.CartonNo
LEFT JOIN Pallet p        ON p.PalletNo = c.PalletNo
OPTION (MAXRECURSION 20);
```

UI hiển thị **nút tóm tắt theo `Location`** (ví dụ "Shipped 3.600 · OnPallet 1.200 · InCarton 300 · WIP 1.100"), mở ra là danh sách phân trang. Nhóm `Shipped` có thêm cột `ShipmentRef` để gửi khách. Nút "Hold" có hai chế độ: Hold theo serial (mọi serial chưa giao) hoặc Hold theo pallet (chọn pallet trong kho, M7 mở rộng ra serial bên trong).

**Bẫy.** Quên điều kiện `cs.UnpackedAt IS NULL`: serial từng bị unpack hiện ra ở hai thùng. Dùng INNER JOIN thay vì LEFT JOIN: serial còn trong line biến mất khỏi kết quả, đúng nhóm nguy hiểm nhất.

### 2.3 Index cho truy vấn đệ quy và bảng lớn

**Nó là gì / vấn đề.** Không index, mỗi bước join trong CTE quét cả bảng. Với 100 triệu dòng `SerialComponent` (mục dưới), truy xuất mất hàng chục giây đến vài phút: đúng lúc audit/recall là lúc không được phép chậm. Đo cụ thể ở 2.7.

**Ước lượng kích thước.** Một line ~250 bo/giờ × 16 giờ × 26 ngày ≈ 104.000 bo/tháng; ~8 vật tư lot-controlled/bo → ~830.000 dòng `SerialComponent`/tháng/line. 2 line, 5 năm ≈ 100 triệu dòng. Chưa kể hồ sơ phải giữ lâu hơn theo hợp đồng (mục 1.2).

**Khái niệm cốt lõi: mỗi đường truy cập một index phủ (covering).**

| Truy vấn | Index | Ghi chú |
| --- | --- | --- |
| Xuôi: lot → serial | `SerialComponent (LotNo) INCLUDE (SerialNo)` | Seek trả ~2.500 dòng/reel, không key lookup nhờ INCLUDE |
| Ngược: serial → lot | Unique `(SerialNo, StationId, ItemId, LotNo)` | Đã có từ R9, cũng phục vụ truy xuất ngược |
| Đệ quy xuôi | `LotGenealogy (ParentLotNo) INCLUDE (ChildLotNo, Qty)` | Mỗi tầng đệ quy một seek |
| Đệ quy ngược | `LotGenealogy (ChildLotNo) INCLUDE (ParentLotNo, Qty)` | |
| Bắt đầu từ supplier lot | `MaterialLot (SupplierLot, ItemId)` | Bảng nhỏ nhưng đừng quét |
| Lot đang nạp trên máy | Unique filtered `MaterialLoad (StationId, ItemId) WHERE UnloadedAt IS NULL` | Vừa nhanh vừa **ép R6 ở tầng DB** |
| Gợi ý FIFO | `MaterialLot (ItemId, Status, ReceivedAt) INCLUDE (RemainingQty)` | |

**Snippet — cấu hình EF Core (đặt trong `IEntityTypeConfiguration`).**

```csharp
// SerialComponentConfiguration
b.HasIndex(x => new { x.SerialNo, x.StationId, x.ItemId, x.LotNo }).IsUnique();
b.HasIndex(x => x.LotNo).IncludeProperties(x => x.SerialNo);      // SQL Server: INCLUDE

// MaterialLoadConfiguration: mỗi (trạm, item) chỉ có 1 lot đang nạp
b.HasIndex(x => new { x.StationId, x.ItemId })
 .IsUnique()
 .HasFilter("[UnloadedAt] IS NULL");                               // filtered unique index
```

Ghi chú không chắc chắn: `IncludeProperties` và `HasFilter` là tiện ích của provider SQL Server/relational; nếu tên không khớp phiên bản EF bạn dùng, viết index bằng `migrationBuilder.Sql("CREATE INDEX ...")` trong migration.

**Bẫy thường gặp.**

- Index `(LotNo)` không INCLUDE `SerialNo`: seek xong còn 2.500 lần key lookup; vẫn chậm hơn nhiều. Luôn xem execution plan, tìm "Key Lookup".
- Thêm index làm chậm insert; `SerialComponent` chịu ghi liên tục mỗi lần scan. 2–3 index là hợp lý; đo lại độ trễ scan (mục tiêu M3: < 100 ms) sau khi thêm consumption.
- Dữ liệu tăng mãi: khi vượt vài chục triệu dòng, cân nhắc partition theo tháng (`ConsumedAt`) và nén PAGE, archive dữ liệu cũ sang bảng lạnh. Ngoài phạm vi MVP; chỉ cần nêu được khi phỏng vấn.

### 2.4 Verification và FIFO: logic thuần trong Domain

**Nó là gì.** Quyết định "reel này có được nạp lên máy này cho WO này không" là **quy tắc nghiệp vụ**, nên đặt trong Domain dưới dạng hàm thuần (không đọc DB, không gọi giờ hệ thống). Handler ở Application chịu trách nhiệm nạp dữ liệu (lot, danh sách yêu cầu vật tư của WO, các lot ứng viên FIFO), gọi hàm, rồi lưu kết quả. Cùng phong cách với hàm OEE thuần ở Module 5 và `RouteCheck` ở Module 3.

**Cách "ngây thơ".** Viết `if` rải trong controller/code-behind kiosk, đọc `DateTime.UtcNow` trực tiếp: không test được cho các trường hợp hết hạn, và mỗi nơi (kiosk, API, import) kiểm tra một kiểu.

**Khái niệm cốt lõi.**

- Kết quả có **ba mức**, không phải hai: `Accepted` (xanh), `Warning` (vàng: FIFO, cho phép nếu supervisor xác nhận), `Rejected` (đỏ: chặn cứng). Kèm `Code` máy đọc được để kiosk hiển thị chuỗi từ `.resx` và để đếm thống kê.
- Lỗi nghiệp vụ trả **HTTP 200 với `outcome = Rejected`**, giống `POST /stations/{id}/scan` của Module 3 (`{ result, message, nextStep }`); chỉ dữ liệu sai định dạng mới trả 400/`ProblemDetails`. Lý do: kiosk cần hiện màu và âm báo theo kết quả, không phải xử lý như lỗi kỹ thuật.
- **Override FIFO**: kiosk hiện cảnh báo; supervisor quẹt thẻ (nhập mã nhân viên) rồi nhập lý do. API kiểm tra người đó có role `Supervisor`, ghi `FifoOverrideBy`/`FifoOverrideReason` vào `MaterialLoad` và một dòng `MaterialVerificationLog`. Audit interceptor của Module 0 chỉ phủ bảng master và trạng thái WO nên phần này ghi rõ ràng bằng cột riêng.

**Snippet — hàm thuần (đã lược bớt kiểu phụ).**

```csharp
// Domain/Material/MaterialVerifier.cs
public enum VerifyOutcome { Accepted, Warning, Rejected }
public enum VerifyCode { Ok, LotNotFound, ItemNotInBom, ItemNotForStation, LotOnHold, LotNotIqcPassed /* [v2] */, LotEmpty, LotExpired, FifoOlderLotAvailable }
public sealed record VerifyResult(VerifyOutcome Outcome, VerifyCode Code, string? SuggestedLotNo = null);

public static class MaterialVerifier
{
    // reqs: yêu cầu vật tư lấy từ SNAPSHOT của WO (không phải BOM sống). stationSeq: bước của trạm/máy đang nạp.
    public static VerifyResult Verify(MaterialLot? lot, IReadOnlyCollection<WoMaterialRequirement> reqs,
                                      int stationSeq, IReadOnlyCollection<MaterialLot> candidates, DateTime utcNow)
    {
        if (lot is null) return Reject(VerifyCode.LotNotFound);
        var req = reqs.FirstOrDefault(r => r.ItemId == lot.ItemId);
        if (req is null) return Reject(VerifyCode.ItemNotInBom);                // R2
        if (req.OperationSeq != stationSeq) return Reject(VerifyCode.ItemNotForStation);
        if (lot.Status == LotStatus.Hold) return Reject(VerifyCode.LotOnHold);   // R4
        if (lot.Status is LotStatus.PendingIqc or LotStatus.IqcRejected)
            return Reject(VerifyCode.LotNotIqcPassed);                           // [v2] R15, không có override
        if (lot.Status == LotStatus.Consumed || lot.RemainingQty <= 0) return Reject(VerifyCode.LotEmpty);
        if (lot.ExpiryAt is { } exp && exp <= utcNow) return Reject(VerifyCode.LotExpired);   // R3

        var older = FifoAdvisor.FindOlder(lot, candidates, utcNow);              // R5
        return older is null
            ? new VerifyResult(VerifyOutcome.Accepted, VerifyCode.Ok)
            : new VerifyResult(VerifyOutcome.Warning, VerifyCode.FifoOlderLotAvailable, older.LotNo);
    }
    private static VerifyResult Reject(VerifyCode c) => new(VerifyOutcome.Rejected, c);
}

public static class FifoAdvisor   // FIFO theo ReceivedAt; muốn FEFO thì đổi khóa sắp xếp sang ExpiryAt
{
    public static MaterialLot? FindOlder(MaterialLot chosen, IEnumerable<MaterialLot> all, DateTime utcNow) =>
        all.Where(l => l.ItemId == chosen.ItemId && l.LotNo != chosen.LotNo
                    && l.Status == LotStatus.Available && l.RemainingQty > 0   // [v2] PendingIqc tự bị loại
                    && !(l.ExpiryAt <= utcNow)              // lot cũ đã hết hạn thì không gợi ý (nullable: null -> false)
                    && l.ReceivedAt < chosen.ReceivedAt)
           .OrderBy(l => l.ReceivedAt).FirstOrDefault();
}
```

**Ví dụ bảng test (xUnit `[Theory]`, tối thiểu 12 case).** **[v2]** Lot `PendingIqc` → Rejected/LotNotIqcPassed; lot `IqcRejected` → Rejected/LotNotIqcPassed; ứng viên FIFO đang `PendingIqc` → **không** được gợi ý. Đúng item/đúng trạm → Accepted. Item không có trong BOM → Rejected/ItemNotInBom. Item có trong BOM nhưng tiêu thụ ở trạm khác → ItemNotForStation. Lot Hold; lot Consumed; `RemainingQty = 0`; hết hạn đúng thời điểm (`exp == utcNow`, biên); còn hạn 1 giây. FIFO: có lot cũ hơn Available → Warning kèm `SuggestedLotNo`; lot cũ hơn đang Hold → **không** cảnh báo; lot cũ hơn đã hết hạn → không cảnh báo; cùng `ReceivedAt` → không cảnh báo (so sánh `<`, không phải `<=`).

**Bẫy thường gặp.**

- FIFO gợi ý một lot đang nạp trên máy khác (Available nhưng đang dùng). Handler nên loại các lot có `MaterialLoad` đang hoạt động khỏi `candidates`.
- Quên loại lot Hold khỏi ứng viên FIFO: kiosk bắt operator nạp lot không nạp được.
- So sánh hạn dùng theo giờ địa phương thay vì UTC; truyền `utcNow` từ `TimeProvider` (xem Module 5) để test biên hạn dùng không phụ thuộc đồng hồ thật.

### 2.4b [v2] Use case IQC: một kết quả cho cả supplier lot

**Nó là gì.** Command `RecordIqcResult(ItemId, SupplierLot, SampleQty, DefectQty, Result, DefectCode?, Note?)`, role `QC`. Trong **một transaction**: ghi một dòng `IqcRecord`, rồi đổi trạng thái mọi reel `PendingIqc` của `(ItemId, SupplierLot)` bằng một câu `UPDATE` set-based.

```csharp
await using var tx = await _db.Database.BeginTransactionAsync(ct);
var newStatus = cmd.Result == IqcResult.Pass ? LotStatus.Available : LotStatus.IqcRejected;
var affected = await _db.MaterialLots
    .Where(l => l.ItemId == cmd.ItemId && l.SupplierLot == cmd.SupplierLot && l.Status == LotStatus.PendingIqc)
    .ExecuteUpdateAsync(s => s.SetProperty(l => l.Status, newStatus), ct);
if (affected == 0) throw new DomainException("No lots pending IQC for this supplier lot.");   // đã kiểm rồi, hoặc sai mã
_db.IqcRecords.Add(IqcRecord.Create(cmd, lotCount: affected, _user.EmployeeCode, _time.GetUtcNow().UtcDateTime));
await _db.SaveChangesAsync(ct);
await tx.CommitAsync(ct);
```

Quy tắc kiểm tra (FluentValidation): `SampleQty > 0`, `DefectQty <= SampleQty`, `Fail` bắt buộc `DefectCode`, `Pass` với `DefectQty > 0` cần `Note` (ngoài đời: số lỗi vẫn trong mức chấp nhận của AQL).

**Bẫy.** (1) Kiểm từng reel riêng: QC phải bấm 10 lần cho một lô 10 reel; và reel nhập **sau** khi đã kiểm (giao bổ sung cùng supplier lot) sẽ ở lại `PendingIqc`. Đó là đúng: lần giao mới cần kiểm mới, màn hình IQC hiển thị lại supplier lot đó. (2) `ExecuteUpdate` bỏ qua audit interceptor: `IqcRecord` chính là hồ sơ audit của thao tác này. (3) Hai QC cùng bấm: lần hai cập nhật 0 dòng và nhận lỗi rõ ràng, không ghi `IqcRecord` thứ hai.

**Màn hình.** Back-office "IQC": danh sách `PendingIqc` **gom theo (Item, SupplierLot)** (số reel, ngày nhận, nhà cung cấp, hạn dùng), chọn một dòng → form kết quả. Kho in nhãn lot có dòng chữ "PENDING IQC" khi `PendingIqc`; IQC đạt thì không cần in lại nhãn vì kiosk đọc trạng thái từ MES, không từ nhãn.

### 2.5 Hook sự kiện giữa module và trừ tồn kho an toàn

**Nó là gì.** Khi scan OK (module Execution, M3), module Material phải ghi `SerialComponent`. **Domain event / notification** là cách module A thông báo "chuyện này vừa xảy ra" mà không biết ai lắng nghe; module B đăng ký xử lý. Trong monolith, đây là gọi trong cùng tiến trình (in-process), dùng `INotification` của MediatR (đã dùng ở Module 1; lưu ý điều khoản license MediatR từ v13 đã nêu ở đó, có thể tự viết dispatcher nhỏ thay thế).

**Cách "ngây thơ".**

1. Handler scan của M3 gọi thẳng `_materialService.Consume(...)`: Execution phụ thuộc Material; muốn bỏ Material (build MVP-2 trước) phải sửa code M3.
2. Material chạy job mỗi 5 giây quét `ScanLog` mới: trễ, dễ sót hoặc trùng, và scan đã "OK" trên kiosk nhưng chưa có hồ sơ vật tư.
3. DB trigger trên `ScanLog`: logic nghiệp vụ ẩn trong DB, khó test và khó debug.

**Khái niệm cốt lõi.** Thiết kế **hai nửa** cho một luồng:

- **Cửa chặn trước (gate, đồng bộ)**: trước khi ghi OK, M3 hỏi `IMaterialGate.CheckAsync(stationId, woId, seq)` (interface định nghĩa ở Application, Material hiện thực). Trả về danh sách vật tư thiếu/không hợp lệ. Có thiếu thì scan bị chặn `MATERIAL_NOT_LOADED` (R8). Gate cũng kiểm tra lot đang nạp **vẫn** hợp lệ: bị Hold sau khi nạp, hoặc hết hạn giữa ca. Có cờ cấu hình để tắt, giúp làm MVP-2 (M2, M3) mà chưa có M6.
- **Thông báo sau khi ghi (notification)**: ScanLog đã lưu → publish `SerialScannedOk` → handler của Material ghi `SerialComponent` + trừ tồn.
- **Cùng một transaction**: mở transaction ở handler scan, publish trong transaction, commit cuối cùng. Handler notification lỗi → cả scan rollback, kiosk báo lỗi. Đây là chủ ý: **scan OK mà thiếu hồ sơ vật tư là kết quả tệ nhất**, tệ hơn scan thất bại.

**Snippet — phía publish (trong handler scan của M3).**

```csharp
await using var tx = await _db.Database.BeginTransactionAsync(ct);
_db.ScanLogs.Add(log);
await _db.SaveChangesAsync(ct);                       // sau dòng này log.Id đã có (bigint identity)
await _publisher.Publish(                             // IPublisher của MediatR; handler chạy tuần tự, cùng DbContext
    new SerialScannedOk(serial.SerialNo, wo.Id, station.Id, step.Seq, log.Id, now), ct);
await tx.CommitAsync(ct);                             // nếu có lỗi trước dòng này: transaction bị dispose -> tự rollback
```

**Snippet — phía Material (handler) và trừ tồn atomic.**

```csharp
public sealed record SerialScannedOk(string SerialNo, int WoId, int StationId, int Seq, long ScanLogId, DateTime At) : INotification;

public sealed class RecordConsumptionHandler(MesDbContext db, IMaterialQueries q) : INotificationHandler<SerialScannedOk>
{
    public async Task Handle(SerialScannedOk e, CancellationToken ct)
    {
        // Các dòng vật tư tiêu thụ tại bước này, lấy từ snapshot WorkOrderMaterial, chỉ item IsLotControlled
        foreach (var line in await q.ConsumingLinesAsync(e.WoId, e.Seq, ct))
        {
            var load = await q.ActiveLoadAsync(e.StationId, line.ItemId, ct)
                       ?? throw new MaterialNotLoadedException(line.ItemId);   // gate đã chặn; đây là lưới an toàn
            db.SerialComponents.Add(new SerialComponent(e.SerialNo, load.LotNo, line.ItemId, line.QtyPer, e.StationId, e.ScanLogId, e.At));
            // Trừ tồn nguyên tử trong 1 câu lệnh, không đọc-rồi-ghi. Floor về 0; hết thì chuyển Consumed (không đè Hold)
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE MaterialLot
                SET RemainingQty = CASE WHEN RemainingQty >= {line.QtyPer} THEN RemainingQty - {line.QtyPer} ELSE 0 END,
                    Status       = CASE WHEN Status = 0 AND RemainingQty <= {line.QtyPer} THEN 2 ELSE Status END
                WHERE LotNo = {load.LotNo}", ct);
        }
        await db.SaveChangesAsync(ct);                         // ghi các SerialComponent trong cùng transaction
    }
}
```

Vì sao `UPDATE ... SET RemainingQty = RemainingQty - x` thay vì đọc entity, trừ, `SaveChanges`: hai scan đồng thời cùng trạm đọc cùng giá trị 100 rồi cùng ghi 96 (mất một lần trừ). Nguyên tắc concurrency đã học ở Module 2 (UPDLOCK/rowversion); ở đây một câu `UPDATE` tự thân là nguyên tử, không cần thêm khóa. Một (trạm, item) chỉ có một lot đang nạp (R6) nên tranh chấp trên một dòng lot rất thấp.

**Bẫy thường gặp.**

- `ExecuteSql...` chạy **ngoài change tracker** và **không đi qua** interceptor `SaveChanges` (audit của Module 0). Chấp nhận được ở đây vì `SerialComponent` mới là hồ sơ; nhưng đừng dùng cách này cho bảng cần audit.
- Nuốt exception trong handler (`try/catch` rồi log) cho êm: scan OK nhưng không có `SerialComponent`. Không làm.
- Số thứ tự handler và `SaveChanges` gọi nhiều lần trong một transaction: bảo đảm `DbContext` cùng scope, không tạo `DbContext` mới trong handler (sẽ nằm ngoài transaction).
- Mỗi scan thêm ~8 dòng insert + 8 câu `UPDATE`: đo lại thời gian scan (M3 đặt mục tiêu < 100 ms). Nếu chậm, gộp thành một câu `INSERT ... SELECT`/`UPDATE` theo lô thay vì vòng lặp.
- Scan lặp idempotent (Module 3): handler chỉ được publish khi scan **thực sự ghi mới**, không publish cho lần scan lặp.

### 2.6 WPF TreeView truy xuất lazy-load

**Nó là gì.** `TreeView` hiển thị dữ liệu phân cấp (bạn đã dùng cho cây BOM ở Module 1 với `HierarchicalDataTemplate`). **Lazy-load** = chỉ tải các nút con khi người dùng bấm mở nút đó.

**Vấn đề.** Cây BOM nhỏ (vài chục nút) nên nạp hết được. Cây truy xuất thì khác: truy xuất xuôi một lot ra 6.200 serial, mỗi serial ra 8 lot: tải toàn bộ là 50.000 nút, API chậm, WPF đứng hình. Người dùng cũng chỉ cần nhìn vài nhánh.

**Khái niệm cốt lõi.**

- Nút chưa tải có **một nút giả (placeholder)** để `TreeView` vẽ mũi tên mở rộng. Khi `IsExpanded` chuyển thành `true` lần đầu, gọi API, xóa placeholder, thêm nút thật.
- Để ViewModel biết nút được mở, bind `TreeViewItem.IsExpanded` hai chiều qua `ItemContainerStyle`.
- Kết quả xuôi rất lớn thì **đừng dùng cây cho danh sách phẳng**: hiển thị nút tóm tắt theo `Serial.Status` (Completed 4.900, InProcess 900...), mở ra thì phân trang; và có tab `DataGrid` phẳng (có xuất CSV để gửi khách). Cây dành cho truy xuất **ngược** (nhỏ, nhiều thuộc tính: lot, máy, người, giờ).
- Endpoint gợi ý cho lazy-load: `GET /trace/nodes/{kind}/{key}/children?skip=&take=`.
- Ràng buộc luồng: sau `await` trong ViewModel gọi từ UI thread, phần còn lại chạy lại trên UI thread (SynchronizationContext của WPF), nên sửa `ObservableCollection` an toàn. (Khác với sự kiện SignalR nhận trên luồng khác, Module 4.)

**Snippet — ViewModel (CommunityToolkit.Mvvm, mã gần đủ; kiểm tra tên attribute theo phiên bản bạn cài).**

```csharp
public partial class TraceNodeViewModel : ObservableObject
{
    private readonly ITraceApi _api; private bool _loaded;
    public string Title { get; } public string? Detail { get; } public NodeKind Kind { get; } public string Key { get; }
    public ObservableCollection<TraceNodeViewModel> Children { get; } = new();

    [ObservableProperty] private bool _isExpanded;          // sinh property IsExpanded + partial hook bên dưới

    public TraceNodeViewModel(ITraceApi api, NodeKind kind, string key, string title, string? detail, bool hasChildren)
    {
        (_api, Kind, Key, Title, Detail) = (api, kind, key, title, detail);
        if (hasChildren) Children.Add(new TraceNodeViewModel(api, NodeKind.Placeholder, "", "Loading...", null, false));
    }

    partial void OnIsExpandedChanged(bool value)            // gọi khi IsExpanded đổi
    {
        if (value && !_loaded) _ = LoadChildrenAsync();
    }

    private async Task LoadChildrenAsync()
    {
        _loaded = true;
        try
        {
            var dtos = await _api.GetChildrenAsync(Kind, Key);
            Children.Clear();
            foreach (var d in dtos) Children.Add(new TraceNodeViewModel(_api, d.Kind, d.Key, d.Title, d.Detail, d.HasChildren));
        }
        catch (Exception ex)                                 // lỗi mạng: cho phép thử lại lần mở sau
        {
            _loaded = false; Children.Clear();
            Children.Add(new TraceNodeViewModel(_api, NodeKind.Error, "", $"Load failed: {ex.Message}", null, false));
        }
    }
}
```

**Snippet — XAML.**

```xml
<TreeView ItemsSource="{Binding RootNodes}"
          VirtualizingStackPanel.IsVirtualizing="True"
          VirtualizingStackPanel.VirtualizationMode="Recycling">
  <TreeView.ItemContainerStyle>
    <Style TargetType="TreeViewItem">
      <Setter Property="IsExpanded" Value="{Binding IsExpanded, Mode=TwoWay}" />
    </Style>
  </TreeView.ItemContainerStyle>
  <TreeView.ItemTemplate>
    <HierarchicalDataTemplate ItemsSource="{Binding Children}">
      <StackPanel Orientation="Horizontal">
        <TextBlock Text="{Binding Title}" FontWeight="SemiBold" />
        <TextBlock Text="{Binding Detail}" Margin="12,0,0,0" Opacity="0.6" />
      </StackPanel>
    </HierarchicalDataTemplate>
  </TreeView.ItemTemplate>
</TreeView>
```

**Bẫy thường gặp.**

- Không bind `IsExpanded` hai chiều thì ViewModel không nhận được sự kiện mở.
- Chuỗi `Loading...` nên lấy từ `Strings.resx` (quy ước UI tiếng Anh + `.resx` của dự án), ở đây viết thẳng cho ngắn.
- Mở/đóng nhanh liên tục gây gọi API trùng: cờ `_loaded` đặt **trước** khi `await`.
- Ảo hóa (virtualization) của `TreeView` chỉ có tác dụng khi các thuộc tính trên được đặt và tree không nằm trong `ScrollViewer` bao ngoài; đo bằng cách mở nhánh 1.000 nút.

### 2.7 Đo hiệu năng truy xuất

Cùng kỹ thuật với Module 3 (seed lớn, đo trước/sau index), chỉ nêu phần khác. Mục tiêu spec: truy xuất < 1 giây.

```sql
-- Chỉ chạy trên DB test. Cột Serial sửa cho khớp schema Module 2 (SerialNo, WoId, ItemId, Status...).
-- 1) 625.000 serial, 8 vật tư mỗi serial = 5 triệu dòng; mỗi lot ~2.500 serial (khớp 1 reel), 2.000 lot.
;WITH K AS (SELECT TOP (8) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS k FROM sys.all_objects),
      L AS (SELECT TOP (250) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS j FROM sys.all_objects)
INSERT INTO MaterialLot (LotNo, ItemId, Qty, RemainingQty, SupplierLot, ReceivedAt, Status)
SELECT CONCAT(N'PERF-LOT-', k, N'-', j), 100 + k, 10000, 0, CONCAT(N'SUP-', j / 5), SYSUTCDATETIME(), 2 FROM K CROSS JOIN L;

;WITH N AS (SELECT TOP (625000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_objects a CROSS JOIN sys.all_objects b)
INSERT INTO Serial (SerialNo, WoId, ItemId, Status)  -- pseudo: bổ sung cột bắt buộc theo M2
SELECT CONCAT(N'PERF-', RIGHT(CONCAT(N'0000000', n), 7)), 1, 1, 3 FROM N;

;WITH S AS (SELECT SerialNo, ROW_NUMBER() OVER (ORDER BY SerialNo) AS rn FROM Serial WHERE SerialNo LIKE N'PERF-%'),
      K AS (SELECT TOP (8) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS k FROM sys.all_objects)
INSERT INTO SerialComponent (SerialNo, LotNo, ItemId, Qty, StationId, ScanLogId, ConsumedAt)
SELECT S.SerialNo, CONCAT(N'PERF-LOT-', K.k, N'-', (S.rn - 1) / 2500), 100 + K.k, 1, 1, 0, SYSUTCDATETIME()
FROM S CROSS JOIN K;

-- 2) Đo: chạy truy vấn xuôi với @SupplierLot = N'SUP-3' (5 lot/item) trước và sau khi tạo index (LotNo) INCLUDE (SerialNo)
SET STATISTICS IO, TIME ON;
```

Ghi vào `docs/perf.md`: số dòng, thời gian, `logical reads`, dạng plan (Index Seek hay Scan, có Key Lookup không), trước/sau index. Cột `ScanLogId = 0` chỉ để seed (nếu bạn giữ FK tới `ScanLog` thì bỏ FK cho DB đo hiệu năng).

### Sơ đồ luồng dữ liệu của module

```
(1) NHẬP LOT                       (2) NẠP VẬT TƯ (setup verification)              (3) GHI TIÊU HAO (khi scan OK)
WPF Back-office                    WPF Kiosk                                        WPF Kiosk / Simulator (trạm tự động)
 màn Receiving                      scan máy + (slot) + scan lot                     POST /stations/{id}/scan (M3)
      │ POST /material/lots              │ POST /material/loads                              │
      ▼                                  ▼                                                   ▼
 Application: ReceiveLotCommand     Application: LoadMaterialCommand                  Application: ScanCommandHandler (M3)
      │ (validate: FluentValidation)     │ nạp lot, snapshot WO, candidates FIFO             │ IMaterialGate.CheckAsync  --> chặn nếu thiếu lot
      ▼                                  ▼                                                   │ RouteCheck -> ghi ScanLog (trong transaction)
 Domain: MaterialLot.Receive()      Domain: MaterialVerifier.Verify()  (hàm thuần)           │ Publish SerialScannedOk
      │                                  │ Accepted/Warning/Rejected                         ▼
      ▼                                  ▼                                            Application (Material): RecordConsumptionHandler
   SQL Server: MaterialLot           SQL Server: MaterialLoad, MaterialVerificationLog        │ INSERT SerialComponent + UPDATE MaterialLot (atomic)
   + ZPL nhãn lot (kỹ thuật M2)         (đóng load cũ cùng item, R6)                          ▼
                                                                                        SQL Server (commit chung 1 transaction)

(4) TRUY XUẤT
WPF Back-office (màn Trace: nhập serial / lot / supplier lot)
      │ GET /trace/backward?serialNo=...      GET /trace/forward?lotNo=... | supplierLot=...       GET /trace/nodes/{kind}/{key}/children
      ▼
Application: TraceBackwardQuery / TraceForwardQuery ──▶ Infrastructure: TraceQueries (ADO.NET, recursive CTE)
      ▼
SQL Server: LotGenealogy (đệ quy, bảng nhỏ) ──join 1 lần──▶ SerialComponent (bảng lớn) ──▶ Serial
      ▼
TreeView lazy-load (ngược) + DataGrid (xuôi, nhóm theo Location, [v2] xuất Excel) + nút Hold lot (M6) / Hold serial, pallet (M7)
```
Không có MQTT/SignalR trong module này. Nếu muốn, có thể đẩy sự kiện "Lot Hold" qua SignalR để kiosk hiện cảnh báo ngay (mở rộng, cách làm ở Module 4).

---

## 3. Kế hoạch thực hiện

Tổng khoảng 28 giờ (2,5–3 tuần với 1,5–2 giờ/ngày; **[v2]** +3 giờ: bước 2b IQC, truy xuất tới thùng/pallet ở bước 7, xuất Excel ở bước 8). Bước 1–5 làm theo thứ tự; bước 6 (seed) làm trước bước 7; bước 10 tùy chọn (không tính vào 28 giờ).

**Dữ liệu kiểm thử chuẩn (dùng từ bước 6 trở đi).** Một WO 200 serial của `PCB-A`. Chọn số sao cho tính tay được:

| Lot | Item | Quan hệ | Serial dùng |
| --- | --- | --- | --- |
| `L-CAP-A` | `CAP-100NF` | SupplierLot `SUP-S1` | serial 1–120 |
| `L-CAP-P` | `CAP-100NF` | SupplierLot `SUP-S1`, reel gốc đã chia | không dùng trực tiếp |
| `L-CAP-B` | `CAP-100NF` | con của `L-CAP-P` (`LotGenealogy`) | serial 121–200 |
| `L-RES-A` | `RES-100K` | SupplierLot `SUP-R1` | cả 200 serial |

**[v2]** Seed thêm trạng thái đóng gói (cần M3b): serial 1–100 nằm trong 10 thùng trên pallet `P-1` đã `Shipped`; serial 101–150 trong 5 thùng chưa lên pallet; serial 151–200 còn WIP.

Kết quả mong đợi (đây chính là test oracle): xuôi từ `L-CAP-A` = 120 serial (**[v2]** gồm Shipped 100, InCarton 20); xuôi từ `L-CAP-B` = 80; xuôi từ `L-CAP-P` = 80 (đi qua genealogy, cấp 1); xuôi từ supplier lot `SUP-S1` + `CAP-100NF` = 200; xuôi từ `L-RES-A` = 200; ngược từ serial 150 = {`L-CAP-B` (cấp 0), `L-CAP-P` (cấp 1), `L-RES-A` (cấp 0)}; ngược từ serial 50 = {`L-CAP-A`, `L-RES-A`}.

- [ ] **Bước 1 — Mô hình dữ liệu, migration, index**
  Mục tiêu: 5 bảng có đủ ràng buộc và index của mục 2.3.
  Việc cần làm: (1) vẽ ERD Mermaid trong `docs/` nối `Item`, `WorkOrderMaterial`, `Serial` với các bảng mới; (2) viết entity `MaterialLot` (**[v2]** enum có `PendingIqc`, `IqcRejected`), `MaterialLoad`, `SerialComponent`, `LotGenealogy`, `MaterialVerificationLog`, **[v2]** `IqcRecord`; (3) bổ sung `OperationSeq`, `QtyPer` vào `WorkOrderMaterial` nếu Module 2 chưa có (snapshot phải mang thông tin tiêu thụ ở công đoạn nào); (4) configuration: unique `LotNo`, unique `(SerialNo, StationId, ItemId, LotNo)`, filtered unique index của `MaterialLoad`, các index INCLUDE; (5) constraint `CHECK (RemainingQty >= 0 AND RemainingQty <= Qty)`; (6) migration.
  File: `src/MiniMes.Domain/Material/*.cs`, `src/MiniMes.Infrastructure/Persistence/Configurations/Material*Configuration.cs`, `docs/erd-material.md`.
  Kiểm tra: `dotnet ef database update` chạy sạch; trong SSMS thử chèn hai `MaterialLoad` đang mở cùng `(StationId, ItemId)` → bị unique index từ chối; thử `RemainingQty = -1` → bị CHECK từ chối.
  Ước lượng: 3 giờ.

- [ ] **Bước 2 — Nhập lot và in nhãn lot**
  Mục tiêu: kho tạo lot và in nhãn.
  Việc cần làm: command `ReceiveLotCommand` (Item phải `IsLotControlled`, Qty > 0, `ExpiryAt` > ngày nhận, `SupplierLot` bắt buộc) + FluentValidation; sinh `LotNo` không trùng bằng SEQUENCE như sinh serial ở Module 2 (ví dụ `L-{itemCode}-{yyMMdd}-{seq}`); chế độ nhập "N reel cùng supplier lot" tạo N lot; sinh ZPL nhãn lot (Code128 của `LotNo`, kèm mã hàng, Qty, hạn dùng, xem trước qua Labelary như Module 2); màn Back-office "Receiving".
  File: `src/MiniMes.Application/Material/ReceiveLot/*`, `src/MiniMes.Api/Controllers/MaterialLotsController.cs`, `src/MiniMes.BackOffice.Wpf/Views/ReceivingView.xaml`.
  **[v2]** Lot mới nhập: `PendingIqc`, trừ Item `SkipIqc` → `Available`; nhãn lot `PendingIqc` có dòng "PENDING IQC".
  Kiểm tra: nhập 3 reel → có 3 dòng `MaterialLot` **[v2]** `PendingIqc` (nhập `PACK-BAG` → `Available`), Qty = RemainingQty; nhập Item không lot-controlled → lỗi `ProblemDetails` dễ hiểu; nhãn ZPL xem trước đúng.
  Ước lượng: 3 giờ.

- [ ] **Bước 2b — [v2] IQC**
  Mục tiêu: QC duyệt hoặc từ chối cả supplier lot; lot chưa đạt không nạp được.
  Việc cần làm: `RecordIqcResultCommand` + validator + handler (mục 2.4b); `GET /material/iqc/pending` gom theo `(ItemId, SupplierLot)`; màn Back-office `IqcView` (role QC); seed `DefectCode` loại `Incoming` (ví dụ `INC-WRONG-VALUE`, `INC-DAMAGED-REEL`).
  File: `src/MiniMes.Application/Material/Iqc/*`, `src/MiniMes.Domain/Material/IqcRecord.cs`, `src/MiniMes.BackOffice.Wpf/Views/IqcView.xaml`, `tests/MiniMes.IntegrationTests/Material/IqcTests.cs`.
  Kiểm tra: nhập 3 reel cùng supplier lot → IQC Pass → cả 3 `Available`, 1 dòng `IqcRecord` có `LotCount = 3`; supplier lot khác → Fail → `IqcRejected`; gọi IQC lần hai cho supplier lot đã kiểm → lỗi, không thêm `IqcRecord`; Operator gọi API → 403.
  Ước lượng: 2 giờ.

- [ ] **Bước 3 — Domain: MaterialVerifier + FifoAdvisor + unit test**
  Mục tiêu: quy tắc R2–R5 chạy được không cần DB.
  Việc cần làm: viết hàm ở mục 2.4; bảng test `[Theory]` ≥ 12 case (mục 2.4, gồm 3 case IQC); truyền `utcNow` qua tham số.
  File: `src/MiniMes.Domain/Material/MaterialVerifier.cs`, `FifoAdvisor.cs`, `tests/MiniMes.UnitTests/Material/MaterialVerifierTests.cs`.
  Kiểm tra: `dotnet test` xanh; cố ý sửa `<` thành `<=` ở FIFO → có test đỏ.
  Ước lượng: 2,5 giờ.

- [ ] **Bước 4 — Kiosk nạp vật tư**
  Mục tiêu: operator nạp/đổi reel, supervisor override FIFO.
  Việc cần làm: `LoadMaterialCommand` (scan mã máy + lot + tùy chọn slot) → handler nạp lot, yêu cầu vật tư từ snapshot WO, candidates FIFO (loại lot Hold, đang nạp nơi khác) → gọi `MaterialVerifier` → Accepted: đóng `MaterialLoad` cũ cùng item, mở `MaterialLoad` mới; Warning: trả về cảnh báo, chờ request thứ hai có `supervisorBadge` + `reason`; Rejected: chỉ ghi log. Mọi trường hợp ghi `MaterialVerificationLog`. Unload có nhập số còn lại thực tế (hiệu chỉnh `RemainingQty`, R10). Màn Kiosk "Load material": ô scan luôn giữ focus (kỹ thuật Module 3), nền xanh/vàng/đỏ, chuỗi trong `Strings.resx`.
  File: `src/MiniMes.Application/Material/LoadMaterial/*`, `src/MiniMes.Contracts/Material/*.cs`, `src/MiniMes.Kiosk.Wpf/Views/LoadMaterialView.xaml`.
  Kiểm tra (tay, theo tình huống 1.3): scan `RES-10K` khi WO chỉ cần `RES-100K` → đỏ, có dòng Rejected trong log; scan lot mới khi còn lot cũ → vàng; supervisor xác nhận → xanh, `MaterialLoad` có `FifoOverrideBy`; nạp lot đã hết hạn (đặt `ExpiryAt` trong quá khứ) → đỏ, không có nút override; **[v2]** nạp lot `PendingIqc` → đỏ "Lot not IQC passed", không có nút override.
  Ước lượng: 4 giờ.

- [ ] **Bước 5 — Gate và ghi tiêu hao khi scan OK**
  Mục tiêu: mỗi scan OK sinh `SerialComponent`; thiếu lot thì chặn scan.
  Việc cần làm: interface `IMaterialGate` (Application) + hiện thực; sửa handler scan (M3) để gọi gate, mở transaction, publish `SerialScannedOk`; `RecordConsumptionHandler` (mục 2.5); cờ cấu hình `Material:EnforceLoadedMaterial`; kiểm tra publish chỉ khi scan mới, không cho scan lặp (idempotent).
  File: `src/MiniMes.Application/Material/Consumption/*`, sửa `src/MiniMes.Application/Execution/Scan/ScanCommandHandler.cs`.
  Kiểm tra: integration test (Testcontainers): (a) nạp lot rồi scan 10 serial → 10 dòng `SerialComponent` mỗi item, `RemainingQty` giảm đúng `10 × QtyPer`; (b) chưa nạp lot → scan bị chặn `MATERIAL_NOT_LOADED`, không có `ScanLog`; (c) scan lặp cùng serial → không thêm `SerialComponent`; (d) 2 request scan song song cùng trạm → tổng trừ chính xác; (e) lot Hold sau khi nạp → scan bị chặn; (f) trừ vượt số dư → `RemainingQty = 0`, lot `Consumed`, không âm; (g) ép handler ném lỗi → không có `ScanLog` (rollback).
  Ước lượng: 3 giờ.

- [ ] **Bước 6 — Seed kịch bản truy xuất**
  Mục tiêu: dữ liệu chuẩn bảng ở đầu mục 3 để test và demo bước 8.
  Việc cần làm: class `TraceabilitySeed` (dùng cho integration test và lệnh CLI `--seed-trace` của API/Simulator), có thể dựng lại nhiều lần (xóa và tạo lại trong DB test).
  File: `tests/MiniMes.IntegrationTests/Support/TraceabilitySeed.cs`.
  Kiểm tra: đếm bằng SQL: 200 serial, 400 dòng `SerialComponent` (mỗi serial một tụ + một điện trở), 1 dòng `LotGenealogy`.
  Ước lượng: 1,5 giờ.

- [ ] **Bước 7 — Truy vấn ngược/xuôi và test**
  Mục tiêu: hai truy vấn recursive CTE đúng test oracle.
  Việc cần làm: `TraceQueries` (ADO.NET, mục 2.2, **[v2]** phần cuối theo 2.2b có `CartonNo`, `PalletNo`, `Location`) với hàm `ForwardByLot`, `ForwardBySupplierLot`, `Backward`; mapping ra DTO có `Depth`, `StationId`, `ConsumedAt`, operator (join `ScanLog`), người nạp (join `MaterialLoad`); test theo số liệu chuẩn ở đầu mục 3, thêm ca lot không tồn tại (danh sách rỗng, không lỗi) và ca có vòng cố ý chèn (`MAXRECURSION`/depth guard dừng, không treo).
  File: `src/MiniMes.Infrastructure/Traceability/TraceQueries.cs`, `tests/MiniMes.IntegrationTests/Traceability/TraceQueriesTests.cs`.
  Kiểm tra: mọi số ở test oracle khớp; serial không lặp trong kết quả xuôi; **[v2]** số theo `Location` khớp seed; serial đã unpack khỏi thùng A rồi đóng vào thùng B chỉ hiện thùng B.
  Ước lượng: 3,5 giờ.

- [ ] **Bước 8 — API truy xuất và màn hình Trace**
  Mục tiêu: QC nhập serial hoặc lot và thấy kết quả.
  Việc cần làm: endpoints `GET /trace/backward`, `/trace/forward`, `/trace/nodes/{kind}/{key}/children`; policy role `QC`/`Supervisor`/`Admin`; WPF `TraceView`: ô nhập, chọn Backward/Forward, `TreeView` lazy-load (mục 2.6) cho backward, tab `DataGrid` **[v2]** nhóm theo `Location` + nút Export Excel (`GET /trace/forward.xlsx`, dùng lại ClosedXML như M5 mục 2.7) cho forward, nút "Hold lot" (đặt `MaterialLot.Status = Hold` cho lot xuất phát và các lot cùng supplier lot); nút "Hold all serials" và **[v2]** "Hold pallets" gọi endpoint của Module 7 (bật khi M7 xong, trước đó hiển thị vô hiệu kèm tooltip).
  File: `src/MiniMes.BackOffice.Wpf/Views/TraceView.xaml`, `ViewModels/TraceViewModel.cs`, `TraceNodeViewModel.cs`, `src/MiniMes.Client.Core/Api/TraceApi.cs`.
  Kiểm tra (theo demo spec v2 bước 12): chạy seed, nhập `L-CAP-B` → thấy 80 serial (nhóm theo Location), Hold lot → nạp `L-CAP-B` lên máy bị chặn với `LotOnHold`; file Excel mở được, có cột Carton/Pallet/ShipmentRef.
  Ước lượng: 4,5 giờ.

- [ ] **Bước 9 — Đo hiệu năng**
  Mục tiêu: truy xuất < 1 giây với 5 triệu dòng.
  Việc cần làm: chạy script seed lớn (mục 2.7); đo trước/sau index; kiểm tra plan; đo thời gian của scan (M3) có ghi tiêu hao so với không ghi.
  File: `docs/perf.md` (thêm mục Traceability).
  Kiểm tra: có bảng số đo trước/sau; truy xuất xuôi 1 supplier lot < 1 giây.
  Ước lượng: 1,5 giờ.

- [ ] **Bước 10 (tùy chọn) — Mở rộng**
  Chọn theo hứng thú: (a) bảng `MachineSlot(MachineId, SlotNo, ItemId)` để verification kiểm tra theo slot (mục 1.5); (b) trạm Repair ghi `SerialComponent` cho linh kiện thay (nối `RepairLog.ComponentReplaced` của M3); (c) trang "audit pack" xuất PDF/CSV hồ sơ truy xuất một serial; (d) SignalR đẩy sự kiện Hold lot lên kiosk (Module 4); (e) **[v2] MSL** (linh kiện nhạy ẩm, chuẩn IPC/JEDEC J-STD-033): Item có `MslLevel` và `FloorLifeHours`; kiosk có thao tác "mở túi" ghi `OpenedAt`; quá `FloorLifeHours` thì verification chặn (`LotFloorLifeExceeded`) cho tới khi sấy (bake) lại. Câu hỏi rất "SMT", có thể chỉ trình bày trong phỏng vấn nếu không code.
  Ước lượng: 3 giờ.

### Nếu bị kẹt

- **CTE ra rỗng.** Gần như luôn là nhầm hướng. In từng tầng: `SELECT * FROM LotGenealogy WHERE ParentLotNo = 'L-CAP-P'`. Kiểm tra dữ liệu seed đã đúng chiều (Parent là reel gốc).
- **Lỗi 530 "maximum recursion 100 has been exhausted".** Dữ liệu có vòng hoặc cây thật sự sâu. Tìm vòng: chạy CTE với cột `Path` nối chuỗi các `LotNo` đã đi qua và lọc những dòng có `LotNo` đã nằm trong `Path`.
- **Serial lặp trong kết quả xuôi.** Thiếu `GROUP BY`/`DISTINCT` cuối (đồ thị hình thoi).
- **Truy vấn chậm.** Bật `SET STATISTICS IO ON` và xem plan thực tế: có `Clustered Index Scan` trên `SerialComponent` hoặc `Key Lookup` số lượng lớn là thiếu index INCLUDE. Cập nhật thống kê nếu vừa seed hàng triệu dòng (`UPDATE STATISTICS SerialComponent`).
- **EF Core báo "Incorrect syntax near the keyword 'WITH'".** Bạn đang dùng `FromSql`/`SqlQuery` và EF bọc CTE vào subquery. Chuyển sang ADO.NET như mục 2.2.
- **Scan OK nhưng không có `SerialComponent`.** Kiểm tra: handler đã đăng ký chưa (MediatR quét assembly Application?), event có publish không, `BomLine.OperationSeq` của item có khớp bước trạm không, item có `IsLotControlled = true` không.
- **`RemainingQty` không khớp phép tính tay.** Có nhiều `MaterialLoad` đang mở cho cùng item (unique filtered index chưa được tạo trong migration), hoặc scan lặp làm trừ hai lần.
- **TreeView không mở được nút / không gọi API.** Thiếu `Mode=TwoWay` cho `IsExpanded`, hoặc quên nút giả (không có con thì không có mũi tên). Đặt breakpoint trong `OnIsExpandedChanged`.
- **Test đồng thời không ổn định.** Mỗi request phải có `DbContext` riêng (scope riêng); không dùng chung một `DbContext` cho hai `Task`.

---

## 4. Vì sao chọn giải pháp này

| Quyết định | Đã chọn | Phương án khác đã cân nhắc | Vì sao không chọn | Khi nào nên đổi |
| --- | --- | --- | --- | --- |
| Cách có được dữ liệu truy xuất | **Ghi `SerialComponent` tại lúc scan OK** (write-time) | Suy ra lúc truy vấn bằng cách join `ScanLog` với `MaterialLoad` theo khoảng thời gian | Vỡ khi lệch giờ, sửa `LoadedAt`, hai lot cùng item nạp sát nhau; không chứng minh được với auditor | Không đổi. Có thể **thêm** kiểm tra chéo bằng cách suy ra theo thời gian để phát hiện sai lệch |
| Lưu trữ đồ thị | Bảng quan hệ (adjacency list): `SerialComponent`, `LotGenealogy` | Graph DB (Neo4j); bảng graph của SQL Server (`NODE`/`EDGE`) | Thêm hạ tầng mới; SQL Server là stack duy nhất chốt; độ sâu thực tế nhỏ (2–4 cấp), CTE đủ nhanh | Đồ thị sâu và dày (hàng chục cấp, truy vấn quan hệ phức tạp): thử bảng graph của SQL Server hoặc graph DB |
| Truy vấn nhiều cấp | Recursive CTE chạy trên bảng nhỏ `LotGenealogy` rồi join 1 lần | Closure table (lưu sẵn mọi cặp tổ tiên–hậu duệ); vòng lặp trong C# | Closure table phải bảo trì khi chèn cạnh, dễ sai; vòng lặp C# là N+1 round-trip | Truy xuất là đường nóng được gọi hàng nghìn lần/ngày với đồ thị lớn: closure table hoặc bảng kết quả nhớ đệm |
| Đơn vị lot NVL | **1 reel = 1 `MaterialLot`**, giữ `SupplierLot` để gom | Lot = supplier lot, reel là đơn vị con | Tiêu hao và số còn lại theo từng reel; lot to hơn thì không biết reel nào đang nạp | Vật tư đổ rời (kem hàn theo hũ, dung dịch theo thùng): hũ/thùng là lot |
| Kiểm tra khi nạp | Chặn cứng lỗi chất lượng (sai item, hết hạn, Hold); **cảnh báo + override có ghi nhận** cho FIFO | Chặn cứng mọi thứ, hoặc chỉ cảnh báo | Chặn cứng FIFO khiến người dùng lách quy trình; chỉ cảnh báo cho lỗi chất lượng thì vô nghĩa | Khách hàng bắt buộc FIFO/FEFO cứng: bỏ override, hoặc cho override chỉ role Quality |
| Tồn còn lại | Cột `RemainingQty` (lý thuyết), trừ nguyên tử bằng một câu `UPDATE` | Tính `Qty − SUM(SerialComponent)` khi cần | `SUM` mỗi lần scan/hiển thị tốn kém và vẫn là số lý thuyết | Khi có kiểm kê thật (M9): số lý thuyết chỉ để tham khảo, sổ kho mới quyết định |
| Liên kết với luồng scan | Gate đồng bộ + `INotification` MediatR **trong cùng transaction** | Gọi trực tiếp service; message bus/outbox; job quét `ScanLog` | Gọi trực tiếp gắn chặt module; bus/outbox thừa cho monolith và cho phép "scan OK mà chưa có hồ sơ"; job quét trễ và sót | Tách Material thành dịch vụ riêng: Transactional Outbox (đã nêu ở M8) |
| **[v2]** Đơn vị IQC | Theo `(ItemId, SupplierLot)`, áp cho mọi reel đang chờ | Theo từng reel; theo phiếu nhập kho (receipt) | Từng reel: QC bấm quá nhiều, không đúng thực tế lấy mẫu; theo phiếu nhập cần bảng phiếu nhập (WMS, ngoài phạm vi) | Có WMS/ERP gửi phiếu nhập: IQC theo phiếu nhập + supplier lot |
| **[v2]** Trạng thái IQC | Giá trị `PendingIqc`/`IqcRejected` trong `MaterialLot.Status` | Cột riêng `IqcStatus` | Verification chỉ cần một cột trạng thái để quyết định "dùng được không"; hai cột phải kết hợp ở mọi chỗ | Khi một lot có thể vừa Hold vừa chờ IQC: tách cột |
| Truy vấn truy xuất | ADO.NET/SQL tham số hóa cho phần đọc | LINQ/EF Core; Dapper | EF Core không bọc được CTE; Dapper ổn nhưng thêm phụ thuộc chỉ để chạy vài câu | Có nhiều truy vấn đọc tương tự: Dapper cho gọn |
| Phạm vi ảnh hưởng | Ở mức lot (bao phủ rộng hơn thực tế) | Truy đến từng linh kiện/vị trí (refdes) | Cần dữ liệu từ máy gắp thật (log từng placement), ngoài phạm vi | Khách yêu cầu khoanh vùng hẹp hơn nữa (lot bị lỗi chỉ một phần cuộn): dùng thời gian nạp để thu hẹp |

**Ghi tại lúc sự kiện thay vì suy ra sau.** Câu trả lời để nói trong phỏng vấn: "Truy xuất là hồ sơ, không phải phép tính. Tôi ghi `SerialComponent` trong cùng transaction với scan, nên một serial đã OK thì chắc chắn có hồ sơ vật tư của nó; nếu ghi hồ sơ lỗi thì scan cũng không thành. Suy ra sau bằng thời gian thì ai cũng có thể hỏi 'anh chứng minh thế nào?' và tôi không có cách trả lời." Lưu ý đánh đổi: tốn thêm ~8 dòng insert mỗi scan (~100 triệu dòng sau 5 năm) nên phải có index đúng và kế hoạch archive.

**Đệ quy trên bảng nhỏ, join bảng lớn một lần.** Genealogy (lot sinh lot) chỉ vài nghìn dòng, còn `SerialComponent` hàng chục triệu dòng. Tôi để CTE chỉ chạy trên `LotGenealogy` để có tập lot hậu duệ, rồi join một lần với `SerialComponent` bằng index `(LotNo) INCLUDE (SerialNo)`. Nhờ đó truy xuất xuôi giữ dưới 1 giây và độ sâu chỉ tốn thêm một seek mỗi tầng. Có ba lớp chống vòng: ràng buộc lúc ghi, giới hạn độ sâu trong CTE và `MAXRECURSION`.

**Phân biệt lỗi chất lượng và lỗi chính sách.** Reel sai item hoặc hết hạn là lỗi chất lượng: chặn cứng, không override, vì hậu quả nằm ở khách hàng. FIFO là chính sách tồn kho: cảnh báo, supervisor override được nhưng phải ghi ai, vì sao. Đây cũng là cách nói với auditor: hệ thống chặn sai lầm, ghi lại ngoại lệ có chủ ý, không cho phép ngoại lệ vô danh.

---

## 5. Hoàn thành module

### 5.1 Definition of Done

- [ ] Kịch bản demo (spec v2 bước 3–4) chạy đúng: nhập 3 reel → `PendingIqc` → nạp bị chặn → IQC đạt → nạp lên máy, scan xác nhận đúng vật tư.
- [ ] **[v2]** IQC áp cho cả supplier lot trong một transaction, `IqcRecord` insert-only; lot `IqcRejected` không nạp được, không có override.
- [ ] Kịch bản demo (spec v2 bước 12) chạy đúng: reel lỗi → truy xuất xuôi → danh sách serial khớp dữ liệu seed, **[v2]** có thùng/pallet/đã giao → Hold lot (Hold serial, pallet khi có M7).
- [ ] Nạp reel sai item, sai trạm, hết hạn, đang Hold: bị chặn, có dòng trong `MaterialVerificationLog`.
- [ ] Nạp lot mới khi còn lot cũ: cảnh báo vàng; supervisor override có ghi `FifoOverrideBy` và lý do.
- [ ] Scan OK ở trạm tiêu thụ vật tư sinh đúng số dòng `SerialComponent`, trừ đúng `RemainingQty`; scan lặp không ghi trùng; chưa nạp lot thì scan bị chặn.
- [ ] Scan và ghi tiêu hao nằm trong cùng transaction (test rollback).
- [ ] Truy xuất xuôi và ngược đúng toàn bộ test oracle ở mục 3, gồm genealogy 2 cấp và điểm bắt đầu là supplier lot.
- [ ] Unit test `MaterialVerifier`/`FifoAdvisor` ≥ 10 case; integration test Testcontainers cho consumption và truy xuất.
- [ ] Truy xuất < 1 giây với 5 triệu dòng `SerialComponent`; số đo trước/sau index trong `docs/perf.md`.
- [ ] WPF: cây truy xuất ngược lazy-load, danh sách xuôi nhóm theo Location có **[v2]** Export Excel, màn IQC, chuỗi UI trong `Strings.resx`.
- [ ] `SerialComponent` và `MaterialVerificationLog` không có đường code nào UPDATE/DELETE.
- [ ] Viết vào `docs/incidents.md` ít nhất một "sự cố" gặp khi làm module (ví dụ nhầm hướng Parent/Child, lỗi bọc CTE của EF) để kể khi phỏng vấn.

### 5.2 Tự kiểm tra

1. Vì sao nguyên vật liệu quản lý theo lot còn thành phẩm theo serial? Cái gì nối hai thứ này?
<details><summary>Gợi ý</summary>Chi phí và khả thi: hàng triệu linh kiện nhỏ, giá trị thấp, không gắn được định danh riêng; thành phẩm giá trị cao, khách trả về từng cái. Cầu nối là `SerialComponent` (serial X dùng lot Y).</details>

2. Supplier lot khác `LotNo` của MES thế nào? Vì sao truy xuất xuôi phải bắt đầu được từ supplier lot?
<details><summary>Gợi ý</summary>Supplier lot do nhà cung cấp gán, một supplier lot thành nhiều reel = nhiều `MaterialLot`. Nhà cung cấp báo lỗi bằng số của họ, nên phải tìm hết các reel MES có cùng supplier lot rồi mới truy xuất xuôi từng reel.</details>

3. Vì sao kiểm tra "item có trong BOM" chưa đủ để chặn nạp nhầm reel? Cách khắc phục?
<details><summary>Gợi ý</summary>Nếu BOM chứa cả hai mã giống nhau (10K và 100K) ở cùng công đoạn thì nạp nhầm giữa hai mã vẫn qua. Cần kiểm tra theo slot: bảng slot → item từ chương trình máy. MVP ghi là giới hạn, làm ở bước 10.</details>

4. Ghi `SerialComponent` lúc scan OK khác gì suy ra lot từ thời gian nạp? Vì sao chọn cách đầu?
<details><summary>Gợi ý</summary>Suy ra theo thời gian sai khi lệch đồng hồ, sửa tay, đổi reel sát nhau và không chứng minh được. Ghi tại sự kiện là bằng chứng gốc, cùng transaction với scan.</details>

5. Trong truy xuất xuôi, vì sao đệ quy trên `LotGenealogy` rồi mới join `SerialComponent`? Dùng `UNION` thay `UNION ALL` trong CTE đệ quy được không?
<details><summary>Gợi ý</summary>`LotGenealogy` nhỏ, `SerialComponent` rất lớn: đệ quy trên bảng nhỏ, join bảng lớn một lần. SQL Server chỉ cho `UNION ALL` giữa anchor và recursive member; lọc trùng ở truy vấn ngoài bằng `DISTINCT`/`GROUP BY`.</details>

6. Đồ thị genealogy có vòng thì chuyện gì xảy ra và bạn chặn ở đâu?
<details><summary>Gợi ý</summary>CTE lặp đến khi chạm `MAXRECURSION` (lỗi 530) hoặc treo nếu đặt 0. Chặn ở ba lớp: kiểm tra lúc ghi (lot cha phải có trước lot con, DFS như M1), giới hạn `Depth` trong CTE, `OPTION (MAXRECURSION n)`.</details>

7. Vì sao `UPDATE MaterialLot SET RemainingQty = RemainingQty - @q` thay vì đọc entity rồi `SaveChanges`? Nó có đi qua interceptor audit không?
<details><summary>Gợi ý</summary>Đọc-rồi-ghi mất lần trừ khi hai scan đồng thời (lost update). Một câu `UPDATE` tự nguyên tử. `ExecuteSql`/`ExecuteUpdate` bỏ qua change tracker và interceptor `SaveChanges`, nên không sinh `AuditLog`; chấp nhận vì `SerialComponent` là hồ sơ.</details>

8. Vì sao gate chặn scan khi chưa nạp lot, và handler consumption chạy chung transaction với scan?
<details><summary>Gợi ý</summary>Scan OK mà thiếu hồ sơ vật tư là kết quả tệ nhất, sau này không dựng lại được. Cùng transaction thì hoặc có cả scan và hồ sơ, hoặc không có gì. Có cờ cấu hình để làm MVP trước khi có M6.</details>

9. Vì sao không tải hết cây truy xuất xuôi lên `TreeView`? Thay bằng gì?
<details><summary>Gợi ý</summary>Hàng nghìn serial nhân 8 lot = hàng chục nghìn nút: chậm, tốn bộ nhớ. Dùng tóm tắt theo Status + phân trang, `DataGrid` phẳng có Export CSV; cây lazy-load (nút giả + `IsExpanded` hai chiều) dành cho truy xuất ngược.</details>

10. Truy xuất theo lot cho phạm vi rộng hơn thực tế. Có chấp nhận được không, và thu hẹp thế nào?
<details><summary>Gợi ý</summary>Chấp nhận trong containment (thà dư còn hơn sót). Thu hẹp bằng khoảng thời gian nạp/gỡ (`MaterialLoad`) hoặc log từng placement nếu có dữ liệu từ máy thật; nếu lot lỗi chỉ một phần cuộn, cần bằng chứng từ nhà cung cấp trước khi thu hẹp.</details>

11. **[v2]** Vì sao IQC ghi theo supplier lot mà không theo từng reel? Reel giao bổ sung sau khi đã kiểm thì sao?
<details><summary>Gợi ý</summary>Ngoài đời QC lấy mẫu một lô giao hàng, không kiểm hết từng reel; kết quả áp cho cả lô. Reel giao sau là lần giao mới, vẫn `PendingIqc` và hiện lại trên màn hình IQC để kiểm lần mới.</details>

12. **[v2]** Truy xuất xuôi ra 6.200 serial. Vì sao phải biết serial nào đã giao, serial nào đang trong thùng?
<details><summary>Gợi ý</summary>Hành động khác nhau: WIP và hàng trong kho thì Hold/cách ly ngay; hàng đã giao thì báo khách kèm `ShipmentRef` và số pallet. Không phân loại thì không biết phải gửi danh sách nào cho khách.</details>

### 5.3 Câu hỏi phỏng vấn liên quan

**"Bạn thiết kế traceability thế nào?"** Gợi ý trả lời: nêu ba tầng định danh (item, lot NVL, serial thành phẩm); hai bảng cạnh (`SerialComponent`, `LotGenealogy`) ghi tại lúc sự kiện trong cùng transaction với scan; verification lúc nạp (đúng item, đúng trạm, còn hạn, không Hold, FIFO cảnh báo); truy vấn recursive CTE ngược và xuôi, index `(LotNo) INCLUDE (SerialNo)`; đo hiệu năng dưới 1 giây với 5 triệu dòng. Nêu thêm giới hạn bạn biết (kiểm tra theo slot, khoanh vùng đến từng placement).

**"Khách báo lỗi serial X, bạn làm gì trong 2 giờ đầu?"** Gợi ý (theo mục 1.3): (1) xác nhận serial và thông tin lỗi, mở hồ sơ ngược: lot, máy, ca, người nạp; (2) xác định lot nghi ngờ, kiểm tra lot đó có **đang chạy** trên máy không, nếu có thì dừng, tháo, Hold ngay (containment tại nguồn, mỗi giờ thêm ~250 bo); (3) liên hệ nhà cung cấp bằng supplier lot; (4) truy xuất xuôi từ mọi reel cùng supplier lot, phân loại theo trạng thái (WIP, kho, đã giao, đã hủy); (5) Hold từng nhóm, gửi khách danh sách serial đã giao; (6) mở NCR, lưu mẫu, hẹn báo cáo nguyên nhân. Nhấn mạnh: số serial bị ảnh hưởng và tốc độ containment quyết định chi phí.

**"Nạp nhầm reel nghiêm trọng ở chỗ nào, chặn thế nào?"** Gợi ý: lỗi không nhìn thấy bằng mắt, có thể lọt qua AOI/test và chỉ hỏng ngoài thị trường (chi phí tăng theo cấp số nhân qua các công đoạn); chặn ngay khi nạp bằng scan đối chiếu với BOM snapshot của WO, có log cả lần bị từ chối.

**"Vì sao lưu snapshot BOM của WO mà không dùng BOM sống khi verification?"** Gợi ý: kỹ sư có thể đổi BOM sau khi WO release; kiểm tra phải bám theo phiên bản WO đang chạy (Module 2).

**"Dữ liệu truy xuất tăng mãi, bạn xử lý thế nào?"** Gợi ý: ước lượng số dòng (8 dòng/bo × sản lượng), index covering, nén PAGE, partition theo tháng, archive dữ liệu cũ theo thời hạn lưu hồ sơ hợp đồng; truy xuất dữ liệu lạnh chấp nhận chậm hơn nhưng vẫn phải có.

**[v2] "IQC là gì, MES liên quan thế nào?"** Gợi ý: kiểm tra hàng mua vào trước khi dùng (số lượng, mã, ngoại quan, đo mẫu, chứng từ; lấy mẫu theo AQL). MES giữ trạng thái `PendingIqc` trên lot và verification lúc nạp chặn lot chưa đạt, không override; kết quả IQC theo supplier lot, có hồ sơ ai kiểm, bao nhiêu mẫu.

**[v2] "Recall: hàng nghi lỗi đang ở những đâu?"** Gợi ý: truy xuất xuôi kèm aggregation (M3b): WIP / trong thùng / trên pallet / đã giao kèm số phiếu giao; Hold theo serial hoặc theo pallet; xuất Excel danh sách đã giao cho khách.

**"Một nhà cung cấp gửi thông báo lô lỗi nhưng chỉ biết supplier lot, bạn làm sao?"** Gợi ý: `SupplierLot` được lưu trên `MaterialLot` và có index; anchor của CTE chọn mọi reel cùng supplier lot; kết quả xuôi gộp lại.

### 5.4 Tài liệu đọc thêm

Chỉ ghi tên tài liệu và nơi tìm, không dẫn URL chi tiết; tự tra trên trang chủ.

- learn.microsoft.com (tài liệu SQL Server): "WITH common_table_expression (Transact-SQL)" (có mục ví dụ đệ quy, `MAXRECURSION`); "Create indexes with included columns"; "Create filtered indexes".
- learn.microsoft.com (tài liệu EF Core): "SQL Queries" (giới hạn khi bọc truy vấn thô), "Indexes" (có `IncludeProperties`, `HasFilter`).
- learn.microsoft.com (tài liệu WPF): "TreeView Overview", "HierarchicalDataTemplate", "Optimizing performance: controls" (virtualization).
- Tài liệu SQL Server về bảng đồ thị (Graph processing) nếu muốn so sánh với cách làm ở đây.
- CommunityToolkit.Mvvm: tài liệu trên learn.microsoft.com (`ObservableProperty`, `RelayCommand`).
- MediatR: README trên GitHub (notification, `IPublisher`) và thông tin license hiện hành.
- Tiêu chuẩn: ISO 9001 và IATF 16949 (điều khoản về nhận dạng và truy xuất), IPC-1782 (truy xuất trong sản xuất điện tử). Đây là tiêu chuẩn có phí; đọc bản tóm tắt của tổ chức ban hành hoặc tài liệu đào tạo nội bộ, và đối chiếu số điều khoản trước khi trích dẫn.
- **[v2]** ISO 2859-1 / ANSI/ASQ Z1.4 (bảng lấy mẫu AQL, chỉ cần đọc phần giới thiệu), IPC/JEDEC J-STD-033 (MSL, nếu làm bước 10e).
- Sách/tài liệu về Lean và 8D nói về containment (phần "khoanh vùng tạm thời") để hiểu tại sao phạm vi phải xác định nhanh.
