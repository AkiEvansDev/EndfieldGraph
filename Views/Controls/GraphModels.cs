using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media.Imaging;

namespace EndfieldGraph.Views.Controls;

public sealed class ResourceGraphNode
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public BitmapImage? Icon { get; init; }

    public Point Position { get; init; }
    public int Level { get; init; }
}

public sealed class ResourceGraphEdge
{
    public Guid FromId { get; init; }
    public Guid ToId { get; init; }

    public int NeedQty { get; init; }
    public int TimeSec { get; init; }
}

public sealed class ResourceGraphOutLabel
{
    public Guid FromId { get; init; }
    public int NeedQty { get; init; }
    public int TimeSec { get; init; }
}

public sealed class ResourceGraphLayout
{
    public ObservableCollection<ResourceGraphNode> Nodes { get; } = [];
    public ObservableCollection<ResourceGraphEdge> Edges { get; } = [];
    public ObservableCollection<ResourceGraphOutLabel> OutLabels { get; } = [];
}
