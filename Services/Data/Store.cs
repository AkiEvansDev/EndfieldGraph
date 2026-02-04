using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace EndfieldGraph.Services.Data;

public interface IStore<T>
{
    ReadOnlyObservableCollection<T> Items { get; }

    Task InitializeAsync(CancellationToken ct = default);

    Task ImportAsync(string archivePath, ImportMode mode, CancellationToken ct = default);
    Task ExportAsync(string archivePath, CancellationToken ct = default);

    void ApplySnapshot(IReadOnlyList<T> snapshot);
    Task FlushAsync(CancellationToken ct = default);

    IDisposable BeginBatchUpdate();
}

public class Store<T> : IStore<T>
{
    private readonly IArchiveService<T> archive;
    private readonly Func<T, Guid> getId;
    private readonly Func<T, T> normalize;

    protected virtual string LocalPath { get; }
    protected virtual bool ShouldSeedOnEmpty { get; } = true;
    protected virtual string? DefaultSeedPackUri { get; } = null;

    private readonly ObservableCollection<T> items = [];
    public ReadOnlyObservableCollection<T> Items { get; }

    private readonly DispatcherTimer debounceTimer;
    private readonly SemaphoreSlim ioGate = new(1, 1);

    private bool initialized;
    private bool dirty;
    private int batchDepth;

    public Store(
        IArchiveService<T> archive,
        Func<T, Guid> getId,
        Func<T, T> normalize,
        string localPath
    )
    {
        this.archive = archive;
        this.getId = getId;
        this.normalize = normalize;
        LocalPath = localPath;

        Items = new ReadOnlyObservableCollection<T>(items);

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
                TrySeed();

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
                items.Clear();
                foreach (var x in imported.Select(normalize))
                    items.Add(x);
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
            var imported = await archive.ImportAsync(archivePath, mode, existing: [.. Items], ct);

            using (BeginBatchUpdate())
            {
                items.Clear();
                foreach (var x in imported.Select(normalize))
                    items.Add(x);
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
            await archive.ExportAsync(archivePath, [.. Items], ct);
        }
        finally
        {
            ioGate.Release();
        }
    }

    public void ApplySnapshot(IReadOnlyList<T> snapshot)
    {
        var before = items.ToList();

        var normalized = snapshot
            .Select(normalize)
            .Where(x => getId(x) != Guid.Empty)
            .ToList();

        using (BeginBatchUpdate())
        {
            var incomingIds = new HashSet<Guid>(normalized.Select(getId));

            for (int i = items.Count - 1; i >= 0; i--)
            {
                var id = getId(items[i]);
                if (!incomingIds.Contains(id))
                    items.RemoveAt(i);
            }

            var byId = items
                .Select((x, idx) => (Id: getId(x), idx))
                .ToDictionary(x => x.Id, x => x.idx);

            foreach (var x in normalized)
            {
                var id = getId(x);

                if (byId.TryGetValue(id, out var idx))
                {
                    if (!Equals(items[idx], x))
                        items[idx] = x;
                }
                else
                {
                    items.Add(x);
                    byId[id] = items.Count - 1;
                }
            }
        }

        if (before.Count != items.Count || !before.SequenceEqual(items))
            MarkDirty();
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (!dirty) return;

        await ioGate.WaitAsync(ct);
        try
        {
            await archive.ExportAsync(LocalPath, [.. items], ct);
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

    private sealed class BatchScope(Store<T> store) : IDisposable
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
