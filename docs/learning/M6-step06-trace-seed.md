# M6 · Bước 6 — Seed kịch bản truy xuất: kiến thức cần nắm

> Tài liệu học cho bước 6 của [module-06](../../spec/module-06-material-traceability.md) (spec mục **3**, bảng "Dữ liệu kiểm thử chuẩn").
> Bước trước: [M6-step05](M6-step05-consumption.md).
> Code: `src/MiniMes.Infrastructure/Persistence/Seed/TraceabilitySeeder.cs`. Test: `tests/MiniMes.IntegrationTests/Traceability/TraceabilitySeedTests.cs`.

## A. Vì sao cần một bộ dữ liệu chuẩn

Truy vấn truy xuất (bước 7) là đệ quy và có nhiều nhánh: lot → serial, lot cha → lot con, supplier lot → nhiều reel. Muốn biết truy vấn đúng hay sai, cần một bộ dữ liệu mà **kết quả tính được bằng tay**. Bộ dữ liệu đó gọi là *test oracle*:

| Lot | Item | Quan hệ | Board dùng |
| --- | --- | --- | --- |
| `L-CAP-A` | `CAP-100NF` | supplier lot `SUP-S1` | 1–120 |
| `L-CAP-P` | `CAP-100NF` | `SUP-S1`, reel gốc đã chia | không dùng trực tiếp |
| `L-CAP-B` | `CAP-100NF` | con của `L-CAP-P` (`LotGenealogy`) | 121–200 |
| `L-RES-A` | `RES-100K` | `SUP-R1` | cả 200 |

Đáp án mà bước 7 phải ra:
- **Truy xuất xuôi** (từ lot ra board):
  - Từ `L-CAP-A` được 120 board, từ `L-CAP-B` được 80.
  - Từ `L-CAP-P` được 80: lot này không lên máy, nhưng con của nó (`L-CAP-B`) có.
  - Từ supplier lot `SUP-S1` được 200.
- **Truy xuất ngược** (từ board ra lot):
  - Board 150 → `L-CAP-B` (cấp 0), `L-CAP-P` (cấp 1), `L-RES-A`.
  - Board 50 → `L-CAP-A`, `L-RES-A`.

Mỗi lot đều có lý do để có mặt:
- **`L-CAP-P`** kiểm tra truy vấn có đi qua genealogy hay không. Ngoài đời, reel bị chia khi chuyển một phần sang line khác, hoặc khi kem hàn được chia ra lọ nhỏ.
- **Ranh giới board 120/121** kiểm tra việc đổi reel giữa chừng.
- **`SUP-S1`** kiểm tra kịch bản nhà cung cấp báo lỗi cả một lô giao hàng.

## B. Thiết kế

| Câu hỏi | Trả lời |
| --- | --- |
| Ghi dữ liệu thế nào? | Giống luồng thật: `ScanLog` cho từng board, `MaterialLoad` (ai nạp reel nào, lúc nào), rồi `SerialComponent` trỏ về đúng scan. Bước 7 cần join ra operator và người nạp, nên thiếu các bảng này thì test không đủ |
| Vì sao không gọi API scan 200 lần? | Chậm, và phải dựng phiên trạm, gate vật tư… Đây là dữ liệu **lịch sử**: WO đã `Closed`, mọi reel đã tháo, nên không hiện trong dispatch list hay checklist vật tư, và simulator không đụng tới |
| "Xóa rồi tạo lại" như spec? | Không được: `SerialComponents` và `LotGenealogy` chỉ cho INSERT (trigger, R11). Hồ sơ truy xuất không được sửa hay xóa, kể cả trong DB test. Thay vào đó mỗi test dùng một **tag**: `L-CAP-A-{tag}`, `SUP-S1-{tag}`. Gọi lại cùng tag thì trả về kịch bản đã có |
| Đặt ở đâu? | Infrastructure, cạnh `OeeDemoSeeder`. DB Development tự seed bản không tag khi khởi động (mã đúng như bảng trên, để demo bước 8); test gọi seeder có tag |

## C. Test "đếm bằng SQL"

- Test đếm bằng **SQL thô** (`SqlQueryRaw<int>("SELECT COUNT(*) AS Value …")`), không qua LINQ. Đây là cách kiểm độc lập với model EF: nếu cấu hình EF sai (tên bảng, quan hệ), LINQ có thể sai theo đúng cách đó và test vẫn xanh.
- Với `SqlQueryRaw` kiểu vô hướng, EF yêu cầu cột tên là **`Value`**.
- Ngoài con số 200 / 400 / 1, test còn khóa những thứ bước 7 sẽ dựa vào:
  - Board 120 dùng `L-CAP-A`, board 121 dùng `L-CAP-B`.
  - Cạnh P → B có số lượng 2.000.
  - Mỗi component trỏ đúng scan của **chính board đó**.

## D. Câu hỏi phỏng vấn

1. **"Kiểm truy vấn truy xuất thế nào?"** Bằng một bộ dữ liệu nhỏ, tính tay được (test oracle), có đủ ca khó: genealogy, đổi reel giữa chừng, truy từ supplier lot. Sau đó mới đo hiệu năng trên dữ liệu lớn (bước 9).
2. **"Dữ liệu chỉ cho INSERT thì test dọn dẹp thế nào?"** Không dọn: mỗi test tạo dữ liệu riêng với mã duy nhất. Container DB bị bỏ sau mỗi lần chạy. Ràng buộc của sản phẩm không được nới ra chỉ để test cho tiện.
