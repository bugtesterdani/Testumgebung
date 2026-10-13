# Lizenz-, Urheberrechts- und Veröffentlichungsprüfung

Stand: **13.10.2026**. Geprüfter Quellstand: `dcf2a33` und die danach lokal erstellten Audit-Dateien. Geprüfter Binärstand: **GitHub-MSIX 1.0.12.3**, Actions-Lauf `38297143352`. Der Rechteinhaber hat bestätigt: private Entwicklung, eigene Rechte, **keine OPC-Foundation-Corporate-Mitgliedschaft**.

## Ergebnis

Das Projekt ist in seinem geprüften Zustand **noch nicht für eine rechtlich als unproblematisch bewertete öffentliche Freigabe vorbereitet**. Das bedeutet keine Feststellung, dass jede öffentliche Bereitstellung verboten wäre. Es bedeutet, dass konkrete Weitergabe- und Kompatibilitätsfragen offen sind, die eine pauschale Aussage „alles MIT / alles frei verwendbar“ ausschließen.

Die wichtigsten Punkte:

| Priorität | Befund | Konsequenz | Nächster konkreter Schritt |
| --- | --- | --- | --- |
| Hoch | OPC-UA-Core `1.5.374.126`: GPLv2 für Nichtmitglieder; Quellkopfzeilen nennen Version 2 ohne erkennbare „or later“-Freigabe | Eine ausschließlich freizügige Lizenz für die verbundene Anwendung ist damit nicht einfach begründbar | Auf eine versionsgenau nachgewiesene MIT-Ausgabe umstellen und testen, oder separate Nutzungsrechte / eine passende Architektur und Lizenz rechtlich klären |
| Hoch | Gleichzeitig verwendete SQLitePCLRaw-Pakete sind Apache-2.0; SQL-SNI enthält zusätzliche proprietäre Bedingungen | „Dann nehmen wir für alles GPLv2“ ist keine automatisch kompatible Lösung | Den konkreten kombinierten Auslieferungsstand beurteilen; den alten OPC-Core nicht mit einer pauschalen Root-Lizenz überdecken |
| Hoch | Keine gewählte eigene Projektlizenz, bisher keine Root-`LICENSE` | Öffentlich sichtbar wäre der eigene Code dadurch noch nicht allgemein zur Weiterverwendung freigegeben | Eigene Lizenz auswählen, nachdem die Abhängigkeitskonfiguration kompatibel ist |
| Hoch | Das alte MSIX enthält keine vollständigen NuGet-/Runtime-/GNU-/OpenSSL-Hinweise | Copyright- und Lizenztextpflichten sind nicht vollständig dokumentiert | Die hier ergänzte Textsammlung in neue Pakete aufnehmen; historische Downloads getrennt behandeln |
| Mittel | Microsoft.Data.SqlClient.SNI.runtime `6.0.2` ist nicht MIT | Besondere Vertriebspflichten und Anforderungen an Endnutzervereinbarungen | Originalvertrag berücksichtigen; Lizenztexte beilegen allein ersetzt keine erforderliche Vereinbarung |
| Mittel | Microsoft VC-Runtimes in Python und native WPF-Dateien | Paketlizenz und native Microsoft-Weitergaberechte nicht unterschiedslos behandeln | Rechte aus der konkreten Originaldistribution / zulässigen Redistributables dokumentieren |
| Mittel | Eine Python-`libtommath.dll` ohne belastbare Versionszuordnung; ein alter xUnit-Lizenzverweis ohne versionsgenauen Originaltext | Zwei echte Nachweislücken bleiben im Inventar sichtbar | Lieferanten-/Quellnachweis beschaffen, bei der ungenutzten DLL gegebenenfalls nach Prüfung auf Mitlieferung verzichten |
| Separat | Zwei bekannte NuGet-Sicherheitswarnungen | Sicherheitsthema, keine Lizenzentscheidung | SQLite und OPC-UA auf einen getesteten, korrigierten Stand bringen |

## 1. Was tatsächlich geprüft wurde

- Alle **20 `.csproj`-Projekte**: 14 Produktprojekte, zwei Testprojekte, vier Test-Fixtures; direkte und importierte Projektverweise sowie Paketdeklarationen.
- Alle vorhandenen `project.assets.json`: **67 NuGet-/Runtime-Paketversionen**, einschließlich transitiver Abhängigkeiten und unterschiedlicher Versionen desselben Namens in verschiedenen Projekten.
- Die drei Laufzeit-`.deps.json` des tatsächlichen MSIX: Desktop, C#-Worker, Native-Worker. **45 Paketversionen** stehen in diesen Release-Manifestsätzen. Ein Manifest-Eintrag ist nicht gleichbedeutend mit einer eigenen DLL.
- **2.104 Binär-/Bibliotheksdateien** aus dem GitHub-Paket, jeweils mit Pfad, Größe, SHA256 und vorhandenen PE-Datei-/Produktversionen: 263 EXEs, 873 DLLs, 22 PYDs, eine pip-Wheeldatei und 945 statische `.a`-Archive.
- w64devkit **2.8.0** samt **21 korrespondierenden Source-Archiven**, Dockerfile, eigenen Quell-/Aliasdateien und mitgelieferten Patches.
- CPython **3.14.8**, native Unterbibliotheken und pip **26.2.1** mit **18 eingebetteten Vendor-Paketen**.
- Build-/Test-/Dokumentationswerkzeuge, GitHub Actions, externe Treiberschnittstellen, Bildherkunft und PDF-Schriften.
- Alle damals eingecheckten **530 Dateien** auf offensichtliche private Schlüssel / Vendor-Executables sowie die acht Beispielprojektdateien auf erkennbar eingebettete DLLs. Es waren keine Vendor-EXEs/DLLs/TTFs oder Schlüsseldateien in Git eingecheckt.

Die Daten stehen in [Inventar](inventar.md), [NuGet-CSV](nuget.csv), [Projekt-CSV](projects.csv), [Binär-CSV](binaries.csv), [Werkzeug-CSV](tools.csv), [pip-Vendor-CSV](pip-vendors.csv), [externen Schnittstellen](external.csv) und [Rohdaten](inventory.json). Die Abhängigkeitskanten stehen gesondert in [dependency-edges.csv](dependency-edges.csv).

Die neu erfassten Lizenz-/Hinweisdateien stehen mit eigenen Prüfsummen in [license-files.csv](license-files.csv). Das Audit kann mit `build/audit-dependencies.py --bundle <Pfad>` und anschließend `build/enrich-license-inventory.py` nachvollzogen werden. Es benötigt die wiederhergestellten NuGet-Caches, die passende portable Toolchain/Python-Laufzeit und optional `pefile` für PE-Versionsdaten. Die Komponententabellen und der rechtliche Text sind für den hier geprüften Lieferstand dokumentiert und müssen bei anderen Toolchain-/Paketversionen erneut fachlich geprüft werden; sie sind kein automatisches Rechtsfreigabeprogramm.

Die Zählung enthält bewusst wiederholt kopierte Worker-Laufzeiten und Aliasprogramme. Es handelt sich nicht um 2.104 unabhängige Fremdprodukte. Historische lokale MSIX-Versionen wurden als vorhandene Veröffentlichungsstände erfasst; ihre vollständigen Datei-Inventare wurden nicht alle erneut erstellt. Die exakte Binäraussage dieses Berichts gilt für **1.0.12.3**.

## 2. Eigene Projekte und eigene Lizenz

Die Produktmodule enthalten eigene C#-/Python-/WPF-Implementierungen und verknüpfen die in der Liste dokumentierten Fremdkomponenten. Für Contracts, Domain, Engine, Hardware, Identity, Persistence, DatabaseAdapters, Notifications, Presentation und Workers wurde keine separate kopierte Fremdbibliothek im eingecheckten Projektbaum gefunden. Instruments nutzt zusätzlich SSH.NET; Desktop bringt Datenbank-/Identitäts-/OPC-Pakete ein; Workers.CSharp nutzt Roslyn. Die Aussage betrifft den sichtbaren Projektbaum und ist kein lückenloser Nachweis der Entstehung jeder Codezeile.

Bisher fehlt eine gewählte Lizenz für den eigenen Code. Die ergänzte [LICENSE-STATUS.md](../../LICENSE-STATUS.md) dokumentiert das, vergibt aber selbst keine Open-Source-Lizenz. Ich habe keine MIT-/GPL-Datei ohne deine Lizenzentscheidung eingeführt.

Ein öffentliches GitHub-Repository und ein Open-Source-lizenziertes Repository sind unterschiedliche Dinge. GitHubs Plattformbedingungen erlauben bestimmte Plattformhandlungen; sie ersetzen keine von dir gewählte umfassende Softwarelizenz. [GitHub: Repository lizenzieren](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/licensing-a-repository).

Nach der Bereinigung der alten OPC-Abhängigkeit wäre etwa MIT für den eigenen Code denkbar, wenn du Weiterverwendung einschließlich kommerzieller Nutzung erlauben möchtest. Die eigenständigen Drittanbieterdateien würden dabei weiterhin ihre jeweiligen Lizenzen behalten. Eine Root-Lizenz darf diese nicht als ausschließlich dein Eigentum darstellen.

## 3. Der konkrete OPC-UA-Befund

Direkt referenziert werden:

- `OPCFoundation.NetStandard.Opc.Ua.Client` **1.5.374.126**.
- `OPCFoundation.NetStandard.Opc.Ua.Configuration` **1.5.374.126**.

Dadurch werden Core und Security.Certificates derselben Version aufgelöst. Die NuGet-Deklaration von Client, Configuration und Security.Certificates nennt MIT. **Core nennt dagegen eine Lizenzdatei mit dem Doppellizenzmodell.** Das ist der Grund, warum eine oberflächliche Prüfung nur der direkten Pakete zu einem falschen Ergebnis führen kann.

Belege:

1. Die Original-NuSpec von Core verweist auf `licenses/LICENSE.txt`; der mitgelieferte Text unterscheidet Corporate-Mitglieder und Nichtmitglieder. Er wurde unverändert unter `licenses/third-party/nuget/OPCFoundation.NetStandard.Opc.Ua.Core/1.5.374.126/` übernommen.
2. Der NuSpec-Repositorycommit ist **`0b23e5f39b570651dc74897080cc6030207d6f7f`**. Die Kopfzeile von [ClientBase.cs dieses Commits](https://github.com/OPCFoundation/UA-.NETStandard/blob/0b23e5f39b570651dc74897080cc6030207d6f7f/Stack/Opc.Ua.Core/Stack/Client/ClientBase.cs) bestätigt RCL für Corporate-Mitglieder und GPLv2 für alle anderen. An dieser Stelle ist keine generelle GPLv3-Weiterlizenzierung durch einen „or later“-Zusatz belegt.
3. Gleichzeitig enthält der alte Quellbaum MIT-Dateien, etwa die Client-Session. Die Pakete sind deshalb **nicht pauschal alle GPL**; ausschlaggebend bleibt, dass der verwendete Client den GPLv2-Core in derselben Anwendung braucht.
4. Die Originaltexte für GPLv2, RCL und OPC-MIT wurden unter `licenses/third-party/opc-1.5.374.126/` gesichert. Das offizielle [OPC-Lizenzportal](https://opcfoundation.org/license/source/1.11/) bestätigt das Modell.

Die konkrete Verwendung liegt in `src/Testumgebung.Desktop/OpcUaClient.cs`, `OpcUaSessionFactory.cs` und `OpcUaNotifier.cs`. Die DLLs laufen im Desktop-Prozess. Es handelt sich hier nicht um ein zufällig beigelegtes unabhängiges Programm.

Für dich als Nichtmitglied ergibt sich daraus ein erheblicher Copyleft-/Weitergabeprüfpunkt. Ob der konkrete verbundene Auslieferungsstand rechtlich als abgeleitetes Werk einzustufen ist, kann eine technische Prüfung nicht verbindlich entscheiden. Die Architektur und die Quellkopfzeilen rechtfertigen jedoch nicht, die Anwendung aktuell bedenkenlos als ausschließlich MIT zu veröffentlichen.

Eine neuere konkrete Gegenprobe wurde durchgeführt: **Core 1.5.378.182** wurde als NuGet-Paket heruntergeladen, ohne es ins Projekt einzubauen. Dessen originale `LICENSE.txt` ist OPC-MIT. Auch der heutige Upstream-Text ist [MIT](https://github.com/OPCFoundation/UA-.NETStandard/blob/master/LICENSE.txt). **Der aktuelle Master-Text lizenziert aber nicht automatisch die alte Version 1.5.374.126 um.** Ein getesteter Wechsel auf eine durchgängig passend lizenzierte Paketfamilie wäre der bevorzugte technische Lösungsweg; API-Veränderungen, Session-Verhalten und Sicherheitskorrekturen müssen geprüft werden.

## 4. Weshalb eine pauschale GPL-Datei nicht genügt

Die SQLitePCLRaw-Komponenten **2.1.10** stehen laut NuSpec unter **Apache-2.0**. Diese Lizenz ist laut [Apache Software Foundation](https://www.apache.org/licenses/GPL-compatibility.html) nicht mit GPLv2 in der Weise kompatibel, die für ein kombiniertes GPLv2-Werk erforderlich wäre. Dieselbe Desktop-Anwendung verbindet sowohl die SQLite- als auch die OPC-Core-Lieferkette. Deshalb wäre „wir veröffentlichen einfach den gesamten Code unter GPLv2“ ohne weitere Klärung keine sichere Lösung.

Hinzu kommen proprietäre Microsoft-Weitergabebedingungen der SQL-SNI-Komponente. Eine pauschale GPL-Lizenz für **alle** Dateien würde deren unveränderte Verträge nicht ersetzen. Auch ein bloßer Wechsel der eigenen Root-Datei auf GPLv3 löst einen nachweislich GPLv2-only lizenzierten Fremdanteil nicht automatisch.

Dieser Befund ist ein Kompatibilitätsrisiko aus den verwendeten Lizenztexten und der tatsächlichen Verknüpfung; er ist keine pauschale juristische Feststellung über jede mögliche Trennung oder jede Nutzungskonstellation.

## 5. Native Microsoft-SQL-Komponente

`Microsoft.Data.SqlClient` **6.0.2** hat eine MIT-Deklaration, zieht für Windows aber `Microsoft.Data.SqlClient.SNI.runtime` **6.0.2** nach. Dessen mitgelieferte `LICENSE.txt` enthält **Microsoft Software License Terms**, keine MIT-Lizenz.

Der Originalvertrag erlaubt die Weitergabe des Objektcodes als Bestandteil eigener Anwendungen, verlangt dabei aber zusätzliche Bedingungen. Dazu gehören unter anderem passende Schutzbedingungen für Vertriebspartner und externe Endnutzer, Beschränkungen einer eigenständigen Weitergabe und eine Klausel zu ausgeschlossenen Lizenzen. Eine Zuordnung unter „alles MIT“ wäre sachlich falsch. Der genaue Text liegt unter `licenses/third-party/nuget/Microsoft.Data.SqlClient.SNI.runtime/6.0.2/LICENSE.txt`.

Die native Datei ist im tatsächlich gebauten MSIX vorhanden und dem Paket über `.deps.json` zugeordnet. Die reine Aufnahme dieses Vertrags in einen Lizenzordner weist **noch keine erforderliche Endnutzerzustimmung** nach. Für öffentliche Binärverteilung sollte geklärt werden, wie die Bedingungen verbindlich berücksichtigt werden oder ob die SQL-Server-Funktion in einer entsprechend getrennten/anderen Konfiguration bereitgestellt wird. Die bestehenden Datenbankfunktionen wurden im Rahmen dieser Analyse nicht entfernt.

## 6. GCC, GDB und die gesamte w64devkit-Toolchain

Es wird der **gesamte** w64devkit-Baum veröffentlicht. Neben GCC **16.1.0** und GDB **17.1** sind unter anderem Binutils, GNU Make, BusyBox-w32, CCache, CMake, Ninja, Universal Ctags, Vim, MinGW-w64 und weitere Bibliotheken enthalten. Das Unlicense-Modell des w64devkit-Glue-Codes ist **keine einheitliche Lizenz für sämtliche enthaltenen Produkte**.

Die GNU-Programme werden von der Anwendung als getrennte Prozesse aufgerufen. Für neu kompilierten C++-Code sind außerdem Runtime-Ausnahmen und MinGW-/Winpthreads-Hinweise relevant. Allein der normale Compileraufruf oder das gemeinsame Installationspaket bedeutet nicht automatisch, dass der eigene C#-Code GPL werden muss. Die Abgrenzung hängt auch von der konkreten Integration ab. Als Orientierung dienen die [GNU-Lizenz-FAQ](https://www.gnu.org/licenses/gpl-faq.en.html) und die Hinweise im [versionsgenauen w64devkit-README](https://github.com/skeeto/w64devkit/tree/v2.8.0#licenses).

Positiv: Bereits bisher wird das zum Kit passende `source.tar` mitgegeben; sein SHA256 stimmt mit der Build-Vorgabe überein. Auch Dockerfile, lokale Anpassungen und eigene Quell-/Aliasdateien befinden sich im Paket. Das ist deutlich besser als eine bloße Verlinkung auf irgendeine aktuelle GCC-/GDB-Version.

Noch zu beachten:

- Original-Lizenztexte, Copyright-Hinweise und Versionsbezüge müssen sichtbar und erhalten bleiben. Ich habe sie aus den **passenden** Quellarchiven extrahiert und als Weitergabesammlung ergänzt.
- Die Originalquellen **einschließlich** der Patches/Buildskripte dürfen bei künftigen Paketen nicht entfernt werden. Ein Quellarchiv einer anderen Version genügt nicht.
- GNU-/LGPL-Unterbibliotheken können eigene Bedingungen zur Änderung und zum Relinking haben. Die Mitlieferung des vollständigen passenden Kit-Quellstands dient als Nachweisgrundlage; sie ist keine pauschale Zertifizierung jeder LGPL-Konstellation.
- Die Toolchain kann gegebenenfalls auf tatsächlich benötigte Programme reduziert werden. Das wäre ein eigenes, zu testendes Vorhaben. In diesem Audit wurden keine Compiler-/Debugger-Dateien entfernt.
- 7-Zip **23.01** ist als Build-Extraktor relevant, seine Source-Dateien sind im Kit-Quellenpaket; der Extraktor selbst ist nicht als 7-Zip-EXE im MSIX inventarisiert. Seine LGPL-/unRAR-Bedingungen dürfen deshalb weder ignoriert noch fälschlich zur Lizenz des eigenen Projekts erklärt werden.
- `dcmake` **1.7.1** ist nach tatsächlichem Originaltext Unlicense mit eigenen Bibliothekslizenzen, nicht wegen seines Namens ein GNU-Make-/GPL-Produkt. Solche Unterschiede wurden im Inventar berücksichtigt.

## 7. Python, pip und native Unterbibliotheken

Die eingebettete CPython-Ausgabe **3.14.8** enthält ihren PSF-/Drittanbietertext bereits. Die ursprüngliche `LICENSE.txt` wurde erhalten. Zusätzlich wurden die nativen Dateien einzeln geprüft und die Laufzeit selbst für Versionsabfragen verwendet:

| Bestandteil | Belegte Version | Lizenz-/Weitergabepunkt |
| --- | --- | --- |
| OpenSSL | 3.5.9 | Apache-2.0; Zusatztext fehlte im bisherigen Release |
| SQLite | 3.50.4 | Public domain; von SQLitePCLRaw-Version 2.1.10 zu unterscheiden |
| libffi | 3.4.4 aus CPython-Buildangaben | MIT; Python-ctypes verwendet die DLL |
| Expat | 2.8.5 aus `pyexpat.EXPAT_VERSION` | MIT |
| libmpdec | 4.0.0 aus `_decimal.__libmpdec_version__` | BSD-2-Clause; Original-Copyrighttext ergänzt |
| zlib-ng | 2.2.4 aus CPython-Buildangaben | Zlib; Laufzeit meldet `1.3.1.zlib-ng` |
| liblzma/XZ | 5.2.5 aus CPython-Buildangaben | Welche Quellteile tatsächlich einfließen, ist zu unterscheiden; nicht pauschal jede XZ-Quelllizenz auf die DLL übertragen |
| zstd | 1.5.7 aus CPython-Buildangaben | BSD-3-Clause oder GPLv2 nach Originalwahl |
| Microsoft VC runtime | 14.51.36247.0 aus PE-Dateien | Separate Microsoft-Redistribution-Bedingungen |
| libtommath.dll | Keine nachgewiesene numerische Version | Datei im unveränderten offiziellen ZIP vorhanden; Herkunft innerhalb dieser Lieferkette belegt, genauer Quell-/Lizenzbezug noch offen |

`libtommath.dll` besitzt keine ausgelesene Versionsressource und wird in den statischen Importtabellen der übrigen Python-Dateien nicht referenziert. Das beweist **nicht**, dass sie niemals dynamisch geladen wird. Ihre aktuelle Upstream-Lizenz wurde als Recherchebeleg gesichert, aber nicht als abschließender Nachweis für diese Binärdatei ausgegeben.

pip **26.2.1** bringt **18 Vendor-Pakete** mit. Auch hier wäre „pip ist MIT, also ist alles darin MIT“ falsch. Beispielsweise sind requests Apache-2.0 und certifi MPL-2.0. Namen, Versionen und Originaltexte stehen in [pip-vendors.csv](pip-vendors.csv) beziehungsweise `licenses/third-party/pip-26.2.1/`. Die Wheeldatei enthält die Python-Quellformen der Vendor-Pakete; deren Hinweise wurden nicht entfernt.

## 8. .NET, MSAL, Roslyn, SSH.NET und Datenbankprovider

Die aufgelösten NuGet-Metadaten nennen überwiegend MIT, außerdem PostgreSQL und Apache-2.0. Roslyn **4.14.0** wird im isolierten C#-Worker verwendet; SSH.NET **2026.0.0** in `RemoteDevice.cs` für SSH/SFTP. HTTP-/CURL-artige Anfragen verwenden .NET `HttpClient`, kein mitgeliefertes libcurl/curl-Programm. Entra-Anmeldung verwendet MSAL **4.65.0** und seine aufgeführten transitiven Pakete.

Das geprüfte selbst enthaltene .NET-/WPF-Runtimepaket ist **10.0.12**. Es wird mehrfach kopiert, damit die Worker unabhängig laufen. Seine Original-Lizenz- und Drittanbietertexte wurden aus den passenden Runtime-NuGet-Paketen übernommen. Frameworkbibliotheken sind nicht 873 unabhängig zu beschaffende NuGets; ihre genaue Paketzuordnung ist in der Binärliste hinterlegt.

Native WPF-Dateien wie `D3DCompiler_47_cor3.dll` und `vcruntime140_cor3.dll` haben Microsoft-Produktmetadaten. Im Inventar bleibt die Herkunft aus dem offiziellen Runtimepack belegt; die Paketdeklaration MIT allein ist keine unabhängig untersuchte Aussage zu jedem denkbaren ursprünglichen Microsoft-Redistributable. Insbesondere dürfen normale VC-Laufzeitbedingungen nicht mit einer selbst gewählten Projektlizenz verwechselt werden. [Microsoft: Redistributable-Regeln](https://learn.microsoft.com/en-us/visualstudio/releases/2022/redistribution).

Ein kleiner offener Testfall: `xunit.abstractions` **2.0.3** nennt nur einen beweglichen alten Lizenz-URL-Verweis. Der versionsgenaue Abstractions-Quellbaum enthält keine gefundene Lizenzdatei. Deshalb ist dieser Eintrag ausdrücklich **UNRESOLVED** statt mit dem heutigen xUnit-Text nachträglich als geprüft deklariert. Er ist nur eine Testabhängigkeit und nicht Bestandteil des untersuchten MSIX.

## 9. Externe Hersteller-Treiber, eigene Geräte und importierte DLLs

NI-DAQmx/VISA, PEAK PCAN/PLIN, Kvaser und Vector werden über externe native APIs verwendet. Die Originalhersteller-DLLs dieser Schnittstellen sind im MSIX **nicht** vorhanden. Ihre Version ist deshalb nicht aus dem Projekt festlegbar. Datei-, Quell- und Herstellerzuordnung stehen in [external.csv](external.csv).

Das reduziert die Weitergabefragen für die derzeitige Distribution, ersetzt aber keine Lizenz für tatsächlich installierte Treiber. Sollten künftig Hersteller-DLLs oder SDK-Dateien mitgeliefert werden, müssen deren Verträge zusätzlich geprüft werden. Dieselbe Regel gilt für LabVIEW-DLLs und für vom Nutzer importierte C#-DLLs. Eine Softwarefunktion zum Importieren einer DLL erteilt keine Rechte an deren Inhalt.

Chroma-/EAQ-, SCPI- und Buskommunikation sind im Projekt implementierte Protokoll-/API-Adapter. Es wurden keine kopierten Herstellerhandbücher oder vollständigen Hersteller-SDKs im eingecheckten Projektbaum gefunden. Diese Prüfung kann trotzdem keine Urheberschaft jedes API-Details oder umfassende Patent-/Markenrechte beweisen. Produktnamen in der Gerätezuordnung sind keine Behauptung einer Herstellerfreigabe oder Zertifizierung.

## 10. Bilder, Schriften, Dokumentation und KI-Unterstützung

Das App-Icon ist laut `Assets/README.md` mit Imagegen erzeugt; der vollständige Prompt liegt bei. Die Handbuchbilder stammen aus den tatsächlichen WPF-Ansichten mit Beispielkonfiguration. Es wurde keine zusätzliche Sammlung fremder Produktlogos gefunden. Das ist eine dokumentierte Herkunft, keine Garantie, dass keinerlei unbekannte Ähnlichkeit zu geschützten Werken oder Marken besteht.

Die Dokumentations-PDFs verwenden Segoe UI und Consolas aus Windows. Die geprüften OS/2-Einbettungsflags aller drei TTF-Dateien sind **8** (editable embedding). Standalone-TTFs werden nicht mit dem Projekt/MSIX weitergegeben. Microsoft unterscheidet erlaubte Dokumenteinbettung von der allgemein nicht gestatteten Weitergabe ganzer Windows-Schriftdateien. [Microsoft: Font FAQ](https://learn.microsoft.com/en-us/typography/fonts/font-faq).

KI-Unterstützung ersetzt keine Lizenzprüfung. Rechte an den eigenen Outputs sind von Rechten an enthaltenen Fremdanteilen und von der Frage gesetzlicher Schutzfähigkeit zu unterscheiden. Es wird hier keine ausschließliche, weltweit garantierte Urheberrechtsschutzbehauptung für das generierte Icon getroffen.

## 11. Fehlende Dateien und vorgenommene Ergänzungen

Im ursprünglichen geprüften MSIX fanden sich nur drei nach dem Namen als Lizenz-/Hinweisdateien erkennbare Treffer: Python `LICENSE.txt`, MinGW-COPYING und ein **generisches CPack-Lizenztemplate**. Letzteres ist kein Nachweis einer gültigen Projektlizenz.

Ergänzt wurden:

- [THIRD-PARTY-NOTICES.md](../../THIRD-PARTY-NOTICES.md) mit Copyright-/Autorenangaben, Paketversionen und Verweisen auf Originaltexte.
- `licenses/third-party/` mit übernommenen Originaltexten sowie Standard-Lizenztexten für Lizenzexpressions. Paketbezogene `PACKAGE-NOTICE.txt` bewahren die NuSpec-Copyright-Angaben; generische MIT-Textvorlagen allein ersetzen diese nicht.
- [LICENSE-STATUS.md](../../LICENSE-STATUS.md), ausdrücklich ohne unerbetene Wahl einer eigenen Lizenz.
- Der vollständige Inventarsatz einschließlich SHA256 der Dateien und Originaltexte.
- `build/publish.ps1` kopiert die neue Sammlung und den Lizenzstatus künftig in die Auslieferung. Eine später ausgewählte Root-Lizenz wird ebenfalls übernommen.

Das beseitigt Dokumentationslücken **für zukünftige Builds mit diesen Dateien**, aber **nicht** automatisch den alten OPC-/SQL-Kompatibilitätsbefund, erforderliche Endnutzerbedingungen oder die beiden offenen Herkunfts-/Lizenznachweise. Das bereits bestehende MSIX 1.0.12.3 ist unverändert und erhält keine Texte rückwirkend. In diesem Arbeitsschritt wurde keine Abhängigkeit heimlich aktualisiert und kein vorhandenes Release umsigniert.

## 12. Separat: Sicherheit und Reproduzierbarkeit

Die NuGet-Abfrage meldet im geprüften Restore-Stand:

- SQLitePCLRaw.lib.e_sqlite3 **2.1.10**: hohe Schwere, [GHSA-2m69-gcr7-jv3q](https://github.com/advisories/GHSA-2m69-gcr7-jv3q).
- OPCFoundation.NetStandard.Opc.Ua.Core **1.5.374.126**: mittlere Schwere, [GHSA-h958-fxgg-g7w3](https://github.com/advisories/GHSA-h958-fxgg-g7w3).

Das sind Sicherheitsbefunde, keine Lizenzkategorien. Ein getesteter OPC-Wechsel kann beide Themen berühren, aber ein Lizenzwechsel allein bestätigt keine behobene Schwachstelle.

SDK-/Runner- und manche Python-Dokumentationsversionen sind im Workflow nur teilweise festgelegt. Paketversionen mit gleichen IDs variieren außerdem zwischen einzelnen Restore-Graphen. Die CSV nennt deshalb den **tatsächlich beobachteten** Stand. Für längerfristig reproduzierbare Releases wären Lockdateien beziehungsweise eine klar dokumentierte Versionierungsstrategie und ein neuer Audit bei Abhängigkeitsänderungen sinnvoll. Die jetzt gespeicherte Liste darf nicht ungeprüft als Nachweis für beliebige spätere Versionen verwendet werden.

Zusätzlicher GitHub-Befund: Der automatisch bereitgestellte Workflow „Automatic Dependency Submission (NuGet)“, Lauf `38348459234`, schlägt beim Restore auf Linux mit `NETSDK1100` fehl, weil Desktop und UI-Tests Windows/WPF anfordern. Das ist keine Lizenzverletzung und betrifft nicht den Windows-MSIX-Build. Das hier erstellte Inventar beruht auf den tatsächlich vorhandenen Windows-Restore- und Release-Daten und ist davon unabhängig. Für diesen zusätzlichen Workflow müsste Windows-Targeting beim reinen Restore aktiviert oder ein geeigneter Windows-basierter Dependency-Submission-Ablauf verwendet werden. Die Anwendung wurde in diesem Audit nicht auf Linux umgestellt.

## 13. Schritte vor einer öffentlichen Veröffentlichung

1. Die gesamte verwendete OPC-Paketfamilie auf einen versionsgenau belegten, passenden Lizenzstand bringen; Funktion und Sicherheit testen. Die beobachtete MIT-Ausgabe ist ein Ausgangspunkt, kein bereits getestetes Upgrade.
2. Die native SQL-SNI-/Microsoft-Redistribution-Konfiguration samt Endnutzerbedingungen klären. Bei Bedarf SQL-Server-Unterstützung anders paketieren; nicht pauschal alles unter GPL stellen.
3. Den noch offenen libtommath-Nachweis und den alten xUnit-Testlizenzverweis schließen beziehungsweise nach Prüfung ersetzen.
4. Die gewünschte eigene Lizenz auswählen und mit passender Rechteinhaberbezeichnung als Root-`LICENSE` ergänzen; Drittanbieter-Ausnahmen ausdrücklich erhalten.
5. Einen neuen MSIX-Stand mit vollständigen Hinweisen bauen, gegen das aktualisierte Inventar prüfen und historische Downloads getrennt beurteilen.
6. Erst dann die öffentliche Repository-/Release-Freigabe vornehmen. Die veröffentlichte Git-Historie, Beispielprojekte, Bilder und importierbaren Nutzerdateien gehören ebenfalls zum Freigabeumfang.

Diese Prüfung ist eine technische und dokumentierte Lizenz-Triage, keine verbindliche Rechtsberatung oder Lizenzfreigabe. Für die konkret belegten GPL-/Apache-/proprietären Kombinationen ist eine abschließende rechtliche Beurteilung sinnvoll; bei überwiegend freizügigen Paketen wurden keine zusätzlichen pauschalen Hindernisse erfunden.
