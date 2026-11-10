/**
 * @file
 * @brief Erzeugt die native ABI und instrumentiert Nutzercode für Host-Aufrufe und Debugging.
 */

using System.Text;

namespace Testumgebung.Workers.Native;

internal sealed record CppVariable(string Name, string Type);

/// <summary>
/// Erzeugt den C++-Quelltext eines CodeBlocks. Stabile C-Schnittstelle (ABI-Version 1):
/// <code>
/// extern "C" int tu_abi_version(void);
/// extern "C" int tu_invoke(const tu_host* host);   // 0 = ok, 1 = Fehler, 2 = abgebrochen
/// </code>
/// <c>tu_host</c> ist eine Tabelle von C-Funktionszeigern (Werte holen/setzen, Hardware, Werker, Log, Abbruch).
/// </summary>
internal static class CppSource
{
    public const int AbiVersion = 3;

    /// <summary>Header mit Host-Tabelle und Nutzer-API (auch für LabVIEW-Glue verwendet).</summary>
    public const string Prelude = """
        #include <string>
        #include <cstdint>
        #include <cmath>
        #include <stdexcept>
        #include <thread>
        #include <chrono>
        #include <vector>
        #include <algorithm>
        #include <sstream>
        #include <cstdio>
        #include <locale>
        #include <type_traits>
        #include <utility>

        #if defined(_WIN32)
        #define TU_EXPORT extern "C" __declspec(dllexport)
        #else
        #define TU_EXPORT extern "C" __attribute__((visibility("default")))
        #endif

        extern "C" {
        typedef struct tu_host {
            void* ctx;
            double (*get_double)(void* ctx, const char* name);
            long long (*get_int)(void* ctx, const char* name);
            int (*get_bool)(void* ctx, const char* name);
            const char* (*get_string)(void* ctx, const char* name);
            void (*set_double)(void* ctx, const char* name, double value);
            void (*set_int)(void* ctx, const char* name, long long value);
            void (*set_bool)(void* ctx, const char* name, int value);
            void (*set_string)(void* ctx, const char* name, const char* value);
            void (*set_outcome)(void* ctx, int rating, int has_value, double value, const char* message, const char* unit,
                                int has_low, double low, int has_high, double high);
            int (*read)(void* ctx, const char* resource, double* value);
            int (*write)(void* ctx, const char* resource, double value);
            void (*log)(void* ctx, const char* message);
            int (*cancelled)(void* ctx);
            int (*ask_number)(void* ctx, const char* text, int has_min, double min, int has_max, double max, const char* unit, double* value);
            int (*confirm)(void* ctx, const char* text);
            int (*var_get)(void* ctx, const char* path, double* value);
            const char* (*var_get_string)(void* ctx, const char* path);
            int (*var_set_double)(void* ctx, const char* path, double value);
            int (*var_set_string)(void* ctx, const char* path, const char* value);
            const char* (*last_error)(void* ctx);
            const char* (*device)(void* ctx, const char* op, const char* args_json);
            int (*dbg_line)(void* ctx, int line, const char* vars_json, uintptr_t frame, uintptr_t pc, unsigned long thread);
            int dbg_active;
        } tu_host;
        }

        namespace tu {
            static const tu_host* H = nullptr;
            enum Rating { RatingNResult = 0, RatingPass = 1, RatingFail = 2, RatingError = 3 };
            struct Outcome {
                int rating = RatingNResult;
                bool hasValue = false; double value = 0;
                std::string message, unit;
                bool hasLow = false; double low = 0; bool hasHigh = false; double high = 0;
            };
            struct HostError : std::runtime_error { using std::runtime_error::runtime_error; };
            struct Cancelled {};
            inline void check(int rc) { if (rc != 0) throw HostError(H->last_error(H->ctx)); }
        }

        // ---- API für Nutzercode -------------------------------------------------------
        inline tu::Outcome NResult() { return tu::Outcome{}; }
        inline tu::Outcome NResult(double value) { tu::Outcome outcome; outcome.hasValue = true; outcome.value = value; return outcome; }
        inline tu::Outcome Pass() { tu::Outcome outcome; outcome.rating = tu::RatingPass; return outcome; }
        inline tu::Outcome Pass(double value) { tu::Outcome outcome = Pass(); outcome.hasValue = true; outcome.value = value; return outcome; }
        inline tu::Outcome Fail(const std::string& message = "") { tu::Outcome outcome; outcome.rating = tu::RatingFail; outcome.message = message; return outcome; }
        inline tu::Outcome Fail(const std::string& message, double value) { tu::Outcome outcome = Fail(message); outcome.hasValue = true; outcome.value = value; return outcome; }
        inline tu::Outcome Error(const std::string& message) { tu::Outcome outcome; outcome.rating = tu::RatingError; outcome.message = message; return outcome; }
        inline tu::Outcome Measured(double value, double low, double high, const std::string& unit = "")
        {
            tu::Outcome outcome; outcome.hasValue = true; outcome.value = value; outcome.unit = unit;
            outcome.hasLow = !std::isnan(low); outcome.low = low; outcome.hasHigh = !std::isnan(high); outcome.high = high;
            bool ok = (!outcome.hasLow || value >= low) && (!outcome.hasHigh || value <= high);
            outcome.rating = ok ? tu::RatingPass : tu::RatingFail;
            if (!ok) { std::ostringstream messageStream; messageStream << value << " außerhalb " << low << "–" << high; outcome.message = messageStream.str(); }
            return outcome;
        }
        inline bool IsCancelled() { return tu::H->cancelled(tu::H->ctx) != 0; }
        inline void ThrowIfCancelled() { if (IsCancelled()) throw tu::Cancelled{}; }
        inline double Read(const std::string& resource) { ThrowIfCancelled(); double value = 0; tu::check(tu::H->read(tu::H->ctx, resource.c_str(), &value)); return value; }
        inline void Write(const std::string& resource, double value) { ThrowIfCancelled(); tu::check(tu::H->write(tu::H->ctx, resource.c_str(), value)); }
        inline void Log(const std::string& message) { tu::H->log(tu::H->ctx, message.c_str()); }
        inline void Sleep(int milliseconds)
        {
            auto end = std::chrono::steady_clock::now() + std::chrono::milliseconds(milliseconds);
            while (std::chrono::steady_clock::now() < end) { ThrowIfCancelled(); std::this_thread::sleep_for(std::chrono::milliseconds(20)); }
            ThrowIfCancelled();
        }
        inline double AskNumber(const std::string& text, double min = NAN, double max = NAN, const std::string& unit = "")
        {
            double value = 0;
            tu::check(tu::H->ask_number(tu::H->ctx, text.c_str(), !std::isnan(min), min, !std::isnan(max), max, unit.c_str(), &value));
            return value;
        }
        inline void Confirm(const std::string& text) { tu::check(tu::H->confirm(tu::H->ctx, text.c_str())); }
        inline double Var(const std::string& path) { double value = 0; tu::check(tu::H->var_get(tu::H->ctx, path.c_str(), &value)); return value; }
        inline std::string VarText(const std::string& path)
        {
            const char* text = tu::H->var_get_string(tu::H->ctx, path.c_str());
            if (!text) throw tu::HostError(tu::H->last_error(tu::H->ctx));
            return text;
        }
        inline void SetVar(const std::string& path, double value) { tu::check(tu::H->var_set_double(tu::H->ctx, path.c_str(), value)); }
        inline void SetVar(const std::string& path, const std::string& value) { tu::check(tu::H->var_set_string(tu::H->ctx, path.c_str(), value.c_str())); }
        inline void SetVar(const std::string& path, const char* value) { SetVar(path, std::string(value)); }

        // ---- Kommunikation mit dem Prüfling (zentrale Vermittlung, Reservierungen gelten). Bytes als Hex-Text "0102FF". ----
        namespace tu {
            inline std::string json(const std::string& text)
            {
                std::string out = "\"";
                for (unsigned char character : text)
                {
                    if (character == '"' || character == '\\') { out += '\\'; out += (char)character; }
                    else if (character < 0x20) { char buf[8]; std::snprintf(buf, sizeof buf, "\\u%04x", character); out += buf; }
                    else out += (char)character;
                }
                return out + "\"";
            }
            inline std::string device(const std::string& op, const std::string& resource, const std::string& fields)
            {
                ThrowIfCancelled();
                std::string args = "{\"resource\":" + json(resource) + (fields.empty() ? "" : "," + fields) + "}";
                const char* response = H->device(H->ctx, op.c_str(), args.c_str());
                if (!response) throw HostError(H->last_error(H->ctx));
                return response;
            }
            inline std::string num(double value) { std::ostringstream numberStream; numberStream.imbue(std::locale::classic()); numberStream << value; return numberStream.str(); }
        }
        inline void CommSend(const std::string& resource, const std::string& text) { tu::device("comm.send", resource, "\"data\":" + tu::json(text)); }
        inline std::string CommReceive(const std::string& resource, int timeoutMs = 1000, const std::string& end = "\r\n")
        { return tu::device("comm.receive", resource, "\"timeout\":" + std::to_string(timeoutMs) + ",\"end\":" + tu::json(end)); }
        inline std::string CommQuery(const std::string& resource, const std::string& text, int timeoutMs = 1000, const std::string& end = "\r\n")
        { return tu::device("comm.query", resource, "\"data\":" + tu::json(text) + ",\"timeout\":" + std::to_string(timeoutMs) + ",\"end\":" + tu::json(end)); }
        inline double ModbusRead(const std::string& resource, const std::string& address, const std::string& type = "UInt16")
        {
            std::string response = tu::device("modbus.read", resource, "\"address\":" + tu::json(address) + ",\"type\":" + tu::json(type));
            if (response == "true") return 1; if (response == "false") return 0;
            std::istringstream responseStream(response); responseStream.imbue(std::locale::classic()); double registerValue = 0; responseStream >> registerValue; return registerValue;
        }
        inline void ModbusWrite(const std::string& resource, const std::string& address, double value, const std::string& type = "UInt16")
        { tu::device("modbus.write", resource, "\"address\":" + tu::json(address) + ",\"value\":" + tu::num(value) + ",\"type\":" + tu::json(type)); }
        inline void CanSend(const std::string& resource, unsigned id, const std::string& hex, bool extended = false)
        { tu::device("can.send", resource, "\"id\":" + std::to_string(id) + ",\"data\":" + tu::json(hex) + ",\"extended\":" + (extended ? "true" : "false")); }
        /// Datenbytes (Hex) des nächsten Rahmens mit dieser ID.
        inline std::string CanReceive(const std::string& resource, unsigned id, int timeoutMs = 1000)
        { return tu::device("can.receive", resource, "\"id\":" + std::to_string(id) + ",\"timeout\":" + std::to_string(timeoutMs)); }
        /// UDS über ISO-TP: positive Antwort als Hex; negative Antwort = tu::HostError mit NRC.
        inline std::string Uds(const std::string& resource, const std::string& requestHex, unsigned tx = 0x7E0, unsigned rx = 0x7E8, int timeoutMs = 1000)
        { return tu::device("uds", resource, "\"request\":" + tu::json(requestHex) + ",\"tx\":" + std::to_string(tx) + ",\"rx\":" + std::to_string(rx) + ",\"timeout\":" + std::to_string(timeoutMs)); }
        inline std::string J1939Receive(const std::string& resource, unsigned pgn, bool request = false, int timeoutMs = 1000)
        { return tu::device("j1939.receive", resource, "\"pgn\":" + std::to_string(pgn) + ",\"request\":" + (request ? "true" : "false") + ",\"timeout\":" + std::to_string(timeoutMs)); }
        inline void J1939Send(const std::string& resource, unsigned pgn, const std::string& hex, int source = 0xF9, int destination = 0xFF)
        { tu::device("j1939.send", resource, "\"pgn\":" + std::to_string(pgn) + ",\"data\":" + tu::json(hex) + ",\"source\":" + std::to_string(source) + ",\"destination\":" + std::to_string(destination)); }
        inline std::string LinRead(const std::string& resource, int id, int length = 8, int timeoutMs = 100)
        { return tu::device("lin.read", resource, "\"id\":" + std::to_string(id) + ",\"length\":" + std::to_string(length) + ",\"timeout\":" + std::to_string(timeoutMs)); }
        inline void LinWrite(const std::string& resource, int id, const std::string& hex)
        { tu::device("lin.write", resource, "\"id\":" + std::to_string(id) + ",\"data\":" + tu::json(hex)); }

        // ---- Zeilen-Debugger (ADR 0017): Instrumentierung ruft __tu_dbg_line vor jeder Anweisung, __tu_dbg_var nach Zuweisungen ----
        namespace tu {
            static bool dbgEnabled = false;
            static std::vector<std::pair<std::string, std::string>> dbgVars; // Name -> JSON-Wert (Zahl, true/false, "Text")
            inline void dbgSet(const std::string& name, const std::string& jsonValue)
            {
                for (auto& kv : dbgVars) if (kv.first == name) { kv.second = jsonValue; return; }
                dbgVars.emplace_back(name, jsonValue);
            }
            inline std::string dbgVarsJson()
            {
                std::string out = "{"; bool first = true;
                for (auto& kv : dbgVars) { if (!first) out += ","; first = false; out += json(kv.first) + ":" + kv.second; }
                return out + "}";
            }
        }
        inline void __tu_dbg_var(const char* name, bool value) { tu::dbgSet(name, value ? "true" : "false"); }
        inline void __tu_dbg_var(const char* name, long long value) { tu::dbgSet(name, std::to_string(value)); }
        inline void __tu_dbg_var(const char* name, int value) { tu::dbgSet(name, std::to_string(value)); }
        inline void __tu_dbg_var(const char* name, unsigned value) { tu::dbgSet(name, std::to_string(value)); }
        inline void __tu_dbg_var(const char* name, double value) { tu::dbgSet(name, tu::num(value)); }
        inline void __tu_dbg_var(const char* name, const std::string& value) { tu::dbgSet(name, tu::json(value)); }
        inline void __tu_dbg_var(const char* name, const char* value) { tu::dbgSet(name, tu::json(std::string(value))); }
        template <class T, class = void> struct __tu_streamable : std::false_type {};
        template <class T> struct __tu_streamable<T, std::void_t<decltype(std::declval<std::ostringstream&>() << std::declval<const T&>())>> : std::true_type {};
        template <class T> inline void __tu_dbg_var(const char* name, const T& value)
        {
            if constexpr (__tu_streamable<T>::value) {
                std::ostringstream valueStream; valueStream.imbue(std::locale::classic()); valueStream << value; tu::dbgSet(name, tu::json(valueStream.str()));
            } else tu::dbgSet(name, tu::json("<native Inspektion mit GDB>"));
        }
        #if defined(_WIN32)
        extern "C" __declspec(dllimport) unsigned long __stdcall GetCurrentThreadId(void);
        #endif
        inline void __tu_dbg_line(int line)
        {
            if (!tu::dbgEnabled || !tu::H || !tu::H->dbg_line) return;
            uintptr_t frame = 0, pc = 0;
            unsigned long thread = 0;
            #if defined(__GNUC__) && defined(__x86_64__)
            frame = *(uintptr_t*)__builtin_frame_address(0) + 16;
            pc = (uintptr_t)__builtin_return_address(0) - 1;
            #endif
            #if defined(_WIN32)
            thread = GetCurrentThreadId();
            #endif
            if (tu::H->dbg_line(tu::H->ctx, line, tu::dbgVarsJson().c_str(), frame, pc, thread) == 2) throw tu::Cancelled{};
        }

        TU_EXPORT int tu_abi_version(void) { return 3; }

        """;

    private static readonly HashSet<string> Keywords =
    [
        "alignas", "alignof", "and", "asm", "auto", "bool", "break", "case", "catch", "char", "class", "const", "constexpr",
        "continue", "default", "delete", "do", "double", "else", "enum", "explicit", "export", "extern", "false", "float", "for",
        "friend", "goto", "if", "inline", "int", "long", "mutable", "namespace", "new", "noexcept", "not", "nullptr", "operator",
        "or", "private", "protected", "public", "register", "return", "short", "signed", "sizeof", "static", "struct", "switch",
        "template", "this", "throw", "true", "try", "typedef", "typename", "union", "unsigned", "using", "virtual", "void",
        "volatile", "while", "xor", "Pass", "Fail", "Error", "NResult", "Measured", "Read", "Write", "Log", "Sleep", "Var",
        "VarText", "SetVar", "AskNumber", "Confirm", "IsCancelled", "ThrowIfCancelled", "CommSend", "CommReceive", "CommQuery",
        "ModbusRead", "ModbusWrite", "CanSend", "CanReceive", "Uds", "J1939Receive", "J1939Send", "LinRead", "LinWrite",
    ];

    public static bool IsIdentifier(string name)
        => name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] == '_') && name.All(name2 => char.IsAsciiLetterOrDigit(name2) || name2 == '_')
           && !Keywords.Contains(name) && !name.StartsWith("__", StringComparison.Ordinal) && !name.StartsWith("tu_", StringComparison.Ordinal);

    private static string CppType(string type) => type switch
    {
        "Bool" => "bool",
        "Int" => "long long",
        "Double" => "double",
        _ => "std::string",
    };

    private static string Literal(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>Vollständige Übersetzungseinheit für einen CodeBlock. <paramref name="instrument"/> baut den Zeilen-Debugger ein.</summary>
    public static string Generate(string userCode, IEnumerable<CppVariable> variables, bool instrument = false)
    {
        if (instrument) userCode = CppInstrument.Instrument(userCode);
        var vars = variables.Where(cppVariable => IsIdentifier(cppVariable.Name)).ToList();
        var sb = new StringBuilder(Prelude);
        sb.AppendLine("TU_EXPORT int tu_invoke(const tu_host* host)");
        sb.AppendLine("{");
        sb.AppendLine("    tu::H = host;");
        sb.AppendLine("    tu::dbgEnabled = host->dbg_active != 0;");
        sb.AppendLine("    tu::dbgVars.clear();");
        foreach (var cppVariable in vars)
        {
            var name = Literal(cppVariable.Name);
            sb.AppendLine(cppVariable.Type switch
            {
                "Bool" => $"    bool {cppVariable.Name} = host->get_bool(host->ctx, {name}) != 0;",
                "Int" => $"    long long {cppVariable.Name} = host->get_int(host->ctx, {name});",
                "Double" => $"    double {cppVariable.Name} = host->get_double(host->ctx, {name});",
                _ => $"    std::string {cppVariable.Name} = host->get_string(host->ctx, {name});",
            });
        }
        sb.AppendLine("    try");
        sb.AppendLine("    {");
        sb.AppendLine("        tu::Outcome __o = [&]() -> tu::Outcome {");
        sb.AppendLine("#line 1 \"CodeBlock\"");
        sb.AppendLine(userCode);
        sb.AppendLine("#line 1 \"Wrapper\"");
        sb.AppendLine("            return NResult();");
        sb.AppendLine("        }();");
        foreach (var cppVariable2 in vars)
        {
            var name = Literal(cppVariable2.Name);
            sb.AppendLine(cppVariable2.Type switch
            {
                "Bool" => $"        host->set_bool(host->ctx, {name}, {cppVariable2.Name} ? 1 : 0);",
                "Int" => $"        host->set_int(host->ctx, {name}, {cppVariable2.Name});",
                "Double" => $"        host->set_double(host->ctx, {name}, {cppVariable2.Name});",
                _ => $"        host->set_string(host->ctx, {name}, {cppVariable2.Name}.c_str());",
            });
        }
        sb.AppendLine("        host->set_outcome(host->ctx, __o.rating, __o.hasValue ? 1 : 0, __o.value, __o.message.c_str(), __o.unit.c_str(),");
        sb.AppendLine("                          __o.hasLow ? 1 : 0, __o.low, __o.hasHigh ? 1 : 0, __o.high);");
        sb.AppendLine("        return 0;");
        sb.AppendLine("    }");
        sb.AppendLine("    catch (const tu::Cancelled&) { return 2; }");
        sb.AppendLine("    catch (const std::exception& error) { std::string message = std::string(\"C++-Ausnahme: \") + error.what(); host->set_outcome(host->ctx, 3, 0, 0, message.c_str(), \"\", 0, 0, 0, 0); return 1; }");
        sb.AppendLine("    catch (...) { host->set_outcome(host->ctx, 3, 0, 0, \"Unbekannte C++-Ausnahme\", \"\", 0, 0, 0, 0); return 1; }");
        sb.AppendLine("}");
        return sb.ToString();
    }
}

/// <summary>
/// Lexikalische Instrumentierung des C++-Nutzercodes für den Zeilen-Debugger (ADR 0017): vor jede Anweisung an einer
/// Zeilengrenze ein <c>__tu_dbg_line(zeile)</c>, nach einer einzeiligen einfachen Zuweisung/Deklaration ein
/// <c>__tu_dbg_var("name", name)</c>. Einschübe bleiben auf derselben physischen Zeile, damit die <c>#line</c>-Zuordnung
/// (Diagnosen, Zeilennummern) unverändert bleibt. Heuristisch: Strings, Zeichen und Kommentare werden übersprungen;
/// bei exotischer Syntax (mehrzeilige Anweisungen, Makros) wird im Zweifel kein Einschub gesetzt.
/// </summary>
internal static class CppInstrument
{
    private static readonly string[] Continuations = ["else", "catch", "case", "default", "public", "private", "protected", "while"];

    public static string Instrument(string userCode)
    {
        var lines = userCode.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var sb = new StringBuilder();
        var inBlockComment = false;
        var inPreprocessor = false;
        var lastSig = '\0';
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var startInComment = inBlockComment;
            var trimmed = line.TrimStart();
            var indent = line[..(line.Length - trimmed.Length)];
            var boundary = IsStatementBoundary(startInComment, lastSig);
            var isPreproc = inPreprocessor || !startInComment && trimmed.StartsWith('#');
            var emitted = line;
            if (boundary && !isPreproc && !IsContinuation(trimmed) && HasCode(line, startInComment))
            {
                emitted = indent + "__tu_dbg_line(" + (index + 1) + "); " + trimmed;
                if (AssignedName(line, startInComment) is { } name) emitted += " __tu_dbg_var(\"" + name + "\", " + name + ");";
            }
            sb.Append(emitted);
            if (index < lines.Length - 1) sb.Append('\n');
            if (isPreproc) inPreprocessor = line.TrimEnd().EndsWith('\\');
            else Scan(line, ref inBlockComment, ref lastSig);
        }
        return sb.ToString();
    }

    private static bool IsStatementBoundary(bool inComment, char lastSig)
        => !inComment && lastSig is '\0' or ';' or '{' or '}';

    private static bool IsContinuation(string trimmed)
    {
        if (trimmed.StartsWith('}')) return true;
        foreach (var kw in Continuations)
            if (trimmed.StartsWith(kw, StringComparison.Ordinal)
                && (trimmed.Length == kw.Length || !char.IsLetterOrDigit(trimmed[kw.Length]) && trimmed[kw.Length] != '_'))
                return true;
        return false;
    }

    /// <summary>Hat die Zeile außerhalb von Kommentaren echten Code?</summary>
    private static bool HasCode(string line, bool inBlockComment)
    {
        var block = inBlockComment;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (block)
            {
                if (character == '*' && FollowedBy(line, index, '/')) { block = false; index++; }
                continue;
            }
            if (character == '/' && FollowedBy(line, index, '/')) return false;
            if (character == '/' && FollowedBy(line, index, '*')) { block = true; index++; continue; }
            if (!char.IsWhiteSpace(character)) return true;
        }
        return false;
    }

    /// <summary>Zustand über die Zeile fortschreiben: Blockkommentar offen? und letztes Code-Zeichen (für die Anweisungsgrenze).</summary>
    private static void Scan(string line, ref bool inBlockComment, ref char lastSig)
    {
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (inBlockComment)
            {
                if (character == '*' && FollowedBy(line, index, '/')) { inBlockComment = false; index++; }
                continue;
            }
            if (character == '/' && FollowedBy(line, index, '/')) return;
            if (character == '/' && FollowedBy(line, index, '*')) { inBlockComment = true; index++; continue; }
            if (character is '"' or '\'')
            {
                var quote = character;
                SkipQuotedText(line, ref index, quote);
                lastSig = quote; // Zeichenkette zählt als Code (kein Anweisungsende)
                continue;
            }
            if (!char.IsWhiteSpace(character)) lastSig = character;
        }
    }

    private static bool FollowedBy(ReadOnlySpan<char> text, int index, char next)
        => index + 1 < text.Length && text[index + 1] == next;

    private static void SkipQuotedText(string line, ref int index, char quote)
    {
        index++;
        while (index < line.Length)
        {
            if (line[index] == '\\') { index += 2; continue; }
            if (line[index] == quote) break;
            index++;
        }
    }

    /// <summary>Name einer einzeiligen einfachen Zuweisung/Deklaration „… name = … ;“ auf dieser Zeile, sonst null.</summary>
    private static string? AssignedName(string line, bool inBlockComment)
    {
        // Nur Code-Zeichen betrachten (Kommentare/Strings entfernen), Positionen auf die Originalzeile bezogen.
        var code = Decomment(line, inBlockComment);
        var depth = 0;
        for (var index = 0; index < code.Length; index++)
        {
            var character = code[index];
            if (character is '(' or '[') depth++;
            else if (character is ')' or ']') depth--;
            else if (character == ';' && depth == 0) return null; // Anweisung endete ohne top-level '='
            else if (character == '=' && depth == 0)
            {
                if (!IsSimpleAssignment(code, index)) continue;
                if (!HasAssignmentTerminator(code, index)) return null;
                return AssignedIdentifier(code, index);
            }
        }
        return null;
    }

    private static bool IsSimpleAssignment(string code, int index)
    {
        var previous = index > 0 ? code[index - 1] : '\0';
        var next = index + 1 < code.Length ? code[index + 1] : '\0';
        return next != '=' && "=<>!+-*/%&|^".IndexOf(previous) < 0;
    }

    private static bool HasAssignmentTerminator(string code, int index)
    {
        // ';' nach dem '=' auf Top-Ebene?
        var hasSemicolon = false;
        var d2 = 0;
        for (var innerIndex = index + 1; innerIndex < code.Length; innerIndex++)
        {
            if (code[innerIndex] is '(' or '[') d2++;
            else if (code[innerIndex] is ')' or ']') d2--;
            else if (code[innerIndex] == ';' && d2 == 0) { hasSemicolon = true; break; }
        }
        return hasSemicolon;
    }

    private static string? AssignedIdentifier(string code, int index)
    {
        // LHS-Bezeichner links von '='
        var previousIndex = index - 1;
        while (previousIndex >= 0 && char.IsWhiteSpace(code[previousIndex])) previousIndex--;
        var end = previousIndex;
        while (previousIndex >= 0 && (char.IsLetterOrDigit(code[previousIndex]) || code[previousIndex] == '_')) previousIndex--;
        if (previousIndex == end) return null; // kein Bezeichner
        var before = previousIndex >= 0 ? code[previousIndex] : '\0';
        if (before is '.' or '>' or ':') return null; // Member/Qualifizierung → nicht erfassen
        var name = code[(previousIndex + 1)..(end + 1)];
        return CppSource.IsIdentifier(name) ? name : null;
    }

    /// <summary>Zeile ohne Kommentare und mit durch Leerzeichen ersetzten String-/Zeichen-Inhalten (Länge bleibt erhalten).</summary>
    private static string Decomment(string line, bool inBlockComment)
    {
        var chars = line.ToCharArray();
        var block = inBlockComment;
        for (var index = 0; index < chars.Length; index++)
        {
            var character = chars[index];
            if (block)
            {
                var star = character == '*' && FollowedBy(chars, index, '/');
                chars[index] = ' ';
                if (star) { chars[index + 1] = ' '; index++; block = false; }
                continue;
            }
            if (character == '/' && FollowedBy(chars, index, '/')) { for (var innerIndex = index; innerIndex < chars.Length; innerIndex++) chars[innerIndex] = ' '; break; }
            if (character == '/' && FollowedBy(chars, index, '*')) { chars[index] = chars[index + 1] = ' '; index++; block = true; continue; }
            if (character is '"' or '\'')
            {
                BlankQuotedText(chars, ref index, character);
            }
        }
        return new string(chars);
    }
    private static void BlankQuotedText(char[] chars, ref int index, char quote)
    {
        chars[index] = ' ';
        index++;
        while (index < chars.Length)
        {
            if (chars[index] == '\\') { if (index + 1 < chars.Length) chars[index + 1] = ' '; chars[index] = ' '; index += 2; continue; }
            if (chars[index] == quote) { chars[index] = ' '; break; }
            chars[index] = ' '; index++;
        }
    }

}
