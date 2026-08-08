namespace Tandem.Delivery;

internal static class VerificationResultFormatting
{
    internal static string Format(IReadOnlyList<VerificationResult> results) =>
        string.Join(
            "\n",
            results.Select(result =>
                $"[{(result.ExitCode == 0 ? "PASS" : "FAIL")}] {result.Command} "
                + $"(exit {result.ExitCode})\nstdout: {result.Stdout}\nstderr: {result.Stderr}"
            )
        );
}
