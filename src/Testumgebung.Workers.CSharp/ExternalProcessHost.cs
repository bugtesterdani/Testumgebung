/**
 * @file
 * @brief Startet externe Programme oder ruft einen .NET-Einstiegspunkt in einem eigenen Host auf.
 */

using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

namespace Testumgebung.Workers.CSharp;

/// <summary>External program modes are separate from the code-worker JSON-lines protocol.</summary>
internal static class ExternalProcessHost
{
    private sealed record Command(string Executable, string[] Arguments, string WorkingDirectory,
        Dictionary<string, string> Environment, bool ShowWindow, bool CaptureOutput, string ErrorFile);

    public static async Task<int> ProxyAsync()
    {
        // The parent attaches the Windows job before sending this request. A target
        // therefore cannot create children in the interval before job assignment.
        Console.InputEncoding = System.Text.Encoding.UTF8;
        var request = JsonSerializer.Deserialize<Command>(await Console.In.ReadLineAsync() ?? "")
            ?? throw new InvalidDataException("Prozessauftrag fehlt.");
        try
        {
            var start = new ProcessStartInfo(request.Executable)
            {
                WorkingDirectory = request.WorkingDirectory, UseShellExecute = false,
                CreateNoWindow = !request.ShowWindow,
                RedirectStandardOutput = request.CaptureOutput, RedirectStandardError = request.CaptureOutput
            };
            foreach (var argument in request.Arguments) start.ArgumentList.Add(argument);
            foreach (var item in request.Environment) start.Environment[item.Key] = item.Value;
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Prozessstart fehlgeschlagen.");
            var output = request.CaptureOutput ? process.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput()) : Task.CompletedTask;
            var error = request.CaptureOutput ? process.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError()) : Task.CompletedTask;
            await process.WaitForExitAsync();
            await Task.WhenAll(output, error);
            return process.ExitCode;
        }
        catch (Exception ex) { return ReportError(request.ErrorFile, ex); }
    }

    public static async Task<int> ManagedAsync(string[] args)
    {
        var file = Path.GetFullPath(args[1]);
        var typeName = args[2];
        var errorFile = args[3];
        try
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            Console.OutputEncoding = System.Text.Encoding.GetEncoding(args[4]);
            using var load = new ProgramLoadContext(file);
            var assembly = load.LoadFromAssemblyPath(file);
            var method = typeName.Length == 0 ? assembly.EntryPoint ?? throw new InvalidOperationException("DLL hat keinen Einstiegspunkt.")
                : SelectMain(assembly.GetType(typeName, throwOnError: true)!);
            var arguments = args.Skip(5).ToArray();
            var value = method.Invoke(null, method.GetParameters().Length == 0 ? null : [arguments]);
            if (value is Task<int> integerTask) return await integerTask;
            if (value is Task task) { await task; return 0; }
            return value is int code ? code : 0;
        }
        catch (Exception ex) { return ReportError(errorFile, ex); }
    }

    private static MethodInfo SelectMain(Type type)
    {
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == "Main" && !m.ContainsGenericParameters
            && (m.GetParameters().Length == 0 || m.GetParameters() is [{ ParameterType: var parameter }] && parameter == typeof(string[]))
            && (m.ReturnType == typeof(void) || m.ReturnType == typeof(int) || m.ReturnType == typeof(Task) || m.ReturnType == typeof(Task<int>))).ToList();
        return methods.Count == 1 ? methods[0] : throw new InvalidOperationException("Genau eine public static Main() oder Main(string[]) mit void, int, Task oder Task<int> erforderlich.");
    }

    private static int ReportError(string file, Exception error)
    {
        var cause = error is TargetInvocationException { InnerException: not null } invocation ? invocation.InnerException : error;
        File.WriteAllText(file, cause!.Message);
        return 253;
    }

    private sealed class ProgramLoadContext(string file) : AssemblyLoadContext(isCollectible: true), IDisposable
    {
        private readonly AssemblyDependencyResolver _resolver = new(file);
        protected override Assembly? Load(AssemblyName name)
            => _resolver.ResolveAssemblyToPath(name) is { } path ? LoadFromAssemblyPath(path) : null;
        protected override IntPtr LoadUnmanagedDll(string name)
            => _resolver.ResolveUnmanagedDllToPath(name) is { } path ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
        public void Dispose() => Unload();
    }
}
