# M3b · Bước 5 — Palletize, đóng lẻ, unpack, ship: kiến thức cần nắm

> Tài liệu học cho bước 5 của [module-03b](../../spec/module-03b-packing.md) (spec mục **1.3** từ 14:30, **1.4** P6–P10, **2.3**, **2.4**).
> Bước trước: [M3b-step04](M3b-step04-packing-labels.md). Bước sau: [M3b-step06](M3b-step06-kiosk-packing.md).
> Code: `src/MiniMes.Application/Packing/Palletize.cs`, `PackingActions.cs`, `PackingReads.cs`; `CartonsController`, `PalletsController`, `StationsController` (palletize).
> Test: `tests/MiniMes.IntegrationTests/Packing/PalletTests.cs`.

## A. Nghiệp vụ: từ thùng tới xe giao hàng

| Ai | Làm gì | Endpoint |
| --- | --- | --- |
| Operator trạm Pack | Chuyển kiosk sang chế độ *Palletize*, scan nhãn từng thùng; thùng thứ 20 vào thì pallet tự đóng và in nhãn | `POST /api/stations/{id}/palletize` |
| Supervisor | Cuối WO: đóng thùng lẻ (7 con), đóng pallet lẻ, kèm lý do | `POST /api/cartons/{no}/close-partial`, `POST /api/pallets/{no}/close-partial` |
| Supervisor | Gặp board lỗi đã đóng gói: gỡ thùng khỏi pallet, rồi unpack serial đó | `…/remove-from-pallet`, `…/unpack` |
| Supervisor (thay thủ kho) | Xuất pallet kèm số phiếu giao `DO-…` | `POST /api/pallets/{no}/ship` |
| QC, mọi người | Xem thùng/pallet và trạng thái Hold | `GET /api/cartons/{no}`, `GET /api/pallets/{no}` |

Sau khi ship, dữ liệu là **bằng chứng giao hàng**. Unpack, gỡ thùng hay ship lần hai đều bị từ chối. Nếu hàng đã giao có lỗi, xử lý bằng quy trình với khách (RMA), không sửa DB.

## B. Hold suy ra: một câu `EXISTS`

```sql
-- thùng đang Hold = có ít nhất một serial CÒN TRONG THÙNG đang Hold
EXISTS (SELECT 1 FROM CartonSerials cs JOIN Serials s ON s.Id = cs.SerialId
        WHERE cs.CartonId = @c AND cs.UnpackedAt IS NULL AND s.Status = 'Hold')
```

- Không có cột `IsOnHold` nào để quên cập nhật. M7 Hold hay Release serial thì thùng và pallet tự đổi theo ngay.
- Điều kiện `UnpackedAt IS NULL` là bắt buộc. Một board Hold đã được lấy ra khỏi thùng thì không còn giữ thùng đó nữa.
- Với cả pallet, dùng **một** truy vấn trả về danh sách thùng đang Hold, không truy vấn từng thùng (tránh N+1).

## C. Palletize = cùng khuôn với pack

Rủi ro giống bước 3: pallet đang 19/20, hai thùng được scan cùng lúc, và pallet có thể thành 21 thùng. Cách giải cũng giống:

1. Hàm Domain `Pallet.CheckAdd` trả lời "có nên thử không".
2. `UPDATE Pallets SET CartonCount = CartonCount + 1 WHERE … AND CartonCount < 20`. Nếu câu này trả 0 dòng thì người khác vừa lấy chỗ cuối, nên đọc lại và dùng pallet mới.
3. Phía thùng cũng là `UPDATE` có điều kiện (`… AND PalletId IS NULL`), để một thùng không lên hai pallet khi hai trạm scan cùng lúc.
4. Đủ thì tự đóng (cũng có điều kiện). In nhãn **sau commit**.

## D. Bẫy khi viết test đồng thời: test xanh chưa chắc đã chạm race

Bản đầu của test "hai thùng cùng vào chỗ cuối" dùng một client, dựng 21 thùng thật, rồi gửi hai request cùng lúc. Test xanh. Tôi thử **đột biến**: bỏ điều kiện `CartonCount < 20` khỏi câu `UPDATE`, mà test **vẫn xanh** cả 3 lần chạy. Nghĩa là hai request không thật sự chồng lên nhau: request đầu đã xong trước khi request sau đọc pallet.

Viết lại theo cách của test repack ở bước 3:
- Hai client.
- Lặp 5 vòng.
- Dựng trạng thái bằng SQL (pallet 19/20, hai thùng đã đóng), nên mỗi vòng chỉ tốn hai request.

Sau đó đột biến bị bắt (pallet thành 21 thùng, test đỏ).

Bài học: với test đồng thời, phải làm thêm một bước **chứng minh test chạm được nhánh cần kiểm**. Bỏ code bảo vệ đi thì test phải đỏ.

## E. Ship: RowVersion thay cho `UPDLOCK`

Spec 2.4 đề xuất khóa dòng pallet (`UPDLOCK`) trong lúc kiểm Hold, để không ai thêm thùng vào giữa chừng. Ở đây dùng **optimistic concurrency**, cùng kiểu với cả dự án:

- Pallet và từng thùng được đọc kèm `RowVersion`. Palletize, unpack hay gỡ thùng xen giữa (kể cả qua `ExecuteUpdate`) đều làm đổi `RowVersion`.
- Vì vậy `SaveChanges` của lần ship trả 409. Không bao giờ ship một pallet đã thay đổi so với lúc kiểm.
- Giới hạn đã biết: một serial bị Hold đúng vào khoảnh khắc đó thì không bị bắt, vì Hold nằm trên dòng `Serial`, không phải dòng pallet. Trường hợp này M7 xử lý bằng NCR. Có ghi chú trong code.

## F. Câu hỏi phỏng vấn

1. **"Hàng đang Hold có xuất được không?"** Không. Ship kiểm `EXISTS` serial Hold trong pallet, trong cùng lần lưu với việc đổi trạng thái, và có `RowVersion` để pallet không đổi giữa chừng. Hold chỉ có một nguồn sự thật là serial.
2. **"Đóng nhầm thùng trên pallet thì sửa thế nào?"** Gỡ thùng khỏi pallet (pallet mở lại), unpack serial có ghi lý do (thùng mở lại, lịch sử giữ nguyên), scan bù, thùng tự đóng và in lại nhãn. Nếu pallet đã ship thì không sửa dữ liệu.
3. **"Làm sao biết test đồng thời của anh thật sự test được race?"** Bằng đột biến: bỏ điều kiện bảo vệ thì test phải đỏ. Lần đầu không đỏ, nên tôi viết lại test cho hai request thật sự chồng nhau.
