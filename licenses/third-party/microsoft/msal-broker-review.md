# Microsoft-Anmeldung: Lizenzprüfung vom 03.11.2026

Die Anwendung verwendet Microsoft.Identity.Client 4.92.0 (MIT) mit dem Systembrowser.
Microsoft.Identity.Client.Broker und Microsoft.Identity.Client.NativeInterop werden
nicht mehr benötigt und nicht ausgeliefert. Der Rechteinhaber hat diese Umstellung
am 03.11.2026 ausdrücklich gewählt. Kontoauswahl und lokale Rechte bleiben erhalten.

Broker 4.92.0 verlangt NativeInterop 0.20.7. Dessen LICENSE ersetzt die frühere
MIT-Deklaration durch proprietäre Bedingungen. Abschnitt 3.e untersagt unter anderem
Veröffentlichung und Weiterverteilung. Für diese Vorbereitung wird daraus keine
Weitergabefreigabe abgeleitet. Die Release-Prüfung weist übrig gebliebene Broker-/
NativeInterop-Binärdateien und msalruntime.dll zurück.

Primärnachweise:

- https://api.nuget.org/v3-flatcontainer/microsoft.identity.client/4.92.0/microsoft.identity.client.nuspec
- https://api.nuget.org/v3-flatcontainer/microsoft.identity.client.broker/4.92.0/microsoft.identity.client.broker.nuspec
- NativeInterop 0.20.7: LICENSE im unveränderten NuGet-Paket, SHA-256
  `0fe665c2ada5962fd01190ab5b2947b9a9d2c625a9f8225577441eb3256e14e3`.
- https://learn.microsoft.com/entra/msal/dotnet/acquiring-tokens/using-web-browsers

MSAL verwendet die Weiterleitung http://localhost und ausdrücklich den Systembrowser.
Die Entra-App-Registrierung muss diese Weiterleitung unter Mobile Anwendungen und
Desktopanwendungen enthalten. Bei einer bestehenden WAM-Registrierung ergänzen;
Client-ID und Mandanten-ID ändern sich dadurch nicht. Einrichtung siehe
[Entra-Anleitung](../../../docs/entra-registrierung.md).