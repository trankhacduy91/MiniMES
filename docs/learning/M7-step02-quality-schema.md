# M7 · Bước 2 — Entity, configuration, migration: kiến thức cần nắm

> Tài liệu học cho bước 2 của [module-07](../../spec/module-07-quality.md) (spec mục **2.3**, **2.3b**, **2.5**, **2.5b**, **2.6**; bản đồ ở [`docs/quality-erd.md`](../quality-erd.md)).
> Code: `src/MiniMes.Domain/Quality/`, `Configurations/Quality/QualityConfigurations.cs`, migration `M7_Quality`, `Seed/QualitySeeder.cs`.

## A. Mỗi bảng, DB tự ép quy tắc nào

Ý chung của dự án: quy tắc nào hai request chạy đua có thể phá thì **DB phải ép**, code kiểm trước chỉ để báo lỗi đẹp hơn.

| Quy tắc | Cách ép | Nếu chỉ kiểm trong code thì sao |
| --- | --- | --- |
| Q5: mỗi (plan, rule) chỉ có một `SpcAlert` đang mở | Filtered unique `UX_SpcAlerts_OpenPerRule … WHERE [Status] = 'Open'` | Hai trạm đo cùng lúc đều thấy "chưa có alert" và cùng chèn: QC nhận hai alert cho một sự cố |
| Q3: mỗi plan chỉ có một baseline đang dùng | Filtered unique `… WHERE [IsActive] = 1` | Hai lần "recompute" song song để lại hai baseline active; chart không biết vẽ cái nào |
| 2.3b: sampler chạy lại không tạo điểm đôi | Unique `(MachineTagId, SourceTs)` lọc `MachineTagId IS NOT NULL` | Điểm trùng làm rule run 7 đếm sai |
| 2.3: trạm gửi lại không ghi đôi | Unique `ClientRequestId` lọc NOT NULL | Một board có hai số đo, chart lệch |
| Q16: số đo là bằng chứng | Trigger `INSTEAD OF UPDATE, DELETE` | Ai đó sửa 3,47 thành 3,46 cho board "qua" |
| Q17: FAI không Pass khi kiểm tự động đỏ | CHECK `[Result] <> 'Pass' OR [AutoChecksPassed] = 1` + kiểm trong constructor | Sửa tay trong SSMS vẫn qua được |
| Hold là lịch sử | Trigger `TR_HoldRecords_ReleaseOnly` | Xóa dòng Hold thì mất dấu "ai đã giữ, vì sao" |

Filtered index phải viết bằng **chữ** (`'Open'`). Đó là lý do enum của M7 lưu dạng chuỗi. `MaterialLotStatus` vẫn là số như M6, vì câu trừ tồn kho của M6 so sánh số.

## B. Trigger "chỉ được release": AFTER khác INSTEAD OF thế nào

- `Measurements` và `FaiRecords` dùng `INSTEAD OF UPDATE, DELETE` + `THROW`: mọi thay đổi bị chặn trước khi chạm dữ liệu.
- `HoldRecords` phải cho phép **một** kiểu UPDATE (release, gắn NCR), nên dùng `AFTER UPDATE, DELETE` và kiểm tra:
  1. Không có dòng trong `inserted` mà có trong `deleted` nghĩa là đây là DELETE: chặn.
  2. `UPDATE(Reason)`, `UPDATE(TargetId)`… trả true khi cột **có mặt trong SET**, kể cả khi giá trị không đổi. EF chỉ đưa vào SET những cột đã đổi, nên kiểm như vậy là đủ.
  3. `deleted.ReleasedAt IS NOT NULL` nghĩa là dòng đã release mà vẫn bị sửa: chặn.
- `THROW` trong trigger AFTER sẽ rollback cả câu lệnh (và transaction), nên dữ liệu không bị đổi một nửa.

Câu hỏi phỏng vấn hay gặp: "Trigger có làm chậm không?". Trigger chặn chỉ chạy khi có UPDATE/DELETE, mà các bảng này gần như chỉ INSERT, nên chi phí gần bằng 0.

## C. Chụp lại giới hạn (snapshot) trên số đo

`Measurement` lưu `Usl`/`Lsl` lúc đo cùng `Result`, và `Result` do **server** tính (`InspectionPlan.Judge`). Unit test `Measurement_keeps_the_limits_of_its_time` cho thấy:
- 3,470 đo trước khi nới USL lên 3,500 vẫn là NG.
- 3,470 đo sau đó là OK.

Đây là cách giữ ý của Q15 mà không cần "plan version". Đổi spec đi qua audit vì `InspectionPlan` là `IAudited`.

## D. Baseline seed không có MR̄: vì sao để null

Spec chỉ cho mean 3,302 và σ 0,040, không cho dữ liệu gốc. Có thể suy MR̄ = 1,128σ, nhưng đó là **bịa** một số chưa ai đo. `MrBar`/`UclMr` để null, `SampleCount = 0`, lý do ghi "Initial baseline". Khi QC chạy `RecomputeControlLimits` (bước 5) trên dữ liệu thật, dòng mới sẽ có đủ.

Cùng nguyên tắc với "data gap" ở 2.3b: không có dữ liệu thì để trống, không bịa.

## E. Khóa đa hình (polymorphic) không có FK

`HoldRecord.TargetId` và `NcrItem.TargetId` trỏ vào `Serials` **hoặc** `MaterialLots` tùy `TargetType`, nên SQL Server không có FK nào khai báo được. Cái giá phải trả: DB không chặn được `TargetId` rác. Bù lại bằng cách:
- Bản ghi Hold hàng loạt sinh ra từ `INSERT … SELECT` trên chính bảng `Serials`/`MaterialLots` (bước 9), nên Id luôn có thật.
- Không có API nào cho gõ tay `TargetId`.

Một cách khác là hai cột `SerialId`/`LotId` nullable + CHECK "đúng một cột có giá trị" (như `SerialComponents` ở M6 bước 10b). Cách đó có FK nhưng mọi câu SQL phải rẽ nhánh theo cột. Bước 1 đã chọn `TargetType + TargetId` để câu Hold/Release chỉ có một dạng.

## F. Câu hỏi phỏng vấn

1. **"Sao không lưu Hold bằng cờ `IsHold` trên serial?"** Vì Release phải trả serial về đúng trạng thái cũ (`StatusBeforeHold`), Hold có thể chồng nhau (Q8, đếm dòng `HoldRecord` active), và cần lịch sử ai giữ, vì sao.
2. **"Filtered unique index dùng khi nào?"** Khi quy tắc chỉ đúng với một **tập con** của bảng: "một alert *đang mở*", "một baseline *đang dùng*". Các dòng đã đóng là lịch sử, có bao nhiêu cũng được.
3. **"Làm sao biết migration Down chạy được?"** Chạy thử: `database update` về migration trước rồi Up lại. Ở M6 bước 10, Down từng bị chính trigger append-only chặn ([incidents](../incidents.md)). Bước này Down xóa trigger trước rồi mới xóa bảng.
