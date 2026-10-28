# M5 · Bước 4 — Đọc dữ liệu OEE từ DB: kiến thức cần nắm

> Tài liệu học cho bước 4 của [module-05](../../spec/module-05-oee-downtime.md) (spec mục **1.2.4**, **1.2.10**, **2.5**,
> quy tắc 2, 3, 7). Bước trước: [M5-step03](M5-step03-shift-calendar-intervals.md).
> Code: `src/MiniMes.Infrastructure/Oee/OeeInputReader.cs`, `src/MiniMes.Domain/Oee/{StopRules, WorkOrderShare}.cs`,
> test: `tests/MiniMes.IntegrationTests/Oee/OeeInputReaderTests.cs`.

---

## A. Năm nguồn dữ liệu của một con số OEE

| # | Đại lượng | Bảng (module) | Cách đọc |
| --- | --- | --- | --- |
| 1 | Total Count | `MachineEvents` (M4), Kind = Count | `SUM(Delta)` theo `MachineTs` trong `[from, to)` |
| 2 | Đoạn dừng | `MachineStateHistory` (M4) + `DowntimeReasons` (M1) | Đoạn **giao** cửa sổ, lấy nguyên đoạn; `EndAt NULL` = đến "now" |
| 3 | Good / NG | `ScanLogs` (M3) tại trạm của máy | Đếm theo `(WorkOrderId, Result)` |
| 4 | Nghỉ giữa ca | `ShiftBreak` (M1) | Đã có trong `ShiftWindow.Breaks` (bước 3) |
| 5 | Ideal cycle time | `WorkOrderOperations` (snapshot M2) | Theo WO và work center của máy |

Mỗi nguồn có lý do riêng (spec 1.2.10): **máy biết bao nhiêu nhưng không biết con nào; serial biết con nào OK/NG**.
Total lấy từ máy, Good lấy từ scan, `ngDelta` của máy chỉ để đối chiếu.

"Trạm của máy" = trạm có cùng `WorkCenterId` với máy. Seed M1: `L1-SMT-01` (pick-and-place) và `L1-REFLOW-01` (lò)
cùng work center `WC-SMT`, cùng trạm scan `L1-SMT-01`: bo qua cả hai máy rồi mới tới trạm scan cuối line SMT.

## B. Vì sao không cần `LAG` lúc đọc

Spec 2.5 Snippet A tính delta bộ đếm bằng `LAG(Counter) OVER (ORDER BY Ts)` lúc báo cáo. Ở dự án này ingest M4 đã
tính `Delta` bằng `CounterMath` (có quy tắc reset) **lúc ghi**, nên đọc chỉ cần `SUM(Delta)`:

| Cách | Ưu | Nhược |
| --- | --- | --- |
| `LAG` lúc đọc (spec) | Không phụ thuộc ingest; tính lại được nếu quy tắc reset đổi | Mỗi báo cáo quét lại, phải lấy dư một khoảng lookback để có dòng trước |
| `Delta` lúc ghi (dự án) | Đọc rẻ (index `(MachineId, MachineTs) INCLUDE (Kind, Delta)`), một chỗ chứa quy tắc reset | Sai lúc ghi thì phải sửa dữ liệu |

`LAG` vẫn đáng học: phỏng vấn SQL hay hỏi, và bẫy "lọc `WHERE Ts >= @start` **trong** CTE làm dòng đầu mất `Prev`"
là lỗi kinh điển.

`MachineTs` là giờ sự kiện, không phải giờ server nhận: message gateway gửi bù sau 60 giây mất broker vẫn rơi vào
đúng giờ nó xảy ra (M4b bước 5).

## C. Quy tắc trạng thái → cách tính (`StopRules`)

| Trạng thái | Không lý do | Lý do phạt A | Lý do không phạt |
| --- | --- | --- | --- |
| Running | Run | — | — |
| PlannedStop | Trừ khỏi Planned | Trừ khỏi Planned | Trừ khỏi Planned |
| Down, Setup | **Phạt** (cần phân loại) | Phạt | Trừ khỏi Planned |
| Idle | Phạt nếu trạm có scan trong ca, ngược lại trừ khỏi Planned | Phạt | Trừ khỏi Planned |
| Offline | Phạt ("không có dữ liệu") | Phạt | Trừ khỏi Planned |

Không lý do vẫn phạt (quy tắc 7): mặc định "không phạt" thì ai cũng có động cơ để trống lý do.

## D. Hai quyết định thiết kế (spec không nói rõ, đã hỏi và chọn)

1. **Chia Total của máy cho từng WO theo tỷ lệ scan.** Ca có 600 scan `PCB-A`, 200 scan `PCB-B`, máy đếm 800 → 600 ×
   20 s + 200 × 30 s. Phần lẻ chia bằng **largest remainder** (làm tròn xuống rồi phát phần dư cho phần thập phân lớn
   nhất) để tổng luôn đúng bằng số máy đếm. Cách khác: gán mỗi lần đếm cho WO của scan gần nhất trước nó (`OUTER APPLY
   TOP 1`): đúng hơn lúc đổi model nhưng SQL phức tạp hơn và scan thường đi sau máy vài giây.
2. **Idle "có kế hoạch" = trạm có ít nhất một scan trong ca.** DB không lưu "WO nào chạy trên máy lúc nào"; trạng
   thái WO hiện tại không nói được quá khứ. Scan là bằng chứng có sẵn rằng máy được giao việc.

## E. Integration test: dựng lại ca mẫu từ dòng dữ liệu

```
202 dòng bộ đếm (baseline 05:55 không tính, 200 × +5, một dòng 14:00 thuộc ca sau)
25 đoạn trạng thái: họp 06:00–06:15 (MEETING), 3 lần dừng (FDR-JAM, MAT-WAIT, chưa phân loại), 6 lần kẹt 20 s,
                    Idle trong cả hai giờ nghỉ
1.000 scan (970 OK + 30 NG) của một WO có cycle time 20 s
→ Planned 405 · Run 360 · Total 1.000 · Good 970 · OEE 0,7984   (khớp tính tay bước 1)
```

Kỹ thuật cách ly trong DB dùng chung: mỗi test tạo máy riêng và chọn một ngày riêng trong năm 2030, nên dòng của test
khác không rơi vào cửa sổ. "Now" do `FakeTimeProvider` cấp khi tạo reader trực tiếp, không qua DI. Nhờ vậy test được
đoạn `EndAt = NULL` (đang dừng) với giờ cố định.

Lần chạy đầu một test đỏ vì **kỳ vọng sai**: Down từ 07:30 đến 08:30, trừ giờ nghỉ 08:00–08:15, là 30 + 15 = 45
phút, tôi ghi 30. Lần thứ hai trong module test bắt lỗi của chính người viết test. Bài học: tính kỳ vọng bằng thác nước
thời gian trên giấy, không nhẩm.

## F. Chỉ mục (spec 2.5)

| Bảng | Chỉ mục | Phục vụ |
| --- | --- | --- |
| `MachineEvents` | `(MachineId, MachineTs) INCLUDE (Kind, Delta)` (có từ M4) | `SUM(Delta)` một máy một khoảng |
| `MachineStateHistory` | `(MachineId, StartAt) INCLUDE (EndAt, State, DowntimeReasonId)` | Đoạn giao cửa sổ, không cần tra bảng chính |
| `ScanLogs` | `(StationId, ScannedAt) INCLUDE (Result, WorkOrderId)` | Good/NG theo trạm và giờ, tách WO |

`INCLUDE` = chỉ mục "phủ" (covering index): mọi cột truy vấn cần đều nằm trong lá của chỉ mục, nên SQL Server không phải
Key Lookup sang bảng chính. Đổi lại mỗi lần INSERT ghi thêm vào chỉ mục, nên chỉ thêm khi có truy vấn thật cần.

## G. Câu hỏi phỏng vấn

1. **"Total Count lấy từ đâu, Good lấy từ đâu, vì sao khác nhau?"** Máy đếm cho Total (đếm cả bo chưa ai scan), scan
   theo serial cho Good (truy được con nào NG, lỗi gì).
2. **"Bộ đếm reset giữa ca thì sao?"** Delta = giá trị mới khi counter giảm; tính lúc ghi, đọc chỉ `SUM`. Test: 100 →
   105 → 3 → 10 ra 15.
3. **"Máy Idle có phải downtime không?"** Tùy có kế hoạch hay không; viết thành quy tắc một chỗ (`StopRules`) để hai
   người tính ra một con số.
4. **"Nhiều sản phẩm trong ca thì ideal cycle time nào?"** Σ count × cycle time theo WO, cycle time từ snapshot lúc
   release WO (sửa routing không làm sai lịch sử).

## H. Đọc thêm

- Microsoft Learn: "LAG (Transact-SQL)", "Create indexes with included columns", "EF Core — Complex query operators
  (GroupJoin / left join)".
- Largest remainder method (phương pháp chia ghế quốc hội Hamilton): cùng bài toán "chia số nguyên theo tỷ lệ mà tổng đúng".
