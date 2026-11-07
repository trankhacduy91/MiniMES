# Tuần 6: Vật tư và truy xuất (M6)

> **Mục tiêu tuần:** trả lời được câu khó nhất và hay gặp nhất khi phỏng vấn MES:
> **"Khách khiếu nại một board lỗi. Trong 2 giờ, bạn cho tôi biết board đó dùng linh kiện lô nào, những board nào khác dùng cùng lô đó, và chúng đang ở đâu."**
> Cuối tuần bạn phải làm được việc này bằng màn hình **và** bằng SQL viết tay, có bấm giờ.
>
> **Thời lượng:** ~10 giờ, gồm đọc 3 · lab 3 · drill 2 · nói 2.

## 0. Ngoài đời việc này diễn ra thế nào

- **Kho nhận hàng:**
  - Nhà cung cấp giao nhiều reel cùng một **supplier lot** (lô sản xuất của họ).
  - Kho in nhãn nội bộ cho từng reel (1 reel = 1 lot nội bộ).
  - Hàng ở trạng thái **chờ IQC**, chưa được dùng.
- **IQC (Incoming Quality Control):**
  - QC lấy mẫu theo lô giao (theo AQL), đo và soi.
  - Đạt thì cả lô giao **Available**; trượt thì bị chặn, trả nhà cung cấp hoặc xử lý qua NCR.
- **Nạp liệu ở máy SMT:** operator scan nhãn reel và vị trí feeder. MES kiểm tra:
  - đúng mã linh kiện của BOM ở bước này;
  - lot đã qua IQC, không Hold, còn hạn;
  - **FIFO**: còn reel cũ hơn thì cảnh báo.

  Nạp sai linh kiện là lỗi chết người trong SMT, vì cả ngàn board hỏng trước khi AOI phát hiện.
- **Truy xuất (traceability, genealogy):**
  - **ngược** (backward): từ board ra vật tư;
  - **xuôi** (forward): từ lot ra mọi board đã dùng nó, và từ board ra thùng/pallet/lần giao.
  - Khách ô tô (IATF 16949) yêu cầu truy xuất được trong thời gian ngắn, nhiều khi được audit bằng bài tập bấm giờ y như drill tuần này.
- **Thực tế khó:** truy xuất theo **reel** chỉ chính xác khi biết reel nào đang nạp lúc board đi qua. Nhiều nhà máy chỉ biết "trong khoảng thời gian", nên phải ghi lúc nạp/tháo reel thật chặt. **MSL** (Moisture Sensitivity Level): IC nhạy ẩm có thời gian sống ngoài túi giới hạn. Câu này hay được hỏi; dự án chưa code, chỉ cần nói được.

## 1. Đọc (3 giờ)

- [ ] `spec/module-06-material-traceability.md`, phần mở đầu và các quy tắc nạp liệu.
- [ ] `docs/erd-material.md`: vẽ tay `MaterialLots`, `LotGenealogy` (reel tách/gộp), `MaterialLoads`, `SerialComponents`, `MaterialVerificationLogs`, `IqcRecords`.
- [ ] `docs/learning/M6-step02-receiving.md`, `M6-step02b-iqc.md`, `M6-step03-material-verifier.md`, `M6-step05-consumption.md`.
- [ ] `docs/learning/M6-step07-trace-queries.md`: recursive CTE, chống vòng lặp; vì sao dùng SQL viết tay.
- [ ] `docs/learning/M6-step09-performance.md` và `docs/perf.md` phần M6: xuôi 12.500 board khoảng 100 ms, ngược 3 ms.

### Bảng tra kết quả nạp liệu (`VerifyCode`)

| Code | Màu | Nghĩa | Ngoài đời nói gì |
| --- | --- | --- | --- |
| `Ok` | xanh | Đúng | |
| `FifoOlderLotAvailable` | vàng | Còn reel cũ hơn có hàng | Lấy reel cũ; nếu không được (reel cũ ở kho xa, đã mở túi…) thì supervisor quẹt thẻ và ghi lý do |
| `ItemNotInBom` | đỏ | Linh kiện không thuộc BOM | Lấy nhầm reel, **dừng lại** |
| `ItemNotForStation` | đỏ | Linh kiện của bước khác | Nạp nhầm máy |
| `LotNotIqcPassed` | đỏ | Chưa qua IQC | Gọi QC; **không có nút cho qua** |
| `LotOnHold` | đỏ | QC đang Hold | Để riêng |
| `LotExpired` / `LotEmpty` | đỏ | Hết hạn / hết hàng | Đổi reel; nếu số trên hệ thống sai thì kiểm kê lại |
| `LotNotFound` | đỏ | Nhãn không có trong hệ thống | Nhãn nhà cung cấp chứ không phải nhãn nội bộ? Kho chưa nhập? |

## 2. Lab (3 giờ)

### 2.1 Vòng đời một reel
1. Đăng nhập BackOffice bằng `wh01`, vào **Receiving**: nhận 3 reel `CAP-100NF` cùng một supplier lot, có hạn dùng. Xem nhãn ZPL.
2. Đăng nhập `qc01`, vào **IQC**: thấy lô giao đang chờ, bấm **Pass**.
3. Kiosk `L1-SMT-01`, nút **Material**:
   - nạp reel mới nhất trước để thấy **vàng FIFO**;
   - nạp một reel linh kiện của bước khác hoặc chưa IQC để thấy **đỏ**;
   - nạp đúng để thấy **xanh**.
4. Chạy vài board qua SMT. Mỗi scan OK ghi `SerialComponents` và trừ `RemainingQty` của reel.

### 2.2 Truy xuất trên dữ liệu mẫu (seed sẵn)
- WO `WO-TRACE`, 200 board `TRACE-0001`…`TRACE-0200`.
- Tụ `CAP-100NF`: board 1–120 dùng `L-CAP-A`, board 121–200 dùng `L-CAP-B`. `L-CAP-B` tách từ reel `L-CAP-P`. Cả hai thuộc supplier lot `SUP-S1`.
- Điện trở `RES-100K`: cả 200 board dùng `L-RES-A` (`SUP-R1`).

Việc cần làm:
1. **Traceability → Serial (backward)** cho `TRACE-0150`: thấy `L-CAP-B` → lot cha `L-CAP-P`.
2. **Lot (forward)** cho `L-CAP-A` (120 board), rồi **Supplier lot** `SUP-S1` + `CAP-100NF` (200 board). Xuất Excel.

### 2.3 SQL viết tay (làm trước khi xem lời giải)

**Đề 1:** board `TRACE-0150` dùng những lot nào, của supplier lot nào, nạp ở trạm nào, lúc nào?

**Đề 2:** mọi board đã dùng lot `L-CAP-A`, kèm trạng thái board, thùng và pallet hiện tại.

**Đề 3:** mọi board dùng **bất kỳ** reel nào của supplier lot `SUP-S1` (mã hàng `CAP-100NF`).

<details><summary>Lời giải</summary>

```sql
-- Đề 1: ngược
SELECT s.SerialNo, i.Code AS Item, l.LotNo, l.SupplierLot, st.Code AS Station, sc.Qty, sc.ConsumedAt
FROM Serials s
JOIN SerialComponents sc ON sc.SerialId = s.Id
JOIN MaterialLots l ON l.Id = sc.LotId
JOIN Items i ON i.Id = sc.ItemId
LEFT JOIN Stations st ON st.Id = sc.StationId
WHERE s.SerialNo = 'TRACE-0150';

-- Đề 2: xuôi một lot, kèm vị trí đóng gói hiện tại (thùng chưa unpack)
SELECT s.SerialNo, s.Status, c.CartonNo, p.PalletNo, p.ShippedAt, p.ShipmentRef
FROM MaterialLots l
JOIN SerialComponents sc ON sc.LotId = l.Id
JOIN Serials s ON s.Id = sc.SerialId
LEFT JOIN CartonSerials cs ON cs.SerialId = s.Id AND cs.UnpackedAt IS NULL
LEFT JOIN Cartons c ON c.Id = cs.CartonId
LEFT JOIN Pallets p ON p.Id = c.PalletId
WHERE l.LotNo = 'L-CAP-A';

-- Đề 3: xuôi theo supplier lot. Reel gốc và mọi reel tách ra từ nó (LotGenealogy) bằng recursive CTE.
WITH LotTree AS (
    SELECT l.Id, 0 AS Depth
    FROM MaterialLots l JOIN Items i ON i.Id = l.ItemId
    WHERE l.SupplierLot = 'SUP-S1' AND i.Code = 'CAP-100NF'
    UNION ALL
    SELECT g.ChildLotId, t.Depth + 1
    FROM LotGenealogy g JOIN LotTree t ON g.ParentLotId = t.Id
    WHERE t.Depth < 10
)
SELECT DISTINCT s.SerialNo, s.Status
FROM LotTree t JOIN SerialComponents sc ON sc.LotId = t.Id JOIN Serials s ON s.Id = sc.SerialId
OPTION (MAXRECURSION 20);
```

Tự kiểm: Đề 3 ra 200 board. Đề 2 ra 120 board. Nếu viết `l.LotNo = N'L-CAP-A'` thì vẫn đúng nhưng chậm hơn trên bảng lớn (bẫy `nvarchar`, xem incident M6 bước 9). So với câu thật của hệ thống ở `src/MiniMes.Infrastructure/Traceability/TraceQueries.cs`.
</details>

## 3. Drill sự cố (2 giờ)

### Drill A (bấm giờ, tối đa 30 phút): "Khách báo board `TRACE-0042` lỗi tụ"
Bạn là IT MES, QC gọi lúc 8 giờ sáng. Hãy viết **một email trả lời QC** gồm:
1. board dùng reel tụ nào, supplier lot nào, nạp lúc nào, ở máy nào, ai nạp;
2. bao nhiêu board khác dùng **cùng reel**, và bao nhiêu dùng **cùng supplier lot**;
3. trong đó bao nhiêu đã giao (số phiếu giao), bao nhiêu trong kho (thùng/pallet), bao nhiêu còn trên line;
4. đề xuất phạm vi Hold, và việc gì cần QC/khách quyết định.

<details><summary>Gợi ý đáp án và điều người phỏng vấn muốn nghe</summary>

- **Số liệu:**
  - board 42 dùng `L-CAP-A`; cùng reel có 120 board; cùng supplier lot `SUP-S1` có 200 board (qua `L-CAP-B` tách từ `L-CAP-P`);
  - phân loại theo vị trí bằng màn **Lot (forward)** hoặc Đề 2;
  - với dữ liệu mẫu, cả 200 board **chưa đóng gói** (đã kiểm bằng SQL: 0 trong thùng, 0 đã giao). Muốn luyện phần "đã giao / trong kho" thì làm drill này trên lot bạn tự nạp ở lab 2.1, sau khi đã đóng gói vài board như tuần 3.
- **Phạm vi Hold:**
  - nếu lỗi do **reel** (hỏng vật lý): Hold theo reel;
  - nếu lỗi do **lô sản xuất** của nhà cung cấp (sai giá trị): Hold cả supplier lot, kể cả reel chưa dùng trong kho.
  - Đây là quyết định của QC; IT cung cấp dữ liệu và cảnh báo phạm vi.
- **Hàng đã giao:** báo khách số lượng và số phiếu giao để khách tự cách ly. Đây là việc của QC/sales, IT chuẩn bị file Excel.
- **Tuần 7** sẽ làm bước tiếp theo: Hold cả phạm vi bằng một nút (M7).
- **Câu nói:** "Truy xuất nhanh được là vì dữ liệu được ghi **lúc sản xuất**, mỗi scan OK ghi board dùng reel nào trong cùng transaction. Không thể dựng lại chính xác sau này."
</details>

### Drill B: "Operator báo không nạp được reel, line SMT đang chờ"
**Cách tạo:** nhận reel mới (chưa IQC) rồi nạp; hoặc nạp reel của item khác.

<details><summary>Đáp án gợi ý</summary>

- Màn hình đã có `code`. Tra bảng ở mục 1.
- Kiểm tra lot bằng SQL:

  ```sql
  SELECT LotNo, Status, RemainingQty, ExpiryAt, SupplierLot FROM MaterialLots WHERE LotNo = '...'
  ```

  Nhớ `Status` lưu **số**: 0 Available, 1 Hold, 2 Consumed, 3 PendingIqc, 4 IqcRejected, 5 Scrapped, 6 Returned.
- Lịch sử các lần thử nạp: `MaterialVerificationLogs`, gồm ai, lúc nào, mã gì, có override không.
- `PendingIqc` thì gọi QC làm IQC **gấp**. Ngoài đời đây là chuyện thường ngày: hàng về trễ, line chờ, áp lực "cho dùng trước". Câu trả lời đúng: **không có nút cho qua**. Nếu QC đồng ý dùng có điều kiện thì phải qua quyết định có ghi vết (NCR, tuần 7).
</details>

### Drill C: "Kho báo số tồn reel trên MES khác thực tế"
<details><summary>Ý chính</summary>

- MES trừ **số lý thuyết** theo BOM khi scan OK; thực tế hao hụt khác: linh kiện rơi, máy gắp hỏng (pick error), cắt đầu reel.
- Khi tháo reel thì nhập số còn lại thật (`unload` có `remainingQty`) để hiệu chỉnh.
- Lệch lớn và đều đặn: kiểm tra định mức BOM, hoặc **kiểu cột**. Xem incident "Kem hàn bị trừ nhiều hơn 57 % mỗi board": `decimal(18,3)` làm tròn 0,0006363 kg thành 0,001 kg, im lặng, không báo lỗi.
</details>

## 4. Câu hỏi phỏng vấn tuần 6

<details><summary>1. Truy xuất ngược và xuôi là gì? Dữ liệu nào cần ghi để làm được?</summary>

- **Ngược:** board → lot vật tư (→ lot cha → supplier lot).
- **Xuôi:** lot → mọi board → thùng/pallet/lần giao.
- **Dữ liệu cần ghi:**
  - lúc nạp reel (`MaterialLoads`);
  - lúc board tiêu thụ (`SerialComponents`, ghi khi scan OK, cùng transaction);
  - tách/gộp reel (`LotGenealogy`);
  - đóng gói (`CartonSerials`, `Cartons`, `Pallets`).
</details>

<details><summary>2. Truy xuất chậm trên bảng lớn thì tối ưu thế nào?</summary>

- **Index đúng cột lọc:** `SerialComponents(LotId)`, `(SerialId)`; `MaterialLots(SupplierLot, ItemId)`.
- **Kiểu tham số khớp kiểu cột** (`varchar` khác `nvarchar`).
- **Recursive CTE** cho cây lot, có `MAXRECURSION`.
- **Lazy-load** cây trên UI: mở nhánh nào tải nhánh đó.
- **Số đo:** xuôi 12.500 board khoảng 100 ms, ngược 3 ms trên 5 triệu dòng.
</details>

<details><summary>3. FIFO vật tư kiểm soát thế nào? Khi nào cho phép phá FIFO?</summary>

- Khi nạp, nếu còn lot **cùng item, nhận sớm hơn, còn hàng, Available** thì cảnh báo vàng.
- Phá FIFO được nhưng phải có **supervisor quẹt thẻ + lý do**, có ghi vết (`FifoOverrideBy`, `FifoOverrideReason`).
- Lý do thật hay gặp: reel cũ ở kho xa, reel cũ đã mở túi (MSL), khách yêu cầu dùng lô cụ thể.
</details>

<details><summary>4. IQC là gì? Vì sao chặn ở MES chứ không chỉ ở kho?</summary>

- Kiểm tra chất lượng hàng nhập theo mẫu, kết quả áp cho cả lô giao.
- Chặn ở **điểm dùng** (nạp vào máy) mới chắc chắn: kho có thể xuất nhầm, nhưng máy không nhận reel chưa IQC.
- Đây là "cổng chặn trong luồng sản xuất", không phải một báo cáo.
</details>

<details><summary>5. Board sửa chữa thay linh kiện thì truy xuất ra sao?</summary>

Kỹ thuật viên scan nhãn reel của linh kiện mới. Hệ thống kiểm tra như lúc nạp, rồi ghi một dòng `SerialComponents` mới nguồn `RepairLogId`. Truy xuất ngược thấy **cả** linh kiện gốc lẫn linh kiện thay.
</details>

<details><summary>6. Nạp reel sai thì phát hiện ở đâu? Nếu lỡ chạy rồi thì sao?</summary>

- **Chặn ngay khi nạp** (đỏ, không có nút cho qua).
- Nếu reel bị xác định lỗi **sau khi** đã chạy: truy xuất xuôi để biết phạm vi, rồi Hold (tuần 7).
- **Chặn cả khi chạy:** reel đang nạp bị Hold thì board tiếp theo scan bị `MaterialNotLoaded`.
</details>

<details><summary>7. MSL là gì?</summary>

- Linh kiện nhạy ẩm (IC, BGA) được đóng túi chống ẩm.
- Mở túi thì có **floor life** giới hạn theo cấp MSL (ví dụ MSL 3 khoảng 168 giờ).
- Quá hạn phải **sấy (bake)** trước khi dùng, nếu không thì dễ nứt khi qua lò (hiện tượng popcorn).
- MES thường theo dõi giờ mở túi và chặn nạp khi hết hạn. Dự án ghi là P2, chưa code.
</details>

<details><summary>8. Vì sao 1 reel = 1 lot nội bộ thay vì dùng thẳng supplier lot?</summary>

- Một supplier lot có thể là hàng chục reel. Hold hoặc truy xuất chính xác tới **từng reel** giúp phạm vi ảnh hưởng nhỏ hơn.
- Nhãn nội bộ in theo chuẩn của nhà máy, nên quét được ổn định.
- Supplier lot vẫn được lưu để truy ngược lên nhà cung cấp và Hold theo cả lô khi cần.
</details>

## 5. Tự kiểm cuối tuần

- [ ] Làm drill A trong ≤ 30 phút, có email trả lời QC.
- [ ] Viết 3 câu SQL truy xuất không nhìn lời giải.
- [ ] Giải thích được FIFO override và vì sao IQC không có nút cho qua.
- [ ] Trả lời ≥ 7/8 câu.
