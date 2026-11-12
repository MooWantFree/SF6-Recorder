using System.Globalization;
using System.Windows.Data;

namespace Sf6Prototype;

public sealed class PlayerHistoryEnabledConverter : IMultiValueConverter
{
    public object Convert(object[] values,Type targetType,object parameter,CultureInfo culture) =>
        values is [string player,string current,bool favorites,string pending] && !string.IsNullOrEmpty(player) && player!=pending && (player!=current || favorites);
    public object[] ConvertBack(object value,Type[] targetTypes,object parameter,CultureInfo culture) => throw new NotSupportedException();
}

public sealed class PlayerHistoryLoadingConverter : IMultiValueConverter
{
    public object Convert(object[] values,Type targetType,object parameter,CultureInfo culture) =>
        values is [string player,string pending] && !string.IsNullOrEmpty(player) && player==pending;
    public object[] ConvertBack(object value,Type[] targetTypes,object parameter,CultureInfo culture) => throw new NotSupportedException();
}
