# Vollständiges Abhängigkeitsinventar

Prüfstand: 2026-11-03. Binärstand: MSIX 1.2.1.0. Die Version einer NuGet-Komponente kann von ihrer Assembly-/Dateiversion abweichen. Tabellen unterscheiden deshalb Paket-, Produkt- und Dateiversionen.

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

Die CSV-Dateien verwenden UTF-8 mit BOM und lassen sich in Excel importieren. `binaries.csv` enthält auch 945 statische `.a`-Bibliotheken; die 2687 Zeilen sind keine 2.104 unabhängigen Produkte. Mehrfach kopierte .NET-Worker-Dateien und Kit-Aliasprogramme sind absichtlich enthalten.

## Eigene Projekte

| Projekt | Framework | Version im Prüfstand | Aufgabe |
| --- | --- | --- | --- |
| src/Testumgebung.Cli/Testumgebung.Cli.csproj | net10.0 | 1.2.1.0 (separate CLI ZIP; not in audited MSIX) | Standalone command-line project validation, code compilation and scenario simulation (separate ZIP) |
| src/Testumgebung.Contracts/Testumgebung.Contracts.csproj | net10.0 | 1.2.1.0 in audited MSIX | Gemeinsame Verträge und Grundtypen |
| src/Testumgebung.DatabaseAdapters/Testumgebung.DatabaseAdapters.csproj | net10.0 | 1.2.1.0 in audited MSIX | Datenbank-Verbindungen |
| src/Testumgebung.Desktop/Testumgebung.Desktop.csproj | net10.0-windows | 1.2.1.0 in audited MSIX | WPF-Oberfläche, Entra/MSAL, Datenbankprovider und OPC UA |
| src/Testumgebung.Domain/Testumgebung.Domain.csproj | net10.0 | 1.2.1.0 in audited MSIX | Prüfprojekt-, Variablen- und Ergebnismodell |
| src/Testumgebung.Engine/Testumgebung.Engine.csproj | net10.0 | 1.2.1.0 in audited MSIX | Ablaufausführung, Bausteine und Ressourcen-Sperren |
| src/Testumgebung.Hardware/Testumgebung.Hardware.csproj | net10.0 | 1.2.1.0 in audited MSIX | Hardware-Abstraktionen und Simulation |
| src/Testumgebung.Identity/Testumgebung.Identity.csproj | net10.0 | 1.2.1.0 in audited MSIX | Anmeldung und Berechtigungsverträge |
| src/Testumgebung.Instruments/Testumgebung.Instruments.csproj | net10.0 | 1.2.1.0 in audited MSIX | Gerätetreiber, SSH/SFTP, Busse und Netzwerk |
| src/Testumgebung.Notifications/Testumgebung.Notifications.csproj | net10.0 | 1.2.1.0 in audited MSIX | Meldewege und Benachrichtigungen |
| src/Testumgebung.Persistence/Testumgebung.Persistence.csproj | net10.0 | 1.2.1.0 in audited MSIX | Projekt-, Ergebnis-, Einstellungs- und Release-Speicherung |
| src/Testumgebung.Presentation/Testumgebung.Presentation.csproj | net10.0 | 1.2.1.0 in audited MSIX | Ansichtsmodelle, Editor und Werkersteuerung |
| src/Testumgebung.Workers/Testumgebung.Workers.csproj | net10.0 | 1.2.1.0 in audited MSIX | C#/Python/C++-Prozesssteuerung, Laufzeitwahl und GDB |
| src/Testumgebung.Workers.CSharp/Testumgebung.Workers.CSharp.csproj | net10.0 | 1.2.1.0 in audited MSIX | Isolierter Roslyn-C#-Compiler/Worker |
| src/Testumgebung.Workers.Native/Testumgebung.Workers.Native.csproj | net10.0 | 1.2.1.0 in audited MSIX | C++-Compileraufruf und native Ausführung |
| tests/Desktop.UiTests/Desktop.UiTests.csproj | net10.0-windows | Default assembly 1.0.0; not shipped | Automated tests |
| tests/Engine.Tests/Engine.Tests.csproj | net10.0 | Default assembly 1.0.0; not shipped | Automated tests |
| tests/Fixtures/FakeCompiler/FakeCompiler.csproj | net10.0 | Default assembly 1.0.0; not shipped | Test-only fixture |
| tests/Fixtures/FakeGdb/FakeGdb.csproj | net10.0 | Default assembly 1.0.0; not shipped | Test-only fixture |
| tests/Fixtures/OrderHelper/OrderHelper.csproj | net10.0 | Default assembly 1.0.0; not shipped | Test-only fixture |
| tests/Fixtures/OrderLibrary/OrderLibrary.csproj | net10.0 | Default assembly 1.0.0; not shipped | Test-only fixture |
| tests/Fixtures/ProcessProbe/ProcessProbe.csproj | net10.0 | Default assembly 1.0.0; not shipped | Test-only fixture |

Die vollständigen Projektverweise und umgekehrten Verwendungsorte stehen in `projects.csv`; importierte Worker-Buildreferenzen sind dort ausdrücklich markiert.

## NuGet-Paketversionen

| Paket | Version | Lizenz | Verwendung im MSIX |
| --- | --- | --- | --- |
| BitFaster.Caching | 2.6.1 | MIT | . |
| BouncyCastle.Cryptography | 2.7.0 | MIT | . |
| coverlet.collector | 10.1.0 | MIT | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| Microsoft.Bcl.Cryptography | 9.0.18 | MIT | . |
| Microsoft.Bcl.TimeProvider | 10.0.12 | MIT | . |
| Microsoft.CodeAnalysis.Analyzers | 5.9.0-1.26328.17 | MIT | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| Microsoft.CodeAnalysis.Common | 5.9.0 | MIT | ., workers/csharp |
| Microsoft.CodeAnalysis.CSharp | 5.9.0 | MIT | ., workers/csharp |
| Microsoft.CodeCoverage | 18.11.0 | MIT | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| Microsoft.Data.SqlClient | 7.1.1 | MIT | . |
| Microsoft.Data.SqlClient.Extensions.Abstractions | 7.1.1 | MIT | . |
| Microsoft.Data.SqlClient.Internal.Logging | 7.1.1 | MIT | . |
| Microsoft.Data.SqlClient.SNI.runtime | 7.1.0 | Microsoft proprietary redistributable terms (LICENSE.txt) | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| Microsoft.Data.Sqlite | 10.0.14 | MIT | . |
| Microsoft.Data.Sqlite.Core | 10.0.14 | MIT | . |
| Microsoft.Extensions.AmbientMetadata.Application | 10.10.0 | MIT | . |
| Microsoft.Extensions.Caching.Abstractions | 9.0.18 | MIT | . |
| Microsoft.Extensions.Caching.Memory | 9.0.18 | MIT | . |
| Microsoft.Extensions.Compliance.Abstractions | 10.10.0 | MIT | . |
| Microsoft.Extensions.Configuration | 10.0.12 | MIT | . |
| Microsoft.Extensions.Configuration.Abstractions | 10.0.12 | MIT | . |
| Microsoft.Extensions.Configuration.Binder | 10.0.12 | MIT | . |
| Microsoft.Extensions.DependencyInjection | 10.0.12 | MIT | . |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.0 | MIT | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.12 | MIT | . |
| Microsoft.Extensions.DependencyInjection.Abstractions | 8.0.2 | MIT | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| Microsoft.Extensions.DependencyInjection.AutoActivation | 10.10.0 | MIT | . |
| Microsoft.Extensions.Diagnostics | 10.0.12 | MIT | . |
| Microsoft.Extensions.Diagnostics.Abstractions | 10.0.12 | MIT | . |
| Microsoft.Extensions.Diagnostics.ExceptionSummarization | 10.10.0 | MIT | . |
| Microsoft.Extensions.FileProviders.Abstractions | 10.0.12 | MIT | . |
| Microsoft.Extensions.Hosting.Abstractions | 10.0.12 | MIT | . |
| Microsoft.Extensions.Http | 10.0.12 | MIT | . |
| Microsoft.Extensions.Http.Diagnostics | 10.10.0 | MIT | . |
| Microsoft.Extensions.Http.Resilience | 10.10.0 | MIT | . |
| Microsoft.Extensions.Logging | 10.0.12 | MIT | . |
| Microsoft.Extensions.Logging.Abstractions | 10.0.0 | MIT | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| Microsoft.Extensions.Logging.Abstractions | 10.0.12 | MIT | . |
| Microsoft.Extensions.Logging.Abstractions | 8.0.3 | MIT | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| Microsoft.Extensions.Logging.Configuration | 10.0.12 | MIT | . |
| Microsoft.Extensions.ObjectPool | 10.0.12 | MIT | . |
| Microsoft.Extensions.Options | 10.0.12 | MIT | . |
| Microsoft.Extensions.Options | 9.0.18 | MIT | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| Microsoft.Extensions.Options.ConfigurationExtensions | 10.0.12 | MIT | . |
| Microsoft.Extensions.Primitives | 10.0.12 | MIT | . |
| Microsoft.Extensions.Primitives | 9.0.18 | MIT | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| Microsoft.Extensions.Resilience | 10.10.0 | MIT | . |
| Microsoft.Extensions.Telemetry | 10.10.0 | MIT | . |
| Microsoft.Extensions.Telemetry.Abstractions | 10.10.0 | MIT | . |
| Microsoft.Identity.Client | 4.92.0 | MIT | . |
| Microsoft.IdentityModel.Abstractions | 8.16.0 | MIT | . |
| Microsoft.IdentityModel.JsonWebTokens | 8.16.0 | MIT | . |
| Microsoft.IdentityModel.Logging | 8.16.0 | MIT | . |
| Microsoft.IdentityModel.Protocols | 8.16.0 | MIT | . |
| Microsoft.IdentityModel.Protocols.OpenIdConnect | 8.16.0 | MIT | . |
| Microsoft.IdentityModel.Tokens | 8.16.0 | MIT | . |
| Microsoft.NET.Test.Sdk | 18.11.0 | MIT | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| Microsoft.SqlServer.Server | 1.0.0 | MIT | . |
| Microsoft.TestPlatform.ObjectModel | 18.11.0 | MIT | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| Microsoft.TestPlatform.TestHost | 18.11.0 | MIT | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| MySqlConnector | 2.6.2 | MIT | . |
| Npgsql | 10.0.3 | PostgreSQL | . |
| OPCFoundation.NetStandard.Opc.Ua.Client | 2.0.1 | MIT | . |
| OPCFoundation.NetStandard.Opc.Ua.Configuration | 2.0.1 | MIT | . |
| OPCFoundation.NetStandard.Opc.Ua.Core | 2.0.1 | MIT | . |
| OPCFoundation.NetStandard.Opc.Ua.Core.Schema | 2.0.1 | MIT | . |
| OPCFoundation.NetStandard.Opc.Ua.Core.Types | 2.0.1 | MIT | . |
| OPCFoundation.NetStandard.Opc.Ua.Security.Certificates | 2.0.1 | MIT | . |
| OPCFoundation.NetStandard.Opc.Ua.Types | 2.0.1 | MIT | . |
| Polly.Core | 8.4.2 | BSD-3-Clause | . |
| Polly.Extensions | 8.4.2 | BSD-3-Clause | . |
| Polly.RateLimiting | 8.4.2 | BSD-3-Clause | . |
| runtimepack.Microsoft.NETCore.App.Runtime.win-x64 | 10.0.14 | MIT plus Microsoft .NET Library / Windows SDK terms; see per-file attribution | ., ., ., workers/csharp, workers/native |
| runtimepack.Microsoft.WindowsDesktop.App.Runtime.win-x64 | 10.0.14 | MIT plus Microsoft .NET Library / Windows SDK terms; see per-file attribution | . |
| SQLitePCLRaw.bundle_e_sqlite3 | 2.1.13 | Apache-2.0 | . |
| SQLitePCLRaw.core | 2.1.13 | Apache-2.0 | . |
| SQLitePCLRaw.lib.e_sqlite3 | 2.1.13 | Apache-2.0 | . |
| SQLitePCLRaw.provider.e_sqlite3 | 2.1.13 | Apache-2.0 | . |
| SSH.NET | 2026.0.0 | MIT | . |
| System.Configuration.ConfigurationManager | 9.0.18 | MIT | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| System.Diagnostics.EventLog | 9.0.18 | MIT | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| System.IdentityModel.Tokens.Jwt | 8.16.0 | MIT | . |
| System.Security.Cryptography.Pkcs | 10.0.14 | MIT | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| System.Security.Cryptography.ProtectedData | 9.0.18 | MIT | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| System.Threading.RateLimiting | 10.0.12 | MIT | . |
| System.Threading.RateLimiting | 9.0.18 | MIT | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| xunit | 2.9.3 | Apache-2.0 | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| xunit.abstractions | 2.0.3 | Apache-2.0 (explicit frozen-abstractions upstream declaration; see UPSTREAM-LICENSE-DECLARATION.md) | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| xunit.analyzers | 1.18.0 | Apache-2.0 | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| xunit.assert | 2.9.3 | Apache-2.0 | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| xunit.core | 2.9.3 | Apache-2.0 | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| xunit.extensibility.core | 2.9.3 | Apache-2.0 | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| xunit.extensibility.execution | 2.9.3 | Apache-2.0 | Nicht im Release-Manifestsatz; nur Restore/Build/Test |
| xunit.runner.visualstudio | 4.0.1 | Apache-2.0 | Nicht im Release-Manifestsatz; nur Restore/Build/Test |

`nuget.csv` nennt zusätzlich direkte und transitive Verwendungsprojekte sowie konkrete Quelltextstellen für die direkt verwendeten APIs. Ein Restore-Eintrag allein bedeutet nicht, dass seine DLL im MSIX liegt. Die tatsächliche Zuordnung erfolgt über die Release-`.deps.json` und anschließend vorhandene Binärpfade.

## Werkzeuge und weitere Komponenten

| Komponente | Version | Lizenz/Status | Verwendung |
| --- | --- | --- | --- |
| w64devkit | 2.11.0 | Unlicense for kit glue; component licenses separately | build/acquire-toolchain.ps1; build/CodeWorkers.targets |
| GCC | 16.2.0 | GPL-3.0-or-later; runtime exception for eligible runtime use | src/Testumgebung.Workers.Native/Toolchain.cs |
| NSIS | 3.13 | zlib/libpng; bzip2 and CPL-1.0 compression modules; see COPYING | Kit installer compiler; corresponding sources included |
| aas-sign | 1.1.1 | Unlicense | Optional kit Authenticode utility; not used for MSIX signing |
| GDB | 18.1 | GPL-3.0-or-later | src/Testumgebung.Workers/GdbInspector.cs |
| GNU Binutils | 2.47 | GPL family, subcomponent licenses in source | GCC linker/assembler/toolchain |
| GNU Make | 4.4.1 | GPL-3.0-or-later | Shipped as part of full kit; not directly called by application |
| BusyBox-w32 | FRP-6354-g5f09fb446 | GPL-2.0-only with component exceptions | Full-kit utilities and launcher aliases |
| CMake | 4.4.4 | BSD-3-Clause plus bundled-library licenses | Shipped as part of full kit |
| Ninja | 1.13.2 | Apache-2.0 | Shipped as part of full kit |
| CCache | 4.14.1 | GPL-3.0-or-later | Full-kit compiler cache |
| Universal Ctags | 6.2.1 | GPL-2.0 family; see source notices | Shipped as part of full kit |
| Vim | 9.0 | Vim license | Full-kit editor and alias launchers |
| MinGW-w64 | 14.0.0 | Mixed permissive/public-domain/LGPL parts; see COPYING | Native compiler headers, import/static libraries, gendef/widl |
| GMP | 6.3.0 | LGPL-3.0-or-later OR GPL-2.0-or-later | Linked into GNU tools |
| MPFR | 4.2.2 | LGPL-3.0-or-later | Linked into GNU tools |
| MPC | 1.4.1 | LGPL-3.0-or-later | Linked into GNU tools |
| Expat (kit) | 2.9.0 | MIT | Linked into GDB |
| libiconv | 1.19 | LGPL-2.1-or-later library; GPL utility | Kit utility and GDB dependency |
| PDCurses | 3.9 | Public domain core; port-specific licenses | Windows-console support in Vim |
| xxHash | 0.8.3 | BSD-2-Clause library; GPL utility portions | Dependency of kit tools |
| Zstandard | 1.5.7 | BSD-3-Clause OR GPL-2.0-only | Kit tools and BusyBox dependency |
| dcmake | 1.7.1 | Unlicense plus bundled MIT/Zlib library notices | Shipped as part of full kit |
| 7-Zip SFX | 23.01 | LGPL and unRAR restriction; see source DOC/License.txt | Build-only archive extractor; not shipped as an EXE in MSIX |
| CPython | 3.15.2 | PSF and included third-party notices | src/Testumgebung.Workers/CodeRuntime.cs; PythonEnvironmentResolver.cs |
| OpenSSL | 3.5.9 | Apache-2.0 | Python ssl/hashlib; PE version verified |
| SQLite (Python) | 3.53.4 | Public domain | Python sqlite3; PE version verified |
| libffi | 3.4.4 | MIT | Python ctypes; version from CPython v3.15.2 build specification |
| Expat (Python) | 2.8.5 | MIT | Version reported by bundled pyexpat.EXPAT_VERSION |
| libmpdec | 5.1.0 | BSD-2-Clause | Version reported by bundled _decimal.__libmpdec_version__ |
| bzip2 | 1.0.8 | bzip2 license | CPython build specification; notices in Python LICENSE.txt |
| XZ/liblzma | 5.2.5 | Public-domain core plus component GPL/LGPL notices | CPython build specification; determine linked portions before assuming every source-license applies |
| zlib-ng | 2.2.4 | Zlib | CPython build specification |
| zstd (Python) | 1.5.7 | BSD-3-Clause OR GPL-2.0-only | CPython build specification |
| MSVC runtime | 14.51.36247.0 | Microsoft redistributable terms, not MIT | Bundled by official Python ZIP; PE version verified |
| pip | 26.3 | MIT plus vendor licenses | build/acquire-pip.ps1; PythonEnvironments.cs |
| ReportGenerator | 5.5.12 | Apache-2.0 | build/coverage.ps1; development only |
| Doxygen | 1.18.0 | GPL-2.0; generated documents exempt per upstream | build/doxygen.ps1; development/CI only; not bundled |
| ReportLab | 5.1.0 | BSD-3-Clause | build/create-complete-manual.py; documentation only |
| Pillow (CI documentation) | 13.0.0 in local dependency review | HPND/Pillow license plus codec notices | ReportLab 5.1.0 dependency; locally resolved on 2026-11-03 |
| charset-normalizer (CI documentation) | 3.5.2 in audited Actions run | MIT | ReportLab 5.1.0 dependency; locally resolved on 2026-11-03 |
| Python (CI documentation) | 3.15.2 | PSF | actions/setup-python; exact patch depends on runner |
| .NET SDK (local) | 10.0.403 | MIT and third-party/MS build tool terms | dotnet build/test/publish; not itself shipped |
| .NET SDK (CI) | 10.0.403 | MIT and third-party/MS build tool terms | actions/setup-dotnet; SDK pinned via global.json and workflow |
| Windows SDK MakeAppx/SignTool | Externally installed; chosen SDK directory | Microsoft SDK terms | build/msix.ps1; build only |
| PowerShell | 7.x for builds; Windows PowerShell external for console | MIT for PowerShell 7; OS terms for Windows PowerShell | build scripts; PythonEnvironments.cs |
| Inno Setup | 6.x, exact version not pinned | Inno Setup license | build/publish.ps1 -Installer; not used by MSIX workflow |
| GitHub CLI | 2.102.0 local; runner version external | MIT and Go dependency notices | setup-github.ps1 and GitHub release job; not shipped |
| Git | External version | GPL-2.0 | Source/wikis upload; not shipped |
| Paramiko | 5.1.0 in CI; local environment may differ | LGPL-2.1; dependencies separate | tests/Fixtures/ssh_server.py; not shipped |
| Segoe UI / Consolas | Windows-provided, exact installed font version | Microsoft document embedding permissions | PDF font subsets; no standalone TTF shipped |
| pefile | 2024.8.26 | MIT | Audit-only PE parser; not shipped |
| PyYAML | 6.0.3 | MIT | Audit/setup-only workflow syntax checker; not shipped |
| actions/checkout | v7.0.1 / 3d3c42e5aac5ba805825da76410c181273ba90b1 | MIT | Both GitHub workflows; not redistributed |
| actions/setup-dotnet | v6.1.0 / 0f4a354dd5547a28c70b7853dbd95d5b67886e73 | MIT | MSIX workflow; not redistributed |
| actions/setup-python | v7.1.0 / 6d4fd22b20465ccc0f7354476588e8df7c9ca0e0 | MIT | Both GitHub workflows; not redistributed |
| actions/upload-artifact | v7.0.2 / cf430e030ddbb5b0abf93d22962f4752f3646cd9 | MIT | Both GitHub workflows; not redistributed |

## pip-Unterpakete

| Paket | Version | Lizenz |
| --- | --- | --- |
| CacheControl | 0.14.4 | Apache-2.0 |
| distlib | 0.4.4 | Python-2.0 |
| distro | 1.9.0 | Apache-2.0 |
| msgpack | 1.2.4 | Apache-2.0 |
| packaging | 26.3 | Apache-2.0 OR BSD-2-Clause |
| platformdirs | 4.12.4 | MIT |
| pyproject-hooks | 1.3.3 | MIT |
| requests | 2.34.2 | Apache-2.0 |
| certifi | 2026.10.23 | MPL-2.0 |
| idna | 3.20 | BSD-3-Clause |
| urllib3 | 2.8.0 | MIT |
| rich | 15.0.0 | MIT |
| pygments | 2.21.0 | BSD-2-Clause |
| resolvelib | 1.2.1 | ISC |
| setuptools | 70.3.0 | MIT |
| tomli | 2.3.2 | MIT |
| tomli-w | 1.2.0 | MIT |
| truststore | 0.10.5 | MIT |

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


## SVN-Integrationstestclient 1.15.0

Der Qualitätslauf verwendet einen per SHA-256 gepinnten Windows-Testclient. Er wird nicht mit dem MSIX oder als Werkzeug-Download weitergegeben. Die Originalhinweise für Subversion und sämtliche enthaltenen Bibliotheken stehen unter [Herkunft und Lizenzen](../../licenses/third-party/svn-tests/1.15.0/NOTICE.md).

## Ergänzung Geräteintegration, 06.11.2026

Diese ergänzten externen Laufzeiten sind nicht Teil des zuvor vermessenen MSIX. Details: [Lizenzvorprüfung](labjack-redlab.md).

| Komponente | Datei | Version / Verteilung | Verwendung |
|---|---|---|---|
| LabJack LJM | LabJackM.dll | extern, nicht festgelegt, nicht mitgeliefert | src/Testumgebung.Instruments/Daq/LjmIoSession.cs |
| LabJack UD | LabJackUD.dll | extern, nicht festgelegt, nicht mitgeliefert | src/Testumgebung.Instruments/Daq/UdIoSession.cs |
| LabJack U12 | ljackuw.dll | extern, nicht festgelegt, nicht mitgeliefert | src/Testumgebung.Instruments/Daq/U12IoSession.cs |
| MCC Universal Library (RedLab) | cbw64.dll | extern, nicht festgelegt, nicht mitgeliefert | src/Testumgebung.Instruments/Daq/NativeUlApi.cs |
