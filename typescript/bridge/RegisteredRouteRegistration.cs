namespace Tandem.NodeApiSpike;

internal static class RegisteredRouteRegistration
{
    public static PipelineBuilder<JavaScriptState> Start(
        RegisteredParticipant start,
        string name
    ) =>
        start switch
        {
            RegisteredInteraction interaction => Pipeline.Start(interaction.Interaction, name),
            RegisteredStage stage => Pipeline.Start<JavaScriptState, GeneratedStepCompletion>(
                stage.Stage,
                name
            ),
            RegisteredStandard standard => Pipeline.Start<
                JavaScriptState,
                Outcome<JavaScriptState>
            >(standard.Standard, name),
            _ => throw new InvalidOperationException("A terminal cannot start a pipeline."),
        };

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
            bool.Parse(callbacks.Invoke(route.PredicateCallback!, state.Json, ""));
        if (source is RegisteredStandard standard)
        {
            var selector = route.Outcome == "failed" ? standard.Failed : standard.Success;
            if (target is RegisteredInteraction targetInteraction)
            {
                if (route.PredicateCallback is null)
                    builder.Route(selector, targetInteraction.Interaction, route.Label!);
                else
                    builder.Route(selector, Predicate, targetInteraction.Interaction, route.Label!);
            }
            else
            {
                if (route.PredicateCallback is null)
                    builder.Route(selector, Destination(target), route.Label!);
                else
                    builder.Route(selector, Predicate, Destination(target), route.Label!);
            }
        }
        else if (source is RegisteredInteraction sourceInteraction)
        {
            if (target is RegisteredInteraction targetInteraction)
            {
                if (route.PredicateCallback is null)
                    builder.Route(
                        sourceInteraction.Interaction,
                        targetInteraction.Interaction,
                        route.Label!
                    );
                else
                    builder.Route(
                        Predicate,
                        sourceInteraction.Interaction,
                        targetInteraction.Interaction,
                        route.Label!
                    );
            }
            else
            {
                if (route.PredicateCallback is null)
                    builder.Route(sourceInteraction.Interaction, Destination(target), route.Label!);
                else
                    builder.Route(
                        Predicate,
                        sourceInteraction.Interaction,
                        Destination(target),
                        route.Label!
                    );
            }
        }
        else if (source is RegisteredStage stage && target is RegisteredInteraction interaction)
        {
            if (route.PredicateCallback is null)
                builder.Route(stage.Stage, interaction.Interaction, route.Label!);
            else
                builder.Route(Predicate, stage.Stage, interaction.Interaction, route.Label!);
        }
        else if (source is RegisteredStage sourceStage)
        {
            if (route.PredicateCallback is null)
                builder.Route(sourceStage.Stage, Destination(target), route.Label!);
            else
                builder.Route(Predicate, sourceStage.Stage, Destination(target), route.Label!);
        }
    }

    private static IPipelineNode<JavaScriptState> Destination(RegisteredParticipant participant) =>
        participant switch
        {
            RegisteredStage stage => stage.Stage,
            RegisteredStandard standard => standard.Standard,
            RegisteredTerminal terminal => terminal.Terminal,
            _ => throw new InvalidOperationException(
                "Interaction destinations must be registered through their request node."
            ),
        };
}
