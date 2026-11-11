using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;

namespace FleetBooking.Client;

public partial class App : Application
{
    private const string InstanceMutexName = @"Local\FleetBookingDemo.FleetBooking.Client";
    private const string ActivationEventName = @"Local\FleetBookingDemo.FleetBooking.Client.Activate";
    private Mutex? _instanceMutex;
    private EventWaitHandle? _activationEvent;
    private CancellationTokenSource? _activationCancellation;

    private async void Application_Startup(object sender, StartupEventArgs e)
    {
        if (!StartSingleInstanceGuard())
        {
            Shutdown();
            return;
        }

        try
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            var settings = JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidOperationException("Neispravna konfiguracija.");
            if (!Uri.TryCreate(settings.ApiBaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("ApiBaseUrl mora biti važeća HTTPS adresa.");
            var api = new ApiClient(uri);
            if (await api.TryRestoreSessionAsync())
            {
                ShowMainWindow(api);
            }
            else
            {
                var login = new LoginWindow(api);
                if (login.ShowDialog() == true && api.Session is not null) ShowMainWindow(api); else Shutdown();
            }
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Fleet Booking Demo — konfiguracija", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(); }
    }

    private void ShowMainWindow(ApiClient api)
    {
        var main = new MainWindow(api); MainWindow = main; ShutdownMode = ShutdownMode.OnMainWindowClose; main.Show();
    }

    private bool StartSingleInstanceGuard()
    {
        _instanceMutex = new Mutex(true, InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            _instanceMutex.Dispose();
            _instanceMutex = null;
            SignalExistingInstance();
            return false;
        }

        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        _activationCancellation = new CancellationTokenSource();
        _ = Task.Run(() => WaitForActivation(_activationCancellation.Token));
        return true;
    }

    private static void SignalExistingInstance()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var activationEvent = EventWaitHandle.OpenExisting(ActivationEventName);
                activationEvent.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(100);
            }
        }
    }

    private void WaitForActivation(CancellationToken cancellationToken)
    {
        if (_activationEvent is null) return;
        var handles = new WaitHandle[] { _activationEvent, cancellationToken.WaitHandle };
        while (WaitHandle.WaitAny(handles) == 0)
            Dispatcher.BeginInvoke(ActivateCurrentWindow);
    }

    private void ActivateCurrentWindow()
    {
        var window = Windows.Cast<Window>().FirstOrDefault(x => x.IsActive)
                     ?? Windows.Cast<Window>().FirstOrDefault(x => x.IsVisible)
                     ?? MainWindow;
        if (window is null) return;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationCancellation?.Cancel();
        _activationEvent?.Set();
        _activationEvent?.Dispose();
        _activationCancellation?.Dispose();
        try { _instanceMutex?.ReleaseMutex(); } catch (ApplicationException) { }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}

public sealed record ClientSettings(string ApiBaseUrl);
