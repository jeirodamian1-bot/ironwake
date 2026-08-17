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

Balance sweep — plays N matches from a base seed and reports outcomes and damage:

```bash
dotnet run --project Ironwake.Console -- --sweep 200 --seed 1
```

Playable hotseat client — one command, no internet, both sides played locally:

```bash
dotnet run --project Ironwake.Client        # then open http://localhost:5170
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
│   ├── Rules/                  Movement, LineOfSight, Wounding, Modifiers, Melee,
│   │                           Morale, Scoring
│   ├── Content/                definitions + IContentPack (types only, no loading)
│   ├── Actions/                GameAction and subclasses
│   ├── Events/                 GameEvent and subclasses
│   ├── Random/                 Rng (SplitMix64), RngState
│   └── Engine/                 IGameEngine, RulesEngine, SampleGame
├── Ironwake.Content/           netstandard2.1, System.Text.Json — loading + validation
│   └── StarterPack/            authored JSON, copied to output on build
├── Ironwake.Client/            net8.0 hotseat client; local host + one page, zero packages
├── Ironwake.Console/           net8.0 headless harness; the composition root
│   └── Viz/                    match recorder, HTML viewer, balance sweep
└── tests/
    ├── Ironwake.Core.Tests/    Hex, Rng, Measure, rules, engine agreement, replay
    ├── Ironwake.Content.Tests/ loading, validation, the shipped starter pack
    └── Ironwake.Console.Tests/ the match viewer, checked without a browser
```

Presentation lives in `Ironwake.Console` only. Nothing under `Viz/` may move into Core —
it does file I/O, formats HTML, and uses doubles freely, all of which Core forbids.

`Ironwake.Core.Tests` builds its packs by hand (`TestContent`) and does NOT reference
Ironwake.Content — keep it that way, or a JSON problem starts failing the engine suite.

## Notes

- `RulesEngine` is the engine. It holds no game data: statlines, weapons and points all come
  from `IContentPack`, the rules live in `Rules/`, and this class sequences them. Client code
  should still bind to `IGameEngine`, not to the concrete type.
- The only constants inside `RulesEngine` are `ActionsPerActivation` and the cover modifier —
  turn structure and a terrain rule, neither of which is a unit stat.
- MISSION parameters are still hardcoded and should move to content when a second mission
  exists: `Scoring.PointsToWin` (12), `Scoring.FinalRound` (5), `Scoring.ControlRadiusHexes`
  (3), and everything in `SampleGame` (board radius, terrain, deployment, objectives). Those
  describe one scenario, not the game's physics.
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
- Points come from `value = sqrt(offence x durability)`. Melee counts in FULL — the 50%
  discount applied while melee was unimplemented was removed once E5 made it usable.
- Objectives score ONCE, at round end, on where control stands as the round closes.
  `ObjectiveState.ControlledBy` is that round-end record; `IGameEngine.ProjectedControl` is
  the live view a client shades the board with. A test asserts the two agree.
- Control needs STRICTLY more models within 3 hexes; equal is contested and pays nobody.
  Shaken models do not count (a ruling — it is what gives morale teeth beyond -1 to hit);
  Engaged models do.
- Win conditions are ordered and the order IS the rule: 12 points, then the end of round 5,
  then annihilation. ANNIHILATION ENDS THE MATCH BUT DOES NOT WIN IT — every ending is
  decided on score, so wiping the enemy while behind on points is a loss. This is a
  mission-objective game, not an elimination game. A draw is a real outcome.
- A wipe stops the match immediately, so the wiping player forfeits whatever they would have
  scored when that round closed.
- `ReplayTests` re-executes a match's action log against a fresh state and compares
  fingerprints. Note the log comparison alone does NOT catch a bad RNG checkpoint — both
  runs take the same wrong path. The assertion that does is
  `TheStoredRngPositionAccountsForEveryDieRolled`, which checks Consumed advances by exactly
  the dice each step reported. Keep it.
- Shaken models count HALF toward objective control, rounded down. Zero stacked three
  penalties onto one nerve test; half leaves a broken unit on the board rather than off it.
- `SampleGame` is matched on POINTS (305 v 300), not unit count — three Ashguard against
  four Cinderkin. Matching unit counts gave Ashguard a 30% points advantage and made every
  balance sweep before E7 a rigged fight. Keep it points-matched.
- `MatchPolicy` plays the WIN CONDITION, not just combat: secure an objective, then fight
  from it, and stand still rather than wander off one. It was a pure combat AI that shot
  whenever anything was in range and never walked anywhere — which made "Ashguard have no
  reason to leave their deployment zone" true of the harness rather than of the game.
  `Pick` requires a `GameState` for exactly this reason; there is deliberately no
  stateless overload to fall back to.
- BALANCE NUMBERS ARE DOMINATED BY POLICY QUALITY. Every policy change so far moved the win
  rate further than any rule or content change: charge-first vs shoot-first, objective-blind
  vs objective-aware. Treat a sweep as a measurement of engine-plus-policy, and never
  compare two sweeps taken with different policies.
- `Ironwake.Client` is a local host, NOT Blazor WebAssembly. WASM was measured and rejected:
  it still needs a server (browsers block `_framework` fetches from `file://`, so its one
  advantage is illusory), it costs a 15 MB payload and a NuGet restore, and Microsoft's own
  framework files carry `https://aka.ms/...` URLs that make a "no external URLs" test
  unpassable. The local host needs zero packages because the ASP.NET shared framework is
  already installed.
- The browser holds NO engine and therefore cannot hold rules. The host sends action ids the
  engine issued via `LegalActions`; the page posts one back. It never constructs an action
  and never computes a path — `MoveUnit` carries the engine's own route. Keep it that way:
  the architecture is what enforces the constraint, not discipline.
- The viewer reads objective control from `RecordedStep.Control`, captured from
  `IGameEngine.ProjectedControl`. It must never work control out for itself — that is the
  stale-win-rule bug in a different costume, and there is a test pinning it.
- The sweep takes its verdict from the engine's `MatchEndedEvent`, never from its own
  arithmetic. It once re-derived the winner itself, and the copy went stale the moment the
  win ordering changed — the sweep reported the old rule while the engine used the new one,
  making a fundamental change look like a no-op. There is a test pinning this.
- The balance instrument is `--sweep N`, deterministic from a base seed:
  `dotnet run --project Ironwake.Console -- --sweep 200 --seed 1`. Compare two runs at the
  same base seed or the comparison is meaningless.
- `MatchPolicy` shoots BEFORE charging. A unit with no melee weapon may legally charge, and
  the free fight then does nothing, so a charge-first policy spends whole activations for
  zero damage — the sweep measured that blunder rather than the game.
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
across runs, and 411 tests pass (319 Core, 54 Content, 31 Console, 7 Client).
