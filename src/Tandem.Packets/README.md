# Meridian.Tandem.Packets

`Meridian.Tandem.Packets` reads UTF-8 Markdown files with strict YAML frontmatter into
application-owned C# types.

```sh
dotnet add package Meridian.Tandem.Packets --version 0.1.0-alpha.1
```

```csharp
using Tandem.Packets;

PacketFile<MyPacket> input = PacketFile.Read<MyPacket>("work.packet.md");
```

YAML property names use `snake_case`. Unknown and duplicate fields, aliases, anchors, custom tags, multiple YAML documents, non-finite numbers, excessive nesting, and files larger than 1 MiB are rejected. The Markdown body is returned separately as `Context`; no pipeline is started and no application path is interpreted automatically.

Pass an `IValidator<T>` overload for explicit application validation. Use `input.Source.ResolvePath(...)` when an application decides that one of its values is source-relative.
