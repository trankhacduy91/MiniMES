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

# Hiệu năng truy xuất (M6 bước 9)

Mục tiêu của spec: truy xuất < **1 giây** khi `SerialComponents` có 5 triệu dòng; đo thêm thời gian scan khi có ghi tiêu hao.
Kết quả: truy xuất xuôi một supplier lot (12.500 serial) **~100 ms qua API**, truy xuất ngược một serial **3 ms**. Ghi tiêu hao làm scan ở SMT chậm thêm ~6 ms khi chạy một luồng, và p95 lên 76 ms khi 8 luồng cùng trừ chung 5 reel. Đo ngày 02/11/2026, cùng máy và cấu hình với phần M3 ở trên.

## Dữ liệu

DB `MiniMesPerf` (đã có 10 triệu `ScanLogs` của M3), thêm bằng [`scripts/seed-trace.sql`](../scripts/seed-trace.sql) theo hình dạng của spec 2.7:

| | |
| --- | --- |
| `SerialComponents` | **5.000.000** dòng = 625.000 serial × 8 vật tư quản lý theo lot |
| Lot | 2.000 = 8 item × 250 reel, mỗi reel 2.500 serial; 5 reel một supplier lot (`PT-SUP-0`…`PT-SUP-49` mỗi item) |
| Genealogy | 400 cạnh: cứ reel thứ 5 được tách từ reel trước nó (thêm so với spec, để đệ quy có việc làm) |
| Thời gian seed | 15 giây insert (bỏ index trước), 5 giây tạo 2 index |

```bash
sqlcmd -S localhost,14333 -U sa -C -I -d MiniMesPerf -i scripts/seed-trace.sql
sqlcmd -S localhost,14333 -U sa -C -I -d MiniMesPerf -i scripts/perf-trace.sql   # đo, in plan, trả index về như model
```

## Mức SQL

[`scripts/perf-trace.sql`](../scripts/perf-trace.sql) gửi **đúng câu SQL** của `TraceQueries` qua `sp_executesql` có tham số (như EF gửi), cache ấm, lấy trung bình nhiều lần; plan lấy từ plan cache (plan thật đã chạy). Thời gian gồm cả ghi kết quả vào bảng tạm.

| Phương án | Câu | Tham số | ms / câu | Logical reads | Dòng | Plan trên 2 bảng lớn |
| --- | --- | --- | ---: | ---: | ---: | --- |
| A. Không index `(LotId)` | xuôi supplier lot | varchar | 231,9 | 155.875 | 12.500 | **Clustered Index Scan** `SerialComponents` (5 triệu dòng) |
| A. Không index `(LotId)` | xuôi 1 lot | varchar | 133,4 | 75.194 | 2.500 | Clustered Index Scan |
| **B. `(LotId) INCLUDE (SerialId)`** (spec, model EF) | xuôi supplier lot | varchar | **144,0** | 158.053 | 12.500 | Index Seek + Key Lookup |
| B | xuôi 1 lot | varchar | 57,3 | 35.634 | 2.500 | Index Seek + Key Lookup |
| B | xuôi supplier lot | nvarchar | 147,8 | 158.097 | 12.500 | như trên (bảng lot nhỏ) |
| B | **ngược 1 serial** | **varchar** | **0,65** | 311 | 8 | Index Seek `IX_Serials_SerialNo` |
| B | **ngược 1 serial** | **nvarchar** | **82,6** | 3.667 | 8 | **Index Scan** `IX_Serials_SerialNo` |
| C. Covering `INCLUDE (SerialId, StationId, ScanLogId, ConsumedAt)` | xuôi supplier lot | varchar | 164,0 | 106.953 | 12.500 | Index Seek, không Key Lookup |
| C | xuôi 1 lot | varchar | 56,4 | 26.929 | 2.500 | Index Seek |

Kích thước index `IX_SerialComponents_LotId`: B **126 MB**, C **222 MB** (+76 %). Bảng (clustered) 370 MB, unique `(SerialId, StationId, ItemId, LotId)` 164 MB.

Cache lạnh (`DBCC DROPCLEANBUFFERS`, phần lõi của câu xuôi, supplier lot khác): **66 ms**; chạy lại ngay: 22 ms.

Ở mọi phương án, `Serials` được đọc bằng **Clustered Index Scan** (630.000 dòng, hash join) thay vì 12.500 lần seek: với kích thước hiện tại, optimizer tính quét rẻ hơn. Khi `Serials` lên hàng chục triệu dòng, phép tính đổi và nó sẽ tự chuyển sang seek; cần đo lại khi có dữ liệu cỡ đó.

## Mức API (HTTP, đo ở client, API Release)

| Request | Lần đầu | Các lần sau | Kết quả |
| --- | ---: | ---: | --- |
| `GET /api/trace/forward?supplierLot=PT-SUP-3&itemCode=PCB-BARE` | 224 ms | 87–120 ms | 12.500 serial, JSON 3,4 MB |
| `GET /api/trace/forward?lotNo=PT-1-100` | 84 ms | 46–55 ms | 2.500 serial |
| `GET /api/trace/backward?serialNo=PT000300000` | 30 ms | 3–5 ms | 8 lot |
| `GET /api/trace/forward.xlsx?supplierLot=PT-SUP-3&…` | 1.415 ms | 279–334 ms | 12.500 dòng, 439 KB (lần đầu: JIT của ClosedXML) |

## Scan có và không ghi tiêu hao

`scripts/scan-bench.cs` thêm tham số `material`: trước khi chạy, nhận + IQC + nạp một reel lớn cho mọi vật tư lot-controlled mà WO cần ở mỗi trạm (SMT 5, ASSY 2, PACK 1). Không có tham số đó thì API chạy với `Material__EnforceLoadedMaterial=false` và không reel nào được nạp (như M3). Cùng code, cùng DB, đo liền nhau; container `mes-simulator` **tắt** trong lúc đo (xem dưới).

| Lượt | Luồng | SMT p50 | SMT p95 | ASSY p95 | TEST p95 | Tất cả p95 | req/s |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Không tiêu hao | 1 | 15,0 | 19,2 | 20,2 | 17,0 | 19,9 | – |
| **Có tiêu hao** | 1 | **20,7** | 26,8 | 22,7 | 16,8 | 24,0 | 54 |
| Không tiêu hao | 8 | 23,5 | 32,5 | 32,6 | 29,3 | 42,1 | 304 |
| **Có tiêu hao** | 8 | **48,1** | **76,1** | 38,0 | 27,0 | 61,1 | 242 |

- Một luồng: phần việc thật của tiêu hao ở SMT (đọc dòng vật tư, load đang mở; 5 INSERT `SerialComponents`; 5 UPDATE lot) là **~6 ms**.
- Tám luồng: SMT tăng thêm ~25 ms, phần lớn là **chờ khóa**. Mọi board đều trừ cùng 5 dòng `MaterialLots` (5 reel đang nạp), và khóa dòng được giữ tới khi transaction commit: đúng hiện tượng "hot row" của bộ đếm `QtyGood` ở M3, nhân 5. TEST (không có vật tư) không đổi.
- Vẫn trong mục tiêu < 100 ms. Và 8 scan **cùng lúc trên cùng một trạm SMT** là tình huống khó hơn thật: một trạm SMT ra khoảng 1 board / 15–30 giây.

## Quyết định

1. **Tham số `varchar` cho truy vấn SQL viết tay (đã sửa, có test).** Bản bước 7 gửi `nvarchar(4000)` (mặc định của chuỗi .NET); câu ngược theo serial thành Index Scan, **0,65 → 82,6 ms**, và sẽ tăng theo số serial. Đây đúng là bẫy của M3, lặp lại ở chỗ không có EF model bảo vệ. `TraceQueries` giờ khai báo `SqlDbType.VarChar` đúng độ dài cột; test `Keys_are_sent_as_varchar_so_the_indexes_can_seek` ghi lại tham số thật gửi xuống DB (đột biến: bỏ sửa → test đỏ). Kèm theo: tham số có độ dài cố định sẽ **cắt** giá trị dài hơn, nên mã dài hơn cột trả rỗng ngay (test riêng).
2. **Giữ index theo spec `(LotId) INCLUDE (SerialId)`, không dùng covering.** Covering bớt 1/3 logical reads nhưng không nhanh hơn (164 so với 144 ms, trong sai số), vì thời gian nằm ở join `Serials` và các bảng nhỏ; đổi lại index to thêm 96 MB trên bảng ghi nhiều nhất của module. Không có index thì 232 ms hôm nay, nhưng tăng tuyến tính theo kích thước bảng (quét 5 triệu dòng; 100 triệu dòng sẽ là vài giây).
3. **Giữ một câu `UPDATE` mỗi lot mỗi scan.** Đo được chi phí khi tranh chấp, nhưng tình huống đó không có ngoài đời ở mức này. Nếu cần: cộng dồn số trừ theo lot rồi cập nhật định kỳ (job), hoặc chỉ trừ khi tháo reel; cả hai làm `RemainingQty` trên màn hình kém tức thời.
4. **Không đổi câu truy vấn để ép seek trên `Serials`.** Optimizer đang đúng ở cỡ dữ liệu này; ép bằng hint sẽ sai khi dữ liệu đổi.

## Bẫy khi đo

Lần chạy đầu của lượt "có tiêu hao" dừng giữa chừng: trạm TEST đột nhiên trả `WrongWo`. Container `mes-simulator` (M4) gọi API ở `localhost:5157`, đúng cổng của API đang trỏ vào DB đo, và đã mở phiên ở trạm cho WO khác. Đo lại sau khi `docker stop mes-simulator` (và tháo các reel lượt trước để lượt "không tiêu hao" không vô tình ghi tiêu hao). Bài học: trước khi đo, liệt kê **mọi** tiến trình có thể gọi vào hệ thống.
