# Engine-Funktionsumfang

Jeder Treiber gibt an, was seine Engine unterstützt — die Oberfläche blendet den Rest aus. Was ein
Treiber angibt, muss er auch können: ein Test prüft, dass alles als nicht unterstützt Gemeldete
eine Ausnahme wirft, statt still nichts zu tun.

| Capability | PostgreSQL | MySQL | SQL Server | SQLite | Oracle | DuckDB | ClickHouse | MongoDB | Redis | Storage | OData |
|---|---|---|---|---|---|---|---|---|---|---|---|
| SQL | ja | ja | ja | ja | ja | ja | ja | — | — | ja | — |
| Als Zeilen durchsehen | ja | ja | ja | ja | ja | ja | ja | ja | ja | ja | ja |
| Container als Zeilen durchsehen | — | — | — | — | — | — | — | — | ja | — | — |
| Werte einer Spalte zählen | ja | ja | ja | ja | ja | ja | ja | — | — | ja | — |
| Schemas | ja | — | ja | — | ja | ja | ja | — | — | — | — |
| Several databases | ja | ja | ja | — | — | — | ja | ja | ja | — | — |
| Transactions | ja | ja | ja | ja | ja | ja | — | — | — | — | — |
| DDL | ja | ja | ja | ja | ja | ja | ja | — | — | — | — |
| Views | ja | ja | ja | ja | ja | ja | ja | — | — | — | — |
| Materialised views | ja | — | — | — | ja | — | ja | — | — | — | — |
| Stored procedures | ja | ja | ja | — | ja | — | — | — | — | — | — |
| Triggers | ja | ja | ja | ja | ja | — | — | — | — | — | — |
| Sequences | ja | — | ja | — | ja | ja | — | — | — | — | — |
| Foreign keys | ja | ja | ja | ja | ja | ja | — | — | — | — | — |
| Partial indexes | ja | — | ja | ja | — | — | — | — | — | — | — |
| Include columns | ja | — | ja | — | — | — | — | — | — | — | — |
| Estimated plan | ja | ja | ja | ja | ja | ja | ja | ja | — | ja | — |
| Actual plan | ja | ja | ja | — | — | ja | — | ja | — | ja | — |
| Backup | ja | ja | ja | ja | — | — | — | ja | ja | — | — |
| Restore | ja | ja | — | — | — | — | — | ja | — | — | — |
| User management | ja | ja | ja | — | ja | — | — | — | — | — | — |
| Session list | ja | ja | ja | — | ja | — | ja | ja | ja | — | — |
| Kill session | ja | ja | ja | — | ja | — | ja | ja | ja | — | — |
| Server metrics | ja | ja | ja | — | ja | — | ja | ja | ja | — | — |
| Slow queries | ja | ja | ja | — | — | — | — | — | — | — | — |
| Geplante Jobs | ja | ja | ja | — | — | — | — | — | — | — | — |
| Maintenance commands | ja | ja | ja | ja | ja | ja | ja | ja | ja | — | — |

MongoDB, Redis und OData sind keine SQL-Engines: ihre Abfrage-Tabs nehmen die Befehle der
jeweiligen Engine, und Dokument-Ergebnisse erscheinen als JSON-Baum mit Tabellenansicht für flache
Dokumente.

Eine OData-Verbindung ist die URL der Service-Root, mit `user:pw@` für Basic-Authentifizierung oder
`bearer:<token>@` für ein Bearer-Token. Ein Dienst, der ein Session-Cookie oder einen API-Key will,
bekommt ihn als weitere Zeile unter der URL: `Cookie: name=value` oder `X-Api-Key: value` — die
Anfrage stellt der Server des Studios, die Cookies eines Browsers erreichen den Dienst also nie. Der
Explorer listet die Entity Sets aus `$metadata`, und der Abfrage-Tab nimmt einen Ressourcenpfad mit
Query-Optionen, etwa `Products?$filter=UnitPrice gt 20&$orderby=ProductName&$top=50`. Der Daten-Tab
blättert, sortiert und filtert über dieselben Optionen, die Arbeit macht also der Dienst. Der
Treiber folgt den Next-Links des Dienstes bis zum Zeilenlimit und liest ausschließlich. Die
Optionen schreibt dir der [Abfrage-Baukasten](query-builder.md#odata-dienste).

Eines vorher wissen, wenn andere Leute drankommen: eine OData-Verbindung lässt den *Server* eine
URL abrufen, die jemand getippt hat. Auf einem Studio, das Fremde erreichen — siehe
[was Leute in diesem Studio dürfen](connections.md#ein-studio-zu-dem-jeder-seine-eigenen-daten-mitbringt) —
ist `WDS_CONNECT_HOSTS` das, was daraus keinen Weg in den Rest des Netzes macht; die Liste gilt für
einen OData-Dienst wie für jedes andere Ziel.

Durchsehen können sie trotzdem. Der Daten-Tab fragt den Treiber nach einer Seite, statt ein
`SELECT` zu bauen: eine MongoDB-Collection wird mit `find().sort().skip().limit()` gelesen — die
Filtersprache des Studios wird in die Abfrage übersetzt —, und eine Redis-Datenbank, ein
Präfix-Ordner oder ein einzelner Schlüssel wird als die Tabelle gelesen, die er ergibt. Zwei Dinge
folgen daraus, dass es kein SQL gibt: das Gitter darüber ist nur lesbar und nennt stattdessen den
Befehl, der schreibt; und das Zählen der Werte einer Spalte (die Häkchenliste im Spaltenmenü) wird
abgelehnt, denn das ist ein `GROUP BY`. Dann eben den Filter tippen.

„Container als Zeilen durchsehen“ ist der zweite Unterschied: bei jeder SQL-Engine ist ein Schema
ein Ordner und sonst nichts, während eine Redis-Datenbank oder ein Schlüssel-Präfix selbst die
interessante Tabelle ist — ihre Schlüssel, deren Typ, deren Ablauf und deren Größe.

DuckDB und SQLite sind Dateien — also keine Sitzungen, keine Benutzer, keine zweite Datenbank zum
Umschalten. SQLite sichert sich selbst mit `VACUUM INTO`; ein Restore hieße, die Datei unter einer
offenen Verbindung zu ersetzen, und das macht die Anwendung bewusst nicht.

Storage ist ein Bucket und keine Datenbank — ein S3-kompatibler Endpunkt, Azure Blob Storage, Google
Cloud Storage oder ein Ordner. Es hat keine Schemas, keine Schlüssel und nichts, wogegen sich DDL
schreiben ließe: eine Datei wird gelesen und abgefragt, aber nie zeilenweise bearbeitet. Gelesen wird
über DuckDB, von dort kommt auch der Plan. Eine genannte Grenze: DuckDB erreicht Google Cloud Storage
über das S3-Protokoll, das HMAC-Schlüssel will — mit einem Service-Account allein funktionieren Baum,
Vorschau und Download, eine Abfrage nicht. Siehe [Objektspeicher](storage.md).
