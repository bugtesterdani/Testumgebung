# Apache Subversion 1.15.0 – ausschließlich Integrationstests

- Anbieter des Windows-x64-Binärpakets: VisualSVN, https://www.visualsvn.com/downloads/
- Archiv: https://www.visualsvn.com/files/Apache-Subversion-1.15.0-x64.zip
- SHA-256: `98bb798dd6c70cc875b9bdacd600594351cf88f0a39a831a5055df061c871bb5`
- Verwendung: `build/prepare-svn-tests.ps1`, `build/coverage.ps1`, `ProgramSourcesTests.Svn_exports_remote_revision_and_preserves_uuid_and_historical_content`.
- Apache Subversion, APR/APR-util und Serf: Apache License 2.0, einschließlich der jeweiligen Original-NOTICE-Texte.
- Weitere Bestandteile: Expat, OpenSSL, SQLite und zlib; alle vom Anbieter mitgelieferten Lizenz-/Hinweisdateien befinden sich unverändert in diesem Verzeichnis.

Download, entpackte Werkzeuge und Test-Repositories liegen ausschließlich in ignorierten Test-/Cacheordnern. Weder SVN-EXE/DLLs noch das Werkzeugarchiv werden mit dem MSIX oder als Release-Download weitergegeben. Diese Vorbereitung ersetzt nicht die separat eingerichteten Git-/SVN-Clients für die Programmentwicklung. Für die produktive Software werden keine zusätzlichen NuGet-Abhängigkeiten eingeführt.
