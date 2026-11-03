# M7 · Bước 4 — Rule engine Western Electric: kiến thức cần nắm

> Tài liệu học cho bước 4 của [module-07](../../spec/module-07-quality.md) (spec mục **1.2** phần rule, **2.2**). Code: `src/MiniMes.Domain/Quality/Spc/WesternElectric.cs`, test `tests/MiniMes.UnitTests/Quality/WesternElectricTests.cs`.

## A. Bốn rule, một hàm

Mọi rule WE đều có cùng một dạng: **"N trong M điểm liên tiếp vượt K·σ, cùng một phía của đường tâm"**.

| Rule | N | M | K | Bắt được |
| --- | --- | --- | --- | --- |
| WE1 | 1 | 1 | 3 | Cú sốc lớn (hỏng jig, lắp nhầm vật tư) |
| WE2 | 2 | 3 | 2 | Dịch chuyển vừa |
| WE3 (tắt mặc định) | 4 | 5 | 1 | Dịch chuyển nhỏ đến vừa |
| WE4 (run) | 7 | 7 | 0 | Trôi nhỏ, kéo dài (nhiệt lò, tool mòn) |

- Vì vậy code chỉ có **một** hàm `Fires(z, i, rule)` cộng một danh sách cấu hình. Muốn thêm rule cùng dạng thì thêm một dòng dữ liệu, không thêm vòng `for`.
- Đổi run 7 thành 8 (số của Western Electric gốc) hay 9 (Nelson) cũng chỉ là đổi tham số: `Default(runLength: 8)`.

## B. Chuẩn hóa về z

`z = (x − mean) / σ`. Sau bước này mọi rule chỉ so z với 0, 1, 2, 3, nên không cần biết đặc tính đo bằng volt hay độ C. Lò reflow (245 °C, σ 0,8) và rail 3V3 (3,302 V, σ 0,040) dùng chung một engine.

Mean và σ lấy từ **baseline cố định** (`ControlLimitSet`, Q3), không tính lại từ chính các điểm đang xét. Nếu tính lại, một đợt trôi chậm sẽ tự kéo tâm theo và không bao giờ bị phát hiện.

## C. Ba chi tiết nhỏ quyết định đúng sai

1. **Cùng phía.** +2,2σ và −2,3σ đều "ngoài 2σ" nhưng ở hai phía khác nhau; ghép lại thì không phải WE2. Đột biến bỏ điều kiện cùng phía làm đỏ test `We2_does_not_mix_the_two_sides`.
2. **"Vượt" là so sánh chặt.** Điểm nằm đúng đường tâm (z = 0) không thuộc phía nào, nên chuỗi run bị ngắt. Đột biến đổi `>` thành `>=` làm đỏ 2 test.
3. **Làm tròn z.** Điểm nằm đúng UCL tính ra z = 3.0000000000000027, nên sẽ bị báo WE1 sai. Làm tròn tới 9 chữ số sẽ loại phần nhiễu nhị phân ([incidents](../incidents.md)).

## D. Báo ở điểm nào, và báo bao nhiêu lần

- Vi phạm gắn với **điểm cuối của cửa sổ**, tức điểm vừa làm đủ mẫu. Chart tô đỏ điểm đó, và `SpcAlert.MeasurementId` trỏ tới nó.
- Hàm không tự dedupe: chuỗi 8 điểm thì báo ở điểm 7 **và** điểm 8. Giữ một alert mở cho mỗi (plan, rule) là việc của use case (bước 5) và của filtered unique index (bước 2). Tách như vậy để hàm vẫn dùng được khi vẽ lại cả chart.
- Online (mỗi lần có số đo mới): `EvaluateLatest` chỉ trả rule báo **ở điểm mới nhất**. Chỉ cần tải `WindowSize(rules)` điểm gần nhất (7 với bộ mặc định), không cần tải 10.000 điểm.

## E. Đầu chuỗi: cửa sổ ngắn hơn M

Hai điểm đầu tiên cùng vượt +2σ đã đủ WE2 (2 trong ≤ 3), nên báo luôn là hợp lệ. Run 7 thì vẫn cần đủ 7 điểm, nên không bao giờ báo sớm.

## F. Câu hỏi phỏng vấn

1. **"Bật cả 4 rule cho chắc có được không?"** Mỗi rule thêm vào làm tăng báo động giả: chỉ WE1 thì khoảng 370 điểm mới có một báo giả (ARL ≈ 370), bật cả bộ thì còn vài chục đến khoảng 100 điểm. Báo giả nhiều thì người ta bỏ qua cả báo thật. Vì vậy dự án chỉ bật 3 rule.
2. **"Run 7 có nghĩa gì về xác suất?"** Nếu quy trình đứng yên, mỗi điểm có 50 % nằm trên tâm. 7 điểm liên tiếp cùng một phía (phía nào cũng được) có xác suất 2 × 0,5⁷ ≈ 1,6 %. Hiếm, nên đó là dấu hiệu mean đã dịch.
3. **"Vì sao rule engine nằm trong Domain, không trong trigger SQL?"** Hàm thuần nên test bảng được với số trong spec (20 test chạy trong khoảng 0,1 giây), và có thể chạy thử đột biến để chứng minh test có bắt lỗi. Trong trigger SQL thì không làm được những việc này.
