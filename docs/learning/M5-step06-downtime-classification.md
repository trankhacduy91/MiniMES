# M5 · Bước 6 — Downtime "cần phân loại" và đổi lý do: kiến thức cần nắm

> Tài liệu học cho bước 6 của [module-05](../../spec/module-05-oee-downtime.md) (spec mục **1.2.9**, **1.3** bước 4,
> 8, 11, quy tắc 7, 8; sai lầm 1.5 #8–9). Bước trước: [M5-step05](M5-step05-oee-snapshot-job.md).
> Code: `src/MiniMes.Application/Oee/Downtime/DowntimeClassification.cs`, test:
> `tests/MiniMes.IntegrationTests/Oee/DowntimeClassificationTests.cs`.

---

## A. Nghiệp vụ: vì sao cần phân loại

Pareto lý do dừng (bước 7–8) chỉ có ích khi mỗi lần dừng đều có lý do. Máy chỉ biết "tôi dừng", không biết **vì sao**.
Người phải nói, và nói vào hai lúc:

1. **Lúc đóng Andon**: supervisor chọn `FDR-JAM` khi resolve. Hệ thống gán lý do đó cho đoạn dừng của máy trong thời gian
   Andon mở (câu chuyện 1.3 bước 4). Phần lớn lần dừng được phân loại ở đây, không tốn thêm thao tác.
2. **Danh sách "cần phân loại"**: các đoạn không có Andon hoặc bị quên (chị Mai quên chọn lý do cho lần chờ QC lúc
   12:40). Cuối ca, khi bàn giao, supervisor xử lý hết danh sách.

| Quy tắc | Lý do |
| --- | --- |
| Không lý do vẫn **phạt A** (quy tắc 7) | Mặc định "không phạt" thì ai cũng có động cơ để trống |
| Micro stop (< 60 s) không vào danh sách | Không bắt operator giải thích từng lần kẹt 20 giây |
| Đoạn còn mở không được gán | Chưa biết dừng bao lâu, chưa biết vì sao |
| `OTHER` bắt buộc ghi chú | `OTHER` > 20 % là dấu hiệu danh sách lý do nghèo hoặc người chọn lười (1.2.9) |
| Lý do supervisor đã chọn không bị Andon ghi đè | Lựa chọn rõ ràng thắng lựa chọn suy ra |
| Ghi audit ai đổi, từ gì sang gì | OEE dễ bị "làm đẹp" bằng cách đổi lý do sang loại không phạt (1.5 #9) |

## B. Đổi lý do → tính lại snapshot (quy tắc 8)

```
Đoạn Down 07:00–07:30, chưa lý do     → A = 390 / 420 = 92,9 %
Supervisor gán PM (không phạt A)      → đánh dấu IsFinal = false: dòng giờ 07 + dòng tổng ca (chỉ cửa sổ giao đoạn)
Lượt job kế tiếp (≤ 1 phút)           → tính lại từ dữ liệu thô: Planned 390, Run 390, A = 100 %
```

Command **không** tự tính OEE. Nó chỉ đánh dấu, và job (một chỗ duy nhất tính snapshot) làm phần còn lại. Ưu điểm:
request của supervisor nhanh, không có hai đường tính OEE. Nhược điểm: dashboard lịch sử trễ tối đa một phút.

Gán lý do vẫn phạt (`FDR-JAM`) thì A không đổi nhưng snapshot vẫn được tính lại (`CalculatedAt` mới). Đổi một lý do
phạt sang một lý do phạt khác vẫn làm Pareto thay đổi.

## C. Audit theo cột (`IAuditedColumns`)

`MachineStateHistory` là bảng **máy** ghi: mỗi lần máy đổi trạng thái là một lần insert đoạn mới và một lần đóng đoạn
cũ. Đánh dấu `IAudited` thì AuditLog sẽ phình theo máy, che mất những thay đổi do **người** làm. Giải pháp:

```csharp
public class MachineStateHistory : IAuditedColumns
{
    [AuditedColumn] public int? DowntimeReasonId { get; private set; }
    [AuditedColumn] public string? DowntimeNote { get; private set; }
}
```

Interceptor M0 thêm một nhánh: với `IAuditedColumns`, chỉ ghi các cột có `[AuditedColumn]` khi chúng đổi; bỏ qua
Added và các cột khác. Test: gán `PM` sinh đúng 2 dòng audit, còn đoạn micro stop (máy tạo và đóng) không có dòng nào.

## D. Nối Andon với OEE

`ResolveAndonCommand` dùng chung `AndonTransition.RunAsync`. Thêm tham số `beforeSave` để việc phân loại đoạn dừng
**cùng một `SaveChanges`** (một transaction) với việc đóng Andon. Nếu một trong hai lỗi thì cả hai không lưu.

Điều kiện chọn đoạn: cùng máy, không phải Running, chưa có lý do, và giao `[RaisedAt, ResolvedAt]`. Đoạn Setup kết thúc
trước khi gọi Andon (đã có `CHG-MODEL`) giữ nguyên.

## E. Bẫy đã gặp

- **Lại là `from` trong query expression**: `h.EndAt > from` trong `from h in … where …` không biên dịch được
  (`from` là từ khóa ngữ cảnh). Lần thứ hai trong module; đặt tên `start`/`end`.
- **Provider-specific function trong Application**: `EF.Functions.DateDiffSecond` nằm ở gói SQL Server, mà Application chỉ
  tham chiếu EF Core lõi. Dùng `h.EndAt >= h.StartAt.AddSeconds(n)`: provider dịch sang `DATEADD`.
- **DB dùng chung trong test**: test master data tạo thêm ca trùng giờ S1, nên snapshot có cả cho các ca đó. Test phải
  lọc theo đúng ca, không giả định "chỉ có dữ liệu của mình".
- **Sai vai trò**: Supervisor không được raise Andon (chỉ Operator). Test raise bằng operator, resolve bằng supervisor.

## F. Câu hỏi phỏng vấn

1. **"Downtime không ai nhập lý do thì sao?"** Vẫn tính là mất Availability, nằm trong danh sách cần phân loại; phần lớn
   được phân loại tự động khi đóng Andon.
2. **"Supervisor đổi lý do sau khi ca đã chốt thì OEE có đổi không?"** Có: snapshot của các giờ và ca liên quan được
   đánh dấu tính lại, job tính lại từ dữ liệu thô; thay đổi được audit.
3. **"Làm sao chống gian lận OEE?"** Audit mọi lần đổi lý do (ai, khi nào, cũ → mới); không lý do = phạt; `OTHER` phải
   ghi chú. Quan trọng hơn: không dùng OEE để chấm điểm operator.
4. **"Audit bảng lớn thế nào cho khỏi phình?"** Audit theo cột: chỉ cột do người sửa, không audit dữ liệu máy ghi.

## G. Đọc thêm

- Spec M4 2.9 (Andon), M0 (audit interceptor).
- Microsoft Learn: "EF Core — function mappings of the SQL Server provider" (`DateTime.AddSeconds` → `DATEADD`).
