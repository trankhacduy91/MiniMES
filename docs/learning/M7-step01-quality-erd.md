# M7 · Bước 1 — Ôn nghiệp vụ Quality và ERD: kiến thức cần nắm

> Tài liệu học cho bước 1 của [module-07](../../spec/module-07-quality.md) (spec mục **1.2**, **1.4** Q1–Q20, **2.3**, **2.5**, **2.5b**, **2.6**).
> Sản phẩm của bước: [`docs/quality-erd.md`](../quality-erd.md).

## A. Năm khái niệm phải nói được trong một câu

| Khái niệm | Một câu | Ví dụ của dự án |
| --- | --- | --- |
| **USL/LSL** | Giới hạn **khách** định; vượt là board hỏng (NG) | Rail 3V3: 3,135–3,465 V |
| **UCL/LCL** | Giới hạn **quy trình** tự tính (mean ± 3σ từ baseline); vượt là quy trình bất thường, board có thể vẫn tốt | 3,182–3,422 V |
| **Western Electric rules** | Hình dạng hiếm gặp khi quy trình ổn định; xuất hiện là có nguyên nhân | WE1 (1 điểm > 3σ), WE2 (2/3 điểm > 2σ cùng phía), run 7 (7 điểm cùng phía tâm) |
| **Cp / Cpk** | Bề rộng spec so với bề rộng quy trình (6σ); Cpk tính thêm độ lệch tâm | Cp 1,634, Cpk 1,436: hẹp đủ, nhưng tâm lệch 0,02 V lên phía USL |
| **I-MR** | Biểu đồ từng giá trị (I) + biểu đồ chênh lệch giữa hai lần đo liên tiếp (MR) | MR̄ 0,0544, UCL_MR 0,178 |

Câu hỏi kiểm tra của spec: "vì sao USL ≠ UCL?". Trả lời bằng board đo được 3,44 V:
- So với USL 3,465, board **đạt**, được giao.
- So với UCL 3,422, điểm này **vượt** (z = 3,45): quy trình hôm nay khác mọi khi. Phải đi tìm nguyên nhân **trước** khi ra board hỏng.

Vì vậy M7 có hai cơ chế độc lập:
- Vượt spec thì board NG (Q1).
- Vượt giới hạn kiểm soát thì tạo alert, không NG board nào và không tự Hold (Q6).

## B. Tự tính lại tay, đừng tin số trong tài liệu

Bước này yêu cầu tính lại ví dụ 10 điểm. Tôi tính độc lập và mọi số đều khớp spec (bảng ở mục 1 của ERD). Ba điều rút ra khi tính:

1. **σ mẫu chia n − 1** (0,03367), không chia n (0,03194). Hai cách lệch nhau khoảng 5 %, đủ làm Cpk thay đổi ở chữ số thứ hai.
2. **Ước lượng σ bằng MR̄/1,128 cho 0,0483**, lớn hơn σ mẫu, vì 10 điểm này lên xuống xen kẽ. Cùng một dữ liệu, giới hạn kiểm soát có thể khác nhau tùy cách ước lượng σ. Báo cáo phải ghi rõ đã dùng cách nào (spec mục 4: σ within / overall, Cpk / Ppk).
3. Unit test ở bước 3 phải lấy **giá trị mong đợi từ tính tay**, không lấy từ kết quả chạy code rồi dán lại vào test.

## C. Ba quyết định thiết kế (đã duyệt)

1. **Khóa `Id` như M6/M3b.** `HoldRecord` lưu `TargetType` + `TargetId`. Lý do: câu Hold set-based phải join với `SerialComponents` (truy xuất xuôi M6) và `CartonSerials` (thùng/pallet M3b), và hai bảng đó đều dùng Id.
2. **Plan ổn định, số đo chụp lại giới hạn.** Q15 muốn "kết quả lịch sử không đổi khi spec đổi". Cách làm ở đây: mỗi `Measurement` lưu `Usl`/`Lsl` lúc đo cùng `Result`, và đổi spec đi qua audit. Không phải tạo một dòng plan mới cho mỗi lần đổi, rồi chép baseline và trỏ lại tag máy sang dòng mới.
   - Lý do sâu hơn: **giới hạn kiểm soát thuộc về quy trình, không thuộc về spec**. Khách nới USL thì quy trình vẫn y như cũ, baseline không cần tính lại.
3. **Plan gắn `ItemId` + mã công đoạn**, không gắn bước routing. Routing có version, còn WO chạy theo snapshot. Mã công đoạn `TEST` thì không đổi qua các version.

## D. Hold: một nguồn sự thật, nhiều điểm thực thi

Bước này kiểm lại và thấy các cửa đã chặn sẵn trạng thái `Hold` từ các module trước:

| Cửa | Đã chặn từ |
| --- | --- |
| Scan serial ở mọi trạm, kể cả trạm tự động | M3 route check (`SerialHold`) |
| Nạp reel lên máy | M6 verification (`LotOnHold`) |
| Scan khi reel đang chạy vừa bị Hold | M6 gate tiêu hao |
| Đưa thùng lên pallet, ship pallet | M3b (Hold suy ra từ serial) |

Vì vậy M7 chỉ làm hai việc:
1. Đổi `Status` ở **một** nơi, kèm `StatusBeforeHold` để khi Release trả về đúng trạng thái cũ.
2. Ghi `HoldRecord` làm lịch sử: ai Hold, vì sao, Hold bằng gì, ai Release.

Hold chồng nhau (Q8): còn dòng `HoldRecord` nào chưa Release thì đối tượng vẫn ở trạng thái Hold.

## E. Câu hỏi phỏng vấn

1. **"USL với UCL khác nhau thế nào?"** Trả lời theo mục A, kèm ví dụ 3,44 V. Thêm tình huống ngược: σ lớn làm UCL nằm ngoài USL, chart trông "sạch" mà vẫn ra hàng lỗi. Phát hiện chuyện này là việc của Cpk.
2. **"Đổi spec thì số đo cũ có thành NG không?"** Không. Mỗi số đo lưu giới hạn và kết quả tại thời điểm đo, và việc đổi spec có audit.
3. **"Hold một lot thì những chỗ nào phải chặn?"** Mọi cửa hàng đi qua (bảng mục D). Thiết kế là một nguồn sự thật cộng nhiều điểm thực thi, có integration test xuyên module.
