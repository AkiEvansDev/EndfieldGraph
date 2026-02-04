namespace EndfieldGraph.Models;

public sealed record BuildGoalSpec(Guid Id, double CountPerMin);

public sealed record BuildLeafNeed(
    Guid Id,
    string Name,
    byte[] Icon,
    double NeedPerMin
);

public sealed record BuildCalcRow(
    Guid Id,
    string Name,
    byte[] Icon,
    bool IsGoal,
    bool IsLeaf,

    double ConsumedPerMin,
    double ProducedPerMin,
    double DeltaPerMin,

    double CraftsPerMin,
    int Machines,

    int Count,
    int Seconds
);

public sealed class BuildCalcResult
{
    public required IReadOnlyList<BuildLeafNeed> Leaves { get; init; }
    public required IReadOnlyList<BuildCalcRow> Rows { get; init; }
    public required IReadOnlyDictionary<Guid, double> NeedPerMinById { get; init; }
}
