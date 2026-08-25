using Microsoft.Extensions.DependencyInjection;
using Tandem;

namespace Examples.CodeWriter;

public static class CodeWriterRegistration
{
    public static IServiceCollection AddCodeWriter(
        this IServiceCollection services,
        CodeWriterClients clients
    )
    {
        var submitImplementation = AgentCapabilities.Create<CodeWriterState, SubmitImplementation>(
            new SubmitImplementationCapability(),
            (state, submission) => state.RecordImplementation(submission)
        );
        services.AddSingleton(clients);
        services.AddSingleton<AgentCapability<CodeWriterState>>(submitImplementation);
        services.AddSingleton(sp =>
            CodeWriterDefinitions.Create(
                sp.GetRequiredService<CodeWriterClients>(),
                sp.GetRequiredService<AgentCapability<CodeWriterState>>()
            )
        );
        services.AddSingleton<CodeWriterComposition>();
        return services;
    }
}
