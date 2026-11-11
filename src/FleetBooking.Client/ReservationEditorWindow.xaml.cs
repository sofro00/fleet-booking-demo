using System.Windows;
using System.Windows.Controls;
using FleetBooking.Shared;
using System.Text.Json;

namespace FleetBooking.Client;

public partial class ReservationEditorWindow : Window
{
    private readonly List<VehicleChoice> _vehicles;
    private readonly bool _admin;
    private readonly Func<SaveReservationRequest, Task> _saveAsync;
    private readonly UnsavedChangesGuard _guard;
    public bool HasUnsavedChanges => _guard.HasChanges;

    public ReservationEditorWindow(string title, string employeeName, IEnumerable<VehicleDto> vehicles, Guid selectedVehicleId,
        DateTime start, DateTime end, ReservationDto? item, bool admin, Func<SaveReservationRequest, Task> saveAsync)
    {
        InitializeComponent(); Title = title; TitleText.Text = title; EmployeeBox.Text = employeeName; _admin = admin; _saveAsync = saveAsync;
        PurposeBox.ItemsSource = ReservationOptions.WithExisting(ReservationOptions.Purposes, item?.Purpose);
        DestinationBox.ItemsSource = ReservationOptions.WithExisting(ReservationOptions.Municipalities, item?.Destination);
        DepartureBox.ItemsSource = ReservationOptions.WithExisting(ReservationOptions.Municipalities, item?.DepartureLocation);
        _vehicles = vehicles.Select(x => new VehicleChoice(x, $"{x.MakeModel} ({x.Registration}) — {x.PickupLocation}{(x.AssignedUserName is null ? "" : $" — zadužio/la: {x.AssignedUserName}")}")).ToList();
        VehicleBox.ItemsSource = _vehicles; VehicleBox.SelectedItem = _vehicles.FirstOrDefault(x => x.Vehicle.Id == selectedVehicleId) ?? _vehicles.FirstOrDefault();
        var timeValues = Enumerable.Range(0, 96).Select(i => TimeSpan.FromMinutes(i * 15)).ToList();
        AddExactTime(timeValues, start.TimeOfDay); AddExactTime(timeValues, end.TimeOfDay); timeValues.Sort();
        StartTime.ItemsSource = timeValues; EndTime.ItemsSource = timeValues; StartTime.ItemStringFormat = @"hh\:mm"; EndTime.ItemStringFormat = @"hh\:mm";
        StartDate.SelectedDate = start.Date; EndDate.SelectedDate = end.Date; StartTime.SelectedItem = start.TimeOfDay; EndTime.SelectedItem = end.TimeOfDay;
        PurposeBox.SelectedItem = item?.Purpose;
        DestinationBox.SelectedItem = item?.Destination;
        DepartureBox.SelectedItem = item?.DepartureLocation ?? ReservationOptions.DefaultDeparture;
        PhoneBox.Text = item?.ContactPhone ?? ""; PassengersBox.Text = item?.PassengerCount?.ToString() ?? ""; NoteBox.Text = item?.Note ?? "";
        AdminReasonPanel.Visibility = admin ? Visibility.Visible : Visibility.Collapsed; UpdateVehicleInfo(); UpdateDuration();
        _guard = new UnsavedChangesGuard(this, Snapshot);
        Loaded += (_, _) => { if (Owner is not null) FontSize = Owner.FontSize; VehicleBox.Focus(); };
    }

    private VehicleDto? SelectedVehicle => (VehicleBox.SelectedItem as VehicleChoice)?.Vehicle;
    private static void AddExactTime(List<TimeSpan> values, TimeSpan time) { if (!values.Contains(time)) values.Add(time); }

    private void VehicleBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        VehicleError.Text = ""; UpdateVehicleInfo();
    }
    private void UpdateVehicleInfo() => CapacityText.Text = SelectedVehicle is null ? "" : $"Kapacitet: {SelectedVehicle.SeatCount} sjedišta  •  Gorivo: {SelectedVehicle.FuelType}{(SelectedVehicle.AssignedUserName is null ? "" : $"  •  Zadužio/la: {SelectedVehicle.AssignedUserName}")}";
    private void Period_Changed(object sender, SelectionChangedEventArgs e) => UpdateDuration();
    private void UpdateDuration()
    {
        var start = ReadLocal(StartDate, StartTime); var end = ReadLocal(EndDate, EndTime);
        if (start is null || end is null || end <= start) { DurationText.Text = "Izaberite ispravan period."; return; }
        var duration = end.Value - start.Value;
        DurationText.Text = duration.TotalDays >= 1 ? $"Trajanje: {(int)duration.TotalDays} d, {duration.Hours} h, {duration.Minutes} min" : $"Trajanje: {(int)duration.TotalHours} h, {duration.Minutes} min";
    }
    private static DateTime? ReadLocal(DatePicker date, ComboBox time) => date.SelectedDate is null || time.SelectedItem is not TimeSpan value ? null : date.SelectedDate.Value.Date + value;

    private string Snapshot() => JsonSerializer.Serialize(new
    {
        Vehicle = SelectedVehicle?.Id, Start = StartDate.SelectedDate, StartText = StartDate.Text, StartTime = StartTime.SelectedItem,
        End = EndDate.SelectedDate, EndText = EndDate.Text, EndTime = EndTime.SelectedItem,
        Purpose = PurposeBox.SelectedItem, Destination = DestinationBox.SelectedItem, Departure = DepartureBox.SelectedItem,
        PhoneBox.Text, Passengers = PassengersBox.Text, Note = NoteBox.Text, Reason = AdminReasonBox.Text
    });
    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync();
    private async Task SaveAsync()
    {
        if (_guard.IsSaving) return;
        SummaryPanel.Visibility = Visibility.Collapsed;
        if (!TryBuildRequest(out var request)) return;
        _guard.IsSaving = true; ((UIElement)Content).IsEnabled = false;
        try { await _saveAsync(request!); _guard.MarkSaved(); _guard.IsSaving = false; DialogResult = true; }
        catch (Exception ex) { SummaryError.Text = ex.Message; SummaryPanel.Visibility = Visibility.Visible; }
        finally { _guard.IsSaving = false; ((UIElement)Content).IsEnabled = true; }
    }

    private bool TryBuildRequest(out SaveReservationRequest? request)
    {
        request = null; var valid = true;
        valid &= Required(SelectedVehicle is null ? "" : "ok", VehicleError);
        var start = ReadLocal(StartDate, StartTime); var end = ReadLocal(EndDate, EndTime);
        StartError.Text = start is null ? "Izaberite datum i vrijeme preuzimanja." : start < LocalTime.Now.AddSeconds(-30) ? "Rezervacija ne može početi u prošlosti." : "";
        EndError.Text = end is null ? "Izaberite datum i vrijeme vraćanja." : start is not null && end <= start ? "Vraćanje mora biti poslije preuzimanja." : "";
        valid &= string.IsNullOrEmpty(StartError.Text) && string.IsNullOrEmpty(EndError.Text);
        valid &= Required(SelectedText(PurposeBox), PurposeError); valid &= Required(SelectedText(DestinationBox), DestinationError);
        valid &= Required(SelectedText(DepartureBox), DepartureError); PhoneError.Text = "";
        int? passengers = null; PassengersError.Text = "";
        if (!string.IsNullOrWhiteSpace(PassengersBox.Text))
        {
            if (!int.TryParse(PassengersBox.Text, out var parsed) || parsed < 1) { PassengersError.Text = "Unesite pozitivan cijeli broj."; valid = false; }
            else if (SelectedVehicle is not null && parsed > SelectedVehicle.SeatCount) { PassengersError.Text = $"Vozilo ima najviše {SelectedVehicle.SeatCount} sjedišta."; valid = false; }
            else passengers = parsed;
        }
        if (_admin) valid &= Required(AdminReasonBox.Text, AdminReasonError); else AdminReasonError.Text = "";
        if (!valid || start is null || end is null || SelectedVehicle is null) return false;
        try
        {
            request = new SaveReservationRequest(SelectedVehicle.Id, LocalTime.ToUtc(start.Value), LocalTime.ToUtc(end.Value), SelectedText(PurposeBox), SelectedText(DestinationBox), SelectedText(DepartureBox), PhoneBox.Text.Trim(), passengers, Clean(NoteBox.Text), _admin ? AdminReasonBox.Text.Trim() : null);
            return true;
        }
        catch (ArgumentException) { StartError.Text = "Izabrano lokalno vrijeme nije važeće zbog promjene računanja vremena."; return false; }
    }
    private static bool Required(string value, TextBlock error) { error.Text = string.IsNullOrWhiteSpace(value) ? "Ovo polje je obavezno." : ""; return string.IsNullOrEmpty(error.Text); }
    private static string SelectedText(ComboBox field) => (field.SelectedItem as string)?.Trim() ?? "";
    private static string? Clean(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    private sealed record VehicleChoice(VehicleDto Vehicle, string DisplayName);
}
