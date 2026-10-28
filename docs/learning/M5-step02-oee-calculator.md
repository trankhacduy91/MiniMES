# M5 · Bước 2 — `OeeCalculator` (hàm thuần) và unit test: kiến thức cần nắm

> Tài liệu học cho bước 2 của [module-05](../../spec/module-05-oee-downtime.md) (spec mục **2.1**, quy tắc 1, 9, 12, 14).
> Bước trước: [M5-step01](M5-step01-oee-hand-calcs.md). Code: `src/MiniMes.Domain/Oee/OeeCalculator.cs`,
> test: `tests/MiniMes.UnitTests/Oee/OeeCalculatorTests.cs`.

---

## A. Vì sao một hàm thuần duy nhất

Nếu dashboard tính trong ViewModel, báo cáo tính trong SQL view, job tính trong service, thì sau vài tuần ba nơi ra ba
con số cho cùng một ca, và không chứng minh được nơi nào đúng. Cách của dự án là tách hai việc:

| Việc | Ở đâu | Test bằng |
| --- | --- | --- |
| Gom dữ liệu: đọc DB, cắt đoạn vào cửa sổ, cộng phút/số đếm | Infrastructure (bước 4) | Integration test (Testcontainers) |
| Tính A/P/Q/OEE từ các **tổng** | `OeeCalculator.Calculate` trong Domain | Unit test bằng số tính tay, chạy trong mili giây |

Hàm thuần = chỉ phụ thuộc đầu vào: không đọc DB, không gọi `DateTime.UtcNow`, không log. Ca đang chạy thì bên ngoài
cắt cửa sổ đến "bây giờ" (`TimeProvider`) rồi đưa số vào; hàm không tự hỏi giờ.

## B. Thiết kế kiểu dữ liệu

```csharp
public readonly record struct ProductionSegment(double IdealCycleSec, long TotalCount);   // một WO
public sealed record OeeInput(TimeSpan ShiftTime, TimeSpan BreakTime, TimeSpan PlannedStop,
    TimeSpan UnplannedDowntime, IReadOnlyList<ProductionSegment> Segments, long GoodCount);
[Flags] public enum OeeWarning { None = 0, PerformanceOver100 = 1, GoodExceedsTotal = 2, DowntimeExceedsPlanned = 4 }
```

- **Thời gian là `TimeSpan`**, không phải `int` phút: tránh trộn phút với giây. `TimeSpan / TimeSpan` trả `double`
  (có từ .NET Core 2.0), nên `A = run / planned` đọc như công thức.
- **Đếm là `long`, tỷ lệ là `double`**: OEE là số thống kê, không phải tiền, không cần `decimal`.
- **`double?` cho tỷ lệ**: `null` = "không xác định" (mẫu số 0), khác 0 %. Giờ nghỉ trưa có Planned = 0: hiện "0 %" là
  vu oan cho máy, ném exception thì job chốt chết (quy tắc 14).
- **`[Flags] enum` cho cảnh báo**: một kết quả có thể vừa P > 100 % vừa Good > Total; gộp bằng `|=`, kiểm bằng
  `HasFlag`. Lưu xuống DB là một cột `int`.
- **`OeeResult` trả cả các tổng** (Planned, Run, Total, Good, IdealSeconds), không chỉ tỷ lệ: tổng hợp nhiều giờ phải
  cộng các tổng rồi tính lại (quy tắc 12), nên snapshot bước 5 cần chúng.

## C. Các nhánh của hàm

| Tình huống | A | P | Q | OEE | Cờ |
| --- | --- | --- | --- | --- | --- |
| Planned ≤ 0 (giờ nghỉ) | null | null | null | null | — |
| Downtime > Planned | 0 | null (Run = 0) | | 0 | `DowntimeExceedsPlanned` |
| Có Planned, máy chưa đếm | x | 0 | null (Total = 0) | 0 | — |
| P > 1 | | **giữ thô** | | | `PerformanceOver100` |
| Good > Total | | | **giữ thô** (1,01) | | `GoodExceedsTotal` |

"Có thời gian kế hoạch mà không làm ra gì" là OEE = 0 (máy thật sự lãng phí), khác với giờ nghỉ (không có gì để chấm).

## D. Ba kiểu test

1. **Ví dụ tính tay** (`[Fact]` từng dòng bảng spec 2.1 + 3 ca tự bịa ở bước 1). So `double` bằng
   `Assert.Equal(expected, actual, precision)`: `Assert.Equal(0.8889, 0.88888…)` không có precision sẽ đỏ.
2. **Test tính chất (property/invariant)**: 1.000 bộ đầu vào ngẫu nhiên (seed cố định để lặp lại được), kiểm
   `A × P × Q = Ideal × (Good/Total) / Planned`. Không cần nghĩ ra ca cụ thể; công thức sai ở đâu cũng lộ. (Có thư viện
   chuyên cho việc này như FsCheck/CsCheck; ở đây một vòng `for` với `Random(seed)` là đủ, không thêm package.)
3. **Kiểm tra test bằng đột biến**: đổi `a * pp * qq` thành `a + pp + qq` → **6/10 test đỏ** (spec đòi ≥ 3). Bốn test
   còn xanh là các nhánh không đi qua phép nhân (giờ nghỉ, chưa đếm, downtime > planned, Good > Total chỉ kiểm Q).

### Sửa nhỏ spec

Spec ghi test tính chất là `A×P×Q = ideal × good / planned`. Với nhiều WO đẳng thức này sai (ca c ở bước 1: Ideal =
18.000 s ứng với 800 bo, không phải 785). Dạng đúng: `ideal × (good / total) / planned`. Đã sửa trong spec.

## E. Bẫy C# đáng nhớ

- `good / total` với hai `long` → chia nguyên ra 0. Ép `(double)` trước khi chia.
- `p > 1.0` khi `p` là `double?`: so sánh lifted, `null > 1.0` là `false`, không ném lỗi.
- `p is { } pp && q is { } qq`: pattern lấy giá trị ra khỏi `Nullable` trong một biểu thức.

## F. Câu hỏi phỏng vấn

1. **"OEE của anh tính ở đâu?"** Một hàm thuần trong Domain; dashboard, job snapshot, báo cáo Excel đều gọi nó. Test
   bằng số tính tay của ca mẫu (405/360/1.000/970 → 79,84 %).
2. **"Giờ nghỉ trưa thì OEE bao nhiêu?"** Không xác định (`null`), không phải 0 %. Hiển thị "—".
3. **"P 130 % thì anh làm gì?"** Giữ số thô, gắn cờ, dashboard hiện "check master data". Cắt về 100 % sẽ che lỗi
   cycle time.
4. **"Làm sao biết test của anh có giá trị?"** Đột biến code (đổi `*` thành `+`) và xem test đỏ; thêm test tính chất
   với dữ liệu ngẫu nhiên.

## G. Đọc thêm

- Microsoft Learn: "TimeSpan.Division operator", "Nullable value types — lifted operators", "FlagsAttribute".
- xUnit: `Assert.Equal(double, double, int precision)`.
- Bài "Property-based testing" (khái niệm; FsCheck cho .NET).
