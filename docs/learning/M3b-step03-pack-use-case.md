# M3b · Bước 3 — Tách `ScanExecutor` và use case Pack: kiến thức cần nắm

> Tài liệu học cho bước 3 của [module-03b](../../spec/module-03b-packing.md) (spec mục **1.4** P1–P5; **2.2**, **2.3**).
> Bước trước: [M3b-step02](M3b-step02-packing-rules.md).
> Code: `src/MiniMes.Application/Execution/ScanExecutor.cs`, `Execution/Scan.cs`, `Packing/Pack.cs`, `Packing/PackingStatus.cs`,
> `src/MiniMes.Contracts/Packing/PackingContracts.cs`, `Api/Controllers/StationsController.cs` (`pack`, `packing-status`).
> Test: `tests/MiniMes.IntegrationTests/Packing/PackTests.cs` (10), toàn bộ test M3/M6 của Scan vẫn xanh.

## A. Nghiệp vụ: một lần scan ở trạm Pack làm gì

```
scan PCBA…011 ở L1-PACK-01
  ├─ serial đã Completed?
  │    ├─ đang ở thùng X            → trả "Already in carton X" (Duplicate), không ghi gì
  │    └─ không ở thùng nào         → REPACK: cho vào thùng, KHÔNG scan PACK lần 2
  │                                    (serial vừa unpack, hoặc đã scan PACK qua /scan trước M3b)
  └─ chưa Completed                 → scan PACK qua route check M3 (đã qua Test? Hold? đúng WO?)
                                        ├─ Blocked/Duplicate → trả nguyên lý do, không ghi gì
                                        └─ OK → cho vào thùng Open của (trạm, WO); đủ 10 → tự đóng
```

- **Repack (bạn đã duyệt):** route check M3 chặn serial `Completed`, nên serial unpack (bước 5) sẽ không bao giờ vào lại thùng nếu bắt buộc scan PACK lần nữa. Serial đó đã qua PACK thật, nên chỉ cần thêm vào thùng; `ScanLog` và `QtyGood` không tăng lần 2. Vẫn kiểm: phải đúng WO của session trạm (`WrongWo`).
- **Mỗi thùng thuộc một WO** (spec 1.2): thùng Open được tìm theo `(StationId, WorkOrderId)`, đúng unique có lọc P4.

## B. Kỹ thuật 1: một transaction cho "scan + thùng" (P1)

Trước bước này, `ScanHandler` tự mở transaction bên trong. Pack cần scan **và** thêm vào thùng trong cùng transaction: scan OK mà thùng lỗi thì phải rollback cả scan.

| Ai | Làm gì |
| --- | --- |
| `ScanExecutor.ExecuteAsync(tx, cmd)` | Toàn bộ lõi scan cũ (route check, material gate, ghi Serial/ScanLog/bộ đếm, publish M6). **Không mở, không commit** transaction; nhận `tx` của người gọi làm tham số, nên không thể quên là phải có transaction |
| `ScanHandler` (M3) | Mở transaction → gọi executor → commit nếu `Recorded` |
| `PackHandler` (M3b) | Mở transaction → executor → thêm vào thùng → commit |

**Retry bên trong transaction của người khác: savepoint.** Scan cũ có vòng retry khi hai request cùng sửa một serial (RowVersion → `ConflictException`): lần thử 1 hỏng thì rollback rồi làm lại. Khi transaction là của người gọi, không được rollback cả transaction (mất phần người gọi đã làm). Giải pháp là **savepoint** (`SAVE TRANSACTION` trong SQL Server):

```csharp
await tx.CreateSavepointAsync("scan", ct);        // điểm đánh dấu
try { return await RecordAsync(...); }
catch (ConflictException) when (attempt == 0)
{
    await tx.RollbackToSavepointAsync("scan", ct); // chỉ bỏ phần ghi của lần thử này
    db.ChangeTracker.Clear();                      // quên entity đã đọc, đọc lại
}
```

Savepoint cũng dùng cho trường hợp "giành mẫu FAI thất bại": lệnh `ExecuteUpdate` đã chạy trước đó được hoàn tác, không để lại rác trong transaction chung.

## C. Kỹ thuật 2: đếm thùng đúng khi đồng thời (P5, spec 2.3)

```csharp
var added = await db.Cartons
    .Where(c => c.Id == id && c.Status == CartonStatus.Open && c.Qty < qtyPerCarton)
    .ExecuteUpdateAsync(s => s.SetProperty(c => c.Qty, c => c.Qty + 1), ct) == 1;
if (!added) continue;                              // thùng vừa đầy/đóng bởi request khác → đọc lại, mở thùng mới
db.CartonSerials.Add(new CartonSerial(...));       // chỉ insert khi đã giành được chỗ
```

- Đóng thùng cũng có điều kiện: `WHERE Status='Open' AND Qty >= 10`. Chỉ request làm thùng đầy mới đóng nó.
- **Thua race thì tự mở thùng mới (bạn đã duyệt)**, không trả "Busy". Thử tối đa 3 lần, quá thì 409.
- **Hai request cùng mở thùng mới:** unique có lọc P4 làm INSERT thứ hai **chờ** transaction thứ nhất commit rồi mới báo trùng. Lúc đó thùng của bên kia đã commit, đọc lại là thấy.

**Phát hiện khi viết test:** hai pack thường của cùng WO **không bao giờ** thật sự đua nhau ở dòng `Carton`. Cả hai đều `UPDATE … SET QtyGood = QtyGood + 1` trên cùng dòng bộ đếm bước PACK; request thứ hai chờ khóa dòng đó tới khi request thứ nhất commit, rồi mới đọc thùng. Nhánh "UPDATE trả 0 dòng" chỉ chạy thật với repack (không đụng bộ đếm), nên có test riêng cho repack đồng thời. Đã kiểm bằng cách tạm cho nhánh đó ném lỗi: test đỏ, nghĩa là test thật sự đi qua nhánh này.

Bài học: test đồng thời xanh chưa chắc đã chạm race bạn nghĩ. Phải biết khóa nào đang tuần tự hóa các request, và kiểm chứng test đi qua đúng nhánh.

## D. Kiểm tra (spec bước 3)

| Spec yêu cầu | Test |
| --- | --- |
| 25 serial qua Test → 2 thùng đủ + 1 thùng Open 5/10 | `Twenty_five_boards_fill_two_cartons_and_leave_one_open_with_five` (+ `packing-status`, mới nhất ở trên) |
| Chưa qua Test → Blocked, không có `CartonSerial` | `Board_that_skipped_test_is_blocked_and_goes_in_no_carton`; Hold: `Board_on_hold_is_blocked` |
| Scan lặp → Duplicate, đúng số | `Packing_a_board_twice_shows_its_carton_and_counts_it_once` |
| Đồng thời 9/10 → không thùng nào > 10 | `Two_boards_at_once_…` (3 vòng), `Two_repacks_at_once_…` (5 vòng, race thật), `Two_first_boards_at_once_share_one_new_carton` |
| Repack (quyết định mới) | `Unpacked_board_is_repacked_without_a_second_scan`, `Repacking_into_the_session_of_another_order_is_blocked` |
| Quyền, trạm sai | `Pack_needs_a_pack_station_and_an_operator` (422 trạm không phải Pack, 404, 403 planner, 400 serial rỗng) |

## E. Câu hỏi phỏng vấn

1. **"Bạn dùng lại logic scan cho đóng gói thế nào mà không lồng transaction?"** Tách lõi thành service không tự mở transaction, nhận transaction của handler ngoài cùng; retry bên trong dùng savepoint.
2. **"Savepoint khác transaction lồng nhau thế nào?"** SQL Server không có transaction lồng thật (`COMMIT` bên trong chỉ giảm `@@TRANCOUNT`, `ROLLBACK` bên trong hủy tất cả). Savepoint cho phép hoàn tác một phần mà transaction ngoài vẫn sống.
3. **"Hai operator scan con thứ 10 cùng lúc?"** Cập nhật có điều kiện + `@@ROWCOUNT`; bên thua đọc lại và vào thùng mới. Và nêu được khóa nào đang thực sự tuần tự hóa (dòng bộ đếm).
