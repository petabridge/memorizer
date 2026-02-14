namespace Memorizer.Services;

/// <summary>
/// Pure computation library for search quality metrics.
/// No IO - takes ranked result lists and relevance judgments, returns scores.
/// </summary>
public static class SearchQualityMetrics
{
    /// <summary>
    /// Reciprocal Rank: 1 / position of the first relevant result.
    /// Returns 0 if no relevant result is found in the ranked list.
    /// </summary>
    /// <param name="rankedResults">Ordered list of result identifiers.</param>
    /// <param name="relevantSet">Set of identifiers considered relevant.</param>
    public static double ReciprocalRank(IReadOnlyList<string> rankedResults, IReadOnlySet<string> relevantSet)
    {
        for (int i = 0; i < rankedResults.Count; i++)
        {
            if (relevantSet.Contains(rankedResults[i]))
                return 1.0 / (i + 1);
        }
        return 0.0;
    }

    /// <summary>
    /// Mean Reciprocal Rank across multiple queries.
    /// </summary>
    /// <param name="reciprocalRanks">RR values for each query.</param>
    public static double MeanReciprocalRank(IReadOnlyList<double> reciprocalRanks)
    {
        if (reciprocalRanks.Count == 0) return 0.0;
        return reciprocalRanks.Average();
    }

    /// <summary>
    /// Recall@K: fraction of relevant items that appear in the top K results.
    /// </summary>
    /// <param name="rankedResults">Ordered list of result identifiers.</param>
    /// <param name="relevantSet">Set of identifiers considered relevant.</param>
    /// <param name="k">Number of top results to consider.</param>
    public static double RecallAtK(IReadOnlyList<string> rankedResults, IReadOnlySet<string> relevantSet, int k)
    {
        if (relevantSet.Count == 0) return 1.0; // No relevant items = perfect recall vacuously

        int topK = Math.Min(k, rankedResults.Count);
        int found = 0;
        for (int i = 0; i < topK; i++)
        {
            if (relevantSet.Contains(rankedResults[i]))
                found++;
        }
        return (double)found / relevantSet.Count;
    }

    /// <summary>
    /// Hit Rate@K: 1 if at least one relevant result is in top K, 0 otherwise.
    /// </summary>
    /// <param name="rankedResults">Ordered list of result identifiers.</param>
    /// <param name="relevantSet">Set of identifiers considered relevant.</param>
    /// <param name="k">Number of top results to consider.</param>
    public static double HitRateAtK(IReadOnlyList<string> rankedResults, IReadOnlySet<string> relevantSet, int k)
    {
        int topK = Math.Min(k, rankedResults.Count);
        for (int i = 0; i < topK; i++)
        {
            if (relevantSet.Contains(rankedResults[i]))
                return 1.0;
        }
        return 0.0;
    }

    /// <summary>
    /// NDCG@K: Normalized Discounted Cumulative Gain at K.
    /// Uses binary relevance (1 for relevant, 0 for not) unless graded relevance is provided.
    /// </summary>
    /// <param name="rankedResults">Ordered list of result identifiers.</param>
    /// <param name="relevantSet">Set of identifiers considered relevant (binary relevance = 1).</param>
    /// <param name="k">Number of top results to consider.</param>
    /// <param name="gradedRelevance">Optional: maps result ID to relevance grade (higher = more relevant).
    /// If null, uses binary relevance from relevantSet.</param>
    public static double NdcgAtK(
        IReadOnlyList<string> rankedResults,
        IReadOnlySet<string> relevantSet,
        int k,
        IReadOnlyDictionary<string, double>? gradedRelevance = null)
    {
        double dcg = DcgAtK(rankedResults, relevantSet, k, gradedRelevance);
        double idcg = IdealDcgAtK(relevantSet, k, gradedRelevance);

        if (idcg == 0.0) return 0.0;
        return dcg / idcg;
    }

    /// <summary>
    /// Discounted Cumulative Gain at K.
    /// </summary>
    private static double DcgAtK(
        IReadOnlyList<string> rankedResults,
        IReadOnlySet<string> relevantSet,
        int k,
        IReadOnlyDictionary<string, double>? gradedRelevance)
    {
        int topK = Math.Min(k, rankedResults.Count);
        double dcg = 0.0;

        for (int i = 0; i < topK; i++)
        {
            double relevance = GetRelevance(rankedResults[i], relevantSet, gradedRelevance);
            // DCG formula: rel_i / log2(i + 2)  [position is 1-indexed, so i+2 for log]
            dcg += relevance / Math.Log2(i + 2);
        }

        return dcg;
    }

    /// <summary>
    /// Ideal DCG at K: the best possible DCG given the relevant set.
    /// </summary>
    private static double IdealDcgAtK(
        IReadOnlySet<string> relevantSet,
        int k,
        IReadOnlyDictionary<string, double>? gradedRelevance)
    {
        // Get all relevance scores, sorted descending
        List<double> idealScores;
        if (gradedRelevance != null)
        {
            idealScores = relevantSet
                .Select(id => gradedRelevance.TryGetValue(id, out var grade) ? grade : 1.0)
                .OrderDescending()
                .ToList();
        }
        else
        {
            idealScores = Enumerable.Repeat(1.0, relevantSet.Count).ToList();
        }

        int topK = Math.Min(k, idealScores.Count);
        double idcg = 0.0;

        for (int i = 0; i < topK; i++)
        {
            idcg += idealScores[i] / Math.Log2(i + 2);
        }

        return idcg;
    }

    private static double GetRelevance(
        string resultId,
        IReadOnlySet<string> relevantSet,
        IReadOnlyDictionary<string, double>? gradedRelevance)
    {
        if (gradedRelevance != null && gradedRelevance.TryGetValue(resultId, out var grade))
            return grade;

        return relevantSet.Contains(resultId) ? 1.0 : 0.0;
    }
}
