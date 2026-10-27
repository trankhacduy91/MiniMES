# M4 · Bước 8 — WPF: dashboard line, Andon, resync: kiến thức cần nắm

> Tài liệu học cho bước 8 của [module-04](../../spec/module-04-machine-connectivity.md) (spec mục **2.6**, 2.7, 1.3).
> Bước 6 làm phía server của SignalR (hub, group, throttle); bước này làm phía **client WPF**: nhận push, đưa về thread
> UI, ghép với snapshot REST, và tự hồi phục khi rớt mạng.

---

## A. Nghiệp vụ: màn hình này để làm gì

| Ai | Nhìn ở đâu | Cần thấy gì | Làm gì |
| --- | --- | --- | --- |
| Supervisor, Maintenance | BackOffice → **Line dashboard** (màn hình lớn ở phòng line) | Máy nào đang đỏ, sản lượng giờ này, nhiệt độ lò, Andon nào đang chờ, Andon nào đã leo thang | Acknowledge, Resolve (chọn lý do dừng) |
| Operator | **Kiosk** ở trạm (màn hình scan) | Máy của trạm mình đang ở trạng thái gì, Andon mình gọi đã có ai nhận chưa | Bấm **ANDON**, chọn máy (nếu trạm có nhiều máy), chọn loại sự cố |

Màu ô máy theo quy ước stack light (spec 1.2g): **Running xanh lá, Idle vàng nhạt, Down đỏ, Setup xanh dương,
PlannedStop xám đậm, Offline xám nhạt**. `Offline` khác `Down`: Down là máy hỏng, Offline là MES không nghe thấy máy.

**"Sản lượng giờ này"** (decision 27/10/2026): server cộng các `Delta` theo **giờ đồng hồ của nhà máy**. Không lấy
"counter bây giờ − counter lúc :00", vì bộ đếm có thể reset giữa giờ. Mở dashboard lúc 9:40 vẫn thấy số 9:00–9:40, và
API restart không làm số về 0. Sang giờ mới, máy đang dừng cũng phải về 0: publisher tự đẩy một lần đúng lúc đổi giờ.

**Kiosk chọn máy** (decision 27/10/2026): trạm không gắn thẳng với máy mà đi qua work center
(trạm → work center → các máy). Trạm 1 máy thì máy được chọn sẵn; trạm nhiều máy thì operator chạm chọn máy; trạm
không có máy (Pack) thì không có Andon (`AndonCall` bắt buộc `MachineId`).

---

## B. Kiến trúc client

```
                    BackOffice.Wpf / Kiosk.Wpf (View: XAML, WpfUiDispatcher)
                                      │ DataContext
                    Client.Core (net10.0 thuần, unit test được trên Linux CI)
   ┌──────────────────────────────────┴──────────────────────────────────────┐
   │ LineDashboardViewModel / AndonPanelViewModel                            │
   │   ├─ IMachineApi        REST: snapshot + lệnh (Acknowledge, Resolve…)   │
   │   ├─ IRealtimeClient    SignalR: push (thread nền)                      │
   │   └─ IUiDispatcher      đưa push về thread UI                           │
   └─────────────────────────────────────────────────────────────────────────┘
```

| File | Vai trò |
| --- | --- |
| [Client.Core/Realtime/RealtimeClient.cs](../../src/MiniMes.Client.Core/Realtime/RealtimeClient.cs) | `HubConnection`, event C#, reconnect mãi mãi, join lại group, `Resync`; `IRealtimeClientFactory` |
| [Client.Core/Realtime/IUiDispatcher.cs](../../src/MiniMes.Client.Core/Realtime/IUiDispatcher.cs) | interface; WPF cài bằng `Dispatcher.InvokeAsync` (`WpfUiDispatcher` ở mỗi app) |
| [Client.Core/Api/MachineApi.cs](../../src/MiniMes.Client.Core/Api/MachineApi.cs) | REST cho dashboard + kiosk |
| [ViewModels/Machines/LineDashboardViewModel.cs](../../src/MiniMes.Client.Core/ViewModels/Machines/LineDashboardViewModel.cs) | dashboard: tile, danh sách Andon, banner |
| [ViewModels/Machines/MachineTileViewModel.cs](../../src/MiniMes.Client.Core/ViewModels/Machines/MachineTileViewModel.cs), [AndonRowViewModel.cs](../../src/MiniMes.Client.Core/ViewModels/Machines/AndonRowViewModel.cs) | một ô máy, một dòng Andon |
| [ViewModels/Kiosk/AndonPanelViewModel.cs](../../src/MiniMes.Client.Core/ViewModels/Kiosk/AndonPanelViewModel.cs) | panel Andon của kiosk |
| [BackOffice.Wpf/Views/Machines/LineDashboardView.xaml](../../src/MiniMes.BackOffice.Wpf/Views/Machines/LineDashboardView.xaml), [Kiosk.Wpf/Views/AndonPanel.xaml](../../src/MiniMes.Kiosk.Wpf/Views/AndonPanel.xaml) | giao diện; màu nằm ở XAML (DataTrigger), ViewModel chỉ biết state |

> ViewModel đặt ở **Client.Core** (CLAUDE.md mục 4), không ở `BackOffice.Wpf/ViewModels` như danh sách file của spec,
> để unit test chạy được mà không cần WPF.

---

## C. Kiến thức kỹ thuật

### C1. Snapshot REST + delta SignalR: mẫu quan trọng nhất của bước

**SignalR không giao lại message cho client đang rớt kết nối** (spec 2.6). Nếu chỉ dựa vào push thì sau khi mạng chập
chờn 5 giây, dashboard có thể hiện máy "Running" mãi trong khi máy đã Down từ lâu. Mẫu đúng:

```
1. Join group của line (bắt đầu NGHE)
2. Tải snapshot REST  (GET /api/lines/L1/status, GET /api/andon?status=open&line=L1)
3. Áp push lên trên snapshot
4. Mỗi lần (re)connect: join lại group → tải lại snapshot
```

- **Nghe trước, tải sau**: một thay đổi xảy ra giữa bước 1 và 2 sẽ đến dưới dạng push, không bị lọt.
- Snapshot đầu tiên được tải trước cả khi kết nối xong (để màn hình có dữ liệu ngay cả khi hub chưa vào được). Khi kết
  nối xong, client join line và bắn `Resync`, nên snapshot được tải **lại**. Không có khoảng hở nào.
- Đây là cùng ý tưởng với *event sourcing / CDC*: "trạng thái = ảnh chụp + các thay đổi sau ảnh chụp".

### C2. Push đến muộn hơn snapshot: không cho màn hình "đi lùi"

Push có thể đang trên đường khi snapshot được tải, nên đến **sau** snapshot nhưng **cũ hơn** nó. Mỗi loại dữ liệu có một
"đồng hồ" riêng để so:

| Dữ liệu | So bằng gì | Lý do |
| --- | --- | --- |
| State | `Since` (lúc vào state) | state mới luôn có `Since` lớn hơn |
| Counter | `LastSeenAt` | counter có thể **reset** (nhỏ đi), nên không so counter |
| Tag process | `SourceTs` | giờ đo của cảm biến |
| Andon | **`RowVersion`** (8 byte big-endian → `ulong`) | SQL Server tăng `rowversion` sau mỗi lần ghi, nên số lớn hơn là bản mới hơn |

Mẹo Andon: dashboard nhớ version lớn nhất đã thấy của **mọi** Andon, kể cả Andon đã bị xóa khỏi danh sách. Nếu không,
một push "Raised" đến trễ sau "Resolved" sẽ làm Andon đã đóng **hiện lại**. Unit test
`Andon_pushes_add_update_and_remove_rows_and_never_go_back` bắt đúng trường hợp này.

### C3. Thread UI và `IUiDispatcher`

- Callback `HubConnection.On<T>` chạy trên **thread nền**. WPF chỉ cho thread UI chạm control và `ObservableCollection`
  đang bind. Sai thì gặp `InvalidOperationException: The calling thread cannot access this object…` hoặc
  `NotSupportedException` khi sửa collection.
- Giải pháp: mọi push đi qua `ui.Post(() => …)`. WPF cài bằng `Application.Current.Dispatcher.InvokeAsync`.
- **`InvokeAsync`, không dùng `Invoke`**: `Invoke` chặn thread gọi đến khi UI chạy xong, dễ deadlock nếu thread UI đang
  chờ chính kết nối đó.
- Đặt sau interface để Client.Core **không tham chiếu WPF** (CI chạy Linux) và unit test dùng `ImmediateDispatcher`
  (chạy luôn trên thread test).

### C4. Reconnect: ba tình huống khác nhau

| Tình huống | Ai lo | Cách làm |
| --- | --- | --- |
| Đang kết nối thì rớt | `WithAutomaticReconnect(IRetryPolicy)` | Chính sách tự viết: 0, 2, 5 giây rồi **10 giây mãi mãi**. Mặc định chỉ thử 0/2/10/30 giây rồi **bỏ cuộc**, không hợp màn hình treo tường |
| Lần kết nối **đầu tiên** thất bại (API chưa chạy) | Tự viết vòng `StartAsync` | `WithAutomaticReconnect` chỉ áp dụng cho kết nối **đã từng thành công** |
| Kết nối lại xong | `Reconnected` | `ConnectionId` mới nên server quên mọi group: **join lại line** (role group thì server tự join ở `OnConnectedAsync`, bước 7b), rồi `Resync` |

- `Reconnecting` → `ConnectionChanged(false)` → banner "Reconnecting…". Không giấu lỗi: người nhìn phải biết màn hình
  có thể đã cũ.
- Đăng ký `On<T>` **trước** `StartAsync`, nếu không sẽ lỡ những push đầu tiên.
- Tên event lấy từ `nameof(IMachineClient.MachineStateChanged)` (hub định kiểu dùng chung trong Contracts): gõ sai tên thì
  lỗi biên dịch, không phải "chẳng bao giờ nhận được gì" lúc chạy.

### C5. Vòng đời kết nối và một cái bẫy của DI

- Mỗi màn hình mở **một** kết nối và phải **đóng** khi rời màn hình. Thêm interface `IClosable { Task CloseAsync(); }`;
  `PageNavigator` (BackOffice) và `Navigation` (Kiosk) gọi nó khi chuyển trang hoặc logout.
- **Vì sao không dùng `IDisposable`/`IAsyncDisposable` cho ViewModel?** Container DI của .NET **giữ tham chiếu tới mọi
  object Transient có `IDisposable` mà nó tạo từ root provider**, để dispose khi app tắt. ViewModel tạo từ root
  (`services.GetRequiredService<T>()`) sẽ không bao giờ được GC: mở dashboard 100 lần là rò 100 ViewModel kèm kết nối.
- Cùng lý do, `RealtimeClient` (có `IAsyncDisposable`) **không đăng ký Transient** mà tạo qua
  `IRealtimeClientFactory.Create()`: ViewModel tạo, ViewModel sở hữu, ViewModel dispose.
- Câu phỏng vấn hay gặp: "Transient IDisposable resolve từ root provider thì sao?" → bị container giữ tới khi root dispose
  (memory leak). Cách tránh: dùng scope, factory, hoặc không implement `IDisposable` cho thứ không cần.

### C6. MVVM: ViewModel không biết màu, View không biết luật

- ViewModel chỉ có `State = Down`; XAML có `DataTrigger Binding="{Binding State}" Value="Down"` → nền đỏ. Đổi màu theo
  quy ước nhà máy chỉ sửa XAML.
- `TextElement.Foreground` đặt trên `Border` là **attached property kế thừa**: mọi `TextBlock` bên trong đổi màu chữ theo
  nền (chữ trắng trên đỏ, chữ đen trên vàng).
- Dòng Andon leo thang **nhấp nháy** bằng `DoubleAnimation` trên `Opacity`, không animate màu nền: brush đặt trong
  `Style` bị **freeze** (đóng băng để chia sẻ giữa các thread), animate sẽ ném "Cannot animate … on an immutable object".
- Nút Acknowledge/Resolve chỉ hiện với role được phép (`CanRespond`, lấy từ JWT). Đây là **UX**, không phải bảo mật:
  API vẫn kiểm tra role và trả 403.
- Kiosk: mọi nút `Focusable="False"`, panel chọn máy dùng **nút**, không dùng `ListBox`. Máy quét mã vạch là một
  "bàn phím", ký tự phải luôn vào ô scan (quy ước M3).

### C7. Server tính "sản lượng giờ này"

- `MachineRuntimeStore` (RAM) giữ `HourStart` + `HourCount` cho mỗi máy, cộng `Delta` khi nhận count. Giờ được tính theo
  **múi giờ nhà máy** (`PlantTimeZone.HourStartUtc`). Với VN (+7) giờ nhà máy trùng giờ UTC, nhưng Ấn Độ (+5:30) thì
  không, và test có case này.
- Khởi động API: `MachineRuntimeLoader` nạp `SUM(Delta)` của giờ hiện tại từ `MachineEvents` (index
  `(MachineId, MachineTs) INCLUDE (Kind, Delta)` có sẵn từ bước 1).
- Đổi giờ: `TakeDirty(now)` thấy máy còn giữ số của giờ cũ thì đặt về 0 và đánh dấu dirty, nên publisher đẩy số 0 **một
  lần**.
- Vì sao để server tính: mọi client thấy cùng một số, đúng cả khi reset/restart, và client không cần biết múi giờ nhà máy.

### C8. Lỗi thao tác không được bị "nuốt"

Supervisor bấm Acknowledge mà người khác đã nhận: API trả 409, ViewModel hiện thông báo rồi **tải lại** để thấy ai đã
nhận. Bản đầu tiên tải lại xong thì xóa luôn thông báo lỗi, nên người dùng không kịp thấy lý do. Unit test
`Acknowledge_conflict_shows_the_message_and_reloads` bắt được lỗi này trước khi chạy thật. Đã sửa bằng tham số
`keepError`.

---

## D. Test

| Loại | File | Nội dung |
| --- | --- | --- |
| Unit | [LineDashboardViewModelTests.cs](../../tests/MiniMes.UnitTests/Client/LineDashboardViewModelTests.cs) | join line **trước** khi đọc snapshot; nhãn nguồn MQTT/OPC UA; push state/counter/tag cập nhật ô, push cũ hoặc của line khác bị bỏ; tag `Bad` vẫn hiện (gạch, mờ); Andon thêm/sửa/xóa, push "Raised" trễ không làm Andon đã đóng sống lại; leo thang: dòng `IsEscalated` + banner; rớt kết nối → banner, kết nối lại → tải lại snapshot; đổi line → leave/join; Acknowledge, 409, Resolve thiếu lý do; nút theo role; API chết lúc mở; đóng màn hình → dispose kết nối |
| Unit | [AndonPanelViewModelTests.cs](../../tests/MiniMes.UnitTests/Client/AndonPanelViewModelTests.cs) | 1 máy chọn sẵn; nhiều máy phải chọn trước khi gửi; đã có Andon mở → báo, không tạo bản thứ hai; trạm không có máy → không có Andon; banner theo Andon (Raised → Acknowledged by → mất khi Resolved) và state; reconnect; màn hình scan mở/đóng panel |
| Unit | `MachineRuntimeStoreTests` (+4), `PlantTimeZoneTests` (+5) | đếm giờ qua reset, sang giờ mới, máy im lặng về 0 một lần, nạp lại sau restart; giờ nhà máy VN/Ấn Độ |
| Integration | `MachineEventWriterTests` (+1), `RealtimeTests` | loader nạp đúng số của giờ hiện tại (bỏ giờ trước, cộng qua reset); snapshot REST có `CountThisHour` |

Chưa có test tự động cho chính `RealtimeClient` với server thật: IntegrationTests chỉ được tham chiếu Api/Infrastructure
(CLAUDE.md mục 4), không thấy Client.Core. Phần này được kiểm bằng chạy thật (mục E).

---

## E. Chạy thật

Điều khiển app WPF bằng **UI Automation** (`System.Windows.Automation`, dựa trên `AutomationProperties.AutomationId` đã
đặt trong XAML) và chụp màn hình bằng PowerShell. Đây cũng là nền của test UI tự động (FlaUI, WinAppDriver).

| Kịch bản | Kết quả |
| --- | --- |
| `down L1-SMT-01` → ô đỏ | **370 ms** (UIA dò chữ "Down" mỗi 50 ms, tính từ lúc gửi lệnh vào simulator) |
| Andon tự động → Acknowledge → chọn lý do → Resolve (bấm trên UI) | dòng hiện, đổi "Acknowledged by sup01", rồi biến mất |
| Tắt API 25 giây, trong lúc đó máy chạy lại | banner "Reconnecting" + ô vẫn Down (cũ); bật API → banner mất, ô **Running**, số giờ giữ nguyên |
| Kill simulator (dashboard mở trước) | 3 ô Offline sau 3,3 giây (LWT) |
| Simulator `cycleTimeSec = 0.2` | ô đổi đúng 1 lần/giây; UIA phản hồi TB 62 ms → UI không nghẽn |
| Kiosk trạm 2 máy: ANDON → chọn máy → Material | "Andon sent…", chip "Material Andon: Raised"; supervisor ack → chip "Acknowledged by sup01"; ô scan vẫn giữ focus |
| Đăng nhập `admin`, chờ 3 phút | banner leo thang + dòng Level 1 nhấp nháy (lấy mẫu điểm ảnh: màu dao động) |

Phát hiện khi chạy thật: lưới Andon rộng quá khung, bị cuộn ngang che cột Machine (đã sửa: cột co giãn `Width="*"`); giá trị
tag process mới nhất mất sau khi API restart (chưa sửa, chờ duyệt, xem ghi chú spec).

---

## F. Câu hỏi phỏng vấn

1. Vì sao dashboard cần snapshot REST nếu đã có SignalR? Thứ tự đúng là gì, vì sao "nghe trước, tải sau"?
2. Push đến sau snapshot nhưng cũ hơn: xử lý thế nào? Vì sao không so counter mà so thời gian? `rowversion` dùng làm gì?
3. "The calling thread cannot access this object" là gì? `Dispatcher.Invoke` khác `InvokeAsync` thế nào?
4. Sau reconnect SignalR phải làm gì? Vì sao `WithAutomaticReconnect` mặc định không đủ cho màn hình treo tường?
5. Transient `IDisposable` resolve từ root provider gây ra vấn đề gì?
6. Vì sao ViewModel không chứa màu? Ẩn nút theo role có phải là bảo mật không?
7. "Sản lượng giờ này" tính ở đâu, vì sao không lấy counter hiện tại trừ counter đầu giờ?
8. Kiosk dùng máy quét mã vạch: điều đó ảnh hưởng thiết kế UI thế nào?

## G. Đọc thêm

- Spec module-04 mục 2.6 (snippet client + bẫy), 2.7 (throttle), 1.2g (màu stack light).
- Microsoft Docs: *ASP.NET Core SignalR .NET Client* (`WithAutomaticReconnect`, `IRetryPolicy`, `Reconnected`),
  *Threading model (WPF)*, *Dependency injection guidelines → Disposal of services*, *Freezable objects overview*.
