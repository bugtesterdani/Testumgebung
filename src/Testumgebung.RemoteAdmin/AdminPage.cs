using System.Net;
using System.Text;

namespace Testumgebung.RemoteAdmin;

internal static class AdminPage
{
    public static string Render(List<StationSnapshot> stations, Func<string, List<StationRevision>> history, string token)
    {
        var html = new StringBuilder("""
            <!doctype html><html lang="de"><meta charset="utf-8"><meta name="viewport" content="width=device-width">
            <title>Prüfplatzverwaltung</title>
            <style>
            body{font:1rem system-ui;margin:2rem auto;padding:0 1rem;max-width:64rem;color:#182b42;background:#f2f5f9}
            header{display:flex;align-items:center;justify-content:space-between;gap:1rem}
            section{background:white;padding:1.25rem;margin:1rem 0;border:1px solid #d1dbe8;border-radius:.5rem}
            fieldset{display:grid;gap:.75rem;margin-bottom:1rem;border:1px solid #d1dbe8}
            input,select,button{font:inherit}button{padding:.5rem .8rem}label{display:block}summary{cursor:pointer}
            li{margin:.6rem 0}code{overflow-wrap:anywhere}h2{margin-bottom:.25rem}
            </style><main><header><h1>Prüfplatzverwaltung</h1><form method="post" action="/logout">
            """);
        html.Append($"{Token(token)}<button>Abmelden</button></form></header>");
        if (stations.Count == 0) html.Append("<p>Es hat sich noch kein Prüfplatz gemeldet.</p>");
        foreach (var station in stations) RenderStation(html, station, history(station.Id), token);
        return html.Append("</main></html>").ToString();
    }

    private static void RenderStation(StringBuilder html, StationSnapshot station, List<StationRevision> history, string token)
    {
        var settings = station.Settings;
        html.Append($"""
            <section><h2>{E(station.Id)}</h2><p>Letzte Meldung (UTC) <time>{E(station.Seen)}</time></p>
            <form method="post" action="/stations/{E(station.Id)}">{Token(token)}
            <input type="hidden" name="version" value="{station.Version}">
            <fieldset><legend>Prüfplatzanzeige</legend>
            {Checkbox("paired", "Zwei Prüfplätze gemeinsam anzeigen", settings.ShowPairedStations)}
            {Select("first", "Erster Prüfplatz", station.Stations, settings.FirstPairedStation)}
            {Select("second", "Zweiter Prüfplatz", station.Stations, settings.SecondPairedStation)}
            </fieldset><fieldset><legend>Alarmierung</legend>
            <label>ERROR-Prüfläufe in Folge (0 = aus) <input type="number" name="errors" min="0" max="2147483647" value="{settings.ErrorStreak}" required></label>
            <label>Offene Outbox-Einträge (0 = aus) <input type="number" name="outbox" min="0" max="2147483647" value="{settings.OutboxThreshold}" required></label>
            {Checkbox("folder", "Alarm bei nicht erreichbarem Ergebnisordner", settings.ResultFolderAlarm)}
            {Checkbox("calibration", "Alarm bei fälliger Kalibrierung", settings.CalibrationAlarm)}
            </fieldset><button>Änderung anfordern</button></form>
            <details><summary>Änderungsverlauf</summary><ul>
            """);
        foreach (var item in history)
            html.Append($"<li>#{item.Id} · {E(Status(item.Status))} · {E(item.Requested)} · {E(item.Actor)}<br>"
                + $"<small>{E(item.Completed ?? "")}</small><details><summary>Vorher und nachher</summary>"
                + $"<p>{E(Describe(item.Before))}</p><p>{E(Describe(item.After))}</p></details></li>");
        html.Append("</ul></details></section>");
    }

    private static string Status(string status) => status switch
    {
        "applied" => "Übernommen",
        "conflict" => "Lokale Einstellungen geändert",
        "invalid" => "Am Prüfplatz ungültig",
        _ => "Wartet auf Übernahme",
    };

    private static string Describe(Contracts.RemoteStationSettings s)
        => $"Gemeinsame Anzeige: {s.ShowPairedStations}, {s.FirstPairedStation} / {s.SecondPairedStation}; ERROR: {s.ErrorStreak}; Outbox: {s.OutboxThreshold}; Ergebnisordner: {s.ResultFolderAlarm}; Kalibrierung: {s.CalibrationAlarm}.";

    private static string Token(string value) => $"<input type='hidden' name='__RequestVerificationToken' value='{E(value)}'>";
    private static string Checkbox(string name, string label, bool selected)
        => $"<label><input name='{name}' type='checkbox' {(selected ? "checked" : "")}> {label}</label>";
    private static string Select(string name, string label, string[] values, string current)
        => $"<label>{label} <select name='{name}'><option value=''>–</option>"
            + string.Concat(values.Select(value => $"<option value='{E(value)}' {(value == current ? "selected" : "")}>{E(value)}</option>"))
            + "</select></label>";
    private static string E(string value) => WebUtility.HtmlEncode(value);
}
