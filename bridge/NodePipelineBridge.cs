using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Microsoft.JavaScript.NodeApi;

namespace Tandem.NodeApiSpike;

/// <summary>Adapts JavaScript-authored participants to the Tandem runtime.</summary>
[JSExport]
public static partial class NodePipelineBridge
{
    private static readonly object _dependencyLock = new();
    private static bool _dependenciesConfigured;

    private static void PreloadDependencies()
    {
        lock (_dependencyLock)
        {
            if (_dependenciesConfigured)
                return;
            LoadManagedDependencies();
            AssemblyLoadContext.Default.ResolvingUnmanagedDll += ResolveNativeDependency;
            _dependenciesConfigured = true;
        }
    }

    // node-api-dotnet resolves missing assemblies by calling back into JavaScript, which
    // fails off Node's thread (0x80131509). Runs move to the thread pool, so every bundled
    // assembly is loaded up front, on Node's thread, into the default context.
    private static void LoadManagedDependencies()
    {
        var loaded = AppDomain
            .CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetName().Name)
            .Where(name => name is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(RuntimeDirectory(), "*.dll").Order())
        {
            var name = System.Reflection.AssemblyName.GetAssemblyName(path);
            if (name.Name is null || !loaded.Add(name.Name))
                continue;
            AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        }
    }

    private static nint ResolveNativeDependency(System.Reflection.Assembly assembly, string name)
    {
        var path = Path.Combine(
            RuntimeDirectory(),
            NativeLibraryFileName(name, RuntimeInformation.RuntimeIdentifier)
        );
        return NativeLibrary.TryLoad(path, out var handle) ? handle : 0;
    }

    internal static string NativeLibraryFileName(string name, string runtimeIdentifier) =>
        runtimeIdentifier.StartsWith("win", StringComparison.Ordinal) ? $"{name}.dll"
        : runtimeIdentifier.StartsWith("osx", StringComparison.Ordinal) ? $"lib{name}.dylib"
        : $"lib{name}.so";

    private static string RuntimeDirectory() =>
        Path.GetDirectoryName(typeof(NodePipelineBridge).Assembly.Location)
        ?? throw new InvalidOperationException("The Tandem runtime directory is unavailable.");

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
