namespace Tandem.NodeApiSpike;

internal static class RegisteredRouteRegistration
{
    public static PipelineBuilder<JavaScriptState> Start(
        RegisteredParticipant start,
        string name
    ) =>
        start.Interaction is not null ? Pipeline.Start(start.Interaction, name)
        : start.Stage is not null
            ? Pipeline.Start<JavaScriptState, GeneratedStepCompletion>(start.Stage, name)
        : Pipeline.Start<JavaScriptState, Outcome<JavaScriptState>>(start.Standard!, name);

    public static void Add(
        PipelineBuilder<JavaScriptState> builder,
        IReadOnlyDictionary<string, RegisteredParticipant> nodes,
        RegisteredRouteContract route,
        CallbackDispatcher callbacks
    )
    {
        var source = nodes[route.Source!];
        var target = nodes[route.Target!];
        bool Predicate(JavaScriptState state) =>
            route.PredicateCallback is null
            || bool.Parse(callbacks.Invoke(route.PredicateCallback, state.Json, ""));
        if (source.Standard is not null)
        {
            var selector = route.Outcome == "failed" ? source.Failed!.Value : source.Success!.Value;
            if (target.Interaction is not null)
            {
                builder.Route(selector, Predicate, target.Interaction, route.Label!);
            }
            else
            {
                builder.Route(selector, Predicate, target.Node, route.Label!);
            }
        }
        else if (source.Interaction is not null)
        {
            builder.Route(Predicate, source.Interaction, target.Node, route.Label!);
        }
        else if (target.Interaction is not null)
        {
            builder.Route(Predicate, source.Stage!, target.Interaction, route.Label!);
        }
        else
        {
            builder.Route(Predicate, source.Stage!, target.Node, route.Label!);
        }
    }
}
