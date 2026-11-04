# M7 · Bước 10 — Chứng minh cả chuỗi (DoD) và tổng kết module

> Tài liệu học cho bước 10 của [module-07](../../spec/module-07-quality.md) (mục **5.1 Definition of Done**). Code: `tests/MiniMes.IntegrationTests/Quality/HoldFlowTests.cs`.

## A. Vì sao cần một test "cả chuỗi" khi từng phần đã có test

Mỗi module đã có test riêng: route check chặn `Hold` (M3), verification chặn lot `Hold` (M6), Hold ghi đúng `Status` (M7 bước 9). Nhưng DoD của M7 là một **câu chuyện**: "reel lỗi → truy xuất → Hold → board bị chặn ở trạm". Câu chuyện có thể gãy ở **chỗ nối** dù từng phần đều xanh. Ví dụ:
- Truy xuất xuôi của M6 đọc `SerialComponent`, mà M6 chỉ ghi bảng này khi cổng vật tư bật và reel được nạp qua API. Nếu test tự chèn dòng vào bảng, sẽ không biết chỗ nối này còn chạy không.
- Hold đổi `Status` bằng SQL set-based; scan đọc `Status` qua EF. Nếu M3 có cache hoặc đọc cột khác, chỉ test cả chuỗi mới thấy.

Vì vậy `HoldFlowTests` **không tự ghi dòng nào thay cho hệ thống**: reel nạp qua API M6, board tiêu thụ thật khi scan, Hold qua API M7, trạm trả lời qua scan M3.

## B. Chuỗi trong test

| Bước | Module | Kết quả kiểm |
| --- | --- | --- |
| Nạp reel ở SMT, 3 board qua SMT (1 Completed, 1 chờ Pack, 1 chờ ASSY) | M6, M3 | 3 dòng `SerialComponent` do scan ghi |
| Truy xuất xuôi reel | M6 | 3 board |
| Preview + Hold theo lot | M7 | 3 board, 2 reel (cùng lần giao) |
| Scan ở ASSY, ở Pack | M3 | `SerialHold`, "Serial is on HOLD." |
| Board mới ở SMT (reel đang nạp bị Hold) | M6 gate | `MaterialNotLoaded`, không ghi `ScanLog` |
| Nạp reel còn lại của lần giao | M6 | `LotOnHold` |
| Release có ghi chú | M7 | 5 target trả lại; board xong vẫn `Completed`; `CurrentSeq` giữ nguyên |
| Line chạy tiếp | M3, M6 | ASSY, Pack, SMT đều OK; reel tiêu thụ lần thứ 4 |
| Bằng chứng | M0 audit | `HoldRecord` qc01 → sup01, 2 dòng audit |

Thêm **test đua**: Hold theo lot và scan ở ASSY cùng lúc, 5 vòng. Thứ tự nào thì board cuối cùng cũng là `Hold`. Nếu scan vào trước, `StatusBeforeHold` là trạng thái **sau** scan. Nếu Hold vào trước, scan nhận `SerialHold` (không phải 422, xem incident bước 9).

## C. Một điều học được khi viết test: scan lặp đứng trước Hold

Scan lại board vừa Completed, **trong 5 giây** sau lần scan OK của nó, trả về "duplicate" (OK, không ghi gì) dù board vừa bị Hold. Nguyên nhân: quy tắc 5 của M3 (scan lặp) được kiểm **trước** trạng thái, để cú bấm đúp của máy quét không bị báo "đã qua bước". Đây là thiết kế có chủ ý, không phải lỗ chặn:
- Không có gì được ghi.
- Câu trả lời chỉ xác nhận lại lần scan đã được chấp nhận trước lúc Hold.
- Ngoài đời, giữa lúc QC bấm Hold và lúc board tới trạm kế tiếp luôn lâu hơn 5 giây.

Biết điểm này giúp trả lời câu "thứ tự các quy tắc route check có ý nghĩa gì".

## D. "CI xanh" khi không được push

Quy tắc dự án: chỉ commit khi được yêu cầu, không tự push. Vì vậy bước này chạy **trên máy đúng các lệnh của `ci.yml`**, cấu hình Release:
- build Api, MachineSimulator, EdgeGateway, BackOffice, Kiosk: 0 warning;
- unit 1253/1253, integration 474/474.

Khác biệt còn lại so với CI: CI chạy Linux (`ubuntu-latest`), máy này chạy Windows. Code không đụng tới đường dẫn file hay múi giờ của OS (giờ nhà máy lấy từ `TimeProvider`).

## E. Tổng kết M7 để ôn phỏng vấn

| Chủ đề | Một câu để nói | Ở đâu |
| --- | --- | --- |
| USL ≠ UCL | Spec của khách và giới hạn của quy trình; 3,44 V vẫn OK nhưng là tín hiệu WE1 | bước 1, 5 |
| SPC I-MR | Một số đo mỗi board, σ ước lượng bằng MR̄/1,128 | bước 3, 7 |
| Western Electric | 3 rule, hàm thuần; alert không trùng nhờ filtered unique index + bắt lỗi khi đua | bước 4, 5 |
| SPC từ máy | Lấy mẫu tag lò mỗi 30 s, "data gap" thay vì bịa điểm | bước 6 |
| NCR | 4 trạng thái, người duyệt khác người đề xuất, RowVersion 409 vs 422 | bước 8 |
| FAI | Cổng chạy hàng loạt; phiếu tự điền 3 kiểm từ dữ liệu MES | bước 8b |
| Hold/Release | Một nguồn sự thật `Status`, ghi set-based, `StatusBeforeHold`, Hold chồng | bước 9, 10 |

Các incident đáng kể (`docs/incidents.md`):
- test đua không chạm race vì cách SQL Server khóa khi đọc;
- test thống kê tự "chứng minh" spec sai;
- Hold giữa hai lần đọc của scan, khiến kiosk nhận 422.
