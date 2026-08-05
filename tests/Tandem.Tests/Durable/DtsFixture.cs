using System.Net.Sockets;
using Xunit.Sdk;

namespace Tandem.Tests.Durable;

/// <summary>
/// Checks whether the Durable Task Scheduler emulator is reachable on
/// localhost:8080. Tests that require the emulator skip with a clear message
/// when it is not running.
/// </summary>
public static class DtsFixture
{
    public const string EmulatorAddress = "http://localhost:8080";
    public const string TaskHub = "default";

    private static readonly Lazy<bool> _reachable = new(IsEmulatorReachable);

    public static bool IsReachable => _reachable.Value;

    public static void EnsureReachable()
    {
        if (!_reachable.Value)
        {
            throw SkipException.ForSkip(
                "DTS emulator is not reachable at "
                    + EmulatorAddress
                    + ". Start it with: docker run -d --name tandem-dts -p 8080:8080 -p 8082:8082 mcr.microsoft.com/dts/dts-emulator:latest"
            );
        }
    }

    private static bool IsEmulatorReachable()
    {
        try
        {
            using var client = new TcpClient();
            var result = client.BeginConnect("localhost", 8080, null, null);
            if (!result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(2)))
            {
                return false;
            }

            client.EndConnect(result);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
