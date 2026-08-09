using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Microsoft.JavaScript.NodeApi;

namespace Tandem.NodeApiSpike;

/// <summary>Adapts JavaScript-authored participants to the Tandem runtime.</summary>
[JSExport]
public static partial class NodePipelineBridge
{
    private static readonly List<nint> _nativeLibraries = [];
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
        if (!name.Contains("e_sqlite3", StringComparison.Ordinal))
            return 0;
        var path = Path.Combine(RuntimeDirectory(), "libe_sqlite3.dylib");
        if (!File.Exists(path))
            return 0;
        var handle = NativeLibrary.Load(path, assembly, null);
        _nativeLibraries.Add(handle);
        return handle;
    }

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
