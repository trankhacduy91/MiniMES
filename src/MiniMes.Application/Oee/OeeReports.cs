using ClosedXML.Excel;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Options;
using MiniMes.Application.Common;
using MiniMes.Application.Oee.Downtime;
using MiniMes.Contracts.Oee;

namespace MiniMes.Application.Oee;

// [v2] Spec M5 2.7: Excel built on the server from the same queries as the dashboard, so the numbers match.
// Real numbers with a cell format (0.0 %), never "88.9%" as text: the reader can still calculate with them.
// Times in plant time, said so in the title. A Definitions sheet: the file is still readable in three months.
public sealed record ExportOeeReportQuery(DateOnly From, DateOnly To, string? Line) : IRequest<byte[]>;

public sealed record ExportParetoReportQuery(DateOnly From, DateOnly To, string? Line, string By = ParetoBy.Minutes) : IRequest<byte[]>;

public sealed class ExportOeeReportValidator : AbstractValidator<ExportOeeReportQuery>
{
    public ExportOeeReportValidator()
        => RuleFor(x => x.To).GreaterThanOrEqualTo(x => x.From)
            .Must((x, to) => to.DayNumber - x.From.DayNumber < OeeWorkbook.MaxDays).WithMessage($"At most {OeeWorkbook.MaxDays} days.");
}

public sealed class ExportParetoReportValidator : AbstractValidator<ExportParetoReportQuery>
{
    public ExportParetoReportValidator()
    {
        RuleFor(x => x.To).GreaterThanOrEqualTo(x => x.From)
            .Must((x, to) => to.DayNumber - x.From.DayNumber < OeeWorkbook.MaxDays).WithMessage($"At most {OeeWorkbook.MaxDays} days.");
        RuleFor(x => x.By).Must(b => b is ParetoBy.Minutes or ParetoBy.Count);
    }
}

public sealed class ExportOeeReportHandler(IAppDbContext db, PlantTimeZone plant, IOptions<OeeOptions> options)
    : IRequestHandler<ExportOeeReportQuery, byte[]>
{
    public async Task<byte[]> Handle(ExportOeeReportQuery q, CancellationToken ct)
    {
        var rows = await new GetOeeHistoryHandler(db).Handle(new GetOeeHistoryQuery(q.From, q.To, null, q.Line, Hourly: false), ct);
        return OeeWorkbook.Oee(rows, q, plant, options.Value);
    }
}

public sealed class ExportParetoReportHandler(IAppDbContext db, IOeeInputReader reader, PlantTimeZone plant, TimeProvider time,
    IOptions<OeeOptions> options) : IRequestHandler<ExportParetoReportQuery, byte[]>
{
    public async Task<byte[]> Handle(ExportParetoReportQuery q, CancellationToken ct)
    {
        var pareto = await new GetDowntimeParetoHandler(db, reader, plant, time)
            .Handle(new GetDowntimeParetoQuery(q.From, q.To, null, q.Line, q.By), ct);
        return OeeWorkbook.Pareto(pareto, q.Line, plant, options.Value);
    }
}

public static class OeeWorkbook
{
    public const int MaxDays = 31;
    public const string Percent = "0.0%";
    private const string Count = "#,##0";
    private const string Minutes = "#,##0.0";
    private const string DateTimeFormat = "yyyy-mm-dd hh:mm";

    public static byte[] Oee(IReadOnlyList<OeeSnapshotDto> rows, ExportOeeReportQuery q, PlantTimeZone plant, OeeOptions options)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("OEE");
        ws.Cell(1, 1).Value = $"OEE by machine and shift, {q.From:yyyy-MM-dd} to {q.To:yyyy-MM-dd}{LineText(q.Line)}. " +
                              $"Times in plant time ({plant.Zone.Id}).";
        ws.Cell(1, 1).Style.Font.Bold = true;
        string[] header = ["Shift date", "Shift", "Machine", "Start", "End", "Planned min", "Run min", "Total count",
            "Good count", "Scans", "Availability", "Performance", "Quality", "OEE", "Warnings", "Final"];
        Header(ws, 3, header);
        var r = 4;
        foreach (var x in rows)
        {
            var f = x.Figures;
            ws.Cell(r, 1).Value = x.ShiftDate.ToDateTime(TimeOnly.MinValue);
            ws.Cell(r, 2).Value = x.Shift;
            ws.Cell(r, 3).Value = x.Machine;
            ws.Cell(r, 4).Value = Local(x.WindowStartUtc, plant);
            ws.Cell(r, 5).Value = Local(x.WindowEndUtc, plant);
            ws.Cell(r, 6).Value = f.PlannedSeconds / 60.0;
            ws.Cell(r, 7).Value = f.RunSeconds / 60.0;
            ws.Cell(r, 8).Value = f.TotalCount;
            ws.Cell(r, 9).Value = f.GoodCount;
            ws.Cell(r, 10).Value = f.ScanCount;
            Ratio(ws.Cell(r, 11), f.Availability);
            Ratio(ws.Cell(r, 12), f.Performance);
            Ratio(ws.Cell(r, 13), f.Quality);
            Ratio(ws.Cell(r, 14), f.Oee);
            ws.Cell(r, 15).Value = string.Join(", ", f.Warnings);
            ws.Cell(r, 16).Value = x.IsFinal;
            r++;
        }
        ws.Column(1).Style.NumberFormat.Format = "yyyy-mm-dd";
        ws.Columns(4, 5).Style.NumberFormat.Format = DateTimeFormat;
        ws.Columns(6, 7).Style.NumberFormat.Format = Minutes;
        ws.Columns(8, 10).Style.NumberFormat.Format = Count;
        ws.Columns(11, 14).Style.NumberFormat.Format = Percent;
        Finish(ws, header.Length);
        Definitions(wb, plant, options);
        return Save(wb);
    }

    public static byte[] Pareto(ParetoDto p, string? line, PlantTimeZone plant, OeeOptions options)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Pareto");
        ws.Cell(1, 1).Value = $"Downtime Pareto by {p.By}, {p.From:yyyy-MM-dd} to {p.To:yyyy-MM-dd}{LineText(line)}. " +
                              $"Total {p.TotalMinutes:0.#} min, {p.TotalCount} stops.";
        ws.Cell(1, 1).Style.Font.Bold = true;
        string[] header = ["Reason", "Name", "Counts against availability", "Minutes", "Stops", "Percent", "Cumulative percent"];
        Header(ws, 3, header);
        var r = 4;
        foreach (var x in p.Rows)
        {
            ws.Cell(r, 1).Value = x.Reason;
            ws.Cell(r, 2).Value = x.Name;
            ws.Cell(r, 3).Value = x.CountsAgainstAvailability;
            ws.Cell(r, 4).Value = x.Minutes;
            ws.Cell(r, 5).Value = x.Count;
            ws.Cell(r, 6).Value = x.Percent / 100;
            ws.Cell(r, 7).Value = x.CumulativePercent / 100;
            r++;
        }
        ws.Column(4).Style.NumberFormat.Format = Minutes;
        ws.Column(5).Style.NumberFormat.Format = Count;
        ws.Columns(6, 7).Style.NumberFormat.Format = Percent;
        Finish(ws, header.Length);
        Definitions(wb, plant, options);
        return Save(wb);
    }

    private static void Definitions(XLWorkbook wb, PlantTimeZone plant, OeeOptions options)
    {
        var ws = wb.AddWorksheet("Definitions");
        string[] lines =
        [
            "OEE = Availability x Performance x Quality (MiniMES, spec M5).",
            "Planned time = shift length - breaks - planned stops (their union: an overlap is counted once).",
            "Run time = planned time - unplanned downtime. Availability = run time / planned time.",
            "Performance = sum(ideal cycle time x count) / run time; ideal cycle time from the work order snapshot.",
            "Quality = good count / total count. Total count = machine counter; good count = OK scans by serial number.",
            $"Stops shorter than {options.MicroStopThresholdSec} s are micro stops: not downtime, they lower Performance.",
            "A stop without a reason counts against Availability until a supervisor classifies it.",
            "Idle counts against Availability only in a shift where the machine's station scanned something.",
            "Performance above 100 % is kept as is and flagged: the ideal cycle time or the counter is wrong.",
            "Several hours, shifts or machines: times and counts are added, then divided (never an average of %).",
            $"Shift date = the plant-time date the shift starts (a night shift belongs to the day it starts). Times: {plant.Zone.Id}.",
            "Percent columns are numbers formatted as %, usable in formulas and pivot tables.",
        ];
        for (var i = 0; i < lines.Length; i++) ws.Cell(i + 1, 1).Value = lines[i];
        ws.Column(1).Width = 120;
    }

    private static void Header(IXLWorksheet ws, int row, string[] header)
    {
        for (var i = 0; i < header.Length; i++) ws.Cell(row, i + 1).Value = header[i];
        ws.Row(row).Style.Font.Bold = true;
        ws.SheetView.FreezeRows(row);
    }

    private static void Finish(IXLWorksheet ws, int columns)
        => ws.Columns(1, columns).Width = 16;   // fixed: AdjustToContents needs system fonts, missing on the Linux CI runner

    private static void Ratio(IXLCell cell, double? value)
    {
        if (value is { } v) cell.Value = v;   // undefined (lunch hour) stays an empty cell, not 0 %
    }

    private static DateTime Local(DateTime utc, PlantTimeZone plant)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), plant.Zone);

    private static string LineText(string? line) => line is null ? "" : $", line {line}";

    private static byte[] Save(XLWorkbook wb)
    {
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
