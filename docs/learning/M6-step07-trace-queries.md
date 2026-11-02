# M6 · Bước 7 — Truy vấn truy xuất xuôi/ngược bằng recursive CTE: kiến thức cần nắm

> Tài liệu học cho bước 7 của [module-06](../../spec/module-06-material-traceability.md) (spec mục **2.2**, **2.3**, R12).
> Bước trước: [M6-step06](M6-step06-trace-seed.md). Bước sau: [M6-step08](M6-step08-trace-screen.md).
> Code: `src/MiniMes.Application/Traceability/TraceQueries.cs` (interface, kiểu dòng), `src/MiniMes.Infrastructure/Traceability/TraceQueries.cs` (SQL).
> Test: `tests/MiniMes.IntegrationTests/Traceability/TraceQueriesTests.cs`.

## A. Nghiệp vụ: hai câu hỏi của mọi cuộc recall

- **Xuôi (forward)**, hỏi khi nhà cung cấp hoặc QC báo "reel này lỗi": nó đã vào những board nào?
  - Có thể bắt đầu từ một lot (reel), hoặc từ một **supplier lot**, tức cả một lô giao hàng gồm nhiều reel.
  - Phải đi qua **genealogy**: reel `L-CAP-P` chưa từng lên máy, nhưng phần tách ra từ nó (`L-CAP-B`) thì có. Hỏi `L-CAP-P` mà trả 0 board là sót 80 board.
- **Ngược (backward)**, hỏi khi khách gửi trả một board hỏng: board này dùng những reel nào?
  - Kết quả gồm cả reel cha, kèm trạm, thời điểm, operator scan và người nạp reel.
  - Dùng để tìm những board khác có cùng nguy cơ, và chứng minh với khách là hồ sơ đầy đủ.

## B. Recursive CTE trong 5 dòng

```sql
WITH LotTree AS (
    SELECT l.Id AS LotId, 0 AS Depth FROM MaterialLots l WHERE l.LotNo = @LotNo   -- anchor: điểm xuất phát
    UNION ALL                                                                     -- SQL Server chỉ cho UNION ALL
    SELECT g.ChildLotId, t.Depth + 1 FROM LotTree t                               -- recursive: tự join chính nó
    JOIN LotGenealogy g ON g.ParentLotId = t.LotId WHERE t.Depth < @MaxDepth      -- đi xuôi cha -> con, có giới hạn
)
```

- Ý tưởng giống explode BOM ở M1.
- Hai nguyên tắc của mục 2.2:
  1. **Hai giai đoạn.** Đệ quy chỉ chạy trên `LotGenealogy` (bảng nhỏ). Bảng khổng lồ `SerialComponents` chỉ được join **một lần**, sau khi đệ quy xong.
  2. **Lọc trùng ở ngoài.** Trong phần đệ quy không dùng được `DISTINCT`. Một board có thể được tìm thấy qua nhiều đường: hai reel của cùng supplier lot, đồ thị hình thoi, hay một vòng. Vì vậy truy vấn ngoài dùng `ROW_NUMBER() OVER (PARTITION BY SerialId ORDER BY Depth)` và giữ `rn = 1`: mỗi board một dòng, kèm lot **gần nhất** trong cây.

## C. Chống vòng: hai lớp

SQL Server không có mệnh đề `CYCLE`. Dữ liệu sai (ví dụ nhập tay A là cha B và B là cha A) sẽ làm đệ quy chạy mãi. Chống bằng hai lớp:

| Lớp | Làm gì | Khi nào kích hoạt |
| --- | --- | --- |
| `WHERE Depth < 10` | Mỗi nhánh dừng ở cấp 10, truy vấn **vẫn trả kết quả** | Luôn chạy trước |
| `OPTION (MAXRECURSION 20)` | SQL Server **hủy** câu lệnh, báo lỗi | Chỉ khi lớp 1 bị bỏ hoặc viết sai |

- Test có vòng cố ý: xuôi vẫn ra 80 board, ngược mỗi lot hiện một lần.
- **Đột biến:** bỏ lớp 1 thì test đỏ với lỗi "maximum recursion 20 has been exhausted" sau 252 ms, tức lớp 2 làm đúng việc của nó: báo lỗi chứ không treo.
- Đừng đặt `MAXRECURSION 0` (không giới hạn).

## D. Gọi từ C#: `SqlQueryRaw<T>`, không cần ADO.NET tay

Snippet trong spec dùng `SqlConnection`/`SqlCommand`, vì lo EF bọc CTE vào subquery. Nhưng EF chỉ bọc khi ta **ghép thêm LINQ** (`.Where`, `.Count`…) lên trên câu SQL. Gọi thẳng `ToListAsync()` thì câu SQL chạy nguyên văn, kể cả `ORDER BY` và `OPTION (...)`. Báo cáo FPY của M3 đã dùng cách này, nên dự án giữ **một** thư viện truy cập dữ liệu.

- Giá trị luôn đi qua `SqlParameter` (không nối chuỗi, tránh SQL injection).
- Phần duy nhất ghép vào chuỗi là điều kiện anchor, và đó là một trong hai **hằng** viết sẵn trong code.
- Kiểu kết quả là class có `init`, tên cột SQL khớp tên thuộc tính.
- Đây là phía **Query** của CQRS: đọc bằng SQL viết tay là bình thường, không bắt buộc LINQ.

## E. Người nạp reel: join theo thời gian, đúng chỗ

Bước 5 đã nói *không* xác định "board dùng lot nào" bằng cách join theo thời gian. Ở đây join theo thời gian lại hợp lý: lot đã biết chắc từ `SerialComponent`, chỉ còn hỏi **ai đã nạp** nó. `OUTER APPLY (SELECT TOP 1 … WHERE LotId = … AND StationId = … AND LoadedAt <= ConsumedAt ORDER BY LoadedAt DESC)` lấy lần nạp đang chạy lúc board được làm. Nếu lần nạp đó có sai, nó chỉ ảnh hưởng thông tin phụ, không làm sai phạm vi truy xuất.

## F. Câu hỏi phỏng vấn

1. **"Recall một supplier lot, truy vấn thế nào?"** Anchor lấy mọi reel của supplier lot, đệ quy xuôi qua genealogy, rồi join `SerialComponents` một lần và lọc trùng theo serial. Có index cho từng bước (mục 2.3).
2. **"Dữ liệu có vòng thì sao?"** Chặn lúc ghi (R12), giới hạn độ sâu trong CTE, và `MAXRECURSION` làm chốt cuối. Có test chèn vòng cố ý.
3. **"Sao không dùng EF LINQ cho truy vấn này?"** LINQ không diễn đạt được đệ quy. Chọn SQL viết tay qua `SqlQueryRaw` vì vẫn dùng connection và transaction của EF, chỉ cần không ghép LINQ lên trên.
4. **"Biết truy vấn đúng bằng cách nào?"** Bằng test oracle tính tay được (bước 6): 120/80/80/200/200, board 150 và board 50. Sau đó mới tính đến hiệu năng (bước 9).
