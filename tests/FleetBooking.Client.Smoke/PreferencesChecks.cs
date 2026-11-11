using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FleetBooking.Client;
using FleetBooking.Shared;

internal static class PreferencesChecks
{
    private static int _checks;
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
        _checks++;
    }
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static object? Call(object instance, string method, params object?[] args) =>
        instance.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args);
    private static void Await(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            var dispatcher = Dispatcher.CurrentDispatcher;
            task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }
    private static void CleanClose(Window window)
    {
        Field<UnsavedChangesGuard>(window, "_guard").MarkSaved(); window.Close();
    }
    public static void Run(string directory)
    {
        var root = Path.Combine(directory, "preferences-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new UserPreferencesStore(root);
        var userA = Guid.NewGuid(); var userB = Guid.NewGuid();
        var settings = new UserPreferences { LastTab = 3, LastAdminTab = 2, CalendarView = 2, TextSize = 16, CompactRows = true, OnlyFreeVehicles = true };
        store.Save(userA, settings);
        var saved = store.Load(userA);
        Check(saved.LastTab == 3 && saved.CalendarView == 2 && saved.TextSize == 16 && saved.OnlyFreeVehicles, "Podešavanja nijesu sačuvana.");
        Check(store.Load(userB).TextSize == 12 && store.Load(userB).LastTab == 0, "Podešavanja dva korisnika se miješaju.");
        File.WriteAllText(Path.Combine(root, userB.ToString("N") + ".json"), "{neispravan json");
        Check(store.Load(userB).TextSize == 12 && store.LastWarning is not null, "Oštećena podešavanja moraju imati bezbjedne početne vrijednosti.");
        var invalid = new UserPreferences { TextSize = 1000, LastTab = 99, CalendarView = -1, StartupPage = "Unknown" };
        store.Save(userB, invalid); var normalized = store.Load(userB);
        Check(normalized.TextSize == 12 && normalized.CalendarView == 0 && normalized.LastTab == 5 && normalized.StartupPage == "Last", "Neispravne vrijednosti nijesu normalizovane.");

        var table = new DataGrid { AutoGenerateColumns = false };
        table.Columns.Add(new DataGridTextColumn { Header = "Naziv", Binding = new Binding(nameof(SortRow.Name)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        table.Columns.Add(new DataGridTextColumn { Header = "Broj", Binding = new Binding(nameof(SortRow.Number)), Width = 100 });
        var layout = new TableLayout(table);
        table.ItemsSource = new[] { new SortRow("B", 2), new SortRow("A", 1) };
        table.Columns[1].Width = 177; table.Columns[1].DisplayIndex = 0;
        CollectionViewSource.GetDefaultView(table.ItemsSource).SortDescriptions.Add(new SortDescription(nameof(SortRow.Number), ListSortDirection.Descending));
        var state = layout.Capture();
        saved.Tables["Test"] = state; store.Save(userA, saved);
        table.Columns[1].Width = 100; table.Columns[1].DisplayIndex = 1;
        layout.Restore(store.Load(userA).Tables["Test"]);
        Check(table.Columns[1].DisplayIndex == 0 && Math.Abs(table.Columns[1].Width.Value - 177) < 0.01 && table.Columns[0].Width.IsStar, "Širina, redosljed i zvjezdana širina nijesu vraćeni.");
        table.ItemsSource = new[] { new SortRow("D", 4), new SortRow("C", 3) }; layout.RestoreSort(state);
        Check(((SortRow)table.Items[0]).Number == 4 && table.Columns[1].SortDirection == ListSortDirection.Descending, "Sortiranje se gubi nakon osvježavanja.");
        state.Sort.Add(new SortPreference("MissingProperty", ListSortDirection.Ascending)); layout.RestoreSort(state);
        Check(CollectionViewSource.GetDefaultView(table.ItemsSource).SortDescriptions.Count == 1, "Nepoznata kolona ne smije pokvariti sortiranje.");

        var value = "početno"; var ask = 0; var discard = false; var testWindow = new Window();
        var guard = new UnsavedChangesGuard(testWindow, () => value, () => { ask++; return discard; });
        Check(!guard.HasChanges && !guard.ShouldCancelClose() && ask == 0, "Prazna/neizmijenjena forma nepotrebno pita.");
        value = "izmjena";
        Check(guard.HasChanges && guard.ShouldCancelClose() && ask == 1, "Odbijeno odbacivanje mora zadržati formu.");
        testWindow.Close();
        Check(ask == 2, "Zatvaranje prozora mora aktivirati zaštitu podataka.");
        discard = true; Check(!guard.ShouldCancelClose(), "Potvrđeno odbacivanje treba dozvoliti zatvaranje.");
        guard.IsSaving = true; Check(guard.ShouldCancelClose(), "Forma se ne smije zatvoriti tokom upisa.");
        guard.IsSaving = false; guard.MarkSaved(); Check(!guard.HasChanges, "Uspješan upis mora ukloniti oznaku izmjena."); testWindow.Close();
        Check(ClientShortcuts.Match(Key.N, ModifierKeys.Control) == ShortcutAction.NewReservation &&
            ClientShortcuts.Match(Key.F, ModifierKeys.Control) == ShortcutAction.Search &&
            ClientShortcuts.Match(Key.F5, ModifierKeys.None) == ShortcutAction.Refresh &&
            ClientShortcuts.Match(Key.N, ModifierKeys.None) == ShortcutAction.None &&
            ClientShortcuts.Match(Key.N, ModifierKeys.Control | ModifierKeys.Shift) == ShortcutAction.None,
            "Prečice ne odgovaraju dogovorenim kombinacijama.");

        var dialog = new FormDialog("Test čuvanja", [new("name", "Naziv", Required: true), new("secret", "Lozinka", Secret: true)]);
        var controls = Field<Dictionary<string, Control>>(dialog, "_controls");
        ((TextBox)controls["name"]).Text = "Uneseni naziv"; ((PasswordBox)controls["secret"]).Password = "test-ne-cuvati-na-disk";
        dialog.SaveAsync = _ => Task.FromException(new ApiException("Server nije dostupan."));
        Await((Task)Call(dialog, "SubmitAsync")!);
        Check(dialog.HasUnsavedChanges && dialog.Values["name"] == "Uneseni naziv" && dialog.Values["secret"] == "test-ne-cuvati-na-disk" &&
            Field<TextBlock>(dialog, "_formError").Text.Contains("Server nije dostupan") && ((UIElement)dialog.Content).IsEnabled, "Neuspješno čuvanje je izbrisalo ili zaključalo formu.");
        var pending = new TaskCompletionSource();
        dialog.SaveAsync = _ => pending.Task;
        var saving = (Task)Call(dialog, "SubmitAsync")!;
        Check(Field<UnsavedChangesGuard>(dialog, "_guard").ShouldCancelClose(), "Čuvanje forme ne blokira X/Esc.");
        pending.SetException(new ApiException("Prekid veze.")); Await(saving);
        Check(dialog.Values["name"] == "Uneseni naziv" && Field<TextBlock>(dialog, "_formError").Text == "Prekid veze.", "Podaci se gube poslije prekida veze.");
        CleanClose(dialog);

        var vehicle = new VehicleDto(Guid.NewGuid(), "Test vozilo", "TEST", 5, "Benzin", "Podgorica", true, null);
        var start = LocalTime.Now.Date.AddDays(2).AddHours(12);
        var reservation = new ReservationEditorWindow("Test rezervacije", "Test Korisnik", [vehicle], vehicle.Id, start, start.AddHours(2), null, false,
            _ => Task.FromException(new ApiException("Veza sa serverom je prekinuta.")));
        Check(!reservation.HasUnsavedChanges, "Početne vrijednosti nove rezervacije nijesu izmjene.");
        ((ComboBox)reservation.FindName("PurposeBox")).SelectedItem = "Terenski rad";
        ((ComboBox)reservation.FindName("DestinationBox")).SelectedItem = "Tivat";
        ((TextBox)reservation.FindName("NoteBox")).Text = "Ne smije nestati.";
        Await((Task)Call(reservation, "SaveAsync")!);
        Check(reservation.HasUnsavedChanges && ((TextBox)reservation.FindName("NoteBox")).Text == "Ne smije nestati." &&
            ((ComboBox)reservation.FindName("DestinationBox")).SelectedItem?.ToString() == "Tivat" &&
            ((Border)reservation.FindName("SummaryPanel")).Visibility == Visibility.Visible && ((UIElement)reservation.Content).IsEnabled,
            "Rezervacija ne čuva polja poslije greške servera.");
        Check(KeyboardNavigation.GetTabNavigation(reservation) == KeyboardNavigationMode.Cycle &&
            !((TextBox)reservation.FindName("EmployeeBox")).IsTabStop, "Tab navigacija nije uređena.");
        CleanClose(reservation);

        var current = new UserPreferences { TextSize = 16, ShowSuccessMessages = false };
        var settingsWindow = new SettingsWindow(current, _ => throw new IOException("Test"));
        Check(!Field<UnsavedChangesGuard>(settingsWindow, "_guard").HasChanges, "Otvaranje podešavanja ne treba označiti izmjene.");
        ((CheckBox)settingsWindow.FindName("SuccessBox")).IsChecked = true;
        Call(settingsWindow, "Save_Click", null, new RoutedEventArgs());
        Check(current.ShowSuccessMessages == false && Field<UnsavedChangesGuard>(settingsWindow, "_guard").HasChanges &&
            ((TextBlock)settingsWindow.FindName("ErrorText")).Text.Length > 0, "Greška čuvanja podešavanja nije pravilno obrađena.");
        ((TextBlock)settingsWindow.FindName("ErrorText")).Text = "";
        Render(settingsWindow, Path.Combine(directory, "settings-v2.10-smoke.png"), 740, 760);
        CleanClose(settingsWindow);

        // Employee must never restore an administrative tab, even if a local JSON file asks for it.
        var api = new ApiClient(new Uri("https://localhost:58443/"));
        typeof(ApiClient).GetProperty(nameof(ApiClient.Session))!.SetValue(api,
            new LoginResponse("fixture", userA, "Test Zaposleni", "fixture@example.invalid", Roles.Employee, false));
        saved.LastTab = 4; saved.CalendarView = 2; store.Save(userA, saved);
        var main = new MainWindow(api, store);
        Check(((TabControl)main.FindName("MainTabs")).SelectedIndex == 0 && ((TabItem)main.FindName("AdminTab")).Visibility == Visibility.Collapsed,
            "Zaposleni ne smije otvoriti administraciju preko podešavanja.");
        Check(main.FontSize == 16 && ((ComboBox)main.FindName("CalendarView")).SelectedIndex == 2, "Lični izgled i kalendar nijesu primijenjeni.");
        var grids = (DataGrid)main.FindName("VehiclesGrid");
        ((TabControl)main.FindName("MainTabs")).SelectedIndex = 1;
        Call(main, "SetGridItems", grids, new[] { new VehicleDisplayRow(vehicle, null), new VehicleDisplayRow(vehicle with { MakeModel = "Drugi model" }, null) });
        ((TextBox)main.FindName("QuickSearchBox")).Text = "Drugi model";
        Check(grids.Items.Count == 1, "Pretraga ne filtrira aktivnu tabelu.");
        Call(main, "SetGridItems", grids, new[] { new VehicleDisplayRow(vehicle, null), new VehicleDisplayRow(vehicle with { MakeModel = "Drugi model" }, null) });
        Check(grids.Items.Count == 1, "Pretraga se gubi pri osvježavanju.");
        ((TextBox)main.FindName("QuickSearchBox")).Clear();
        CollectionViewSource.GetDefaultView(grids.ItemsSource).SortDescriptions.Add(new SortDescription("Item.MakeModel", ListSortDirection.Descending));
        Call(main, "SetGridItems", grids, new[] { new VehicleDisplayRow(vehicle with { MakeModel = "A" }, null), new VehicleDisplayRow(vehicle with { MakeModel = "Z" }, null) });
        Check(((VehicleDisplayRow)grids.Items[0]).Item.MakeModel == "Z" && grids.Columns[1].SortDirection == ListSortDirection.Descending,
            "Sortiranje složenih podataka vozila gubi se pri osvježavanju.");
        Render(main, Path.Combine(directory, "client-v2.10-large-smoke.png"), 1360, 820);
        Render(main, Path.Combine(directory, "client-v2.10-small-window-smoke.png"), 1040, 720);
        main.Close();
        saved.LastTab = 3; saved.StartupPage = "Last"; store.Save(userA, saved);
        var restored = new MainWindow(api, store);
        Check(((TabControl)restored.FindName("MainTabs")).SelectedIndex == 3, "Posljednja kartica nije vraćena pri novom pokretanju.");
        restored.Close();
        saved.LastTab = 3; saved.StartupPage = "Calendar"; store.Save(userA, saved);
        var explicitStartup = new MainWindow(api, store);
        Check(((TabControl)explicitStartup.FindName("MainTabs")).SelectedIndex == 2, "Izabrani početni ekran mora imati prednost nad posljednjom karticom.");
        explicitStartup.Close();
        Console.WriteLine($"Podešavanja i zaštita: {_checks} provjera OK; izolovani fajlovi, korisnici, kolone, sortiranje, pretraga, prečice, čuvanje i prekid veze. Bez produkcionog API-ja.");
        File.WriteAllText(Path.Combine(directory, "preferences-smoke-result.json"), JsonSerializer.Serialize(new { Success = true, Checks = _checks, ProductionApiUsed = false }));
    }
    private sealed record SortRow(string Name, int Number);
    private static void Render(Window window, string path, int width, int height)
    {
        var root = (FrameworkElement)window.Content; window.Content = null;
        root.Width = double.NaN; root.Height = double.NaN;
        var host = new Border { Width = width, Height = height, Child = root, Background = Brushes.WhiteSmoke };
        host.SetValue(System.Windows.Documents.TextElement.FontSizeProperty, window.FontSize);
        host.Measure(new Size(width, height)); host.Arrange(new Rect(0, 0, width, height)); host.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (var dc = backdrop.RenderOpen()) dc.DrawRectangle(Brushes.WhiteSmoke, null, new Rect(0, 0, width, height));
        bitmap.Render(backdrop); bitmap.Render(host);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
        // Keep the content attached to the closed form so cleanup/guard checks still see the controls.
        host.Child = null; window.Content = root;
    }
}
