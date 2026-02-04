using System.IO;

namespace EndfieldGraph.Services.Data;

public enum ImportMode
{
    ReplaceAll,
    MergeById
}

public interface IArchiveService<T>
{
    Task ExportAsync(
        string archivePath,
        IReadOnlyCollection<T> items,
        CancellationToken ct = default
    );

    Task<IReadOnlyList<T>> ImportAsync(
        string archivePath,
        ImportMode mode,
        IReadOnlyCollection<T> existing,
        CancellationToken ct = default
    );
}

public interface IArchiveCodec<T>
{
    string ManifestEntryName { get; }

    Guid GetId(T item);
    IEnumerable<T> Normalize(IEnumerable<T> items);

    (object Manifest, IReadOnlyList<ArchiveAsset> Assets) Encode(IEnumerable<T> items);
    Task<object> DeserializeManifestAsync(Stream stream, CancellationToken ct);
    IReadOnlyList<string> GetRequiredAssetPaths(object manifest);
    IReadOnlyList<T> Decode(object manifest, IReadOnlyDictionary<string, byte[]> assetsByPath);
}

public sealed record ArchiveAsset(string Path, byte[] Bytes);
