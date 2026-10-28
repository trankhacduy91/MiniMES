# M5 · Bước 5 — `OeeSnapshot` và job chốt theo giờ/cuối ca: kiến thức cần nắm

> Tài liệu học cho bước 5 của [module-05](../../spec/module-05-oee-downtime.md) (spec mục **2.3**, **2.4**, quy tắc 8,
> 12, 13). Bước trước: [M5-step04](M5-step04-oee-input-reader.md).
> Code: `src/MiniMes.Domain/Oee/OeeSnapshot.cs`, `src/MiniMes.Infrastructure/Oee/{OeeSnapshotService, OeeSnapshotWorker}.cs`,
> test: `tests/MiniMes.IntegrationTests/Oee/OeeSnapshotTests.cs`.

---

## A. Snapshot lưu gì và vì sao

Một dòng = OEE của **một máy** trong **một giờ** của ca (`Hour` = 6, 7, … theo giờ đồng hồ nhà máy) hoặc **cả ca**
(`Hour = NULL`). Khóa duy nhất `(MachineId, ShiftDate, ShiftId, Hour)`.

Lưu **các tổng** (Planned, Run, Total, Good, IdealSeconds, ScanCount), không chỉ A/P/Q/OEE. Lý do là quy tắc 12: OEE
của một ngày/tuần/nhiều máy = cộng tổng rồi chia lại bằng cùng công thức (`OeeCalculator.Combine`).

```
Giờ 1: Planned 60 phút, OEE 90 %      Trung bình cộng: (90 + 40) / 2 = 65 %       ← sai
Giờ 2: Planned  6 phút, OEE 40 %      Cộng rồi chia:  (54 + 2,4) / 66  = 85,45 %  ← đúng
```

`IdealSeconds` là cột thêm so với spec gốc: suy ngược `P × Run` cũng được nhưng mất chính xác vì làm tròn.

## B. Job "bắt kịp" (catch-up), không phải "đúng phút 0"

| Cách | Server khởi động lại lúc 13:30 | Chạy hai lần |
| --- | --- | --- |
| `Task.Delay(1h)`, đến phút 0 thì chốt giờ trước | Giờ 13:00 không bao giờ được chốt | Nhân đôi dòng |
| **Mỗi phút hỏi "bucket nào đến hạn mà chưa chốt?"** | Lượt kế tiếp chốt bù | Lượt hai thấy đã chốt, ghi 0 dòng |

- **Đến hạn** = `End + Grace ≤ now`. Grace 5 phút để message QoS 1 về muộn (hoặc gateway gửi bù) kịp vào giờ của nó.
  Test: 14:04:59 chưa có tổng ca, 14:05:00 có.
- **Idempotent upsert**: có dòng thì ghi đè, chưa có thì thêm. Unique index là chốt chặn cuối khi hai lượt chạy đua nhau.
- **`IsFinal`**: dòng đã final thì bỏ qua. Bước 6 đổi lý do dừng sẽ đặt `IsFinal = false` (`MarkForRecalculation`),
  lượt kế tiếp tính lại, kể cả ngày cũ hơn khoảng bắt kịp.
- **Tổng ca đọc lại từ dữ liệu thô**, không cộng các dòng giờ. Như vậy kết quả không phụ thuộc thứ tự chốt. Test
  kiểm hai cách cho cùng kết quả (Planned/Run/Total/Good bằng nhau, OEE sai số 1e-9).

## C. `BackgroundService` + `PeriodicTimer` + `TimeProvider`

```csharp
using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), time);   // .NET 8+: nhận TimeProvider
while (await timer.WaitForNextTickAsync(ct))
{
    try { using var scope = scopes.CreateScope(); await scope...CloseDueBucketsAsync(ct); }
    catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(...); }
}
```

- **Không chồng lượt**: tick sau chờ lượt trước xong.
- **Scope mỗi lượt**: worker là singleton, `DbContext` là scoped.
- **Bắt exception trong vòng lặp**: từ .NET 6, exception thoát khỏi `ExecuteAsync` làm **dừng cả host** (cả API).
- **Test không cần chờ**: `new PeriodicTimer(period, fakeTimeProvider)` + `fake.Advance(1 phút)` làm timer tick ngay.
  Logic chính test bằng cách gọi thẳng `CloseDueBucketsAsync` với `FakeTimeProvider`; worker chỉ có một test mỏng.
- **Một instance**: nhiều API cùng chạy job thì cần khóa phân tán hoặc chỉ một node chạy job. Unique index chỉ bảo vệ
  dữ liệu, không ngăn tính trùng. Nhiều job phức tạp → Hangfire/Quartz (spec mục 4).

## D. Bẫy EF: unique index trên cột nullable

EF Core tự sinh `filter: "[Hour] IS NOT NULL"` cho unique index có cột nullable. Với filter đó, dòng tổng ca
(`Hour = NULL`) **không** được index bảo vệ. Sửa bằng `.HasFilter(null)`: không có filter, SQL Server coi các NULL là
bằng nhau trong unique index. Đã ghi vào `docs/incidents.md`; test `Database_refuses_a_second_shift_total_row` giữ nó.
Bài học: **đọc migration trước khi chạy**.

## E. Realtime vs định kỳ (README mục "OEE (M5)")

| | Realtime (ca hiện tại) | Định kỳ (snapshot) |
| --- | --- | --- |
| Ưu | Số tức thời | Đã chốt thì ổn định, dễ kiểm chứng, báo cáo rẻ |
| Nhược | Tốn truy vấn mỗi lần mở, số nhảy khi dữ liệu về muộn | Trễ tới 1 giờ + grace |

Dùng cả hai, cùng **một** hàm tính.

## F. Kiểm tra đã chạy

| Test | Kết quả |
| --- | --- |
| Grace 5 phút | 14:04:59 có giờ 06–12; 14:05 thêm giờ 13 + tổng ca |
| Tổng ca | Planned 405 phút, Run 360, Total 1.000, Good 970, **OEE 0,7984** |
| Tổng ca = `Combine(8 giờ)` | Khớp; trung bình cộng 8 OEE giờ thì **khác** |
| Chạy hai lần | Lần hai ghi 0 dòng, `Id`/`CalculatedAt` không đổi |
| Unique index | DB từ chối dòng tổng ca thứ hai |
| Tắt job 06:30 → 09:05 | Chốt bù giờ 06, 07, 08 |
| Worker + `FakeTimeProvider` | `Advance(1 phút)` → giờ 06 được chốt |

## G. Câu hỏi phỏng vấn

1. **"Server tắt vài giờ thì báo cáo OEE thế nào?"** Job mỗi phút hỏi bucket nào đến hạn mà chưa chốt, nên tự chốt
   bù; unique index + upsert nên chạy lại không trùng.
2. **"Dữ liệu MQTT đến trễ thì sao?"** Grace trước khi chốt; message gửi bù mang giờ sự kiện nên rơi đúng giờ; đổi lý
   do dừng đánh dấu tính lại.
3. **"OEE tuần tính sao?"** Cộng Planned/Run/Total/Good/Ideal của các snapshot rồi chia; không lấy trung bình %.
4. **"Test job theo giờ mà không chờ cả giờ?"** `TimeProvider` + `FakeTimeProvider`; `PeriodicTimer` nhận
   `TimeProvider` nên test tua được thời gian.
5. **"Vì sao không dùng Hangfire/Quartz?"** Một job đơn giản trong một instance; thêm thư viện và hạ tầng không cần. Có
   nhiều job, nhiều instance, cần retry/dashboard thì chuyển.

## H. Đọc thêm

- Microsoft Learn: "Background tasks with hosted services", "PeriodicTimer(TimeSpan, TimeProvider)",
  "Breaking change: unhandled exceptions from a BackgroundService" (.NET 6).
- EF Core docs: "Indexes — index filter" (mục unique index và NULL trên SQL Server).
