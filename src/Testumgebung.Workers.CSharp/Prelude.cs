namespace Testumgebung.Workers.CSharp;

/// <summary>
/// Quelltext, der zusammen mit jedem CodeBlock übersetzt wird: Basisklasse mit der API für Nutzercode.
/// </summary>
internal static class Prelude
{
    public const string Source = """
        using System;
        using System.Threading;
        using System.Globalization;
        using System.Collections.Generic;
        using System.Reflection;
        using System.Text.Json.Nodes;

        namespace Testumgebung.Code
        {
            /// <summary>Abbruch durch den Debugger (Stopp-Befehl am Haltepunkt).</summary>
            public sealed class DebugStopException : Exception { }

            /// <summary>NRESULT &lt; PASS &lt; FAIL &lt; ERROR</summary>
            public enum ResultRating { NResult, Pass, Fail, Error }

            /// <summary>Ergebnis eines CodeBlocks: Bewertung, optional Messwert mit Einheit und Grenzen.</summary>
            public sealed record Outcome(ResultRating Rating, object Value = null, string Message = null, string Unit = null, double? Low = null, double? High = null);

            /// <summary>Empfangener CAN-Rahmen bzw. J1939-Parametergruppe.</summary>
            public sealed record CanMessage(uint Id, byte[] Data, bool Extended, uint Pgn = 0, byte Source = 0);

            /// <summary>API für Nutzercode. Variablen des Ordners bzw. Ports sind als Felder verfügbar.</summary>
            public abstract class BlockBase
            {
                private Func<string, string, JsonObject, JsonNode> _call;
                private string _id;

                /// <summary>Kooperativer Abbruch (Werkerabbruch). Lange Schleifen sollten ihn prüfen.</summary>
                public CancellationToken CancellationToken { get; private set; }

                public void __Setup(Func<string, string, JsonObject, JsonNode> call, string id, CancellationToken token)
                {
                    _call = call;
                    _id = id;
                    CancellationToken = token;
                }

                public abstract Outcome Run();

                // ---- Zeilen-Debugger (ADR 0017): Haltepunkte, Einzelschritt, Variablen-Schnappschuss ----

                private bool _dbgEnabled;
                private bool _dbgStep;
                private bool _dbgEntry;
                private readonly HashSet<int> _dbgBreakpoints = new HashSet<int>();
                private readonly Dictionary<string, object> _dbgLocals = new Dictionary<string, object>();

                /// <summary>Vom Worker beim Aufruf gesetzt: Startzustand des Zeilen-Debuggers.</summary>
                public void __DbgSetup(bool enabled, int[] breakpoints, bool entry, bool step)
                {
                    _dbgEnabled = enabled;
                    _dbgEntry = entry;
                    _dbgStep = step;
                    _dbgBreakpoints.Clear();
                    if (breakpoints != null) foreach (var b in breakpoints) _dbgBreakpoints.Add(b);
                }

                /// <summary>Bei Eintritt anhalten (vom Worker vor Run aufgerufen, wenn konfiguriert).</summary>
                public void __DbgEntry()
                {
                    if (_dbgEnabled && _dbgEntry) __DbgEmit(1, "entry");
                }

                /// <summary>Lokale Variable des Nutzercodes am Haltepunkt erfassen (vom instrumentierten Code aufgerufen).</summary>
                protected void __DbgVar(string name, object value) => _dbgLocals[name] = value;

                /// <summary>Haltepunkt-Prüfung je Nutzercode-Zeile (vom instrumentierten Code aufgerufen).</summary>
                protected void __DbgLine(int line)
                {
                    if (!_dbgEnabled) return;
                    if (!_dbgStep && !_dbgBreakpoints.Contains(line)) return;
                    __DbgEmit(line, "line");
                }

                private void __DbgEmit(int line, string kind)
                {
                    var vars = new JsonObject();
                    foreach (var f in GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
                        vars[f.Name] = ToNode(f.GetValue(this));
                    foreach (var kv in _dbgLocals) vars[kv.Key] = ToNode(kv.Value);
                    var reply = _call(_id, "debug.at", new JsonObject { ["line"] = line, ["kind"] = kind, ["vars"] = vars });
                    if (reply is JsonObject obj)
                    {
                        _dbgBreakpoints.Clear();
                        if (obj["breakpoints"] is JsonArray arr)
                            foreach (var b in arr) if (b != null) _dbgBreakpoints.Add(b.GetValue<int>());
                        _dbgStep = obj["step"] != null && obj["step"].GetValue<bool>();
                        if ((string)obj["cmd"] == "stop") throw new DebugStopException();
                    }
                }

                protected bool IsCancelled => CancellationToken.IsCancellationRequested;
                protected void ThrowIfCancelled() => CancellationToken.ThrowIfCancellationRequested();

                protected Outcome Pass(object value = null) => new Outcome(ResultRating.Pass, value);
                protected Outcome Fail(string message = null, object value = null) => new Outcome(ResultRating.Fail, value, message);
                protected Outcome Error(string message) => new Outcome(ResultRating.Error, null, message);
                protected Outcome NResult(object value = null) => new Outcome(ResultRating.NResult, value);

                /// <summary>Messwert mit Grenzwerten: PASS innerhalb, sonst FAIL.</summary>
                protected Outcome Measured(double value, double? low, double? high, string unit = null)
                {
                    var ok = (low == null || value >= low) && (high == null || value <= high);
                    return new Outcome(ok ? ResultRating.Pass : ResultRating.Fail, value, ok ? null : $"{value.ToString(CultureInfo.InvariantCulture)} außerhalb {low}–{high}", unit, low, high);
                }

                /// <summary>Messwert über die zentrale Hardwarevermittlung lesen (Ressourcenlocks gelten).</summary>
                protected double Read(string resource)
                {
                    ThrowIfCancelled();
                    return _call(_id, "read", new JsonObject { ["resource"] = resource }).GetValue<double>();
                }

                protected void Write(string resource, double value)
                {
                    ThrowIfCancelled();
                    _call(_id, "write", new JsonObject { ["resource"] = resource, ["value"] = value });
                }

                protected void Log(string message) => _call(_id, "log", new JsonObject { ["message"] = message });

                /// <summary>Variable per Pfad lesen, z. B. Var&lt;double&gt;("../Spannung").</summary>
                protected T Var<T>(string path)
                {
                    var node = _call(_id, "get", new JsonObject { ["path"] = path });
                    return node is null ? default : node.GetValue<T>();
                }

                /// <summary>Variable per Pfad schreiben (Typprüfung im Host).</summary>
                protected void SetVar(string path, object value)
                    => _call(_id, "set", new JsonObject { ["path"] = path, ["value"] = ToNode(value) });

                /// <summary>Werkereingabe (Text).</summary>
                protected string Ask(string text)
                    => _call(_id, "ask", new JsonObject { ["kind"] = "Input", ["text"] = text, ["type"] = "String", ["required"] = true })?.GetValue<string>();

                /// <summary>Werkereingabe (Zahl) mit optionalem Bereich.</summary>
                protected double AskNumber(string text, double? min = null, double? max = null, string unit = null)
                    => _call(_id, "ask", new JsonObject { ["kind"] = "Input", ["text"] = text, ["type"] = "Double", ["required"] = true, ["min"] = min, ["max"] = max, ["unit"] = unit }).GetValue<double>();

                /// <summary>Anweisung mit Bestätigung durch den Werker.</summary>
                protected void Confirm(string text)
                    => _call(_id, "ask", new JsonObject { ["kind"] = "Instruction", ["text"] = text });

                /// <summary>Eigener Auswahlablauf: Testprogramm festlegen (Testkennung, optional Version/Auftrag/Prüfparameter „Name=Wert;…“).</summary>
                protected void SelectTest(string test, string version = null, string order = null, string parameters = null, string pythonVenv = null)
                    => _call(_id, "select.test", new JsonObject { ["test"] = test, ["version"] = version, ["order"] = order, ["parameters"] = parameters, ["pythonVenv"] = pythonVenv });

                // ---- Kommunikation mit dem Prüfling (über die zentrale Vermittlung; Reservierungen gelten) ----

                private JsonNode Dev(string op, string resource, JsonObject args)
                {
                    ThrowIfCancelled();
                    args["resource"] = resource;
                    return _call(_id, op, args);
                }

                private static string HexOf(byte[] data) => data == null ? "" : Convert.ToHexString(data);

                protected void ConfigureConnection(string resource, string address, string options = "")
                    => Dev("comm.configure", resource, new JsonObject { ["address"] = address, ["options"] = options });
                protected JsonNode HttpRequest(string resource, string path, string method = "GET", string body = "", string headers = "", int timeoutMs = 30000)
                    => Dev("http.request", resource, new JsonObject { ["path"] = path, ["method"] = method, ["body"] = body, ["headers"] = headers, ["timeout"] = timeoutMs });
                protected JsonNode SshExecute(string resource, string command, int timeoutMs = 30000)
                    => Dev("ssh.exec", resource, new JsonObject { ["command"] = command, ["timeout"] = timeoutMs });
                protected JsonNode SftpUpload(string resource, string local, string remote, bool overwrite = false, int timeoutMs = 30000)
                    => Dev("sftp.upload", resource, new JsonObject { ["local"] = local, ["remote"] = remote, ["overwrite"] = overwrite, ["timeout"] = timeoutMs });
                protected JsonNode SftpDownload(string resource, string remote, string local, bool overwrite = false, int timeoutMs = 30000)
                    => Dev("sftp.download", resource, new JsonObject { ["local"] = local, ["remote"] = remote, ["overwrite"] = overwrite, ["timeout"] = timeoutMs });

                /// <summary>Text (mit \r \n \xHH) bzw. Bytes senden.</summary>
                protected void CommSend(string resource, string text) => Dev("comm.send", resource, new JsonObject { ["data"] = text });
                protected void CommSend(string resource, byte[] data) => Dev("comm.send", resource, new JsonObject { ["data"] = HexOf(data), ["hex"] = true });

                /// <summary>Antwort bis Zeilenende (Standard \r\n), bei length feste Länge.</summary>
                protected string CommReceive(string resource, int timeoutMs = 1000, string end = "\r\n", int? length = null)
                    => Dev("comm.receive", resource, new JsonObject { ["timeout"] = timeoutMs, ["end"] = end, ["length"] = length })?.GetValue<string>();

                /// <summary>Befehl senden (Zeilenende wird angehängt) und Antwort lesen.</summary>
                protected string CommQuery(string resource, string text, int timeoutMs = 1000, string end = "\r\n")
                    => Dev("comm.query", resource, new JsonObject { ["data"] = text, ["timeout"] = timeoutMs, ["end"] = end })?.GetValue<string>();

                protected byte[] CommQuery(string resource, byte[] data, int length, int timeoutMs = 1000)
                    => Convert.FromHexString(Dev("comm.query", resource, new JsonObject { ["data"] = HexOf(data), ["hex"] = true, ["length"] = length, ["timeout"] = timeoutMs }).GetValue<string>());

                /// <summary>Modbus-Wert lesen, z. B. ModbusRead("Pruefling_Modbus", "IR10", "Float32").</summary>
                protected double ModbusRead(string resource, string address, string type = "UInt16", bool lowWordFirst = false)
                {
                    var v = Dev("modbus.read", resource, new JsonObject { ["address"] = address, ["type"] = type, ["lowFirst"] = lowWordFirst });
                    return v.GetValueKind() == System.Text.Json.JsonValueKind.True ? 1 : v.GetValueKind() == System.Text.Json.JsonValueKind.False ? 0 : v.GetValue<double>();
                }

                protected string ModbusReadText(string resource, string address, int length)
                    => Dev("modbus.read", resource, new JsonObject { ["address"] = address, ["type"] = "Text", ["length"] = length })?.GetValue<string>();

                protected void ModbusWrite(string resource, string address, double value, string type = "UInt16", bool lowWordFirst = false)
                    => Dev("modbus.write", resource, new JsonObject { ["address"] = address, ["value"] = value, ["type"] = type, ["lowFirst"] = lowWordFirst });

                protected void CanSend(string resource, uint id, byte[] data, bool extended = false, bool fd = false)
                    => Dev("can.send", resource, new JsonObject { ["id"] = id, ["data"] = HexOf(data), ["extended"] = extended, ["fd"] = fd });

                /// <summary>Nächsten Rahmen mit ID (und Maske) empfangen.</summary>
                protected CanMessage CanReceive(string resource, uint id, uint? mask = null, int timeoutMs = 1000)
                {
                    var f = Dev("can.receive", resource, new JsonObject { ["id"] = id, ["mask"] = mask, ["timeout"] = timeoutMs });
                    return new CanMessage(f["id"].GetValue<uint>(), Convert.FromHexString(f["data"].GetValue<string>()), f["extended"].GetValue<bool>());
                }

                /// <summary>UDS-Anfrage über ISO-TP; liefert die positive Antwort. Negative Antwort = Ausnahme mit NRC im Text.</summary>
                protected byte[] Uds(string resource, byte[] request, uint tx = 0x7E0, uint rx = 0x7E8, int timeoutMs = 1000, bool fd = false)
                    => Convert.FromHexString(Dev("uds", resource, new JsonObject { ["request"] = HexOf(request), ["tx"] = tx, ["rx"] = rx, ["timeout"] = timeoutMs, ["fd"] = fd }).GetValue<string>());

                protected void J1939Send(string resource, uint pgn, byte[] data, byte source = 0xF9, byte destination = 0xFF, int priority = 6)
                    => Dev("j1939.send", resource, new JsonObject { ["pgn"] = pgn, ["data"] = HexOf(data), ["source"] = source, ["destination"] = destination, ["priority"] = priority });

                protected CanMessage J1939Receive(string resource, uint pgn, byte? source = null, bool request = false, int timeoutMs = 1000)
                {
                    var m = Dev("j1939.receive", resource, new JsonObject { ["pgn"] = pgn, ["source"] = source, ["request"] = request, ["timeout"] = timeoutMs });
                    return new CanMessage(0, Convert.FromHexString(m["data"].GetValue<string>()), true, m["pgn"].GetValue<uint>(), m["source"].GetValue<byte>());
                }

                protected byte[] LinRead(string resource, int id, int length = 8, bool classicChecksum = false, int timeoutMs = 100)
                    => Convert.FromHexString(Dev("lin.read", resource, new JsonObject { ["id"] = id, ["length"] = length, ["classic"] = classicChecksum, ["timeout"] = timeoutMs }).GetValue<string>());

                protected void LinWrite(string resource, int id, byte[] data, bool classicChecksum = false)
                    => Dev("lin.write", resource, new JsonObject { ["id"] = id, ["data"] = HexOf(data), ["classic"] = classicChecksum });

                /// <summary>Abbrechbares Warten.</summary>
                protected void Sleep(int milliseconds)
                {
                    CancellationToken.WaitHandle.WaitOne(milliseconds);
                    ThrowIfCancelled();
                }

                private static JsonNode ToNode(object value) => value switch
                {
                    null => null,
                    bool b => JsonValue.Create(b),
                    int i => JsonValue.Create((long)i),
                    long l => JsonValue.Create(l),
                    double d => JsonValue.Create(d),
                    float f => JsonValue.Create((double)f),
                    string s => JsonValue.Create(s),
                    DateTime dt => JsonValue.Create(dt.ToString("O", CultureInfo.InvariantCulture)),
                    _ => JsonValue.Create(value.ToString()),
                };
            }
        }
        """;
}
