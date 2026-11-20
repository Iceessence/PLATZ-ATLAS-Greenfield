using System.Text.RegularExpressions;

internal static partial class GroundedLearning
{
    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Words();

    public static string[] Tokens(string text) =>
        Words().Matches(text.ToLowerInvariant()).Select(match => match.Value).ToArray();

    public static string[] Features(string text, bool bigrams, bool characters)
    {
        var words = Tokens(text);
        IEnumerable<string> features = words.Select(word => "w:" + word);
        if (bigrams)
            features = features.Concat(words.Zip(words.Skip(1), (left, right) => "b:" + left + " " + right));
        if (characters)
            features = features.Concat(words.SelectMany(word =>
            {
                var padded = "^" + word + "$";
                return Enumerable.Range(0, padded.Length - 2).Select(start => "c:" + padded.Substring(start, 3));
            }));
        return features.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    public static LinearModel Train(
        IReadOnlyList<TrainingRow> rows, string[] labels, int epochs, double rate, double decay)
    {
        var vocabulary = rows.SelectMany(row => row.Features)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var model = new LinearModel(labels, vocabulary,
            labels.Select(_ => new double[vocabulary.Length + 1]).ToArray());
        var indices = model.MakeIndex();
        var vectors = rows.Select(row => (Input: Encode(indices, row.Features),
            Target: Array.IndexOf(labels, row.Label))).ToArray();
        for (var epoch = 0; epoch < epochs; epoch++)
        {
            var step = rate / Math.Sqrt(1 + epoch / 12.0);
            var start = epoch * 31 % vectors.Length;
            for (var offset = 0; offset < vectors.Length; offset++)
            {
                var (input, target) = vectors[(start + offset) % vectors.Length];
                var probabilities = model.Probabilities(input);
                for (var label = 0; label < labels.Length; label++)
                {
                    var shift = step * ((label == target ? 1 : 0) - probabilities[label]);
                    var row = model.Weights[label];
                    row[0] += shift;
                    foreach (var feature in input)
                        row[feature] += shift - step * decay * row[feature];
                }
            }
        }
        return model;
    }

    public static Func<string[], Vote> NaiveBayes(TrainingRow[] rows)
    {
        var groups = rows.GroupBy(row => row.Label).OrderBy(group => group.Key, StringComparer.Ordinal);
        var vocabulary = rows.SelectMany(row => row.Features).ToHashSet(StringComparer.Ordinal);
        var counts = groups.Select(group => new
        {
            Label = group.Key,
            Prior = Math.Log((double)group.Count() / rows.Length),
            Features = group.SelectMany(row => row.Features).CountBy(feature => feature).ToDictionary(),
            Total = group.Sum(row => row.Features.Length) + vocabulary.Count
        }).ToArray();
        return input => Vote.From(counts.Select(group => (group.Label, group.Prior +
            input.Where(vocabulary.Contains).Sum(feature =>
                Math.Log((group.Features.GetValueOrDefault(feature) + 1.0) / group.Total)))));
    }

    public static Func<string[], Vote> Centroid(TrainingRow[] rows)
    {
        var idf = rows.SelectMany(row => row.Features).CountBy(feature => feature)
            .ToDictionary(pair => pair.Key, pair => 1 + Math.Log((rows.Length + 1.0) / (pair.Value + 1.0)));
        var centroids = rows.GroupBy(row => row.Label).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var counts = group.SelectMany(row => row.Features).CountBy(feature => feature)
                    .ToDictionary(pair => pair.Key, pair => pair.Value * idf[pair.Key]);
                return (group.Key, Counts: counts, Norm: Math.Sqrt(counts.Values.Sum(value => value * value)));
            }).ToArray();
        return input =>
        {
            var norm = Math.Sqrt(input.Sum(feature => Math.Pow(idf.GetValueOrDefault(feature), 2)));
            return Vote.From(centroids.Select(center => (center.Key, norm == 0 ? 0 : input.Sum(feature =>
                center.Counts.GetValueOrDefault(feature) * idf.GetValueOrDefault(feature)) / (norm * center.Norm))));
        };
    }

    public static Func<string[], Vote> AveragedPerceptron(TrainingRow[] rows, string[] labels)
    {
        var vocabulary = rows.SelectMany(row => row.Features)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var weights = labels.Select(_ => new double[vocabulary.Length + 1]).ToArray();
        var model = new LinearModel(labels, vocabulary, weights);
        var indices = model.MakeIndex();
        var vectors = rows.Select(row => (Input: Encode(indices, row.Features), Target: Array.IndexOf(labels, row.Label))).ToArray();
        var totals = weights.Select(row => new double[row.Length]).ToArray();
        for (var epoch = 0; epoch < 12; epoch++)
        {
            foreach (var (input, target) in vectors)
            {
                var predicted = Array.IndexOf(labels, model.Predict(input).Label);
                if (predicted != target)
                {
                    weights[target][0]++;
                    weights[predicted][0]--;
                    foreach (var feature in input)
                    {
                        weights[target][feature]++;
                        weights[predicted][feature]--;
                    }
                }
                for (var label = 0; label < weights.Length; label++)
                    for (var feature = 0; feature < weights[label].Length; feature++)
                        totals[label][feature] += weights[label][feature];
            }
        }
        var averaged = model with { Weights = totals.Select(row => row.Select(value => value / (12 * rows.Length)).ToArray()).ToArray() };
        return input => averaged.Predict(Encode(indices, input));
    }

    internal static int[] Encode(Dictionary<string, int> index, string[] features) =>
        features.Where(index.ContainsKey).Select(feature => index[feature]).ToArray();
}

internal sealed record TrainingRow(string Label, string[] Features);

internal sealed record LinearModel(string[] Labels, string[] Vocabulary, double[][] Weights)
{
    public Dictionary<string, int> MakeIndex() => Vocabulary.Select((feature, position) => (feature, position))
        .ToDictionary(item => item.feature, item => item.position + 1, StringComparer.Ordinal);

    public double[] Probabilities(int[] input)
    {
        var scores = Weights.Select(row => row[0] + input.Sum(feature => row[feature])).ToArray();
        var peak = scores.Max();
        var mass = scores.Select(score => Math.Exp(score - peak)).ToArray();
        var sum = mass.Sum();
        return mass.Select(value => value / sum).ToArray();
    }

    public Vote Predict(Dictionary<string, int> index, string[] features) =>
        Predict(GroundedLearning.Encode(index, features));

    public Vote Predict(int[] input) =>
        Vote.From(Labels.Zip(Probabilities(input)));
}

internal sealed record Vote(string Label, double Confidence, double Margin)
{
    public static Vote From(IEnumerable<(string Label, double Score)> scores)
    {
        var ranked = scores.OrderByDescending(item => item.Score).ThenBy(item => item.Label, StringComparer.Ordinal).ToArray();
        return new Vote(ranked[0].Label, ranked[0].Score, ranked[0].Score - ranked[1].Score);
    }
}
