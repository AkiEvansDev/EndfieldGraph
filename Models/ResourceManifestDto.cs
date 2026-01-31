namespace EndfieldGraph.Models;

public sealed class ResourceManifestDto
{
    public int Version { get; set; } = 2;
    public List<ResourceDto> Resources { get; set; } = [];
}
