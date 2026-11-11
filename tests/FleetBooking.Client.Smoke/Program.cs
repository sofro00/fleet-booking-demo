using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls.Primitives;
using System.Reflection;
using FleetBooking.Client;
using FleetBooking.Shared;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try { Run(args); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Run(string[] args)
    {
        // Render the real WPF controls without opening windows, making HTTP requests or touching user sessions.
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        // Resource-only test host: dispatcher layout work must not run the real login/single-instance startup.
        app.Startup -= (StartupEventHandler)Delegate.CreateDelegate(typeof(StartupEventHandler), app,
            typeof(App).GetMethod("Application_Startup", BindingFlags.Instance | BindingFlags.NonPublic)!);
        var api = new ApiClient(new Uri("https://localhost:58443/"));
        typeof(ApiClient).GetProperty(nameof(ApiClient.Session))!.SetValue(api,
            new LoginResponse("test-fixture", Guid.NewGuid(), "Demo Administrator", "admin@example.com", Roles.Administrator, false));
        var preferencesDirectory = Path.GetFullPath("artifacts/preferences-smoke-" + Guid.NewGuid().ToString("N"));
        var window = new MainWindow(api, new UserPreferencesStore(preferencesDirectory));
        var tabs = (TabControl)window.FindName("MainTabs");
        tabs.SelectedIndex = 1;
        var grid = (DataGrid)window.FindName("VehiclesGrid");
        var states = new[] { VehicleStates.Free, VehicleStates.Reserved, VehicleStates.Assigned, VehicleStates.Service, VehicleStates.Inactive };
        var models = new[] { "Toyota Corolla", "Škoda Octavia", "Renault Clio", "Ford Transit", "Volkswagen Golf" };
        var images = new[] { "toyota-corolla.png", "skoda-octavia.png", "renault-clio.png", "ford-transit.png", "volkswagen-golf.png" };
        grid.ItemsSource = states.Select((state, i) => new VehicleDisplayRow(new VehicleDto(Guid.NewGuid(),
            models[i], $"DEMO-{i + 1:000}", i == 3 ? 3 : 5, i == 0 ? "Hibrid" : i == 2 || i == 4 ? "Benzin" : "Dizel", "Demo centar", state != VehicleStates.Inactive, null,
            HasImage: true, ImageUpdatedUtc: DateTime.UtcNow,
            AssignedUserName: state == VehicleStates.Assigned ? "Ana Demo" : null, StatusCode: state,
            HasReservation: state == VehicleStates.Reserved), LoadDemoImage(images[i]))).ToList();
        var root = (FrameworkElement)window.Content;
        ((Panel)root).Background = (Brush)app.FindResource("Canvas");
        window.Content = null;
        root.Width = 1360; root.Height = 820;
        root.Measure(new Size(1360, 820)); root.Arrange(new Rect(0, 0, 1360, 820)); root.UpdateLayout();
        foreach (var name in new[] { "AvailableStartDate", "AvailableEndDate", "CalendarDate" })
        {
            var picker = (DatePicker)window.FindName(name);
            picker.ApplyTemplate();
            picker.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        }
        root.UpdateLayout();
        if (grid.Columns.Count != 8 || grid.Columns[3].Header?.ToString() != "Status za izabrani termin")
            throw new InvalidOperationException("Status kolona nije prikazana.");
        for (var i = 0; i < states.Length; i++)
        {
            if (grid.ItemContainerGenerator.ContainerFromIndex(i) is not DataGridRow)
                throw new InvalidOperationException("Red vozila nije renderovan.");
            if (((VehicleDisplayRow)grid.Items[i]).Image is not BitmapSource image || image.PixelWidth < 1)
                throw new InvalidOperationException("Demo slika vozila nije učitana.");
        }
        var output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/client-v2.8-smoke.png");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var bitmap = new RenderTargetBitmap(1360, 820, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(output)) encoder.Save(stream);
        Console.WriteLine("WPF smoke: OK; 5 vozila sa slikama, 5 statusa i 8 kolona. Slika: " + output);
        VerifyReservationEditor(Path.GetDirectoryName(output)!);
        PreferencesChecks.Run(Path.GetDirectoryName(output)!);
        UsersTableChecks.Run(Path.GetDirectoryName(output)!);
        AdminTableChecks.Run(Path.GetDirectoryName(output)!);
        VehicleIssueChecks.Run(Path.GetDirectoryName(output)!);
        window.Close();
        app.Shutdown();
    }

    private static BitmapImage LoadDemoImage(string file)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("FleetBooking.SmokeVehicles." + file)
            ?? throw new InvalidOperationException("Nedostaje demo slika vozila.");
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 240;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static void VerifyReservationEditor(string outputDirectory)
    {
        var vehicle = new VehicleDto(Guid.NewGuid(), "Renault Clio", "TEST-01", 5, "Benzin", "Bar", true, null);
        var otherVehicle = vehicle with { Id = Guid.NewGuid(), PickupLocation = "Nikšić" };
        var start = LocalTime.Now.Date.AddDays(2).AddHours(10);
        var form = new ReservationEditorWindow("Nova rezervacija", "Test Zaposleni", [vehicle, otherVehicle],
            vehicle.Id, start, start.AddHours(2), null, false, _ => Task.CompletedTask);
        var purpose = (ComboBox)form.FindName("PurposeBox");
        var destination = (ComboBox)form.FindName("DestinationBox");
        var departure = (ComboBox)form.FindName("DepartureBox");
        var note = (TextBox)form.FindName("NoteBox");
        if (destination.Items.Count != 25 || departure.Items.Count != 25 || !Equals(departure.SelectedItem, "Podgorica"))
            throw new InvalidOperationException("Opštine ili početna polazna lokacija nijesu ispravne.");
        departure.SelectedItem = "Zeta";
        ((ComboBox)form.FindName("VehicleBox")).SelectedIndex = 1;
        if (!Equals(departure.SelectedItem, "Zeta")) throw new InvalidOperationException("Promjena vozila ne smije brisati polaznu opštinu.");
        departure.SelectedItem = "Podgorica";
        purpose.SelectedItem = "Terenski rad";
        destination.SelectedItem = "Nikšić";
        note.Text = "Dogovoren obilazak radnje.";
        var validate = typeof(ReservationEditorWindow).GetMethod("TryBuildRequest", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object?[] arguments = [null];
        if (validate.Invoke(form, arguments) is not true || arguments[0] is not SaveReservationRequest request ||
            request.ContactPhone != "" || request.DepartureLocation != "Podgorica" || request.Purpose != "Terenski rad")
            throw new InvalidOperationException("Forma mora prihvatiti rezervaciju bez telefona.");
        destination.SelectedItem = null;
        arguments[0] = null;
        if (validate.Invoke(form, arguments) is not false || note.Text != "Dogovoren obilazak radnje." ||
            !Equals(purpose.SelectedItem, "Terenski rad") || string.IsNullOrEmpty(((TextBlock)form.FindName("DestinationError")).Text))
            throw new InvalidOperationException("Validacija mora zadržati unesene podatke i prikazati grešku ispod polja.");
        destination.SelectedItem = "Nikšić";
        validate.Invoke(form, arguments); // Clear validation messages.
        var root = (FrameworkElement)form.Content;
        form.Content = null;
        root.Width = 700; root.Height = 860;
        root.Measure(new Size(700, 860)); root.Arrange(new Rect(0, 0, 700, 860)); root.UpdateLayout();
        foreach (var name in new[] { "StartDate", "EndDate" })
        {
            var picker = (DatePicker)form.FindName(name);
            picker.ApplyTemplate();
            var text = (DatePickerTextBox)picker.Template.FindName("PART_TextBox", picker);
            text.ApplyTemplate();
            root.UpdateLayout();
            var host = (ScrollViewer)text.Template.FindName("PART_ContentHost", text);
            var button = (Button)picker.Template.FindName("PART_Button", picker);
            var glyph = (FrameworkElement)button.Content;
            var textCenter = host.TranslatePoint(new Point(0, host.ActualHeight / 2), picker).Y;
            var glyphCenter = glyph.TranslatePoint(new Point(0, glyph.ActualHeight / 2), picker).Y;
            if (host.ActualHeight <= 0 || Math.Abs(textCenter - glyphCenter) > 0.6)
                throw new InvalidOperationException($"Datum i ikonica nijesu centrirani: {name}, delta={textCenter - glyphCenter:F2}px.");
            var popup = (Popup)picker.Template.FindName("PART_Popup", picker);
            if (popup.Child is not Calendar calendar) throw new InvalidOperationException("Kalendar nije povezan sa datumom.");
            var original = picker.SelectedDate;
            calendar.SelectedDate = original!.Value.AddDays(1);
            if (picker.SelectedDate != original.Value.AddDays(1)) throw new InvalidOperationException("Izbor datuma iz kalendara ne radi.");
            calendar.SelectedDate = original;
            text.Text = original.Value.AddDays(1).ToString("dd.MM.yyyy");
            text.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            if (picker.SelectedDate != original.Value.AddDays(1)) throw new InvalidOperationException("Ručni unos datuma ne radi.");
            picker.SelectedDate = original;
        }
        root.UpdateLayout();
        foreach (var fontSize in new[] { 12d, 16d })
        {
            root.SetValue(System.Windows.Documents.TextElement.FontSizeProperty, fontSize);
            root.UpdateLayout();
            foreach (var name in new[] { "StartDate", "EndDate" })
            {
                var picker = (DatePicker)form.FindName(name);
                var text = (DatePickerTextBox)picker.Template.FindName("PART_TextBox", picker);
                var host = (ScrollViewer)text.Template.FindName("PART_ContentHost", text);
                var glyph = (FrameworkElement)((Button)picker.Template.FindName("PART_Button", picker)).Content;
                var textCenter = host.TranslatePoint(new Point(0, host.ActualHeight / 2), picker).Y;
                var glyphCenter = glyph.TranslatePoint(new Point(0, glyph.ActualHeight / 2), picker).Y;
                if (Math.Abs(textCenter - glyphCenter) > 0.6)
                    throw new InvalidOperationException($"Datum i ikonica nijesu centrirani za font {fontSize}: {name}.");
            }
        }
        root.SetValue(System.Windows.Documents.TextElement.FontSizeProperty, 12d);
        root.UpdateLayout();
        foreach (var scale in new[] { 1.0, 1.5 })
        {
            var bitmap = new RenderTargetBitmap((int)(700 * scale), (int)(860 * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            var backdrop = new DrawingVisual();
            using (var drawing = backdrop.RenderOpen()) drawing.DrawRectangle(Brushes.WhiteSmoke, null, new Rect(0, 0, 700, 860));
            bitmap.Render(backdrop); bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(outputDirectory, $"reservation-editor-{scale:0.0}-smoke.png"));
            encoder.Save(file);
        }
        var old = new ReservationDto(Guid.NewGuid(), vehicle.Id, vehicle.MakeModel, vehicle.Registration, Guid.NewGuid(),
            "Test Zaposleni", LocalTime.ToUtc(start), LocalTime.ToUtc(start.AddHours(2)), "Ranija svrha", "Tivat — poslovnica",
            "Stari magacin", "", null, "Napomena", ReservationStatuses.Active, null, DateTime.UtcNow);
        var editing = new ReservationEditorWindow("Izmjena rezervacije", old.EmployeeName, [vehicle], vehicle.Id,
            start, start.AddHours(2), old, false, _ => Task.CompletedTask);
        if (!Equals(((ComboBox)editing.FindName("PurposeBox")).SelectedItem, old.Purpose) ||
            !Equals(((ComboBox)editing.FindName("DestinationBox")).SelectedItem, old.Destination) ||
            !Equals(((ComboBox)editing.FindName("DepartureBox")).SelectedItem, old.DepartureLocation))
            throw new InvalidOperationException("Stare vrijednosti moraju ostati sačuvane pri izmjeni.");
        ((UnsavedChangesGuard)typeof(ReservationEditorWindow).GetField("_guard", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!).MarkSaved();
        editing.Close(); form.Close();
        Console.WriteLine("Forma: OK; 25 opština, svrhe, opcionalni telefon, očuvana polja i istorija, centriran datum/ikonica, unos i kalendar; render 100%/150%.");
    }
}
