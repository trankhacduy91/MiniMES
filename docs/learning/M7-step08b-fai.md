# M7 · Bước 8b — FAI: phiếu kiểm và mở cổng cho M3

> Tài liệu học cho bước 8b của [module-07](../../spec/module-07-quality.md) (spec mục **1.2 FAI**, **2.5b**, Q17). Code:
> - `src/MiniMes.Domain/Quality/Fai/FaiChecks.cs` (3 kiểm tự động + luật Q17, hàm thuần), `Serial.RejectFirstArticle`
> - `src/MiniMes.Application/Quality/Fai/` (`FaiWorksheet.cs`: đọc dữ liệu; `FaiUseCases.cs`: hàng chờ, phiếu, ghi kết quả)
> - `src/MiniMes.Api/Controllers/FaiController.cs`, `src/MiniMes.Client.Core/ViewModels/Quality/FaiViewModel.cs`

## A. FAI là gì, trong một câu

Sau khi setup line cho một WO, chỉ cho **một** board (mẫu) chạy hết line. QC kiểm board đó rồi mới cho chạy cả lô. Sai setup (lắp nhầm reel, sai chương trình test) bị bắt ở board số 1, không phải board số 200.

Bước này nối ba phần đã có:

| Phần | Làm ở | Vai trò |
| --- | --- | --- |
| `WorkOrder.FaiStatus` / `FaiSerialNo` | M2 | Trạng thái: `Pending` → `Passed`; Fail thì xóa mẫu |
| Route check quy tắc 6 | M3 | Khi `Pending`, chỉ mẫu được scan |
| **Phiếu FAI + kết quả QC** | M7 bước 8b | Thay endpoint tạm `fai/result` của M3 |

## B. Ba kiểm tự động: MES đã biết, QC khỏi chép lại

| Mục | Lấy từ | Đỏ khi |
| --- | --- | --- |
| `BomMatch` | `SerialComponent` của mẫu (M6) so với `WorkOrderMaterial` lot-controlled (M2) | Thiếu item hoặc thừa item lạ (reel nhầm) |
| `TestPassed` | `ScanLog` OK của mẫu ở mọi bước `IsInspection` (M3) | Chưa có scan OK ở bước kiểm tra |
| `MeasurementsInSpec` | `Measurement` của mẫu (M7 bước 5) | Có giá trị NG, hoặc đặc tính đã lên kế hoạch mà chưa đo |

Điểm phỏng vấn: **phiếu giấy bắt QC chép lại dữ liệu máy đã có** (và chép sai). MES điền sẵn phần tự động, QC chỉ làm phần cần mắt người (ngoại quan, phân cực, nhãn).

Ba chỗ làm chặt hơn câu chữ spec (đã ghi trong spec):
- **BomMatch chỉ so các bước mẫu đã đi qua.** Túi đóng gói tiêu thụ ở PACK; QC chấm sau TEST, trước PACK. So cả BOM thì mục này luôn đỏ.
- **"Trong spec" cần có số đo.** Đặc tính đã lên kế hoạch ở bước mẫu đã qua mà chưa có giá trị thì đỏ ("not measured"), không coi là xanh vì không có gì sai.
- **Số đo đã hiệu chỉnh (Q16) tính theo dòng hiệu chỉnh**, không tính dòng gốc.

Các hàm này là **hàm thuần trong Domain** (unit test không cần DB). Phần đọc DB (`FaiWorksheet.ReadAsync`) chỉ gom dữ liệu rồi gọi chúng.

## C. Q17: khi nào được chấm, khi nào được Pass

1. **Mẫu phải qua bước kiểm tra cuối (TEST)**, áp dụng cho cả Pass lẫn Fail. Chưa có mẫu, mẫu đang NG/Repair, hoặc mẫu chưa tới TEST → 422 kèm lý do. Màn hình hiện đúng lý do server trả về.
2. **Pass cần mọi mục tự động xanh và mọi mục tay đã tick** (`FaiSheet.RequireCanRecord`). Người không "ghi đè" được dữ liệu máy: kiểm ở Domain, ở UI (nút tắt), và ở DB (CHECK `Result <> 'Pass' OR AutoChecksPassed = 1` từ bước 2).
3. **Fail luôn được phép**: QC có thể thấy lỗi mà phiếu không liệt kê.

**Server không tin "xanh" từ lúc mở phiếu.** Khi QC bấm Pass, server tính lại cả 3 mục. Nếu giữa chừng có ai sửa số đo, Pass sẽ bị chặn.

## D. Fail làm gì với board mẫu (quyết định của bạn)

Spec viết "chuyển mẫu sang NG bằng cổng scan M3", nhưng cổng scan không ghi NG được cho một bước mà board đã OK. Bạn chọn **hàm domain riêng** `Serial.RejectFirstArticle()`:
- `InProcess` → `Ng`; `CurrentSeq` giữ nguyên. Ở Repair, kỹ thuật viên chọn bước làm lại (rework về TEST chẳng hạn).
- **Không ghi `ScanLog`**, để số Good/NG của các bước không bị đếm hai lần. Lý do Fail nằm ở `FaiRecord.DefectCode` (mặc định `FAI-FAIL`; cột mới, migration `M7_FaiDefectCode`, CHECK: Fail phải có mã, Pass không có).
- Mẫu đã `Completed` (đã qua PACK, có thể đã vào thùng) → 422 "judge before packing". Đây là giới hạn đã biết: nếu mẫu lỡ bị đóng thùng thì xử lý bằng Hold (bước 9).

Sau Fail: `wo.RecordFaiResult(false)` xóa `FaiSerialNo`, board kế tiếp scan ở SMT thành mẫu mới (UPDATE có điều kiện của M3).

## E. Một transaction, và không có cache phải xóa

Ghi kết quả là **một `SaveChanges`**, tức một transaction, gồm ba thay đổi:
- thêm `FaiRecord` (insert-only),
- cập nhật `FaiStatus` / `FaiSerialNo` của WO,
- nếu Fail: chuyển serial mẫu sang `Ng`.

RowVersion của WO lấy từ phiếu QC đang xem. Nếu hai QC cùng chấm, người sau nhận **409**; nếu có board mới thành mẫu sau khi phiếu được mở, cũng 409.

Spec dặn "nhớ xóa cache WO của M3". Thực tế M3 **không cache** `FaiStatus` (ghi chú M3 bước 3b): route check đọc WO từ DB ở mỗi scan, nên Pass có hiệu lực ngay ở scan kế tiếp. Push SignalR `FaiStatusChanged` tới group của line **chỉ để tắt banner vàng** trên kiosk, không ảnh hưởng việc chặn.

Kiosk: panel Andon giữ kết nối SignalR duy nhất của trạm. Trước đây trạm không có máy (Pack) không kết nối; giờ trạm nào cũng join group line để nhận push FAI. Phần Andon vẫn chỉ hiện khi trạm có máy.

## F. Kiểm tra đã làm

- **Unit:**
  - `FaiChecksTests` (17): từng kiểm tự động, Q17, JSON phiếu, `RejectFirstArticle`.
  - `FaiViewModelTests` (6): Pass chờ tick đủ; mục đỏ khóa Pass và hiện lý do; Supervisor chỉ xem; Fail hỏi xác nhận; 409 thì nạp lại.
  - Kiosk nhận push.
- **Integration `FaiApiTests` (5)**, mỗi test dùng item riêng để plan của test khác không làm "not measured" đỏ:
  - Kịch bản đủ của spec: mẫu qua TEST, phiếu xanh, Pass, board 2 và 3 scan SMT được, push tới line.
  - Fail: mẫu `Ng`, WO vẫn `Pending`, board 2 thành mẫu mới.
  - Reel sai item trên mẫu: `BomMatch` đỏ, ghi rõ item, Pass trả 422, không ghi gì.
  - Chấm sớm và thiếu tick: 422; mục tay lạ: 400.
  - Phân quyền 403; phiếu cũ 409; WO lạ 404; hàng chờ xếp mẫu sẵn sàng lên trên.
- **Mutation:** bỏ điều kiện "mẫu đã qua TEST" thì test đỏ.
- **Chạy thật:**
  - Dữ liệu: nhận và nạp reel ASSY qua API, WO `WO-2611-00101`, mẫu qua SMT/ASSY/TEST với 3V3_RAIL = 3,301; board 2 bị `FaiNotApproved`.
  - BackOffice qua UI Automation: qc01 thấy 3 mục xanh, Pass tắt tới khi tick đủ 3 mục tay; bấm Pass, WO thành `Passed`, lịch sử có dòng của qc01.
  - Sau đó board 2 và 3 scan SMT OK; audit ghi `FaiStatus Pending → Passed` bởi qc01.
  - Ảnh: `docs/images/m7-fai-worksheet.png`, `m7-fai-passed.png`.

## G. Câu hỏi phỏng vấn

1. **"FAI là gì, MES hỗ trợ thế nào?"** Kiểm board đầu tiên sau setup. MES chặn line bằng route check tới khi QC Pass; phiếu tự điền kiểm BOM, test và số đo; Fail thì chọn mẫu mới. Bắt lỗi setup ở board số 1.
2. **"QC có Pass được khi máy báo đỏ không?"** Không, chặn ba tầng: UI, Domain (422), CHECK trong DB. Server tính lại các kiểm lúc ghi.
3. **"Hai QC cùng bấm?"** RowVersion của WO trên phiếu: một người thành công, người kia nhận 409 và màn hình nạp lại.
4. **"Sao QC Pass xong line chạy ngay, không phải chờ?"** Route check đọc `FaiStatus` từ DB mỗi lần scan, không cache. SignalR chỉ cập nhật banner.
