# M6 · Bước 5 — Chặn scan khi thiếu vật tư và ghi tiêu hao khi scan OK: kiến thức cần nắm

> Tài liệu học cho bước 5 của [module-06](../../spec/module-06-material-traceability.md) (spec mục **1.4** R7–R10, **2.5**).
> Bước trước: [M6-step04](M6-step04-load-material.md).
> Code: `src/MiniMes.Domain/Material/MaterialGateRule.cs`, `src/MiniMes.Application/Material/Consumption.cs`,
> `src/MiniMes.Application/Execution/Scan.cs`. Test: `tests/MiniMes.IntegrationTests/Material/ConsumptionTests.cs`,
> `tests/MiniMes.UnitTests/Material/MaterialGateRuleTests.cs`.

## A. Nghiệp vụ

- Bước 4 trả lời câu hỏi **"reel nào đang ở trên máy"**. Bước 5 dùng câu trả lời đó để ghi cho từng board **"board này lấy linh kiện từ lot nào"** (`SerialComponent`), ngay khi board được scan OK ở trạm tiêu thụ vật tư.
- Đây là dữ liệu của mọi câu hỏi truy xuất sau này:
  - **Xuôi**: reel lỗi đã vào những board nào.
  - **Ngược**: board này dùng những reel nào.
- **Chặn scan khi thiếu reel (R8)**: nếu bước này gắn tụ mà trạm chưa nạp reel tụ nào, scan OK bị chặn (`MaterialNotLoaded`). Nếu vẫn cho qua, board có lịch sử sản xuất mà thiếu hồ sơ vật tư, và sau này không dựng lại được.
- Gate (cửa chặn) còn chặn khi reel đang nạp **bị Hold** sau khi nạp (M7 containment) hoặc **hết hạn giữa ca**.
- **Số còn lại là số lý thuyết (R10).** Mỗi board trừ `QtyPerUnit` (số trong snapshot BOM của WO, đã cộng hao hụt). Khi hết về 0:
  - Số không âm, lot thành `Consumed`.
  - Board vẫn được scan, vì máy thật có thể còn linh kiện.
  - Số thật được sửa khi tháo reel (bước 4).

## B. Thiết kế: hai nửa, một transaction

```
ScanHandler (M3)
  route check  ── Block/Duplicate ─▶ trả về, không ghi gì
  IMaterialGate.CheckAsync  ── thiếu/Hold/hết hạn ─▶ Blocked MaterialNotLoaded, không ghi gì
  BEGIN TRAN
    Serial, ScanLog, bộ đếm bước (như M3)
    Publish(SerialScannedOk)  ─▶ RecordConsumptionHandler (M6): SerialComponent + UPDATE MaterialLots
  COMMIT                         (handler ném lỗi → transaction dispose → rollback cả lần scan)
```

| Câu hỏi | Trả lời |
| --- | --- |
| Vì sao không gọi thẳng service của Material trong handler scan? | Execution chỉ biết `IMaterialGate` và một notification. Material có thể tắt (cờ `Material:EnforceLoadedMaterial`) hoặc thêm handler mới mà không sửa code scan |
| Vì sao không dùng job quét `ScanLog` sau? | Trễ, dễ sót hoặc trùng; và trong lúc đó board đã "OK" mà chưa có hồ sơ vật tư |
| Vì sao cùng transaction? | Board scan OK mà thiếu hồ sơ vật tư là kết quả **tệ nhất**, tệ hơn cả scan thất bại. Handler lỗi thì cả scan rollback, kiosk báo lỗi, operator scan lại |
| Gate đặt ở đâu? | Sau route check, **trước** transaction. Bị chặn thì không ghi gì, giống mọi lần bị chặn khác. Chỉ áp cho scan OK ở bước routing: scan NG vẫn ghi được lỗi, check-in Repair không đi qua gate |
| Trừ tồn thế nào? | Một câu `ExecuteUpdate` (SQL `UPDATE … SET RemainingQty = CASE …`), không đọc-trừ-ghi. Hai board cùng lúc sẽ cùng đọc 100 và cùng ghi 96 nếu đọc rồi ghi |
| Scan lặp? | Handler scan trả "duplicate" trước khi publish. Unique index `(SerialId, StationId, ItemId, LotId)` là lưới an toàn |
| Board rework đi lại SMT? | Bỏ qua item đã có `SerialComponent` cho (serial, trạm): linh kiện được gắn ở lần đầu (quyết định khi làm, xem mục D) |

**Một điểm nhỏ của SQL đáng nhớ.** Trong `UPDATE`, mọi vế `SET` đều đọc giá trị **cũ** của dòng. Vì vậy hai `CASE` (trừ số lượng, đổi trạng thái) cùng thấy `RemainingQty` trước lần scan này, không phụ thuộc thứ tự viết:

```csharp
await db.MaterialLots.Where(l => l.Id == lotId).ExecuteUpdateAsync(s => s
    .SetProperty(l => l.RemainingQty, l => l.RemainingQty >= qtyPer ? l.RemainingQty - qtyPer : 0m)
    .SetProperty(l => l.Status, l => l.Status == MaterialLotStatus.Available && l.RemainingQty <= qtyPer
        ? MaterialLotStatus.Consumed : l.Status), ct);
```

`ExecuteUpdate` không đi qua change tracker nên **không có audit** (interceptor M0). Điều này chấp nhận được: hồ sơ chính là `SerialComponent`, còn số lượng là số lý thuyết.

## C. Test: một cờ cấu hình cho hai thế giới

- Các test M3–M5 (hơn 100 test) scan board mà **không nạp reel**. Đây đúng là tình huống cờ `EnforceLoadedMaterial = false` được làm ra: chạy Execution khi chưa có Material.
- Vì vậy host test dùng chung để cờ **tắt**. `ConsumptionTests` **bật** cờ trong constructor và tắt lại trong `Dispose`. Cách này an toàn vì xUnit chạy các test cùng collection lần lượt, không song song.
- Ca (g) "handler lỗi thì rollback" dùng một `INotificationHandler` thứ hai, chỉ đăng ký trong test. Handler này chạy sau handler thật (MediatR chạy handler theo thứ tự đăng ký) và ném lỗi khi gặp đúng serial được chỉ định. Kết quả kiểm:
  - Không có `ScanLog` nào, không có `SerialComponent` nào.
  - Lot không bị trừ; bộ đếm và trạng thái WO không đổi.
  - Bỏ lỗi đi thì chính board đó scan OK bình thường.
- **Đột biến:** dời `Publish` ra **sau** `CommitAsync` thì ca (g) đỏ. Như vậy test thật sự bắt được trường hợp ghi ngoài transaction.

## D. Phát hiện khi chạy thật (API dev + simulator)

1. **Simulator (máy scan qua MQTT) cũng bị chặn.**
   - Trạm `L1-SMT-01` mới nạp 2/5 vật tư, nên scan của máy bị chặn với thông báo "PCB-BARE, IC-MCU-QFN, SOLDER-PASTE not loaded".
   - Nạp đủ 3 reel thì các board tiếp theo của máy qua và được ghi tiêu hao.
   - Gate nằm trong `ScanCommand` nên áp cho mọi nguồn scan.
2. **Thời gian scan.**
   - Lần đầu mất 115 ms (EF biên dịch các query mới).
   - Các lần sau 29–51 ms, trong mục tiêu < 100 ms của M3. Mỗi scan OK có thêm khoảng 5 insert và 5 update.
3. **Làm tròn số lượng.**
   - `WorkOrderMaterials.QtyPerUnit` là `decimal(28,9)` (M2), còn `MaterialLots.RemainingQty` và `SerialComponents.Qty` là `decimal(18,3)` (spec M6).
   - Kem hàn 0,0006363 kg/board được ghi thành 0,001 và trừ 0,001 mỗi board, sai khoảng 57 % cho vật tư tính theo kg. Với vật tư `pcs` (1,01505) chỉ lệch ở số thứ tư.
   - Dữ liệu vẫn **nhất quán** (tổng `SerialComponent.Qty` = số đã trừ), và truy xuất "lot nào" không bị ảnh hưởng. Đã sửa (user duyệt): bốn cột số lượng vật tư thành `decimal(18,6)`, migration `M6_MaterialQtyScale6`. Migration phải bỏ rồi tạo lại `CK_MaterialLots_Qty`, vì SQL Server không cho `ALTER COLUMN` một cột mà CHECK constraint đang dùng. Chi tiết: `docs/incidents.md`.

## E. Câu hỏi phỏng vấn

1. **"Làm sao bảo đảm board nào cũng có hồ sơ vật tư?"**
   - Hai lớp: gate chặn trước khi ghi, và ghi tiêu hao trong **cùng transaction** với scan. Không có trạng thái "scan OK nhưng thiếu vật tư".
   - Có test ép lỗi để chứng minh rollback.
2. **"Hai máy cùng trừ một reel thì sao?"** Một câu `UPDATE … SET x = x - @q` là nguyên tử. Test 10 scan song song cho kết quả trừ đúng 10 × QtyPer.
3. **"Hết số lý thuyết mà máy còn linh kiện?"** Không chặn: số không âm, lot thành `Consumed`, board vẫn được ghi vào lot đó. Số được sửa khi tháo reel.
4. **"Sao không join theo thời gian (board làm lúc 10:00 thì dùng reel đang nạp lúc 10:00)?"** Cách này vỡ khi đồng hồ lệch, khi `LoadedAt` bị sửa tay, hoặc khi hai reel đổi sát nhau. Ghi tại sự kiện scan thì không phải đoán.
