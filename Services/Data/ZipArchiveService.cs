using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace EndfieldGraph.Services.Data;

public sealed class ZipArchiveService<T>(IArchiveCodec<T> codec) : IArchiveService<T>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public async Task ExportAsync(
        string archivePath,
        IReadOnlyCollection<T> items,
        CancellationToken ct = default
    )
    {
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath) ?? ".");

        var tmpPath = archivePath + ".tmp";
        TryDelete(tmpPath);

        var normalized = codec.Normalize(items).ToList();
        var (manifestObj, assets) = codec.Encode(normalized);

        await using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
        {
            var manifestEntry = zip.CreateEntry(codec.ManifestEntryName, CompressionLevel.Optimal);
            await using (var entryStream = manifestEntry.Open())
            {
                await JsonSerializer.SerializeAsync(entryStream, manifestObj, JsonOptions, ct);
            }

            foreach (var a in assets)
            {
                ct.ThrowIfCancellationRequested();

                var path = a.Path.Replace('\\', '/');
                var e = zip.CreateEntry(path, CompressionLevel.Optimal);
                await using var s = e.Open();
                await s.WriteAsync(a.Bytes, ct);
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

    public async Task<IReadOnlyList<T>> ImportAsync(
        string archivePath,
        ImportMode mode,
        IReadOnlyCollection<T> existing,
        CancellationToken ct = default
    )
    {
        if (!File.Exists(archivePath))
            throw new FileNotFoundException("Archive not found.", archivePath);

        await using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);

        var manifestEntry = zip.GetEntry(codec.ManifestEntryName)
            ?? throw new InvalidDataException($"Archive has no {codec.ManifestEntryName}");

        object manifest;
        await using (var entryStream = manifestEntry.Open())
        {
            manifest = await codec.DeserializeManifestAsync(entryStream, ct)
                ?? throw new InvalidDataException($"Failed to read {codec.ManifestEntryName}");
        }

        var requiredPaths = codec.GetRequiredAssetPaths(manifest);

        var assets = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in requiredPaths)
        {
            ct.ThrowIfCancellationRequested();

            var path = (p ?? "").Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(path)) continue;

            var entry = zip.GetEntry(path);
            if (entry is null) continue;

            await using var s = entry.Open();
            using var ms = new MemoryStream();
            await s.CopyToAsync(ms, ct);

            var bytes = ms.ToArray();
            if (bytes.Length == 0) continue;

            assets[path] = bytes;
        }

        var imported = codec.Decode(manifest, assets);
        var normalized = codec.Normalize(imported).ToList();

        return mode switch
        {
            ImportMode.ReplaceAll => normalized,
            ImportMode.MergeById => Merge(existing, normalized),
            _ => normalized
        };
    }

    private IReadOnlyList<T> Merge(IReadOnlyCollection<T> existing, IReadOnlyCollection<T> imported)
    {
        var map = existing.ToDictionary(codec.GetId, x => x);

        foreach (var item in imported)
            map[codec.GetId(item)] = item;

        return [.. map.Values];
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
}