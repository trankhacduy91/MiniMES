# Tuần 3: Shop floor execution và đóng gói (M3, M3b)

> **Mục tiêu tuần:** nắm vững luồng scan, chỗ IT MES support bị gọi nhiều nhất. Nghe operator nói "scan không được" là biết ngay cần hỏi gì, xem bảng nào, và câu trả lời nào là **đúng thiết kế** chứ không phải lỗi.
>
> **Thời lượng:** ~10 giờ, gồm đọc 3 · lab 3 · drill 2 · nói 2.

## 0. Ngoài đời việc này diễn ra thế nào

- Mỗi trạm có một PC (thường Windows, màn hình cảm ứng) và một máy quét mã vạch dạng USB HID, tức máy quét "gõ" serial rồi Enter.
- Operator **không đọc màn hình** khi làm nhanh; họ nghe tiếng bíp và nhìn màu nền. Vì vậy kiosk dùng nền xanh/đỏ toàn màn hình và âm báo khác nhau.
- **Interlock / poka-yoke:** hệ thống chặn board đi sai đường, ví dụ bỏ qua bước Test, board NG đi tiếp, board bị QC Hold. Đây là lý do khách hàng (ô tô, y tế) bắt nhà máy có MES.
- **Ca support điển hình lúc 2 giờ sáng:** "board này scan không được, line đang chờ". Trong 90 % trường hợp hệ thống chặn **đúng**: board bị bỏ bước, chưa sửa xong, đang Hold. Việc của IT là chỉ ra lý do trong 2 phút và hướng operator đúng quy trình, **không** "cho qua".
- **Đóng gói:** 10 board/thùng; thùng đầy thì in nhãn thùng; thùng gom thành pallet; pallet giao đi. Khách khiếu nại theo **số thùng/pallet**, nên quan hệ serial–thùng–pallet phải chính xác.

## 1. Đọc (3 giờ)

- [ ] `spec/module-03-shop-floor-execution.md`:
  - luồng scan; route check 19 case và **thứ tự** kiểm tra;
  - Repair/Rework/Scrap; phiên trạm (station session); FPY.
- [ ] `src/MiniMes.Domain/Execution/RouteCheck.cs`: chỉ đọc comment ở đầu file và các dòng `Block(...)`. Đây là "bảng tra" khi support.
- [ ] `src/MiniMes.Domain/Execution/ScanContext.cs`: danh sách `BlockReason`.
- [ ] `docs/learning/M3b-step01` → `step06`: đóng thùng, nhãn thùng, pallet, giao hàng, kiosk Pack.
- [ ] `docs/perf.md`, phần M3: p95 24,8 ms trên 10 triệu dòng; vì sao index đúng thì 12 logical reads, còn tham số `nvarchar` thì 49.895.

### Bảng tra reason code (học thuộc phần "nói gì với operator")

| `reasonCode` | Nghĩa | Nói gì với operator / làm gì |
| --- | --- | --- |
| `NoOpenSession` | Trạm chưa mở phiên WO | Đăng nhập lại, chọn WO trên dispatch list |
| `SerialNotFound` | Serial không có trong hệ thống | Nhãn in sai hoặc quét nhầm nhãn khác (nhãn linh kiện, nhãn thùng) |
| `WoNotActive` | WO đang Hold/Completed… | Hỏi trưởng ca vì sao WO bị dừng |
| `WrongWo` | Board thuộc WO khác WO đang mở | Lẫn hàng giữa hai WO; tách ra, báo trưởng ca |
| `SerialHold` | QC đang Hold board | Để riêng, gọi QC |
| `SerialScrapped` | Board đã hủy | Board này không được dùng nữa; báo QC (có thể bị lẫn lại vào line) |
| `SerialCompleted` | Board đã xong | Quét nhầm board đã đóng gói? |
| `MustGoRepair` | Board NG chưa sửa | Đưa sang trạm Repair |
| `NotDefective` | Đem board tốt tới trạm Repair | Đưa về line |
| `StationNotInRoute` | Trạm này không có trong routing của model | Sai trạm hoặc routing thiếu bước (master data) |
| `FaiNotApproved` | Đang chờ QC duyệt FAI | Chỉ board mẫu được chạy; gọi QC |
| `SkippedStep` | Board bỏ qua một bước | Đưa về trạm còn thiếu |
| `AlreadyPassed` | Bước này đã làm rồi | Đưa sang trạm kế tiếp |
| `MaterialNotLoaded` | Trạm thiếu vật tư đúng (M6) | Nạp reel đúng (tuần 6) |

"Duplicate" (quét lại trong 5 giây) trả OK và không ghi gì. Đây là chống double-trigger của máy quét.

## 2. Lab (3 giờ)

1. Tạo WO 20 pcs `PCB-A`, **bỏ tick RequireFai** để không vướng cổng FAI (FAI để tuần 7), rồi Release.
2. Mở 4 kiosk, mỗi trạm một cửa sổ:

   ```powershell
   dotnet run --project src/MiniMes.Kiosk.Wpf -- --StationCode=L1-SMT-01
   ```

   (Các trạm còn lại: `L1-ASM-01`, `L1-TEST-01`, `L1-PACK-01`.) Hoặc chạy simulator với `--auto-operator` cho line tự chạy rồi chỉ quan sát.
3. Cố tình làm sai, mỗi lần ghi lại `reasonCode`:
   - quét ở ASSY board chưa qua SMT;
   - quét lại ngay (trong 5 giây) và sau 10 giây;
   - ở TEST bấm **NG**, chọn mã lỗi, quét board rồi mang board đó sang PACK;
   - đưa board NG tới `L1-REPAIR`, check-in, ghi sửa chữa, quay lại TEST;
   - đưa một board tốt tới Repair.
4. **Đóng gói:** quét 10 board ở PACK, xem thùng đầy và nhãn in ra. Chuyển Pack sang chế độ Palletize, gom thùng vào pallet.
5. BackOffice, menu **Reports**: FPY theo bước, RTY, final yield, WIP. Board NG rồi sửa xong có làm FPY của TEST tăng lại không?

### SQL: bộ "điều tra một serial" (lưu lại thành file `serial.sql`, dùng cả 8 tuần)

```sql
DECLARE @sn varchar(30) = '<serial>';

-- 1. Board đang ở đâu, trạng thái gì
SELECT s.SerialNo, s.Status, s.StatusBeforeHold, s.CurrentSeq, w.WoNo, w.Status AS WoStatus, w.FaiStatus
FROM Serials s JOIN WorkOrders w ON w.Id = s.WorkOrderId WHERE s.SerialNo = @sn;

-- 2. Lịch sử scan
SELECT l.ScannedAt, st.Code, l.Seq, l.Result, l.DefectCode, l.OperatorCode, l.Source
FROM ScanLogs l JOIN Stations st ON st.Id = l.StationId
WHERE l.SerialNo = @sn ORDER BY l.ScannedAt;

-- 3. Sửa chữa
SELECT * FROM RepairLogs WHERE SerialId = (SELECT Id FROM Serials WHERE SerialNo = @sn);

-- 4. Đang ở thùng/pallet nào
SELECT c.CartonNo, c.Status, p.PalletNo, p.Status AS PalletStatus, cs.PackedAt, cs.UnpackedAt
FROM CartonSerials cs JOIN Cartons c ON c.Id = cs.CartonId LEFT JOIN Pallets p ON p.Id = c.PalletId
WHERE cs.SerialId = (SELECT Id FROM Serials WHERE SerialNo = @sn);
```

Ghi chú: `SerialNo` là `varchar`. Nếu viết `N'...'` (tức `nvarchar`), SQL Server phải chuyển kiểu cả cột và không dùng được index. Đó là bẫy trong incident "Bẫy nvarchar của M3 quay lại" (M6).

## 3. Drill sự cố (2 giờ)

### Drill A: "Board scan không được ở ASSY, line đang chờ"
**Cách tạo:** chọn một board, cho NG ở TEST rồi mang lên ASSY. Hoặc cho board bỏ qua SMT. Rồi giả làm IT được gọi.

**Tự làm trước:** chỉ dùng `serial.sql` và câu trên màn hình kiosk, trả lời được 3 ý: board bị chặn vì sao, nó phải đi đâu, có phải lỗi hệ thống không.

<details><summary>Đáp án gợi ý</summary>

- Màn hình đã ghi lý do. Hỏi operator **đọc nguyên văn** câu đỏ, hoặc nhờ chụp ảnh.
- `serial.sql` mục 1 và 2 cho thấy `CurrentSeq` và lần scan cuối.
  - Ví dụ: `CurrentSeq = 10` nhưng quét ở ASSY (20)? Thì phải OK. Nếu bị chặn `SkippedStep` thì phải xem lại: board đang ở SMT hay chưa qua SMT (`CurrentSeq = 0`)?
- **Kết luận cần nói với trưởng ca:** "Hệ thống chặn đúng. Board chưa qua bước X, đưa về trạm X." Nếu là `MustGoRepair` thì nói "board NG từ TEST lúc hh:mm, mã lỗi Y".
- **Khi nào mới là lỗi hệ thống:** reason code không khớp với lịch sử scan, hoặc kiosk báo lỗi 500/Offline thay vì BLOCKED.
- **Tuyệt đối không:** sửa `CurrentSeq` bằng SQL để "cho qua". Nếu thật sự phải can thiệp dữ liệu (ví dụ board bị scan nhầm trạm do cấu hình sai), cần quy trình có duyệt và để lại audit.
</details>

### Drill B: "Thùng đầy mà không in nhãn"
**Cách tạo:** chạy kiosk Pack với thư mục nhãn trỏ tới chỗ không ghi được, rồi đóng đầy một thùng:

```powershell
dotnet run --project src/MiniMes.Kiosk.Wpf -- --StationCode=L1-PACK-01 --LabelOutputFolder=Z:\nowhere
```

`Z:` là ổ không tồn tại. Mặc định nhãn ghi vào `%LOCALAPPDATA%\MiniMes\labels`; xem `FolderLabelPrinter` trong `Client.Core/Kiosk/LabelPrinter.cs`.

<details><summary>Đáp án gợi ý</summary>

- Kiosk báo **không ghi được nhãn**, nhưng thùng **vẫn Closed** trong DB. Thiết kế chủ ý: lỗi máy in không được làm mất dữ liệu đóng gói (test `Label_folder_not_writable_is_said_but_the_carton_stays_closed`).
- **Xử lý:** sửa quyền thư mục hoặc máy in, rồi **in lại** nhãn thùng.
- **Ngoài đời:** máy in hết nhãn hoặc ribbon, sai driver, đổi IP máy in. IT MES kiểm tra "dữ liệu có đúng không" trước, "máy in có in không" sau.
</details>

### Drill C: "Scan chậm, operator phải chờ"
**Đọc:** `docs/perf.md`. Trong SSMS bật *Include Actual Execution Plan*, rồi chạy hai câu:

```sql
SELECT TOP 1 * FROM ScanLogs WHERE SerialNo = '<serial>' ORDER BY ScannedAt DESC;
SELECT TOP 1 * FROM ScanLogs WHERE SerialNo = N'<serial>' ORDER BY ScannedAt DESC;
```

So hai plan (Index Seek và Index Scan) và số logical reads (`SET STATISTICS IO ON`).

**Bài học khi phỏng vấn:** lúc chậm, đo trước rồi mới đoán. Một scan mất khoảng 12 ms, phần lớn là khoảng 10 lượt hỏi DB, không phải một câu SQL chậm. Còn "hot row": 8 trạm cùng cập nhật một dòng bộ đếm `QtyGood` thì phải chờ nhau.

## 4. Câu hỏi phỏng vấn tuần 3

<details><summary>1. Interlock / poka-yoke trong MES là gì? Cho ví dụ.</summary>

Hệ thống **chặn lỗi trước khi xảy ra** thay vì phát hiện sau. Trong dự án, mỗi lần scan chạy route check:
- board phải qua đúng bước kế tiếp;
- board NG chỉ đi Repair;
- board Hold hoặc Scrapped bị chặn;
- WO cần FAI thì chỉ cho một board mẫu chạy.

Chặn nằm ở server nên kiosk, máy hay API trực tiếp đều không qua mặt được.
</details>

<details><summary>2. Thứ tự các quy tắc route check có quan trọng không?</summary>

Có. Ví dụ:
- quét lặp (duplicate) phải được nhận ra **trước** "board đã Completed", nếu không thì cú bấm đúp ở trạm cuối báo lỗi oan;
- board FAI bị NG phải vào được Repair, nên luật NG đứng trước cổng FAI.

Thứ tự là một phần hợp đồng và có test giữ nó.
</details>

<details><summary>3. Máy quét bấm đúp, hoặc hai trạm quét cùng một board cùng lúc: làm sao không ghi hai lần?</summary>

- **Quét lặp trong 5 giây:** trả "duplicate", không ghi gì.
- **Hai request đồng thời:** `RowVersion` (optimistic concurrency) chỉ cho một bên ghi; bên kia đọc lại và nhận duplicate.
- **Bộ đếm:** tăng bằng `UPDATE … SET QtyGood = QtyGood + 1`, không đọc rồi ghi.

Có integration test đồng thời. Đọc incident "Đo hiệu năng tìm ra race condition mà test đồng thời bỏ sót".
</details>

<details><summary>4. FPY, final yield, RTY khác nhau thế nào?</summary>

- **FPY của một bước:** % board **qua lần đầu** không NG. Chỉ tính lần quét đầu của mỗi serial; board sửa xong test lại OK vẫn tính là fail lần đầu.
- **Final yield:** % board cuối cùng đạt, trong số board đã xong.
- **RTY = tích các FPY:** xác suất một board đi hết line mà không NG lần nào.

Ví dụ trong dự án: đếm theo lượt quét ra 66,67 %, đếm đúng theo serial ra 70 %.
</details>

<details><summary>5. Repair, Rework, Scrap khác nhau ra sao?</summary>

- **Repair:** sửa board NG (thay linh kiện), ghi `RepairLog` (hành động, linh kiện thay, quay lại bước nào), rồi test lại. Linh kiện thay cũng được truy xuất (M6 bước 10).
- **Rework:** làm lại một số bước theo quyết định (thường từ NCR).
- **Scrap:** hủy board. Cần Supervisor; không quay lại line được.
</details>

<details><summary>6. Operator báo "scan không được", bạn xử lý thế nào?</summary>

Kể bằng drill A:
1. đọc reason code;
2. tra lịch sử scan bằng SQL;
3. kết luận đúng hay sai thiết kế;
4. hướng dẫn đúng quy trình;
5. nếu là lỗi hệ thống thì ghi incident.

Nhấn mạnh: không sửa DB để "cho qua".
</details>

<details><summary>7. Quan hệ serial – thùng – pallet lưu thế nào? Mở thùng ra lấy board thì sao?</summary>

- `CartonSerials` (có `PackedAt`/`UnpackedAt`, lý do unpack), `Cartons.PalletId`.
- Không xóa dòng mà đánh dấu unpack, nên truy xuất vẫn biết board **từng** ở thùng nào.
- Đóng thùng dùng UPDATE có điều kiện để hai trạm không cùng nhét board thứ 11 (ADR-010).
</details>

<details><summary>8. Route check p95 bao nhiêu? Bạn đo thế nào?</summary>

- **Kết quả:** p95 **24,8 ms** với 10 triệu dòng `ScanLogs`, 8 luồng song song; mục tiêu < 100 ms.
- **Cách đo:** script bench gọi API thật; seed dữ liệu bằng T-SQL; so sánh execution plan và logical reads giữa có index và không index.
- **Quyết định không làm:** không cache, không dùng stored procedure, vì đã nhanh gấp 4 lần mục tiêu. Tối ưu thêm không đáng với độ phức tạp tăng lên.
</details>

<details><summary>9. Station session là gì, vì sao cần?</summary>

Trạm "mở phiên" cho một WO, có operator đăng nhập.
- Mỗi lần scan ghi được **ai** làm, ở **WO nào** (`OperatorCode` lấy từ token, không lấy từ request).
- Chặn scan khi không có phiên (`NoOpenSession`).
- Đổi WO (changeover) là đóng phiên cũ, mở phiên mới.
</details>

## 5. Tự kiểm cuối tuần

- [ ] Nhìn một `reasonCode` bất kỳ trong bảng tra, nói được nguyên nhân và hành động trong 10 giây.
- [ ] Có file `serial.sql` chạy được trên DB của bạn.
- [ ] Giải thích được bằng plan vì sao `N'...'` làm chậm.
- [ ] Trả lời ≥ 8/9 câu.
