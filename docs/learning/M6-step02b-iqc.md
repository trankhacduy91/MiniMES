# M6 · Bước 2b — [v2] IQC, kiểm tra vật tư đầu vào: kiến thức cần nắm

> Tài liệu học cho bước 2b của [module-06](../../spec/module-06-material-traceability.md) (spec mục **1.2b**, **2.4b**, R15–R17).
> Bước trước: [M6-step02](M6-step02-receiving.md).
> Code: `src/MiniMes.Application/Material/Iqc.cs`, `src/MiniMes.Api/Controllers/IqcController.cs`,
> BackOffice `Views/Material/IqcView.xaml`. Ảnh: [m6-iqc.png](../images/m6-iqc.png).

---

## A. Nghiệp vụ

- **IQC** (Incoming Quality Control): QC kiểm hàng mua **trước khi** cho dùng: đếm, đối chiếu nhãn nhà cung cấp, đo vài mẫu
  (ví dụ đo giá trị 5 con tụ), kiểm chứng từ.
- Kho đặt hàng chờ kiểm vào **khu cách ly (quarantine)**. Trong MES đó là trạng thái `PendingIqc`, và (bước 4) kiosk sẽ
  **chặn nạp**: tờ giấy dán "chờ kiểm" không chặn được operator đang thiếu hàng, hệ thống thì chặn được.
- QC kiểm theo **một lần giao (supplier lot)**, lấy mẫu vài reel; kết quả áp cho mọi reel của lần giao đó.
- **AQL** (ISO 2859-1 / ANSI Z1.4): lô N đơn vị → kiểm n mẫu, chấp nhận nếu ≤ c lỗi. Vì vậy "Pass nhưng có 1 lỗi" là hợp lệ,
  cần ghi chú giải thích. Dự án chưa làm bảng gợi ý cỡ mẫu; chỉ cần biết tên chuẩn khi phỏng vấn.
- **Fail** = `IqcRejected`, không quay lại bằng thao tác thường (R17): trả hàng hay "dùng tạm" là quyết định NCR (M7).

## B. Kỹ thuật

| Vấn đề | Cách làm |
| --- | --- |
| Một kết quả cho N reel | Một câu `ExecuteUpdate ... WHERE ItemId, SupplierLot, Status = PendingIqc` (set-based), không vòng lặp từng reel |
| Ghi kết quả + đổi trạng thái là một việc | Cùng transaction: `UPDATE` rồi `INSERT IqcRecord`, commit một lần |
| Hai QC bấm cùng lúc | Người thứ hai: `UPDATE` chờ khóa dòng, đọc lại thấy không còn `PendingIqc` → 0 dòng → 422, không ghi record thứ hai. Không cần khóa trong bộ nhớ |
| `ExecuteUpdate` bỏ qua audit interceptor | `IqcRecord` (insert-only) chính là hồ sơ audit của thao tác |
| Reel giao thêm sau lần kiểm | Vẫn `PendingIqc`: lần giao mới cần kiểm mới, hiện lại trong danh sách chờ |
| Mã lỗi IQC | Dùng `DefectCode` của M1, nhóm `Incoming`; không cho chọn mã lỗi sản xuất (`SLD-BRG`) |

Quy tắc Domain thêm ở bước này: lot cần IQC **phải có supplier lot**, nếu không nó không bao giờ rời `PendingIqc` được.

## C. Lỗi tìm ra bằng unit test ViewModel

Ghi kết quả xong, màn hình tải lại danh sách chờ. Dòng vừa kiểm biến mất nên `Selected = null`, và `OnSelectedChanged` xóa
luôn thông báo kết quả vừa đặt: người dùng bấm Pass mà không thấy gì. Sửa: đặt thông báo **sau** khi tải lại. Bài học: hai
hành vi đúng riêng lẻ ("chọn dòng khác thì xóa thông báo cũ", "kiểm xong thì tải lại") có thể triệt tiêu nhau; test ViewModel
không cần server bắt được ngay.

## D. Câu hỏi phỏng vấn

1. **"Vì sao MES cần biết IQC?"** Để chặn nạp vật tư chưa kiểm ngay ở kiosk, không dựa vào nhãn giấy.
2. **"Kiểm một lần cho cả lô 10 reel thế nào?"** Một `UPDATE` set-based trong transaction, kèm một bản ghi IQC; hai người bấm
   cùng lúc thì `UPDATE` thứ hai thấy 0 dòng.
3. **"Pass mà vẫn có lỗi?"** Theo AQL, lô vẫn được chấp nhận nếu số lỗi ≤ ngưỡng của kế hoạch lấy mẫu; hệ thống bắt ghi chú.
4. **"Lô bị từ chối thì sao?"** Không tự quay lại được; NCR (M7) quyết định trả hàng hay dùng có điều kiện, có người duyệt.
