# M6 · Bước 1 — Mô hình dữ liệu lot, nạp vật tư, genealogy: kiến thức cần nắm

> Tài liệu học cho bước 1 của [module-06](../../spec/module-06-material-traceability.md) (spec mục **1.2**, **2.1**, **2.3**, quy tắc R1–R16).
> Code: `src/MiniMes.Domain/Material/*.cs`, `src/MiniMes.Infrastructure/Persistence/Configurations/MaterialConfigurations.cs`,
> migration `M6_MaterialTraceability`. ERD: [docs/erd-material.md](../erd-material.md).

---

## A. Nghiệp vụ: ba tầng định danh

| Tầng | Ví dụ | Vì sao ở tầng đó |
| --- | --- | --- |
| Item | `CAP-100N-0402` | Loại hàng, không phải vật thể |
| Lot | Reel `L-CAP-260915-03` (10.000 con) | Không dán mã từng con tụ; lot lỗi thì cả lot đáng ngờ |
| Serial | Bo `PCBA2611010006519` | Bo đắt, khách trả về từng cái |

**Supplier lot ≠ LotNo.** Nhà cung cấp gọi theo `MUR-2609-A17`, một lần giao nhiều reel. MES sinh `LotNo` cho **từng reel**
(1 reel = 1 lot), nên nhà cung cấp báo lỗi thì truy xuất xuôi phải bắt đầu được từ supplier lot.

**Cầu nối:** `SerialComponent` = "serial X đã dùng lot Y", ghi **lúc scan OK**, không đoán lại theo thời gian lúc truy vấn.

## B. Sáu bảng và vai trò

| Bảng | Một dòng là | Đặc điểm |
| --- | --- | --- |
| `MaterialLots` | Một reel | Không xóa; `Status` 0–4; audited (Hold, IQC là quyết định của người) |
| `MaterialLoads` | Một lần gắn reel lên trạm | Mỗi (trạm, item) tối đa một dòng đang mở |
| `SerialComponents` | Serial dùng lot ở trạm | Bảng lớn nhất; insert-only |
| `LotGenealogy` | Lot cha → lot con | Chia reel, bán thành phẩm theo lô; insert-only |
| `MaterialVerificationLogs` | Một lần thử nạp | Ghi cả lần bị từ chối (R13) |
| `IqcRecords` | Một lần kiểm đầu vào cho supplier lot | [v2], insert-only |

## C. Kỹ thuật: để DB giữ quy tắc

- **Filtered unique index** `(StationId, ItemId) WHERE UnloadedAt IS NULL`: hai kiosk nạp cùng lúc thì một bên 409. Nó cũng
  là index mỗi scan OK dùng để hỏi "lot nào đang nạp", nhờ `INCLUDE (LotId)` mà không cần đọc bảng.
- **CHECK constraint** `0 ≤ RemainingQty ≤ Qty`: bước 5 trừ tồn bằng một câu `UPDATE` thô (bỏ qua entity); DB vẫn chặn số âm.
- **Trigger `INSTEAD OF UPDATE, DELETE`**: hồ sơ truy xuất không sửa được, kể cả bằng SSMS (phải DROP trigger trước,
  hành động đó lộ rõ). Cùng mẫu `ScanLogs` và `AuditLog` (ADR-004).
- **Khóa số thay vì mã chuỗi** (quyết định của bạn ở bước này): `SerialComponents` ~100 triệu dòng sau 5 năm. `LotId int` +
  `SerialId bigint` (12 byte) thay cho hai chuỗi (~50 byte) ở mỗi dòng **và** ở mỗi index. Đổi lại, truy vấn join thêm
  `MaterialLots`/`Serials` một lần ở cuối để in mã.
- **varchar cho mã**: tham số `nvarchar` so với cột `varchar` làm SQL Server phải `CONVERT_IMPLICIT` và quét. Đã gặp ở M3.
- **Không FK tới `Serials`/`Stations`/`ScanLogs`** ở `SerialComponents` (giống `ScanLogs`): mỗi FK là một lần kiểm tra trên
  lệnh INSERT nóng nhất. Giữ FK tới `MaterialLots`: dòng tiêu hao trỏ vào lot không tồn tại là truy xuất hỏng.
- **Đọc migration EF sinh ra** trước khi chạy (bài học M5): lần này không có filter ẩn nào; INCLUDE ra đúng
  `SqlServer:Include`.

## D. Hướng cạnh genealogy (dễ nhầm nhất)

`LotGenealogy(ParentLotId, ChildLotId)` = "vật liệu của Parent đã dùng để làm Child". Xuôi: Parent → Child. Ngược:
Child → Parent. Viết ngay trên entity, và test bằng dữ liệu hai cấp ở bước 6–7.

## E. Câu hỏi phỏng vấn

1. **"Vì sao NVL theo lot, thành phẩm theo serial?"** Chi phí và khả thi; cầu nối là bảng tiêu hao serial–lot.
2. **"Làm sao đảm bảo mỗi scan biết đúng lot đang dùng?"** Một load đang mở cho mỗi (trạm, item), ép bằng filtered unique
   index, không bằng code kiểm tra trước (hai request song song đều qua được bước kiểm tra).
3. **"Hồ sơ truy xuất bị sửa thì sao?"** Insert-only ở tầng DB (trigger), sai thì ghi bổ sung/NCR, không sửa dòng cũ.
4. **"Bảng 100 triệu dòng thiết kế thế nào?"** Khóa hẹp, index phủ theo từng đường truy cập, ít FK trên đường ghi nóng;
   sau đó mới tính partition theo tháng và archive.
