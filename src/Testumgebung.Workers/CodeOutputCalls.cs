/** @file @brief Trennt Text-/Fortschrittsmeldungen vom Geräte- und Variablenprotokoll. */
using System.Text.Json.Nodes;
using Testumgebung.Domain.Code;
using Testumgebung.Domain.Definitions;
namespace Testumgebung.Workers;
internal static class CodeOutputCalls
{
    public static bool TryHandle(string? operation, JsonObject args, ICodeHost host)
    {
        switch (operation)
        {
            case "log": host.Log((string?)args["message"] ?? ""); return true;
            case "live.output":
                host.ReportLiveOutput(new LiveOutputUpdate((string?)args["text"] ?? "", (double?)args["percent"],
                    (string?)args["status"], (string?)args["channel"] ?? "Ausgabe", (bool?)args["reset"] ?? false));
                return true;
            default: return false;
        }
    }
}
