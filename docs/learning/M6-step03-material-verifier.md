# M6 · Bước 3 — MaterialVerifier và FifoAdvisor: kiến thức cần nắm

> Tài liệu học cho bước 3 của [module-06](../../spec/module-06-material-traceability.md) (spec mục **1.2** "Setup và Material
> verification", **1.3 A**, **2.4**, R2–R5, R15).
> Bước trước: [M6-step02b](M6-step02b-iqc.md). Bước sau: [M6-step04](M6-step04-load-material.md).
> Code: `src/MiniMes.Domain/Material/MaterialVerifier.cs`, test `tests/MiniMes.UnitTests/Material/MaterialVerifierTests.cs`.

---

## A. Nghiệp vụ: kiểm tra vật tư lúc nạp (setup verification)

Operator scan reel trước khi gắn lên máy; hệ thống trả lời một trong ba mức:

| Mức | Khi nào | Kiosk |
| --- | --- | --- |
| Accepted | Đúng item, đúng trạm, dùng được, không có reel cũ hơn | Xanh, ghi `MaterialLoad` |
| Warning | Còn reel **cũ hơn** cùng item dùng được (FIFO) | Vàng, gợi ý reel cũ; supervisor được cho qua, có lý do |
| Rejected | Sai item, sai trạm, Hold, chưa qua IQC, hết hàng, hết hạn | Đỏ, không có nút cho qua |

Vì sao FIFO chỉ là cảnh báo: đó là chính sách tồn kho, có ngoại lệ hợp lý (reel cũ đang nghi vấn, khách yêu cầu nhà cung
cấp khác). Chặn cứng thì người dùng sẽ tìm cách lách. Còn lỗi chất lượng (sai item, hết hạn, chưa IQC) thì không có ngoại lệ.

## B. Thiết kế hàm thuần

- **Không đọc DB, không đọc đồng hồ**: handler (bước 4) nạp dữ liệu, truyền `utcNow` từ `TimeProvider`. Nhờ vậy test được biên
  "hết hạn đúng lúc này" và "còn 1 giây".
- **`LotFacts` thay vì entity**: entity có setter private, muốn test lot Hold/Consumed thì phải thêm phương thức chỉ để test.
  Một record bản chụp mô tả được mọi trạng thái bằng `with`.
- **Thứ tự kiểm là hợp đồng**: "reel này là gì" (BOM, trạm) → "nó đang ra sao" (Hold, IQC, hết hàng) → "ngày" (hết hạn) →
  FIFO. Ví dụ: reel sai item mà đang Hold báo "không có trong BOM", vì đó mới là điều operator cần sửa.
- **Danh sách bước của trạm**: một item dùng ở hai bước (keo ở SMT và ASSY) phải nạp được ở cả hai trạm; snippet
  `FirstOrDefault` của spec sẽ chọn nhầm bước đầu.
- **FIFO "nhỏ hơn hẳn"**: các reel cùng lần giao có cùng `ReceivedAt`, không reel nào "cũ hơn" reel nào.

## C. Kỹ thuật test: bảng + đột biến

- Một `[Theory]` với 23 dòng, mỗi dòng có **tên tình huống** (thông báo lỗi in ra tên đó), dựng dữ liệu bằng `with` từ một
  reel chuẩn.
- **Mutation testing bằng tay**: sửa `<` thành `<=` → đúng một test đỏ ("same ReceivedAt"). Test không đỏ khi code sai thì
  test đó không bảo vệ gì. Công cụ tự động: Stryker.NET (chỉ cần biết tên).

## D. Câu hỏi phỏng vấn

1. **"Làm sao chặn nạp nhầm reel?"** Scan nhãn lot, đối chiếu item với BOM **snapshot của WO** và bước của trạm; sai thì đỏ, ghi
   log. Giới hạn: nếu BOM có cả 10K và 100K ở cùng trạm thì cần kiểm theo **slot** (bảng slot của chương trình máy, bước 10).
2. **"Vì sao không đặt logic này trong controller/kiosk?"** Một chỗ duy nhất, hàm thuần, test không cần DB; kiosk, API, import
   dùng chung.
3. **"FIFO hay FEFO?"** FIFO theo ngày nhận; vật tư có hạn dùng ngắn thì FEFO (sắp theo `ExpiryAt`), chỉ đổi khóa sắp xếp.
