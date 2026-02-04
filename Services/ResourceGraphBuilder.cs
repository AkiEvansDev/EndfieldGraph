using EndfieldGraph.Models;
using EndfieldGraph.Services.Helpers;
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
                    TimeSeconds = e.TimeSeconds
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

            var parentOutput = Math.Max(1, parent.Count);
            var parentCrafts = CeilDiv(needParentUnits, parentOutput);

            foreach (var input in parent.Inputs)
            {
                var childId = input.Id;
                if (childId == Guid.Empty)
                    continue;
                if (!reachable.Contains(childId))
                    continue;

                var q = Math.Max(1, input.Count);
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

            var parentOutput = Math.Max(1, parent.Count);
            var parentCrafts = CeilDiv(needParentUnits, parentOutput);

            foreach (var input in parent.Inputs)
            {
                var childId = input.Id;
                if (childId == Guid.Empty || !reachable.Contains(childId))
                    continue;

                var child = map[childId];
                var q = Math.Max(1, input.Count);

                var edgeNeed = parentCrafts * q;

                var childOut = Math.Max(1, child.Count);
                var craftsForEdge = CeilDiv(edgeNeed, childOut);
                var edgeTime = craftsForEdge * Math.Max(0, child.Seconds);

                edges.Add(new ResourceGraphEdge
                {
                    FromId = childId,
                    ToId = parentId,
                    NeedCount = edgeNeed,
                    TimeSeconds = edgeTime
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
                var totalNeed = g.Sum(x => x.NeedCount);

                var childOut = Math.Max(1, child.Count);
                var crafts = CeilDiv(totalNeed, childOut);
                var totalTime = crafts * Math.Max(0, child.Seconds);

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
        Dictionary<Guid, ResourceViewModel> map,
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
        Dictionary<Guid, ResourceViewModel> map,
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
        Dictionary<Guid, ResourceViewModel> map
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
        Dictionary<Guid, ResourceViewModel> map,
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
