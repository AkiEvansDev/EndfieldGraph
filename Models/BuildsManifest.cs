namespace EndfieldGraph.Models;

public sealed class BuildGoal
{
    public Guid Id { get; set; }
    public int Count { get; set; } = 1;
}

public sealed class Build
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public List<BuildGoal> Goals { get; set; } = [];
}

public sealed class BuildsManifest
{
    public int Version { get; set; } = 1;
    public List<Build> Builds { get; set; } = [];
}