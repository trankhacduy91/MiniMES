# M3b · Bước 6 — Màn Packing trên Kiosk: kiến thức cần nắm

> Tài liệu học cho bước 6 của [module-03b](../../spec/module-03b-packing.md) (spec mục **2.5**, kịch bản **1.3** từ 13:00 tới 14:30).
> Bước trước: [M3b-step05](M3b-step05-pallet-ship.md).
> Code: `src/MiniMes.Client.Core/ViewModels/Kiosk/PackingViewModel.cs`, `Client.Core/Api/PackingApi.cs`, `Client.Core/Kiosk/LabelPrinter.cs`,
> `src/MiniMes.Kiosk.Wpf/Views/PackingView.xaml`, `Kiosk.Wpf/Navigation.cs`; server: `Application/Common/SupervisorBadges.cs`.
> Test: `tests/MiniMes.UnitTests/Client/PackingViewModelTests.cs`, `tests/MiniMes.IntegrationTests/Packing/PalletTests.cs`.

## A. Nghiệp vụ: một trạm, hai việc

Hoa ở trạm Pack làm hai việc xen kẽ: xếp board vào thùng, và xếp thùng lên pallet. Màn hình có **hai chế độ** dùng chung **một ô scan**:

| | PACK | PALLETIZE |
| --- | --- | --- |
| Scan gì | Serial `PCBA…` | Nhãn thùng `C…` |
| Số to | `7 / 10` (board trong thùng) | `12 / 20` (thùng trên pallet) |
| Danh sách | Board trong thùng, mới nhất ở trên | Thùng trên pallet, thùng có board Hold hiện màu cam |
| Đủ thì | Thùng tự đóng, nền xanh dương, âm riêng, nhãn ra máy in | Pallet tự đóng, nhãn pallet |

Scan nhầm loại mã (nhãn thùng ở chế độ Pack, serial ở chế độ Palletize) thì server nhận ra **theo hình dạng mã** và trả về chặn kèm câu dễ hiểu ("This is a carton label…"), không ghi gì.

## B. Màu và âm báo: kênh thứ hai

Operator nhìn tay và board, không nhìn màn hình. Vì vậy mỗi kết quả có màu và âm riêng (đã có từ M3 mục 2.8):
- Xanh lá: board vào thùng.
- **Xanh dương + âm khác** (`ScanState.Full`, mới ở bước này): thùng vừa đầy, lấy thùng ra và dán nhãn.
- Đỏ: bị chặn.

ViewModel chỉ đặt trạng thái. Màu nằm trong XAML (`DataTrigger`), âm nằm trong app WPF (`SystemSoundFeedback`). Nhờ vậy `Client.Core` không cần kiểu WPF nào và unit test được.

## C. Supervisor duyệt ở kiosk: quẹt thẻ, không gõ mật khẩu

Token của kiosk chỉ mang role Operator, trong khi đóng thùng lẻ cần Supervisor. Cách làm giống duyệt FIFO ở M6:

1. Bấm "Close short carton".
2. Chạm một lý do có sẵn (Work order end / Shift end / Model change). Không gõ, vì operator đeo găng.
3. Supervisor **quẹt thẻ** vào chính ô scan. API nhận `SupervisorBadge`, kiểm thẻ đó là Supervisor đang active, rồi ghi `ClosedBy` = supervisor.

- **Thẻ sai** (400): màn hình giữ bước quẹt thẻ để quẹt lại. **Lỗi khác** (422, ví dụ thùng vừa đầy): kết thúc thao tác.
- Hàm kiểm thẻ trước đây là `private` trong use case nạp vật tư của M6. Nay nó được tách thành `SupervisorBadges.VerifyAsync` để dùng chung. Hai chỗ duyệt dùng một quy tắc, nên không thể lệch nhau.

## D. In nhãn ở kiosk: ghi file vào thư mục

Server tạo nhãn ZPL **sau commit** và ghi log (bước 4). Kiosk chỉ cần đẩy nhãn ra máy in:
- `ILabelPrinter` (Client.Core) và bản `FolderLabelPrinter` ghi mỗi lần in thành một file `C261103000001-20261103T202520444.zpl` vào `LabelOutputFolder`, đóng vai một hàng đợi in. Máy Zebra thật nằm ngoài phạm vi, như ở M2.
- Ghi file lỗi thì thùng **vẫn đóng**, và màn hình báo "Label NOT saved" để in lại. Không bao giờ rollback việc đóng thùng chỉ vì máy in.
- Tên file theo **giờ máy trạm** (giờ nhà máy). Bản đầu dùng giờ UTC; khi chạy thật thấy tên file `T1325` trong khi màn hình hiện `20:25`, người tìm file sẽ nhầm, nên đã sửa.

## E. Kiểm tra bằng UI Automation

Kịch bản 1.3 chạy trên app thật:
- Script tạo WO 24 board qua API (23 board qua Test, 1 board chưa).
- Kiosk được điều khiển bằng UI Automation: tìm control theo `AutomationId`, gõ vào ô scan, gửi Enter **chỉ khi kiosk đang ở phía trước** màn hình.
- Kết quả từng scan được đọc lại từ các `TextBlock` (`Headline`, `Message`, `Progress`).
- 5 ảnh chụp nằm ở `docs/images/m3b-*.png`.

## F. Câu hỏi phỏng vấn

1. **"Operator đeo găng, làm sao supervisor duyệt trên màn hình cảm ứng?"** Lý do chọn bằng nút to, xác nhận bằng quẹt thẻ vào cùng ô scan. Server kiểm thẻ là Supervisor đang active và ghi tên người duyệt. Không ai phải gõ mật khẩu ở trạm.
2. **"Máy in hỏng thì sao?"** Thùng vẫn đóng, vì dữ liệu là chuẩn và nhãn chỉ là bản in của dữ liệu. Màn hình báo nhãn chưa in, rồi in lại từ endpoint có log `PrintNo` và lý do.
3. **"Sao không cho operator tự chọn chế độ bằng cách scan, khỏi bấm nút?"** Server đã tự nhận ra loại mã và báo nhầm chế độ. Chế độ vẫn để operator chọn rõ ràng, để một mã scan nhầm không âm thầm làm việc khác với ý định.
