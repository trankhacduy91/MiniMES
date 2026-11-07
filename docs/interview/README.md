# Lộ trình học vận hành MES qua MiniMES (8 tuần)

Mục tiêu: đủ kiến thức để phỏng vấn vị trí **IT MES (vận hành/support)** ở khu công nghiệp, học bằng chính hệ thống MiniMES. Mỗi tuần khoảng 10 giờ.

| Tuần | Chủ đề | Module | File |
| --- | --- | --- | --- |
| 1 | Bức tranh nhà máy, ISA-95, vai trò IT MES | M0 | [week-01.md](week-01.md) |
| 2 | Master data, Work Order, ECN | M1, M2 | [week-02.md](week-02.md) |
| 3 | Shop floor execution, đóng gói: bảng tra reason code | M3, M3b | [week-03.md](week-03.md) |
| 4 | Kết nối máy (MQTT, OPC UA, Modbus), Andon | M4, M4b | [week-04.md](week-04.md) |
| 5 | OEE và downtime | M5 | [week-05.md](week-05.md) |
| 6 | Vật tư và truy xuất: "khách khiếu nại trong 2 giờ" | M6 | [week-06.md](week-06.md) |
| 7 | Chất lượng: SPC, NCR, FAI, Hold/Release | M7 | [week-07.md](week-07.md) |
| 8 | Video demo, STAR, glossary, mock interview, CV | tất cả | [week-08.md](week-08.md) |

## Mỗi tuần gồm

| Khối | Giờ | Nội dung |
| --- | --- | --- |
| 0. Ngoài đời | — | Việc này diễn ra thế nào ở nhà máy thật, ai làm, ticket hay gặp |
| 1. Đọc | 3 | Trỏ đúng mục trong spec, ADR và tài liệu học |
| 2. Lab | 3 | Chạy hệ thống, lệnh cụ thể, SQL trên DB thật |
| 3. Drill | 2 | Tự gây lỗi rồi điều tra như IT MES trực ca; đáp án ẩn |
| 4. Câu hỏi | 2 | Trả lời thành tiếng, ghi âm, rồi mới mở đáp án |
| 5. Tự kiểm | — | Điều kiện để qua tuần |

## Lưu ý

- Mọi câu SQL trong các file đã chạy thử trên DB Development ngày 04/11/2026.
  - Bảng WO, serial, scan, đóng gói và chất lượng lưu trạng thái dạng **chuỗi**.
  - Bảng máy và `MaterialLots` lưu dạng **số**; mỗi file có ghi bảng mã.
- Drill có bước "tạo lỗi": chỉ làm trên máy dev, và **nhớ hoàn tác** (ví dụ `wordOrder` trong `gateway.tags.json`).
- Tài liệu chi tiết của từng bước code nằm ở [`docs/learning/`](../learning/), các sự cố ở [`docs/incidents.md`](../incidents.md).
