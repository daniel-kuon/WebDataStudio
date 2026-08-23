# Daten durchsuchen

Ein Doppelklick auf eine Tabelle im Explorer öffnet ihre Zeilen in einem Daten-Tab. Der Tab ist
mehr als eine Seite Zeilen: Eine Abfrageleiste über dem Grid filtert, sortiert, joint und
gruppiert ohne SQL, und die Fremdschlüssel der Tabelle sind der Weg zu den Zeilen, auf die sie
zeigen — in beide Richtungen.

Alles, was die Abfrageleiste sagt, läuft **auf dem Server**: Eine Seite hält 200 von womöglich
Millionen Zeilen, im Browser zu filtern oder zu sortieren würde also auf der falschen Menge
arbeiten. Maskierung, schreibgeschützte Verbindungen und die Zeilenobergrenze gelten genauso wie
für ein von Hand geschriebenes Statement.

## Die Abfrageleiste

- **Filter** fügt eine Bedingung hinzu: Spalte, Operator (`contains`, `=`, `≠`, `>`, `≥`, `<`,
  `≤`, `starts with`, `ends with`, `is null`, `is not null`) und Wert. Mehrere Filter gelten
  gemeinsam. Der Schnellfilter im Spaltenmenü ist eine Abkürzung für einen `contains`-Filter.
- **Sort** fügt eine Sortierspalte hinzu; mehrere sortieren nach der ersten, dann der zweiten.
  *Sort ascending / descending* im Spaltenmenü ersetzt die Sortierung, *Add to sort* hängt an.
- **Join** folgt den eigenen Fremdschlüsseln der Tabelle: Schlüssel wählen, und die Spalten der
  referenzierten Tabelle erscheinen im selben Grid, mit dem Namen der referenzierten Tabelle als
  Präfix (`customers.name`). Ein Join ist immer ein LEFT JOIN — eine Zeile mit leerem Schlüssel
  bleibt eine Zeile. Gejoint wird zum Lesen; eine gejointe Ansicht ist nicht editierbar, und der
  Tab sagt das auch.
- **Group** gruppiert nach einer oder mehreren Spalten — eigenen oder gejointen — mit Aggregaten
  (`count`, `sum`, `avg`, `min`, `max`). Eine gruppierte Ansicht zeigt die Gruppenspalten und die
  Aggregate, lässt sich nach beidem sortieren, und Gruppieren ohne Aggregat zeigt die Zeilenzahl
  jeder Gruppe.

Jedes aktive Stück ist ein Chip, der mit einem Klick wieder abgeht; das Grid zeigt immer genau
das, was die Chips sagen. Eine maskierte Spalte bleibt hinter einem Join maskiert, unter beiden
Namen.

## Einem Fremdschlüssel folgen

Eine Fremdschlüsselspalte trägt einen Pfeil; der Pfeil an einer Zelle führt zur referenzierten
Zeile. Wo das landet, ist einstellbar — der Routen-Knopf in der Werkzeugleiste schaltet um, für
alle Daten-Tabs zugleich, und die Wahl wird mit dem Workspace gespeichert:

| Modus | Was sich öffnet |
|---|---|
| als Query-Tab | ein Query-Tab mit dem `SELECT` — das bisherige Verhalten und der Standard |
| in einem neuen Daten-Tab | die referenzierte Tabelle als Daten-Tab, gefiltert auf die referenzierte Zeile |
| in einer geteilten Ansicht | derselbe gefilterte Daten-Tab, als Split **neben** dem Tab, aus dem gefolgt wurde |

Im Split-Modus öffnet eine Kette von Sprüngen — Bestellung → Kunde → Land — jedes Mal einen
weiteren Split, sodass der genommene Weg von links nach rechts auf dem Bildschirm bleibt.

## Referenzierende Zeilen: die andere Richtung

Die eigenen Schlüssel einer Tabelle sagen, wohin ihre Zeilen zeigen. Der Daten-Tab kennt auch die
Gegenrichtung: Referenzieren andere Tabellen diese, bekommt jede Zeile einen Aufklapper, und eine
von anderswo referenzierte Spalte trägt eine Rücksprung-Markierung im Kopf.

- Ein eingehender Schlüssel: Der Aufklapper zeigt dessen referenzierende Zeilen inline unter der
  Zeile — die Bestellungen dieses Kunden, unter dem Kunden.
- Mehrere eingehende Schlüssel: Der Aufklapper fragt, welche — jeder einzeln, oder **Expand all**
  für alle auf einmal.
- Jede Erweiterung ist eine Vorschau von bis zu 50 Zeilen; ihr Öffnen-Knopf öffnet die
  referenzierende Tabelle gefiltert auf genau diese Zeilen, im selben Modus wie ein verfolgter
  Schlüssel.
