/**
 * @file
 * @brief Prüft und übernimmt .NET-Bibliotheken als projektbezogene Abhängigkeiten der Codebausteine.
 */

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Testumgebung.Domain.Code;

namespace Testumgebung.Persistence;

public static class ManagedAssemblyImport
{
    public static ManagedAssembly Read(string path)
    {
        var assembly = new ManagedAssembly(Path.GetFileName(path), File.ReadAllBytes(path));
        Validate(assembly);
        return assembly;
    }

    public static void Validate(ManagedAssembly assembly)
    {
        if (string.IsNullOrWhiteSpace(assembly.Name) || Path.GetFileName(assembly.Name) != assembly.Name
            || assembly.Name.IndexOfAny(['/', '\\', ':']) >= 0 || !assembly.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || assembly.Data is null || assembly.Data.Length == 0)
            throw new ArgumentException("Ungültige .NET-DLL: " + assembly.Name);
        try
        {
            using var pe = new PEReader(new MemoryStream(assembly.Data, writable: false));
            if (!pe.HasMetadata || !pe.GetMetadataReader().IsAssembly)
                throw new BadImageFormatException("Keine verwaltete .NET-Assembly.");
            var metadata = pe.GetMetadataReader();
            var name = metadata.GetString(metadata.GetAssemblyDefinition().Name);
            if (!string.Equals(name + ".dll", assembly.Name, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"DLL-Dateiname muss dem Assemblynamen entsprechen: {name}.dll");
        }
        catch (BadImageFormatException ex) { throw new ArgumentException("DLL ist keine gültige .NET-Assembly: " + assembly.Name, ex); }
    }
}
