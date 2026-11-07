# DAQ-Demo: Analog, Digital und Messverlauf ohne Hardware

[Projektdatei öffnen](daq-demo.tproj.json). Das Beispiel verwendet die vorhandenen Ablaufpunkte und prüft eine Rückkopplung von DAC0 auf AIN0 sowie den Zustand von DIO4. Es zeichnet eine Sekunde lang die Spannung auf und bewertet das Band 2,4–2,6 V.

## Prüfplatz einrichten

1. Projekt im Ablaufeditor öffnen.
2. Unter **Prüfplatzkonfiguration → Geräte → + Gerät** den Adapter **DAQ DIO / AIO (Nachbildung ohne Treiber)** wählen.
3. Modell **T7**, Ressource **DAQ-DEMO**. Optionen, je eine Zeile:

```text
Rueckkopplung=AIN0=DAC0
Sichere Ausgänge=DAC0=0;DIO4=0
```

4. Ressourcen zuordnen:

| Projektressource | Gerät | Kanal |
|---|---|---|
| DAQ_Ausgang | angelegte DAQ-Nachbildung | DAC0 |
| DAQ_Eingang | dieselbe Nachbildung | AIN0 |
| DAQ_Digital | dieselbe Nachbildung | DIO4 |

5. Zuordnung speichern und Geräteprüfung ausführen.
6. **Start** mit der Prüfplatzzuordnung verwenden. Obwohl dies der reale Ausführungsweg ist, arbeitet das ausgewählte Gerät vollständig ohne Hardware. **Simulation starten** verwendet die allgemeine Projektsimulation und prüft daher nicht den neuen DAQ-Adapter.
7. Seriennummer eingeben. Erwartet: PASS, 2,5 V, Digitalzustand true, Spannungskurve innerhalb des Bands. Regulär und in der Exit-Gruppe werden DAC0/DIO4 anschließend auf null gesetzt.

Es sind weder Herstellerbibliotheken noch ein angeschlossenes Messgerät erforderlich. Für ein reales Gerät Adapter, Kennung, Verdrahtung und Ressourcen passend ändern; die Nachbildungs-Rückkopplung nicht auf einen echten Treiber übertragen. [Geräteanleitung](../labjack-redlab.md).
