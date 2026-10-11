# Mini MES — Spec v2 (đối chiếu với MES mã nguồn mở)

> File này gồm 2 phần:
> - **Phần A**: so sánh tính năng của `spec.md` và `module-00` → `module-07` với các dự án MES mã nguồn mở. Kết quả là danh sách tính năng đã đủ, còn thiếu và nên bỏ.
> - **Phần B**: khung spec cuối cùng để lập plan và code. Phần B **kế thừa** `spec.md`; chỗ nào không ghi lại thì giữ nguyên như `spec.md` và file module tương ứng.
>
> Ký hiệu: **[GIỮ]** giữ nguyên · **[THÊM]** tính năng mới · **[SỬA]** đổi phạm vi · **[BỎ]** đưa ra khỏi phạm vi.

---

# PHẦN A — So sánh với MES mã nguồn mở

## A1. Các dự án đã khảo sát

| Dự án | Stack | Vì sao đáng tham khảo |
| --- | --- | --- |
| [qcadoo/mes](https://github.com/qcadoo/mes) | Java, PostgreSQL, AGPL | MES web lâu năm (~950★) cho doanh nghiệp vừa và nhỏ: technology (BOM/routing), lệnh sản xuất, ghi nhận sản lượng, dòng vật tư |
| [Mes-Open/OpenMes](https://github.com/Mes-Open/OpenMes) | Laravel, PostgreSQL, MQTT | Bám ISA-95, giao diện tablet cho operator, Andon có leo thang, audit không sửa được, trạm đóng gói scan EAN, import CSV/Excel, MQTT + OPC UA gateway |
| [crbnos/carbon](https://github.com/crbnos/carbon) | TypeScript, Postgres | ERP + MES + QMS: phiếu công đoạn điện tử, nhãn ZPL, genealogy lot/serial hai chiều, QMS (FAI, NCR, CAPA, hiệu chuẩn) |
| [iplus-framework/iPlusMES](https://github.com/iplus-framework/iPlusMES) | **C#, WPF, SQL Server**, GPL | MES viết bằng .NET, cùng stack với dự án này; gồm master data, sản xuất, vật tư, chất lượng, bảo trì, giao diện SCADA |
| ktg-mes ([Gitee](https://gitee.com/kutangguo/ktg-mes)) | Java, RuoYi, MIT | MES rời rạc phổ biến ở Trung Quốc: master data, lịch ca/ngày nghỉ, kho + barcode, quản lý thiết bị, màn hình cảm ứng, PDA, màn hình lớn tại xưởng |
| Spruik/Libre | Grafana, InfluxDB, Postgres | Chỉ làm performance: lệnh sản xuất, lý do dừng máy, OEE, phân tích tổn thất |
| [United Manufacturing Hub](https://umh.docs.umh.app/docs/features/datainfrastructure/unified-namespace/) | MQTT, Kafka, Node-RED | Kiến trúc IIoT hiện đại: Unified Namespace theo ISA-95; kết nối S7, OPC UA, Modbus qua gateway |
| [factorysemantics-mes](https://github.com/factorysemantics/factorysemantics-mes) | Python | Dữ liệu vào từ OPC UA để tính OEE, có connector ERPNext |
| [berkanserbes/FactoryX](https://github.com/berkanserbes/FactoryX) | .NET 9, EF Core | Dự án MES cá nhân bằng .NET (phân lớp, CRUD máy/WO/downtime). Dùng làm mốc: spec của bạn sâu hơn hẳn |
| [OPCFoundation/UA-.NETStandard](https://github.com/OPCFoundation/UA-.NETStandard) · [NModbus](https://github.com/NModbus/NModbus) | C# | Thư viện OPC UA chính thức và thư viện Modbus cho .NET, dùng cho phần IoT |

> Nhận xét chung: các MES mã nguồn mở thường chỉ mạnh ở một mảng (qcadoo/ktg mạnh về quản lý, Libre/UMH mạnh về dữ liệu máy, Carbon mạnh về chất lượng/truy xuất). Spec hiện tại đã nối được cả ba mảng này qua một kịch bản demo. Đây là điểm mạnh nhất của spec, nên giữ.

## A2. Ma trận tính năng

✔ có / ◐ có một phần / ✘ không có. Cột cuối là kết luận cho spec của bạn.

| # | Nhóm tính năng | qcadoo | OpenMES | Carbon | ktg | Libre/UMH | **Spec hiện tại** | Kết luận |
| --- | --- | :-: | :-: | :-: | :-: | :-: | :-: | --- |
| 1 | Item, BOM đa cấp, Routing, Work Center | ✔ | ✔ | ✔ | ✔ | ◐ | ✔ (có version) | **[GIỮ]** — làm sâu hơn đa số dự án |
| 2 | Lịch ca, nghỉ giữa ca, **ngày nghỉ lễ** | ◐ | ✔ | ✘ | ✔ | ◐ | ◐ (chưa có ngày nghỉ) | **[THÊM nhỏ]** bảng `PlantCalendar` |
| 3 | **Import master data từ Excel/CSV** | ✔ | ✔ | ✔ | ✔ | ✘ | ✘ | **[THÊM]** — nhà máy VN dùng Excel rất nhiều |
| 4 | Vòng đời Work Order + snapshot | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | **[GIỮ]** |
| 5 | Lập lịch Gantt kéo thả / APS | ✔ (bản thương mại) | ✔ | ◐ | ✔ | ✔ | ◐ (Gantt tùy chọn) | **[BỎ]** Gantt; thay bằng danh sách WO theo line + thứ tự ưu tiên |
| 6 | Kiosk operator: scan, route check, OK/NG | ◐ | ✔ | ✔ | ✔ | ◐ | ✔ | **[GIỮ]** — trái tim của dự án |
| 7 | **Hướng dẫn công việc (SOP/Work Instruction) tại trạm** | ✘ | ✔ | ✔ (3D) | ◐ | ✘ | ✘ | **[THÊM nhỏ]** mở file PDF/ảnh SOP theo `RoutingStep` |
| 8 | Repair / Rework / Scrap | ◐ | ◐ | ✔ | ◐ | ✘ | ✔ | **[GIỮ]** |
| 9 | **Đóng gói: serial → thùng → pallet (aggregation)** | ✘ | ✔ (trạm EAN) | ◐ | ◐ | ✘ | ✘ (chỉ có bước Pack) | **[THÊM]** — nghiệp vụ rất hay gặp ở nhà máy điện tử |
| 10 | Nhãn barcode/ZPL | ◐ | ✔ | ✔ | ✔ | ✘ | ✔ | **[GIỮ]** |
| 11 | Andon: Open → Ack → Resolved → Closed | ✘ | ✔ | ✘ | ◐ | ✘ | ✔ | **[GIỮ]** |
| 12 | **Andon leo thang theo thời gian** | ✘ | ✔ | ✘ | ✘ | ✘ | ✘ | **[THÊM nhỏ]** quá X phút chưa Ack thì báo lên cấp 2 |
| 13 | Thu thập trạng thái máy, bộ đếm qua **MQTT** | ✘ | ✔ | ✘ | ◐ | ✔ | ✔ | **[GIỮ]** |
| 14 | **OPC UA / Modbus (giao thức PLC)** qua gateway | ✘ | ✔ (opcua-gateway) | ✘ | ✘ | ✔ | ✘ | **[THÊM — ưu tiên cao]** đây là lỗ hổng lớn nhất so với mục tiêu "hiểu cách kết nối IoT" |
| 15 | Unified Namespace theo ISA-95 | ✘ | ◐ | ✘ | ✘ | ✔ | ◐ (topic gần giống) | **[SỬA]** chuẩn hóa topic theo ISA-95 (thêm cấp area) |
| 16 | **Dữ liệu thông số quy trình** (nhiệt độ, áp suất) | ✘ | ✘ | ✘ | ✘ | ✔ | ✘ (chỉ có state/count/alarm) | **[THÊM]** tag nhiệt độ lò reflow, đưa vào SPC tự động |
| 17 | Store-and-forward khi mất mạng | ✘ | ✔ (offline queue) | ✘ | ✘ | ✔ | ◐ (chỉ dựa vào bộ đếm tích lũy) | **[THÊM]** bộ đệm tại gateway; kiosk vẫn không offline |
| 18 | OEE, Six Big Losses, Pareto lý do dừng | ✘ | ◐ | ✘ | ◐ | ✔ | ✔ | **[GIỮ]** — định nghĩa trong spec đã chặt |
| 19 | Truy xuất lot/serial ngược + xuôi | ◐ | ◐ | ✔ | ✔ | ✘ | ✔ | **[GIỮ]** |
| 20 | Kiểm tra nạp vật tư (feeder verification), FIFO | ✘ | ✘ | ✘ | ◐ | ✘ | ✔ | **[GIỮ]** — điểm cộng riêng cho ngành SMT |
| 21 | **IQC — kiểm tra vật tư đầu vào** | ✘ | ✘ | ✔ | ✔ | ✘ | ✘ | **[THÊM nhỏ]** lot nhập kho ở trạng thái `PendingIqc`, chưa qua IQC thì không nạp được |
| 22 | **FAI — kiểm tra sản phẩm đầu tiên** | ✘ | ✘ | ✔ | ◐ | ✘ | ✘ | **[THÊM nhỏ]** WO/ca mới: serial đầu phải được QC duyệt mới chạy hàng loạt |
| 23 | SPC, Cpk, control chart | ✘ | ✘ | ◐ | ◐ | ✘ | ✔ | **[SỬA]** giữ, nhưng chỉ làm 3 rule Western Electric |
| 24 | NCR, Hold/Release | ✘ | ◐ | ✔ | ◐ | ✘ | ✔ | **[GIỮ]** Hold/Release; **[SỬA]** NCR rút gọn còn 4 trạng thái |
| 25 | CAPA, hiệu chuẩn thiết bị đo, quản lý rủi ro | ✘ | ✘ | ✔ | ✘ | ✘ | ✘ | **[BỎ]** — thuộc QMS, không phải MES |
| 26 | Kho theo vị trí, PDA, kiểm kê | ✔ | ◐ | ✔ | ✔ | ✘ | ◐ (M9 tùy chọn) | **[BỎ]** M9 — đó là việc của WMS. M6 đã có nhập lot tối thiểu |
| 27 | Bảo trì PM/BM, MTBF/MTTR | ✔ (thương mại) | ✘ | ◐ | ✔ | ✘ | ◐ (M10 tùy chọn) | **[SỬA]** M10 bản rút gọn: chỉ BM từ Andon + MTBF/MTTR |
| 28 | Quản lý dụng cụ (stencil, đồ gá) theo số lần dùng | ✘ | ✘ | ✘ | ✔ | ✘ | ✘ | Tùy chọn P2 (gộp vào M10 nếu còn thời gian) |
| 29 | Tích hợp ERP | ✔ | ✔ (REST, CSV) | — | ✘ | ✔ | ◐ (M8 tùy chọn) | **[GIỮ quyết định cũ]** M8 tùy chọn, làm sau |
| 30 | RBAC, audit trail | ✔ | ✔ (không sửa được) | ✔ | ✔ | ✘ | ✔ | **[GIỮ]**; **[THÊM nhỏ]** chặn UPDATE/DELETE trên `AuditLog` |
| 31 | Dashboard realtime / TV tại line | ◐ | ✔ | ✔ | ✔ | ✔ | ✔ (SignalR) | **[GIỮ]**; thêm 1 view "Line TV" toàn màn hình |
| 32 | Xuất báo cáo Excel/CSV | ✔ | ✔ | ✔ | ✔ | ✔ | ✘ | **[THÊM nhỏ]** xuất Excel cho FPY, OEE, danh sách truy xuất |
| 33 | Multi-tenant, plugin/hook, MCP/AI, 3D | — | ✔ | ✔ | — | — | ✘ | **[BỎ]** — không giúp gì cho phỏng vấn IT MES ở KCN |

## A3. Kết luận

**Đã đủ, nên giữ nguyên (khoảng 80% spec):** master data có version, snapshot WO, route check/interlock, NG/Repair, MQTT + bộ đếm tích lũy + idempotency, OEE có định nghĩa rõ nguồn dữ liệu, truy xuất bằng recursive CTE, Hold xuyên module, audit, đo hiệu năng. Phần lớn MES mã nguồn mở không có một luồng xuyên suốt như kịch bản demo của bạn.

**Còn thiếu, cần thêm (xếp theo giá trị cho CV):**

1. **Edge Gateway OPC UA + Modbus TCP → MQTT** (#14, #15, #16, #17). Bạn muốn "hiểu các phương thức kết nối IoT", nhưng spec hiện chỉ có MQTT, mà ngoài đời MES gần như không đọc PLC trực tiếp qua MQTT. Thêm module **M4b** để kể được đủ chuỗi PLC → gateway → broker → MES.
2. **Đóng gói thùng/pallet** (#9): khép lại luồng sản xuất (serial → thùng → pallet → lô xuất hàng) và mở rộng truy xuất tới "pallet nào đã giao cho khách".
3. **IQC + FAI** (#21, #22): hai cổng chất lượng mà phỏng vấn QA/MES ở nhà máy điện tử gần như luôn hỏi. Mỗi cái chỉ cần thêm 1 trạng thái và 1 quy tắc chặn.
4. **Import Excel, xuất Excel, lịch ngày nghỉ, SOP tại trạm, Andon leo thang**: mỗi việc 1–3 giờ, nhưng là những thứ người dùng thật đòi hỏi hằng ngày.

**Thừa hoặc nên bỏ:** Gantt kéo thả, M9 Warehouse mở rộng, CAPA/hiệu chuẩn, bộ Western Electric đầy đủ, NCR nhiều trạng thái. Thời gian tiết kiệm được (~25 giờ) dùng cho M4b.

---

# PHẦN B — Khung spec hoàn chỉnh

## B0. Mục tiêu và tiêu chí thành công

**Mục tiêu**: một MES thu nhỏ cho nhà máy lắp ráp điện tử, chạy end-to-end bằng `docker compose up` cùng các simulator. Dùng làm portfolio để ứng tuyển vị trí **IT MES / MES Developer** ở KCN, bù cho việc chưa có kinh nghiệm trong nhà máy.

**Dự án coi là thành công khi**:

1. Video demo 5–7 phút chạy hết kịch bản B2 mà không phải sửa dữ liệu bằng tay.
2. Mỗi module có ít nhất 2 câu phỏng vấn mà bạn trả lời được bằng chính code và số liệu của mình (xem B9).
3. README có sơ đồ kiến trúc ISA-95, ERD, số đo hiệu năng và `docs/incidents.md`.
4. CI xanh: unit test + integration test (Testcontainers: SQL Server, Mosquitto).

## B1. Phạm vi

| Hạng mục | Quyết định |
| --- | --- |
| Ngành | **[GIỮ]** Lắp ráp điện tử, sản xuất rời rạc: SMT → Reflow → AOI/Test → Assembly → Pack |
| Quy mô demo | 1 site, 1 area, 2 line, mỗi line 4–5 trạm/máy, 2 ca/ngày |
| Truy xuất | Thành phẩm theo **serial**, NVL theo **lot**, đóng gói theo **carton/pallet** **[THÊM]** |
| Stack | **[chốt 10/2026]** **.NET 10 (LTS)** thay .NET 8 (.NET 8 và .NET 9 cùng hết hỗ trợ 10/11/2026); ASP.NET Core (Controller), EF Core, SQL Server, WPF (MVVM), MQTTnet, SignalR, OxyPlot |
| Nguồn dữ liệu máy | **[SỬA]** Vẫn chỉ dùng simulator, nhưng có **3 giao thức**: MQTT (máy "thông minh"), **OPC UA** (reflow oven), **Modbus TCP** (máy cũ/bộ đếm). Simulator OPC UA/Modbus được gateway đọc rồi đẩy vào MQTT |
| ERP | **[GIỮ]** Ngoài MVP (M8 tùy chọn) |
| Ngoài phạm vi | APS/Gantt, WMS theo vị trí, CAPA, hiệu chuẩn, kiosk chạy offline, multi-tenant, mobile/PDA |

## B2. Kịch bản demo end-to-end (bản v2)

Các bước có **[THÊM]** là bước mới so với `spec.md`.

1. Admin **import Excel** danh sách Item/BOM/Routing của model `PCB-A`. **[THÊM]**
2. Planner tạo WO 200 pcs cho Line 1 → Release → sinh 200 serial, in nhãn ZPL.
3. Kho nhập 3 reel → lot ở trạng thái `PendingIqc` → QC làm **IQC** đạt → `Available`. **[THÊM]**
4. Operator nạp reel lên máy SMT, MES kiểm tra đúng vật tư và FIFO.
5. **FAI**: serial đầu tiên của WO qua hết các bước → QC duyệt FAI → line mới được chạy hàng loạt. **[THÊM]**
6. Simulator chạy:
   - Máy SMT gửi state/count qua **MQTT**.
   - Lò reflow là **OPC UA server**: gateway đọc nhiệt độ vùng peak và bộ đếm.
   - Máy dán keo cũ dùng **Modbus TCP**: gateway đọc thanh ghi bộ đếm và trạng thái. **[THÊM]**
   - Trạm Test gửi OK/NG theo serial qua API.
7. Operator scan tay trên kiosk, có xem SOP của bước; 1 con NG → Repair → Test lại → OK.
8. Ngắt mạng giữa gateway và broker 60 giây → gateway đệm dữ liệu → khi có mạng lại gửi bù, **không mất bộ đếm**. **[THÊM]**
9. Máy Down → Andon → quá 3 phút chưa Ack → **leo thang** lên cấp trưởng ca → chọn lý do dừng. **[THÊM leo thang]**
10. Nhiệt độ reflow trôi dần → **SPC** báo vi phạm rule → QC tạo NCR, Hold các serial đã qua lò trong khoảng thời gian đó.
11. Trạm Pack: scan serial vào thùng (10 pcs/thùng) → in nhãn thùng → gom thùng thành pallet. **[THÊM]**
12. QC phát hiện reel lỗi → truy xuất xuôi: serial → **thùng → pallet** bị ảnh hưởng → Hold.
13. WO Completed → báo cáo sản lượng/FPY/OEE theo WO, **xuất Excel**. **[THÊM xuất Excel]**

## B3. Kiến trúc (theo các tầng ISA-95)

```
 L4  ERP (ngoài phạm vi; M8 MockErp nếu làm)
 ─────────────────────────────────────────────────────────────────────────────
 L3  MES API (.NET 10, modular monolith) ── SQL Server
       MasterData | Planning | Execution | Packing | Machine | OEE
       Material/Trace | Quality | Maintenance
       ▲ REST + SignalR                       ▲ MQTT subscribe (MqttIngestService)
       │                                      │
   [WPF Kiosk] [WPF Back-office] [Line TV]    │
 ─────────────────────────────────────────────┼───────────────────────────────
 L2  Mosquitto broker  ◀──── MQTT (UNS) ──────┤
                                              │
       [MiniMes.EdgeGateway]  (.NET Worker)  ─┘  ← [THÊM] M4b
        • OPC UA client  ── subscribe ──▶ [OPC UA server: lò reflow giả lập]
        • Modbus TCP master ── poll ──▶ [Modbus slave: máy dán keo giả lập]
        • Store-and-forward (SQLite/file) khi mất kết nối broker
 ─────────────────────────────────────────────────────────────────────────────
 L1/L0  MachineSimulator: máy SMT (MQTT trực tiếp), OPC UA server, Modbus slave,
        trạm Test tự động (HTTP scan API)
```

**Unified Namespace [SỬA]**: topic theo cấp ISA-95.

```
minimes/{site}/{area}/{line}/{machine}/state | count | alarm | heartbeat
minimes/{site}/{area}/{line}/{machine}/process/{tag}     # [THÊM] ví dụ process/peakTemp
```

Payload giữ như `spec.md`, thêm 2 trường `source` (`mqtt` | `opcua` | `modbus`) và `quality` (`Good` | `Bad` | `Uncertain`, lấy theo status code của OPC UA).

**Solution [SỬA]**: thêm 1 project vào cấu trúc ở `spec.md` mục 2.

```
src/MiniMes.EdgeGateway/    Worker Service: OPC UA client, Modbus master, MQTT publisher, bộ đệm
```

## B4. Danh sách module (bản cuối)

Mức ưu tiên: **P0** bắt buộc cho demo · **P1** nên có · **P2** làm nếu còn thời gian.

| Module | Ưu tiên | Thay đổi so với spec.md | Giờ (người mới) |
| --- | :-: | --- | --: |
| M0 Foundation | P0 | **[THÊM]** cấp `Area` (ISA-95), chặn UPDATE/DELETE trên `AuditLog` bằng trigger | 25 |
| M1 Master Data | P0 | **[THÊM]** import Excel, `PlantCalendar`, `RoutingStep.SopUrl`, `Item.SkipIqc`, máy reflow/dán keo | 30 |
| M2 Work Order | P0 | **[BỎ]** Gantt; **[THÊM]** `Priority`, dispatch list theo line, `RequireFai`/`FaiStatus` | 27 |
| M3 Shop Floor Execution | P0 | **[THÊM]** xem SOP tại kiosk, cổng FAI trong route check | 30 |
| M3b Packing **[MỚI]** | P1 | Carton/pallet aggregation, nhãn thùng | 10 |
| M4 Machine Connectivity (MQTT, Andon) | P0 | **[THÊM]** Andon leo thang, topic UNS, tag process | 26 |
| M4b Edge Gateway (OPC UA + Modbus) **[MỚI]** | P0 | Module mới | 24 |
| M5 OEE & Downtime | P0 | **[THÊM]** ngày nghỉ, xuất Excel, view Line TV | 25 |
| M6 Material & Traceability | P0 | **[THÊM]** IQC gate, truy xuất tới carton/pallet, xuất Excel | 28 |
| M7 Quality | P1 | **[SỬA]** I-MR + 3 rule, NCR 4 trạng thái; **[THÊM]** FAI, SPC tự động từ tag process, Hold theo pallet/khoảng thời gian | 33 |
| M10 Maintenance lite | P2 | **[SỬA]** chỉ BM từ Andon + MTBF/MTTR | 10 |
| M8 ERP Integration | P2 | **[GIỮ]** tùy chọn | ~20 |
| ~~M9 Warehouse~~ | — | **[BỎ]** | — |
| Polish | P0 | README, ARCHITECTURE.md, video | 10 |

**Tổng P0 + P1 ≈ 268 giờ (≈ 27 tuần ở nhịp 10 giờ/tuần).** Số này lấy từ tổng giờ từng bước trong các file module (sau khi viết chi tiết, cao hơn ước lượng ban đầu 250 giờ). Nếu thiếu thời gian: làm hết P0 (≈ 225 giờ), bỏ M3b và rút M7 xuống còn Hold/Release + FAI (≈ 10 giờ).

## B5. Chi tiết phần thay đổi theo module

Phần không nhắc tới ở đây giữ nguyên như `spec.md` và file `module-0X-*.md`.

### M0 — Foundation

- **[THÊM]** `AuditLog` chỉ được INSERT: trigger `INSTEAD OF UPDATE, DELETE` trả lỗi, hoặc `DENY UPDATE, DELETE` cho user ứng dụng. Phỏng vấn: "Làm sao chứng minh audit log không bị sửa?"

### M1 — Master Data

| Thêm | Chi tiết |
| --- | --- |
| Import Excel | `ClosedXML` đọc file mẫu (sheet Items, BomLines, RoutingSteps). Quy trình 2 bước: **validate toàn bộ → hiển thị lỗi theo dòng → commit trong 1 transaction**. Không import nửa vời |
| `PlantCalendar` | Date, IsWorkingDay, Note. OEE (M5) bỏ qua ngày nghỉ khi tính Planned Production Time |
| `RoutingStep.SopUrl` | Đường dẫn PDF/ảnh hướng dẫn công việc, có version theo routing |

DoD bổ sung: file Excel có 1 dòng sai mã UoM → không import dòng nào, báo đúng số dòng lỗi.

### M2 — Work Order

- **[BỎ]** Gantt.
- **[THÊM]** `WorkOrder.Priority`; kiosk chỉ hiện các WO Released/InProgress của line, sắp theo Priority rồi PlannedStart.
- **[THÊM]** `WorkOrder.RequireFai`, `FaiStatus` (`NotRequired` | `Pending` | `Passed`), `FaiSerialNo`, dùng ở M3/M7. Kết quả không đạt lưu ở `FaiRecord` (M7); WO giữ `Pending` và chọn mẫu mới, nên không cần giá trị `Failed`.

### M3 — Shop Floor Execution

- **[THÊM] Quy tắc route check số 6**: nếu `FaiStatus = Pending` thì chỉ cho 1 serial (serial FAI) đi qua các bước; serial thứ 2 bị chặn với lý do `FAI_NOT_APPROVED`.
- **[THÊM]** Kiosk có nút "SOP" mở tài liệu của bước hiện tại (`Process.Start` hoặc `WebView2`).
- Bộ test RouteCheck tăng từ 10 lên 12 case (thêm case FAI pending, FAI passed).

### M3b — Packing **[MỚI]**

**Nghiệp vụ**: gom thành phẩm thành đơn vị giao hàng. Khách hàng (Samsung, Apple...) yêu cầu biết thùng/pallet nào chứa serial nào. Khi recall, nhà máy thường chặn theo pallet chứ không chặn từng con.

| Entity | Cột quan trọng |
| --- | --- |
| `PackingSpec` | ItemId, QtyPerCarton, CartonsPerPallet |
| `Carton` | CartonNo, WoId, ItemId, Qty, Status (Open/Closed/Shipped), PalletNo. "Đang Hold" là trạng thái **suy ra** (có serial Hold bên trong), không lưu cột riêng |
| `CartonSerial` | CartonNo, SerialNo, PackedAt, PackedBy (unique SerialNo) |
| `Pallet` | PalletNo, Status, ShippedAt, ShipmentRef |

**Quy tắc**

- Chỉ đóng gói serial đã OK ở bước cuối trước Pack, đúng model với thùng, và không Hold.
- Đủ `QtyPerCarton` thì tự đóng thùng và in nhãn ZPL (tái dùng code của M2). Thùng chưa đủ (cuối WO) thì supervisor đóng tay, có ghi audit.
- Mỗi serial chỉ nằm trong 1 thùng. Muốn **unpack** phải có quyền supervisor và ghi lý do.
- Hold 1 serial thì carton/pallet chứa nó hiện là Hold (suy ra, xem M3b mục 2.4); pallet chứa serial Hold không Ship được. Pallet đã `Shipped` thì không sửa được nữa.

**DoD**: 200 serial → 20 thùng → 2 pallet; scan serial thứ 11 vào thùng đầy → tự mở thùng mới; scan serial Hold → bị chặn.

**Phỏng vấn**: "Khách báo lỗi 1 pallet, bạn trả lời những gì?", "Đóng nhầm serial vào thùng thì xử lý thế nào?"

### M4 — Machine Connectivity (MQTT, Andon)

- **[SỬA]** Topic theo UNS (B3) và thêm trường `source`, `quality`.
- **[THÊM]** Tag process: `MachineTag` (MachineId, TagName, Unit, LinkedCharacteristicId) và `ProcessValue` (insert-only, bigint, partition theo ngày nếu muốn thử). Tag nào gắn với một đặc tính SPC thì chuyển thành `Measurement` cho M7.
- **[THÊM] Andon leo thang**: `AndonEscalationRule` (AndonType, Level, AfterMinutes, NotifyRole). Một job (`PeriodicTimer`) kiểm tra Andon chưa Ack, nâng `Level` lên và push SignalR tới role tương ứng. Unit test bằng `FakeTimeProvider`.

### M4b — Edge Gateway: OPC UA + Modbus TCP → MQTT **[MỚI]**

**Nghiệp vụ / vì sao cần**: ngoài đời, MES (L3) không nói chuyện trực tiếp với PLC (L1). Ở giữa có lớp **gateway/edge** (Kepware, Node-RED, Ignition Edge, UMH, hoặc dịch vụ C# tự viết) với 3 việc:

- dịch giao thức công nghiệp (OPC UA, Modbus, S7, MC Protocol của Mitsubishi) sang một dạng chung;
- chuẩn hóa tên tag theo namespace;
- đệm dữ liệu khi mạng tới MES/broker bị đứt.

Đây là phần nhà tuyển dụng IT MES ở KCN hỏi nhiều nhất ("anh kết nối PLC thế nào?").

**Kiến thức cần nắm (để nói được trong phỏng vấn, kể cả phần không code)**

| Giao thức | Mô hình | Trong dự án | Chỉ cần biết để trả lời |
| --- | --- | --- | --- |
| OPC UA | Client/server, có kiểu dữ liệu, có **subscription** (server tự đẩy khi giá trị đổi), status code chất lượng, bảo mật bằng certificate | **Code** bằng `OPCFoundation.NetStandard.Opc.Ua` | Endpoint, NodeId, Browse, MonitoredItem, SamplingInterval, PublishingInterval, SecurityPolicy, trust certificate |
| Modbus TCP | Master/slave, **polling**, chỉ có thanh ghi 16-bit (coil, holding register), không có tên tag | **Code** bằng `NModbus` | Function code 03/06, địa chỉ thanh ghi, ghép 2 register thành int32/float, thứ tự byte (endianness) |
| MQTT | Pub/sub qua broker, QoS, retained, LWT | Đã có ở M4 | So sánh với OPC UA: khi nào dùng cái nào |
| Siemens S7, Mitsubishi MC, Omron FINS | Giao thức riêng của hãng PLC | Không code | Biết tên, biết có thư viện (S7netplus, HslCommunication), biết hay gặp ở nhà máy VN |
| SECS/GEM | Chuẩn kết nối thiết bị bán dẫn | Không code | Biết tồn tại, dùng ở fab/OSAT (Intel, Amkor, Hana Micron) |

**Thiết kế**

- Simulator thêm 2 vai:
  - **OPC UA server** (lò reflow): node `PeakTemp` (float, dao động ± nhiễu, có chế độ "trôi" để demo SPC), `State`, `Counter`.
  - **Modbus TCP slave** (máy dán keo): HR 40001–40002 là bộ đếm int32, HR 40003 là mã trạng thái.
  - Thay thế: dùng container `mcr.microsoft.com/iotedge/opc-plc` cho OPC UA. Nhưng tự viết server cho phép điều khiển kịch bản demo.
- `MiniMes.EdgeGateway` (Worker Service), cấu hình tag bằng file JSON:

  ```json
  { "machine": "L1-REFLOW-01", "protocol": "opcua", "endpoint": "opc.tcp://simulator:4840",
    "tags": [ { "name": "peakTemp", "nodeId": "ns=2;s=Reflow.PeakTemp", "mode": "subscribe", "deadband": 0.5 },
              { "name": "counter",  "nodeId": "ns=2;s=Reflow.Counter",  "mode": "subscribe" } ] }
  { "machine": "L1-GLUE-01", "protocol": "modbus", "endpoint": "simulator:502", "unitId": 1,
    "tags": [ { "name": "counter", "address": 0, "type": "int32", "byteOrder": "ABCD", "pollMs": 1000 },
              { "name": "state",   "address": 2, "type": "uint16", "map": { "1": "Running", "2": "Idle", "3": "Down" } } ] }
  ```

- Mỗi giá trị đọc được đổi thành message UNS (B3) rồi publish QoS 1. `msgId` sinh từ (machine, tag, sourceTimestamp) để MES có thể khử trùng.
- **Store-and-forward**: broker không kết nối được thì ghi message vào hàng đợi cục bộ (SQLite hoặc file append-only). Có kết nối lại thì gửi theo đúng thứ tự, gửi thành công mới xóa. Giới hạn dung lượng; đầy thì bỏ message cũ nhất và log cảnh báo.
- **Health**: mỗi kết nối nguồn có trạng thái Connected/Reconnecting/Faulted; mất kết nối máy thì publish `status = offline` cho máy đó; trạng thái gateway (kèm LWT) ở `minimes/{site}/_sys/gateway/{id}/status`. Mất kết nối PLC thì phải hiện là **Offline**, không được hiện **Down** (2 trạng thái này khác nghĩa trong OEE).
- Reconnect với backoff tăng dần (Polly) cho cả OPC UA session và Modbus socket.

**Các bước**

1. Học OPC UA: chạy server mẫu, dùng UaExpert browse node, subscribe thử.
2. Viết OPC UA server trong simulator (1 namespace, 3 node).
3. Gateway: OPC UA client subscribe và in giá trị ra log.
4. Modbus slave trong simulator + Modbus master trong gateway; unit test hàm ghép register theo 4 kiểu byte order.
5. Publish ra MQTT theo UNS; MES (M4) nhận được mà **không phải sửa** `MqttIngestService` (chứng minh lợi ích của hợp đồng dữ liệu).
6. Store-and-forward + integration test: dừng container Mosquitto 60 giây, bật lại, kiểm tra không thiếu message nào (Testcontainers).
7. Ghi vào ARCHITECTURE.md: vì sao có gateway thay vì cho MES gọi PLC trực tiếp.

**DoD**

- Dashboard hiện bộ đếm của cả 3 loại máy (MQTT, OPC UA, Modbus) giống nhau.
- Mất broker 60 giây: không mất message, không trùng bộ đếm.
- Tắt simulator OPC UA: máy hiện Offline trong ≤ 30 giây và tự kết nối lại.

**Phỏng vấn**: "OPC UA khác Modbus thế nào?", "Polling hay subscription, khi nào dùng cái nào?", "Mất mạng giữa máy và MES thì dữ liệu đi đâu?", "Đọc được 2 thanh ghi nhưng ra số sai, anh kiểm tra gì?" (byte order).

### M5 — OEE & Downtime

- **[THÊM]** Planned Production Time trừ thêm ngày nghỉ trong `PlantCalendar`.
- **[THÊM]** Xuất Excel (ClosedXML) cho OEE theo ca và Pareto lý do dừng.
- **[THÊM]** View "Line TV": toàn màn hình, chữ lớn, gồm OEE ca hiện tại, sản lượng/kế hoạch theo giờ, Andon đang mở. Tự xoay vòng giữa các line.
- Total Count của máy OPC UA/Modbus đi cùng đường MQTT, nên công thức không đổi.

### M6 — Material & Traceability

- **[THÊM] IQC gate**: thêm trạng thái `PendingIqc` vào `MaterialLot.Status` (trạng thái mặc định khi nhập kho), cùng `IqcRecord` (LotNo, SampleQty, Result, DefectCode, InspectedBy). Lot chưa ở trạng thái `Available` thì nạp lên máy bị chặn (lý do `LOT_NOT_IQC_PASSED`). Item có cờ `SkipIqc` cho NVL rủi ro thấp.
- **[THÊM]** Truy xuất xuôi trả thêm `CartonNo`, `PalletNo`, `Shipped?` (join `CartonSerial`, `Carton`, `Pallet`). Nút "Hold" có thể chọn Hold theo serial hoặc theo pallet.
- (P2) MSL — linh kiện nhạy ẩm: `FloorLifeHours` tính từ lúc mở túi; hết hạn thì chặn nạp. Câu hỏi rất "SMT", có thể chỉ nói trong phỏng vấn mà không code.

### M7 — Quality

- **[SỬA] SPC**: chỉ làm biểu đồ I-MR (dữ liệu từng giá trị, khớp với tag nhiệt độ) và 3 rule: (1) 1 điểm ngoài 3σ, (2) 2/3 điểm ngoài 2σ cùng phía, (3) 7 điểm liên tiếp cùng phía đường tâm. Cp/Cpk giữ nguyên.
- **[THÊM] SPC tự động**: `ProcessValue` của tag `peakTemp` → `Measurement` (lấy mẫu mỗi N giây hoặc mỗi serial qua lò) → rule engine → `SpcAlert`. Đây là chỗ nối IoT với Quality, rất đáng kể trong phỏng vấn.
- **[SỬA] NCR** còn 4 trạng thái: `Open → UnderReview → Dispositioned (UseAsIs | Rework | Scrap | ReturnToVendor) → Closed`. Đóng NCR bắt buộc có disposition và người duyệt.
- **[THÊM] FAI**: `FaiRecord` (WoId, SerialNo, Checklist JSON, Result, ApprovedBy). Kết quả Passed thì `WorkOrder.FaiStatus = Passed`, route check (M3) mở cho cả lô. Kết quả Failed thì WO giữ `Pending`, cần serial FAI mới.
- **[GIỮ]** Hold/Release xuyên module; thêm đích Hold là Carton/Pallet.
- **[BỎ]** CAPA, hiệu chuẩn, X̄-R với subgroup.

### M10 — Maintenance lite (P2)

- Andon loại `MachineBreakdown` khi Resolved thì tự sinh `MaintenanceOrder` (BM) với thời gian dừng lấy từ `MachineStateHistory`.
- Báo cáo MTBF = Run Time / số lần hỏng; MTTR = tổng thời gian sửa / số lần hỏng, theo máy và theo tháng.
- (P2+) Dụng cụ: `Tool` (stencil) có `UsageCount`/`MaxUsage`; quá giới hạn thì chặn bắt đầu WO.

## B6. Yêu cầu phi chức năng (gom lại để kiểm thử)

| Mã | Yêu cầu | Đo bằng |
| --- | --- | --- |
| NFR-1 | API scan (route check) p95 < 100 ms với 10 triệu dòng `ScanLog` | Script đo hiệu năng, ghi vào `docs/perf.md` |
| NFR-2 | Truy xuất xuôi 1 lot < 1 giây | Như trên |
| NFR-3 | Andon hiện trên dashboard < 2 giây | Log timestamp từ đầu tới cuối |
| NFR-4 | Mất broker/gateway 60 giây: không mất dữ liệu đếm | Integration test (Testcontainers) |
| NFR-5 | Mọi thời gian lưu UTC, hiển thị theo giờ nhà máy; ca qua đêm tính theo ngày sản xuất | Unit test dùng `FakeTimeProvider` |
| NFR-6 | Mọi thay đổi master data và trạng thái có audit; `AuditLog` không sửa được | Test thử UPDATE thì bị lỗi |
| NFR-7 | Message MQTT trùng (QoS 1) không làm đếm trùng | Test gửi lại cùng `msgId` |
| NFR-8 | Dựng lại toàn bộ hệ thống bằng `docker compose up` + 1 lệnh migrate + seed | Clone repo sạch trên máy khác |

## B7. Lộ trình (thay cho `spec.md` mục 3)

| Giai đoạn | Module | Giờ | Mốc demo (commit/tag) |
| --- | --- | --: | --- |
| MVP-1 | M0, M1 | 55 | `v0.1` Master data + import Excel, 2 app WPF chạy |
| MVP-2 | M2, M3 | 57 | `v0.2` Release WO, nhãn, scan, NG/Repair, cổng FAI |
| MVP-3 | M4, M4b | 50 | `v0.3` 3 giao thức về chung 1 dashboard, Andon leo thang, store-and-forward |
| MVP-4 | M5 | 25 | `v0.4` OEE, Pareto, Line TV, xuất Excel |
| MVP-5 | M6 | 28 | `v0.5` IQC, nạp vật tư, truy xuất ngược/xuôi |
| MVP-6 | M3b, M7 | 43 | `v0.6` Đóng gói, SPC tự động từ tag, NCR, FAI, Hold |
| Polish | — | 10 | `v1.0` README, video demo theo B2 |
| Tùy chọn | M10, M8 | 30 | — |

**Mốc dừng an toàn**: xong MVP-5 (≈ 215 giờ) là đã đủ để phỏng vấn tốt: có execution, IoT 3 giao thức, OEE và truy xuất. (Cổng FAI ở M3 vẫn chạy được trước khi có M7 nhờ endpoint tạm ghi kết quả FAI.)

**Gợi ý**: sau MVP-3, đưa dòng IoT lên CV và bắt đầu ứng tuyển ngay; không cần đợi hoàn thành 100%.

## B8. Tài liệu cần sinh ra (artefact cho CV)

- `README.md`: GIF demo, sơ đồ B3, cách chạy, tài khoản demo.
- `ARCHITECTURE.md`: các quyết định kèm lý do. Thêm vào danh sách ở `spec.md`: **gateway thay vì MES đọc PLC trực tiếp**, **UNS theo ISA-95**, **subscription vs polling**, **FAI/IQC là cổng chặn trong route check chứ không phải báo cáo**.
- `docs/erd.md` (Mermaid), `docs/perf.md`, `docs/incidents.md` (gợi ý sự cố: sai byte order Modbus, OPC UA không trust certificate, ca qua đêm tính sai OEE, scan trùng do double-Enter).
- `docs/glossary.md`: thuật ngữ Anh–Việt (dùng lại bảng thuật ngữ trong các file module).

## B9. Ánh xạ sang CV và phỏng vấn

**Mẫu dòng CV (tiếng Anh):**

- Built a mini **Manufacturing Execution System** for an electronics assembly line (.NET 10, SQL Server, WPF) covering work orders, serial-level shop-floor execution with route interlock, OEE, lot/serial genealogy, SPC and packing aggregation.
- Designed an **edge gateway** collecting machine data via **OPC UA** (subscription) and **Modbus TCP** (polling), normalised into an ISA-95 **MQTT Unified Namespace**, with store-and-forward for zero data loss during network outages.
- Optimised route-check API to p95 < 100 ms over 10M scan records; forward traceability over multi-level genealogy < 1 s via recursive CTE and index tuning.
- Implemented quality gates (**IQC, FAI, Hold/Release**) and automatic SPC (I-MR, Western Electric rules, Cpk) fed by live process tags.

**Ma trận chủ đề phỏng vấn → nơi chứng minh trong code**

| Chủ đề hay bị hỏi | Module |
| --- | --- |
| ISA-95, MES khác ERP/SCADA chỗ nào | M0, B3 |
| BOM/Routing version, master data sai | M1 |
| Tránh trùng serial khi release song song | M2 |
| Chặn bỏ công đoạn (interlock), poka-yoke | M3 |
| Kết nối PLC: OPC UA, Modbus, MQTT; mất mạng | M4, M4b |
| OEE và cách cải thiện | M5 |
| Khách khiếu nại 1 serial: truy xuất trong 2 giờ | M6, M3b |
| SPC, Cpk, NCR, Hold | M7 |
| SQL chậm: index, execution plan, bảng lớn | M3, M6 (`docs/perf.md`) |
| Sự cố production lúc nửa đêm | `docs/incidents.md` |

## B10. Quyết định đã chốt (bổ sung vào `spec.md` mục 5)

| # | Câu hỏi | Đã chốt |
| --- | --- | --- |
| 6 | Giao thức máy | MQTT + OPC UA + Modbus TCP, tất cả qua simulator. S7/MC/SECS-GEM chỉ học lý thuyết |
| 7 | Gateway | Tự viết bằng .NET Worker (không dùng Node-RED), để thể hiện kỹ năng C# |
| 8 | Bộ đệm gateway | SQLite cục bộ |
| 9 | Đóng gói | Có, ở mức serial → carton → pallet. Không làm shipment/ASN |
| 10 | Bỏ khỏi phạm vi | Gantt, M9 Warehouse, CAPA, hiệu chuẩn, kiosk offline |
| 11 | Phiên bản .NET **[chốt 10/2026]** | .NET 10 (LTS, hỗ trợ đến 11/2028); `net10.0`, WPF `net10.0-windows`; SDK ghim bằng `global.json`; solution `MiniMes.slnx` |
| 12 | Tên dự án **[chốt 10/2026]** | `MiniMes.*`; khung `OpenMES.*` tạo trước khi chốt spec sẽ bị xóa ở M0 bước 1 |
| 13 | Kiểu API **[chốt 10/2026]** | Controller (`Api/Controllers/*Controller.cs`) cho cả dự án; OpenAPI có sẵn của ASP.NET Core + Swagger UI (`Swashbuckle.AspNetCore.SwaggerUI`) |
| 14 | CI **[chốt 10/2026]** | GitHub Actions, branch `master`, repo đẩy lên GitHub |
| 15 | Lỗi HTTP và audit **[chốt 10/2026]** | 400 validation, 404, 409 `ConflictException`, 422 `DomainException`, 500. Audit Added/Deleted lưu 1 dòng `ColumnName="*"` với JSON, ghi cùng transaction |

## B11. Tài liệu chi tiết theo module (đã cập nhật theo spec v2)

| Thứ tự làm | File | Giờ |
| --- | --- | --: |
| 1 | [module-00-foundation.md](module-00-foundation.md) | 25 |
| 2 | [module-01-master-data.md](module-01-master-data.md) | 30 |
| 3 | [module-02-work-order.md](module-02-work-order.md) | 27 |
| 4 | [module-03-shop-floor-execution.md](module-03-shop-floor-execution.md) | 30 |
| 5 | [module-04-machine-connectivity.md](module-04-machine-connectivity.md) | 26 |
| 6 | [module-04b-edge-gateway.md](module-04b-edge-gateway.md) **[MỚI]** | 24 |
| 7 | [module-05-oee-downtime.md](module-05-oee-downtime.md) | 25 |
| 8 | [module-06-material-traceability.md](module-06-material-traceability.md) | 28 |
| 9 | [module-03b-packing.md](module-03b-packing.md) **[MỚI]** | 10 |
| 10 | [module-07-quality.md](module-07-quality.md) | 33 |

Mỗi file có đầu trang "Cập nhật theo spec-v2" tóm tắt phần đổi; mọi chỗ đổi trong thân bài đánh dấu **[v2]**. M3b làm sau M6 dù đánh số 3b, vì truy xuất xuôi của M6 và Hold của M7 dùng dữ liệu đóng gói, còn bản thân M3b chỉ cần M2–M3. M8, M10 chưa có file chi tiết (P2).

## B12. Việc tiếp theo

1. Lập plan chi tiết cho MVP-1 (M0 + M1) ở mức task 1–3 giờ, rồi bắt đầu code.
2. Khi xong mỗi mốc `v0.x`, cập nhật README và `docs/incidents.md` ngay, không dồn đến cuối.
