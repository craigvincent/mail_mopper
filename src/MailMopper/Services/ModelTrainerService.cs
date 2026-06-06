using MailMopper.Data;
using MailMopper.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.ML;

namespace MailMopper.Services;

/// <summary>
/// Trains an ML.NET text classifier on rule-classified emails.
/// </summary>
public class ModelTrainerService(AppDbContext db)
{
    private readonly AppDbContext _db = db;

    /// <summary>
    /// Trains the email classifier and saves it to the specified path.
    /// Returns training metrics.
    /// </summary>
    public async Task<TrainingResult> TrainAsync(string modelPath, Action<string>? onStatus, CancellationToken ct)
    {
        onStatus?.Invoke("Loading training data from database...");

        // Export rule-classified emails as training data
        var trainingData = await _db.Classifications
            .Include(c => c.Email)
            .Where(c => c.Email != null && c.ClassifiedBy == "rule" && c.Category != ClassificationCategory.Unclassified)
            .Select(c => new EmailFeature
            {
                From = c.Email!.From ?? string.Empty,
                Subject = c.Email!.Subject ?? string.Empty,
                Snippet = c.Email!.Snippet ?? string.Empty,
                Label = c.Category.ToString()
            })
            .ToListAsync(ct);

        if (trainingData.Count < 100)
        {
            throw new InvalidOperationException(
                $"Insufficient training data: {trainingData.Count} emails. Need at least 100 rule-classified emails. Run 'classify --skip-ml' first.");
        }

        onStatus?.Invoke($"Loaded {trainingData.Count:N0} training samples across {trainingData.Select(t => t.Label).Distinct().Count()} categories");

        var mlContext = new MLContext(seed: 42);
        var dataView = mlContext.Data.LoadFromEnumerable(trainingData);

        var categories = trainingData.Select(t => t.Label).Distinct().OrderBy(l => l).ToList();

        // Build the ML pipeline
        var pipeline = mlContext.Transforms.Text.FeaturizeText("FromFeatures", nameof(EmailFeature.From))
            .Append(mlContext.Transforms.Text.FeaturizeText("SubjectFeatures", nameof(EmailFeature.Subject)))
            .Append(mlContext.Transforms.Text.FeaturizeText("SnippetFeatures", nameof(EmailFeature.Snippet)))
            .Append(mlContext.Transforms.Concatenate("Features", "FromFeatures", "SubjectFeatures", "SnippetFeatures"))
            .Append(mlContext.Transforms.Conversion.MapValueToKey("Label"))
            .Append(mlContext.MulticlassClassification.Trainers.SdcaMaximumEntropy("Label", "Features"))
            .Append(mlContext.Transforms.Conversion.MapKeyToValue("PredictedLabel"));

        // Cross-validate to get metrics before final training
        onStatus?.Invoke("Cross-validating (5-fold)...");
        var cvResults = mlContext.MulticlassClassification.CrossValidate(dataView, pipeline, numberOfFolds: 5, labelColumnName: "Label");

        var avgAccuracy = cvResults.Average(r => r.Metrics.MacroAccuracy);
        var avgLogLoss = cvResults.Average(r => r.Metrics.LogLoss);

        onStatus?.Invoke($"Cross-validation: Accuracy={avgAccuracy:P1}, LogLoss={avgLogLoss:F3}");

        // Gather per-class metrics from the best fold
        var bestFold = cvResults.OrderByDescending(r => r.Metrics.MacroAccuracy).First();
        var perClassMetrics = new Dictionary<string, (double Precision, double Recall, double F1)>();
        var confusionMatrix = bestFold.Metrics.ConfusionMatrix;
        var classNames = confusionMatrix.PerClassPrecision.Select((_, i) => i < categories.Count
            ? categories[i]
            : $"Class{i}").ToList();

        for (var i = 0; i < confusionMatrix.PerClassPrecision.Count; i++)
        {
            var name = i < classNames.Count ? classNames[i] : $"Class{i}";
            perClassMetrics[name] = (
                confusionMatrix.PerClassPrecision[i],
                confusionMatrix.PerClassRecall[i],
                2 * confusionMatrix.PerClassPrecision[i] * confusionMatrix.PerClassRecall[i] /
                    (confusionMatrix.PerClassPrecision[i] + confusionMatrix.PerClassRecall[i] + 1e-10)
            );
        }

        // Train final model on all data
        onStatus?.Invoke("Training final model on all data...");
        var model = pipeline.Fit(dataView);

        // Save model
        Directory.CreateDirectory(Path.GetDirectoryName(modelPath) ?? ".");
        mlContext.Model.Save(model, dataView.Schema, modelPath);

        var fileSize = new FileInfo(modelPath).Length;
        onStatus?.Invoke($"Model saved to {modelPath} ({fileSize / 1024.0 / 1024.0:F1} MB)");

        return new TrainingResult(
            TrainingSamples: trainingData.Count,
            Categories: categories,
            Accuracy: avgAccuracy,
            LogLoss: avgLogLoss,
            PerClassMetrics: perClassMetrics,
            ModelPath: modelPath,
            ModelSizeBytes: fileSize);
    }

    /// <summary>
    /// Returns the default model path.
    /// </summary>
    public static string GetDefaultModelPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, "MailMopper", "email_classifier.zip");
    }
}

public record TrainingResult(
    int TrainingSamples,
    List<string> Categories,
    double Accuracy,
    double LogLoss,
    Dictionary<string, (double Precision, double Recall, double F1)> PerClassMetrics,
    string ModelPath,
    long ModelSizeBytes);
