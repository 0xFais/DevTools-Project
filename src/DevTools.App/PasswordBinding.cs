using System.Windows;
using System.Windows.Controls;

namespace DevTools.App;

public static class PasswordBinding
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.RegisterAttached("Value", typeof(string), typeof(PasswordBinding), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, ValueChanged));
    private static readonly DependencyProperty UpdatingProperty = DependencyProperty.RegisterAttached("Updating", typeof(bool), typeof(PasswordBinding), new PropertyMetadata(false));
    public static string GetValue(DependencyObject target) => (string)target.GetValue(ValueProperty);
    public static void SetValue(DependencyObject target, string value) => target.SetValue(ValueProperty, value);
    public static void Attach(PasswordBox box) { box.PasswordChanged -= PasswordChanged; box.PasswordChanged += PasswordChanged; }
    private static void ValueChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not PasswordBox box) return;
        Attach(box);
        if (box.Password == (string?)e.NewValue) return;
        box.SetValue(UpdatingProperty, true); box.Password = (string?)e.NewValue ?? ""; box.SetValue(UpdatingProperty, false);
    }
    private static void PasswordChanged(object sender, RoutedEventArgs e)
    {
        var box = (PasswordBox)sender;
        if (!(bool)box.GetValue(UpdatingProperty)) box.SetCurrentValue(ValueProperty, box.Password);
    }
}
