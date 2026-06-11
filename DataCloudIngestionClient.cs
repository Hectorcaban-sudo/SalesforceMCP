using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YourCompany.Data360;

/// <summary>
/// One chunk record. Property names are serialized to snake_case to match
/// the Ingestion API schema (field names are case-sensitive).
/// </summary>
public sealed class DocumentChunk
{
    [JsonPropertyName("chunk_id")] public required string ChunkId { get; init; }
    [JsonPropertyName("document_id")] public required string DocumentId { get; init; }
    [JsonPropertyName("document_title")] public string? DocumentTitle { get; init; }
    [JsonPropertyName("chunk_sequence")] public int ChunkSequence { get; init; }
    [JsonPropertyName("content")] public required string Content { get; init; }
    [JsonPropertyName("source_url")] public string? SourceUrl { get; init; }
    [JsonPropertyName("author")] public string? Author { get; init; }
    [JsonPropertyName("category")] public string? Category { get; init; }
    [JsonPropertyName("language")] public string? Language { get; init; }
    [JsonPropertyName("modified_date")] public DateTimeOffset ModifiedDate { get; init; }
    [JsonPropertyName("ingested_date")] public DateTimeOffset IngestedDate { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class DataCloudOptions
{
    /// <summary>e.g. https://yourorg.my.salesforce.com</summary>
    public required string LoginUrl { get; init; }
    /// <summary>Consumer key of the External Client App / Connected App.</summary>
    public required string ClientId { get; init; }
    /// <summary>Consumer secret (client credentials flow).</summary>
    public required string ClientSecret { get; init; }
    /// <summary>API name of the Ingestion API connector (the "source").</summary>
    public required string SourceApiName { get; init; }
    /// <summary>Object name from the schema, e.g. DocumentChunk.</summary>
    public string ObjectName { get; init; } = "DocumentChunk";
}

/// <summary>
/// Client for the Data 360 (Data Cloud) Ingestion API.
/// Handles core OAuth (client credentials), the Data Cloud token exchange,
/// token caching, batching (200 records / ~1 MB per request), and retries.
/// Register as a singleton; it is safe for concurrent use.
/// </summary>
public sealed class DataCloudIngestionClient(HttpClient http, DataCloudOptions options)
{
    private const int MaxRecordsPerBatch = 200;
    private const int MaxBatchBytes = 950_000; // stay under the 1 MB payload limit
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _dcToken;
    private string? _dcInstanceUrl;
    private DateTimeOffset _dcTokenExpiresAt = DateTimeOffset.MinValue;

    // ---------------------------------------------------------------------
    // Public API
    // ---------------------------------------------------------------------

    /// <summary>Upserts chunks via the streaming endpoint, batching automatically.</summary>
    public async Task IngestAsync(IEnumerable<DocumentChunk> chunks, CancellationToken ct = default)
    {
        foreach (var batch in Batch(chunks))
            await SendBatchAsync(batch, ct);
    }

    /// <summary>
    /// Chunks a full document's text and upserts the resulting chunk records.
    /// Chunk ids are deterministic ({documentId}-0001, -0002, ...) so re-ingesting
    /// an updated document overwrites in place. Pass <paramref name="previousChunkCount"/>
    /// (from your own tracking) so chunks that no longer exist are deleted.
    /// Returns the chunk count, which you should persist for the next update.
    /// </summary>
    public async Task<int> IngestDocumentAsync(
        string documentId,
        string documentTitle,
        string text,
        string? sourceUrl = null,
        string? author = null,
        string? category = null,
        string? language = "en",
        DateTimeOffset? modifiedDate = null,
        int previousChunkCount = 0,
        ChunkingOptions? chunking = null,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var parts = DocumentChunker.Split(text, chunking);

        var chunks = parts.Select((content, i) => new DocumentChunk
        {
            ChunkId = $"{documentId}-{i + 1:D4}",
            DocumentId = documentId,
            DocumentTitle = documentTitle,
            ChunkSequence = i + 1,
            Content = content,
            SourceUrl = sourceUrl,
            Author = author,
            Category = category,
            Language = language,
            ModifiedDate = modifiedDate ?? now,
            IngestedDate = now
        }).ToList();

        await IngestAsync(chunks, ct);

        if (previousChunkCount > chunks.Count)
        {
            var orphans = Enumerable.Range(chunks.Count + 1, previousChunkCount - chunks.Count)
                                    .Select(i => $"{documentId}-{i:D4}");
            await DeleteAsync(orphans, ct);
        }

        return chunks.Count;
    }

    /// <summary>Deletes records by primary key (chunk_id). Max 200 ids per call.</summary>
    public async Task DeleteAsync(IEnumerable<string> chunkIds, CancellationToken ct = default)
    {
        foreach (var idBatch in chunkIds.Chunk(MaxRecordsPerBatch))
        {
            var ids = string.Join(",", idBatch.Select(Uri.EscapeDataString));
            await SendWithRetryAsync(() => new HttpRequestMessage(
                HttpMethod.Delete,
                $"{_dcInstanceUrl}/api/v1/ingest/sources/{options.SourceApiName}/{options.ObjectName}?ids={ids}"), ct);
        }
    }

    // ---------------------------------------------------------------------
    // Batching
    // ---------------------------------------------------------------------

    private static IEnumerable<List<DocumentChunk>> Batch(IEnumerable<DocumentChunk> chunks)
    {
        var batch = new List<DocumentChunk>(MaxRecordsPerBatch);
        var size = 0;

        foreach (var chunk in chunks)
        {
            var recordSize = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(chunk, Json)) + 1;
            if (batch.Count > 0 && (batch.Count >= MaxRecordsPerBatch || size + recordSize > MaxBatchBytes))
            {
                yield return batch;
                batch = new List<DocumentChunk>(MaxRecordsPerBatch);
                size = 0;
            }
            batch.Add(chunk);
            size += recordSize;
        }

        if (batch.Count > 0)
            yield return batch;
    }

    private Task SendBatchAsync(List<DocumentChunk> batch, CancellationToken ct) =>
        SendWithRetryAsync(() =>
        {
            var req = new HttpRequestMessage(
                HttpMethod.Post,
                $"{_dcInstanceUrl}/api/v1/ingest/sources/{options.SourceApiName}/{options.ObjectName}");
            req.Content = JsonContent.Create(new { data = batch }, options: Json);
            return req;
        }, ct);

    // ---------------------------------------------------------------------
    // Request pipeline: auth header, 401 refresh, 429/5xx backoff
    // ---------------------------------------------------------------------

    private async Task SendWithRetryAsync(Func<HttpRequestMessage> requestFactory, CancellationToken ct)
    {
        const int maxAttempts = 5;

        for (var attempt = 1; ; attempt++)
        {
            var token = await GetDataCloudTokenAsync(ct);
            var req = requestFactory();
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            HttpResponseMessage resp;
            try
            {
                resp = await http.SendAsync(req, ct);
            }
            catch (HttpRequestException) when (attempt < maxAttempts)
            {
                await DelayAsync(attempt, ct);
                continue;
            }

            if (resp.IsSuccessStatusCode) // streaming ingest returns 202 Accepted
                return;

            if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt < maxAttempts)
            {
                InvalidateToken();
                continue;
            }

            if ((resp.StatusCode == HttpStatusCode.TooManyRequests || (int)resp.StatusCode >= 500) && attempt < maxAttempts)
            {
                await DelayAsync(attempt, ct, resp.Headers.RetryAfter?.Delta);
                continue;
            }

            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Ingestion API call failed ({(int)resp.StatusCode}): {body}");
        }
    }

    private static Task DelayAsync(int attempt, CancellationToken ct, TimeSpan? retryAfter = null)
    {
        var delay = retryAfter ?? TimeSpan.FromSeconds(Math.Pow(2, attempt)) +
                    TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500));
        return Task.Delay(delay, ct);
    }

    // ---------------------------------------------------------------------
    // Auth: core token (client credentials) -> Data Cloud token exchange
    // ---------------------------------------------------------------------

    private void InvalidateToken() => _dcTokenExpiresAt = DateTimeOffset.MinValue;

    private async Task<string> GetDataCloudTokenAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow < _dcTokenExpiresAt && _dcToken is not null)
            return _dcToken;

        await _tokenLock.WaitAsync(ct);
        try
        {
            if (DateTimeOffset.UtcNow < _dcTokenExpiresAt && _dcToken is not null)
                return _dcToken;

            // Step 1: core org access token
            var coreResp = await http.PostAsync($"{options.LoginUrl}/services/oauth2/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = options.ClientId,
                    ["client_secret"] = options.ClientSecret
                }), ct);
            coreResp.EnsureSuccessStatusCode();
            var core = await coreResp.Content.ReadFromJsonAsync<TokenResponse>(ct)
                       ?? throw new InvalidOperationException("Empty core token response.");

            // Step 2: exchange for a Data Cloud token
            var dcResp = await http.PostAsync($"{core.InstanceUrl}/services/a360/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:salesforce:grant-type:external:cdp",
                    ["subject_token"] = core.AccessToken,
                    ["subject_token_type"] = "urn:ietf:params:oauth:token-type:access_token"
                }), ct);
            dcResp.EnsureSuccessStatusCode();
            var dc = await dcResp.Content.ReadFromJsonAsync<TokenResponse>(ct)
                     ?? throw new InvalidOperationException("Empty Data Cloud token response.");

            _dcToken = dc.AccessToken;
            // instance_url comes back without a scheme (e.g. abc123.c360a.salesforce.com)
            _dcInstanceUrl = dc.InstanceUrl.StartsWith("http") ? dc.InstanceUrl : $"https://{dc.InstanceUrl}";
            _dcTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(dc.ExpiresIn - 120); // refresh 2 min early
            return _dcToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public required string AccessToken { get; init; }
        [JsonPropertyName("instance_url")] public required string InstanceUrl { get; init; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; } = 7200;
    }
}

/// <summary>Tuning knobs for the chunker.</summary>
public sealed class ChunkingOptions
{
    /// <summary>Target chunk size in (estimated) tokens. Keep under the search
    /// index's 512-token default so Data 360 doesn't re-split your chunks.</summary>
    public int MaxTokensPerChunk { get; init; } = 450;

    /// <summary>Tokens repeated from the end of one chunk at the start of the
    /// next, so retrieval doesn't lose context at chunk boundaries.</summary>
    public int OverlapTokens { get; init; } = 50;

    /// <summary>Token estimation heuristic (~4 chars/token for English prose).
    /// Swap in a real tokenizer (e.g. Microsoft.ML.Tokenizers) for exact counts.</summary>
    public double CharsPerToken { get; init; } = 4.0;

    /// <summary>Trailing chunks smaller than this are merged into the previous
    /// chunk instead of being emitted as near-empty records.</summary>
    public int MinChunkChars { get; init; } = 200;
}

/// <summary>
/// Recursive character splitter for RAG. Splits on paragraph breaks first,
/// then line breaks, sentence ends, and finally words, so each chunk stays
/// as semantically intact as possible while respecting the size limit.
/// </summary>
public static class DocumentChunker
{
    private static readonly string[] Separators = ["\n\n", "\n", ". ", "? ", "! ", "; ", " "];

    public static IReadOnlyList<string> Split(string text, ChunkingOptions? options = null)
    {
        var o = options ?? new ChunkingOptions();
        var maxChars = (int)(o.MaxTokensPerChunk * o.CharsPerToken);
        var overlapChars = (int)(o.OverlapTokens * o.CharsPerToken);

        text = Normalize(text);
        if (text.Length == 0)
            return [];
        if (text.Length <= maxChars)
            return [text];

        var pieces = SplitRecursive(text, maxChars, 0);
        return Assemble(pieces, maxChars, overlapChars, o.MinChunkChars);
    }

    private static string Normalize(string text)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        while (text.Contains("\n\n\n"))
            text = text.Replace("\n\n\n", "\n\n");
        return text.Trim();
    }

    /// <summary>Splits text into pieces no larger than maxChars, trying the
    /// gentlest separator first and only escalating for oversized pieces.</summary>
    private static List<string> SplitRecursive(string text, int maxChars, int level)
    {
        if (text.Length <= maxChars)
            return [text];

        if (level >= Separators.Length) // no separators left: hard slice
        {
            var slices = new List<string>();
            for (var i = 0; i < text.Length; i += maxChars)
                slices.Add(text.Substring(i, Math.Min(maxChars, text.Length - i)));
            return slices;
        }

        var sep = Separators[level];
        var parts = text.Split(sep, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
            return SplitRecursive(text, maxChars, level + 1);

        // Re-attach the separator so sentences keep their punctuation.
        var keep = sep.TrimEnd() is { Length: > 0 } p && p != sep ? p + " " : sep;
        var result = new List<string>();
        for (var i = 0; i < parts.Length; i++)
        {
            var piece = i < parts.Length - 1 && sep != "\n\n" && sep != "\n" && sep != " "
                ? parts[i] + keep.TrimEnd() + " "
                : parts[i];
            result.AddRange(SplitRecursive(piece.Trim(), maxChars, level + 1));
        }
        return result;
    }

    /// <summary>Greedily packs pieces into chunks up to maxChars, adding a
    /// word-boundary-snapped overlap from the previous chunk's tail.</summary>
    private static List<string> Assemble(List<string> pieces, int maxChars, int overlapChars, int minChunkChars)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();

        foreach (var piece in pieces)
        {
            if (current.Length > 0 && current.Length + piece.Length + 1 > maxChars)
            {
                chunks.Add(current.ToString().Trim());
                current.Clear();
                if (overlapChars > 0)
                    current.Append(TailOnWordBoundary(chunks[^1], overlapChars)).Append(' ');
            }
            if (current.Length > 0)
                current.Append(' ');
            current.Append(piece);
        }

        if (current.Length > 0)
        {
            var last = current.ToString().Trim();
            if (last.Length < minChunkChars && chunks.Count > 0 && chunks[^1].Length + last.Length + 1 <= maxChars + overlapChars)
                chunks[^1] = chunks[^1] + " " + last;
            else
                chunks.Add(last);
        }

        return chunks;
    }

    private static string TailOnWordBoundary(string text, int maxChars)
    {
        if (text.Length <= maxChars)
            return text;
        var start = text.Length - maxChars;
        var space = text.IndexOf(' ', start);
        return space >= 0 ? text[(space + 1)..] : text[start..];
    }
}

/* ---------------------------------------------------------------------------
Example usage (e.g. Program.cs):

var options = new DataCloudOptions
{
    LoginUrl      = "https://yourorg.my.salesforce.com",
    ClientId      = Environment.GetEnvironmentVariable("SF_CLIENT_ID")!,
    ClientSecret  = Environment.GetEnvironmentVariable("SF_CLIENT_SECRET")!,
    SourceApiName = "Document_Chunks_API",   // your Ingestion API connector name
    ObjectName    = "DocumentChunk"
};

var client = new DataCloudIngestionClient(new HttpClient(), options);

// Hand the client a whole document; it chunks, ingests, and cleans up.
var fullText = await File.ReadAllTextAsync("refund-policy.txt");

var chunkCount = await client.IngestDocumentAsync(
    documentId: "doc-42",
    documentTitle: "Refund policy",
    text: fullText,
    sourceUrl: "https://intranet.example.com/policies/refunds",
    category: "Policies",
    previousChunkCount: 10); // last known count -> orphaned chunks get deleted

// Persist chunkCount (e.g. in your app's DB) for the next re-ingest.

// Custom chunking, e.g. smaller chunks for dense FAQ content:
await client.IngestDocumentAsync("doc-43", "FAQ", faqText,
    chunking: new ChunkingOptions { MaxTokensPerChunk = 250, OverlapTokens = 30 });
--------------------------------------------------------------------------- */
