# Ironwake — Engine Constraints

`Ironwake.Core` is a pure .NET Standard 2.1 rules engine consumed by both a Unity client
and (later) an ASP.NET server. The same compiled assembly runs in both. Every constraint
below exists to protect that property.

Read `Ironwake-README.md` (the shared contract with the client) before changing the
boundary between engine and client.

## Hard constraints

These are not style preferences. Breaking any of them is a defect.

1. **No platform dependencies in Core.** `Ironwake.Core` must NEVER reference
   `UnityEngine`, ASP.NET, file I/O, or network access. Not once, not temporarily, not
   behind a compiler flag. **Zero package references** in `Ironwake.Core.csproj`.
   Serialization included: Core defines `IContentPack` but never loads one. JSON, file
   access and validation live in `Ironwake.Content`, and the dependency only ever points
   Content → Core.

2. **No `System.Random`, no `UnityEngine.Random`.** All randomness goes through
   `Ironwake.Core.Rng`. Determinism is the guarantee the whole architecture rests on —
   server authority, replays, balance simulation and cheat detection all depend on it.

3. **No `DateTime.Now` anywhere in Core.** Nothing in the engine may read wall-clock time.

4. **No floats in rules maths.** Distances are `int`. Hex coordinates are integer
   `(Q, R)`. The *only* permitted float/double usage is `Hex.ToPixel`, `Hex.FromPixel`
   and `Hex.LineTo`. Content authors distances in tenths of an inch, also `int`;
   converting tenths to hexes goes through `Measure` and nowhere else — never write
   `/ 10` at a call site.

5. **No iteration over `Dictionary` or `HashSet` where order affects the outcome.**
   Enumeration order is not guaranteed and will silently desync client from server. Sort
   explicitly, or use an ordered collection. `BoardState.AllHexes()` is the pattern to
   copy — it sorts before returning.

6. **`GameState` is immutable.** Mutations return new instances. Never mutate state in
   place.

7. **The client never computes outcomes.** Not movement range, not whether a shot can be
   taken, not damage. If the client needs to know something, the engine must expose it.
   A missing answer is a gap in the engine, not a thing to work around client-side —
   every workaround becomes a rule implemented twice, and the two copies will disagree.

8. **Run `dotnet test` before declaring any task complete.**

## Build & run

Run these from the repo root, next to `Ironwake.sln`.

```bash
dotnet build
dotnet run --project Ironwake.Console          # stub match plays to completion
dotnet test
```

Visual match viewer — writes one self-contained HTML file and prints its absolute path:

```bash
dotnet run --project Ironwake.Console -- --html match.html
dotnet run --project Ironwake.Console -- --html match.html --seed 777
```

Determinism check — the diff must be empty:

```bash
dotnet run --project Ironwake.Console 777 > /tmp/a.txt
dotnet run --project Ironwake.Console 777 > /tmp/b.txt
diff /tmp/a.txt /tmp/b.txt
```

`DeterminismTests` also pins this in-process, so a regression fails `dotnet test` rather
than waiting to be caught by diffing console runs.

## Layout

```
Ironwake.sln
├── Ironwake.Core/              netstandard2.1, zero package references
│   ├── State/                  Hex, Ids, GameState, Measure
│   ├── Rules/                  Movement, LineOfSight, Wounding, Modifiers, Melee, Morale
│   ├── Content/                definitions + IContentPack (types only, no loading)
│   ├── Actions/                GameAction and subclasses
│   ├── Events/                 GameEvent and subclasses
│   ├── Random/                 Rng (SplitMix64), RngState
│   └── Engine/                 IGameEngine, StubEngine, SampleGame
├── Ironwake.Content/           netstandard2.1, System.Text.Json — loading + validation
│   └── StarterPack/            authored JSON, copied to output on build
├── Ironwake.Console/           net8.0 headless harness; the composition root
│   └── Viz/                    match recorder + self-contained HTML viewer
└── tests/
    ├── Ironwake.Core.Tests/    Hex, Rng, Measure, movement, StubEngine, determinism
    ├── Ironwake.Content.Tests/ loading, validation, the shipped starter pack
    └── Ironwake.Console.Tests/ the match viewer, checked without a browser
```

Presentation lives in `Ironwake.Console` only. Nothing under `Viz/` may move into Core —
it does file I/O, formats HTML, and uses doubles freely, all of which Core forbids.

`Ironwake.Core.Tests` builds its packs by hand (`TestContent`) and does NOT reference
Ironwake.Content — keep it that way, or a JSON problem starts failing the engine suite.

## Notes

- `StubEngine.cs` is temporary and gets deleted when the real `RulesEngine` lands. Nothing
  outside `Ironwake.Core` should reference it by name — code against `IGameEngine`.
- `StubEngine` reads every number from `IContentPack`. The only constants left are turn
  structure (`ActionsPerActivation`) and the cover modifier.
- Wounding is `Wounding.TargetFor(power, resilience)`. The bands overlap, so they are tested
  strongest-first, and "exactly double"/"exactly half" are inclusive at the extreme end.
  Never write `resilience / 2` — the check is `power * 2 <= resilience` so odd values do not
  truncate.
- Roll modifiers use the player-facing sign: `Value` modifies the ROLL, so -1 RAISES the
  target number. `Modifiers.FinalTarget` is the only place that conversion happens, and it
  clamps to 2+..6+ — but never rescues a base of 7, which is content's "cannot" sentinel.
- `DiceRolledEvent` is structured (`RollKind`, `Roller`, `BaseTarget`, `FinalTarget`,
  `Modifiers`). `Describe()` composes prose FROM those fields — never parse it back apart.
- Line of sight is deliberately generous. A line running exactly along a hex edge is
  genuinely ambiguous, so `LineOfSight.Trace` traces both candidate lines and counts sight
  as blocked only when BOTH are blocked. Never resolve LOS from a single `Hex.LineTo` —
  the epsilon tie-break makes that an arbitrary choice.
- Floating point stays sealed inside `Hex`. `Hex.LineTo` takes a `LineTieBreak` enum rather
  than a raw epsilon precisely so rules code never handles a double.
- Sight is symmetric EXCEPT when exactly one end is `Elevated` — high ground sees over
  Obscuring, so it can see into a pocket it cannot be seen out of. That asymmetry is a rule
  and there is a test asserting it, not excluding it.
- `StatusKind.Engaged` is DERIVED, never accumulated. `Melee.RefreshEngagement` recomputes it
  from adjacency after every action, which is what makes "clears when no enemy is adjacent"
  true without anyone remembering to clear it. Never set or unset it by hand.
- A charge is a move that ends beside the target, so it goes through `Movement.FindPath`.
  There is exactly one pathfinder; do not add a second for charges.
- Shooting and melee share `ResolveAttack` — only the to-hit stat and the modifier list
  differ. Cover does not apply in melee (`Melee.CoverAppliesInMelee`), which is a ruling.
- Morale order at round end matters and gives Shaken its one-round life: clear Shaken from
  everyone, THEN test whoever lost models, THEN reset the loss counters.
- The RNG checkpoint is taken AFTER `CheckRoundEnd`, because morale rolls dice. Move it back
  before and the stored RNG position silently under-counts.
- Leaving melee is currently free — no zone of control, no parting attacks. Deferred, not
  decided.
- Content authoring: a unit declares its `factionId`; faction membership is derived from
  that, not authored separately. Two sources of truth for the same fact will disagree.
- Validation collects every error before throwing. Keep it that way — fixing content one
  error per build is miserable.
- Any test that plays a whole match must pick `ActivateUnit` explicitly. Fall through to
  the last legal action and you get `PassActivation` forever: no unit activates, no dice
  roll, and a determinism comparison of two such runs passes while testing nothing.
- Engine tests assert the `ReasonCode`, not just that an action was refused. The client
  branches on those codes, so a rule failing for the wrong reason is still a bug.
- Restoring NuGet packages on this machine has been slow enough to time out on the first
  attempt. If restore fails, retry with `dotnet restore --disable-parallel` before
  assuming the network is down.

## Repo status

As of 2026-08-16: all five projects compile clean against .NET SDK 8.0.129, the stub match
plays to completion on the starter pack, the seeded determinism check is byte-identical
across runs, and 363 tests pass (284 Core, 54 Content, 25 Console).
