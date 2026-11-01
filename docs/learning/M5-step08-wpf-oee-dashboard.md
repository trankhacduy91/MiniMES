# M5 · Bước 8 — Dashboard OEE trên WPF với OxyPlot: kiến thức cần nắm

> Tài liệu học cho bước 8 của [module-05](../../spec/module-05-oee-downtime.md) (spec mục **2.6**, 1.2.9, 1.3).
> Bước trước: [M5-step07](M5-step07-oee-api-excel-signalr.md).
> Code: `src/MiniMes.Client.Core/ViewModels/Oee/*`, `src/MiniMes.BackOffice.Wpf/Views/Oee/OeeDashboardView.xaml`,
> ảnh: [docs/images/m5-oee-dashboard.png](../images/m5-oee-dashboard.png).

---

## A. Màn hình cho ai, trả lời câu hỏi gì

| Vùng | Câu hỏi của quản lý xưởng |
| --- | --- |
| 4 thẻ A / P / Q / OEE | Ca này máy dùng tốt đến đâu, mất ở thành phần nào? |
| Huy hiệu vàng ⚠ | Số này có đáng tin không? (P > 100 % = master data sai, counter mismatch) |
| Cột sản lượng tốt theo giờ + đường mục tiêu | Giờ nào chậm? (giờ 12:00 thấp vì chờ QC, câu chuyện 1.3) |
| Pareto top 5 (phút / số lần) | Sửa cái gì trước? |
| Danh sách cần phân loại | Còn lần dừng nào chưa ai giải thích? |

**Mục tiêu theo giờ** = giây kế hoạch của giờ / cycle time chuẩn: giờ đủ 60 phút ở 20 s là 180 bo, giờ có 15 phút nghỉ
là 135, giờ nghỉ trưa là 0. Đây là mục tiêu "lý tưởng", cùng định nghĩa với Performance, nên khoảng hở giữa cột và
đường chính là phần mất của A và P trong giờ đó.

## B. OxyPlot: `PlotModel` ở ViewModel, `PlotView` ở WPF

```
Client.Core (net10.0)   OxyPlot.Core: PlotModel, Axes, Series, PlotController   ← test được trên Linux
BackOffice.Wpf          OxyPlot.Wpf:  <oxy:PlotView Model="{Binding Pareto.PlotModel}" Controller="..."/>
```

`PlotModel` là dữ liệu thuần, không phải control WPF. Vì vậy quy tắc "Client.Core không có kiểu WPF" vẫn giữ, và test
ViewModel kiểm được cả cấu trúc biểu đồ (nhãn, giá trị cột, trục %).

Mỗi lần `Load`, ViewModel tạo `PlotModel` mới và gán cho thuộc tính. View vẽ lại nhờ binding, nên không cần
`InvalidatePlot(true)`. Đánh đổi: mất trạng thái zoom; dashboard 15 giây/lần thì chấp nhận được (đã tắt zoom/pan).

### OxyPlot 2.2 khác snippet của spec

| Spec (2.0) | 2.2 thực tế |
| --- | --- |
| `ColumnSeries` + `ColumnItem` | **Không còn** (bỏ từ 2.1) |
| Cột dọc | `BarSeries` + `BarItem`, "chuyển vị": `CategoryAxis` ở **Bottom**, trục giá trị ở Left; series `XAxisKey` = trục giá trị, `YAxisKey` = trục danh mục |
| Tooltip | Mặc định chỉ hiện khi **bấm**; muốn rê chuột: `PlotController.BindMouseEnter(PlotCommands.HoverSnapTrack)` |

Cách tìm ra: đọc file `OxyPlot.xml` (tài liệu XML đi kèm package) trong thư mục NuGet, không đoán. Test khóa lại hành
vi: `((IPlotModel)model).Update(true)` rồi `Assert.True(bars.IsTransposed())`.

Pareto: cột (phút) trục trái + đường % tích lũy trục phải **khóa 0–100**; điểm thứ `i` của đường có `x = i` (chỉ số
nhóm) nên đi qua giữa cột. Màu không chỉ đỏ/xanh: đường nét đứt có điểm tròn, huy hiệu có ký hiệu ⚠.

## C. Một công thức OEE: thêm `GET /api/oee/shift`

Xem cả line cần A/P/Q của **nhiều máy cộng lại**. Nếu ViewModel tự cộng Planned/Run/Ideal/Good rồi chia, thì công thức
OEE nằm ở hai nơi (server và WPF), vi phạm quy tắc 1. Vì vậy thêm endpoint: server cộng bằng `OeeCalculator.Combine`,
client chỉ hiển thị. Tổng ca tính từ dữ liệu thô (đến bây giờ hoặc hết ca), các giờ lấy từ snapshot đã chốt.

## D. Cập nhật: poll + SignalR

| Nguồn | Khi nào | Vì sao |
| --- | --- | --- |
| Poll 15 s (`PeriodicTimer(15 s, TimeProvider)`) | Chỉ khi xem ca đang chạy | OEE không đổi từng giây; ca đã qua không đổi |
| `DowntimeNeedsClassification` | Máy vừa kết thúc một lần dừng chưa có lý do | Thêm dòng vào danh sách ngay (bỏ trùng theo `Id`) |
| `OeeSnapshotClosed` | Job vừa chốt giờ/ca của line | Làm mới để có cột giờ mới |
| `Resync` sau khi nối lại | Mất kết nối rồi có lại | SignalR không phát lại sự kiện đã lỡ |

Mọi push chạy trên thread nền nên đi qua `IUiDispatcher.Post` (bài học M4). Test dùng `FakeTimeProvider.Advance(15 s)`
để timer tick, không chờ thật.

## E. Kiểm tra trên app thật

UI Automation trong PowerShell (`System.Windows.Automation`) điều khiển app qua các `AutomationId` đã đặt sẵn: đăng
nhập `sup01`, mở menu, bỏ *Current shift*, chọn ngày 01/10/2026, ca S1, máy `L1-SMT-01`, rồi chụp màn hình.

- Ca mẫu (do `OeeDemoSeeder` dựng): **88,9 / 92,6 / 97,0 / 79,8 %**. Khớp tính tay bước 1.
- Chọn `QC-WAIT` cho đoạn 12:40 → Pareto đổi `(Down)` thành `QC-WAIT`, OEE vẫn 79,8 % (lý do vẫn phạt A).
- Rê chuột lên cột: "FDR-JAM: 25".

Bẫy khi viết script: hàm tên `Select` trùng alias `Select-Object` của PowerShell; `PlotView` không có automation peer
(không tìm theo `AutomationId` được) nên đặt chuột theo tọa độ tương đối của cửa sổ; `FakeTimeProvider` mặc định múi giờ
UTC, test phải `SetLocalTimeZone` để giờ hiển thị giống PC ở xưởng.

## F. Câu hỏi phỏng vấn

1. **"Dashboard OEE của anh có gì?"** Bốn thẻ A/P/Q/OEE, cảnh báo dữ liệu, sản lượng giờ so với mục tiêu theo cycle
   time, Pareto top 5, danh sách cần phân loại; số nào cũng truy ngược được tới đoạn dừng và dòng scan.
2. **"Dashboard tính OEE ở đâu?"** Không tính: server tính bằng một hàm thuần; WPF chỉ hiển thị. Xem cả line thì server
   cộng tổng rồi chia.
3. **"Realtime thế nào?"** Poll 15 giây cho ca đang chạy + SignalR cho sự kiện cần phản ứng ngay; nối lại thì tải lại.
4. **"Test UI ra sao?"** ViewModel thuần (kể cả biểu đồ) test bằng xUnit trên Linux; app thật kiểm bằng UI Automation
   và ảnh chụp.

## G. Đọc thêm

- OxyPlot docs: "PlotModel", "BarSeries", "Tracker" (và CHANGELOG bản 2.1: removal of ColumnSeries).
- Microsoft Learn: "UI Automation overview", "PeriodicTimer(TimeSpan, TimeProvider)".
