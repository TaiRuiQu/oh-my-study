namespace OhMyStudy.Domain;

public sealed record SearchModel(string BaseUrl, string Model, string? KeyRef = null, string ApiType = "openai");
public sealed record QuestionVector(string QuestionId, string ContentHash, float[] Values);
public sealed record VectorMatch(string QuestionId, double Score);

public static class VectorSearch
{
    public static float[] Normalize(ReadOnlySpan<float> values)
    {
        if (values.Length is < 1 or > 65536) throw new InvalidDataException("Invalid embedding dimensions");
        double norm = 0;
        foreach (float value in values)
        {
            if (!float.IsFinite(value)) throw new InvalidDataException("Embedding contains a non-finite value");
            norm += (double)value * value;
        }
        if (norm == 0) throw new InvalidDataException("Embedding must not be a zero vector");
        norm = Math.Sqrt(norm);
        var result = new float[values.Length];
        for (int index = 0; index < values.Length; index++) result[index] = (float)(values[index] / norm);
        return result;
    }

    public static IReadOnlyList<VectorMatch> Find(float[] query, IEnumerable<QuestionVector> vectors, int limit, CancellationToken cancellationToken = default)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        var normalized = Normalize(query);
        var nearest = new PriorityQueue<VectorMatch, double>();
        foreach (var vector in vectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (vector.Values.Length != normalized.Length) throw new InvalidDataException("Embedding dimensions changed. Rebuild the semantic search index.");
            var values = Normalize(vector.Values);
            double score = 0;
            for (int index = 0; index < values.Length; index++) score += (double)normalized[index] * values[index];
            var match = new VectorMatch(vector.QuestionId, Math.Clamp(score, -1, 1));
            nearest.Enqueue(match, score);
            if (nearest.Count > limit) nearest.Dequeue();
        }
        return nearest.UnorderedItems.Select(item => item.Element).OrderByDescending(item => item.Score).ThenBy(item => item.QuestionId, StringComparer.Ordinal).ToList();
    }
}
