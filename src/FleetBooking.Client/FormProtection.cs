using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FleetBooking.Client;

public sealed class UnsavedChangesGuard
{
    private readonly Func<string> _snapshot;
    private readonly Func<bool> _confirmDiscard;
    private string _baseline;
    public bool IsSaving { get; set; }
    public bool HasChanges => _snapshot() != _baseline;
    public UnsavedChangesGuard(Window window, Func<string> snapshot, Func<bool>? confirmDiscard = null)
    {
        _snapshot = snapshot;
        _baseline = snapshot();
        _confirmDiscard = confirmDiscard ?? (() => MessageBox.Show(window,
            "Imate nesačuvane izmjene. Da li želite da ih odbacite?", "Nesačuvani podaci",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);
        window.Closing += OnClosing;
        DialogKeyboard.Attach(window);
    }
    public void MarkSaved() => _baseline = _snapshot();
    public bool ShouldCancelClose() => IsSaving || (HasChanges && !_confirmDiscard());
    private void OnClosing(object? sender, CancelEventArgs e) { if (ShouldCancelClose()) e.Cancel = true; }
}

public enum ShortcutAction { None, NewReservation, Search, Refresh }
public static class ClientShortcuts
{
    public static ShortcutAction Match(Key key, ModifierKeys modifiers) => (key, modifiers) switch
    {
        (Key.N, ModifierKeys.Control) => ShortcutAction.NewReservation,
        (Key.F, ModifierKeys.Control) => ShortcutAction.Search,
        (Key.F5, ModifierKeys.None) => ShortcutAction.Refresh,
        _ => ShortcutAction.None
    };
}
public static class DialogKeyboard
{
    public static void Attach(Window window)
    {
        KeyboardNavigation.SetTabNavigation(window, KeyboardNavigationMode.Cycle);
        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || Keyboard.Modifiers != ModifierKeys.None) return;
            for (var element = Keyboard.FocusedElement as DependencyObject; element is not null;)
            {
                if (element is ComboBox { IsDropDownOpen: true } or DatePicker { IsDropDownOpen: true }) return;
                element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
            }
            e.Handled = true;
            window.Close(); // Closing always goes through UnsavedChangesGuard, including X and Alt+F4.
        };
    }
}
