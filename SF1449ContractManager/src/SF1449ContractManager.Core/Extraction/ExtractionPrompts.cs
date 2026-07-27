namespace SF1449ContractManager.Core.Extraction;

public static class ExtractionPrompts
{
    /// <summary>
    /// (FieldName, CLR type hint, Block/description) - FieldName must match the
    /// property name on Sf1449Contract exactly, because the mapper below uses
    /// reflection to apply it.
    /// </summary>
    public static readonly (string FieldName, string Type, string Description)[] HeaderFieldCatalogue =
    {
        ("RequisitionNumber", "string", "Block 1 - Requisition Number"),
        ("ContractNumber", "string", "Block 2 - Contract Number"),
        ("AwardEffectiveDate", "date", "Block 3 - Award/Effective Date"),
        ("OrderNumber", "string", "Block 4 - Order Number"),
        ("SolicitationNumber", "string", "Block 5 - Solicitation Number"),
        ("SolicitationIssueDate", "date", "Block 6 - Solicitation Issue Date"),
        ("SolicitationContactName", "string", "Block 7a - For Solicitation Information Call: Name"),
        ("SolicitationContactPhone", "string", "Block 7b - Telephone Number"),
        ("OfferDueDateLocalTime", "datetime", "Block 8 - Offer Due Date/Local Time"),
        ("IssuedByCode", "string", "Block 9 - Issued By Code"),
        ("IssuedByName", "string", "Block 9 - Issued By, agency/office name line"),
        ("IssuedByAddress", "string", "Block 9 - Issued By, full mailing address"),
        ("IssuedByContactEmail", "string", "Block 9 - contracting POC email, if printed"),
        ("AcquisitionType", "enum:Unrestricted,SetAside", "Block 10 - Unrestricted or Set-Aside checkbox"),
        ("SetAsidePercent", "decimal", "Block 10 - % FOR, next to Set-Aside"),
        ("IsSmallBusiness", "bool", "Block 10 - SMALL BUSINESS checkbox"),
        ("IsHubZoneSmallBusiness", "bool", "Block 10 - HUBZONE SMALL BUSINESS checkbox"),
        ("IsServiceDisabledVeteranOwned", "bool", "Block 10 - SERVICE-DISABLED VETERAN-OWNED SMALL BUSINESS checkbox"),
        ("IsWomenOwnedSmallBusiness", "bool", "Block 10 - WOMEN-OWNED SMALL BUSINESS (WOSB) checkbox"),
        ("IsEconomicallyDisadvantagedWomenOwned", "bool", "Block 10 - EDWOSB checkbox"),
        ("Is8A", "bool", "Block 10 - 8(A) checkbox"),
        ("NaicsCode", "string", "Block 10 - NAICS code"),
        ("SizeStandardUsd", "decimal", "Block 10 - Size Standard in USD"),
        ("FobDestinationSeeSchedule", "bool", "Block 11 - SEE SCHEDULE checkbox"),
        ("DiscountTerms", "string", "Block 12 - Discount Terms"),
        ("IsDpasRatedOrder", "bool", "Block 13a - rated order under DPAS checkbox"),
        ("DpasRating", "string", "Block 13b - Rating"),
        ("MethodOfSolicitation", "enum:RequestForQuote,InvitationForBid,RequestForProposal", "Block 14"),
        ("DeliverToCode", "string", "Block 15 - Deliver To Code"),
        ("DeliverToAddress", "string", "Block 15 - Deliver To address"),
        ("AdministeredByCode", "string", "Block 16 - Administered By Code"),
        ("AdministeredByAddress", "string", "Block 16 - Administered By address"),
        ("ContractorCode", "string", "Block 17a - Contractor/Offeror CAGE Code"),
        ("ContractorFacilityCode", "string", "Block 17a - Facility Code"),
        ("ContractorName", "string", "Block 17a - Contractor legal name"),
        ("ContractorAddress", "string", "Block 17a - Contractor address"),
        ("ContractorPhone", "string", "Block 17a - Contractor telephone number"),
        ("RemittanceAddressDiffers", "bool", "Block 17b checkbox"),
        ("PaymentWillBeMadeByCode", "string", "Block 18a - Payment Will Be Made By Code"),
        ("PaymentWillBeMadeByAddress", "string", "Block 18a - Payment office address"),
        ("SubmitInvoicesSeeAddendum", "bool", "Block 18b - SEE ADDENDUM checkbox"),
        ("AccountingAndAppropriationData", "string", "Block 25"),
        ("TotalAwardAmount", "decimal", "Block 26 - Total Award Amount"),
        ("SolicitationIncorporatesFarByReference", "bool", "Block 27a - ARE checkbox"),
        ("SolicitationAddendaAttached", "bool", "Block 27a - ADDENDA ARE ATTACHED checkbox"),
        ("ContractIncorporatesFarByReference", "bool", "Block 27b - ARE checkbox"),
        ("ContractAddendaAttached", "bool", "Block 27b - ADDENDA ARE ATTACHED checkbox"),
        ("ContractorSignatureRequired", "bool", "Block 28 checkbox"),
        ("AwardReferenceOfferDate", "string", "Block 29 - reference offer date text"),
        ("OfferorSignerNameAndTitle", "string", "Block 30b"),
        ("OfferorSignedDate", "date", "Block 30c"),
        ("ContractingOfficerName", "string", "Block 31b"),
        ("ContractingOfficerSignedDate", "date", "Block 31c"),
        ("Title", "string", "A short descriptive title for the requirement, usually printed near the top of the continuation pages"),
        ("ScopeOfWorkSummary", "string", "2-4 sentence summary of the Scope of Work / Performance Work Statement"),
        ("PeriodOfPerformanceSummary", "string", "Plain-language period of performance, e.g. 'Base year through 30 Apr 2026'"),
        ("WageDeterminationNumber", "string", "Service Contract Act wage determination number, if present"),
        ("WageDeterminationUrl", "string", "URL to the wage determination, if present"),
    };

    public static string BuildSystemInstructions() => $$"""
        You are a meticulous federal contracts data-entry specialist. You extract structured
        data from the text of a Standard Form 1449 (SF-1449), "Solicitation/Contract/Order for
        Commercial Products and Commercial Services," including its continuation pages,
        performance work statement, and incorporated FAR/DFARS clauses.

        You will be given the PDF's text, page by page, each preceded by a `[[PAGE n]]` marker.

        Return ONLY a single valid JSON object - no markdown code fences, no commentary before
        or after it. The JSON object must have exactly these top-level keys: "HeaderFields",
        "LineItems", "Clauses".

        "HeaderFields" is an object whose keys are EXACTLY the field names below (do not invent
        new keys, do not omit a key - if a field is not present in the document, still include
        the key with Value = null and Confidence = 0). Each value is an object:
          { "Value": <string|null>, "Confidence": <0.0-1.0>, "SourcePage": <int|null> }
        For boolean fields, Value must be the literal string "true" or "false" based on whether
        the checkbox is marked (an "X" or similar mark inside/near the box counts as marked).
        For date fields, Value must be an ISO-8601 date string (YYYY-MM-DD) or null.
        For enum fields, Value must be exactly one of the allowed enum names given.

        Field catalogue (FieldName : Type : Description):
        {{string.Join("\n", HeaderFieldCatalogue.Select(f => $"- {f.FieldName} : {f.Type} : {f.Description}"))}}

        "LineItems" is an array reflecting Block 19-24, "Schedule of Supplies/Services" (one
        entry per CLIN/sub-line, including recurring service lines described in a Performance
        Work Statement location/frequency table if a formal bid schedule is only referenced,
        not attached as text). Each entry:
          { "ItemNumber", "Description", "Quantity", "Unit", "UnitPrice", "Amount",
            "FrequencyOfService", "PerformanceLocation", "SourcePage" }
        Leave numeric fields as plain number strings (no currency symbols/commas), or null if unknown.

        "Clauses" is an array covering every FAR/DFARS/agency clause and provision you can find,
        in every section of the document (Contract Clauses, Addendum to Contract Clauses,
        Solicitation Provisions, Addendum to Solicitation Provisions, Offeror Representations
        and Certifications). Each entry:
          { "ClauseNumber" (e.g. "52.212-4"), "Title", "EffectiveDate" (as printed, e.g. "Nov 2023"),
            "Category" ("FAR"|"DFARS"|"Agency"|"Other"), "IncorporationType" ("ByReference"|"FullText"),
            "Section" ("ContractClauses"|"AddendumToContractClauses"|"SolicitationProvisions"|
                       "AddendumToSolicitationProvisions"|"OfferorRepresentationsAndCertifications"),
            "IsChecked" (true if a checkbox next to the clause is marked applicable),
            "SourcePage" }
        For clauses incorporated by full text (e.g. 52.212-5, 52.219-28, 52.252-2), do not copy
        the entire clause body into JSON - just capture the identifying metadata above.

        Be conservative with Confidence: use 0.9+ only when the value is printed unambiguously
        and legibly; use 0.4-0.6 for values you inferred from context rather than reading
        directly; use under 0.3 for guesses.
        """;
}
