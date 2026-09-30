# Meridian.Tandem.Generators

The Roslyn source generator used by `Meridian.Tandem` for typed C# stages.

Install this package alongside `Meridian.Tandem` when using generated C# stages:

```sh
dotnet add package Meridian.Tandem.Generators
```

It is a development dependency, so `dotnet add package` marks the reference `PrivateAssets="all"` and it does not
become part of the application's published API.

Then annotate a partial class with `[PipelineStage("step-id")]`.

See the [Tandem repository](https://github.com/maxanstey-meridian/tandem) for generated-stage examples.
