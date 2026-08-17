using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Ironwake.Core;

namespace Ironwake.ConsoleHarness.Viz
{
    /// <summary>
    /// What a batch of matches came to. Everything here is a count or a sum — the report
    /// does the dividing, so nothing rounds twice.
    /// </summary>
    public sealed class SweepResult
    {
        public int Matches;
        public int WinsA;
        public int WinsB;
        public int Draws;

        public int TotalScoreA;
        public int TotalScoreB;
        public int TotalRounds;
        public int TotalSteps;

        /// <summary>Guard-hit matches: the harness gave up rather than the rules deciding.</summary>
        public int Unfinished;

        /// <summary>Damage dealt and taken, by unit definition id.</summary>
        public readonly Dictionary<string, UnitTally> ByUnit = new Dictionary<string, UnitTally>(StringComparer.Ordinal);

        public sealed class UnitTally
        {
            public string DefinitionId;
            public string Faction;
            public long DamageDealt;
            public long DamageTaken;
            public long ModelsLost;
            public int Appearances;
        }

        public UnitTally TallyFor(string definitionId, string faction)
        {
            if (!ByUnit.TryGetValue(definitionId, out var tally))
            {
                tally = new UnitTally { DefinitionId = definitionId, Faction = faction };
                ByUnit[definitionId] = tally;
            }
            return tally;
        }
    }

    /// <summary>
    /// Plays many matches and reports what happened. The instrument for balance work: a
    /// single match tells you nothing, and eyeballing the viewer tells you less.
    ///
    /// Deterministic given a base seed — sweep N uses seeds base..base+N-1, so a before and
    /// after comparison is over exactly the same matches.
    /// </summary>
    public static class Sweep
    {
        public static SweepResult Run(IContentPack content, int matches, ulong baseSeed, int guard = 500)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));
            if (matches < 1) throw new ArgumentOutOfRangeException(nameof(matches));

            var result = new SweepResult { Matches = matches };
            IGameEngine engine = new RulesEngine(content);

            for (int i = 0; i < matches; i++)
            {
                ulong seed = baseSeed + (ulong)i;
                var state = SampleGame.Create(content, seed);

                // Which definition each unit id belongs to, so damage can be attributed.
                var definitions = state.Units.ToDictionary(u => u.Id.Value, u => u.DefinitionId);
                var factions = state.Units.ToDictionary(
                    u => u.Id.Value, u => content.GetUnit(u.DefinitionId).FactionId);

                foreach (var unit in state.Units)
                    result.TallyFor(unit.DefinitionId, factions[unit.Id.Value]).Appearances++;

                int steps = 0;
                bool finished = false;
                bool decided = false;
                PlayerId? winner = null;

                while (state.Phase != PhaseKind.Complete && steps < guard)
                {
                    var legal = engine.LegalActions(state, state.ActivePlayer);
                    if (legal.Count == 0) break;

                    var outcome = engine.Execute(state, MatchPolicy.Pick(state, legal));
                    steps++;

                    // The ENGINE decides who won. Re-deriving it here would be the win rule
                    // implemented twice, and the two copies would disagree the moment the
                    // rule changed — which is exactly what happened when annihilation stopped
                    // outranking points and this sweep went on reporting the old ordering.
                    foreach (var ended in outcome.Events.OfType<MatchEndedEvent>())
                    {
                        winner = ended.Winner;
                        decided = true;
                    }

                    foreach (var attack in outcome.Events.OfType<AttackResolvedEvent>())
                    {
                        if (attack.DamageDealt <= 0) continue;

                        if (definitions.TryGetValue(attack.Attacker.Value, out var dealer))
                            result.TallyFor(dealer, factions[attack.Attacker.Value]).DamageDealt += attack.DamageDealt;

                        if (definitions.TryGetValue(attack.Target.Value, out var taker))
                            result.TallyFor(taker, factions[attack.Target.Value]).DamageTaken += attack.DamageDealt;
                    }

                    foreach (var slain in outcome.Events.OfType<ModelSlainEvent>())
                        if (definitions.TryGetValue(slain.Unit.Value, out var lost))
                            result.TallyFor(lost, factions[slain.Unit.Value]).ModelsLost++;

                    state = outcome.NextState;
                    if (outcome.IsTerminal) { finished = true; break; }
                }

                if (state.Phase == PhaseKind.Complete) finished = true;
                if (!finished) result.Unfinished++;

                result.TotalSteps += steps;
                result.TotalRounds += state.Round;
                result.TotalScoreA += state.ScoreA;
                result.TotalScoreB += state.ScoreB;

                // A match that never emitted MatchEnded hit the step guard; it is counted as
                // unfinished above and does not get a verdict invented for it here.
                if (!decided) continue;

                if (winner == PlayerId.A) result.WinsA++;
                else if (winner == PlayerId.B) result.WinsB++;
                else result.Draws++;
            }

            return result;
        }

        /// <summary>A fixed-width report, stable enough to diff two runs against each other.</summary>
        public static string Report(SweepResult r, IContentPack content, ulong baseSeed)
        {
            var sb = new StringBuilder();
            var ci = CultureInfo.InvariantCulture;

            double pct(int n) => 100.0 * n / r.Matches;

            sb.AppendLine($"=== sweep: {r.Matches} matches, seeds {baseSeed}..{baseSeed + (ulong)r.Matches - 1} " +
                          $"| content {content.Version} ===");
            sb.AppendLine();
            sb.AppendLine("outcome              count      share");
            sb.AppendLine($"  Ashguard (P1) {r.WinsA,10}{pct(r.WinsA),9:0.0}%");
            sb.AppendLine($"  Cinderkin (P2){r.WinsB,10}{pct(r.WinsB),9:0.0}%");
            sb.AppendLine($"  draw          {r.Draws,10}{pct(r.Draws),9:0.0}%");
            if (r.Unfinished > 0)
                sb.AppendLine($"  UNFINISHED    {r.Unfinished,10}{pct(r.Unfinished),9:0.0}%   (hit the step guard)");
            sb.AppendLine();
            sb.AppendLine($"mean score      {(double)r.TotalScoreA / r.Matches,6:0.00} - {(double)r.TotalScoreB / r.Matches,-6:0.00}");
            sb.AppendLine($"mean rounds     {(double)r.TotalRounds / r.Matches,6:0.00}");
            sb.AppendLine($"mean steps      {(double)r.TotalSteps / r.Matches,6:0.00}");
            sb.AppendLine();

            sb.AppendLine("per unit                     dealt/match  taken/match  lost/match");
            foreach (var tally in r.ByUnit.Values
                         .OrderBy(t => t.Faction, StringComparer.Ordinal)
                         .ThenBy(t => t.DefinitionId, StringComparer.Ordinal))
            {
                int n = Math.Max(1, tally.Appearances);
                sb.AppendLine(string.Format(ci,
                    "  {0,-26}{1,11:0.00}{2,13:0.00}{3,12:0.00}",
                    tally.DefinitionId,
                    (double)tally.DamageDealt / n,
                    (double)tally.DamageTaken / n,
                    (double)tally.ModelsLost / n));
            }

            var byFaction = r.ByUnit.Values.GroupBy(t => t.Faction).OrderBy(g => g.Key, StringComparer.Ordinal);
            sb.AppendLine();
            sb.AppendLine("per faction                  dealt/match  taken/match");
            foreach (var group in byFaction)
            {
                sb.AppendLine(string.Format(ci,
                    "  {0,-26}{1,11:0.00}{2,13:0.00}",
                    group.Key,
                    group.Sum(t => (double)t.DamageDealt) / r.Matches,
                    group.Sum(t => (double)t.DamageTaken) / r.Matches));
            }

            return sb.ToString();
        }
    }
}
