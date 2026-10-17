using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using MiniMes.Client.Core.ViewModels.MasterData;

namespace MiniMes.BackOffice.Wpf.Views.MasterData;

// One view for every catalog. Which columns to show is data (CatalogViewModel.Columns), so they are built
// here when the DataContext arrives; DataGrid columns cannot be bound from XAML.
public partial class CatalogView : UserControl
{
    public CatalogView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BuildColumns();
    }

    private void BuildColumns()
    {
        RowsGrid.Columns.Clear();
        if (DataContext is not CatalogViewModel vm) return;

        foreach (var c in vm.Columns)
        {
            var binding = new Binding(c.Path) { StringFormat = c.Format is null ? null : "{0:" + c.Format + "}" };
            DataGridColumn column = c.IsCheckBox
                ? new DataGridCheckBoxColumn { Binding = binding }
                : new DataGridTextColumn { Binding = binding };
            column.Header = c.Header;
            column.Width = new DataGridLength(c.Width);
            RowsGrid.Columns.Add(column);
        }
        // The last column takes the remaining width.
        if (RowsGrid.Columns.Count > 0) RowsGrid.Columns[^1].Width = new DataGridLength(1, DataGridLengthUnitType.Star);
    }
}
