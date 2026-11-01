# M5 · Bước 8b — [v2] Line TV và nút Export Excel: kiến thức cần nắm

> Tài liệu học cho bước 8b của [module-05](../../spec/module-05-oee-downtime.md) (spec mục **2.8**, 2.7).
> Bước trước: [M5-step08](M5-step08-wpf-oee-dashboard.md).
> Code: `src/MiniMes.Client.Core/ViewModels/Oee/LineTvViewModel.cs`, `src/MiniMes.Kiosk.Wpf/Views/LineTvView.xaml`,
> ảnh: [m5-line-tv.png](../images/m5-line-tv.png), [m5-line-tv-offline.png](../images/m5-line-tv-offline.png).

---

## A. Nghiệp vụ: màn hình lớn ở đầu line

Dashboard BackOffice dành cho người ngồi trước PC. Tổ trưởng và công nhân **đứng trên line** cần nhìn từ 5–10 m một
câu: "giờ này đang chậm bao nhiêu so với kế hoạch, máy nào đang dừng, ai đang gọi Andon". Nhà máy Nhật gọi là
生産管理板 / アンドン表示板; nhiều MES gọi là "big screen" hay "supervisor board".

| Ô trên TV | Đọc thế nào |
| --- | --- |
| OEE ca đang chạy | Số to, không có số lẻ |
| Good + "plan n" | Đỏ khi thiếu so với kế hoạch tới giờ này |
| Andon mở | Số lượng + từng dòng máy · loại · cấp leo thang; thẻ chuyển đỏ |
| Bảng giờ | Plan / Actual / Gap với ▲/▼, không chỉ dựa vào màu |
| Dải máy | Màu **và** chữ trạng thái (Running, Down…) |

## B. Thiết kế cho TV

- **Không người dùng**: không nút, không chuột, không ai đăng nhập. App tự gọi `login-operator` bằng badge `LINE-TV`
  (role Operator, không mật khẩu, giống tài khoản của simulator), token hết hạn (401) thì tự đăng nhập lại. TV chạy
  nhiều tuần liền; token thì không sống lâu như thế.
- **Một app**: chế độ của Kiosk, bật bằng dòng lệnh `--mode=linetv --line=L1,L2`. Dòng lệnh là một nguồn cấu hình của
  .NET Generic Host, nên `Mode`, `Line` bind vào `ClientOptions` như giá trị JSON.
- **`Viewbox` + lưới 1920×1080 cố định**: thiết kế một lần, TV Full HD hay 4K đều phóng đều; khác tỷ lệ (4:3) thì có viền.
  Số chính 120 px, chữ ≥ 28 px trên lưới gốc.
- **Nhịp cập nhật**: số liệu 15 giây (nhảy từng giây thì khó đọc), Andon và trạng thái máy **ngay lập tức** (SignalR).
- **Xoay vòng line**: 20 giây/line. Join nhóm SignalR của mọi line một lần lúc đầu, nên xoay không phải join lại.
- **Offline trung thực**: mất API hoặc SignalR → dải cam "OFFLINE — data at 18:40". Số cũ vẫn hiện nhưng có nhãn; số cũ
  trông như số mới nguy hiểm hơn màn hình trống.
- **Triển khai**: tắt sleep/tắt màn hình trong Power plan của PC gắn TV, đặt app vào Startup (README).

## C. Nút Export Excel (BackOffice)

Hai nút trên dashboard gọi `GET /api/reports/oee.xlsx` và `downtime-pareto.xlsx` (bước 7) cho **ngày sản xuất đang xem**
và line đang chọn, rồi lưu bằng `IDialogService.PickFileToSave`. ViewModel không biết `SaveFileDialog` (WPF), nên test
được bằng dialog giả. File sinh ở server, nên số trong Excel khớp màn hình.

## D. Kiểm tra trên máy thật và bài học

| Kiểm | Cách | Kết quả |
| --- | --- | --- |
| Có ca để xem (hôm nay Chủ nhật) | Tạm thêm ngày tăng ca S2 vào `PlantCalendar`, xong thì xóa | TV hiện "Line L1 · Shift S2" |
| Andon hiện ngay | Raise Andon qua API, chụp sau 2 giây | Hiện sau ~3 s, trước lượt làm mới 15 s |
| Xoay line | Đọc tiêu đề qua UI Automation mỗi 5 giây | L1 → L2 (20 s) → L1 (40 s) |
| Bố cục | Cửa sổ 1920×1080 và 1280×960 | Không vỡ; 4:3 có viền |
| Offline | Tắt API | "OFFLINE — data at 18:40" |

Bài học khi kiểm UI trên máy đang có người dùng:
- `Graphics.CopyFromScreen` chụp **màn hình**, không phải cửa sổ. Kiosk bị che thì ảnh chứa cửa sổ khác. Dùng
  `PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT)` để lấy đúng nội dung cửa sổ.
- Muốn kiểm hành vi theo thời gian (xoay line), đọc văn bản qua UI Automation thay vì chụp nhiều ảnh. Ảnh chụp dễ trúng
  sai pha và kết luận nhầm (lần đầu tôi tưởng xoay line hỏng).
- Mọi thay đổi tạm trên DB dev (ngày tăng ca, Andon thử) phải dọn lại và báo rõ phần còn sót.

## E. Câu hỏi phỏng vấn

1. **"Màn hình ở line khác dashboard văn phòng thế nào?"** Đọc từ xa, không tương tác, tự đăng nhập/tự phục hồi, cập
   nhật chậm cho số và nhanh cho sự kiện, báo Offline rõ ràng.
2. **"Làm sao một bố cục chạy trên mọi TV?"** Lưới kích thước cố định trong `Viewbox`; tỷ lệ khác thì có viền.
3. **"TV chạy 24/7, token hết hạn thì sao?"** Gặp 401 thì đăng nhập lại bằng badge; tài khoản đó chỉ có role Operator.
4. **"Vì sao không làm trang web cho TV?"** Spec chốt WPF; thêm một stack front-end chỉ cho một màn hình là thừa. Có
   nhiều TV ở nhiều xưởng mà không muốn cài app thì chuyển sang trang web chỉ đọc (spec mục 4).

## F. Đọc thêm

- Microsoft Learn: "Viewbox", "Command-line configuration provider", "PrintWindow function", "UI Automation overview".
- Spec M4 2.9 (Andon, leo thang) và M5 2.8.
