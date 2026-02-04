using EndfieldGraph.Services.Data.Resources;
using System.IO;

namespace EndfieldGraph.Services.Data.Builds;

public interface IBuildsStore : IStore<ResourceRecord> { }

public sealed class BuildsStore(IArchiveService<ResourceRecord> archive) : Store<ResourceRecord>(
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
        "builds.egtabs"
    )
), IBuildsStore
{
    protected override bool ShouldSeedOnEmpty { get; } = false;
    protected override string? DefaultSeedPackUri { get; } = null;
}
