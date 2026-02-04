using EndfieldGraph.Models;
using EndfieldGraph.ViewModels.Resource;
using System.Windows;

namespace EndfieldGraph.Services;

public sealed record GraphCycleInfo(IReadOnlyList<Guid> Path);

public sealed record ResourceGraphBuildResult(
    bool HasCycle,
    GraphCycleInfo? Cycle,
    ResourceGraphLayout? Layout
);

public interface IResourceGraphBuilder
{
    ResourceGraphLayout ComposeVertical(IReadOnlyList<ResourceGraphLayout> parts, double gapY);

    ResourceGraphBuildResult BuildFor(
        ResourceViewModel root,
        IReadOnlyCollection<ResourceViewModel> allResources,
        int desiredRootUnits = 1
    );
}

public sealed class ResourceGraphBuilder : IResourceGraphBuilder
{
    private const double XStep = 260;
    private const double YStep = 150;
    private const double MarginLeft = 140;
    private const double MarginTop = 120;

    public ResourceGraphLayout ComposeVertical(IReadOnlyList<ResourceGraphLayout> parts, double gapY)
    {
        var result = new ResourceGraphLayout();
        double curY = 0;

        foreach (var part in parts)
        {
            if (part.Nodes.Count == 0)
                continue;

            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;

            foreach (var n in part.Nodes)
            {
                minX = Math.Min(minX, n.Position.X);
                minY = Math.Min(minY, n.Position.Y);
                maxX = Math.Max(maxX, n.Position.X);
                maxY = Math.Max(maxY, n.Position.Y);
            }

            var height = Math.Max(1, maxY - minY);

            var idMap = new Dictionary<Guid, Guid>();
            foreach (var n in part.Nodes)
                idMap[n.Id] = Guid.NewGuid();

            foreach (var n in part.Nodes)
            {
                var newId = idMap[n.Id];
                var newPos = new Point(
                    x: n.Position.X - minX + 140,
                    y: n.Position.Y - minY + curY + 120
                );

                result.Nodes.Add(new ResourceGraphNode
                {
                    Id = newId,
                    Name = n.Name,
                    Icon = n.Icon,
                    Level = n.Level,
                    Position = newPos
                });
            }

            foreach (var e in part.Edges)
            {
                if (!idMap.TryGetValue(e.FromId, out var nf)) continue;
                if (!idMap.TryGetValue(e.ToId, out var nt)) continue;

                result.Edges.Add(new ResourceGraphEdge
                {
                    FromId = nf,
                    ToId = nt,
                    NeedCount = e.NeedCount,
                    TimeSeconds = e.TimeSeconds,
                    IsDashed = e.IsDashed,
                });
            }

            foreach (var ol in part.OutLabels)
            {
                if (!idMap.TryGetValue(ol.FromId, out var nf)) continue;

                result.OutLabels.Add(new ResourceGraphOutLabel
                {
                    FromId = nf,
                    NeedCount = ol.NeedCount,
                    TimeSeconds = ol.TimeSeconds
                });
            }

            curY += height + gapY;
        }

        return result;
    }

    public ResourceGraphBuildResult BuildFor(
        ResourceViewModel root,
        IReadOnlyCollection<ResourceViewModel> allResources,
        int desiredRootUnits = 1
    )
    {
        if (root.Id == Guid.Empty)
            return new ResourceGraphBuildResult(false, null, new ResourceGraphLayout());

        desiredRootUnits = Math.Max(1, desiredRootUnits);

        var map = allResources
            .Where(r => r.Id != Guid.Empty)
            .GroupBy(r => r.Id)
            .ToDictionary(g => g.Key, g => g.First());

        if (!map.ContainsKey(root.Id))
            map[root.Id] = root;

        var reachable = new HashSet<Guid>();
        var calcEdges = new HashSet<(Guid Product, Guid Ingredient)>();
        CollectReachableAndCalcEdges(root.Id, map, reachable, calcEdges);

        if (reachable.Count == 0)
            return new ResourceGraphBuildResult(false, null, new ResourceGraphLayout());

        var sccs = TarjanScc(reachable, id => GetCalcChildren(id, reachable, calcEdges));

        var removedCalcEdges = new HashSet<(Guid Product, Guid Ingredient)>();
        var dashedDisplayEdges = new HashSet<(Guid From, Guid To)>();

        bool allowAnyTwoNodeMutualIfOnlyTwoNodesInGraph = reachable.Count == 2;

        var incomingFromOutside = BuildIncomingFromOutside(reachable, calcEdges);

        foreach (var comp in sccs)
        {
            if (comp.Count <= 1)
                continue;

            if (comp.Count != 2)
                return new ResourceGraphBuildResult(true, new GraphCycleInfo(comp.Concat([comp[0]]).ToList()), null);

            var a = comp[0];
            var b = comp[1];

            bool hasAB = calcEdges.Contains((a, b));
            bool hasBA = calcEdges.Contains((b, a));
            if (!hasAB || !hasBA)
                return new ResourceGraphBuildResult(true, new GraphCycleInfo(comp.Concat([comp[0]]).ToList()), null);

            bool isSelfRenewable = IsSelfRenewablePair(a, b, map) || IsSelfRenewablePair(b, a, map);
            bool allowed = isSelfRenewable || allowAnyTwoNodeMutualIfOnlyTwoNodesInGraph;

            if (!allowed)
                return new ResourceGraphBuildResult(true, new GraphCycleInfo(comp.Concat([comp[0]]).ToList()), null);

            var near = PickNearNodeInTwoCycle(a, b, root.Id, incomingFromOutside);
            var far = near == a ? b : a;

            removedCalcEdges.Add((far, near));
            dashedDisplayEdges.Add((From: near, To: far));
        }

        var calcChildren = reachable.ToDictionary(id => id, _ => new List<Guid>());
        foreach (var (p, i) in calcEdges)
        {
            if (!reachable.Contains(p) || !reachable.Contains(i)) continue;
            if (removedCalcEdges.Contains((p, i))) continue;
            calcChildren[p].Add(i);
        }

        var realCycle = DetectAnyCycle(root.Id, reachable, calcChildren);
        if (realCycle is not null)
            return new ResourceGraphBuildResult(true, realCycle, null);

        var topo = TopoSortFromRootPrefer(root.Id, reachable, calcChildren);

        var needUnits = reachable.ToDictionary(id => id, _ => 0);
        needUnits[root.Id] = desiredRootUnits;

        static int CeilDiv(int a, int b) => (a + b - 1) / b;

        foreach (var productId in topo)
        {
            var product = map[productId];

            var needProductUnits = needUnits[productId];
            if (needProductUnits <= 0) continue;

            var productOut = Math.Max(1, product.Count);
            var productCrafts = CeilDiv(needProductUnits, productOut);

            foreach (var ingId in calcChildren[productId])
            {
                var q = GetInputCount(product, ingId);
                needUnits[ingId] += productCrafts * q;
            }
        }

        var solidEdges = new List<ResourceGraphEdge>();

        foreach (var productId in topo)
        {
            var product = map[productId];

            var needProductUnits = needUnits[productId];
            if (needProductUnits <= 0) continue;

            var productOut = Math.Max(1, product.Count);
            var productCrafts = CeilDiv(needProductUnits, productOut);

            foreach (var ingId in calcChildren[productId])
            {
                var ing = map[ingId];

                var q = GetInputCount(product, ingId);
                var edgeNeed = productCrafts * q;

                var ingOut = Math.Max(1, ing.Count);
                var craftsForEdge = CeilDiv(edgeNeed, ingOut);
                var edgeTime = craftsForEdge * Math.Max(0, ing.Seconds);

                solidEdges.Add(new ResourceGraphEdge
                {
                    FromId = ingId,
                    ToId = productId,
                    NeedCount = edgeNeed,
                    TimeSeconds = edgeTime,
                    IsDashed = false
                });
            }
        }

        var level = reachable.ToDictionary(id => id, _ => 0);
        level[root.Id] = 0;

        foreach (var productId in topo)
        {
            var l = level[productId];
            foreach (var ingId in calcChildren[productId])
                level[ingId] = Math.Max(level[ingId], l + 1);
        }

        var baseLevel = new Dictionary<Guid, int>(level);

        var allEdges = dashedDisplayEdges.Select(p => new ResourceGraphEdge
        {
            FromId = p.From,
            ToId = p.To,
            NeedCount = 0,
            TimeSeconds = 0,
            IsDashed = true
        }).Concat(solidEdges).ToList();

        RelaxLevelsToReduceLongSharedEdges(reachable, allEdges, level, baseLevel, slack: 2);

        var positions = ComputePositions(root.Id, reachable, allEdges, level, map);

        var outLabels = solidEdges
            .GroupBy(e => e.FromId)
            .Select(g =>
            {
                var ing = map[g.Key];
                var totalNeed = g.Sum(x => x.NeedCount);

                var ingOut = Math.Max(1, ing.Count);
                var crafts = CeilDiv(totalNeed, ingOut);
                var totalTime = crafts * Math.Max(0, ing.Seconds);

                return new ResourceGraphOutLabel
                {
                    FromId = g.Key,
                    NeedCount = totalNeed,
                    TimeSeconds = totalTime
                };
            })
            .ToList();

        var layout = new ResourceGraphLayout();

        foreach (var id in reachable
            .OrderBy(id => level[id])
            .ThenBy(id => map[id].Name, StringComparer.OrdinalIgnoreCase)
        )
        {
            var r = map[id];

            layout.Nodes.Add(new ResourceGraphNode
            {
                Id = id,
                Name = r.Name,
                Icon = r.Icon,
                Level = level[id],
                Position = positions[id]
            });
        }

        foreach (var e in solidEdges)
            layout.Edges.Add(e);

        foreach (var (from, to) in dashedDisplayEdges)
        {
            var product = map[to];
            var ingredient = map[from];

            var needProductUnits = needUnits[to];
            if (needProductUnits <= 0)
                continue;

            var productOut = Math.Max(1, product.Count);
            var productCrafts = CeilDiv(needProductUnits, productOut);

            var q = GetInputCount(product, from);
            var edgeNeed = productCrafts * q;

            var ingOut = Math.Max(1, ingredient.Count);
            var craftsForEdge = CeilDiv(edgeNeed, ingOut);
            var edgeTime = craftsForEdge * Math.Max(0, ingredient.Seconds);

            layout.Edges.Add(new ResourceGraphEdge
            {
                FromId = from,
                ToId = to,
                NeedCount = edgeNeed,
                TimeSeconds = edgeTime,
                IsDashed = true
            });
        }

        foreach (var ol in outLabels)
            layout.OutLabels.Add(ol);

        return new ResourceGraphBuildResult(false, null, layout);
    }

    private static void CollectReachableAndCalcEdges(
        Guid rootId,
        Dictionary<Guid, ResourceViewModel> map,
        HashSet<Guid> reachable,
        HashSet<(Guid Product, Guid Ingredient)> calcEdges
    )
    {
        void Dfs(Guid id)
        {
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

        Dfs(rootId);
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

    private static bool IsSelfRenewablePair(Guid aId, Guid bId, Dictionary<Guid, ResourceViewModel> map)
    {
        if (!map.TryGetValue(aId, out var a)) return false;
        if (!map.TryGetValue(bId, out var b)) return false;

        if (b.Inputs.Count != 1) return false;

        var bInp = b.Inputs[0];
        if (bInp.Id != aId) return false;

        int qBA = Math.Max(1, bInp.Count);

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
        Guid rootId,
        Dictionary<Guid, HashSet<Guid>> incoming
    )
    {
        if (a == rootId) return a;
        if (b == rootId) return b;

        bool aHasExternal = incoming[a].Any(x => x != a && x != b);
        bool bHasExternal = incoming[b].Any(x => x != a && x != b);

        if (aHasExternal && !bHasExternal) return a;
        if (bHasExternal && !aHasExternal) return b;

        return a.CompareTo(b) <= 0 ? a : b;
    }

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

    private static GraphCycleInfo? DetectAnyCycle(
        Guid rootId,
        HashSet<Guid> reachable,
        Dictionary<Guid, List<Guid>> calcChildren
    )
    {
        var visiting = new HashSet<Guid>();
        var visited = new HashSet<Guid>();
        var stack = new List<Guid>();

        GraphCycleInfo? Dfs(Guid id)
        {
            if (!reachable.Contains(id)) return null;
            if (visited.Contains(id)) return null;

            if (visiting.Contains(id))
            {
                var idx = stack.IndexOf(id);
                var path = idx >= 0 ? stack.Skip(idx).Concat([id]).ToList() : new List<Guid> { id, id };
                return new GraphCycleInfo(path);
            }

            visiting.Add(id);
            stack.Add(id);

            foreach (var ch in calcChildren[id])
            {
                var cyc = Dfs(ch);
                if (cyc is not null) return cyc;
            }

            stack.RemoveAt(stack.Count - 1);
            visiting.Remove(id);
            visited.Add(id);
            return null;
        }

        return Dfs(rootId);
    }

    private static List<Guid> TopoSortFromRootPrefer(
        Guid rootId,
        HashSet<Guid> reachable,
        Dictionary<Guid, List<Guid>> calcChildren
    )
    {
        var indeg = reachable.ToDictionary(id => id, _ => 0);

        foreach (var u in reachable)
            foreach (var v in calcChildren[u])
                indeg[v]++;

        var zeros = indeg.Where(kv => kv.Value == 0).Select(kv => kv.Key).ToList();

        zeros.Sort((x, y) =>
        {
            if (x == rootId && y != rootId) return -1;
            if (y == rootId && x != rootId) return 1;
            return x.CompareTo(y);
        });

        var q = new Queue<Guid>(zeros);
        var order = new List<Guid>(reachable.Count);

        while (q.Count > 0)
        {
            var u = q.Dequeue();
            order.Add(u);

            foreach (var v in calcChildren[u])
            {
                indeg[v]--;
                if (indeg[v] == 0)
                    q.Enqueue(v);
            }
        }

        return order;
    }

    private static void RelaxLevelsToReduceLongSharedEdges(
        HashSet<Guid> reachable,
        List<ResourceGraphEdge> edges,
        Dictionary<Guid, int> level,
        Dictionary<Guid, int> baseLevel,
        int slack = 2
    )
    {
        var productsOfIngredient = reachable.ToDictionary(id => id, _ => new List<Guid>());
        var ingredientsOfProduct = reachable.ToDictionary(id => id, _ => new List<Guid>());

        foreach (var e in edges)
        {
            if (e.IsDashed) continue;
            if (!reachable.Contains(e.FromId) || !reachable.Contains(e.ToId)) continue;

            productsOfIngredient[e.FromId].Add(e.ToId);
            ingredientsOfProduct[e.ToId].Add(e.FromId);
        }

        int Cap(Guid id, int v)
        {
            if (!baseLevel.TryGetValue(id, out var b)) b = 0;
            var max = b + slack;
            if (v > max) v = max;
            if (v < 0) v = 0;
            return v;
        }

        bool changed;
        int guard = 0;

        do
        {
            changed = false;
            guard++;
            if (guard > 2000) break;

            foreach (var ing in reachable)
            {
                var ps = productsOfIngredient[ing];
                if (ps.Count <= 1) continue;

                var desiredProductLevel = Math.Max(0, level[ing] - 1);

                foreach (var p in ps)
                {
                    var capped = Cap(p, desiredProductLevel);
                    if (level[p] < capped)
                    {
                        level[p] = capped;
                        changed = true;
                    }
                }
            }

            foreach (var prod in reachable)
            {
                foreach (var ing in ingredientsOfProduct[prod])
                {
                    var desiredIngLevel = level[prod] + 1;
                    var capped = Cap(ing, desiredIngLevel);

                    if (level[ing] < capped)
                    {
                        level[ing] = capped;
                        changed = true;
                    }
                }
            }
        }
        while (changed);
    }

    
    private Dictionary<Guid, Point> ComputePositions(
        Guid rootId,
        HashSet<Guid> reachable,
        List<ResourceGraphEdge> edges,
        Dictionary<Guid, int> level,
        Dictionary<Guid, ResourceViewModel> map
    )
    {
        var parentsOf = reachable.ToDictionary(id => id, _ => new List<Guid>());
        var childrenOf = reachable.ToDictionary(id => id, _ => new List<Guid>());

        var dashedPairs = edges
            .Where(e => e.IsDashed)
            .Select(e => (A: e.FromId, B: e.ToId))
            .Where(p => reachable.Contains(p.A) && reachable.Contains(p.B))
            .ToList();

        var cycleMate = new Dictionary<Guid, Guid>();
        foreach (var (a, b) in dashedPairs)
        {
            cycleMate[a] = b;
            cycleMate[b] = a;
        }

        bool IsCycleMember(Guid id) => cycleMate.ContainsKey(id);

        foreach (var e in edges)
        {
            if (e.IsDashed) continue;
            if (!reachable.Contains(e.FromId) || !reachable.Contains(e.ToId)) continue;

            parentsOf[e.FromId].Add(e.ToId);
            childrenOf[e.ToId].Add(e.FromId);
        }

        bool HasDashedBetween(Guid parent, Guid child)
        {
            return edges.Any(e =>
                e.IsDashed &&
                (
                    (e.FromId == parent && e.ToId == child) ||
                    (e.FromId == child && e.ToId == parent)
                ));
        }

        var lengthMemo = new Dictionary<Guid, int>();

        int LengthToLeaf(Guid id)
        {
            if (lengthMemo.TryGetValue(id, out var v)) return v;

            if (!childrenOf.TryGetValue(id, out var kids) || kids.Count == 0)
                return lengthMemo[id] = 0;

            int best = 0;
            foreach (var k in kids.Distinct())
                best = Math.Max(best, 1 + LengthToLeaf(k));

            return lengthMemo[id] = best;
        }

        var spanMemo = new Dictionary<Guid, int>();

        int Span(Guid id)
        {
            if (spanMemo.TryGetValue(id, out var v)) return v;

            if (!childrenOf.TryGetValue(id, out var kids))
                return spanMemo[id] = 1;

            var uniqKids = kids.Distinct().ToList();
            if (uniqKids.Count == 0)
                return spanMemo[id] = 1;

            int sum = 0;
            foreach (var k in uniqKids)
                sum += Math.Max(1, Span(k));

            return spanMemo[id] = Math.Max(1, sum);
        }

        var lane = reachable.ToDictionary(id => id, _ => int.MinValue);
        lane[rootId] = 0;

        var rootKids = childrenOf[rootId]
            .Distinct()
            .OrderByDescending(id => HasDashedBetween(rootId, id))
            .ThenByDescending(LengthToLeaf)
            .ThenBy(id => map[id].Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int curLane = 0;
        foreach (var kid in rootKids)
        {
            lane[kid] = curLane;
            curLane += Span(kid);
        }

        var maxLevel = reachable.Max(id => level[id]);
        var idsByLevel = Enumerable.Range(0, maxLevel + 1)
            .Select(l => reachable.Where(id => level[id] == l).ToList())
            .ToList();

        if (!idsByLevel[0].Contains(rootId))
            idsByLevel[0].Add(rootId);

        var usedAtLevel = new Dictionary<int, HashSet<int>>();
        for (int l = 0; l <= maxLevel; l++)
            usedAtLevel[l] = [];

        foreach (var id in reachable)
            if (lane[id] != int.MinValue)
                usedAtLevel[level[id]].Add(lane[id]);

        for (int l = 1; l <= maxLevel; l++)
        {
            var occupied = usedAtLevel[l];

            int ParentDelta(Guid id)
            {
                var ps = parentsOf[id];
                if (ps.Count == 0) return 9999;
                var nearestParentLevel = ps.Max(p => level[p]);
                return level[id] - nearestParentLevel;
            }

            List<Guid> OrderedSiblings(Guid parentId)
            {
                return [.. childrenOf[parentId]
                    .Distinct()
                    .OrderByDescending(c => HasDashedBetween(parentId, c))
                    .ThenByDescending(c => ParentDelta(c))
                    .ThenByDescending(LengthToLeaf)
                    .ThenBy(c => map[c].Name, StringComparer.OrdinalIgnoreCase)];
            }

            int PreferredLane(Guid id)
            {
                if (cycleMate.TryGetValue(id, out var mate) && lane[mate] != int.MinValue)
                    return lane[mate];

                if (lane[id] != int.MinValue) return lane[id];

                var ps = parentsOf[id];
                if (ps.Count == 0) return 0;

                var parentLanes = ps.Where(p => lane[p] != int.MinValue).Select(p => lane[p]).ToList();
                if (parentLanes.Count == 0) return 0;

                if (ps.Count == 1)
                {
                    var p = ps[0];
                    var pLane = parentLanes[0];

                    var siblings = OrderedSiblings(p);
                    var idx = Math.Max(0, siblings.IndexOf(id));
                    return pLane + idx;
                }

                return parentLanes.Max();
            }

            var candidates = idsByLevel[l]
                .Select(id => (Id: id, Pref: PreferredLane(id)))
                .OrderByDescending(x => x.Id == rootId)
                .ThenByDescending(x => IsCycleMember(x.Id))
                .ThenByDescending(x => ParentDelta(x.Id))
                .ThenByDescending(x => LengthToLeaf(x.Id))
                .ThenBy(x => map[x.Id].Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var (id, pref) in candidates)
            {
                if (lane[id] != int.MinValue)
                {
                    occupied.Add(lane[id]);
                    continue;
                }

                int chosen = Math.Max(0, pref);
                while (occupied.Contains(chosen))
                    chosen++;

                lane[id] = chosen;
                occupied.Add(chosen);
            }
        }

        var used = lane.Values.Where(v => v != int.MinValue).Distinct().OrderBy(v => v).ToList();
        var remap = used.Select((v, idx) => (v, idx)).ToDictionary(x => x.v, x => x.idx);

        foreach (var id in reachable)
        {
            if (lane[id] == int.MinValue) lane[id] = 0;
            lane[id] = remap[lane[id]];
        }

        var pos = new Dictionary<Guid, Point>(reachable.Count);
        foreach (var id in reachable)
        {
            var x = MarginLeft + level[id] * XStep;
            var y = MarginTop + lane[id] * YStep;
            pos[id] = new Point(x, y);
        }

        return pos;
    }
}
