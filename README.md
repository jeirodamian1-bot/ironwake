# Ironwake — Shared Contract v0.1

This is the boundary between the two halves of the project. Damian owns everything
inside `Ironwake.Core`. The Unity client owns everything outside it.
yh
Nothing here is final except the *shape*. Field names and rules will change. What should
not change is the direction things flow:

```
   client input  ──►  GameAction  ──►  [ ENGINE ]  ──►  GameEvent[]  ──►  client animation
                                          │
                                          └──►  new GameState  ──►  client re-render
```

---

## First run

```bash
dotnet build
dotnet run --project Ironwake.Console
```

You should see a full stub match play out in the terminal. Run it twice with the same
seed — the output must be identical. That is the determinism guarantee the whole
architecture rests on.

```bash
dotnet run --project Ironwake.Console 777 > a.txt
dotnet run --project Ironwake.Console 777 > b.txt
diff a.txt b.txt      # must be empty
```

> **Not yet compiled.** This was written without a .NET SDK available. Build it locally
> first and fix any small compile errors before committing — don't assume it's clean.

---

## For the client (Nicolas)

### Getting the DLL into Unity

Build `Ironwake.Core`, then drop `Ironwake.Core.dll` into `Assets/Plugins/`.
Re-copy it whenever the engine changes. Later we'll automate this with a build step.

### The three things you need

```csharp
var engine = new StubEngine();          // swap for RulesEngine later, no client changes
var state  = SampleGame.Create();       // a real board with 6 units and terrain
```

**1. Render from `GameState`.**
`state.Board.AllHexes()` gives you every hex. `state.Board.TerrainAt(h)` gives you what's
on it. `state.Units` gives you what to draw and where.

**2. Convert taps to `Hex`, and `Hex` to positions.**

```csharp
// screen → hex
Vector3 world = Camera.main.ScreenToWorldPoint(Input.mousePosition);
Hex clicked = Hex.FromPixel(world.x, world.y, HEX_SIZE);

// hex → screen
clicked.ToPixel(HEX_SIZE, out double x, out double y);
transform.position = new Vector3((float)x, (float)y, 0f);
```

These two methods are the **only** place floats are allowed near hexes. Everything else
is integer `(Q, R)`.

**3. Send actions, animate events.**

```csharp
var action = new MoveUnit(myPlayerId, selectedUnit, path);

var check = engine.Validate(state, action);
if (!check.IsLegal) {
    ShowToast(check.Detail);        // "Out of range by 2 hex(es)."
    return;
}

var result = engine.Execute(state, action);
foreach (var e in result.Events)
    yield return PlayAnimation(e);   // animate one at a time
state = result.NextState;            // then apply authoritative state
```

### The rule that matters most

**Never compute a game outcome in the client.** Not movement range, not whether a shot
can be taken, not damage. If you need to know something, ask the engine:

```csharp
// highlight reachable hexes — no range maths in the client
foreach (var h in unit.Position.WithinRange(6)) {
    var probe = new MoveUnit(me, unit.Id, unit.Position.LineTo(h));
    if (engine.Validate(state, probe).IsLegal) Highlight(h);
}

// or just ask for everything at once
var options = engine.LegalActions(state, me);
```

If the answer isn't available from the engine, that's a gap in the engine — tell Damian,
don't work around it locally. Every workaround becomes a rule implemented twice, and the
two copies will disagree.

### What you can safely build right now

- Hex grid rendering + terrain tinting
- Camera pan / zoom / clamp to board bounds
- Tap to select a unit, tap to preview a path, tap to confirm
- Highlighting legal destinations and valid shooting targets
- Unit sprites, health pips, activation state
- The combat log — just append `e.Describe()` for now
- Move and shoot animations driven by `UnitMovedEvent` / `DiceRolledEvent`

All of that will still work unchanged when the real engine replaces the stub.

---

## What the stub does and doesn't do

| Works | Missing |
|---|---|
| Activation and turn handover | Line of sight |
| Movement with path validation | Cover modifiers |
| Shooting: hit → wound → save → damage | Charge and melee |
| Model removal, unit destruction | Morale |
| Round advance, 5-round match end | Objective scoring |
| Seeded, reproducible dice | Real statlines (everything is flat) |

Statlines are hardcoded constants at the top of `StubEngine.cs`. Real ones come from the
content pack later. Don't build anything that depends on those numbers.

**`StubEngine.cs` gets deleted** once the real engine lands. Nothing outside `Ironwake.Core`
should ever reference it by name — code against `IGameEngine`.

---

## Architectural law

`Ironwake.Core` must never reference `UnityEngine`. Not once, not temporarily, not behind
a compiler flag.

This is what lets the same compiled engine run in Unity *and* on a server later, and what
makes headless testing and balance simulation possible. It is trivially easy to break and
expensive to undo. If Core needs something from Unity, the design is wrong.

Also inside Core: no `System.Random` (use `Rng`), no `DateTime.Now`, no file or network
access, no floats in rules maths, and no iteration over `Dictionary`/`HashSet` where order
affects the outcome.

---

## Open decisions

Settle these together before Phase 1 proper:

1. **Hex size and orientation in world units** — pick a number, put it in one constant.
2. **Board radius** — sample uses 5 (91 hexes). Might be too big for phone screens.
3. **Multi-model units on one hex** — currently a unit occupies one hex regardless of
   model count. Simplest option; confirm it feels right before building around it.
4. **Facing** — the field exists (0–5) but nothing uses it. Are we doing facing rules?
   Decide now; retrofitting it later touches every combat path.
5. **`SampleGame` unit names** — placeholders. The setting doc needs to exist before these
   become real.

---

## Layout

```
Ironwake.Core/
├── State/
│   ├── Hex.cs           coordinates, distance, line, pixel conversion
│   ├── Ids.cs           UnitId, PlayerId, ObjectiveId, enums
│   └── GameState.cs     GameState, BoardState, UnitState, ModelState, ObjectiveState
├── Actions/GameAction.cs
├── Events/GameEvent.cs
├── Random/Rng.cs        SplitMix64, seeded and reproducible
└── Engine/
    ├── IGameEngine.cs   the interface + ValidationResult + reason codes
    ├── StubEngine.cs    TEMPORARY — delete when RulesEngine lands
    └── SampleGame.cs    a playable starting state

Ironwake.Console/        headless harness, no Unity
```
