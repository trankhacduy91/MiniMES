# M6 · Bước 9 — Đo hiệu năng truy xuất và chi phí ghi tiêu hao: kiến thức cần nắm

> Tài liệu học cho bước 9 của [module-06](../../spec/module-06-material-traceability.md) (spec mục **2.3**, **2.7**).
> Bước trước: [M6-step08](M6-step08-trace-screen.md).
> Số đo đầy đủ: [`docs/perf.md`](../perf.md) mục "Hiệu năng truy xuất". Script: `scripts/seed-trace.sql`, `scripts/perf-trace.sql`, `scripts/scan-bench.cs`.

## A. Vì sao phải đo truy xuất

Truy xuất chạy đúng vào lúc căng nhất: khách báo lỗi, audit, recall. Spec 1.5 tả cảnh "truy vấn 40 giây vì thiếu index". Bảng `SerialComponents` lớn rất nhanh: khoảng 8 dòng mỗi board, 2 line, 5 năm là gần 100 triệu dòng. Mục tiêu của spec: < 1 giây với 5 triệu dòng.

## B. Cách đo (dùng lại kỹ thuật M3)

1. **DB riêng** (`MiniMesPerf`), không đụng DB dev.
2. **Seed bằng T-SQL**: `ROW_NUMBER()` trên cross join `sys.all_objects` sinh 5 triệu dòng. Bỏ index trước, insert xong mới tạo lại: 20 giây cho cả việc.
3. **Đo đúng câu mà ứng dụng gửi**: chép câu SQL của `TraceQueries`, gửi qua `sp_executesql` có tham số, như EF làm. Đo một câu "gần giống" sẽ cho số đẹp nhưng sai.
4. **Plan thật**, lấy từ plan cache (`sys.dm_exec_query_stats` + `sys.dm_exec_query_plan`), rồi dùng XQuery liệt kê Seek / Scan / Key Lookup trên các bảng lớn. Mỗi phương án được ghi kèm plan.
5. **Đo ở hai mức**:
   - Mức SQL: logical reads, ms.
   - Mức API: HTTP, gồm cả JSON 3,4 MB và file Excel.
   - Thêm một lần **cache lạnh** (`DBCC DROPCLEANBUFFERS`).

## C. Ba điều đo ra

1. **Đạt mục tiêu dễ dàng**: xuôi 12.500 serial khoảng 100 ms qua API, ngược 3 ms. Thiết kế "đệ quy trên bảng nhỏ, join bảng lớn một lần" (mục 2.2) cộng index `(LotId)` là đủ.
2. **Index phủ đủ cột không làm nhanh hơn.**
   - Nó bớt 1/3 logical reads nhưng thời gian không đổi, vì thời gian nằm ở chỗ khác: join `Serials` và các bảng nhỏ.
   - Đổi lại index to thêm 76 %, trên bảng ghi nhiều nhất của module.
   - Bài học giống M3: **đo trước rồi mới thêm index**; "ít logical reads hơn" chưa chắc là "nhanh hơn".
3. **Bẫy `nvarchar` quay lại.**
   - Câu ngược theo serial: 0,65 ms với `varchar`, 82,6 ms với `nvarchar`. Index Seek thành Index Scan vì SQL Server phải đổi kiểu cả cột.
   - Code bước 7 tạo `SqlParameter` từ chuỗi .NET, tức `nvarchar`.
   - EF LINQ không mắc lỗi này vì lấy kiểu từ model; SQL viết tay thì phải tự khai kiểu.
   - Test trên dữ liệu nhỏ không bắt được: kết quả vẫn đúng, chỉ chậm dần theo dữ liệu.
   - Đã sửa, và có test kiểm **kiểu tham số thật** gửi xuống DB.

## D. Chi phí ghi tiêu hao khi scan

| | SMT p50 (1 luồng) | SMT p95 (8 luồng) |
| --- | ---: | ---: |
| Không ghi | 15,0 ms | 32,5 ms |
| Có ghi | 20,7 ms | 76,1 ms |

- **Một luồng**: việc thật (5 INSERT + 5 UPDATE + 2 câu đọc) tốn khoảng 6 ms.
- **Tám luồng**: tăng thêm 25 ms, chủ yếu là **chờ khóa**. Mọi board cùng trừ 5 dòng lot, và khóa dòng giữ tới lúc commit. Đây là "hot row", cùng hiện tượng với bộ đếm `QtyGood` ở M3.
  - Câu `UPDATE … SET x = x - @q` vẫn đúng (không mất lần trừ nào), nhưng các transaction phải xếp hàng.
  - Ngoài đời, một trạm SMT ra khoảng 1 board / 15–30 giây, nên 8 scan cùng lúc trên một reel gần như không xảy ra. Vì vậy giữ thiết kế.
  - Nếu cần nhanh hơn: cộng dồn rồi trừ định kỳ, hoặc chỉ trừ khi tháo reel. Đổi lại, số còn lại kém tức thời.

## E. Bẫy khi đo

Lượt đo đầu dừng giữa chừng vì trạm TEST đột nhiên trả `WrongWo`. Container `mes-simulator` (M4) gọi API ở cùng cổng 5157 và đã mở phiên cho WO khác. Trước khi đo, phải liệt kê **mọi** thứ có thể gọi vào hệ thống, và tắt chúng đi.

## F. Câu hỏi phỏng vấn

1. **"Truy xuất trên 100 triệu dòng có nhanh không?"**
   - Đã đo trên 5 triệu: khoảng 100 ms. Câu truy vấn seek theo `LotId`, nên thời gian tăng theo **số dòng kết quả**, không theo kích thước bảng.
   - Phần quét `Serials` hiện nay sẽ tự chuyển sang seek khi bảng lớn lên, vì optimizer tính theo chi phí; cần đo lại ở cỡ đó.
   - Xa hơn: partition theo tháng, nén PAGE (mục 2.3).
2. **"Có index mà vẫn chậm, anh tìm thế nào?"** Xem plan thật, tìm Index Scan hoặc `CONVERT_IMPLICIT`. Thủ phạm hay gặp nhất là kiểu tham số khác kiểu cột, như đã gặp hai lần trong dự án.
3. **"Ghi tiêu hao có làm chậm dây chuyền không?"** Khoảng 6 ms mỗi scan; tranh chấp khóa chỉ đáng kể khi nhiều scan cùng trừ một reel cùng lúc. Có số đo, có phương án nếu cần.
