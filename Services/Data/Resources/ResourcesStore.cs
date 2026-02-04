using System.IO;

namespace EndfieldGraph.Services.Data.Resources;

public interface IResourcesStore : IStore<ResourceRecord> { }

public sealed class ResourcesStore(IArchiveService<ResourceRecord> archive) : Store<ResourceRecord>(
    archive,
    getId: r => r.Id,
    normalize: r => r with
    {
        Name = (r.Name ?? "").Trim(),
        Count = r.Count <= 0 ? 1 : r.Count,
        Inputs = [.. (r.Inputs ?? []).Where(i => i.Id != Guid.Empty && i.Count > 0)]
    },
    localPath: Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "EndfieldGraph",
        "resources.egres"
    )
), IResourcesStore
{
    protected override bool ShouldSeedOnEmpty { get; } = true;
    protected override string? DefaultSeedPackUri { get; } =
        "pack://application:,,,/Assets/default_resources.egres";
}

