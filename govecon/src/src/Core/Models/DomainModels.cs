// ============================================================
//  GovConRAG.Core — Domain Models
//  Enterprise RAG + Graph platform for GovCon contractors
// ============================================================

using System.Text.Json.Serialization;

namespace GovConRAG.Core.Models;

// ── Document & Chunk ──────────────────────────────────────────

public enum DocumentSource { SharePoint, Database, Excel, CustomApi, FileSystem }
public enum DocumentStatus { Pending, Processing, Indexed, Failed, Reconciling }
public enum AgentDomain   { Accounts, Contracts, Operations, Performance, Proposal, Competitor }

public class SourceDocument
{
    public Guid   Id            { get; init; } = Guid.NewGuid();
    public string Title         { get; set; } = "";
    public string SourceUrl     { get; set; } = "";
    public DocumentSource Source { get; set; }
    public DocumentStatus Status { get; set; } = DocumentStatus.Pending;
    public string MimeType      { get; set; } = "text/plain";
    public string ContentHash   { get; set; } = "";
    public long   SizeBytes     { get; set; }
    public DateTime CreatedAt   { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt   { get; set; } = DateTime.UtcNow;
    public DateTime? IndexedAt  { get; set; }
    public string TenantId      { get; set; } = "default";
    public string Domain        { get; set; } = "";          // accounts / contracts / ops
    public Dictionary<string, string> Metadata { get; set; } = new();
    public string? ErrorMessage { get; set; }
    public int    RetryCount    { get; set; }
    public Guid?  LiteGraphNodeId { get; set; }
}

public class DocumentChunk
{
    public Guid   Id            { get; init; } = Guid.NewGuid();
    public Guid   DocumentId    { get; set; }
    public int    ChunkIndex    { get; set; }
    public string Content       { get; set; } = "";
    public int    TokenCount    { get; set; }
    public string ChunkStrategy { get; set; } = "fixed";    // fixed | semantic | paragraph
    public float[]? Embedding   { get; set; }
    public DateTime CreatedAt   { get; init; } = DateTime.UtcNow;
    public Dictionary<string, string> Metadata { get; set; } = new();
    public Guid?  LiteGraphNodeId { get; set; }
}

// ── Ingestion Queue Message ───────────────────────────────────

public class IngestionMessage
{
    public Guid           MessageId   { get; init; } = Guid.NewGuid();
    public Guid           DocumentId  { get; set; }
    public DocumentSource Source      { get; set; }
    public string         SourceRef   { get; set; } = "";   // path / URL / DB query
    public string         Domain      { get; set; } = "";
    public string         TenantId    { get; set; } = "default";
    public DateTime       EnqueuedAt  { get; init; } = DateTime.UtcNow;
    public int            Priority    { get; set; } = 5;
    public Dictionary<string, string> AdditionalMeta { get; set; } = new();
}

// ── RAG Query / Response ──────────────────────────────────────

public class RagQuery
{
    public string   Question    { get; set; } = "";
    public string   TenantId    { get; set; } = "default";
    public string?  Domain      { get; set; }               // null = search all
    public int      TopK        { get; set; } = 8;
    public float    MinScore    { get; set; } = 0.72f;
    public bool     UseGraph    { get; set; } = true;
    public bool     Rerank      { get; set; } = true;
    public string   SessionId   { get; set; } = "";
    public string   UserId      { get; set; } = "";
}

public class RagResult
{
    public string   Answer      { get; set; } = "";
    public List<RetrievedChunk> Chunks { get; set; } = new();
    public List<GraphContext>   GraphNodes { get; set; } = new();
    public AgentDomain  RoutedTo { get; set; }
    public double   LatencyMs   { get; set; }
    public string   SessionId   { get; set; } = "";
    public string   TraceId     { get; set; } = Guid.NewGuid().ToString();
}

public class RetrievedChunk
{
    public Guid   ChunkId     { get; set; }
    public Guid   DocumentId  { get; set; }
    public string Content     { get; set; } = "";
    public float  Score       { get; set; }
    public string SourceTitle { get; set; } = "";
    public string SourceUrl   { get; set; } = "";
    public Dictionary<string, string> Metadata { get; set; } = new();
}

public class GraphContext
{
    public Guid   NodeId   { get; set; }
    public string Name     { get; set; } = "";
    public string Type     { get; set; } = "";
    public object? Data    { get; set; }
    public List<GraphEdgeInfo> Edges { get; set; } = new();
}

public class GraphEdgeInfo
{
    public Guid   EdgeId   { get; set; }
    public string Label    { get; set; } = "";
    public Guid   ToNodeId { get; set; }
    public string ToName   { get; set; } = "";
}

// ── GovCon Domain Models ──────────────────────────────────────

public class Contract
{
    public Guid    Id              { get; init; } = Guid.NewGuid();
    public string  ContractNumber  { get; set; } = "";
    public string  Agency          { get; set; } = "";
    public string  Naics           { get; set; } = "";
    public decimal Value           { get; set; }
    public DateTime AwardDate      { get; set; }
    public DateTime ExpiryDate     { get; set; }
    public string  PeriodOfPerf    { get; set; } = "";
    public string  SetAside        { get; set; } = "";
    public string  PlaceOfPerf     { get; set; } = "";
    public string  PrimaryContact  { get; set; } = "";
    public ContractStatus Status   { get; set; }
    public List<string> Keywords   { get; set; } = new();
}

public enum ContractStatus { Active, Expiring, Expired, Recompete }

public class PastPerformance
{
    public Guid    Id              { get; init; } = Guid.NewGuid();
    public Guid    ContractId     { get; set; }
    public string  ContractNumber  { get; set; } = "";
    public string  Agency          { get; set; } = "";
    public decimal ContractValue   { get; set; }
    public DateTime StartDate      { get; set; }
    public DateTime EndDate        { get; set; }
    public string  Scope           { get; set; } = "";
    public string  KeyAccomplishments { get; set; } = "";
    public float   CparsScore      { get; set; }            // 1-5
    public string  CparsNarrative  { get; set; } = "";
    public List<string> Capabilities { get; set; } = new();
    public List<string> TechStack   { get; set; } = new();
}

public class RfpOpportunity
{
    public Guid    Id              { get; init; } = Guid.NewGuid();
    public string  SamOpportunityId { get; set; } = "";
    public string  Title           { get; set; } = "";
    public string  Agency          { get; set; } = "";
    public string  Naics           { get; set; } = "";
    public decimal EstimatedValue  { get; set; }
    public DateTime DueDate        { get; set; }
    public string  SetAside        { get; set; } = "";
    public string  Description     { get; set; } = "";
    public List<string> Keywords   { get; set; } = new();
    public float   MatchScore      { get; set; }            // 0-1 calculated
    public float   WinProbability  { get; set; }            // 0-1 calculated
    public string  RecommendedAction { get; set; } = "";
}

public class ProposalDraft
{
    public Guid    Id              { get; init; } = Guid.NewGuid();
    public Guid    OpportunityId   { get; set; }
    public string  Volume          { get; set; } = "";      // Technical / Management / Price
    public string  Content         { get; set; } = "";
    public string  Status          { get; set; } = "Draft";
    public DateTime GeneratedAt    { get; set; } = DateTime.UtcNow;
    public List<string> SourceDocIds { get; set; } = new();
}

// ── Audit & Metrics ───────────────────────────────────────────

public class AuditEvent
{
    public Guid    Id           { get; init; } = Guid.NewGuid();
    public string  EventType    { get; set; } = "";
    public string  Actor        { get; set; } = "";
    public string  TenantId     { get; set; } = "";
    public string  ResourceId   { get; set; } = "";
    public string  ResourceType { get; set; } = "";
    public DateTime OccurredAt  { get; init; } = DateTime.UtcNow;
    public string  TraceId      { get; set; } = "";
    public string  SessionId    { get; set; } = "";
    public string  IpAddress    { get; set; } = "";
    public Dictionary<string, object> Details { get; set; } = new();
    public string  Outcome      { get; set; } = "Success";  // FedRAMP AU-2
}

public class IngestionMetrics
{
    public int    TotalDocuments      { get; set; }
    public int    IndexedDocuments    { get; set; }
    public int    FailedDocuments     { get; set; }
    public int    PendingDocuments    { get; set; }
    public long   TotalChunks         { get; set; }
    public double AvgChunkMs         { get; set; }
    public double AvgEmbedMs         { get; set; }
    public long   TotalTokensIndexed  { get; set; }
    public Dictionary<string, int> BySource { get; set; } = new();
    public Dictionary<string, int> ByDomain { get; set; } = new();
    public DateTime AsOf              { get; set; } = DateTime.UtcNow;
}

public class QueryMetrics
{
    public int    TotalQueries        { get; set; }
    public double AvgLatencyMs       { get; set; }
    public double P95LatencyMs       { get; set; }
    public double P99LatencyMs       { get; set; }
    public Dictionary<string, int> ByAgent { get; set; } = new();
    public int    CacheHits          { get; set; }
    public int    CacheMisses        { get; set; }
    public DateTime AsOf             { get; set; } = DateTime.UtcNow;
}
