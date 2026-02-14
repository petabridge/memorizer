using System.Reflection;
using System.Text.Json;
using Memorizer.Models;
using Memorizer.Models.Enums;
using Memorizer.Models.ValueTypes;
using Memorizer.Services;
using Microsoft.AspNetCore.Mvc;

namespace Memorizer.Controllers;

[ApiController]
[Route("api/search-eval")]
public class SearchEvalController : ControllerBase
{
    private readonly IStorage _storage;
    private readonly ILogger<SearchEvalController> _logger;

    private const string EvalWorkspaceName = "[Search Eval] Test Corpus";
    private const string CorpusTag = "search-eval-corpus";

    public SearchEvalController(IStorage storage, ILogger<SearchEvalController> logger)
    {
        _storage = storage;
        _logger = logger;
    }

    /// <summary>
    /// Seed the synthetic corpus into a dedicated workspace.
    /// Returns a mapping of corpus IDs to Memorizer memory IDs.
    /// </summary>
    [HttpPost("seed-corpus")]
    public async Task<ActionResult<CorpusMapping>> SeedCorpus(CancellationToken cancellationToken)
    {
        try
        {
            // Load corpus from embedded resource
            var corpus = await LoadCorpusAsync();
            if (corpus == null || corpus.Count == 0)
                return BadRequest(new { message = "Failed to load synthetic corpus" });

            // Create or find the eval workspace
            var workspace = await GetOrCreateEvalWorkspaceAsync(cancellationToken);

            var corpusToMemory = new Dictionary<string, MemoryId>();
            var memoryToCorpus = new Dictionary<MemoryId, string>();
            int created = 0;
            int skipped = 0;

            foreach (var entry in corpus)
            {
                // Check if already seeded by searching for the corpus tag + corpusId tag
                var existingTag = $"corpus-id:{entry.CorpusId}";
                var tags = new List<string> { CorpusTag, existingTag };
                tags.AddRange(entry.Tags);

                var existing = await _storage.Search(
                    entry.Title,
                    limit: 1,
                    minSimilarity: new SimilarityScore(0.95),
                    filterTags: [CorpusTag, existingTag],
                    cancellationToken: cancellationToken);

                if (existing.Count > 0)
                {
                    corpusToMemory[entry.CorpusId] = existing[0].Id;
                    memoryToCorpus[existing[0].Id] = entry.CorpusId;
                    skipped++;
                    continue;
                }

                var memory = await _storage.StoreMemory(
                    type: entry.Type,
                    content: entry.Text,
                    source: "search-eval",
                    tags: tags.ToArray(),
                    confidence: Confidence.Full,
                    title: entry.Title,
                    owner: MemoryOwner.ForWorkspace(workspace.Id),
                    cancellationToken: cancellationToken);

                corpusToMemory[entry.CorpusId] = memory.Id;
                memoryToCorpus[memory.Id] = entry.CorpusId;
                created++;

                _logger.LogInformation("Seeded corpus entry {CorpusId} as memory {MemoryId}", entry.CorpusId, memory.Id);
            }

            var mapping = new CorpusMapping
            {
                CorpusToMemoryId = corpusToMemory,
                MemoryToCorpusId = memoryToCorpus,
                WorkspaceId = workspace.Id
            };

            return Ok(new
            {
                success = true,
                message = $"Corpus seeded: {created} created, {skipped} already existed",
                totalEntries = corpus.Count,
                created,
                skipped,
                workspaceId = workspace.Id.Value,
                mapping = corpusToMemory.ToDictionary(kv => kv.Key, kv => kv.Value.Value.ToString())
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error seeding corpus");
            return StatusCode(500, new { message = $"Error seeding corpus: {ex.Message}" });
        }
    }

    /// <summary>
    /// Run evaluation queries against all search methods and return metrics comparison.
    /// </summary>
    [HttpPost("run")]
    public async Task<ActionResult<EvaluationRunResult>> RunEvaluation(
        [FromBody] EvaluationRunRequest? request,
        CancellationToken cancellationToken)
    {
        try
        {
            request ??= new EvaluationRunRequest();
            var thresholds = request.Thresholds ?? [0.3, 0.5, 0.7];

            // Load corpus and queries
            var corpus = await LoadCorpusAsync();
            var queries = await LoadQueriesAsync();
            if (corpus == null || queries == null)
                return BadRequest(new { message = "Failed to load corpus or queries" });

            // Build corpus ID mapping from seeded data
            var corpusMapping = await BuildCorpusMappingAsync(cancellationToken);
            if (corpusMapping.CorpusToMemoryId.Count == 0)
                return BadRequest(new { message = "Corpus not seeded. Call POST /api/search-eval/seed-corpus first." });

            var methodResults = new List<EvaluationMethodResult>();

            // Define search methods to test
            var searchMethods = new (string Name, Func<string, int, SimilarityScore, CancellationToken, Task<List<Memory>>> Execute)[]
            {
                ("Search", (q, limit, sim, ct) => _storage.Search(q, limit, sim, cancellationToken: ct)),
                ("SearchWithFullEmbedding", (q, limit, sim, ct) => _storage.SearchWithFullEmbedding(q, limit, sim, cancellationToken: ct)),
                ("SearchWithMetadataEmbedding", (q, limit, sim, ct) => _storage.SearchWithMetadataEmbedding(q, limit, sim, cancellationToken: ct)),
            };

            foreach (var threshold in thresholds)
            {
                var minSimilarity = new SimilarityScore(threshold);

                foreach (var (methodName, executeSearch) in searchMethods)
                {
                    var perQueryResults = new List<QueryEvaluationResult>();

                    foreach (var query in queries)
                    {
                        var results = await executeSearch(query.Query, request.SearchLimit, minSimilarity, cancellationToken);

                        // Map returned memory IDs to corpus IDs
                        var returnedCorpusIds = results
                            .Select(m => corpusMapping.MemoryToCorpusId.TryGetValue(m.Id, out var cid) ? cid : m.Id.ToString())
                            .ToList();

                        var returnedSimilarities = results
                            .Select(m => m.Similarity.HasValue ? m.Similarity.Value.Value : 0.0)
                            .ToList();

                        var relevantSet = query.ExpectedRelevant.ToHashSet();

                        var rr = SearchQualityMetrics.ReciprocalRank(returnedCorpusIds, relevantSet);
                        var recall = SearchQualityMetrics.RecallAtK(returnedCorpusIds, relevantSet, request.RecallK);
                        var hit = SearchQualityMetrics.HitRateAtK(returnedCorpusIds, relevantSet, request.HitK);
                        var ndcg = SearchQualityMetrics.NdcgAtK(returnedCorpusIds, relevantSet, request.NdcgK);

                        perQueryResults.Add(new QueryEvaluationResult
                        {
                            Query = query.Query,
                            QueryType = query.QueryType,
                            Method = methodName,
                            Threshold = threshold,
                            ExpectedRelevant = query.ExpectedRelevant,
                            ReturnedResults = returnedCorpusIds.ToArray(),
                            ReturnedSimilarities = returnedSimilarities.ToArray(),
                            ReciprocalRank = rr,
                            IsHit = hit > 0,
                            Recall = recall,
                            Ndcg = ndcg
                        });
                    }

                    methodResults.Add(new EvaluationMethodResult
                    {
                        Method = methodName,
                        Threshold = threshold,
                        Mrr = SearchQualityMetrics.MeanReciprocalRank(perQueryResults.Select(r => r.ReciprocalRank).ToList()),
                        RecallAtK = perQueryResults.Average(r => r.Recall),
                        RecallK = request.RecallK,
                        HitRateAtK = perQueryResults.Average(r => r.IsHit ? 1.0 : 0.0),
                        HitK = request.HitK,
                        NdcgAtK = perQueryResults.Average(r => r.Ndcg),
                        NdcgK = request.NdcgK,
                        TotalQueries = perQueryResults.Count,
                        PerQueryResults = perQueryResults
                    });
                }
            }

            // Build failure analysis for queries where NO method achieved Hit@3 at the lowest threshold
            var lowestThreshold = thresholds.Min();
            var failures = BuildFailureAnalysis(queries, methodResults, lowestThreshold, request.HitK);

            var result = new EvaluationRunResult
            {
                CorpusSize = corpus.Count,
                QueryCount = queries.Count,
                Results = methodResults,
                Failures = failures
            };

            _logger.LogInformation("Evaluation run {RunId} complete: {Methods} method-threshold combos, {Queries} queries",
                result.RunId, methodResults.Count, queries.Count);

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error running evaluation");
            return StatusCode(500, new { message = $"Error running evaluation: {ex.Message}" });
        }
    }

    /// <summary>
    /// Delete the seeded evaluation corpus.
    /// </summary>
    [HttpDelete("corpus")]
    public async Task<ActionResult> DeleteCorpus(CancellationToken cancellationToken)
    {
        try
        {
            var mapping = await BuildCorpusMappingAsync(cancellationToken);
            int deleted = 0;

            foreach (var memoryId in mapping.CorpusToMemoryId.Values)
            {
                var success = await _storage.Delete(memoryId, cancellationToken);
                if (success) deleted++;
            }

            // Try to delete the workspace too
            if (mapping.WorkspaceId != WorkspaceId.Empty)
            {
                try
                {
                    await _storage.DeleteWorkspaceAsync(mapping.WorkspaceId, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not delete eval workspace (may have other content)");
                }
            }

            return Ok(new
            {
                success = true,
                message = $"Deleted {deleted} corpus memories",
                deleted
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting corpus");
            return StatusCode(500, new { message = $"Error deleting corpus: {ex.Message}" });
        }
    }

    /// <summary>
    /// Get a summary of the current corpus mapping (what's seeded).
    /// </summary>
    [HttpGet("corpus-status")]
    public async Task<ActionResult> GetCorpusStatus(CancellationToken cancellationToken)
    {
        try
        {
            var mapping = await BuildCorpusMappingAsync(cancellationToken);
            var corpus = await LoadCorpusAsync();

            return Ok(new
            {
                success = true,
                totalCorpusEntries = corpus?.Count ?? 0,
                seededEntries = mapping.CorpusToMemoryId.Count,
                workspaceId = mapping.WorkspaceId != WorkspaceId.Empty ? mapping.WorkspaceId.Value.ToString() : null,
                isFullySeeded = corpus != null && mapping.CorpusToMemoryId.Count == corpus.Count,
                mapping = mapping.CorpusToMemoryId.ToDictionary(kv => kv.Key, kv => kv.Value.Value.ToString())
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting corpus status");
            return StatusCode(500, new { message = $"Error: {ex.Message}" });
        }
    }

    #region Private helpers

    private async Task<List<EvaluationCorpusEntry>?> LoadCorpusAsync()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("synthetic_corpus.json", StringComparison.OrdinalIgnoreCase));

        if (resourceName == null)
        {
            _logger.LogError("Could not find synthetic_corpus.json embedded resource. Available: {Resources}",
                string.Join(", ", assembly.GetManifestResourceNames()));
            return null;
        }

        await using var stream = assembly.GetManifestResourceStream(resourceName)!;
        return await JsonSerializer.DeserializeAsync<List<EvaluationCorpusEntry>>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
    }

    private async Task<List<EvaluationQuery>?> LoadQueriesAsync()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("evaluation_queries.json", StringComparison.OrdinalIgnoreCase));

        if (resourceName == null)
        {
            _logger.LogError("Could not find evaluation_queries.json embedded resource. Available: {Resources}",
                string.Join(", ", assembly.GetManifestResourceNames()));
            return null;
        }

        await using var stream = assembly.GetManifestResourceStream(resourceName)!;
        return await JsonSerializer.DeserializeAsync<List<EvaluationQuery>>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
    }

    private async Task<Workspace> GetOrCreateEvalWorkspaceAsync(CancellationToken cancellationToken)
    {
        // Search for existing eval workspace
        var workspaces = await _storage.SearchWorkspacesAsync(EvalWorkspaceName, cancellationToken: cancellationToken);
        var existing = workspaces.FirstOrDefault(w => w.Workspace.Name == EvalWorkspaceName);
        if (existing != null)
            return existing.Workspace;

        return await _storage.CreateWorkspaceAsync(
            EvalWorkspaceName,
            "Workspace for search quality evaluation corpus. Safe to delete.",
            cancellationToken: cancellationToken);
    }

    private async Task<CorpusMapping> BuildCorpusMappingAsync(CancellationToken cancellationToken)
    {
        var corpusToMemory = new Dictionary<string, MemoryId>();
        var memoryToCorpus = new Dictionary<MemoryId, string>();
        var workspaceId = WorkspaceId.Empty;

        // Find the eval workspace
        var workspaces = await _storage.SearchWorkspacesAsync(EvalWorkspaceName, cancellationToken: cancellationToken);
        var evalWorkspace = workspaces.FirstOrDefault(w => w.Workspace.Name == EvalWorkspaceName);

        if (evalWorkspace != null)
        {
            workspaceId = evalWorkspace.Workspace.Id;

            // Search for all corpus-tagged memories
            // Use a very low threshold to find all tagged memories
            var corpusMemories = await _storage.Search(
                CorpusTag,
                limit: 100,
                minSimilarity: new SimilarityScore(0.01),
                filterTags: [CorpusTag],
                cancellationToken: cancellationToken);

            foreach (var memory in corpusMemories)
            {
                if (memory.Tags == null) continue;
                var corpusIdTag = memory.Tags.FirstOrDefault(t => t.StartsWith("corpus-id:"));
                if (corpusIdTag != null)
                {
                    var corpusId = corpusIdTag["corpus-id:".Length..];
                    corpusToMemory[corpusId] = memory.Id;
                    memoryToCorpus[memory.Id] = corpusId;
                }
            }
        }

        return new CorpusMapping
        {
            CorpusToMemoryId = corpusToMemory,
            MemoryToCorpusId = memoryToCorpus,
            WorkspaceId = workspaceId
        };
    }

    private static List<FailureAnalysis> BuildFailureAnalysis(
        List<EvaluationQuery> queries,
        List<EvaluationMethodResult> methodResults,
        double lowestThreshold,
        int hitK)
    {
        var failures = new List<FailureAnalysis>();

        foreach (var query in queries)
        {
            // Skip negative queries (expected empty results)
            if (query.ExpectedRelevant.Length == 0) continue;

            // Check if ANY method at the lowest threshold achieved a hit
            bool anyHit = methodResults
                .Where(mr => Math.Abs(mr.Threshold - lowestThreshold) < 0.001)
                .SelectMany(mr => mr.PerQueryResults)
                .Where(pq => pq.Query == query.Query)
                .Any(pq => pq.IsHit);

            if (!anyHit)
            {
                var methodDetails = methodResults
                    .Where(mr => Math.Abs(mr.Threshold - lowestThreshold) < 0.001)
                    .Select(mr =>
                    {
                        var pq = mr.PerQueryResults.FirstOrDefault(p => p.Query == query.Query);
                        return new MethodFailureDetail
                        {
                            Method = mr.Method,
                            Threshold = mr.Threshold,
                            TopResults = pq?.ReturnedResults
                                .Zip(pq.ReturnedSimilarities)
                                .Take(5)
                                .Select((pair, _) => new ReturnedResultDetail
                                {
                                    CorpusId = pair.Item1,
                                    Title = pair.Item1, // Corpus ID as title fallback
                                    Similarity = pair.Item2,
                                    IsRelevant = query.ExpectedRelevant.Contains(pair.Item1)
                                })
                                .ToList() ?? [],
                            ExpectedResultScores = query.ExpectedRelevant
                                .Select(expectedId =>
                                {
                                    var idx = pq?.ReturnedResults.ToList().IndexOf(expectedId) ?? -1;
                                    double? similarity = idx >= 0 && pq != null ? pq.ReturnedSimilarities[idx] : null;
                                    return new ExpectedResultDetail
                                    {
                                        CorpusId = expectedId,
                                        ActualSimilarity = similarity,
                                        DistanceFromThreshold = similarity.HasValue ? mr.Threshold - similarity.Value : null
                                    };
                                })
                                .ToList()
                        };
                    })
                    .ToList();

                failures.Add(new FailureAnalysis
                {
                    Query = query.Query,
                    QueryType = query.QueryType,
                    ExpectedRelevant = query.ExpectedRelevant,
                    MethodDetails = methodDetails
                });
            }
        }

        return failures;
    }

    #endregion
}
