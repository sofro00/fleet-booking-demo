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

internal static class UsersTableChecks
{
    public static void Run(string outputDirectory)
    {
        var fixtureDirectory = Path.Combine(outputDirectory, "users-table-tests-" + Guid.NewGuid().ToString("N"));
        var store = new UserPreferencesStore(fixtureDirectory);
        var userId = Guid.NewGuid();
        var api = new ApiClient(new Uri("https://localhost:58443/"));
        typeof(ApiClient).GetProperty(nameof(ApiClient.Session))!.SetValue(api,
            new LoginResponse("fixture", userId, "Test Administrator", "layout.xxxx@example.com", Roles.Administrator, false));
        var legacy = new UserPreferences { TextSize = 12, LastTab = 4, LastAdminTab = 1 };
        legacy.Tables["UsersGrid"] = new TablePreferences
        {
            LayoutVersion = 2,
            Columns = new[] { "Ime i prezime", "E-mail", "Uloga", "Telefon", "Aktivan", "Mora promijeniti lozinku" }
                .Select((header, i) => new ColumnPreference(i + ":" + header, i, i == 0 ? 1 : 220, i == 0 ? "Star" : "Pixel")).ToList(),
            Sort = [new("FullName", ListSortDirection.Ascending)]
        };
        var fixtures = new[]
        {
            new UserDto(Guid.NewGuid(), "Kristina Popovic (Drakulovic)", "layout.xxxxxxxxxxxxxxx@example.com", Roles.Employee, true, true, null),
            new UserDto(Guid.NewGuid(), "Anica Spasojevic Radulovic", "layout.xxxxxxxxxxxx@example.com", Roles.Employee, true, true, "+382 67 123 456"),
            new UserDto(Guid.NewGuid(), "IT Administrator", "layout.xx@example.com", Roles.Administrator, true, false, null)
        };
        var checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; }
        foreach (var font in new[] { 12, 16 })
        {
            legacy.TextSize = font; store.Save(userId, legacy);
            var window = new MainWindow(api, store);
            var grid = (DataGrid)window.FindName("UsersGrid");
            typeof(MainWindow).GetMethod("SetGridItems", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [grid, fixtures]);
            Check(grid.Columns.All(c => c.Width.IsStar) && Math.Abs(grid.Columns[0].Width.Value - 260) < 0.01,
                "Stari raspored ne smije vratiti fiksne širine ili nesrazmjernu kolonu imena.");
            Check(((UserDto)grid.Items[0]).FullName.StartsWith("Anica") && grid.Columns[0].SortDirection == ListSortDirection.Ascending,
                "Promjena rasporeda ne smije izgubiti zapamćeno sortiranje.");
            foreach (var width in new[] { 1040, 1920 })
            {
                var root = (FrameworkElement)window.Content;
                window.Content = null;
                var host = new Border { Width = width, Height = 820, Background = Brushes.WhiteSmoke, Child = root };
                host.SetValue(System.Windows.Documents.TextElement.FontSizeProperty, (double)font);
                host.Measure(new Size(width, 820)); host.Arrange(new Rect(0, 0, width, 820)); host.UpdateLayout();
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame); host.UpdateLayout();
                var parentWidth = ((FrameworkElement)grid.Parent).ActualWidth - grid.Margin.Left - grid.Margin.Right;
                Check(Math.Abs(grid.ActualWidth - parentWidth) <= 1 &&
                    Math.Abs(grid.Columns.Sum(c => c.ActualWidth) - grid.ActualWidth) <= 24,
                    "Tabela i kolone ne popunjavaju cijelu širinu: " + grid.ActualWidth + "/" + parentWidth + "; kolone=" + grid.Columns.Sum(c => c.ActualWidth));
                Check(!Children<TextBlock>(root).Any(t => t.Text.Contains("Ctrl+N")), "Uputstvo za prečice ostalo je u glavnom prozoru.");
                for (var rowIndex = 0; rowIndex < fixtures.Length; rowIndex++)
                {
                    var row = grid.ItemContainerGenerator.ContainerFromIndex(rowIndex) as DataGridRow;
                    Check(row is not null, "Red korisnika nije renderovan.");
                    foreach (var column in grid.Columns)
                    {
                        var content = column.GetCellContent(row!);
                        Check(content is not null && content.ActualHeight > 0, "Sadržaj ćelije nije renderovan.");
                        var cell = Parent<DataGridCell>(content!);
                        var center = content!.TranslatePoint(new Point(content.ActualWidth / 2, content.ActualHeight / 2), cell);
                        Check(Math.Abs(center.Y - cell.ActualHeight / 2) <= 1 && Math.Abs(center.X - cell.ActualWidth / 2) <= 1,
                            $"Ćelija nije centrirana: {column.Header}, font={font}, širina={width}, X={center.X - cell.ActualWidth / 2:F2}, Y={center.Y - cell.ActualHeight / 2:F2}.");
                        if (content is TextBlock text)
                            Check(text.TextAlignment == TextAlignment.Center && text.VerticalAlignment == VerticalAlignment.Center, "Tekst nije centriran.");
                        if (content is CheckBox checkbox)
                            Check(!checkbox.IsHitTestVisible && !checkbox.Focusable && checkbox.VerticalAlignment == VerticalAlignment.Center,
                                "Kućica treba da bude centrirana i samo za čitanje.");
                    }
                }
                Check(((TextBlock)grid.Columns[2].GetCellContent(grid.Items[0])).Text == "Zaposleni", "Uloga zaposlenog nije prevedena.");
                var bitmap = new RenderTargetBitmap(width, 820, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(host);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var file = File.Create(Path.Combine(outputDirectory, $"users-table-v2.10.3-{width}-font{font}.png"))) encoder.Save(file);
                host.Child = null; window.Content = root;
            }
            var layout = new TableLayout(grid, 3);
            grid.Columns[0].Width = 275;
            var adjusted = layout.Capture(); grid.Columns[0].Width = 260; layout.Restore(adjusted);
            Check(adjusted.LayoutVersion == 3 && Math.Abs(grid.Columns[0].Width.Value - 275) < 0.01,
                "Novi raspored mora i dalje pamtiti korisničko podešavanje širine.");
            window.Close();
        }
        var settings = new SettingsWindow(new UserPreferences(), _ => { });
        var settingsRoot = (FrameworkElement)settings.Content;
        settingsRoot.Measure(new Size(660, 750)); settingsRoot.Arrange(new Rect(0, 0, 660, 750)); settingsRoot.UpdateLayout();
        Check(Children<TextBlock>(settingsRoot).Any(t => t.Text.Contains("Ctrl+N")), "Prečice moraju ostati u Podešavanjima.");
        settings.Close();
        Console.WriteLine($"Tabela korisnika: {checks} provjera OK; tekst i kućice centrirani unutar 1 px; 1040/1920 px, font 12/16, migracija širina i sačuvano sortiranje.");
    }
    private static T Parent<T>(DependencyObject child) where T : DependencyObject
    {
        for (var current = VisualTreeHelper.GetParent(child); current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is T result) return result;
        throw new InvalidOperationException("Nema roditeljske ćelije.");
    }
    private static IEnumerable<T> Children<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T result) yield return result;
            foreach (var descendant in Children<T>(child)) yield return descendant;
        }
    }
}
