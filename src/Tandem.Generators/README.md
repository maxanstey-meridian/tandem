# Meridian.Tandem.Generators

The Roslyn source generator used by `Meridian.Tandem` for typed C# stages.

Install this package alongside `Meridian.Tandem` when using generated C# stages. Keep the reference
private so it does not become part of the application's published API:

```xml
<PackageReference
    Include="Meridian.Tandem.Generators"
    Version="0.1.0-alpha.1"
    PrivateAssets="all"
    IncludeAssets="runtime; build; native; contentfiles; analyzers; buildtransitive"
/>
```

Then annotate a partial class with `[PipelineStage("step-id")]`.

See the [Tandem repository](https://github.com/maxanstey-meridian/tandem) for generated-stage examples.
