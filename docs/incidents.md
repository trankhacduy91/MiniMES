# Incidents

Lỗi đáng nhớ (khó tìm nguyên nhân, có bài học) để dùng khi phỏng vấn.

## 2026-10-11 — `Login failed for user 'sa'` dù mật khẩu đúng (M0 bước 3)

- **Triệu chứng:** `dotnet ef database update` và API báo `Error 18456, Login failed for user 'sa'`. Cùng mật khẩu đó, `sqlcmd` chạy *bên trong* container lại đăng nhập được.
- **Nguyên nhân:** máy dev có SQL Server Express cài sẵn (service `MSSQL$SQLEXPRESS`) cũng nghe cổng 1433. `netstat -ano` thấy hai tiến trình cùng LISTENING `0.0.0.0:1433`: `sqlservr.exe` (Express) và `com.docker.backend.exe`. Kết nối từ host tới `localhost,1433` rơi vào SQL Express, nơi `sa` có mật khẩu khác.
- **Cách tìm:** so kết quả "đăng nhập trong container thì được, từ host thì không" → nghi sai đích kết nối, không phải sai mật khẩu → `netstat -ano | findstr :1433` + `tasklist /FI "PID eq ..."`.
- **Cách sửa:** cổng host của container đọc từ `MSSQL_PORT` trong `.env` (mặc định 1433, máy này đặt 14333); connection string trỏ `localhost,14333`.
- **Bài học:** "Login failed" không chứng minh mật khẩu sai; kiểm tra *đang nói chuyện với server nào* trước. Hai process cùng bind một cổng trên Windows vẫn có thể xảy ra.

## 2026-10-11 — CorrelationId mất ở response lỗi và ở file log (M0 bước 4)

- **Triệu chứng:** response 422/500 không có header `X-Correlation-Id`, `correlationId` trong ProblemDetails rỗng; file log hiện `-` ở cột CorrelationId dù request có gửi id.
- **Nguyên nhân (2 lỗi riêng):**
  1. `ExceptionHandlerMiddleware` gọi `Response.Clear()` trước khi chạy `IExceptionHandler`, xóa header middleware đã đặt. Handler đọc lại từ `Response.Headers` nên được chuỗi rỗng.
  2. `Enrich.WithProperty("CorrelationId", "-")` đặt *trước* `Enrich.FromLogContext()`. Cả hai chỉ thêm thuộc tính khi chưa có, nên giá trị mặc định thắng.
- **Cách tìm:** không tin console; mở file `logs/minimes-*.log` và `grep` theo id của từng request, đối chiếu với response.
- **Cách sửa:** middleware lưu id vào `HttpContext.Items`, handler đặt lại header + `correlationId` từ đó; đảo thứ tự enricher (`FromLogContext` trước, `WithProperty` mặc định sau).
- **Bài học:** kiểm tra đầu-cuối (response ↔ log file) cho cross-cutting concern; thứ tự middleware/enricher quyết định kết quả và build xanh không bắt được.

## 2026-10-12 — README chạy được trên máy dev nhưng không chạy được trên bản clone mới (M0 bước 11)

- **Triệu chứng:** làm đúng README trên một bản clone mới, `dotnet ef database update` báo `NETSDK1004: Assets file ... project.assets.json not found`.
- **Nguyên nhân:** `dotnet ef` đọc metadata của project Infrastructure (cần `obj/project.assets.json`, sinh ra lúc restore) trước khi tự build. Trên máy dev project luôn đã được restore từ trước, nên lỗi không bao giờ hiện.
- **Cách tìm:** nghiệm thu đúng nghĩa: clone vào thư mục **tên khác** (để Docker tạo volume và DB mới), sao lưu user-secrets rồi đặt lại theo README, tắt stack dev đang chạy, và làm từng lệnh một như người lạ.
- **Cách sửa:** README thêm `dotnet build` trước `dotnet ef database update`. Thêm vào mục "Gặp lỗi": mật khẩu SA lưu trong volume ở lần chạy đầu; user-secrets dùng chung giữa các bản clone trên một máy.
- **Bài học:** máy dev có rất nhiều trạng thái ẩn (package đã restore, secrets, volume DB, container đang chạy). Tài liệu "cách cài đặt" chỉ đáng tin khi đã chạy thử trên môi trường sạch; đó cũng là lý do CI chạy trên máy sạch.

## 2026-10-13 — Quy tắc Domain so khóa ngoại trước khi EF kịp gán (M1 bước 7)

- **Triệu chứng (bắt được trước khi chạy, xác nhận bằng mutation):** tạo trạm mới kèm `WorkCenterId` của **đúng** line vẫn bị 422 "Work center is on another line".
- **Nguyên nhân:** `line.AddStation(...)` chỉ thêm `Station` vào collection `Line.Stations`; EF chỉ điền `Station.LineId` lúc `SaveChanges` (relationship fix-up). Quy tắc `Station.AssignWorkCenter` chạy *trước* `SaveChanges` nên thấy `LineId = 0`.
- **Cách tìm:** đọc lại luồng tạo entity con qua aggregate cha và hỏi "lúc quy tắc chạy, giá trị này đã có chưa?"; thử lại bằng mutation (truyền 0 như cũ) → integration test đỏ đúng chỗ.
- **Cách sửa:** `Line.AddStation` truyền `Id` của line vào constructor của `Station`, nên `LineId` có ngay trong bộ nhớ.
- **Bài học:** với EF, khóa ngoại của entity mới thêm qua navigation chưa có giá trị cho tới `SaveChanges`. Quy tắc Domain cần dữ liệu đó thì entity phải tự giữ nó, đừng dựa vào ORM. Unit test Domain không bắt được lỗi này (không có EF); integration test chạy qua API thật thì bắt được.

## 2026-10-17 — Lưới trống sau khi Save vì DTO là `record` (M1 bước 8, WPF)

- **Triệu chứng (bắt bởi unit test ViewModel):** trong Routing editor, bấm Save xong thì lưới các bước trống trơn dù server đã lưu đúng. BOM editor bị y hệt khi lưu mà số dòng không đổi.
- **Nguyên nhân:** sau khi lưu, ViewModel xóa lưới, tải lại danh sách version và gán lại `SelectedVersion`. DTO là `record` nên bản mới tải về **bằng giá trị** với bản cũ. Setter do CommunityToolkit sinh ra so bằng `EqualityComparer<T>.Default`, thấy "không đổi" nên không gọi `OnSelectedVersionChanged`, và các bước không bao giờ được tải lại.
- **Cách tìm:** test "di chuyển bước rồi Save" đọc lưới sau khi lưu và thấy chuỗi rỗng; fake trả về một instance `record` mới nhưng cùng giá trị, đúng như HTTP thật làm.
- **Cách sửa:** gán `SelectedVersion = null` trước khi chọn lại, để luôn có một lần đổi thật. Thêm test hồi quy cho cả hai màn hình.
- **Bài học:** `record` so sánh theo giá trị là tính năng; nhưng các thứ dựa trên "giá trị có đổi không" (setter của MVVM toolkit, `SelectedItem` của WPF, `distinct`) sẽ coi hai lần tải là một. Khi cần phản ứng với *lần tải mới* chứ không phải *giá trị mới*, hãy tạo thay đổi tường minh. Fake trong test nên trả instance mới như mạng thật, không trả lại cùng một object.

## 2026-10-17 — Màn hình Calendar trống vì client hỏi 2 năm, API chỉ cho 1 năm (M1 bước 8, WPF)

- **Triệu chứng (bắt khi chạy app thật):** thêm một ngày tăng ca, lưu thành công, nhưng lưới Plant calendar vẫn trống; mọi unit test ViewModel đều xanh.
- **Nguyên nhân:** `PlantCalendarViewModel` xin `from = 1/1 năm nay`, `to = 31/12 năm sau` (2 năm). `GetPlantCalendarValidator` ở API (viết ở bước 7) giới hạn tối đa 366 ngày, nên trả 400 và lưới không có dữ liệu. Fake `IMasterDataApi` trong unit test trả dữ liệu cho mọi khoảng, không biết luật của server.
- **Cách tìm:** gọi đúng URL đó bằng `Invoke-RestMethod` → `400 "Ask for at most one year at a time."`.
- **Cách sửa:** client gọi 2 lần, mỗi lần một năm (năm sau vẫn cần vì Tết thường rơi vào tháng 1–2). Fake trong test giờ cũng từ chối khoảng > 366 ngày, và test kiểm tra 2 lần gọi.
- **Bài học:** fake chỉ tốt bằng hiểu biết của người viết về hợp đồng (contract) của server. Luật nằm ở validator phía server thì fake nên mô phỏng nó, hoặc phải có một lần chạy đầu-cuối thật (app + API) trước khi coi là xong.

## 2026-10-17 — File PDF có thể hỏng sau khi clone vì git đổi xuống dòng (M1 bước 9)

- **Triệu chứng (bắt được ngay lúc commit, trước khi ai clone):** `git commit` cảnh báo `LF will be replaced by CRLF` cho 4 file SOP `docs/sop/PCB-A/*.pdf`. File trên máy dev vẫn mở bình thường.
- **Nguyên nhân:** repo chưa có `.gitattributes`. Máy Windows bật `core.autocrlf=true`, nên git đoán file theo nội dung. PDF tự sinh chỉ chứa ký tự ASCII nên bị coi là **text**; khi checkout trên Windows, mọi `\n` sẽ thành `\r\n`. Bảng `xref` của PDF lưu **vị trí byte** của từng object; mỗi dòng dài thêm 1 byte thì các vị trí lệch hết, trình đọc phải dò lại hoặc báo file hỏng. File `.xlsx` không bị vì là zip (có byte nhị phân) nên git tự nhận là binary.
- **Cách tìm:** không bỏ qua cảnh báo của git; `git ls-files --eol` cho thấy PDF mang `i/lf` (text) còn xlsx mang `-text` (binary).
- **Cách sửa:** thêm `.gitattributes` đánh dấu `*.pdf`, `*.xlsx`, `*.png` là `binary` (không đổi xuống dòng, không diff/merge như text); `git add --renormalize`. Kiểm tra: xóa file, `git checkout` lại, render bằng `Windows.Data.Pdf` vẫn mở được.
- **Bài học:** repo dùng trên Windows nên có `.gitattributes` từ đầu, đừng để git đoán. Một file "trông như text" vẫn có thể là định dạng nhạy cảm từng byte (PDF, chữ ký số, file có checksum). Cảnh báo `LF will be replaced by CRLF` trên file không phải code là dấu hiệu cần dừng lại xem.

## 2026-10-17 — Test đồng thời xanh, nhưng không vì lý do mình tưởng (M2 bước 5)

- **Triệu chứng:** test "2 request Release cùng một WO" xanh: một 200, một 409, đúng 200 serial. Nhưng log có `DbUpdateException` chung chung, không phải thông báo concurrency của EF ("expected to affect 1 row(s)").
- **Nguyên nhân:** in `ITestOutputHelper` và đọc inner exception: `SqlException 2601` trên unique index `(WorkOrderId, ComponentItemId, OperationSeq)` của `WorkOrderMaterials`. Trong một `SaveChanges`, EF gửi INSERT các bảng con (serial, vật tư, công đoạn) **trước** UPDATE dòng `WorkOrders`. Request thua bị khóa chờ dòng vật tư request thắng vừa insert; khi bên thắng commit, nó trùng khóa và rollback. `RowVersion` (thứ spec nói sẽ chặn) chưa kịp được dùng tới.
- **Cách tìm:** không dừng ở "test xanh"; hỏi "409 này đến từ đâu?" → log chi tiết (`--logger "console;verbosity=detailed"`) → đếm các inner exception.
- **Cách sửa:** không cần sửa logic: cả 3 lớp (unique index vật tư, unique `(WorkOrderId, Seq)` công đoạn, `RowVersion` trên `WorkOrders`) nằm trong cùng một transaction, lớp nào chặn trước thì toàn bộ cũng rollback. Chỉ sửa thông báo 409 cho lỗi unique thành trung tính ("vừa được tạo hoặc sửa bởi người khác") vì "record already exists" gây hiểu nhầm. Thêm mutation theo spec: thay sequence bằng `MAX+1` → 2 WO release cùng lúc: một bên 409 vô lý; bỏ thêm unique index `SerialNo` → serial trùng **được lưu thật**. Chạy test đồng thời 20 lần liên tiếp đều xanh.
- **Bài học:** test xanh chỉ chứng minh kết quả, không chứng minh cơ chế. Muốn nói "rowversion chặn double release" khi phỏng vấn thì phải biết chính xác lớp nào chặn trong thực tế. Phòng thủ nhiều lớp trong một transaction làm hệ thống đúng ngay cả khi lớp mình định dùng không phải lớp chạy trước. Và: thứ tự lệnh trong một `SaveChanges` là chi tiết của EF, đừng giả định.

## 2026-10-17 — Test "scan nhanh liên tiếp" xanh cả khi tắt cấu hình nó cần bảo vệ (M3 bước 7, Kiosk)

- **Triệu chứng (bắt bằng mutation):** `ScanCommand` cần `[RelayCommand(AllowConcurrentExecutions = true)]`: máy quét gửi serial thứ hai trước khi API trả lời serial thứ nhất. Test "gửi scan thứ hai khi scan thứ nhất còn chờ" xanh. Xóa `AllowConcurrentExecutions` đi, test **vẫn xanh**.
- **Nguyên nhân:** test gọi `ScanCommand.ExecuteAsync(...)` trực tiếp. `AsyncRelayCommand.ExecuteAsync` **không hỏi `CanExecute`**, nên lần gọi thứ hai luôn chạy. Trên app thật, `KeyBinding Key="Return"` (Enter của máy quét) **hỏi `CanExecute` trước**. Với mặc định của toolkit, `CanExecute` = false khi lệnh đang chạy, nên Enter thứ hai bị bỏ qua mà không có tiếng báo nào.
- **Cách sửa:** test kiểm tra đúng điều app thật kiểm tra: `Assert.True(vm.ScanCommand.CanExecute(null))` khi lần scan đầu còn chờ. Xóa cấu hình đi lần nữa thì test đỏ.
- **Bài học:** gọi thẳng API của một lớp trong test có thể đi vòng qua đúng chỗ mà UI dùng để chặn (CanExecute, validation, binding). Test nên đi đúng đường người dùng đi, và mutation (xóa dòng cần bảo vệ rồi xem test có đỏ không) là cách rẻ nhất để biết test có thật sự bảo vệ dòng đó.

## 2026-10-17 — Đo hiệu năng tìm ra race condition mà test đồng thời bỏ sót (M3 bước 10)

- **Triệu chứng (bắt khi chạy benchmark):** chạy 8 luồng scan khi tạm bỏ index `ScanLogs`, bo thứ hai của một WO vừa release nhận **422** "Work order is InProgress; Start is not allowed", dù lần scan đó hợp lệ. Có index thì không thấy. Integration test "2 bo đầu tiên scan cùng lúc" vẫn xanh.
- **Nguyên nhân:** handler đọc context (trong đó có `WoStatus = Released`), rồi mới mở transaction và nạp WO để gọi `Start()`. Giữa hai lúc đó còn câu đọc `ScanLogs`. Không có index, câu này mất khoảng 150 ms; trong thời gian ấy, một bo khác đã `Start` WO và commit. Request đến sau nạp WO mới (`InProgress`), vẫn gọi `Start()`, và state machine của M2 từ chối đúng luật. Có index thì khoảng hở dưới 1 ms nên gần như không bao giờ trúng. Trường hợp hai bên gọi `Start` **cùng lúc** thì `RowVersion` + thử lại đã xử lý; trường hợp **lệch nhau** thì chưa.
- **Cách tìm:** đọc kỹ thông báo 422 của benchmark (không coi là "nhiễu do quá tải"), rồi hỏi: giữa lúc đọc `WoStatus` và lúc nạp WO có những gì?
- **Cách sửa:** thêm `WorkOrder.EnsureStarted()` trong Domain: WO đang `InProgress` thì không làm gì; các trạng thái khác vẫn qua `Start()` và vẫn bị từ chối nếu sai. Handler gọi `EnsureStarted`. Trước khi sửa, viết test **dựng lại đúng thứ tự** bằng một `DbCommandInterceptor` trong test (`ScanGate`): chặn câu `ScanLogs` của bo B, cho bo A scan xong, rồi thả B. Test này đỏ 3/3 lần với đúng thông báo 422, sửa xong thì xanh. Test "8 bo cùng lúc × 3 vòng" (thay cho 2 bo) vẫn **không** bắt được lỗi, điều đó cũng được ghi lại.
- **Bài học:** test đồng thời kiểu "bắn N request cùng lúc" chỉ bắt được race khi khoảng hở đủ rộng; trên máy dev nhanh, nó gần như luôn xanh. Muốn chứng minh, hãy dựng lại thứ tự (hook vào giữa hai bước, như interceptor ở đây) thay vì trông vào may rủi. Và: làm chậm hệ thống (bỏ index, thêm độ trễ) là cách rẻ để mở rộng khoảng hở, lộ race ẩn. "Đọc rồi mới ghi" thì giữa hai bước ấy luôn phải hỏi: nếu trạng thái đổi thì sao?

## 2026-10-17 — Mảng có giá trị mặc định bị cấu hình "nhân đôi" (M4 bước 2, simulator)

- **Triệu chứng:** simulator vừa chạy đã thoát: `L1-SMT-01: downDurationSec must be [min, max] seconds` cho cả 3 máy, dù `machines.json` ghi đúng `"downDurationSec": [30, 180]`.
- **Nguyên nhân:** trong class options mình khởi tạo sẵn `DownDurationSec = [30, 180]` làm mặc định. `Microsoft.Extensions.Configuration` binder **nối thêm** các phần tử của JSON vào mảng/list đã có, không thay thế. Kết quả: `[30, 180, 30, 180]`, 4 phần tử, validation từ chối. Nếu không có validation, code chỉ đọc `[0]` và `[1]` nên sẽ chạy "đúng" một cách tình cờ; nhưng khi JSON ghi `[20, 90]`, máy sẽ dùng `[30, 180]` mặc định mà không ai biết.
- **Cách sửa:** không đặt giá trị mặc định cho thuộc tính mảng của options (`= []`). Bắt buộc khai báo trong JSON và để validation báo lỗi nếu thiếu.
- **Bài học:** binder cấu hình .NET gộp mảng theo chỉ số (key `machines:0:downDurationSec:0`), nên mặc định khởi tạo trong code và giá trị JSON sẽ bị cộng dồn. Validate cấu hình ngay lúc khởi động, báo rõ lý do và dừng app (fail fast), thay vì chạy với cấu hình sai.

## 2026-10-17 — Xả hàng đợi lúc dừng không chạy vì .NET 10 đổi cách chạy `BackgroundService` (M4 bước 4)

- **Triệu chứng:** test "dừng thì ghi nốt phần còn trong hàng đợi" của `MachineEventBatchWriter` đỏ: 250 dòng đưa vào channel, DB có 0 dòng. Hai test khác cũng thấy bảng rỗng. Tất cả chạy xong trong 20–40 ms, nghĩa là writer chưa hề chạm tới DB.
- **Nguyên nhân:** code theo snippet của spec: đoạn xả nằm ở **cuối** `ExecuteAsync`, sau vòng lặp. Từ .NET 10, `BackgroundService.StartAsync` chạy `ExecuteAsync` trên thread nền thay vì chạy đồng bộ tới `await` đầu tiên. Test gọi `StartAsync` rồi `StopAsync` ngay; token bị hủy trước khi `ExecuteAsync` kịp chạy, nên đoạn xả cuối hàm không bao giờ được thực thi. Một script nhỏ (start rồi stop ngay, lặp 20 lần) cho thấy `ExecuteAsync` chỉ kịp chạy **1/20** lần, và luôn ở thread khác thread gọi.
- **Cách sửa:** chuyển việc xả sang override `StopAsync`: gọi `base.StopAsync` (chờ `ExecuteAsync` kết thúc hoặc chưa từng chạy), rồi đọc hết channel và ghi với tối đa 3 lần thử. Batch đang gom dở được giữ ở field để `StopAsync` ghi được. Nếu `ExecuteTask` vẫn chưa xong (quá thời gian dừng của host) thì log lỗi kèm số dòng thay vì ghi song song. Chạy lại 5 lần đều xanh.
- **Bài học:** "dọn dẹp lúc dừng" đặt ở cuối `ExecuteAsync` phụ thuộc vào việc hàm đó đã chạy và kết thúc gọn; chỗ chắc chắn được gọi là `StopAsync`. Snippet viết cho phiên bản cũ (spec ghi .NET 8) có thể đúng ở đó nhưng sai ở bản mới. Test start/stop ngay lập tức là cách rẻ để lộ loại lỗi này, vì ngoài đời hiếm khi host dừng ngay sau khi khởi động.

## 2026-10-17 — Tắt broker làm máy bị Offline oan qua chính LWT (M4 bước 5)

- **Triệu chứng (bắt khi chạy thật kịch bản của spec):** `docker compose stop mosquitto` trong 2 phút. Phần ân hạn của watchdog làm đúng: suốt lúc broker chết không có verdict nào. Nhưng **ngay giây broker dừng**, hai trong ba máy bị ghi `Offline` với lý do "status offline (LWT)". DoD cấm đúng điều này: "không có máy nào bị Offline oan khi chỉ broker chết".
- **Nguyên nhân:** khi tự shutdown, Mosquitto ngắt từng client và **phát LWT (will) của các client đó**. Ingest còn kết nối thêm vài mili giây, đủ để nhận will của hai máy. Code coi `offline` live là tin chắc chắn ("máy chết thật, broker báo hộ") và đổi state ngay. Máy thứ ba thoát vì ingest bị ngắt trước khi nhận will của nó, tức kết quả phụ thuộc vào thứ tự broker ngắt client.
- **Cách sửa:** `offline` live chỉ là **báo cáo** (`MachineRuntimeStore.ReportOffline`). `OfflineDetector` chỉ xác nhận sau 3 giây nếu (1) ingest vẫn giữ **cùng phiên kết nối** từ trước lúc nhận báo cáo (`ConnectedSince <= reportedAt`) và (2) máy không gửi gì từ lúc đó. Broker shutdown làm ingest mất kết nối ngay, sau khi nối lại thì phiên mới hơn báo cáo, nên báo cáo bị bỏ. Máy bị kill thật thì phiên của ingest không đổi, nên Offline được xác nhận sau khoảng 3–4 giây (watchdog chạy mỗi 1 giây thay vì 5 giây). Thêm 3 unit test với `FakeTimeProvider`: xác nhận khi kết nối còn; không tính khi broker shutdown (mù rồi nối lại); bỏ báo cáo khi máy nói lại.
- **Bài học:** "LWT = máy chết" chỉ đúng khi broker còn sống. Một tín hiệu do bên trung gian phát hộ phải được hiểu trong bối cảnh của chính bên trung gian đó. Kịch bản lỗi trong spec ("tắt broker 2 phút") đáng chạy thật: unit test của watchdog đều xanh mà vẫn không lường được hành vi lúc broker shutdown.

## 2026-10-28 — OPC UA server tự gọi chính nó mỗi vài giây (M4b bước 1)

- **Triệu chứng:** server OPC UA của simulator (lò reflow, cổng 4840) khởi động bình thường, client nối được, nhưng log cứ vài giây lại báo `RegisterServer2 failed for opc.tcp://localhost:4840/ ... Server does not support the requested security policy 'Basic256Sha256' and security mode 'SignAndEncrypt'`, với thời gian chờ thử lại tăng dần (2, 4, 8 giây...).
- **Nguyên nhân:** server mẫu của stack OPC Foundation mặc định **đăng ký với Local Discovery Server (LDS)** của máy, ở địa chỉ chuẩn `opc.tcp://localhost:4840`. 4840 là cổng IANA của OPC UA, dành cho LDS. Server của ta chiếm đúng cổng đó, nên nó gọi RegisterServer tới **chính nó**. Server chỉ mở endpoint `None`, trong khi lời gọi đăng ký đòi `SignAndEncrypt`, nên bị từ chối.
- **Cách tìm:** URL trong thông báo lỗi trùng endpoint của chính server; tra cấu hình `ServerConfiguration` thấy `RegistrationEndpoint` / `MaxRegistrationInterval`.
- **Cách sửa:** `MaxRegistrationInterval = 0` (tắt đăng ký LDS; simulator không cần discovery). Nhân tiện lọc log của stack xuống Warning: category log là tên class con `ReflowServer+OvenServer` của ta chứ không phải `Opc.Ua.*`, nên filter `"Opc.Ua"` không bắt được.
- **Bài học:** cổng "mặc định" của một giao thức có thể đã có chủ (4840 = LDS). Khi log báo lỗi tới một địa chỉ, đối chiếu xem địa chỉ đó có phải chính mình không. Ngoài nhà máy, PLC/máy thường dùng 4840 và **không** có LDS trên cùng thiết bị, nên lỗi này hay gặp với server tự viết chạy trên PC.

## 2026-10-28 — Máy dán keo báo 2,26 tỷ sản phẩm (M4b bước 3, cố tình tái hiện)

- **Triệu chứng:** máy `L1-GLUE-01` (Modbus TCP) đang ở bộ đếm 100.000, gateway đọc ra **2.258.632.705**; mỗi bo sau đó làm bộ đếm tăng **65.536** thay vì 1. Không có lỗi, không có exception, chất lượng `Good`.
- **Nguyên nhân:** bộ đếm uint32 nằm trên 2 thanh ghi 16-bit (40001–40002). PLC lưu **word thấp trước** (`CDAB`: HR0 = `0x86A0`, HR1 = `0x0001`), cấu hình gateway ghép theo `ABCD` (word cao trước), ra `0x86A0_0001`. Word thấp bị hiểu thành word cao nên mỗi đơn vị thật thành 65.536.
- **Cách tìm:** so với một giá trị đã biết (màn hình HMI / `list` của simulator: 100.000). Bước 65.536 = 2^16 giữa hai lần đọc là dấu hiệu chắc chắn của đảo word. Thử 4 kiểu `ABCD/CDAB/BADC/DCBA` đến khi khớp.
- **Cách sửa:** `"wordOrder": "CDAB"` trong `gateway.tags.json`, restart gateway. `RegisterDecoder` có unit test bảng cho cả 4 thứ tự.
- **Bài học:** với Modbus mọi cấu hình sai (địa chỉ lệch 1, FC03/FC04, thứ tự word, có dấu/không dấu) đều vẫn trả về **một con số**. Kiểm tra cấu hình mới bằng giá trị đã biết trước khi tin dữ liệu; ở M5 OEE sẽ bật "Performance 1.000.000 %" nếu lọt.

## 2026-10-28 — Switch expression âm thầm đổi bộ đếm uint32 thành float (M4b bước 3)

- **Triệu chứng:** unit test `Decode(...)` báo `Expected: 100000, Actual: 100000` mà vẫn fail.
- **Nguyên nhân:** `object Decode(...) => type switch { "uint16" => block[i], "uint32" => ToUInt32(...), "float32" => ToFloat(...), ... }`. C# chọn **kiểu chung tốt nhất** cho cả switch expression: `ushort`, `short`, `uint`, `int` đều đổi ngầm sang `float`, nên mọi nhánh trả `float`, rồi mới boxing thành `object`. `Assert.Equal(100000u, (object)100000f)` khác kiểu nên fail, dù in ra giống nhau. Ngoài đời: float chỉ có 24 bit mantissa, bộ đếm 16.777.217 thành 16.777.216 → mất sản lượng lặt vặt, rất khó thấy.
- **Cách tìm:** "giá trị in ra giống nhau nhưng không bằng" → nghi kiểu dữ liệu; kiểm tra `value.GetType()`.
- **Cách sửa:** ép từng nhánh `(object)...`; thêm test hồi quy `A_large_counter_keeps_every_digit_and_its_type` (2^24 + 1, `Assert.IsType<uint>`).
- **Bài học:** switch expression/toán tử `?:` có kiểu tự suy; khi kết quả là `object`, kiểu "tự nhiên" vẫn được chọn trước khi boxing. Test nên kiểm cả kiểu khi kiểu là một phần của hợp đồng.

## 2026-10-28 — Gateway không nối được Modbus slave ở `localhost` dù slave đang chạy (M4b bước 3)

- **Triệu chứng:** `Modbus L1-GLUE-01 at localhost:5020: connect timed out after 2000 ms` lặp mãi; simulator báo slave đang nghe cổng 5020.
- **Nguyên nhân:** slave nghe `IPAddress.Any` (chỉ IPv4). `localhost` phân giải ra `::1` (IPv6) trước; trên Windows, kết nối loopback tới cổng không ai nghe bị từ chối sau khoảng **2 giây** (hệ điều hành gửi lại SYN), đúng bằng timeout kết nối của gateway, nên client chưa kịp thử `127.0.0.1`.
- **Cách sửa:** slave dùng `TcpListener.Create(port)` (dual mode IPv6 + IPv4).
- **Bài học:** "timeout đúng bằng con số mình cấu hình" là dấu hiệu bị cắt ngang chứ không phải mạng chậm. `localhost` ≠ `127.0.0.1`. Ngoài nhà máy cấu hình PLC bằng IP cụ thể, ít gặp; nhưng chạy trên máy dev thì gặp ngay.

## 2026-10-28 — Gửi bù đủ message nhưng sản lượng theo giờ vẫn sai (M4b bước 5, sửa ingest M4)

- **Triệu chứng:** dừng Mosquitto 87 giây, gateway gửi bù 66 message trong 2,9 giây, `SUM(Delta)` khớp PLC, không trùng, không reset giả. Nhưng trong 2 phút máy dán keo chỉ có **3** sự kiện count thay vì khoảng 11, và API log `Clock of L1-GLUE-01 is off ... the received time is used instead`.
- **Nguyên nhân:** luật M4 "ts lệch quá 60 s so với giờ nhận = đồng hồ PLC sai, dùng giờ nhận". Message đầu hàng đợi (cũ 87 s) bị dời sang **giờ nhận**; message kế tiếp (cũ 50 s, trong ngưỡng) giữ ts thật, mà ts đó lại **sớm hơn** mốc vừa dời, nên bị bỏ với lý do "count older than the last one processed". Bộ đếm tích lũy nên tổng vẫn đúng, nhưng chi tiết theo giờ (OEE theo giờ, dashboard "giờ này") dồn hết vào lúc nối lại.
- **Cách tìm:** kiểm tra không chỉ tổng (`SUM(Delta)`) mà cả **số dòng** và `MachineTs` từng dòng; đọc warning của API trong đúng khoảng thời gian gửi bù.
- **Cách sửa (user duyệt):** `MachineRuntimeStore.EventTime`: message từ gateway (`source` ≠ `mqtt`) có `ts` ở quá khứ tới 7 ngày (= tuổi tối đa của outbox) là **gửi muộn**, giữ `ts`; chỉ `ts` tương lai > 60 s hoặc cũ hơn 7 ngày mới là lệch giờ. Máy MQTT trực tiếp giữ luật cũ. Sau khi sửa: 9 count trong 70 s mất broker, mỗi cái đúng giờ máy, `Delta = 1`.
- **Bài học:** "lệch giờ" và "đến muộn" trông giống hệt nhau nếu chỉ nhìn `ts` vs giờ nhận; phải biết **đường đi** của message (trực tiếp hay qua hàng đợi). Hai tính năng đúng riêng lẻ (chống lệch giờ của M4, store-and-forward của M4b) có thể sai khi ghép lại: kiểm tra đầu-cuối bằng dữ liệu chi tiết, không chỉ bằng tổng.
