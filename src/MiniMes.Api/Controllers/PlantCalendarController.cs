using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MiniMes.Application.MasterData.Calendar;
using MiniMes.Contracts.MasterData;
using MiniMes.Domain.Users;

namespace MiniMes.Api.Controllers;

// [v2] Holidays and overtime days, keyed by production date (yyyy-MM-dd). PUT is an upsert.
[ApiController]
[Route("api/plant-calendar")]
public class PlantCalendarController(ISender sender) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<PlantCalendarDayDto>> List([FromQuery] DateOnly from, [FromQuery] DateOnly to, CancellationToken ct)
        => sender.Send(new GetPlantCalendarQuery(from, to), ct);

    // Which shifts run that day: the calendar row if there is one, else each shift's weekly days.
    [HttpGet("{date}/shifts")]
    public Task<ScheduledShiftsDto> ScheduledShifts(DateOnly date, CancellationToken ct)
        => sender.Send(new GetScheduledShiftsQuery(date), ct);

    [Authorize(Roles = nameof(Role.Admin))]
    [HttpPut("{date}")]
    public Task<PlantCalendarDayDto> Set(DateOnly date, SetPlantCalendarDayRequest r, CancellationToken ct)
        => sender.Send(new SetPlantCalendarDayCommand(date, r.IsWorkingDay, r.ShiftCodes, r.Note), ct);

    // An exception row is not referenced by other data, so removing it is allowed (audited).
    [Authorize(Roles = nameof(Role.Admin))]
    [HttpDelete("{date}")]
    public async Task<IActionResult> Remove(DateOnly date, CancellationToken ct)
    {
        await sender.Send(new RemovePlantCalendarDayCommand(date), ct);
        return NoContent();
    }
}
