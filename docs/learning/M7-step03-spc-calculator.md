# M7 · Bước 3 — SpcCalculator: kiến thức cần nắm

> Tài liệu học cho bước 3 của [module-07](../../spec/module-07-quality.md) (spec mục **1.2**, **2.1**). Code: `src/MiniMes.Domain/Quality/Spc/SpcCalculator.cs`, test `tests/MiniMes.UnitTests/Quality/SpcCalculatorTests.cs`.

## A. Ba hàm, mỗi hàm trả lời một câu hỏi

| Hàm | Câu hỏi | Ví dụ 10 điểm (1.2) |
| --- | --- | --- |
| `Compute` | Quy trình đang ở đâu (mean), dao động bao nhiêu (σ), "bình thường" kéo tới đâu (UCL/LCL = mean ± 3σ) | 3,320; 0,03367; 3,421 / 3,219 |
| `CalcCapability` | Dao động đó có lọt vừa spec khách không (Cp), và có nằm giữa không (Cpk) | Cp 1,634, Cpk 1,436 |
| `MovingRange` | Từ điểm này sang điểm kế, quy trình nhảy bao nhiêu (MR chart) | MR̄ 0,0544, UCL_MR 0,178 |

Cp − Cpk = 0,2 là phần "mất" do lệch tâm: mean 3,32 lệch 0,02 V về phía USL. Chỉnh tâm về 3,30 thì Cpk sẽ gần bằng Cp.

## B. Hàm thuần: vì sao đặt ở Domain

- Không DB, không giờ hệ thống, không I/O. Cùng đầu vào thì luôn cùng đầu ra, nên test bằng số tính tay là đủ (17 test chạy trong khoảng 0,2 giây).
- Dùng chung cho mọi nơi cần: lệnh tính lại baseline (bước 5), chart WPF (bước 7), báo cáo. Không có ba chỗ tự tính ba kiểu.
- Không đặt trong SQL (`AVG`/`STDEV`) vì không unit test được. Lưu ý thêm: `STDEV` của SQL Server là σ **mẫu**, còn `STDEVP` mới là σ quần thể. Tên dễ nhầm.

## C. `double` cho thống kê, `decimal` cho spec

- So spec (Q2) cần đúng tuyệt đối: 3,465 là OK, 3,4650001 là NG. Vì vậy `InspectionPlan.Judge` dùng `decimal`.
- Thống kê có `Math.Sqrt`, nên vốn đã là số gần đúng; `double` nhanh và đủ chính xác.
- Cái giá phải trả: **đừng so `== 0` trên kết quả**. Ba số 3,3 cho σ ≈ 5e-16, nên Cpk ≈ 10^14 ([incidents](../incidents.md)). Cách sửa là nhận ra trường hợp từ **đầu vào** (mọi giá trị bằng nhau), không phải từ kết quả.
- Trong test luôn dùng `Assert.Equal(expected, actual, precision)`.

## D. Expected phải đến từ nguồn độc lập

Test tự chứng minh chính nó (chạy code, dán kết quả vào làm expected) là bẫy 2 của spec 2.1. Bước này dùng ba nguồn:
1. Tính tay (ERD bước 1).
2. `statistics.stdev` của Python.
3. Engine công thức của ClosedXML: `AVERAGE`, `STDEV.S`, `STDEV.P`. Đây là "đối chiếu Excel" của spec, nhưng chạy được trong CI, không cần mở Excel.

Kiểm thêm bằng đột biến: chia n thay n − 1 thì 6 test đỏ (σ 0,03194 thay 0,03367).

## E. σ mẫu hay σ từ MR̄

- MVP dùng σ mẫu (chia n − 1), như spec chọn.
- Giáo trình I chart dùng σ = MR̄ / 1,128. Với 10 điểm này ra 0,0483, rộng hơn σ mẫu, vì dữ liệu lên xuống xen kẽ.
- Test `MovingRange_depends_on_the_order_of_the_points` cho thấy cùng 10 số, sắp lại theo thứ tự tăng dần thì σ mẫu giữ nguyên, nhưng MR̄ còn 0,0111. Vì vậy MR phải tính theo **thứ tự đo** (`MeasuredAt`, bước 5).

## F. Câu hỏi phỏng vấn

1. **"Cp và Cpk khác nhau thế nào?"** Cp chỉ so độ rộng; Cpk so khoảng cách từ mean tới giới hạn gần nhất. Cp cao mà Cpk thấp nghĩa là quy trình đủ hẹp nhưng lệch tâm: chỉnh tâm, không cần máy tốt hơn.
2. **"Đặc tính chỉ có USL thì tính Cpk thế nào?"** Không có Cp, Cpk = Cpu. Hàm trả `null` cho phần không tính được, không trả 0, vì 0 sẽ đọc thành "quy trình rất tệ".
3. **"Vì sao chia n − 1?"** Mean được tính từ chính mẫu đó nên các điểm "gần" mean hơn thực tế. Chia n sẽ đánh giá thấp σ, giới hạn hẹp hơn thật và báo động giả nhiều hơn.
