using System.Net;
using MiniMes.Client.Core.Api;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Resources;
using MiniMes.Client.Core.ViewModels.Execution;
using MiniMes.Contracts.Common;
using MiniMes.Contracts.Execution;
using MiniMes.Contracts.Planning;

namespace MiniMes.UnitTests.Client;

// Spec M3 step 9: the BackOffice report screen (FPY per step, RTY, final yield, WIP).
public class ExecutionReportViewModelTests
{
    private static WorkOrderSummaryDto Wo(int id, string status) => new(
        id, $"WO-2610-0000{id}", 1, "PCB-A", 1, "L1", 200, 0, new DateTime(2026, 11, 2, 1, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 11, 2, 5, 0, 0, DateTimeKind.Utc), status, 5, null);

    private sealed class Orders(params WorkOrderSummaryDto[] list) : FakeWorkOrderApi
    {
        public override Task<PagedResult<WorkOrderSummaryDto>> GetWorkOrdersAsync(string? status, int? lineId, string? search, int page, int pageSize, CancellationToken ct = default)
            => Task.FromResult(new PagedResult<WorkOrderSummaryDto>(list, page, pageSize, list.Length));
    }

    private sealed class Reports : IReportApi
    {
        public List<int> Asked { get; } = [];
        public Exception? Fail { get; init; }

        public Task<FpyReportDto> GetFpyAsync(int workOrderId, CancellationToken ct = default)
        {
            Asked.Add(workOrderId);
            if (Fail is not null) return Task.FromException<FpyReportDto>(Fail);
            return Task.FromResult(new FpyReportDto(workOrderId, "WO", [new FpyStepDto(10, "SMT", 11, 11, 100m), new FpyStepDto(30, "TEST", 10, 7, 70m),
                new FpyStepDto(40, "PACK", 0, 0, null)], 70m, 6, 1, 85.71m));
        }

        public Task<WipReportDto> GetWipAsync(int workOrderId, CancellationToken ct = default)
            => Task.FromResult(new WipReportDto(workOrderId, "WO", [new WipRowDto(30, "TEST", "PACK", "InProcess", 2), new WipRowDto(20, "ASSY", "TEST", "Ng", 1)], 3));
    }

    [Fact]
    public async Task Opens_on_the_first_order_with_boards_and_shows_both_reports()
    {
        var reports = new Reports();
        var vm = new ExecutionReportViewModel(reports, new Orders(Wo(1, "Draft"), Wo(2, "InProgress"), Wo(3, "Cancelled"), Wo(4, "Completed")));

        await vm.LoadAsync();

        Assert.Equal([2, 4], vm.WorkOrders.Select(w => w.Id));   // no Draft, no Cancelled: nothing ran there
        Assert.Equal(2, vm.SelectedWorkOrder?.Id);
        Assert.Equal([2], reports.Asked);
        Assert.Equal(["100.00 %", "70.00 %", "-"], vm.FpySteps.Select(r => r.Fpy));   // "-" = no board yet, not 0 %
        Assert.Equal(string.Format(Strings.Report_Summary, "70.00 %", "85.71 %", 6, 1), vm.Summary);
        Assert.Equal([20, 30], vm.WipRows.Select(r => r.CurrentSeq));                 // in line order
        Assert.Equal(string.Format(Strings.Report_WipTotal, 3), vm.WipTotal);
    }

    [Fact]
    public async Task Choosing_another_order_reloads()
    {
        var reports = new Reports();
        var vm = new ExecutionReportViewModel(reports, new Orders(Wo(2, "InProgress"), Wo(4, "Completed")));
        await vm.LoadAsync();

        vm.SelectedWorkOrder = vm.WorkOrders[1];
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Contains(4, reports.Asked);
    }

    [Fact]
    public async Task Api_error_is_shown()
    {
        var vm = new ExecutionReportViewModel(new Reports { Fail = new ApiException(HttpStatusCode.NotFound, "Work order 2 not found.") },
            new Orders(Wo(2, "InProgress")));

        await vm.LoadAsync();
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("Work order 2 not found.", vm.ErrorMessage);
        Assert.Empty(vm.FpySteps);
    }
}
