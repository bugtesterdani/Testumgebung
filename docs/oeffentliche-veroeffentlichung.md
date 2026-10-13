# Veröffentlichung, Lizenztexte und passende Quellen

Der eigene Code von Daniel (bugtesterdani) steht unter MIT; siehe `LICENSE`.
Drittanbieter behalten ihre eigenen Lizenzen. `THIRD-PARTY-NOTICES.md` ordnet
die Komponenten zu; vollständige Originaltexte stehen in `licenses/third-party/`.
Die MIT-Erlaubnis gilt nicht pauschal für alle mitgelieferten Binärdateien.

## In der Software

Die Schaltfläche **Lizenzen & Quellen** in der Kopfzeile ist ohne Anmeldung
erreichbar. Sie zeigt MIT, Drittanbieterhinweise, Laufzeitbedingungen und diese
Anleitung. **Originaltexte und Quellenordner öffnen** öffnet den Installationsordner.
Die Bedingungen der betroffenen Microsoft-Laufzeiten werden vor der ersten
Nutzung einschließlich der .NET-/SDK-Originalverträge angezeigt. Bei geänderten
Texten ist eine erneute Zustimmung erforderlich. Verteiler müssen die
Original-Vertriebsbedingungen selbst beachten; der Dialog ersetzt keine
Vereinbarung vor einer Weitergabe ohne Programmstart.

## Mit jedem Download weitergeben

Zum geprüften MSIX-Bundle gehören `SHA256SUMS.txt`, `LICENSE-VERIFICATION.json`,
`INSTALLATION.md`, `PUBLICATION.md`, `LICENSE`, `THIRD-PARTY-NOTICES.md`,
`THIRD-PARTY-TERMS.md` und das gleichversionierte **Licenses-and-Sources.zip**.
Der Release-Build erzeugt dieses ZIP aus genau dem verwendeten Installationsstand.
Es enthält Originalhinweise, das Inventar und die passenden GNU-/LGPL-Toolchain-
Quellen einschließlich Dockerfile und Anpassungen. Dieselben Quellen sind im MSIX.
Download und Quellenpaket müssen am selben Release kostenlos zugänglich bleiben.
Ein Link allein auf die neueste Version eines fremden Projekts ersetzt dies nicht.

Im Installationsordner liegen die Toolchain-Quellen unter
`toolchains/w64devkit/source/source.tar`, weitere Anpassungen unter
`toolchains/w64devkit/src` und die Buildbeschreibung unter
`toolchains/w64devkit/Dockerfile`. Das TAR lässt sich mit einem TAR-fähigen
Archivprogramm entpacken. Die enthaltenen Buildbeschreibungen benötigen ihre
jeweiligen Entwicklungswerkzeuge; der Ordner ist kein automatischer Windows-Installer.
Die Komponentenlizenzen bestimmen Änderung, Weitergabe und gegebenenfalls
Relinking; eigene Bedingungen schränken diese Open-Source-Rechte nicht ein.

GNU erläutert die Quellen-Mitgabe unter https://www.gnu.org/licenses/gpl-faq.en.html.
Eine dauerhafte schriftliche Quellenzusage wird hier nicht als Ersatz verwendet:
Die passenden Quellen werden direkt mitgeliefert.

## Zusätzliche Komponenten und Installation

Eigene importierte DLLs, nachinstallierte pip-Pakete und Herstellertreiber gehören
nicht automatisch zum geprüften Bestand. Vor deren Weitergabe sind ihre Lizenzen,
Hinweise und gegebenenfalls Quellenpflichten gesondert zu prüfen.
Microsoft-Anteile der Windows-Python-Laufzeit haben zusätzliche Bedingungen;
deren Originaltext steht in `licenses/third-party/python/LICENSE.txt`.

Das Entwicklungszertifikat ist selbst signiert. Empfänger müssen es bewusst
als vertrauenswürdig installieren; eine Signatur ist keine Lizenzfreigabe.
Für breite Verteilung kann ein geeigneter vertrauenswürdiger Signierweg gewählt
werden. Private Schlüssel und Passwörter dürfen niemals mit veröffentlicht werden.
Frühere, nicht bereinigte Releases werden nicht durch neue Unterlagen freigegeben.

## Grenze dieser Vorbereitung

Der technische Release-Check prüft die bekannten Komponenten und mitgelieferten
Nachweise. Er ist keine verbindliche Rechtsberatung. Insbesondere die Wirksamkeit
der Microsoft-Zustimmungsbedingungen in der konkreten Vertriebssituation kann
nicht durch Software geprüft werden. Das Repository wird durch diese Vorbereitung
nicht automatisch öffentlich geschaltet.
