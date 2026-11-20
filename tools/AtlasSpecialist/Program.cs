using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

if (args.Length != 2)
{
    Console.Error.WriteLine("Pass the specialist source directory and a writable output directory.");
    return 2;
}

var source = Path.GetFullPath(args[0]);
var output = Directory.CreateDirectory(args[1]).FullName;
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true, NewLine = "\n" };
var configPath = Path.Combine(source, "training.json");
var config = JsonSerializer.Deserialize<TrainingConfig>(File.ReadAllText(configPath), json)
    ?? throw new InvalidDataException("The specialist training configuration is missing.");
var train = ReadSet(Path.Combine(source, "train.tsv"));
var holdout = ReadSet(Path.Combine(source, "holdout.tsv"));
var trainText = train.Select(example => Normalize(example.Text)).ToHashSet(StringComparer.Ordinal);
if (holdout.Any(example => trainText.Contains(Normalize(example.Text))) ||
    train.Select(example => example.Group).Intersect(holdout.Select(example => example.Group), StringComparer.Ordinal).Any())
    throw new InvalidDataException("The held-out data overlaps the training examples or groups.");
var labels = train.Select(example => example.Label).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
if (labels.Length != 5 || !labels.Contains("unsupported") || !labels.ToHashSet(StringComparer.Ordinal).SetEquals(holdout.Select(item => item.Label)))
    throw new InvalidDataException("Both splits must contain each route.");

var vocabulary = train.SelectMany(item => Features(item.Text)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
var classes = labels.Select(label =>
{
    var examples = train.Where(example => example.Label == label).ToArray();
    var counts = examples.SelectMany(example => Features(example.Text)).GroupBy(x => x, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
    var denominator = counts.Values.Sum() + config.Alpha * vocabulary.Length;
    return new LearnedClass(label, examples.Length, Math.Log((double)examples.Length / train.Length),
        vocabulary.ToDictionary(feature => feature, feature => Math.Log((counts.GetValueOrDefault(feature) + config.Alpha) / denominator),
            StringComparer.Ordinal));
}).ToArray();
var model = new Model(config.ModelVersion, Hash(Path.Combine(source, "train.tsv")), Hash(configPath), train.Length, vocabulary, classes);
var modelPath = Path.Combine(output, "model.json");
var reportPath = Path.Combine(output, "evaluation.json");
File.WriteAllText(modelPath, JsonSerializer.Serialize(model, json) + "\n");
var reload = JsonSerializer.Deserialize<Model>(File.ReadAllText(modelPath), json)
    ?? throw new InvalidDataException("The written model could not be reopened.");
var majority = reload.Classes.OrderByDescending(route => route.Examples).ThenBy(route => route.Label, StringComparer.Ordinal).First().Label;
var predictions = holdout.Select(row =>
{
    var learned = Predict(reload, config, row.Text);
    return new Prediction(row.Group, row.Text, row.Label, ExactSyntax(row.Text), Keywords(row.Text),
        majority, learned.Route, learned.Confidence, learned.Margin, learned.TopRoute);
}).ToArray();
var exact = Score("Studio syntax", item => item.Exact, predictions);
var keywords = Score("Keyword rules", item => item.Keywords, predictions);
var frequency = Score("Most frequent label", item => item.Majority, predictions);
var trained = Score("Trained multinomial naive Bayes", item => item.Learned, predictions);
var strongest = new[] { exact, keywords, frequency }.OrderByDescending(value => value.Correct).First();
var qualifies = trained.UnsafeAccepts == 0 && (double)trained.SupportedCorrect / trained.SupportedTotal >= config.MinimumSupportedAccuracy &&
    trained.Correct >= strongest.Correct + config.MinimumAdditionalCorrect;
var report = new
{
    model.ModelVersion, ModelSha256 = Hash(modelPath), model.TrainingSha256, model.ConfigSha256,
    HoldoutSha256 = Hash(Path.Combine(source, "holdout.tsv")), ModelClasses = labels, Training = train.Length,
    Holdout = holdout.Length, Criteria = config, Scores = new[] { exact, keywords, frequency, trained },
    AuthorQualification = qualifies ? "meets-automated-thresholds" : "failed-automated-thresholds",
    Deployment = "deterministic-only", Predictions = predictions
};
File.WriteAllText(reportPath, JsonSerializer.Serialize(report, json) + "\n");
foreach (var score in report.Scores)
    Console.WriteLine($"{score.Name}: {score.Correct}/{holdout.Length} correct, {score.SupportedCorrect}/{score.SupportedTotal} supported, {score.UnsafeAccepts} unsupported accepted");
Console.WriteLine($"{report.AuthorQualification}; deployment: {report.Deployment}");
return 0;

static Example[] ReadSet(string path) => File.ReadLines(path).Skip(1).Where(line => line.Length != 0).Select(line =>
{
    var fields = line.Split('\t');
    if (fields is not [var group, var label, var query] || fields.Any(string.IsNullOrWhiteSpace))
        throw new InvalidDataException($"Malformed evaluation row in {Path.GetFileName(path)}.");
    return new Example(group, label, query);
}).ToArray();

static string Normalize(string sentence) => string.Join(' ', Regex.Matches(sentence.ToLowerInvariant(), "<object>|[a-z0-9]+")
    .Select(match => match.Value));

static string[] Features(string sentence)
{
    var words = Normalize(sentence).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(word => word != "<object>").ToArray();
    return words.Concat(words.Zip(words.Skip(1), (first, second) => first + " " + second)).ToArray();
}

static (string Route, string TopRoute, double Confidence, double Margin) Predict(Model model, TrainingConfig config, string text)
{
    var features = Features(text);
    var scores = model.Classes.Select(route => (route.Label, Value: route.LogPrior +
        features.Sum(feature => route.LogLikelihood.GetValueOrDefault(feature)))).OrderByDescending(item => item.Value)
        .ThenBy(item => item.Label, StringComparer.Ordinal).ToArray();
    var denominator = scores.Sum(item => Math.Exp(item.Value - scores[0].Value));
    var confidence = 1 / denominator;
    var margin = confidence - Math.Exp(scores[1].Value - scores[0].Value) / denominator;
    var route = confidence >= config.MinimumPosterior && margin >= config.MinimumMargin && text.Contains("<object>", StringComparison.Ordinal)
        ? scores[0].Label : "unsupported";
    return (route, scores[0].Label, confidence, margin);
}

static string ExactSyntax(string text)
{
    var normalized = Normalize(text);
    return normalized switch
    {
        "inspect <object>" or "describe <object>" => "inspect",
        "where is <object>" => "locate",
        "what does <object> control" => "link",
        "what state is <object>" => "state",
        _ => "unsupported"
    };
}

static string Keywords(string text)
{
    if (!text.Contains("<object>", StringComparison.Ordinal))
        return "unsupported";
    var tokens = Normalize(text).Split(' ');
    if (tokens.Any(word => word is "move" or "remove" or "delete" or "export" or "swap" or "rename" or "simulate"))
        return "unsupported";
    if (tokens.Any(word => word is "target" or "control" or "controls" or "wired" or "link" or "triggers"))
        return "link";
    if (tokens.Any(word => word is "where" or "location" or "position" or "coordinates" or "located"))
        return "locate";
    if (tokens.Any(word => word is "state" or "open" or "closed" or "off" or "on" or "lit"))
        return "state";
    if (tokens.Any(word => word is "inspect" or "describe" or "details" or "properties" or "type"))
        return "inspect";
    return "unsupported";
}

static Summary Score(string name, Func<Prediction, string> select, Prediction[] rows) =>
    new(name, rows.Count(item => select(item) == item.Expected),
        rows.Count(item => item.Expected != "unsupported" && select(item) == item.Expected),
        rows.Count(item => item.Expected != "unsupported"),
        rows.Count(item => item.Expected == "unsupported" && select(item) != "unsupported"),
        rows.Count(item => select(item) == "unsupported"));

static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

internal sealed record TrainingConfig(string ModelVersion, double Alpha, double MinimumPosterior, double MinimumMargin,
    double MinimumSupportedAccuracy, int MinimumAdditionalCorrect);
internal sealed record Example(string Group, string Label, string Text);
internal sealed record LearnedClass(string Label, int Examples, double LogPrior, IReadOnlyDictionary<string, double> LogLikelihood);
internal sealed record Model(string ModelVersion, string TrainingSha256, string ConfigSha256, int TrainingExamples,
    string[] Vocabulary, LearnedClass[] Classes);
internal sealed record Prediction(string Group, string Text, string Expected, string Exact, string Keywords, string Majority,
    string Learned, double Confidence, double Margin, string TopRoute);
internal sealed record Summary(string Name, int Correct, int SupportedCorrect, int SupportedTotal, int UnsafeAccepts, int Abstained);
