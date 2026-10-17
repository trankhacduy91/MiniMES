using System.Net;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.Resources;
using MiniMes.Client.Core.ViewModels.MasterData;
using MiniMes.Contracts.MasterData;

namespace MiniMes.UnitTests.Client;

// The generic catalog screen (through three of its catalogs) and the Excel import screen.
public class CatalogAndImportViewModelTests
{
    private sealed class Dialogs(bool confirm = true, string? openPath = null) : IDialogService
    {
        public int Asked { get; private set; }
        public bool Confirm(string message) { Asked++; return confirm; }
        public string? PickFileToOpen(string filter) => openPath;
        public string? PickFileToSave(string defaultFileName, string filter) => null;
        public string? AskText(string title, string prompt) => null;
    }

    private sealed class CatalogApi : FakeMasterDataApi
    {
        public List<DowntimeReasonDto> Reasons { get; } = [new(1, "MCH-FAULT", "Machine fault", "Unplanned", true, true)];
        public CreateDowntimeReasonRequest? CreatedReason { get; private set; }
        public CreateShiftRequest? CreatedShift { get; private set; }
        public (DateOnly Date, SetPlantCalendarDayRequest Request)? SetDay { get; private set; }
        public List<DateOnly> RemovedDays { get; } = [];
        public Exception? Fail { get; set; }

        public override Task<IReadOnlyList<DowntimeReasonDto>> GetDowntimeReasonsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DowntimeReasonDto>>(Reasons);

        public override Task<DowntimeReasonDto> CreateDowntimeReasonAsync(CreateDowntimeReasonRequest request, CancellationToken ct = default)
        {
            CreatedReason = request;
            return Fail is null ? Task.FromResult(Reasons[0]) : Task.FromException<DowntimeReasonDto>(Fail);
        }

        public override Task<IReadOnlyList<ShiftDto>> GetShiftsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ShiftDto>>([]);

        public override Task<ShiftDto> CreateShiftAsync(CreateShiftRequest request, CancellationToken ct = default)
        {
            CreatedShift = request;
            return Task.FromResult<ShiftDto>(null!);
        }

        public List<(DateOnly From, DateOnly To)> CalendarRanges { get; } = [];

        // Same limit as the API's GetPlantCalendarValidator: a longer range is a 400 there.
        public override Task<IReadOnlyList<PlantCalendarDayDto>> GetPlantCalendarAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
        {
            if (to.DayNumber - from.DayNumber > 366)
                return Task.FromException<IReadOnlyList<PlantCalendarDayDto>>(new ApiException(HttpStatusCode.BadRequest, "Ask for at most one year at a time."));
            CalendarRanges.Add((from, to));
            return Task.FromResult<IReadOnlyList<PlantCalendarDayDto>>(from.Year == 2026 ? [new(new DateOnly(2026, 4, 30), false, null, "Reunification Day")] : []);
        }

        public override Task<PlantCalendarDayDto> SetPlantCalendarDayAsync(DateOnly date, SetPlantCalendarDayRequest request, CancellationToken ct = default)
        {
            SetDay = (date, request);
            return Task.FromResult(new PlantCalendarDayDto(date, request.IsWorkingDay, request.ShiftCodes, request.Note));
        }

        public override Task RemovePlantCalendarDayAsync(DateOnly date, CancellationToken ct = default)
        {
            RemovedDays.Add(date);
            return Task.CompletedTask;
        }
    }

    // A clock fixed in time (no extra package needed for one value).
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static CatalogField F(CatalogViewModel vm, string key) => vm.Fields.Single(f => f.Key == key);

    // ---------- generic catalog ----------

    [Fact]
    public async Task New_form_is_opened_and_save_sends_the_fields()
    {
        var api = new CatalogApi();
        var vm = new DowntimeReasonsViewModel(api, new Dialogs());
        await vm.LoadAsync();

        await vm.NewCommand.ExecuteAsync(null);
        F(vm, "Code").Text = "pm";
        F(vm, "Name").Text = " Preventive maintenance ";
        F(vm, "Category").Text = "Planned";
        F(vm, "CountsAgainstAvailability").Flag = false;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(new CreateDowntimeReasonRequest("PM", "Preventive maintenance", "Planned", false), api.CreatedReason);
        Assert.False(vm.IsEditing);
        Assert.Single(vm.Rows);
    }

    [Fact]
    public async Task Server_400_lands_on_the_matching_field_and_the_form_stays_open()
    {
        var api = new CatalogApi
        {
            Fail = new ApiException(HttpStatusCode.BadRequest, "x", fieldErrors: new Dictionary<string, string[]>
            {
                ["CountsAgainstAvailability"] = ["Unplanned downtime always counts against availability."],
            }),
        };
        var vm = new DowntimeReasonsViewModel(api, new Dialogs());
        await vm.NewCommand.ExecuteAsync(null);
        F(vm, "Code").Text = "X";
        F(vm, "Name").Text = "x";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Unplanned downtime always counts against availability.", F(vm, "CountsAgainstAvailability").Error);
        Assert.True(vm.IsEditing);
    }

    [Fact]
    public async Task Duplicate_code_409_is_shown_on_the_code_field()
    {
        var api = new CatalogApi { Fail = new ApiException(HttpStatusCode.Conflict, "Downtime reason 'PM' already exists.") };
        var vm = new DowntimeReasonsViewModel(api, new Dialogs());
        await vm.NewCommand.ExecuteAsync(null);
        F(vm, "Code").Text = "PM";
        F(vm, "Name").Text = "x";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Downtime reason 'PM' already exists.", F(vm, "Code").Error);
    }

    [Fact]
    public async Task Editing_locks_the_code_and_deactivate_is_off_for_inactive_rows()
    {
        var api = new CatalogApi();
        api.Reasons.Add(new(2, "OLD", "Old", "Planned", false, false));
        var vm = new DowntimeReasonsViewModel(api, new Dialogs());
        await vm.LoadAsync();

        vm.Selected = vm.Rows[0];
        await vm.EditCommand.ExecuteAsync(null);
        Assert.True(F(vm, "Code").IsReadOnly);
        Assert.True(vm.RemoveCommand.CanExecute(null));

        vm.Selected = vm.Rows[1];
        Assert.False(vm.RemoveCommand.CanExecute(null));
    }

    [Fact]
    public async Task Shift_times_and_breaks_are_parsed_on_the_client()
    {
        var api = new CatalogApi();
        var vm = new ShiftsViewModel(api, new Dialogs());
        await vm.NewCommand.ExecuteAsync(null);
        F(vm, "Code").Text = "s3";
        F(vm, "Name").Text = "Night";
        F(vm, "StartTime").Text = "22:00";
        F(vm, "EndTime").Text = "6:00";
        F(vm, "Days").Text = "Mon, Tue";
        F(vm, "Breaks").Text = "01:00-01:45, 03:30-03:45";

        await vm.SaveCommand.ExecuteAsync(null);

        var s = api.CreatedShift!;
        Assert.Equal(("S3", new TimeOnly(22, 0), new TimeOnly(6, 0)), (s.Code, s.StartTime, s.EndTime));
        Assert.Equal(["Mon", "Tue"], s.Days);
        Assert.Equal([new ShiftBreakDto(new(1, 0), new(1, 45)), new ShiftBreakDto(new(3, 30), new(3, 45))], s.Breaks);
    }

    [Theory]
    [InlineData("StartTime", "6h")]
    [InlineData("Breaks", "10:00 to 10:15")]
    public async Task Unreadable_shift_input_is_flagged_and_nothing_is_sent(string field, string text)
    {
        var api = new CatalogApi();
        var vm = new ShiftsViewModel(api, new Dialogs());
        await vm.NewCommand.ExecuteAsync(null);
        F(vm, field).Text = text;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.NotEmpty(F(vm, field).Error);
        Assert.Null(api.CreatedShift);
    }

    [Fact]
    public async Task Calendar_lists_this_and_next_year_and_remove_deletes_after_asking()
    {
        var api = new CatalogApi();
        var dialogs = new Dialogs();
        var vm = new PlantCalendarViewModel(api, dialogs, new FixedTime(new DateTimeOffset(2026, 10, 17, 8, 0, 0, TimeSpan.Zero)));
        await vm.LoadAsync();

        vm.Selected = vm.Rows.Single();
        await vm.RemoveCommand.ExecuteAsync(null);

        Assert.Equal([(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)), (new DateOnly(2027, 1, 1), new DateOnly(2027, 12, 31))],
            api.CalendarRanges.Take(2));                         // two one-year calls: the API refuses more
        Assert.Equal(Strings.Common_Remove, vm.RemoveLabel);
        Assert.Equal(1, dialogs.Asked);
        Assert.Equal([new DateOnly(2026, 4, 30)], api.RemovedDays);
    }

    [Fact]
    public async Task Calendar_save_is_an_upsert_with_empty_boxes_sent_as_null()
    {
        var api = new CatalogApi();
        var vm = new PlantCalendarViewModel(api, new Dialogs(), new FixedTime(new DateTimeOffset(2026, 10, 17, 8, 0, 0, TimeSpan.Zero)));
        await vm.NewCommand.ExecuteAsync(null);
        F(vm, "Date").Text = "2026-10-18";
        F(vm, "IsWorkingDay").Flag = true;
        F(vm, "ShiftCodes").Text = " ";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal((new DateOnly(2026, 10, 18), true, (string?)null, (string?)null),
            (api.SetDay!.Value.Date, api.SetDay.Value.Request.IsWorkingDay, api.SetDay.Value.Request.ShiftCodes, api.SetDay.Value.Request.Note));
    }

    // ---------- import ----------

    private sealed class ImportApi(params ImportErrorDto[] errors) : FakeMasterDataApi
    {
        public List<(bool DryRun, byte[] Content)> Calls { get; } = [];

        public override Task<ImportResultDto> ImportMasterDataAsync(string fileName, byte[] content, bool dryRun, CancellationToken ct = default)
        {
            Calls.Add((dryRun, content));
            return Task.FromResult(new ImportResultDto(dryRun, !dryRun && errors.Length == 0, 5, 2, 1, errors));
        }
    }

    [Fact]
    public async Task Import_is_enabled_only_after_the_file_validated_without_errors()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        var api = new ImportApi();
        var vm = new ImportViewModel(api, new Dialogs(openPath: path));

        Assert.False(vm.ValidateCommand.CanExecute(null));      // no file yet
        vm.PickFileCommand.Execute(null);
        Assert.False(vm.ImportCommand.CanExecute(null));        // not validated yet
        await vm.ValidateCommand.ExecuteAsync(null);
        Assert.True(vm.ImportCommand.CanExecute(null));

        await File.WriteAllBytesAsync(path, [9, 9, 9]);          // file edited after Validate
        await vm.ImportCommand.ExecuteAsync(null);

        Assert.Equal([1, 2, 3], api.Calls[1].Content);           // what was checked is what is imported
        Assert.Equal((true, false), (api.Calls[0].DryRun, api.Calls[1].DryRun));
        Assert.False(vm.ImportCommand.CanExecute(null));        // one import per validation
        File.Delete(path);
    }

    [Fact]
    public async Task Validation_errors_fill_the_grid_and_keep_import_off()
    {
        var path = Path.GetTempFileName();
        var vm = new ImportViewModel(new ImportApi(new ImportErrorDto("Items", 5, "Uom", "Unit must be one of: pcs, kg, g, m.")),
            new Dialogs(openPath: path));
        vm.PickFileCommand.Execute(null);

        await vm.ValidateCommand.ExecuteAsync(null);

        Assert.Equal(("Items", 5, "Uom"), (vm.Errors.Single().Sheet, vm.Errors.Single().Row, vm.Errors.Single().Column));
        Assert.False(vm.ImportCommand.CanExecute(null));
        Assert.Equal(string.Format(Strings.Import_ValidErrors, 1), vm.ResultMessage);
        File.Delete(path);
    }
}
