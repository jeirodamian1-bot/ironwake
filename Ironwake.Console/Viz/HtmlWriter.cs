using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Ironwake.Core;

namespace Ironwake.ConsoleHarness.Viz
{
    /// <summary>
    /// Renders a <see cref="MatchRecording"/> to one self-contained HTML file.
    ///
    /// Self-contained is a hard requirement: inline CSS and JS only, no CDN, no external
    /// assets, no framework. It must open on a machine with no network.
    ///
    /// Every SVG element is emitted statically by C# and the script only ever updates
    /// attributes. That keeps the JS small, and it avoids createElementNS — which would have
    /// meant embedding the SVG namespace URL in a file that deliberately contains no URLs.
    /// A unit is never added or removed mid-match (destroyed units stay in GameState.Units),
    /// so a fixed set of elements is enough.
    ///
    /// Output is deterministic: no timestamps, no GUIDs, and every number formatted with
    /// InvariantCulture — a comma decimal separator would change the bytes and, worse,
    /// produce invalid SVG.
    /// </summary>
    public static class HtmlWriter
    {
        /// <summary>Hex circumradius in SVG units.</summary>
        private const double HexSize = 30.0;

        public static void Write(string path, MatchRecording recording, IContentPack content)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (recording == null) throw new ArgumentNullException(nameof(recording));

            File.WriteAllText(path, Render(recording, content), new UTF8Encoding(false));
        }

        public static string Render(MatchRecording recording, IContentPack content)
        {
            if (recording == null) throw new ArgumentNullException(nameof(recording));

            var geometry = Geometry.Build(recording.InitialState);
            var payload = BuildPayload(recording, content, geometry);

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false,
            });

            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n");
            sb.Append("<meta charset=\"utf-8\">\n");
            sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
            sb.Append("<title>Ironwake match — seed ").Append(recording.Seed).Append("</title>\n");
            sb.Append("<style>\n").Append(Css).Append("</style>\n</head>\n");

            sb.Append("<body data-step-count=\"").Append(recording.Steps.Count).Append('"')
              .Append(" data-frame-count=\"").Append(payload.Frames.Count).Append('"')
              .Append(" data-seed=\"").Append(recording.Seed).Append("\">\n");

            AppendHeader(sb);
            AppendBoard(sb, geometry, payload);
            AppendPanels(sb);

            sb.Append("<script>\nconst DATA = ").Append(json).Append(";\n");
            sb.Append(Script).Append("</script>\n</body>\n</html>\n");

            return sb.ToString();
        }

        // ---- static markup ---------------------------------------------------

        private static void AppendHeader(StringBuilder sb) => sb.Append(HeaderMarkup);

        private static void AppendBoard(StringBuilder sb, Geometry geometry, Payload payload)
        {
            sb.Append("<main>\n  <section class=\"board\">\n");
            sb.Append("    <svg id=\"board\" viewBox=\"").Append(geometry.ViewBox).Append("\">\n");

            sb.Append("      <g id=\"terrain\">\n");
            foreach (var cell in geometry.Cells)
            {
                sb.Append("        <polygon class=\"hex t-").Append(cell.Terrain)
                  .Append("\" points=\"").Append(cell.Points).Append("\"></polygon>\n");
            }
            sb.Append("      </g>\n");

            sb.Append("      <g id=\"coords\">\n");
            foreach (var cell in geometry.Cells)
            {
                sb.Append("        <text class=\"coord\" x=\"").Append(F(cell.Cx))
                  .Append("\" y=\"").Append(F(cell.Cy + 20)).Append("\">")
                  .Append(cell.Q).Append(',').Append(cell.R).Append("</text>\n");
            }
            sb.Append("      </g>\n");

            sb.Append("      <g id=\"objectives\">\n");
            foreach (var objective in geometry.Objectives)
            {
                sb.Append("        <polygon class=\"obj\" points=\"").Append(objective.Points).Append("\"></polygon>\n");
                sb.Append("        <text class=\"objtext\" x=\"").Append(F(objective.Cx))
                  .Append("\" y=\"").Append(F(objective.Cy - 17)).Append("\">")
                  .Append(objective.PointValue).Append("vp</text>\n");
            }
            sb.Append("      </g>\n");

            sb.Append("      <polyline id=\"trail\" class=\"trail\" points=\"\"></polyline>\n");

            // Line of sight for a shot: the sight line itself, a ring on a target in cover,
            // and a cross on whatever blocked it.
            sb.Append("      <line id=\"losline\" class=\"los\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"0\"></line>\n");
            sb.Append("      <circle id=\"covermark\" class=\"covermark\" cx=\"0\" cy=\"0\" r=\"20\"></circle>\n");
            sb.Append("      <g id=\"blockmark\" class=\"blockmark\">\n");
            sb.Append("        <line id=\"blockA\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"0\"></line>\n");
            sb.Append("        <line id=\"blockB\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"0\"></line>\n");
            sb.Append("      </g>\n");

            // One circle + label per unit, reused for every frame.
            sb.Append("      <g id=\"units\">\n");
            for (int i = 0; i < payload.Units.Count; i++)
            {
                var unit = payload.Units[i];
                sb.Append("        <circle id=\"uc").Append(i).Append("\" class=\"unit o-")
                  .Append(unit.Owner).Append("\" r=\"14\" cx=\"0\" cy=\"0\"><title id=\"ut")
                  .Append(i).Append("\">").Append(Esc(unit.Id + " " + unit.Name)).Append("</title></circle>\n");
                sb.Append("        <text id=\"ul").Append(i).Append("\" class=\"ulabel\" x=\"0\" y=\"0\"></text>\n");
            }
            sb.Append("      </g>\n    </svg>\n");

            sb.Append(LegendMarkup);
            sb.Append("  </section>\n");
        }

        private static void AppendPanels(StringBuilder sb) => sb.Append(PanelsMarkup);

        // ---- geometry --------------------------------------------------------

        private sealed class Geometry
        {
            public string ViewBox;
            public List<CellGeometry> Cells = new List<CellGeometry>();
            public List<ObjectiveGeometry> Objectives = new List<ObjectiveGeometry>();

            public static Geometry Build(GameState state)
            {
                var g = new Geometry();
                var board = state.Board;

                double minX = double.MaxValue, minY = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue;

                // AllHexes sorts before returning, so this markup is stable run to run.
                foreach (var hex in board.AllHexes())
                {
                    hex.ToPixel(HexSize, out double cx, out double cy);

                    var corners = new List<string>(6);
                    for (int i = 0; i < 6; i++)
                    {
                        // Pointy-top: corners sit at 60i-30 degrees around the centre.
                        double angle = Math.PI / 180.0 * (60.0 * i - 30.0);
                        double px = cx + HexSize * Math.Cos(angle);
                        double py = cy + HexSize * Math.Sin(angle);

                        minX = Math.Min(minX, px); maxX = Math.Max(maxX, px);
                        minY = Math.Min(minY, py); maxY = Math.Max(maxY, py);

                        corners.Add(F(px) + "," + F(py));
                    }

                    g.Cells.Add(new CellGeometry
                    {
                        Q = hex.Q,
                        R = hex.R,
                        Cx = cx,
                        Cy = cy,
                        Points = string.Join(" ", corners),
                        Terrain = board.TerrainAt(hex).ToString(),
                    });
                }

                foreach (var objective in state.Objectives.OrderBy(o => o.Id.Value))
                {
                    objective.Position.ToPixel(HexSize, out double ox, out double oy);
                    const double s = 13.0;

                    g.Objectives.Add(new ObjectiveGeometry
                    {
                        Cx = ox,
                        Cy = oy,
                        PointValue = objective.PointValue,
                        // A diamond, so an objective never reads as a unit.
                        Points = string.Join(" ",
                            F(ox) + "," + F(oy - s), F(ox + s) + "," + F(oy),
                            F(ox) + "," + F(oy + s), F(ox - s) + "," + F(oy)),
                    });
                }

                const double pad = 14.0;
                g.ViewBox = string.Join(" ",
                    F(minX - pad), F(minY - pad), F(maxX - minX + pad * 2), F(maxY - minY + pad * 2));

                return g;
            }
        }

        private sealed class CellGeometry
        {
            public int Q, R;
            public double Cx, Cy;
            public string Points;
            public string Terrain;
        }

        private sealed class ObjectiveGeometry
        {
            public double Cx, Cy;
            public int PointValue;
            public string Points;
        }

        // ---- payload ---------------------------------------------------------

        private static Payload BuildPayload(MatchRecording recording, IContentPack content, Geometry geometry)
        {
            // Unit identity is fixed for the match; only their numbers and positions move.
            var roster = recording.InitialState.Units
                .OrderBy(u => u.Id.Value)
                .Select(u => new UnitIdentity
                {
                    Id = u.Id.ToString(),
                    Name = NameOf(u, content),
                    Owner = u.Owner.ToString(),
                })
                .ToList();

            var frames = new List<Frame>
            {
                FrameOf(recording.InitialState, null, null, "Starting position.")
            };

            foreach (var step in recording.Steps)
                frames.Add(FrameOf(step.StateAfter, step, step.Events, null));

            return new Payload
            {
                Seed = recording.Seed.ToString(CultureInfo.InvariantCulture),
                ContentVersion = recording.ContentVersion ?? "unknown",
                Completed = recording.Completed,
                Units = roster,
                Frames = frames,
            };
        }

        private static Frame FrameOf(
            GameState state, RecordedStep step, IReadOnlyList<GameEvent> events, string fallbackAction)
        {
            var units = state.Units
                .OrderBy(u => u.Id.Value)
                .Select(u =>
                {
                    u.Position.ToPixel(HexSize, out double ux, out double uy);
                    return new UnitFrame
                    {
                        Cx = Round(ux),
                        Cy = Round(uy),
                        Alive = u.ModelsAlive,
                        Total = u.Models.Count,
                        Activated = u.HasActivated,
                        IsActive = state.ActiveUnit == u.Id,
                    };
                })
                .ToList();

            // Draw the route of a move, so a step reads as movement rather than a teleport.
            var trail = new List<string>();
            if (events != null)
            {
                foreach (var moved in events.OfType<UnitMovedEvent>())
                {
                    foreach (var hex in moved.Path)
                    {
                        hex.ToPixel(HexSize, out double px, out double py);
                        trail.Add(F(px) + "," + F(py));
                    }
                }
            }

            return new Frame
            {
                Round = state.Round,
                Active = state.ActivePlayer.ToString(),
                ScoreA = state.ScoreA,
                ScoreB = state.ScoreB,
                Phase = state.Phase.ToString(),
                Action = step?.Action == null ? fallbackAction : DescribeAction(step.Action),
                Events = (events ?? Array.Empty<GameEvent>()).Select(e => e.Describe()).ToList(),
                Units = units,
                Trail = trail.Count > 1 ? string.Join(" ", trail) : null,
                Shot = ShotViewOf(step?.Shot),
            };
        }

        private static ShotView ShotViewOf(ShotTrace shot)
        {
            if (shot == null) return null;

            shot.From.ToPixel(HexSize, out double x1, out double y1);
            shot.To.ToPixel(HexSize, out double x2, out double y2);

            double? blockX = null, blockY = null;
            if (shot.BlockingHex.HasValue)
            {
                shot.BlockingHex.Value.ToPixel(HexSize, out double bx, out double by);
                blockX = Round(bx);
                blockY = Round(by);
            }

            return new ShotView
            {
                X1 = Round(x1),
                Y1 = Round(y1),
                X2 = Round(x2),
                Y2 = Round(y2),
                Blocked = shot.Blocked,
                Cover = shot.TargetInCover,
                BlockX = blockX,
                BlockY = blockY,
            };
        }

        private static string NameOf(UnitState unit, IContentPack content)
        {
            // DisplayName is not reachable from GameState alone; it needs the content pack.
            if (content == null) return unit.DefinitionId;
            return content.TryGetUnit(unit.DefinitionId, out var def) ? def.DisplayName : unit.DefinitionId;
        }

        /// <summary>
        /// A readable form of the submitted action. No GameEvent names the action itself, so
        /// the recorder keeps it alongside the events it produced.
        /// </summary>
        private static string DescribeAction(GameAction action)
        {
            switch (action)
            {
                case ActivateUnit a:
                    return $"{a.Actor} activates {a.Unit}.";
                case MoveUnit m:
                    var to = m.Path.Count > 0 ? m.Path[m.Path.Count - 1].ToString() : "?";
                    return $"{m.Actor} moves {m.Unit} {Math.Max(0, m.Path.Count - 1)} hex(es) to {to}.";
                case ShootAt s:
                    return $"{s.Actor} has {s.Unit} shoot {s.Target}.";
                case EndActivation e:
                    return $"{e.Actor} ends {e.Unit}'s activation.";
                case PassActivation p:
                    return $"{p.Actor} passes.";
                default:
                    return $"{action.Actor}: {action.Kind}.";
            }
        }

        // ---- formatting -------------------------------------------------------

        /// <summary>Invariant and rounded. Culture-dependent formatting would break the SVG outright.</summary>
        private static string F(double value) =>
            Round(value).ToString("0.##", CultureInfo.InvariantCulture);

        private static double Round(double value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

        private static string Esc(string text) => text == null
            ? string.Empty
            : text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        // ---- DTOs -------------------------------------------------------------

        private sealed class Payload
        {
            public string Seed { get; set; }
            public string ContentVersion { get; set; }
            public bool Completed { get; set; }
            public List<UnitIdentity> Units { get; set; }
            public List<Frame> Frames { get; set; }
        }

        private sealed class UnitIdentity
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Owner { get; set; }
        }

        private sealed class Frame
        {
            public int Round { get; set; }
            public string Active { get; set; }
            public int ScoreA { get; set; }
            public int ScoreB { get; set; }
            public string Phase { get; set; }
            public string Action { get; set; }
            public List<string> Events { get; set; }
            public List<UnitFrame> Units { get; set; }
            public string Trail { get; set; }
            public ShotView Shot { get; set; }
        }

        /// <summary>The sight line for a shot, as the engine traced it.</summary>
        private sealed class ShotView
        {
            public double X1 { get; set; }
            public double Y1 { get; set; }
            public double X2 { get; set; }
            public double Y2 { get; set; }
            public bool Blocked { get; set; }
            public bool Cover { get; set; }
            public double? BlockX { get; set; }
            public double? BlockY { get; set; }
        }

        private sealed class UnitFrame
        {
            public double Cx { get; set; }
            public double Cy { get; set; }
            public int Alive { get; set; }
            public int Total { get; set; }
            public bool Activated { get; set; }
            public bool IsActive { get; set; }
        }

        // ---- inline assets ----------------------------------------------------

        private const string Css = @"
:root{
  --bg:#11141a; --panel:#181d26; --line:#2b3342; --ink:#e7eaf0; --muted:#8d97a8;
  --p1:#4a9eff; --p2:#ff8a3d; --gold:#ffd24a;
  --t-open:#242b38; --t-cover:#2c6b48; --t-obscuring:#4a3d72;
  --t-elevated:#6d5a30; --t-impassable:#7a3030;
}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--ink);
  font:14px/1.5 ui-sans-serif,system-ui,-apple-system,'Segoe UI',Roboto,sans-serif}
header{padding:14px 18px;border-bottom:1px solid var(--line);background:var(--panel)}
h1{margin:0 0 2px;font-size:16px;letter-spacing:.02em}
.meta{color:var(--muted);font-size:12px}
.status{margin-top:8px;display:flex;flex-wrap:wrap;gap:8px}
.chip{background:#0e1218;border:1px solid var(--line);border-radius:999px;
  padding:3px 10px;font-size:12px;white-space:nowrap}
.chip b{font-weight:600}
.p1{color:var(--p1)} .p2{color:var(--p2)}
main{display:flex;flex-wrap:wrap;gap:16px;padding:16px;align-items:flex-start}
.board{flex:1 1 520px;min-width:320px}
svg{width:100%;height:auto;display:block;background:#0d1015;
  border:1px solid var(--line);border-radius:10px}
.hex{stroke:#0d1015;stroke-width:1.5}
.t-Open{fill:var(--t-open)} .t-Cover{fill:var(--t-cover)}
.t-Obscuring{fill:var(--t-obscuring)} .t-Elevated{fill:var(--t-elevated)}
.t-Impassable{fill:var(--t-impassable)}
.coord{fill:#5b6577;font-size:8px;text-anchor:middle;pointer-events:none}
.obj{fill:none;stroke:var(--gold);stroke-width:2.5}
.objtext{fill:var(--gold);font-size:10px;font-weight:700;text-anchor:middle;pointer-events:none}
.trail{fill:none;stroke:#ffffff;stroke-width:3;stroke-opacity:.5;
  stroke-linejoin:round;stroke-linecap:round;stroke-dasharray:6 5}
.los{stroke:#ff5a5a;stroke-width:3;stroke-opacity:.85;stroke-linecap:round}
.los.blocked{stroke-dasharray:5 4}
.covermark{fill:none;stroke:var(--gold);stroke-width:2.5;stroke-dasharray:4 4}
.blockmark line{stroke:#ff5a5a;stroke-width:4;stroke-linecap:round}
.unit{stroke:#0d1015;stroke-width:2}
.unit.o-P1{fill:var(--p1)} .unit.o-P2{fill:var(--p2)}
.unit.activated{opacity:.4}
.unit.active{stroke:#ffffff;stroke-width:3}
.unit.dead{fill:none;stroke:#556072;stroke-dasharray:3 3;opacity:.45}
.ulabel{fill:#0d1015;font-size:12px;font-weight:700;text-anchor:middle;pointer-events:none}
.ulabel.activated{opacity:.5}
.legend{display:flex;flex-wrap:wrap;gap:10px;margin-top:10px;font-size:12px;color:var(--muted)}
.legend span{display:inline-flex;align-items:center;gap:6px}
.sw{width:12px;height:12px;border-radius:3px;border:1px solid #0d1015;display:inline-block}
.sw.swobj{border-color:var(--gold);background:transparent;transform:rotate(45deg)}
aside{flex:0 1 330px;min-width:260px;background:var(--panel);
  border:1px solid var(--line);border-radius:10px;padding:14px}
aside h2{margin:0 0 6px;font-size:13px;color:var(--muted);font-weight:600;
  text-transform:uppercase;letter-spacing:.06em}
aside h2+h2{margin-top:14px}
.action{margin:0;font-weight:600}
ol{margin:0;padding-left:18px;max-height:44vh;overflow:auto}
ol li{margin-bottom:4px;font-variant-numeric:tabular-nums}
ol li.none{list-style:none;margin-left:-18px;color:var(--muted)}
footer{position:sticky;bottom:0;background:var(--panel);border-top:1px solid var(--line);
  padding:10px 16px;display:flex;gap:12px;align-items:center}
button{background:#222b38;color:var(--ink);border:1px solid var(--line);
  border-radius:7px;padding:6px 14px;font:inherit;cursor:pointer}
button:hover:not(:disabled){background:#2b3646}
button:disabled{opacity:.4;cursor:default}
input[type=range]{flex:1;accent-color:var(--p1)}
.counter{color:var(--muted);font-size:12px;min-width:110px;text-align:right;
  font-variant-numeric:tabular-nums}
";

        private const string HeaderMarkup = @"<header>
  <h1>Ironwake &mdash; match viewer</h1>
  <div class=""meta"" id=""meta""></div>
  <div class=""status"">
    <span class=""chip"">Round <b id=""round"">&mdash;</b></span>
    <span class=""chip"">To act <b id=""active"">&mdash;</b></span>
    <span class=""chip"">Score <b class=""p1"" id=""scoreA"">0</b> &ndash; <b class=""p2"" id=""scoreB"">0</b></span>
    <span class=""chip"">Phase <b id=""phase"">&mdash;</b></span>
  </div>
</header>
";

        private const string LegendMarkup = @"    <div class=""legend"">
      <span><i class=""sw"" style=""background:var(--t-open)""></i>Open</span>
      <span><i class=""sw"" style=""background:var(--t-cover)""></i>Cover</span>
      <span><i class=""sw"" style=""background:var(--t-obscuring)""></i>Obscuring</span>
      <span><i class=""sw"" style=""background:var(--t-elevated)""></i>Elevated</span>
      <span><i class=""sw"" style=""background:var(--t-impassable)""></i>Impassable</span>
      <span><i class=""sw swobj""></i>Objective</span>
      <span><i class=""sw"" style=""background:#ff5a5a""></i>Line of sight</span>
      <span><i class=""sw swobj"" style=""border-style:dashed;transform:none;border-radius:50%""></i>Target in cover</span>
      <span><i class=""sw"" style=""background:var(--p1)""></i>Player 1</span>
      <span><i class=""sw"" style=""background:var(--p2)""></i>Player 2</span>
      <span>Dimmed = already activated</span>
    </div>
";

        private const string PanelsMarkup = @"  <aside>
    <h2>Step <span id=""stepNo""></span></h2>
    <p class=""action"" id=""action""></p>
    <h2>Events</h2>
    <ol id=""events""></ol>
  </aside>
</main>
<footer>
  <button id=""prev"">&larr; Prev</button>
  <input type=""range"" id=""slider"" min=""0"" value=""0"">
  <button id=""next"">Next &rarr;</button>
  <span class=""counter"" id=""counter""></span>
</footer>
";

        private const string Script = @"
const $ = id => document.getElementById(id);
const frames = DATA.frames;
const slider = $('slider');
const trail = $('trail');
const eventList = $('events');
let at = 0;

$('meta').textContent =
  'seed ' + DATA.seed + '  ·  content ' + DATA.contentVersion +
  '  ·  ' + (frames.length - 1) + ' steps  ·  ' +
  (DATA.completed ? 'match completed' : 'stopped at step guard');

slider.max = frames.length - 1;

function draw(i) {
  at = Math.max(0, Math.min(i, frames.length - 1));
  const f = frames[at];

  $('round').textContent = f.round;
  $('active').textContent = f.active;
  $('active').className = f.active === 'P1' ? 'p1' : 'p2';
  $('scoreA').textContent = f.scoreA;
  $('scoreB').textContent = f.scoreB;
  $('phase').textContent = f.phase;
  $('stepNo').textContent = at === 0 ? '0 (start)' : at;
  $('action').textContent = f.action || '—';

  eventList.textContent = '';
  if (!f.events || f.events.length === 0) {
    const li = document.createElement('li');
    li.className = 'none';
    li.textContent = 'No events.';
    eventList.appendChild(li);
  } else {
    for (const text of f.events) {
      const li = document.createElement('li');
      li.textContent = text;
      eventList.appendChild(li);
    }
  }

  if (f.trail) { trail.setAttribute('points', f.trail); trail.style.display = ''; }
  else { trail.style.display = 'none'; }

  const losLine = $('losline'), coverMark = $('covermark'), blockMark = $('blockmark');
  if (f.shot) {
    losLine.setAttribute('x1', f.shot.x1); losLine.setAttribute('y1', f.shot.y1);
    losLine.setAttribute('x2', f.shot.x2); losLine.setAttribute('y2', f.shot.y2);
    losLine.setAttribute('class', 'los' + (f.shot.blocked ? ' blocked' : ''));
    losLine.style.display = '';

    if (f.shot.cover) {
      coverMark.setAttribute('cx', f.shot.x2);
      coverMark.setAttribute('cy', f.shot.y2);
      coverMark.style.display = '';
    } else {
      coverMark.style.display = 'none';
    }

    if (f.shot.blocked && f.shot.blockX !== null && f.shot.blockX !== undefined) {
      const k = 11;
      const a = $('blockA'), b = $('blockB');
      a.setAttribute('x1', f.shot.blockX - k); a.setAttribute('y1', f.shot.blockY - k);
      a.setAttribute('x2', f.shot.blockX + k); a.setAttribute('y2', f.shot.blockY + k);
      b.setAttribute('x1', f.shot.blockX - k); b.setAttribute('y1', f.shot.blockY + k);
      b.setAttribute('x2', f.shot.blockX + k); b.setAttribute('y2', f.shot.blockY - k);
      blockMark.style.display = '';
    } else {
      blockMark.style.display = 'none';
    }
  } else {
    losLine.style.display = 'none';
    coverMark.style.display = 'none';
    blockMark.style.display = 'none';
  }

  f.units.forEach((u, idx) => {
    const who = DATA.units[idx];
    const circle = $('uc' + idx);
    const label = $('ul' + idx);
    const dead = u.alive <= 0;

    let cls = 'unit o-' + who.owner;
    if (dead) cls += ' dead';
    else {
      if (u.activated) cls += ' activated';
      if (u.isActive) cls += ' active';
    }
    circle.setAttribute('class', cls);
    circle.setAttribute('cx', u.cx);
    circle.setAttribute('cy', u.cy);
    circle.setAttribute('r', dead ? 8 : 14);

    $('ut' + idx).textContent =
      who.id + ' · ' + who.name + ' · ' + who.owner +
      ' · ' + u.alive + '/' + u.total + ' models' +
      (dead ? ' · destroyed' : u.activated ? ' · activated' : '');

    label.setAttribute('x', u.cx);
    label.setAttribute('y', u.cy + 4);
    label.setAttribute('class', 'ulabel' + (u.activated ? ' activated' : ''));
    label.textContent = dead ? '' : u.alive;
  });

  slider.value = at;
  $('counter').textContent = at + ' / ' + (frames.length - 1);
  $('prev').disabled = at === 0;
  $('next').disabled = at === frames.length - 1;
}

$('prev').addEventListener('click', () => draw(at - 1));
$('next').addEventListener('click', () => draw(at + 1));
slider.addEventListener('input', () => draw(parseInt(slider.value, 10)));
document.addEventListener('keydown', ev => {
  if (ev.key === 'ArrowLeft') { draw(at - 1); ev.preventDefault(); }
  else if (ev.key === 'ArrowRight') { draw(at + 1); ev.preventDefault(); }
  else if (ev.key === 'Home') { draw(0); ev.preventDefault(); }
  else if (ev.key === 'End') { draw(frames.length - 1); ev.preventDefault(); }
});

draw(0);
";
    }
}
