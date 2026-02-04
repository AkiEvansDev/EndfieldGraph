using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media.Imaging;

namespace EndfieldGraph.Models;

public sealed class ResourceGraphNode
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public byte[]? Icon { get; init; }

    public Point Position { get; init; }
    public int Level { get; init; }
}

public sealed class ResourceGraphEdge
{
    public Guid FromId { get; init; }
    public Guid ToId { get; init; }

    public int NeedCount { get; init; }
    public int TimeSeconds { get; init; }
}

public sealed class ResourceGraphOutLabel
{
    public Guid FromId { get; init; }
    public int NeedCount { get; init; }
    public int TimeSeconds { get; init; }
}

public sealed class ResourceGraphLayout
{
    public ObservableCollection<ResourceGraphNode> Nodes { get; } = [];
    public ObservableCollection<ResourceGraphEdge> Edges { get; } = [];
    public ObservableCollection<ResourceGraphOutLabel> OutLabels { get; } = [];
}
