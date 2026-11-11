using System.Windows;
using System.Windows.Controls;
using System.Text.Json;

namespace FleetBooking.Client;

public sealed record FormField(
    string Key,
    string Label,
    string Value = "",
    IReadOnlyList<KeyValuePair<string, string>>? Options = null,
    bool Secret = false,
    bool Multiline = false,
    bool ReadOnly = false,
    bool Required = false,
    Func<string, string?>? Validator = null);

public sealed class FormDialog : Window
{
    private readonly Dictionary<string, Control> _controls = new();
    private readonly Dictionary<string, TextBlock> _errors = new();
    private readonly Dictionary<string, FormField> _fields = new();
    private readonly Func<IReadOnlyDictionary<string, string>, string?>? _formValidator;
    private readonly UnsavedChangesGuard _guard;
    private readonly Button _confirm;
    public Func<IReadOnlyDictionary<string, string>, Task>? SaveAsync { get; set; }
    public bool HasUnsavedChanges => _guard.HasChanges;
    private readonly TextBlock _formError = new() { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 10, 4, 0) };
    public IReadOnlyDictionary<string, string> Values => _controls.ToDictionary(x => x.Key, x => x.Value switch { TextBox t => t.Text, PasswordBox p => p.Password, ComboBox c => c.SelectedValue?.ToString() ?? "", _ => "" });

    public FormDialog(string title, IEnumerable<FormField> fields, string confirmText = "Sačuvaj", Func<IReadOnlyDictionary<string, string>, string?>? formValidator = null)
    {
        _formValidator = formValidator;
        Title = title; Width = 540; MaxHeight = 780; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = System.Windows.Media.Brushes.White;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 0, 4, 18), Foreground = (System.Windows.Media.Brush)Application.Current.Resources["Navy"] });
        foreach (var field in fields)
        {
            _fields[field.Key] = field;
            panel.Children.Add(new TextBlock { Text = field.Label + (field.Required ? " *" : ""), Margin = new Thickness(4, 8, 4, 0), FontWeight = FontWeights.Medium });
            Control control;
            if (field.Options is not null)
            {
                var combo = new ComboBox { ItemsSource = field.Options, DisplayMemberPath = "Value", SelectedValuePath = "Key", MinHeight = 36 };
                combo.SelectedValue = field.Value; control = combo;
            }
            else if (field.Secret) control = new PasswordBox { Password = field.Value, MinHeight = 36 };
            else control = new TextBox { Text = field.Value, MinHeight = field.Multiline ? 70 : 36, AcceptsReturn = field.Multiline, TextWrapping = TextWrapping.Wrap, IsReadOnly = field.ReadOnly };
            _controls[field.Key] = control; panel.Children.Add(control);
            if (field.ReadOnly) control.IsTabStop = false;
            var error = new TextBlock { Foreground = System.Windows.Media.Brushes.Firebrick, FontSize = 12, Margin = new Thickness(6, 0, 4, 2), TextWrapping = TextWrapping.Wrap };
            _errors[field.Key] = error; panel.Children.Add(error);
            switch (control)
            {
                case TextBox textBox: textBox.TextChanged += (_, _) => error.Text = ""; break;
                case PasswordBox passwordBox: passwordBox.PasswordChanged += (_, _) => error.Text = ""; break;
                case ComboBox comboBox: comboBox.SelectionChanged += (_, _) => error.Text = ""; break;
            }
        }
        panel.Children.Add(_formError);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "Odustani", Background = System.Windows.Media.Brushes.Gray }; cancel.Click += (_, _) => Close();
        _confirm = new Button { Content = confirmText, IsDefault = true }; _confirm.Click += async (_, _) => await SubmitAsync();
        buttons.Children.Add(cancel); buttons.Children.Add(_confirm); panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _guard = new UnsavedChangesGuard(this, () => JsonSerializer.Serialize(Values));
        Loaded += (_, _) => { if (Owner is not null) FontSize = Owner.FontSize; _controls.Values.FirstOrDefault(c => c is not TextBox { IsReadOnly: true })?.Focus(); };
    }

    private async Task SubmitAsync()
    {
        if (_guard.IsSaving) return;
        _formError.Text = "";
        if (!ValidateFields()) return;
        _guard.IsSaving = true; ((UIElement)Content).IsEnabled = false;
        try
        {
            if (SaveAsync is not null) await SaveAsync(Values);
            _guard.MarkSaved(); _guard.IsSaving = false; DialogResult = true;
        }
        catch (Exception ex) { _formError.Text = ex.Message; }
        finally { _guard.IsSaving = false; ((UIElement)Content).IsEnabled = true; }
    }

    private bool ValidateFields()
    {
        var valid = true; Control? firstInvalid = null;
        foreach (var (key, field) in _fields)
        {
            var value = Values[key];
            var message = field.Required && string.IsNullOrWhiteSpace(value) ? "Ovo polje je obavezno." : field.Validator?.Invoke(value);
            _errors[key].Text = message ?? "";
            if (message is null) continue;
            valid = false; firstInvalid ??= _controls[key];
        }
        _formError.Text = valid ? _formValidator?.Invoke(Values) ?? "" : "";
        if (!string.IsNullOrEmpty(_formError.Text)) valid = false;
        firstInvalid?.Focus(); return valid;
    }
}

public static class Dialogs
{
    public static string? Prompt(Window owner, string title, string label, bool secret = false)
    {
        var d = new FormDialog(title, [new FormField("value", label, Secret: secret, Required: true)]) { Owner = owner };
        return d.ShowDialog() == true ? d.Values["value"] : null;
    }
    public static void Error(Window owner, Exception ex) => MessageBox.Show(owner, ex.Message, "Fleet Booking Demo", MessageBoxButton.OK, MessageBoxImage.Error);
    public static bool Confirm(Window owner, string message) => MessageBox.Show(owner, message, "Potvrda", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
}
