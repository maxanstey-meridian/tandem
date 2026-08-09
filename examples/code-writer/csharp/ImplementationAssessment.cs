using System.Diagnostics;
using System.Text.Json;

namespace Tandem.Sample.CodeWriter;

public sealed class ImplementationAssessment
{
    private const int OutputLimit = 64 * 1024;
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(2);
    private static readonly AssessmentCase[] _cases =
    [
        new("  Hello, World!  ", "hello-world"),
        new("Crème brûlée", "creme-brulee"),
        new("already---slugged", "already-slugged"),
        new("___Edge___", "edge"),
        new("!!!", ""),
        new("mañana café 123", "manana-cafe-123"),
    ];
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public async ValueTask<VerificationResult> AssessAsync(
        string source,
        CancellationToken cancellationToken
    )
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "node",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add("--input-type=module");
        process.StartInfo.ArgumentList.Add("--eval");
        process.StartInfo.ArgumentList.Add(WorkerSource);

        try
        {
            process.Start();
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Failed($"Assessment failed: {exception.Message}");
        }

        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await JsonSerializer.SerializeAsync(
                process.StandardInput.BaseStream,
                new AssessmentRequest(source, _cases),
                _jsonOptions,
                cancellationToken
            );
            process.StandardInput.Close();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_timeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Failed($"Assessment timed out after {_timeout.TotalMilliseconds:0}ms.");
            }

            var output = await outputTask;
            var error = await errorTask;
            if (output.Length > OutputLimit || error.Length > OutputLimit)
            {
                return Failed($"Assessment output exceeded {OutputLimit} characters.");
            }
            if (process.ExitCode != 0)
            {
                return Failed(
                    $"Assessment exited with {process.ExitCode}{(string.IsNullOrWhiteSpace(error) ? "." : $": {error.Trim()}")}"
                );
            }

            try
            {
                return JsonSerializer.Deserialize<VerificationResult>(output, _jsonOptions)
                    ?? Failed("Assessment returned no result.");
            }
            catch (JsonException exception)
            {
                return Failed($"Assessment returned invalid output: {exception.Message}");
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    private static VerificationResult Failed(string error) => new(false, [], error);

    private sealed record AssessmentRequest(string Source, IReadOnlyList<AssessmentCase> Cases);

    private sealed record AssessmentCase(string Input, string Expected);

    private const string WorkerSource = """
        import vm from "node:vm";

        const chunks = [];
        for await (const chunk of process.stdin) chunks.push(chunk);
        const request = JSON.parse(Buffer.concat(chunks).toString("utf8"));
        const context = vm.createContext(Object.create(null), {
          codeGeneration: { strings: false, wasm: false },
        });

        try {
          const script = new vm.Script(`"use strict"; (${request.source})`);
          const implementation = script.runInContext(context, { timeout: 100 });
          if (typeof implementation !== "function") {
            throw new Error("JavaScript must evaluate to a function");
          }
          const cases = request.cases.map(({ input, expected }) => {
            try {
              const actual = implementation(input);
              if (Object.prototype.toString.call(actual) === "[object Promise]") {
                throw new Error("Implementation returned a Promise; a synchronous string is required.");
              }
              if (typeof actual !== "string") {
                throw new Error(`Implementation returned ${typeof actual}; a string is required.`);
              }
              return { input, expected, actual, passed: actual === expected, error: null };
            } catch (error) {
              return { input, expected, actual: null, passed: false, error: String(error?.message ?? error) };
            }
          });
          process.stdout.write(JSON.stringify({
            passed: cases.every((item) => item.passed), cases, error: null,
          }));
        } catch (error) {
          process.stdout.write(JSON.stringify({
            passed: false, cases: [], error: String(error?.message ?? error),
          }));
        }
        """;
}
