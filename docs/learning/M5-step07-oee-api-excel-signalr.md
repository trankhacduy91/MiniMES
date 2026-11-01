# M5 · Bước 7 — API OEE, Pareto, Excel và SignalR: kiến thức cần nắm

> Tài liệu học cho bước 7 của [module-05](../../spec/module-05-oee-downtime.md) (spec mục **1.2.9**, **2.5**, **2.7**,
> 2.4 "realtime vs định kỳ"). Bước trước: [M5-step06](M5-step06-downtime-classification.md).
> Code: `src/MiniMes.Application/Oee/{OeeQueries, OeeReports, OeeScope}.cs`, `.../Oee/Downtime/DowntimePareto.cs`,
> `src/MiniMes.Api/Controllers/{OeeController, DowntimeController, ReportsController}.cs`.

---

## A. Các endpoint và ai dùng

| Endpoint | Ai dùng | Nguồn |
| --- | --- | --- |
| `GET /api/oee/current?machine=` \| `?line=` | Dashboard, Line TV (bước 8b) | **Tính ngay**: reader + `OeeCalculator`, cửa sổ ca cắt đến bây giờ |
| `GET /api/oee/history?from&to&…&hourly` | Dashboard (sản lượng theo giờ), so sánh ngày | **Snapshot** đã chốt |
| `GET /api/downtime/pareto?…&shift&by&includePlanned` | Họp cải tiến, bàn giao ca | Cùng đoạn dừng với A |
| `GET /api/downtime/unclassified`, `PUT /api/downtime/{id}/reason` | Supervisor | Bước 6 |
| `GET /api/reports/oee.xlsx`, `downtime-pareto.xlsx` | Quản lý xưởng (báo cáo tuần) | Cùng query với dashboard |

Đọc: mọi role đăng nhập (operator cũng nên thấy OEE line mình). Ghi (đổi lý do): Supervisor, Admin.
Controller vẫn mỏng: nhận HTTP → `ISender.Send` → trả HTTP (có test `ControllerConventionTests` giữ quy tắc này).

## B. Pareto phải khớp Availability

Spec 2.5 đưa Snippet C (SQL window function trên `MachineStateHistory`) nhưng cũng cảnh báo: SQL đó **không trừ** giờ
nghỉ, PlannedStop, micro stop, nên tổng phút Pareto lệch với phần mất của A. Quản lý thấy "Pareto 52 phút, A mất 45
phút" sẽ không tin cả hai con số.

Cách làm: `OeeInputBuilder.BuildDetailed` trả thêm `StopLoss` của từng đoạn (thời gian **thật sự** bị tính, sau khi trừ
giờ nghỉ và PlannedStop và áp ngưỡng micro stop). Pareto cộng các `StopLoss` đó theo lý do, ca theo ca.

```
Ca mẫu:  FDR-JAM 25 · MAT-WAIT 12 · (Down) 8      = 45 phút = Planned 405 − Run 360   ✓
         55,56 %      82,22 % tích lũy   100 %
```

Đánh đổi: mỗi máy, mỗi ca đọc DB một lần. Với một line, một tháng thì chạy được; báo cáo cả năm thì cần bảng tổng hợp
riêng (ví dụ lưu `StopLoss` theo snapshot). Đó là điểm "khi nào nên đổi" để nói trong phỏng vấn.

`by=minutes` cho thấy cái **đau nhất** (hỏng lớn → bảo trì); `by=count` cho thấy cái **lặp nhiều nhất** (kẹt vặt →
chuẩn hóa thao tác). Thứ tự hòa: theo mã lý do, để kết quả ổn định giữa hai lần gọi. Phần trăm tích lũy tính trong C#
(tương đương `SUM() OVER (ORDER BY … ROWS UNBOUNDED PRECEDING)` trong SQL).

## C. Excel bằng ClosedXML

| Quy tắc | Vì sao |
| --- | --- |
| Ghi **số** `0.7984` + định dạng ô `0.0%` | Người nhận làm pivot/công thức được; chuỗi "79.8%" thì không |
| Giờ nhà máy, ghi múi giờ ở tiêu đề | Ghi UTC thì báo cáo lệch 7 tiếng |
| Ô trống khi tỷ lệ không xác định | Giờ nghỉ trưa không phải 0 % |
| Sheet `Definitions` | Ba tháng sau vẫn hiểu Planned trừ gì, micro stop bao nhiêu giây, Total/Good từ đâu |
| Tối đa 31 ngày | Không sinh file khổng lồ trong một request |
| Độ rộng cột cố định | `AdjustToContents` cần font hệ thống; runner Linux của CI không có (bài học từ M1) |

Test mở file tải về bằng chính ClosedXML: ô OEE là số 0,7984, định dạng `0.0%`, ô giờ bắt đầu ca là 06:00 (không
phải 23:00 UTC).

## D. SignalR: hai sự kiện mới

| Sự kiện | Phát ở đâu | Khi nào |
| --- | --- | --- |
| `DowntimeNeedsClassification` | Batch writer của M4 | **Sau commit**, khi đóng một đoạn Down/Setup/Offline chưa có lý do, dài ≥ ngưỡng micro stop |
| `OeeSnapshotClosed` | Service chốt (bước 5) | Mỗi máy, mỗi ca, mỗi lượt job (tối đa 1 lần/phút: chính nhịp job là throttle) |

Phát **sau commit** để không bao giờ báo về một dòng bị rollback. Batch bị ghi lại sau lỗi thì có thể báo hai lần;
client giữ một dòng theo `Id` đoạn (idempotent ở phía nhận).

## E. Bẫy đã gặp

- **Test đầu-cuối qua MQTT lùi giờ không hợp lệ.** Muốn có đoạn Down 60 giây mà không chờ, tôi gửi Down với `ts` lùi 55
  giây. Nhưng máy đang `Offline` (watchdog của M4), và luật M4 "không viết lại lịch sử" đặt Down bắt đầu khi đoạn
  Offline bắt đầu, nên Down chỉ dài 10 giây. Code đúng, test sai. Sửa: host MQTT của test đặt ngưỡng 5 giây
  (`services.Configure<OeeOptions>`), không lùi giờ. Thêm thông báo chẩn đoán in các đoạn trạng thái khi test hết giờ chờ.
- **Thiếu `AccessTokenProvider`** trên `HubConnection` → 401 ở bước negotiate (token chỉ có trên WebSocket handshake).
- **Ca trùng giờ trong DB dùng chung.** M1 không chặn hai ca trùng giờ, mà Pareto cộng theo ca, nên có thể đếm một lần
  dừng hai lần. Thêm tham số `shift` (dashboard cần thật: "Pareto ca này"); test lọc `shift=S1`.
- **API chạy theo giờ thật**: dữ liệu test năm 2030 là "tương lai", nên Pareto bỏ qua. Test API dùng ngày tháng 9/2026.

## F. Câu hỏi phỏng vấn

1. **"Vì sao Pareto của anh khớp OEE?"** Cùng một danh sách đoạn dừng, đã trừ giờ nghỉ/kế hoạch/micro stop, do cùng một
   hàm tính ra. SQL riêng cho Pareto sẽ lệch.
2. **"Xuất Excel có gì phải chú ý?"** Số thật + định dạng, giờ nhà máy, sheet định nghĩa, giới hạn khoảng ngày, sinh ở
   server từ cùng query với dashboard.
3. **"Realtime hay định kỳ?"** Ca hiện tại tính theo yêu cầu (`/oee/current`), lịch sử đọc snapshot (`/oee/history`);
   cùng một hàm tính.
4. **"Đẩy sự kiện realtime trong transaction có vấn đề gì?"** Báo về dữ liệu có thể bị rollback; phát sau commit, client
   chịu được bản trùng.

## G. Đọc thêm

- ClosedXML docs: "Number formats", "Using tables".
- Microsoft Learn: "ASP.NET Core SignalR .NET client — bearer token authentication" (`AccessTokenProvider`).
- Spec M5 mục 4: bảng "Vì sao chọn giải pháp này" (dòng Xuất báo cáo, Phân chia tính toán).
