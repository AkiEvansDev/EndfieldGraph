using EndfieldGraph.ViewModels.Resource;

namespace EndfieldGraph.Services;

public interface IBuildsCalculationService
{
    BuildCalcResult Calculate(
        IReadOnlyCollection<BuildGoalSpec> goals,
        IReadOnlyCollection<ResourceItemViewModel> allResources
    );
}

public sealed class BuildsCalculationService : IBuildsCalculationService
{
    public BuildCalcResult Calculate(
        IReadOnlyCollection<BuildGoalSpec> goals,
        IReadOnlyCollection<ResourceItemViewModel> allResources
    )
    {
        var map = allResources
            .Where(r => r.Id != Guid.Empty)
            .GroupBy(r => r.Id)
            .ToDictionary(g => g.Key, g => g.First());

        var normalizedGoals = goals
            .Where(g => g.ResourceId != Guid.Empty && g.QtyPerMin > 0)
            .GroupBy(g => g.ResourceId)
            .Select(g => new BuildGoalSpec(g.Key, g.Sum(x => x.QtyPerMin)))
            .ToList();

        if (normalizedGoals.Count == 0)
        {
            return new BuildCalcResult
            {
                Leaves = [],
                Rows = [],
                NeedPerMinById = new Dictionary<Guid, double>()
            };
        }

        var reachable = new HashSet<Guid>();
        var topoParentFirst = BuildTopoParentFirst(normalizedGoals.Select(g => g.ResourceId), map, reachable);

        var need = reachable.ToDictionary(id => id, _ => 0.0);
        foreach (var g in normalizedGoals)
        {
            if (reachable.Contains(g.ResourceId))
                need[g.ResourceId] += g.QtyPerMin;
        }

        foreach (var parentId in topoParentFirst)
        {
            if (!map.TryGetValue(parentId, out var parent))
                continue;

            var needParent = need[parentId];
            if (needParent <= 0)
                continue;

            if (parent.Inputs.Count == 0)
                continue;

            var outQty = Math.Max(1, parent.OutputQty);
            var craftsPerMin = needParent / outQty;

            foreach (var inp in parent.Inputs)
            {
                var childId = inp.Id;
                if (childId == Guid.Empty) continue;
                if (!reachable.Contains(childId)) continue;

                var q = Math.Max(1, inp.Qty);
                need[childId] += craftsPerMin * q;
            }
        }

        var leaves = reachable
            .Where(id => map.TryGetValue(id, out var r) && r.Inputs.Count == 0)
            .Select(id =>
            {
                var r = map[id];
                return new BuildLeafNeed(
                    id,
                    r.Name,
                    r.IconPngBytes,
                    NeedPerMin: need[id]
                );
            })
            .OrderByDescending(x => x.NeedPerMin)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var goalSet = new HashSet<Guid>(normalizedGoals.Select(g => g.ResourceId));

        var rows = reachable
            .Where(id => map.TryGetValue(id, out _))
            .Select(id =>
            {
                var r = map[id];
                var isGoal = goalSet.Contains(id);
                var isLeaf = r.Inputs.Count == 0;
                var isCraftable = r.Inputs.Count > 0;

                if (!isGoal && !isCraftable)
                    return null;

                var outQty = Math.Max(1, r.OutputQty);
                var craftTime = Math.Max(0, r.CraftTimeSec);

                var consumed = need[id];

                if (isLeaf)
                {
                    var produced = consumed;
                    return new BuildCalcRow(
                        r.Id,
                        r.Name,
                        r.IconPngBytes,
                        IsGoal: isGoal,
                        IsLeaf: true,
                        ConsumedPerMin: consumed,
                        ProducedPerMin: produced,
                        DeltaPerMin: produced - consumed,
                        CraftsPerMin: 0,
                        Machines: 0,
                        OutputQty: outQty,
                        CraftTimeSec: craftTime
                    );
                }

                var craftsPerMin = consumed / outQty;

                int machines = craftTime <= 0
                    ? 0
                    : (int)Math.Ceiling(craftsPerMin * craftTime / 60.0);

                double producedPerMin = (machines <= 0 || craftTime <= 0)
                    ? 0
                    : machines * (60.0 / craftTime) * outQty;

                return new BuildCalcRow(
                    r.Id,
                    r.Name,
                    r.IconPngBytes,
                    IsGoal: isGoal,
                    IsLeaf: false,
                    ConsumedPerMin: consumed,
                    ProducedPerMin: producedPerMin,
                    DeltaPerMin: producedPerMin - consumed,
                    CraftsPerMin: craftsPerMin,
                    Machines: machines,
                    OutputQty: outQty,
                    CraftTimeSec: craftTime
                );
            })
            .Where(x => x is not null)
            .Select(x => x!)
            .ToList();

        var memoIntermediate = new Dictionary<Guid, HashSet<Guid>>();
        var memoDepth = new Dictionary<Guid, int>();

        var complexityById = rows
            .Select(r => r.Id)
            .Distinct()
            .ToDictionary(
                id => id,
                id => CalcComplexity(id, map, memoIntermediate, memoDepth)
            );

        rows = [.. rows
            .OrderByDescending(r => r.IsGoal)
            .ThenByDescending(r => complexityById[r.Id].IntermediateCount)
            .ThenByDescending(r => complexityById[r.Id].MaxDepth)
            .ThenByDescending(r => r.ConsumedPerMin)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)];

        return new BuildCalcResult
        {
            Leaves = leaves,
            Rows = rows,
            NeedPerMinById = need
        };
    }

    private static List<Guid> BuildTopoParentFirst(
        IEnumerable<Guid> roots,
        Dictionary<Guid, ResourceItemViewModel> map,
        HashSet<Guid> reachableOut
    )
    {
        var visiting = new HashSet<Guid>();
        var visited = new HashSet<Guid>();
        var post = new List<Guid>();

        void Dfs(Guid id)
        {
            if (id == Guid.Empty) return;
            if (!map.TryGetValue(id, out ResourceItemViewModel? node)) return;

            reachableOut.Add(id);

            if (visited.Contains(id))
                return;

            if (visiting.Contains(id))
                throw new InvalidOperationException("Cycle detected in recipes graph.");

            visiting.Add(id);
            foreach (var inp in node.Inputs)
            {
                var childId = inp.Id;
                if (childId == Guid.Empty) continue;
                if (!map.ContainsKey(childId)) continue;
                Dfs(childId);
            }

            visiting.Remove(id);
            visited.Add(id);
            post.Add(id);
        }

        foreach (var r in roots.Where(x => x != Guid.Empty).Distinct())
            Dfs(r);

        post.Reverse();
        return post;
    }

    private static (int IntermediateCount, int MaxDepth) CalcComplexity(
        Guid id,
        Dictionary<Guid, ResourceItemViewModel> map,
        Dictionary<Guid, HashSet<Guid>> memoIntermediate,
        Dictionary<Guid, int> memoDepth
    )
    {
        HashSet<Guid> DfsIntermediate(Guid cur)
        {
            if (memoIntermediate.TryGetValue(cur, out var cached))
                return cached;

            if (!map.TryGetValue(cur, out var r) || r.Inputs.Count == 0)
                return memoIntermediate[cur] = [];

            var set = new HashSet<Guid> { cur };

            foreach (var inp in r.Inputs)
            {
                if (inp.Id == Guid.Empty) continue;

                var childSet = DfsIntermediate(inp.Id);
                foreach (var mid in childSet)
                    set.Add(mid);
            }

            memoIntermediate[cur] = set;
            return set;
        }

        int DfsDepth(Guid cur)
        {
            if (memoDepth.TryGetValue(cur, out var d))
                return d;

            if (!map.TryGetValue(cur, out var r) || r.Inputs.Count == 0)
                return memoDepth[cur] = 0;

            var best = 0;
            foreach (var inp in r.Inputs)
            {
                if (inp.Id == Guid.Empty) continue;
                best = Math.Max(best, 1 + DfsDepth(inp.Id));
            }

            memoDepth[cur] = best;
            return best;
        }

        var intermediateCount = Math.Max(0, DfsIntermediate(id).Count - 1);
        var maxDepth = DfsDepth(id);

        return (intermediateCount, maxDepth);
    }
}
