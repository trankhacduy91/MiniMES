# M5 · Bước 9 — Kiểm tra chéo bộ đếm, cờ cảnh báo, chạy demo: kiến thức cần nắm

> Tài liệu học cho bước 9 của [module-05](../../spec/module-05-oee-downtime.md) (spec mục **1.2.10**, quy tắc 9–10, DoD 5.1).
> Bước trước: [M5-step08b](M5-step08b-line-tv.md).
> Code: `src/MiniMes.Domain/Oee/CounterCrossCheck.cs`, `src/MiniMes.Infrastructure/Persistence/Seed/OeeDemoSeeder.cs`,
> `src/MiniMes.MachineSimulator/sim-oee-demo.json`. Ảnh: [P > 100 %](../images/m5-oee-performance-over-100.png),
> [Counter mismatch](../images/m5-oee-counter-mismatch.png).

---

## A. Nghiệp vụ: hai nguồn đếm, đối chiếu chứ không "sửa"

| Đại lượng | Nguồn | Biết gì |
| --- | --- | --- |
| Total Count | Bộ đếm máy (MQTT) | "Bao nhiêu" lượt máy xử lý |
| Good / NG | Scan theo serial (M3) | "Con nào" tốt/hỏng, lỗi gì |

Hai nguồn độc lập nên **đối chiếu được**. Lệch > 2 % so với số máy đếm nghĩa là một bên sai: máy đếm cả chu kỳ chạy
không, operator bỏ scan, máy scan hỏng. Khi đó Q và OEE không đáng tin: thiếu 60 scan làm Q = 91 % dù NG thật chỉ 3 %.
Hệ thống **gắn cờ `CounterMismatch`, giữ nguyên số**, giống P > 100 %: che lỗi tệ hơn một con số xấu (quy tắc 9).

So sánh **số dòng scan**, không `COUNT(DISTINCT SerialNo)`: bo NG sửa xong test lại là lượt xử lý thứ hai ở cả máy và
`ScanLog`.

## B. Thiết kế

- **Hàm thuần trong Domain:** `CounterCrossCheck.IsMismatch(machine, scans)`: `Gap = |máy − scan| / máy > 0,02`.
  Máy đếm 0 thì không có mẫu số: 0/0 là khớp, 0/5 là lệch (máy không được nghe thấy).
- **Quyết định cờ theo đúng tổng đang hiển thị.** `Apply` đặt **hoặc xóa** cờ theo tổng được đưa vào. Gọi ở
  `OeeSnapshot.Apply` (lưu) và ở `ToDto` (mọi số API trả về). Tổng ca hay tổng line được xét trên tổng của chính nó: một
  giờ lệch 3 bo không làm cả ca có cờ. Đây là cùng tinh thần với quy tắc 12 (cộng rồi mới chia).
- **Cửa sổ nhỏ dễ vượt 2 %.** Đầu ca 5 đếm và 7 scan (một bo FAI scan tay, một bo đang trong chu kỳ) là lệch 40 %. Cờ
  có nghĩa ở mức ca; trên dashboard cờ hiện ở tổng ca.

## C. "Cycle time 15 s thay vì 20 s" đọc thế nào

P = Σ(count × ideal cycle time) / run time. P > 100 % khi máy ra bo **nhanh hơn** con số "lý tưởng" trong WO. Vậy
dữ liệu sai là: **WO ghi 20 s, máy thật ra một bo mỗi ~15 s** → 1.440 bo trong 360 phút → P = 133 %. Nếu ngược lại
(WO ghi 15 s, máy chạy 20 s) thì P chỉ thấp đi, không có cờ. Nguyên nhân thật hay gặp: cycle time nhập theo panel
trong khi máy đếm theo bo, routing cũ chưa cập nhật sau khi nâng cấp máy.

## D. Dữ liệu demo phải "thật" đến từng giờ

Lần đầu seeder cho bộ đếm tăng đều suốt ca, kể cả lúc Down và giờ nghỉ. Tổng ca vẫn đúng 79,84 %, nhưng xem theo giờ thì:
- giờ có dừng ra P > 100 % (đếm vẫn tăng mà run time bị trừ);
- giờ nào cũng lệch ~2 % giữa đếm và scan (hai nhịp 27,6 s và 27 s).

Sửa: bo chỉ được làm trong thời gian **Running**, scan cùng lúc với message đếm bo đó. Bài học: dữ liệu mẫu cũng cần
kiểm theo từng giờ, vì cờ cảnh báo ở mức giờ sẽ "bắt lỗi" chính dữ liệu giả.

## E. Bug tìm được khi chạy demo (ghi `docs/incidents.md`)

Đổi lý do dừng của một ca **cũ hơn khoảng bắt kịp** làm job snapshot lỗi duplicate key mỗi phút. Chỉ dòng chồng lên đoạn
dừng bị đánh dấu chưa final; service chỉ nạp các dòng đó nhưng dựng bucket cho cả ngày, nên giờ đã final bị coi là
"chưa có" và INSERT lại. Unique index chặn được (đúng việc của nó), test bước 6 không bắt vì đổi lý do ngay trong
khoảng bắt kịp. Sửa: nạp **mọi** dòng của các ngày sẽ xét. Test mới đỏ khi bỏ bản sửa.

## F. Kịch bản demo trực tiếp

1. `docker stop mes-simulator` (hai simulator cùng đóng `L1-SMT-01` sẽ đá nhau).
2. `dotnet run --project src/MiniMes.MachineSimulator -- --config src/MiniMes.MachineSimulator/sim-oee-demo.json`:
   chỉ một máy, cycle 30 s (= cycle time routing), không tự Down, không NG → mọi dừng trên dashboard là dừng do mình ép.
3. Gõ `down L1-SMT-01 300` → 5 phút sau đoạn Down nằm trong *Stops to classify*, Run chỉ tăng vài giây.
4. Chọn `PM` (không phạt A) → Planned giảm đúng 300 s, A tăng (25,2 % → 29,0 % lần chạy thử); chọn lại `FDR-JAM` → A
   về mức cũ. Pareto của ca cộng đúng bằng phần A mất.

Điều kiện: một WO có bo đang chờ ở SMT và đã qua FAI; simulator bỏ qua WO đang chờ FAI.

## G. Câu hỏi phỏng vấn

1. **"Total từ máy, Good từ scan: hai nguồn lệch nhau thì sao?"** Đối chiếu số dòng scan với số máy đếm; lệch > 2 % gắn
   cờ, không sửa số, vì không biết bên nào sai.
2. **"OEE 115 % có phải máy rất tốt?"** Không: P > 100 % là cycle time chuẩn sai (hoặc bộ đếm sai). Giữ số thô và cờ.
3. **"Bạn test job chạy nền thế nào?"** Hàm thuần + service với `FakeTimeProvider` + SQL Server thật (Testcontainers),
   cộng một case "sửa dữ liệu cũ ngoài khoảng thường xét": chính case đó lộ bug duplicate key.
