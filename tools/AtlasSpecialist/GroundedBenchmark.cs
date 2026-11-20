using System.Security.Cryptography;
using System.Text.Json;
using PlatzAtlas.Atlas;

internal static class GroundedBenchmark
{
    private const string Deny = "deny";
    private static readonly string[] ReadLabels = ["inspect", "locate", "link", "state"];
    private static readonly string[] Labels = [Deny, .. ReadLabels];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, NewLine = "\n" };

    public static int Run(string source, string output)
    {
        var configPath = Path.Combine(source, "training.json");
        var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(configPath), Json)
            ?? throw new InvalidDataException("Missing grounded training settings.");
        if (settings.Epochs <= 0 || settings.LearningRate is not (> 0 and <= 1) || settings.Decay < 0 ||
            settings.MinimumSupportedAccuracy is not (>= .9 and <= 1) || settings.MaximumUnsafeAccepts != 0)
            throw new InvalidDataException("Invalid training settings or qualification gate.");
        var scenePath = Path.Combine(source, "scenes.tsv");
        var rooms = GroundedEvidence.ReadRooms(scenePath);
        var trainPath = Path.Combine(source, "train.tsv");
        var calibrationPath = Path.Combine(source, "calibration.tsv");
        var testPath = Path.Combine(source, "test.tsv");
        var train = Load(trainPath);
        var calibration = Load(calibrationPath);
        var test = Load(testPath);
        ValidateDisjoint(train, calibration, test);

        var groundedTrain = train.Where(item => item.Grounding is not null).ToArray();
        var richTrain = ToTraining(groundedTrain, true, true);
        var safeTrain = richTrain.Select(row => row with { Label = row.Label == Deny ? Deny : "read" }).ToArray();
        var intent = Fit(richTrain.Where(row => row.Label != Deny).ToArray(), ReadLabels);
        var safety = Fit(safeTrain, [Deny, "read"]);
        var calibrationSafetyIndex = safety.MakeIndex();
        var calibrationIntentIndex = intent.MakeIndex();
        var learnedCalibration = calibration.Select(sample =>
            Classify(sample, safety, intent, calibrationSafetyIndex, calibrationIntentIndex)).ToArray();
        var gate = Calibrate(learnedCalibration);
        var inputs = new Inputs(Hash(scenePath), Hash(trainPath), Hash(calibrationPath), Hash(configPath));
        var model = new GroundedModel(settings.ModelVersion, inputs, gate, safety, intent);
        Directory.CreateDirectory(output);
        var modelPath = Path.Combine(output, "model.json");
        Write(modelPath, model);
        var reload = JsonSerializer.Deserialize<GroundedModel>(File.ReadAllText(modelPath), Json)
            ?? throw new InvalidDataException("Unable to reload the trained model.");
        var safeIndex = reload.Safety.MakeIndex();
        var intentIndex = reload.Intent.MakeIndex();

        var unigrams = ToTraining(groundedTrain, false, false);
        var bigrams = ToTraining(groundedTrain, true, false);
        var mostFrequent = unigrams.CountBy(row => row.Label).OrderByDescending(item => item.Value)
            .ThenBy(item => item.Key, StringComparer.Ordinal).First().Key;
        var routes = new Dictionary<string, Func<Sample, Route?>>
        {
            ["Studio read syntax"] = sample => Exact(rooms[sample.Prompt.Room], sample),
            ["Keyword rules"] = sample => FromLabel(sample, Keywords(sample)),
            ["Majority label"] = sample => FromLabel(sample, mostFrequent),
            ["Unigram MNB"] = UseBaseline(GroundedLearning.NaiveBayes(unigrams), false),
            ["Word bigram MNB"] = UseBaseline(GroundedLearning.NaiveBayes(bigrams), true),
            ["TF-IDF cosine centroid"] = UseBaseline(GroundedLearning.Centroid(unigrams), false),
            ["Averaged bag-of-words perceptron"] = UseBaseline(GroundedLearning.AveragedPerceptron(unigrams, Labels), false)
        };
        var predictions = test.Select(sample =>
        {
            var vote = Classify(sample, reload.Safety, reload.Intent, safeIndex, intentIndex);
            var result = routes.ToDictionary(pair => pair.Key, pair => pair.Value(sample));
            result.Add("Grounded intent without safety (diagnostic)",
                vote.Intent is { } intentVote ? FromLabel(sample, intentVote.Label) : null);
            result.Add("Grounded safety and intent", Decide(vote, reload.Gate));
            return new Prediction(sample.Prompt, sample.Expected, result, vote.Safety, vote.Intent);
        }).ToArray();
        var scores = predictions[0].Routes.Keys.Select(name => ScoreRoutes(name, predictions)).ToArray();
        var candidate = scores[^1];
        var qualifies = candidate.UnsafeAccepts <= settings.MaximumUnsafeAccepts &&
            (double)candidate.SupportedCorrect / candidate.Supported >= settings.MinimumSupportedAccuracy;
        var report = new
        {
            model.ModelVersion,
            model.Inputs,
            ModelSha256 = Hash(modelPath),
            HeldOutSha256 = Hash(testPath),
            Rows = new { Train = train.Length, Calibration = calibration.Length, Test = test.Length },
            Families = new { Train = Families(train), Calibration = Families(calibration), Test = Families(test) },
            Settings = settings,
            Gate = reload.Gate,
            CalibrationScore = Counts(learnedCalibration, reload.Gate),
            CalibrationSweep = Thresholds().Select(threshold => new { Gate = threshold, Score = Counts(learnedCalibration, threshold) }),
            Grounding = new
            {
                SupportedResolvedCorrectly = test.Count(sample => sample.Expected is { } expected &&
                    sample.Grounding?.Object.Id == expected.SourceId),
                DeniedAtGrounding = test.Count(sample => sample.Expected is null && sample.Grounding is null)
            },
            Scores = scores,
            AuthorQualification = qualifies ? "meets-automated-thresholds" : "failed-automated-thresholds",
            Deployment = "offline experiment only",
            Predictions = predictions
        };
        Write(Path.Combine(output, "evaluation.json"), report);
        foreach (var score in scores)
            Console.WriteLine($"{score.Name}: {score.Correct}/{test.Length} correct; {score.SupportedCorrect}/{score.Supported} supported; {score.UnsafeAccepts}/{test.Length - score.Supported} deny cases routed");
        Console.WriteLine($"{report.AuthorQualification}; {report.Deployment}");
        return 0;

        LinearModel Fit(TrainingRow[] rows, string[] labels) =>
            GroundedLearning.Train(rows, labels, settings.Epochs, settings.LearningRate, settings.Decay);

        Sample[] Load(string path) => GroundedEvidence.ReadPrompts(path).Select(prompt =>
        {
            var room = rooms[prompt.Room];
            var expanded = prompt with { Text = ExpandIds(room, prompt.Text) };
            var sourceObject = prompt.Source == "-" ? null : room.Objects[prompt.Source];
            if (prompt.Label == Deny ? sourceObject is not null : sourceObject is null ||
                !GroundedEvidence.Supports(prompt.Label, sourceObject.Kind))
                throw new InvalidDataException($"Inconsistent expected source or route in {path}.");
            var expected = sourceObject is null ? null : new Route(prompt.Label, sourceObject.Id,
                SceneEvidenceProfile.Open(room.Snapshot).Sha256, new[] { sourceObject.TargetId, sourceObject.SecondTargetId }.OfType<Guid>().ToArray());
            return new Sample(expanded, GroundedEvidence.Resolve(room, expanded), expected);
        }).ToArray();
    }

    private static string ExpandIds(RoomFixture room, string text)
    {
        foreach (var (name, obj) in room.Objects)
            text = text.Replace("{stable-id:" + name + "}", obj.Id.ToString("D"), StringComparison.Ordinal);
        if (text.Contains("{stable-id:", StringComparison.Ordinal))
            throw new InvalidDataException("A prompt refers to an unknown fixture identifier.");
        return text;
    }

    private static string[] Families(Sample[] split) =>
        split.Select(sample => sample.Prompt.Room).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static void ValidateDisjoint(params Sample[][] splits)
    {
        var groups = new HashSet<string>(StringComparer.Ordinal);
        var prompts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var split in splits)
        {
            if (!Labels.ToHashSet(StringComparer.Ordinal).SetEquals(split.Select(sample => sample.Prompt.Label)))
                throw new InvalidDataException("Each split must include all supported intents and denial cases.");
            var families = Families(split);
            var texts = split.Select(sample => string.Join(' ', GroundedLearning.Tokens(sample.Prompt.Text)))
                .Distinct(StringComparer.Ordinal).ToArray();
            if (families.Any(groups.Contains) || texts.Any(prompts.Contains))
                throw new InvalidDataException("Room families or normalized prompt text cross data splits.");
            groups.UnionWith(families);
            prompts.UnionWith(texts);
        }
    }

    private static TrainingRow[] ToTraining(Sample[] set, bool bigrams, bool characters) =>
        set.Select(sample => new TrainingRow(sample.Prompt.Label, Features(sample.Grounding!, bigrams, characters))).ToArray();

    private static string[] Features(Grounding grounding, bool bigrams, bool characters) =>
        [.. GroundedLearning.Features(grounding.Text, bigrams, characters), "kind:" + grounding.Object.Kind];

    private static Ranked Classify(Sample sample, LinearModel safety, LinearModel intent,
        Dictionary<string, int> safeIndex, Dictionary<string, int> intentIndex)
    {
        if (sample.Grounding is not { } grounded)
            return new Ranked(sample, 0, null);
        var features = Features(grounded, true, true);
        var read = safety.Probabilities(GroundedLearning.Encode(safeIndex, features))[1];
        return new Ranked(sample, read, intent.Predict(intentIndex, features));
    }

    private static Route? Decide(Ranked row, Gate gate) =>
        row.Safety >= gate.MinimumRead && row.Intent is { } vote &&
        vote.Confidence >= gate.MinimumIntent && vote.Margin >= gate.MinimumMargin
            ? FromLabel(row.Sample, vote.Label) : null;

    private static IEnumerable<Gate> Thresholds() =>
        from safety in new[] { .5, .6, .7, .8, .9, .95, .975, .99, 1.01 }
        from intent in new[] { .4, .55, .7, .85 }
        from margin in new[] { .05, .15, .3, .5 }
        select new Gate(safety, intent, margin);

    private static Gate Calibrate(Ranked[] rows) =>
        Thresholds().Select(gate => (Gate: gate, Score: Counts(rows, gate)))
            .Where(row => row.Score.Unsafe == 0)
            .OrderByDescending(row => row.Score.Exact)
            .ThenBy(row => row.Score.Wrong)
            .ThenByDescending(row => row.Gate.MinimumRead)
            .ThenByDescending(row => row.Gate.MinimumIntent)
            .ThenByDescending(row => row.Gate.MinimumMargin).First().Gate;

    private static Calibration Counts(Ranked[] rows, Gate gate)
    {
        var predictions = rows.Select(row => (Expected: row.Sample.Expected, Actual: Decide(row, gate))).ToArray();
        return new Calibration(predictions.Count(pair => pair.Expected is not null && Same(pair.Expected, pair.Actual)),
            predictions.Count(pair => pair.Expected is null && pair.Actual is not null),
            predictions.Count(pair => pair.Expected is not null && pair.Actual is not null && !Same(pair.Expected, pair.Actual)));
    }

    private static Route? Exact(RoomFixture room, Sample sample)
    {
        if (sample.Grounding is not { } grounded)
            return null;
        var host = new SceneQuestionHost();
        var allowed = host.Allow(room.Scope, () => room.Snapshot);
        var selection = sample.Prompt.Selection == "-" ? (Guid?)null : room.Objects[sample.Prompt.Selection].Id;
        var text = sample.Prompt.Text;
        var answer = host.Ask(allowed, SceneEvidenceProfile.Version, selection, text).Answer;
        if (answer is not { Source: { } source, Proposal: null } || source.Id != grounded.Object.Id)
            return null;
        var label = text.StartsWith("Where is ", StringComparison.OrdinalIgnoreCase) ? "locate"
            : text.StartsWith("What does ", StringComparison.OrdinalIgnoreCase) ? "link"
            : text.StartsWith("What state ", StringComparison.OrdinalIgnoreCase) ? "state" : "inspect";
        return FromLabel(sample, label);
    }

    private static string Keywords(Sample sample)
    {
        var words = GroundedLearning.Tokens(sample.Grounding?.Text ?? "").ToHashSet(StringComparer.Ordinal);
        if (words.Overlaps(["properties", "dimensions", "details", "record", "transform", "inspect", "describe", "size"]))
            return "inspect";
        if (words.Overlaps(["where", "find", "position", "coordinates", "location", "locate", "map"]))
            return "locate";
        if (words.Overlaps(["connected", "connection", "trigger", "linked", "control", "controls", "target", "wired"]))
            return "link";
        return words.Overlaps(["state", "off", "lit", "shut", "open", "closed"]) ? "state" : Deny;
    }

    private static Func<Sample, Route?> UseBaseline(Func<string[], Vote> predict, bool bigrams) =>
        sample => sample.Grounding is { } grounded
            ? FromLabel(sample, predict(Features(grounded, bigrams, false)).Label) : null;

    private static Route? FromLabel(Sample sample, string label) =>
        sample.Grounding is { } grounded && GroundedEvidence.Supports(label, grounded.Object.Kind)
            ? new Route(label, grounded.Object.Id, grounded.Sha256, grounded.LinkedIds) : null;

    private static bool Same(Route? expected, Route? actual) =>
        expected is null ? actual is null : actual is not null && expected.Label == actual.Label &&
            expected.SourceId == actual.SourceId && expected.EvidenceSha256 == actual.EvidenceSha256 &&
            expected.LinkedIds.SequenceEqual(actual.LinkedIds);

    private static Score ScoreRoutes(string name, Prediction[] rows) => new(name,
        rows.Count(row => Same(row.Expected, row.Routes[name])),
        rows.Count(row => row.Expected is not null),
        rows.Count(row => row.Expected is not null && Same(row.Expected, row.Routes[name])),
        rows.Count(row => row.Expected is null && row.Routes[name] is not null));

    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Write<T>(string path, T value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json) + "\n");

    private sealed record Sample(Prompt Prompt, Grounding? Grounding, Route? Expected);
    private sealed record Route(string Label, Guid SourceId, string EvidenceSha256, Guid[] LinkedIds);
    private sealed record Ranked(Sample Sample, double Safety, Vote? Intent);
    private sealed record Score(string Name, int Correct, int Supported, int SupportedCorrect, int UnsafeAccepts);
    private sealed record Prediction(Prompt Prompt, Route? Expected, Dictionary<string, Route?> Routes, double ReadProbability, Vote? Intent);
    private sealed record Calibration(int Exact, int Unsafe, int Wrong);
    private sealed record Settings(string ModelVersion, int Epochs, double LearningRate, double Decay,
        double MinimumSupportedAccuracy, int MaximumUnsafeAccepts);
    private sealed record Inputs(string ScenesSha256, string TrainingSha256, string CalibrationSha256, string ConfigSha256);
    private sealed record Gate(double MinimumRead, double MinimumIntent, double MinimumMargin);
    private sealed record GroundedModel(string ModelVersion, Inputs Inputs, Gate Gate, LinearModel Safety, LinearModel Intent);
}
