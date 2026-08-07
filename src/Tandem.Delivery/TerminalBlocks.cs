namespace Tandem.Delivery;

public sealed class CompleteBlock
{
    public DeliveryState Execute(DeliveryState state) =>
        state with
        {
            Status = Domain.RunStatus.Ready,
        };
}

public sealed class FailedBlock
{
    public DeliveryState Execute(DeliveryState state) =>
        state with
        {
            Status = Domain.RunStatus.Failed,
        };

    public string Summarize(string sourceBlock, string sourceKind) =>
        $"Unhandled outcome '{sourceKind}' from block '{sourceBlock}'";
}
