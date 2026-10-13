# Datenverarbeitung in Testumgebung

Diese Beschreibung erläutert den aktuellen Softwareaufbau. Der Betreiber des
Prüfplatzes legt Zweck, Rechtsgrundlage, Zugriffsrechte und Löschfristen fest;
sie ist keine fertige Datenschutzerklärung für jedes Unternehmen.

## Lokale Daten und konfigurierte Ziele

Die Anwendung verarbeitet Fertigungsaufträge, Seriennummern, Messwerte,
Prüfergebnisse und gegebenenfalls Benutzeridentitäten. Projekt- und Ergebnisdaten
werden in den ausgewählten Dateien beziehungsweise in der konfigurierten
Datenbank gespeichert. Eingeschaltete Meldewege, Geräteverbindungen und eigene
Codebausteine können Daten an die jeweils eingerichteten Ziele übertragen.
Insbesondere eigene DLLs oder Skripte können zusätzliche Netzwerkzugriffe ausführen.

Einstellungen liegen unter `%LOCALAPPDATA%/Testumgebung`, die administrative
Zugangskonfiguration unter `%ProgramData%/Testumgebung`. Bestimmte Geheimnisse
werden lokal durch Windows DPAPI geschützt. Dies ersetzt keine angemessenen
Dateirechte oder die Absicherung der Zielsysteme.

Fehlerprotokolle unter `%LOCALAPPDATA%/Testumgebung/diagnose` enthalten technische
Ausnahmen, Zeitpunkte und Versionen und können Pfade oder weitere Daten aus
Fehlermeldungen enthalten. Es bleiben höchstens 50 Protokolle erhalten.
Vor dem Weitergeben von Protokollen, Screenshots oder Projekten sensible Daten
entfernen. Keine produktiven Zugangsdaten in öffentliche GitHub-Issues einfügen.

## Microsoft-Dienste

Bei konfigurierter Entra-Anmeldung erfolgt eine Verbindung zu Microsoft über
MSAL und den Windows-Kontomanager. Die Anwendung erhält unter anderem Konto-,
Tenant- und Rolleninformationen. Microsoft- und Tenant-Bedingungen gelten dafür
zusätzlich. Ohne entsprechende Konfiguration ist dies keine automatische
Firmenkonto-Anmeldung.

Windows- und .NET-Fehlerberichterstattung kann Microsoft Diagnosedaten
bereitstellen, abhängig von Windows-Konfiguration und Fehlerfall. Das Vorhandensein
der .NET-Laufzeit beweist weder eine regelmäßige Übertragung aller Prüfergebnisse
noch vollständige Offline-Nutzung. Windows-Fehlerberichte und optionale Dienste
sind durch den Betreiber passend zu konfigurieren.
Siehe [Microsoft-Datenschutzbestimmungen](https://privacy.microsoft.com/privacystatement)
und die mitgelieferten Microsoft-Laufzeitbedingungen.

## Vor produktivem Betrieb

Der Betreiber dokumentiert die tatsächlich aktivierten Verbindungen und Ziele,
die verarbeiteten personenbezogenen Daten, berechtigte Personen und Löschfristen.
Eine Netzwerkmessung einer konkreten Konfiguration ist zusätzlich nötig, wenn
eine verbindliche Aussage über deren tatsächliche Datenübertragung gebraucht wird.
In dieser Vorbereitung wurde keine vollständige Laufzeit-Netzwerkmessung durchgeführt.
