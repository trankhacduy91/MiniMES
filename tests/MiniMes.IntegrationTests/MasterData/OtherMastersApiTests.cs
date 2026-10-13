using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MiniMes.Contracts.Audit;
using MiniMes.Contracts.Common;
using MiniMes.Contracts.Factory;
using MiniMes.Contracts.MasterData;
using MiniMes.IntegrationTests.Infrastructure;

namespace MiniMes.IntegrationTests.MasterData;

// Spec M1 step 7: the remaining masters follow the Item pattern; these tests cover what is specific to each.
[Collection(ApiCollection.Name)]
public class OtherMastersApiTests(ApiFixture api)
{
    private static TimeOnly T(int h, int m = 0) => new(h, m);

    // Short and unique: shift codes are at most 10 characters; the test database is shared.
    private static string ShortCode(string prefix) => prefix + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    // A date no other test uses (PlantCalendar has one row per date).
    private static DateOnly UniqueDate(DayOfWeek day)
    {
        var d = new DateOnly(2100, 1, 1).AddDays(Random.Shared.Next(0, 300 * 52) * 7);
        while (d.DayOfWeek != day) d = d.AddDays(1);
        return d;
    }

    // ---------- Work center + Machine ----------

    [Fact]
    public async Task Machine_with_mqtt_separator_in_its_topic_key_is_400()
    {
        var admin = await api.LoginAsync("admin");
        var wc = await CreateWorkCenterAsync(admin);

        var response = await admin.PostAsJsonAsync("/api/machines",
            new CreateMachineRequest(wc.Id, ApiFixture.Unique("M"), "Pick and place", "PickAndPlace", "L1/SMT", "Mqtt"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("MqttTopicKey", (await ReadErrorsAsync(response)).Keys);
    }

    [Fact]
    public async Task Two_machines_on_one_topic_key_is_409()
    {
        var admin = await api.LoginAsync("admin");
        var wc = await CreateWorkCenterAsync(admin);
        var key = ApiFixture.Unique("KEY");
        (await admin.PostAsJsonAsync("/api/machines",
            new CreateMachineRequest(wc.Id, ApiFixture.Unique("M"), "First", "PickAndPlace", key, "Mqtt"))).EnsureSuccessStatusCode();

        var response = await admin.PostAsJsonAsync("/api/machines",
            new CreateMachineRequest(wc.Id, ApiFixture.Unique("M"), "Second", "PickAndPlace", key, "OpcUa"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Machine_update_keeps_its_topic_key_and_unknown_data_source_is_400()
    {
        var admin = await api.LoginAsync("admin");
        var wc = await CreateWorkCenterAsync(admin);
        var created = await (await admin.PostAsJsonAsync("/api/machines",
                new CreateMachineRequest(wc.Id, ApiFixture.Unique("M"), "Reflow", "ReflowOven", ApiFixture.Unique("K"), "OpcUa")))
            .Content.ReadFromJsonAsync<MachineDto>();

        var updated = await (await admin.PutAsJsonAsync($"/api/machines/{created!.Id}",
            new UpdateMachineRequest("Reflow oven 2", "ReflowOven", "Modbus"))).Content.ReadFromJsonAsync<MachineDto>();
        var bad = await admin.PutAsJsonAsync($"/api/machines/{created.Id}", new UpdateMachineRequest("x", "x", "Profinet"));

        Assert.Equal(("Reflow oven 2", "Modbus", created.MqttTopicKey), (updated!.Name, updated.DataSource, updated.MqttTopicKey));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Deactivated_work_center_leaves_the_active_list_and_takes_no_new_machine()
    {
        var admin = await api.LoginAsync("admin");
        var wc = await CreateWorkCenterAsync(admin);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/work-centers/{wc.Id}/deactivate", null)).StatusCode);

        var active = await admin.GetFromJsonAsync<List<WorkCenterDto>>("/api/work-centers?isActive=true");
        Assert.DoesNotContain(active!, w => w.Id == wc.Id);
        var machine = await admin.PostAsJsonAsync("/api/machines",
            new CreateMachineRequest(wc.Id, ApiFixture.Unique("M"), "x", "x", ApiFixture.Unique("K"), "Mqtt"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, machine.StatusCode);
    }

    [Fact]
    public async Task Operator_reads_masters_but_cannot_write()
    {
        var op = await api.LoginOperatorAsync();

        Assert.Equal(HttpStatusCode.OK, (await op.GetAsync("/api/downtime-reasons")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await op.PostAsJsonAsync("/api/defect-codes", new CreateDefectCodeRequest("X", "x", "Solder"))).StatusCode);
    }

    // ---------- Station.WorkCenterId ----------

    [Fact]
    public async Task Station_gets_a_work_center_of_its_own_line_only()
    {
        var admin = await api.LoginAsync("admin");
        var lines = (await admin.GetFromJsonAsync<List<LineDto>>("/api/lines"))!.Where(l => l.IsActive).OrderBy(l => l.Id).ToList();
        var wcLine1 = await CreateWorkCenterAsync(admin, lines[0].Id);
        var wcLine2 = await CreateWorkCenterAsync(admin, lines[1].Id);

        var created = await admin.PostAsJsonAsync("/api/stations", new CreateStationRequest
            { LineId = lines[0].Id, Code = ApiFixture.Unique("ST"), Name = "Test bench", Type = "Test", WorkCenterId = wcLine1.Id });
        var station = (await created.Content.ReadFromJsonAsync<StationDto>())!;
        var otherLine = await admin.PutAsJsonAsync($"/api/stations/{station.Id}",
            new UpdateStationRequest { Name = station.Name, Type = "Test", WorkCenterId = wcLine2.Id });
        var cleared = await (await admin.PutAsJsonAsync($"/api/stations/{station.Id}",
            new UpdateStationRequest { Name = station.Name, Type = "Repair" })).Content.ReadFromJsonAsync<StationDto>();

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(wcLine1.Id, station.WorkCenterId);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, otherLine.StatusCode);
        Assert.Null(cleared!.WorkCenterId);
    }

    // ---------- Shift ----------

    [Fact]
    public async Task Night_shift_S3_is_created_with_420_planned_minutes()
    {
        var admin = await api.LoginAsync("admin");

        var response = await admin.PostAsJsonAsync("/api/shifts", new CreateShiftRequest(ShortCode("N"), "Night", T(22), T(6),
            ["Mon", "Tue", "Wed", "Thu", "Fri"], [new(T(1), T(1, 45)), new(T(3, 30), T(3, 45))]));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var shift = (await response.Content.ReadFromJsonAsync<ShiftDto>())!;
        Assert.Equal((true, 480, 420), (shift.CrossesMidnight, shift.DurationMinutes, shift.PlannedProductionMinutes));
        Assert.Equal(["Mon", "Tue", "Wed", "Thu", "Fri"], shift.Days);
    }

    [Theory]
    [InlineData(15, 0, 15, 15, "Breaks")]   // outside 06:00-14:00
    [InlineData(10, 0, 10, 0, "Breaks")]    // zero length
    public async Task Bad_break_is_400_on_the_breaks_field(int h1, int m1, int h2, int m2, string field)
    {
        var admin = await api.LoginAsync("admin");

        var response = await admin.PostAsJsonAsync("/api/shifts", new CreateShiftRequest(ShortCode("B"), "Morning", T(6), T(14),
            ["Mon"], [new(T(h1, m1), T(h2, m2))]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field, (await ReadErrorsAsync(response)).Keys);
    }

    [Fact]
    public async Task Overlapping_breaks_on_update_are_400_and_the_shift_is_unchanged()
    {
        var admin = await api.LoginAsync("admin");
        var shift = await CreateShiftAsync(admin, ShortCode("U"), ["Mon"]);

        var response = await admin.PutAsJsonAsync($"/api/shifts/{shift.Id}", new UpdateShiftRequest("Morning", T(6), T(14),
            ["Mon"], [new(T(10), T(10, 30)), new(T(10, 15), T(10, 45))]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(420, (await admin.GetFromJsonAsync<ShiftDto>($"/api/shifts/{shift.Id}"))!.PlannedProductionMinutes);
    }

    // ---------- Plant calendar [v2] ----------

    [Fact]
    public async Task Holiday_stops_every_shift_and_removing_it_restores_the_week_pattern()
    {
        var admin = await api.LoginAsync("admin");
        var code = ShortCode("H");
        await CreateShiftAsync(admin, code, ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat"]);
        var thursday = UniqueDate(DayOfWeek.Thursday);

        (await admin.PutAsJsonAsync($"/api/plant-calendar/{thursday:yyyy-MM-dd}",
            new SetPlantCalendarDayRequest(false, null, "Reunification Day"))).EnsureSuccessStatusCode();
        var onHoliday = await ScheduledAsync(admin, thursday);
        (await admin.DeleteAsync($"/api/plant-calendar/{thursday:yyyy-MM-dd}")).EnsureSuccessStatusCode();
        var afterRemoval = await ScheduledAsync(admin, thursday);

        Assert.Empty(onHoliday.ShiftCodes);
        Assert.Contains(code, afterRemoval.ShiftCodes);
    }

    [Fact]
    public async Task Sunday_overtime_runs_only_the_listed_shift()
    {
        var admin = await api.LoginAsync("admin");
        var s1 = ShortCode("A");
        var s2 = ShortCode("B");
        await CreateShiftAsync(admin, s1, ["Mon"]);
        await CreateShiftAsync(admin, s2, ["Mon"]);
        var sunday = UniqueDate(DayOfWeek.Sunday);

        var set = await admin.PutAsJsonAsync($"/api/plant-calendar/{sunday:yyyy-MM-dd}",
            new SetPlantCalendarDayRequest(true, s1, "Overtime for order X"));
        var scheduled = await ScheduledAsync(admin, sunday);

        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Contains(s1, scheduled.ShiftCodes);
        Assert.DoesNotContain(s2, scheduled.ShiftCodes);
        var range = await admin.GetFromJsonAsync<List<PlantCalendarDayDto>>(
            $"/api/plant-calendar?from={sunday:yyyy-MM-dd}&to={sunday:yyyy-MM-dd}");
        Assert.Equal(s1, Assert.Single(range!).ShiftCodes);
    }

    [Fact]
    public async Task Unknown_shift_code_or_shifts_on_a_day_off_are_400()
    {
        var admin = await api.LoginAsync("admin");
        var date = UniqueDate(DayOfWeek.Sunday);

        var unknown = await admin.PutAsJsonAsync($"/api/plant-calendar/{date:yyyy-MM-dd}", new SetPlantCalendarDayRequest(true, "NOPE", null));
        var dayOff = await admin.PutAsJsonAsync($"/api/plant-calendar/{date:yyyy-MM-dd}", new SetPlantCalendarDayRequest(false, "S1", null));

        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, dayOff.StatusCode);
    }

    [Fact]
    public async Task Calendar_change_is_audited_under_its_date()
    {
        var admin = await api.LoginAsync("admin");
        var date = UniqueDate(DayOfWeek.Saturday);

        (await admin.PutAsJsonAsync($"/api/plant-calendar/{date:yyyy-MM-dd}", new SetPlantCalendarDayRequest(false, null, "Tet"))).EnsureSuccessStatusCode();

        var audit = await admin.GetFromJsonAsync<PagedResult<AuditLogDto>>($"/api/audit-logs?tableName=PlantCalendar&recordKey={date:yyyy-MM-dd}");
        Assert.Single(audit!.Items);
    }

    // ---------- Defect code + Downtime reason ----------

    [Fact]
    public async Task Defect_code_create_update_and_deactivate()
    {
        var admin = await api.LoginAsync("admin");
        var created = await (await admin.PostAsJsonAsync("/api/defect-codes",
            new CreateDefectCodeRequest(ApiFixture.Unique("SLD"), "Solder bridge", "Solder"))).Content.ReadFromJsonAsync<DefectCodeDto>();

        var updated = await (await admin.PutAsJsonAsync($"/api/defect-codes/{created!.Id}",
            new UpdateDefectCodeRequest("Solder bridge (short)", "Solder"))).Content.ReadFromJsonAsync<DefectCodeDto>();
        (await admin.PostAsync($"/api/defect-codes/{created.Id}/deactivate", null)).EnsureSuccessStatusCode();

        Assert.Equal("Solder bridge (short)", updated!.Name);
        Assert.False((await admin.GetFromJsonAsync<DefectCodeDto>($"/api/defect-codes/{created.Id}"))!.IsActive);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/defect-codes",
            new CreateDefectCodeRequest(created.Code, "dup", "Solder"))).StatusCode);
    }

    [Fact]
    public async Task Unplanned_downtime_that_does_not_count_is_400_planned_may_choose()
    {
        var admin = await api.LoginAsync("admin");

        var unplanned = await admin.PostAsJsonAsync("/api/downtime-reasons",
            new CreateDowntimeReasonRequest(ApiFixture.Unique("MCH"), "Machine fault", "Unplanned", false));
        var planned = await admin.PostAsJsonAsync("/api/downtime-reasons",
            new CreateDowntimeReasonRequest(ApiFixture.Unique("PM"), "Preventive maintenance", "Planned", false));

        Assert.Equal(HttpStatusCode.BadRequest, unplanned.StatusCode);
        Assert.Contains("CountsAgainstAvailability", (await ReadErrorsAsync(unplanned)).Keys);
        Assert.Equal(HttpStatusCode.Created, planned.StatusCode);
    }

    // ---------- helpers ----------

    private static async Task<WorkCenterDto> CreateWorkCenterAsync(HttpClient admin, int lineId = 1)
    {
        var response = await admin.PostAsJsonAsync("/api/work-centers",
            new CreateWorkCenterRequest(lineId, ApiFixture.Unique("WC"), "Work center", "SMT"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<WorkCenterDto>())!;
    }

    private static async Task<ShiftDto> CreateShiftAsync(HttpClient admin, string code, string[] days)
    {
        var response = await admin.PostAsJsonAsync("/api/shifts", new CreateShiftRequest(code, "Morning", T(6), T(14), days,
            [new(T(10), T(10, 15)), new(T(11, 30), T(12, 15))]));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ShiftDto>())!;
    }

    private static async Task<ScheduledShiftsDto> ScheduledAsync(HttpClient admin, DateOnly date)
        => (await admin.GetFromJsonAsync<ScheduledShiftsDto>($"/api/plant-calendar/{date:yyyy-MM-dd}/shifts"))!;

    private static async Task<Dictionary<string, string[]>> ReadErrorsAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("errors").Deserialize<Dictionary<string, string[]>>()!;
    }
}
