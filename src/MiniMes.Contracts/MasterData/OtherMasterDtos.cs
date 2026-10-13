namespace MiniMes.Contracts.MasterData;

// Small masters (a few dozen rows each): lists are not paged. Code is set once and cannot change;
// "delete" is POST {id}/deactivate (history keeps referring to the row).

// ---------- Work center ----------

public sealed record WorkCenterDto(int Id, int LineId, string Code, string Name, string Type, bool IsActive);
public sealed record CreateWorkCenterRequest(int LineId, string Code, string Name, string Type);
public sealed record UpdateWorkCenterRequest(string Name, string Type);

// ---------- Machine ----------

// DataSource: Mqtt | OpcUa | Modbus (display only; every machine reaches the MES over MQTT).
public sealed record MachineDto(
    int Id, int WorkCenterId, string Code, string Name, string Type, string MqttTopicKey, string DataSource, bool IsActive);
public sealed record CreateMachineRequest(
    int WorkCenterId, string Code, string Name, string Type, string MqttTopicKey, string DataSource);
public sealed record UpdateMachineRequest(string Name, string Type, string DataSource);

// ---------- Shift ----------

// Times are factory local time ("06:00"), not UTC instants. Days: Mon, Tue, ... Sun.
public sealed record ShiftBreakDto(TimeOnly StartTime, TimeOnly EndTime);
public sealed record ShiftDto(
    int Id, string Code, string Name, TimeOnly StartTime, TimeOnly EndTime, IReadOnlyList<string> Days,
    bool CrossesMidnight, int DurationMinutes, int PlannedProductionMinutes,
    IReadOnlyList<ShiftBreakDto> Breaks, bool IsActive);
public sealed record CreateShiftRequest(
    string Code, string Name, TimeOnly StartTime, TimeOnly EndTime, IReadOnlyList<string> Days,
    IReadOnlyList<ShiftBreakDto> Breaks);
public sealed record UpdateShiftRequest(
    string Name, TimeOnly StartTime, TimeOnly EndTime, IReadOnlyList<string> Days, IReadOnlyList<ShiftBreakDto> Breaks);

// ---------- Plant calendar [v2] ----------

// Only exceptions to Shift.Days: holidays (IsWorkingDay=false) and overtime days (true, optional ShiftCodes "S1,S2").
public sealed record PlantCalendarDayDto(DateOnly Date, bool IsWorkingDay, string? ShiftCodes, string? Note);
public sealed record SetPlantCalendarDayRequest(bool IsWorkingDay, string? ShiftCodes, string? Note);
public sealed record ScheduledShiftsDto(DateOnly Date, IReadOnlyList<string> ShiftCodes);

// ---------- Defect code ----------

public sealed record DefectCodeDto(int Id, string Code, string Name, string Category, bool IsActive);
public sealed record CreateDefectCodeRequest(string Code, string Name, string Category);
public sealed record UpdateDefectCodeRequest(string Name, string Category);

// ---------- Downtime reason ----------

// Category: Planned | Unplanned. Unplanned always counts against availability (spec M1 1.4 rule 12).
public sealed record DowntimeReasonDto(
    int Id, string Code, string Name, string Category, bool CountsAgainstAvailability, bool IsActive);
public sealed record CreateDowntimeReasonRequest(string Code, string Name, string Category, bool CountsAgainstAvailability);
public sealed record UpdateDowntimeReasonRequest(string Name, string Category, bool CountsAgainstAvailability);
