namespace EndfieldGraph.Services;

public sealed record BuildGoalSpec(Guid ResourceId, double QtyPerMin);

public sealed record BuildLeafNeed(
    Guid Id,
    string Name,
    byte[] IconPngBytes,
    double NeedPerMin
);

public sealed record BuildCalcRow(
    Guid Id,
    string Name,
    byte[] IconPngBytes,
    bool IsGoal,
    bool IsLeaf,

    double ConsumedPerMin,
    double ProducedPerMin,
    double DeltaPerMin,

    double CraftsPerMin,
    int Machines,

    int OutputQty,
    int CraftTimeSec
);

public sealed class BuildCalcResult
{
    public required IReadOnlyList<BuildLeafNeed> Leaves { get; init; }
    public required IReadOnlyList<BuildCalcRow> Rows { get; init; }
    public required IReadOnlyDictionary<Guid, double> NeedPerMinById { get; init; }
}
