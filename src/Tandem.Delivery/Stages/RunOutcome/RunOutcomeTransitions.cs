namespace Tandem.Delivery;

public sealed class CompleteRunTransition
{
    public DeliveryState Execute(DeliveryState state) => state with { Status = RunStatus.Ready };
}

public sealed class FailRunTransition
{
    public DeliveryState Execute(DeliveryState state) => state with { Status = RunStatus.Failed };

    public string Summarize(string sourceBlock, string sourceKind) =>
        $"Unhandled outcome '{sourceKind}' from block '{sourceBlock}'";
}
