using System.Windows;

namespace EQOverwatch.App.Controls;

/// <summary>Carries the DataContext to places outside the visual tree (DataGrid columns): put it in a resource and bind to Data.</summary>
public sealed class BindingProxy : Freezable
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy));

    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new BindingProxy();
}
