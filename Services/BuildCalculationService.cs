using System;
using System.Collections.Generic;
using System.Linq;
using EndfieldGraph.Models;
using EndfieldGraph.ViewModels.Resource;

namespace EndfieldGraph.Services;

public interface IBuildCalculationService
{
    BuildCalcResult Calculate(
        IReadOnlyCollection<BuildGoalSpec> goals,
        IReadOnlyCollection<ResourceViewModel> allResources
    );
}

public sealed class BuildCalculationService : IBuildCalculationService
{
    public BuildCalcResult Calculate(
        IReadOnlyCollection<BuildGoalSpec> goals,
        IReadOnlyCollection<ResourceViewModel> allResources
    )
    {
        var map = allResources
            .Where(r => r.Id != Guid.Empty)
            .GroupBy(r => r.Id)
            .ToDictionary(g => g.Key, g => g.First());

        var normalizedGoals = goals
            .Where(g => g.Id != Guid.Empty && g.CountPerMin > 0)
            .GroupBy(g => g.Id)
            .Select(g => new BuildGoalSpec(g.Key, g.Sum(x => x.CountPerMin)))
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

        // A) reachable + calcEdges(Product -> Ingredient) from goals
        var reachable = new HashSet<Guid>();
        var calcEdges = new HashSet<(Guid Product, Guid Ingredient)>();

        CollectReachableAndCalcEdgesFromRoots(
            roots: normalizedGoals.Select(g => g.Id),
            map: map,
            reachable: reachable,
            calcEdges: calcEdges
        );

        if (reachable.Count == 0)
        {
            return new BuildCalcResult
            {
                Leaves = [],
                Rows = [],
                NeedPerMinById = new Dictionary<Guid, double>()
            };
        }

        // B) SCC + allowed 2-cycles breaking, but remember the allowed pairs
        var sccs = TarjanScc(reachable, id => GetCalcChildren(id, reachable, calcEdges));

        var removedCalcEdges = new HashSet<(Guid Product, Guid Ingredient)>();
        var cyclePairs = new List<(Guid Near, Guid Far)>(); // keep Near->Far, remove Far->Near
        bool allowAnyTwoNodeMutualIfOnlyTwoNodesInGraph = reachable.Count == 2;

        var incoming = BuildIncomingFromOutside(reachable, calcEdges);
        var goalSet = new HashSet<Guid>(normalizedGoals.Select(g => g.Id));

        foreach (var comp in sccs)
        {
            if (comp.Count <= 1)
                continue;

            if (comp.Count != 2)
                throw new InvalidOperationException($"Cycle detected in recipes graph (SCC size={comp.Count}).");

            var a = comp[0];
            var b = comp[1];

            bool hasAB = calcEdges.Contains((a, b));
            bool hasBA = calcEdges.Contains((b, a));
            if (!hasAB || !hasBA)
                throw new InvalidOperationException("Cycle detected in recipes graph (non-mutual SCC).");

            bool isSelfRenewable = IsSelfRenewablePair(a, b, map) || IsSelfRenewablePair(b, a, map);
            bool allowed = isSelfRenewable || allowAnyTwoNodeMutualIfOnlyTwoNodesInGraph;

            if (!allowed)
                throw new InvalidOperationException("Cycle detected in recipes graph (2-cycle not allowed).");

            // pick near
            var near = PickNearNodeInTwoCycle(a, b, goalSet, incoming);
            var far = near == a ? b : a;

            // remove far -> near
            removedCalcEdges.Add((far, near));
            cyclePairs.Add((Near: near, Far: far));
        }

        // Build final calc adjacency for DAG propagation:
        // - remove the chosen calc edge (far->near)
        // - AND remove the other internal edge too from propagation, so we can solve the 2-cycle via math
        bool IsInternalPairEdge(Guid p, Guid i)
        {
            foreach (var (near, far) in cyclePairs)
                if ((p == near && i == far) || (p == far && i == near))
                    return true;
            return false;
        }

        var calcChildren = reachable.ToDictionary(id => id, _ => new List<Guid>());
        foreach (var (p, i) in calcEdges)
        {
            if (!reachable.Contains(p) || !reachable.Contains(i)) continue;
            if (removedCalcEdges.Contains((p, i))) continue;
            if (IsInternalPairEdge(p, i)) continue; // IMPORTANT: remove internal 2-cycle edges from propagation graph

            calcChildren[p].Add(i);
        }
        foreach (var id in reachable)
            calcChildren[id] = [.. calcChildren[id].Distinct()];

        // sanity check (should be DAG now)
        if (DetectAnyCycle(normalizedGoals.Select(g => g.Id), reachable, calcChildren))
            throw new InvalidOperationException("Cycle detected in recipes graph after cycle-breaking step (unexpected).");

        // C) topo parent-first on DAG
        var topoParentFirst = BuildTopoParentFirstDAG(
            roots: normalizedGoals.Select(g => g.Id),
            reachable: reachable,
            children: calcChildren
        );

        // D0) goal need
        var goalNeed = reachable.ToDictionary(id => id, _ => 0.0);
        foreach (var g in normalizedGoals)
            if (reachable.Contains(g.Id))
                goalNeed[g.Id] += g.CountPerMin;

        // D1) First pass: compute "external" consumed from non-cycle propagation graph
        // (ignoring internal 2-cycle edges, because they were removed from calcChildren)
        var consumedExternal = reachable.ToDictionary(id => id, _ => 0.0);

        foreach (var parentId in topoParentFirst)
        {
            if (!map.TryGetValue(parentId, out var parent))
                continue;

            var totalNeedParent = goalNeed[parentId] + consumedExternal[parentId];
            if (totalNeedParent <= 0)
                continue;

            if (parent.Inputs.Count == 0)
                continue;

            var outQty = Math.Max(1, parent.Count);
            var craftsPerMin = totalNeedParent / outQty;

            foreach (var childId in calcChildren[parentId])
            {
                if (!reachable.Contains(childId)) continue;
                var q = GetInputCount(parent, childId);
                consumedExternal[childId] += craftsPerMin * q;
            }
        }

        // D2) Solve each allowed 2-cycle using a 2x2 system on "external demands"
        // External demand for node = goals + consumedExternal (does not include internal mate consumption)
        var cycleMate = new Dictionary<Guid, Guid>();
        foreach (var (near, far) in cyclePairs)
        {
            cycleMate[near] = far;
            cycleMate[far] = near;
        }
        bool IsCycleMember(Guid id) => cycleMate.ContainsKey(id);

        var craftsById = reachable.ToDictionary(id => id, _ => 0.0);
        var internalConsumed = reachable.ToDictionary(id => id, _ => 0.0); // how much is consumed by mate (units/min)

        foreach (var (near, far) in cyclePairs)
        {
            if (!map.TryGetValue(near, out var N) || !map.TryGetValue(far, out var F))
                continue;

            double DN = goalNeed[near] + consumedExternal[near]; // units/min
            double DF = goalNeed[far] + consumedExternal[far];

            double outN = Math.Max(1, N.Count);
            double outF = Math.Max(1, F.Count);

            // qNF: Far needed per craft of Near (Near uses Far)
            double qNF = Math.Max(1, GetInputCount(N, far));
            // qFN: Near needed per craft of Far (Far uses Near)
            double qFN = Math.Max(1, GetInputCount(F, near));

            // System:
            // outN*x - qFN*y = DN
            // -qNF*x + outF*y = DF
            double a = outN, b = -qFN, c = -qNF, d = outF;
            double det = a * d - b * c;

            if (Math.Abs(det) < 1e-9)
                throw new InvalidOperationException("Degenerate 2-cycle system (det≈0).");

            double x = (DN * d - b * DF) / det; // crafts/min for near
            double y = (a * DF - DN * c) / det; // crafts/min for far

            // clamp (shouldn't go negative for allowed self-renewable, but keep safe)
            if (x < 0) x = 0;
            if (y < 0) y = 0;

            craftsById[near] = x;
            craftsById[far] = y;

            // internal consumption (units/min)
            internalConsumed[near] = qFN * y; // near consumed by far
            internalConsumed[far] = qNF * x;  // far consumed by near
        }

        // D3) Second pass: propagate with craftsById fixed for cycle members
        // For non-cycle nodes, crafts are computed from (goals + external consumed) / outQty during this pass.
        var consumedExternal2 = reachable.ToDictionary(id => id, _ => 0.0);

        foreach (var parentId in topoParentFirst)
        {
            if (!map.TryGetValue(parentId, out var parent))
                continue;

            if (parent.Inputs.Count == 0)
                continue;

            double craftsPerMin;

            if (IsCycleMember(parentId))
            {
                craftsPerMin = craftsById[parentId];
                if (craftsPerMin <= 0) continue;
            }
            else
            {
                var totalNeedParent = goalNeed[parentId] + consumedExternal2[parentId];
                if (totalNeedParent <= 0) continue;

                var outQty = Math.Max(1, parent.Count);
                craftsPerMin = totalNeedParent / outQty;
                craftsById[parentId] = craftsPerMin;
            }

            foreach (var childId in calcChildren[parentId])
            {
                if (!reachable.Contains(childId)) continue;
                var q = GetInputCount(parent, childId);
                consumedExternal2[childId] += craftsPerMin * q;
            }
        }

        // Final numbers:
        // producedNeedUnits = goals + externalConsumed + internalConsumed
        var producedNeedUnitsById = reachable.ToDictionary(
            id => id,
            id => goalNeed[id] + consumedExternal2[id] + internalConsumed[id]
        );

        // Leaves (only true leaves: no recipe)
        var leaves = reachable
            .Where(id => map.TryGetValue(id, out var r) && r.Inputs.Count == 0)
            .Select(id =>
            {
                var r = map[id];
                return new BuildLeafNeed(
                    id,
                    r.Name,
                    r.Icon,
                    NeedPerMin: producedNeedUnitsById[id]
                );
            })
            .OrderByDescending(x => x.NeedPerMin)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Rows
        var rows = reachable
            .Where(id => map.TryGetValue(id, out _))
            .Select(id =>
            {
                var r = map[id];
                var isGoal = goalSet.Contains(id);

                bool isLeaf = r.Inputs.Count == 0;
                bool isCraftable = r.Inputs.Count > 0;

                if (!isGoal && !isCraftable)
                    return null;

                var outQty = Math.Max(1, r.Count);
                var craftTime = Math.Max(0, r.Seconds);

                // consumed: external + internal (internal only for cycle members)
                var consumed = consumedExternal2[id] + internalConsumed[id];

                // "need" in units/min (the amount we must supply)
                var needUnits = producedNeedUnitsById[id];

                if (isLeaf)
                {
                    return new BuildCalcRow(
                        r.Id,
                        r.Name,
                        r.Icon,
                        IsGoal: isGoal,
                        IsLeaf: true,
                        ConsumedPerMin: consumed,
                        ProducedPerMin: needUnits,
                        DeltaPerMin: needUnits - consumed,
                        CraftsPerMin: 0,
                        Machines: 0,
                        Count: outQty,
                        Seconds: craftTime
                    );
                }

                // crafts per min:
                // - for cycle members: from solved system
                // - for others: from second pass
                var craftsPerMin = craftsById[id];
                if (craftsPerMin < 0) craftsPerMin = 0;

                int machines = craftTime <= 0
                    ? 0
                    : (int)Math.Ceiling(craftsPerMin * craftTime / 60.0);

                double producedPerMin = (machines <= 0 || craftTime <= 0)
                    ? 0
                    : machines * (60.0 / craftTime) * outQty;

                return new BuildCalcRow(
                    r.Id,
                    r.Name,
                    r.Icon,
                    IsGoal: isGoal,
                    IsLeaf: false,
                    ConsumedPerMin: consumed,
                    ProducedPerMin: producedPerMin,
                    DeltaPerMin: producedPerMin - consumed,
                    CraftsPerMin: craftsPerMin,
                    Machines: machines,
                    Count: outQty,
                    Seconds: craftTime
                );
            })
            .Where(x => x is not null)
            .Select(x => x!)
            .ToList();

        // Complexity (use DAG children to avoid recursion cycles)
        var memoIntermediate = new Dictionary<Guid, HashSet<Guid>>();
        var memoDepth = new Dictionary<Guid, int>();

        var complexityById = rows
            .Select(r => r.Id)
            .Distinct()
            .ToDictionary(
                id => id,
                id => CalcComplexityOnDag(id, reachable, calcChildren, memoIntermediate, memoDepth)
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
            NeedPerMinById = producedNeedUnitsById
        };
    }

    // -----------------------
    // Helpers: graph build
    // -----------------------

    private static void CollectReachableAndCalcEdgesFromRoots(
        IEnumerable<Guid> roots,
        Dictionary<Guid, ResourceViewModel> map,
        HashSet<Guid> reachable,
        HashSet<(Guid Product, Guid Ingredient)> calcEdges
    )
    {
        void Dfs(Guid id)
        {
            if (id == Guid.Empty) return;
            if (!reachable.Add(id)) return;
            if (!map.TryGetValue(id, out var node)) return;

            foreach (var inp in node.Inputs)
            {
                var childId = inp.Id;
                if (childId == Guid.Empty) continue;
                if (!map.ContainsKey(childId)) continue;

                calcEdges.Add((Product: id, Ingredient: childId));
                Dfs(childId);
            }
        }

        foreach (var r in roots.Where(x => x != Guid.Empty).Distinct())
            Dfs(r);
    }

    private static List<Guid> GetCalcChildren(
        Guid productId,
        HashSet<Guid> reachable,
        HashSet<(Guid Product, Guid Ingredient)> calcEdges
    )
    {
        var res = new List<Guid>();
        foreach (var (p, i) in calcEdges)
            if (p == productId && reachable.Contains(i))
                res.Add(i);

        return [.. res.Distinct()];
    }

    private static int GetInputCount(ResourceViewModel product, Guid ingredientId)
    {
        var inp = product.Inputs.FirstOrDefault(x => x.Id == ingredientId);
        return inp is null ? 1 : Math.Max(1, inp.Count);
    }

    // -----------------------
    // Cycle rules
    // -----------------------

    private static bool IsSelfRenewablePair(Guid aId, Guid bId, Dictionary<Guid, ResourceViewModel> map)
    {
        if (!map.TryGetValue(aId, out var a)) return false;
        if (!map.TryGetValue(bId, out var b)) return false;

        // b made ONLY from a
        if (b.Inputs.Count != 1) return false;
        var bInp = b.Inputs[0];
        if (bInp.Id != aId) return false;

        int qBA = Math.Max(1, bInp.Count);

        // a requires b
        var aToB = a.Inputs.FirstOrDefault(x => x.Id == bId);
        if (aToB is null) return false;

        int qAB = Math.Max(1, aToB.Count);

        int outA = Math.Max(1, a.Count);
        int outB = Math.Max(1, b.Count);

        int craftsA = outB / qAB;
        if (craftsA <= 0) return false;

        int producedA = craftsA * outA;

        return producedA >= 2 * qBA;
    }

    private static Dictionary<Guid, HashSet<Guid>> BuildIncomingFromOutside(
        HashSet<Guid> reachable,
        HashSet<(Guid Product, Guid Ingredient)> calcEdges
    )
    {
        var incoming = reachable.ToDictionary(id => id, _ => new HashSet<Guid>());

        foreach (var (p, i) in calcEdges)
        {
            if (!reachable.Contains(p) || !reachable.Contains(i)) continue;
            incoming[i].Add(p);
        }

        return incoming;
    }

    private static Guid PickNearNodeInTwoCycle(
        Guid a,
        Guid b,
        HashSet<Guid> roots,
        Dictionary<Guid, HashSet<Guid>> incoming
    )
    {
        bool aIsRoot = roots.Contains(a);
        bool bIsRoot = roots.Contains(b);
        if (aIsRoot && !bIsRoot) return a;
        if (bIsRoot && !aIsRoot) return b;

        bool aHasExternal = incoming[a].Any(x => x != a && x != b);
        bool bHasExternal = incoming[b].Any(x => x != a && x != b);

        if (aHasExternal && !bHasExternal) return a;
        if (bHasExternal && !aHasExternal) return b;

        return a.CompareTo(b) <= 0 ? a : b;
    }

    // -----------------------
    // Algorithms: SCC / cycle detect / topo
    // -----------------------

    private static List<List<Guid>> TarjanScc(HashSet<Guid> nodes, Func<Guid, List<Guid>> next)
    {
        int index = 0;
        var stack = new Stack<Guid>();
        var onStack = new HashSet<Guid>();
        var idx = new Dictionary<Guid, int>();
        var low = new Dictionary<Guid, int>();
        var result = new List<List<Guid>>();

        void StrongConnect(Guid v)
        {
            idx[v] = index;
            low[v] = index;
            index++;

            stack.Push(v);
            onStack.Add(v);

            foreach (var w in next(v))
            {
                if (!nodes.Contains(w)) continue;

                if (!idx.ContainsKey(w))
                {
                    StrongConnect(w);
                    low[v] = Math.Min(low[v], low[w]);
                }
                else if (onStack.Contains(w))
                {
                    low[v] = Math.Min(low[v], idx[w]);
                }
            }

            if (low[v] == idx[v])
            {
                var comp = new List<Guid>();
                while (true)
                {
                    var w = stack.Pop();
                    onStack.Remove(w);
                    comp.Add(w);
                    if (w == v) break;
                }
                result.Add(comp);
            }
        }

        foreach (var v in nodes)
            if (!idx.ContainsKey(v))
                StrongConnect(v);

        return result;
    }

    private static bool DetectAnyCycle(
        IEnumerable<Guid> roots,
        HashSet<Guid> reachable,
        Dictionary<Guid, List<Guid>> children
    )
    {
        var visiting = new HashSet<Guid>();
        var visited = new HashSet<Guid>();

        bool Dfs(Guid id)
        {
            if (!reachable.Contains(id)) return false;
            if (visited.Contains(id)) return false;
            if (visiting.Contains(id)) return true;

            visiting.Add(id);

            foreach (var ch in children[id])
                if (Dfs(ch)) return true;

            visiting.Remove(id);
            visited.Add(id);
            return false;
        }

        foreach (var r in roots.Where(x => x != Guid.Empty).Distinct())
            if (Dfs(r)) return true;

        return false;
    }

    private static List<Guid> BuildTopoParentFirstDAG(
        IEnumerable<Guid> roots,
        HashSet<Guid> reachable,
        Dictionary<Guid, List<Guid>> children
    )
    {
        var indeg = reachable.ToDictionary(id => id, _ => 0);
        foreach (var u in reachable)
            foreach (var v in children[u])
                indeg[v]++;

        var rootSet = new HashSet<Guid>(roots.Where(x => x != Guid.Empty));
        var zeros = indeg.Where(kv => kv.Value == 0).Select(kv => kv.Key).ToList();

        zeros.Sort((x, y) =>
        {
            bool xr = rootSet.Contains(x), yr = rootSet.Contains(y);
            if (xr && !yr) return -1;
            if (yr && !xr) return 1;
            return x.CompareTo(y);
        });

        var q = new Queue<Guid>(zeros);
        var order = new List<Guid>(reachable.Count);

        while (q.Count > 0)
        {
            var u = q.Dequeue();
            order.Add(u);

            foreach (var v in children[u])
            {
                indeg[v]--;
                if (indeg[v] == 0)
                    q.Enqueue(v);
            }
        }

        if (order.Count != reachable.Count)
            throw new InvalidOperationException("Cycle detected in DAG topo (unexpected).");

        return order;
    }

    // -----------------------
    // Complexity on DAG
    // -----------------------

    private static (int IntermediateCount, int MaxDepth) CalcComplexityOnDag(
        Guid id,
        HashSet<Guid> reachable,
        Dictionary<Guid, List<Guid>> children,
        Dictionary<Guid, HashSet<Guid>> memoIntermediate,
        Dictionary<Guid, int> memoDepth
    )
    {
        HashSet<Guid> DfsIntermediate(Guid cur)
        {
            if (memoIntermediate.TryGetValue(cur, out var cached))
                return cached;

            if (!reachable.Contains(cur) || !children.TryGetValue(cur, out var kids) || kids.Count == 0)
                return memoIntermediate[cur] = [];

            var set = new HashSet<Guid> { cur };
            foreach (var ch in kids)
            {
                var childSet = DfsIntermediate(ch);
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

            if (!reachable.Contains(cur) || !children.TryGetValue(cur, out var kids) || kids.Count == 0)
                return memoDepth[cur] = 0;

            var best = 0;
            foreach (var ch in kids)
                best = Math.Max(best, 1 + DfsDepth(ch));

            memoDepth[cur] = best;
            return best;
        }

        var intermediateCount = Math.Max(0, DfsIntermediate(id).Count - 1);
        var maxDepth = DfsDepth(id);

        return (intermediateCount, maxDepth);
    }
}
