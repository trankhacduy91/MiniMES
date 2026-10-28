# OPC UA & Modbus — 10 dòng ghi nhớ (M4b Bước 0)

Kết quả từ `docs/learning/labs/opcua-lab.cs` (chạy với container OPC PLC) và `docs/learning/labs/modbus-lab.cs`, ngày 28/10/2026.

1. **NodeId** là định danh của một node trong address space, gồm namespace index và định danh: `ns=3;s=SpikeData` (chuỗi) hay `i=2253` (số, ns=0 là namespace chuẩn của OPC Foundation).
2. **Namespace index không cố định**: cùng server OPC PLC, namespace `http://microsoft.com/Opc/OpcPlc/` nằm ở `ns=3`. Server khác hoặc firmware mới có thể đổi số, nên client lưu URI rồi tra index lúc kết nối (`session.NamespaceUris.GetIndex`).
3. Mỗi giá trị đọc được là một **DataValue**: value + kiểu (`Double`, `UInt32`) + **StatusCode** (`Good`/`Uncertain`/`Bad...`) + **SourceTimestamp** (lúc đo) + **ServerTimestamp** (lúc server ghi nhận).
4. **SamplingInterval** = server lấy mẫu nguồn bao lâu một lần (500 ms); **PublishingInterval** = server gom các thay đổi và gửi cho client bao lâu một lần (1000 ms). Sampling ≤ Publishing, các mẫu chờ trong `QueueSize`.
5. **Deadband** do **server** lọc: cùng node `SpikeData` trong 15 giây, không deadband nhận 43 notification, deadband tuyệt đối 60 chỉ còn 17. Client không phải tự lọc.
6. **Subscription** = server đẩy khi đổi; **Modbus polling** = master hỏi theo chu kỳ, slave không bao giờ tự gửi.
7. Tài liệu máy ghi **40001** = holding register (vùng `4xxxx`, đếm từ 1) → **địa chỉ 0 trên dây**, đọc bằng **FC03**. Đọc lệch 1 (địa chỉ 1) ra `0x0001 0x0001` → 65.537, sai mà trông vẫn "hợp lý". Đọc nhầm FC04 (input register) ra toàn 0.
8. Một register chỉ 16-bit; số 32-bit (uint32, float) nằm trên **2 register liên tiếp** và phải ghép theo một thứ tự.
9. **CDAB** = word thấp đứng trước (đảo word). Bộ đếm 100.000 = `0x0001_86A0` lưu thành `0x86A0 0x0001`; đọc đúng CDAB ra 100.000, đọc nhầm ABCD ra **2.258.632.705** ("sản lượng 2 tỷ"), BADC ra 2.693.136.640, DCBA ra 16.818.310.
10. Float cũng vậy: `0x42F6 0xE979` đọc ABCD = 123,456, đọc CDAB = −1,88·10²⁵. Cách tìm thứ tự đúng: lấy một giá trị đã biết trên HMI của máy, thử cả 4 kiểu đến khi khớp.
