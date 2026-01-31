namespace EndfieldGraph.Models;

public sealed class BuildsDto
{
    public int Version { get; set; } = 1;
    public List<ResourceDto> Builds { get; set; } = [];
}
