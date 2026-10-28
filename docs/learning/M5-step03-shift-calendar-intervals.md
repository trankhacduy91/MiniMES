# M5 · Bước 3 — Ca, ngày sản xuất, phép hợp khoảng thời gian: kiến thức cần nắm

> Tài liệu học cho bước 3 của [module-05](../../spec/module-05-oee-downtime.md) (spec mục **1.2.8**, **2.2**, 2.3,
> quy tắc 4, 5, 6, 11). Bước trước: [M5-step02](M5-step02-oee-calculator.md).
> Code: `src/MiniMes.Domain/Time/{ShiftCalendar, IntervalMath}.cs`, `src/MiniMes.Domain/Oee/OeeInputBuilder.cs`.

---

## A. Nghiệp vụ: ngày sản xuất (production date)

Ca đêm S3 chạy 22:00 hôm nay → 06:00 hôm sau. Quy tắc: **`ShiftDate` = ngày (giờ nhà máy) lúc ca bắt đầu**. Mọi sự kiện
của ca, kể cả lúc 01:30 sáng hôm sau, mang ngày bắt đầu ca. Gộp theo ngày lịch thì xé ca đêm làm đôi. Gộp theo ngày UTC
thì ca sáng 06:00 (= 23:00 UTC hôm trước) bị đẩy về hôm trước; lỗi này chỉ lộ ở một số giờ nên rất khó tìm.

| UTC (DB) | Giờ nhà máy | Ca | ShiftDate |
| --- | --- | --- | --- |
| 01/10 15:00 | 01/10 22:00 | S3 | 01/10 |
| 01/10 18:30 | **02/10** 01:30 | S3 | **01/10** |
| 01/10 22:59:59 | 02/10 05:59:59 | S3 | 01/10 |
| 01/10 23:00 | 02/10 06:00 | S1 | 02/10 |

**Ngày lễ ([v2] `PlantCalendar`)**: ngày 30/4 nghỉ thì không có cửa sổ ca nào mang ShiftDate 30/4, nên không có
Planned Time và không có OEE. Nhưng 00:00–05:59 ngày 30/4 vẫn là ca đêm của 29/4 (ca đó có chạy). Đây là chỗ spec viết
hơi tắt ("mọi giờ trong ngày"); test đã kiểm đúng theo quy tắc ShiftDate.

## B. `ShiftCalendar.Resolve(utc)`

```
utc ──ConvertTimeFromUtc──▶ giờ nhà máy ──▶ thử ngày bắt đầu = hôm nay, rồi hôm qua
      với mỗi ca được lên lịch ngày đó (IsShiftScheduled của M1): [start, start + duration) có chứa không?
```

- **Thử cả hôm qua** vì ca đêm bắt đầu hôm qua có thể đang chạy.
- **Khoảng nửa mở `[start, end)`**: 06:00 thuộc S1, không thuộc S3. `BETWEEN` trong SQL là khoảng đóng, nên đếm trùng
  sự kiện đúng giây ranh giới.
- **Dùng lại quy tắc M1**: ca có chạy ngày đó không là `ProductionCalendar.IsShiftScheduled(shift, date, entry)` (ngoại
  lệ lịch thắng lịch tuần). Spec 2.2 gợi ý tạo `ShiftDef`/`CalendarException` riêng, nhưng như thế M1 và M5 có hai bản
  quy tắc dễ lệch nhau. Tôi cho `ShiftCalendar` nhận thẳng entity `Shift` và `PlantCalendarDay`.
- **Nghỉ giữa ca tính bằng độ lệch từ đầu ca**: phép trừ `TimeOnly` tự quay vòng (`01:00 − 22:00 = 3 giờ`), nên giờ
  nghỉ sau nửa đêm không cần xử lý riêng. Kết quả là `ShiftWindow.Breaks` ở dạng UTC.

### Bẫy `DateTimeKind`

| Kind | Từ đâu | `ShiftCalendar` xử lý |
| --- | --- | --- |
| `Utc` | `TimeProvider.GetUtcNow().UtcDateTime` | Dùng luôn |
| `Unspecified` | EF Core đọc `datetime2` | Coi là UTC (quy ước cả hệ thống lưu UTC) |
| `Local` | Ai đó gọi `DateTime.Now` | **Ném lỗi**, thay vì lệch âm thầm 7 tiếng |

Id múi giờ `"Asia/Ho_Chi_Minh"` (IANA) chạy được cả Windows (.NET 6+ dùng ICU để đổi id) lẫn Linux CI. Việt Nam không có
giờ mùa hè (DST); ở nơi có DST, ca 22:00–06:00 có thể dài 7 hoặc 9 giờ. Vì vậy cửa sổ ca được đổi sang UTC ngay sau khi tính.

## C. Phép hợp khoảng thời gian (interval union)

```
nghỉ       08:00 ████████████ 08:15
họp   07:55 ███████████████████████ 08:20
hợp   07:55 ███████████████████████ 08:20   = 25 phút (không phải 15 + 25 = 40)
```

Thuật toán `UnionLength`: sắp theo giờ bắt đầu, đi một lượt. Khoảng mới bắt đầu sau điểm cuối hiện tại thì chốt đoạn
cũ và mở đoạn mới; nếu không thì kéo dài điểm cuối. Độ phức tạp O(n log n) vì phải sắp xếp.

Hai công thức của bước này:

```
E = nghỉ ∪ PlannedStop                Planned = |cửa sổ| − |E|
D = đoạn dừng bị phạt (≥ ngưỡng)      Unplanned = |D \ E| = |D ∪ E| − |E|     ← dừng trong giờ nghỉ không tính
```

## D. `OeeInputBuilder`: từ khoảng thời gian sang tổng

`Build(window, breaks, plannedStops, downtime, microStopThreshold, segments, good)` cũng là hàm thuần. Bước 4 (reader)
chỉ việc đọc DB và xếp từng đoạn trạng thái vào đúng danh sách theo bảng spec 1.2.4.

**Ngưỡng micro stop xét trên cả đoạn, trước khi cắt.** Đoạn Down 06:50–07:20 (30 phút) khi nhìn từ giờ 07:00–08:00 chỉ
còn 20 phút; nếu cắt trước rồi mới so ngưỡng thì một đoạn dừng 61 giây vắt qua ranh giới giờ có thể thành hai micro stop
và biến mất khỏi downtime.

**Ca đang chạy cắt đến "bây giờ"** (`ShiftWindow.Until(now)`, spec 2.3). Lúc 07:00 ca S1 mới qua 60 phút; lấy cả 480
phút làm mẫu số thì A = 11 %. `now` lấy từ `TimeProvider`; test dùng `FakeTimeProvider` và `Advance(2h)` để thấy giờ
nghỉ 08:00 bắt đầu được tính.

## E. Kiểm tra đã chạy

| Test | Nội dung |
| --- | --- |
| `ShiftCalendarResolveTests` | 4 mốc của 1.2.8; breaks ca đêm ra UTC đúng; `Unspecified`/`Local`; ngày lễ 30/4; Chủ nhật 01/11 tăng ca chỉ S1 |
| `IntervalMathTests` | Rời nhau, chồng, lồng, chạm nhau, trùng, rỗng; `Clip`; `LengthOutside` |
| `OeeInputBuilderTests` | Ca mẫu dựng từ khoảng thời gian ra 0,7984; ca (b) 410 phút, 0,8659; dừng suốt giờ nghỉ không làm giảm A; ngưỡng 0; đoạn dừng vắt qua giờ; ca đang chạy với `FakeTimeProvider` |

Một test đỏ lúc đầu là do tôi **ghi sai kỳ vọng** (0–10 và 15–25 phút là 20 phút, tôi ghi 25), không phải code sai. Lần
này test bắt lỗi của chính test; lý do vẫn là: số kỳ vọng phải tính tay độc lập với code.

## F. Câu hỏi phỏng vấn

1. **"Ca đêm báo cáo ngày nào?"** Ngày bắt đầu ca theo giờ nhà máy; DB lưu UTC, cửa sổ ca là cặp UTC nửa mở.
2. **"Họp đầu ca trùng giờ nghỉ thì sao?"** Phép hợp khoảng: trừ 25 phút, không phải 40.
3. **"Máy dừng trong giờ ăn trưa có làm giảm OEE không?"** Không: downtime là phần đoạn dừng nằm ngoài (nghỉ ∪ PlannedStop).
4. **"Test logic phụ thuộc thời gian thế nào?"** `TimeProvider` (.NET 8+) trong code, `FakeTimeProvider` trong test;
   không có `DateTime.Now` trong Domain.
5. **"Ngày lễ thì OEE tháng có bị kéo xuống không?"** Không: ngày lễ không có cửa sổ ca, nên không có Planned Time.

## G. Đọc thêm

- Microsoft Learn: "TimeZoneInfo.ConvertTimeFromUtc", "Use DateOnly and TimeOnly", "TimeProvider class",
  "Microsoft.Extensions.TimeProvider.Testing — FakeTimeProvider".
- Bài toán "Merge Intervals" (LeetCode 56): đúng thuật toán của `UnionLength`.
