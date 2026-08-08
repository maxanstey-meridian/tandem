using System.Text.Json;
using Tandem.Infrastructure;

namespace Tandem;

public sealed record PipelineRunOptions(
    Guid? RunId = null,
    PipelineInteractionHandlers? Interactions = null,
    IPipelineObserver? Observer = null
)
{
    internal IPipelineAcceptanceUnitOfWork? AcceptanceUnitOfWork { get; init; }
}

public sealed record PipelineRunResult<TState>(
    Guid RunId,
    TState State,
    PipelineRunStatus Status,
    PipelineRunOutcome? Outcome
)
{
    public bool Succeeded => Status is PipelineRunStatus.Succeeded;
}

public sealed record PipelineRunOutcome(
    string Kind,
    string StepId,
    string Summary,
    JsonElement Payload,
    TimeSpan Duration
);

public sealed record PipelineInteractionContext<TRequest, TResponse>(
    Guid RunId,
    string RequestId,
    string InteractionId,
    TRequest Request
);

public sealed class PipelineInteractionHandlers
{
    private readonly Dictionary<InteractionKey, IRegistration> _registrations = [];

    public PipelineInteractionHandlers Handle<TState, TRequest, TResponse>(
        PipelineInteraction<TState, TRequest, TResponse> interaction,
        Func<
            PipelineInteractionContext<TRequest, TResponse>,
            CancellationToken,
            ValueTask<TResponse>
        > handler
    )
    {
        ArgumentNullException.ThrowIfNull(interaction);
        ArgumentNullException.ThrowIfNull(handler);
        var key = new InteractionKey(interaction.Id, typeof(TRequest), typeof(TResponse));
        if (!_registrations.TryAdd(key, new Registration<TRequest, TResponse>(handler)))
        {
            throw new InvalidOperationException(
                $"An interaction handler for '{interaction.Id}' with '{typeof(TRequest).FullName}' and "
                    + $"'{typeof(TResponse).FullName}' is already registered."
            );
        }
        return this;
    }

    internal ValueTask<object> DispatchAsync(
        PendingExternalRequest request,
        CancellationToken cancellationToken
    )
    {
        var requestType =
            request.RequestClrType
            ?? throw new InvalidOperationException(
                $"Interaction '{request.PortId}' has no request CLR type."
            );
        var responseType =
            request.ResponseClrType
            ?? throw new InvalidOperationException(
                $"Interaction '{request.PortId}' has no response CLR type."
            );
        if (
            !_registrations.TryGetValue(
                new InteractionKey(request.PortId, requestType, responseType),
                out var handler
            )
        )
        {
            throw new InvalidOperationException(
                $"No typed handler is registered for interaction '{request.PortId}' with "
                    + $"request/response types '{requestType.FullName}' and '{responseType.FullName}'."
            );
        }
        return handler.InvokeAsync(request, cancellationToken);
    }

    private readonly record struct InteractionKey(
        string InteractionId,
        Type RequestType,
        Type ResponseType
    );

    private interface IRegistration
    {
        public ValueTask<object> InvokeAsync(
            PendingExternalRequest request,
            CancellationToken cancellationToken
        );
    }

    private sealed class Registration<TRequest, TResponse>(
        Func<
            PipelineInteractionContext<TRequest, TResponse>,
            CancellationToken,
            ValueTask<TResponse>
        > handler
    ) : IRegistration
    {
        public async ValueTask<object> InvokeAsync(
            PendingExternalRequest request,
            CancellationToken cancellationToken
        )
        {
            if (request.Value is not TRequest value)
            {
                throw new InvalidOperationException(
                    $"Interaction '{request.PortId}' produced '{request.Value?.GetType().FullName}', "
                        + $"not '{typeof(TRequest).FullName}'."
                );
            }
            return await handler(
                    new PipelineInteractionContext<TRequest, TResponse>(
                        request.RunId,
                        request.RequestId,
                        request.PortId,
                        value
                    ),
                    cancellationToken
                )
                ?? throw new InvalidOperationException(
                    $"Interaction '{request.PortId}' returned a null response."
                );
        }
    }
}

public sealed class PipelineRunner
{
    public async Task<PipelineRunResult<TState>> RunAsync<TState>(
        Pipeline<TState> pipeline,
        TState initialState,
        PipelineRunOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(initialState);
        options ??= new PipelineRunOptions();
        var runId = options.RunId ?? Guid.CreateVersion7();
        var runner = new InProcessPipelineRunner();
        var output = options.Interactions is null
            ? await runner.RunAsync(
                pipeline,
                runId,
                initialState,
                options.Observer,
                options.AcceptanceUnitOfWork,
                cancellationToken
            )
            : await runner.RunAsync(
                pipeline,
                runId,
                initialState,
                new TypedInteractionHandler(options.Interactions),
                options.Observer,
                options.AcceptanceUnitOfWork,
                cancellationToken
            );
        var outcome = output.LatestOutcome is null
            ? null
            : new PipelineRunOutcome(
                output.LatestOutcome.Kind,
                output.LatestOutcome.BlockId,
                output.LatestOutcome.Summary,
                output.LatestOutcome.Payload,
                output.LatestOutcome.Duration
            );
        return new PipelineRunResult<TState>(runId, output.State, output.Status, outcome);
    }

    private sealed class TypedInteractionHandler(PipelineInteractionHandlers handlers)
        : IExternalRequestHandler
    {
        public async ValueTask<ExternalRequestAnswer> WaitAsync(
            PendingExternalRequest request,
            CancellationToken cancellationToken
        )
        {
            var response = await handlers.DispatchAsync(request, cancellationToken);
            return new ExternalRequestAnswer(request.RunId, request.RequestId, default)
            {
                Value = response,
            };
        }
    }
}
