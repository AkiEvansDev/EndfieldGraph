namespace EndfieldGraph.Models;

public sealed class ResourceDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "";

    public int OutputQty { get; set; } = 1;
    public int CraftTimeSec { get; set; } = 2;

    public List<ResourceInputDto> Inputs { get; set; } = [];
}
