using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Data;

namespace FleetBooking.Client;

public sealed class TableLayout
{
    private readonly DataGrid _grid;
    private readonly Dictionary<DataGridColumn, string> _keys;
    private readonly int _layoutVersion;
    public TablePreferences Defaults { get; }
    public TableLayout(DataGrid grid, int layoutVersion = 1)
    {
        _grid = grid;
        _layoutVersion = layoutVersion;
        _keys = grid.Columns.Select((column, index) => (column, key: index + ":" + column.Header?.ToString()))
            .ToDictionary(x => x.column, x => x.key);
        Defaults = Capture();
    }
    public TablePreferences Capture() => new()
    {
        LayoutVersion = _layoutVersion,
        Columns = _grid.Columns.Select(c => new ColumnPreference(_keys[c], c.DisplayIndex,
            c.Width.Value, c.Width.UnitType.ToString())).ToList(),
        Sort = CollectionViewSource.GetDefaultView(_grid.ItemsSource)?.SortDescriptions
            .Select(s => new SortPreference(s.PropertyName, s.Direction)).ToList() ?? []
    };
    public void Restore(TablePreferences state)
    {
        var known = state.Columns.Where(c => _keys.ContainsValue(c.Key)).GroupBy(c => c.Key).Select(g => g.First()).ToList();
        // Reject an incomplete/duplicate order rather than corrupting WPF DisplayIndex.
        if (state.LayoutVersion == _layoutVersion && known.Count == _grid.Columns.Count && known.Select(c => c.DisplayIndex).Order().SequenceEqual(Enumerable.Range(0, known.Count)))
        {
            foreach (var saved in known.OrderBy(c => c.DisplayIndex))
            {
                var column = _keys.Single(c => c.Value == saved.Key).Key;
                column.DisplayIndex = saved.DisplayIndex;
                if (Enum.TryParse<DataGridLengthUnitType>(saved.Unit, out var unit) && double.IsFinite(saved.Width) && saved.Width > 0)
                    column.Width = new DataGridLength(saved.Width, unit);
            }
        }
        RestoreSort(state);
    }
    private static string SortPath(DataGridColumn column) => !string.IsNullOrWhiteSpace(column.SortMemberPath)
        ? column.SortMemberPath : (column as DataGridBoundColumn)?.Binding is Binding binding ? binding.Path?.Path ?? "" : "";
    public void RestoreSort(TablePreferences state)
    {
        var view = CollectionViewSource.GetDefaultView(_grid.ItemsSource);
        if (view is null || !view.CanSort) return;
        var paths = _grid.Columns.Select(SortPath).Where(x => x.Length > 0).ToHashSet();
        using (view.DeferRefresh())
        {
            view.SortDescriptions.Clear();
            foreach (var column in _grid.Columns) column.SortDirection = null;
            foreach (var sort in state.Sort.Where(s => paths.Contains(s.Property)).DistinctBy(s => s.Property))
            {
                view.SortDescriptions.Add(new SortDescription(sort.Property, sort.Direction));
                foreach (var column in _grid.Columns.Where(c => SortPath(c) == sort.Property)) column.SortDirection = sort.Direction;
            }
        }
    }
}
