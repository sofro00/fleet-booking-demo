using System.Collections;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FleetBooking.Client;
using FleetBooking.Shared;

internal static class AdminTableChecks
{
    private sealed record Fixture(string Grid, int Tab, string[] Headers, string Sort, IEnumerable Items, int Count);
    public static void Run(string outputDirectory)
    {
        var now = DateTime.UtcNow;
        var vehicle = new VehicleDto(Guid.NewGuid(), "Renault Fluence", "TEST-01", 5, "Dizel", "Podgorica", true, null,
            AssignedUserId: Guid.NewGuid(), AssignedUserName: "Test Zaduženi Korisnik", StatusCode: VehicleStates.Assigned);
        var reservation = new ReservationDto(Guid.NewGuid(), vehicle.Id, vehicle.MakeModel, vehicle.Registration, Guid.NewGuid(),
            "Test Zaposleni", now.AddDays(2), now.AddDays(3), "Transfer robe između radnji", "Tivat", "Podgorica", "", null, null,
            ReservationStatuses.Active, null, now);
        var longText = "Duži tekst za provjeru prikaza: sadržaj ostaje dostupan u punom obliku preko oblačića, bez širenja cijele tabele.";
        var fixtures = new[]
        {
            new Fixture("AdminVehiclesGrid", 0, ["Slika", "Marka i model", "Registracija", "Status sada", "Sjedišta", "Gorivo", "Lokacija", "Aktivno"], "Item.MakeModel",
                new[] { new VehicleDisplayRow(vehicle, null), new VehicleDisplayRow(vehicle with { Id = Guid.NewGuid(), MakeModel = "Renault Clio", AssignedUserId = null,
                    AssignedUserName = null, StatusCode = VehicleStates.Inactive, IsActive = false }, null) }, 2),
            new Fixture("AllReservationsGrid", 2, ["Zaposleni", "Vozilo", "Početak", "Kraj", "Svrha", "Status"], "Employee",
                new[] { new ReservationRow(reservation), new ReservationRow(reservation with { EmployeeName = "Drugi Korisnik", Status = ReservationStatuses.Cancelled }) }, 2),
            new Fixture("UnavailabilityGrid", 3, ["Vozilo", "Početak", "Kraj", "Razlog"], "Reason",
                new[] { new UnavailabilityRow(new(Guid.NewGuid(), vehicle.Id, vehicle.MakeModel, now, now.AddDays(1), "Redovni servis")),
                    new UnavailabilityRow(new(Guid.NewGuid(), vehicle.Id, "Renault Clio", now, now.AddDays(2), longText)) }, 2),
            new Fixture("AuditGrid", 4, ["Vrijeme", "Korisnik", "Radnja", "Vrsta", "Detalji"], "Actor",
                new[] { new AuditRow(new(1, now, "Test Administrator", "Promijenjena slika vozila", "Vehicle", vehicle.Id.ToString(), "TEST-01")),
                    new AuditRow(new(2, now, "Drugi Administrator", "Izmijenjena rezervacija", "Reservation", reservation.Id.ToString(), longText)) }, 2)
        };
        var directory = Path.Combine(outputDirectory, "admin-table-tests-" + Guid.NewGuid().ToString("N"));
        var store = new UserPreferencesStore(directory); var userId = Guid.NewGuid();
        var api = new ApiClient(new Uri("https://localhost:58443/"));
        typeof(ApiClient).GetProperty(nameof(ApiClient.Session))!.SetValue(api,
            new LoginResponse("fixture", userId, "Test Administrator", "fixture@example.invalid", Roles.Administrator, false));
        var checks = 0;
        void Check(bool success, string message) { if (!success) throw new InvalidOperationException(message); checks++; }
        foreach (var font in new[] { 12, 16 })
        foreach (var fixture in fixtures)
        {
            var preferences = new UserPreferences { TextSize = font, LastTab = 4, LastAdminTab = fixture.Tab };
            preferences.Tables[fixture.Grid] = new TablePreferences
            {
                LayoutVersion = 2,
                Columns = fixture.Headers.Select((header, i) => new ColumnPreference(i + ":" + header, i, i == 0 ? 1 : 600, i == 0 ? "Star" : "Pixel")).ToList(),
                Sort = [new(fixture.Sort, ListSortDirection.Ascending)]
            };
            store.Save(userId, preferences);
            var window = new MainWindow(api, store);
            var grid = (DataGrid)window.FindName(fixture.Grid);
            typeof(MainWindow).GetMethod("SetGridItems", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [grid, fixture.Items]);
            Check(grid.Columns.All(c => c.Width.IsStar && c.Width.Value < 600), "Stari fiksni raspored nije zamijenjen proporcionalnim: " + fixture.Grid);
            Check(CollectionViewSource.GetDefaultView(grid.ItemsSource).SortDescriptions.Any(s => s.PropertyName == fixture.Sort),
                "Sortiranje nije sačuvano: " + fixture.Grid);
            foreach (var width in new[] { 1040, 1920 })
            {
                var root = (FrameworkElement)window.Content; window.Content = null;
                var host = new Border { Width = width, Height = 820, Background = Brushes.WhiteSmoke, Child = root };
                host.SetValue(System.Windows.Documents.TextElement.FontSizeProperty, (double)font);
                host.Measure(new Size(width, 820)); host.Arrange(new Rect(0, 0, width, 820)); host.UpdateLayout();
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame); host.UpdateLayout();
                var parentWidth = ((FrameworkElement)grid.Parent).ActualWidth - grid.Margin.Left - grid.Margin.Right;
                Check(Math.Abs(grid.ActualWidth - parentWidth) <= 1 &&
                    Math.Abs(grid.Columns.Sum(c => c.ActualWidth) - grid.ActualWidth) <= 24,
                    "Tabela i kolone ne popunjavaju cijelu širinu: " + fixture.Grid);
                for (var index = 0; index < fixture.Count; index++)
                {
                    var row = grid.ItemContainerGenerator.ContainerFromIndex(index) as DataGridRow;
                    Check(row is not null, "Nedostaje red: " + fixture.Grid);
                    foreach (var column in grid.Columns)
                    {
                        var content = column.GetCellContent(row!);
                        Check(content is not null && content.ActualHeight > 0, "Ćelija nije renderovana.");
                        var cell = Parent<DataGridCell>(content!);
                        var element = column is DataGridTemplateColumn ? Descendants<Border>(content!).First() : content!;
                        var center = element.TranslatePoint(new Point(element.ActualWidth / 2, element.ActualHeight / 2), cell);
                        Check(Math.Abs(center.X - cell.ActualWidth / 2) <= 1 && Math.Abs(center.Y - cell.ActualHeight / 2) <= 1,
                            $"Ćelija nije centrirana: {fixture.Grid}/{column.Header}, tekst={font}, ekran={width}.");
                        if (element is TextBlock text)
                            Check(text.TextAlignment == TextAlignment.Center && text.VerticalAlignment == VerticalAlignment.Center &&
                                text.TextTrimming == TextTrimming.CharacterEllipsis && Equals(text.ToolTip, text.Text), "Tekst nije centriran ili nema puni opis.");
                        if (element is CheckBox checkbox)
                            Check(!checkbox.IsHitTestVisible && !checkbox.Focusable, "Kućica mora biti samo za čitanje.");
                    }
                }
                SaveImage(host, Path.Combine(outputDirectory, $"admin-{fixture.Grid}-v2.10.3-{width}-font{font}.png"), width, 820);
                host.Child = null; window.Content = root;
            }
            var layout = new TableLayout(grid, 3);
            var before = grid.Columns[1].Width.Value; grid.Columns[1].Width = before + 10;
            var captured = layout.Capture(); grid.Columns[1].Width = before; layout.Restore(captured);
            Check(captured.LayoutVersion == 3 && Math.Abs(grid.Columns[1].Width.Value - before - 10) < 0.01, "Nove širine se ne pamte.");
            window.Close();
        }
        store.Save(userId, new UserPreferences { LastTab = 4, LastAdminTab = 5 });
        var maintenance = new MainWindow(api, store);
        var summary = (TextBlock)maintenance.FindName("BackupSummaryText");
        summary.Text = "Automatske kopije: uključene\nRaspored: svakog dana u 02:00\nPosljednja uspješna kopija: 19.10.2026.\n\n" + longText;
        var maintenanceRoot = (FrameworkElement)maintenance.Content; maintenance.Content = null;
        var maintenanceHost = new Border { Width = 1920, Height = 820, Child = maintenanceRoot, Background = Brushes.WhiteSmoke };
        maintenanceHost.Measure(new Size(1920, 820)); maintenanceHost.Arrange(new Rect(0, 0, 1920, 820)); maintenanceHost.UpdateLayout();
        Check(Parent<Border>(summary).ActualWidth <= 760 && summary.TextWrapping == TextWrapping.Wrap, "Održavanje treba imati kompaktan čitljiv blok.");
        SaveImage(maintenanceHost, Path.Combine(outputDirectory, "admin-maintenance-v2.10.2.png"), 1920, 820);
        maintenanceHost.Child = null; maintenance.Content = maintenanceRoot; maintenance.Close();
        Console.WriteLine($"Ostala Administracija: {checks} provjera OK; 4 tabele, centar do 1 px, tekst 12/16, 1040/1920 px, migracija i sortiranje; bez produkcionog API-ja.");
    }
    private static void SaveImage(FrameworkElement visual, string path, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    private static T Parent<T>(DependencyObject child) where T : DependencyObject
    {
        for (var element = VisualTreeHelper.GetParent(child); element is not null; element = VisualTreeHelper.GetParent(element))
            if (element is T found) return found;
        throw new InvalidOperationException("Nema roditeljskog elementa.");
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) yield return found;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
