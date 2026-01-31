using EndfieldGraph.Models;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace EndfieldGraph.Services;

public enum ImportMode
{
    ReplaceAll,
    MergeById
}

public sealed record ResourceRecord(
    Guid Id,
    string Name,
    byte[] IconPngBytes,
    int OutputQty,
    int CraftTimeSec,
    IReadOnlyList<(Guid Id, int Qty)> Inputs
);

public interface IResourcesArchiveService
{
    Task ExportAsync(
        string archivePath,
        IReadOnlyCollection<ResourceRecord> resources,
        CancellationToken ct = default
    );

    Task<IReadOnlyList<ResourceRecord>> ImportAsync(
        string archivePath,
        ImportMode mode,
        IReadOnlyCollection<ResourceRecord> existing,
        CancellationToken ct = default
    );
}

public sealed class ResourcesArchiveService : IResourcesArchiveService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public async Task ExportAsync(
        string archivePath,
        IReadOnlyCollection<ResourceRecord> resources,
        CancellationToken ct = default
    )
    {
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath) ?? ".");

        var tmpPath = archivePath + ".tmp";
        TryDelete(tmpPath);

        var manifest = new ResourceManifestDto
        {
            Version = 2,
            Resources = [.. resources
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .Select(r => new ResourceDto
                {
                    Id = r.Id,
                    Name = r.Name,
                    Icon = $"icons/{r.Id:N}.png",
                    OutputQty = r.OutputQty <= 0 ? 1 : r.OutputQty,
                    CraftTimeSec = r.CraftTimeSec,
                    Inputs = [.. r.Inputs.Select(i => new ResourceInputDto { Id = i.Id, Qty = i.Qty })]
                })]
        };

        await using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
        {
            var manifestEntry = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
            await using (var entryStream = manifestEntry.Open())
            {
                await JsonSerializer.SerializeAsync(entryStream, manifest, JsonOptions, ct);
            }

            foreach (var r in resources)
            {
                ct.ThrowIfCancellationRequested();

                var iconEntry = zip.CreateEntry($"icons/{r.Id:N}.png", CompressionLevel.Optimal);
                await using var iconStream = iconEntry.Open();
                await iconStream.WriteAsync(r.IconPngBytes, ct);
            }
        }

        if (File.Exists(archivePath))
        {
            var backup = archivePath + ".bak";
            TryDelete(backup);

            await ReplaceWithRetryAsync(tmpPath, archivePath, backup, ct);

            TryDelete(backup);
        }
        else
        {
            await MoveWithRetryAsync(tmpPath, archivePath, ct);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static async Task ReplaceWithRetryAsync(string tmp, string target, string backup, CancellationToken ct)
    {
        const int tries = 5;
        int delay = 40;

        for (int i = 0; i < tries; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                File.Replace(tmp, target, backup, ignoreMetadataErrors: true);
                return;
            }
            catch (IOException) when (i < tries - 1) { }
            catch (UnauthorizedAccessException) when (i < tries - 1) { }

            await Task.Delay(delay, ct);
            delay = Math.Min(delay * 2, 300);
        }

        File.Replace(tmp, target, backup, ignoreMetadataErrors: true);
    }

    private static async Task MoveWithRetryAsync(string tmp, string target, CancellationToken ct)
    {
        const int tries = 5;
        int delay = 40;

        for (int i = 0; i < tries; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                File.Move(tmp, target);
                return;
            }
            catch (IOException) when (i < tries - 1) { }
            catch (UnauthorizedAccessException) when (i < tries - 1) { }

            await Task.Delay(delay, ct);
            delay = Math.Min(delay * 2, 300);
        }

        File.Move(tmp, target);
    }

    public async Task<IReadOnlyList<ResourceRecord>> ImportAsync(
        string archivePath,
        ImportMode mode,
        IReadOnlyCollection<ResourceRecord> existing,
        CancellationToken ct = default
    )
    {
        if (!File.Exists(archivePath))
            throw new FileNotFoundException("Archive not found.", archivePath);

        await using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);

        var manifestEntry = zip.GetEntry("manifest.json")
            ?? throw new InvalidDataException("Archive has no manifest.json");

        ResourceManifestDto manifest;
        await using (var entryStream = manifestEntry.Open())
        {
            manifest = await JsonSerializer.DeserializeAsync<ResourceManifestDto>(entryStream, JsonOptions, ct)
                ?? throw new InvalidDataException("Failed to read manifest.json");
        }

        manifest.Version = manifest.Version == 0 ? 1 : manifest.Version;

        foreach (var dto in manifest.Resources)
        {
            dto.Name ??= "";
            dto.Icon ??= "";
            dto.OutputQty = dto.OutputQty <= 0 ? 1 : dto.OutputQty;
            dto.Inputs ??= [];
        }

        var imported = new List<ResourceRecord>(manifest.Resources.Count);

        foreach (var dto in manifest.Resources)
        {
            ct.ThrowIfCancellationRequested();

            if (dto.Id == Guid.Empty)
                continue;

            var iconPath = dto.Icon.Replace('\\', '/');
            var iconEntry = zip.GetEntry(iconPath);
            if (iconEntry is null)
                continue;

            await using var iconStream = iconEntry.Open();
            using var ms = new MemoryStream();
            await iconStream.CopyToAsync(ms, ct);

            var bytes = ms.ToArray();
            if (bytes.Length == 0)
                continue;

            imported.Add(new ResourceRecord(
                dto.Id,
                dto.Name?.Trim() ?? "",
                bytes,
                dto.OutputQty <= 0 ? 1 : dto.OutputQty,
                dto.CraftTimeSec,
                [.. dto.Inputs
                    .Where(x => x.Id != Guid.Empty && x.Qty > 0)
                    .Select(x => (x.Id, x.Qty))]
            ));
        }

        return mode switch
        {
            ImportMode.ReplaceAll => imported,
            ImportMode.MergeById => Merge(existing, imported),
            _ => imported
        };
    }

    private static IReadOnlyList<ResourceRecord> Merge(
        IReadOnlyCollection<ResourceRecord> existing,
        IReadOnlyCollection<ResourceRecord> imported
    )
    {
        var map = existing.ToDictionary(x => x.Id, x => x);

        foreach (var r in imported)
        {
            map[r.Id] = r;
        }

        return [.. map.Values.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)];
    }
}
