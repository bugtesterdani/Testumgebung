# App-Icon der Testumgebung

Motiv: weiße Messkurve mit integriertem Prüfhaken auf abgerundeter blauer Kachel. Farben greifen #0D3B70 und #1565D8 aus der Oberfläche auf. Die Form bleibt ohne Beschriftung in der Taskleiste erkennbar; außerhalb der Kachel ist der Hintergrund transparent.

Testumgebung-icon-master.png ist die hochauflösende Quelle. AppIcon.png wird in der Kopfzeile angezeigt; Testumgebung.ico enthält die Größen 16, 24, 32, 48, 64, 128 und 256 für EXE, Fenster und Taskleiste. Msix enthält Store-, Startmenü- und DPI-Varianten sowie Icons ohne zusätzliche Windows-Kachelfläche.

Alle abgeleiteten Dateien sind im Projekt gespeicherte Ressourcen und werden bei normalen Builds ohne Bildgenerierung verwendet. Nach einem Austausch des Masters einmal `pwsh -File build/icon-assets.ps1` im Projekt ausführen. Das Script skaliert die Quelle und erstellt die PNG-/ICO-Dateien reproduzierbar. Das MSIX-Script kopiert diese Assets anstelle des bisherigen provisorischen T-Logos.

Erstellt mit dem eingebauten Imagegen-Werkzeug, mit transparentem Hintergrund. Vollständiger Entwurfsprompt:

> Design a finished Windows desktop application icon for 'Testumgebung', professional industrial automated test bench software. Single square icon, transparent outside a rounded square blue tile. Deep navy #0D3B70 and cobalt #1565D8, restrained subtle gradient. Inside: one bold elegant white oscilloscope measurement waveform transitioning into a checkmark, unified geometric symbol, centered, strong thick strokes and generous padding. Small cyan accent may be used sparingly. Crisp flat vector-like edges, highly legible at 16 to 48 pixels. Premium engineering utility, calm and precise. No text, no letters, no numbers, no tiny circuit detail, no decorative objects, no perspective, no mockup, no surrounding scene, no drop shadow outside tile. Deliver just one large frontal icon with true transparent background.
