using EndfieldGraph.Services.Data.Resources;
using System.IO;

namespace EndfieldGraph.Services.Data.Builds;

public interface IBuildsStore : IStore<BuildRecord> { }

public sealed class BuildsStore(IArchiveService<BuildRecord> archive) : Store<BuildRecord>(
    archive,
    getId: r => r.Id,
    normalize: r => r with
    {
        Name = (r.Name ?? "").Trim(),
        Goals = [.. (r.Goals ?? []).Where(i => i.Id != Guid.Empty && i.Count > 0)]
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
