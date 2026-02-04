using EndfieldGraph.Models;
using System.IO;
using System.Text.Json;

namespace EndfieldGraph.Services.Data.Resources;

public sealed record ResourceRecord(
    Guid Id,
    string Name,
    byte[] Icon,
    int Count,
    int Seconds,
    IReadOnlyList<(Guid Id, int Count)> Inputs
);

public sealed class ResourcesArchiveCodec : IArchiveCodec<ResourceRecord>
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string ManifestEntryName => "manifest.json";

    public Guid GetId(ResourceRecord item) => item.Id;

    public IEnumerable<ResourceRecord> Normalize(IEnumerable<ResourceRecord> items)
    {
        foreach (var r in items)
        {
            if (r.Id == Guid.Empty)
                continue;

            var name = (r.Name ?? "").Trim();
            var outQty = r.Count <= 0 ? 1 : r.Count;

            var inputs = (r.Inputs ?? [])
                .Where(i => i.Id != Guid.Empty && i.Count > 0)
                .ToList();

            yield return r with
            {
                Name = name,
                Count = outQty,
                Inputs = inputs
            };
        }
    }

    public (object Manifest, IReadOnlyList<ArchiveAsset> Assets) Encode(IEnumerable<ResourceRecord> items)
    {
        var list = items.ToList();

        var manifest = new ResourcesManifest
        {
            Version = 2,
            Resources = [.. list
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .Select(r => new Resource
                {
                    Id = r.Id,
                    Name = r.Name,
                    Icon = $"icons/{r.Id:N}.png",
                    Count = r.Count <= 0 ? 1 : r.Count,
                    Seconds = r.Seconds,
                    Inputs = [.. (r.Inputs ?? [])
                        .Select(i => new ResourceInput { Id = i.Id, Count = i.Count })]
                })]
        };

        var assets = new List<ArchiveAsset>(list.Count);
        foreach (var r in list)
        {
            assets.Add(new ArchiveAsset($"icons/{r.Id:N}.png", r.Icon ?? []));
        }

        return (manifest, assets);
    }

    public async Task<object> DeserializeManifestAsync(Stream stream, CancellationToken ct)
    {
        var manifest = await JsonSerializer.DeserializeAsync<ResourcesManifest>(stream, JsonOptions, ct);
        return manifest ?? throw new InvalidDataException("Failed to read manifest.json");
    }

    public IReadOnlyList<string> GetRequiredAssetPaths(object manifestObj)
    {
        var manifest = (ResourcesManifest)manifestObj;

        manifest.Version = manifest.Version == 0 ? 1 : manifest.Version;

        if (manifest.Resources is null)
            return [];

        foreach (var dto in manifest.Resources)
        {
            dto.Name ??= "";
            dto.Icon ??= "";
            dto.Count = dto.Count <= 0 ? 1 : dto.Count;
            dto.Inputs ??= [];
        }

        return [.. manifest.Resources
            .Where(x => x.Id != Guid.Empty)
            .Select(x => (x.Icon ?? "").Replace('\\', '/'))
            .Where(p => !string.IsNullOrWhiteSpace(p))];
    }

    public IReadOnlyList<ResourceRecord> Decode(object manifestObj, IReadOnlyDictionary<string, byte[]> assetsByPath)
    {
        var manifest = (ResourcesManifest)manifestObj;
        if (manifest.Resources is null)
            return [];

        var result = new List<ResourceRecord>(manifest.Resources.Count);

        foreach (var dto in manifest.Resources)
        {
            if (dto.Id == Guid.Empty)
                continue;

            var iconPath = (dto.Icon ?? "").Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(iconPath))
                continue;

            if (!assetsByPath.TryGetValue(iconPath, out var bytes))
                continue;

            if (bytes is null || bytes.Length == 0)
                continue;

            var inputs = (dto.Inputs ?? [])
                .Where(x => x.Id != Guid.Empty && x.Count > 0)
                .Select(x => (x.Id, x.Count))
                .ToList();

            result.Add(new ResourceRecord(
                dto.Id,
                (dto.Name ?? "").Trim(),
                bytes,
                dto.Count <= 0 ? 1 : dto.Count,
                dto.Seconds,
                inputs
            ));
        }

        return result;
    }
}
