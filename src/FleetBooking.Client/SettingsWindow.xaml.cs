using System.Text.Json;
using System.Windows;

namespace FleetBooking.Client;

public partial class SettingsWindow : Window
{
    private UserPreferences _draft;
    private readonly Action<UserPreferences> _save;
    private readonly UnsavedChangesGuard _guard;
    public bool ResetRequested { get; private set; }
    public SettingsWindow(UserPreferences current, Action<UserPreferences> save)
    {
        InitializeComponent();
        _draft = current.Copy(); _save = save; FontSize = current.TextSize;
        if (current.TextSize > 12) Width = 760;
        MaxHeight = Math.Max(520, SystemParameters.WorkArea.Height - 30);
        TextSizeBox.ItemsSource = new[] { new KeyValuePair<int, string>(12, "Standardna"), new(14, "Uvećana"), new(16, "Velika") };
        StartupBox.ItemsSource = new[] { new KeyValuePair<string, string>("Last", "Posljednja kartica"), new("Home", "Početna"),
            new("Vehicles", "Dostupna vozila"), new("Calendar", "Kalendar zauzetosti"), new("Mine", "Moje rezervacije"), new("Issues", "Prijava kvarova") };
        Populate();
        _guard = new UnsavedChangesGuard(this, () => JsonSerializer.Serialize(ReadSettings()));
    }
    private void Populate()
    {
        TextSizeBox.SelectedValue = _draft.TextSize; StartupBox.SelectedValue = _draft.StartupPage;
        CompactRowsBox.IsChecked = _draft.CompactRows; MaximizedBox.IsChecked = _draft.StartMaximized;
        DoubleClickBox.IsChecked = _draft.DoubleClickReservation; TablesBox.IsChecked = _draft.RememberTableLayouts;
        CalendarBox.IsChecked = _draft.RememberCalendar; SuccessBox.IsChecked = _draft.ShowSuccessMessages;
    }
    private UserPreferences ReadSettings()
    {
        var result = _draft.Copy();
        result.TextSize = TextSizeBox.SelectedValue is int size ? size : 12;
        result.StartupPage = StartupBox.SelectedValue as string ?? "Last";
        result.CompactRows = CompactRowsBox.IsChecked == true; result.StartMaximized = MaximizedBox.IsChecked == true;
        result.DoubleClickReservation = DoubleClickBox.IsChecked == true; result.RememberTableLayouts = TablesBox.IsChecked == true;
        result.RememberCalendar = CalendarBox.IsChecked == true; result.ShowSuccessMessages = SuccessBox.IsChecked == true;
        return result;
    }
    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        ResetRequested = true; _draft = new UserPreferences(); Populate();
        ErrorText.Text = "Početne vrijednosti biće primijenjene tek kada kliknete Sačuvaj.";
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try { _save(ReadSettings()); _guard.MarkSaved(); DialogResult = true; }
        catch { ErrorText.Text = "Podešavanja nijesu sačuvana. Provjerite pristup lokalnom korisničkom folderu i pokušajte ponovo."; }
    }
}
