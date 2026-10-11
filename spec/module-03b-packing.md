# Module 3b — Packing: đóng gói serial → thùng → pallet (aggregation)

> Module này khép lại luồng sản xuất: thành phẩm đã qua hết các bước được scan vào **thùng (carton)**, thùng đầy thì tự đóng và in nhãn thùng, các thùng được gom thành **pallet**, pallet được đánh dấu đã giao. Kết quả là truy xuất đi được tới tận đơn vị giao hàng: "serial X nằm trong thùng nào, pallet nào, đã giao chưa".
> Sau khi xong bạn có: bảng `PackingSpec`, `Carton`, `CartonSerial`, `Pallet`; API `POST /stations/{id}/pack` gọi lại đúng use case Scan của M3 trong cùng transaction; tự đóng thùng khi đủ số lượng (an toàn khi đồng thời); nhãn thùng/pallet ZPL (dùng lại M2); unpack/đóng thùng lẻ có quyền supervisor; màn hình Packing trên Kiosk.
> Thời lượng ước tính: ~10 giờ (khoảng 6 buổi 1,5–2 giờ).
> Module tiên quyết: M2 (`Serial`, `ZplLabelBuilder`, `LabelPrintLog`, SEQUENCE), M3 (use case Scan, route check, Kiosk). Làm trước M7 trong giai đoạn MVP-6; M6 dùng dữ liệu của module này cho truy xuất xuôi.
>
> Module mới theo [spec-v2](spec-v2.md) (mục A2 #9, B5 M3b).

---

## 1. Kiến thức nghiệp vụ MES

### 1.1 Bức tranh tổng thể

Ở M3, bước cuối của routing là `PACK`: scan OK thì serial thành `Completed`. Nhưng ngoài đời "đóng gói" không chỉ là một lần scan. Sản phẩm được cho vào túi chống tĩnh điện, xếp vào thùng carton (ví dụ 10 bo/thùng), thùng dán nhãn, các thùng xếp lên pallet (ví dụ 20 thùng/pallet), pallet quấn màng, dán nhãn, chuyển kho thành phẩm rồi xuất cho khách.

MES phải ghi lại quan hệ **chứa** (containment) này, gọi là **aggregation** hay **packing hierarchy**:

```
Pallet  P2610110001
 ├─ Carton C2610110001  (10 serial)
 │    ├─ PCBA2610010000011
 │    ├─ PCBA2610010000029
 │    └─ ...
 ├─ Carton C2610110002  (10 serial)
 └─ ...                  (20 carton/pallet)
```

**Vì sao nhà máy cần.**

- **Khách hàng yêu cầu.** Khách OEM (Samsung, Apple, các hãng ô tô) nhận hàng theo pallet/thùng và quét nhãn thùng khi nhập kho. Khi phát hiện lỗi, họ báo theo **số thùng hoặc số pallet** chứ không theo từng serial. Nhà máy phải trả lời được "thùng C…002 chứa những serial nào, làm từ lot linh kiện nào".
- **Recall nhanh.** Khi M6 tìm ra 87 serial nghi lỗi, câu hỏi tiếp theo luôn là "những serial đó đang ở đâu": còn trong xưởng, đã vào thùng, đã lên pallet, hay đã giao. Có aggregation thì trả lời bằng một truy vấn.
- **Đếm và giao đúng số lượng.** Mỗi thùng có đúng 10 con, không thiếu, không lẫn model. Sai số lượng là lỗi giao hàng bị khách phạt.

**Ai dùng.** Operator trạm Pack (scan serial vào thùng, gom thùng lên pallet), Supervisor (đóng thùng lẻ cuối WO, unpack khi đóng nhầm, xác nhận xuất pallet), QC (Hold theo thùng/pallet ở M7).

### 1.2 Khái niệm & thuật ngữ

| Khái niệm | Nghĩa | Ví dụ trong dự án |
| --- | --- | --- |
| **Packing spec** | Quy cách đóng gói của một Item: bao nhiêu con/thùng, bao nhiêu thùng/pallet | `PCB-A`: 10 pcs/carton, 20 carton/pallet |
| **Carton (thùng)** | Đơn vị đóng gói cấp 1, có số thùng duy nhất và nhãn riêng | `C2610110001` |
| **Pallet** | Đơn vị đóng gói cấp 2, chứa nhiều thùng | `P2610110001` |
| **Aggregation** | Ghi quan hệ "con nằm trong cha" (serial ∈ carton ∈ pallet) | Bảng `CartonSerial`, cột `Carton.PalletNo` |
| **Full carton / Partial carton** | Thùng đủ số lượng / thùng lẻ (thường là thùng cuối WO) | WO 197 con → 19 thùng đủ + 1 thùng lẻ 7 con |
| **Unpack** | Lấy serial ra khỏi thùng (đóng nhầm, cần kiểm tra lại) | Supervisor unpack serial NG bị lọt |
| **Ship** | Xác nhận pallet đã xuất cho khách, sau đó không được sửa | `Pallet.Status = Shipped`, `ShipmentRef = DO-2610-015` |
| **Label carton / pallet** | Nhãn dán ngoài thùng/pallet: số thùng, mã hàng, số lượng, WO, ngày, mã vạch | ZPL, dùng lại `ZplLabelBuilder` của M2 |

**Thùng gắn với WO hay chỉ với Item?** Ngoài đời có nhà máy cho phép một thùng chứa serial của nhiều WO cùng model (gom thùng lẻ). MVP chọn **mỗi thùng thuộc đúng một WO**: truy xuất đơn giản hơn ("thùng này làm theo BOM version nào" chỉ có một câu trả lời) và báo cáo đóng gói theo WO không bị chia đôi. Phương án gom thùng lẻ ghi ở mục 4.

**Trạng thái Hold của thùng/pallet là trạng thái suy ra (derived).** Thùng được coi là "đang Hold" khi **có ít nhất một serial bên trong đang Hold**. Dự án **không lưu** cột `Hold` trên `Carton`/`Pallet` mà tính bằng `EXISTS` khi cần (gom pallet, xuất hàng, hiển thị). Lý do: Hold có nguồn sự thật duy nhất là `Serial.Status` + `HoldRecord` (M7). Nếu lưu thêm ở thùng thì mỗi lần Hold/Release phải đồng bộ hai nơi, và hai nơi sẽ có lúc lệch nhau. (Spec v2 ghi `Carton.Status` có `Hold`; tài liệu này chi tiết hóa thành trạng thái suy ra, ý nghĩa nghiệp vụ không đổi.)

### 1.3 Một ngày ở xưởng

Nhân vật: **Hoa** (operator trạm Pack Line 1), **Tuấn** (Supervisor), **Minh** (QC), **Hùng** (thủ kho thành phẩm).

**13:00.** WO-2610-00001 (200 `PCB-A`) đã có 120 con qua Test. Hoa đăng nhập kiosk trạm `L1-PACK-01`, chọn WO. Màn hình Packing hiện "Carton: (new) 0 / 10".

**13:01.** Hoa scan serial `…011`. Hệ thống kiểm tra như mọi trạm khác (route check M3: đã qua Test chưa, có Hold không), ghi scan bước PACK, serial thành `Completed`, rồi cho vào thùng mới `C2610110001`. Màn hình: "1 / 10".

**13:06.** Con thứ 10 vào thùng. Hệ thống tự đóng thùng, máy in Zebra in nhãn thùng. Hoa dán nhãn, đặt thùng lên pallet đang xếp. Thùng mới tự mở khi Hoa scan con tiếp theo.

**13:20.** Hoa lỡ scan một con bo chưa qua Test (bị để nhầm chỗ). Kiosk đỏ: "Expected step 3 (TEST)". Bo không vào thùng. Đây là lý do pack phải đi qua route check chứ không được ghi thẳng vào thùng.

**14:30.** Hoa chuyển kiosk sang chế độ "Palletize", scan lần lượt nhãn 20 thùng. Thùng thứ 20 vào, pallet `P2610110001` tự đóng, in nhãn pallet.

**15:10.** Minh (QC) nhận báo cáo SPC: lò reflow lệch nhiệt từ 14:00–14:20. Minh Hold 30 serial đã qua lò trong khoảng đó (M7). 6 serial trong số đó đã nằm trong thùng `C…009` và `C…010` trên pallet `P…001`. Màn hình Packing và báo cáo hiện hai thùng đó và pallet `P…001` ở trạng thái **ON HOLD**. Tuấn bấm "Ship" pallet `P…001` thì bị chặn: "Pallet contains held serials".

**16:00.** Kiểm tra lại: 1 serial thật sự lỗi. Tuấn **unpack** serial đó khỏi thùng `C…009` (lý do: "NCR-0007 confirmed defect"), serial đi Repair. 29 serial còn lại được Release. Thùng `C…009` còn 9 con. Tuấn mở lại thùng, Hoa scan bổ sung 1 con mới cho đủ 10, thùng đóng lại và in lại nhãn (`PrintNo = 2`).

**17:30.** WO còn 7 con cuối. Không đủ 10, Tuấn **đóng thùng lẻ** (lý do "WO end, 7 pcs"). Hùng xác nhận xuất pallet `P…001` với số phiếu giao `DO-2610-015`. Pallet chuyển `Shipped`, từ giờ không ai unpack hay sửa được thùng trên đó.

### 1.4 Quy tắc nghiệp vụ & lý do tồn tại

| # | Quy tắc | Lý do |
| --- | --- | --- |
| P1 | Pack một serial = **scan bước PACK qua route check của M3** + thêm vào thùng, trong **cùng transaction** | Không có đường tắt cho hàng chưa qua Test vào thùng; scan thành công mà không vào thùng (hoặc ngược lại) là dữ liệu lệch |
| P2 | Serial phải cùng Item và cùng WO với thùng đang mở | Lẫn model trong thùng là lỗi giao hàng nghiêm trọng |
| P3 | Mỗi serial nằm trong **tối đa một** thùng đang hiệu lực (unique có lọc trên `CartonSerial.SerialNo` khi chưa unpack) | Một con bo không thể ở hai thùng; DB là lưới an toàn cuối |
| P4 | Mỗi trạm Pack có tối đa **một thùng Open** cho mỗi WO | Operator biết rõ đang xếp thùng nào; tránh nhiều thùng dở dang |
| P5 | Thùng đủ `QtyPerCarton` thì **tự đóng** và in nhãn; không bao giờ vượt `QtyPerCarton` | Số lượng mỗi thùng đúng quy cách là cam kết với khách |
| P6 | Thùng lẻ chỉ đóng được bởi Supervisor, bắt buộc lý do, đánh dấu `IsPartial` | Thùng thiếu là ngoại lệ, cần người chịu trách nhiệm |
| P7 | Gom pallet: chỉ thùng `Closed`, cùng Item, không Hold, chưa thuộc pallet nào; pallet đủ `CartonsPerPallet` thì tự đóng | Như P2, P5 ở cấp pallet |
| P8 | **Ship** pallet: chỉ Supervisor, pallet `Closed`, không chứa serial Hold; sau Ship thì thùng/pallet/serial bên trong **không được unpack hay sửa** | Hàng đã rời nhà máy, dữ liệu là bằng chứng giao hàng |
| P9 | Unpack: Supervisor, có lý do, thùng chưa thuộc pallet (muốn unpack thùng trên pallet phải gỡ thùng khỏi pallet trước); serial được ghi `UnpackedAt/By/Reason`, **không xóa dòng** | Giữ lịch sử "serial từng ở thùng nào"; tránh đóng/mở tùy tiện |
| P10 | Thùng/pallet "đang Hold" = có serial Hold bên trong (trạng thái suy ra) | Một nguồn sự thật cho Hold (M7) |
| P11 | Đóng lại thùng sau unpack/bổ sung → **in lại nhãn** (`LabelPrintLog.PrintNo` tăng) | Số lượng trên nhãn phải khớp thực tế |

### 1.5 Sai lầm/sự cố thường gặp ngoài đời

| Sự cố | Hậu quả | Cách module phòng |
| --- | --- | --- |
| Đóng gói bằng phiếu giấy, nhập máy cuối ca | Thùng ghi 10 nhưng thực tế 9; serial ghi nhầm thùng | Scan từng serial vào thùng, tự đếm, tự đóng |
| Hai model giống nhau (`PCB-A`, `PCB-A2`) lẫn trong một thùng | Khách trả cả lô | P2 kiểm tra Item/WO |
| Thùng "11 con" do scan đúp hoặc race hai trạm | Sai số lượng giao | Scan lặp idempotent (M3) + cập nhật có điều kiện `Qty < QtyPerCarton` (mục 2.3) |
| Hàng Hold vẫn được xuất vì Hold chỉ ghi trên serial mà kho chỉ nhìn pallet | Hàng nghi lỗi tới tay khách | P8 + P10: Ship kiểm tra serial bên trong |
| Unpack không ghi lại | Truy xuất báo serial trong thùng A nhưng thực tế ở thùng B | P9: unpack là thao tác có lý do, giữ lịch sử |
| Nhãn thùng in lại không kiểm soát, hai thùng cùng số | Kho nhập trùng | Số thùng từ SEQUENCE; reprint ghi log |

### 1.6 Bảng thuật ngữ

| English | Tiếng Việt | 日本語 | Giải thích ngắn |
| --- | --- | --- | --- |
| Packing | Đóng gói | 梱包 (こんぽう) | Cho thành phẩm vào bao bì giao hàng |
| Carton | Thùng | カートン / 箱 | Đơn vị đóng gói cấp 1 |
| Pallet | Pallet | パレット | Đơn vị đóng gói cấp 2 |
| Aggregation | Gom/liên kết cha–con | 集約 / 紐付け (ひもづけ) | Ghi serial ∈ carton ∈ pallet |
| Packing spec | Quy cách đóng gói | 梱包仕様 | Số con/thùng, thùng/pallet |
| Partial carton | Thùng lẻ | 端数箱 (はすうばこ) | Thùng không đủ số lượng |
| Unpack | Tháo thùng | 開梱 (かいこん) | Lấy serial ra khỏi thùng |
| Shipment | Lô xuất hàng | 出荷 (しゅっか) | Hàng rời nhà máy |

---

## 2. Kiến thức lập trình

### 2.1 Mô hình dữ liệu aggregation và unique có lọc

**Nó là gì.** Quan hệ chứa nhiều cấp lưu bằng các bảng cha–con thông thường. Điểm mới là "**thành viên đang hiệu lực**": một serial có thể từng ở thùng A (đã unpack) và hiện ở thùng B. Muốn DB đảm bảo "tối đa một thùng hiện tại" mà vẫn giữ lịch sử, dùng **unique index có lọc (filtered unique index)**.

| Entity | Cột quan trọng | Ghi chú |
| --- | --- | --- |
| `PackingSpec` | ItemId (PK), QtyPerCarton, CartonsPerPallet | Master data nhỏ; tạo trong M3b vì chỉ module này dùng. Admin sửa (có audit M0) |
| `Carton` | CartonNo (PK, `varchar(20)`), WoId, ItemId, StationId, Qty, Status (`Open`/`Closed`/`Shipped`), IsPartial, PalletNo (nullable), OpenedAt, ClosedAt, ClosedBy, CloseReason, RowVersion | `Qty` là số serial đang hiệu lực |
| `CartonSerial` | Id (bigint), CartonNo, SerialNo, PackedAt, PackedBy, UnpackedAt, UnpackedBy, UnpackReason | Insert khi pack; unpack chỉ điền 3 cột cuối |
| `Pallet` | PalletNo (PK), ItemId, CartonCount, Status (`Open`/`Closed`/`Shipped`), ClosedAt, ShippedAt, ShippedBy, ShipmentRef | |

```csharp
// CartonSerialConfiguration
b.HasIndex(x => x.SerialNo)
 .IsUnique()
 .HasFilter("[UnpackedAt] IS NULL");          // mỗi serial tối đa 1 thùng đang hiệu lực
b.HasIndex(x => new { x.CartonNo, x.UnpackedAt });   // liệt kê serial trong thùng

// CartonConfiguration: tối đa 1 thùng Open cho mỗi (StationId, WoId)
b.HasIndex(x => new { x.StationId, x.WoId })
 .IsUnique()
 .HasFilter("[Status] = 'Open'");
```

**Bẫy.** (1) Filtered index chỉ được SQL Server dùng cho truy vấn khi điều kiện trong `WHERE` khớp bộ lọc; truy vấn "serial X đang ở thùng nào" phải viết `WHERE SerialNo = @s AND UnpackedAt IS NULL`. (2) Kết nối cần `SET ANSI_NULLS ON`, `QUOTED_IDENTIFIER ON` khi ghi vào bảng có filtered index (mặc định của SqlClient đã bật; script SQL tay trong SSMS cũng bật sẵn, nhưng công cụ cũ có thể tắt). (3) `HasFilter` nhận chuỗi SQL thô; đổi tên cột thì phải sửa tay.

### 2.2 Dùng lại use case Scan trong cùng transaction

**Vấn đề.** Pack = "scan PACK" (M3) + "thêm vào thùng". Có ba cách:

| Cách | Vấn đề |
| --- | --- |
| Kiosk gọi `POST /scan` rồi gọi `POST /cartons/add` | Hai request, hai transaction: scan thành công nhưng mạng rớt trước request thứ hai → serial `Completed` mà không ở thùng nào |
| `PackHandler` gửi `ScanCommand` qua MediatR (`_sender.Send`) | Handler Scan tự mở transaction riêng (M3 mục 2.2); lồng transaction qua MediatR khó kiểm soát |
| **Tách lõi Scan thành application service** `ScanExecutor.ExecuteAsync(ctx, ...)` **không tự mở transaction**; `ScanCommandHandler` (M3) và `PackCommandHandler` (M3b) cùng gọi nó bên trong transaction của mình | Chọn cách này |

```csharp
// MiniMes.Application/Packing/Pack/PackCommandHandler.cs (rút gọn)
public async Task<PackResponse> Handle(PackCommand cmd, CancellationToken ct)
{
    var spec = await _specs.GetAsync(cmd.ItemId, ct) ?? throw new DomainException("No packing spec for item.");
    await using var tx = await _db.Database.BeginTransactionAsync(ct);

    var scan = await _scanExecutor.ExecuteAsync(cmd.StationId, cmd.SerialNo, "OK", null, ct); // route check + ScanLog + Serial + bộ đếm
    if (scan.Result != ScanResult.Ok)
    {
        await tx.RollbackAsync(ct);
        return PackResponse.FromScan(scan);                      // Blocked / Duplicate: không đụng tới thùng
    }

    var carton = await _cartons.GetOrOpenAsync(cmd.StationId, scan.WoId, scan.ItemId, ct);  // thùng Open của trạm, hoặc mở mới
    var added = await _cartons.TryAddAsync(carton.CartonNo, cmd.SerialNo, spec.QtyPerCarton, ct); // mục 2.3
    if (!added) { await tx.RollbackAsync(ct); return PackResponse.Busy(); }  // hiếm: thùng vừa đầy bởi request khác

    var closed = await _cartons.CloseIfFullAsync(carton.CartonNo, spec.QtyPerCarton, ct);
    await tx.CommitAsync(ct);

    if (closed) await _labels.QueueCartonLabelAsync(carton.CartonNo, ct);   // in sau commit: không in nhãn cho thùng chưa chắc tồn tại
    return PackResponse.Packed(carton.CartonNo, carton.Qty + 1, spec.QtyPerCarton, closed);
}
```

**Bẫy.** (1) Scan lặp (`Duplicate`) ở bước PACK: serial đã ở trong thùng, trả lại thông tin thùng hiện tại thay vì báo lỗi. (2) In nhãn **trong** transaction: nếu commit thất bại, nhãn đã in cho một thùng không tồn tại. In sau commit; nếu in lỗi thì cho in lại (có log). (3) Đừng để `ScanCommandHandler` và `ScanExecutor` cùng mở transaction: chỉ handler ngoài cùng mở.

### 2.3 Đóng thùng chính xác khi đồng thời: cập nhật có điều kiện + SEQUENCE

**Vấn đề.** Hai serial được pack gần như cùng lúc vào thùng đang 9/10 (scanner bắn nhanh, hoặc 2 operator dùng chung trạm). Đọc `Qty = 9` ở cả hai rồi cùng ghi 10 → thùng "10" nhưng có 11 dòng `CartonSerial`.

**Giải pháp.** Tăng `Qty` bằng **một câu UPDATE có điều kiện** và kiểm tra số dòng bị ảnh hưởng; chỉ insert `CartonSerial` khi tăng thành công:

```sql
UPDATE dbo.Carton
SET    Qty = Qty + 1
WHERE  CartonNo = @CartonNo AND Status = 'Open' AND Qty < @QtyPerCarton;
-- @@ROWCOUNT = 1 → được phép insert CartonSerial; 0 → thùng đã đầy/đã đóng
```

```csharp
var ok = await _db.Cartons
    .Where(c => c.CartonNo == no && c.Status == CartonStatus.Open && c.Qty < qtyPerCarton)
    .ExecuteUpdateAsync(s => s.SetProperty(c => c.Qty, c => c.Qty + 1), ct) == 1;
if (ok) _db.CartonSerials.Add(new CartonSerial(no, serialNo, user, now));   // unique có lọc chặn serial ở 2 thùng
```

Đóng thùng cũng là cập nhật có điều kiện: `UPDATE Carton SET Status='Closed', ClosedAt=... WHERE CartonNo=@no AND Status='Open' AND Qty = @QtyPerCarton`.

**Số thùng/pallet** dùng SEQUENCE riêng (`CartonSeq`, `PalletSeq`) giống serial ở M2: `C` + `yyMMdd` (giờ nhà máy) + 4 chữ số → `C2610110001`. Không dùng `MAX + 1` (lý do như M2 mục 2.4).

### 2.4 Hold suy ra thay vì lưu: truy vấn `EXISTS`

```sql
-- Thùng nào đang Hold (dùng cho màn hình, gom pallet, ship)
SELECT c.CartonNo,
       IsOnHold = CASE WHEN EXISTS (
            SELECT 1 FROM dbo.CartonSerial cs
            JOIN   dbo.Serial s ON s.SerialNo = cs.SerialNo
            WHERE  cs.CartonNo = c.CartonNo AND cs.UnpackedAt IS NULL AND s.Status = 'Hold')
       THEN 1 ELSE 0 END
FROM dbo.Carton c
WHERE c.PalletNo = @PalletNo;
```

Use case `ShipPallet` chạy cùng truy vấn này trong transaction (kèm `UPDLOCK` trên dòng `Pallet` để không ai thêm thùng trong lúc kiểm tra) và từ chối nếu có thùng Hold.

**Hold theo thùng/pallet (M7).** QC chọn Hold một pallet → M7 mở rộng ra danh sách serial đang hiệu lực trong pallet (`Pallet → Carton → CartonSerial`) rồi Hold từng serial bằng đúng use case Hold set-based của M7. Không có "Hold cấp pallet" riêng; pallet tự hiện Hold nhờ truy vấn trên.

**Đánh đổi.** Truy vấn `EXISTS` tốn hơn đọc một cột. Với vài nghìn thùng/ngày và index `CartonSerial(CartonNo, UnpackedAt)` + `Serial(SerialNo)` thì không đáng kể. Khi thật sự cần (dashboard hàng trăm nghìn thùng), có thể thêm cột tính sẵn được cập nhật trong **cùng transaction** với Hold của M7. Đó là tối ưu có chủ đích, không phải mặc định.

### 2.5 Kiosk: màn hình Packing

Dùng lại khung Kiosk của M3 (ô scan luôn giữ focus, nền xanh/đỏ, overlay thay `MessageBox`).

- Chế độ **Pack**: thanh tiến độ to "7 / 10", số thùng đang mở, danh sách serial trong thùng (mới nhất ở trên), nút (Supervisor) "Close partial carton".
- Chế độ **Palletize**: scan nhãn thùng; hiện "12 / 20" và danh sách thùng trên pallet.
- Phân biệt mã scan: serial bắt đầu bằng mã model (`PCBA…`), thùng `C…`, pallet `P…`. Ở chế độ Pack mà scan nhãn thùng thì báo "This is a carton label".
- Âm báo riêng khi **thùng đầy** (khác âm OK thường) để operator biết lấy thùng ra dán nhãn.

### Sơ đồ luồng dữ liệu của module

```
Kiosk Packing (WPF)                 API                         Application                               DB
-------------------   ---------------------------------   ----------------------------------------   ----------------------
scan PCBA…011 ─────▶  POST /stations/{id}/pack  ─────────▶ PackCommandHandler
                      [JWT Operator]                        BEGIN TRAN
                                                            ScanExecutor (M3): RouteCheck,
                                                              ScanLog, Serial→Completed, QtyGood+1 ──▶ INSERT/UPDATE
                                                            Carton GetOrOpen (CartonSeq) ──────────────▶ INSERT Carton (nếu mới)
                                                            UPDATE Carton SET Qty=Qty+1 WHERE Qty<10 ─▶ @@ROWCOUNT
                                                            INSERT CartonSerial ───────────────────────▶ unique có lọc
                                                            UPDATE … Status='Closed' WHERE Qty=10 ─────▶
                                                            COMMIT
                                                            (sau commit) ZplLabelBuilder → máy in ──────▶ INSERT LabelPrintLog
           ◀──────── { result, cartonNo, qty, capacity, cartonClosed }
```

---

## 3. Kế hoạch thực hiện

Tổng khoảng 10 giờ. Commit sau mỗi bước.

- [ ] **Bước 1 — Entity, migration, seed packing spec (1,5 giờ)**
  - Việc cần làm: entity `PackingSpec`, `Carton`, `CartonSerial`, `Pallet`; enum lưu chuỗi; filtered unique index như 2.1; SEQUENCE `CartonSeq`, `PalletSeq`; seed `PackingSpec` cho `PCB-A` (10 / 20) và `PCB-B`; thêm station `L1-PACK-01` nếu M0 chưa có. Đăng ký `PackingSpec` vào audit M0.
  - File: `src/MiniMes.Domain/Packing/*.cs`, `src/MiniMes.Infrastructure/Persistence/Configurations/PackingConfigurations.cs`, migration `AddPacking`.
  - Kiểm tra: insert tay 2 dòng `CartonSerial` cùng serial chưa unpack → lỗi unique; điền `UnpackedAt` cho dòng đầu rồi insert lại → được.

- [ ] **Bước 2 — Domain rules + unit test (1,5 giờ)**
  - Việc cần làm: `Carton.EnsureCanAdd(serial)` (P2), `Carton.ClosePartial(reason, user)` (P6), `Carton.Unpack(...)` (P9), `Pallet.EnsureCanAdd(carton, isOnHold)` (P7), `Pallet.Ship(ref, hasHold)` (P8); `CartonNumberFormat`.
  - File: `src/MiniMes.Domain/Packing/*`, `tests/MiniMes.UnitTests/Packing/*`.
  - Kiểm tra: ≥ 10 case (sai Item, sai WO, thùng Closed, đóng lẻ thiếu lý do, unpack thùng đã lên pallet, ship pallet có Hold, ship pallet Open...).

- [ ] **Bước 3 — Tách `ScanExecutor` và use case Pack (2,5 giờ)**
  - Việc cần làm: tách lõi handler Scan của M3 thành `ScanExecutor` không tự mở transaction (mục 2.2), `ScanCommandHandler` gọi lại nó (test M3 phải vẫn xanh); `PackCommandHandler` theo 2.2 + 2.3; endpoint `POST /stations/{id}/pack`; `GET /stations/{id}/packing-status?woId=` (thùng đang mở, danh sách serial).
  - File: `src/MiniMes.Application/Execution/Scan/ScanExecutor.cs`, `src/MiniMes.Application/Packing/Pack/*`, `src/MiniMes.Contracts/Packing/*`, `tests/MiniMes.IntegrationTests/Packing/PackTests.cs`.
  - Kiểm tra: 25 serial đã qua Test → pack hết → 2 thùng đủ + 1 thùng Open 5/10; serial chưa qua Test → Blocked, không có dòng `CartonSerial`; scan lặp → Duplicate, thùng vẫn đúng số; **đồng thời**: thùng 9/10, `Task.WhenAll` pack 2 serial → đúng 1 vào thùng này, serial còn lại vào thùng mới (hoặc nhận `Busy` và pack lại thành công), không thùng nào > 10.

- [ ] **Bước 4 — Nhãn thùng và pallet (1 giờ)**
  - Việc cần làm: mở rộng `ZplLabelBuilder` (M2) với mẫu thùng (CartonNo Code128, Item, Qty, WoNo, ngày) và pallet; `LabelPrintLog` thêm cột `LabelType` (`Serial`/`Carton`/`Pallet`) và `TargetKey`; reprint có lý do.
  - Kiểm tra: xem trước bằng Labelary; đóng thùng thì có 1 dòng log; in lại thì `PrintNo = 2`.

- [ ] **Bước 5 — Palletize, đóng thùng lẻ, unpack, ship (1,5 giờ)**
  - Việc cần làm: `POST /stations/{id}/palletize` (scan thùng), `POST /cartons/{no}/close-partial`, `POST /cartons/{no}/unpack` (serial, reason), `POST /cartons/{no}/remove-from-pallet`, `POST /pallets/{no}/ship` (ShipmentRef); quyền: Operator pack/palletize, Supervisor các thao tác còn lại; truy vấn Hold suy ra (2.4).
  - Kiểm tra: 20 thùng → pallet tự đóng; Hold 1 serial trong thùng trên pallet (đặt `Serial.Status = Hold` trực tiếp nếu chưa có M7) → ship bị chặn; Release → ship được; sau ship, unpack bị chặn.

- [ ] **Bước 6 — Kiosk Packing (2 giờ)**
  - Việc cần làm: `PackingView` hai chế độ như 2.5; dùng lại `ScanBox`, overlay, âm báo của M3; chuỗi UI trong `.resx`.
  - Kiểm tra bằng tay: kịch bản mục 1.3 từ 13:00 đến 14:30 chạy được trên UI.

### Nếu bị kẹt

| Triệu chứng | Kiểm tra |
| --- | --- |
| Test M3 đỏ sau khi tách `ScanExecutor` | Handler M3 còn mở transaction không? `ScanExecutor` có lỡ gọi `SaveChanges` ngoài transaction không? |
| Thùng có 11 serial | Đang đọc-tăng-ghi `Qty` thay vì `ExecuteUpdate` có điều kiện; hoặc insert `CartonSerial` trước khi kiểm tra `@@ROWCOUNT` |
| Lỗi "Cannot insert duplicate key" khi pack serial đã unpack | Filtered index thiếu `HasFilter`, hoặc unpack chưa điền `UnpackedAt` |
| In nhãn cho thùng mà sau đó transaction rollback | Đang in trong transaction; chuyển ra sau `CommitAsync` |
| Pallet không hiện Hold dù serial bên trong Hold | Truy vấn `EXISTS` quên điều kiện `UnpackedAt IS NULL` hoặc join sai cột |

---

## 4. Vì sao chọn giải pháp này

| Quyết định | Đã chọn | Phương án khác | Vì sao không chọn | Khi nào nên đổi |
| --- | --- | --- | --- | --- |
| Pack có qua route check không | Có: Pack = scan PACK (M3) + thêm vào thùng, 1 transaction | Pack chỉ ghi vào thùng, scan PACK riêng | Hai thao tác tách rời sẽ có lúc lệch nhau; hàng chưa Test có thể vào thùng | Không nên đổi |
| Dùng lại logic Scan | Tách `ScanExecutor` không mở transaction | Gửi `ScanCommand` qua MediatR; gọi 2 API | Lồng transaction khó kiểm soát; 2 API mất tính nguyên tử | Khi Packing tách thành service riêng: dùng saga/outbox |
| Thùng gắn với | Một WO | Một Item, gom nhiều WO | Truy xuất và báo cáo theo WO đơn giản; một thùng chỉ một version BOM | Khách cho phép gom thùng lẻ: bỏ ràng buộc WO, giữ ràng buộc Item |
| Trạng thái Hold của thùng/pallet | Suy ra từ serial bằng `EXISTS` | Lưu cột Hold trên thùng/pallet | Hai nguồn sự thật dễ lệch | Khi đọc quá nhiều: cột tính sẵn cập nhật cùng transaction Hold |
| Đếm và đóng thùng | `UPDATE … WHERE Qty < Capacity` + `@@ROWCOUNT` | Khóa bi quan `UPDLOCK` trên Carton; đếm `COUNT(*)` | Khóa giữ lâu; COUNT chậm dần và vẫn race | Không cần đổi ở quy mô nhà máy |
| Unpack | Điền `UnpackedAt`, giữ dòng; filtered unique | Xóa dòng `CartonSerial` | Mất lịch sử "từng ở thùng nào" | Không nên đổi |
| Số thùng | SEQUENCE + ngày | Theo chuẩn GS1 SSCC (18 chữ số) | SSCC cần mã công ty GS1 thật | Khi khách yêu cầu nhãn GS1: thay `CartonNumberFormat`, phần còn lại giữ nguyên |
| Shipment | Chỉ `ShipmentRef` trên pallet | Bảng Shipment/ASN, tích hợp WMS | Vượt phạm vi MES (spec v2 B10 #9) | Khi làm M8 hoặc có WMS |

**Nói lại được khi phỏng vấn.** Đóng gói là nơi dữ liệu sản xuất gặp dữ liệu giao hàng. Ba điểm thiết kế: (1) pack đi qua cùng route check như mọi trạm nên không có đường tắt; (2) số lượng thùng đúng tuyệt đối nhờ cập nhật có điều kiện + unique có lọc, kể cả khi đồng thời; (3) Hold có một nguồn sự thật là serial, thùng/pallet chỉ suy ra nên không bao giờ lệch, và ship bị chặn nếu còn hàng Hold.

---

## 5. Hoàn thành module

### 5.1 Definition of Done

- [ ] 200 serial → 20 thùng → 1 pallet đầy (20 thùng); WO 197 con → 19 thùng đủ + 1 thùng lẻ 7 con do Supervisor đóng kèm lý do.
- [ ] Serial chưa qua Test, serial Hold, serial khác WO/Item không vào được thùng; scan lặp không làm sai số lượng.
- [ ] Test đồng thời: không thùng nào vượt `QtyPerCarton`; không serial nào ở 2 thùng hiệu lực.
- [ ] Thùng đầy tự đóng và sinh nhãn ZPL; reprint có `PrintNo` và lý do.
- [ ] Pallet chứa serial Hold không Ship được; pallet đã Ship không unpack/sửa được.
- [ ] Unpack giữ lịch sử (`UnpackedAt/By/Reason`); serial unpack có thể đóng vào thùng khác.
- [ ] Test của M3 vẫn xanh sau khi tách `ScanExecutor`.
- [ ] Kiosk Packing hai chế độ chạy được; chuỗi UI trong `.resx`.
- [ ] Ghi vào `ARCHITECTURE.md`: pack qua route check, Hold suy ra, cập nhật có điều kiện.

### 5.2 Tự kiểm tra

1. Vì sao pack phải gọi route check thay vì chỉ kiểm tra `Serial.Status = InProcess`?
<details><summary>Gợi ý</summary>Route check kiểm tra đủ: đúng bước kế tiếp (đã qua Test), không Hold, đúng WO, scan lặp. Chỉ xem status thì bo chưa Test vẫn vào thùng được.</details>

2. Thùng đang 9/10, hai request pack đến cùng lúc. Mô tả từng bước điều gì xảy ra ở DB.
<details><summary>Gợi ý</summary>Hai câu `UPDATE … WHERE Qty < 10` được SQL Server tuần tự hóa bằng khóa dòng. Câu đầu tăng lên 10 (`@@ROWCOUNT = 1`), câu sau thấy `Qty = 10`, không cập nhật (`@@ROWCOUNT = 0`) → handler rollback và báo Busy (hoặc mở thùng mới và thử lại). Không thùng nào có 11.</details>

3. Vì sao không lưu cột `IsOnHold` trên `Carton`?
<details><summary>Gợi ý</summary>Hold có một nguồn sự thật (`Serial.Status` + `HoldRecord`). Lưu thêm ở thùng thì mỗi Hold/Release/unpack phải đồng bộ, dễ lệch. Suy ra bằng `EXISTS` luôn đúng, chi phí nhỏ ở quy mô này.</details>

4. Filtered unique index khác unique index thường thế nào, và vì sao cần ở đây?
<details><summary>Gợi ý</summary>Chỉ áp ràng buộc cho các dòng thỏa bộ lọc (`UnpackedAt IS NULL`). Cho phép giữ lịch sử serial từng ở thùng cũ (đã unpack) mà vẫn đảm bảo tối đa một thùng hiện tại.</details>

5. Vì sao in nhãn sau commit?
<details><summary>Gợi ý</summary>Nếu in trong transaction rồi commit lỗi, nhãn vật lý đã in cho thùng không tồn tại trong DB. In sau commit; in lỗi thì in lại (có log).</details>

### 5.3 Câu hỏi phỏng vấn liên quan

1. **"Khách báo lỗi 1 pallet, bạn trả lời những gì?"** — Từ `PalletNo` → danh sách thùng → serial (bảng aggregation) → với mỗi serial: WO, version BOM, lot linh kiện (M6), kết quả từng trạm, máy và operator (M3), số đo SPC (M7). Đồng thời truy xuất xuôi từ lot nghi lỗi để biết còn pallet nào khác bị ảnh hưởng (đã giao hay còn trong kho) và Hold phần còn trong nhà máy.
2. **"Đóng nhầm serial vào thùng thì xử lý thế nào?"** — Supervisor unpack có lý do; lịch sử giữ lại; thùng mở lại, bổ sung, đóng lại và in lại nhãn; pallet đã ship thì không sửa dữ liệu mà xử lý bằng quy trình với khách (RMA).
3. **"Làm sao đảm bảo mỗi thùng đúng 10 con?"** — Cập nhật có điều kiện + `@@ROWCOUNT`, unique có lọc cho serial, scan lặp idempotent, tự đóng thùng khi đủ; test đồng thời chứng minh.
4. **"Hàng đang Hold có xuất ra khách được không?"** — Không: ship kiểm tra serial bên trong trong cùng transaction với khóa trên pallet; Hold suy ra từ một nguồn sự thật.

### 5.4 Tài liệu đọc thêm

- learn.microsoft.com, SQL Server: "Create filtered indexes"; "@@ROWCOUNT".
- learn.microsoft.com, EF Core: "Indexes" (mục filter), "ExecuteUpdate and ExecuteDelete".
- GS1: tài liệu về SSCC (Serial Shipping Container Code) và nhãn logistics GS1 (gs1.org), để biết chuẩn nhãn thùng/pallet ngoài đời.
- Trong dự án: M2 mục 2.4–2.5 (SEQUENCE, ZPL), M3 mục 2.2 (use case Scan), M6 (truy xuất xuôi dùng `CartonSerial`), M7 mục 2.6 (Hold set-based).
