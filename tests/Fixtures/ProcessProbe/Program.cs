using System.Diagnostics;
using System.Text.Json;

namespace ProcessProbe;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args[0] == "-X") { Console.Write(JsonSerializer.Serialize(args)); return 0; }
        if (args[0] == "sleep") { await Task.Delay(int.Parse(args[1])); return 0; }
        if (args[0] == "streams")
        {
            await Task.WhenAll(Console.Out.WriteAsync(new string('O', 200000)), Console.Error.WriteAsync(new string('E', 200000)));
            return int.Parse(args[1]);
        }
        if (args[0] is "spawn" or "spawn-and-exit")
        {
            var start = new ProcessStartInfo(System.Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("sleep"); start.ArgumentList.Add("60000");
            using var child = Process.Start(start)!;
            File.WriteAllText(args[1], child.Id.ToString());
            if (args[0] == "spawn") await Task.Delay(60000);
            return 0;
        }
        if (args[0] == "pid") { File.WriteAllText(args[1], System.Environment.ProcessId.ToString()); await Task.Delay(60000); return 0; }
        if (args[0] == "throw") throw new InvalidOperationException("fixture invocation failed");
        Console.Write(JsonSerializer.Serialize(new { Arguments = args.Skip(2), WorkingDirectory = System.Environment.CurrentDirectory, Environment = System.Environment.GetEnvironmentVariable("PROBE_VALUE") }));
        Console.Error.Write("probe stderr");
        return int.Parse(args[1]);
    }
}
public static class Methods { public static Task<int> Main(string[] args) => Program.Main(args); }
public static class NoArguments { public static int Main() => 7; }
public static class VoidMain { public static void Main() => Console.Write("void"); }
public static class TaskMain { public static async Task Main() { await Task.Yield(); Console.Write("task"); } }
public static class InvalidMain { public static string Main() => "invalid"; }
public static class AmbiguousMain { public static int Main() => 0; public static int Main(string[] args) => 0; }
