using EndfieldGraph.ViewModels.Resource;
using EndfieldGraph.Views.Controls;
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
    ResourceGraphBuildResult BuildFor(
        ResourceItemViewModel root,
        IReadOnlyCollection<ResourceItemViewModel> allResources,
        int desiredRootUnits = 1
    );
}

public sealed class ResourceGraphBuilder : IResourceGraphBuilder
{
    private const double XStep = 260;
    private const double YStep = 150;
    private const double MarginLeft = 140;
    private const double MarginTop = 120;

    public ResourceGraphBuildResult BuildFor(
        ResourceItemViewModel root,
        IReadOnlyCollection<ResourceItemViewModel> allResources,
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
        var cycle = DetectCycle(root.Id, map, reachable);
        if (cycle is not null)
            return new ResourceGraphBuildResult(true, cycle, null);

        var topo = TopoOrder(root.Id, map, reachable);

        var needUnits = reachable.ToDictionary(id => id, _ => 0);
        needUnits[root.Id] = desiredRootUnits;

        static int CeilDiv(int a, int b) => (a + b - 1) / b;

        foreach (var parentId in topo)
        {
            var parent = map[parentId];

            var needParentUnits = needUnits[parentId];
            if (needParentUnits <= 0)
                continue;

            var parentOutput = Math.Max(1, parent.OutputQty);
            var parentCrafts = CeilDiv(needParentUnits, parentOutput);

            foreach (var input in parent.Inputs)
            {
                var childId = input.Id;
                if (childId == Guid.Empty)
                    continue;
                if (!reachable.Contains(childId))
                    continue;

                var q = Math.Max(1, input.Qty);
                needUnits[childId] += parentCrafts * q;
            }
        }

        var edges = new List<ResourceGraphEdge>();

        foreach (var parentId in topo)
        {
            var parent = map[parentId];

            var needParentUnits = needUnits[parentId];
            if (needParentUnits <= 0)
                continue;

            var parentOutput = Math.Max(1, parent.OutputQty);
            var parentCrafts = CeilDiv(needParentUnits, parentOutput);

            foreach (var input in parent.Inputs)
            {
                var childId = input.Id;
                if (childId == Guid.Empty || !reachable.Contains(childId))
                    continue;

                var child = map[childId];
                var q = Math.Max(1, input.Qty);

                var edgeNeed = parentCrafts * q;

                var childOut = Math.Max(1, child.OutputQty);
                var craftsForEdge = CeilDiv(edgeNeed, childOut);
                var edgeTime = craftsForEdge * Math.Max(0, child.CraftTimeSec);

                edges.Add(new ResourceGraphEdge
                {
                    FromId = childId,
                    ToId = parentId,
                    NeedQty = edgeNeed,
                    TimeSec = edgeTime
                });
            }
        }

        var level = reachable.ToDictionary(id => id, _ => 0);
        foreach (var parentId in topo)
        {
            var parentLevel = level[parentId];
            var parent = map[parentId];

            foreach (var input in parent.Inputs)
            {
                var childId = input.Id;
                if (childId == Guid.Empty || !reachable.Contains(childId))
                    continue;

                level[childId] = Math.Max(level[childId], parentLevel + 1);
            }
        }

        RelaxLevelsToReduceLongSharedEdges(reachable, edges, map, level);

        var positions = ComputePositions(root.Id, reachable, edges, level, map);

        var outLabels = edges
            .GroupBy(e => e.FromId)
            .Select(g =>
            {
                var child = map[g.Key];
                var totalNeed = g.Sum(x => x.NeedQty);

                var childOut = Math.Max(1, child.OutputQty);
                var crafts = CeilDiv(totalNeed, childOut);
                var totalTime = crafts * Math.Max(0, child.CraftTimeSec);

                return new ResourceGraphOutLabel
                {
                    FromId = g.Key,
                    NeedQty = totalNeed,
                    TimeSec = totalTime
                };
            })
            .ToList();

        var layout = new ResourceGraphLayout();

        foreach (var id in reachable
                     .OrderBy(id => level[id])
                     .ThenBy(id => map[id].Name, StringComparer.OrdinalIgnoreCase))
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

        foreach (var e in edges)
            layout.Edges.Add(e);

        foreach (var ol in outLabels)
            layout.OutLabels.Add(ol);

        return new ResourceGraphBuildResult(false, null, layout);
    }

    private static GraphCycleInfo? DetectCycle(
        Guid rootId,
        Dictionary<Guid, ResourceItemViewModel> map,
        HashSet<Guid> reachable
    )
    {
        var visiting = new HashSet<Guid>();
        var visited = new HashSet<Guid>();
        var stack = new List<Guid>();

        GraphCycleInfo? Dfs(Guid id)
        {
            if (visited.Contains(id))
                return null;

            if (visiting.Contains(id))
            {
                var idx = stack.IndexOf(id);
                if (idx >= 0)
                {
                    var cyclePath = stack.Skip(idx).Concat([id]).ToList();
                    return new GraphCycleInfo(cyclePath);
                }
                return new GraphCycleInfo([id, id]);
            }

            visiting.Add(id);
            stack.Add(id);
            reachable.Add(id);

            if (map.TryGetValue(id, out var node))
            {
                foreach (var input in node.Inputs)
                {
                    var childId = input.Id;
                    if (childId == Guid.Empty) continue;
                    if (!map.ContainsKey(childId)) continue;

                    var cyc = Dfs(childId);
                    if (cyc is not null)
                        return cyc;
                }
            }

            stack.RemoveAt(stack.Count - 1);
            visiting.Remove(id);
            visited.Add(id);
            return null;
        }

        return Dfs(rootId);
    }

    private static List<Guid> TopoOrder(
        Guid rootId,
        Dictionary<Guid, ResourceItemViewModel> map,
        HashSet<Guid> reachable
    )
    {
        var order = new List<Guid>();
        var seen = new HashSet<Guid>();

        void Visit(Guid id)
        {
            if (!reachable.Contains(id)) return;
            if (!seen.Add(id)) return;

            order.Add(id);

            if (!map.TryGetValue(id, out var node)) return;

            foreach (var input in node.Inputs)
            {
                var childId = input.Id;
                if (childId == Guid.Empty) continue;
                if (!reachable.Contains(childId)) continue;
                Visit(childId);
            }
        }

        Visit(rootId);
        return order;
    }

    private Dictionary<Guid, Point> ComputePositions(
        Guid rootId,
        HashSet<Guid> reachable,
        List<ResourceGraphEdge> edges,
        Dictionary<Guid, int> level,
        Dictionary<Guid, ResourceItemViewModel> map
    )
    {
        var parentsOf = reachable.ToDictionary(id => id, _ => new List<Guid>());
        var childrenOf = reachable.ToDictionary(id => id, _ => new List<Guid>());

        foreach (var e in edges)
        {
            if (!reachable.Contains(e.FromId) || !reachable.Contains(e.ToId)) continue;
            parentsOf[e.FromId].Add(e.ToId);
            childrenOf[e.ToId].Add(e.FromId);
        }

        var lengthMemo = new Dictionary<Guid, int>();

        int LengthToLeaf(Guid id)
        {
            if (lengthMemo.TryGetValue(id, out var v)) return v;

            if (!childrenOf.TryGetValue(id, out var kids) || kids.Count == 0)
                return lengthMemo[id] = 0;

            int best = 0;
            foreach (var k in kids)
                best = Math.Max(best, 1 + LengthToLeaf(k));

            return lengthMemo[id] = best;
        }

        var lane = reachable.ToDictionary(id => id, _ => int.MinValue);
        lane[rootId] = 0;

        var rootKids = childrenOf[rootId]
            .Distinct()
            .OrderBy(LengthToLeaf)
            .ThenBy(id => map[id].Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (int i = 0; i < rootKids.Count; i++)
            lane[rootKids[i]] = i;

        var maxLevel = reachable.Max(id => level[id]);
        var idsByLevel = Enumerable.Range(0, maxLevel + 1)
            .Select(l => reachable.Where(id => level[id] == l).ToList())
            .ToList();

        if (!idsByLevel[0].Contains(rootId)) idsByLevel[0].Add(rootId);

        var usedAtLevel = new Dictionary<int, HashSet<int>>();
        for (int l = 0; l <= maxLevel; l++)
            usedAtLevel[l] = [];

        foreach (var id in reachable)
        {
            if (lane[id] != int.MinValue)
                usedAtLevel[level[id]].Add(lane[id]);
        }

        static int PickNearestFree(HashSet<int> occupied, int preferred)
        {
            preferred = Math.Max(0, preferred);

            if (!occupied.Contains(preferred))
                return preferred;

            for (int d = 1; d < 5000; d++)
            {
                var up = preferred - d;
                if (up >= 0 && !occupied.Contains(up))
                    return up;

                var down = preferred + d;
                if (!occupied.Contains(down))
                    return down;
            }

            return preferred;
        }

        for (int l = 1; l <= maxLevel; l++)
        {
            var candidates = idsByLevel[l]
                .Select(id =>
                {
                    if (lane[id] != int.MinValue)
                        return (Id: id, Preferred: lane[id]);

                    var ps = parentsOf[id];
                    if (ps.Count == 0)
                        return (Id: id, Preferred: 0);

                    var parentLanes = ps
                        .Where(p => lane[p] != int.MinValue)
                        .Select(p => lane[p])
                        .ToList();

                    if (parentLanes.Count == 0)
                        return (Id: id, Preferred: 0);

                    int preferred;

                    if (ps.Count == 1)
                    {
                        var parentId = ps[0];
                        var parentLane = parentLanes[0];

                        var siblings = childrenOf[parentId]
                            .Where(c => level[c] == level[parentId] + 1)
                            .Distinct()
                            .OrderByDescending(LengthToLeaf)
                            .ThenBy(x => map[x].Name, StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        var siblingIndex = Math.Max(0, siblings.IndexOf(id));
                        preferred = parentLane + siblingIndex;
                    }
                    else
                    {
                        preferred = parentLanes.Min();
                    }

                    return (Id: id, Preferred: preferred);
                })
                .OrderBy(x => x.Preferred)
                .ThenByDescending(x => LengthToLeaf(x.Id))
                .ThenBy(x => map[x.Id].Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var occupied = usedAtLevel[l];

            foreach (var (id, preferred) in candidates)
            {
                if (lane[id] != int.MinValue)
                {
                    occupied.Add(lane[id]);
                    continue;
                }

                var chosen = PickNearestFree(occupied, preferred);
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

    private static void RelaxLevelsToReduceLongSharedEdges(
        HashSet<Guid> reachable,
        List<ResourceGraphEdge> edges,
        Dictionary<Guid, ResourceItemViewModel> map,
        Dictionary<Guid, int> level
    )
    {
        var parentsOf = reachable.ToDictionary(id => id, _ => new List<Guid>());
        foreach (var e in edges)
        {
            if (!reachable.Contains(e.FromId) || !reachable.Contains(e.ToId)) continue;
            parentsOf[e.FromId].Add(e.ToId);
        }

        bool changed;
        int guard = 0;

        do
        {
            changed = false;
            guard++;
            if (guard > 2000) break;

            foreach (var child in reachable)
            {
                var ps = parentsOf[child];
                if (ps.Count <= 1) continue;

                var desiredParentLevel = Math.Max(0, level[child] - 1);

                foreach (var p in ps)
                {
                    if (level[p] < desiredParentLevel)
                    {
                        level[p] = desiredParentLevel;
                        changed = true;
                    }
                }
            }

            foreach (var parent in reachable)
            {
                if (!map.TryGetValue(parent, out var pr)) continue;

                foreach (var inp in pr.Inputs)
                {
                    var child = inp.Id;
                    if (child == Guid.Empty || !reachable.Contains(child)) continue;

                    var desiredChildLevel = level[parent] + 1;
                    if (level[child] < desiredChildLevel)
                    {
                        level[child] = desiredChildLevel;
                        changed = true;
                    }
                }
            }
        }
        while (changed);
    }
}

//public sealed class ResourceGraphBuilder : IResourceGraphBuilder
//{
//    private const double XStep = 260;
//    private const double YStep = 150;
//    private const double MarginLeft = 140;
//    private const double MarginTop = 120;

//    public ResourceGraphBuildResult BuildFor(
//        ResourceItemViewModel root,
//        IReadOnlyCollection<ResourceItemViewModel> allResources,
//        int desiredRootUnits = 1
//    )
//    {
//        if (root.Id == Guid.Empty)
//            return new ResourceGraphBuildResult(false, null, new ResourceGraphLayout());

//        desiredRootUnits = Math.Max(1, desiredRootUnits);

//        var map = allResources
//            .Where(r => r.Id != Guid.Empty)
//            .GroupBy(r => r.Id)
//            .ToDictionary(g => g.Key, g => g.First());

//        if (!map.ContainsKey(root.Id))
//            map[root.Id] = root;

//        var reachable = new HashSet<Guid>();
//        var cycle = DetectCycle(root.Id, map, reachable);
//        if (cycle is not null)
//            return new ResourceGraphBuildResult(true, cycle, null);

//        var topo = TopoOrder(root.Id, map, reachable);

//        var needUnits = reachable.ToDictionary(id => id, _ => 0);
//        needUnits[root.Id] = desiredRootUnits;

//        static int CeilDiv(int a, int b) => (a + b - 1) / b;

//        foreach (var parentId in topo)
//        {
//            var parent = map[parentId];

//            var needParentUnits = needUnits[parentId];
//            if (needParentUnits <= 0)
//                continue;

//            var parentOutput = Math.Max(1, parent.OutputQty);
//            var parentCrafts = CeilDiv(needParentUnits, parentOutput);

//            foreach (var input in parent.Inputs)
//            {
//                var childId = input.Id;
//                if (childId == Guid.Empty)
//                    continue;
//                if (!reachable.Contains(childId))
//                    continue;

//                var q = Math.Max(1, input.Qty);

//                needUnits[childId] += parentCrafts * q;
//            }
//        }

//        var edges = new List<ResourceGraphEdge>();

//        var craftsTotal = reachable.ToDictionary(id => id, id =>
//        {
//            var r = map[id];
//            var outQty = Math.Max(1, r.OutputQty);
//            return CeilDiv(Math.Max(0, needUnits[id]), outQty);
//        });

//        foreach (var parentId in topo)
//        {
//            var parent = map[parentId];

//            var needParentUnits = needUnits[parentId];
//            if (needParentUnits <= 0)
//                continue;

//            var parentOutput = Math.Max(1, parent.OutputQty);
//            var parentCrafts = CeilDiv(needParentUnits, parentOutput);

//            foreach (var input in parent.Inputs)
//            {
//                var childId = input.Id;
//                if (childId == Guid.Empty || !reachable.Contains(childId))
//                    continue;

//                var child = map[childId];
//                var q = Math.Max(1, input.Qty);

//                var edgeNeed = parentCrafts * q;

//                var childOut = Math.Max(1, child.OutputQty);
//                var craftsForEdge = CeilDiv(edgeNeed, childOut);
//                var edgeTime = craftsForEdge * Math.Max(0, child.CraftTimeSec);

//                edges.Add(new ResourceGraphEdge
//                {
//                    FromId = childId,
//                    ToId = parentId,
//                    NeedQty = edgeNeed,
//                    TimeSec = edgeTime
//                });
//            }
//        }

//        var level = reachable.ToDictionary(id => id, _ => 0);
//        foreach (var parentId in topo)
//        {
//            var parentLevel = level[parentId];
//            var parent = map[parentId];

//            foreach (var input in parent.Inputs)
//            {
//                var childId = input.Id;
//                if (childId == Guid.Empty || !reachable.Contains(childId))
//                    continue;

//                level[childId] = Math.Max(level[childId], parentLevel + 1);
//            }
//        }

//        RelaxLevelsToReduceLongSharedEdges(reachable, edges, map, level);

//        var positions = ComputePositions(root.Id, reachable, edges, level, map);
//        var layout = new ResourceGraphLayout();

//        foreach (var id in reachable.OrderBy(id => level[id]).ThenBy(id => map[id].Name, StringComparer.OrdinalIgnoreCase))
//        {
//            var r = map[id];

//            layout.Nodes.Add(new ResourceGraphNode
//            {
//                Id = id,
//                Name = r.Name,
//                Icon = r.Icon,
//                Level = level[id],
//                Position = positions[id]
//            });
//        }

//        foreach (var e in edges)
//            layout.Edges.Add(e);

//        return new ResourceGraphBuildResult(false, null, layout);
//    }

//    private static GraphCycleInfo? DetectCycle(
//        Guid rootId,
//        Dictionary<Guid, ResourceItemViewModel> map,
//        HashSet<Guid> reachable
//    )
//    {
//        var visiting = new HashSet<Guid>();
//        var visited = new HashSet<Guid>();
//        var stack = new List<Guid>();

//        GraphCycleInfo? Dfs(Guid id)
//        {
//            if (visited.Contains(id))
//                return null;

//            if (visiting.Contains(id))
//            {
//                var idx = stack.IndexOf(id);
//                if (idx >= 0)
//                {
//                    var cyclePath = stack.Skip(idx).Concat([id]).ToList();
//                    return new GraphCycleInfo(cyclePath);
//                }
//                return new GraphCycleInfo([id, id]);
//            }

//            visiting.Add(id);
//            stack.Add(id);
//            reachable.Add(id);

//            if (map.TryGetValue(id, out var node))
//            {
//                foreach (var input in node.Inputs)
//                {
//                    var childId = input.Id;
//                    if (childId == Guid.Empty) continue;
//                    if (!map.ContainsKey(childId)) continue;

//                    var cyc = Dfs(childId);
//                    if (cyc is not null)
//                        return cyc;
//                }
//            }

//            stack.RemoveAt(stack.Count - 1);
//            visiting.Remove(id);
//            visited.Add(id);
//            return null;
//        }

//        return Dfs(rootId);
//    }

//    private static List<Guid> TopoOrder(
//        Guid rootId,
//        Dictionary<Guid, ResourceItemViewModel> map,
//        HashSet<Guid> reachable
//    )
//    {
//        var order = new List<Guid>();
//        var seen = new HashSet<Guid>();

//        void Visit(Guid id)
//        {
//            if (!reachable.Contains(id)) return;
//            if (!seen.Add(id)) return;

//            order.Add(id);

//            if (!map.TryGetValue(id, out var node)) return;

//            foreach (var input in node.Inputs)
//            {
//                var childId = input.Id;
//                if (childId == Guid.Empty) continue;
//                if (!reachable.Contains(childId)) continue;
//                Visit(childId);
//            }
//        }

//        Visit(rootId);
//        return order;
//    }

//    private Dictionary<Guid, Point> ComputePositions(
//        Guid rootId,
//        HashSet<Guid> reachable,
//        List<ResourceGraphEdge> edges,
//        Dictionary<Guid, int> level,
//        Dictionary<Guid, ResourceItemViewModel> map
//    )
//    {
//        var parentsOf = reachable.ToDictionary(id => id, _ => new List<Guid>());
//        var childrenOf = reachable.ToDictionary(id => id, _ => new List<Guid>());

//        foreach (var e in edges)
//        {
//            if (!reachable.Contains(e.FromId) || !reachable.Contains(e.ToId)) continue;
//            parentsOf[e.FromId].Add(e.ToId);
//            childrenOf[e.ToId].Add(e.FromId);
//        }

//        var lengthMemo = new Dictionary<Guid, int>();

//        int LengthToLeaf(Guid id)
//        {
//            if (lengthMemo.TryGetValue(id, out var v)) return v;

//            if (!childrenOf.TryGetValue(id, out var kids) || kids.Count == 0)
//                return lengthMemo[id] = 0;

//            int best = 0;
//            foreach (var k in kids)
//                best = Math.Max(best, 1 + LengthToLeaf(k));

//            return lengthMemo[id] = best;
//        }

//        var lane = reachable.ToDictionary(id => id, _ => int.MinValue);
//        lane[rootId] = 0;

//        var rootKids = childrenOf[rootId]
//            .Distinct()
//            .OrderByDescending(LengthToLeaf)
//            .ThenBy(id => map[id].Name, StringComparer.OrdinalIgnoreCase)
//            .ToList();

//        for (int i = 0; i < rootKids.Count; i++)
//            lane[rootKids[i]] = i;

//        var maxLevel = reachable.Max(id => level[id]);
//        var idsByLevel = Enumerable.Range(0, maxLevel + 1)
//            .Select(l => reachable.Where(id => level[id] == l).ToList())
//            .ToList();

//        if (!idsByLevel[0].Contains(rootId)) idsByLevel[0].Add(rootId);

//        for (int l = 1; l <= maxLevel; l++)
//        {
//            foreach (var id in idsByLevel[l]
//                .OrderByDescending(LengthToLeaf)
//                .ThenBy(x => map[x].Name, StringComparer.OrdinalIgnoreCase))
//            {
//                if (lane[id] != int.MinValue)
//                    continue;

//                var ps = parentsOf[id];
//                if (ps.Count == 0)
//                {
//                    lane[id] = 0;
//                    continue;
//                }

//                var parentLanes = ps
//                    .Where(p => lane[p] != int.MinValue)
//                    .Select(p => lane[p])
//                    .ToList();

//                if (parentLanes.Count == 0)
//                {
//                    lane[id] = 0;
//                    continue;
//                }

//                if (ps.Count == 1)
//                {
//                    lane[id] = parentLanes[0];
//                }
//                else
//                {
//                    lane[id] = parentLanes.Max() + 1;
//                }
//            }
//        }

//        var used = lane.Values.Where(v => v != int.MinValue).Distinct().OrderBy(v => v).ToList();
//        var remap = used.Select((v, idx) => (v, idx)).ToDictionary(x => x.v, x => x.idx);

//        foreach (var id in reachable.ToList())
//        {
//            if (lane[id] == int.MinValue) lane[id] = 0;
//            lane[id] = remap[lane[id]];
//        }

//        var pos = new Dictionary<Guid, Point>(reachable.Count);

//        foreach (var id in reachable)
//        {
//            var x = MarginLeft + level[id] * XStep;
//            var y = MarginTop + lane[id] * YStep;
//            pos[id] = new Point(x, y);
//        }

//        return pos;
//    }

//    private static void RelaxLevelsToReduceLongSharedEdges(
//        HashSet<Guid> reachable,
//        List<ResourceGraphEdge> edges,
//        Dictionary<Guid, ResourceItemViewModel> map,
//        Dictionary<Guid, int> level
//    )
//    {
//        var parentsOf = reachable.ToDictionary(id => id, _ => new List<Guid>());
//        foreach (var e in edges)
//        {
//            if (!reachable.Contains(e.FromId) || !reachable.Contains(e.ToId)) continue;
//            parentsOf[e.FromId].Add(e.ToId);
//        }

//        bool changed;
//        int guard = 0;

//        do
//        {
//            changed = false;
//            guard++;
//            if (guard > 2000) break;

//            foreach (var child in reachable)
//            {
//                var ps = parentsOf[child];
//                if (ps.Count <= 1) continue;

//                var desiredParentLevel = Math.Max(0, level[child] - 1);

//                foreach (var p in ps)
//                {
//                    if (level[p] < desiredParentLevel)
//                    {
//                        level[p] = desiredParentLevel;
//                        changed = true;
//                    }
//                }
//            }

//            foreach (var parent in reachable)
//            {
//                if (!map.TryGetValue(parent, out var pr)) continue;

//                foreach (var inp in pr.Inputs)
//                {
//                    var child = inp.Id;
//                    if (child == Guid.Empty || !reachable.Contains(child)) continue;

//                    var desiredChildLevel = level[parent] + 1;
//                    if (level[child] < desiredChildLevel)
//                    {
//                        level[child] = desiredChildLevel;
//                        changed = true;
//                    }
//                }
//            }
//        }
//        while (changed);
//    }
//}
