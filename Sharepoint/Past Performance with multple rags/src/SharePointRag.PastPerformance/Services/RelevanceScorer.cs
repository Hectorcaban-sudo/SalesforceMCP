using SharePointRag.PastPerformance.Interfaces;
using SharePointRag.PastPerformance.Models;

namespace SharePointRag.PastPerformance.Services;

/// <summary>
/// Scores and ranks extracted <see cref="ContractRecord"/> objects using
/// GovCon-standard evaluation criteria drawn from FAR 15.305(a)(2):
///
///   1. Relevancy of scope (vector similarity already factored in)
///   2. Recency           (more recent = higher score)
///   3. Dollar value      (larger / closer-to-threshold = higher score)
///   4. CPARS ratings     (Exceptional > Very Good > Satisfactory …)
///   5. NAICS match       (exact 6-digit match = bonus)
///   6. Agency match      (same department = bonus)
///
/// Weights are configurable through <see cref="RelevanceScorerOptions"/>.
/// </summary>
public sealed class RelevanceScorer(RelevanceScorerOptions? opts = null) : IRelevanceScorer
{
    private readonly RelevanceScorerOptions _opts = opts ?? new RelevanceScorerOptions();

    public List<ContractRecord> ScoreAndRank(
        List<ContractRecord> contracts,
        PastPerformanceQuery query)
    {
        if (contracts.Count == 0) return [];

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        foreach (var c in contracts)
        {
            double score = 0;

            // ── 1. Recency ────────────────────────────────────────────────────
            var endDate = c.IsOngoing ? today : (c.EndDate ?? c.StartDate ?? today);
            double ageYears = (today.DayNumber - endDate.DayNumber) / 365.25;

            score += _opts.RecencyWeight * Math.Max(0, 1.0 - ageYears / 10.0);

            // ── 2. Dollar value ───────────────────────────────────────────────
            // Score peaks when contract value is close to query MinValueFilter,
            // or simply scales logarithmically if no filter set.
            decimal value = c.FinalObligatedValue ?? c.ContractValue ?? 0m;
            if (value > 0)
            {
                if (query.MinValueFilter.HasValue && value >= query.MinValueFilter)
                    score += _opts.ValueWeight * 1.0;
                else
                    score += _opts.ValueWeight * Math.Min(1.0, Math.Log10((double)value + 1) / 8.0);
            }

            // ── 3. CPARS ratings ──────────────────────────────────────────────
            double cparsScore = CparsToScore(c.CPARSRatingOverall)
                              + CparsToScore(c.CPARSRatingQuality) * 0.5
                              + CparsToScore(c.CPARSRatingSchedule) * 0.3;
            score += _opts.CparsWeight * Math.Min(1.0, cparsScore / 1.8);

            // ── 4. NAICS match ────────────────────────────────────────────────
            if (!string.IsNullOrEmpty(query.NaicsFilter)
                && c.NaicsCodes.Contains(query.NaicsFilter))
                score += _opts.NaicsMatchBonus;

            // ── 5. Agency match ───────────────────────────────────────────────
            if (!string.IsNullOrEmpty(query.AgencyFilter)
                && (c.AgencyName.Contains(query.AgencyFilter, StringComparison.OrdinalIgnoreCase)
                    || c.AgencyAcronym?.Contains(query.AgencyFilter, StringComparison.OrdinalIgnoreCase) == true))
                score += _opts.AgencyMatchBonus;

            // ── 6. Recency hard filter ────────────────────────────────────────
            if (query.RecencyYearsFilter.HasValue && ageYears > query.RecencyYearsFilter.Value)
                score *= 0.1;   // heavily penalise — don't filter out completely

            // ── 7. Completeness bonus ─────────────────────────────────────────
            // Reward records with rich data (CO contact, CPARS, accomplishments)
            if (!string.IsNullOrEmpty(c.ContractingOfficerEmail)) score += 0.05;
            if (c.KeyAccomplishments.Count > 0)                    score += 0.05;
            if (!string.IsNullOrEmpty(c.CPARSRatingOverall))       score += 0.05;

            c.RelevanceScore = Math.Round(score, 4);
        }

        return [.. contracts.OrderByDescending(c => c.RelevanceScore)];
    }

    private static double CparsToScore(string? rating) => rating?.ToUpperInvariant() switch
    {
        "EXCEPTIONAL"     => 1.0,
        "VERY GOOD"       => 0.8,
        "SATISFACTORY"    => 0.6,
        "MARGINAL"        => 0.2,
        "UNSATISFACTORY"  => 0.0,
        _                 => 0.0
    };
}

public sealed class RelevanceScorerOptions
{
    public double RecencyWeight    { get; set; } = 0.30;
    public double ValueWeight      { get; set; } = 0.20;
    public double CparsWeight      { get; set; } = 0.25;
    public double NaicsMatchBonus  { get; set; } = 0.15;
    public double AgencyMatchBonus { get; set; } = 0.10;
}
