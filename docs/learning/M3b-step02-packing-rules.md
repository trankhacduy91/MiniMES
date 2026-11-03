# M3b · Bước 2 — Quy tắc đóng gói trong Domain: kiến thức cần nắm

> Tài liệu học cho bước 2 của [module-03b](../../spec/module-03b-packing.md) (spec mục **1.4** P2, P5–P11; **2.3**).
> Bước trước: [M3b-step01](M3b-step01-packing-schema.md).
> Code: `src/MiniMes.Domain/Packing/Carton.cs`, `Pallet.cs`, `CartonSerial.cs`, `PackingRules.cs`, `PackingNumberFormat.cs`.
> Test: `tests/MiniMes.UnitTests/Packing/PackingRulesTests.cs` (50 ca).

## A. Nghiệp vụ: vòng đời thùng và pallet

```
Thùng:   Open ──(đủ 10, tự đóng)──▶ Closed ──(lên pallet)──▶ [trên pallet] ──(pallet Ship)──▶ Shipped
           ▲    └─(Supervisor: thùng lẻ + lý do)─▶ Closed           │
           └──────────── unpack 1 serial (mở lại để bù) ◀──────────┘ (phải gỡ khỏi pallet trước)

Pallet:  Open ──(đủ 20 thùng / Supervisor đóng lẻ)──▶ Closed ──(Ship: không serial Hold, có số phiếu giao)──▶ Shipped
           ▲                                          │
           └──────────── gỡ 1 thùng ra ◀──────────────┘                   Shipped: đóng băng, không sửa nữa
```

- **Thùng lẻ / pallet lẻ:** thùng hoặc pallet cuối WO thì không đủ số lượng. Đây là ngoại lệ, nên chỉ Supervisor được đóng và phải ghi lý do. Pallet lẻ là phần bạn đã duyệt thêm; nếu không có, pallet cuối WO không bao giờ ship được.
- **Unpack:** lấy một serial ra khỏi thùng, ví dụ khi phát hiện serial đó lỗi (spec 1.3, lúc 16:00).
  - Dòng `CartonSerial` của serial đó được giữ lại làm lịch sử.
  - Thùng **mở lại** để scan bù cho đủ. Khi đủ, thùng tự đóng và in lại nhãn (`PrintNo = 2`).
- **Ship:** pallet rời nhà máy. Từ lúc đó dữ liệu là bằng chứng giao hàng, không ai sửa được nữa. Nếu hàng có lỗi, xử lý bằng quy trình với khách (RMA), không sửa DB.

## B. Hai kiểu kết quả: "bị chặn" và "lỗi"

| Tình huống | Cách báo | Vì sao |
| --- | --- | --- |
| Scan serial khác model vào thùng, thùng đầy, scan một thùng chưa đóng để lên pallet | Hàm trả **mã chặn** (`PackBlock.WrongItem`, `PalletizeBlock.CartonNotClosed`…), API trả 200 + `reasonCode` | Operator gặp chuyện này hằng ngày, giống scan sai bước ở M3. Kiosk hiện màu đỏ cùng thông báo lấy từ `.resx` |
| Supervisor đóng thùng lẻ mà không ghi lý do, ship pallet có serial Hold | `DomainException` → 422 | Vi phạm quy tắc nghiệp vụ trong một thao tác có chủ ý |

Thứ tự kiểm tra là một phần của quy tắc, và có test riêng cho thứ tự:
1. Trạng thái: thùng đã đóng thì từ chối, bất kể scan gì.
2. Cái được scan là gì: sai model báo trước sai WO, vì lẫn model là lỗi đắt nhất.
3. Trạng thái của thùng được scan, rồi Hold.
4. Cuối cùng mới đến còn chỗ hay không.

## C. Vì sao số lượng không tăng trong Domain

Hai scan đến cùng lúc vào một thùng đang 9/10. Nếu mỗi request đọc entity (`Qty = 9`), tăng lên rồi lưu, cả hai đều ghi 10, và thùng thực tế có 11 con. Vì vậy:
- `Carton.CheckAdd` chỉ trả lời câu hỏi "có nên thử thêm không".
- Việc tăng thật sự là một câu `UPDATE … SET Qty = Qty + 1 WHERE Qty < 10`, làm ở bước 3. SQL Server xếp hai câu này nối tiếp nhau, và câu thứ hai thấy `Qty = 10` nên không cập nhật dòng nào (spec mục 2.3).

Phần Domain giữ những gì không có tranh chấp: đóng lẻ, unpack, gỡ khỏi pallet, ship. Các thao tác này do một Supervisor làm có chủ ý, và có `RowVersion` bắt trường hợp hai người cùng sửa một dòng.

## D. Bài học từ test: kiểm hết rồi mới đổi

Test đầu tiên cho `ClosePartial` có lý do rỗng đã đỏ. Phương thức có ném lỗi đúng, nhưng thùng đã bị đổi thành `Closed`, vì code gán trạng thái trước khi kiểm lý do. Lỗi tương tự có ở `Ship`. Cách sửa: mỗi phương thức kiểm **mọi** điều kiện trước, sau đó mới đổi bất cứ thứ gì. Mỗi test cho đường từ chối kiểm luôn trạng thái sau lời từ chối, không chỉ kiểm có ném lỗi. Chi tiết ở `docs/incidents.md`.

## E. Số thùng, số pallet

`C261103000123` và `P261103000004`: tiền tố + ngày theo giờ nhà máy + 6 chữ số lấy từ SEQUENCE, không reset theo ngày. Spec ghi 4 chữ số, nhưng như vậy chỉ đủ 9.999 thùng là tràn. Không dùng dấu `-`. Ở trạm Pack, operator scan cả serial `PCBA…`, nhãn thùng và nhãn pallet vào cùng một ô; chỉ nhìn hình dạng mã (`^C\d{12}$`, `^P\d{12}$`) là phân biệt được.

## F. Câu hỏi phỏng vấn

1. **"Đóng nhầm một serial vào thùng thì xử lý thế nào?"**
   - Supervisor unpack có ghi lý do. Dòng lịch sử được giữ lại.
   - Thùng mở lại, scan bù, tự đóng và in lại nhãn.
   - Nếu thùng đang trên pallet thì gỡ khỏi pallet trước. Pallet đã ship thì không sửa dữ liệu nữa.
2. **"Làm sao chắc không ship nhầm hàng đang Hold?"** `Ship` nhận kết quả "có serial Hold trong pallet không", tính bằng `EXISTS` trên các serial ở thời điểm ship (bước 5). Có Hold thì từ chối. Không có cột Hold nào có thể bị lệch.
3. **"Vì sao không tăng `Qty` trong entity cho gọn?"** Đọc, tăng rồi ghi sẽ mất một lần tăng khi hai scan đến cùng lúc, và thùng có 11 con. Câu `UPDATE` có điều kiện để DB tuần tự hóa việc này. Domain chỉ quyết định có nên thử thêm hay không.
