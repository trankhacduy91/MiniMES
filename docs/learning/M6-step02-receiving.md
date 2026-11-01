# M6 · Bước 2 — Nhập lot NVL và in nhãn lot: kiến thức cần nắm

> Tài liệu học cho bước 2 của [module-06](../../spec/module-06-material-traceability.md) (spec mục **1.2**, **1.3 A**, **1.5**, R1, R15).
> Bước trước: [M6-step01](M6-step01-data-model.md). Bước sau: [M6-step02b](M6-step02b-iqc.md).
> Code: `src/MiniMes.Application/Material/MaterialLots.cs`, `src/MiniMes.Application/Labels/LotLabelBuilder.cs`,
> `src/MiniMes.Domain/Material/LotNumberFormat.cs`, BackOffice `Views/Material/ReceivingView.xaml`. Ảnh: [m6-receiving.png](../images/m6-receiving.png).

---

## A. Nghiệp vụ: nhận hàng ở kho

1. Nhà cung cấp giao một **supplier lot** (`MUR-2609-A17`) gồm nhiều reel.
2. Kho tạo **một lot MES cho mỗi reel** và dán nhãn ngay. Reel không có nhãn thì không ai nạp lên máy được (kiosk scan nhãn).
3. [v2] Lot mới ở `PendingIqc` (khu chờ kiểm, nhãn có dải "PENDING IQC"). QC kiểm xong mới thành `Available` (bước 2b).
   Hàng rủi ro thấp (túi đóng gói, `SkipIqc`) vào thẳng `Available`.

## B. Quyết định của bước này

| Quyết định | Vì sao |
| --- | --- |
| Role mới `Warehouse` (`wh01`) | "Ai nhận reel này" là câu auditor hỏi; Planner nhận hàng thì sai vai |
| `LotNo` = `L-yyMMdd-######` | Mã hàng dài tới 50 ký tự, `LotNo` tối đa 30. Chỉ số thứ tự làm mã duy nhất; ngày để người đọc |
| Số từ SQL `SEQUENCE` | Hai người nhận hàng cùng lúc vẫn khác số; rollback để lại lỗ hổng, không bao giờ trùng (giống serial M2) |
| Lấy số **sau** mọi kiểm tra | Lần nhập bị từ chối không làm mất số |
| Hạn dùng là ngày giờ nhà máy, chặn từ 00:00 ngày đó | Quy tắc chất lượng: nghi ngờ thì chặn sớm; lưu UTC, hiển thị giờ nhà máy |
| Nhãn ghi `03 SEP 2028` | 03/09 và 09/03 là lỗi kinh điển (spec 1.5); chữ tháng không nhầm được |

## C. Kỹ thuật

- **Hàm thuần cho định dạng**: `LotNumberFormat.Build`, `LotLabelBuilder.Build` test bằng chuỗi, không cần DB hay máy in.
- **ZPL an toàn**: dữ liệu chứa `^` hoặc `~` sẽ thành lệnh máy in, nên bị chặn trước khi in (dùng chung `RequireSafe` với nhãn M2).
- **EF Core và DTO**: `Where`/`OrderBy` trên thuộc tính của DTO tạo bằng **constructor record** (`new LotWithItem(l, i)`)
  không dịch được sang SQL (EF không "nhìn xuyên" tham số constructor). Cách sửa: lọc/sắp xếp trên kiểu khởi tạo bằng
  **member init** (`new LotWithItem { Lot = l, Item = i }`), rồi `Select` sang DTO ở **cuối** câu truy vấn.
- **Hiển thị giờ trong WPF**: DTO giữ UTC; ViewModel đổi sang giờ nhà máy thành chuỗi (`LotRow`), không để XAML tự đổi.

## D. Kiểm tra trên máy thật và bài học

- Nhãn render qua Labelary thật: mã vạch, `EXP 03 SEP 2028`, dải `PENDING IQC` đúng như test chuỗi.
- Script UI Automation bấm "Receive" nhưng không trả lời được hộp thoại Save (hộp thoại Windows 11 không lộ ô tên file đúng
  lúc), nên chạy lại và **nhận hàng hai lần**: 6 lot thay vì 3. Lot không xóa được (đúng thiết kế), nên giữ làm dữ liệu IQC.
  Bài học: thao tác có tác dụng phụ (tạo dữ liệu) thì kiểm bằng API/unit test; UI script chỉ dùng cho thao tác chỉ đọc.

## E. Câu hỏi phỏng vấn

1. **"Lot NVL khác supplier lot thế nào?"** Supplier lot là một lần giao của nhà cung cấp; MES chia thành lot theo từng reel
   để biết reel nào đang trên máy và còn bao nhiêu.
2. **"Sinh mã lot không trùng khi nhiều người nhập cùng lúc?"** SQL `SEQUENCE`, không `MAX + 1`; unique index trên `LotNo` là
   lưới an toàn cuối.
3. **"Hạn dùng lưu thế nào?"** UTC trong DB, nhập và in theo ngày giờ nhà máy, so sánh bằng `TimeProvider` (test được biên).
