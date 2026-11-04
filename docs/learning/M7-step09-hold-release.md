# M7 · Bước 9 — Hold/Release xuyên module

> Tài liệu học cho bước 9 của [module-07](../../spec/module-07-quality.md) (spec mục **2.6**, **2.5** bảng hiệu ứng, Q6–Q10, Q12). Code:
> - `src/MiniMes.Application/Quality/Holds/` (`HoldSelection.cs`: chọn target; `HoldWrites.cs`: ghi set-based; `HoldUseCases.cs`)
> - `src/MiniMes.Infrastructure/Quality/HoldWriter.cs` (INSERT … SELECT)
> - `Serial.ApplyDisposition`, `MaterialLot.ApplyDisposition`; handler `ApproveNcrCommand`
> - `HoldPanelViewModel` (hộp thoại dùng chung), `HoldsViewModel`

## A. Một nguồn sự thật, nhiều điểm chặn

Hold là **trạng thái** của serial/lot (`Status = Hold`), không phải một cờ riêng hay một message gửi đi. Các điểm chặn đã đọc `Status` từ các module trước:

| Điểm chặn | Module | Đọc |
| --- | --- | --- |
| Route check khi scan (kể cả Pack) | M3 | `Serial.Status = Hold` → `SerialHold` (nền đỏ) |
| Nạp reel lên máy | M6 | `MaterialLot.Status = Hold` → `LotOnHold` |
| Ship pallet | M3b | còn serial Hold trong pallet → 422 |

Vì vậy bước này **không sửa module nào khác** để "báo tin": chỉ cần ghi đúng `Status` trong một transaction. Nếu sau này tách thành service riêng thì mới cần sự kiện (spec mục 4).

Bảng `HoldRecord` (một dòng một target) là **lý do và lịch sử**: ai, khi nào, vì sao, đợt nào (`BatchId`), chọn bằng gì (`Scope`/`ScopeRef`), thuộc NCR nào.

## B. Năm cách chọn target, một cách ghi

| Scope | Chọn ra |
| --- | --- |
| `Lot` | lot + các lot **cùng lần giao** (supplier lot) + lot **tách ra** (genealogy), và mọi serial đã dùng chúng (`SerialComponent`). Lot đã Consumed không bị Hold, nhưng serial làm từ nó **vẫn** bị Hold. |
| `Serials` | danh sách serial (tối đa 500) |
| `Carton` / `Pallet` | serial còn trong thùng/pallet (bỏ dòng đã unpack). Pallet đã ship vẫn bị Hold trên dữ liệu; preview cảnh báo "notify the customer". |
| `MachineWindow` | serial scan OK ở trạm thuộc work center của máy trong `[From, To)`. Dùng cho SPC của máy (không có serial). Rộng hơn thực tế một chút: thà dư còn hơn sót. |

Chỉ phần **chọn** khác nhau (LINQ, dễ đọc). Phần **ghi** giống nhau và set-based:
1. `INSERT HoldRecords … SELECT … FROM OPENJSON(@ids)`: một câu lệnh cho 20.000 serial, ghi trong Infrastructure. Đây là chỗ duy nhất EF không diễn đạt được.
2. `ExecuteUpdate` serial/lot: `StatusBeforeHold = CASE WHEN Status <> Hold THEN Status ELSE StatusBeforeHold END, Status = Hold`. T-SQL tính vế phải bằng **giá trị cũ**, nên một câu `UPDATE` làm được cả hai việc.
3. Một dòng `AuditLog` cho cả đợt. SQL thô và `ExecuteUpdate` **đi vòng qua** audit interceptor (spec 2.6 bẫy 1).

Serial `Scrapped` không bao giờ bị Hold (Q10). Phép join trong câu INSERT kiểm lại trạng thái lúc ghi, nên một board vừa bị scrap sau lúc preview cũng không lọt vào.

## C. Release và Hold chồng nhau (Q8)

Hai NCR cùng nghi một board: board có hai dòng Hold active. Release đợt thứ nhất thì:
1. đóng các dòng của đợt đó (`ReleasedAt`, người, ghi chú bắt buộc theo Q9);
2. chỉ trả lại trạng thái cũ cho target **không còn dòng active nào khác**. Board vẫn còn dòng Hold của NCR kia thì vẫn là `Hold`, và kết quả trả về báo "still held 1".

Release trả đúng trạng thái trước Hold, **kể cả `Completed`**. Đây là lý do có cột `StatusBeforeHold`: nếu chỉ có cờ `IsHold`, board đã xong sẽ quay về sai trạng thái.

## D. Quyết định NCR tác động lên serial/lot (quyết định của bạn: chỉ target của NCR)

`ApproveNcrCommand`, trong **một transaction** với quyết định:
1. `ncr.Approve()`: Domain kiểm Q11 trước khi thay đổi gì.
2. Release các dòng Hold **của NCR này, trên target của NCR**.
3. Áp hiệu ứng (Domain, hàm thuần):

| Disposition | Serial | Lot |
| --- | --- | --- |
| UseAsIs | giữ trạng thái đã khôi phục | IqcRejected → Available (cách duy nhất, M6 R17) |
| Rework | → `Repair` (luồng M3 kiểm lại) | IqcRejected → Available (MVP) |
| Scrap | → `Scrapped` (cuối, có lý do "NCR-…: scrap") | `Scrapped`, RemainingQty = 0 |
| ReturnToVendor | không áp dụng (Q13) | `Returned`, RemainingQty = 0 |

Nếu target vẫn còn Hold của NCR khác (Q8), hiệu ứng **chờ** trong `StatusBeforeHold` và xảy ra lúc Hold cuối được thả. Gate vẫn thấy `Hold` cho tới lúc đó.

**Vì sao chỉ target của NCR:** khi Hold "tất cả theo lot" kèm NCR, NCR ghi target là lot, còn 87 board làm từ lot cũng bị giữ dưới NCR đó. Duyệt Use as is cho lot thì lot được thả, còn **các board vẫn bị giữ** tới khi QC chủ động Release (có ghi chú). Q12 không cho đóng NCR cho tới lúc đó. Không có board nào được thả ngầm. Ví dụ dễ thấy: duyệt Scrap cho lot mà lại tự thả 87 board làm từ lot đó thì không ai chấp nhận được.

## E. Đua Hold với scan

`ExecuteUpdate` đổi `rowversion` của serial, nên scan đang giữ bản cũ sẽ lệch lúc `SaveChanges`, phải retry và thấy Hold. Test đua (5 vòng, hai luồng thật) còn bắt thêm một khe khác: Hold commit **giữa** lúc route check đọc và lúc nạp entity để ghi, khiến kiosk nhận 422 thay vì `SerialHold`. Đã sửa bằng cách coi "serial đổi giữa hai lần đọc" là xung đột để đánh giá lại. Xem `docs/incidents.md`.

Bất biến được test kiểm: dù thứ tự nào, board cuối cùng là `Hold`. Nếu scan vào trước thì `StatusBeforeHold` là trạng thái **sau** scan, không có lần ghi nào bị mất.

## F. Màn hình

- **Truy xuất (M6) → "Hold all…"**: mở panel Hold. Panel preview ngay ("87 boards (55 InProcess, 24 Completed…). Lots held… Lots left as they are…"), cảnh báo nếu có board đã ship. Người dùng nhập lý do (bắt buộc), tick "Open an NCR" nếu cần, rồi bấm Hold. Đổi target thì preview bị xóa: thứ được Hold luôn là thứ vừa xem. Nút "Hold lot" cũ của M6 (không ghi HoldRecord) đã bỏ, theo lựa chọn của bạn.
- **SPC chart của máy → "Hold serials in window"**: khoảng mặc định từ 20 phút trước alert tới 10 phút sau, theo giờ nhà máy, sửa được.
- **Menu Holds**: các đợt Hold còn hiệu lực, Release (hỏi ghi chú, báo số board trả về và số còn bị giữ), "New hold…" theo serial, thùng, pallet.

## G. Câu hỏi phỏng vấn

1. **"Hold một lot dùng cho 20.000 board, làm sao cho nhanh và an toàn?"** Chọn bằng truy xuất xuôi, ghi bằng `INSERT … SELECT` và `UPDATE … JOIN`, một transaction. Không nạp 20.000 entity. Nhớ tự ghi audit, vì SQL thô đi vòng qua interceptor.
2. **"Thả Hold thì board về trạng thái nào?"** Trạng thái trước Hold (`StatusBeforeHold`), và chỉ khi không còn Hold nào khác (Q8).
3. **"Các module khác biết Hold bằng cách nào?"** Cùng một DB, cùng nguồn sự thật `Status`; mỗi gate đọc nó. Không cần message bus trong modular monolith.
4. **"Hold và scan cùng lúc?"** RowVersion cộng đánh giá lại khi dữ liệu đổi giữa lúc đọc và lúc ghi; có test đua chứng minh. Kể incident 422.
