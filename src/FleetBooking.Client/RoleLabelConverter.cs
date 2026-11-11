using System.Globalization;
using System.Windows.Data;
using FleetBooking.Shared;

namespace FleetBooking.Client;

public sealed class RoleLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        Roles.Administrator => "IT administrator", Roles.Employee => "Zaposleni", _ => value
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
