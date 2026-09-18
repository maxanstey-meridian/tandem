using FluentValidation;

namespace Tandem.Advanced;

/// <summary>
/// A packet-declared workspace command. The fixed command string and the target tool remain the
/// single source of argument semantics; declared arguments are a plain passthrough string array
/// composed per element onto the command.
/// </summary>
public sealed record PacketCommand(
    string Name,
    string Description,
    string Command,
    IReadOnlyList<string>? Arguments = null
)
{
    /// <summary>
    /// Admits the declared command into the workspace command surface through
    /// <see cref="AgentCommand"/>, enforcing the same count and length bounds.
    /// </summary>
    public AgentCommand ToAgentCommand() =>
        AgentCommand.Define(Name, Description, Command, Arguments);
}

/// <summary>
/// Admission validation for packet-declared commands. Failures surface as ordinary
/// ArgumentException-class validation problems with corrective guidance.
/// </summary>
public sealed class PacketValidator : AbstractValidator<PacketCommand>
{
    public PacketValidator()
    {
        RuleFor(command => command.Name)
            .NotEmpty()
            .Matches("^[A-Za-z_][A-Za-z0-9_-]{0,63}$")
            .WithMessage(
                "Tool names must contain at most 64 ASCII letters, digits, underscores, or hyphens and start with a letter or underscore."
            );
        RuleFor(command => command.Description).NotEmpty();
        RuleFor(command => command.Command).NotEmpty();
        RuleFor(command => command.Arguments)
            .Must(arguments =>
                arguments is null || arguments.Count <= AgentCommand.MaximumArgumentCount
            )
            .WithMessage(
                $"A command accepts at most {AgentCommand.MaximumArgumentCount} arguments."
            );
        RuleForEach(command => command.Arguments)
            .NotNull()
            .WithMessage("A command argument cannot be null.")
            .Must(value => value is null || !string.IsNullOrWhiteSpace(value))
            .WithMessage("A command argument must not be blank.")
            .Must(value => value is null || value.Length <= AgentCommand.MaximumArgumentLength)
            .WithMessage(
                $"A command argument must be at most {AgentCommand.MaximumArgumentLength} characters."
            );
    }
}
