# M7 · Bước 8 — NCR: kiến thức cần nắm

> Tài liệu học cho bước 8 của [module-07](../../spec/module-07-quality.md) (spec mục **2.5**, Q11–Q14). Code:
> - `src/MiniMes.Domain/Quality/Ncr.cs`
> - `src/MiniMes.Application/Quality/Ncrs/`, `src/MiniMes.Api/Controllers/NcrsController.cs`
> - `src/MiniMes.Client.Core/ViewModels/Quality/NcrViewModel.cs`

## A. NCR là gì, trong một câu

Hồ sơ của một lần "hàng không đạt": thấy gì, bao nhiêu cái, ai điều tra, đề xuất xử lý thế nào, **ai khác** duyệt, và khi nào đóng. Nhà máy giữ hồ sơ này để trả lời khách và đoàn audit: "lô này có vấn đề gì, ai cho phép giao".

## B. Bốn trạng thái [v2]

```
Open ─StartReview─▶ UnderReview ─Approve (người khác)─▶ Dispositioned ─Close (hết Hold)─▶ Closed
  │                   │ ▲ Propose / RejectProposal (không đổi trạng thái)
  └───────────────────┴─▶ Cancelled (mở nhầm, trùng)
```

- **Đề xuất không phải một trạng thái.** QC có thể sửa đề xuất nhiều lần trong UnderReview; đề xuất mới thay đề xuất cũ cùng tên người đề xuất. Supervisor không đồng ý thì `RejectProposal` xóa đề xuất, NCR vẫn ở UnderReview.
- **Các quy tắc spec liệt kê:**
  - Q11: người duyệt khác người đề xuất. So bằng mã user, không phân biệt hoa thường, không so tên hiển thị.
  - Q12: không đóng, không hủy khi còn Hold đang mở do NCR này đặt.
  - Q13: trả nhà cung cấp chỉ áp dụng cho lot.
  - Q14: phải có ghi chú; số concession chỉ đi cùng quyết định use-as-is.
- Các quy tắc nằm trong **Domain** (unit test 16 case), không nằm trong UI. Nút trên màn hình bật/tắt cho tiện, còn API thì kiểm lại: 403 sai role, 422 sai trạng thái hoặc tự duyệt.

## C. Phân tách nhiệm vụ (segregation of duties)

"Người sửa hồ sơ không tự duyệt cho mình" được kiểm ở hai tầng:
1. **Role:** QC đề xuất, Supervisor duyệt (`[Authorize]`).
2. **Người:** dù là Admin có đủ hai role, ai đề xuất thì không duyệt được (Domain, 422). Test `Nobody_approves_their_own_proposal` thử đúng trường hợp này.

Màn hình cũng tắt nút Approve và ghi lý do ("You proposed this disposition…") khi chính mình là người đề xuất.

## D. Chống ghi đè và chống đua

- **RowVersion trên mỗi lệnh**, như Andon của M4. Hai người cùng mở một NCR, một người hủy trong lúc người kia bấm review: người sau nhận 409 ("người khác vừa sửa, nạp lại"), không phải 422. Màn hình tự nạp lại NCR khi gặp 409.
- **Một alert chỉ sinh một NCR.** Hai QC cùng bấm "Create NCR" trên một alert: nếu đọc "chưa có NCR" rồi mới ghi thì cả hai đều tạo được. Ở đây dùng `UPDATE SpcAlerts SET NcrId = … WHERE Id = … AND NcrId IS NULL`; người thua thấy 0 dòng, transaction của họ rollback và nhận 409. Số NCR họ đã lấy từ SEQUENCE thành một chỗ trống, như mọi sequence bị rollback. Thử bỏ điều kiện `NcrId IS NULL` thì test đỏ.

## E. Vì sao Approve chưa làm gì với serial/lot

Bảng hiệu ứng ở spec 2.5 (UseAsIs thả Hold, Rework đưa vào Repair, Scrap hủy, RTV trả nhà cung cấp) dòng nào cũng bắt đầu bằng "Release Hold", mà Hold/Release là bước 9. Bạn đã chọn làm hiệu ứng ở bước 9, trong **cùng transaction** với Approve, dùng chung một đường Release. Lợi ích: không phải viết phần Release hai lần.

`Ncr` vẫn không biết `Serial` hay `MaterialLot` (spec 2.5 bẫy 2): hiệu ứng nằm ở handler `ApproveNcrCommand`.

## F. NCR và SPC alert

- Nút **Create NCR** trên từng alert của chart mở màn hình NCR với form đã điền: nguồn `SpcAlert`, mô tả (rule, plan, giờ, ý nghĩa) và serial của điểm đo nếu có.
- **Quyết định (spec 2.5 bẫy 5 bảo chọn một):** alert đóng cùng NCR, với ghi chú "NCR … closed: disposition". Nếu NCR bị hủy thì alert được gỡ liên kết, để mở NCR khác cho nó.

## G. Câu hỏi phỏng vấn

1. **"Làm sao ngăn một người vừa đề xuất vừa duyệt?"** Hai lớp: role và so người (mã user) trong Domain. Kể thêm test với Admin có đủ hai role.
2. **"Hai người cùng xử lý một NCR thì sao?"** RowVersion: người sau nhận 409 và màn hình nạp lại. Phân biệt 409 ("dữ liệu đã đổi") với 422 ("quy tắc không cho").
3. **"Vì sao không đóng NCR được khi hàng còn bị Hold?"** Q12: nếu không chặn, sẽ có hồ sơ đã đóng mà hàng vẫn bị giữ, hoặc hàng đã thả mà hồ sơ chưa xử lý. Cả hai đều là lỗi audit.
