using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MiniMes.Contracts.MasterData;

namespace MiniMes.Client.Core.ViewModels.MasterData;

// One editable BOM line in the grid (Draft only). ScrapRate stays a fraction (0.02 = 2%) like the API.
public partial class BomLineRow : ObservableObject
{
    [ObservableProperty]
    private int _componentItemId;

    [ObservableProperty]
    private decimal _qtyPer = 1m;

    [ObservableProperty]
    private decimal _scrapRate;

    [ObservableProperty]
    private int _operationSeq = 10;

    public static BomLineRow From(BomLineDto l) => new()
    {
        ComponentItemId = l.ComponentItemId, QtyPer = l.QtyPer, ScrapRate = l.ScrapRate, OperationSeq = l.OperationSeq,
    };

    public BomLineInput ToInput() => new(ComponentItemId, QtyPer, ScrapRate, OperationSeq);
}

// One node of the BOM tree (spec M1 2.6: HierarchicalDataTemplate binds Children).
public sealed class BomNodeViewModel(BomTreeNodeDto node, bool isRoot)
{
    public string Code { get; } = node.Code;
    public string Name { get; } = node.Name;
    public string Type { get; } = node.Type;
    public string QtyText { get; } = isRoot ? string.Empty : "x" + node.QtyPer.ToString("0.######", CultureInfo.InvariantCulture);
    public string ScrapText { get; } = node.ScrapRate > 0 ? $"scrap {node.ScrapRate * 100:0.##}%" : string.Empty;
    public string NeedText { get; } = $"= {node.RequiredQty.ToString("0.#####", CultureInfo.InvariantCulture)} {node.Uom}";
    public IReadOnlyList<BomNodeViewModel> Children { get; } = node.Children.Select(c => new BomNodeViewModel(c, false)).ToList();
}
