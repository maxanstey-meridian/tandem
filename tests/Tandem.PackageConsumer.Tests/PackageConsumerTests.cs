using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;

namespace Tandem.PackageConsumer.Tests;

public sealed class PackageConsumerTests
{
    private static readonly string _root = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")
    );

    [Fact]
    public async Task PackedPackages_RunProgressiveSamplesWithIsolatedDependencies()
    {
        var temp = Path.Combine(
            Path.GetTempPath(),
            "tandem-pack-proof-" + Guid.NewGuid().ToString("N")
        );
        var feed = Path.Combine(temp, "feed");
        var packages = Path.Combine(temp, "packages");
        const string version = "0.0.0-packageproof";
        Directory.CreateDirectory(feed);

        try
        {
            await PackAsync("src/Tandem.Generators/Tandem.Generators.csproj", feed, version);
            await PackAsync("src/Tandem/Tandem.csproj", feed, version);
            await PackAsync("src/Tandem.Advanced/Tandem.Advanced.csproj", feed, version);
            await PackAsync("src/Tandem.Ledger/Tandem.Ledger.csproj", feed, version);
            var config = WriteNuGetConfig(temp, feed);

            await ProveConsumerAsync(
                temp,
                packages,
                config,
                version,
                "Songwriter",
                "examples/songwriter/csharp",
                advanced: false,
                SongwriterProgram
            );
            await ProveConsumerAsync(
                temp,
                packages,
                config,
                version,
                "CodeWriter",
                "examples/code-writer/csharp",
                advanced: false,
                CodeWriterProgram
            );
            await ProveConsumerAsync(
                temp,
                packages,
                config,
                version,
                "Debate",
                "examples/debate/csharp",
                advanced: true,
                DebateProgram
            );
            await ProveLedgerConsumerAsync(temp, packages, config, version);
        }
        finally
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, recursive: true);
            }
        }
    }

    private static async Task ProveLedgerConsumerAsync(
        string temp,
        string packages,
        string config,
        string version
    )
    {
        var directory = Path.Combine(temp, "Ledger");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "Ledger.csproj"),
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Tandem.Ledger" Version="{version}" />
              </ItemGroup>
            </Project>
            """
        );
        await File.WriteAllTextAsync(
            Path.Combine(directory, "Program.cs"),
            """
            using Tandem;
            using Tandem.Ledger;

            var path = Path.Combine(Path.GetTempPath(), $"tandem-packed-{Guid.NewGuid():N}.sqlite3");
            var observer = await new SqliteLedgerStore(path).CreateObserverAsync(
                Guid.CreateVersion7(),
                "packed-consumer");
            if (observer is not IPipelinePersistenceObserver)
            {
                throw new InvalidOperationException("Observer is not durable.");
            }
            """
        );
        var project = Path.Combine(directory, "Ledger.csproj");
        await RunAsync(
            directory,
            "dotnet",
            "restore",
            project,
            "--configfile",
            config,
            "--packages",
            packages,
            "--force",
            "--no-cache"
        );
        await RunAsync(
            directory,
            "dotnet",
            "run",
            "--project",
            project,
            "--configuration",
            "Release",
            "--no-restore"
        );
    }

    private static async Task PackAsync(string project, string feed, string version) =>
        await RunAsync(
            _root,
            "dotnet",
            "pack",
            Path.Combine(_root, project),
            "--configuration",
            "Release",
            "--output",
            feed,
            $"-p:Version={version}"
        );

    private static async Task ProveConsumerAsync(
        string temp,
        string packages,
        string config,
        string version,
        string name,
        string sample,
        bool advanced,
        string program
    )
    {
        var directory = Path.Combine(temp, name);
        Directory.CreateDirectory(directory);
        foreach (var source in Directory.EnumerateFiles(Path.Combine(_root, sample), "*.cs"))
        {
            File.Copy(source, Path.Combine(directory, Path.GetFileName(source)));
        }
        await File.WriteAllTextAsync(
            Path.Combine(directory, "ScriptedChatClient.cs"),
            ScriptedClient
        );
        await File.WriteAllTextAsync(Path.Combine(directory, "Program.cs"), program);
        await File.WriteAllTextAsync(
            Path.Combine(directory, name + ".csproj"),
            Project(version, advanced)
        );

        var project = Path.Combine(directory, name + ".csproj");
        await RunAsync(
            directory,
            "dotnet",
            "restore",
            project,
            "--configfile",
            config,
            "--packages",
            packages,
            "--force",
            "--no-cache"
        );
        await RunAsync(
            directory,
            "dotnet",
            "run",
            "--project",
            project,
            "--configuration",
            "Release",
            "--no-restore"
        );

        using var assets = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(directory, "obj", "project.assets.json"))
        );
        var libraries = assets
            .RootElement.GetProperty("libraries")
            .EnumerateObject()
            .Select(p => p.Name)
            .ToArray();
        libraries.Should().Contain($"Tandem/{version}");
        libraries.Should().Contain($"Tandem.Generators/{version}");
        if (advanced)
        {
            libraries.Should().Contain($"Tandem.Advanced/{version}");
        }
        else
        {
            libraries
                .Should()
                .NotContain(name => name.StartsWith("Tandem.Advanced/", StringComparison.Ordinal));
            libraries
                .Should()
                .NotContain(name =>
                    name.StartsWith(
                        "Microsoft.Agents.AI.Harness/",
                        StringComparison.OrdinalIgnoreCase
                    )
                );
        }
        libraries
            .Should()
            .NotContain(name =>
                ForbiddenPackages.Any(prefix =>
                    name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                )
            );
    }

    private static string WriteNuGetConfig(string temp, string feed)
    {
        var path = Path.Combine(temp, "NuGet.config");
        File.WriteAllText(
            path,
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="local" value="{feed}" />
                <add key="nuget" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
            </configuration>
            """
        );
        return path;
    }

    private static string Project(string version, bool advanced) =>
        $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Tandem" Version="{version}" />
                {(
                advanced
                    ? $"<PackageReference Include=\"Tandem.Advanced\" Version=\"{version}\" />"
                    : ""
            )}
                <PackageReference Include="Tandem.Generators" Version="{version}" PrivateAssets="all" IncludeAssets="runtime; build; native; contentfiles; analyzers; buildtransitive" />
                <PackageReference Include="FluentValidation" Version="12.1.1" />
                <PackageReference Include="Microsoft.Extensions.AI" Version="10.8.3" />
                <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.0.10" />
              </ItemGroup>
            </Project>
            """;

    private static async Task RunAsync(
        string workingDirectory,
        string fileName,
        params string[] arguments
    )
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        process
            .ExitCode.Should()
            .Be(0, $"{fileName} {string.Join(' ', arguments)}\n{await stdout}\n{await stderr}");
    }

    private static readonly string[] ForbiddenPackages =
    [
        "Tandem.Delivery/",
        "Tandem.Tool/",
        "ModelContextProtocol",
        "Spectre.Console/",
        "YamlDotNet/",
        "OpenAI/",
        "Microsoft.Extensions.AI.OpenAI/",
        "Microsoft.Extensions.Hosting/",
        "Microsoft.Extensions.Hosting.Abstractions/",
        "System.CommandLine/",
    ];

    private const string ScriptedClient = """
        using System.Runtime.CompilerServices;
        using Microsoft.Extensions.AI;

        internal sealed class ScriptedChatClient(params ChatResponse[] responses) : IChatClient
        {
            private readonly Queue<ChatResponse> _responses = new(responses);

            public static ChatResponse Text(string value) =>
                new(new ChatMessage(ChatRole.Assistant, [new TextContent(value)]))
                {
                    FinishReason = ChatFinishReason.Stop,
                    ModelId = "package-proof",
                };

            public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                foreach (var update in _responses.Dequeue().ToChatResponseUpdates())
                {
                    yield return update;
                }
                await Task.CompletedTask;
            }

            public object? GetService(Type serviceType, object? serviceKey = null) => null;
            public void Dispose() { }
        }
        """;

    private const string SongwriterProgram = """
        using Tandem;
        using Tandem.Sample.Songwriter;

        var participants = SongwriterDefinitions.Create(
            new SongwriterClients(
                new ScriptedChatClient(
                    ScriptedChatClient.Text("{\"lyrics\":\"First draft\"}"),
                    ScriptedChatClient.Text("{\"lyrics\":\"Linted\\ndraft\"}"),
                    ScriptedChatClient.Text("{\"lyrics\":\"Final\\ndraft\"}")
                ),
                new ScriptedChatClient(
                    ScriptedChatClient.Text("{\"accepted\":false,\"feedback\":\"Sharpen it.\"}"),
                    ScriptedChatClient.Text("{\"accepted\":true,\"feedback\":\"Accepted.\"}")
                )
            )
        );
        var result = await new PipelineRunner().RunAsync(
            new SongwriterComposition(participants).Build(),
            new SongwriterState("Rebuild after a storm."),
            cancellationToken: CancellationToken.None
        );
        if (!result.Succeeded || result.State.Lyrics != "Final\ndraft") throw new Exception("Songwriter package proof failed.");
        """;

    private const string CodeWriterProgram = """
        using Microsoft.Extensions.AI;
        using Tandem;
        using Tandem.Sample.CodeWriter;

        var submitImplementation = AgentCapabilities.Create<CodeWriterState, SubmitImplementation>(
            new SubmitImplementationCapability(),
            (state, submission) => state.RecordImplementation(submission)
        );
        var implementationResponse = new ChatResponse(
            new ChatMessage(
                ChatRole.Assistant,
                [new FunctionCallContent(
                    "implementation-1",
                    "submit_implementation",
                    new Dictionary<string, object?>
                    {
                        ["implementation"] = "function (input) { return input.normalize(\"NFD\").replace(/[\\u0300-\\u036f]/g, \"\").toLowerCase().replace(/[^a-z0-9]+/g, \"-\").replace(/^-+|-+$/g, \"\"); }",
                        ["rationale"] = "Normalize diacritics, collapse separators, and trim the slug."
                    }
                )]
            )
        ) { FinishReason = ChatFinishReason.ToolCalls, ModelId = "package-proof" };
        var participants = CodeWriterDefinitions.Create(
            new CodeWriterClients(
                new ScriptedChatClient(implementationResponse),
                new ScriptedChatClient(
                    ScriptedChatClient.Text("{\"decision\":\"Accept\",\"summary\":\"Implementation verified and accepted.\",\"findings\":[]}")
                )
            ),
            submitImplementation
        );
        var result = await new PipelineRunner().RunAsync(
            new CodeWriterComposition(participants).Build(),
            new CodeWriterState(
                [
                    "Return a URL slug for the input string.",
                    "Remove diacritics and non-alphanumeric separators.",
                    "Return lowercase words joined by single hyphens."
                ]
            ),
            new PipelineRunOptions(Observer: new PackagePersistenceObserver()),
            CancellationToken.None
        );
        if (
            !result.Succeeded
            || result.State.Implementation is null
            || result.State.Verification?.Passed is not true
            || result.State.Review?.Decision != ReviewDisposition.Accept
        )
        {
            throw new Exception("CodeWriter package proof failed.");
        }

        var approval = PipelineNodes.WaitFor<CodeWriterState, string, string>(
            "approval",
            _ => "Approve the implementation.",
            (state, _) => state
        );
        var directComplete = PipelineNodes.Complete(new DirectCompletion());
        var direct = Pipeline
            .Start(approval, "direct-interaction")
            .Persist(approval)
            .DoNotPersist(approval)
            .Route(approval, directComplete, "answered")
            .Build(directComplete);
        var handlers = new PipelineInteractionHandlers().Handle(
            approval,
            (_, _) => ValueTask.FromResult("Approved")
        );
        var directResult = await new PipelineRunner().RunAsync(
            direct,
            new CodeWriterState(["Approve this code."]),
            new PipelineRunOptions(Interactions: handlers),
            CancellationToken.None
        );
        if (!directResult.Succeeded) throw new Exception("Interaction package proof failed.");

        sealed class PackagePersistenceObserver : IPipelinePersistenceObserver
        {
            public ValueTask ObserveAsync(
                PipelineObservation observation,
                CancellationToken cancellationToken
            ) => ValueTask.CompletedTask;
        }

        sealed class DirectCompletion : IPipelineCompletion<CodeWriterState>
        {
            public string Id => "direct-complete";
            public string Summarize(CodeWriterState state) => "Direct interaction complete";
        }
        """;

    private const string DebateProgram = """
        using System.Text.Json;
        using FluentValidation;
        using Microsoft.Extensions.AI;
        using Tandem;
        using Tandem.Advanced;
        using Tandem.Sample.Debate;

        var verdict = AgentCapabilities.Create<DebateState, SubmitVerdict>(
            new SubmitVerdictCapability(),
            (state, request) => state.RecordVerdict(request)
        );
        var judgeResponse = new ChatResponse(
            new ChatMessage(
                ChatRole.Assistant,
                [new FunctionCallContent("verdict-1", "submit_verdict", new Dictionary<string, object?> { ["verdict"] = "Affirmed", ["reason"] = "Accepted." })]
            )
        ) { FinishReason = ChatFinishReason.ToolCalls, ModelId = "package-proof" };
        var participants = DebateDefinitions.Create(
            new DebateOptions(
                new ScriptedChatClient(ScriptedChatClient.Text("{\"text\":\"Initial case\"}"), ScriptedChatClient.Text("{\"text\":\"Revised case\"}")),
                new ScriptedChatClient(ScriptedChatClient.Text("{\"accepted\":false,\"critique\":\"Revise\"}"), ScriptedChatClient.Text("{\"accepted\":true,\"critique\":\"Accepted\"}")),
                new ScriptedChatClient(judgeResponse)
            ),
            verdict
        );
        var result = await new PipelineRunner().RunAsync(
            new DebateComposition(participants).Build(),
            new DebateState("Should we proceed?", [], 0, null),
            cancellationToken: CancellationToken.None
        );
        if (!result.Succeeded || result.State.Verdict?.Value != "Affirmed") throw new Exception("Debate package proof failed.");

        using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
        var jsonOutput = Agent
            .Create<DebateState>("json-output", "Return JSON.", new ScriptedChatClient(ScriptedChatClient.Text("{\"value\":1}")))
            .WithMessage(_ => "Return a value.")
            .WithJsonOutput(
                new AgentJsonOutputDefinition<DebateState>(schema.RootElement, "Return a value.", _ => [], ValueType: "package.dynamic-output"),
                (state, _) => state
            )
            .Build();
        var outputResult = await new PipelineRunner().RunAsync(
            Pipeline.Start(jsonOutput, "json-output-proof").Build(jsonOutput),
            new DebateState("JSON output", [], 0, null)
        );
        if (!outputResult.Succeeded) throw new Exception("JSON output package proof failed.");

        var jsonCapability = AgentCapabilities.CreateJson(
            new AgentJsonCapabilityDefinition<DebateState>(
                "accept_json",
                "Accept JSON.",
                schema.RootElement,
                _ => [],
                null,
                _ => "Accepted JSON.",
                "package.dynamic-capability"
            ),
            (state, _) => state
        );
        var capabilityResponse = new ChatResponse(
            new ChatMessage(
                ChatRole.Assistant,
                [new FunctionCallContent("json-1", "accept_json", new Dictionary<string, object?> { ["value"] = 1 })]
            )
        ) { FinishReason = ChatFinishReason.ToolCalls, ModelId = "package-proof" };
        var jsonCapabilityAgent = Agent
            .Create<DebateState>("json-capability", "Accept JSON.", new ScriptedChatClient(capabilityResponse))
            .WithMessage(_ => "Accept a value.")
            .WithCapability(jsonCapability)
            .Build();
        var capabilityResult = await new PipelineRunner().RunAsync(
            Pipeline.Start(jsonCapabilityAgent, "json-capability-proof").Build(jsonCapabilityAgent),
            new DebateState("JSON capability", [], 0, null)
        );
        if (!capabilityResult.Succeeded) throw new Exception("JSON capability package proof failed.");
        """;
}
