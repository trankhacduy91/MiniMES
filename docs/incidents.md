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
