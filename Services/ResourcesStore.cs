using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace EndfieldGraph.Services;

public interface IResourcesStore
{
    ReadOnlyObservableCollection<ResourceRecord> Resources { get; }

    Task InitializeAsync(CancellationToken ct = default);

    Task ImportAsync(string archivePath, ImportMode mode, CancellationToken ct = default);
    Task ExportAsync(string archivePath, CancellationToken ct = default);

    void ApplySnapshot(IReadOnlyList<ResourceRecord> snapshot);
    Task FlushAsync(CancellationToken ct = default);

    IDisposable BeginBatchUpdate();
}

public class ResourcesStore : IResourcesStore
{

    private readonly IResourcesArchiveService archive;

    protected virtual string LocalPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "EndfieldGraph",
        "resources.egres"
    );
    protected virtual bool ShouldSeedOnEmpty { get; } = true;
    protected virtual string? DefaultSeedPackUri { get; } = "pack://application:,,,/Assets/default_resources.egres";

    private readonly ObservableCollection<ResourceRecord> resources = [];
    public ReadOnlyObservableCollection<ResourceRecord> Resources { get; }

    private readonly DispatcherTimer debounceTimer;
    private readonly SemaphoreSlim ioGate = new(1, 1);

    private bool initialized;
    private bool dirty;
    private int batchDepth;

    public ResourcesStore(IResourcesArchiveService archive)
    {
        this.archive = archive;

        Resources = new ReadOnlyObservableCollection<ResourceRecord>(resources);

        debounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(600)
        };
        debounceTimer.Tick += async (_, __) =>
        {
            try
            {
                debounceTimer.Stop();
                await FlushAsync();
            }
            catch { }
        };
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (initialized) return;
        initialized = true;

        await ioGate.WaitAsync(ct);
        try
        {
            if (!File.Exists(LocalPath) && ShouldSeedOnEmpty)
            {
                TrySeed();
            }

            var imported = File.Exists(LocalPath)
                ? await archive.ImportAsync(LocalPath, ImportMode.ReplaceAll, existing: [], ct)
                : [];

            if (imported.Count == 0 && ShouldSeedOnEmpty)
            {
                TrySeed();
                imported = await archive.ImportAsync(LocalPath, ImportMode.ReplaceAll, existing: [], ct);
            }

            using (BeginBatchUpdate())
            {
                resources.Clear();
                foreach (var r in imported)
                    resources.Add(r);
            }

            dirty = false;
        }
        finally
        {
            ioGate.Release();
        }
    }

    private void TrySeed()
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

    public async Task ImportAsync(string archivePath, ImportMode mode, CancellationToken ct = default)
    {
        await ioGate.WaitAsync(ct);
        try
        {
            var imported = await archive.ImportAsync(archivePath, mode, existing: [.. Resources], ct);

            using (BeginBatchUpdate())
            {
                resources.Clear();
                foreach (var r in imported)
                    resources.Add(r);
            }

            MarkDirty();
        }
        finally
        {
            ioGate.Release();
        }

        await FlushAsync(ct);
    }

    public async Task ExportAsync(string archivePath, CancellationToken ct = default)
    {
        await ioGate.WaitAsync(ct);
        try
        {
            await archive.ExportAsync(archivePath, [.. Resources], ct);
        }
        finally
        {
            ioGate.Release();
        }
    }

    public void ApplySnapshot(IReadOnlyList<ResourceRecord> snapshot)
    {
        var before = resources.ToList();

        var normalized = snapshot
            .Where(r => r.Id != Guid.Empty)
            .Select(r => r with
            {
                Name = (r.Name ?? "").Trim(),
                OutputQty = r.OutputQty <= 0 ? 1 : r.OutputQty,
                Inputs = [.. r.Inputs.Where(i => i.Id != Guid.Empty && i.Qty > 0)]
            })
            .ToList();

        using (BeginBatchUpdate())
        {
            var incomingIds = new HashSet<Guid>(normalized.Select(r => r.Id));

            for (int i = resources.Count - 1; i >= 0; i--)
            {
                var id = resources[i].Id;
                if (!incomingIds.Contains(id))
                    resources.RemoveAt(i);
            }

            var byId = resources
                .Select((r, idx) => (r.Id, idx))
                .ToDictionary(x => x.Id, x => x.idx);

            for (int i = 0; i < normalized.Count; i++)
            {
                var item = normalized[i];

                if (byId.TryGetValue(item.Id, out var existingIndex))
                {
                    if (!resources[existingIndex].Equals(item))
                        resources[existingIndex] = item;
                }
                else
                {
                    resources.Add(item);
                    byId[item.Id] = resources.Count - 1;
                }
            }
        }

        if (before.Count != resources.Count || !before.SequenceEqual(resources))
            MarkDirty();
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (!dirty) return;

        await ioGate.WaitAsync(ct);
        try
        {
            await archive.ExportAsync(LocalPath, [.. resources], ct);
            dirty = false;
        }
        finally
        {
            ioGate.Release();
        }
    }

    public IDisposable BeginBatchUpdate()
    {
        batchDepth++;
        return new BatchScope(this);
    }

    private sealed class BatchScope(ResourcesStore store) : IDisposable
    {
        public void Dispose()
        {
            store.batchDepth = Math.Max(0, store.batchDepth - 1);
        }
    }

    private void MarkDirty()
    {
        dirty = true;

        if (batchDepth == 0)
        {
            debounceTimer.Stop();
            debounceTimer.Start();
        }
    }
}
