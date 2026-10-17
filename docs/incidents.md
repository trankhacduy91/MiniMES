# Incidents

Lỗi đáng nhớ (khó tìm nguyên nhân, có bài học) để dùng khi phỏng vấn.

## 2026-10-11 — `Login failed for user 'sa'` dù mật khẩu đúng (M0 bước 3)

- **Triệu chứng:** `dotnet ef database update` và API báo `Error 18456, Login failed for user 'sa'`. Cùng mật khẩu đó, `sqlcmd` chạy *bên trong* container lại đăng nhập được.
- **Nguyên nhân:** máy dev có SQL Server Express cài sẵn (service `MSSQL$SQLEXPRESS`) cũng nghe cổng 1433. `netstat -ano` thấy hai tiến trình cùng LISTENING `0.0.0.0:1433`: `sqlservr.exe` (Express) và `com.docker.backend.exe`. Kết nối từ host tới `localhost,1433` rơi vào SQL Express, nơi `sa` có mật khẩu khác.
- **Cách tìm:** so kết quả "đăng nhập trong container thì được, từ host thì không" → nghi sai đích kết nối, không phải sai mật khẩu → `netstat -ano | findstr :1433` + `tasklist /FI "PID eq ..."`.
- **Cách sửa:** cổng host của container đọc từ `MSSQL_PORT` trong `.env` (mặc định 1433, máy này đặt 14333); connection string trỏ `localhost,14333`.
- **Bài học:** "Login failed" không chứng minh mật khẩu sai; kiểm tra *đang nói chuyện với server nào* trước. Hai process cùng bind một cổng trên Windows vẫn có thể xảy ra.

## 2026-10-11 — CorrelationId mất ở response lỗi và ở file log (M0 bước 4)

- **Triệu chứng:** response 422/500 không có header `X-Correlation-Id`, `correlationId` trong ProblemDetails rỗng; file log hiện `-` ở cột CorrelationId dù request có gửi id.
- **Nguyên nhân (2 lỗi riêng):**
  1. `ExceptionHandlerMiddleware` gọi `Response.Clear()` trước khi chạy `IExceptionHandler`, xóa header middleware đã đặt. Handler đọc lại từ `Response.Headers` nên được chuỗi rỗng.
  2. `Enrich.WithProperty("CorrelationId", "-")` đặt *trước* `Enrich.FromLogContext()`. Cả hai chỉ thêm thuộc tính khi chưa có, nên giá trị mặc định thắng.
- **Cách tìm:** không tin console; mở file `logs/minimes-*.log` và `grep` theo id của từng request, đối chiếu với response.
- **Cách sửa:** middleware lưu id vào `HttpContext.Items`, handler đặt lại header + `correlationId` từ đó; đảo thứ tự enricher (`FromLogContext` trước, `WithProperty` mặc định sau).
- **Bài học:** kiểm tra đầu-cuối (response ↔ log file) cho cross-cutting concern; thứ tự middleware/enricher quyết định kết quả và build xanh không bắt được.

## 2026-10-12 — README chạy được trên máy dev nhưng không chạy được trên bản clone mới (M0 bước 11)

- **Triệu chứng:** làm đúng README trên một bản clone mới, `dotnet ef database update` báo `NETSDK1004: Assets file ... project.assets.json not found`.
- **Nguyên nhân:** `dotnet ef` đọc metadata của project Infrastructure (cần `obj/project.assets.json`, sinh ra lúc restore) trước khi tự build. Trên máy dev project luôn đã được restore từ trước, nên lỗi không bao giờ hiện.
- **Cách tìm:** nghiệm thu đúng nghĩa: clone vào thư mục **tên khác** (để Docker tạo volume và DB mới), sao lưu user-secrets rồi đặt lại theo README, tắt stack dev đang chạy, và làm từng lệnh một như người lạ.
- **Cách sửa:** README thêm `dotnet build` trước `dotnet ef database update`. Thêm vào mục "Gặp lỗi": mật khẩu SA lưu trong volume ở lần chạy đầu; user-secrets dùng chung giữa các bản clone trên một máy.
- **Bài học:** máy dev có rất nhiều trạng thái ẩn (package đã restore, secrets, volume DB, container đang chạy). Tài liệu "cách cài đặt" chỉ đáng tin khi đã chạy thử trên môi trường sạch; đó cũng là lý do CI chạy trên máy sạch.

## 2026-10-13 — Quy tắc Domain so khóa ngoại trước khi EF kịp gán (M1 bước 7)

- **Triệu chứng (bắt được trước khi chạy, xác nhận bằng mutation):** tạo trạm mới kèm `WorkCenterId` của **đúng** line vẫn bị 422 "Work center is on another line".
- **Nguyên nhân:** `line.AddStation(...)` chỉ thêm `Station` vào collection `Line.Stations`; EF chỉ điền `Station.LineId` lúc `SaveChanges` (relationship fix-up). Quy tắc `Station.AssignWorkCenter` chạy *trước* `SaveChanges` nên thấy `LineId = 0`.
- **Cách tìm:** đọc lại luồng tạo entity con qua aggregate cha và hỏi "lúc quy tắc chạy, giá trị này đã có chưa?"; thử lại bằng mutation (truyền 0 như cũ) → integration test đỏ đúng chỗ.
- **Cách sửa:** `Line.AddStation` truyền `Id` của line vào constructor của `Station`, nên `LineId` có ngay trong bộ nhớ.
- **Bài học:** với EF, khóa ngoại của entity mới thêm qua navigation chưa có giá trị cho tới `SaveChanges`. Quy tắc Domain cần dữ liệu đó thì entity phải tự giữ nó, đừng dựa vào ORM. Unit test Domain không bắt được lỗi này (không có EF); integration test chạy qua API thật thì bắt được.

## 2026-10-17 — Lưới trống sau khi Save vì DTO là `record` (M1 bước 8, WPF)

- **Triệu chứng (bắt bởi unit test ViewModel):** trong Routing editor, bấm Save xong thì lưới các bước trống trơn dù server đã lưu đúng. BOM editor bị y hệt khi lưu mà số dòng không đổi.
- **Nguyên nhân:** sau khi lưu, ViewModel xóa lưới, tải lại danh sách version và gán lại `SelectedVersion`. DTO là `record` nên bản mới tải về **bằng giá trị** với bản cũ. Setter do CommunityToolkit sinh ra so bằng `EqualityComparer<T>.Default`, thấy "không đổi" nên không gọi `OnSelectedVersionChanged`, và các bước không bao giờ được tải lại.
- **Cách tìm:** test "di chuyển bước rồi Save" đọc lưới sau khi lưu và thấy chuỗi rỗng; fake trả về một instance `record` mới nhưng cùng giá trị, đúng như HTTP thật làm.
- **Cách sửa:** gán `SelectedVersion = null` trước khi chọn lại, để luôn có một lần đổi thật. Thêm test hồi quy cho cả hai màn hình.
- **Bài học:** `record` so sánh theo giá trị là tính năng; nhưng các thứ dựa trên "giá trị có đổi không" (setter của MVVM toolkit, `SelectedItem` của WPF, `distinct`) sẽ coi hai lần tải là một. Khi cần phản ứng với *lần tải mới* chứ không phải *giá trị mới*, hãy tạo thay đổi tường minh. Fake trong test nên trả instance mới như mạng thật, không trả lại cùng một object.
