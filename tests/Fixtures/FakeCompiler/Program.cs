using System.Text.Json;

// Controlled process peer: records compiler switches and produces only fixture files.
var scenario = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scenario.json")))!;
File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "arguments.json"), JsonSerializer.Serialize(args));
if (args.Contains("--version") || args.Length == 0)
{
    Console.WriteLine(scenario.GetValueOrDefault("version", "Fixture Compiler 14.2.1"));
    return 0;
}
Console.WriteLine(scenario.GetValueOrDefault("stdout", ""));
Console.Error.WriteLine(scenario.GetValueOrDefault("stderr", ""));
if (scenario.GetValueOrDefault("create") == "yes")
{
    var output = args.FirstOrDefault(a => a.StartsWith("/Fe:"))?[4..];
    var index = Array.IndexOf(args, "-o");
    if (index >= 0) output = args[index + 1];
    if (output is not null) File.WriteAllBytes(output, [1,2,3]);
}
return int.Parse(scenario.GetValueOrDefault("exit", "0"));
