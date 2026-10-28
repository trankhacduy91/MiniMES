# OEE — tính tay và bảng test

> M5 bước 1 ([spec module-05](../spec/module-05-oee-downtime.md) mục 1.2, 3). Bốn ca dưới đây là "đáp án" mà
> `OeeCalculator` (bước 2) và integration test (bước 4) phải khớp. Số đã kiểm lại bằng script (sai số < 0,0001).

Công thức (spec 1.2.1):

```
Planned = Ca − (Nghỉ ∪ PlannedStop)          ← phần HỢP, không trừ hai lần
Run     = Planned − Unplanned downtime        (đoạn Down < 60 s là micro stop, không trừ)
A = Run / Planned     P = Ideal / Run     Q = Good / Total     OEE = A × P × Q
Ideal   = Σ (cycle time × count) theo từng WO
Rút gọn: OEE = Ideal × Q / Planned            (một WO: = cycle time × Good / Planned)
```

## Tổng hợp

| Ca | Planned | Run | Total | Good | Ideal | A | P | Q | OEE | Rút gọn |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Mẫu `L1-SMT-01` S1 | 405 ph | 360 ph | 1.000 | 970 | 20.000 s | 0,8889 | 0,9259 | 0,9700 | **0,7984** | 19.400 / 24.300 = 0,7984 ✓ |
| (a) P > 100 % | 405 | 360 | 1.440 | 1.420 | 28.800 s | 0,8889 | **1,3333** | 0,9861 | **1,1687** | 28.400 / 24.300 = 1,1687 ✓ |
| (b) Họp trùng giờ nghỉ | 410 | 380 | 1.080 | 1.065 | 21.600 s | 0,9268 | 0,9474 | 0,9861 | **0,8659** | 21.300 / 24.600 = 0,8659 ✓ |
| (c) Hai WO | 420 | 390 | 800 | 785 | 18.000 s | 0,9286 | 0,7692 | 0,9813 | **0,7009** | 18.000 × 0,98125 / 25.200 = 0,7009 ✓ |

---

## Ca mẫu — `L1-SMT-01`, S1 06:00–14:00, 01/10/2026 (spec 1.2.2)

| Dữ kiện | Giá trị |
| --- | --- |
| Ca | 480 phút |
| Nghỉ | 08:00–08:15 + 10:00–10:45 = 60 |
| PlannedStop | 06:00–06:15 `PLN-MEETING` = 15 (không chồng giờ nghỉ) |
| Unplanned | `BRK-FEEDER` 25 + `MAT-WAIT` 12 + `QC-WAIT` 8 = 45 |
| Micro stop | 6 × 20 s = 2 phút < ngưỡng 60 s → nằm trong Run |
| Cycle time / Total / Good | 20 s / 1.000 / 970 |

```
480,00  Thời gian ca
−60,00 nghỉ, −15,00 PlannedStop
405,00  Planned (100 %)
−45,00  dừng ngoài kế hoạch          → mất A
360,00  Run (A 88,89 %)
−26,67  chạy chậm + micro stop       → mất P   (360 − 1.000 × 20 s / 60)
333,33  thời gian lý tưởng cho 1.000 bo
−10,00  30 bo NG × 20 s              → mất Q
323,33  thời gian có giá trị = 79,84 % của 405
```

Bằng lời: trong 405 phút đáng lẽ dùng máy, mất 45 phút vì dừng, 26,67 phút vì chạy chậm hơn chuẩn (trong đó 2 phút là
micro stop), 10 phút làm ra bo NG; còn 323,33 phút tạo ra bo đạt.

---

## (a) Performance > 100 % — cycle time chuẩn nhập sai

Giống ca mẫu, nhưng máy thật chạy **15 s/bo** trong khi master/snapshot WO ghi **20 s**. Máy đếm 1.440, Good 1.420.

```
Ideal = 1.440 × 20 = 28.800 s = 480 phút   >   Run 360 phút
P = 28.800 / 21.600 = 1,3333      Q = 1.420 / 1.440 = 0,9861
OEE = 0,8889 × 1,3333 × 0,9861 = 1,1687      ← OEE 116,9 %: không thể có
```

Thác nước "đi ngược": bước P **cộng** 120 phút (480 > 360), tức máy làm ra nhiều hơn mức nhanh nhất có thể. Kết luận:
không phải máy giỏi mà master data sai (spec 1.2.7). Hệ thống giữ P = 1,3333 thô và gắn cờ `PerformanceOver100`.

Nếu sửa cycle time về đúng 15 s: Ideal = 21.600 s = Run → P = 1,0000, OEE = 0,8889 × 1 × 0,9861 = **0,8765**.
Cắt P về 100 % mà không sửa master thì cũng ra 0,8765 trên báo cáo, nhưng không ai biết phải sửa cycle time → đó là
lý do không cắt im lặng.

---

## (b) PlannedStop trùng giờ nghỉ — trừ phần hợp

Ca S1 480 phút. Nghỉ 08:00–08:15 và 10:00–10:45 (60 phút). Họp 5S `PLN-MEETING` **07:55–08:20** (25 phút) chồng lên
giờ giải lao 08:00–08:15. Dừng `BRK-FEEDER` 07:10–07:40 (30 phút). Máy báo `Down` 10:10–10:30 **trong giờ ăn** (kỹ
thuật viên tắt máy để vệ sinh). Cycle time 20 s, Total 1.080, Good 1.065.

```
Nghỉ ∪ PlannedStop = [07:55–08:20] (25) + [10:00–10:45] (45) = 70 phút
Planned = 480 − 70 = 410          Unplanned = 30 (Down 10:10–10:30 nằm trong giờ nghỉ → không tính)
Run = 380     Ideal = 1.080 × 20 = 21.600 s = 360 phút
A = 380/410 = 0,9268   P = 360/380 = 0,9474   Q = 1.065/1.080 = 0,9861   OEE = 0,8659
```

Hai cách tính sai và hậu quả:

| Sai lầm | Planned | Run | A | P | OEE |
| --- | --- | --- | --- | --- | --- |
| Đúng (phần hợp) | 410 | 380 | 0,9268 | 0,9474 | **0,8659** |
| Trừ hai lần (480 − 60 − 25) | 395 | 365 | 0,9241 | 0,9863 | 0,8987 (đẹp ảo +3,3 điểm) |
| Tính Down trong giờ ăn là downtime | 410 | 360 | 0,8780 | 1,0000 | 0,8659 |

Dòng 3 đáng nhớ: OEE **không đổi** (rút gọn chỉ phụ thuộc Ideal, Q, Planned), nhưng mất mát bị chuyển từ P sang A:
A tụt oan 4,9 điểm, P lên tròn 100 %. Báo cáo sẽ nói "máy dừng nhiều" trong khi thực tế máy chạy chậm. Vì vậy phải
test cả A, P chứ không chỉ test OEE.

---

## (c) Hai WO trong ca, cycle time khác nhau

Ca S2 14:00–22:00, nghỉ 60 phút. Đổi model `SET-CHANGEOVER` 18:50–19:10 (20 phút; Category = Planned nhưng
`CountsAgainstAvailability = true` → vẫn phạt A), `BRK-NOZZLE` 10 phút → Unplanned 30.

| WO | Sản phẩm | Cycle time | Máy đếm | Good |
| --- | --- | --- | --- | --- |
| WO-1 | `PCB-A` | 20 s | 600 | 590 |
| WO-2 | `PCB-B` | 30 s | 200 | 195 |

```
Planned = 480 − 60 = 420    Run = 420 − 30 = 390 = 23.400 s
Ideal = 600 × 20 + 200 × 30 = 12.000 + 6.000 = 18.000 s = 300 phút
A = 390/420 = 0,9286   P = 18.000/23.400 = 0,7692   Q = 785/800 = 0,98125   OEE = 0,7009
```

Thác nước: 420 → −30 (A) → 390 → −90 (P) → 300 → −5,63 (Q) → 294,38 phút có giá trị.

Hai bẫy:

- **Dùng một cycle time cho cả ca** (ví dụ lấy của WO đầu, 20 s): Ideal = 800 × 20 = 16.000 s, P = 0,6838 → P thấp oan
  vì `PCB-B` vốn chậm hơn. Phải cộng `Σ count × cycle time` theo từng WO (quy tắc 3).
- **Rút gọn với nhiều WO**: công thức `cycle time × Good / Planned` chỉ đúng khi một cycle time. Tổng quát là
  `Ideal × Q / Planned` = 0,7009. Nếu tính "thời gian có giá trị" theo từng WO (`20 × 590 + 30 × 195 = 17.650 s`) thì
  ra 0,7004: lệch 0,05 điểm vì Q của dự án là Q gộp (785/800), không phải Q riêng từng WO. Đây là quy ước, không phải
  lỗi; `OeeCalculator` theo spec dùng Q gộp, và test tính chất `A×P×Q = Ideal × Good / (Total × Planned)` vẫn đúng.
