using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Input;
using System.Windows.Threading;
using FleetBooking.Shared;
using Microsoft.Win32;

namespace FleetBooking.Client;

public partial class MainWindow : Window
{
    private readonly ApiClient _api;
    private readonly DispatcherTimer _serverTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool _checkingServer;
    private bool _windowClosed;
    private bool _openingReservation;
    private List<OccupancyRow> _calendarRows = [];
    private List<VehicleDto> _calendarVehicles = [];
    private DateTime _calendarFrom;
    private DateTime _calendarTo;
    private double _lastCalendarViewportWidth;
    private readonly Dictionary<Guid, (DateTime? Version, ImageSource? Image)> _vehicleImageCache = [];
    private static readonly IReadOnlyList<KeyValuePair<string, string>> RoleOptions = [new(Roles.Employee, "Zaposleni"), new(Roles.Administrator, "IT administrator")];
    private static readonly IReadOnlyList<KeyValuePair<string, string>> BoolOptions = [new("true", "Da"), new("false", "Ne")];

    public MainWindow(ApiClient api, UserPreferencesStore? preferencesStore = null)
    {
        InitializeComponent(); _api = api;
        VersionText.Text = $"v{typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}";
        ConnectionText.ToolTip = api.ServerAddress.ToString();
        _serverTimer.Tick += async (_, _) => await RefreshSystemStatusAsync();
        Closed += (_, _) => { _windowClosed = true; _serverTimer.Stop(); };
        var session = api.Session ?? throw new InvalidOperationException("Korisnik nije prijavljen.");
        UserText.Text = $"{session.FullName}  •  {(IsAdmin ? "IT administrator" : "Zaposleni")}";
        DashboardGreeting.Text = $"Dobro došli, {session.FullName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? session.FullName}";
        AdminTab.Visibility = IsAdmin ? Visibility.Visible : Visibility.Collapsed;
        ProcessIssueButton.Visibility = IsAdmin ? Visibility.Visible : Visibility.Collapsed;
        var times = Enumerable.Range(0, 96).Select(i => TimeSpan.FromMinutes(i * 15)).ToList();
        AvailableStartTime.ItemsSource = times; AvailableEndTime.ItemsSource = times;
        AvailableStartTime.ItemStringFormat = @"hh\:mm"; AvailableEndTime.ItemStringFormat = @"hh\:mm";
        var start = LocalTime.Now.AddHours(1); start = new DateTime(start.Year, start.Month, start.Day, start.Hour, 0, 0);
        AvailableStartDate.SelectedDate = start.Date; AvailableStartTime.SelectedItem = start.TimeOfDay;
        AvailableEndDate.SelectedDate = start.AddHours(2).Date; AvailableEndTime.SelectedItem = start.AddHours(2).TimeOfDay;
        CalendarDate.SelectedDate = LocalTime.Now.Date;
        InitializePreferences(preferencesStore);
        Loaded += async (_, _) =>
        {
            _serverTimer.Start();
            _ = RefreshSystemStatusAsync();
            await RunAsync(RefreshSelectedAsync);
            if (session.MustChangePassword) await ForcePasswordChangeAsync();
        };
    }
    private bool IsAdmin => _api.Session?.Role == Roles.Administrator;

    private async Task RefreshSystemStatusAsync()
    {
        if (_checkingServer || _windowClosed) return;
        _checkingServer = true;
        try
        {
            var health = await _api.GetHealthAsync();
            if (_windowClosed) return;
            ConnectionText.Text = "● Server povezan";
            ConnectionText.Foreground = Brushes.SeaGreen;
            ConnectionText.ToolTip = $"{_api.ServerAddress}\nAPI v{health.Version}\nProvjereno: {LocalTime.Show(health.Utc)}";
            if (IsAdmin)
            {
                BackupFooterText.Visibility = Visibility.Visible;
                try
                {
                    var backup = await _api.GetBackupStatusAsync();
                    if (_windowClosed) return;
                    BackupFooterText.Text = backup.Running ? "Kopija baze: izrada u toku" :
                        !backup.Enabled ? "Kopija baze: isključena" :
                        backup.LastAttemptFailed ? "Kopija baze: posljednji pokušaj nije uspio" :
                        backup.LastSuccessUtc is { } success ? $"Kopija baze: {LocalTime.Show(success)}" : "Kopija baze: čeka se prva kopija";
                    BackupFooterText.Foreground = backup.LastAttemptFailed || !backup.Enabled ? Brushes.DarkOrange : Brushes.SlateGray;
                    BackupFooterText.ToolTip = backup.Message;
                    BackupSummaryText.Text = $"Automatske kopije: {(backup.Enabled ? "uključene" : "isključene")}\n" +
                        $"Raspored: svakog dana u {backup.DailyTime} ({backup.TimeZone})\n" +
                        $"Posljednja uspješna kopija: {(backup.LastSuccessUtc is { } last ? LocalTime.Show(last) : "još nije napravljena")}\n" +
                        $"Posljednji pokušaj: {(backup.LastAttemptUtc is { } attempt ? LocalTime.Show(attempt) : "—")}\n\n{backup.Message}\n\n" +
                        "Kopije se čuvaju na serveru. Ako je server ugašen u vrijeme kopiranja, kopija se pravi nakon pokretanja. " +
                        "Za zaštitu od kvara diska kopije treba čuvati i na odvojenom uređaju.";
                }
                catch
                {
                    BackupFooterText.Text = "Kopija baze: stanje nije dostupno";
                    BackupFooterText.Foreground = Brushes.DarkOrange;
                    BackupSummaryText.Text = "Stanje rezervnih kopija nije dostupno. Provjerite sesiju i verziju API servera.";
                }
            }
        }
        catch
        {
            if (_windowClosed) return;
            ConnectionText.Text = "● Server nije dostupan";
            ConnectionText.Foreground = Brushes.Firebrick;
            if (IsAdmin)
            {
                BackupFooterText.Text = "Kopija baze: stanje nije dostupno";
                BackupSummaryText.Text = "Server nije dostupan. Posljednja kopija trenutno se ne može provjeriti.";
            }
        }
        finally { _checkingServer = false; }
    }
    private async void RefreshBackup_Click(object sender, RoutedEventArgs e) => await RefreshSystemStatusAsync();

    private async Task RunAsync(Func<Task> action)
    {
        if (!IsEnabled) return;
        try { IsEnabled = false; StatusText.Text = "Učitavanje..."; await action(); StatusText.Text = "Spremno"; }
        catch (Exception ex) { StatusText.Text = "Greška"; Dialogs.Error(this, ex); }
        finally { IsEnabled = true; }
    }

    private static DateTime ReadDateTime(DatePicker date, ComboBox time, string field)
    {
        if (date.SelectedDate is null || time.SelectedItem is not TimeSpan value) throw new ApiException($"Izaberite datum i vrijeme za polje '{field}'.");
        return date.SelectedDate.Value.Date + value;
    }
    private static string ShowInput(DateTime value) => value.ToString("dd.MM.yyyy HH:mm");
    private static DateTime ParseLocal(string value, string field)
    {
        if (!DateTime.TryParseExact(value.Trim(), ["dd.MM.yyyy HH:mm", "d.M.yyyy H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var result))
            throw new ApiException($"Polje '{field}' mora biti u formatu dd.MM.gggg HH:mm.");
        return result;
    }

    private async Task LoadDashboardAsync()
    {
        var today = LocalTime.Now.Date; var tomorrow = today.AddDays(1);
        var vehiclesTask = _api.GetVehiclesAsync(); var availableTask = _api.GetVehiclesAsync(today, tomorrow);
        var mineTask = _api.GetMineAsync(); var occupancyTask = _api.GetOccupancyAsync(today, tomorrow);
        await Task.WhenAll(vehiclesTask, availableTask, mineTask, occupancyTask);
        var mine = mineTask.Result.Where(x => x.Status == ReservationStatuses.Active && x.EndUtc > DateTime.UtcNow).OrderBy(x => x.StartUtc).ToList();
        var occupancy = occupancyTask.Result.Select(x => new OccupancyRow(x)).OrderBy(x => x.Item.StartUtc).ToList();
        ActiveVehiclesCount.Text = vehiclesTask.Result.Count.ToString(); AvailableTodayCount.Text = availableTask.Result.Count.ToString();
        MyUpcomingCount.Text = mine.Count.ToString(); UnavailableTodayCount.Text = occupancy.Select(x => x.Item).Where(x => x.Type == "Nedostupno").Select(x => x.VehicleId).Distinct().Count().ToString();
        SetGridItems(DashboardMineGrid, mine.Take(6).Select(x => new ReservationRow(x)).ToList()); SetGridItems(DashboardOccupancyGrid, occupancy.Take(8).ToList());
    }
    private void RefreshDashboard_Click(object sender, RoutedEventArgs e) => _ = RunAsync(LoadDashboardAsync);
    private void GoToAvailable_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 1;

    private async Task LoadAvailableAsync()
    {
        var from = ReadDateTime(AvailableStartDate, AvailableStartTime, "Od"); var to = ReadDateTime(AvailableEndDate, AvailableEndTime, "Do");
        if (to <= from) throw new ApiException("Vrijeme završetka mora biti poslije početka.");
        SetGridItems(VehiclesGrid, await BuildVehicleRowsAsync(await _api.GetVehiclesAsync(from, to, availableOnly: OnlyFreeVehicles.IsChecked == true)));
    }
    private void SearchAvailable_Click(object sender, RoutedEventArgs e) => _ = RunAsync(LoadAvailableAsync);
    private async void CreateReservation_Click(object sender, RoutedEventArgs e)
    {
        if (_openingReservation) return;
        _openingReservation = true;
        try { await OpenReservationAsync(); }
        catch (Exception ex) { Dialogs.Error(this, ex); }
        finally { _openingReservation = false; }
    }
    private void VehiclesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!_preferences.DoubleClickReservation) return;
        if (e.ChangedButton != MouseButton.Left || e.OriginalSource is not DependencyObject element) return;
        var row = ItemsControl.ContainerFromElement(VehiclesGrid, element) as DataGridRow;
        if (row?.Item is not VehicleDisplayRow vehicle) return; // Ignore headers and empty space.
        VehiclesGrid.SelectedItem = vehicle;
        e.Handled = true;
        CreateReservation_Click(sender, e);
    }
    private async Task OpenReservationAsync()
    {
        if (VehiclesGrid.SelectedItem is not VehicleDisplayRow row) { MessageBox.Show(this, "Izaberite vozilo."); return; }
        var from = ReadDateTime(AvailableStartDate, AvailableStartTime, "Od");
        var to = ReadDateTime(AvailableEndDate, AvailableEndTime, "Do");
        if (to <= from || LocalTime.ToUtc(from) < DateTime.UtcNow)
            throw new ApiException("Izaberite budući početak i vrijeme vraćanja poslije preuzimanja.");
        // Refresh before opening the editor: another user may have booked/assigned the vehicle meanwhile.
        var vehicles = await _api.GetVehiclesAsync(from, to, availableOnly: false);
        var selected = vehicles.SingleOrDefault(x => x.Id == row.Item.Id)
            ?? throw new ApiException("Vozilo više nije aktivno ili dostupno.");
        var session = _api.Session!;
        if (selected.AssignedUserId.HasValue && selected.AssignedUserId.Value != session.UserId)
        {
            var assignedName = selected.AssignedUserName ?? "zaduženom korisniku";
            MessageBox.Show(this, $"Vozilo je zaduženo korisniku {assignedName} i ne može ga rezervisati drugi korisnik.\n\nObratite se: {assignedName}.",
                "Vozilo je zaduženo", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (selected.StatusCode == VehicleStates.Service || selected.HasReservation)
        {
            MessageBox.Show(this, selected.StatusCode == VehicleStates.Service
                ? "Vozilo je na servisu ili nedostupno u izabranom terminu. Izaberite drugi termin ili vozilo."
                : "Vozilo je rezervisano u izabranom terminu. Izaberite drugi termin ili vozilo.", "Vozilo nije dostupno");
            await RunAsync(LoadAvailableAsync);
            return;
        }
        var reservableVehicles = vehicles.Where(x => x.IsActive && x.StatusCode != VehicleStates.Service && !x.HasReservation &&
            (!x.AssignedUserId.HasValue || x.AssignedUserId.Value == session.UserId)).ToList();
        var dialog = new ReservationEditorWindow("Nova rezervacija", session.FullName, reservableVehicles, selected.Id,
            from, to, null, false,
            request => _api.AddReservationAsync(request)) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        await RunAsync(async () => { await LoadAvailableAsync(); await LoadMineAsync(); await LoadDashboardAsync(); });
        ShowSuccess("Rezervacija je potvrđena.");
    }

    private async Task LoadMineAsync() => SetGridItems(MineGrid, (await _api.GetMineAsync()).Select(x => new ReservationRow(x)).ToList());
    private void LoadMine_Click(object sender, RoutedEventArgs e) => _ = RunAsync(LoadMineAsync);
    private async void EditMine_Click(object sender, RoutedEventArgs e) => await EditReservationAsync((MineGrid.SelectedItem as ReservationRow)?.Item, false);
    private async void CancelMine_Click(object sender, RoutedEventArgs e) => await CancelReservationAsync((MineGrid.SelectedItem as ReservationRow)?.Item, false);
    private async Task EditReservationAsync(ReservationDto? item, bool admin)
    {
        if (item is null) { MessageBox.Show(this, "Izaberite rezervaciju."); return; }
        List<VehicleDto> vehicles;
        try { vehicles = await _api.GetVehiclesAsync(); } catch (Exception ex) { Dialogs.Error(this, ex); return; }
        if (vehicles.All(x => x.Id != item.VehicleId)) vehicles.Add(new VehicleDto(item.VehicleId, item.Vehicle, item.Registration, Math.Max(item.PassengerCount ?? 1, 1), "", item.DepartureLocation, true, null));
        var dialog = new ReservationEditorWindow("Izmjena rezervacije", item.EmployeeName, vehicles, item.VehicleId,
            LocalTime.FromUtc(item.StartUtc), LocalTime.FromUtc(item.EndUtc), item, admin,
            request => _api.UpdateReservationAsync(item.Id, request)) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        await RunAsync(async () => { await LoadMineAsync(); if (admin) await LoadAllAsync(); await LoadDashboardAsync(); });
    }
    private async Task CancelReservationAsync(ReservationDto? item, bool admin)
    {
        if (item is null) { MessageBox.Show(this, "Izaberite rezervaciju."); return; }
        if (!Dialogs.Confirm(this, $"Da li sigurno želite da otkažete rezervaciju vozila {item.Registration}?")) return;
        var dialog = new FormDialog("Otkazivanje rezervacije", [new("reason", "Razlog otkazivanja", Required: true, Multiline: true)]) { Owner = this };
        dialog.SaveAsync = values => _api.CancelReservationAsync(item.Id, values["reason"]);
        if (dialog.ShowDialog() != true) return;
        await RunAsync(async () => { await LoadMineAsync(); if (admin) await LoadAllAsync(); await LoadDashboardAsync(); });
    }

    private async Task LoadCalendarAsync()
    {
        var anchor = CalendarDate.SelectedDate ?? LocalTime.Now.Date;
        switch (CalendarView.SelectedIndex)
        {
            case 0: _calendarFrom = anchor.Date; _calendarTo = _calendarFrom.AddDays(1); break;
            case 2: _calendarFrom = new DateTime(anchor.Year, anchor.Month, 1); _calendarTo = _calendarFrom.AddMonths(1); break;
            default: var offset = ((int)anchor.DayOfWeek + 6) % 7; _calendarFrom = anchor.Date.AddDays(-offset); _calendarTo = _calendarFrom.AddDays(7); break;
        }
        var occupancyTask = _api.GetOccupancyAsync(_calendarFrom, _calendarTo); var vehiclesTask = _api.GetVehiclesAsync(); await Task.WhenAll(occupancyTask, vehiclesTask);
        _calendarRows = occupancyTask.Result.Select(x => new OccupancyRow(x)).ToList(); _calendarVehicles = vehiclesTask.Result;
        ApplyCalendarFilter(); StatusText.Text = $"Period: {_calendarFrom:dd.MM.yyyy} — {_calendarTo.AddSeconds(-1):dd.MM.yyyy}";
    }
    private void LoadCalendar_Click(object sender, RoutedEventArgs e) => _ = RunAsync(LoadCalendarAsync);
    private void CalendarSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyCalendarFilter();
    private void CalendarScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsLoaded || _calendarTo <= _calendarFrom || Math.Abs(e.NewSize.Width - _lastCalendarViewportWidth) < 8) return;
        _lastCalendarViewportWidth = e.NewSize.Width;
        ApplyCalendarFilter();
    }
    private void ApplyCalendarFilter()
    {
        if (CalendarTimelinePanel is null || _calendarTo <= _calendarFrom) return;
        var query = string.Join(" ", new[] { CalendarSearch?.Text?.Trim(), QuickSearchBox?.Text?.Trim() }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var vehicles = terms.Length == 0 ? _calendarVehicles : _calendarVehicles.Where(v =>
        {
            var text = $"{v.MakeModel} {v.Registration} " + string.Join(" ", _calendarRows.Where(r => r.Item.VehicleId == v.Id).Select(r => $"{r.Type} {r.Description}"));
            return terms.All(term => text.Contains(term, StringComparison.CurrentCultureIgnoreCase));
        }).ToList();
        RenderCalendar(vehicles);
    }
    private void RenderCalendar(IEnumerable<VehicleDto> vehicles)
    {
        CalendarTimelinePanel.Children.Clear(); const double labelWidth = 210; const double rowHeight = 68;
        var minimumTimelineWidth = CalendarView.SelectedIndex switch { 0 => 960d, 2 => Math.Max(1200d, (_calendarTo - _calendarFrom).TotalDays * 52d), _ => 840d };
        var viewportWidth = CalendarScrollViewer.ViewportWidth > 0 ? CalendarScrollViewer.ViewportWidth : CalendarScrollViewer.ActualWidth;
        var timelineWidth = Math.Max(minimumTimelineWidth, Math.Max(0, viewportWidth - labelWidth - 2));
        var ticks = CalendarTicks().ToList(); CalendarTimelinePanel.Children.Add(BuildTimelineHeader(labelWidth, timelineWidth, ticks));
        var index = 0;
        foreach (var vehicle in vehicles.OrderBy(x => x.MakeModel))
        {
            var grid = new Grid { Width = labelWidth + timelineWidth, Height = rowHeight, Background = index++ % 2 == 0 ? Brushes.White : new SolidColorBrush(Color.FromRgb(249, 250, 251)) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(timelineWidth) });
            var label = new Border { BorderBrush = (Brush)Application.Current.Resources["Line"], BorderThickness = new Thickness(0, 0, 1, 1), Padding = new Thickness(12, 7, 8, 5), Child = new StackPanel { Children = { new TextBlock { Text = vehicle.MakeModel, FontWeight = FontWeights.SemiBold, Foreground = (Brush)Application.Current.Resources["Navy"] }, new TextBlock { Text = vehicle.Registration, Foreground = new SolidColorBrush(Color.FromRgb(102, 112, 133)), FontSize = 12 }, new TextBlock { Text = vehicle.AssignedUserName is null ? "Nije zaduženo" : $"Zadužio/la: {vehicle.AssignedUserName}", Foreground = vehicle.AssignedUserName is null ? new SolidColorBrush(Color.FromRgb(152, 162, 179)) : new SolidColorBrush(Color.FromRgb(22, 128, 74)), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis } } } };
            grid.Children.Add(label); var canvas = new Canvas { Width = timelineWidth, Height = rowHeight }; Grid.SetColumn(canvas, 1); AddGridLines(canvas, timelineWidth, rowHeight, ticks);
            foreach (var row in _calendarRows.Where(x => x.Item.VehicleId == vehicle.Id)) AddOccupancyBlock(canvas, row, timelineWidth);
            grid.Children.Add(canvas); CalendarTimelinePanel.Children.Add(grid);
        }
        if (index == 0) CalendarTimelinePanel.Children.Add(new TextBlock { Text = "Nema vozila koja odgovaraju pretrazi.", Margin = new Thickness(24), Foreground = Brushes.Gray });
    }
    private FrameworkElement BuildTimelineHeader(double labelWidth, double timelineWidth, List<(DateTime Time, string Label)> ticks)
    {
        var grid = new Grid { Width = labelWidth + timelineWidth, Height = 48, Background = new SolidColorBrush(Color.FromRgb(238, 241, 244)) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(timelineWidth) });
        grid.Children.Add(new Border { BorderBrush = (Brush)Application.Current.Resources["Line"], BorderThickness = new Thickness(0, 0, 1, 1), Child = new TextBlock { Text = "Vozilo", FontWeight = FontWeights.SemiBold, Margin = new Thickness(12), VerticalAlignment = VerticalAlignment.Center } });
        var canvas = new Canvas { Width = timelineWidth, Height = 48 }; Grid.SetColumn(canvas, 1);
        foreach (var tick in ticks)
        {
            var x = Position(tick.Time, timelineWidth); canvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = 28, Y2 = 48, Stroke = new SolidColorBrush(Color.FromRgb(201, 208, 216)), StrokeThickness = 1 });
            var text = new TextBlock { Text = tick.Label, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(71, 84, 103)) }; Canvas.SetLeft(text, Math.Max(3, x + 5)); Canvas.SetTop(text, 8); canvas.Children.Add(text);
        }
        grid.Children.Add(canvas); return grid;
    }
    private IEnumerable<(DateTime Time, string Label)> CalendarTicks()
    {
        if (CalendarView.SelectedIndex == 0) for (var h = 0; h < 24; h += 2) yield return (_calendarFrom.AddHours(h), $"{h:00}:00");
        else for (var day = _calendarFrom; day < _calendarTo; day = day.AddDays(1)) yield return (day, CalendarView.SelectedIndex == 1 ? day.ToString("ddd dd.MM") : day.ToString("dd"));
    }
    private void AddGridLines(Canvas canvas, double width, double height, IEnumerable<(DateTime Time, string Label)> ticks)
    {
        foreach (var tick in ticks) { var x = Position(tick.Time, width); canvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = 0, Y2 = height, Stroke = new SolidColorBrush(Color.FromRgb(231, 234, 238)), StrokeThickness = 1 }); }
        canvas.Children.Add(new Line { X1 = 0, X2 = width, Y1 = height - 1, Y2 = height - 1, Stroke = new SolidColorBrush(Color.FromRgb(221, 226, 231)), StrokeThickness = 1 });
    }
    private void AddOccupancyBlock(Canvas canvas, OccupancyRow row, double width)
    {
        var start = LocalTime.FromUtc(row.Item.StartUtc); var end = LocalTime.FromUtc(row.Item.EndUtc);
        var visibleStart = start < _calendarFrom ? _calendarFrom : start; var visibleEnd = end > _calendarTo ? _calendarTo : end;
        if (visibleEnd <= visibleStart) return;
        var left = Position(visibleStart, width); var blockWidth = Math.Max(10, Position(visibleEnd, width) - left);
        var color = row.Type == "Nedostupno" ? Color.FromRgb(229, 138, 31) : Color.FromRgb(50, 118, 200);
        var border = new Border { Width = blockWidth, Height = 36, Background = new SolidColorBrush(color), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 4, 6, 3), ToolTip = $"{row.Vehicle}\n{row.Start} — {row.End}\n{row.Type}{(string.IsNullOrWhiteSpace(row.Description) ? "" : "\n" + row.Description)}", Child = new TextBlock { Text = blockWidth > 90 ? row.Type + (string.IsNullOrWhiteSpace(row.Description) ? "" : " • " + row.Description) : "", Foreground = Brushes.White, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center } };
        Canvas.SetLeft(border, left + 2); Canvas.SetTop(border, 16); canvas.Children.Add(border);
    }
    private double Position(DateTime time, double width) => Math.Clamp((time - _calendarFrom).TotalSeconds / (_calendarTo - _calendarFrom).TotalSeconds * width, 0, width);

    private async Task<List<VehicleDisplayRow>> BuildVehicleRowsAsync(IEnumerable<VehicleDto> vehicles)
    {
        var rows = await Task.WhenAll(vehicles.Select(async vehicle => new VehicleDisplayRow(vehicle, await LoadVehicleImageAsync(vehicle))));
        return rows.ToList();
    }
    private async Task<ImageSource?> LoadVehicleImageAsync(VehicleDto vehicle)
    {
        if (!vehicle.HasImage) { _vehicleImageCache[vehicle.Id] = (null, null); return null; }
        if (_vehicleImageCache.TryGetValue(vehicle.Id, out var cached) && cached.Version == vehicle.ImageUpdatedUtc) return cached.Image;
        try
        {
            var bytes = await _api.GetVehicleImageAsync(vehicle.Id);
            using var stream = new MemoryStream(bytes);
            var image = VehicleThumbnail.Load(stream);
            _vehicleImageCache[vehicle.Id] = (vehicle.ImageUpdatedUtc, image); return image;
        }
        catch { _vehicleImageCache.Remove(vehicle.Id); return null; }
    }

    private async Task LoadAdminVehiclesAsync() => SetGridItems(AdminVehiclesGrid, await BuildVehicleRowsAsync(await _api.GetVehiclesAsync(includeInactive: true)));
    private void LoadAdminVehicles_Click(object sender, RoutedEventArgs e) => _ = RunAsync(LoadAdminVehiclesAsync);
    private async void AddVehicle_Click(object sender, RoutedEventArgs e)
    {
        var d = VehicleDialog(null); d.SaveAsync = _ => _api.AddVehicleAsync(ReadVehicle(d));
        if (d.ShowDialog() != true) return;
        await RunAsync(async () => { await LoadAdminVehiclesAsync(); await LoadAvailableAsync(); await LoadDashboardAsync(); });
    }
    private async void EditVehicle_Click(object sender, RoutedEventArgs e)
    {
        var v = (AdminVehiclesGrid.SelectedItem as VehicleDisplayRow)?.Item; if (v is null) { MessageBox.Show(this, "Izaberite vozilo."); return; }
        var d = VehicleDialog(v); d.SaveAsync = _ => _api.UpdateVehicleAsync(v.Id, ReadVehicle(d));
        if (d.ShowDialog() != true) return;
        await RunAsync(async () => { await LoadAdminVehiclesAsync(); await LoadDashboardAsync(); });
    }
    private FormDialog VehicleDialog(VehicleDto? v) => new(v is null ? "Novo vozilo" : "Izmjena vozila", [
        new("model", "Marka i model", v?.MakeModel ?? "", Required: true), new("registration", "Registarska oznaka", v?.Registration ?? "", Required: true),
        new("seats", "Broj sjedišta", v?.SeatCount.ToString() ?? "5", Required: true, Validator: SeatError), new("fuel", "Vrsta goriva", v?.FuelType ?? "", Required: true),
        new("location", "Lokacija preuzimanja", v?.PickupLocation ?? "", Required: true), new("note", "Napomena", v?.Note ?? "", Multiline: true)]) { Owner = this };
    private static string? SeatError(string value) => !int.TryParse(value, out var seats) || seats is < 1 or > 100 ? "Unesite broj između 1 i 100." : null;
    private static SaveVehicleRequest ReadVehicle(FormDialog d) => new(d.Values["model"], d.Values["registration"], int.Parse(d.Values["seats"]), d.Values["fuel"], d.Values["location"], d.Values["note"]);
    private async void ToggleVehicle_Click(object sender, RoutedEventArgs e)
    {
        var v = (AdminVehiclesGrid.SelectedItem as VehicleDisplayRow)?.Item; if (v is null) { MessageBox.Show(this, "Izaberite vozilo."); return; }
        var action = v.IsActive ? "deaktivirate" : "aktivirate"; if (!Dialogs.Confirm(this, $"Da li želite da {action} vozilo {v.Registration}?")) return;
        await RunAsync(async () => { await _api.SetVehicleStatusAsync(v.Id, !v.IsActive); await LoadAdminVehiclesAsync(); await LoadDashboardAsync(); });
    }
    private async void AssignVehicle_Click(object sender, RoutedEventArgs e)
    {
        var vehicle = (AdminVehiclesGrid.SelectedItem as VehicleDisplayRow)?.Item; if (vehicle is null) { MessageBox.Show(this, "Izaberite vozilo."); return; }
        List<UserDto> users;
        try { users = (await _api.GetUsersAsync()).Where(x => x.IsActive).OrderBy(x => x.FullName).ToList(); }
        catch (Exception ex) { Dialogs.Error(this, ex); return; }
        var options = new List<KeyValuePair<string, string>> { new("", "Nije zaduženo") };
        options.AddRange(users.Select(x => new KeyValuePair<string, string>(x.Id.ToString(), $"{x.FullName} ({x.Email})")));
        var dialog = new FormDialog("Zaduženje vozila", [new("user", $"Korisnik koji je zadužio {vehicle.MakeModel} ({vehicle.Registration})", vehicle.AssignedUserId?.ToString() ?? "", options, Required: false)], "Sačuvaj") { Owner = this };
        dialog.SaveAsync = values => _api.AssignVehicleAsync(vehicle.Id, Guid.TryParse(values["user"], out var selected) ? selected : null);
        if (dialog.ShowDialog() != true) return;
        await RunAsync(async () => { await LoadAdminVehiclesAsync(); await LoadAvailableAsync(); });
    }
    private async void SetVehicleImage_Click(object sender, RoutedEventArgs e)
    {
        var vehicle = (AdminVehiclesGrid.SelectedItem as VehicleDisplayRow)?.Item; if (vehicle is null) { MessageBox.Show(this, "Izaberite vozilo."); return; }
        var picker = new OpenFileDialog { Title = $"Izaberite sliku za {vehicle.MakeModel}", Filter = "Slike (*.jpg;*.jpeg;*.png)|*.jpg;*.jpeg;*.png", CheckFileExists = true, Multiselect = false };
        if (picker.ShowDialog(this) != true) return;
        (byte[] Content, string ContentType) image;
        try { image = PrepareVehicleImage(picker.FileName); }
        catch (Exception ex) { Dialogs.Error(this, new ApiException($"Slika nije mogla biti obrađena. {ex.Message}")); return; }
        await RunAsync(async () => { await _api.SaveVehicleImageAsync(vehicle.Id, image.Content, image.ContentType); _vehicleImageCache.Remove(vehicle.Id); await LoadAdminVehiclesAsync(); await LoadAvailableAsync(); });
    }
    private async void DeleteVehicleImage_Click(object sender, RoutedEventArgs e)
    {
        var vehicle = (AdminVehiclesGrid.SelectedItem as VehicleDisplayRow)?.Item; if (vehicle is null) { MessageBox.Show(this, "Izaberite vozilo."); return; }
        if (!vehicle.HasImage) { MessageBox.Show(this, "Izabrano vozilo nema sliku."); return; }
        if (!Dialogs.Confirm(this, $"Da li želite da uklonite sliku vozila {vehicle.Registration}?")) return;
        await RunAsync(async () => { await _api.DeleteVehicleImageAsync(vehicle.Id); _vehicleImageCache.Remove(vehicle.Id); await LoadAdminVehiclesAsync(); await LoadAvailableAsync(); });
    }
    private static (byte[] Content, string ContentType) PrepareVehicleImage(string path)
    {
        var source = new BitmapImage(); source.BeginInit(); source.CacheOption = BitmapCacheOption.OnLoad; source.DecodePixelWidth = 1280; source.UriSource = new Uri(path, UriKind.Absolute); source.EndInit(); source.Freeze();
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0); converted.Freeze();
        var stride = converted.PixelWidth * 4; var pixels = new byte[stride * converted.PixelHeight]; converted.CopyPixels(pixels, stride, 0);
        var hasTransparency = false;
        for (var offset = 3; offset < pixels.Length; offset += 4)
            if (pixels[offset] < 250) { hasTransparency = true; break; }
        var minX = converted.PixelWidth; var minY = converted.PixelHeight; var maxX = -1; var maxY = -1;
        for (var y = 0; y < converted.PixelHeight; y++)
        for (var x = 0; x < converted.PixelWidth; x++)
        {
            var offset = y * stride + x * 4; var b = pixels[offset]; var g = pixels[offset + 1]; var r = pixels[offset + 2]; var a = pixels[offset + 3];
            if (hasTransparency)
            {
                if (a <= 8) continue;
            }
            else if (r >= 242 && g >= 242 && b >= 242)
            {
                pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = 255;
                continue;
            }
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        if (maxX < 0) { minX = minY = 0; maxX = converted.PixelWidth - 1; maxY = converted.PixelHeight - 1; }
        const int cropMargin = 12; minX = Math.Max(0, minX - cropMargin); minY = Math.Max(0, minY - cropMargin);
        maxX = Math.Min(converted.PixelWidth - 1, maxX + cropMargin); maxY = Math.Min(converted.PixelHeight - 1, maxY + cropMargin);
        var cleaned = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, converted.DpiX, converted.DpiY, PixelFormats.Bgra32, null, pixels, stride); cleaned.Freeze();
        var cropped = new CroppedBitmap(cleaned, new Int32Rect(minX, minY, maxX - minX + 1, maxY - minY + 1)); cropped.Freeze();
        const int width = 800, height = 500, padding = 8;
        var scale = Math.Min((width - padding * 2d) / cropped.PixelWidth, (height - padding * 2d) / cropped.PixelHeight);
        var imageWidth = cropped.PixelWidth * scale; var imageHeight = cropped.PixelHeight * scale;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            if (!hasTransparency) drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            drawing.DrawImage(cropped, new Rect((width - imageWidth) / 2, (height - imageHeight) / 2, imageWidth, imageHeight));
        }
        var standardized = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); standardized.Render(visual); standardized.Freeze();
        BitmapEncoder encoder = hasTransparency ? new PngBitmapEncoder() : new JpegBitmapEncoder { QualityLevel = 88 }; encoder.Frames.Add(BitmapFrame.Create(standardized));
        using var output = new MemoryStream(); encoder.Save(output);
        if (output.Length > 3 * 1024 * 1024) throw new InvalidOperationException("Slika je i nakon optimizacije veća od 3 MB.");
        return (output.ToArray(), hasTransparency ? "image/png" : "image/jpeg");
    }

    private async Task LoadUsersAsync() => SetGridItems(UsersGrid, await _api.GetUsersAsync());
    private void LoadUsers_Click(object sender, RoutedEventArgs e) => _ = RunAsync(LoadUsersAsync);
    private async void AddUser_Click(object sender, RoutedEventArgs e)
    {
        var d = new FormDialog("Novi korisnik", [new("name", "Ime i prezime", Required: true), new("email", "E-mail", Required: true, Validator: EmailError), new("phone", "Kontakt telefon"), new("role", "Uloga", Roles.Employee, RoleOptions, Required: true), new("password", "Privremena lozinka", Secret: true, Required: true, Validator: PasswordError)]) { Owner = this };
        d.SaveAsync = values => _api.AddUserAsync(new(values["name"], values["email"], values["role"], values["password"], values["phone"]));
        if (d.ShowDialog() != true) return; await RunAsync(LoadUsersAsync);
    }
    private async void EditUser_Click(object sender, RoutedEventArgs e)
    {
        var u = UsersGrid.SelectedItem as UserDto; if (u is null) { MessageBox.Show(this, "Izaberite korisnika."); return; }
        var d = new FormDialog("Izmjena korisnika", [new("name", "Ime i prezime", u.FullName, Required: true), new("email", "E-mail", u.Email, ReadOnly: true), new("phone", "Kontakt telefon", u.Phone ?? ""), new("role", "Uloga", u.Role, RoleOptions, Required: true), new("active", "Aktivan nalog", u.IsActive.ToString().ToLowerInvariant(), BoolOptions, Required: true)]) { Owner = this };
        d.SaveAsync = values => _api.UpdateUserAsync(u.Id, new(values["name"], values["role"], bool.Parse(values["active"]), values["phone"]));
        if (d.ShowDialog() != true) return; await RunAsync(LoadUsersAsync);
    }
    private async void ResetPassword_Click(object sender, RoutedEventArgs e)
    {
        var u = UsersGrid.SelectedItem as UserDto; if (u is null) { MessageBox.Show(this, "Izaberite korisnika."); return; }
        var d = new FormDialog("Resetovanje lozinke", [new("password", "Nova privremena lozinka", Secret: true, Required: true, Validator: PasswordError)]) { Owner = this };
        d.SaveAsync = values => _api.ResetPasswordAsync(u.Id, values["password"]);
        if (d.ShowDialog() != true) return;
        await RunAsync(LoadUsersAsync); ShowSuccess("Lozinka je resetovana; korisnik je mora promijeniti pri sljedećoj prijavi.");
    }
    private static string? EmailError(string value) => value.Contains('@') && value.Contains('.') ? null : "Unesite ispravnu e-mail adresu.";
    private static string? PasswordError(string value) => PasswordPolicy.IsValid(value) ? null : PasswordPolicy.Message;

    private async Task LoadAllAsync() => SetGridItems(AllReservationsGrid, (await _api.GetAllReservationsAsync()).Select(x => new ReservationRow(x)).ToList());
    private void LoadAllReservations_Click(object sender, RoutedEventArgs e) => _ = RunAsync(LoadAllAsync);
    private async void EditAll_Click(object sender, RoutedEventArgs e) => await EditReservationAsync((AllReservationsGrid.SelectedItem as ReservationRow)?.Item, true);
    private async void CancelAll_Click(object sender, RoutedEventArgs e) => await CancelReservationAsync((AllReservationsGrid.SelectedItem as ReservationRow)?.Item, true);

    private async Task LoadUnavailabilityAsync() => SetGridItems(UnavailabilityGrid, (await _api.GetUnavailabilityAsync()).Select(x => new UnavailabilityRow(x)).ToList());
    private void LoadUnavailability_Click(object sender, RoutedEventArgs e) => _ = RunAsync(LoadUnavailabilityAsync);
    private async void AddUnavailability_Click(object sender, RoutedEventArgs e)
    {
        List<VehicleDto> vehicles; try { vehicles = await _api.GetVehiclesAsync(includeInactive: true); } catch (Exception ex) { Dialogs.Error(this, ex); return; }
        if (vehicles.Count == 0) { MessageBox.Show(this, "Nema vozila."); return; }
        var options = vehicles.Select(x => new KeyValuePair<string, string>(x.Id.ToString(), $"{x.MakeModel} ({x.Registration})")).ToList(); var now = LocalTime.Now.AddHours(1);
        var fields = new[] { new FormField("vehicle", "Vozilo", vehicles[0].Id.ToString(), options, Required: true), new FormField("start", "Početak (dd.MM.gggg HH:mm)", ShowInput(now), Required: true, Validator: DateError), new FormField("end", "Kraj (dd.MM.gggg HH:mm)", ShowInput(now.AddDays(1)), Required: true, Validator: DateError), new FormField("reason", "Razlog (servis, kvar ili drugo)", Multiline: true, Required: true) };
        var d = new FormDialog("Novi period nedostupnosti", fields, formValidator: values => ParseLocal(values["end"], "Kraj") <= ParseLocal(values["start"], "Početak") ? "Kraj perioda mora biti poslije početka." : null) { Owner = this };
        d.SaveAsync = values => _api.AddUnavailabilityAsync(new(Guid.Parse(values["vehicle"]), LocalTime.ToUtc(ParseLocal(values["start"], "Početak")), LocalTime.ToUtc(ParseLocal(values["end"], "Kraj")), values["reason"]));
        if (d.ShowDialog() != true) return;
        await RunAsync(async () => { await LoadUnavailabilityAsync(); await LoadDashboardAsync(); });
    }
    private static string? DateError(string value) => DateTime.TryParseExact(value.Trim(), ["dd.MM.yyyy HH:mm", "d.M.yyyy H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? null : "Koristite format dd.MM.gggg HH:mm.";
    private async void DeleteUnavailability_Click(object sender, RoutedEventArgs e)
    {
        var row = UnavailabilityGrid.SelectedItem as UnavailabilityRow; if (row is null) { MessageBox.Show(this, "Izaberite period."); return; }
        if (!Dialogs.Confirm(this, "Da li želite da uklonite izabrani period nedostupnosti?")) return; await RunAsync(async () => { await _api.DeleteUnavailabilityAsync(row.Item.Id); await LoadUnavailabilityAsync(); await LoadDashboardAsync(); });
    }
    private async Task LoadAuditAsync() => SetGridItems(AuditGrid, (await _api.GetAuditAsync()).Select(x => new AuditRow(x)).ToList());
    private void LoadAudit_Click(object sender, RoutedEventArgs e) => _ = RunAsync(LoadAuditAsync);

    private async void ChangePassword_Click(object sender, RoutedEventArgs e) => await ChangePasswordAsync(false);
    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        if (!Dialogs.Confirm(this, "Odjava će završiti aktivnu sesiju. Ako je uključeno „Zapamti me”, e-mail i lozinka će ostati popunjeni na ovom računaru. Da li želite da se odjavite?")) return;
        SavePreferencesSafely();
        _api.Logout(); var application = Application.Current; application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var login = new LoginWindow(_api) { Owner = this }; Hide();
        if (login.ShowDialog() == true && _api.Session is not null)
        {
            var main = new MainWindow(_api); application.MainWindow = main; main.Show(); Close(); application.ShutdownMode = ShutdownMode.OnMainWindowClose;
        }
        else application.Shutdown();
    }
    private async Task ForcePasswordChangeAsync() { MessageBox.Show(this, "Administrator je postavio privremenu lozinku. Morate je promijeniti prije nastavka."); while (!await ChangePasswordAsync(true)) { } }
    private async Task<bool> ChangePasswordAsync(bool required)
    {
        var d = new FormDialog("Promjena lozinke", [new("current", "Trenutna lozinka", Secret: true, Required: true), new("new", "Nova lozinka", Secret: true, Required: true, Validator: PasswordError)]) { Owner = this };
        d.SaveAsync = values => _api.ChangePasswordAsync(new(values["current"], values["new"]));
        if (d.ShowDialog() != true) { if (required) MessageBox.Show(this, "Promjena lozinke je obavezna."); return !required; }
        ShowSuccess("Lozinka je promijenjena."); return true;
    }
    private async void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != MainTabs || !IsLoaded || _restoringPreferences) return;
        QueuePreferencesSave(); ApplySearch(); await RunAsync(RefreshSelectedAsync);
    }
}

public sealed record VehicleDisplayRow(VehicleDto Item, ImageSource? Image)
{
    public string Assignment => Item.AssignedUserName ?? "Nije zaduženo";
    public string Status => VehicleStates.Label(Item.StatusCode, Item.AssignedUserName) +
        (Item.StatusCode == VehicleStates.Assigned && Item.HasReservation ? " • rezervisano" : "");
    public Brush StatusBackground => Item.StatusCode switch
    {
        VehicleStates.Service => Brushes.PapayaWhip, VehicleStates.Reserved => Brushes.LightCyan,
        VehicleStates.Assigned => Brushes.Lavender, VehicleStates.Inactive => Brushes.Gainsboro, _ => Brushes.Honeydew
    };
    public Brush StatusForeground => Item.StatusCode switch
    {
        VehicleStates.Service => Brushes.SaddleBrown, VehicleStates.Reserved => Brushes.DarkBlue,
        VehicleStates.Assigned => Brushes.Indigo, VehicleStates.Inactive => Brushes.DimGray, _ => Brushes.DarkGreen
    };
}
