# M3b · Bước 1 — Bảng đóng gói (aggregation) và filtered unique index: kiến thức cần nắm

> Tài liệu học cho bước 1 của [module-03b](../../spec/module-03b-packing.md) (spec mục **1.2**, **1.4**, **2.1**).
> Code: `src/MiniMes.Domain/Packing/*`, `src/MiniMes.Infrastructure/Persistence/Configurations/PackingConfigurations.cs`, migration `M3b_Packing`.
> Test: `tests/MiniMes.IntegrationTests/Packing/PackingSchemaTests.cs`, `tests/MiniMes.UnitTests/Packing/PackingEntityTests.cs`.

## A. Nghiệp vụ: aggregation là gì

Ở M3, scan bước PACK chỉ đổi serial thành `Completed`. Ngoài đời, thành phẩm còn được xếp vào **thùng** (ví dụ 10 board), các thùng lại xếp lên **pallet** (ví dụ 20 thùng), rồi pallet được giao cho khách. MES phải ghi lại quan hệ chứa này: serial ∈ thùng ∈ pallet. Việc đó gọi là **aggregation** (packing hierarchy).

Lý do cần ghi:

- Khách báo lỗi theo **số thùng hoặc số pallet**, không theo từng serial.
- Truy xuất xuôi ở M6 trả lời được câu "các board nghi lỗi **đang ở đâu**": còn trong line, đã vào thùng, đã lên pallet, hay đã giao.
- Mỗi thùng phải đúng số lượng. Thùng 11 con là lỗi giao hàng, có thể bị khách phạt.

## B. Bốn bảng

| Bảng | Vai trò |
| --- | --- |
| `PackingSpecs` | Quy cách của từng item: số con/thùng, số thùng/pallet. Là master data nên có audit |
| `Cartons` | Thùng: số thùng, WO, trạm, `Qty` hiện tại, Open/Closed/Shipped, thùng lẻ, thuộc pallet nào |
| `CartonSerials` | "Serial X đang (hoặc đã từng) ở thùng Y". Khi unpack, dòng **không bị xóa** mà chỉ được điền thêm ai lấy ra, lúc nào, vì sao |
| `Pallets` | Pallet: số pallet, item, trạm, số thùng, Open/Closed/Shipped, số phiếu giao |

**Khóa:** dùng `Id`, giống M6 (bạn đã duyệt). Mã thùng và mã pallet in trên nhãn là cột unique riêng. Lợi ích: truy xuất xuôi của M6 join thẳng `SerialId`.

**Trạng thái Hold không có cột riêng.** Một thùng (hay pallet) được coi là Hold khi có serial Hold bên trong; giá trị này được tính bằng `EXISTS` ở bước 5. Nếu lưu thêm cột thì có hai nguồn sự thật, và sớm muộn hai nguồn sẽ lệch nhau.

## C. Filtered unique index: "tối đa một **hiện tại**, giữ hết **lịch sử**"

Đây là kỹ thuật chính của bước này. Unique index thường cấm hai dòng có cùng `SerialId`. Như vậy một serial không thể có dòng của thùng cũ (đã unpack) cùng lúc với dòng của thùng mới, tức là mất lịch sử. Filtered unique index chỉ áp ràng buộc cho các dòng thỏa bộ lọc:

```sql
CREATE UNIQUE INDEX UX_CartonSerials_LivePerSerial ON CartonSerials (SerialId) WHERE UnpackedAt IS NULL;
CREATE UNIQUE INDEX UX_Cartons_OpenPerStationWo   ON Cartons (StationId, WorkOrderId) WHERE Status = 'Open';
```

- Dòng thứ nhất là quy tắc P3: một serial nằm ở tối đa một thùng còn hiệu lực.
- Dòng thứ hai là quy tắc P4: mỗi trạm có tối đa một thùng đang mở cho mỗi WO. Hai lần scan đầu tiên đến cùng lúc thì chỉ một thùng được mở; request còn lại nhận 409.
- M6 đã dùng cùng kỹ thuật này: mỗi trạm có tối đa một reel đang nạp cho mỗi item.

Ba bẫy cần nhớ:

1. Truy vấn phải viết đúng điều kiện của bộ lọc (`AND UnpackedAt IS NULL`) thì SQL Server mới dùng index đó.
2. Trạng thái được lưu dạng **chuỗi**, để bộ lọc đọc được như SQL (`[Status] = 'Open'`). Nếu đổi sang lưu số thì phải sửa bộ lọc bằng tay.
3. Câu lệnh ghi vào bảng có filtered index cần `QUOTED_IDENTIFIER ON`. SqlClient bật sẵn; `sqlcmd` thì phải thêm `-I` (đã gặp ở M6 bước 9).

## D. Số thùng, số pallet: SEQUENCE

Dùng SEQUENCE `CartonSeq` và `PalletSeq`, giống số WO, số serial, số lot. Không dùng `MAX + 1`, vì khi hai trạm đóng thùng cùng lúc, cả hai sẽ đọc cùng một giá trị MAX. Định dạng số (tiền tố, ngày) để bước 2.

## E. Câu hỏi phỏng vấn

1. **"Một serial bị đóng nhầm thùng, anh lấy ra thế nào mà vẫn giữ lịch sử?"** Không xóa dòng. Unpack điền `UnpackedAt/By/Reason`. Filtered unique index chỉ ràng buộc các dòng chưa unpack, nên serial vào được thùng mới, còn dòng của thùng cũ vẫn ở lại làm bằng chứng.
2. **"Sao không lưu cờ Hold trên thùng?"** Hold có một nguồn sự thật duy nhất là serial. Thùng và pallet chỉ suy ra từ đó, nên không bao giờ lệch.
3. **"Đảm bảo thế nào là không có hai thùng đang mở cho cùng một WO ở cùng một trạm?"** Bằng filtered unique index trong DB, không bằng `lock` trong bộ nhớ. DB là lưới an toàn cuối, kể cả khi có nhiều instance API.
