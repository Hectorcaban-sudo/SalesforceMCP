using SharePointRag.PastPerformance.Interfaces;
using SharePointRag.PastPerformance.Models;

namespace SharePointRag.PastPerformance.Services;

/// <summary>
/// Source-aware GovCon relevance scorer.
///
/// Scores ContractRecords using FAR 15.305(a)(2) criteria — regardless of which
/// data source produced the record — and applies source-specific bonuses:
///
///   Structured sources (SQL, Deltek, Excel):
///     +DataCompletenessBonus if the record has a contract number AND agency name
///     (structured sources usually have clean, authoritative field data)
///
///   Document sources (SharePoint, Custom):
///     +DocumentRichnessBonus if the record has CPARS text AND accomplishments
///     (documents have narrative detail that structured sources often lack)
///
/// Standard weights (all sources):
///   Recency (30%) · Dollar value (20%) · CPARS (25%) · NAICS match (15%) · Agency match (10%)
/// </summary>
public sealed class RelevanceScorer(RelevanceScorerOptions? opts = null) : IRelevanceScorer
{
    private readonly RelevanceScorerOptions _opts = opts ?? new RelevanceScorerOptions();

    private static readonly HashSet<string> StructuredTypes =
        new(StringComparer.OrdinalIgnoreCase) { "SqlDatabase", "Deltek", "Excel" };

    public List<ContractRecord> ScoreAndRank(
        List<ContractRecord> contracts,
        PastPerformanceQuery query)
    {
        if (contracts.Count == 0) return [];

        // Apply connector-type filter if specified
        var filtered = query.ConnectorTypeFilter.Count > 0
            ? contracts.Where(c =>
                query.ConnectorTypeFilter.Contains(c.ConnectorType, StringComparer.OrdinalIgnoreCase))
              .ToList()
            : contracts;

        // Apply data-source name filter if specified
        if (query.DataSourceFilter.Count > 0)
            filtered = filtered.Where(c =>
                query.DataSourceFilter.Contains(c.DataSourceName, StringComparer.OrdinalIgnoreCase))
              .ToList();

        if (filtered.Count == 0) return [];

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        foreach (var c in filtered)
        {
            double score = 0;

            // ── 1. Recency ────────────────────────────────────────────────────
            var endDate  = c.IsOngoing ? today : (c.EndDate ?? c.StartDate ?? today);
            double ageYr = (today.DayNumber - endDate.DayNumber) / 365.25;
            score += _opts.RecencyWeight * Math.Max(0, 1.0 - ageYr / 10.0);

            // ── 2. Dollar value ───────────────────────────────────────────────
            decimal value = c.FinalObligatedValue ?? c.ContractValue ?? 0m;
            if (value > 0)
            {
                score += query.MinValueFilter.HasValue && value >= query.MinValueFilter
                    ? _opts.ValueWeight
                    : _opts.ValueWeight * Math.Min(1.0, Math.Log10((double)value + 1) / 8.0);
            }

            // ── 3. CPARS ratings ──────────────────────────────────────────────
            double cpars = CparsToScore(c.CPARSRatingOverall)
                         + CparsToScore(c.CPARSRatingQuality)  * 0.5
                         + CparsToScore(c.CPARSRatingSchedule) * 0.3;
            score += _opts.CparsWeight * Math.Min(1.0, cpars / 1.8);

            // ── 4. NAICS match ────────────────────────────────────────────────
            if (!string.IsNullOrEmpty(query.NaicsFilter)
                && c.NaicsCodes.Contains(query.NaicsFilter))
                score += _opts.NaicsMatchBonus;

            // ── 5. Agency match ───────────────────────────────────────────────
            if (!string.IsNullOrEmpty(query.AgencyFilter)
                && (c.AgencyName.Contains(query.AgencyFilter, StringComparison.OrdinalIgnoreCase)
                    || c.AgencyAcronym?.Contains(query.AgencyFilter, StringComparison.OrdinalIgnoreCase) == true))
                score += _opts.AgencyMatchBonus;

            // ── 6. Recency hard-penalise ──────────────────────────────────────
            if (query.RecencyYearsFilter.HasValue && ageYr > query.RecencyYearsFilter.Value)
                score *= 0.1;

            // ── 7. Source-type bonuses ────────────────────────────────────────
            bool isStructured = StructuredTypes.Contains(c.ConnectorType);

            if (isStructured)
            {
                // Structured records are authoritative — bonus for clean anchor data
                if (!string.IsNullOrEmpty(c.ContractNumber) &&
                    !string.IsNullOrEmpty(c.AgencyName))
                    score += _opts.StructuredDataCompletenessBonus;
            }
            else
            {
                // Document records are narrative-rich — bonus for CPARS + accomplishments
                if (!string.IsNullOrEmpty(c.CPARSRatingOverall) &&
                    c.KeyAccomplishments.Count > 0)
                    score += _opts.DocumentRichnessBonus;
            }

            // ── 8. General completeness bonus ────────────────────────────────
            if (!string.IsNullOrEmpty(c.ContractingOfficerEmail)) score += 0.05;
            if (c.KeyAccomplishments.Count > 0)                    score += 0.05;
            if (!string.IsNullOrEmpty(c.CPARSRatingOverall))       score += 0.05;

            c.RelevanceScore = Math.Round(score, 4);
        }

        return [.. filtered.OrderByDescending(c => c.RelevanceScore)];
    }

    private static double CparsToScore(string? rating) => rating?.ToUpperInvariant() switch
    {
        "EXCEPTIONAL"    => 1.0,
        "VERY GOOD"      => 0.8,
        "SATISFACTORY"   => 0.6,
        "MARGINAL"       => 0.2,
        "UNSATISFACTORY" => 0.0,
        _                => 0.0
    };
}

public sealed class RelevanceScorerOptions
{
    public double RecencyWeight    { get; set; } = 0.30;
    public double ValueWeight      { get; set; } = 0.20;
    public double CparsWeight      { get; set; } = 0.25;
    public double NaicsMatchBonus  { get; set; } = 0.15;
    public double AgencyMatchBonus { get; set; } = 0.10;

    /// <summary>Bonus for structured-source records that have both ContractNumber and AgencyName.</summary>
    public double StructuredDataCompletenessBonus { get; set; } = 0.08;

    /// <summary>Bonus for document-source records that have both CPARS ratings and accomplishments.</summary>
    public double DocumentRichnessBonus { get; set; } = 0.08;
}
