using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ironwake.Content;
using Ironwake.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ironwake.Client
{
    /// <summary>
    /// A hotseat client: one local process, one browser page, both sides played by whoever
    /// is sitting there.
    ///
    /// THE ARCHITECTURE IS THE POINT. The browser holds no engine and therefore cannot hold
    /// rules. It receives a list of legal actions the engine produced, each with an id, and
    /// posts back an id. It never builds an action, never computes a path, never decides
    /// whether anything is legal. Everything below comes from IGameEngine: LegalActions,
    /// Validate, ReachableHexes, CheckLineOfSight, ProjectedControl, Execute.
    ///
    /// Nothing is fetched from anywhere — the page is one hand-written file and there are
    /// zero package references, so this builds and runs with no network.
    /// </summary>
    public static class Program
    {
        /// <summary>Hex circumradius in SVG units. Matches the match viewer.</summary>
        private const double HexSize = 30.0;

        private static readonly object Gate = new object();
        private static Session _session;

        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            builder.Logging.ClearProviders();

            var app = builder.Build();

            var content = StarterPack.Load();
            _session = Session.Start(content, 777UL);

            // Plain middleware rather than minimal-API lambdas: the delegate-based Map
            // overloads do not resolve under LangVersion 9, and this needs no extra surface.
            app.Use(async (context, next) =>
            {
                var path = context.Request.Path.Value ?? string.Empty;
                if (!path.StartsWith("/api/", StringComparison.Ordinal)) { await next(); return; }

                await HandleApi(context, content);
            });

            app.UseDefaultFiles();
            app.UseStaticFiles();

            Console.WriteLine();
            Console.WriteLine("  Ironwake hotseat client");
            Console.WriteLine("  Open http://localhost:5170 in a browser. Ctrl-C to stop.");
            Console.WriteLine();

            app.Run("http://localhost:5170");
        }

        private static async System.Threading.Tasks.Task HandleApi(HttpContext context, IContentPack content)
        {
            var path = context.Request.Path.Value ?? string.Empty;

            if (path == "/api/state")
            {
                await WriteJson(context, Snapshot());
                return;
            }

            if (path == "/api/new")
            {
                var body = await ReadJson(context);
                ulong seed = body.HasValue && body.Value.TryGetProperty("seed", out var s) &&
                             s.ValueKind == JsonValueKind.Number
                    ? (ulong)Math.Max(0, s.GetInt64())
                    : 777UL;

                lock (Gate) _session = Session.Start(content, seed);
                await WriteJson(context, Snapshot());
                return;
            }

            if (path == "/api/act")
            {
                var body = await ReadJson(context);
                if (!body.HasValue || !body.Value.TryGetProperty("actionId", out var id) ||
                    id.ValueKind != JsonValueKind.Number)
                {
                    context.Response.StatusCode = 400;
                    await WriteJson(context, new { error = "actionId required" });
                    return;
                }

                lock (Gate) _session.Submit(id.GetInt32());
                await WriteJson(context, Snapshot());
                return;
            }

            context.Response.StatusCode = 404;
        }

        private static async System.Threading.Tasks.Task<JsonElement?> ReadJson(HttpContext context)
        {
            try
            {
                using var document = await JsonDocument.ParseAsync(context.Request.Body);
                return document.RootElement.Clone();
            }
            catch (JsonException) { return null; }
        }

        private static async System.Threading.Tasks.Task WriteJson(HttpContext context, object value)
        {
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(JsonSerializer.Serialize(value, JsonOptions));
        }

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private static object Snapshot() { lock (Gate) return _session.ToView(); }

        // =================================================================================

        /// <summary>One match in progress. Held in memory; there is exactly one, hotseat.</summary>
        private sealed class Session
        {
            private readonly IGameEngine _engine;
            private readonly IContentPack _content;
            private readonly List<string> _log = new List<string>();

            private GameState _state;
            private ulong _seed;

            /// <summary>The actions the engine currently offers, in the order it offered them.</summary>
            private IReadOnlyList<GameAction> _offered = Array.Empty<GameAction>();

            private Session(IGameEngine engine, IContentPack content) { _engine = engine; _content = content; }

            public static Session Start(IContentPack content, ulong seed)
            {
                var session = new Session(new RulesEngine(content), content) { _seed = seed };
                session._state = SampleGame.Create(content, seed);
                session._log.Add($"New match. Seed {seed}, content {content.Version}.");
                session.RefreshOffers();
                return session;
            }

            /// <summary>Ask the engine what is legal. The one source of every option shown.</summary>
            private void RefreshOffers() =>
                _offered = _state.Phase == PhaseKind.Complete
                    ? Array.Empty<GameAction>()
                    : _engine.LegalActions(_state, _state.ActivePlayer);

            public void Submit(int actionId)
            {
                if (actionId < 0 || actionId >= _offered.Count) return;

                var action = _offered[actionId];

                // The engine offered it, and the engine is asked again before it is taken.
                var check = _engine.Validate(_state, action);
                if (!check.IsLegal)
                {
                    _log.Add($"Refused: {check.Detail}");
                    return;
                }

                var result = _engine.Execute(_state, action);
                foreach (var e in result.Events) _log.Add($"[R{_state.Round}] {e.Describe()}");

                _state = result.NextState;
                RefreshOffers();
            }

            // ---- view model ------------------------------------------------------------

            public object ToView()
            {
                var active = _state.ActiveUnit.IsNone ? null : _state.GetUnit(_state.ActiveUnit);
                var control = _engine.ProjectedControl(_state);

                return new
                {
                    seed = _seed.ToString(CultureInfo.InvariantCulture),
                    contentVersion = _content.Version,
                    round = _state.Round,
                    activePlayer = _state.ActivePlayer.ToString(),
                    scoreA = _state.ScoreA,
                    scoreB = _state.ScoreB,
                    phase = _state.Phase.ToString(),
                    matchOver = _state.Phase == PhaseKind.Complete,
                    activeUnit = active?.Id.ToString(),
                    actionsRemaining = active?.ActionsRemaining ?? 0,
                    board = BoardView(),
                    objectives = ObjectiveViews(control),
                    units = UnitViews(active),
                    moves = MoveViews(),
                    targets = TargetViews(active),
                    commands = CommandViews(),
                    log = _log.ToList(),
                };
            }

            private object BoardView()
            {
                var cells = new List<object>();
                double minX = double.MaxValue, minY = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue;

                foreach (var hex in _state.Board.AllHexes())
                {
                    hex.ToPixel(HexSize, out double cx, out double cy);
                    var corners = new List<string>(6);
                    for (int i = 0; i < 6; i++)
                    {
                        double angle = Math.PI / 180.0 * (60.0 * i - 30.0);
                        double px = cx + HexSize * Math.Cos(angle);
                        double py = cy + HexSize * Math.Sin(angle);
                        minX = Math.Min(minX, px); maxX = Math.Max(maxX, px);
                        minY = Math.Min(minY, py); maxY = Math.Max(maxY, py);
                        corners.Add(F(px) + "," + F(py));
                    }

                    cells.Add(new
                    {
                        q = hex.Q,
                        r = hex.R,
                        cx = Round(cx),
                        cy = Round(cy),
                        points = string.Join(" ", corners),
                        terrain = _state.Board.TerrainAt(hex).ToString(),
                    });
                }

                const double pad = 14.0;
                return new
                {
                    viewBox = string.Join(" ", F(minX - pad), F(minY - pad),
                                               F(maxX - minX + pad * 2), F(maxY - minY + pad * 2)),
                    hexes = cells,
                };
            }

            private object ObjectiveViews(IReadOnlyDictionary<ObjectiveId, PlayerId?> control)
            {
                return _state.Objectives.OrderBy(o => o.Id.Value).Select(o =>
                {
                    o.Position.ToPixel(HexSize, out double cx, out double cy);
                    control.TryGetValue(o.Id, out var holder);

                    var ring = new List<string>();
                    foreach (var hex in o.Position.WithinRange(Scoring.ControlRadiusHexes))
                    {
                        if (!_state.Board.Contains(hex)) continue;
                        hex.ToPixel(HexSize, out double hx, out double hy);
                        var corners = new List<string>(6);
                        for (int i = 0; i < 6; i++)
                        {
                            double angle = Math.PI / 180.0 * (60.0 * i - 30.0);
                            corners.Add(F(hx + HexSize * Math.Cos(angle)) + "," +
                                        F(hy + HexSize * Math.Sin(angle)));
                        }
                        ring.Add(string.Join(" ", corners));
                    }

                    return (object)new
                    {
                        id = o.Id.ToString(),
                        cx = Round(cx),
                        cy = Round(cy),
                        value = o.PointValue,
                        holder = holder?.ToString(),
                        ring,
                    };
                }).ToList();
            }

            private object UnitViews(UnitState active)
            {
                return _state.Units.OrderBy(u => u.Id.Value).Select(u =>
                {
                    u.Position.ToPixel(HexSize, out double cx, out double cy);

                    // DisplayName is not on GameState — it needs the content pack.
                    string name = _content.TryGetUnit(u.DefinitionId, out var def)
                        ? def.DisplayName : u.DefinitionId;

                    return (object)new
                    {
                        id = u.Id.ToString(),
                        name,
                        owner = u.Owner.ToString(),
                        cx = Round(cx),
                        cy = Round(cy),
                        alive = u.ModelsAlive,
                        total = u.Models.Count,
                        activated = u.HasActivated,
                        engaged = u.HasStatus(StatusKind.Engaged),
                        shaken = u.HasStatus(StatusKind.Shaken),
                        isActive = active != null && active.Id == u.Id,
                        actionsRemaining = u.ActionsRemaining,
                        activateId = OfferIdOf(a => a is ActivateUnit act && act.Unit == u.Id),
                    };
                }).ToList();
            }

            /// <summary>
            /// Every destination the active unit can move to. Costs come from ReachableHexes;
            /// the ACTION comes from LegalActions, which carries the engine's own path — the
            /// client never has to work a route out.
            /// </summary>
            private object MoveViews()
            {
                if (_state.ActiveUnit.IsNone) return new List<object>();

                var reachable = _engine.ReachableHexes(_state, _state.ActiveUnit);
                var views = new List<object>();

                for (int i = 0; i < _offered.Count; i++)
                {
                    if (!(_offered[i] is MoveUnit move)) continue;

                    var dest = move.Path[move.Path.Count - 1];
                    dest.ToPixel(HexSize, out double cx, out double cy);
                    reachable.TryGetValue(dest, out int cost);

                    views.Add(new
                    {
                        actionId = i,
                        q = dest.Q,
                        r = dest.R,
                        cx = Round(cx),
                        cy = Round(cy),
                        cost,
                        path = move.Path.Select(h =>
                        {
                            h.ToPixel(HexSize, out double px, out double py);
                            return F(px) + "," + F(py);
                        }).ToList(),
                    });
                }

                return views;
            }

            /// <summary>
            /// Every enemy, with whether it can be shot, charged or fought — and when it
            /// cannot, the engine's own ReasonCode and Detail to show on hover.
            /// </summary>
            private object TargetViews(UnitState active)
            {
                if (active == null) return new List<object>();

                var views = new List<object>();
                foreach (var enemy in _state.Units.Where(u => u.Owner != active.Owner && u.IsAlive)
                                                  .OrderBy(u => u.Id.Value))
                {
                    var los = _engine.CheckLineOfSight(_state, active.Id, enemy.Id);

                    var shoot = _engine.Validate(_state, new ShootAt(active.Owner, active.Id, enemy.Id, null));
                    var charge = _engine.Validate(_state, new ChargeAt(active.Owner, active.Id, enemy.Id));
                    var fight = _engine.Validate(_state, new FightUnit(active.Owner, active.Id, enemy.Id));

                    views.Add(new
                    {
                        unit = enemy.Id.ToString(),
                        losBlocked = los.IsBlocked,
                        inCover = los.TargetInCover,
                        shootId = OfferIdOf(a => a is ShootAt s && s.Target == enemy.Id),
                        shootReason = shoot.IsLegal ? null : shoot.ReasonCode,
                        shootDetail = shoot.IsLegal ? null : shoot.Detail,
                        chargeId = OfferIdOf(a => a is ChargeAt c && c.Target == enemy.Id),
                        chargeReason = charge.IsLegal ? null : charge.ReasonCode,
                        chargeDetail = charge.IsLegal ? null : charge.Detail,
                        fightId = OfferIdOf(a => a is FightUnit f && f.Target == enemy.Id),
                        fightReason = fight.IsLegal ? null : fight.ReasonCode,
                        fightDetail = fight.IsLegal ? null : fight.Detail,
                    });
                }
                return views;
            }

            private object CommandViews() => new
            {
                endId = OfferIdOf(a => a is EndActivation),
                passId = OfferIdOf(a => a is PassActivation),
            };

            /// <summary>Index of the first offered action matching, or null if none is offered.</summary>
            private int? OfferIdOf(Func<GameAction, bool> match)
            {
                for (int i = 0; i < _offered.Count; i++)
                    if (match(_offered[i])) return i;
                return null;
            }
        }

        private static string F(double value) =>
            Round(value).ToString("0.##", CultureInfo.InvariantCulture);

        private static double Round(double value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
    }
}
