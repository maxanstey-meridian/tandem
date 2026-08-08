namespace Tandem.Delivery;

public sealed class RunReady : IPipelineCompletion<DeliveryState>
{
    public string Id => DeliveryIds.Complete;

    public string Summarize(DeliveryState state) => "Run ready";
}

public sealed class RunFailed : IPipelineFailure<DeliveryState>
{
    public string Id => DeliveryIds.Failed;

    public string Summarize(DeliveryState state) => "Run failed";
}
