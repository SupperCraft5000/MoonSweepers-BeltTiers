# Belt Tiers

BepInEx-6-IL2CPP-Mod für **Moon Sweepers**: Förderbänder, Splitter, Merger und Kapselfänger in den Stufen Mk1–Mk4, mit Abgleich im Multiplayer.

> Status: Beta. Einzelspieler ist getestet, Koop bisher nur anhand des Spielcodes geprüft (siehe [Koop](#koop-nur-theoretisch-geprüft)).

## Installation
1. [BepInEx 6.0.0-be.791](https://builds.bepinex.dev/projects/bepinex_be) (Unity.IL2CPP win-x64) ins Spielverzeichnis entpacken.
2. Den [MoonSweepers BepInEx Patcher](https://www.nexusmods.com/moonsweepers/mods/9) installieren. Ohne ihn kann BepInEx die Metadaten des Spiels nicht lesen.
3. Spiel einmal starten, damit BepInEx die Interop-Dateien erzeugt, dann beenden.
4. `BeltTiers.dll` aus den Releases nach `<Spiel>\BepInEx\plugins\BeltTiers\` kopieren.

Im Koop brauchen alle Spieler den Mod in derselben Version.

## Bauen
Voraussetzungen: .NET 8 SDK und ein Spielordner, in dem BepInEx schon einmal gelaufen ist (die Interop-DLLs aus `BepInEx\interop` werden referenziert und nicht mitgeliefert).

```
dotnet build -c Release
```
Der Build kopiert `BeltTiers.dll` nach `<Spiel>\BepInEx\plugins\BeltTiers\`. Standard ist der Steam-Ordner unter `C:\Program Files (x86)`. Einen anderen Spielordner trägt man in `GamePath.props` ein (Vorlage: `GamePath.props.example`, wird nicht eingecheckt) oder gibt ihn mit `-p:GamePath="..."` an.

## Bedienung
- **Stufe wählen:** Im Bau-Menü (Rechtsklick mit dem Hammer) liegen auf der Förderband-Karte die Knöpfe MK1–MK4. Preisschild, Detailansicht und Bauhinweis („MK3 · … BAND SETZEN $…“) zeigen den Preis der gewählten Stufe.
- **Bestehendes Band aufrüsten:** Band anvisieren und **Einfg** drücken. Es wird auf die im Menü gewählte Stufe gesetzt und kostet den Preisunterschied für seine Länge. Abstufen ist kostenlos, ohne Erstattung.
- **Splitter/Merger:** Auch ihre Karten im Bau-Menü haben MK1–MK4-Knöpfe. Gebaut wird mit Aufpreis wie ein Band von `MachineBeltMetres` Metern (Standard 4 m, Mk4 also +48 $). Bestehende anvisieren und **Einfg** drücken rüstet auf. Beim Abriss wird der Aufpreis anteilig wie im Spiel erstattet.
- **Fänger:** Auch die Fänger-Karte hat MK1–MK4-Knöpfe, bestehende Fänger rüstet Einfg auf. Aufpreis wie ein Band von `CatcherBeltMetres` Metern (Standard 10 m, Mk4 also +120 $). Je Stufe:

  | Stufe | Landung alle | Speicher |
  |---|---|---|
  | Mk1 | 15 s | 300 kg |
  | Mk2 | 8 s | 450 kg |
  | Mk3 | 4 s | 600 kg |
  | Mk4 | 1 s | 750 kg |

  Intervall je Stufe unter `[Catchers]` Mk1–Mk4. Vor jedem Start von J.O.O.N.A.S. lenkt die Mod die Kapsel auf den Fänger in der Nähe um, der am frühesten frei ist (`CatcherChoice.cs`), bei Gleichstand auf die höhere Stufe. Mit F10 steht jede Umlenkung im Log. Speicher = 100 kg × `OutputBufferMultiplier` + `CatcherStorePerTierKg` (150) je Stufe über Mk1.
- **Mit dem Bauwerkzeug aufrüsten:** Im Band-Modus **Strg** halten: Das Band, der Splitter, Merger oder Fänger im Ziel leuchtet grün (orange, wenn er die gewählte Stufe schon hat). **Strg + Linksklick** setzt ihn auf die im Menü gewählte Stufe, zum gleichen Preis wie mit Einfg. Ein normaler Klick setzt wie gewohnt ein neues Band an.
- **Abriss:** Erstattet wird der Preis der Stufe des Bands (75 %, wie im Spiel).

| Taste | Funktion |
|---|---|
| Einfg | Anvisiertes Band, Splitter, Merger oder Fänger auf die gewählte Stufe aufrüsten |
| Strg + Linksklick | Dasselbe mit dem Bauwerkzeug im Band-Modus (Ziel leuchtet bei gehaltener Strg-Taste grün) |
| F9 | Diagnose: Warteschlange des anvisierten Bands, ohne Ziel alle Bänder |
| F10 | Messprotokoll an/aus: Position, Bandgeschwindigkeit, Ausgaben der Maschinen |

Oben rechts stehen Stufe und Geschwindigkeit des anvisierten Bands, Splitters, Mergers oder Fängers. Aufgerüstete Bänder sind eingefärbt: Mk2 grün, Mk3 blau, Mk4 orange.

## Konfiguration
`BepInEx\config\moonsweepers.belttiers.cfg`:
- `[Speeds]` Mk1–Mk4: Geschwindigkeitsfaktor (Standard 1 / 2 / 3,5 / 5 × 1,1 m/s)
- `[Costs]` Mk1–Mk4: Preisfaktor (Standard 1 / 4 / 10 / 25 × 0,50 $/m)
- `BoostCargo`, `BoostMachines`, `OutputBufferMultiplier` (Standard 3), `CatcherStorePerTierKg` (Standard 150)
- `[Catchers]` Mk1–Mk4: Sekunden zwischen zwei Kapsel-Landungen (Standard 15 / 8 / 4 / 1)
- `[Costs] MachineBeltMetres` / `CatcherBeltMetres`: Aufpreis von Splittern/Mergern bzw. Fängern in Bandmetern
- `ShowTierColors`: Bänder nach Stufe einfärben
- `[Layout] PickerY`: Höhe der MK-Knöpfe auf der Karte

## Funktionsweise
- **Stufen:** Eine Tabelle ordnet jeder Band-ID eine Stufe zu, Splitter und Merger stehen darin mit negativem Schlüssel (−ID − 1). Sie liegt als `belttiers.txt` im Ordner des Spielstands. Bänder ohne Eintrag sind Mk1, deshalb laden auch Spielstände ohne Mod normal.
- **Geschwindigkeit:** Die Laufgeschwindigkeit auf dem Band wird in `MoonWorld.BeltSurfaceVelocity` skaliert. Für die Fracht bekommt ein aufgewertetes Band vor jedem `MoonWorld.StepConveyors` zusätzliche Strecke in `MoonWorld.beltOwed` gutgeschrieben. Die Kapseln bewegt dann das Spiel selbst, mit seinem eigenen Abstand und seiner eigenen Übergabe ans nächste Band.
- **Wichtig:** Die Positionen in `beltQueues` sind nur eine abgeleitete Kopie; die echte Position steht in `riders`. Den Interop-Indexer für `List<(float,int)>` nicht verwenden, er liefert Datenmüll. `BeltQueue.cs` liest die Liste direkt aus dem Speicher.
- **Maschinen:** Fänger, Garagen-Silos, Verpacker, Produktionsmaschinen und Robot-Docks geben im Takt von 0,35 s aus. Nach einem Ausgabeversuch auf ein aufgewertetes Band wird die Wartezeit durch den Geschwindigkeitsfaktor geteilt (Mk4: 0,07 s). Vorrat und Platz am Bandanfang prüft weiter das Spiel.
- **Ausgabepuffer:** Ausgabefach von Druckkammer und Steinpresse, Speicher der Robot-Docks und Kapselfänger × `OutputBufferMultiplier`. Der Verpacker bleibt bei 100 kg (Konstante im Maschinencode).
- **Fänger:** Die 100-kg-Grenze ist an mehreren Stellen fest einkompiliert (Fängerwahl, `CatchersAllFull`, Füllanzeige, Infotafel). Alle lesen `CatcherKilograms`, das deshalb den Füllstand geteilt durch `OutputBufferMultiplier` meldet; die Infotafel rechnet ihre kg-Texte zurück. Jede Landung belegt einen Fänger 9 s (`ReserveCatch`). Die Mod setzt stattdessen das Intervall der Fänger-Stufe. Mit F10 steht pro Fänger im Log, wie viele Landungen gebucht wurden und ob Kapseln warten mussten (Fänger zu langsam) oder nicht (zu wenig Nachschub).
- **Splitter/Merger:** Maschinen mit eigenem Band (`MoonMachineBelt`). Ihre Fracht läuft über negative Routen-IDs (`MoonWorld.MachineRoute`) durch dieselben `beltOwed`-Einträge und wird genauso beschleunigt.
- **Maschinencode lesen:** Beim ersten Start schreibt das Plugin die echten Adressen aller Spielmethoden nach `BepInEx\belttiers-rva.tsv`. Die Adressen im Dummy-Dump stimmen nicht.
- **Reststrecke:** Die Extra-Strecke bekommen nur Bänder, die gerade Fracht tragen, und höchstens bis 0,3 m offene Strecke. Vorher sammelte sie sich auf leeren oder gestauten Bändern an, und die nächste Kapsel sprang dann mehrere Meter (sichtbar als Teleportieren in Band-Schleifen).
- **Farben:** Der Bandkörper aufgerüsteter Bänder bekommt eine eingefärbte Kopie des Spielmaterials: Mk2 grün, Mk3 blau, Mk4 orange (`ShowTierColors`). Die laufenden Lamellen zeichnet das Spiel für alle Bänder gemeinsam, die bleiben unverändert.
- **Robot-Docks außer Sichtweite:** Das Spiel lässt sie pro Durchlauf bis zu 6 Kapseln aufs Band legen, solange der Bandanfang frei ist. Sie laufen also schon mit Bandgeschwindigkeit. Ihr Speicher (`FarRobotSpec.bin`) wird mit `OutputBufferMultiplier` vergrößert.
- **Multiplayer:** Eigene Nachrichten mit der Typnummer 200. Der Host verschickt die ganze Tabelle bei Anfrage und alle 15 s. Einzelne Änderungen gehen sofort an die Crew.
- **Trennen/Verschieben:** Wird ein Band geteilt oder verschoben, übernehmen die neuen Teilstücke die Stufe.

## Koop (nur theoretisch geprüft)
Mit zwei Spielern ist der Mod noch nicht getestet. Aus dem Spielcode:
- **IDs:** Jeder Spieler baut aus eigenen ID-Blöcken (`MoonWorld.UseIdBlock`). Band- und Maschinen-IDs sind deshalb auf allen Rechnern gleich, und die Stufentabelle passt überall.
- **Stufen:** Wer baut oder aufrüstet, schickt die Änderung an den Host. Der Host speichert sie in `belttiers.txt` und verteilt sie, die ganze Tabelle zusätzlich alle 15 s. Clients fragen nach dem Laden alle 3 s, bis sie die Tabelle haben.
- **Geld:** Aufpreise laufen über `MoonWorld.ChargeBuild`, dieselbe Funktion wie beim normalen Bauen. Das Spiel gleicht sie selbst mit dem Host ab.
- **Simulation:** Fänger, J.O.O.N.A.S. und die Docks außer Sichtweite rechnet der Host. Fracht auf Bändern rechnen alle mit denselben Stufen.
- **Risiko:** Ein Spieler ohne Mod bekommt Nachrichten vom Typ 200, die sein Spiel nicht kennt, und sieht andere Bandgeschwindigkeiten. Deshalb müssen alle den Mod haben.
- **Woran man Probleme erkennt:** Im Log „Crew message header“ beim Laden, und auf dem Client nach dem Beitritt Stufen und Farben wie beim Host.

## Offene Punkte
- Koop-Test mit zwei Spielern.
- Ein Mk4-Fänger nimmt bis ~17 kg/s an, ein Mk4-Band trägt nur ~7 kg/s, weil jede Kapsel auf dem Band 1 kg hat. Der Überschuss bleibt im Fänger.

## Lizenz
Belt Tiers steht unter der **GNU General Public License v3.0 oder später** (`GPL-3.0-or-later`), siehe [LICENSE](LICENSE).

Kurz: Jeder darf den Mod nutzen, ändern und weitergeben, auch als Fork. Wer eine geänderte Fassung veröffentlicht oder weitergibt, muss sie ebenfalls unter der GPL und mit Quellcode freigeben. Eine Closed-Source-Version ist damit nicht erlaubt.

Moon Sweepers gehört DragDropGames. Dieses Projekt enthält keine Dateien des Spiels und ist kein offizielles Produkt.
