# M7 · Bước 7 — Control chart trong WPF: kiến thức cần nắm

> Tài liệu học cho bước 7 của [module-07](../../spec/module-07-quality.md) (spec mục **2.4**, 1.5 sai lầm 1). Code:
> - `src/MiniMes.Client.Core/ViewModels/Quality/` (builder, ViewModel)
> - `src/MiniMes.BackOffice.Wpf/Views/Quality/ControlChartView.xaml`
>
> Ảnh: [`m7-spc-reflow.png`](../images/m7-spc-reflow.png), [`m7-spc-rail.png`](../images/m7-spc-rail.png).

## A. Đọc một control chart trong 10 giây

1. **Đường đen liền (USL/LSL):** của khách. Có điểm nào ra ngoài không?
2. **Đường đỏ nét đứt (UCL/LCL):** của quy trình. Có điểm nào ra ngoài không?
3. **Chấm đỏ:** điểm đã kích hoạt một rule. Rule nào thì xem danh sách bên phải.
4. **Hình dạng:** nhiều điểm liên tiếp một phía tâm, một xu hướng đi lên… Rule bắt đúng những hình dạng này.
5. **MR chart bên dưới:** quy trình có đang "nhảy" nhiều hơn bình thường không (độ biến động ngắn hạn).

Ảnh chart lò cho thấy chính câu chuyện của spec 1.2: các điểm cuối vẫn cách USL 255 tới 8 °C nhưng đã đỏ (WE2, run). Đó là phát hiện trước khi ra hàng hỏng.

## B. Vì sao `PlotModel` nằm ở `Client.Core`, không ở WPF

Spec gợi ý `BackOffice.Wpf/Quality/ControlChartBuilder`. Dự án đã chọn từ M5:
- `PlotModel` của **OxyPlot.Core** chỉ là dữ liệu (series, trục, annotation), không có type WPF. Vì vậy nó nằm trong ViewModel ở `Client.Core`.
- Hệ quả: unit test được trên Linux (CI), ví dụ "điểm 7 và 8 có chấm đỏ", "UCL nét đứt đỏ, USL nét liền đen".
- `PlotView` (OxyPlot.Wpf) chỉ vẽ. Mỗi lần nạp lại thì gán một model mới qua binding, nên không cần gọi `InvalidatePlot` từ ViewModel.

## C. Bẫy của spec 2.4 và cách làm ở đây

| Bẫy | Cách làm |
| --- | --- |
| Trục Y tự co theo dữ liệu, USL/LSL rơi ra ngoài khung | Min/Max của trục tính từ dữ liệu **và** mọi đường giới hạn |
| Nhầm UCL với USL | Khác màu (đỏ / đen), khác kiểu (đứt / liền), khác độ dày, có nhãn chữ, có chú giải. Không chỉ dựa vào màu (người mù màu) |
| Tô điểm vi phạm theo Id DB hay theo vị trí | Server trả `MeasurementId`; builder đổi sang vị trí trong cửa sổ đang xem |
| Tạo lại model mỗi giây gây giật | Chỉ nạp lại khi có alert (SignalR) hoặc mỗi 15 giây |
| Giờ trên trục là UTC | Trục X là số thứ tự mẫu; tooltip ghi giờ nhà máy (`TimeProvider.LocalTimeZone`) |
| [v2] MR chart | `PlotModel` thứ hai, cùng trục X, đặt ngay dưới. MR̄/UCL_MR lấy từ baseline; baseline seed chưa có thì dùng của cửa sổ, ghi rõ "(this window)" |

## D. SignalR: đẩy cho đúng người, sau khi commit

- `SpcAlertRaised` gửi tới **group role** QC / Supervisor / Admin, không theo line, vì một plan có thể là của máy hoặc của một bước. Server tự đưa mỗi kết nối vào group theo role trong JWT (M4); client không tự xin vào được.
- Đẩy **sau** commit. Nếu đẩy trước, client có thể nạp lại chart mà chưa thấy alert.
- Client: alert thuộc plan đang xem thì nạp lại; plan khác thì hiện một dòng thông báo. Sự kiện tới trên thread nền, nên mọi thay đổi UI đi qua `IUiDispatcher`.
- Điểm mới mà không có alert thì không có push, nên màn hình poll 15 giây (lò lấy mẫu 30 giây một lần).

## E. Cp/Cpk trên màn hình là của cái gì

- **Cp/Cpk** tính trên **các điểm đang xem**, so với **spec hiện tại**. Nó trả lời "quy trình bây giờ có đáp ứng khách không".
- **UCL/LCL** là của **baseline cố định**. Nó trả lời "quy trình có đang khác mọi khi không".
- Đổi số điểm (25 / 100 / 200) thì Cp/Cpk thay đổi, còn UCL/LCL thì không.

## F. Câu hỏi phỏng vấn

1. **"Chart của anh phân biệt USL và UCL thế nào?"** Màu, kiểu nét, độ dày và nhãn. Kể thêm vì sao điều này quan trọng: nhầm hai cặp này là sai lầm số 1 của SPC (spec 1.5).
2. **"Dashboard realtime cập nhật thế nào mà không giật?"** Push qua SignalR chỉ cho sự kiện có ý nghĩa (alert), poll chậm cho phần còn lại, và mỗi lần chỉ gán model mới.
3. **"Làm sao test một biểu đồ?"** Tách phần "dữ liệu của biểu đồ" (`PlotModel`) khỏi phần vẽ, rồi assert trên model: chấm đỏ ở X = 7, UCL nét đứt. Phần vẽ thì kiểm bằng một lần chạy thật và ảnh chụp màn hình.
