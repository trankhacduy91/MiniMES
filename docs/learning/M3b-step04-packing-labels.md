# M3b · Bước 4 — Nhãn thùng và pallet: kiến thức cần nắm

> Tài liệu học cho bước 4 của [module-03b](../../spec/module-03b-packing.md) (spec mục **1.4** P6, P11; **2.2** bẫy 2).
> Bước trước: [M3b-step03](M3b-step03-pack-use-case.md).
> Code: `src/MiniMes.Application/Labels/PackingLabelBuilder.cs`, `Application/Packing/PackingLabels.cs`, `Packing/Pack.cs` (`WithLabelAsync`),
> `src/MiniMes.Domain/Planning/LabelPrintLog.cs`, `Api/Controllers/CartonsController.cs`, `PalletsController.cs`, migration `M3b_PackingLabels`.
> Test: `tests/MiniMes.UnitTests/Packing/PackingLabelTests.cs` (12), `tests/MiniMes.IntegrationTests/Packing/PackingLabelApiTests.cs` (6).

## A. Nghiệp vụ: nhãn thùng để làm gì

- Thủ kho và khách hàng **không mở thùng**: họ quét Code128 số thùng, đọc ITEM / QTY từ xa. Vì vậy nhãn 4 x 3 inch, chữ to; nhãn serial (60 x 30 mm) dán lên bo thì nhỏ.
- **Thùng lẻ phải tự khai là lẻ** (dải đen PARTIAL): ai nhìn "QTY 7" cũng biết đây không phải thùng thiếu hàng do lỗi.
- **Số lượng trên nhãn phải khớp thực tế (P11).** Unpack 1 con rồi bù 1 con: số lượng vẫn 10 nhưng nhãn cũ có thể đã rách hoặc bị gỡ khi mở thùng. Hệ thống in lại và ghi `PrintNo = 2` với lý do "Carton closed again with 10 pcs.". Nhờ vậy, khi một thùng có hai nhãn, ta biết nhãn nào in sau và vì sao.
- Nhãn **in sau khi thùng đã thật sự đóng trong DB**, không in trước.

## B. Kỹ thuật 1: một bảng log cho ba loại nhãn

| Cột | Nhãn serial (M2) | Nhãn thùng | Nhãn pallet |
| --- | --- | --- | --- |
| `LabelType` | `Serial` | `Carton` | `Pallet` |
| `TargetKey` (mã in thành barcode) | số serial | số thùng | số pallet |
| `SerialId`, `SerialNo` | có | NULL | NULL |
| `WorkOrderId` | có | WO của thùng | NULL (pallet có thể gom nhiều WO) |

- `PrintNo` đếm theo `(LabelType, TargetKey)`; unique index trên ba cột này làm hai lần in lại cùng lúc ra 409, không bao giờ chung số.
- **CHECK constraint** giữ hai kiểu dòng không lẫn nhau: dòng `Serial` phải có serial và `SerialNo = TargetKey`; dòng thùng/pallet không được trỏ tới serial. Đây là cách giữ đúng dữ liệu khi một bảng chứa nhiều "loại" dòng (single-table với cột phân loại).
- Bạn chọn **giữ `SerialNo`** (đúng chữ spec, code M2 không đổi), đổi lại dòng serial có `SerialNo` trùng `TargetKey`. CHECK bảo đảm hai cột đó không bao giờ lệch nhau.

## C. Kỹ thuật 2: migration có dữ liệu cũ

EF sinh `AddColumn(... nullable: false, defaultValue: "")`. Với các dòng nhãn serial đã có, điều đó nghĩa là `LabelType = ''`, `TargetKey = ''`: unique index mới vỡ ngay (mọi dòng PrintNo 1 cùng key `''`), CHECK cũng vỡ. Phải tự chèn:

```csharp
migrationBuilder.Sql("UPDATE dbo.LabelPrintLogs SET LabelType = 'Serial', TargetKey = SerialNo;");   // trước CreateIndex / AddCheckConstraint
```

Down cũng cần xử lý tay: bảng M2 không chứa được dòng nhãn thùng (SerialId NOT NULL), nên phải xóa những dòng đó trước. Đã chạy Up → Down → Up trên DB dev. Bài học cũ (incidents.md, M6) vẫn đúng: **đọc migration EF sinh ra, đừng tin nó biết dữ liệu của bạn.**

## D. Kỹ thuật 3: in sau commit

```csharp
await using (var tx = await db.BeginTransactionAsync(ct))
{
    response = await PackAsync(tx, ...);
    if (ok) await tx.CommitAsync(ct);
}
if (response.CartonClosed) response = await WithLabelAsync(response, ct);   // log nhãn + ZPL, ngoài transaction
```

- In trong transaction rồi commit lỗi → nhãn giấy đã ra cho một thùng không tồn tại (spec 2.2 bẫy 2).
- Ngược lại, nếu in lỗi sau commit thì **không** hoàn tác pack: thùng đã đóng là sự thật. API vẫn trả 200, message báo "Label NOT printed", operator in lại qua `POST /api/cartons/{no}/labels/print`.
- "In" ở dự án này = sinh ZPL + ghi log; ZPL trả về cho kiosk gửi máy in (giống M2). Máy in thật nằm ở trạm, không nằm ở server.

## E. Kiểm tra (spec bước 4)

| Spec yêu cầu | Cách kiểm |
| --- | --- |
| Xem trước bằng Labelary | Render thật nhãn thùng lẻ và nhãn pallet qua `api.labelary.com` (khổ `4x3`): barcode vừa khổ, dải PARTIAL hiện. API preview trong test dùng `FakeLabelPreview`, kiểm tra khổ `4x3` được truyền |
| Đóng thùng → 1 dòng log | `Closing_a_carton_prints_its_label_once` |
| In lại → `PrintNo = 2` | `Reprint_needs_a_reason_and_gets_print_number_two`, `Carton_closed_again_after_an_unpack_prints_its_label_again` |

## F. Câu hỏi phỏng vấn

1. **"Làm sao biết một nhãn là bản in lại?"** Mỗi lần in ghi một dòng, `PrintNo` tăng theo mã nhãn, unique index chống trùng số; in lại bắt buộc có lý do.
2. **"Vì sao in nhãn sau commit?"** Nhãn vật lý không rollback được. Thứ tự đúng: DB chắc chắn trước, vật lý sau; vật lý lỗi thì làm lại (idempotent, có log).
3. **"Thêm cột NOT NULL vào bảng đã có dữ liệu?"** Thêm với default, chép dữ liệu thật bằng `UPDATE`, rồi mới tạo index/constraint; kiểm cả Down.
