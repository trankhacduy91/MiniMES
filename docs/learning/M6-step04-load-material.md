# M6 · Bước 4 — Nạp vật tư trên Kiosk: kiến thức cần nắm

> Tài liệu học cho bước 4 của [module-06](../../spec/module-06-material-traceability.md) (spec mục **1.3 A**, **2.4**, R2–R6, R10, R13).
> Bước trước: [M6-step03](M6-step03-material-verifier.md). Bước sau: [M6-step05](M6-step05-consumption.md).
> Code: `src/MiniMes.Application/Material/MaterialLoads.cs`, `src/MiniMes.Api/Controllers/MaterialLoadsController.cs`,
> `src/MiniMes.Client.Core/ViewModels/Kiosk/LoadMaterialViewModel.cs`, `src/MiniMes.Kiosk.Wpf/Views/LoadMaterialView.xaml`.
> Ảnh: [vàng FIFO](../images/m6-load-material-fifo.png), [đỏ](../images/m6-load-material-rejected.png).

---

## A. Nghiệp vụ: setup và đổi reel

- Trước khi chạy WO, operator gắn reel lên máy (**setup**); trong ca, hết reel thì đổi. Mỗi lần là một cơ hội nạp nhầm, và nạp
  nhầm tụ/điện trở thì mắt thường không thấy. Kiosk kiểm từng reel trước khi gắn.
- Màn hình là một **checklist**: WO cần gì ở trạm này, reel nào đang gắn. Dòng đỏ "NOT LOADED" là việc còn phải làm.
- **Vàng (FIFO)**: còn reel cũ hơn có hàng. Đúng nhất là lấy reel cũ; nếu có lý do (reel cũ nghi vấn, yêu cầu khách) thì
  **supervisor** duyệt bằng thẻ, và lý do được ghi lại.
- **Tháo reel**: nhập số còn lại thật (đếm, cân). Số trong hệ thống là lý thuyết vì máy gắp làm rơi 0,5–2 %; khi tháo là lúc sửa.

## B. Thiết kế

| Câu hỏi | Trả lời |
| --- | --- |
| Kết quả nạp trả về thế nào? | 200 + `Outcome`/`Code`, như scan M3: kết quả nghiệp vụ không phải lỗi kỹ thuật. 400/404/409/422 chỉ cho request sai, trạm không có, tranh chấp, thiếu WO |
| Kiểm ở đâu? | Hàm thuần `MaterialVerifier` (bước 3); handler chỉ nạp dữ liệu và lưu |
| Một reel mỗi (trạm, item) | Filtered unique index; nạp reel mới thì đóng load cũ **trong cùng** `SaveChanges`. Hai kiosk cùng lúc: một bên 409 |
| Override FIFO | Request thứ hai mang thẻ supervisor + lý do; API kiểm role `Supervisor`; ghi `FifoOverrideBy/Reason` trên load và trên log |
| Log | Mọi lần thử (xanh, vàng, đỏ, duyệt) một dòng `MaterialVerificationLog`: bằng chứng cho khách, dữ liệu đào tạo lại |

## C. Kỹ thuật Kiosk: một ô nhập, nhiều nghĩa

Máy quét mã vạch là bàn phím; ô scan phải luôn giữ focus (đã học ở M3). Vì vậy không có ô thứ hai để gõ thẻ hay số lượng:
ViewModel giữ trạng thái `MaterialInput` (`Lot` → `SupervisorBadge` → `Remaining`) và hiện dòng hướng dẫn **"giờ scan gì"**.
Lý do override là **nút chọn sẵn** (đeo găng, không gõ). Đây là mẫu "state machine nhỏ trong ViewModel", test được không cần
WPF.

## D. Lỗi tìm ra khi chạy thật

1. **Dòng hướng dẫn không đổi sau cảnh báo vàng.** `Prompt` là thuộc tính tính toán, phụ thuộc `PendingLotNo`, nhưng thiếu
   `[NotifyPropertyChangedFor(nameof(Prompt))]`. Unit test đọc thẳng getter nên vẫn xanh. Sửa và thêm test **lắng nghe
   `PropertyChanged`** (đỏ khi bỏ bản sửa). Bài học: với thuộc tính tính toán trong MVVM, test cả thông báo thay đổi, không chỉ giá trị.
2. **Chữ khó đọc**: hướng dẫn màu vàng nhạt trên nền vàng. Đặt trên dải tối, đọc được trên mọi màu nền.
3. **An toàn khi tự động hóa UI**: phím Enter (KeyBinding) không bấm được qua UI Automation, phải dùng `SendKeys`, mà `SendKeys`
   gõ vào **cửa sổ đang ở trên cùng**, có thể là cửa sổ của người dùng. Script chỉ gửi Enter khi cửa sổ trên cùng đúng là Kiosk.

## E. Câu hỏi phỏng vấn

1. **"Operator nạp nhầm reel thì sao?"** Kiosk đỏ, có âm báo, không ghi load; lần thử được log. Chỉ FIFO mới có ngoại lệ, và
   ngoại lệ cần supervisor + lý do.
2. **"Hai người cùng nạp một item lên một trạm?"** Unique index lọc theo `UnloadedAt IS NULL`; người sau nhận 409.
3. **"Số lượng còn lại có chính xác không?"** Không: là số lý thuyết, trừ theo BOM mỗi lần scan; hiệu chỉnh khi tháo reel. Truy
   xuất quan trọng hơn tồn kho tuyệt đối.
