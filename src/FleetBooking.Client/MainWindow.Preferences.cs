using System.Collections;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using FleetBooking.Shared;

namespace FleetBooking.Client;

public partial class MainWindow
{
    private UserPreferencesStore _preferencesStore = null!;
    private UserPreferences _preferences = new();
    private Guid _preferenceUserId;
    private bool _restoringPreferences;
    private readonly Dictionary<DataGrid, TableLayout> _tableLayouts = [];
    private readonly List<(DependencyPropertyDescriptor Descriptor, DataGridColumn Column, EventHandler Handler)> _columnHandlers = [];
    private readonly DispatcherTimer _preferencesTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    private void InitializePreferences(UserPreferencesStore? store)
    {
        _preferencesStore = store ?? new UserPreferencesStore();
        _preferenceUserId = _api.Session!.UserId;
        _preferences = _preferencesStore.Load(_preferenceUserId);
        foreach (var grid in new[] { DashboardMineGrid, DashboardOccupancyGrid, VehiclesGrid, MineGrid, AdminVehiclesGrid,
            UsersGrid, AllReservationsGrid, UnavailabilityGrid, AuditGrid, IssuesGrid })
        {
            var adminGrid = grid == UsersGrid || grid == AdminVehiclesGrid || grid == AllReservationsGrid || grid == UnavailabilityGrid || grid == AuditGrid;
            _tableLayouts.Add(grid, new TableLayout(grid, adminGrid ? 3 : 1));
            grid.ColumnReordered += (_, _) => QueuePreferencesSave();
            grid.Sorting += (_, _) => Dispatcher.BeginInvoke(QueuePreferencesSave);
            foreach (var column in grid.Columns)
            {
                var descriptor = DependencyPropertyDescriptor.FromProperty(DataGridColumn.WidthProperty, typeof(DataGridColumn));
                EventHandler handler = (_, _) => QueuePreferencesSave();
                descriptor.AddValueChanged(column, handler); _columnHandlers.Add((descriptor, column, handler));
            }
        }
        _preferencesTimer.Tick += (_, _) => { _preferencesTimer.Stop(); SavePreferencesSafely(); };
        CalendarView.SelectionChanged += (_, _) => QueuePreferencesSave();
        OnlyFreeVehicles.Checked += (_, _) => QueuePreferencesSave();
        OnlyFreeVehicles.Unchecked += (_, _) => QueuePreferencesSave();
        Closing += (_, _) => { _preferencesTimer.Stop(); SavePreferencesSafely(); };
        Closed += (_, _) =>
        {
            _preferencesTimer.Stop();
            foreach (var registration in _columnHandlers) registration.Descriptor.RemoveValueChanged(registration.Column, registration.Handler);
            _columnHandlers.Clear();
        };
        PreviewKeyDown += MainWindow_KeyDown;
        ApplyPreferences(startup: true);
        if (_preferencesStore.LastWarning is { } warning) StatusText.Text = warning;
    }
    private void QueuePreferencesSave()
    {
        if (_restoringPreferences || !IsLoaded || _windowClosed) return;
        _preferencesTimer.Stop(); _preferencesTimer.Start();
    }
    private UserPreferences CapturePreferences()
    {
        var value = _preferences.Copy();
        value.LastTab = Math.Max(0, MainTabs.SelectedIndex); value.LastAdminTab = Math.Max(0, AdminTabs.SelectedIndex);
        value.CalendarView = value.RememberCalendar ? Math.Max(0, CalendarView.SelectedIndex) : 1;
        value.OnlyFreeVehicles = OnlyFreeVehicles.IsChecked == true;
        value.Tables = value.RememberTableLayouts
            ? _tableLayouts.ToDictionary(x => x.Key.Name, x => x.Value.Capture()) : [];
        return value;
    }
    private void SavePreferencesSafely()
    {
        try { _preferencesStore.Save(_preferenceUserId, CapturePreferences()); }
        catch { if (!_windowClosed) StatusText.Text = "Lična podešavanja nijesu sačuvana. Provjerite pristup korisničkom folderu."; }
    }
    private void ApplyPreferences(bool startup)
    {
        _restoringPreferences = true;
        try
        {
            FontSize = _preferences.TextSize;
            foreach (var (grid, layout) in _tableLayouts)
            {
                if (grid != VehiclesGrid && grid != AdminVehiclesGrid) grid.RowHeight = Math.Max(_preferences.CompactRows ? 32 : 38, _preferences.TextSize + 18);
                layout.Restore(_preferences.RememberTableLayouts && _preferences.Tables.TryGetValue(grid.Name, out var saved) ? saved : layout.Defaults);
            }
            WindowState = _preferences.StartMaximized ? WindowState.Maximized : WindowState.Normal;
            if (startup)
            {
                CalendarView.SelectedIndex = _preferences.RememberCalendar ? _preferences.CalendarView : 1;
                OnlyFreeVehicles.IsChecked = _preferences.OnlyFreeVehicles;
                AdminTabs.SelectedIndex = _preferences.LastAdminTab;
                var index = _preferences.StartupPage switch
                {
                    "Home" => 0, "Vehicles" => 1, "Calendar" => 2, "Mine" => 3, "Issues" => 5, _ => _preferences.LastTab
                };
                MainTabs.SelectedIndex = index == 4 && !IsAdmin ? 0 : index;
            }
        }
        finally { _restoringPreferences = false; }
        ApplySearch();
    }
    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        SettingsWindow? dialog = null;
        dialog = new SettingsWindow(CapturePreferences(), value =>
        {
            _preferencesStore.Save(_preferenceUserId, value); // No UI changes if saving fails.
            _preferences = value; ApplyPreferences(startup: dialog!.ResetRequested);
            StatusText.Text = "Podešavanja su sačuvana.";
        }) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.ResetRequested) await RunAsync(RefreshSelectedAsync);
    }
    private void SetGridItems(DataGrid grid, IEnumerable items)
    {
        var layout = _tableLayouts.GetValueOrDefault(grid);
        var current = grid.ItemsSource is not null ? layout?.Capture() :
            _preferences.RememberTableLayouts ? _preferences.Tables.GetValueOrDefault(grid.Name) : null;
        grid.ItemsSource = items;
        if (current is not null) layout?.RestoreSort(current);
        FilterGrid(grid);
    }
    private IEnumerable<DataGrid> ActiveGrids() => MainTabs.SelectedIndex switch
    {
        0 => [DashboardMineGrid, DashboardOccupancyGrid], 1 => [VehiclesGrid], 3 => [MineGrid], 5 => [IssuesGrid],
        4 when IsAdmin => AdminTabs.SelectedIndex switch
        {
            0 => [AdminVehiclesGrid], 1 => [UsersGrid], 2 => [AllReservationsGrid],
            3 => [UnavailabilityGrid], 4 => [AuditGrid], _ => []
        },
        _ => []
    };
    private void FilterGrid(DataGrid grid)
    {
        var view = CollectionViewSource.GetDefaultView(grid.ItemsSource);
        if (view is null || !view.CanFilter) return;
        var query = QuickSearchBox.Text.Trim();
        var status = grid == IssuesGrid ? ((IssuesStatusBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "") : "";
        view.Filter = item => (query.Length == 0 || SearchText(item).Contains(query, StringComparison.CurrentCultureIgnoreCase)) &&
            (status.Length == 0 || item is VehicleIssueRow issue && issue.Item.Status == status);
    }
    private static string SearchText(object row) => row switch
    {
        VehicleDisplayRow v => $"{v.Item.MakeModel} {v.Item.Registration} {v.Item.PickupLocation} {v.Item.FuelType} {v.Item.Note} {v.Status}",
        ReservationRow r => $"{r.Vehicle} {r.Employee} {r.Start} {r.End} {r.Purpose} {r.Destination} {r.Status}",
        OccupancyRow o => $"{o.Vehicle} {o.Start} {o.End} {o.ReservedBy}",
        UserDto u => $"{u.FullName} {u.Email} {u.Phone} {(u.Role == Roles.Administrator ? "IT administrator" : "Zaposleni")}",
        UnavailabilityRow u => $"{u.Vehicle} {u.Start} {u.End} {u.Reason}",
        AuditRow a => $"{a.Time} {a.Actor} {a.Action} {a.Entity} {a.Details}",
        VehicleIssueRow i => $"{i.Vehicle} {i.Reporter} {i.Created} {i.Category} {i.Description} {i.Status} {i.Item.AdminNote}", _ => ""
    };
    private void ApplySearch()
    {
        if (QuickSearchBox is null || MainTabs is null) return;
        foreach (var grid in ActiveGrids()) FilterGrid(grid);
        if (MainTabs.SelectedIndex == 2) ApplyCalendarFilter();
    }
    private void QuickSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplySearch();
    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        QuickSearchBox.Clear(); CalendarSearch.Clear(); QuickSearchBox.Focus();
    }
    private async void AdminTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != AdminTabs || _restoringPreferences || !IsLoaded || MainTabs.SelectedIndex != 4) return;
        QueuePreferencesSave(); ApplySearch(); await RunAsync(RefreshSelectedAsync);
    }
    private Task RefreshSelectedAsync() => MainTabs.SelectedIndex switch
    {
        0 => LoadDashboardAsync(), 1 => LoadAvailableAsync(), 2 => LoadCalendarAsync(), 3 => LoadMineAsync(), 5 => LoadIssuesAsync(),
        4 when IsAdmin => AdminTabs.SelectedIndex switch
        {
            0 => LoadAdminVehiclesAsync(), 1 => LoadUsersAsync(), 2 => LoadAllAsync(), 3 => LoadUnavailabilityAsync(),
            4 => LoadAuditAsync(), _ => RefreshSystemStatusAsync()
        },
        _ => Task.CompletedTask
    };
    private async void MainWindow_KeyDown(object sender, KeyEventArgs e)
    {
        var action = ClientShortcuts.Match(e.Key, Keyboard.Modifiers);
        if (action == ShortcutAction.None || !IsEnabled || OwnedWindows.Cast<Window>().Any(w => w.IsVisible)) return;
        e.Handled = true;
        if (action == ShortcutAction.Search)
        {
            var search = MainTabs.SelectedIndex == 2 ? CalendarSearch : QuickSearchBox;
            search.Focus(); search.SelectAll();
        }
        else if (action == ShortcutAction.Refresh) await RunAsync(RefreshSelectedAsync);
        else await StartQuickReservationAsync();
    }
    private async Task StartQuickReservationAsync()
    {
        if (_openingReservation) return;
        _openingReservation = true;
        try
        {
            var selected = (VehiclesGrid.SelectedItem as VehicleDisplayRow)?.Item.Id;
            _restoringPreferences = true;
            try { MainTabs.SelectedIndex = 1; } finally { _restoringPreferences = false; }
            QueuePreferencesSave();
            var loaded = false;
            await RunAsync(async () => { await LoadAvailableAsync(); loaded = true; });
            if (!loaded) return; // Never open an editor from stale results after a failed refresh.
            var rows = VehiclesGrid.Items.OfType<VehicleDisplayRow>().ToList();
            VehiclesGrid.SelectedItem = rows.FirstOrDefault(x => x.Item.Id == selected) ??
                rows.FirstOrDefault(x => !x.Item.HasReservation && x.Item.StatusCode != VehicleStates.Service &&
                    (!x.Item.AssignedUserId.HasValue || x.Item.AssignedUserId == _api.Session!.UserId));
            if (VehiclesGrid.SelectedItem is null) { StatusText.Text = "Nema slobodnih vozila u ovom pregledu. Promijenite termin ili pretragu."; return; }
            await OpenReservationAsync();
        }
        catch (Exception ex) { Dialogs.Error(this, ex); }
        finally { _openingReservation = false; }
    }
    private void ShowSuccess(string message)
    {
        StatusText.Text = message;
        if (_preferences.ShowSuccessMessages) MessageBox.Show(this, message, "Fleet Booking Demo", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
