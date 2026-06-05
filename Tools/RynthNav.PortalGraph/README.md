# RynthNav.PortalGraph

Offline tool that converts the GoArrow location database into a clean portal
travel-edge list (`portals.json`) for the RynthNav plugin to consume.

This is **step 1** of portal-assisted routing: just the data ingestion. No graph
solver and no runtime portal use yet — those come later.

## Data provenance

`Data/GAlocations.xml` is vendored (committed in-repo) so the parser always has
its input, even offline. Source: GoArrow / Roogon CoD database
(https://github.com/Darktorizo/GoArrow_Data_CoD), last upstream update 2012-04-15.

> ⚠ This is **retail** data. We play on an ACE server (retail-derived). Most town
> portals match, but validate routes empirically before trusting the DB wholesale.
> Arrival coords are stored to ~0.1° and are approximate.

## Run

```
dotnet run -c Release
```

Reads `Data/GAlocations.xml`, writes `Data/portals.json`.

Options:
- `--in <path>` / `--out <path>` — override input/output.
- `--min-arrival <deg>` — minimum |arrivalNs|+|arrivalEw| to count as a real
  destination (default 0.5; drops portals with no recorded arrival).
- `--include-retired` — keep entries flagged `retired=Y` (default: dropped).

Current output: **817 usable portal edges** from 5,258 locations.

## Output schema (`portals.json`)

Each entry is a **directed travel edge** in AC `/loc` decimal degrees (NS, EW):

| field | meaning |
|-------|---------|
| `Id` | original GoArrow location id |
| `Name` | e.g. "Al-Arqas to Samsur" |
| `Type` | GoArrow portal type (Town/Wilderness/etc.) |
| `SrcNs`, `SrcEw` | where the portal stands — walk here |
| `DstNs`, `DstEw` | where it drops you |
| `Restrictions` | advisory level band, e.g. "1-6" (optional) |
| `PkRequired` | portal is PK-only (optional) |

## Route planner (`PortalRoute.cs`)

`PortalRoute.Plan(...)` runs a uniform-cost (Dijkstra) search over a graph of
portal endpoints + START + GOAL and returns ordered walk legs, each optionally
flagged "use a portal on arrival". This same source file is `<Compile Include>`-d
into the RynthNav plugin, so what's validated here is exactly what ships.

Validate a route offline:
```
dotnet run -c Release -- --route "78.0N,55.0E" "85.0S,60.0W"
```
prints e.g. `47876u direct  ->  5342u via 4 portal hop(s)` with each leg.

Tunables (top of `PortalRoute.cs`): `PortalPenaltyUnits`, `RecallPenaltyUnits`,
`ChainWalkRadiusUnits`.

## Plugin output: `portals.tsv`

The tool also emits `portals.tsv` (same dir as `portals.json`): one line per
portal, `srcNs⇥srcEw⇥dstNs⇥dstEw⇥name`. The NativeAOT plugin reads this with
plain `string.Split` (no JSON-reflection dependency in the AOT runtime). Deploy
it to `C:\Games\RynthCore\NavData\portals.tsv`.

## Plugin integration (RynthNav v0.5.0)

`/rnav goto <coord>` now plans a portal route first; if portals beat walking it
walks each leg, walks **into** each portal entrance, waits for the teleport
(`Host.IsPortaling` falling-edge or a >240u position jump), then resumes from the
landing. Pure navmesh walk is used when no portal helps. New commands:
`/rnav route <coord>` (preview the plan, no movement) and `/rnav portals on|off`.

> ⚠ Walk-in trigger only — portals that require a click aren't handled yet (the
> plugin can't enumerate world objects to `UseObject` them). If a portal doesn't
> fire within ~12s the goto stops with "portal not triggered — check route".
