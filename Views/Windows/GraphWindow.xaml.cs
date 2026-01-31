using Wpf.Ui.Appearance;

namespace EndfieldGraph.Views.Windows;

public partial class GraphWindow
{
    public GraphWindow()
    {
        InitializeComponent();
        ApplicationThemeManager.Apply(this);
    }

    public void SetLayout(EndfieldGraph.Views.Controls.ResourceGraphLayout layout)
    {
        GraphView.Layout = layout;
        GraphView.FitToContent();
    }
}
