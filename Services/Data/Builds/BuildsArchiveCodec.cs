using EndfieldGraph.Models;
using System.IO;
using System.Text.Json;

namespace EndfieldGraph.Services.Data.Builds;

public sealed record BuildRecord(
    Guid Id,
    string Name,
    IReadOnlyList<(Guid Id, int Count)> Goals
);

public sealed class BuildsArchiveCodec : IArchiveCodec<BuildRecord>
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string ManifestEntryName => "builds.json";

    public Guid GetId(BuildRecord item) => item.Id;

    public IEnumerable<BuildRecord> Normalize(IEnumerable<BuildRecord> items)
    {
        foreach (var b in items)
        {
            if (b.Id == Guid.Empty)
                continue;

            var name = (b.Name ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name))
                name = "Build";

            var goals = (b.Goals ?? [])
                .Where(g => g.Id != Guid.Empty && g.Count > 0)
                .Select(g => (g.Id, Math.Max(1, g.Count)))
                .ToList();

            yield return b with { Name = name, Goals = goals };
        }
    }

    public (object Manifest, IReadOnlyList<ArchiveAsset> Assets) Encode(IEnumerable<BuildRecord> items)
    {
        var list = items.ToList();

        var manifest = new BuildsManifest
        {
            Version = 1,
            Builds = [.. list
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => new Build
                {
                    Id = x.Id,
                    Name = x.Name,
                    Goals = [.. (x.Goals ?? []).Select(g => new BuildGoal { Id = g.Id, Count = g.Count })]
                })]
        };

        return (manifest, Assets: []);
    }

    public async Task<object> DeserializeManifestAsync(Stream stream, CancellationToken ct)
    {
        var m = await JsonSerializer.DeserializeAsync<BuildsManifest>(stream, JsonOptions, ct);
        return m ?? throw new InvalidDataException("Failed to read builds.json");
    }

    public IReadOnlyList<string> GetRequiredAssetPaths(object manifest)
        => [];

    public IReadOnlyList<BuildRecord> Decode(object manifestObj, IReadOnlyDictionary<string, byte[]> assetsByPath)
    {
        var m = (BuildsManifest)manifestObj;
        m.Version = m.Version == 0 ? 1 : m.Version;

        if (m.Builds is null)
            return [];

        var result = new List<BuildRecord>(m.Builds.Count);

        foreach (var dto in m.Builds)
        {
            if (dto.Id == Guid.Empty)
                continue;

            var name = (dto.Name ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name))
                name = "Build";

            var goals = (dto.Goals ?? [])
                .Where(g => g.Id != Guid.Empty && g.Count > 0)
                .Select(g => (g.Id, Math.Max(1, g.Count)))
                .ToList();

            result.Add(new BuildRecord(dto.Id, name, goals));
        }

        return result;
    }
}
