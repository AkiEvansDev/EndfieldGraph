using System.IO;
using System.Windows;

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

    protected override void SeedIfNeeded()
    {
        if (DefaultSeedPackUri is null)
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(LocalPath) ?? ".");

        var bytes = ReadResourceBytes(DefaultSeedPackUri);
        File.WriteAllBytes(LocalPath, bytes);
    }

    private static byte[] ReadResourceBytes(string packUri)
    {
        var info = Application.GetResourceStream(new Uri(packUri, UriKind.Absolute))
            ?? throw new FileNotFoundException($"Resource not found: {packUri}");

        using var s = info.Stream;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}

