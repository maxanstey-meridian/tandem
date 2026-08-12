using System.Diagnostics;
using System.Text;

Console.OutputEncoding = Encoding.UTF8;

switch (args[0])
{
    case "inspect":
        Console.WriteLine(Environment.CurrentDirectory);
        Console.WriteLine(Environment.GetEnvironmentVariable(args[1]) ?? "<missing>");
        foreach (var argument in args.Skip(2))
        {
            Console.WriteLine($"[{argument}]");
        }
        return 0;
    case "result":
        Console.Write(args[1]);
        Console.Error.Write(args[2]);
        return int.Parse(args[3]);
    case "noisy":
        var bytes = Encoding.UTF8.GetBytes(new string('x', int.Parse(args[1])));
        await Task.WhenAll(
            Console.OpenStandardOutput().WriteAsync(bytes).AsTask(),
            Console.OpenStandardError().WriteAsync(bytes).AsTask()
        );
        return 0;
    case "wait":
        Console.WriteLine("ready");
        Console.Out.Flush();
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    case "tree":
        var marker = args[1];
        using (var child = Process.Start(CreateStartInfo("mark", marker)))
        {
            Console.WriteLine(child?.Id ?? -1);
            Console.Out.Flush();
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
        return 0;
    case "mark":
        await Task.Delay(TimeSpan.FromSeconds(2));
        await File.WriteAllTextAsync(args[1], "alive");
        return 0;
    default:
        return 2;
}

static ProcessStartInfo CreateStartInfo(params string[] arguments)
{
    var assembly = typeof(Program).Assembly.Location;
    var startInfo = new ProcessStartInfo("dotnet") { UseShellExecute = false };
    startInfo.ArgumentList.Add(assembly);
    foreach (var argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }
    return startInfo;
}
