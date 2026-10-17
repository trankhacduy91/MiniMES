# Hiệu năng scan (M3 bước 10)

Mục tiêu của spec: `POST /api/stations/{id}/scan` có **p95 < 100 ms** khi `ScanLogs` có 5–10 triệu dòng.
Kết quả: **p95 24,8 ms** với 10 triệu dòng, 8 luồng song song. Đo ngày 17/10/2026.

## Cấu hình đo

| | |
| --- | --- |
| Máy | Intel Core i5-12400F (6 nhân / 12 luồng), RAM 16 GB, SSD, Windows 11 Pro |
| SQL Server | 2022 RTM-CU27 (16.0.4295.3) trong Docker (Ubuntu 22.04), Docker cấp 12 CPU / 8 GB RAM, cổng 14333 |
| API | `dotnet run -c Release`, .NET 10, cùng máy với DB, Serilog mức Information (mỗi scan một dòng log) |
| Dữ liệu | DB riêng `MiniMesPerf` (không đụng DB dev). `ScanLogs` = **10.018.790 dòng**: 10 triệu dòng seed (2,5 triệu serial × 4 bước, 2 % NG ở TEST, 2.000 serial mỗi WO) + các dòng của chính lần đo |
| Cache | Đo nóng (dữ liệu đã nằm trong buffer pool: bảng + index khoảng 1,5 GB, nhỏ hơn 8 GB RAM của container) |

Seed bằng T-SQL (`ROW_NUMBER` trên cross join `sys.all_objects`), bỏ index trước rồi tạo lại sau: **25 giây insert, 10 giây tạo 2 index**.

## Cách chạy lại

```bash
# 1. DB riêng cho đo: migrate, rồi chạy API một lần trên DB đó để seeder tạo master data
dotnet ef database update --project src/MiniMes.Infrastructure --startup-project src/MiniMes.Api --connection "<chuỗi kết nối, Database=MiniMesPerf>"
# 2. 10 triệu dòng ScanLogs (script tự từ chối chạy trên DB MiniMes)
sqlcmd -S localhost,14333 -U sa -C -I -d MiniMesPerf -v Rows=10000000 -i scripts/seed-scanlog.sql
# 3. Mức SQL: 3 phương án index + bẫy nvarchar, có plan
sqlcmd -S localhost,14333 -U sa -C -I -d MiniMesPerf -i scripts/perf-lastscan.sql
# 4. Mức API: API Release trỏ vào MiniMesPerf (ConnectionStrings__Default), rồi
dotnet run scripts/scan-bench.cs -- http://localhost:5157 2000 8
```

`scan-bench.cs` là script một file (.NET 10 file-based app, không phải project trong solution). Nó tạo và release một WO N bo trên Line 1 (không FAI), mở phiên ở 5 trạm. Sau đó W luồng song song, mỗi luồng đưa bo của mình qua SMT → ASSY → TEST → PACK. Cứ 50 bo có 1 bo NG ở TEST: check-in Repair, ghi repair, rồi test lại. Bỏ 100 request đầu (JIT, model EF, connection pool), in p50/p95/p99 theo bước.

## Kết quả mức API (thời gian HTTP đo ở client)

| Lần đo | Request | p50 ms | p95 ms | p99 ms | max ms | req/s |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| **Index theo spec, 8 luồng** (2.000 bo) | 8.020 | **17,9** | **24,8** | **31,4** | 46,8 | 434 |
| Index theo spec, 1 luồng (500 bo) | 1.930 | 12,0 | 15,7 | 20,2 | 37,2 | 80 |
| **Không index** trên `SerialNo`, 8 luồng (150 bo) | 509 | 1.396 | **1.708** | 1.786 | 1.844 | 7 |

Theo bước (8 luồng, có index): SMT p95 23,5; ASSY 23,3; TEST 24,6; PACK 28,5; Repair check-in 20,7; ghi repair 18,6 ms.
Mọi câu trả lời đúng như kịch bản (OK, NG, check-in), không có lần bị chặn ngoài ý muốn.

Thời gian xử lý trong handler (log Serilog `in {ElapsedMs} ms`, 1 luồng): p50 11, p95 15, p99 19 ms.
Câu đọc `ScanLogs` chỉ mất **0,03 ms** (bảng dưới), nên gần như toàn bộ thời gian đến từ khoảng 10 lượt hỏi DB của một lần scan:
trạm, phiên, mã lỗi, serial + WO, routing, scan gần nhất, mở transaction, nạp serial, INSERT + UPDATE, bộ đếm, commit.
Mỗi lượt khoảng 1 ms qua mạng Docker. Chạy 8 luồng thì p50 tăng từ 12 lên 18 ms: các scan của cùng một bước cùng cập nhật một dòng bộ đếm (`QtyGood`), nên phải chờ nhau (hot row).

## Kết quả mức SQL: câu "scan gần nhất của serial tại trạm"

Đây là câu chạy trong **mỗi** lần scan (chống scan lặp). Đo đúng câu SQL mà EF Core gửi, dạng `sp_executesql` có tham số, với serial ngẫu nhiên.

| Phương án | Logical reads | Trung bình / câu | Kích thước index |
| --- | ---: | ---: | ---: |
| A. Không index trên `SerialNo` | 128.519 | 145,5 ms (20 câu) | – |
| **B. Index `(SerialNo, ScannedAt)` theo spec** | **12** | **0,028 ms** (5.000 câu) | 379 MB |
| B2. Như B nhưng tham số `nvarchar` | 49.895 | 147,0 ms (20 câu) | 379 MB |
| C. `(SerialNo, ScannedAt) INCLUDE (StationId, Result)` | 4 | 0,014 ms (5.000 câu) | 456 MB (+20 %) |

Plan (trích `SET SHOWPLAN_TEXT ON`, chạy trong `perf-lastscan.sql`):

```text
A. Không index: đọc cả bảng 10 triệu dòng, song song, rồi sắp xếp
  |--Sort(TOP 1, ORDER BY:([ScanLogs].[ScannedAt] DESC))
       |--Parallelism(Gather Streams)
            |--Clustered Index Scan(OBJECT:([PK_ScanLogs]), WHERE:([StationId]=(3) AND [SerialNo]='SEED001234567'))

B. Index theo spec: seek ngược theo ScannedAt (không cần sort), mỗi dòng của serial một Key Lookup để lọc StationId
  |--Top(TOP EXPRESSION:((1)))
       |--Nested Loops(Inner Join, OUTER REFERENCES:([Id]))
            |--Index Seek(OBJECT:([IX_ScanLogs_SerialNo_ScannedAt]), SEEK:([SerialNo]='SEED001234567') ORDERED BACKWARD)
            |--Clustered Index Seek(OBJECT:([PK_ScanLogs]), SEEK:([Id]=[Id]), WHERE:([StationId]=(3)) LOOKUP ORDERED FORWARD)

B2. Tham số nvarchar: SQL Server đổi kiểu CẢ CỘT, nên không seek được, phải đọc lướt toàn bộ index
  |--Index Scan(OBJECT:([IX_ScanLogs_SerialNo_ScannedAt]), WHERE:(CONVERT_IMPLICIT(nvarchar(30),[SerialNo],0)=N'SEED001234567'))

C. Covering index: chỉ một Index Seek, không Key Lookup
  |--Top(TOP EXPRESSION:((1)))
       |--Index Seek(OBJECT:([IX_ScanLogs_SerialNo_ScannedAt]), SEEK:([SerialNo]='SEED001234567'), WHERE:([StationId]=(3)) ORDERED BACKWARD)
```

## Quyết định

1. **Giữ index theo spec `(SerialNo, ScannedAt)`, không thêm `INCLUDE`.**
   - Covering index giảm từ 12 xuống 4 logical reads (0,028 → 0,014 ms), tức bớt 14 µs trên tổng khoảng 12 ms của một scan (0,1 %).
   - Đổi lại, index to hơn 77 MB (+20 %) cho 10 triệu dòng, và mỗi INSERT vào bảng ghi nhiều nhất hệ thống phải ghi thêm 2 cột.
   - Key Lookup ở đây rẻ: một serial chỉ có vài dòng.
2. **Không phi chuẩn hóa `LastScanStationId/At/Result` lên `Serial`.**
   - Cái được tiết kiệm chỉ là câu 0,028 ms ở trên.
   - Cái phải trả: mỗi scan cập nhật thêm 3 cột, và phải giữ chúng đúng với `ScanLogs` (ai sửa log, ai sửa cột?).
   - Sẽ đáng làm nếu `ScanLogs` lên hàng tỷ dòng, hoặc nếu câu này nằm trên bảng không có index phù hợp.
3. **Không cache trạm/routing bằng `IMemoryCache`** (snippet của spec có gợi ý). p95 đã thấp hơn mục tiêu 4 lần. Cache thêm việc phải làm mất cache khi master data đổi, đổi lại vài ms. Nếu cần nhanh hơn, cách hiệu quả nhất là bớt số lượt hỏi DB (gộp các câu đọc), không phải cache.
4. **Giữ `varchar` + `IsUnicode(false)` cho `SerialNo`.** Nếu để EF gửi tham số `nvarchar`, câu chạy chậm gấp khoảng 5.000 lần dù có index. Đây là kiểu lỗi "có index mà vẫn scan" hay gặp ngoài đời.
5. **Không thử stored procedure** (spec để tùy chọn). Câu SQL đã ở mức micro giây; procedure chỉ bớt được lượt hỏi DB nếu dồn cả luồng scan vào một procedure, mà như vậy thì luật route check lại nằm trong SQL thay vì trong Domain có unit test.

## Lỗi tìm ra nhờ đo

Lần đo **không index**, bo thứ hai của một WO vừa release nhận **422** "Work order is InProgress; Start is not allowed" cho một lần scan hợp lệ:
- request đọc context thấy WO `Released`;
- trong 150 ms nó chờ câu `ScanLogs`, một bo khác đã `Start` WO và commit;
- nó nạp lại WO, gọi `Start()` lần nữa → bị state machine từ chối.

Integration test "2 bo đầu tiên cùng lúc" không thấy, vì khoảng hở chỉ dưới 1 ms. Đã sửa (`WorkOrder.EnsureStarted`), và dựng lại đúng thứ tự đó bằng một interceptor trong test (`ScanGate`): test đỏ 3/3 lần trước khi sửa. Chi tiết ở [`incidents.md`](incidents.md).
