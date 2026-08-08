namespace Tandem;

public sealed class PipelineRunException(string message, Exception? inner = null)
    : Exception(message, inner);
