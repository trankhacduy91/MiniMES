# Tuần 2: Master data và Work Order (M1, M2)

> **Mục tiêu tuần:** hiểu vì sao "dữ liệu gốc sai thì cả nhà máy sai", và một WO đi từ kế hoạch ra line như thế nào. Ở nhà máy, đây là nhóm ticket IT MES nhận nhiều thứ hai: model mới, đổi BOM, WO không release được.
>
> **Thời lượng:** ~10 giờ, gồm đọc 3 · lab 3 · drill 2 · nói 2.

## 0. Ngoài đời việc này diễn ra thế nào

- **NPI (New Product Introduction):**
  - Khách giao model mới. Kỹ sư sản phẩm (PE) gửi BOM và quy trình, thường là file Excel xuất từ ERP hoặc PLM.
  - IT MES (hoặc PE có quyền) nhập vào MES.
  - Chạy thử vài chục board (pilot run) trước khi chạy hàng loạt.
- **ECN/ECO (Engineering Change Notice/Order):**
  - Khách đổi một linh kiện, ví dụ tụ 10 µF hãng A thành hãng B.
  - Phải tạo **BOM version mới** có ngày hiệu lực, không sửa bản cũ. Lý do: các board đã làm phải truy xuất được theo đúng BOM lúc làm.
  - Tranh cãi hay gặp: "WO đang chạy dùng BOM cũ hay mới?"
- **Lập kế hoạch:**
  - Planner nhận đơn hàng từ ERP, chia thành các WO theo line và ca.
  - Nhà máy nhỏ ở KCN thường nhập WO tay, hoặc import Excel mỗi sáng.
- **Release:** từ lúc release, WO "đóng băng":
  - BOM và routing được chụp lại (snapshot);
  - serial được sinh ra, nhãn được in;
  - line thấy WO trên dispatch list.

## 1. Đọc (3 giờ)

- [ ] `spec/module-01-master-data.md`: phần mở đầu, versioning (Draft → Approved, hiệu lực nửa mở), import Excel.
- [ ] `spec/module-02-work-order.md`: state machine WO, snapshot khi release, sinh serial không trùng, nhãn ZPL, dispatch list.
- [ ] `docs/erd-master-data.md`: vẽ lại tay quan hệ Item, BOM, BomLine, Routing, RoutingStep, WorkCenter, Station.
- [ ] `ARCHITECTURE.md`:
  - ADR-005: explode BOM bằng recursive CTE;
  - ADR-006: BOM/Routing có version.

Câu cần tự trả lời sau khi đọc:
- Vì sao BOM có `ScrapRate`? (Kem hàn hao 5 %: WO 200 board phải xuất kho nhiều hơn 200 × định mức.)
- Vì sao Routing đánh số bước 10, 20, 30 thay vì 1, 2, 3? (Để chèn bước 15 sau này mà không đánh số lại.)

## 2. Lab (3 giờ)

### 2.1 Master data
1. Đăng nhập BackOffice bằng `admin`, vào **Import**, chọn `docs/samples/import-PCB-B.xlsx`. Bấm **Validate** (kiểm tra, không ghi), rồi **Import** (nút chỉ bật khi 0 lỗi).
2. **Làm ngay drill A** (mục 3) trước khi đi tiếp: `planner01` tạo WO `PCB-B`, bấm Preview materials hoặc Release. Import luôn tạo BOM/Routing ở trạng thái **Draft** ("import là lối tắt để nhập liệu, không phải để duyệt"), nên release bị từ chối.
3. Đăng nhập `admin`: vào **BOM** của `PCB-B` → **Approve**; vào **Routing** → **Approve**. Màn hình WPF approve với hiệu lực "từ bây giờ". Muốn đặt ngày tương lai thì gọi `POST /api/boms/{id}/approve` `{ "effectiveFromUtc": "..." }` qua Swagger.
4. **Explode** BOM `PCB-B` cho 200 pcs: số chính xác khác số cần xuất kho ở đâu?
5. Tạo version BOM mới (Draft) cho `PCB-B`, đổi một linh kiện, Approve. Xem cột EffectiveFrom/EffectiveTo: version cũ được đóng đúng lúc version mới mở.
6. Xem **Routing** của `PCB-A`: SMT 10 → ASSY 20 → TEST 30 → PACK 40, cycle time từng bước, SOP URL.

### 2.2 Work Order
1. Đăng nhập bằng `planner01`, vào **Work orders → New**: `PCB-A`, 20 pcs, Line 1.
2. Bấm **Preview materials**, rồi **Save**, rồi **Release**.
3. Mở tab **Serials**: 20 serial; bấm **Preview label** để xem nhãn ZPL.
4. Đăng nhập bằng `sup01`: Hold WO, rồi Resume. Ở kiosk, WO vẫn có trên dispatch list nhưng mang trạng thái OnHold. Scan một serial của nó lúc đang Hold thì bị chặn `WoNotActive`.

### 2.3 SQL

```sql
-- a) WO vừa tạo: trạng thái, version BOM/routing đã chụp, FAI
SELECT TOP 5 WoNo, Status, PlannedQty, BomVersion, RoutingVersion, RequireFai, FaiStatus, ReleasedAt
FROM WorkOrders ORDER BY Id DESC;

-- b) Snapshot: các bước và vật tư WO đã "đóng băng" lúc release
SELECT * FROM WorkOrderOperations WHERE WorkOrderId = <id> ORDER BY Seq;
SELECT * FROM WorkOrderMaterials  WHERE WorkOrderId = <id>;

-- c) Ai đã release/hold WO này? (audit)
SELECT * FROM AuditLog WHERE TableName = 'WorkOrders' AND RecordKey = '<id>' ORDER BY Id;
```

Câu hỏi:
- Sau khi bạn Approve BOM version mới, `WorkOrderMaterials` của WO đã release có đổi không? Vì sao không được đổi?

## 3. Drill sự cố (2 giờ)

### Drill A: "Planner báo không release được WO model mới"
**Cách tạo lỗi:** dùng `PCB-B` ngay sau khi import (BOM/Routing còn Draft). Thử thêm: approve BOM qua Swagger với `effectiveFromUtc` ở tương lai, rồi release.

**Tự làm trước:** đọc thông báo lỗi, rồi kiểm tra bằng SQL xem BOM/Routing của item đang ở trạng thái gì và hiệu lực từ ngày nào.

<details><summary>Đáp án gợi ý</summary>

- **Thông báo:** "Item 'X' has no approved BOM in effect at …" (hoặc "… no approved routing …").
- **Nguyên nhân thường gặp ngoài đời:**
  - PE quên Approve;
  - ngày hiệu lực đặt sai múi giờ: hệ thống lưu UTC nên 00:00 giờ VN là 17:00 UTC hôm trước;
  - routing chưa tạo cho item mới.
- **Cách xử lý đúng:** **không sửa thẳng DB**. Báo PE Approve, hoặc đổi ngày hiệu lực qua màn hình để có audit.
- **Lỗi khác cùng nhóm:** "A material is consumed at step 25, which is not in the routing". BOM ghi linh kiện dùng ở bước 25 nhưng routing không có bước 25. Đây là lỗi kinh điển sau ECN, khi chỉ một trong hai file được sửa.

**Câu nói khi phỏng vấn:** "Master data sai là lỗi của dữ liệu, không phải của hệ thống. Tôi chỉ ra chỗ sai, người sở hữu dữ liệu sửa qua quy trình có duyệt; IT không tự sửa DB để chạy cho kịp."
</details>

### Drill B: "Line báo không thấy WO trên kiosk"
**Cách tạo lỗi:** tạo WO cho **Line 2** rồi mở kiosk trạm Line 1; hoặc để WO ở trạng thái Draft; hoặc Hold WO.

<details><summary>Đáp án gợi ý</summary>

Dispatch list hiện WO **Released/InProgress/OnHold** của **đúng line** của trạm, xếp theo Priority và PlannedStart (`GetDispatchListHandler` trong `Application/Planning/WorkOrderTransitions.cs`). Kiểm tra lần lượt:
1. `SELECT WoNo, Status, LineId FROM WorkOrders WHERE WoNo = '...'`: trạng thái là gì, line có đúng không. Draft, Completed hoặc Cancelled thì không hiện.
2. Trạm kiosk đang cấu hình `StationCode` nào (trong `appsettings.json` của kiosk), và trạm đó thuộc line nào. Ngoài đời, PC trạm bị đổi chỗ mà quên sửa cấu hình là chuyện hay gặp.
3. WO hiện nhưng scan bị chặn `WoNotActive`: WO đang OnHold. Hỏi supervisor lý do (`HoldReason`), IT không tự Resume.
</details>

### Drill C: import Excel lỗi
Sửa một bản sao của file mẫu: xóa một ItemCode mà BOM có dùng, hoặc cho A là con của B và B là con của A. Import và đọc báo lỗi từng dòng.

Điều cần ghi nhớ: import kiểm **tất cả** rồi mới ghi. Lỗi một dòng thì không ghi dòng nào, nên không bao giờ có nửa file vào DB. `BomCycleDetector` chặn vòng lặp BOM.

## 4. Câu hỏi phỏng vấn tuần 2

<details><summary>1. Khách đổi một linh kiện giữa chừng (ECN). Các WO đang chạy dùng BOM nào?</summary>

- WO **đã release** dùng snapshot chụp lúc release, tức BOM cũ.
- WO release **sau ngày hiệu lực** dùng BOM mới.
- Muốn WO đang chạy dùng BOM mới thì phải có quyết định của PE/QC: đóng WO cũ, mở WO mới (hoặc lần sau có M8 thì ERP gửi lại).

Truy xuất luôn biết board nào làm theo version nào (`WorkOrders.BomVersion`).
</details>

<details><summary>2. Vì sao không sửa thẳng BOM cũ cho nhanh?</summary>

Board đã giao cho khách phải truy xuất được theo đúng BOM lúc làm. Sửa đè thì mất lịch sử; khi khách khiếu nại không trả lời được "board này lẽ ra có linh kiện gì". Vì vậy dùng version cộng hiệu lực nửa mở `[From, To)`, và chỉ bản Draft mới sửa được.
</details>

<details><summary>3. Hai planner release hai WO cùng lúc, làm sao serial không trùng?</summary>

- Số chạy lấy từ **SQL sequence**, nguyên tử, không cần lock trong ứng dụng.
- Thêm **unique index** trên `SerialNo` làm chốt cuối.

Dự án có integration test release song song. Đọc thêm incident "Test đồng thời xanh, nhưng không vì lý do mình tưởng" (M2): test đồng thời phải chứng minh được là có chạm race.
</details>

<details><summary>4. Explode BOM là gì? Khi nào cần?</summary>

Nhân định mức qua các cấp BOM (thành phẩm → bán thành phẩm → linh kiện) để biết cần bao nhiêu nguyên vật liệu cho N sản phẩm, có cộng hao hụt.
- **Dùng cho:** xuất kho theo WO, kiểm tra thiếu hàng trước khi release.
- **Cách làm:** recursive CTE trong SQL Server, chống vòng lặp bằng `MAXRECURSION` và bộ phát hiện vòng lặp ở Domain.
</details>

<details><summary>5. Các trạng thái của WO? Ai được chuyển trạng thái nào?</summary>

- **Luồng chính:** Draft → Released → InProgress (tự động khi scan đầu tiên) → Completed → Closed.
- **Nhánh phụ:** OnHold ↔ Released/InProgress; Cancelled chỉ khi chưa có sản lượng.
- **Phân quyền:** Planner tạo và release; Supervisor Hold/Resume/Complete/Close.

Đây là state machine trong Domain, chuyển sai thì trả 422.
</details>

<details><summary>6. Nhãn serial in thế nào ở nhà máy?</summary>

- Máy in nhãn công nghiệp (Zebra, Honeywell) nhận **ZPL**, một ngôn ngữ lệnh dạng text, gửi qua cổng 9100 hoặc driver.
- MES sinh ZPL theo template, có barcode (Code128 hoặc DataMatrix) chứa serial.
- In lại phải có lý do và log. Dự án ghi `LabelPrintLog` cho mỗi nhãn.
- Ticket hay gặp: nhãn lệch, mờ, barcode không đọc được. Thường do cài đặt máy in (darkness, kích thước nhãn), không phải lỗi MES.
</details>

<details><summary>7. Cycle time lý tưởng lấy ở đâu, dùng vào việc gì?</summary>

- Ở routing step (`IdealCycleTimeSec`), do IE/PE đo hoặc lấy từ thông số máy.
- Dùng cho Performance của OEE (tuần 5) và để lập kế hoạch.
- Nhập sai thì OEE có thể vượt 100 % (ví dụ (a) trong `docs/oee-hand-calcs.md`).
</details>

<details><summary>8. Import Excel master data: rủi ro và cách bạn làm?</summary>

- **Rủi ro:** file sai cột, mã không tồn tại, vòng lặp BOM, import nửa chừng thì lỗi.
- **Cách làm trong dự án:** đọc hết, validate hết, báo lỗi theo dòng; chỉ khi sạch mới ghi, trong **một transaction**. Có preview trước khi ghi.
- **Ngoài đời:** luôn import vào môi trường test trước nếu là model mới.
</details>

## 5. Tự kiểm cuối tuần

- [ ] Giải thích được snapshot khi release bằng một ví dụ ECN cụ thể.
- [ ] Làm 3 drill, viết runbook 5 dòng cho drill A.
- [ ] Viết được câu SQL tìm "các WO đã release dùng BOM version cũ của item X".
- [ ] Trả lời ≥ 7/8 câu.
