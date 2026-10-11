# Mini MES — Tài liệu học theo module

> **Bản mới:** [spec-v2.md](spec-v2.md) so sánh spec với các MES mã nguồn mở và chốt phạm vi cuối (thêm M3b Packing, M4b Edge Gateway OPC UA/Modbus, IQC/FAI; bỏ M9, Gantt). Khi hai bản khác nhau, theo `spec-v2.md`.

Bộ tài liệu tự học đi kèm `spec.md` và `spec-v2.md`. Mọi file module đã được cập nhật theo spec v2 (chỗ đổi đánh dấu **[v2]**). Mỗi file gồm 5 phần: nghiệp vụ MES, kiến thức lập trình, kế hoạch từng bước, lý do chọn giải pháp, và checklist hoàn thành kèm câu hỏi tự kiểm tra/phỏng vấn.

| Thứ tự | File | Nội dung chính | Kỹ thuật mới học | Ước lượng |
| --- | --- | --- | --- | --- |
| 0 | [module-00-foundation.md](module-00-foundation.md) | MES là gì, vai trò người dùng, phân quyền, audit, Site/Area/Line | Clean Architecture, Docker, Serilog, JWT, Testcontainers, WPF + MVVM từ gốc, .resx, trigger chặn sửa audit | ~25 giờ |
| 1 | [module-01-master-data.md](module-01-master-data.md) | Item, BOM đa cấp, Routing + SOP, ca làm việc, lịch nghỉ, mã lỗi, lý do dừng | MediatR/CQRS, FluentValidation, DFS, recursive CTE, DataGrid/TreeView, import Excel (ClosedXML) | ~30 giờ |
| 2 | [module-02-work-order.md](module-02-work-order.md) | Vòng đời lệnh sản xuất, release, snapshot, serial, nhãn, ưu tiên, trạng thái FAI | State machine, concurrency, SEQUENCE, ZPL | ~27 giờ |
| 3 | [module-03-shop-floor-execution.md](module-03-shop-floor-execution.md) | Scan tại trạm, route check, cổng FAI, NG/Repair, WIP, FPY, SOP tại trạm | Idempotency, index & execution plan, WPF kiosk, WebView2 | ~30 giờ |
| 4 | [module-04-machine-connectivity.md](module-04-machine-connectivity.md) | Trạng thái máy, bộ đếm, tag quy trình, Andon có leo thang | MQTT + Unified Namespace, MQTTnet, BackgroundService, SignalR, simulator | ~26 giờ |
| 4b | [module-04b-edge-gateway.md](module-04b-edge-gateway.md) | Gateway đọc PLC, store-and-forward, Offline vs Down | OPC UA (server + client), Modbus TCP, SQLite outbox, Worker Service | ~24 giờ |
| 5 | [module-05-oee-downtime.md](module-05-oee-downtime.md) | OEE, Six Big Losses, Pareto lý do dừng, Line TV | Tính toán thời gian/ca, PeriodicTimer, OxyPlot, xuất Excel | ~25 giờ |
| 6 | [module-06-material-traceability.md](module-06-material-traceability.md) | IQC, lot, nạp vật tư, genealogy, truy xuất ngược/xuôi tới pallet | Mô hình đồ thị, recursive CTE, domain event | ~28 giờ |
| 3b | [module-03b-packing.md](module-03b-packing.md) | Đóng gói serial → thùng → pallet, xuất hàng | Filtered unique index, cập nhật có điều kiện, trạng thái suy ra | ~10 giờ |
| 7 | [module-07-quality.md](module-07-quality.md) | FAI, SPC (I-MR) thủ công và tự động từ máy, Cpk, NCR, Hold/Release | Thống kê, rule engine, control chart | ~33 giờ |

Tổng ≈ 258 giờ cho các module trên (+10 giờ polish ≈ 268 giờ, khoảng 27 tuần ở nhịp 10 giờ/tuần). Thứ tự làm theo bảng (M3b sau M6). Mốc dừng an toàn và mức ưu tiên: xem [spec-v2.md](spec-v2.md) mục B4, B7.

## Quy ước chung cho mọi module

- **[chốt 10/2026]** **.NET 10 (LTS)** cho toàn dự án (`net10.0`, WPF `net10.0-windows`), SDK ghim bằng `global.json`, solution `MiniMes.slnx`. Snippet nào ghi .NET 8 là viết trước khi đổi; API dùng trong tài liệu đều có từ .NET 8 nên vẫn đúng.
- **[chốt 10/2026]** API dùng **Controller** (`Api/Controllers/XxxController.cs`), không dùng Minimal API. Swagger UI đọc OpenAPI có sẵn của ASP.NET Core (Module 0, mục 2.5b).
- **[chốt 10/2026]** Ánh xạ lỗi: 400 validation, 404 không tìm thấy, 409 `ConflictException` (trùng, concurrency), 422 `DomainException` (vi phạm quy tắc), 500 còn lại. Scan bị chặn ở M3 trả 200 kèm `reasonCode`.
- **[chốt 10/2026]** CI bằng GitHub Actions, branch `main`.

- Đường dẫn API trong tài liệu viết rút gọn; khi code, đặt tất cả dưới tiền tố `/api` (ví dụ `/api/stations/{id}/scan`).
- Một thư viện biểu đồ duy nhất: OxyPlot (Module 5 giới thiệu, Module 7 dùng tiếp).
- MediatR: dùng bản 12.x (Apache 2.0) như Module 1 hướng dẫn; từ v13 thư viện chuyển sang license thương mại.
- Snippet trong tài liệu là minh họa, chưa được biên dịch. Chỗ nào ghi "kiểm tra README nếu compile lỗi" là API có thể khác theo phiên bản thư viện (MQTTnet v4/v5, xUnit v2/v3, OxyPlot, CommunityToolkit.Mvvm): khi gặp lỗi, đối chiếu tài liệu chính thức của đúng phiên bản bạn cài.
- Mỗi module chỉ dạy kỹ thuật mới của nó; kỹ thuật đã dạy ở module trước được tham chiếu "(xem Module X, mục Y)", nên hãy học theo thứ tự.
