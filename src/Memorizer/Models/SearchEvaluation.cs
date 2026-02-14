using System.Text.Json.Serialization;
using Memorizer.Models.ValueTypes;

namespace Memorizer.Models;

/// <summary>
/// A memory to be seeded into the evaluation corpus.
/// </summary>
public class EvaluationCorpusEntry
{
    /// <summary>
    /// Unique identifier within the corpus (e.g., "docker-compose-basics").
    /// Used by queries to reference expected results.
    /// </summary>
    public required string CorpusId { get; init; }

    /// <summary>
    /// Title for the memory.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Full text content of the memory (2-5 paragraphs).
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Tags for the memory.
    /// </summary>
    public required string[] Tags { get; init; }

    /// <summary>
    /// Memory type string.
    /// </summary>
    public string Type { get; init; } = "reference";

    /// <summary>
    /// Topic cluster this entry belongs to (for analysis).
    /// </summary>
    public required string Cluster { get; init; }
}

/// <summary>
/// A query to run against the corpus, with expected results.
/// </summary>
public class EvaluationQuery
{
    /// <summary>
    /// The search query text.
    /// </summary>
    public required string Query { get; init; }

    /// <summary>
    /// Category of query for analysis (e.g., "short-keyword", "natural-language", "exact-title").
    /// </summary>
    public required string QueryType { get; init; }

    /// <summary>
    /// Corpus IDs of memories that should be considered relevant for this query.
    /// Order does not matter for Recall but matters for NDCG graded relevance.
    /// </summary>
    public required string[] ExpectedRelevant { get; init; }

    /// <summary>
    /// The single corpus ID that should ideally be the #1 result.
    /// Used for MRR calculation.
    /// </summary>
    public string? ExpectedTopResult { get; init; }
}

/// <summary>
/// Result of running a single query against a specific search method.
/// </summary>
public class QueryEvaluationResult
{
    /// <summary>
    /// The query text.
    /// </summary>
    public required string Query { get; init; }

    /// <summary>
    /// The query type category.
    /// </summary>
    public required string QueryType { get; init; }

    /// <summary>
    /// Search method used (e.g., "Search", "SearchWithFullEmbedding", "SearchWithMetadataEmbedding").
    /// </summary>
    public required string Method { get; init; }

    /// <summary>
    /// Similarity threshold used.
    /// </summary>
    public double Threshold { get; init; }

    /// <summary>
    /// Expected corpus IDs for this query.
    /// </summary>
    public required string[] ExpectedRelevant { get; init; }

    /// <summary>
    /// Actual results returned, in order. Corpus IDs (or memory IDs if not in corpus).
    /// </summary>
    public required string[] ReturnedResults { get; init; }

    /// <summary>
    /// Similarity scores for each returned result, in the same order as ReturnedResults.
    /// </summary>
    public required double[] ReturnedSimilarities { get; init; }

    /// <summary>
    /// Reciprocal rank: 1/position of first relevant result (0 if none found).
    /// </summary>
    public double ReciprocalRank { get; init; }

    /// <summary>
    /// Whether at least one relevant result appeared in top K.
    /// </summary>
    public bool IsHit { get; init; }

    /// <summary>
    /// Recall@K for this query.
    /// </summary>
    public double Recall { get; init; }

    /// <summary>
    /// NDCG@K for this query.
    /// </summary>
    public double Ndcg { get; init; }
}

/// <summary>
/// Aggregate metrics for a (method, threshold) combination across all queries.
/// </summary>
public class EvaluationMethodResult
{
    /// <summary>
    /// Search method name.
    /// </summary>
    public required string Method { get; init; }

    /// <summary>
    /// Similarity threshold used.
    /// </summary>
    public double Threshold { get; init; }

    /// <summary>
    /// Mean Reciprocal Rank across all queries.
    /// </summary>
    public double Mrr { get; init; }

    /// <summary>
    /// Average Recall@K across all queries.
    /// </summary>
    public double RecallAtK { get; init; }

    /// <summary>
    /// K value used for Recall calculation.
    /// </summary>
    public int RecallK { get; init; }

    /// <summary>
    /// Fraction of queries with at least one relevant result in top K.
    /// </summary>
    public double HitRateAtK { get; init; }

    /// <summary>
    /// K value used for Hit Rate calculation.
    /// </summary>
    public int HitK { get; init; }

    /// <summary>
    /// Average NDCG@K across all queries.
    /// </summary>
    public double NdcgAtK { get; init; }

    /// <summary>
    /// K value used for NDCG calculation.
    /// </summary>
    public int NdcgK { get; init; }

    /// <summary>
    /// Total number of queries evaluated.
    /// </summary>
    public int TotalQueries { get; init; }

    /// <summary>
    /// Per-query breakdown.
    /// </summary>
    public required List<QueryEvaluationResult> PerQueryResults { get; init; }
}

/// <summary>
/// Complete evaluation run result.
/// </summary>
public class EvaluationRunResult
{
    /// <summary>
    /// Unique identifier for this run.
    /// </summary>
    public string RunId { get; init; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>
    /// When the evaluation was run.
    /// </summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Number of corpus entries seeded.
    /// </summary>
    public int CorpusSize { get; init; }

    /// <summary>
    /// Number of queries evaluated.
    /// </summary>
    public int QueryCount { get; init; }

    /// <summary>
    /// Results for each (method, threshold) combination.
    /// </summary>
    public required List<EvaluationMethodResult> Results { get; init; }

    /// <summary>
    /// Failure analysis: queries where no method achieved Hit@3.
    /// </summary>
    public required List<FailureAnalysis> Failures { get; init; }
}

/// <summary>
/// Detailed failure analysis for a query that returned no relevant results.
/// </summary>
public class FailureAnalysis
{
    /// <summary>
    /// The query that failed.
    /// </summary>
    public required string Query { get; init; }

    /// <summary>
    /// Query type category.
    /// </summary>
    public required string QueryType { get; init; }

    /// <summary>
    /// Expected corpus IDs.
    /// </summary>
    public required string[] ExpectedRelevant { get; init; }

    /// <summary>
    /// What each method actually returned (top 5 with scores).
    /// </summary>
    public required List<MethodFailureDetail> MethodDetails { get; init; }
}

/// <summary>
/// What a specific method returned for a failed query.
/// </summary>
public class MethodFailureDetail
{
    /// <summary>
    /// Search method name.
    /// </summary>
    public required string Method { get; init; }

    /// <summary>
    /// Threshold used.
    /// </summary>
    public double Threshold { get; init; }

    /// <summary>
    /// Top 5 results returned with their similarity scores.
    /// </summary>
    public required List<ReturnedResultDetail> TopResults { get; init; }

    /// <summary>
    /// Where the expected results actually ranked (even below threshold).
    /// Null if the expected result had no similarity at all.
    /// </summary>
    public required List<ExpectedResultDetail> ExpectedResultScores { get; init; }
}

/// <summary>
/// A result that was actually returned by the search.
/// </summary>
public class ReturnedResultDetail
{
    public required string CorpusId { get; init; }
    public required string Title { get; init; }
    public double Similarity { get; init; }
    public bool IsRelevant { get; init; }
}

/// <summary>
/// Where an expected result actually scored.
/// </summary>
public class ExpectedResultDetail
{
    public required string CorpusId { get; init; }
    public double? ActualSimilarity { get; init; }
    public double? DistanceFromThreshold { get; init; }
}

/// <summary>
/// Mapping between corpus IDs and actual Memorizer memory IDs after seeding.
/// </summary>
public class CorpusMapping
{
    /// <summary>
    /// Maps corpus ID to the Memorizer memory ID created during seeding.
    /// </summary>
    public required Dictionary<string, MemoryId> CorpusToMemoryId { get; init; }

    /// <summary>
    /// Reverse mapping: memory ID to corpus ID.
    /// </summary>
    public required Dictionary<MemoryId, string> MemoryToCorpusId { get; init; }

    /// <summary>
    /// The workspace ID where corpus was seeded.
    /// </summary>
    public WorkspaceId WorkspaceId { get; init; }
}

/// <summary>
/// Request body for the /api/search-eval/run endpoint.
/// </summary>
public class EvaluationRunRequest
{
    /// <summary>
    /// Similarity thresholds to test (default: 0.3, 0.5, 0.7).
    /// </summary>
    public double[]? Thresholds { get; init; }

    /// <summary>
    /// K value for Recall@K (default: 5).
    /// </summary>
    public int RecallK { get; init; } = 5;

    /// <summary>
    /// K value for Hit Rate@K (default: 3).
    /// </summary>
    public int HitK { get; init; } = 3;

    /// <summary>
    /// K value for NDCG@K (default: 10).
    /// </summary>
    public int NdcgK { get; init; } = 10;

    /// <summary>
    /// Maximum results to request from each search method.
    /// </summary>
    public int SearchLimit { get; init; } = 10;
}
