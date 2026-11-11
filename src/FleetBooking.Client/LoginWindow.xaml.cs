using System.Windows;
using System.Windows.Input;

namespace FleetBooking.Client;

public partial class LoginWindow : Window
{
    private readonly ApiClient _api;
    public LoginWindow(ApiClient api)
    {
        InitializeComponent();
        _api = api;
        var credentials = SessionStore.LoadCredentials();
        if (credentials is not null)
        {
            EmailBox.Text = credentials.Email;
            PasswordBox.Password = credentials.Password;
            RememberMeBox.IsChecked = true;
            ForgetButton.Visibility = Visibility.Visible;
            LoginButton.Focus();
        }
        else
        {
            var remembered = SessionStore.Load();
            if (remembered is not null) EmailBox.Text = remembered.Email;
            EmailBox.Focus();
        }
        ErrorText.Text = api.LastRestoreFailureMessage ?? "";
    }
    private void Forget_Click(object sender, RoutedEventArgs e)
    {
        SessionStore.ClearAll();
        EmailBox.Clear();
        PasswordBox.Clear();
        RememberMeBox.IsChecked = false;
        ForgetButton.Visibility = Visibility.Collapsed;
        ErrorText.Text = "Sačuvani podaci su uklonjeni sa ovog računara.";
        EmailBox.Focus();
    }
    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = ""; LoginButton.IsEnabled = false;
        try { await _api.LoginAsync(EmailBox.Text, PasswordBox.Password, RememberMeBox.IsChecked == true); DialogResult = true; }
        catch (Exception ex) { ErrorText.Text = ex.Message; }
        finally { LoginButton.IsEnabled = true; }
    }
    private void PasswordBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) Login_Click(sender, e); }
}
