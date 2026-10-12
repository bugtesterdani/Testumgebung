// Tests seriell ausführen: Die Suite nutzt echte Worker-Prozesse, Sockets und virtuelle CAN-Busse mit realen
// Zeitlimits (z. B. ISO-TP-Flusssteuerung N_Bs). Bei xUnit-Parallelbetrieb konkurrieren diese um den Thread-Pool,
// wodurch asynchrone Antworten die Zeitlimits überschreiten können (sporadische Fehler, v. a. unter Windows).
// Das Attribut gibt es nur im echten xUnit (DB_PROVIDERS = NuGet-Build); der Offline-Shim läuft ohnehin seriell.
#if DB_PROVIDERS
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
#endif
