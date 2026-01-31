using System.IO;
using System.IO.Compression;

namespace EndfieldGraph.Services;

public interface IBuildsArchiveService
{
    Task ExportAsync(
        string path,
        IReadOnlyCollection<ResourceRecord> tabs,
        IReadOnlyCollection<ResourceRecord> resources,
        CancellationToken ct = default);

    Task<(IReadOnlyList<ResourceRecord> Tabs, IReadOnlyList<ResourceRecord> Resources)> ImportAsync(
        string path,
        CancellationToken ct = default);
}

public sealed class BuildsArchiveService(IResourcesArchiveService archive) : IBuildsArchiveService
{
    private const string TabsEntryName = "tabs.egtabs";
    private const string ResourcesEntryName = "resources.egres";

    public async Task ExportAsync(
        string path,
        IReadOnlyCollection<ResourceRecord> tabs,
        IReadOnlyCollection<ResourceRecord> resources,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");

        var tmp = path + ".tmp";
        TryDelete(tmp);

        await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
        {
            var resBytes = await ExportToBytesAsync(resources, ct);
            var resEntry = zip.CreateEntry(ResourcesEntryName, CompressionLevel.Optimal);
            await using (var s = resEntry.Open())
                await s.WriteAsync(resBytes, ct);

            var tabBytes = await ExportToBytesAsync(tabs, ct);
            var tabEntry = zip.CreateEntry(TabsEntryName, CompressionLevel.Optimal);
            await using (var s = tabEntry.Open())
                await s.WriteAsync(tabBytes, ct);
        }

        if (File.Exists(path))
        {
            var bak = path + ".bak";
            TryDelete(bak);
            File.Replace(tmp, path, bak, ignoreMetadataErrors: true);
            TryDelete(bak);
        }
        else
        {
            File.Move(tmp, path);
        }
    }

    public async Task<(IReadOnlyList<ResourceRecord> Tabs, IReadOnlyList<ResourceRecord> Resources)> ImportAsync(
        string path,
        CancellationToken ct = default)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Builds archive not found.", path);

        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);

        var resEntry = zip.GetEntry(ResourcesEntryName)
            ?? throw new InvalidDataException($"Archive has no {ResourcesEntryName}");

        var tabsEntry = zip.GetEntry(TabsEntryName)
            ?? throw new InvalidDataException($"Archive has no {TabsEntryName}");

        var resBytes = await ReadAllBytesAsync(resEntry, ct);
        var tabBytes = await ReadAllBytesAsync(tabsEntry, ct);

        var resources = await ImportFromBytesAsync(resBytes, ct);
        var tabs = await ImportFromBytesAsync(tabBytes, ct);

        return (tabs, resources);
    }

    private async Task<byte[]> ExportToBytesAsync(IReadOnlyCollection<ResourceRecord> records, CancellationToken ct)
    {
        var tmp = Path.GetTempFileName();
        try
        {
            await archive.ExportAsync(tmp, records, ct);
            return await File.ReadAllBytesAsync(tmp, ct);
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    private async Task<IReadOnlyList<ResourceRecord>> ImportFromBytesAsync(byte[] bytes, CancellationToken ct)
    {
        var tmp = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(tmp, bytes, ct);
            return await archive.ImportAsync(tmp, ImportMode.ReplaceAll, existing: [], ct);
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    private static async Task<byte[]> ReadAllBytesAsync(ZipArchiveEntry entry, CancellationToken ct)
    {
        await using var s = entry.Open();
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}