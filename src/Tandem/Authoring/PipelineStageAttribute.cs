namespace Tandem;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class PipelineStageAttribute(string id) : Attribute
{
    public string Id { get; } = id;
}
