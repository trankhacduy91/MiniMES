# M7 · Bước 6 — Nguồn số đo cho demo: trạm test và SPC tự động từ lò

> Tài liệu học cho bước 6 của [module-07](../../spec/module-07-quality.md) (spec mục **2.3b**, Q18). Code:
> - `src/MiniMes.MachineSimulator/Scenarios/QualityDrift.cs` và `Stations/AutoStationClient.cs`
> - `src/MiniMes.Domain/Quality/Spc/SpcSampling.cs`
> - `src/MiniMes.Infrastructure/Quality/ProcessSpcSampler.cs`

## A. Hai nguồn, cùng một đường SPC

| | Trạm test (board) | Lò reflow (máy) |
| --- | --- | --- |
| Ai gửi | Simulator, vai trạm `L1-TEST-01` | Gateway OPC UA → MQTT → `ProcessValues` (M4b) |
| Đi vào M7 qua | `POST /api/quality/inspections` (bước 5) | `ProcessSpcSampler` lấy mẫu mỗi 30 giây |
| `Measurement` | `SerialId`, `StationId`, `Source = Station` | `MachineTagId`, `SourceTs`, `Source = Machine`, không serial |
| Ngoài spec | Board NG qua luồng M3 (Q1) | Alert `OutOfSpec` + Andon Quality (Q18), không có board để NG |
| Rule WE | `ISpcEvaluator` | **cùng** `ISpcEvaluator` |

Tách `ISpcEvaluator` ở bước 5 để bước này chỉ cần gọi lại nó: rule, chống trùng alert và savepoint là một đoạn code duy nhất.

## B. Simulator: giá trị theo phân phối chuẩn, có kịch bản drift

- `QualityDrift.Next()` sinh N(3,302; 0,040) bằng Box-Muller, làm tròn 3 chữ số như một đồng hồ đo. Random có seed, nên cùng seed thì ra cùng chuỗi và demo lặp lại được.
- `--scenario drift --after 100 --shift 0.02`: từ board 101, tâm dịch lên 0,02 V (0,5σ). Mọi board vẫn nằm trong spec 3,135–3,465; chỉ SPC nhìn thấy.
- **Board NG** (theo `ngProb` hay lệnh `ng`) vẫn đi scan NG với mã lỗi chức năng: board không lên nguồn thì không có điện áp để đo. Nếu thay bằng một điểm +6σ, cứ 3 % board sẽ có một WE1 giả và hỏng demo drift.
- Máy đếm NG theo **câu trả lời của MES**: board đo ngoài spec bị MES chuyển NG thì bộ đếm MQTT của máy cũng tính là NG (ADR-007: Good/NG lấy từ scan).

**Số liệu thật của kịch bản** (500 seed, đo bằng test và kiểm lại bằng một mô phỏng Python độc lập): từ board bị dịch tới alert đầu tiên mất trung vị 20 board (p25: 10). Không dịch thì trung vị 61; đó là báo động giả của bộ 3 rule (ARL vài chục điểm). Dịch 0,04 V (1σ) thì trung vị chỉ còn 7.

## C. Lấy mẫu cách quãng: vì sao không đưa mọi giá trị vào SPC

Gateway gửi giá trị theo deadband, có khi vài giây một lần. Các điểm sát nhau gần như bằng nhau (**tự tương quan**), nên rule run 7 sẽ báo liên tục trên nhiễu. Lấy một điểm mỗi 30 giây cho chuỗi gần độc lập, đúng giả thiết của control chart.

`SpcSampling.Decide` là hàm thuần nên unit test bảng được:

| Đã bao lâu từ mẫu trước | Có giá trị Good mới? | Quyết định |
| --- | --- | --- |
| < 30 s | gì cũng vậy | `NotDue` |
| ≥ 30 s | có | `Take`: giá trị Good **mới nhất**, đặt ở `SourceTs` của nó |
| ≥ 30 s, < 90 s | không | `Wait` |
| ≥ 90 s (3 chu kỳ) | không | `Gap`: alert `DataGap`, **không** bịa điểm |

Chi tiết:
- Bỏ giá trị `Bad` (dây cảm biến đứt, M4) và giá trị có `SourceTs` ở tương lai (đồng hồ PLC chạy nhanh).
- Mẫu trở lại thì alert gap tự đóng ("Samples resumed").
- Chạy trùng (hai instance, restart chồng nhau): unique `(MachineTagId, SourceTs)` giữ lại một điểm, bên thua trả `Duplicate`.
- Vượt USL: một alert `OutOfSpec` (chống trùng như rule WE) và một Andon Quality. `RaiseAndon` trả lại cuộc gọi đang mở, nên một đợt vượt dài vẫn chỉ là một cuộc gọi.

**Giới hạn đã biết:** với deadband, một lò **rất ổn định** (dao động nhỏ hơn deadband 0,5 °C) sẽ không gửi giá trị mới và bị báo gap dù vẫn chạy tốt. Simulator có nhiễu ±0,8 °C nên không gặp. Cách sửa ngoài đời: gateway gửi lại giá trị định kỳ (publish "keep-alive" mỗi N giây) dù không đổi.

## D. Test với `FakeTimeProvider`

Sampler nhận `TimeProvider`. Test tạo lò, tag và plan **riêng** (không đụng `L1-REFLOW-01` cùng Andon của nó), ghi `ProcessValues` có thời điểm cố định, rồi `clock.Advance(...)`. Job thật tắt trong Testing (`Spc__SamplerEnabled=false`), như job OEE của M5.

Các test:
- Nhịp lấy mẫu.
- Bỏ giá trị Bad và giá trị ở tương lai.
- Gap: một alert, không có điểm giả, tự đóng khi giá trị trở lại.
- Vượt USL tạo một `OutOfSpec` và một Andon.
- Drift 0,3 °C/phút: alert WE4 ở điểm 7 (246,05 °C), cách USL hơn 30 phút.
- Hai sampler chạy cùng lúc chỉ ghi một điểm.

Đột biến: bỏ lọc `Good`, bỏ chặn giá trị tương lai, hoặc bỏ đóng alert gap thì đều có test đỏ.

## E. Câu hỏi phỏng vấn

1. **"Sao không chạy SPC trên mọi giá trị máy gửi lên?"** Vì tự tương quan: điểm sau gần bằng điểm trước, rule run báo giả liên tục. Lấy mẫu cách quãng đều.
2. **"Mất dữ liệu thì chart vẽ gì?"** Không vẽ gì cho khoảng đó, và có một alert "data gap". Nội suy hay lặp lại giá trị cũ là bịa dữ liệu chất lượng.
3. **"Nhiệt độ lò vượt spec thì NG board nào?"** Không NG board nào ngay: không biết chính xác board nào nằm trong lò lúc đó. Tạo alert + Andon Quality; việc Hold các board đi qua lò trong khoảng thời gian đó là bước 9 (Hold theo khoảng thời gian qua máy).
