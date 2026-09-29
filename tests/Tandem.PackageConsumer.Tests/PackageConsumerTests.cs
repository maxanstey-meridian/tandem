using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Tandem.Advanced;

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
            await PackAsync("src/Tandem.Packets/Tandem.Packets.csproj", feed, version);
            await PackAsync("src/Tandem.Terminal/Tandem.Terminal.csproj", feed, version);
            await PackAsync(
                "src/Tandem.OpenAICompatible/Tandem.OpenAICompatible.csproj",
                feed,
                version
            );
            foreach (var packageId in PackageIds)
            {
                AssertPackageMetadata(feed, packageId, version);
            }
            var config = WriteNuGetConfig(temp, feed);

            await ProveConsumerAsync(
                temp,
                packages,
                config,
                version,
                "Songwriter",
                "examples/songwriter/csharp",
                advanced: false,
                "SongwriterProgram.cs"
            );
            await ProveConsumerAsync(
                temp,
                packages,
                config,
                version,
                "CodeWriter",
                "examples/code-writer/csharp",
                advanced: false,
                "CodeWriterProgram.cs"
            );
            await ProveConsumerAsync(
                temp,
                packages,
                config,
                version,
                "Debate",
                "examples/debate/csharp",
                advanced: true,
                "DebateProgram.cs"
            );
            await ProveLedgerConsumerAsync(temp, packages, config, version);
            await ProvePacketsConsumerAsync(temp, packages, config, version);
            await ProveOptionalPackagesConsumerAsync(temp, packages, config, version);
            await ProveGlobalNamespaceStageConsumerAsync(temp, packages, config, version);
        }
        finally
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, recursive: true);
            }
        }
    }

    private static async Task ProveGlobalNamespaceStageConsumerAsync(
        string temp,
        string packages,
        string config,
        string version
    )
    {
        var directory = Path.Combine(temp, "GlobalNamespaceStage");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "GlobalNamespaceStage.csproj"),
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Meridian.Tandem" Version="{version}" />
                <PackageReference Include="Meridian.Tandem.Generators" Version="{version}" PrivateAssets="all" IncludeAssets="runtime; build; native; contentfiles; analyzers; buildtransitive" />
              </ItemGroup>
            </Project>
            """
        );
        await File.WriteAllTextAsync(
            Path.Combine(directory, "Program.cs"),
            """
            using Tandem;

            var normalize = new NormalizeStage();
            var result = await new PipelineRunner().RunAsync(
                Pipeline.Start(normalize, "global-namespace-stage").Build(normalize),
                new ConsumerState("  Hello  ")
            );
            Console.WriteLine(result.State.Value);

            public sealed record ConsumerState(string Value);

            [PipelineStage("normalize")]
            public sealed partial class NormalizeStage
            {
                public ValueTask<ConsumerState> ExecuteAsync(
                    ConsumerState state,
                    CancellationToken cancellationToken
                ) => ValueTask.FromResult(state with { Value = state.Value.Trim() });
            }
            """
        );
        var project = Path.Combine(directory, "GlobalNamespaceStage.csproj");
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

    private static async Task ProvePacketsConsumerAsync(
        string temp,
        string packages,
        string config,
        string version
    )
    {
        var directory = Path.Combine(temp, "Packets");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "Packets.csproj"),
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
              <ItemGroup><PackageReference Include="Meridian.Tandem.Packets" Version="{version}" /></ItemGroup>
            </Project>
            """
        );
        await File.WriteAllTextAsync(
            Path.Combine(directory, "Program.cs"),
            """
            using Tandem.Packets;
            var input = PacketFile.Parse<Input>("---\ntitle: Packed\nitems: []\n---\nContext");
            if (input.Value.Title != "Packed" || input.Context != "Context") throw new Exception("Packet proof failed.");
            sealed record Input(string Title, IReadOnlyList<string> Items);
            """
        );
        var project = Path.Combine(directory, "Packets.csproj");
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
            .Select(property => property.Name)
            .ToArray();
        libraries.Should().Contain($"Meridian.Tandem.Packets/{version}");
        libraries
            .Should()
            .Contain(name => name.StartsWith("YamlDotNet/", StringComparison.OrdinalIgnoreCase));
        libraries
            .Should()
            .NotContain(name => name.StartsWith("Meridian.Tandem/", StringComparison.Ordinal));
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
                <PackageReference Include="Meridian.Tandem.Ledger" Version="{version}" />
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

    private static async Task ProveOptionalPackagesConsumerAsync(
        string temp,
        string packages,
        string config,
        string version
    )
    {
        var directory = Path.Combine(temp, "OptionalPackages");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "OptionalPackages.csproj"),
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Meridian.Tandem.OpenAICompatible" Version="{version}" />
                <PackageReference Include="Meridian.Tandem.Terminal" Version="{version}" />
              </ItemGroup>
            </Project>
            """
        );
        await File.WriteAllTextAsync(
            Path.Combine(directory, "Program.cs"),
            """
            using Tandem.OpenAICompatible;
            using Tandem.Terminal;

            _ = typeof(ReasoningExtractionChatClient);
            _ = typeof(TerminalPipelineRunner);
            """
        );
        var project = Path.Combine(directory, "OptionalPackages.csproj");
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

    private static void AssertPackageMetadata(string feed, string packageId, string version)
    {
        var packagePath = Path.Combine(feed, $"{packageId}.{version}.nupkg");
        File.Exists(packagePath).Should().BeTrue($"{packageId} should be packed");
        using var package = ZipFile.OpenRead(packagePath);
        var nuspecEntry = package.Entries.Single(entry => entry.FullName.EndsWith(".nuspec"));
        using var nuspecStream = nuspecEntry.Open();
        var metadata = XDocument.Load(nuspecStream).Root!.Elements().Single().Elements();
        metadata.Single(element => element.Name.LocalName == "id").Value.Should().Be(packageId);
        metadata.Single(element => element.Name.LocalName == "version").Value.Should().Be(version);
        metadata
            .Single(element => element.Name.LocalName == "authors")
            .Value.Should()
            .Be("Max Anstey");
        var repository = metadata.Single(element => element.Name.LocalName == "repository");
        repository.Attribute("type")?.Value.Should().Be("git");
        repository
            .Attribute("url")
            ?.Value.Should()
            .Be("https://github.com/maxanstey-meridian/tandem.git");
        package.Entries.Should().Contain(entry => entry.FullName == "README.md");
        File.Exists(Path.Combine(feed, $"{packageId}.{version}.snupkg")).Should().BeTrue();
    }

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
        File.Copy(
            Fixture("ScriptedChatClient.cs"),
            Path.Combine(directory, "ScriptedChatClient.cs")
        );
        File.Copy(Fixture(program), Path.Combine(directory, "Program.cs"), overwrite: true);
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
        libraries.Should().Contain($"Meridian.Tandem/{version}");
        libraries.Should().Contain($"Meridian.Tandem.Generators/{version}");
        if (advanced)
        {
            libraries.Should().Contain($"Meridian.Tandem.Advanced/{version}");
            libraries.Should().Contain("Tavily/1.0.1");
        }
        else
        {
            libraries
                .Should()
                .NotContain(name =>
                    name.StartsWith("Meridian.Tandem.Advanced/", StringComparison.Ordinal)
                );
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
                <PackageReference Include="Meridian.Tandem" Version="{version}" />
                {(
                advanced
                    ? $"<PackageReference Include=\"Meridian.Tandem.Advanced\" Version=\"{version}\" />"
                    : ""
            )}
                <PackageReference Include="Meridian.Tandem.Generators" Version="{version}" PrivateAssets="all" IncludeAssets="runtime; build; native; contentfiles; analyzers; buildtransitive" />
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
        var result = await LocalProcess.RunAsync(new(fileName, arguments, workingDirectory));
        result
            .ExitCode.Should()
            .Be(0, $"{fileName} {string.Join(' ', arguments)}\n{result.Stdout}\n{result.Stderr}");
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

    private static readonly string[] PackageIds =
    [
        "Meridian.Tandem",
        "Meridian.Tandem.Advanced",
        "Meridian.Tandem.Generators",
        "Meridian.Tandem.Ledger",
        "Meridian.Tandem.OpenAICompatible",
        "Meridian.Tandem.Packets",
        "Meridian.Tandem.Terminal",
    ];

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
