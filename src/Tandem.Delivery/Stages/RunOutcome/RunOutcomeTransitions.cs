namespace Tandem.Delivery;

public sealed class CompleteRunTransition
{
    public DeliveryState Execute(DeliveryState state) => state;
}

public sealed class FailRunTransition
{
    public DeliveryState Execute(DeliveryState state) => state;

    public string Summarize(string sourceBlock, string sourceKind) =>
        $"Unhandled outcome '{sourceKind}' from block '{sourceBlock}'";
}
