# Tuần 8: Tổng hợp, kể chuyện và mock interview

> **Mục tiêu tuần:** biến 7 tuần kiến thức thành **câu trả lời có cấu trúc** và **bằng chứng nhìn thấy được**:
> - video demo;
> - 5 câu chuyện STAR;
> - glossary;
> - CV đã cập nhật;
> - 3 buổi mock interview.
>
> **Thời lượng:** ~10–12 giờ.

## 0. Phỏng vấn IT MES ở KCN thường thế nào

Đây là kinh nghiệm chung, mỗi công ty một khác.

- **Vòng 1 (HR/IT manager):**
  - giới thiệu bản thân;
  - vì sao chuyển từ web sang nhà máy;
  - có chịu được ca, trực và gọi đêm không;
  - tiếng Anh, có khi tiếng Nhật hoặc Hàn tùy công ty mẹ.
- **Vòng 2 (kỹ thuật):**
  - **SQL trên giấy hoặc máy:** JOIN, GROUP BY, tìm bản ghi trùng, top N theo nhóm; đôi khi index và execution plan;
  - **câu tình huống:** "line dừng vì hệ thống, bạn làm gì";
  - **nghiệp vụ:** WO, routing, truy xuất, OEE;
  - **kết nối máy:** PLC, OPC UA, Modbus.

  Ít khi hỏi sâu thuật toán.
- **Họ muốn thấy:**
  1. hiểu nhà máy chạy thế nào;
  2. bình tĩnh và có phương pháp khi sự cố;
  3. phối hợp được với Sản xuất/QC;
  4. học nhanh MES của vendor họ đang dùng. Họ thường có hệ thống sẵn (Siemens Opcenter, Critical Manufacturing, Aegis, MES tự viết…), ít khi viết MES mới từ đầu.
- **Lợi thế của bạn:** đã tự làm một MES đủ module, nên nói được **vì sao** hệ thống làm như vậy, không chỉ **cách dùng**. Kể bằng số liệu và incident thật.
- **Điểm yếu cần trả lời trước:** "chưa làm nhà máy thật". Câu trả lời: "Đúng, nên tôi đã dựng mô phỏng sát nhất có thể: 3 giao thức máy, ca và giờ nghỉ thật, các tình huống lỗi thật, và ghi lại 34 sự cố gặp phải. Tôi biết mình còn thiếu cảm giác hiện trường, nên tháng đầu tôi muốn đi line nhiều nhất có thể."

## 1. Video demo 5–7 phút (3 giờ)

Quay màn hình bằng OBS hoặc Xbox Game Bar (`Win+G`), có giọng nói (tiếng Việt hoặc tiếng Anh). Kịch bản theo `spec/spec-v2.md` B2, rút gọn:

| Phút | Cảnh | Câu nói chính |
| --- | --- | --- |
| 0:00 | Sơ đồ ISA-95 (B3) | "Mini MES cho line SMT: 3 giao thức máy, 8 module" |
| 0:30 | Import Excel, tạo WO, Release, nhãn | "BOM/routing có version; release chụp snapshot" |
| 1:30 | FAI: board mẫu, phiếu tự điền, Pass | "Cổng chặn trong route check" |
| 2:15 | Kiosk scan: OK, nhảy bước bị chặn, NG → Repair | "Interlock ở server" |
| 3:00 | Dashboard máy, `down`, Andon leo thang | "MQTT, OPC UA, Modbus về chung một hợp đồng" |
| 3:45 | Tắt broker 60 giây, bật lại, số đếm không mất | "Store-and-forward" |
| 4:15 | Lò trôi, SPC báo ở 247 °C (USL 255) | "Báo trước khi ra hàng hỏng" |
| 5:00 | Reel lỗi: truy xuất xuôi, Hold all, kiosk đỏ, Release | "Một transaction, chặn mọi điểm" |
| 6:00 | OEE dashboard + Excel | "79,8 % khớp tính tay" |

Mẹo: chuẩn bị dữ liệu trước; quay từng cảnh rồi ghép. Đặt video lên YouTube (unlisted) và gắn link vào README và CV.

## 2. Glossary `docs/glossary.md` (1,5 giờ)

**Tự viết**, mỗi thuật ngữ một dòng tiếng Việt kèm ví dụ trong dự án. Tự viết mới nhớ. Spec M7 mục 1.6 và các spec module khác có bảng thuật ngữ để đối chiếu. Danh sách tối thiểu:

ISA-95 · MES/ERP/SCADA · UNS · Work Order · Routing · BOM · ECN/ECO · NPI · Serial/Lot/Supplier lot · Dispatch list · Route check / Interlock / Poka-yoke · FPY / RTY / Final yield · Repair / Rework / Scrap · Changeover / SMED · OPC UA / Modbus / MQTT · Subscription / Polling · Store-and-forward · LWT · Andon / Escalation · OEE / A / P / Q · Micro-stop · Six big losses · Pareto · IQC / IPQC / OQC · AQL · FIFO · MSL · Traceability (backward/forward) · Genealogy · FAI · SPC / I-MR · USL / UCL · Cp / Cpk · Western Electric · NCR / Disposition / MRB / Concession · SCAR / 8D · Hold / Release / Containment · Audit trail · IATF 16949 / ISO 9001 · PPAP · 5S / Kaizen / PDCA / 5 Why.

## 3. Năm câu chuyện STAR (2 giờ)

**STAR** = Situation (bối cảnh) · Task (nhiệm vụ) · Action (bạn làm gì) · Result (kết quả, có số). Mỗi chuyện **2 phút** khi nói. Dưới đây là 5 gợi ý chọn từ `docs/incidents.md`; bạn đọc incident gốc rồi tự viết lại bằng lời của mình.

**1. "Máy dán keo báo 2,26 tỷ sản phẩm"** (M4b): kết nối máy, đọc tài liệu thanh ghi.
- **S:** đọc bộ đếm Modbus của máy cũ qua gateway.
- **T:** sản lượng phải đúng để tính OEE.
- **A:** nhận ra 2,26 tỷ ≈ 65.536 × số thật, tức sai thứ tự word; đọc thô 2 thanh ghi, ghép tay; sửa cấu hình `wordOrder`; viết test cho cả 4 thứ tự.
- **R:** số đúng; có quy trình kiểm khi thay PLC.

**2. "Tắt broker làm máy bị Offline oan"** (M4): logic phân tán, không vội tin tín hiệu.
- **S:** tắt Mosquitto để thử mất mạng.
- **T:** không được báo máy Offline oan.
- **A:** đọc thứ tự sự kiện, phát hiện broker phát LWT hộ các client lúc tự tắt; đổi thành "báo cáo cần xác nhận 3 giây, cùng phiên kết nối".
- **R:** tắt broker 2 phút thì 0 máy Offline oan.

**3. "Hold đúng lúc scan: operator nhận lỗi 422"** (M7): race condition, góc nhìn người dùng.
- **S:** Hold hàng loạt chạy song song với scan.
- **T:** board bị Hold thì kiosk phải báo dễ hiểu.
- **A:** test đua 2 luồng thật bắt được khe giữa hai lần đọc; thêm kiểm tra rồi retry.
- **R:** kiosk luôn nhận `SerialHold`; mutation test chứng minh test bắt được lỗi.

**4. "Kem hàn bị trừ nhiều hơn 57 % mỗi board"** (M6): dữ liệu sai im lặng, kiểm tra ở hai đầu.
- **S:** tiêu hao vật tư theo BOM.
- **T:** tồn kho reel phải đúng.
- **A:** so số trên bảng với snapshot BOM, tìm ra `decimal(18,3)` làm tròn im lặng; migration đổi sang `decimal(18,6)`.
- **R:** đúng tới 6 chữ số; bài học test với dữ liệu "khó" (kg, số rất nhỏ).

**5. "Đo hiệu năng tìm ra race condition mà test đồng thời bỏ sót"** (M3), hoặc "Bẫy nvarchar": hiệu năng có số đo.
- **S:** route check phải < 100 ms trên 10 triệu dòng.
- **T:** đo và tối ưu.
- **A:** seed 10 triệu dòng, bench 8 luồng, so execution plan; tìm ra tham số `nvarchar` làm mất index (12 lên 49.895 logical reads).
- **R:** p95 24,8 ms; quyết định **không** cache vì không cần.

Dự phòng cho câu "kể một lần bạn sai": **"Test thống kê chứng minh spec sai, và phép tính tay của tôi xác nhận theo"** (M7). Bài học: kiểm chứng phải đến từ một đường độc lập. Câu chuyện thừa nhận sai lầm luôn được đánh giá cao.

## 4. Câu hỏi tổng hợp, trộn module (tự luyện)

Mỗi câu trả lời trong 2–3 phút, nối được nhiều module:

1. "Đi một vòng cho tôi: từ lúc kho nhận reel đến lúc pallet lên xe." (M6 → M2 → M3 → M3b; nhắc IQC, nạp liệu, FAI, scan, đóng gói, giao)
2. "Một board bị khách trả về. Bạn trả lời được những câu gì, bằng bảng nào?" (truy xuất, scan log, repair log, số đo, audit)
3. "Mất mạng toàn nhà máy 30 phút. Chuyện gì xảy ra với từng phần của hệ thống?" (kiosk Offline chặn scan; gateway đệm; OEE chốt bù; Andon không tới)
4. "OEE tháng này giảm 5 %, quản lý hỏi vì sao. Bạn bắt đầu từ đâu?" (A/P/Q nào giảm, Pareto, ca không khai `NOPLAN`, cycle time, lệch đếm)
5. "Hệ thống chậm vào đầu ca. Bạn điều tra thế nào?" (đo trước; DMV `sys.dm_exec_requests`, blocking; hot row; execution plan; job nền)
6. "Khách audit yêu cầu chứng minh ai đã thả lô hàng bị giữ tuần trước." (`HoldRecords`, `AuditLog` chỉ INSERT, NCR người duyệt)
7. "Vendor máy mới chỉ hỗ trợ OPC UA. Bạn đưa nó vào MES thế nào?" (endpoint, chứng chỉ, node id, cấu hình gateway, test bằng UaExpert, master data máy và tag)
8. "Một user Supervisor muốn quyền sửa trực tiếp trạng thái board cho nhanh. Bạn trả lời sao?"

## 5. SQL tự luyện (dạng hay gặp trong bài test)

Tự viết trên DB của bạn, không nhìn tài liệu:

1. Top 3 mã lỗi NG nhiều nhất **theo từng trạm** trong 7 ngày (gợi ý: `ROW_NUMBER() OVER (PARTITION BY …)`).
2. Serial có **nhiều hơn 1** lần NG ở TEST.
3. Với mỗi WO: số serial theo trạng thái, xoay thành cột (`SUM(CASE …)` hoặc `PIVOT`).
4. Sản lượng tốt theo **giờ nhà máy** (UTC+7) của hôm nay (`DATEADD(hour, 7, ScannedAt)`).
5. Khoảng thời gian (phút) giữa hai lần scan liên tiếp của một board (`LAG`).
6. Lot sắp hết hạn trong 30 ngày còn hàng, xếp theo FIFO.
7. Tìm `WoNo` bị trùng nếu không có unique index (`GROUP BY … HAVING COUNT(*) > 1`).

Ngoài ra, nắm các lệnh xem hệ thống: `sp_who2`, `sys.dm_exec_requests` + `sys.dm_exec_sql_text`, `SET STATISTICS IO, TIME ON`, xem execution plan; backup/restore một DB.

## 6. Mock interview (3 buổi × 45 phút)

Mở Claude Code trong thư mục dự án và gõ, ví dụ:
- "Mock interview IT MES, buổi 1: nghiệp vụ. Hỏi tôi từng câu, chờ tôi trả lời, chấm 1–5 và góp ý, 10 câu."
- "Buổi 2: tình huống sự cố, đóng vai trưởng ca gọi lúc 2 giờ sáng."
- "Buổi 3: SQL, cho đề trên schema MiniMES, tôi viết rồi bạn chấm."

Sau mỗi buổi, ghi các câu dưới 3 điểm vào cuối file này, ôn lại trước buổi sau.

## 7. CV (1 giờ)

- Dùng mẫu 4 dòng ở `spec/spec-v2.md` B9; thay số bằng số thật của bạn:
  - p95 **24,8 ms** / 10 triệu scan;
  - truy xuất xuôi 12.500 board khoảng 100 ms;
  - 1.253 unit + 474 integration test, CI xanh trên GitHub Actions.
- Thêm link GitHub (tag `v0.7`), video demo.
- Mục kỹ năng: SQL Server (index, execution plan, recursive CTE), MQTT/OPC UA/Modbus, .NET, WPF, Docker. Có thể thêm phần nghiệp vụ: OEE, SPC, traceability, IATF 16949 (ở mức hiểu).

## 8. Câu nên hỏi lại nhà tuyển dụng

- Nhà máy đang dùng MES nào? Tự viết hay của vendor? Đội IT MES có mấy người, chia ca thế nào?
- Máy kết nối bằng gì (OPC UA, CFX, file log)? Có edge gateway không?
- Một ngày điển hình và sự cố gần nhất đội đã xử lý?
- Tháng đầu người mới làm gì? Có được đi line và đào tạo về máy không?

## 9. Tự kiểm cuối cùng

- [ ] Video demo ≤ 7 phút, có link.
- [ ] `docs/glossary.md` ≥ 40 thuật ngữ, do bạn tự viết.
- [ ] 5 câu chuyện STAR, mỗi câu nói trôi chảy trong 2 phút.
- [ ] 3 buổi mock interview, điểm trung bình ≥ 4/5.
- [ ] CV mới có số liệu và link.

## Ghi chú sau mock interview

(ghi các câu yếu ở đây)
