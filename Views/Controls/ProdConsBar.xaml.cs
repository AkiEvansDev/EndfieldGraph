using System.Windows;
using System.Windows.Controls;

namespace EndfieldGraph.Views.Controls;

public partial class ProdConsBar : UserControl
{
    public static readonly DependencyProperty ProducedProperty =
        DependencyProperty.Register(nameof(Produced), typeof(double), typeof(ProdConsBar),
            new PropertyMetadata(0d, OnAnyChanged));

    public static readonly DependencyProperty ConsumedProperty =
        DependencyProperty.Register(nameof(Consumed), typeof(double), typeof(ProdConsBar),
            new PropertyMetadata(0d, OnAnyChanged));

    public double Produced
    {
        get => (double)GetValue(ProducedProperty);
        set => SetValue(ProducedProperty, value);
    }

    public double Consumed
    {
        get => (double)GetValue(ConsumedProperty);
        set => SetValue(ConsumedProperty, value);
    }

    public ProdConsBar()
    {
        InitializeComponent();
        SizeChanged += (_, __) => UpdateBars();
        Loaded += (_, __) => UpdateBars();
    }

    private static void OnAnyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((ProdConsBar)d).UpdateBars();
    }

    private void UpdateBars()
    {
        var w = ActualWidth;
        if (w <= 2) return;

        var prod = Math.Max(0, Produced);
        var cons = Math.Max(0, Consumed);

        var max = Math.Max(1e-6, Math.Max(prod, cons));

        var prodW = (prod / max) * w;
        var consW = (cons / max) * w;

        ProducedBar.Width = prodW;
        ConsumedBar.Width = consW;
    }
}
