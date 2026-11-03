# M7 · Bước 5 — Use case ghi số đo: kiến thức cần nắm

> Tài liệu học cho bước 5 của [module-07](../../spec/module-07-quality.md) (spec mục **2.3**, **2.4**, Q1, Q3, Q4, Q5). Code: `src/MiniMes.Application/Quality/`, `src/MiniMes.Api/Controllers/QualityController.cs`, test `tests/MiniMes.IntegrationTests/Quality/InspectionTests.cs`.

## A. Một request, một transaction, chín việc

`POST /api/quality/inspections` mang serial, trạm và **danh sách** `(planCode, value)`. Server làm theo thứ tự sau:

| # | Việc | Ai làm |
| --- | --- | --- |
| 0 | Request cũ gửi lại (cùng `ClientRequestId`) thì trả lại câu trả lời cũ | handler |
| 1 | Plan đúng item của board, đúng công đoạn của trạm | handler (422 nếu sai) |
| 2 | So spec: NG nếu có giá trị ngoài USL/LSL | `InspectionPlan.Judge` (Domain, `decimal`) |
| 3 | Route check + ghi scan OK/NG | `ScanExecutor` của M3, dùng lại nguyên vẹn |
| 4 | Lưu `Measurement`, kèm giới hạn đã dùng | `Measurement.OfSerial` |
| 5–8 | Đánh giá rule tại điểm mới, mở alert nếu chưa có alert mở | `ISpcEvaluator` |
| 9 | Commit | handler |

Vì sao **một** request thay vì "scan" rồi "gửi số đo" riêng:
- **Không lệch.** Không thể có số đo mà không có scan, hay ngược lại (mất mạng giữa hai lần gọi).
- **Không hai nơi quyết định.** Client không tự gửi "NG"; server tính từ số đo.
- **Route check vẫn áp dụng.** Board chưa tới bước, hoặc đang Hold, sẽ bị chặn trước khi số đo được ghi.

Bước 3 đứng trước bước 4 vì `ScanExecutor` có thể rollback về savepoint và `ChangeTracker.Clear()` khi đua ghi serial. Thêm số đo **sau** scan thì không bị xóa mất.

## B. `ISpcEvaluator`: đánh giá một điểm

- **Baseline đã lưu** (Q3). Không có baseline thì vẫn giữ điểm, không đánh giá gì.
- **Cửa sổ kết thúc tại chính điểm đó**, theo `MeasuredAt` rồi `Id` (Q4). Một lần gửi lại đến muộn sẽ được đánh giá giữa các điểm cùng thời với nó.
- **Chỉ lấy `WindowSize` điểm** (7 điểm), không tải cả chuỗi.
- **Dedupe hai lớp (Q5):**
  1. Kiểm "đã có alert mở cho (plan, rule)?" để tránh tạo trùng trong trường hợp thường.
  2. Filtered unique index `UX_SpcAlerts_OpenPerRule` cho trường hợp đua. Bên thua `RollbackToSavepoint` và detach entity, nên số đo và scan của nó vẫn được giữ, chỉ không có alert mới.
- Bước 6 (sampler của lò reflow) gọi lại đúng service này. Đó là lý do tách nó ra.

## C. Hai bẫy dữ liệu nhỏ

- **Hơn 6 chữ số thập phân:** cột là `decimal(18,6)`, nên 3,4650004 được so spec là NG nhưng lưu thành 3,465000, đọc lại thấy OK. Validator từ chối từ đầu.
- **Hiển thị:** `decimal(18,6)` đọc lên là `3.135000`. Message dùng `0.######` + `InvariantCulture`, nên hiện `3.135`.

## D. Chart: đánh giá kèm ngữ cảnh

`GET /api/quality/spc/{planId}/chart?take=100` tải thêm 6 điểm trước cửa sổ. Điểm đầu của cửa sổ vì vậy được đánh giá cùng những điểm thật sự đứng trước nó. Test: xem 5 điểm cuối của chuỗi 8 điểm, run 7 vẫn báo ở điểm 7 và 8, giống hệt khi xem cả chuỗi.

Cp/Cpk trên chart là của **cửa sổ đang xem** so với spec hiện tại ("quy trình bây giờ"); UCL/LCL là của **baseline**. Hai thứ khác nhau và không trộn lẫn.

## E. Recompute: Phase I có kiểm soát

- QC chọn khoảng `[From, To)` khi quy trình ổn định, ghi lý do. Cần ≥ 20 điểm (Q3).
- Dòng baseline cũ chuyển `IsActive = false` và được giữ làm lịch sử.
- Ghi bằng hai lần `SaveChanges` trong một transaction: tắt dòng cũ trước, thêm dòng mới sau. Filtered unique "một baseline active" vì vậy không bao giờ thấy hai dòng. Hai QC bấm cùng lúc thì một người nhận 409.

## F. Bài học đáng kể nhất: race có tồn tại hay không tùy mức cô lập

Thử đột biến (bỏ `catch`) cho thấy test đua qua HTTP **không chạm race** ([incidents](../incidents.md)). Không bật `READ_COMMITTED_SNAPSHOT` thì câu đọc cửa sổ điểm của request sau phải chờ dòng chưa commit của request trước, nên hai request tự xếp hàng. Test ép race dùng `QueryBarrier` cho cả hai cùng đi qua câu kiểm, rồi assert barrier đã nhả.

Câu hỏi phỏng vấn tốt: **"Bật RCSI thì điều gì thay đổi?"** Đọc không còn chờ ghi, nên race "cả hai thấy chưa có" sẽ xuất hiện thật. Code này vẫn đúng nhờ unique index + savepoint; một code chỉ dựa vào câu kiểm `AnyAsync` thì sẽ ra hai alert.

## G. Câu hỏi phỏng vấn

1. **"Vì sao client không tự gửi OK/NG?"** Hai nơi tự quyết thì sẽ có lúc mâu thuẫn. Giới hạn spec nằm ở server; client có thể dùng bản cũ.
2. **"Gửi lại request sau timeout thì sao?"** `ClientRequestId` → trả lời cũ, `Duplicate = true`. Không có id thì rule "scan trùng trong 5 giây" của M3 vẫn chặn được, nhưng không trả lại đúng số đo cũ.
3. **"Vì sao không tính lại UCL sau mỗi điểm?"** Q3: quy trình trôi chậm thì giới hạn trôi theo, không bao giờ báo. Chỉ QC tính lại có chủ đích, có lý do, có lịch sử.
