using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FleetBooking.Client;
using FleetBooking.Shared;

internal static class VehicleIssueChecks
{
    private static object? Call(object instance, string name, params object?[] args) =>
        instance.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(instance, args);
    private static T Field<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;
    private static void CloseForm(FormDialog form) { Field<UnsavedChangesGuard>(form, "_guard").MarkSaved(); form.Close(); }
    public static void Run(string outputDirectory)
    {
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; }
        var store = new UserPreferencesStore(Path.Combine(outputDirectory, "issue-ui-tests-" + Guid.NewGuid().ToString("N")));
        var vehicle = new VehicleDto(Guid.NewGuid(), "Renault Clio", "TEST", 5, "Benzin", "Podgorica", true, null);
        foreach (var role in new[] { Roles.Employee, Roles.Administrator })
        {
            var userId = Guid.NewGuid();
            store.Save(userId, new UserPreferences { LastTab = 5 });
            var api = new ApiClient(new Uri("https://localhost:58443/"));
            typeof(ApiClient).GetProperty(nameof(ApiClient.Session))!.SetValue(api,
                new LoginResponse("fixture", userId, "Test Korisnik", "fixture@example.invalid", role, false));
            var window = new MainWindow(api, store);
            Check(((TabControl)window.FindName("MainTabs")).SelectedIndex == 5, "Kartica prijava nije obnovljena.");
            Check(((Button)window.FindName("ProcessIssueButton")).Visibility == (role == Roles.Administrator ? Visibility.Visible : Visibility.Collapsed),
                "Obrada prijava nije ograničena na administratora.");
            var report = new VehicleIssueDto(Guid.NewGuid(), vehicle.Id, vehicle.MakeModel, vehicle.Registration, userId, "Test Korisnik",
                VehicleIssueCategories.Fault, "Ne radi lijevo svijetlo.", VehicleIssueStatuses.Open, null, DateTime.UtcNow, DateTime.UtcNow, null, null, 1);
            var grid = (DataGrid)window.FindName("IssuesGrid");
            Call(window, "SetGridItems", grid, new[] { new VehicleIssueRow(report), new VehicleIssueRow(report with
                { Id = Guid.NewGuid(), Status = VehicleIssueStatuses.Resolved, Description = "Nedostaje oprema.", AdminNote = "Dopunjeno." }) });
            ((TextBox)window.FindName("QuickSearchBox")).Text = "lijevo";
            Check(grid.Items.Count == 1, "Pretraga prijava ne radi.");
            ((TextBox)window.FindName("QuickSearchBox")).Clear();
            ((ComboBox)window.FindName("IssuesStatusBox")).SelectedIndex = 3; Call(window, "ApplySearch");
            Check(grid.Items.Count == 1 && ((VehicleIssueRow)grid.Items[0]).Item.Status == VehicleIssueStatuses.Resolved, "Filter statusa ne radi.");
            ((ComboBox)window.FindName("IssuesStatusBox")).SelectedIndex = 0; Call(window, "ApplySearch");
            var form = (FormDialog)Call(window, "CreateIssueDialog", new List<VehicleDto> { vehicle })!;
            var fields = Field<Dictionary<string, Control>>(form, "_controls");
            Check(Call(form, "ValidateFields") is false, "Opis mora biti obavezan.");
            ((TextBox)fields["description"]).Text = new string('x', 4001);
            Check(Call(form, "ValidateFields") is false, "Predug opis mora biti odbijen u formi.");
            ((TextBox)fields["description"]).Text = "Primijećen problem.";
            Check(Call(form, "ValidateFields") is true, "Valjana prijava nije prihvaćena.");
            form.SaveAsync = _ => Task.FromException(new ApiException("Server nije dostupan."));
            ((Task)Call(form, "SubmitAsync")!).GetAwaiter().GetResult();
            Check(form.HasUnsavedChanges && form.Values["description"] == "Primijećen problem." &&
                Field<TextBlock>(form, "_formError").Text == "Server nije dostupan.", "Greška je izbrisala prijavu.");
            CloseForm(form);
            if (role == Roles.Administrator)
            {
                var processing = (FormDialog)Call(window, "CreateIssueProcessingDialog", report)!;
                var controls = Field<Dictionary<string, Control>>(processing, "_controls");
                ((ComboBox)controls["status"]).SelectedValue = VehicleIssueStatuses.Resolved;
                Check(Call(processing, "ValidateFields") is false, "Rješavanje mora zahtijevati napomenu.");
                ((TextBox)controls["note"]).Text = "Svijetlo popravljeno.";
                Check(Call(processing, "ValidateFields") is true, "Obrada sa napomenom nije prihvaćena.");
                Check(((TextBox)controls["description"]).IsReadOnly, "Administrator ne treba da prepisuje izvorni opis.");
                CloseForm(processing);
            }
            var root = (FrameworkElement)window.Content; window.Content = null;
            var host = new Border { Width = 1360, Height = 820, Child = root, Background = Brushes.WhiteSmoke };
            host.Measure(new Size(1360, 820)); host.Arrange(new Rect(0, 0, 1360, 820)); host.UpdateLayout();
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame); host.UpdateLayout();
            Check(Math.Abs(grid.ActualWidth - ((FrameworkElement)grid.Parent).ActualWidth + grid.Margin.Left + grid.Margin.Right) <= 1 &&
                Math.Abs(grid.Columns.Sum(c => c.ActualWidth) - grid.ActualWidth) <= 24, "Tabela prijava mora popuniti cijelu širinu.");
            var image = new RenderTargetBitmap(1360, 820, 96, 96, PixelFormats.Pbgra32); image.Render(host);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (var file = File.Create(Path.Combine(outputDirectory, $"vehicle-issues-{role}-v2.11.png"))) encoder.Save(file);
            host.Child = null; window.Content = root; window.Close();
        }
        Console.WriteLine($"Prijave kvarova: {checks} WPF provjera OK; uloge, kartica, filter, obavezni opis, napomena rješenja i očuvanje forme pri prekidu veze.");
    }
}
