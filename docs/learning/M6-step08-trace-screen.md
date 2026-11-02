# M6 · Bước 8 — API truy xuất và màn hình Trace: kiến thức cần nắm

> Tài liệu học cho bước 8 của [module-06](../../spec/module-06-material-traceability.md) (spec mục **2.2b**, **2.6**, demo spec v2 bước 12).
> Bước trước: [M6-step07](M6-step07-trace-queries.md). Bước sau: [M6-step09](M6-step09-performance.md).
> Code: `src/MiniMes.Api/Controllers/TraceController.cs`, `src/MiniMes.Application/Traceability/Trace.cs`,
> `src/MiniMes.Client.Core/ViewModels/Traceability/*`, `src/MiniMes.BackOffice.Wpf/Views/Traceability/TraceView.xaml`.

## A. Nghiệp vụ: từ câu hỏi đến hành động (containment)

Kịch bản demo (spec v2 bước 12): nhà cung cấp báo **lô tụ SUP-S1 có vấn đề**. QC:

1. Mở **Traceability**, chọn *Supplier lot*, gõ `SUP-S1` + `CAP-100NF`. Kết quả là 200 board, nhóm theo **chúng đang ở đâu**:
   - Còn trong line: Hold ngay.
   - Đã đóng thùng hoặc lên pallet trong kho: cách ly.
   - Đã giao: báo khách.

   Đây là lý do có cột `Location` (mục 2.2b). Thùng và pallet sẽ có khi làm M3b.
2. **Export Excel**: gửi khách, hoặc lưu cho audit. File ghi giờ nhà máy, có sẵn cột Carton/Pallet/Shipment ref.
3. **Hold lot**: chặn mọi reel của lô giao hàng đó, cả reel đã tách ra (genealogy). Từ lúc này:
   - Reel còn trong kho không nạp lên máy được (bước 4, `LotOnHold`).
   - Reel **đang trên máy** chặn ngay board kế tiếp (gate của bước 5).
4. *Hold all serials* / *Hold pallets*: việc của module chất lượng M7, nên hiện mờ kèm tooltip.

Theo chiều ngược, khách gửi trả board `TRACE-0150`: cây cho thấy board dùng `L-CAP-B` (gắn ở L1-SMT-01 lúc 09:15, operator scan là EMP00123, reel do EMP00123 nạp). `L-CAP-B` tách ra từ `L-CAP-P` (2.000 cái).

**Vì sao Hold rộng hơn một lot?** Mục 1.5 gọi đây là lỗi "Hold quá hẹp": chỉ Hold đúng reel đang nhìn thấy mà quên các reel khác cùng lô giao hàng, và các lot con tách ra từ nó.

Hold chỉ đổi lot `Available`:
- Lot `PendingIqc`, `IqcRejected` và `Consumed` vốn đã bị chặn, nên được liệt kê là "Unchanged".
- Hold đè lên `PendingIqc` còn làm lot biến mất khỏi danh sách IQC.

Gỡ Hold là quyết định chất lượng (NCR, M7), không có nút "Unhold".

## B. API

| Endpoint | Ghi chú |
| --- | --- |
| `GET /api/trace/forward?lotNo=` / `?supplierLot=&itemCode=` | Đúng một trong hai cách hỏi, sai thì 400. Mã hàng thay cho id vì người dùng gõ mã |
| `GET /api/trace/forward.xlsx` | Cùng dữ liệu với màn hình, tạo ở server bằng ClosedXML (như M5) |
| `GET /api/trace/nodes/{serial\|lot}/{key}/children` | Một cấp của cây, phục vụ lazy-load |
| `POST /api/trace/hold-lot` | Một `SaveChanges`, có audit (ai Hold lot nào) |

Hỏi một mã không tồn tại thì trả **200 + danh sách rỗng**, không phải 404. Đây là ô tìm kiếm: gõ nhầm là chuyện bình thường. Riêng `hold-lot` là hành động trên một lot cụ thể, nên lot không có thì trả 404.

## C. TreeView lazy-load (mục 2.6)

- Nút có con thì giữ sẵn một nút giả "Loading...", để TreeView vẽ mũi tên mở.
- `IsExpanded` được bind **hai chiều** qua `ItemContainerStyle`. Lần mở đầu tiên gọi API, rồi thay nút giả bằng nút thật.
- Cờ `_loaded` được đặt **trước** `await`. Nếu đặt sau, mở rồi đóng nhanh sẽ gọi API hai lần (có test).
- Lỗi mạng: hiện một nút báo lỗi và đặt lại cờ, để lần mở sau tự thử lại (có test).
- Không có dữ liệu: hiện "No material recorded" thay vì một nút rỗng không giải thích gì.
- Sau `await` trong ViewModel, code chạy tiếp trên UI thread (SynchronizationContext của WPF), nên sửa `ObservableCollection` an toàn. Khác với sự kiện SignalR ở M4: sự kiện đó đến trên thread khác.

## D. Lỗi tìm ra khi chạy thật: grouping và cột `*`

Lưới truy xuất xuôi nhóm theo Location bằng `CollectionViewSource` + `PropertyGroupDescription`. Trên màn hình thật, **mọi cột chỉ rộng vài pixel**, trong khi unit test ViewModel vẫn xanh hết.

Tìm bằng cách đổi **một thứ mỗi lần** rồi chụp lại màn hình:

| Thử | Kết quả |
| --- | --- |
| Bỏ ảo hóa khi group | vẫn lỗi |
| Bỏ ẩn/hiện khung chứa | vẫn lỗi |
| Bỏ `GroupStyle` | cột đúng, nhưng mất nhóm |
| Dùng `ContainerStyle` + `Expander` | vẫn lỗi |
| Cột cuối `Width="*"` → số cố định | **đúng** |

Nguyên nhân: khi DataGrid bật grouping, một cột star (`*`) làm các cột khác bị ép về tối thiểu. Ghi chi tiết ở `docs/incidents.md`.

Bài học: test ViewModel không kiểm được layout, phải chạy app thật và nhìn.

## E. Câu hỏi phỏng vấn

1. **"Nhà cung cấp báo lỗi một lô, anh làm gì trong MES?"**
   - Truy xuất xuôi từ supplier lot qua genealogy, xem board đang ở đâu, xuất danh sách.
   - Hold lô giao hàng: reel trong kho không nạp được, reel trên máy chặn board kế tiếp.
   - Hold serial/pallet và báo khách qua NCR.
2. **"Cây truy xuất lớn thì sao?"** Lazy-load từng cấp, ảo hóa TreeView. Danh sách xuôi lớn thì không dùng cây: dùng bảng phẳng nhóm theo vị trí, cộng file Excel.
3. **"Ai được Hold, có vết không?"** Chỉ QC, Supervisor, Admin. Mỗi lot đổi trạng thái ghi một dòng `AuditLog` (cột Status, người, giờ) trong cùng transaction.
