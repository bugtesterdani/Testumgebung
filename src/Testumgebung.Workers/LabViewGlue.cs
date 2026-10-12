using System.Text;
using Testumgebung.Contracts;
using Testumgebung.Domain.Code;

namespace Testumgebung.Workers;

/// <summary>
/// Einstellungen eines LabVIEW-Bausteins (Quelltextfeld, Zeilen „schlüssel = wert“):
/// <code>
/// vi         = C:\Pruefung\Spannungsbewertung.vi      (Bearbeitung extern in LabVIEW)
/// dll        = C:\Pruefung\build\Spannung.dll         (mit dem Application Builder gebaut)
/// funktion   = Spannungsbewertung                     (exportierter Funktionsname)
/// aufruf     = C                                      (C oder stdcall)
/// </code>
/// </summary>
public sealed record LabViewSettings(string? ViPath, string? DllPath, string? Function, string CallingConvention)
{
    public static string Template =>
        "# LabVIEW-VI als DLL (Application Builder, Prototyp „C“-Aufruf)\n" +
        "vi       = C:\\Pruefung\\Spannungsbewertung.vi\n" +
        "dll      = C:\\Pruefung\\build\\Spannungsbewertung.dll\n" +
        "funktion = Spannungsbewertung\n" +
        "aufruf   = C\n";

    public static LabViewSettings Parse(string source)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in source.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            map[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return new LabViewSettings(
            map.GetValueOrDefault("vi"),
            map.GetValueOrDefault("dll"),
            map.GetValueOrDefault("funktion") ?? map.GetValueOrDefault("function"),
            (map.GetValueOrDefault("aufruf") ?? "C").Equals("stdcall", StringComparison.OrdinalIgnoreCase) ? "stdcall" : "C");
    }

    /// <summary>Prüfung der Einstellungen und des Connectors (Diagnosen für „Build prüfen“).</summary>
    public IReadOnlyList<CodeDiagnostic> Check(IReadOnlyList<CodePort> ports)
    {
        var list = new List<CodeDiagnostic>();
        if (string.IsNullOrWhiteSpace(DllPath))
            list.Add(new CodeDiagnostic(0, 0, "error", "Keine gebaute DLL angegeben (dll = …)."));
        else if (!File.Exists(DllPath))
            list.Add(new CodeDiagnostic(0, 0, "warning", $"DLL '{DllPath}' nicht gefunden – VI mit dem Application Builder bauen; zur Ausführung ist die LabVIEW-Runtime nötig."));
        if (string.IsNullOrWhiteSpace(Function))
            list.Add(new CodeDiagnostic(0, 0, "error", "Kein exportierter Funktionsname angegeben (funktion = …)."));
        if (!string.IsNullOrWhiteSpace(ViPath) && !File.Exists(ViPath))
            list.Add(new CodeDiagnostic(0, 0, "warning", $"VI '{ViPath}' nicht gefunden (nur für die Bearbeitung in LabVIEW nötig)."));
        foreach (var p in ports.Where(p => p.Type == VarType.DateTime))
            list.Add(new CodeDiagnostic(0, 0, "error", $"Port '{p.Name}': DateTime wird am LabVIEW-Connector nicht unterstützt."));
        if (ports.Any(p => p.Name == "Bewertung" && (p.Type != VarType.Int || p.Direction == PortDirection.Input)))
            list.Add(new CodeDiagnostic(0, 0, "error", "Port 'Bewertung' muss ein Int-Ausgang sein (0 = NRESULT, 1 = PASS, 2 = FAIL, 3 = ERROR)."));
        return list;
    }

    private static string Literal(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>
    /// C++-Glue mit stabiler tu_invoke-Schnittstelle. Connector-Konvention (Reihenfolge = Portliste):
    /// Eingang Double → double, Int → int32_t, Bool → uint8_t (LVBoolean), String → const char*;
    /// Ausgang bzw. Ein-/Ausgang als Zeiger, String-Ausgang als (char* puffer, int32_t länge).
    /// Optionaler Int-Ausgang „Bewertung“ setzt die Bewertung, ein nicht leerer String-Ausgang „Fehler“ ergibt ERROR.
    /// </summary>
    public string GenerateGlue(IReadOnlyList<CodePort> ports)
    {
        static string InType(VarType t) => t switch
        {
            VarType.Double => "double",
            VarType.Int => "int32_t",
            VarType.Bool => "uint8_t",
            _ => "const char*",
        };
        var sb = new StringBuilder("//#tu-raw\n");
        sb.AppendLine("#if defined(_WIN32)");
        sb.AppendLine("#include <windows.h>");
        sb.AppendLine(CallingConvention == "stdcall" ? "#define TU_CC __stdcall" : "#define TU_CC __cdecl");
        sb.AppendLine("static void* tu_load(const char* p) { return (void*)LoadLibraryA(p); }");
        sb.AppendLine("static void* tu_sym(void* h, const char* n) { return (void*)GetProcAddress((HMODULE)h, n); }");
        sb.AppendLine("#else");
        sb.AppendLine("#include <dlfcn.h>");
        sb.AppendLine("#define TU_CC");
        sb.AppendLine("static void* tu_load(const char* p) { return dlopen(p, RTLD_NOW); }");
        sb.AppendLine("static void* tu_sym(void* h, const char* n) { return dlsym(h, n); }");
        sb.AppendLine("#endif");

        var parameters = new List<string>();
        foreach (var p in ports)
        {
            if (p.Direction == PortDirection.Input) parameters.Add(InType(p.Type));
            else if (p.Type == VarType.String)
            {
                parameters.Add("char*");
                parameters.Add("int32_t");
            }
            else parameters.Add(InType(p.Type) + "*");
        }
        sb.AppendLine($"typedef void (TU_CC *tu_vi_fn)({string.Join(", ", parameters.DefaultIfEmpty("void"))});");
        sb.AppendLine("TU_EXPORT int tu_invoke(const tu_host* host)");
        sb.AppendLine("{");
        sb.AppendLine("    tu::H = host;");
        sb.AppendLine("    try {");
        sb.AppendLine($"        static void* lib = tu_load({Literal(DllPath ?? "")});");
        sb.AppendLine($"        if (!lib) return host->set_outcome(host->ctx, 3, 0, 0, \"LabVIEW-DLL nicht ladbar: {(DllPath ?? "").Replace("\\", "\\\\").Replace("\"", "'")} – gebaute DLL und LabVIEW-Runtime prüfen.\", \"\", 0, 0, 0, 0), 1;");
        sb.AppendLine($"        tu_vi_fn fn = (tu_vi_fn)tu_sym(lib, {Literal(Function ?? "")});");
        sb.AppendLine($"        if (!fn) return host->set_outcome(host->ctx, 3, 0, 0, \"Funktion '{(Function ?? "").Replace("\"", "'")}' nicht in der DLL exportiert.\", \"\", 0, 0, 0, 0), 1;");

        var args = new List<string>();
        var i = 0;
        WriteArguments();

        void WriteArguments()
        {
            foreach (var p in ports)
            {
                var v = $"p{i++}";
                var name = Literal(p.Name);
                switch (p.Type)
                {
                    case VarType.String when p.Direction == PortDirection.Input:
                        sb.AppendLine($"        std::string {v}s = host->get_string(host->ctx, {name});");
                        args.Add($"{v}s.c_str()");
                        break;
                    case VarType.String:
                        sb.AppendLine($"        std::vector<char> {v}(4096, 0);");
                        if (p.Direction == PortDirection.InOut)
                            sb.AppendLine($"        {{ std::string s = host->get_string(host->ctx, {name}); std::copy_n(s.begin(), std::min<size_t>(s.size(), 4095), {v}.begin()); }}");
                        args.Add($"{v}.data()");
                        args.Add("(int32_t)4096");
                        break;
                    case VarType.Double:
                        sb.AppendLine($"        double {v} = {(p.IsInput ? $"host->get_double(host->ctx, {name})" : "0")};");
                        args.Add(p.Direction == PortDirection.Input ? v : "&" + v);
                        break;
                    case VarType.Int:
                        sb.AppendLine($"        int32_t {v} = {(p.IsInput ? $"(int32_t)host->get_int(host->ctx, {name})" : "0")};");
                        args.Add(p.Direction == PortDirection.Input ? v : "&" + v);
                        break;
                    case VarType.Bool:
                        sb.AppendLine($"        uint8_t {v} = {(p.IsInput ? $"(uint8_t)(host->get_bool(host->ctx, {name}) ? 1 : 0)" : "0")};");
                        args.Add(p.Direction == PortDirection.Input ? v : "&" + v);
                        break;
                    default:
                        sb.AppendLine($"        throw std::runtime_error(\"Typ von {p.Name.Replace("\"", "'")} wird nicht unterstützt\");");
                        break;
                }
            }
        }

        sb.AppendLine($"        fn({string.Join(", ", args)});");

        sb.AppendLine("        int rating = 0; std::string error;");
        i = 0;
        foreach (var p in ports)
        {
            var v = $"p{i++}";
            if (!p.IsOutput) continue;
            var name = Literal(p.Name);
            sb.AppendLine(p.Type switch
            {
                VarType.String => $"        {v}.back() = 0; host->set_string(host->ctx, {name}, {v}.data());",
                VarType.Double => $"        host->set_double(host->ctx, {name}, {v});",
                VarType.Int => $"        host->set_int(host->ctx, {name}, (long long){v});",
                _ => $"        host->set_bool(host->ctx, {name}, {v} != 0);",
            });
            if (p.Name == "Bewertung" && p.Type == VarType.Int) sb.AppendLine($"        rating = ({v} >= 0 && {v} <= 3) ? (int){v} : 3;");
            if (p.Name == "Fehler" && p.Type == VarType.String) sb.AppendLine($"        error = {v}.data();");
        }
        sb.AppendLine("        if (!error.empty()) rating = 3;");
        sb.AppendLine("        host->set_outcome(host->ctx, rating, 0, 0, error.c_str(), \"\", 0, 0, 0, 0);");
        sb.AppendLine("        return 0;");
        sb.AppendLine("    }");
        sb.AppendLine("    catch (const std::exception& e) { host->set_outcome(host->ctx, 3, 0, 0, e.what(), \"\", 0, 0, 0, 0); return 1; }");
        sb.AppendLine("}");
        return sb.ToString();
    }
}
