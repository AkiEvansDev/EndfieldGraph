using System.IO;

namespace EndfieldGraph.Services;

public interface ITabsStore : IResourcesStore { }

public sealed class TabsStore(IResourcesArchiveService archive) : ResourcesStore(archive), ITabsStore
{
    protected override string LocalPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "EndfieldGraph",
        "tabs.egtabs"
    );
    protected override bool ShouldSeedOnEmpty { get; } = false;
    protected override string? DefaultSeedPackUri { get; } = null;
}
