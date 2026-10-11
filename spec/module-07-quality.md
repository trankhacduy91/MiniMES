# Module 7 — Quality: Inspection, SPC, NCR, Hold/Release

> Module này làm ba việc: (1) nhận số đo tại trạm kiểm tra và phát hiện bất thường sớm bằng SPC (control chart + Western Electric rules + Cpk); (2) ghi nhận và xử lý hàng lỗi có hệ thống bằng NCR (Non-Conformance Report) với disposition và người phê duyệt; (3) chặn hàng nghi ngờ bằng Hold/Release, có hiệu lực ở mọi cửa đi qua (scan ở trạm — M3, nạp reel lên máy — M6).
> Sau khi xong bạn có: bộ hàm thống kê SPC có unit test khớp số tính tay, rule engine nhỏ, control chart trong WPF, workflow NCR, và kịch bản demo bước 8: reel lỗi → truy xuất xuôi → Hold → scan serial bị hold thì bị chặn.
> Thời lượng ước tính: khoảng 33 giờ (3–3,5 tuần ở nhịp 1,5–2 giờ/ngày; xem mục 3). **[v2]** Phần bỏ đi (WE3, NCR 6 trạng thái, simulator số đo riêng) bù cho phần thêm (FAI, SPC tự động từ dữ liệu máy, Hold theo thùng/pallet/khoảng thời gian).
> Module tiên quyết: M0 (auth, audit, WPF khung), M1 (`DefectCode`, `RoutingStep.IsInspection`), M2 (state machine, rowversion, **[v2]** `FaiStatus`), M3 (route check, kiosk, **[v2]** cổng FAI), **[v2]** M3b (thùng/pallet), **[v2]** M4/M4b (`MachineTag`, `ProcessValue`), M6 (truy xuất xuôi, `MaterialLot`, "Hold tất cả").
>
> **Cập nhật theo [spec-v2](spec-v2.md):** (1) biểu đồ **I-MR** (thêm biểu đồ moving range) và chỉ bật **3 rule**: 1 điểm ngoài 3σ, 2/3 điểm ngoài 2σ cùng phía, **7** điểm liên tiếp cùng phía đường tâm (WE3 thành tùy chọn); (2) **SPC tự động** từ tag nhiệt độ lò reflow (mục 2.3b); (3) **NCR rút gọn còn 4 trạng thái** `Open → UnderReview → Dispositioned → Closed` (mục 2.5); (4) **FAI**: QC duyệt sản phẩm đầu tiên, mở cổng route check của M3 (mục 2.5b); (5) Hold theo **thùng/pallet** và theo **khoảng thời gian qua máy** (mục 2.6). **Bỏ**: CAPA, hiệu chuẩn, X̄-R với subgroup. Các chỗ đổi được đánh dấu **[v2]**.

---

## 1. Kiến thức nghiệp vụ MES

### 1.1 Bức tranh tổng thể

**Module này nằm ở đâu trong nhà máy.** Đi dọc line PCB-A: SMT (in kem hàn, gắp linh kiện, hàn reflow) → Assembly → Test → Pack. Ở mỗi công đoạn, ngoài việc "làm ra sản phẩm", còn có việc "kiểm tra sản phẩm có đạt không". Việc này thuộc về bộ phận Quality (QC/QA). M3 đã xử lý kết quả OK/NG theo từng serial; M7 làm phần sâu hơn:

| Câu hỏi của xưởng | Công cụ trong M7 |
| --- | --- |
| Kích thước/điện áp/độ dày này có nằm trong dung sai bản vẽ không? | Inspection plan + kiểm tra USL/LSL |
| Quy trình đang trôi (drift) dù chưa ra NG không? | SPC: control chart, Western Electric rules |
| Quy trình có đủ "khỏe" để sản xuất ổn định không? | Cp/Cpk |
| Lỗi này ghi lại thế nào, ai quyết định xử lý ra sao? | NCR + disposition + MRB |
| Nghi ngờ một lô, làm sao chặn không cho đi tiếp? | Hold/Release xuyên module |

**Ai dùng.** QC inspector (đo, nhập số), QC engineer (đọc control chart, mở NCR), Supervisor (phê duyệt disposition, xử lý Hold), Operator (bị chặn khi scan serial Hold — người dùng "gián tiếp"), Planner (chịu ảnh hưởng khi WO thiếu sản lượng vì hàng bị giữ).

**Vì sao nhà máy cần.** Ba lý do, đều có tiền:

1. Lỗi phát hiện càng muộn càng tốn. Một board lỗi phát hiện ở Test tốn công sửa; phát hiện sau khi đóng gói giao khách tốn chi phí thu hồi, phạt, và mất uy tín. Quy tắc kinh nghiệm hay nghe ở xưởng: chi phí sửa lỗi tăng gấp bậc mười qua mỗi công đoạn (con số minh họa, không phải định luật).
2. Khách hàng điện tử/ô tô yêu cầu chứng cứ: dữ liệu đo, control chart, Cpk, hồ sơ NCR. Đến audit mà không đưa được hồ sơ là mất đơn hàng.
3. SPC báo động trước khi có sản phẩm NG. Kiểm tra USL/LSL chỉ nói "con này hỏng rồi"; SPC nói "quy trình đang trôi, sửa ngay trước khi hỏng".

**Vị trí kỹ thuật trong hệ thống.** M7 đọc dữ liệu từ M3 (serial, trạm, route), M6 (lot, truy xuất), M1 (DefectCode, RoutingStep) và **ghi ngược** vào M3/M6 (đổi trạng thái serial/lot thành Hold). Đây là module duy nhất mà hành động của nó có hiệu lực ở các module khác — nên phần Hold/Release cần thiết kế cẩn thận (mục 2.6).

### 1.2 Khái niệm & thuật ngữ

#### QC, QA và các loại kiểm tra

- **QC (Quality Control)**: kiểm tra sản phẩm/quy trình để phát hiện lỗi. **QA (Quality Assurance)**: xây dựng hệ thống, quy trình để lỗi ít xảy ra (audit, quy trình chuẩn, đào tạo). Ngoài đời hay lẫn lộn, nhưng người QC ở xưởng chủ yếu làm việc kiểm tra và xử lý bất thường.
- Kiểm tra chia theo vị trí:

| Loại | Viết tắt | Kiểm tra cái gì, ở đâu | Ví dụ ở xưởng PCB-A |
| --- | --- | --- | --- |
| Incoming | IQC | NVL vừa nhập kho, trước khi cho lên line | Đo điện trở mẫu 5 con từ reel `R-10K-0603`, kiểm tra ngày hết hạn, độ ẩm MSD |
| In-process | IPQC | Trong lúc sản xuất (kiểm tra tuần tra, first article) | Đo độ dày kem hàn sau in; kiểm tra lệch vị trí linh kiện |
| Final | FQC | Thành phẩm trước khi đóng gói | Test chức năng, kiểm tra ngoại quan |
| Outgoing | OQC | Lô hàng trước khi giao khách | Lấy mẫu từ pallet, kiểm tra nhãn/thùng |

- **First Article Inspection (FAI)**: kiểm tra sản phẩm đầu tiên sau khi setup/đổi model/đổi ca, trước khi chạy hàng loạt. Nếu sai ở FAI thì chưa ai làm hàng lỗi.
- Trong điện tử còn có thiết bị kiểm tra tự động: **SPI** (kiểm tra kem hàn), **AOI** (kiểm tra ngoại quan bằng camera), **X-ray** (kiểm tra mối hàn BGA), **ICT** (test mạch điện từng điểm), **FCT** (test chức năng). Dữ liệu của chúng đến MES qua API (simulator vai trạm test trong dự án này). M7 tập trung vào **số đo** (measurement) mà thiết bị hoặc người tạo ra.

#### Inspection plan và đặc tính (characteristic)

**Inspection plan** trả lời: kiểm tra cái gì, ở công đoạn nào, so với giới hạn nào, lấy bao nhiêu mẫu, bao lâu một lần. Một dòng trong plan gồm:

| Trường | Ý nghĩa | Ví dụ |
| --- | --- | --- |
| RoutingStep | Công đoạn (M1) — bước có `IsInspection = true` | Step 30 — FCT |
| Characteristic | Đặc tính cần đo | `3V3_RAIL` (điện áp rail 3,3 V) |
| Target (nominal) | Giá trị danh định | 3,300 V |
| USL / LSL | Giới hạn kỹ thuật trên/dưới (theo bản vẽ, spec khách) | 3,465 V / 3,135 V (3,3 V ± 5%) |
| SampleSize | Số đơn vị lấy mỗi lần kiểm | 1 (đo từng board) |
| Frequency | Tần suất | Mỗi board; hoặc 1 board / 20 board; hoặc mỗi 30 phút |

Hai loại dữ liệu:

- **Variable data (dữ liệu liên tục, 計量値)**: có giá trị số đo được — điện áp, độ dày, lực kéo. Đây là loại SPC/Cpk làm việc, và là phạm vi M7.
- **Attribute data (dữ liệu đếm, 計数値)**: đạt/không đạt, số lỗi trên mỗi board. Có control chart riêng (p-chart, c-chart) — ngoài phạm vi MVP, chỉ cần biết tồn tại.

Một số đặc tính chỉ có **một phía**: độ phẳng chỉ có USL (càng nhỏ càng tốt), lực kéo chân linh kiện chỉ có LSL (càng lớn càng tốt). Hàm tính Cpk của bạn phải xử lý USL hoặc LSL bị null.

#### Sampling (lấy mẫu)

- **Kiểm tra 100% (全数検査, 100% inspection)**: kiểm tra từng cái. Dùng khi đặc tính quan trọng và có thể đo tự động rẻ (FCT test từng board).
- **Kiểm tra lấy mẫu (抜取検査, sampling)**: kiểm tra một phần rồi suy ra cho cả lô/dòng sản phẩm. Dùng khi đo tốn kém hoặc phá hủy sản phẩm (đo lực kéo chân — kéo đứt là hỏng).
- **AQL (Acceptable Quality Level, 合格品質水準)**: mức chất lượng chấp nhận được, dùng với bảng lấy mẫu chuẩn (ISO 2859-1 / ANSI Z1.4): lô N cái → lấy n cái → nếu số lỗi ≤ Ac thì nhận lô, ≥ Re thì trả lô. Bảng cụ thể tra tài liệu chuẩn, MVP không cài đặt bảng AQL; chỉ lưu `SampleSize` và `Frequency`.
- **Rational sampling cho SPC**: lấy mẫu sao cho biến động *trong* mẫu là biến động "bình thường" của quy trình, và biến động *giữa* các mẫu mới là tín hiệu. Cách đơn giản nhất và là cách M7 dùng: mỗi lần đo 1 giá trị (**Individuals chart, I-MR**).

Ví dụ số: Line 1 chạy 200 PCB-A. Đặc tính `3V3_RAIL` đo ở FCT trên **mọi** board (100%, tự động) → 200 số đo. Đặc tính `SOLDER_PASTE_THICKNESS` (SPI) mỗi 20 board lấy 1 → 10 số đo. Cả hai đều vào control chart, nhưng chart đầu dày hơn nên phát hiện drift nhanh hơn.

#### USL/LSL và UCL/LCL — hai cặp giới hạn hay bị nhầm

Đây là chỗ nhầm phổ biến nhất của người mới. Hai cặp giới hạn **đến từ hai nguồn hoàn toàn khác nhau**, trả lời hai câu hỏi khác nhau:

| | USL / LSL (Specification limits, 規格上限/下限) | UCL / LCL (Control limits, 管理上限/下限) |
| --- | --- | --- |
| Nguồn | Con người định ra: bản vẽ, spec khách hàng | Dữ liệu tự nói: tính từ chính quy trình (mean ± 3σ) |
| Tiếng nói của | Khách hàng (Voice of Customer) | Quy trình (Voice of Process) |
| Áp dụng cho | **Từng sản phẩm**: đạt hay không | **Hành vi quy trình**: ổn định hay không |
| Câu hỏi | "Con này có dùng được không?" | "Quy trình có đang làm việc như mọi khi không?" |
| Đổi khi nào | Khi khách đổi spec / ECN | Khi quy trình thật sự đổi (thay máy, đổi vật tư) |
| Vượt giới hạn nghĩa là | Sản phẩm NG | Có nguyên nhân bất thường (special cause) cần tìm |

Ví dụ ẩn dụ đời thường: cửa ra vào cao 2,0 m (USL — do kiến trúc sư quyết). Nhóm khách hôm nay có chiều cao trung bình 1,70 m, σ = 0,08 m → UCL = 1,70 + 3×0,08 = 1,94 m. Ai cao 1,96 m: chưa đụng cửa (chưa vượt USL) nhưng đã "bất thường" so với nhóm (vượt UCL) — có thể nhóm hôm nay có cầu thủ bóng rổ lẫn vào.

**Ví dụ số với rail 3,3 V của PCB-A.** Spec: 3,3 V ± 5% → **LSL = 3,135 V, USL = 3,465 V**. Quy trình ổn định trong 25 board đầu (baseline): mean = 3,302 V, σ = 0,040 V. Khi đó:

```
UCL = 3,302 + 3 × 0,040 = 3,422 V
LCL = 3,302 − 3 × 0,040 = 3,182 V
```

Ba tình huống cho thấy hai cặp giới hạn độc lập nhau:

| Giá trị đo | So với USL/LSL (3,135–3,465) | So với UCL/LCL (3,182–3,422) | Kết luận |
| --- | --- | --- | --- |
| 3,300 | Đạt | Trong | Bình thường |
| 3,440 | **Đạt** | **Vượt UCL** (z = 3,45) | Sản phẩm dùng được, nhưng SPC báo động: quy trình bất thường, đi tìm nguyên nhân trước khi ra NG |
| 3,470 | **NG** (> USL) | Vượt UCL | Sản phẩm NG (đi Repair — M3) *và* quy trình bất thường |
| 3,120 | NG (< LSL) | Vượt LCL | Cả hai |

Tình huống ngược, còn nguy hiểm hơn: quy trình "in control" nhưng vẫn làm ra hàng lỗi. Giả sử σ = 0,070 V. Khi đó UCL = 3,302 + 0,21 = 3,512 V, **cao hơn USL = 3,465**. Chart trông rất đẹp (mọi điểm trong UCL/LCL), nhưng theo phân phối chuẩn khoảng 1,85% board (≈ 18.500 ppm) nằm ngoài spec. Control chart chỉ nói quy trình ổn định, không nói quy trình đủ tốt — đó là việc của Cpk.

Quy tắc nhớ: **USL/LSL nhìn từ ngoài vào (yêu cầu), UCL/LCL nhìn từ trong ra (thực tế).** Không bao giờ vẽ USL/LSL rồi gọi là "control limit", và không bao giờ tính USL/LSL từ dữ liệu.

#### Control chart đọc thế nào

Control chart (管理図) là đồ thị chuỗi thời gian của giá trị đo, kèm ba đường ngang:

```
 3,465 ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─  USL (spec, đường của khách)
 3,422 ━━━━━━━━━━━━━━━━━━━━━━━━━━  UCL = mean + 3σ
 3,382 ·············································  +2σ  ┐ Zone A
 3,342 ·············································  +1σ  ┤ Zone B
 3,302 ─────────●──●─────●────────  Center line (mean)   ┤ Zone C
 3,262 ·············································  −1σ
 3,222 ·············································  −2σ
 3,182 ━━━━━━━━━━━━━━━━━━━━━━━━━━  LCL = mean − 3σ
 3,135 ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─  LSL
        điểm 1  2  3  4  5 ...  (thứ tự thời gian, không phải thứ tự giá trị)
```

Cách đọc:

- **Common cause variation (偶然原因)**: dao động tự nhiên do rất nhiều yếu tố nhỏ (nhiệt độ phòng, dung sai linh kiện, hao mòn nhẹ). Điểm nằm lộn xộn quanh đường tâm, trong ±3σ, không có hình dạng. Đây là **bình thường — đừng chỉnh máy**. Chỉnh máy khi chỉ có common cause (over-adjustment, tampering) làm quy trình dao động *nhiều hơn*.
- **Special cause variation (異常原因)**: có nguyên nhân cụ thể — reel lệch dung sai, nozzle mòn, đổi ca, nhiệt reflow lệch. Dấu hiệu là điểm ngoài giới hạn hoặc **hình dạng bất thường** (chuỗi cùng phía, xu hướng tăng dần...). Đây là lúc phải điều tra.
- Con số 3σ: với phân phối chuẩn, khoảng 99,73% điểm nằm trong ±3σ, tức 0,27% (≈ 1/370 điểm) nằm ngoài dù quy trình hoàn toàn bình thường. Nên mỗi báo động 1-điểm-ngoài-3σ có xác suất nhỏ là báo động giả; nếu xuất hiện dồn dập thì gần như chắc chắn là thật.
- Chart chia 3 vùng mỗi phía: **Zone C** (0–1σ), **Zone B** (1–2σ), **Zone A** (2–3σ). Western Electric rules dùng các vùng này.

**Hai pha (Phase I / Phase II).** Phase I: thu thập 20–25 điểm (hoặc hơn) khi quy trình được xác nhận ổn định, tính mean và σ → cố định thành **giới hạn baseline**. Phase II: vẽ điểm mới lên các giới hạn đã cố định đó và giám sát. Tuyệt đối không tính lại UCL/LCL sau mỗi điểm mới: khi quy trình bắt đầu trôi, giới hạn "tự trôi theo" và chart không bao giờ báo động.

#### Western Electric rules (WE rules)

Điểm vượt 3σ chỉ bắt được dịch chuyển lớn. Dịch chuyển nhỏ — mean trôi 0,5σ — có thể không bao giờ chạm 3σ nhưng vẫn làm hỏng Cpk. Western Electric (bộ Statistical Quality Control Handbook, 1956) bổ sung các rule dựa trên xác suất: những hình dạng này rất hiếm khi quy trình ổn định, nên xuất hiện là có vấn đề.

Ví dụ dùng baseline `mean = 3,302 V, σ = 0,040 V` (đường ranh vùng: +1σ = 3,342; +2σ = 3,382; +3σ = 3,422; phía dưới đối xứng 3,262 / 3,222 / 3,182).

| Rule | Điều kiện | Bắt được loại thay đổi nào | Ví dụ số |
| --- | --- | --- | --- |
| **WE1** | 1 điểm ngoài 3σ | Cú sốc lớn, đột ngột (hỏng jig, đổi nhầm vật tư) | Đo 3,430 V → z = +3,2 > 3 → vi phạm. (Vẫn đạt USL 3,465!) |
| **WE2** | 2 trong 3 điểm liên tiếp ngoài 2σ, **cùng phía** | Dịch chuyển vừa | Chuỗi 3,390 / 3,310 / 3,400 → z = +2,2 / +0,2 / +2,45 → 2 trong 3 điểm > +2σ → vi phạm ở điểm thứ 3 |
| **WE3** *(tùy chọn, **[v2]** tắt mặc định)* | 4 trong 5 điểm liên tiếp ngoài 1σ, **cùng phía** | Dịch chuyển nhỏ đến vừa | Chuỗi 3,350 / 3,360 / 3,310 / 3,370 / 3,350 → z = +1,2 / +1,45 / +0,2 / +1,7 / +1,2 → 4 trong 5 điểm > +1σ → vi phạm ở điểm thứ 5 |
| **WE4** (run rule) | N điểm liên tiếp cùng phía đường tâm; **[v2]** N = 7 | Dịch chuyển nhỏ, dai dẳng (tool mòn dần, nhiệt độ trôi) | Xem bảng dưới |

Ví dụ WE4 đầy đủ — 8 điểm liên tiếp, đều trong Zone C, đều trên đường tâm 3,302:

| # | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Giá trị (V) | 3,318 | 3,325 | 3,311 | 3,322 | 3,309 | 3,330 | 3,316 | 3,321 |
| z | +0,40 | +0,58 | +0,23 | +0,50 | +0,18 | +0,70 | +0,35 | +0,48 |

Không điểm nào vượt 1σ, không điểm nào gần UCL, và **tất cả đều nằm trong spec 3,135–3,465**. Nhìn từng điểm thì không có gì đáng chú ý. (**[v2]** Với N = 7, hệ thống báo ngay ở điểm thứ 7; điểm thứ 8 không tạo alert mới nhờ dedupe.) Nhưng 8 điểm liên tiếp cùng phía có xác suất khoảng 2 × 0,5⁸ ≈ 0,8% nếu quy trình đứng yên — quá hiếm. Kết luận: mean đã dịch lên khoảng +0,5σ. Đây chính là ví dụ mà SPC có giá trị: phát hiện khi chưa có board NG nào.

Lưu ý về con số "8":

- Western Electric dùng 8 điểm; Nelson rules (bộ rule phổ biến khác) dùng 9; sổ tay SPC của AIAG (ngành ô tô) và nhiều nhà máy Nhật dùng 7. **[v2] Dự án chốt 7** (spec v2 B5). Trong code, `RunLength` vẫn là tham số cấu hình.
- **[v2] Vì sao chỉ 3 rule.** Bật càng nhiều rule càng nhiều báo động giả (đoạn dưới). Ba rule đã bắt được: cú sốc lớn (WE1), dịch chuyển vừa (WE2), dịch chuyển nhỏ dai dẳng (run 7), đúng ba kiểu thay đổi hay gặp ở lò reflow và mạch nguồn. WE3 vẫn có trong code (cùng một hàm tổng quát) nhưng tắt mặc định.
- Bốn rule trên là bộ WE cơ bản. Nelson còn có thêm: 6 điểm liên tiếp tăng hoặc giảm (xu hướng), 14 điểm liên tiếp đan xen lên xuống, 15 điểm liên tiếp trong ±1σ (variation *quá ít* — đáng ngờ: số liệu bị copy, thiết bị đo độ phân giải thấp). Không cần làm ở MVP; thiết kế rule engine để thêm được.
- Dùng nhiều rule cùng lúc làm tăng tỷ lệ báo động giả. Chỉ dùng 1 rule "1 điểm ngoài 3σ" thì cỡ 370 điểm mới có 1 báo giả (ARL ≈ 370); bật cả 4 rule WE thì con số này giảm còn khoảng vài chục đến ~100 điểm (số tham khảo, phụ thuộc bộ rule). Đây là đánh đổi độ nhạy — độ ồn, xem 1.5.

#### Cp và Cpk (Process Capability, 工程能力指数)

Câu hỏi: quy trình *ổn định* (control chart sạch) đã đủ tốt để làm ra hàng nằm trong spec chưa? Cp/Cpk so **độ rộng spec** với **độ rộng quy trình** (6σ).

```
Cp  = (USL − LSL) / (6σ)                       # tiềm năng: nếu quy trình căn giữa
Cpu = (USL − mean) / (3σ)
Cpl = (mean − LSL) / (3σ)
Cpk = min(Cpu, Cpl)                            # thực tế: tính cả độ lệch tâm
```

- **Cp** không quan tâm mean nằm đâu. Nó chỉ nói "bề rộng quy trình vừa hay không vừa khung spec".
- **Cpk** phạt khi mean lệch khỏi trung tâm spec. Cpk ≤ Cp, bằng nhau khi mean đúng giữa spec.
- Đặc tính một phía: chỉ tính Cpu (chỉ có USL) hoặc Cpl (chỉ có LSL), Cp không có nghĩa.

**Ví dụ tính tay (10 điểm, để dễ tính; thực tế cần tối thiểu 20–25).** Đo rail 3V3 trên 10 board: `3,32  3,36  3,28  3,33  3,31  3,37  3,27  3,32  3,35  3,29`. Spec: LSL = 3,135; USL = 3,465.

```
Bước 1 — mean:   tổng = 33,20  →  mean = 33,20 / 10 = 3,320
Bước 2 — độ lệch so với mean:
                 0,00  +0,04  −0,04  +0,01  −0,01  +0,05  −0,05  0,00  +0,03  −0,03
Bước 3 — bình phương (đơn vị 10⁻⁴):
                 0     16     16     1      1      25     25     0     9      9   → tổng 102 ×10⁻⁴ = 0,0102
Bước 4 — σ mẫu (chia n−1 = 9):  σ² = 0,0102 / 9 = 0,0011333  →  σ = 0,03367
Bước 5 — giới hạn kiểm soát:   UCL = 3,320 + 3×0,03367 = 3,421
                               LCL = 3,320 − 3×0,03367 = 3,219
Bước 6 — Cp  = (3,465 − 3,135) / (6 × 0,03367) = 0,330 / 0,2020 = 1,634
Bước 7 — Cpu = (3,465 − 3,320) / (3 × 0,03367) = 0,145 / 0,1010 = 1,436
         Cpl = (3,320 − 3,135) / (3 × 0,03367) = 0,185 / 0,1010 = 1,832
         Cpk = min(1,436 ; 1,832) = 1,436
```

Đọc kết quả: Cp = 1,63 (quy trình đủ hẹp), nhưng Cpk = 1,44 thấp hơn vì mean = 3,320 lệch 0,02 V lên phía USL (trung tâm spec là 3,300). Khoảng cách Cp − Cpk cho biết "chỉ cần căn lại tâm là được tăng thêm bao nhiêu".

Ví dụ minh họa Cp và Cpk khác nhau: hai line cùng σ = 0,0367 V → Cp = 0,330 / 0,220 = 1,50 cho cả hai. Line A mean 3,300 → Cpk = 1,50, ≈ 7 ppm ngoài spec. Line B mean 3,400 → Cpk = (3,465 − 3,400) / 0,110 = 0,59 và ≈ 3,8% board ngoài spec. Cùng Cp mà chất lượng khác nhau hoàn toàn. Vì vậy báo cáo Cpk, không chỉ Cp.

Mức tham khảo (dùng cho phân phối chuẩn, quy trình ổn định; ngưỡng cụ thể do khách hàng quy định — nhiều nơi yêu cầu tối thiểu 1,33, đặc tính an toàn/quan trọng yêu cầu 1,67):

| Cpk | Ý nghĩa | Tỷ lệ ngoài spec (2 phía, căn giữa) |
| --- | --- | --- |
| < 1,00 | Không đủ năng lực | > 2.700 ppm |
| 1,00 | Vừa đủ, không có dự phòng | ≈ 2.700 ppm (0,27%) |
| 1,33 | Thường là ngưỡng tối thiểu | ≈ 63–66 ppm |
| 1,67 | Yêu cầu cao | ≈ 0,5 ppm |

Điều kiện để Cpk có nghĩa: (1) quy trình đã ổn định (control chart không có tín hiệu) — tính Cpk trên quy trình đang trôi cho con số vô nghĩa; (2) dữ liệu gần chuẩn; (3) đủ mẫu; (4) hệ thống đo đủ tốt (Gauge R&R / MSA — ngoài phạm vi, chỉ cần biết: nếu dụng cụ đo dao động nhiều thì Cpk tệ do dụng cụ, không phải quy trình). Chi tiết Cp/Cpk (dùng σ within) và Pp/Ppk (dùng σ toàn bộ) có khác nhau — xem mục 4.

#### NCR, disposition, MRB

- **Nonconformance (不適合)**: sản phẩm/vật tư/quy trình không đáp ứng yêu cầu.
- **NCR (Non-Conformance Report, 不適合報告書)**: hồ sơ ghi lại một sự không phù hợp: cái gì, bao nhiêu, ở đâu, khi nào, ai phát hiện, quyết định xử lý thế nào, ai duyệt. Là bằng chứng cho audit.
- **Disposition (処置)**: quyết định xử lý số hàng không phù hợp. Bốn lựa chọn phổ biến (khớp spec):

| Disposition | Nghĩa | Khi nào dùng | Điều kiện đi kèm |
| --- | --- | --- | --- |
| **Use-as-is** (特別採用, 特採) | Dùng nguyên trạng dù không đạt spec | Sai lệch nhỏ, không ảnh hưởng chức năng/an toàn | Cần lý do kỹ thuật bằng văn bản; nếu vi phạm spec của khách thì phải có **concession/deviation** (khách đồng ý). Quyền duyệt cao nhất |
| **Rework** (手直し) | Làm lại/sửa theo quy trình được duyệt để đạt spec | Sửa được, có quy trình | Sau rework phải kiểm tra lại (re-inspection) từ đầu. Trong M3, "Repair" và "Rework" đều đi qua nhánh Repair → quay lại trạm kiểm tra |
| **Scrap** (廃棄) | Hủy bỏ | Không sửa được hoặc sửa tốn hơn giá trị | Ghi nhận số lượng và giá trị phế phẩm; serial → `Scrapped` (trạng thái cuối) |
| **Return to Supplier — RTV** (返品) | Trả nhà cung cấp | Chỉ với **vật tư mua ngoài** (lot) | Thường kèm SCAR (Supplier Corrective Action Request, 仕入先是正処置要求) yêu cầu nhà cung cấp tìm nguyên nhân |

Ngoài đời còn các lựa chọn như **Sort/Screen** (kiểm tra 100% để tách con tốt/xấu rồi mới xử lý từng nhóm) hay **Downgrade**. MVP gộp: kiểm tra 100% là hoạt động, kết quả tách thành NCR con cho từng nhóm.

- **MRB (Material Review Board, tiếng Nhật thường gọi là buổi họp/hội đồng xử lý hàng không phù hợp)**: nhóm liên phòng ban — QC, kỹ sư quy trình, sản xuất, đôi khi mua hàng/khách hàng — họp và quyết định disposition. Lý do cần nhóm chứ không để một người quyết: mỗi disposition có hệ quả tiền, rủi ro và pháp lý khác nhau (QC muốn scrap cho an toàn, sản xuất muốn rework cho đủ sản lượng, mua hàng muốn RTV để đòi bồi thường). Trong MVP, MRB được mô phỏng bằng hai vai: **QC đề xuất disposition, Supervisor phê duyệt**, hai người khác nhau (xem 1.4).

#### Hold / Release

- **Hold (保留)**: đánh dấu serial/lot là "không được đi tiếp" cho đến khi có quyết định. Ngoài đời là tấm thẻ đỏ dán lên thùng, đưa vào khu cách ly (quarantine). MES phải làm tương đương bằng điện tử.
- **Release (解除)**: dỡ Hold sau khi có quyết định (use-as-is, hoặc đã rework và kiểm tra lại đạt...). Phải có người có quyền và lý do.
- **Containment (流出防止 / 暫定処置)**: hành động tức thời chặn hàng nghi ngờ trước khi biết nguyên nhân. Hold chính là containment trong hệ thống. Nguyên tắc: **hold trước, hỏi sau** — mở rộng Hold hơi thừa rẻ hơn nhiều so với để hàng lỗi lọt.

**Vì sao Hold phải chặn xuyên module.** Thẻ đỏ vật lý chỉ có tác dụng nếu mọi người nhìn thấy nó. Trong MES, "mọi người" là các cửa mà hàng đi qua:

| Cửa | Module | Hành vi khi Hold |
| --- | --- | --- |
| Scan serial ở trạm (kể cả Pack) | M3 route check (quy tắc 2) | Trả kết quả chặn, kiosk nền đỏ |
| Nạp reel/lot lên máy | M6 verification | Từ chối nạp, báo đỏ |
| Simulator/trạm tự động gọi API scan | M3 (cùng API) | Bị chặn như kiosk |
| Hoàn thành WO/nhập kho thành phẩm | M2/M9 (mở rộng) | Serial Hold không tính vào QtyGood đủ điều kiện hoàn thành |

Nếu chỉ chặn ở một cửa (ví dụ chỉ trong màn hình QC), operator vẫn scan được serial đó ở Pack và hàng nghi ngờ đi ra khách. Vì vậy trạng thái Hold phải nằm ở **một nơi duy nhất là nguồn sự thật** (`Serial.Status`, `MaterialLot.Status` + bảng `HoldRecord`), còn **mọi điểm thực thi** đều kiểm tra nguồn đó.

#### 8D (chỉ giới thiệu)

8D là khung giải quyết vấn đề mà nhiều khách hàng (ô tô, điện tử) yêu cầu nhà cung cấp dùng khi có khiếu nại. NCR ghi *sự việc*; 8D đi sâu tìm *nguyên nhân gốc* và ngăn tái diễn.

| Bước | Nội dung | Liên hệ với MES |
| --- | --- | --- |
| D0 | Chuẩn bị, đánh giá có cần 8D không | NCR nghiêm trọng hoặc lặp lại |
| D1 | Lập nhóm | |
| D2 | Mô tả vấn đề (5W2H: cái gì, ở đâu, khi nào, bao nhiêu...) | Lấy từ NCR + dữ liệu truy xuất M6 |
| D3 | Hành động cô lập tạm thời (containment) | **Hold** (mục 2.6) |
| D4 | Tìm nguyên nhân gốc (5 Why, fishbone) | Đối chiếu control chart, lot, máy, ca |
| D5 | Chọn hành động khắc phục | |
| D6 | Thực hiện và xác nhận hiệu quả | Theo dõi Cpk, control chart sau khắc phục |
| D7 | Ngăn tái diễn (cập nhật quy trình, FMEA, control plan) | Cập nhật inspection plan/master data |
| D8 | Ghi nhận, chúc mừng nhóm | |

MVP không làm 8D. Chỉ cần thêm trường tùy chọn `CapaReference` (văn bản tự do) trong NCR để trỏ tới hồ sơ 8D/CAPA bên ngoài. CAPA = Corrective and Preventive Action (是正処置・予防処置).

#### [v2] Biểu đồ I-MR

Dữ liệu của dự án là **từng giá trị riêng lẻ** (mỗi serial một số đo điện áp; mỗi 30 giây một mẫu nhiệt độ lò), không có nhóm con tự nhiên. Biểu đồ chuẩn cho loại dữ liệu này là **I-MR** (Individuals – Moving Range), gồm hai biểu đồ xếp chồng:

- **I chart**: từng giá trị, đường tâm, UCL/LCL (đã mô tả ở trên).
- **MR chart**: `MRᵢ = |xᵢ − xᵢ₋₁|`, đường tâm `MR̄`, `UCL_MR = 3,267 × MR̄`, `LCL_MR = 0`. MR chart theo dõi **độ dao động** ngắn hạn: một điểm MR vọt lên nghĩa là hai lần đo liên tiếp chênh nhau bất thường (đo sai, đổi vật tư đột ngột), kể cả khi mean không đổi.

Với 10 điểm mẫu ở trên: các MR là 0,04 / 0,08 / 0,05 / 0,02 / 0,06 / 0,10 / 0,05 / 0,03 / 0,06 → `MR̄ = 0,49 / 9 = 0,0544`, `UCL_MR = 0,178`. Không MR nào vượt → dao động ổn định.

X̄-R (nhóm con 4–5 mẫu) **không làm** ở dự án (spec v2): cần thiết kế lấy mẫu theo nhóm, không khớp dữ liệu theo serial.

#### [v2] FAI — kiểm tra sản phẩm đầu tiên

Nghiệp vụ đã giới thiệu ở M2 (mục f2). Phần của M7 là **phiếu kiểm FAI** (checklist) và **quyết định đạt/không đạt** của QC:

| Hạng mục kiểm (mẫu cho `PCB-A`) | Cách kiểm | Nguồn dữ liệu trong MES |
| --- | --- | --- |
| Linh kiện thực tế đúng BOM | So danh sách lot/item đã dùng cho serial mẫu với BOM snapshot của WO | **Tự động**: truy xuất ngược (M6) so với `WorkOrderMaterial` (M2) |
| Kết quả test chức năng | Serial mẫu đã OK ở bước Test | **Tự động**: `ScanLog` (M3) |
| Số đo đặc tính (3V3_RAIL) trong spec | Đọc `Measurement` của serial mẫu | **Tự động**: `Measurement` |
| Ngoại quan, hàn, phân cực | QC nhìn bằng kính lúp/AOI | Tick tay + ghi chú |
| Nhãn đúng model/format | QC đối chiếu | Tick tay |

Kết hợp kiểm tự động và kiểm tay là điểm mạnh của MES so với phiếu giấy: QC không phải chép lại dữ liệu đã có. FAI **đạt** → `WorkOrder.FaiStatus = Passed`, route check M3 mở cho cả lô, kiosk hết banner vàng. **Không đạt** → serial mẫu chuyển NG (Repair), WO giữ `Pending`, serial kế tiếp vào line thành mẫu mới (M2). Ngoài đời còn FAI theo chuẩn AS9102 (hàng không) với biểu mẫu chi tiết hơn; biết tên là đủ.

#### [v2] SPC tự động từ dữ liệu máy

Ở trên, số đo đến từ trạm kiểm tra (mỗi serial một số). Spec v2 thêm nguồn thứ hai: **thông số quy trình đọc tự động từ máy** qua Edge Gateway (M4b): nhiệt độ vùng peak của lò reflow. Mỗi `SpcSampleEverySec` giây (ví dụ 30 giây), M7 lấy giá trị `Good` gần nhất của tag thành một `Measurement` không gắn serial, rồi chạy đúng rule engine như số đo thủ công.

Khác biệt quan trọng: số đo máy **không có serial**, nên vượt spec **không làm NG serial nào** (Q1 không áp dụng). Thay vào đó hệ thống tạo `SpcAlert` (và Andon loại Quality ở M4 nếu vượt USL/LSL). Muốn biết serial nào bị ảnh hưởng, QC dùng **Hold theo khoảng thời gian**: mọi serial đã scan OK ở trạm chứa máy đó trong khoảng `[t1, t2)` (mục 2.6). Đó là bước 10 của kịch bản demo spec v2.

### 1.3 Một ngày ở xưởng: reel điện trở lệch và cái Hold

Nhân vật: **Hương** (QC engineer), **Tuấn** (operator trạm Pack), **Minh** (supervisor ca sáng), **Thảo** (QC manager), **Lan** (planner). Line 1, WO `WO-0001`: 200 PCB-A. Đặc tính giám sát: `3V3_RAIL` tại FCT (đo tự động 100%). Baseline đã chốt từ tuần trước: mean 3,302 V, σ 0,040 V, spec 3,135–3,465 V, Cpk baseline ≈ 1,36.

1. **06:30.** Hương mở màn hình Inspection Plan, xác nhận đặc tính `3V3_RAIL` gắn với bước FCT (`IsInspection = true`). **[v2]** WO vừa release có `FaiStatus = Pending`: line chỉ chạy được serial mẫu. Serial mẫu qua Test xong, Hương mở phiếu FAI: phần tự động (BOM khớp, Test OK, 3V3_RAIL = 3,301 V) đã xanh, cô tick phần ngoại quan và nhãn, bấm **Pass**. Kiosk các trạm hết banner vàng, line chạy hàng loạt.
2. **07:00–09:00.** Simulator (vai trạm test) gửi kết quả FCT từng serial kèm số đo qua API. Mỗi request thành một dòng `Measurement`, kèm Result OK. Control chart trên Back-office cập nhật; điểm nằm lộn xộn quanh 3,302.
3. **09:10.** Reel `LOT-R10K-0930-A` (điện trở 10 kΩ, 1% — chia áp cho mạch hồi tiếp của regulator) được operator SMT nạp lên máy (M6 verification cho qua: đúng Item, còn hạn). Từ board thứ 101, điện áp rail hơi cao hơn — nhà cung cấp giao lô có giá trị thực nằm cuối dung sai, không phải hỏng hẳn.
4. **09:40.** Tám board liên tiếp đo 3,318 / 3,325 / 3,311 / 3,322 / 3,309 / 3,330 / 3,316 / 3,321 V. Tất cả đạt spec, tất cả trong 1σ. Không ai để ý — trừ hệ thống. Rule **WE4** (run 7) vi phạm ở điểm thứ 7: hệ thống tạo `SpcAlert` (rule WE4, plan `3V3_RAIL`) và hiện chấm đỏ trên chart + dòng trong danh sách alert. Hương nhận thông báo.
5. **09:50.** Hương xem chart: mean mới ước tính ≈ 3,32 V, dịch +0,5σ, đúng lúc reel mới nạp. Cô mở màn hình truy xuất (M6), tìm **truy xuất xuôi** theo `LOT-R10K-0930-A`: **87 serial** đã dùng lot này. Tình trạng: 55 đang giữa các trạm, 24 đang chờ ở Pack, 8 đã đóng gói (Completed).
6. **09:55.** Hương bấm "Hold tất cả" ngay trên màn hình truy xuất, chọn lý do "Suspected R12 tolerance — SPC alert WE4", tick "Create NCR". Hệ thống trong một transaction: tạo 87 `HoldRecord` (serial) + 1 `HoldRecord` (lot), đặt `Serial.Status = Hold` (lưu `StatusBeforeHold`), đặt `MaterialLot.Status = Hold`, tạo `NCR-0001` (Source = SpcAlert, trạng thái Open).
7. **10:00.** Tuấn scan các serial không thuộc lot xấu: vẫn qua bình thường. Nhưng khi anh scan một serial thuộc nhóm 87 serial bị hold ở Pack: kiosk nền đỏ, còi, dòng chữ "Serial is on hold — see NCR-0001". Cùng lúc operator SMT thử nạp cuộn khác của cùng lot `LOT-R10K-0930-A` (reel #2): M6 từ chối "Lot is on hold".
8. **10:30.** Hương đưa NCR sang **UnderReview**: đo mẫu 20 board trong số 87 (bằng đồng hồ đo ngoài) → kết luận: giá trị thực của điện trở lệch +0,8%, vẫn trong dung sai 1% của điện trở, nhưng cộng với dung sai các linh kiện khác đẩy rail lên sát biên. 3 trong 20 board chạm 3,44–3,46 V — sát USL. Chưa hỏng nhưng không còn dự phòng.
9. **14:00.** Họp MRB (Hương, Thảo, Minh, đại diện mua hàng). Quyết định: (a) reel `LOT-R10K-0930-A` **Return to Supplier**, gửi SCAR; (b) 87 serial: thực hiện **Rework** — thay R12 bằng điện trở lô tốt, test lại FCT đạt mới cho đi tiếp; 8 board đã đóng gói phải mở thùng. Hương tạo `NCR-0002` cho nhóm serial (NCR-0001 dành cho lot) và ghi disposition.
10. **14:30.** Hương nhập disposition đề xuất (vẫn ở UnderReview); Minh (supervisor — người khác Hương) bấm **Approve**, NCR chuyển **Dispositioned**. Hệ thống: lot → trạng thái `Returned`(mở rộng, xem 2.5); 87 serial → hủy Hold, chuyển sang luồng Repair của M3 (serial ở trạng thái `Repair`, chờ trạm Repair scan).
11. **Ngày hôm sau.** Rework xong, mỗi serial phải test FCT đạt để đi tiếp. Khi không còn Hold active, NCR được **Close**. Chart được xem lại: giá trị quay về quanh 3,302. Hương ghi vào NCR mã hồ sơ 8D của khách để theo dõi nguyên nhân gốc tại nhà cung cấp.

Chú ý nhiều điểm sẽ thành yêu cầu phần mềm: FAI chặn line tới khi QC duyệt, alert khi tất cả điểm đều đạt spec (run rule), Hold hàng loạt lấy từ truy xuất, một transaction, chặn tại hai module, tách người đề xuất và người duyệt, restore trạng thái khi release, đóng NCR chỉ khi hết Hold.

### 1.4 Quy tắc nghiệp vụ & lý do tồn tại

| # | Quy tắc | Lý do |
| --- | --- | --- |
| Q1 | Giá trị đo ngoài [LSL, USL] → sản phẩm NG ngay: serial chuyển sang luồng NG của M3 (Repair); bằng đúng USL hoặc LSL vẫn đạt | Spec là hợp đồng với khách. Biên: bản vẽ thường ghi "≤ USL"; xác nhận với QA khi áp dụng thật |
| Q2 | So sánh spec bằng `decimal` (đã làm tròn theo độ phân giải dụng cụ), thống kê bằng `double` | Số thực nhị phân: 3,4650000000000003 > 3,465 cho kết quả NG oan |
| Q3 | UCL/LCL = mean ± 3σ, tính từ tập baseline ổn định (≥ 20 điểm), **cố định** cho đến khi người có quyền tính lại có lý do | Nếu tự tính lại theo từng điểm thì giới hạn trôi theo quy trình và không bao giờ báo động |
| Q4 | Rule engine đánh giá theo thứ tự thời gian đo (`MeasuredAt`), không theo thứ tự nhập | Hình dạng chuỗi (WE2–WE4) chỉ có nghĩa trên chuỗi thời gian |
| Q5 | Một `SpcAlert` đang mở cho mỗi (plan, rule); vi phạm lặp lại trong khi alert còn mở không tạo alert mới | Nếu không, sau WE4 ở điểm 8 thì điểm 9, 10... đều vi phạm → hàng chục alert giống nhau, QC bỏ qua tất cả |
| Q6 | SPC alert không tự động làm NG serial và không tự Hold. Chỉ Q1 mới làm NG. Hold do người quyết định (QC/Supervisor) | SPC nói "quy trình có vấn đề", không nói con nào hỏng. Hold tự động vì báo động giả sẽ dừng line |
| Q7 | Serial/Lot ở trạng thái Hold thì route check (M3) chặn; lot Hold thì không nạp lên máy được (M6) | Xem 1.2: nguồn sự thật duy nhất, nhiều điểm thực thi |
| Q8 | Hold nhiều lần trên cùng một đối tượng được phép (hai NCR khác nhau cùng nghi một serial). Chỉ khi **hết** Hold đang active mới khôi phục trạng thái cũ | Nếu Release một NCR mà serial vẫn còn bị NCR khác nghi ngờ thì hàng lọt |
| Q9 | Release/Hold cần lý do bắt buộc và ghi người, thời điểm; Release chỉ do role QC hoặc Supervisor | Truy vết trách nhiệm; audit khách hàng hay hỏi "ai cho phép thả lô này" |
| Q10 | Serial `Scrapped` không Hold, không Release. Hold không đổi số lượng, chỉ đổi khả năng đi tiếp | Trạng thái cuối không quay lại |
| Q11 | NCR đóng chỉ khi có disposition **và** người phê duyệt, và người phê duyệt khác người đề xuất | Phân tách nhiệm vụ (segregation of duties): người sửa hồ sơ không tự duyệt cho mình |
| Q12 | NCR không đóng được khi còn `HoldRecord` active gắn với nó | Tránh "đóng hồ sơ nhưng hàng vẫn bị giữ" hoặc ngược lại "hàng đã thả mà hồ sơ chưa xử lý" |
| Q13 | `ReturnToVendor` chỉ áp dụng cho đối tượng loại Lot | Serial là thành phẩm của mình, không trả cho nhà cung cấp |
| Q14 | `Use-as-is` bắt buộc có ghi chú lý do và (nếu ngoài spec khách) tham chiếu concession | Là disposition rủi ro nhất, thường bị lạm dụng để "cho xong" |
| Q15 | Inspection plan đã có số đo thì không sửa tại chỗ: đổi USL/LSL → tạo version mới; mỗi `Measurement` ghi `InspectionPlanId` và kết quả Result tại thời điểm đo | Giống BOM/Routing version (M1): kết quả lịch sử không đổi khi spec đổi |
| Q16 | Kết quả đo là insert-only (không sửa, không xóa); sửa số đo sai bằng bản ghi hiệu chỉnh có lý do | Dữ liệu đo là bằng chứng chất lượng |
| Q17 **[v2]** | FAI chỉ QC ghi kết quả; serial mẫu phải đã OK ở bước kiểm tra cuối (Test) trước khi chấm FAI; phần kiểm tự động (BOM, Test, số đo) phải xanh mới cho chọn Pass | FAI là chữ ký chất lượng; Pass khi dữ liệu máy đã báo sai là tự lừa mình |
| Q18 **[v2]** | Số đo tự động từ máy chỉ lấy giá trị `Quality = Good`; không gắn serial nên không làm NG serial; vượt spec → `SpcAlert` + Andon Quality | Giá trị lỗi cảm biến không được làm sai chart; không có serial thì không có "con nào hỏng" để NG |
| Q19 **[v2]** | Hold theo thùng/pallet được **mở rộng thành Hold từng serial** bên trong (thùng/pallet không có trạng thái Hold riêng, M3b) | Một nguồn sự thật cho Hold |
| Q20 **[v2]** | NCR có 4 trạng thái chính: Open → UnderReview → Dispositioned → Closed (+ Cancelled). Disposition do QC đề xuất trong UnderReview, Supervisor (người khác) duyệt mới sang Dispositioned | Đủ kiểm soát (Q11) mà ít trạng thái hơn, ít màn hình hơn |

### 1.5 Sai lầm/sự cố thường gặp ngoài đời

1. **Nhầm control limit với spec limit.** Vẽ USL/LSL rồi gọi là UCL/LCL; hoặc tính UCL/LCL bằng cách "mean ± spec". Hệ quả: chart luôn "đẹp" trong khi quy trình đã dịch. Khắc phục: hai cặp đường khác màu, khác kiểu nét, nhãn rõ.
2. **Tính lại UCL/LCL mỗi khi có điểm mới** (dùng cả dữ liệu đang trôi). Chart tự thích nghi với sự trôi. Khắc phục: baseline cố định (Q3).
3. **Baseline lẫn điểm bất thường.** Tính σ trên dữ liệu có cả những điểm ngoài kiểm soát làm σ phình, UCL/LCL rộng và không bắt được gì. Khắc phục: loại các điểm có nguyên nhân bất thường đã biết trước khi tính baseline.
4. **Tính Cpk trên quy trình chưa ổn định.** Cpk 1,8 nghe rất đẹp, nhưng tuần sau mean trôi sang chỗ khác. Kiểm tra control chart sạch trước.
5. **Chỉnh máy vì một điểm trong vùng thường.** Operator thấy điểm cao hơn giá trị trước liền chỉnh. Đây là tampering — làm tăng dao động. Quy tắc: chỉ hành động khi có tín hiệu rule.
6. **Báo động giả nhiều → QC tắt cảnh báo.** Bật đủ mọi Nelson rule cho mọi đặc tính, mỗi giờ 5 alert, sau một tuần không ai đọc. Bắt đầu với 4 rule WE, đo tỷ lệ alert thật/giả, rồi mới chỉnh.
7. **Không dedupe alert** (Q5).
8. **Dụng cụ đo lỗi hoặc thiếu hiệu chuẩn.** Số đo sai làm control chart và Cpk sai. Hiệu chuẩn gauge, kiểm tra Gauge R&R là việc ngoài MES nhưng MES nên ghi `GaugeId` để truy vết sau này.
9. **Hold tại một chỗ, quên chỗ khác.** Chỉ chặn trong route check mà quên chặn nạp lot, hoặc có đường API "tắt" chưa kiểm tra Hold (ví dụ endpoint dành cho simulator). Test xuyên module là bắt buộc (mục 3, bước 9).
10. **Hold serial đã dùng lot nhưng để lot vẫn nạp được**, máy tiếp tục làm thêm serial mới từ lot xấu. Phải hold *cả lot lẫn serial*.
11. **Release không khôi phục đúng trạng thái.** Serial Completed bị hold rồi release, thành `Created` hoặc `InProcess`; quay lại luồng sản xuất từ đầu. Cần lưu `StatusBeforeHold` (2.6).
12. **NCR chỉ ghi lý do "lỗi", không có số lượng/serial/lot.** Không truy được ảnh hưởng. NCR phải link tới danh sách target cụ thể.
13. **Use-as-is bị dùng như đường tắt**, không duyệt đúng cấp. Tách người đề xuất/duyệt và ghi lý do bắt buộc.
14. **Hold quá rộng, dừng cả line.** Truy xuất xuôi theo lot phổ biến (ví dụ lot keo dán dùng cho mọi serial) → hold hàng trăm serial vô nghĩa. Cần preview số lượng + xác nhận trước khi Hold hàng loạt, và cần cách thu hẹp (theo khoảng thời gian, theo máy).
15. **Múi giờ.** Số đo lưu UTC (quy ước dự án); chart hiển thị theo giờ nhà máy. Ranh giới ca/ngày sản xuất xử lý như M5.

### 1.6 Bảng thuật ngữ

| English | Tiếng Việt | 日本語 | Giải thích ngắn |
| --- | --- | --- | --- |
| Quality Control (QC) | Kiểm soát chất lượng | 品質管理 | Kiểm tra để phát hiện lỗi |
| Quality Assurance (QA) | Đảm bảo chất lượng | 品質保証 | Hệ thống/quy trình để ngăn lỗi |
| Incoming inspection (IQC) | Kiểm tra đầu vào | 受入検査 | Kiểm NVL nhập kho |
| In-process inspection (IPQC) | Kiểm tra trong chuyền | 工程内検査 | Kiểm giữa công đoạn |
| Final inspection (FQC) | Kiểm tra cuối | 最終検査 | Kiểm thành phẩm |
| Outgoing inspection (OQC) | Kiểm tra xuất hàng | 出荷検査 | Kiểm trước giao khách |
| First article inspection (FAI) | Kiểm tra sản phẩm đầu | 初物検査 | Sau setup/đổi model |
| Inspection plan | Kế hoạch kiểm tra | 検査計画 / 検査基準書 | Kiểm gì, giới hạn, mẫu, tần suất |
| Characteristic | Đặc tính | 特性 / 検査項目 | Đại lượng cần kiểm |
| Variable data | Dữ liệu liên tục | 計量値 | Giá trị đo được |
| Attribute data | Dữ liệu đếm | 計数値 | Đạt/không đạt, số lỗi |
| 100% inspection | Kiểm tra toàn bộ | 全数検査 | Mọi sản phẩm |
| Sampling inspection | Kiểm tra lấy mẫu | 抜取検査 | Một phần đại diện |
| AQL | Mức chất lượng chấp nhận | 合格品質水準 | Cơ sở bảng lấy mẫu |
| Upper/Lower Spec Limit (USL/LSL) | Giới hạn kỹ thuật trên/dưới | 規格上限 / 規格下限 | Từ bản vẽ, khách hàng |
| Upper/Lower Control Limit (UCL/LCL) | Giới hạn kiểm soát trên/dưới | 管理上限 / 管理下限 | Tính từ dữ liệu: mean ± 3σ |
| Center line (CL) | Đường tâm | 中心線 | Mean của baseline |
| Standard deviation (σ) | Độ lệch chuẩn | 標準偏差 | Đo độ phân tán |
| Control chart | Biểu đồ kiểm soát | 管理図 | Chuỗi thời gian + giới hạn |
| Common cause | Nguyên nhân chung (ngẫu nhiên) | 偶然原因 | Dao động tự nhiên |
| Special cause | Nguyên nhân đặc biệt | 異常原因 | Có nguyên nhân cụ thể, phải điều tra |
| Western Electric rules | Các rule Western Electric | ウェスタン・エレクトリック・ルール | Rule phát hiện hình dạng bất thường |
| Process capability (Cp/Cpk) | Năng lực quy trình | 工程能力指数 | Spec so với độ rộng quy trình |
| Non-conformance (NC) | Sự không phù hợp | 不適合 | Không đạt yêu cầu |
| NCR | Báo cáo không phù hợp | 不適合報告書 | Hồ sơ ghi nhận và xử lý |
| Disposition | Quyết định xử lý | 処置 | Use-as-is / Rework / Scrap / RTV |
| Use-as-is | Sử dụng nguyên trạng | 特別採用 (特採) | Chấp nhận dù lệch spec |
| Rework | Làm lại | 手直し | Sửa để đạt spec |
| Scrap | Phế | 廃棄 | Hủy bỏ |
| Return to Supplier (RTV) | Trả nhà cung cấp | 返品 | Chỉ với vật tư mua |
| MRB | Hội đồng xử lý hàng lỗi | 不適合品審査 (MRB) | Nhóm liên phòng ban quyết định disposition |
| Hold | Giữ lại/cách ly | 保留 | Chặn không cho đi tiếp |
| Release | Thả (dỡ Hold) | 解除 | Cho đi tiếp sau quyết định |
| Quarantine | Khu cách ly | 隔離 | Nơi vật lý để hàng bị Hold |
| Containment | Hành động cô lập tạm thời | 流出防止 / 暫定処置 | Chặn trước khi biết nguyên nhân |
| Root cause | Nguyên nhân gốc | 真因 / 根本原因 | Nguyên nhân sâu nhất |
| CAPA | Hành động khắc phục và phòng ngừa | 是正処置・予防処置 | |
| 8D | 8 bước giải quyết vấn đề | 8D | Khung điều tra khiếu nại |
| SCAR | Yêu cầu nhà cung cấp khắc phục | 仕入先是正処置要求 | Kèm RTV |
| Concession / Deviation | Chấp thuận ngoại lệ | 特別採用申請 / 逸脱 | Khách đồng ý nhận hàng lệch spec |
| Gauge R&R (MSA) | Phân tích hệ thống đo | ゲージR&R / 測定システム解析 | Độ tin cậy của dụng cụ đo |
| SPI / AOI / ICT / FCT | Kiểm kem hàn / ngoại quan / mạch / chức năng | はんだ印刷検査 / 外観検査 / インサーキットテスト / ファンクションテスト | Thiết bị kiểm tra ở line SMT |

---

## 2. Kiến thức lập trình

Ghi chú phạm vi: MediatR/FluentValidation, EF Core configuration, ICommand/RelayCommand: xem Module 1. State machine, rowversion, transaction: xem Module 2. Route check và keyboard wedge: Module 3. Recursive CTE truy xuất: Module 6. Audit interceptor: Module 0. SignalR/Dispatcher: Module 4. Chart trong WPF (LiveCharts2): Module 5. Mục này chỉ nói phần mới của M7.

### 2.1 Thống kê SPC trong C# (mean, σ, UCL/LCL, Cp/Cpk) và unit test

**Nó là gì.** Một static class thuần (pure functions) trong `MiniMes.Domain/Quality/Spc/SpcCalculator.cs`: nhận danh sách số, trả về mean, σ, UCL/LCL, Cp/Cpk. Không đụng DB, không đụng thời gian.

**Vấn đề nó giải quyết.** Cách "ngây thơ": tính trong câu SQL (`AVG`, `STDEV`) trực tiếp ở controller, hoặc trong ViewModel WPF. Nhược điểm: không unit test được với số tính tay, không tái sử dụng khi tính ở chỗ khác (simulator, báo cáo), và logic thống kê rải nhiều nơi. Tách thành hàm thuần thì test bằng dữ liệu mẫu như 10 điểm ở mục 1.2, kết quả khớp tay.

**Khái niệm cốt lõi.**

- σ mẫu chia cho `n − 1` (Bessel), khác σ quần thể chia `n`. Với 10 điểm ở 1.2: chia 9 → 0,03367; chia 10 → 0,03194 — chênh gần 5%. Chọn n−1 và ghi trong tài liệu.
- Các trường hợp biên: n < 2 (không tính được), σ = 0 (mọi giá trị bằng nhau → chia 0 khi tính Cpk), USL hoặc LSL null (đặc tính một phía).
- `double` cho thống kê; `decimal` cho so sánh spec (Q2). Đọc từ DB `decimal(18,6)` rồi `(double)` khi đưa vào hàm thống kê.

```csharp
// MiniMes.Domain/Quality/Spc/SpcCalculator.cs
public sealed record SpcStats(int Count, double Mean, double Sigma, double Ucl, double Lcl);
public sealed record Capability(double? Cp, double? Cpu, double? Cpl, double? Cpk);

public static class SpcCalculator
{
    public static SpcStats Compute(IReadOnlyList<double> values)
    {
        if (values.Count < 2)
            throw new ArgumentException("Need at least 2 values.", nameof(values));

        double mean  = values.Average();
        double sumSq = values.Sum(v => (v - mean) * (v - mean));
        double sigma = Math.Sqrt(sumSq / (values.Count - 1));   // n-1: sigma mẫu

        return new SpcStats(values.Count, mean, sigma, mean + 3 * sigma, mean - 3 * sigma);
    }

    // [v2] I-MR: moving range giữa hai điểm liên tiếp
    public static (double MrBar, double UclMr) MovingRange(IReadOnlyList<double> values)
    {
        if (values.Count < 2) throw new ArgumentException("Need at least 2 values.", nameof(values));
        double mrBar = Enumerable.Range(1, values.Count - 1).Average(i => Math.Abs(values[i] - values[i - 1]));
        return (mrBar, 3.267 * mrBar);                          // D4 = 3,267 cho n = 2
    }

    public static Capability CalcCapability(SpcStats s, double? usl, double? lsl)
    {
        if (s.Sigma == 0) return new Capability(null, null, null, null); // tránh chia 0

        double? cpu = usl is null ? null : (usl - s.Mean) / (3 * s.Sigma);
        double? cpl = lsl is null ? null : (s.Mean - lsl) / (3 * s.Sigma);
        double? cp  = (usl is null || lsl is null) ? null : (usl - lsl) / (6 * s.Sigma);
        double? cpk = cpu is null ? cpl : cpl is null ? cpu : Math.Min(cpu.Value, cpl.Value);

        return new Capability(cp, cpu, cpl, cpk);
    }
}
```

Unit test khớp số tính tay ở mục 1.2 (xUnit):

```csharp
public class SpcCalculatorTests
{
    private static readonly double[] Data =
        { 3.32, 3.36, 3.28, 3.33, 3.31, 3.37, 3.27, 3.32, 3.35, 3.29 };

    [Fact]
    public void Compute_MatchesHandCalculation()
    {
        var s = SpcCalculator.Compute(Data);
        // Assert.Equal(expected, actual, precision): so sánh sau khi làm tròn tới 'precision' chữ số
        Assert.Equal(3.32,   s.Mean,  precision: 6);
        Assert.Equal(0.0337, s.Sigma, precision: 4);
        Assert.Equal(3.421,  s.Ucl,   precision: 3);
        Assert.Equal(3.219,  s.Lcl,   precision: 3);
    }

    [Fact]
    public void Capability_MatchesHandCalculation()
    {
        var c = SpcCalculator.CalcCapability(SpcCalculator.Compute(Data), usl: 3.465, lsl: 3.135);
        Assert.Equal(1.634, c.Cp!.Value,  precision: 3);
        Assert.Equal(1.436, c.Cpk!.Value, precision: 3);
    }

    [Fact]
    public void MovingRange_MatchesHandCalculation()          // [v2]
    {
        var (mrBar, ucl) = SpcCalculator.MovingRange(Data);
        Assert.Equal(0.0544, mrBar, precision: 4);
        Assert.Equal(0.178,  ucl,   precision: 3);
    }

    [Fact]
    public void Capability_OneSided_OnlyUsl() { /* Cp = null, Cpk = Cpu */ }
    [Fact]
    public void Capability_ZeroSigma_ReturnsNulls() { /* mọi giá trị bằng nhau */ }
}
```

**Bẫy thường gặp.**

- Dùng `Assert.Equal(1.634, x)` không có precision với `double` → thất bại vì sai số nhị phân. Luôn dùng overload có `precision`.
- Chọn giá trị expected là kết quả chạy code rồi dán lại (test tự chứng minh chính nó). Expected phải từ tính tay/Excel độc lập.
- Ước lượng σ cho I chart: bài dạy SPC chuẩn dùng `MR-bar / 1,128` (moving range trung bình chia d2 của n = 2), giới hạn = mean ± 2,66 × MR-bar. Sample σ nhạy hơn với biến thiên dài hạn và là lựa chọn của MVP; xem mục 4.
- Trộn chart baseline và điểm mới trong cùng lần tính (2.3).

### 2.2 Western Electric rules như rule engine nhỏ

**Nó là gì.** Một hàm nhận chuỗi giá trị đo (đã sắp theo thời gian) + baseline (mean, σ) và trả về danh sách điểm vi phạm rule nào.

**Vấn đề nó giải quyết.** Cách ngây thơ: bốn vòng `for` lồng nhau, mỗi rule một hàm dài, đầy điều kiện `i - 4 >= 0`, dễ sai off-by-one, và thêm rule mới là copy-paste. Quan sát then chốt: **cả bốn rule WE đều là cùng một mẫu "N trong M điểm liên tiếp vượt k×σ cùng phía"**:

| Rule | N | M | k |
| --- | --- | --- | --- |
| WE1 | 1 | 1 | 3 |
| WE2 | 2 | 3 | 2 |
| WE3 (tùy chọn) | 4 | 5 | 1 |
| WE4 | 7 | 7 | 0 |

Vậy viết **một** hàm tổng quát rồi cấu hình bốn dòng dữ liệu. Rule ngoài mẫu này (xu hướng 6 điểm...) viết thành class riêng cùng interface `ISpcRule`.

**Khái niệm cốt lõi.**

- **Chuẩn hóa về z-score**: `z = (x − mean) / σ`. Sau đó mọi rule chỉ so sánh z với hằng số (1, 2, 3), không phụ thuộc đơn vị đo.
- **Cùng phía**: đếm riêng phía trên (z > k) và phía dưới (z < −k), không trộn.
- **Chỉ báo tại điểm cuối cửa sổ** (điểm mới nhất) để mỗi vi phạm gắn với một điểm cụ thể — cần cho dedupe (Q5) và cho việc đánh dấu trên chart.
- **Online vs offline**: khi có điểm đo mới, chỉ cần đánh giá cửa sổ gần nhất và xem điểm cuối có bị báo không. Khi vẽ lại cả chart thì đánh giá cả chuỗi. Cùng một hàm dùng cho cả hai.

```csharp
// MiniMes.Domain/Quality/Spc/WesternElectric.cs
public sealed record WeRule(string Code, int N, int M, double K);
public sealed record RuleViolation(string Rule, int Index);

public static class WesternElectric
{
    public static readonly WeRule We3 = new("WE3", 4, 5, 1.0);   // 4/5 điểm ngoài 1σ cùng phía — [v2] tùy chọn, tắt mặc định

    public static IReadOnlyList<WeRule> Default(int runLength = 7) => new WeRule[]   // [v2] 3 rule, run = 7
    {
        new("WE1", 1, 1, 3.0),          // 1 điểm ngoài 3σ
        new("WE2", 2, 3, 2.0),          // 2/3 điểm ngoài 2σ cùng phía
        new("WE4", runLength, runLength, 0.0), // N điểm liên tiếp cùng phía tâm
    };

    public static IReadOnlyList<RuleViolation> Evaluate(
        IReadOnlyList<double> values, double mean, double sigma, IEnumerable<WeRule> rules)
    {
        var z = values.Select(v => (v - mean) / sigma).ToArray();
        var result = new List<RuleViolation>();
        foreach (var rule in rules)
            for (int i = 0; i < z.Length; i++)
                foreach (int side in new[] { +1, -1 })          // +1: phía trên, -1: phía dưới
                {
                    if (side * z[i] <= rule.K) continue;        // điểm i phải vượt ngưỡng đúng phía
                    int from = Math.Max(0, i - rule.M + 1);
                    int count = 0;
                    for (int j = from; j <= i; j++) if (side * z[j] > rule.K) count++;
                    if (count >= rule.N) { result.Add(new RuleViolation(rule.Code, i)); break; }
                }
        return result;
    }
}
```

Test bằng chính các ví dụ ở mục 1.2:

```csharp
[Fact]
public void SevenPointsAboveCenter_FiresWe4_OnlyAtLastPoint()      // [v2] run = 7
{
    double[] x = { 3.318, 3.325, 3.311, 3.322, 3.309, 3.330, 3.316 };
    var v = WesternElectric.Evaluate(x, mean: 3.302, sigma: 0.04, WesternElectric.Default());
    var only = Assert.Single(v);                 // không rule nào khác được kích hoạt
    Assert.Equal("WE4", only.Rule);
    Assert.Equal(6, only.Index);
}

[Fact]
public void SixPointsAboveCenter_DoesNotFire()                       // test âm
    => Assert.Empty(WesternElectric.Evaluate(new[] { 3.318, 3.325, 3.311, 3.322, 3.309, 3.330 },
                                             3.302, 0.04, WesternElectric.Default()));

[Theory]
[InlineData(new[] { 3.39, 3.31, 3.40 }, "WE2", 2)]              // 2/3 ngoài +2σ
[InlineData(new[] { 3.43 }, "WE1", 0)]                          // 1 điểm ngoài +3σ
// WE3 (tùy chọn): test riêng với rules = Default().Append(WesternElectric.We3)
public void Rule_Fires_AtExpectedIndex(double[] x, string rule, int index)
{
    var v = WesternElectric.Evaluate(x, 3.302, 0.04, WesternElectric.Default());
    Assert.Contains(v, r => r.Rule == rule && r.Index == index);
}
```

**Bẫy thường gặp.**

- Điểm nằm đúng đường tâm (z = 0) không thuộc phía nào (điều kiện `> 0` cho cả hai phía loại nó ra) — chuỗi cùng phía bị ngắt. Đây là cách phổ biến, ghi lại trong test.
- Cửa sổ đầu chuỗi ngắn hơn M: cho phép đếm trên cửa sổ ngắn hơn (như code trên). Với WE4 vẫn cần đủ N điểm nên không báo sớm; với WE2/WE3 ở đầu chuỗi có thể báo sớm hợp lệ (2 điểm đầu cùng ngoài 2σ).
- Một run dài sinh vi phạm ở **mọi** điểm từ điểm thứ 7 trở đi. Dedupe ở tầng use case (2.3), không sửa hàm rule.
- σ = 0 làm z = ±∞/NaN. Chặn ở tầng tạo `ControlLimitSet` (σ phải > 0).
- Khi cập nhật liên tục (online), đánh giá cả chuỗi 10.000 điểm mỗi lần đo mới là lãng phí. Chỉ lấy `max(M) − 1` điểm gần nhất cộng điểm mới: ví dụ 6 điểm cũ + 1 mới cho WE4 (N = 7).

### 2.3 Use case ghi số đo: từ số đo tới SpcAlert và NG

**Nó là gì.** Command `RecordInspectionCommand` (MediatR — M1) nhận serial + trạm + danh sách (characteristic, value) và thực hiện chuỗi: xác định plan → kiểm tra spec → lưu → chạy SPC → tạo alert → (nếu ngoài spec) chuyển sang luồng NG của M3.

**Vấn đề nó giải quyết.** Cách ngây thơ: kiosk gọi hai API độc lập — một cho scan (M3), một cho lưu số đo (M7). Rủi ro: số đo lưu được nhưng scan thất bại (mạng), hoặc ngược lại; và hai nơi tự quyết NG/OK dẫn đến kết quả mâu thuẫn. Thiết kế đề xuất: **một request duy nhất**, phía server tính OK/NG từ số đo (client không tự quyết), rồi dùng chính luồng scan của M3 (nên route check vẫn áp dụng: không đo được serial chưa đến bước này, hoặc đang Hold).

**Khái niệm cốt lõi.**

- Thứ tự: (1) route check (M3), (2) so spec, (3) lưu `Measurement` (insert-only), (4) tải N điểm gần nhất theo `MeasuredAt` + `ControlLimitSet` đang active, (5) đánh giá WE, (6) chỉ xét vi phạm tại điểm mới nhất, (7) dedupe theo (plan, rule), (8) ghi `SpcAlert`, (9) nếu Result = NG thì gọi cổng scan của M3 với result NG + DefectCode của plan. Tất cả trong **một transaction** (`SaveChangesAsync` một lần).
- **Không tính lại limits** trong use case này (Q3). `ControlLimitSet` là dữ liệu đã lưu; chỉ có command riêng `RecomputeControlLimitsCommand` do QC gọi.
- Truy vấn control chart cần chỉ mục đúng: lấy 100 điểm mới nhất của một plan.

```sql
-- Chỉ mục cho "N điểm gần nhất của một plan" (insert-only, bigint identity theo quy ước dự án)
CREATE INDEX IX_Measurement_Plan_MeasuredAt
    ON dbo.Measurement (InspectionPlanId, MeasuredAt DESC)
    INCLUDE (Value, SerialNo, Result);

-- Dedupe ở mức DB: mỗi (plan, rule) chỉ có 1 alert đang mở
CREATE UNIQUE INDEX UX_SpcAlert_OpenPerRule
    ON dbo.SpcAlert (InspectionPlanId, RuleCode)
    WHERE Status = 'Open';                   -- filtered unique index
```

```csharp
// Skeleton handler (tên IScanGateway / IMeasurementRepository là minh họa của tài liệu, tự đặt theo code của bạn)
public async Task<InspectionResultDto> Handle(RecordInspectionCommand cmd, CancellationToken ct)
{
    await using var tx = await _db.Database.BeginTransactionAsync(ct);

    var plan   = await _plans.GetActiveAsync(cmd.PlanId, ct);
    var value  = cmd.Value;                                           // decimal
    bool ngOut = (plan.Usl is { } u && value > u) || (plan.Lsl is { } l && value < l);

    var m = Measurement.Create(plan.Id, cmd.SerialNo, value, ngOut ? MeasureResult.Ng : MeasureResult.Ok,
                               _clock.UtcNow, cmd.OperatorId);
    _db.Measurements.Add(m);
    await _db.SaveChangesAsync(ct);                                   // cần Id/MeasuredAt trước khi truy vấn chuỗi

    var limits = await _limits.GetActiveAsync(plan.Id, ct);           // baseline cố định
    if (limits is not null)
    {
        await _spcEvaluator.EvaluateLatestAsync(plan.Id, limits, m.Id, ct);     // [v2] tách ra để 2.3b dùng chung:
        // bên trong: lấy 10 điểm gần nhất theo MeasuredAt, WesternElectric.Evaluate(..., Default()),
        // chỉ xét điểm mới nhất, dedupe (plan, rule), thêm SpcAlert
    }

    if (ngOut)                                                        // Q1: ngoài spec → NG qua luồng M3
        await _scan.RegisterNgAsync(cmd.StationId, cmd.SerialNo, plan.DefectCodeOnFail, cmd.OperatorId, ct);
    else
        await _scan.RegisterOkAsync(cmd.StationId, cmd.SerialNo, cmd.OperatorId, ct);

    await _db.SaveChangesAsync(ct);
    await tx.CommitAsync(ct);
    return InspectionResultDto.From(m, ngOut);
}
```

(Khối trên là skeleton minh họa, không phải mã chạy được nguyên xi; các repository/gateway là interface do bạn tự định nghĩa. Nếu bạn dùng `TimeProvider` như M5 thì thay `_clock.UtcNow` bằng `GetUtcNow()`.)

**Bẫy thường gặp.**

- Thứ tự lấy "N điểm gần nhất" phải theo `MeasuredAt` (thời điểm đo), không theo `Id`: số đo từ trạm tự động có thể tới không đúng thứ tự do retry. Nếu khác thứ tự đáng kể, cân nhắc sắp theo (`MeasuredAt`, `Id`).
- Hai request đo đồng thời cùng plan (2 trạm) có thể đều thấy "chưa có alert mở" và cùng chèn → filtered unique index chặn cái thứ hai; bắt `DbUpdateException` và bỏ qua. Không dựa vào `HasOpenAsync` để đảm bảo tính duy nhất.
- Serial NG chỉ vì số đo ngoài spec, nhưng nếu hàm scan M3 là **idempotent** (quy tắc 5 của M3: cùng serial, cùng trạm, vài giây), gửi lại request không được tạo hai `Measurement`. Thêm `ClientRequestId` (Guid) unique nếu simulator retry.
- Đặt logic đánh giá rule trong DB trigger. Đừng: khó test, khó đọc; giữ trong Domain.

### 2.3b [v2] SPC tự động từ tag thông số quy trình

**Nó là gì.** `ProcessSpcSampler` (BackgroundService + `PeriodicTimer` 10 giây + `TimeProvider`, như job OEE ở M5). Với mỗi `MachineTag` có `SpcCharacteristicId` (M4): nếu đã đến hạn lấy mẫu (`SpcSampleEverySec` kể từ mẫu trước), lấy `ProcessValue` **Good** mới nhất có `SourceTs` sau mẫu trước, ghi một `Measurement` (`Source = Machine`, `MachineTagId`, `SerialNo = null`), rồi gọi cùng `ISpcEvaluator` ở 2.3.

**Vấn đề nó giải quyết.** Đưa mọi giá trị nhiệt độ (gửi theo deadband, có thể vài giây một lần) vào SPC sẽ làm các điểm **tự tương quan** (điểm sau gần như bằng điểm trước), rule run 7 báo giả liên tục. Lấy mẫu cách quãng đều (30 giây) cho chuỗi điểm gần độc lập hơn, đúng giả thiết của control chart.

**Khái niệm cốt lõi.**

- Bổ sung `Measurement`: `Source` (`Manual`/`Station`/`Machine`), `MachineTagId` (nullable), `SerialNo` nullable khi `Source = Machine`.
- Inspection plan mẫu: `REFLOW_PEAK_TEMP` (USL 255, LSL 235 °C), baseline `ControlLimitSet` mean 245,0, σ 0,8 (tính từ 1 giờ chạy ổn định của simulator rồi chốt bằng `RecomputeControlLimitsCommand`).
- Vượt USL/LSL: `SpcAlert` loại `OutOfSpec` + gửi `RaiseAndonCommand(Type = Quality)` của M4 (có leo thang). Không NG serial (Q18).
- Không có mẫu mới (máy Offline, tag `Bad`): không ghi gì; sau 3 chu kỳ liên tiếp thiếu mẫu thì ghi cảnh báo "SPC data gap" trên chart. Không bịa giá trị.
- Idempotent: unique `(MachineTagId, SourceTs)` trên `Measurement` khi `Source = Machine`, job chạy trùng không tạo điểm đôi.

```csharp
foreach (var tag in await _tags.SpcEnabledAsync(ct))
{
    var lastSampleTs = await _measurements.LastMachineSampleTsAsync(tag.Id, ct);
    if (lastSampleTs is { } t && now - t < TimeSpan.FromSeconds(tag.SpcSampleEverySec)) continue;   // chưa đến hạn
    var pv = await _processValues.LatestGoodAfterAsync(tag.Id, lastSampleTs, ct);
    if (pv is null) { _gaps.Record(tag.Id, now); continue; }
    var m = Measurement.FromMachine(tag.SpcCharacteristicId!.Value, tag.Id, (decimal)pv.Value, pv.SourceTs);
    _db.Measurements.Add(m);
    await _db.SaveChangesAsync(ct);
    await _spcEvaluator.EvaluateLatestAsync(m.InspectionPlanId, await _limits.GetActiveAsync(m.InspectionPlanId, ct), m.Id, ct);
    if (m.Result == MeasureResult.Ng) await _sender.Send(new RaiseAndonCommand(tag.MachineId, AndonType.Quality, "system"), ct);
}
```

**Kiểm tra nhanh với simulator.** Lệnh `drift L1-REFLOW-01 0.3` (M4b) làm nhiệt tăng 0,3 °C/phút: sau khoảng 7 mẫu liên tiếp trên đường tâm (3,5 phút với mẫu 30 giây) run rule báo; tăng tiếp thì WE2 rồi WE1 báo trước khi chạm USL. Đây là đoạn demo "phát hiện trước khi ra hàng hỏng" mạnh nhất của dự án.

### 2.4 Control chart trong WPF (OxyPlot)

**Nó là gì.** View trong BackOffice hiển thị chuỗi số đo của một plan: đường đo, đường tâm, UCL/LCL (đỏ, nét đứt), USL/LSL (khác kiểu), các điểm vi phạm tô đỏ, và danh sách vi phạm bên cạnh.

**Chọn thư viện.** Toàn dự án dùng **OxyPlot** (Module 5 đã giới thiệu `PlotModel`, `Series`, `Axis`, `PlotView` ở mục 2.6). Control chart tận dụng thêm `LineAnnotation` cho đường ngang (UCL/LCL/CL/USL/LSL) và `ScatterSeries` để tô nổi điểm vi phạm. Xem mục 4.

**Vấn đề nó giải quyết.** Cách ngây thơ: `DataGrid` hiện bảng số đo. QC không thấy được hình dạng (drift, run) — thứ SPC cần. Vẽ tay bằng `Canvas` thì phải tự làm trục, scale, tooltip.

**Khái niệm cốt lõi.** `PlotModel` (model) gồm `Axes`, `Series`, `Annotations`; `PlotView` (control WPF) bind tới `PlotModel`. Model không tự thông báo thay đổi qua `INotifyPropertyChanged`; sau khi sửa dữ liệu phải gọi `InvalidatePlot(true)` hoặc gán model mới. Cập nhật từ SignalR (M4) chạy trên thread khác → phải marshal lên UI thread bằng `Dispatcher`.

```csharp
// BackOffice.Wpf/Quality/ControlChartBuilder.cs  (OxyPlot 2.x; kiểm tra tên property nếu compile lỗi)
public static PlotModel Build(IReadOnlyList<PointDto> pts, LimitsDto lim, IReadOnlySet<int> violations)
{
    var model = new PlotModel { Title = "3V3_RAIL (V)" };
    model.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Title = "Sample #" });
    model.Axes.Add(new LinearAxis
    {
        Position = AxisPosition.Left, Title = "Volt",
        Minimum = (double)lim.Lsl - 0.02, Maximum = (double)lim.Usl + 0.02   // cho thấy cả USL/LSL
    });

    var line = new LineSeries { Color = OxyColors.SteelBlue, MarkerType = MarkerType.Circle, MarkerSize = 3 };
    var bad  = new ScatterSeries { MarkerType = MarkerType.Circle, MarkerFill = OxyColors.Red, MarkerSize = 6 };
    for (int i = 0; i < pts.Count; i++)
    {
        line.Points.Add(new DataPoint(i + 1, pts[i].Value));
        if (violations.Contains(i)) bad.Points.Add(new ScatterPoint(i + 1, pts[i].Value));
    }
    model.Series.Add(line);
    model.Series.Add(bad);

    // Control limits (từ dữ liệu): đỏ, nét đứt.  Spec limits (từ bản vẽ): đen, nét liền.
    void H(double y, OxyColor c, LineStyle s, string t) => model.Annotations.Add(
        new LineAnnotation { Type = LineAnnotationType.Horizontal, Y = y, Color = c, LineStyle = s, Text = t });
    H(lim.Mean, OxyColors.Green, LineStyle.Solid, "CL");
    H(lim.Ucl,  OxyColors.Red,   LineStyle.Dash,  "UCL");
    H(lim.Lcl,  OxyColors.Red,   LineStyle.Dash,  "LCL");
    H((double)lim.Usl, OxyColors.Black, LineStyle.Solid, "USL");
    H((double)lim.Lsl, OxyColors.Black, LineStyle.Solid, "LSL");
    return model;
}
```

```xml
<!-- View: xmlns:oxy="http://oxyplot.org/wpf"  (package OxyPlot.Wpf; kiểm tra README của gói nếu khác) -->
<oxy:PlotView Model="{Binding ChartModel}" />
```

**Bẫy thường gặp.**

- Trục Y tự co theo dữ liệu nên USL/LSL nằm ngoài khung nhìn; đặt `Minimum`/`Maximum` (như trên) hoặc cho tùy chọn "Fit to data" vs "Show spec".
- Nhầm màu: UCL/LCL và USL/LSL phải phân biệt kiểu (mục 1.5, sai lầm 1). Hiện chú giải.
- Tô điểm vi phạm theo `Index` của cửa sổ đang hiển thị, không theo `Id` DB; khi cuộn/phân trang phải ánh xạ lại.
- Thêm điểm mới bằng cách tạo lại cả `PlotModel` mỗi giây → giật. Throttle (đã học ở M4), hoặc chỉ `Add` điểm rồi `InvalidatePlot(true)`.
- Hiển thị giờ trên trục X: dữ liệu UTC, hiển thị theo giờ nhà máy (M5).
- **[v2] MR chart**: `PlotModel` thứ hai đặt ngay dưới I chart, cùng trục X; `LineSeries` các giá trị MR, `LineAnnotation` cho `MR̄` và `UCL_MR`. Hai `PlotView` xếp dọc trong `Grid` là đủ, không cần đồng bộ zoom ở MVP.

### 2.5 NCR workflow (state machine đơn giản)

**Nó là gì.** Aggregate `Ncr` trong `MiniMes.Domain/Quality/Ncr.cs` với vòng đời trạng thái và các method `StartReview()`, `ProposeDisposition()`, `Approve()`, `RejectProposal()`, `Close()`, `Cancel()`. **[v2]** Rút gọn còn 4 trạng thái chính: đề xuất disposition không còn là một trạng thái riêng. Chi tiết state machine và unit test mọi chuyển trạng thái: xem Module 2, mục về state machine trong rich domain model; ở đây chỉ nêu phần riêng của NCR.

**Vòng đời.**

```
Open ──► UnderReview ──(Approve, người khác người đề xuất)──► Dispositioned ──► Closed
  │          │  ▲
  │          │  └── QC ghi/sửa ProposedDisposition (không đổi trạng thái);
  │          │      RejectProposal xóa đề xuất, vẫn UnderReview
  └──────────┴──► Cancelled  (từ Open hoặc UnderReview: mở nhầm/trùng)
```

| Trạng thái | Ý nghĩa | Ai làm | Điều kiện chuyển |
| --- | --- | --- | --- |
| Open | Vừa tạo (từ SPC alert hoặc thủ công) | QC | Có ít nhất 1 target (serial/lot), có Source và Qty |
| UnderReview | Đang điều tra; QC ghi đề xuất disposition (`UseAsIs` / `Rework` / `Scrap` / `ReturnToVendor`) + ghi chú | QC | Người review được ghi nhận; RTV chỉ khi mọi target là lot |
| Dispositioned | Disposition đã được duyệt, hệ thống áp dụng hiệu ứng | Supervisor duyệt (khác người đề xuất) | Có đề xuất; hiệu ứng lên serial/lot chạy cùng transaction |
| Closed | Xong hồ sơ | QC/Supervisor | Không còn `HoldRecord` active gắn NCR; hành động đã hoàn tất |
| Cancelled | Hủy | QC | Chỉ khi chưa Dispositioned; phải giải phóng Hold nếu có |

**Hiệu ứng khi chuyển Dispositioned** (áp dụng cho từng target trong `NcrItem`, trong cùng transaction):

| Disposition | Serial | Lot |
| --- | --- | --- |
| UseAsIs | Release Hold, khôi phục `StatusBeforeHold` | Release Hold → `Available` (**[v2]** cũng là cách duy nhất đưa lot `IqcRejected` của M6 về `Available`) |
| Rework | Release Hold, chuyển sang luồng Repair của M3 (`Status = Repair`), kiểm tra lại mới đi tiếp | Release Hold → `Available` sau kiểm tra lại (ghi chú); MVP đơn giản hóa |
| Scrap | Release Hold rồi `Scrapped` (cuối) | `RemainingQty = 0`, trạng thái `Scrapped` |
| ReturnToVendor | Không áp dụng (Q13) | Trạng thái `Returned`, ghi tham chiếu SCAR/RMA |

Lưu ý: spec liệt kê `MaterialLot.Status` gồm `Available/Hold/Consumed`. `Scrapped` và `Returned` là **giá trị bổ sung** (chi tiết hóa, không thay đổi ý nghĩa các giá trị cũ). Nếu muốn giữ nguyên enum của M6, thay bằng `Consumed` + ghi lý do trong `HoldRecord.ReleaseNote`/NCR — ít rõ ràng hơn.

**Entity gợi ý** (bổ sung chi tiết cho spec `Ncr`):

| Bảng | Cột chính |
| --- | --- |
| `Ncr` | Id, NcrNo (`NCR-0001`), Source (SpcAlert/Inspection/ScanNg/Traceability/Customer/Manual), SourceRef, DefectCodeId, Qty, Status, Disposition, DispositionNote, ConcessionRef, CapaReference, CreatedBy/At, DisposedBy/At, ApprovedBy/At, ClosedAt |
| `NcrItem` | NcrId, TargetType (Serial/Lot), TargetKey |

`NcrNo` sinh tuần tự bằng SEQUENCE (như serial ở M2). Disposition ở mức NCR (một NCR = một quyết định); nếu MRB muốn chia nhóm (12 scrap, 75 rework), tách thành NCR con — cách đơn giản hơn nhiều so với disposition từng item. Ai muốn nâng cấp thì chuyển `Disposition` xuống `NcrItem`.

```csharp
public sealed class Ncr
{
    public NcrStatus Status { get; private set; } = NcrStatus.Open;
    public NcrDisposition? Disposition { get; private set; }
    public string? DispositionNote { get; private set; }
    public string? ProposedBy { get; private set; }
    public string? ApprovedBy { get; private set; }
    private readonly List<NcrItem> _items = new();
    public IReadOnlyList<NcrItem> Items => _items;

    public void StartReview() { Require(NcrStatus.Open); Status = NcrStatus.UnderReview; }

    // [v2] Đề xuất không đổi trạng thái: QC có thể sửa đề xuất nhiều lần trong UnderReview
    public void ProposeDisposition(NcrDisposition d, string note, string by)
    {
        Require(NcrStatus.UnderReview);
        if (string.IsNullOrWhiteSpace(note)) throw new DomainException("Disposition note is required.");
        if (d == NcrDisposition.ReturnToVendor && _items.Any(i => i.TargetType != TargetType.Lot))
            throw new DomainException("Return to vendor applies to lots only.");      // Q13
        (Disposition, DispositionNote, ProposedBy) = (d, note, by);
    }

    public void Approve(string by)
    {
        Require(NcrStatus.UnderReview);
        if (Disposition is null) throw new DomainException("No disposition proposed.");
        if (string.Equals(by, ProposedBy, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("Approver must differ from proposer.");         // Q11
        (ApprovedBy, Status) = (by, NcrStatus.Dispositioned);
    }

    public void RejectProposal(string by)
    { Require(NcrStatus.UnderReview); (Disposition, DispositionNote, ProposedBy) = (null, null, null); }
    public void Close(int activeHolds) // số Hold active còn gắn NCR (do Application truyền vào)
    { Require(NcrStatus.Dispositioned); if (activeHolds > 0) throw new DomainException("Active holds remain."); Status = NcrStatus.Closed; }

    private void Require(NcrStatus expected)
    { if (Status != expected) throw new DomainException($"Invalid transition from {Status}."); }
}
```

**Bẫy thường gặp.**

- Kiểm tra "khác người" bằng `UserId` (không phải tên hiển thị); JWT claim của M0.
- Hiệu ứng lên serial/lot phải nằm ở tầng Application (handler `ApproveNcrCommand`) chứ không nhét vào aggregate `Ncr` — `Ncr` không nên biết `Serial`/`MaterialLot`. Handler gọi `Ncr.Approve()`, rồi gọi dịch vụ Hold/M3/M6 tương ứng, `SaveChanges` một lần.
- Quên chặn đóng NCR khi còn Hold active (Q12) hoặc chỉ kiểm tra ở UI. Kiểm tra ở domain/Application.
- Nút trên WPF bật/tắt theo trạng thái: dùng `CanExecute` như M2, và quyền theo role (`QC` đề xuất, `Supervisor` duyệt). Server vẫn phải kiểm tra lại.
- Tạo NCR từ SpcAlert: liên kết `SpcAlert.NcrId`; đóng alert khi NCR Closed hoặc khi QC đánh dấu "đã xử lý" — chọn một và ghi rõ.

### 2.5b [v2] FAI: phiếu kiểm, kiểm tự động và mở cổng cho M3

**Nó là gì.** Use case `RecordFaiResultCommand(WoId, Checklist, Result, Note)` (role QC) và query `GetFaiWorksheet(WoId)` trả phiếu FAI đã điền sẵn phần tự động. Thay cho endpoint tạm `POST /work-orders/{id}/fai/result` của M3 (bước 3b).

**Entity.**

| Bảng | Cột |
| --- | --- |
| `FaiTemplate` | ItemId, Items (JSON: danh sách `{ code, text, kind: Auto/Manual }`), Version |
| `FaiRecord` (insert-only) | Id, WoId, SerialNo, TemplateVersion, ChecklistJson (kết quả từng mục), AutoChecksPassed, Result (Pass/Fail), Note, InspectedBy, InspectedAt |

**Kiểm tự động** (query đọc, không ghi):

1. `BomMatch`: tập `ItemId` trong truy xuất ngược của serial mẫu (M6 `SerialComponent`) phải bằng tập Item lot-controlled trong `WorkOrderMaterial` (M2). Thiếu hoặc thừa → đỏ, liệt kê item.
2. `TestPassed`: `ScanLog` của serial có OK ở mọi bước `IsInspection`.
3. `MeasurementsInSpec`: mọi `Measurement` của serial có `Result = OK`.

**Luồng ghi (một transaction).** Kiểm Q17 (serial đã qua bước kiểm tra cuối; Pass chỉ khi `AutoChecksPassed`) → thêm `FaiRecord` → `wo.RecordFaiResult(passed)` (M2) → nếu Fail: chuyển serial mẫu sang NG bằng cổng scan M3 (`RegisterNgAsync`, `DefectCode = FAI-FAIL` hoặc mã QC chọn) → `SaveChanges` (rowversion trên `WorkOrder`) → sau commit push SignalR `FaiStatusChanged` cho group line để kiosk tắt banner vàng.

**Nhớ xóa cache.** M3 có thể cache thông tin WO trong `IMemoryCache` (M3 bước 3b). Sau khi đổi `FaiStatus` phải xóa key cache của WO đó, nếu không route check vẫn chặn thêm vài phút.

**Màn hình.** BackOffice "FAI": danh sách WO `FaiStatus = Pending` có `FaiSerialNo` đã qua Test; mở phiếu: phần tự động hiện xanh/đỏ kèm chi tiết, phần tay là checkbox + ghi chú; nút Pass chỉ bật khi phần tự động xanh và mọi mục tay đã tick.

### 2.6 Hold/Release xuyên module

**Nó là gì.** Use case đặt và dỡ Hold cho tập serial/lot, làm thay đổi trạng thái ở dữ liệu của M2/M3 (`Serial`) và M6 (`MaterialLot`) trong một transaction, đồng thời ghi `HoldRecord`.

**Vấn đề nó giải quyết.** Cách ngây thơ 1: mỗi serial một vòng lặp `foreach` load entity → gọi `PlaceOnHold()` → `SaveChanges`. Với 87 serial chấp nhận được; với lot dùng cho 20.000 serial thì tải hết vào bộ nhớ và chậm, trong khi việc cần làm là **chặn ngay**. Cách ngây thơ 2: chỉ set cờ `IsHold`; khi release không nhớ trạng thái cũ, serial Completed trở thành `Created`. Cách ngây thơ 3: Hold ở một module rồi các module khác "sẽ tự biết" — không có cơ chế thì không biết.

**Khái niệm cốt lõi.**

1. **Nguồn sự thật**: `Serial.Status = Hold` và `MaterialLot.Status = Hold`, cộng bảng `HoldRecord` làm lịch sử/lý do. Vì cùng một SQL Server và cùng modular monolith, các điểm thực thi (route check M3, load verification M6) chỉ cần đọc `Status` — đã có sẵn trong quy tắc M3 (#2) và M6. Không cần message bus. (Nếu module tách thành service riêng thì phải chuyển sang sự kiện: xem mục 4.)
2. **Lưu trạng thái trước Hold**: thêm cột `StatusBeforeHold` (nullable) vào `Serial` và `MaterialLot` (chi tiết hóa, không thay đổi enum M2/M6).
3. **Hold chồng nhau (Q8)**: `HoldRecord` mỗi dòng một target; "Hold còn active" = dòng có `ReleasedAt IS NULL`. Khi release, chỉ khôi phục trạng thái nếu **không còn** dòng active nào khác cho target đó.
4. **Set-based**: một `INSERT ... SELECT` để ghi HoldRecord (nguồn từ truy xuất xuôi của M6), một `UPDATE ... JOIN` để đổi trạng thái. Trong T-SQL, vế phải của `SET` luôn dùng **giá trị cũ** của hàng, nên `CASE WHEN Status <> 'Hold' THEN Status ...` đứng trước khi `Status = 'Hold'` trong cùng câu `UPDATE` là hợp lệ.
5. **Concurrency**: `Serial` có `rowversion` (M2). `UPDATE` bằng SQL thô vẫn làm rowversion đổi, nên request scan đang giữ bản cũ sẽ gặp `DbUpdateConcurrencyException` khi ghi, retry và thấy Hold. Chỉ số `HoldRecord (TargetType, TargetKey) WHERE ReleasedAt IS NULL` để đếm Hold active nhanh.

Bảng `HoldRecord` (bổ sung cho spec, mỗi dòng một target):

| Cột | Ghi chú |
| --- | --- |
| Id (bigint identity) | Bảng insert-mostly |
| TargetType | `Serial` / `Lot` (**[v2]** thùng/pallet được mở rộng thành `Serial`, xem dưới) |
| TargetKey | SerialNo hoặc LotNo |
| BatchId (Guid) | Gom các dòng của một lần Hold hàng loạt |
| Reason | Bắt buộc |
| NcrId | Nullable — cho phép hold trước, tạo NCR sau (containment) |
| HeldBy, HeldAt | UTC |
| ReleasedBy, ReleasedAt, ReleaseNote | Null khi còn active |

```sql
-- HOLD theo lot: gọi trong một transaction. Thay khối SELECT bằng truy xuất xuôi (CTE) của M6 nếu có LotGenealogy.
INSERT dbo.HoldRecord (TargetType, TargetKey, BatchId, Reason, NcrId, HeldBy, HeldAt)
SELECT DISTINCT 'Serial', sc.SerialNo, @BatchId, @Reason, @NcrId, @User, SYSUTCDATETIME()
FROM   dbo.SerialComponent sc
JOIN   dbo.Serial s ON s.SerialNo = sc.SerialNo
WHERE  sc.LotNo = @LotNo AND s.Status <> 'Scrapped';          -- Q10
INSERT dbo.HoldRecord (TargetType, TargetKey, BatchId, Reason, NcrId, HeldBy, HeldAt)
VALUES ('Lot', @LotNo, @BatchId, @Reason, @NcrId, @User, SYSUTCDATETIME());

UPDATE s                                                       -- vế phải dùng giá trị CŨ
SET    s.StatusBeforeHold = CASE WHEN s.Status <> 'Hold' THEN s.Status ELSE s.StatusBeforeHold END,
       s.Status = 'Hold'
FROM   dbo.Serial s
JOIN   dbo.HoldRecord h ON h.TargetType = 'Serial' AND h.TargetKey = s.SerialNo AND h.BatchId = @BatchId;

UPDATE l
SET    l.StatusBeforeHold = CASE WHEN l.Status <> 'Hold' THEN l.Status ELSE l.StatusBeforeHold END,
       l.Status = 'Hold'
FROM   dbo.MaterialLot l WHERE l.LotNo = @LotNo;

-- RELEASE cả batch: đóng HoldRecord, rồi chỉ khôi phục target không còn Hold active nào khác (Q8)
UPDATE dbo.HoldRecord SET ReleasedBy = @User, ReleasedAt = SYSUTCDATETIME(), ReleaseNote = @Note
WHERE  BatchId = @BatchId AND ReleasedAt IS NULL;

UPDATE s SET s.Status = s.StatusBeforeHold, s.StatusBeforeHold = NULL
FROM   dbo.Serial s
WHERE  s.Status = 'Hold'
  AND  EXISTS     (SELECT 1 FROM dbo.HoldRecord h WHERE h.BatchId = @BatchId AND h.TargetType = 'Serial' AND h.TargetKey = s.SerialNo)
  AND  NOT EXISTS (SELECT 1 FROM dbo.HoldRecord a WHERE a.TargetType = 'Serial' AND a.TargetKey = s.SerialNo AND a.ReleasedAt IS NULL);
-- (làm tương tự cho dbo.MaterialLot theo LotNo)
```

(Chuỗi trạng thái viết dạng chữ cho dễ đọc; nếu M2/M6 lưu enum dạng số thì đổi literal cho khớp.)

**[v2] Hai nguồn chọn serial mới.** Lệnh Hold chỉ khác nhau ở khối `SELECT` sinh danh sách serial; phần `INSERT HoldRecord` + `UPDATE Serial` giữ nguyên.

```sql
-- (a) Theo pallet hoặc thùng (M3b): mọi serial đang hiệu lực bên trong, trừ Scrapped
SELECT cs.SerialNo
FROM   dbo.CartonSerial cs
JOIN   dbo.Carton c ON c.CartonNo = cs.CartonNo
JOIN   dbo.Serial s ON s.SerialNo = cs.SerialNo
WHERE  cs.UnpackedAt IS NULL AND s.Status <> 'Scrapped'
  AND (c.PalletNo = @PalletNo OR c.CartonNo = @CartonNo);

-- (b) Theo khoảng thời gian qua một máy (SPC tự động, mục 2.3b):
--     serial scan OK ở trạm thuộc Work Center của máy trong [@From, @To)
SELECT DISTINCT sl.SerialNo
FROM   dbo.ScanLog sl
JOIN   dbo.Station st ON st.Id = sl.StationId
JOIN   dbo.Machine m  ON m.WorkCenterId = st.WorkCenterId
JOIN   dbo.Serial s   ON s.SerialNo = sl.SerialNo
WHERE  m.Id = @MachineId AND sl.Result = 'OK'
  AND  sl.ScannedAt >= @From AND sl.ScannedAt < @To
  AND  s.Status <> 'Scrapped';
```

Cách (b) bao phủ rộng hơn thực tế một chút (scan ở trạm SMT xảy ra trước/sau lúc bo thật sự nằm trong lò vài phút). Thêm một khoảng đệm, ví dụ ±10 phút, và ghi rõ trong lý do Hold. Đây là containment: thà dư còn hơn sót (cùng nguyên tắc với M6). Pallet đã `Shipped` vẫn được Hold về mặt dữ liệu (serial bị đánh dấu), màn hình hiển thị cảnh báo "contains shipped units — notify customer".

Gọi từ C#:

```csharp
// Application/Quality/Holds/HoldTargetsHandler.cs — EF Core: ExecuteSqlInterpolatedAsync trên DatabaseFacade
await using var tx = await _db.Database.BeginTransactionAsync(ct);
await _db.Database.ExecuteSqlInterpolatedAsync($@"
    INSERT dbo.HoldRecord (TargetType, TargetKey, BatchId, Reason, NcrId, HeldBy, HeldAt)
    SELECT DISTINCT 'Serial', sc.SerialNo, {batchId}, {cmd.Reason}, {cmd.NcrId}, {_user.Id}, SYSUTCDATETIME()
    FROM dbo.SerialComponent sc JOIN dbo.Serial s ON s.SerialNo = sc.SerialNo
    WHERE sc.LotNo = {cmd.LotNo} AND s.Status <> 'Scrapped'", ct);
// ... các câu UPDATE như trên ...
_db.AuditLogs.Add(AuditLog.ForBatch("HoldBatch", batchId, affectedCount, _user.Id)); // xem bẫy: interceptor không thấy SQL thô
await _db.SaveChangesAsync(ct);
await tx.CommitAsync(ct);
```

**Bẫy thường gặp.**

- **Audit interceptor của M0 chỉ bắt được thay đổi qua ChangeTracker** (`SaveChanges`). `ExecuteSql*` hoặc `ExecuteUpdate` bỏ qua nó. Vì vậy phải tự ghi `AuditLog` (hoặc coi `HoldRecord` là audit chính) cho các thay đổi trạng thái hàng loạt. Bài học chung: tối ưu bằng SQL thô mất luôn những thứ "miễn phí" mà pipeline EF mang lại.
- `ExecuteSqlInterpolated` tham số hóa các giá trị nội suy (an toàn SQL injection), nhưng nếu bạn tự nối chuỗi `$"..."` rồi đưa vào `ExecuteSqlRaw` thì **không** an toàn. Dùng đúng `ExecuteSqlInterpolatedAsync`.
- Hold trong lúc serial đang được scan: ai thắng? Nhờ rowversion (điểm 5) một trong hai sẽ được thực hiện lại. Cần integration test có đua (2 task đồng thời) như DoD của M2.
- Hold lot **không** làm dừng ngay reel đang nạp trên máy — spec chỉ chặn *nạp mới*. Máy vẫn có thể đang chạy với reel bị Hold. Mở rộng gợi ý: khi Hold lot có `MaterialLoad` chưa `UnloadedAt`, tạo Andon/cảnh báo cho supervisor (M4), và khi scan OK ở trạm đó thì hook M6 từ chối ghi `SerialComponent` từ lot Hold (ngăn phát sinh serial mới nhiễm). Ghi vào backlog nếu chưa làm.
- Đừng để Hold WO-level (M2: `InProgress ⇄ OnHold`) lẫn với Hold chất lượng. WO OnHold là dừng kế hoạch (thiếu vật tư, chờ quyết định); Hold M7 là chặn serial/lot cụ thể vì nghi ngờ chất lượng. Hai khái niệm khác nhau, hai chỗ lưu khác nhau.
- Hold hàng loạt nên có bước **preview**: "Lot này đã dùng cho 87 serial (55 InProcess, 24 chờ Pack, 8 Completed). Hold tất cả?" Đây là hộp thoại xác nhận trên màn hình truy xuất (M6, bước 5).
- Release cần quyền (`QC`/`Supervisor`, M0), lý do bắt buộc, và tôn trọng Q12 khi NCR liên quan chưa được duyệt.
- Chặn một cách nhất quán: viết **integration test tổng hợp** (bước 9 mục 3) thay vì tin vào từng unit.

### 2.7 Luồng dữ liệu của module

```
 (a) Đo & SPC
 ┌────────────────────┐   POST /quality/inspections    ┌──────────────────────────────────────────┐
 │ Kiosk QC / Simulator│ ────────────────────────────▶ │ Api QualityController (JWT: QC/Operator) │
 │ (vai trạm test)     │                                └───────────────┬──────────────────────────┘
 └────────────────────┘                                                │ MediatR
                                                          ┌────────────▼─────────────┐
                                                          │ RecordInspectionHandler   │
                                                          │ 1 route check (M3)        │
                                                          │ 2 so USL/LSL (decimal)    │
                                                          │ 3 lưu Measurement         │
                                                          │ 4 WesternElectric.Evaluate│──▶ Domain (SpcCalculator, rules)
                                                          │ 5 SpcAlert (dedupe)       │
                                                          │ 6 NG? → luồng scan M3     │
                                                          └────────────┬─────────────┘
                                                                       ▼
                                                   SQL Server: Measurement, ControlLimitSet, SpcAlert, ScanLog, Serial
                                                                       │ SignalR (M4): "SpcAlertRaised", "MeasurementAdded"
                                                                       ▼
                                                   BackOffice.Wpf: ControlChartView (OxyPlot) + danh sách alert

 (b) NCR & Hold
 ┌───────────────────────────┐  POST /quality/holds       ┌────────────────────────────────────┐
 │ BackOffice: màn hình truy │ ─────────────────────────▶ │ HoldTargetsHandler (1 transaction)  │
 │ xuất M6 → "Hold tất cả"   │                            │  INSERT HoldRecord                  │
 └───────────────────────────┘                            │  UPDATE Serial.Status  = Hold       │
 ┌───────────────────────────┐  POST /quality/ncrs/...    │  UPDATE MaterialLot.Status = Hold   │
 │ BackOffice: màn hình NCR  │ ─────────────────────────▶ │ NCR [v2]: Open→UnderReview          │
 │ (QC đề xuất, Sup. duyệt)  │                            │      →Dispositioned→Closed (Domain) │
 └───────────────────────────┘                            │ Approve → hiệu ứng lên Serial/Lot    │
                                                          └───────────────┬─────────────────────┘
                                                                          │ chỉ ghi vào DB, không gọi module khác
                  ┌───────────────────────────────────────────────────────┴───────────────┐
                  ▼ (cùng nguồn sự thật: Serial.Status / MaterialLot.Status)               ▼
   M3 Kiosk scan → RouteCheck: Status == Hold → chặn            M6 Kiosk nạp reel → Verification: Status == Hold → chặn
```

---

## 3. Kế hoạch thực hiện

Tổng ước lượng: khoảng 33 giờ (**[v2]**: bước 4 nhẹ hơn, bước 6 chuyển thành SPC tự động, bước 8 NCR 4 trạng thái, thêm bước 8b FAI, bước 9 thêm Hold theo pallet/khoảng thời gian). Thiếu thời gian: giữ bước 3–5, 8b, 9 vì là DoD và là cổng của M3; giảm bước 7 (bỏ MR chart) và bước 8 (UI NCR tối thiểu).

- [ ] **Bước 1 — Ôn nghiệp vụ và ERD Quality**: hiểu USL/LSL vs UCL/LCL, rule WE, Cpk, I-MR, vòng đời NCR, FAI, Hold · việc cần làm: tự tính lại tay ví dụ 10 điểm (mục 1.2, **[v2]** cả MR̄ = 0,0544); vẽ ERD (Mermaid trong `docs/`) cho `InspectionPlan`, `ControlLimitSet`, `Measurement` (**[v2]** `Source`, `MachineTagId`), `SpcAlert`, `Ncr`, `NcrItem`, `HoldRecord`, **[v2]** `FaiTemplate`, `FaiRecord` + cột `StatusBeforeHold` của `Serial`/`MaterialLot`; liệt kê enum · file: `docs/quality-erd.md` · kiểm tra: giải thích được cho một người khác vì sao USL ≠ UCL bằng ví dụ 3,44 V · **~2 giờ**.
- [ ] **Bước 2 — Entity, configuration, migration**: lưu được dữ liệu Quality · việc: entity trong Domain, `IEntityTypeConfiguration` (decimal(18,6) cho Value/USL/LSL, datetime2 UTC, bigint cho `Measurement`/`HoldRecord`), index như 2.3 và 2.6, filtered unique index cho `SpcAlert`, migration, seed 1 plan `3V3_RAIL` (LSL 3,135 / USL 3,465) và 1 `ControlLimitSet` (mean 3,302, σ 0,040), **[v2]** plan `REFLOW_PEAK_TEMP` (235–255 °C, mean 245,0, σ 0,8) gắn với `MachineTag` `L1-REFLOW-01/peakTemp` (`SpcSampleEverySec = 30`), `FaiTemplate` cho `PCB-A` · file: `MiniMes.Domain/Quality/*.cs`, `MiniMes.Infrastructure/Persistence/Configurations/Quality/*.cs`, `Migrations/*_AddQuality.cs` · kiểm tra: `dotnet ef database update` chạy sạch; xem index đúng trong SSMS · **~3 giờ**.
- [ ] **Bước 3 — SpcCalculator + unit test**: hàm thống kê đúng · việc: `Compute`, `CalcCapability`, **[v2]** `MovingRange` (mục 2.1), các case: 10 điểm mẫu, một phía, σ = 0, n < 2 · file: `MiniMes.Domain/Quality/Spc/SpcCalculator.cs`, `MiniMes.UnitTests/Quality/SpcCalculatorTests.cs` · kiểm tra: test khớp tay (mean 3,32; σ 0,0337; UCL 3,421; Cp 1,634; Cpk 1,436); thử với Excel (`STDEV.S`) đối chiếu · **~3 giờ**.
- [ ] **Bước 4 — Rule engine + test**: **[v2]** bắt đúng 3 rule mặc định (WE1, WE2, run 7) · việc: `WeRule`, `WesternElectric.Evaluate`, `Default()` + `We3` tùy chọn; test từng rule bằng ví dụ 1.2 (chuỗi 7 điểm cho run rule); test âm (6 điểm không kích hoạt; điểm đúng tâm ngắt chuỗi); một test WE3 khi bật thêm; `RunLength` cấu hình · file: `MiniMes.Domain/Quality/Spc/WesternElectric.cs`, `MiniMes.UnitTests/Quality/WesternElectricTests.cs` · kiểm tra: tất cả pass, có ít nhất 8 test · **~2,5 giờ**.
- [ ] **Bước 5 — Use case ghi số đo**: một request → Measurement + SPC + NG · việc: `RecordInspectionCommand` + validator (FluentValidation), tách `SpcAlert` dedupe, gọi gateway M3; API `POST /quality/inspections`; endpoint `GET /quality/spc/{planId}/chart?take=100` trả điểm + limits + vi phạm; `POST /quality/spc/{planId}/limits/recompute` (QC) · file: `MiniMes.Application/Quality/Inspections/*`, `MiniMes.Api/Controllers/QualityController.cs` · kiểm tra: gửi 3,300 → OK; 3,470 → serial sang NG (M3) và Measurement Result = NG; gửi chuỗi run 7 → có đúng 1 `SpcAlert` (gửi thêm điểm thứ 8, 9 vẫn 1 alert); **[v2]** tách `ISpcEvaluator` dùng chung cho bước 6 · **~3,5 giờ**.
- [ ] **Bước 6 — [v2] Nguồn số đo cho demo: trạm test + SPC tự động từ lò reflow**: · việc: (a) vai trạm test của simulator (M4) gửi `POST /quality/inspections` với giá trị ~ N(3,302; 0,04), tùy chọn `--scenario drift --after 100 --shift 0.02`; (b) `ProcessSpcSampler` (mục 2.3b), cột `Source`/`MachineTagId` trên `Measurement`, unique `(MachineTagId, SourceTs)`, Andon Quality khi vượt spec, cảnh báo "SPC data gap" · file: `MiniMes.MachineSimulator/Scenarios/QualityDrift.cs`, `MiniMes.Infrastructure/Quality/ProcessSpcSampler.cs` · kiểm tra: chạy 200 board có drift → run rule kích hoạt trong ~10–20 board sau điểm dịch; `drift L1-REFLOW-01 0.3` → chart `REFLOW_PEAK_TEMP` có alert run 7 trước khi chạm USL; tắt OPC UA server 2 phút → không có điểm giả, chart hiện "data gap"; test `FakeTimeProvider` cho nhịp lấy mẫu · **~2,5 giờ**.
- [ ] **Bước 7 — Control chart WPF**: nhìn thấy hình dạng · việc: `ControlChartView` + `ControlChartViewModel` (chọn plan, số điểm, làm mới), `ControlChartBuilder` như 2.4, danh sách alert bên phải, hiển thị Cp/Cpk hiện tại; cập nhật khi có SignalR `SpcAlertRaised` (qua Dispatcher) · file: `MiniMes.BackOffice.Wpf/Quality/*`, chuỗi UI trong `Strings.resx` · **[v2]** thêm MR chart dưới I chart · kiểm tra: kịch bản drift hiện chấm đỏ tại điểm 7 của chuỗi; USL/LSL khác màu UCL/LCL; MR chart có `MR̄` và `UCL_MR` · **~3 giờ**.
- [ ] **Bước 8 — NCR**: workflow **[v2]** 4 trạng thái · việc: aggregate `Ncr` (2.5) + unit test mọi chuyển trạng thái và mọi vi phạm (tự duyệt, RTV cho serial, Approve khi chưa có đề xuất, đóng khi còn Hold); commands (Create, StartReview, Propose, Approve, RejectProposal, Close, Cancel); tạo NCR từ `SpcAlert` (nút "Create NCR"); UI danh sách + chi tiết, nút theo trạng thái/role · file: `MiniMes.Domain/Quality/Ncr.cs`, `MiniMes.Application/Quality/Ncrs/*`, `MiniMes.BackOffice.Wpf/Quality/NcrView*.xaml` · kiểm tra: unit test pass; thao tác tay đi đủ Open → UnderReview → Dispositioned → Closed với hai user khác nhau · **~3 giờ**.
- [ ] **Bước 8b — [v2] FAI**: QC duyệt sản phẩm đầu tiên, mở cổng M3 · việc: `FaiTemplate`, `FaiRecord`, `GetFaiWorksheet` (3 kiểm tự động, mục 2.5b), `RecordFaiResultCommand` (Q17, gọi `wo.RecordFaiResult`, Fail → NG serial mẫu qua cổng M3, xóa cache WO, push `FaiStatusChanged`); xóa endpoint tạm của M3; màn hình FAI trong BackOffice · file: `MiniMes.Domain/Quality/Fai/*`, `MiniMes.Application/Quality/Fai/*`, `MiniMes.BackOffice.Wpf/Quality/FaiView.xaml` · kiểm tra: integration test: release WO → serial mẫu qua hết Test → phiếu tự động xanh → Pass → serial thứ 2 scan SMT được; Fail → serial mẫu ở `Ng`, WO vẫn `Pending`, serial thứ 2 thành mẫu mới; cố tình nạp lot sai item cho serial mẫu (bỏ qua verification trong test) → `BomMatch` đỏ, nút Pass tắt · **~3 giờ**.
- [ ] **Bước 9 — Hold/Release xuyên module**: chặn thật sự · việc: `HoldTargetsCommand` (theo lot dùng truy xuất xuôi M6 / theo danh sách serial / **[v2]** theo thùng, pallet / theo khoảng thời gian qua máy, mục 2.6), `ReleaseHoldCommand`, SQL như 2.6, audit, nút "Hold tất cả" trên màn hình truy xuất M6 mở hộp thoại (lý do, preview số lượng, tùy chọn tạo NCR), kiểm tra route check M3 và verification M6 thật sự đọc `Status = Hold`; **[v2]** nút "Hold serials in window" trên chart SPC của máy (chọn khoảng thời gian quanh alert, preview số serial) · file: `MiniMes.Application/Quality/Holds/*`, hộp thoại trong BackOffice · kiểm tra: xem DoD ở 5.1; **[v2]** Hold pallet → mọi serial trong pallet Hold, Ship bị chặn (M3b) · **~4,5 giờ**.
- [ ] **Bước 10 — Integration test DoD + demo**: chứng minh cả chuỗi · việc: Testcontainers (SQL Server): seed lot + 3 serial đã dùng lot; gọi Hold theo lot; scan serial ở trạm tiếp → bị chặn; thử nạp lot Hold → bị chặn; Release → scan lại được và trạng thái khôi phục đúng; test đua Hold/scan; cập nhật kịch bản demo bước 8 trong README · file: `MiniMes.IntegrationTests/Quality/HoldFlowTests.cs` · kiểm tra: CI xanh · **~3 giờ**.

### Nếu bị kẹt

- **Số liệu không khớp tay**: kiểm tra chia `n−1` hay `n`; kiểm tra bạn có tính trên đúng danh sách (không lẫn điểm cũ/mới); in `Mean`, `Sigma` ra rồi đối chiếu Excel `AVERAGE`, `STDEV.S`.
- **Rule không kích hoạt**: in ra mảng z-score; kiểm tra điểm có thật sự "> k" chứ không phải "≥"; kiểm tra dữ liệu đã sắp theo `MeasuredAt`. Với run rule, đếm đúng 7 điểm liên tiếp cùng phía (điểm z = 0 ngắt chuỗi).
- **[v2] SPC tự động báo giả liên tục**: lấy mẫu quá dày (điểm tự tương quan), hoặc baseline σ tính từ đoạn quá ngắn. Tăng `SpcSampleEverySec`, tính lại baseline từ ít nhất 1 giờ ổn định.
- **[v2] FAI Pass rồi mà kiosk vẫn chặn**: cache WO ở M3 chưa xóa; SignalR `FaiStatusChanged` chưa tới kiosk (chỉ ảnh hưởng banner, không ảnh hưởng route check).
- **Alert bị nhân đôi**: kiểm tra filtered unique index đã tạo chưa (`sys.indexes` có `filter_definition`); kiểm tra có `catch (DbUpdateException)` khi đua.
- **NG oan ở biên**: bạn đang so `double` với `decimal`? So sánh spec bằng `decimal` (Q2), kiểm tra làm tròn theo độ phân giải dụng cụ.
- **Chart không cập nhật**: gán `PlotModel` mới cho property có `[ObservableProperty]` hoặc gọi `InvalidatePlot(true)`; kiểm tra bạn cập nhật trên UI thread.
- **Hold xong scan vẫn cho qua**: xem route check M3 có đọc `Status` mới nhất không (cache?), xem transaction đã commit chưa, xem kiosk có đang gọi endpoint khác không đi qua route check (đường tắt của simulator). Chạy `SELECT Status, StatusBeforeHold FROM Serial WHERE SerialNo = @s`.
- **Release xong serial về trạng thái sai**: `StatusBeforeHold` có được ghi không (dòng `CASE` trong `UPDATE`); hoặc còn Hold active khác (Q8) — đúng hành vi.
- **Deadlock/chậm khi Hold hàng nghìn serial**: kiểm tra chỉ mục `SerialComponent(LotNo)` (M6) và `HoldRecord(BatchId)`; xem execution plan (M3).

---

## 4. Vì sao chọn giải pháp này

| Quyết định | Đã chọn | Phương án khác đã cân nhắc | Vì sao không chọn | Khi nào nên đổi |
| --- | --- | --- | --- | --- |
| Loại control chart | Individuals (I) — mỗi lần đo một giá trị | Xbar-R / Xbar-S (nhóm con n = 4–5) | Dữ liệu ở đây là số đo theo serial, không có nhóm con tự nhiên; nhóm con nhân tạo làm phức tạp hóa mô hình `Measurement` | Khi lấy mẫu theo nhóm (ví dụ 5 board mỗi 30 phút) hoặc cần chart nhạy hơn với dịch chuyển nhỏ |
| Ước lượng σ | Sample σ (n−1) trên tập baseline | `MR-bar / 1,128` (moving range) — chuẩn cho I-MR chart | Sample σ đơn giản hơn để cài và test; MR-bar cần thêm bảng hằng số | Khi dữ liệu có drift trong baseline (σ mẫu bị thổi phồng) hoặc khi khách yêu cầu chuẩn AIAG. Ví dụ 10 điểm ở 1.2: σ mẫu = 0,0337 nhưng `MR-bar/1,128` = 0,0483 → Cpk 1,44 vs 1,00 — chênh lớn vì dữ liệu nhảy qua lại; 10 điểm quá ít để kết luận |
| Cp/Cpk vs Pp/Ppk | Một cặp Cp/Cpk tính bằng σ tính toán từ baseline | Tách σ within (Cp/Cpk) và σ overall (Pp/Ppk) | Sự khác biệt chỉ đáng khi có nhiều nhóm con và biến động giữa nhóm; MVP chưa có | Khi khách (ô tô) yêu cầu báo cáo cả Ppk cho PPAP |
| Giới hạn kiểm soát | Baseline cố định, tính lại khi người có quyền yêu cầu | Rolling: tính lại từ N điểm gần nhất | Rolling giới hạn tự trôi theo quy trình; không bắt được dịch chuyển chậm (WE4 vô dụng) | Khi quy trình có trend tự nhiên hợp lệ (mòn công cụ có lịch thay) — dùng chart khác |
| Bộ rule | **[v2]** 3 rule (WE1, WE2, run 7), WE3 có sẵn nhưng tắt; `RunLength` cấu hình | 4 rule WE; đầy đủ 8 rule Nelson | Nhiều rule → nhiều báo động giả → QC bỏ qua; ba rule bắt được ba kiểu thay đổi chính | Khi đã đo được tỷ lệ báo giả thấp và cần bắt thêm xu hướng, đan xen, phân tầng |
| **[v2]** Nguồn số đo SPC | Trạm kiểm tra (theo serial) + lấy mẫu định kỳ từ tag máy | Đưa mọi giá trị tag vào chart | Giá trị gửi theo deadband dày và tự tương quan → báo giả; mẫu cách quãng gần độc lập hơn | Cần phát hiện thay đổi trong vài giây: dùng EWMA/CUSUM trên dữ liệu dày thay cho Shewhart |
| **[v2]** FAI | Cổng trên WO (M2) + kiểm tra trong route check (M3) + phiếu có kiểm tự động (M7) | Phiếu FAI giấy/Excel; FAI chỉ là báo cáo không chặn | Không chặn thì line chạy hàng loạt trước khi QC kiểm; phiếu giấy phải chép tay dữ liệu MES đã có | FAI theo ca/đổi model/sau sửa máy: thêm bảng yêu cầu FAI theo sự kiện |
| Đánh giá SPC | Đồng bộ trong transaction ghi số đo (chỉ xét cửa sổ gần nhất) | Job nền chạy mỗi vài phút | Đồng bộ cho alert ngay và nhất quán với dữ liệu; chi phí nhỏ vì cửa sổ chỉ ~10 điểm | Khi tần suất đo rất cao (hàng nghìn điểm/giây) hoặc rule tính nặng — chuyển sang worker theo lô |
| Nơi tính NG theo spec | Server (một request đo → NG/OK → luồng scan M3) | Client (kiosk tự quyết) hoặc hai API tách biệt | Client tự quyết dễ sai, lệch phiên bản; hai API tách biệt có thể mâu thuẫn | Nếu thiết bị test tự quyết pass/fail và chỉ gửi số đo tham khảo → nhận kết quả từ thiết bị, server vẫn kiểm tra chéo |
| Thư viện chart | OxyPlot (thống nhất với M5) | LiveCharts2 | OxyPlot có `LineAnnotation` sẵn cho đường giới hạn, API ổn định; LiveCharts2 phải vẽ đường ngang bằng series hằng số, API đổi nhiều giữa các bản | Khi cần hiệu ứng động đẹp cho dashboard demo |
| Cơ chế Hold | `Status = Hold` + `HoldRecord` (nhiều dòng, active = ReleasedAt null) + `StatusBeforeHold` | Chỉ cờ `IsHold`; hoặc chỉ bảng Hold mà không đổi Status | Cờ không nhớ trạng thái cũ và không đếm được Hold chồng; bảng Hold độc lập buộc mọi điểm thực thi phải join thêm và dễ sót | Khi cần Hold theo nhiều kiểu (Hold một phần số lượng, Hold theo công đoạn) → thêm bảng Hold có số lượng |
| Chặn xuyên module | Điểm thực thi đọc trực tiếp `Status` (cùng DB, modular monolith) | Domain event/message tới các module rồi mỗi module tự cập nhật trạng thái cục bộ | Trong một DB, đọc trực tiếp đơn giản, nhất quán tức thì, không có cửa sổ không đồng bộ; event thêm độ trễ và khả năng lọt | Khi tách service/DB riêng: dùng event + outbox (M8), chấp nhận nhất quán cuối cùng và thiết kế bù trừ |
| Cập nhật hàng loạt | SQL set-based trong transaction | Tải entity, gọi method domain, `SaveChanges` | Hàng chục nghìn serial thì chậm và tốn RAM; nhưng mất audit interceptor (phải tự ghi) | Với số lượng nhỏ (< vài trăm) và cần đầy đủ domain event → dùng entity |
| NCR | **[v2]** 4 trạng thái (đề xuất là dữ liệu, không là trạng thái), disposition ở mức NCR | 6 trạng thái có PendingApproval/Approved; workflow engine; disposition từng item | Ít trạng thái, ít màn hình mà vẫn giữ phân tách người đề xuất/người duyệt; tách NCR con khi MRB chia nhóm | Khi cần nhiều bước duyệt, ngưỡng theo giá trị tiền, SLA, thông báo → workflow engine hoặc chuyển quy trình ra hệ thống QMS chuyên dụng |
| Lưu số đo | SQL Server, bảng `Measurement` insert-only bigint | Time-series DB (InfluxDB/TimescaleDB) | Đã chốt một DB duy nhất (spec mục 0); khối lượng số đo thấp hơn nhiều so với `MachineEvent` | Khi số đo từ cảm biến tần số cao (kHz); khi đó vẫn lưu tổng hợp vào SQL Server |
| Phê duyệt | QC đề xuất, Supervisor duyệt (2 người) | MRB nhiều thành viên ký | MVP chỉ cần phân tách nhiệm vụ; nhiều chữ ký thêm bảng và luồng | Khi khách/audit yêu cầu biên bản MRB có nhiều chữ ký |

**Ba quyết định đáng nói lại được trong phỏng vấn.**

*Vì sao control limit là baseline cố định.* UCL/LCL mô tả cách quy trình cư xử khi nó ổn định. Nếu bạn tính lại giới hạn từ mọi điểm gần nhất thì khi quy trình bắt đầu trôi, giới hạn trôi theo; điểm trôi luôn nằm "trong giới hạn" và không có gì báo động. Cố định baseline nghĩa là chart trả lời câu hỏi "quy trình hôm nay có còn giống quy trình hôm đó khi ta xác nhận ổn định không?". Tính lại baseline là quyết định của con người khi có thay đổi thật (đổi máy, đổi vật tư) và phải có lý do, người, thời điểm.

*Vì sao Hold có nguồn sự thật duy nhất và nhiều điểm thực thi.* Hold là tấm thẻ đỏ điện tử. Thẻ đỏ có tác dụng nếu mọi cửa đều kiểm tra nó. Đặt trạng thái ở `Serial.Status`/`MaterialLot.Status` (kèm `HoldRecord` cho lịch sử) khiến các cửa hiện có — route check ở M3, verification ở M6 — chỉ cần đọc trạng thái, không phải biết M7 tồn tại. Chi phí là M7 ghi vào bảng của module khác; trong modular monolith cùng một DB, điều đó chấp nhận được và cho nhất quán tức thì. Nếu tách thành microservice thì đánh đổi ngược lại: dùng sự kiện, chấp nhận cửa sổ trễ. Kèm theo là `StatusBeforeHold` để release khôi phục đúng, và đếm Hold active để hai NCR cùng nghi một serial không thả nhầm.

*Vì sao SPC alert không tự Hold và không tự làm NG.* SPC nói quy trình có tín hiệu bất thường; không nói con nào hỏng. Tự động Hold khi báo giả làm dừng line, tốn tiền, và sau vài lần QC sẽ tắt tính năng. Quyết định Hold do người (QC) đưa ra, có hỗ trợ của dữ liệu (chart + truy xuất M6 cho biết phạm vi). Ngược lại, giá trị ngoài spec là NG tuyệt đối (Q1), nên hệ thống tự động làm NG mà không chờ người.

---

## 5. Hoàn thành module

### 5.1 Definition of Done

Theo spec: **reel lỗi → truy xuất → Hold → scan serial bị hold ở trạm bị chặn.** Cụ thể:

- [ ] Có inspection plan cho `3V3_RAIL` (USL 3,465 / LSL 3,135) và `ControlLimitSet` baseline (mean 3,302, σ 0,040) trong seed.
- [ ] `SpcCalculator` có unit test khớp số tính tay (mean 3,32; σ 0,0337; UCL 3,421; LCL 3,219; Cp 1,634; Cpk 1,436), có test cho USL/LSL null và σ = 0.
- [ ] Rule engine có test cho WE1, WE2, run 7 (ví dụ mục 1.2), test âm (6 điểm), và một test WE3 khi bật thêm.
- [ ] **[v2]** `MovingRange` khớp tay (MR̄ 0,0544; UCL_MR 0,178); control chart có MR chart.
- [ ] Gửi số đo 3,470 → `Measurement.Result = NG` và serial vào luồng NG của M3; gửi 3,440 → OK nhưng có tín hiệu vượt UCL (WE1) — chứng minh hai cặp giới hạn độc lập.
- [ ] Chuỗi 7 điểm kịch bản drift tạo đúng 1 `SpcAlert` run rule; điểm thứ 8, 9 không tạo alert trùng.
- [ ] **[v2]** SPC tự động: `drift` nhiệt độ lò → alert trước khi chạm USL; vượt USL → Andon Quality; mất dữ liệu → "data gap", không có điểm giả; không tạo điểm trùng khi job chạy lại.
- [ ] **[v2]** FAI: WO `RequireFai` chỉ chạy hàng loạt sau khi QC Pass; Pass bị khóa khi kiểm tự động đỏ; Fail chọn mẫu mới (integration test xuyên M2–M3–M6–M7).
- [ ] Control chart hiển thị điểm, CL, UCL/LCL, USL/LSL (khác kiểu) và điểm vi phạm đánh dấu đỏ.
- [ ] NCR chạy đủ vòng đời **[v2]** Open → UnderReview → Dispositioned → Closed; không tự duyệt được; RTV bị từ chối với serial; không đóng được khi còn Hold active.
- [ ] **[v2]** Hold theo pallet/thùng và theo khoảng thời gian qua máy hoạt động; pallet chứa serial Hold không Ship được (M3b).
- [ ] Từ màn hình truy xuất M6, "Hold tất cả" theo lot: `HoldRecord` cho mọi serial + lot, `Status = Hold`, `StatusBeforeHold` được ghi.
- [ ] Scan serial bị Hold ở trạm tiếp theo (kể cả Pack) → bị chặn với thông báo rõ, kiosk nền đỏ. Nạp reel bị Hold lên máy → bị chặn.
- [ ] Release khôi phục đúng trạng thái trước Hold (kể cả `Completed`); Hold chồng (2 NCR) chỉ khôi phục khi hết cả hai.
- [ ] Integration test (Testcontainers) chứng minh chuỗi Hold → chặn → Release; có test đua Hold vs scan.
- [ ] Mọi thay đổi trạng thái hàng loạt có bản ghi audit; chuỗi UI trong `.resx`; README cập nhật kịch bản demo spec v2 bước 5, 10, 12.

### 5.2 Tự kiểm tra

**Câu 1.** USL/LSL và UCL/LCL khác nhau ở nguồn gốc như thế nào? Cho ví dụ một giá trị đạt spec nhưng vượt UCL.
<details><summary>Đáp án gợi ý</summary>USL/LSL do con người/khách định (bản vẽ) và áp dụng cho từng sản phẩm; UCL/LCL tính từ dữ liệu (mean ± 3σ) và mô tả hành vi quy trình. Ví dụ: rail 3,3 V ± 5% (3,135–3,465), baseline mean 3,302, σ 0,04 → UCL 3,422. Giá trị 3,440: nằm trong spec (sản phẩm đạt) nhưng vượt UCL (z = 3,45) nên SPC báo động.</details>

**Câu 2.** Quy trình có σ = 0,07 V, mean 3,302 V, spec 3,135–3,465. Chart có báo "ngoài kiểm soát" không? Sản phẩm có ổn không?
<details><summary>Đáp án gợi ý</summary>Nếu quy trình ổn định thì chart không có tín hiệu (UCL = 3,512, LCL = 3,092 — rộng hơn spec). Nhưng Cpk = min(3,465−3,302 ; 3,302−3,135)/(3×0,07) = min(0,163 ; 0,167)/0,21 = 0,776 — dưới 1, khoảng 1,85% board ngoài spec. Chart chỉ nói ổn định, Cpk mới nói đủ năng lực.</details>

**Câu 3.** Tính σ mẫu, Cp, Cpk cho `3,32 3,36 3,28 3,33 3,31 3,37 3,27 3,32 3,35 3,29` với LSL 3,135, USL 3,465.
<details><summary>Đáp án gợi ý</summary>mean 3,320; tổng bình phương lệch 0,0102; σ² = 0,0102/9 = 0,001133; σ = 0,03367. Cp = 0,33/(6×0,03367) = 1,634. Cpu = 0,145/0,1010 = 1,436; Cpl = 0,185/0,1010 = 1,832; Cpk = 1,436.</details>

**Câu 4.** Vì sao các rule WE có thể viết thành một hàm? Bộ tham số (N, M, k) là gì?
<details><summary>Đáp án gợi ý</summary>Tất cả đều là "N trong M điểm liên tiếp vượt k×σ cùng phía". WE1 (1,1,3), WE2 (2,3,2), WE3 (4,5,1, tùy chọn), run rule (7,7,0) theo cấu hình [v2]. Chuẩn hóa về z-score làm rule độc lập đơn vị.</details>

**Câu 5.** Vì sao 7 điểm liên tiếp cùng phía đường tâm là tín hiệu dù mọi điểm trong spec và trong 1σ?
<details><summary>Đáp án gợi ý</summary>Nếu quy trình đứng yên, mỗi điểm có ~50% cùng phía; 7 liên tiếp cùng phía có xác suất ≈ 0,5⁷ ≈ 0,8% mỗi phía (~1,6% cho cả hai phía) — đủ hiếm để coi là tín hiệu (8 điểm thì ~0,8%, ít báo giả hơn nhưng chậm hơn một điểm). Nó cho thấy mean dịch chuyển nhỏ, phát hiện sớm trước khi ra NG.</details>

**Câu 6.** Vì sao UCL/LCL không được tính lại sau mỗi điểm mới?
<details><summary>Đáp án gợi ý</summary>Giới hạn sẽ trôi theo dữ liệu đang trôi và mọi điểm mới luôn "trong giới hạn". Baseline (Phase I) cố định, Phase II giám sát so với baseline; chỉ tính lại có kiểm soát khi quy trình thật sự thay đổi.</details>

**Câu 7.** Cp = 1,5 cho cả hai line, nhưng Line B có 3,8% board ngoài spec. Giải thích.
<details><summary>Đáp án gợi ý</summary>Cp không tính vị trí mean. Line B mean lệch tâm 3,400 (gần USL) nên Cpk = (3,465−3,400)/(3×0,0367) = 0,59. Cp là tiềm năng nếu căn tâm; Cpk là thực tế.</details>

**Câu 8.** Vì sao NCR không thể do cùng một người vừa đề xuất disposition vừa duyệt? Ai làm gì trong MVP?
<details><summary>Đáp án gợi ý</summary>Phân tách nhiệm vụ để tránh lạm dụng (đặc biệt Use-as-is) và cho audit. MVP: QC đề xuất, Supervisor duyệt, kiểm tra bằng UserId ở domain; MRB đầy đủ là mở rộng.</details>

**Câu 9.** Vì sao Hold phải chặn ở cả M3 và M6, và điều gì xảy ra nếu chỉ chặn ở M3?
<details><summary>Đáp án gợi ý</summary>Serial đã dùng lot xấu (M3 chặn) nhưng lot vẫn được nạp lên máy (M6) thì sản xuất tiếp serial mới nhiễm lỗi. Nguồn sự thật `Status`, nhiều điểm thực thi đọc nó.</details>

**Câu 10.** Vì sao cần `StatusBeforeHold` và đếm Hold active khi Release?
<details><summary>Đáp án gợi ý</summary>Để khôi phục đúng trạng thái (serial Completed không được thành Created), và để Hold chồng từ hai NCR không bị thả khi chỉ một NCR được release. Serial chỉ về trạng thái cũ khi không còn `HoldRecord` active.</details>

**Câu 11 (thêm).** Vì sao dùng `ExecuteSqlInterpolatedAsync` để Hold hàng loạt, và cái giá phải trả?
<details><summary>Đáp án gợi ý</summary>Set-based nhanh, không load hàng nghìn entity. Giá phải trả: ChangeTracker/audit interceptor (M0) không thấy thay đổi nên phải tự ghi audit; phải cẩn thận mapping enum/chuỗi trong SQL thô.</details>

**Câu 12 [v2].** Vì sao lấy mẫu nhiệt độ lò mỗi 30 giây thay vì đưa mọi giá trị vào control chart?
<details><summary>Đáp án gợi ý</summary>Giá trị liên tiếp vài giây một lần gần như bằng nhau (tự tương quan), vi phạm giả thiết điểm độc lập; run rule sẽ báo liên tục. Lấy mẫu cách quãng cho chuỗi điểm gần độc lập. Muốn phát hiện rất nhanh trên dữ liệu dày thì dùng EWMA/CUSUM.</details>

**Câu 13 [v2].** Phiếu FAI có phần tự động nào và lấy từ đâu?
<details><summary>Đáp án gợi ý</summary>BOM khớp (truy xuất ngược M6 so với `WorkOrderMaterial` M2), Test OK (`ScanLog` M3), số đo trong spec (`Measurement` M7). QC chỉ kiểm phần mắt thường; Pass bị khóa khi phần tự động đỏ.</details>

### 5.3 Câu hỏi phỏng vấn liên quan + gợi ý trả lời

1. **"Giải thích khác biệt giữa spec limit và control limit."** Spec limit là yêu cầu của khách cho từng sản phẩm, control limit tính từ dữ liệu quy trình (mean ± 3σ). Ví dụ 3,3 V ± 5% và σ 0,04: một giá trị 3,44 V đạt spec nhưng vượt UCL nên SPC báo động; ngược lại, quy trình σ lớn có thể in-control mà vẫn tạo hàng ngoài spec — Cpk trả lời phần đó.
2. **"Cpk 1,33 nghĩa là gì? Khi nào con số này không đáng tin?"** Khoảng cách từ mean tới giới hạn gần nhất bằng 4σ; thường là ngưỡng tối thiểu (khách quy định). Không đáng tin khi quy trình chưa ổn định, dữ liệu không chuẩn, ít mẫu, hoặc hệ thống đo kém (Gauge R&R).
3. **"Làm sao phát hiện quy trình trôi trước khi ra hàng NG?"** SPC với 3 rule (1 điểm ngoài 3σ, 2/3 ngoài 2σ, 7 điểm cùng phía); ví dụ 7 điểm cùng phía đường tâm dù mọi điểm trong spec. **[v2]** Kể ví dụ nhiệt độ lò reflow đọc qua OPC UA, lấy mẫu 30 giây, báo trước khi chạm USL, rồi Hold serial đã qua lò trong khoảng đó. Kèm điểm đáng nói: baseline cố định, dedupe alert, kiểm soát báo động giả.
4. **"Thiết kế Hold trong MES như thế nào để không lọt hàng?"** Nguồn sự thật duy nhất (`Status` + `HoldRecord`), các cửa (route check, nạp lot, hoàn thành WO) đều kiểm tra; hold trước hỏi sau; transaction để hold cả lot lẫn serial; release phải có quyền/lý do và khôi phục đúng trạng thái; test tích hợp xuyên module.
5. **"Khách báo lỗi một serial, hai giờ đầu bạn làm gì?"** Truy xuất ngược (M6) tìm lot/máy/ca; truy xuất xuôi tìm serial cùng lot; Hold ngay (containment); mở NCR; đối chiếu control chart vùng thời gian đó; báo QC/supervisor; chuẩn bị dữ liệu cho 8D. (Liên hệ M6.)
6. **"NCR khác gì Andon/ticket thường?"** NCR là hồ sơ chất lượng có vòng đời, disposition, người duyệt và liên kết serial/lot cụ thể; dùng làm chứng cứ audit. Andon là cảnh báo tức thời tại xưởng.
7. **"Tại sao không dùng Hold tự động khi SPC báo động?"** Báo giả làm dừng line; SPC nói quy trình bất thường, không nói con nào hỏng. Người quyết định Hold có dữ liệu truy xuất hỗ trợ.
8. **"Bạn đảm bảo hai người đo cùng lúc không tạo alert trùng thế nào?"** Filtered unique index (một alert mở cho mỗi plan-rule), bắt lỗi trùng khóa và bỏ qua; không dựa vào kiểm tra trước.
9. **[v2] "FAI là gì, MES hỗ trợ thế nào?"** Kiểm tra sản phẩm đầu tiên sau khi setup; MES chặn line (route check) tới khi QC Pass, phiếu FAI có kiểm tự động từ dữ liệu truy xuất, test, số đo; Fail thì chọn mẫu mới. Bắt lỗi setup ở con số 1 thay vì con số 200.
10. **[v2] "I-MR chart khác X̄-R chart thế nào, khi nào dùng cái nào?"** I-MR cho dữ liệu từng giá trị (đo 100% theo serial, mẫu định kỳ từ máy); X̄-R cho nhóm con lấy cùng lúc (5 mẫu mỗi giờ), nhạy hơn với dịch chuyển nhỏ của mean nhưng cần kế hoạch lấy mẫu theo nhóm.

### 5.4 Tài liệu đọc thêm

- **[v2]** AIAG PPAP manual / AS9102 (FAI, chỉ cần đọc phần giới thiệu); NIST e-Handbook mục "Individuals control charts" và "EWMA/CUSUM".
- NIST/SEMATECH e-Handbook of Statistical Methods (itl.nist.gov/div898/handbook): chương "Process or Product Monitoring and Control" — control chart, process capability, giải thích đầy đủ và miễn phí.
- Western Electric, *Statistical Quality Control Handbook* (1956): nguồn gốc các rule WE. Nelson (1984) là nguồn của bộ rule Nelson.
- Montgomery, *Introduction to Statistical Quality Control*: giáo trình chuẩn về control chart, Cp/Cpk, sampling.
- Wheeler & Chambers, *Understanding Statistical Process Control*: cách đọc chart và tránh tampering.
- AIAG SPC Reference Manual và MSA Reference Manual (ngành ô tô): chuẩn tính Cpk/Ppk, Gauge R&R.
- ISO 2859-1 (lấy mẫu chấp nhận theo AQL); ISO 9001 điều khoản kiểm soát đầu ra không phù hợp (mục 8.7 bản 2015) — nền tảng của NCR/disposition.
- Microsoft Learn: tài liệu EF Core về `ExecuteUpdate`/`ExecuteDelete` và SQL thô (`ExecuteSql`, `ExecuteSqlInterpolated`) ở learn.microsoft.com/ef/core — kiểm tra trang hiện hành cho đúng phiên bản EF Core của bạn.
- OxyPlot: github.com/oxyplot/oxyplot (README và tài liệu); LiveCharts2: github.com/beto-rodriguez/LiveCharts2 — kiểm tra tên lớp/property theo phiên bản gói bạn cài.
- Microsoft Learn: tài liệu xUnit `Assert.Equal` overload có `precision` cho số thực (xunit.net).
