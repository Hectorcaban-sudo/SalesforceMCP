namespace SF1449ContractManager.Core.Models;

public enum AcquisitionType
{
    Unrestricted = 0,
    SetAside = 1
}

public enum MethodOfSolicitation
{
    Unspecified = 0,
    RequestForQuote = 1,
    InvitationForBid = 2,
    RequestForProposal = 3
}

public enum ClauseCategory
{
    FAR = 0,
    DFARS = 1,
    Agency = 2,
    Other = 3
}

public enum ClauseIncorporationType
{
    ByReference = 0,
    FullText = 1
}

/// <summary>
/// Which part of the SF-1449 package a clause/provision was found in. Useful for
/// reproducing the document's own grouping (Contract Clauses, Addendum to Contract
/// Clauses, Solicitation Provisions, Addendum to Solicitation Provisions, Offeror
/// Reps &amp; Certs) in the UI.
/// </summary>
public enum ClauseSection
{
    ContractClauses = 0,
    AddendumToContractClauses = 1,
    SolicitationProvisions = 2,
    AddendumToSolicitationProvisions = 3,
    OfferorRepresentationsAndCertifications = 4
}

public enum ContractStatus
{
    Draft = 0,
    PendingReview = 1,
    Reviewed = 2,
    Finalized = 3
}

public enum SmallBusinessSetAsideType
{
    None = 0,
    SmallBusiness = 1,
    HubZoneSmallBusiness = 2,
    ServiceDisabledVeteranOwned = 3,
    WomenOwnedSmallBusiness = 4,
    EconomicallyDisadvantagedWomenOwned = 5,
    EightA = 6
}
