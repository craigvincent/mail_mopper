using MailMopper.Config;
using MailMopper.Models;
using Microsoft.ML;

namespace MailMopper.Services;

/// <summary>
/// ML.NET-based email classifier. Replaces the previous GPT-4o-mini API classifier.
/// Loads a pre-trained model and classifies emails locally — no API limits, near-instant inference.
/// </summary>
public class MlClassifier : IDisposable
{
    private readonly AppSettings _appSettings;
    private readonly MLContext _mlContext;
    private readonly ITransformer _model;
    private readonly PredictionEngine<EmailFeature, EmailPrediction> _predictionEngine;
    private bool _disposed;

    public MlClassifier(AppSettings appSettings, string? modelPath = null)
    {
        _appSettings = appSettings ?? throw new ArgumentNullException(nameof(appSettings));
        _mlContext = new MLContext(seed: 42);

        modelPath ??= appSettings.Ml?.ModelPath ?? ModelTrainerService.GetDefaultModelPath();

        if (!File.Exists(modelPath))
        {
            throw new InvalidOperationException(
                $"ML model not found at '{modelPath}'. Run 'mail-mopper train' first to train the classifier.");
        }

        _model = _mlContext.Model.Load(modelPath, out _);
        _predictionEngine = _mlContext.Model.CreatePredictionEngine<EmailFeature, EmailPrediction>(_model);
    }

    /// <summary>
    /// Classifies a batch of emails using the trained ML model (CPU-bound, synchronous).
    /// </summary>
    public IReadOnlyList<AiClassificationResult> ClassifyBatch(
        IReadOnlyList<EmailRecord> emails,
        CancellationToken ct)
    {
        var results = new List<AiClassificationResult>(emails.Count);

        foreach (var email in emails)
        {
            ct.ThrowIfCancellationRequested();

            var feature = new EmailFeature
            {
                From = email.From ?? string.Empty,
                Subject = email.Subject ?? string.Empty,
                Snippet = email.Snippet ?? string.Empty
            };

            var prediction = _predictionEngine.Predict(feature);

            if (!Enum.TryParse<ClassificationCategory>(prediction.PredictedLabel, ignoreCase: true, out var category))
                category = ClassificationCategory.Unclassified;

            // Confidence = max probability from the Score array
            var confidence = prediction.Score is { Length: > 0 }
                ? prediction.Score.Max()
                : 0.0;

            var reasoning = $"ml: {prediction.PredictedLabel} ({confidence:P0})";
            results.Add(new AiClassificationResult(email.MessageId, category, confidence, reasoning));
        }

        return results.AsReadOnly();
    }

    /// <summary>
    /// Classifies all emails in batches, calling onBatchComplete after each batch for incremental saves.
    /// </summary>
    public async Task<int> ClassifyAllAsync(
        IReadOnlyList<EmailRecord> emails,
        Func<IReadOnlyList<AiClassificationResult>, int, int, Task>? onBatchComplete,
        CancellationToken ct)
    {
        var batchSize = _appSettings.Ml?.BatchSize ?? 500;
        var totalBatches = (int)Math.Ceiling((double)emails.Count / batchSize);
        var processedCount = 0;

        for (var batchIndex = 0; batchIndex < totalBatches; batchIndex++)
        {
            ct.ThrowIfCancellationRequested();

            var start = batchIndex * batchSize;
            var end = Math.Min(start + batchSize, emails.Count);
            var batchEmails = emails.Skip(start).Take(end - start).ToList();

            Console.WriteLine($"  [Batch {batchIndex + 1}/{totalBatches}] Classifying {batchEmails.Count} emails...");

            var batchResults = ClassifyBatch(batchEmails, ct);
            processedCount += batchEmails.Count;

            if (onBatchComplete != null)
                await onBatchComplete(batchResults, processedCount, emails.Count);
        }

        return processedCount;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _predictionEngine.Dispose();
            _disposed = true;
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Represents the result of ML classification for an email.
/// </summary>
public record AiClassificationResult(
    string MessageId,
    ClassificationCategory Category,
    double Confidence,
    string Reasoning);
