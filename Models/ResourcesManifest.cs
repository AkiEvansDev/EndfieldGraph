namespace EndfieldGraph.Models;

public sealed class ResourceInput
{
    public Guid Id { get; set; }
    public int Count { get; set; } = 1;
}

public sealed class Resource
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "";

    public int Count { get; set; } = 1;
    public int Seconds { get; set; } = 2;

    public List<ResourceInput> Inputs { get; set; } = [];
}

public sealed class ResourcesManifest
{
    public int Version { get; set; } = 2;
    public List<Resource> Resources { get; set; } = [];
}
