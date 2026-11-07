# Tuần 5: OEE và downtime (M5)

> **Mục tiêu tuần:** OEE là ngôn ngữ chung của quản lý sản xuất. Họp sáng nào cũng có câu "line 1 hôm qua OEE bao nhiêu, mất vì gì". Cuối tuần bạn phải:
> - tự tính tay OEE một ca từ dữ liệu thô;
> - giải thích được vì sao một con số OEE "lạ";
> - nói được cách cải thiện bằng Pareto.
>
> **Thời lượng:** ~10 giờ, gồm đọc 3 · lab 3 · drill 2 · nói 2.

## 0. Ngoài đời việc này diễn ra thế nào

- **Ai xem:**
  - quản lý sản xuất và trưởng ca, hằng ngày;
  - giám đốc nhà máy, hằng tuần;
  - nhà máy FDI ở KCN (Nhật, Hàn) thường báo cáo OEE về công ty mẹ hằng tháng.
- **Con số tham khảo hay được nhắc:**
  - "world class" khoảng **85 %**;
  - nhiều nhà máy thực tế ở mức 50–70 %;
  - lần đầu đo chính xác thường **thấp hơn** con số ghi tay trước đó, vì ghi tay không có micro-stop.
- **Downtime phải có lý do:**
  - máy dừng thì trưởng ca hoặc bảo trì chọn lý do (feeder kẹt, chờ vật tư, chờ QC, đổi model…);
  - cuối tuần làm **Pareto** (80/20): 2–3 lý do chiếm phần lớn thời gian mất thì tập trung sửa vào đó;
  - đây là đầu vào cho Kaizen / TPM.
- **Ticket hay gặp:**
  - "OEE ca đêm hơn 100 %";
  - "OEE hôm qua thấp bất thường";
  - "số sản lượng trên OEE khác báo cáo kho";
  - "đổi lý do dừng của ca trước mà báo cáo không đổi".

  IT MES phải **giải thích được con số**, không chỉ "hệ thống tính vậy".
- **Ba nguồn mất mát kinh điển:** Availability (dừng), Performance (chạy chậm, micro-stop), Quality (hàng NG). Thêm "Six big losses" của TPM: hỏng máy, setup/changeover, micro-stop, chạy chậm, lỗi trong sản xuất, lỗi lúc khởi động.

## 1. Đọc (3 giờ)

- [ ] `docs/oee-hand-calcs.md`: đọc chậm ca mẫu `L1-SMT-01` (79,84 %) và 3 ca biến thể (a) (b) (c).
- [ ] `spec/module-05-oee-downtime.md`, mục 1.2: công thức, thời gian kế hoạch, micro-stop < 60 giây, ca qua nửa đêm.
- [ ] `ARCHITECTURE.md` ADR-007: Total Count lấy từ máy, Good/NG lấy từ scan. Vì sao hai nguồn?
- [ ] `docs/learning/M5-step03-shift-calendar-intervals.md` (ca, giờ nghỉ, phép HỢP khoảng thời gian), `M5-step06-downtime-classification.md`, `M5-step09-cross-check-demo.md`.
- [ ] README phần OEE: realtime khác snapshot; job "bắt kịp".

### Công thức (học thuộc, kèm ví dụ ca mẫu)

```
Planned = Ca − (Nghỉ ∪ PlannedStop)        480 − 60 − 15 = 405 phút
Run     = Planned − Unplanned downtime     405 − 45      = 360 phút   (dừng < 60 s là micro-stop, KHÔNG trừ ở đây)
A = Run / Planned                          360 / 405     = 88,89 %
P = Σ(count × ideal cycle) / Run           1000 × 20 s = 333,33 ph / 360 = 92,59 %
Q = Good / Total                           970 / 1000    = 97,00 %
OEE = A × P × Q                                          = 79,84 %
Rút gọn: OEE = Good × cycle / Planned      970 × 20 s / 405 ph = 79,84 %
```

Câu "rút gọn" là thứ nên nói khi phỏng vấn: **OEE = thời gian tạo ra hàng tốt / thời gian đáng lẽ chạy được**.

### Lý do dừng (seed, `DowntimeReasons`)

| Mã | Loại | Tính vào A? |
| --- | --- | --- |
| `PM`, `NOPLAN`, `MEETING` | Planned | Không (trừ khỏi Planned) |
| `CHG-MODEL` | Planned | **Có**: tính để đo được và giảm changeover (quyết định có chủ ý) |
| `MCH-FAULT`, `FDR-JAM`, `MAT-WAIT`, `QC-WAIT`, `NO-OPR`, `OTHER` | Unplanned | Có |

## 2. Lab (3 giờ)

1. BackOffice → **OEE dashboard** → `L1-SMT-01`, ngày **01/10/2026** ca S1: phải thấy 88,9 / 92,6 / 97,0 / **79,8 %**.
2. Ngày **02/10/2026**: P 133,3 %, cờ *Performance above 100 %*. Ngày **05/10/2026**: Q 91 %, cờ *Counter mismatch*.
3. Bấm **Export OEE** và mở file Excel: sheet `Definitions` giải thích từng cột.
4. **Demo trên ca đang chạy** (README mục OEE):
   - `docker stop mes-simulator` nếu đang chạy bằng Docker;
   - `dotnet run --project src/MiniMes.MachineSimulator -- --config src/MiniMes.MachineSimulator/sim-oee-demo.json`;
   - `down L1-SMT-01 300` → chờ 5 phút → *Stops to classify* có một dòng;
   - chọn `PM` (A tăng) rồi đổi sang `FDR-JAM` (A giảm lại), mỗi lần xem Pareto.
5. Chạy Line TV:

   ```powershell
   dotnet run --project src/MiniMes.Kiosk.Wpf -- --mode=linetv --line=L1
   ```

### SQL: tự tính lại OEE từ snapshot

```sql
-- Tổng ca (Hour IS NULL) của 3 ngày demo: tự tính lại A, P, Q từ cột tổng, so với cột đã lưu
SELECT ShiftDate, ShiftId, PlannedSeconds/60.0 AS PlannedMin, RunSeconds/60.0 AS RunMin, TotalCount, GoodCount, ScanCount,
       RunSeconds*1.0/NULLIF(PlannedSeconds,0) AS A_tinh_lai,      -- NULLIF: ca không chạy thì mẫu số = 0
       IdealSeconds/NULLIF(RunSeconds,0)       AS P_tinh_lai,
       GoodCount*1.0/NULLIF(TotalCount,0)      AS Q_tinh_lai,
       Availability, Performance, Quality, Oee, Warnings   -- Warnings là cờ bit: 1 P>100%, 2 Good>Total, 4 Down>Planned, 8 lệch đếm
FROM OeeSnapshots s JOIN Machines m ON m.Id = s.MachineId
WHERE m.Code = 'L1-SMT-01' AND s.Hour IS NULL AND s.ShiftDate BETWEEN '2026-10-01' AND '2026-10-05'
ORDER BY ShiftDate;

-- OEE của CẢ TUẦN: cộng tổng rồi mới chia, KHÔNG lấy trung bình các %
SELECT SUM(IdealSeconds * GoodCount / NULLIF(TotalCount,0)) / SUM(PlannedSeconds) AS OeeTuan
FROM OeeSnapshots s JOIN Machines m ON m.Id = s.MachineId
WHERE m.Code = 'L1-SMT-01' AND s.Hour IS NULL AND s.ShiftDate BETWEEN '2026-10-01' AND '2026-10-07';

-- Pareto thô: phút dừng theo lý do
SELECT r.Code, r.Category, COUNT(*) AS Lan, SUM(DATEDIFF(second, h.StartAt, h.EndAt))/60.0 AS Phut
FROM MachineStateHistory h JOIN DowntimeReasons r ON r.Id = h.DowntimeReasonId
WHERE h.EndAt IS NOT NULL GROUP BY r.Code, r.Category ORDER BY Phut DESC;
```

Kết quả chạy thật trên DB dev (04/11/2026):

| Ngày | Ca | Total | Good | Scan | OEE | Warnings |
| --- | --- | --- | --- | --- | --- | --- |
| 01/10 | 2 (S1) | 1.000 | 970 | 1.000 | 79,8 % | 0 |
| 01/10 | 1, 3, 4 | 0 | 0 | 0 | 0 % | 0 |
| 02/10 | 2 | 1.440 | 1.397 | 1.440 | 115,0 % | 1 |
| 05/10 | 2 | 1.000 | 910 | 940 | 74,9 % | 8 |

Câu "OEE tuần" ra khoảng **44 %**. Vì sao thấp vậy?

- Ngày 01/10 có 3 ca **không sản xuất** nhưng vẫn có 420 phút Planned mỗi ca, vì ca có trong lịch mà không có ai gán lý do `NOPLAN`.
- Đây đúng là chuyện ngoài đời: "OEE tháng này tụt" thường là do có ca trống không được khai báo là không có kế hoạch.
- Sửa **dữ liệu nghiệp vụ** (lịch, lý do dừng), không sửa công thức.
- Thử: chỉ cộng các ca có `TotalCount > 0`, so hai con số, và nghĩ xem con số nào nên đưa lên báo cáo.

Câu hỏi:
- Vì sao "OEE tuần = trung bình OEE các ngày" là sai? Gợi ý: một ngày chạy 2 giờ và một ngày chạy 8 giờ không nặng như nhau.

## 3. Drill sự cố (2 giờ)

### Drill A: "Quản lý hỏi: OEE ngày 02/10 là 115 %, máy chạy giỏi quá à?"
**Tự làm trước:** chỉ dùng SQL ở trên và `WorkOrderOperations` (cycle time của WO), tìm ra con số nào sai và đề xuất ai sửa.

<details><summary>Đáp án gợi ý</summary>

- **Không phải máy giỏi.** P = 133 % nghĩa là máy ra hàng **nhanh hơn** cycle time lý tưởng đã khai. Không máy nào chạy nhanh hơn "lý tưởng", nên con số lý tưởng sai: WO ghi 20 s, máy thật khoảng 15 s.
- **Nguyên nhân hay gặp ngoài đời:**
  - routing chưa cập nhật sau khi nâng cấp máy;
  - cycle time nhập theo **panel** (nhiều board ghép trên một tấm) trong khi máy đếm theo **board**;
  - đơn vị giây/phút nhầm.
- **Hệ thống làm đúng:** **giữ số thô và gắn cờ**, không cắt về 100 %. Che lỗi còn tệ hơn một con số xấu.
- **Xử lý:** IE/PE đo lại và sửa routing (version mới). WO sau sẽ đúng; WO cũ giữ snapshot.
</details>

### Drill B: "Ngày 05/10 Quality chỉ 91 %, QC nói NG thật chỉ khoảng 3 %"
<details><summary>Đáp án gợi ý</summary>

- Cờ *Counter mismatch*: máy đếm 1.000, chỉ có 940 dòng scan (lệch > 2 %).
- Theo dữ liệu demo, từ khoảng 13:38 máy scan ở trạm hỏng hoặc operator bỏ scan, nên 60 board "không có tên". Q tính theo scan nên bị kéo xuống.
- **Tìm giờ bắt đầu lệch:** xem `OeeSnapshots` theo giờ (`Hour IS NOT NULL`), so `TotalCount` với `ScanCount`.
- **Hệ quả nghiêm trọng hơn OEE:** 60 board **không có lịch sử scan**, nên không truy xuất được. Phải báo QC xử lý các board đó: scan bổ sung có kiểm soát, hoặc Hold.
- **Bài học:** hai nguồn độc lập thì đối chiếu được. Lệch thì cảnh báo, **không tự sửa số**, vì không biết bên nào sai.
</details>

### Drill C: "Supervisor đổi lý do dừng của ca tuần trước, báo cáo không đổi, log API báo lỗi mỗi phút"
**Đọc:** incident "Đổi lý do dừng của ca cũ làm job snapshot OEE lỗi mỗi phút" (M5 bước 9).

<details><summary>Ý chính</summary>

- **Cơ chế đúng:** đổi lý do thì snapshot của ca đó bị đánh dấu chưa final, và job tính lại.
- **Bug đã gặp:** ca cũ hơn khoảng "bắt kịp" làm job dựng lại bucket, chèn trùng, unique index chặn (`duplicate key`) mỗi phút.
- **Bài học support:** log lỗi lặp lại **mỗi phút** gần như luôn là job nền gặp cùng một dữ liệu xấu. Tìm dữ liệu vừa đổi (`AuditLog`, `TableName = 'MachineStateHistory'`) quanh lúc lỗi bắt đầu.
</details>

## 4. Câu hỏi phỏng vấn tuần 5

<details><summary>1. OEE là gì? Tính thế nào? Cho ví dụ số.</summary>

OEE = A × P × Q, hoặc gọn: thời gian tạo ra hàng tốt / thời gian kế hoạch. Đọc vanh vách ca mẫu: 405 → 360 → 333,33 → 323,33 phút, ra 79,84 %.
</details>

<details><summary>2. OEE 85 % có tốt không? Muốn cải thiện thì bắt đầu từ đâu?</summary>

- **Phụ thuộc ngành:** SMT chạy nhiều model, changeover nhiều thì 85 % rất cao.
- **Bắt đầu:** xem thành phần nào thấp nhất (A, P hay Q). Với A thì làm Pareto lý do dừng, chọn top 1–2, tìm nguyên nhân gốc (5 Why) và hành động.
- **Ví dụ SMT:**
  - `FDR-JAM` nhiều: bảo trì feeder, đào tạo nạp reel;
  - `MAT-WAIT`: kho cấp liệu trễ, cải thiện bằng cảnh báo sắp hết reel;
  - changeover dài: SMED (chuẩn bị feeder offline).
</details>

<details><summary>3. Micro-stop là gì? Vì sao quan trọng?</summary>

- Dừng ngắn (ở đây < 60 giây), không ai ghi, nhưng cộng lại có thể mất hàng chục phút mỗi ca.
- Không trừ vào A mà hiện ra ở **P**, vì máy "chạy" nhưng ra ít hàng.
- Chỉ đo được khi lấy dữ liệu tự động từ máy; ghi tay không bao giờ có.
</details>

<details><summary>4. Total Count lấy ở đâu, Good lấy ở đâu? Vì sao không lấy cả hai từ một nguồn?</summary>

- **Total:** bộ đếm máy, khách quan, có cả hàng không scan.
- **Good/NG:** scan theo serial; biết **con nào** hỏng và lỗi gì.
- Hai nguồn đối chiếu được nhau; lệch > 2 % thì gắn cờ (ADR-007).
</details>

<details><summary>5. Ca đêm 22:00–06:00 tính vào ngày nào? Có gì khó?</summary>

- Tính vào **ngày bắt đầu ca** (ShiftDate). Dữ liệu lưu UTC, ca theo giờ nhà máy, nên phải đổi múi giờ đúng.
- Giờ nghỉ và PlannedStop có thể **chồng nhau**, nên lấy **phép hợp** khoảng thời gian, không trừ hai lần (ca (b) trong `oee-hand-calcs.md`).
- Dự án có lịch nghỉ lễ Việt Nam (`VietnamHolidays`).
</details>

<details><summary>6. OEE realtime và OEE báo cáo khác nhau thế nào?</summary>

- **Realtime:** tính mỗi lần hỏi, số có thể nhảy khi dữ liệu về muộn.
- **Snapshot:** job chốt từng giờ sau khi hết giờ đó 5 phút (để chờ message về muộn); dùng cho lịch sử và Excel.
- Cùng một hàm tính, nên hai bên không lệch logic.
- Job "bắt kịp": API tắt 3 giờ thì lần chạy sau chốt bù đủ; chạy lại không tạo dòng trùng (upsert theo unique index).
</details>

<details><summary>7. Changeover có tính vào downtime không?</summary>

- Tùy chính sách nhà máy; phải thống nhất và ghi rõ định nghĩa.
- Dự án chọn **tính** (Planned nhưng tính vào A) để đo và giảm được.
- Nếu loại khỏi Planned thì OEE đẹp hơn nhưng che mất cơ hội cải thiện.

Đây là câu kiểm tra xem bạn có hiểu "định nghĩa quyết định con số" không.
</details>

<details><summary>8. Báo cáo OEE xuất Excel cần chú ý gì?</summary>

- Số thật định dạng %, không phải chuỗi, để người dùng tự tính tiếp được.
- Giờ theo nhà máy, không UTC.
- Có sheet `Definitions` giải thích công thức. Tranh cãi về OEE thường là tranh cãi về định nghĩa.
- Cộng nhiều ngày thì cộng tổng rồi mới chia.
</details>

## 5. Tự kiểm cuối tuần

- [ ] Tính tay ca mẫu trên giấy, không nhìn tài liệu, ra 79,84 %.
- [ ] Giải thích được 3 ngày demo (01, 02, 05/10) cho một "quản lý" tưởng tượng trong 3 phút.
- [ ] Viết được SQL OEE tuần bằng "cộng rồi chia".
- [ ] Trả lời ≥ 7/8 câu.
