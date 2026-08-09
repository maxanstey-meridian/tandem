using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Microsoft.JavaScript.NodeApi;

namespace Tandem.NodeApiSpike;

/// <summary>Adapts JavaScript-authored participants to the Tandem runtime.</summary>
[JSExport]
public static partial class NodePipelineBridge
{
    private static readonly List<nint> _nativeLibraries = [];

    private static void PreloadDependencies()
    {
        var directory = Path.GetDirectoryName(typeof(NodePipelineBridge).Assembly.Location);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        var loaded = AppDomain
            .CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetName().Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(directory, "*.dll"))
        {
            try
            {
                var name = AssemblyName.GetAssemblyName(path).Name;
                if (name is not null && loaded.Add(name))
                {
                    AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
                }
            }
            catch (BadImageFormatException) { }
            catch (FileLoadException) { }
        }
        var nativePath = Path.Combine(directory, "libe_sqlite3.dylib");
        if (!File.Exists(nativePath) || _nativeLibraries.Count != 0)
        {
            return;
        }

        _nativeLibraries.Add(NativeLibrary.Load(nativePath));
        var sqliteHandle = _nativeLibraries[0];
        var provider = AppDomain
            .CurrentDomain.GetAssemblies()
            .FirstOrDefault(assembly =>
                assembly.GetName().Name == "SQLitePCLRaw.provider.e_sqlite3"
            );
        if (provider is not null)
        {
            NativeLibrary.SetDllImportResolver(
                provider,
                (name, _, _) =>
                    name.Contains("e_sqlite3", StringComparison.Ordinal) ? sqliteHandle : 0
            );
        }
    }

    internal static Task<T> InvokeOnJavaScriptThreadAsync<T>(
        SynchronizationContext context,
        Func<Task<T>> callback
    )
    {
        if (ReferenceEquals(SynchronizationContext.Current, context))
        {
            return callback();
        }

        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        context.Post(
            async _ =>
            {
                try
                {
                    completion.SetResult(await callback());
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
            },
            null
        );
        return completion.Task;
    }
}

internal sealed record JavaScriptState(string Json);

internal sealed class JavaScriptStage(string id, Func<string, Task<string>> run)
    : IGeneratedPipelineStep<JavaScriptState, GeneratedStepCompletion>
{
    public string Id => id;
    public PipelineNodeDescriptor Descriptor =>
        new GeneratedStateStepDescriptor<JavaScriptState>(Id, ExecuteAsync);

    private async ValueTask<JavaScriptState> ExecuteAsync(
        JavaScriptState state,
        CancellationToken cancellationToken
    ) => new(await run(state.Json));
}

internal sealed class JavaScriptCompletion(string id, Func<string, string> summarize)
    : IPipelineCompletion<JavaScriptState>
{
    public string Id => id;

    public string Summarize(JavaScriptState state) => summarize(state.Json);
}

internal sealed class JavaScriptFailure(string id, Func<string, string> summarize)
    : IPipelineFailure<JavaScriptState>
{
    public string Id => id;

    public string Summarize(JavaScriptState state) => summarize(state.Json);
}
