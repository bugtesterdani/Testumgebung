# Vollständiges Abhängigkeitsinventar

Prüfstand: 2026-10-14. Binärstand: MSIX 1.0.15.1. Die Version einer NuGet-Komponente kann von ihrer Assembly-/Dateiversion abweichen. Tabellen unterscheiden deshalb Paket-, Produkt- und Dateiversionen.

## Dateien für die genaue Auswertung

- [Alle eigenen Projekte](projects.csv)
- [NuGet-Versionen, Lizenzen und Verwendungsorte](nuget.csv)
- [Jede ausgelieferte Binär-/Bibliotheksdatei](binaries.csv)
- [Prüfsummen aller Lizenz- und Hinweisdateien](license-files.csv)
- [Aufgelöste NuGet-Abhängigkeitskanten je Projekt/Framework](dependency-edges.csv)
- [Werkzeuge, Laufzeiten und native Bestandteile](tools.csv)
- [In pip eingebettete Pakete](pip-vendors.csv)
- [Externe Treiber und DLLs](external.csv)
- [Vollständige Rohdaten und Release-Manifeste](inventory.json)

Die CSV-Dateien verwenden UTF-8 mit BOM und lassen sich in Excel importieren. `binaries.csv` enthält auch 945 statische `.a`-Bibliotheken; die 2106 Zeilen sind keine 2.104 unabhängigen Produkte. Mehrfach kopierte .NET-Worker-Dateien und Kit-Aliasprogramme sind absichtlich enthalten.

## Eigene Projekte

| Projekt | Framework | Version im Prüfstand | Aufgabe |
| --- | --- | --- | --- |
| src/Testumgebung.Contracts/Testumgebung.Contracts.csproj | net10.0 | 1.0.15.1 in audited MSIX | Gemeinsame Verträge und Grundtypen |
| src/Testumgebung.DatabaseAdapters/Testumgebung.DatabaseAdapters.csproj | net10.0 | 1.0.15.1 in audited MSIX | Datenbank-Verbindungen |
| src/Testumgebung.Desktop/Testumgebung.Desktop.csproj | net10.0-windows | 1.0.15.1 in audited MSIX | WPF-Oberfläche, Entra/MSAL, Datenbankprovider und OPC UA |
| src/Testumgebung.Domain/Testumgebung.Domain.csproj | net10.0 | 1.0.15.1 in audited MSIX | Prüfprojekt-, Variablen- und Ergebnismodell |
| src/Testumgebung.Engine/Testumgebung.Engine.csproj | net10.0 | 1.0.15.1 in audited MSIX | Ablaufausführung, Bausteine und Ressourcen-Sperren |
| src/Testumgebung.Hardware/Testumgebung.Hardware.csproj | net10.0 | 1.0.15.1 in audited MSIX | Hardware-Abstraktionen und Simulation |
| src/Testumgebung.Identity/Testumgebung.Identity.csproj | net10.0 | 1.0.15.1 in audited MSIX | Anmeldung und Berechtigungsverträge |
| src/Testumgebung.Instruments/Testumgebung.Instruments.csproj | net10.0 | 1.0.15.1 in audited MSIX | Gerätetreiber, SSH/SFTP, Busse und Netzwerk |
| src/Testumgebung.Notifications/Testumgebung.Notifications.csproj | net10.0 | 1.0.15.1 in audited MSIX | Meldewege und Benachrichtigungen |
| src/Testumgebung.Persistence/Testumgebung.Persistence.csproj | net10.0 | 1.0.15.1 in audited MSIX | Projekt-, Ergebnis-, Einstellungs- und Release-Speicherung |
| src/Testumgebung.Presentation/Testumgebung.Presentation.csproj | net10.0 | 1.0.15.1 in audited MSIX | Ansichtsmodelle, Editor und Werkersteuerung |
| src/Testumgebung.Workers/Testumgebung.Workers.csproj | net10.0 | 1.0.15.1 in audited MSIX | C#/Python/C++-Prozesssteuerung, Laufzeitwahl und GDB |
| src/Testumgebung.Workers.CSharp/Testumgebung.Workers.CSharp.csproj | net10.0 | 1.0.15.1 in audited MSIX | Isolierter Roslyn-C#-Compiler/Worker |
| src/Testumgebung.Workers.Native/Testumgebung.Workers.Native.csproj | net10.0 | 1.0.15.1 in audited MSIX | C++-Compileraufruf und native Ausführung |
| tests/Desktop.UiTests/Desktop.UiTests.csproj | net10.0-windows | Default assembly 1.0.0; not shipped | Automated tests |
| tests/Engine.Tests/Engine.Tests.csproj | net10.0 | Default assembly 1.0.0; not shipped | Automated tests |
| tests/Fixtures/FakeCompiler/FakeCompiler.csproj | net10.0 | Default assembly 1.0.0; not shipped | Test-only fixture |
| tests/Fixtures/FakeGdb/FakeGdb.csproj | net10.0 | Default assembly 1.0.0; not shipped | Test-only fixture |
| tests/Fixtures/OrderHelper/OrderHelper.csproj | net10.0 | Default assembly 1.0.0; not shipped | Test-only fixture |
| tests/Fixtures/OrderLibrary/OrderLibrary.csproj | net10.0 | Default assembly 1.0.0; not shipped | Test-only fixture |

Die vollständigen Projektverweise und umgekehrten Verwendungsorte stehen in `projects.csv`; importierte Worker-Buildreferenzen sind dort ausdrücklich markiert.

## NuGet-Paketversionen

| Paket | Version | Lizenz | Verwendung im MSIX |
| --- | --- | --- | --- |
| Azure.Core | 1.38.0 | MIT | . |
| Azure.Identity | 1.11.4 | MIT | . |
| BitFaster.Caching | 2.6.0 | MIT | . |
| BouncyCastle.Cryptography | 2.7.0 | MIT | . |
| coverlet.collector | 10.1.0 | MIT | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| Microsoft.Bcl.AsyncInterfaces | 1.1.1 | MIT | . |
| Microsoft.Bcl.Cryptography | 9.0.4 | MIT | . |
| Microsoft.CodeAnalysis.Analyzers | 3.11.0 | MIT | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| Microsoft.CodeAnalysis.Common | 4.14.0 | MIT | ., workers/csharp |
| Microsoft.CodeAnalysis.CSharp | 4.14.0 | MIT | ., workers/csharp |
| Microsoft.CodeCoverage | 18.5.1 | MIT | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| Microsoft.Data.SqlClient | 6.0.2 | MIT | . |
| Microsoft.Data.SqlClient.SNI.runtime | 6.0.2 | Microsoft proprietary redistributable terms (LICENSE.txt) | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| Microsoft.Data.Sqlite | 10.0.13 | MIT | . |
| Microsoft.Data.Sqlite.Core | 10.0.13 | MIT | . |
| Microsoft.Extensions.Caching.Abstractions | 9.0.4 | MIT | . |
| Microsoft.Extensions.Caching.Memory | 9.0.4 | MIT | . |
| Microsoft.Extensions.DependencyInjection | 10.0.8 | MIT | . |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.8 | MIT | . |
| Microsoft.Extensions.DependencyInjection.Abstractions | 8.0.2 | MIT | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| Microsoft.Extensions.DependencyInjection.Abstractions | 9.0.4 | MIT | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| Microsoft.Extensions.Logging | 10.0.8 | MIT | . |
| Microsoft.Extensions.Logging.Abstractions | 10.0.8 | MIT | . |
| Microsoft.Extensions.Logging.Abstractions | 8.0.3 | MIT | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| Microsoft.Extensions.Logging.Abstractions | 9.0.4 | MIT | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| Microsoft.Extensions.Options | 10.0.8 | MIT | . |
| Microsoft.Extensions.Options | 9.0.4 | MIT | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| Microsoft.Extensions.Primitives | 10.0.8 | MIT | . |
| Microsoft.Extensions.Primitives | 9.0.4 | MIT | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| Microsoft.Identity.Client | 4.61.3 | MIT | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| Microsoft.Identity.Client | 4.65.0 | MIT | . |
| Microsoft.Identity.Client.Broker | 4.65.0 | MIT | . |
| Microsoft.Identity.Client.Extensions.Msal | 4.61.3 | MIT | . |
| Microsoft.Identity.Client.NativeInterop | 0.16.2 | MIT | . |
| Microsoft.IdentityModel.Abstractions | 7.5.0 | MIT | . |
| Microsoft.IdentityModel.JsonWebTokens | 7.5.0 | MIT | . |
| Microsoft.IdentityModel.Logging | 7.5.0 | MIT | . |
| Microsoft.IdentityModel.Protocols | 7.5.0 | MIT | . |
| Microsoft.IdentityModel.Protocols.OpenIdConnect | 7.5.0 | MIT | . |
| Microsoft.IdentityModel.Tokens | 7.5.0 | MIT | . |
| Microsoft.NET.Test.Sdk | 18.5.1 | MIT | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| Microsoft.SqlServer.Server | 1.0.0 | MIT | . |
| Microsoft.TestPlatform.ObjectModel | 18.5.1 | MIT | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| Microsoft.TestPlatform.TestHost | 18.5.1 | MIT | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| MySqlConnector | 2.4.0 | MIT | . |
| Newtonsoft.Json | 13.0.3 | MIT | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| Newtonsoft.Json | 13.0.4 | MIT | . |
| Npgsql | 9.0.3 | PostgreSQL | . |
| OPCFoundation.NetStandard.Opc.Ua.Client | 1.5.378.182 | MIT | . |
| OPCFoundation.NetStandard.Opc.Ua.Configuration | 1.5.378.182 | MIT | . |
| OPCFoundation.NetStandard.Opc.Ua.Core | 1.5.378.182 | MIT | . |
| OPCFoundation.NetStandard.Opc.Ua.Security.Certificates | 1.5.378.182 | MIT | . |
| OPCFoundation.NetStandard.Opc.Ua.Types | 1.5.378.182 | MIT | . |
| runtimepack.Microsoft.NETCore.App.Runtime.win-x64 | 10.0.13 | MIT plus Microsoft .NET Library / Windows SDK terms; see per-file attribution | ., ., ., workers/csharp, workers/native |
| runtimepack.Microsoft.WindowsDesktop.App.Runtime.win-x64 | 10.0.13 | MIT plus Microsoft .NET Library / Windows SDK terms; see per-file attribution | . |
| SQLitePCLRaw.bundle_e_sqlite3 | 2.1.13 | Apache-2.0 | . |
| SQLitePCLRaw.core | 2.1.13 | Apache-2.0 | . |
| SQLitePCLRaw.lib.e_sqlite3 | 2.1.13 | Apache-2.0 | . |
| SQLitePCLRaw.provider.e_sqlite3 | 2.1.13 | Apache-2.0 | . |
| SSH.NET | 2026.0.0 | MIT | . |
| System.ClientModel | 1.0.0 | MIT | . |
| System.Configuration.ConfigurationManager | 9.0.4 | MIT | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| System.Diagnostics.EventLog | 9.0.4 | MIT | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| System.IdentityModel.Tokens.Jwt | 7.5.0 | MIT | . |
| System.Memory.Data | 1.0.2 | MIT | . |
| System.Security.Cryptography.Pkcs | 9.0.21 | MIT | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| System.Security.Cryptography.ProtectedData | 9.0.0 | MIT | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| System.Security.Cryptography.ProtectedData | 9.0.4 | MIT | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| xunit | 2.9.3 | Apache-2.0 | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| xunit.abstractions | 2.0.3 | Apache-2.0 (explicit frozen-abstractions upstream declaration; see UPSTREAM-LICENSE-DECLARATION.md) | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| xunit.analyzers | 1.18.0 | Apache-2.0 | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| xunit.assert | 2.9.3 | Apache-2.0 | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| xunit.core | 2.9.3 | Apache-2.0 | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| xunit.extensibility.core | 2.9.3 | Apache-2.0 | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| xunit.extensibility.execution | 2.9.3 | Apache-2.0 | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| xunit.runner.visualstudio | 3.1.1 | Apache-2.0 | Nicht im Release-Manifestsatz; nur Restore/Build/Test |

`nuget.csv` nennt zusätzlich direkte und transitive Verwendungsprojekte sowie konkrete Quelltextstellen für die direkt verwendeten APIs. Ein Restore-Eintrag allein bedeutet nicht, dass seine DLL im MSIX liegt. Die tatsächliche Zuordnung erfolgt über die Release-`.deps.json` und anschließend vorhandene Binärpfade.

## Werkzeuge und weitere Komponenten

| Komponente | Version | Lizenz/Status | Verwendung |
| --- | --- | --- | --- |
| w64devkit | 2.8.0 | Unlicense for kit glue; component licenses separately | build/acquire-toolchain.ps1; build/CodeWorkers.targets |
| GCC | 16.1.0 | GPL-3.0-or-later; runtime exception for eligible runtime use | src/Testumgebung.Workers.Native/Toolchain.cs |
| GDB | 17.1 | GPL-3.0-or-later | src/Testumgebung.Workers/GdbInspector.cs |
| GNU Binutils | 2.46.0 | GPL family, subcomponent licenses in source | GCC linker/assembler/toolchain |
| GNU Make | 4.4.1 | GPL-3.0-or-later | Shipped as part of full kit; not directly called by application |
| BusyBox-w32 | FRP-5857-g3681e397f | GPL-2.0-only with component exceptions | Full-kit utilities and launcher aliases |
| CMake | 4.3.2 | BSD-3-Clause plus bundled-library licenses | Shipped as part of full kit |
| Ninja | 1.13.2 | Apache-2.0 | Shipped as part of full kit |
| CCache | 4.13.4 | GPL-3.0-or-later | Full-kit compiler cache |
| Universal Ctags | 6.2.1 | GPL-2.0 family; see source notices | Shipped as part of full kit |
| Vim | 9.0 | Vim license | Full-kit editor and alias launchers |
| MinGW-w64 | 14.0.0 | Mixed permissive/public-domain/LGPL parts; see COPYING | Native compiler headers, import/static libraries, gendef/widl |
| GMP | 6.3.0 | LGPL-3.0-or-later OR GPL-2.0-or-later | Linked into GNU tools |
| MPFR | 4.2.2 | LGPL-3.0-or-later | Linked into GNU tools |
| MPC | 1.4.1 | LGPL-3.0-or-later | Linked into GNU tools |
| Expat (kit) | 2.8.0 | MIT | Linked into GDB |
| libiconv | 1.19 | LGPL-2.1-or-later library; GPL utility | Kit utility and GDB dependency |
| PDCurses | 3.9 | Public domain core; port-specific licenses | Windows-console support in Vim |
| xxHash | 0.8.3 | BSD-2-Clause library; GPL utility portions | Dependency of kit tools |
| Zstandard | 1.5.7 | BSD-3-Clause OR GPL-2.0-only | Kit tools and BusyBox dependency |
| dcmake | 1.7.1 | Unlicense plus bundled MIT/Zlib library notices | Shipped as part of full kit |
| 7-Zip SFX | 23.01 | LGPL and unRAR restriction; see source DOC/License.txt | Build-only archive extractor; not shipped as an EXE in MSIX |
| CPython | 3.14.8 | PSF and included third-party notices | src/Testumgebung.Workers/CodeRuntime.cs; PythonEnvironmentResolver.cs |
| OpenSSL | 3.5.9 | Apache-2.0 | Python ssl/hashlib; PE version verified |
| SQLite (Python) | 3.50.4 | Public domain | Python sqlite3; PE version verified |
| libffi | 3.4.4 | MIT | Python ctypes; version from CPython v3.14.8 build specification |
| Expat (Python) | 2.8.5 | MIT | Version reported by bundled pyexpat.EXPAT_VERSION |
| libmpdec | 4.0.0 | BSD-2-Clause | Version reported by bundled _decimal.__libmpdec_version__ |
| bzip2 | 1.0.8 | bzip2 license | CPython build specification; notices in Python LICENSE.txt |
| XZ/liblzma | 5.2.5 | Public-domain core plus component GPL/LGPL notices | CPython build specification; determine linked portions before assuming every source-license applies |
| zlib-ng | 2.2.4 | Zlib | CPython build specification |
| zstd (Python) | 1.5.7 | BSD-3-Clause OR GPL-2.0-only | CPython build specification |
| MSVC runtime | 14.51.36247.0 | Microsoft redistributable terms, not MIT | Bundled by official Python ZIP; PE version verified |
| pip | 26.2.1 | MIT plus vendor licenses | build/acquire-pip.ps1; PythonEnvironments.cs |
| ReportGenerator | 5.5.11 | Apache-2.0 | build/coverage.ps1; development only |
| Doxygen | 1.18.0 | GPL-2.0; generated documents exempt per upstream | build/doxygen.ps1; development/CI only; not bundled |
| ReportLab | 4.4.9 | BSD-3-Clause | build/create-complete-manual.py; documentation only |
| Pillow (CI documentation) | 12.3.0 in audited Actions run | HPND/Pillow license plus codec notices | ReportLab dependency; version from run 38297143493 |
| charset-normalizer (CI documentation) | 3.5.2 in audited Actions run | MIT | ReportLab dependency; version from run 38297143493 |
| Python (CI documentation) | 3.12.x | PSF | actions/setup-python; exact patch depends on runner |
| .NET SDK (local) | 10.0.402 | MIT and third-party/MS build tool terms | dotnet build/test/publish; not itself shipped |
| .NET SDK (CI) | 10.0.402 | MIT and third-party/MS build tool terms | actions/setup-dotnet; SDK pinned via global.json and workflow |
| Windows SDK MakeAppx/SignTool | Externally installed; chosen SDK directory | Microsoft SDK terms | build/msix.ps1; build only |
| PowerShell | 7.x for builds; Windows PowerShell external for console | MIT for PowerShell 7; OS terms for Windows PowerShell | build scripts; PythonEnvironments.cs |
| Inno Setup | 6.x, exact version not pinned | Inno Setup license | build/publish.ps1 -Installer; not used by MSIX workflow |
| GitHub CLI | 2.102.0 local; runner version external | MIT and Go dependency notices | setup-github.ps1 and GitHub release job; not shipped |
| Git | External version | GPL-2.0 | Source/wikis upload; not shipped |
| Paramiko | Optional, unpinned | LGPL-2.1-or-later; dependencies separate | tests/Fixtures/ssh_server.py; not shipped |
| Segoe UI / Consolas | Windows-provided, exact installed font version | Microsoft document embedding permissions | PDF font subsets; no standalone TTF shipped |
| pefile | 2024.8.26 | MIT | Audit-only PE parser; not shipped |
| PyYAML | 6.0.3 | MIT | Audit/setup-only workflow syntax checker; not shipped |
| actions/checkout | v4 / 11d5960a326750d5838078e36cf38b85af677262 | MIT | Both GitHub workflows; not redistributed |
| actions/setup-dotnet | v4 / 67a3573c9a986a3f9c594539f4ab511d57bb3ce9 | MIT | MSIX workflow; not redistributed |
| actions/setup-python | v5 / a26af69be951a213d495a4c3e4e4022e16d87065 | MIT | Both GitHub workflows; not redistributed |
| actions/upload-artifact | v4 / ea165f8d65b6e75b540449e92b4886f43607fa02 | MIT | Both GitHub workflows; not redistributed |

## pip-Unterpakete

| Paket | Version | Lizenz |
| --- | --- | --- |
| CacheControl | 0.14.4 | Apache-2.0 |
| distlib | 0.4.2 | Python-2.0 |
| distro | 1.9.0 | Apache-2.0 |
| msgpack | 1.1.2 | Apache-2.0 |
| packaging | 26.2 | Apache-2.0 OR BSD-2-Clause |
| platformdirs | 4.10.0 | MIT |
| pyproject-hooks | 1.2.0 | MIT |
| requests | 2.34.2 | Apache-2.0 |
| certifi | 2026.6.17 | MPL-2.0 |
| idna | 3.18 | BSD-3-Clause |
| urllib3 | 2.7.0 | MIT |
| rich | 14.2.0 | MIT |
| pygments | 2.20.0 | BSD-2-Clause |
| resolvelib | 1.2.1 | ISC |
| setuptools | 70.3.0 | MIT |
| tomli | 2.4.1 | MIT |
| tomli-w | 1.2.0 | MIT |
| truststore | 0.10.4 | MIT |

## Externe, nicht gebündelte Treiber

| Komponente | Datei | Version | Quellverwendung |
| --- | --- | --- | --- |
| NI-DAQmx | nicaiu.dll | Externally installed; not pinned and not bundled | src/Testumgebung.Instruments/Daqmx.cs |
| VISA | visa64.dll / nivisa64 / ktvisa64 | Externally installed; not pinned and not bundled | src/Testumgebung.Instruments/Io.cs |
| PEAK PCAN | PCANBasic.dll | Externally installed; not pinned and not bundled | src/Testumgebung.Instruments/Communication/Can.cs |
| Kvaser CAN | canlib32.dll | Externally installed; not pinned and not bundled | src/Testumgebung.Instruments/Communication/Can.cs |
| Vector CAN | vxlapi64.dll | Externally installed; not pinned and not bundled | src/Testumgebung.Instruments/Communication/Can.cs |
| PEAK LIN | PLinApi.dll | Externally installed; not pinned and not bundled | src/Testumgebung.Instruments/Communication/Lin.cs |
| LabVIEW VI DLL | User-selected .dll | Externally installed; not pinned and not bundled | src/Testumgebung.Workers/LabViewGlue.cs |
| User .NET DLLs | User-imported .dll | Externally installed; not pinned and not bundled | src/Testumgebung.Workers.CSharp/Program.cs |
| System Win32 | kernel32.dll and OS libraries | Externally installed; not pinned and not bundled | src/Testumgebung.Instruments/Communication/Transports.cs |

## Genauigkeit und Grenzen

Numerische Versionen werden nur genannt, wenn sie im Paket, im Quellarchiv, in einer PE-Versionsressource oder im versionsgenauen Buildskript belegt sind. `UNRESOLVED` bedeutet, dass kein belastbarer Versionsnachweis vorliegt. Für extern installierte Software lässt sich keine Version aus dem Projekt erzwingen.

Manche EXEs sind Alias-Launcher ohne eigene Versionsressource. Bei GNU-Utilities, Headern und statischen Bibliotheken beruht die Komponentenzuordnung auf dem unveränderten Kit und seinen Quellen; solche Zuordnungen sind in `attribution_evidence` als abgeleitet markiert. Die genaue Unterlizenz wird durch die Originaltexte und Quellkopfzeilen bestimmt, nicht durch den allgemeinen Kit-Namen.

Historische lokale Bundles 1.0.0.0 bis 1.0.12.0 wurden hinsichtlich ihrer vorhandenen Versionen erfasst, aber nicht alle erneut bis zur letzten Datei inventarisiert. Die genaue Dateiliste gilt für den oben genannten inventarisierten MSIX-Stand. Frühere Veröffentlichungen erhalten fehlende Lizenztexte nicht rückwirkend.
