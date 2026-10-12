using System.Text.Json;

// Controlled MI peer for process-boundary tests. It never attaches to a process.
var responses = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scenario.json")))!;
var commandsPath = Path.Combine(AppContext.BaseDirectory, "commands.txt");
while (Console.ReadLine() is { } line)
{
    var digits = line.TakeWhile(char.IsDigit).Count();
    var token = line[..digits];
    var command = line[digits..];
    File.AppendAllText(commandsPath, command + Environment.NewLine);
    var response = responses.OrderByDescending(r => r.Key.Length).FirstOrDefault(r => command.StartsWith(r.Key, StringComparison.Ordinal)).Value ?? "^done";
    if (response == "$close") return;
    Console.WriteLine("=thread-created,id=\"ignored\"");
    Console.WriteLine(token + response);
    if (command == "-gdb-exit") return;
}
