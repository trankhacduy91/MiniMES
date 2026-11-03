# M6 · Bước 10 (b) — Linh kiện thay ở Repair vào hồ sơ truy xuất: kiến thức cần nắm

> Tài liệu học cho bước 10 (tùy chọn, hướng b) của [module-06](../../spec/module-06-material-traceability.md) (spec M3 mục 1.5 "thay linh kiện nhưng không ghi", M6 R7, R9, R11).
> Bước trước: [M6-step09](M6-step09-performance.md).
> Code: `src/MiniMes.Application/Execution/Repair.cs`, `src/MiniMes.Domain/Material/MaterialVerifier.cs` (`VerifyReplacement`), `src/MiniMes.Domain/Material/SerialComponent.cs` (`FromRepair`), `src/MiniMes.Infrastructure/Traceability/TraceQueries.cs`.
> Test: `tests/MiniMes.IntegrationTests/Material/RepairReplacementTests.cs`.

## A. Nghiệp vụ: lỗ hổng mà bước 10b vá

Board `TRACE-0150` fail ở Test vì IC U3 hỏng. Kỹ thuật viên ở trạm Repair gỡ U3, hàn một con IC mới lấy từ reel `L-MCU-07`, board retest OK rồi giao cho khách.

Trước bước này, M3 chỉ ghi một dòng chữ tự do `ComponentReplaced = "U3 IC-MCU-QFN lot L2610-07"`. Hai câu hỏi truy xuất vì vậy trả lời sai:

| Câu hỏi | Trước bước 10b | Sau bước 10b |
| --- | --- | --- |
| Ngược: board này dùng lot nào? | Thiếu `L-MCU-07` | Có `L-MCU-07`, ghi "replaced at repair by EMP…" |
| Xuôi: lot `L-MCU-07` lỗi, vào board nào? | **Sót** board đã sửa | Có board đã sửa |

Dòng chữ tự do không truy vấn được, và có thể gõ sai mã lot. Vì vậy kỹ thuật viên **scan nhãn reel**, giống operator nạp reel lên máy.

## B. Thiết kế

1. **Một dòng `SerialComponent` cho linh kiện thay.**
   - Không tạo bảng mới. Truy vấn xuôi và ngược của bước 7 vốn đã đọc bảng này, nên tự động thấy linh kiện thay.
   - Nguồn của dòng: `ScanLogId` (scan OK ở trạm) **hoặc** `RepairLogId` (sửa chữa). CHECK `CK_SerialComponents_OneSource` bắt buộc đúng một trong hai, để truy vấn luôn join được ra người làm.
   - Migration chỉ đổi metadata (cho cột thành NULL được, thêm cột NULL được). Trên SQL Server việc này tức thời, kể cả với 100 triệu dòng.
2. **Kiểm tra lot như lúc nạp, nhưng chỉ phần chất lượng** (`MaterialVerifier.VerifyReplacement`, hàm thuần):
   - Giữ lại: lot phải có thật (400 trên field), thuộc BOM snapshot của WO, không Hold, đã qua IQC, còn hạn (422).
   - Bỏ đi:
     - Kiểm trạm: Repair không nằm trong routing.
     - FIFO: chỉ lấy một linh kiện bằng tay.
     - Lot đã `Consumed` trên sổ vẫn cho qua, vì số còn lại chỉ là lý thuyết mà linh kiện đang nằm trong tay kỹ thuật viên. Cùng lý do với gate ở bước 5.
3. **Một transaction:** `RepairLog` → `SerialComponent` (Qty = 1) → trừ lot bằng đúng câu `UPDATE` nguyên tử của bước 5, đã tách thành `Consumption.SubtractAsync` để hai nơi dùng chung.
   - Nếu có lỗi thì không ghi gì: board vẫn ở trạng thái `Repair`, chưa có hồ sơ sửa.
4. **Bắt buộc lot khi cờ `Material:EnforceLoadedMaterial` bật.** Đây là cùng một công tắc "M6 đang chạy" với gate scan. Cờ tắt thì quy trình M3 không đổi.

## C. Hai ca biên đáng kể

- **Thay hai lần cùng một lot trên một board** (U3 thay xong, fail lại, lại thay từ cùng reel):
  - Unique index R9 `(Serial, Station, Item, Lot)` chỉ cho một dòng. Dòng đó đã chứng minh mối liên kết board ↔ lot, nên không thêm dòng thứ hai.
  - Số lượng lot vẫn trừ hai lần, và cả hai lần sửa đều có `RepairLog`.
  - Có test đột biến: bỏ phần kiểm "đã ghi chưa" thì lần thay thứ hai trả 409.
- **Linh kiện cũ bị gỡ ra vẫn nằm trong hồ sơ.**
  - `SerialComponents` là bảng chỉ thêm (R11), không xóa được dòng của linh kiện cũ.
  - Hệ quả: truy xuất xuôi từ lot cũ vẫn ra board này, tức là **rộng hơn thực tế**. Đây là hướng chấp nhận được trong containment: thà dư còn hơn sót.
  - Muốn chính xác hơn thì thêm một bảng "linh kiện đã gỡ" (không xóa, chỉ ghi thêm). Hiện chưa làm.

## D. Lỗi gặp khi làm: Down của migration va vào trigger

Khi chạy thử Down trên DB dev, migration lỗi "SerialComponents is append-only".

- **Nguyên nhân:** EF sinh `UPDATE … SET ScanLogId = 0 WHERE ScanLogId IS NULL` trước khi đặt lại `NOT NULL`. Trigger chặn **câu lệnh** `UPDATE`, kể cả câu không chạm dòng nào.
- **Cách sửa:** bỏ `defaultValue` là chưa đủ (đọc script thấy EF vẫn sinh `UPDATE`); thay `AlterColumn` của Down bằng một câu `ALTER COLUMN` viết tay (`migrationBuilder.Sql`).
- **Bài học:** đọc SQL mà migration sinh ra, không chỉ đọc code C#. Ghi ở `docs/incidents.md`.

## E. Câu hỏi phỏng vấn

1. **"Board đã sửa và thay linh kiện, truy xuất có còn đúng không?"**
   - Có. Linh kiện thay được ghi như một lần tiêu hao: `SerialComponent` có nguồn là `RepairLog`, cùng transaction với hồ sơ sửa.
   - Kỹ thuật viên scan nhãn reel và hệ thống kiểm lot như lúc nạp: không cho dùng reel đang Hold hoặc chưa qua IQC để sửa.
2. **"Linh kiện cũ đã gỡ thì sao?"**
   - Vẫn nằm trong hồ sơ, vì bảng chỉ thêm. Truy xuất xuôi từ lot cũ sẽ ra cả board này, tức là rộng hơn thực tế, nhưng không bao giờ sót.
   - Nếu khách cần chính xác hơn, thêm một bản ghi "đã gỡ" chứ không xóa dòng cũ.
3. **"Sao không lưu lot vào cột text của RepairLog?"**
   - Text không truy vấn được và có thể gõ sai.
   - Một dòng trong bảng cạnh `SerialComponent` thì hai truy vấn đệ quy có sẵn tự động thấy, có khóa ngoại tới lot, và có index.
