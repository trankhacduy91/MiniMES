# Tuần 1: Bức tranh nhà máy và vai trò IT MES

> Mục tiêu tuần: nói được trong 2 phút "MES là gì, nằm ở đâu, ai dùng, IT MES làm gì", và chạy được cả hệ thống MiniMES từ đầu.
> Thời lượng: ~10 giờ (đọc 3 · lab 3 · drill 2 · nói 2). Lộ trình tổng: 8 tuần.

## 1. Đọc (3 giờ)

- [ ] `spec/spec-v2.md`:
  - B0: mục tiêu;
  - B2: kịch bản demo 13 bước, đọc như đọc một ngày ở nhà máy;
  - B3: các tầng ISA-95.
- [ ] `spec/module-00-foundation.md`: phần mở đầu và phần "ai dùng".
- [ ] `ARCHITECTURE.md`. Lần này chỉ đọc 3 quyết định:
  - ADR-002: WPF gọi API, không nối DB;
  - ADR-003/004: audit trail;
  - ADR-008: dữ liệu máy đi qua MQTT.
- [ ] README: mục "Tài khoản demo" và "Kịch bản demo".

Khi đọc, ghi ra giấy 3 cột: **Ai** (operator, planner, QC, supervisor, kho, bảo trì) · **Họ làm gì trên MES** · **Khi hỏng thì họ gọi ai**.

## 2. Lab: chạy cả hệ thống và đóng vai từng người (3 giờ)

1. Bật hạ tầng: `docker compose up -d sqlserver mosquitto`, rồi chạy API (`dotnet run --project src/MiniMes.Api`).
2. Mở http://localhost:5157/health để kiểm tra API sống, rồi mở Swagger.
3. Chạy Simulator, Gateway, BackOffice và Kiosk theo README.
4. Đăng nhập lần lượt các tài khoản dưới đây. Mỗi tài khoản, ghi lại màn hình thấy được và một việc làm được:

| Tài khoản | Vai trò ngoài đời | Việc thử |
| --- | --- | --- |
| `planner01` | Kế hoạch sản xuất | Tạo WO 20 pcs, Release |
| `EMP00123` (kiosk) | Operator | Chọn WO, scan 1 serial ở trạm SMT |
| `qc01` | QC | Mở control chart, NCR |
| `sup01` | Trưởng ca | Hold/Resume WO, duyệt NCR |
| `admin` | IT MES | Master data, import |

5. Mở SSMS hoặc Azure Data Studio, nối tới `localhost,1433`. Mật khẩu SA lấy trong user-secrets hoặc `.env` của bạn. Chạy 3 câu SQL ở mục 4 bên dưới.

**Sản phẩm của tuần:** một trang sơ đồ "ai gọi IT MES vì chuyện gì" (vẽ tay cũng được). Gợi ý khung:

```
Operator ──(kiosk không scan được, máy in nhãn lỗi)──┐
QC ───────(cần truy xuất, Hold lot, số SPC lạ)───────┤
Planner ──(WO không release được, sai BOM)───────────┼──► IT MES ──► vendor / IT hạ tầng / tự sửa
Bảo trì ──(máy chạy mà dashboard báo Offline)────────┤
Quản lý ──(OEE/báo cáo sai, cần xuất Excel)──────────┘
```

## 3. Drill sự cố: "Kiosk trạm ASSY báo Offline, line dừng" (2 giờ)

**Tình huống:** 2 giờ sáng, trưởng ca gọi: kiosk trạm L1-ASM-01 hiện banner đỏ Offline, operator không scan được.

**Cách tạo lỗi:** khi Kiosk đang chạy, tắt API (Ctrl+C ở cửa sổ API). Thử thêm lần hai: để API chạy nhưng tắt SQL Server (`docker stop mes-sqlserver`).

**Tự làm trước, chưa mở đáp án:** viết ra 5 bước kiểm tra theo thứ tự, mỗi bước kèm lệnh hoặc nơi xem.

<details><summary>Đáp án gợi ý</summary>

1. **Phạm vi:** một trạm hay cả line? Hỏi trưởng ca hoặc nhìn kiosk ở trạm khác. Nếu cả line bị thì gần như chắc chắn lỗi ở server, không phải PC trạm.
2. **API sống không:** mở `http://<server>:5157/health` từ máy khác.
   - Không trả lời: process API chết. Xem log trong `logs/minimes-*.log` và Event Viewer.
3. **Database:** chạy `docker ps` (hoặc xem service SQL Server ngoài đời).
   - Nếu API vẫn sống mà DB chết, `/health` trả lỗi và log có `SqlException`.
4. **Khôi phục:** khởi động lại đúng thành phần hỏng, xác nhận `/health` OK. Kiosk tự hết Offline vì có cơ chế thử kết nối lại, không cần khởi động lại kiosk.
5. **Sau sự cố:**
   - Trong lúc Offline kiosk chặn scan, nên không có dữ liệu "nửa vời". Đây là quyết định thiết kế: B10 chọn không làm kiosk offline.
   - Ghi incident: thời điểm, ảnh hưởng (line dừng bao lâu), nguyên nhân, cách phòng.

**Câu nên nói khi phỏng vấn:** "Việc đầu tiên tôi làm là xác định phạm vi ảnh hưởng, rồi đi từ ngoài vào trong: mạng → API → DB. Khôi phục sản xuất trước, tìm nguyên nhân gốc sau."
</details>

## 4. SQL khởi động (trong lab)

Lưu ý khi viết SQL: các cột trạng thái lưu dạng **chuỗi** (ví dụ `'InProcess'`), không phải số.

```sql
-- a) WO nào đang chạy, bao nhiêu serial ở từng trạng thái?
SELECT w.WoNo, w.Status AS WoStatus, s.Status AS SerialStatus, COUNT(*) AS Qty
FROM WorkOrders w JOIN Serials s ON s.WorkOrderId = w.Id
GROUP BY w.WoNo, w.Status, s.Status
ORDER BY w.WoNo, s.Status;

-- b) 20 lần scan gần nhất: ai, ở trạm nào, kết quả gì?
SELECT TOP 20 l.ScannedAt, l.SerialNo, st.Code AS Station, l.Seq, l.Result, l.DefectCode, l.OperatorCode, l.Source
FROM ScanLogs l JOIN Stations st ON st.Id = l.StationId
ORDER BY l.Id DESC;

-- c) Ai đã sửa gì trong 1 giờ qua? (audit, chỉ INSERT)
SELECT TOP 50 * FROM AuditLog ORDER BY Id DESC;
```

Tự hỏi sau khi chạy:
- Vì sao `ScannedAt` lệch 7 giờ so với đồng hồ của bạn? (Hệ thống lưu UTC, hiển thị theo giờ nhà máy.)
- `Source` có những giá trị nào, và mỗi giá trị đến từ đâu (kiosk hay simulator)?

## 5. Câu hỏi phỏng vấn tuần 1 (2 giờ: tự trả lời to, ghi âm, rồi mới mở đáp án)

Mục tiêu: trả lời được ≥ 8/10 câu mà không nhìn tài liệu.

<details><summary>1. MES là gì? Khác ERP và SCADA ở đâu?</summary>

- **ERP (L4):** kế hoạch theo ngày/tuần, tiền, kho tổng. Biết "cần làm 200 cái".
- **SCADA/PLC (L1–L2):** điều khiển máy theo mili giây. Biết "máy đang chạy, nhiệt độ 245 °C".
- **MES (L3):** nằm giữa, theo dõi **từng sản phẩm, từng phút**: WO nào, serial nào đi qua trạm nào, dùng reel nào, ai làm, đạt hay không.

Ví dụ trong dự án: route check chặn board nhảy bước. ERP không làm việc này (quá chi tiết), PLC cũng không (không biết serial thuộc WO nào).
</details>

<details><summary>2. Mô tả các tầng ISA-95 bằng chính dự án của bạn.</summary>

Xem sơ đồ B3:
- L0/L1: simulator đóng vai máy;
- L2: Mosquitto và Edge Gateway (OPC UA, Modbus → MQTT);
- L3: MiniMES API cùng SQL Server và hai app WPF;
- L4: ERP (ngoài phạm vi).

Topic MQTT cũng đặt theo cấp ISA-95: `minimes/{site}/{area}/{line}/{machine}/...` (Unified Namespace).
</details>

<details><summary>3. Một line SMT gồm những công đoạn nào?</summary>

- In kem hàn (stencil);
- gắp đặt linh kiện (pick & place, máy SMT, nạp reel);
- lò reflow (nung chảy kem hàn theo profile nhiệt);
- AOI/Test (kiểm tra quang học hoặc điện);
- Assembly (lắp tay);
- Pack.

Routing của `PCB-A` trong dự án (seed ở `MasterDataSeeder`) có 4 bước: SMT (10) → ASSY (20) → TEST (30) → PACK (40). Lò reflow không phải một bước scan: board qua lò cùng dòng SMT, còn dữ liệu lò (nhiệt độ, bộ đếm) đi riêng qua OPC UA.
</details>

<details><summary>4. Work Order, Serial, Lot khác nhau thế nào?</summary>

- **WO:** lệnh sản xuất, ví dụ "200 PCB-A trên Line 1".
- **Serial:** mã của **một** thành phẩm; mỗi board một mã, in nhãn ZPL lúc release.
- **Lot:** mã của **một lô vật tư**, ví dụ một reel linh kiện, theo lô nhà cung cấp.

Truy xuất: serial ↔ lot, nối với nhau bằng bảng `SerialComponents`, ghi khi scan tiêu thụ vật tư.
</details>

<details><summary>5. IT MES ở nhà máy làm những việc gì hằng ngày?</summary>

- Hỗ trợ line: scan lỗi, máy in nhãn, kiosk Offline.
- Master data: item, BOM, routing mới, đổi version.
- Kết nối máy: máy mới, tag mới, gateway mất kết nối.
- Báo cáo và SQL: truy xuất cho QC khi khách khiếu nại, số liệu OEE/FPY.
- Quản lý user và quyền; backup; theo dõi hiệu năng DB.
- Phối hợp: Sản xuất, QC, Kho, Bảo trì, IT hạ tầng, vendor MES.
</details>

<details><summary>6. Vì sao app ở trạm không nối thẳng vào database?</summary>

ADR-002, ba lý do:
1. Quy tắc nghiệp vụ (route check, cổng FAI, Hold) nằm ở **một chỗ** trên server; trạm không thể bỏ qua.
2. Không phải phát mật khẩu DB tới hàng chục PC trên line.
3. Đổi DB hay schema không phải cài lại kiosk.

Ghi chú thêm: các MES cũ ở nhà máy thường nối thẳng DB, nên người phỏng vấn có thể hỏi điều này để xem bạn hiểu rủi ro.
</details>

<details><summary>7. Audit trail là gì, vì sao nhà máy cần?</summary>

Ghi lại **ai, sửa gì, lúc nào, giá trị cũ và mới**.
- Khách hàng ô tô và y tế (IATF 16949, FDA) bắt buộc chứng minh được ai đã thay đổi dữ liệu chất lượng.
- Trong dự án: interceptor EF ghi cùng transaction; bảng `AuditLog` có trigger chặn UPDATE/DELETE (ADR-003/004).
</details>

<details><summary>8. Line dừng lúc 2 giờ sáng vì hệ thống, bạn làm gì đầu tiên?</summary>

Theo thứ tự: xác định phạm vi → khôi phục sản xuất → tìm nguyên nhân gốc → ghi incident và báo cáo.
- Kể bằng drill ở mục 3 của tuần này.
- Nói thêm cách liên lạc: báo trưởng ca thời gian dự kiến, và quyết định có cho line ghi tay tạm thời rồi nhập bù sau hay không.
</details>

<details><summary>9. Dữ liệu máy vào MES bằng đường nào?</summary>

Có 3 đường, ứng với 3 loại máy:
- máy "thông minh" tự đẩy MQTT;
- lò reflow là OPC UA server, gateway **subscribe**;
- máy cũ dùng Modbus TCP, gateway **poll** thanh ghi.

Gateway chuẩn hóa mọi thứ về MQTT theo UNS. Phần này tuần 4 học sâu; tuần này chỉ cần nói được 3 đường.
</details>

<details><summary>10. Giới thiệu dự án của bạn trong 1 phút.</summary>

Khung gợi ý:
- "Mini MES cho line lắp ráp điện tử, .NET 10 + SQL Server + WPF, gồm 8 module."
- Kể tóm tắt kịch bản demo B2: WO → FAI → scan có route check → máy gửi dữ liệu qua 3 giao thức → OEE → reel lỗi → truy xuất → Hold.
- Chọn 2 con số: route check p95 24,8 ms trên 10 triệu scan; CI có 474 integration test với SQL Server và Mosquitto thật.
- Kết bằng một incident đáng nhớ trong `docs/incidents.md`.
</details>

## 6. Tự kiểm cuối tuần

- [ ] Chạy được cả hệ thống từ đầu trong < 15 phút, không phải hỏi.
- [ ] Có trang sơ đồ "ai gọi IT MES".
- [ ] Viết được 5 bước của drill mà không mở đáp án.
- [ ] Trả lời ≥ 8/10 câu; câu nào chưa trả lời được thì ghi lại để ôn ở tuần 8.
