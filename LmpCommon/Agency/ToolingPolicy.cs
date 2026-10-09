using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace LmpCommon.Agency
{
    public sealed class ToolingPart
    {
        public string Name;
        public double UnitCost;
        public bool IsScience;
    }
    public sealed class ToolingCargo
    {
        public string Name;
        public int Count;
        public int ContainerPartIndex = -1;
        public uint ContainerFlightId;
        public string CrewName;
        public double UnitCost;
    }
    public sealed class ToolingManifest
    {
        public ToolingPart[] Parts = Array.Empty<ToolingPart>();
        public ToolingCargo[] Cargo = Array.Empty<ToolingCargo>();
    }
    public sealed class ToolingDesign
    {
        public string Fingerprint;
        public ToolingManifest Manifest;
        public double ToolingBasis;
        /// <summary>Optional display name; not part of the fingerprint.</summary>
        public string Name;
    }
    public sealed class ToolingMatch
    {
        public string Fingerprint;
        public int Count;
        public double CombineCost;
    }
    public sealed class ToolingQuote
    {
        public bool Success;
        public string Reason, Fingerprint;
        public double ToolingCost, LaunchCost, ScienceCost, NonScienceCost, CargoCost;
        public bool AlreadyTooled;
        /// <summary>True when the saved-design cover search gave up (too complex); ToolingCost is then the full uncovered price and Matches is empty.</summary>
        public bool CoverSearchExhausted;
        public ToolingMatch[] Matches = Array.Empty<ToolingMatch>();
    }

    /// <summary>Pure pricing of physical-part multisets. Inventory is always full price.</summary>
    public static class ToolingPolicy
    {
        public const int MaxParts = 2048;
        public const int MaxCargo = 2048;
        public const int MaxDesigns = 512;
        public const int MaxSearchStates = 20000;
        public const double MaxCost = 1e15;
        public static bool FiniteNonNegative(double value) => !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0 && value <= MaxCost;

        public static void Validate(ToolingManifest manifest)
        {
            if (manifest == null || manifest.Parts == null || manifest.Cargo == null || manifest.Parts.Length == 0 || manifest.Parts.Length > MaxParts || manifest.Cargo.Length > MaxCargo)
                throw new ArgumentException("Invalid or oversized craft manifest.");
            foreach (var part in manifest.Parts)
                if (part == null || !ValidName(part.Name) || !FiniteNonNegative(part.UnitCost)) throw new ArgumentException("Invalid physical part price or name.");
            foreach (var cargo in manifest.Cargo)
                if (cargo == null || !ValidName(cargo.Name) || cargo.Count <= 0 || cargo.Count > MaxCargo || cargo.ContainerPartIndex < -1 || cargo.ContainerPartIndex >= manifest.Parts.Length || (cargo.CrewName != null && !ValidName(cargo.CrewName)) || !FiniteNonNegative(cargo.UnitCost)) throw new ArgumentException("Invalid inventory manifest.");
            if (manifest.Cargo.Sum(c => (long)c.Count) > MaxCargo) throw new ArgumentException("Inventory quantity exceeds limit.");
            if (manifest.Parts.GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Select(p => p.IsScience).Distinct().Count() > 1))
                throw new ArgumentException("Inconsistent science classification for the same part.");
            CheckCost(manifest.Parts.Sum(p => p.UnitCost) + manifest.Cargo.Sum(c => c.UnitCost * c.Count));
        }
        private static bool ValidName(string name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 256 && !name.Any(char.IsControl);
        private static double CheckCost(double value)
        {
            if (!FiniteNonNegative(value)) throw new ArgumentException("Cost exceeds supported finite range.");
            return value;
        }
        private static string Digest(Action<BinaryWriter> write)
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true)) write(writer);
                using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(stream.ToArray())).Replace("-", string.Empty).ToLowerInvariant();
            }
        }
        public static string Fingerprint(ToolingManifest manifest)
        {
            Validate(manifest);
            return Digest(writer =>
            {
                writer.Write(1);
                var groups = manifest.Parts.GroupBy(p => p.Name, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).ToArray();
                writer.Write(groups.Length);
                foreach (var group in groups) { writer.Write(group.Key); writer.Write(group.Count()); }
            });
        }
        public static string ManifestHash(ToolingManifest manifest)
        {
            Validate(manifest);
            return Digest(writer =>
            {
                writer.Write(1); writer.Write(manifest.Parts.Length);
                foreach (var part in manifest.Parts)
                { writer.Write(part.Name); writer.Write(part.UnitCost); writer.Write(part.IsScience); }
                writer.Write(manifest.Cargo.Length);
                foreach (var item in manifest.Cargo.OrderBy(c => c.Name, StringComparer.Ordinal).ThenBy(c => c.UnitCost).ThenBy(c => c.Count).ThenBy(c => c.ContainerPartIndex).ThenBy(c => c.ContainerFlightId).ThenBy(c => c.CrewName, StringComparer.Ordinal))
                { writer.Write(item.Name); writer.Write(item.Count); writer.Write(item.UnitCost); writer.Write(item.ContainerPartIndex); writer.Write(item.ContainerFlightId); writer.Write(item.CrewName ?? string.Empty); }
            });
        }

        /// <summary>What a launch costs. Science parts and inventory are always full price; only the other parts follow the tooled or untooled multiplier.</summary>
        public static double LaunchCost(double science, double cargo, double nonScience, bool tooled, ToolingRates rates) => science + cargo + nonScience * (tooled ? rates.TooledLaunch : rates.UntooledLaunch);

        public static ToolingQuote Quote(ToolingManifest manifest, IEnumerable<ToolingDesign> existing, ToolingRates rates)
        {
            try
            {
                Validate(manifest);
                if (rates == null || !rates.Valid) throw new ArgumentException("Invalid tooling settings.");
                var designs = (existing ?? Array.Empty<ToolingDesign>()).Take(MaxDesigns + 1).ToArray();
                if (designs.Length > MaxDesigns) throw new ArgumentException("Too many saved designs to quote safely.");
                foreach (var design in designs)
                    if (design == null || !FiniteNonNegative(design.ToolingBasis) || Fingerprint(design.Manifest) != design.Fingerprint) throw new ArgumentException("Invalid saved tooling design.");
                var result = new ToolingQuote { Success = true, Reason = "Quote ready.", Fingerprint = Fingerprint(manifest) };
                result.ScienceCost = manifest.Parts.Where(p => p.IsScience).Sum(p => p.UnitCost);
                result.NonScienceCost = manifest.Parts.Where(p => !p.IsScience).Sum(p => p.UnitCost);
                result.CargoCost = manifest.Cargo.Sum(c => c.UnitCost * c.Count);
                result.AlreadyTooled = designs.Any(d => d.Fingerprint == result.Fingerprint);
                result.LaunchCost = CheckCost(LaunchCost(result.ScienceCost, result.CargoCost, result.NonScienceCost, result.AlreadyTooled, rates));
                if (result.AlreadyTooled) return result;
                Cover solution;
                try { solution = new CoverSearch(manifest, designs, rates.Tooling, rates.Combine).Solve(); }
                catch (CoverSearchExhaustedException)
                {
                    // Launch pricing never needs the cover search, so a craft too complex to match against saved designs still quotes: tooling is priced with no reuse.
                    result.CoverSearchExhausted = true;
                    result.ToolingCost = CheckCost(result.NonScienceCost * rates.Tooling);
                    return result;
                }
                result.ToolingCost = CheckCost(solution.Cost);
                result.Matches = solution.Matches.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => new ToolingMatch { Fingerprint = m.Key, Count = m.Value.Count, CombineCost = m.Value.CombineCost }).ToArray();
                return result;
            }
            catch (ArgumentException error) { return new ToolingQuote { Success = false, Reason = error.Message }; }
        }

        private sealed class CoverSearchExhaustedException : Exception
        {
            internal CoverSearchExhaustedException() : base("This combination is too complex to quote safely. Tool smaller assemblies first.") { }
        }
        private sealed class Cover
        {
            internal double Cost;
            internal Dictionary<string, ToolingMatch> Matches = new Dictionary<string, ToolingMatch>(StringComparer.Ordinal);
        }
        private sealed class Candidate
        {
            internal string Fingerprint;
            internal int[] Counts;
        }
        private sealed class CoverSearch
        {
            private readonly int[] initial;
            private readonly double[][] costs;
            private readonly Candidate[] candidates;
            private readonly Dictionary<string, Cover> cache = new Dictionary<string, Cover>(StringComparer.Ordinal);
            private readonly double multiplier, combine;
            private int states;
            internal CoverSearch(ToolingManifest manifest, ToolingDesign[] designs, double tooling, double combine)
            {
                multiplier = tooling; this.combine = combine;
                var groups = manifest.Parts.GroupBy(p => p.Name, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).ToArray();
                var names = groups.Select(g => g.Key).ToArray();
                initial = groups.Select(g => g.Count()).ToArray();
                // Covered instances consume the most expensive equal-name rows. Remaining rows are cheapest.
                costs = groups.Select(g => g.Select(p => p.IsScience ? 0 : p.UnitCost).OrderBy(c => c).ToArray()).ToArray();
                candidates = designs.Select(design => new { design, counts = design.Manifest.Parts.GroupBy(p => p.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal) })
                    .Where(x => x.counts.All(p => Array.IndexOf(names, p.Key) >= 0 && p.Value <= initial[Array.IndexOf(names, p.Key)]))
                    .Select(x => new Candidate { Fingerprint = x.design.Fingerprint, Counts = names.Select(n => x.counts.TryGetValue(n, out var count) ? count : 0).ToArray() })
                    .GroupBy(c => c.Fingerprint, StringComparer.Ordinal).Select(g => g.First()).OrderBy(c => c.Fingerprint, StringComparer.Ordinal).ToArray();
            }
            internal Cover Solve() => Solve(0, initial);
            // Each saved design covers at most one instance per quote. Candidates are tried in index order, so the state is (next candidate, remaining parts)
            // and there are no permutations: every subset of designs is considered exactly once.
            private Cover Solve(int start, int[] remaining)
            {
                var key = start.ToString(CultureInfo.InvariantCulture) + ":" + string.Join(",", remaining.Select(n => n.ToString(CultureInfo.InvariantCulture)));
                if (cache.TryGetValue(key, out var cached)) return cached;
                if (++states > MaxSearchStates) throw new CoverSearchExhaustedException();
                var best = new Cover();
                for (var i = 0; i < remaining.Length; i++) best.Cost += costs[i].Take(remaining[i]).Sum() * multiplier;
                CheckCost(best.Cost);
                for (var index = start; index < candidates.Length; index++)
                {
                    var candidate = candidates[index];
                    var next = new int[remaining.Length]; var fits = true; double replaced = 0;
                    for (var i = 0; i < remaining.Length; i++)
                    {
                        next[i] = remaining[i] - candidate.Counts[i];
                        if (next[i] < 0) { fits = false; break; }
                        replaced += costs[i].Skip(next[i]).Take(candidate.Counts[i]).Sum() * multiplier;
                    }
                    // The fee is the combine share of the full tooling value of the parts this craft would stop paying for. It ignores what the saved design cost.
                    var fee = replaced * combine;
                    if (!fits || fee >= replaced) continue;
                    var tail = Solve(index + 1, next);
                    var total = CheckCost(tail.Cost + fee);
                    if (total >= best.Cost) continue;
                    best = new Cover { Cost = total, Matches = tail.Matches.ToDictionary(p => p.Key, p => new ToolingMatch { Fingerprint = p.Key, Count = p.Value.Count, CombineCost = p.Value.CombineCost }, StringComparer.Ordinal) };
                    best.Matches[candidate.Fingerprint] = new ToolingMatch { Fingerprint = candidate.Fingerprint, Count = 1, CombineCost = fee };
                }
                cache[key] = best;
                return best;
            }
        }
    }
}
