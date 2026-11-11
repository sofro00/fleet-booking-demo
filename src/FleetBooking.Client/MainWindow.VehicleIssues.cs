using System.Windows;
using System.Windows.Media;
using FleetBooking.Shared;

namespace FleetBooking.Client;

public partial class MainWindow
{
    private bool _openingIssue;
    private async Task LoadIssuesAsync()
    {
        SetGridItems(IssuesGrid, (await _api.GetVehicleIssuesAsync()).Select(x => new VehicleIssueRow(x)).ToList());
        IssuesScopeText.Text = IsAdmin ? "Pregled svih prijava zaposlenih." : "Pregled vaših prijava i odgovora administratora.";
    }
    private void RefreshIssues_Click(object sender, RoutedEventArgs e) => _ = RunAsync(LoadIssuesAsync);
    private void IssuesStatus_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (IsLoaded) ApplySearch();
    }
    private async void AddIssue_Click(object sender, RoutedEventArgs e)
    {
        if (_openingIssue) return;
        _openingIssue = true;
        try
        {
            var vehicles = await _api.GetVehiclesAsync(availableOnly: false);
            if (vehicles.Count == 0) { StatusText.Text = "Nema aktivnih vozila za prijavu."; return; }
            var dialog = CreateIssueDialog(vehicles);
            dialog.Owner = this;
            if (dialog.ShowDialog() != true) return;
            IssuesStatusBox.SelectedIndex = 0;
            await RunAsync(LoadIssuesAsync);
            ShowSuccess("Prijava je sačuvana i dostupna administratorima.");
        }
        catch (Exception ex) { Dialogs.Error(this, ex); }
        finally { _openingIssue = false; }
    }
    private FormDialog CreateIssueDialog(List<VehicleDto> vehicles)
    {
        var selected = (VehiclesGrid.SelectedItem as VehicleDisplayRow)?.Item.Id ?? vehicles[0].Id;
        var options = vehicles.Select(x => new KeyValuePair<string, string>(x.Id.ToString(), $"{x.MakeModel} ({x.Registration})")).ToList();
        if (!vehicles.Any(x => x.Id == selected)) selected = vehicles[0].Id;
        var dialog = new FormDialog("Nova prijava problema", [
                new("vehicle", "Vozilo", selected.ToString(), options, Required: true),
                new("category", "Vrsta prijave", VehicleIssueCategories.Fault, VehicleIssueCategories.Options, Required: true),
                new("description", "Šta ste primijetili? (najviše 4000 znakova)", Multiline: true, Required: true, Validator: IssueTextError)
            ], "Pošalji prijavu");
        dialog.SaveAsync = values => _api.AddVehicleIssueAsync(new(Guid.Parse(values["vehicle"]), values["category"], values["description"]));
        return dialog;
    }
    private void IssueDetails_Click(object sender, RoutedEventArgs e)
    {
        if (IssuesGrid.SelectedItem is not VehicleIssueRow row) { StatusText.Text = "Izaberite prijavu."; return; }
        var item = row.Item;
        var dialog = new FormDialog("Detalji prijave", [
            new("vehicle", "Vozilo", row.Vehicle, ReadOnly: true),
            new("reporter", "Prijavio/la", item.ReporterName, ReadOnly: true),
            new("created", "Datum prijave", row.Created, ReadOnly: true),
            new("category", "Vrsta", row.Category, ReadOnly: true),
            new("description", "Opis problema", item.Description, ReadOnly: true, Multiline: true),
            new("status", "Status", row.Status, ReadOnly: true),
            new("note", "Odgovor administratora", item.AdminNote ?? "Još nema odgovora.", ReadOnly: true, Multiline: true),
            new("updated", "Posljednja obrada", item.UpdatedByName is null ? "Još nije obrađeno." : $"{item.UpdatedByName} — {LocalTime.Show(item.UpdatedUtc)}", ReadOnly: true),
            new("resolved", "Riješeno", item.ResolvedUtc is { } resolved ? LocalTime.Show(resolved) : "—", ReadOnly: true)
        ], "Zatvori") { Owner = this };
        dialog.ShowDialog();
    }
    private async void ProcessIssue_Click(object sender, RoutedEventArgs e)
    {
        if (!IsAdmin) return;
        if (IssuesGrid.SelectedItem is not VehicleIssueRow row) { StatusText.Text = "Izaberite prijavu."; return; }
        var dialog = CreateIssueProcessingDialog(row.Item);
        dialog.Owner = this;
        if (dialog.ShowDialog() != true) return;
        await RunAsync(LoadIssuesAsync);
        ShowSuccess("Obrada prijave je sačuvana.");
    }
    private FormDialog CreateIssueProcessingDialog(VehicleIssueDto item)
    {
        var row = new VehicleIssueRow(item);
        var dialog = new FormDialog("Obrada prijave", [
            new("vehicle", "Vozilo", row.Vehicle, ReadOnly: true),
            new("reporter", "Prijavio/la", item.ReporterName, ReadOnly: true),
            new("description", "Prijavljeni problem", item.Description, ReadOnly: true, Multiline: true),
            new("status", "Status", item.Status, VehicleIssueStatuses.Options, Required: true),
            new("note", "Napomena administratora (obavezna za riješenu prijavu)", item.AdminNote ?? "", Multiline: true, Validator: IssueTextError)
        ], formValidator: values => values["status"] == VehicleIssueStatuses.Resolved && string.IsNullOrWhiteSpace(values["note"])
            ? "Opišite šta je urađeno prije označavanja prijave kao riješene." : null);
        dialog.SaveAsync = values => _api.UpdateVehicleIssueAsync(item.Id, new(values["status"], values["note"], item.Version));
        return dialog;
    }
    private static string? IssueTextError(string value) => value.Trim().Length > 4000 ? "Dozvoljeno je najviše 4000 znakova." : null;
}
public sealed record VehicleIssueRow(VehicleIssueDto Item)
{
    public string Vehicle => $"{Item.Vehicle} ({Item.Registration})";
    public string Reporter => Item.ReporterName;
    public string Created => LocalTime.Show(Item.CreatedUtc);
    public string Category => VehicleIssueCategories.Label(Item.Category);
    public string Description => Item.Description;
    public string Status => VehicleIssueStatuses.Label(Item.Status);
    public Brush StatusForeground => Item.Status switch
    {
        VehicleIssueStatuses.InProgress => Brushes.SaddleBrown, VehicleIssueStatuses.Resolved => Brushes.ForestGreen, _ => Brushes.Firebrick
    };
}
