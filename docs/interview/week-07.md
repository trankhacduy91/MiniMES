# Tuần 7: Chất lượng: SPC, NCR, FAI, Hold/Release (M7)

> **Mục tiêu tuần:** IT MES làm việc với QC hằng ngày. Bạn không cần giỏi thống kê như QC engineer, nhưng phải:
> - đọc được control chart;
> - phân biệt USL và UCL;
> - hiểu vòng đời NCR;
> - biết Hold/Release đúng cách. QC Hold nhầm hoặc Release nhầm là sự cố lớn.
>
> **Thời lượng:** ~10 giờ, gồm đọc 3 · lab 3 · drill 2 · nói 2.

## 0. Ngoài đời việc này diễn ra thế nào

- **Phòng QC/QA ở nhà máy FDI:**
  - IQC (hàng vào), IPQC (trong quy trình), OQC (trước khi giao);
  - QA lo hệ thống: ISO 9001, IATF 16949, audit khách.
  - Thuật ngữ Nhật hay dùng ở công ty Nhật: 不適合 (không phù hợp), 処置 (xử lý), 保留 (hold). Trong spec M7 mục 1.6 có bảng thuật ngữ đủ.
- **SPC:** đặc tính quan trọng (điện áp, nhiệt độ lò, lực, độ dày) được vẽ control chart. Mục đích là phát hiện quy trình **đang trôi** trước khi ra hàng hỏng. Khách ô tô yêu cầu Cpk ≥ 1,33 (thường thấy trong PPAP).
- **NCR:** mọi "không phù hợp" có hồ sơ. QC đề xuất xử lý; người có thẩm quyền khác duyệt (MRB). Các lựa chọn:
  - dùng nguyên trạng (Use as is), thường phải xin khách chấp nhận (concession/deviation);
  - sửa lại (Rework);
  - hủy (Scrap);
  - trả nhà cung cấp (Return to vendor, kèm SCAR).
- **FAI:** sau setup, đổi model hoặc đổi ca, board đầu tiên phải được QC duyệt rồi mới chạy hàng loạt. Rất phổ biến ở SMT.
- **Hold:** ngoài đời là thẻ đỏ dán lên thùng hàng. Trong MES, Hold phải chặn ở **mọi** điểm: scan, nạp liệu, đóng gói, giao hàng.
- **Ticket hay gặp:**
  - "QC Hold rồi mà vẫn đóng gói được";
  - "Release rồi mà board không scan được";
  - "đóng NCR không được";
  - "chart báo đỏ mà hàng vẫn đạt, có phải lỗi?".

## 1. Đọc (3 giờ)

- [ ] `spec/module-07-quality.md`:
  - 1.2 khái niệm (USL/UCL, I-MR, Cp/Cpk, Western Electric);
  - **1.3 "Một ngày ở xưởng"**: đọc như một câu chuyện, rất sát thực tế;
  - 1.4 bảng quy tắc Q1–Q20;
  - 1.5 sai lầm thường gặp ngoài đời.
- [ ] `docs/quality-erd.md`.
- [ ] `docs/learning/M7-step03-spc-calculator.md`, `M7-step04-western-electric.md`, `M7-step06-spc-sources.md`, `M7-step08-ncr.md`, `M7-step08b-fai.md`, `M7-step09-hold-release.md`, `M7-step10-dod-flow.md`.

### Số liệu của dự án (học thuộc vài con)

| Đặc tính | Spec (LSL–USL) | Baseline | UCL/LCL = mean ± 3σ |
| --- | --- | --- | --- |
| `3V3_RAIL` (đo ở TEST, mỗi board) | 3,135 – 3,465 V | mean 3,302, σ 0,040 | 3,182 – 3,422 V |
| Nhiệt độ đỉnh lò (tag `peakTemp`, mỗi 30 giây) | 235 – 255 °C | mean 245, σ 0,8 | 242,6 – 247,4 °C |

- **Cpk baseline của rail** = min(3,465 − 3,302; 3,302 − 3,135) / (3 × 0,040) = 0,163 / 0,12 ≈ **1,36**.
- **3,440 V:** **đạt spec** (OK) nhưng vượt UCL, nên báo **WE1**. **3,470 V:** **NG**, board vào luồng Repair.
- **Lò trôi 0,3 °C/phút:** alert run 7 ở khoảng **247 °C**, còn cách USL 255 °C rất xa. SPC báo trước khi ra hàng hỏng.

### Rule đang bật (`SpcRuleCode`)

| Rule | Nghĩa | Bắt được gì |
| --- | --- | --- |
| WE1 | 1 điểm ngoài 3σ | Cú sốc lớn |
| WE2 | 2 trong 3 điểm ngoài 2σ, cùng phía | Dịch vừa |
| WE4 | 7 điểm liên tiếp cùng một phía đường tâm | Dịch nhỏ kéo dài (reel lệch, lò trôi) |
| WE3 | 4 trong 5 điểm ngoài 1σ (tắt mặc định) | Nhiều báo giả |
| OutOfSpec | Mẫu máy vượt USL/LSL | Andon Quality |
| DataGap | 3 chu kỳ không có giá trị Good | Mất dữ liệu: hiện ra, **không bịa điểm** |

## 2. Lab (3 giờ)

### 2.1 SPC
1. Chạy simulator trạm test có dịch tâm:

   ```powershell
   dotnet run --project src/MiniMes.MachineSimulator -- --auto-operator --scenario drift --after 100 --shift 0.04
   ```

   Cần WO đang chạy, không chờ FAI. BackOffice `qc01`, menu **SPC chart** → `3V3_RAIL`: xem alert xuất hiện khoảng 7 board sau khi dịch.
2. Chạy gateway. Ở simulator gõ `drift L1-REFLOW-01 0.3`, chờ khoảng 5 phút → chart nhiệt độ lò có alert run 7. Rồi `opcua stop` → alert **DataGap**.
3. Trên alert bấm **Create NCR** (form điền sẵn), hoặc **Hold serials in window** (Hold các board qua lò trong khoảng thời gian, mặc định −20/+10 phút).

### 2.2 NCR
1. `qc01`: mở NCR → Start review → đề xuất **Rework** kèm ghi chú.
2. Thử **Approve** bằng chính `qc01`: nút tắt; gọi API trực tiếp thì nhận 422.
3. `sup01`: Approve → Dispositioned. Thử **Close** khi còn Hold gắn NCR: 422 (Q12).

### 2.3 FAI
1. Tạo WO 20 pcs **có** RequireFai, Release. Kiosk có banner vàng; board thứ hai bị chặn `FaiNotApproved`.
2. Cho board mẫu qua hết các bước tới TEST.
3. `qc01`, menu **First article (FAI)**: 3 mục tự động xanh (BOM khớp, Test OK, số đo trong spec); tick các mục tay (VISUAL, POLARITY, LABEL) → **Pass**. Kiosk hết banner, line chạy.

### 2.4 Kịch bản "reel lỗi" (README mục Quality, 5 bước)
Truy xuất xuôi `L-CAP-B` → **Hold all…** (xem preview, tick *Open an NCR*) → kiosk scan board trong danh sách: đỏ "Board on HOLD" → nạp reel cùng lô giao: `LotOnHold` → NCR *Use as is* (`qc01` đề xuất, `sup01` duyệt) → **Holds** → Release → Close NCR.

### SQL

```sql
-- Đợt Hold gần nhất: ai, phạm vi, bao nhiêu đối tượng, đã release chưa
SELECT BatchId, Scope, ScopeRef, Reason, HeldBy, MIN(HeldAt) AS HeldAt, ReleasedBy, MAX(ReleasedAt) AS ReleasedAt,
       SUM(CASE WHEN TargetType = 'Serial' THEN 1 ELSE 0 END) AS Serials, SUM(CASE WHEN TargetType = 'Lot' THEN 1 ELSE 0 END) AS Lots
FROM HoldRecords GROUP BY BatchId, Scope, ScopeRef, Reason, HeldBy, ReleasedBy ORDER BY MIN(HeldAt) DESC;

-- Board nào đang Hold, trước đó đang ở trạng thái gì (Release sẽ trả về đúng trạng thái đó)
SELECT SerialNo, Status, StatusBeforeHold, CurrentSeq FROM Serials WHERE Status = 'Hold';

-- NCR và người đề xuất / người duyệt (phải khác nhau)
SELECT NcrNo, Source, Status, Disposition, ProposedBy, ApprovedBy, ClosedBy FROM Ncrs ORDER BY Id DESC;

-- 20 số đo gần nhất của rail, kèm kết quả lúc đo
SELECT TOP 20 m.MeasuredAt, s.SerialNo, m.Value, m.Lsl, m.Usl, m.Result
FROM Measurements m JOIN InspectionPlans p ON p.Id = m.InspectionPlanId LEFT JOIN Serials s ON s.Id = m.SerialId
WHERE p.Code = '3V3_RAIL' ORDER BY m.MeasuredAt DESC;
```

## 3. Drill sự cố (2 giờ)

### Drill A: "QC Hold nhầm cả lot keo dán dùng cho mọi board, line dừng hết"
**Cách tạo:** Hold theo một lot dùng cho mọi board (ví dụ `L-RES-A`), rồi thử scan.

**Tự làm trước:** IT MES nên làm gì, và **không** được làm gì?

<details><summary>Đáp án gợi ý</summary>

- **Không** UPDATE thẳng `Serials.Status` trong DB. Làm vậy mất `StatusBeforeHold`, mất audit, NCR không khớp.
- **Đúng:**
  1. báo QC/Supervisor (người có quyền Release);
  2. họ Release **đúng đợt** (BatchId) trong menu **Holds**, có ghi lý do;
  3. board về đúng trạng thái trước Hold (board xong vẫn là Completed).
- **Hold chồng (Q8):** nếu board còn bị một Hold khác giữ thì Release đợt này **không** thả nó. Đúng thiết kế, không phải lỗi.
- **Phòng ngừa:**
  - preview luôn hiện số lượng trước khi Hold ("87 boards…");
  - với lot phổ biến, dùng phạm vi hẹp hơn: theo khoảng thời gian qua máy, hoặc danh sách serial (spec 1.5 mục 14).
- **Câu nói:** "IT không quyết định chất lượng. Tôi giúp đúng người làm đúng thao tác nhanh nhất, và dữ liệu có dấu vết."
</details>

### Drill B: "Release rồi mà board vẫn không scan được"
<details><summary>Đáp án gợi ý</summary>

- **Kiểm tra:**

  ```sql
  SELECT * FROM HoldRecords WHERE TargetType = 'Serial' AND TargetId = <serialId> AND ReleasedAt IS NULL
  ```

  Còn dòng tức là còn Hold khác (Q8). Xem `Reason`, `NcrId` để biết hỏi ai.
- **Không còn Hold** nhưng vẫn bị chặn: đọc `reasonCode` mới. Có thể board đang ở bước khác (`SkippedStep`), hoặc là NG (`MustGoRepair`): trước khi Hold nó đã NG thì Release trả nó về NG.
- **Trong 5 giây** sau lần scan OK cuối, scan lại trả "duplicate". Đây là chi tiết thú vị đã ghi trong `docs/learning/M7-step10-dod-flow.md`.
</details>

### Drill C: "Chart báo đỏ mà board vẫn đạt. Hệ thống lỗi à?" (người hỏi: trưởng ca)
<details><summary>Đáp án gợi ý</summary>

- **Không lỗi:**
  - **spec (USL/LSL)** là yêu cầu của khách: quyết định board đạt hay hỏng;
  - **control limit (UCL/LCL)** là "giọng nói của quy trình": cảnh báo quy trình đã thay đổi.
- Đỏ trên chart nghĩa là "có nguyên nhân đặc biệt, hãy tìm": vừa đổi reel? vừa chỉnh máy? đổi ca?
- SPC **không tự NG, không tự Hold** (Q6), vì báo động giả sẽ dừng line. QC xem rồi quyết định.
- **Sai lầm hay gặp:** chỉnh máy vì một điểm bình thường (tampering) làm dao động tăng thêm.
</details>

## 4. Câu hỏi phỏng vấn tuần 7

<details><summary>1. USL/LSL và UCL/LCL khác nhau thế nào?</summary>

- **Spec limit:** khách định ra, quyết định OK/NG từng sản phẩm.
- **Control limit:** tính từ dữ liệu quy trình ổn định (mean ± 3σ), cố định cho tới khi QC tính lại có lý do.
- **Ví dụ:** 3,44 V đạt spec (USL 3,465) nhưng vượt UCL 3,422, nên báo WE1. Hàng vẫn tốt nhưng quy trình có vấn đề.
</details>

<details><summary>2. Cp, Cpk là gì? Cpk 1,33 nghĩa là gì?</summary>

- **Cp** = (USL − LSL) / 6σ: quy trình có "vừa" khung spec không.
- **Cpk** = min(USL − mean, mean − LSL) / 3σ: có tính độ lệch tâm. Cpk ≤ Cp.
- **Cpk 1,33:** biên spec gần nhất cách mean 4σ, tỷ lệ lỗi cỡ vài chục ppm. Thường là yêu cầu tối thiểu của khách ô tô.
- **Chỉ tính Cpk khi quy trình ổn định** (chart sạch). Cpk trên quy trình đang trôi là vô nghĩa.
- **Ví dụ trong dự án:** baseline rail Cpk ≈ 1,36.
</details>

<details><summary>3. Western Electric rules là gì? Vì sao dự án chỉ bật 3 rule?</summary>

- Các mẫu hình bất thường trên chart: 1 điểm ngoài 3σ; 2/3 ngoài 2σ; 4/5 ngoài 1σ; 7–8 điểm cùng phía.
- Bật nhiều rule thì nhiều báo động giả, QC bỏ qua hết (spec 1.5 mục 6). Bật 3 rule bắt đủ: sốc lớn, dịch vừa, dịch nhỏ kéo dài.
- **Số đo của dự án:** dịch 0,02 V, trung vị khoảng 20 board mới báo; dịch 0,04 V khoảng 7 board. Kiểm bằng mô phỏng độc lập; xem incident "Test thống kê 'chứng minh' spec sai".
</details>

<details><summary>4. Vòng đời NCR? Vì sao người duyệt phải khác người đề xuất?</summary>

- **Luồng:** Open → UnderReview (QC điều tra, đề xuất) → Dispositioned (Supervisor khác người đề xuất duyệt) → Closed (+ Cancelled).
- **Tách nhiệm vụ (segregation of duties):** chống "tự đề xuất Use as is rồi tự duyệt cho xong". Audit khách hay hỏi "ai cho phép thả lô này".
- **Dự án chặn cả ở UI** (nút tắt) **và API** (422). Chỉ có UI thì gọi thẳng API là qua mặt được.
</details>

<details><summary>5. Các disposition và tác động lên hàng?</summary>

- **Use as is:** dùng tiếp, bắt buộc ghi lý do (và concession nếu ngoài spec khách). Rủi ro nhất.
- **Rework:** board vào luồng Repair.
- **Scrap:** hủy, trạng thái cuối.
- **Return to vendor:** chỉ cho **lot** (Q13), có SCAR.
- Tác động áp lên đúng các đối tượng của NCR khi Approve, trong cùng transaction.
</details>

<details><summary>6. FAI là gì? MES hỗ trợ thế nào?</summary>

- Kiểm tra sản phẩm đầu sau setup/đổi model, trước khi chạy hàng loạt.
- **MES làm 3 việc:**
  - chỉ cho **một** board mẫu chạy, gán bằng UPDATE có điều kiện nên hai trạm không cùng lấy mẫu;
  - tự điền 3 kiểm từ dữ liệu có sẵn (BOM khớp linh kiện thật đã nạp, Test OK, số đo trong spec);
  - QC Pass thì mở cổng cho cả line.
- FAI là **cổng chặn trong route check**, không phải một báo cáo.
</details>

<details><summary>7. Hold/Release hoạt động xuyên module thế nào?</summary>

- **Một nguồn sự thật:** cột `Status` của serial/lot. Mọi điểm (scan, nạp reel, đóng gói, giao pallet) đọc cùng cột đó.
- **Phạm vi Hold:** Lot (cả lô giao và board làm từ nó), Serials, Carton/Pallet (mở rộng thành serial), MachineWindow (board qua máy trong khoảng thời gian).
- **Ghi set-based** trong một transaction (`INSERT … SELECT`, `UPDATE` có CASE giữ `StatusBeforeHold`). Release trả đúng trạng thái cũ.
- **Hold chồng:** chỉ thả khi hết mọi Hold đang active.
- **Có test đua** Hold với scan cùng lúc; xem incident "Hold đúng lúc scan".
</details>

<details><summary>8. SPC từ dữ liệu máy khác SPC từ trạm test thế nào?</summary>

- **Trạm test:** mỗi board một số đo, gắn serial; vượt spec thì NG board đó.
- **Tag máy** (nhiệt độ lò):
  - lấy mẫu định kỳ (30 giây), không gắn serial nên không NG board nào;
  - vượt spec thì alert + Andon Quality;
  - QC dùng **Hold serials in window** để giữ board qua lò trong khoảng đó.
- Chỉ lấy giá trị `Quality = Good`. Mất dữ liệu thì báo DataGap, không bịa điểm.
</details>

<details><summary>9. Số đo sai (nhập nhầm) thì sửa thế nào?</summary>

- **Không sửa, không xóa** (Q16): số đo là bằng chứng chất lượng.
- Ghi **bản hiệu chỉnh** có lý do (`CorrectsMeasurementId`, `CorrectionReason`).
- Cùng tinh thần với audit log chỉ INSERT.
</details>

## 5. Tự kiểm cuối tuần

- [ ] Giải thích USL và UCL cho một "trưởng ca" bằng ví dụ 3,44 V, không dùng chữ σ.
- [ ] Tính tay Cpk baseline của rail ra ≈ 1,36.
- [ ] Làm hết kịch bản "reel lỗi" 5 bước không cần đọc README.
- [ ] Trả lời ≥ 8/9 câu.
