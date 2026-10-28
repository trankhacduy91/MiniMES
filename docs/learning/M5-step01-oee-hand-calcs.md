# M5 · Bước 1 — Tính OEE bằng tay: kiến thức cần nắm

> Tài liệu học cho bước 1 của [module-05](../../spec/module-05-oee-downtime.md) (spec mục **1.2**, 1.4, 1.5).
> Bài tính chi tiết: [docs/oee-hand-calcs.md](../oee-hand-calcs.md). Bước trước (module trước):
> [M4b-step07](M4b-step07-docker-e2e-docs.md).

Bước này chưa viết code. Mục tiêu là hiểu OEE đến mức nhìn bốn con số trung gian (Planned, Run, Total, Good) là
biết A/P/Q. Bốn ca trong `oee-hand-calcs.md` sẽ thành bảng test của bước 2 và 4.

---

## A. OEE trong một câu

**OEE = thời gian lý tưởng để làm ra đúng số sản phẩm tốt ÷ thời gian đáng lẽ máy được dùng.**

```
OEE = A × P × Q = (Run/Planned) × (Ideal/Run) × (Good/Total) = Ideal × (Good/Total) / Planned
```

`Run` triệt tiêu. Một WO thì `Ideal/Total` là cycle time, nên OEE = `cycle time × Good / Planned`. Ca mẫu:
`20 × 970 / (405 × 60) = 0,7984`. Đây là cách kiểm chéo nhanh nhất khi số OEE trông lạ.

## B. Ba thành phần = ba loại mất thời gian

| Thành phần | Mất gì | Ở ca mẫu | Six Big Losses |
| --- | --- | --- | --- |
| A | Thời gian dừng ngoài kế hoạch | 45 phút | Breakdown, Setup |
| P | Thời gian chạy chậm + micro stop | 26,67 phút | Minor stop, Reduced speed |
| Q | Thời gian làm ra NG | 10 phút | Process defects, Startup rejects |

Vẽ "thác nước thời gian" (405 → 360 → 333,33 → 323,33) là cách giải thích OEE dễ hiểu nhất cho quản lý xưởng: mỗi
bậc là một loại mất mát, bậc nào cao nhất thì cải tiến ở đó trước.

## C. Năm điều rút ra từ bốn ca

1. **P > 100 % là cảnh báo dữ liệu, không phải tin vui** (ca a). Cycle time chuẩn 20 s trong khi máy chạy 15 s cho
   P 133 %, OEE 117 %. Giữ số thô và gắn cờ, đừng cắt về 100 % mà không sửa master.
2. **Nghỉ ∪ PlannedStop là phép hợp khoảng thời gian** (ca b). Họp 07:55–08:20 chồng giờ giải lao 08:00–08:15:
   trừ hai lần làm Planned nhỏ đi 15 phút và OEE đẹp ảo 3,3 điểm. Bước 3 sẽ viết `IntervalMath.UnionLength` cho việc này.
3. **Dừng trong giờ nghỉ không phải downtime** (ca b). Tính nhầm thì OEE không đổi, nhưng A tụt và P tăng: báo cáo chỉ
   sai chỗ, vẫn dẫn đến cải tiến sai. Test phải kiểm cả A và P.
4. **Nhiều WO: cộng `count × cycle time` theo từng WO** (ca c). Dùng một cycle time cho cả ca làm P sai.
5. **`Category` để báo cáo, `CountsAgainstAvailability` để tính** (ca c). `SET-CHANGEOVER` thuộc nhóm Planned nhưng vẫn
   phạt A, vì nhà máy muốn thấy đổi model trong Pareto và có mục tiêu giảm.

## D. Tổng hợp nhiều giờ: cộng rồi chia

Đừng lấy trung bình cộng các OEE giờ. Giờ 1: Planned 60 phút, OEE 90 %; giờ 2: Planned 6 phút, OEE 40 %. Trung bình
cộng = 65 %. Đúng: cộng thời gian có giá trị `0,9 × 60 + 0,4 × 6 = 56,4` chia tổng Planned `66` = **85,5 %**. Vì vậy
`OeeSnapshot` (bước 5) sẽ lưu Planned, Run, Total, Good, Ideal (các tổng), không chỉ lưu tỷ lệ.

## E. Câu hỏi phỏng vấn

1. **"OEE là gì, tính thế nào?"** Nói công thức và đọc ngay bốn số của ca mẫu: Planned 405, Run 360, Total 1.000,
   Good 970 → 88,9 / 92,6 / 97,0 → 79,8 %. Kiểm lại bằng `20 × 970 / 24.300`.
2. **"Máy chạy không bao giờ hỏng có OEE thấp được không?"** Có: A = 100 % nhưng test 30 s/bo so với chuẩn 20 s thì
   P = 67 %, OEE khoảng 66 %.
3. **"Performance 130 % nghĩa là gì?"** Cycle time chuẩn sai (quá chậm), sai đơn vị (panel vs bo), hoặc bộ đếm sai.
4. **"Họp đầu ca trùng giờ nghỉ thì trừ thế nào?"** Trừ phần hợp, không cộng hai khoảng.
5. **"OEE 85 % có phải chỉ tiêu chuẩn?"** Chỉ là con số tham khảo; mục tiêu thật tùy loại máy, mức đổi model. Dùng OEE
   để so một máy với chính nó theo thời gian, không phải chấm điểm operator.

## F. Đọc thêm

- Nakajima, *Introduction to TPM* (nguồn gốc Six Big Losses).
- oee.com: "OEE Calculation", "Six Big Losses".
- Spec M5 mục 1.2.4 (Planned vs Unplanned), 1.2.5 (micro stop), 1.2.7 (P > 100 %).
