using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using MailMopper.Config;
using MailMopper.Data;
using MailMopper.Models;
using Microsoft.EntityFrameworkCore;

namespace MailMopper.Services;

public partial class GmailFetchService(
    GmailSession session,
    AppDbContext dbContext,
    AppSettings appSettings)
{
    private readonly AppDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly AppSettings _appSettings = appSettings ?? throw new ArgumentNullException(nameof(appSettings));
    private readonly GmailSession _session = session ?? throw new ArgumentNullException(nameof(session));

    private GmailService GetGmailService() =>
        _session.Service ?? throw new InvalidOperationException("GmailSession not authenticated. Call AuthenticateAsync first.");

    /// <summary>
    /// Fetches all emails from Gmail and stores them in the local database.
    /// </summary>
    public async Task<int> FetchAllAsync(
        IProgress<(int fetched, int total)>? progress,
        CancellationToken ct)
    {
        // Step 1: Collect all message IDs
        var allMessageIds = new List<string>();
        string? pageToken = null;

        do
        {
            var listRequest = GetGmailService().Users.Messages.List("me");
            listRequest.MaxResults = 500;
            if (pageToken != null)
                listRequest.PageToken = pageToken;

            var response = await listRequest.ExecuteAsync(ct);

            if (response.Messages != null)
                allMessageIds.AddRange(response.Messages.Select(m => m.Id));

            pageToken = response.NextPageToken;
        } while (pageToken != null);

        // Filter out messages already in DB
        var existingIds = new HashSet<string>(
            await _dbContext.Emails.Select(e => e.MessageId).ToListAsync(ct));
        var newMessageIds = allMessageIds.Where(id => !existingIds.Contains(id)).ToList();

        Console.WriteLine($"Found {allMessageIds.Count} total messages, {newMessageIds.Count} new to fetch.");

        // Step 2: Fetch message details in sequential batches with rate limiting
        var savedCount = await FetchAndSaveMessagesAsync(
            newMessageIds, progress, existingIds.Count, allMessageIds.Count, ct);

        // Update SyncState
        await UpdateSyncStateAsync(savedCount, ct);

        Console.WriteLine($"Fetch complete. Saved {savedCount} new emails.");
        return savedCount;
    }

    /// <summary>
    /// Fetches only new emails since the last sync using Gmail history API.
    /// Falls back to FetchAllAsync if no prior sync exists.
    /// </summary>
    public async Task<int> FetchIncrementalAsync(
        IProgress<(int fetched, int total)>? progress,
        CancellationToken ct)
    {
        var lastSync = await _dbContext.SyncStates
            .FirstOrDefaultAsync(s => s.Key == "default", cancellationToken: ct);

        if (lastSync?.LastHistoryId == null)
        {
            Console.WriteLine("No prior sync found. Performing full fetch...");
            return await FetchAllAsync(progress, ct);
        }

        Console.WriteLine($"Fetching incremental changes since history ID {lastSync.LastHistoryId}...");

        var newMessageIds = new List<string>();
        string? pageToken = null;

        try
        {
            do
            {
                var historyRequest = GetGmailService().Users.History.List("me");
                historyRequest.StartHistoryId = ulong.Parse(lastSync.LastHistoryId, System.Globalization.CultureInfo.InvariantCulture);
                if (pageToken != null)
                    historyRequest.PageToken = pageToken;

                var response = await historyRequest.ExecuteAsync(ct);

                if (response.History != null)
                {
                    foreach (var historyItem in response.History)
                    {
                        if (historyItem.MessagesAdded != null)
                            newMessageIds.AddRange(historyItem.MessagesAdded.Select(m => m.Message.Id));
                    }
                }

                pageToken = response.NextPageToken;
            } while (pageToken != null);
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            Console.WriteLine("History ID expired. Performing full fetch...");
            return await FetchAllAsync(progress, ct);
        }

        // Deduplicate and filter out already-fetched
        var existingIds = new HashSet<string>(
            await _dbContext.Emails.Select(e => e.MessageId).ToListAsync(ct));
        newMessageIds = [.. newMessageIds.Distinct().Where(id => !existingIds.Contains(id))];

        Console.WriteLine($"Found {newMessageIds.Count} new messages to fetch.");

        if (newMessageIds.Count == 0)
        {
            lastSync.LastSyncAt = DateTimeOffset.UtcNow;
            _dbContext.SyncStates.Update(lastSync);
            await _dbContext.SaveChangesAsync(ct);
            return 0;
        }

        // Fetch sequentially with rate limiting
        var savedCount = await FetchAndSaveMessagesAsync(
            newMessageIds, progress, 0, newMessageIds.Count, ct);

        // Update SyncState
        lastSync.TotalMessagesFetched += savedCount;
        lastSync.LastSyncAt = DateTimeOffset.UtcNow;
        _dbContext.SyncStates.Update(lastSync);
        await _dbContext.SaveChangesAsync(ct);

        Console.WriteLine($"Incremental fetch complete. New emails: {savedCount}");
        return savedCount;
    }

    /// <summary>
    /// Core message fetching loop: fetches individual message details from Gmail,
    /// parsing headers and saving in batches. Shared by full and incremental fetches.
    /// </summary>
    private async Task<int> FetchAndSaveMessagesAsync(
        List<string> messageIds,
        IProgress<(int fetched, int total)>? progress,
        int progressOffset,
        int progressTotal,
        CancellationToken ct)
    {
        var fetchedCount = 0;
        var savedCount = 0;
        var pendingRecords = new List<EmailRecord>();
        var batchSize = _appSettings.Gmail.BatchSize;

        for (var i = 0; i < messageIds.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var getRequest = GetGmailService().Users.Messages.Get("me", messageIds[i]);
                getRequest.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Metadata;
                getRequest.MetadataHeaders = new[] { "From", "To", "Subject", "Date", "List-Unsubscribe" };

                var message = await getRequest.ExecuteAsync(ct);
                pendingRecords.Add(ParseEmailRecord(message));
            }
            catch (Google.GoogleApiException ex) when (ex.HttpStatusCode is System.Net.HttpStatusCode.TooManyRequests
                or System.Net.HttpStatusCode.Forbidden)
            {
                Console.WriteLine($"Rate limited at message {i + 1}. Waiting 60 seconds...");
                await Task.Delay(TimeSpan.FromSeconds(60), ct);
                i--;
                continue;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching message {messageIds[i]}: {ex.Message}");
            }

            fetchedCount++;
            progress?.Report((progressOffset + fetchedCount, progressTotal));

            if (pendingRecords.Count >= batchSize)
            {
                savedCount += await SaveBatchAsync(pendingRecords, ct);
                pendingRecords.Clear();
                await Task.Delay(200, ct);
            }
        }

        if (pendingRecords.Count > 0)
            savedCount += await SaveBatchAsync(pendingRecords, ct);

        return savedCount;
    }

    private async Task<int> SaveBatchAsync(List<EmailRecord> records, CancellationToken ct)
    {
        var batchIds = records.Select(r => r.MessageId).ToList();
        var existingIds = new HashSet<string>(
            await _dbContext.Emails
                .Where(e => batchIds.Contains(e.MessageId))
                .Select(e => e.MessageId)
                .ToListAsync(ct));

        var added = 0;
        foreach (var record in records)
        {
            if (!existingIds.Contains(record.MessageId))
            {
                record.FetchedAt = DateTimeOffset.UtcNow;
                _dbContext.Emails.Add(record);
                added++;
            }
        }
        await _dbContext.SaveChangesAsync(ct);
        return added;
    }

    private async Task UpdateSyncStateAsync(int newCount, CancellationToken ct)
    {
        var syncState = await _dbContext.SyncStates
            .FirstOrDefaultAsync(s => s.Key == "default", cancellationToken: ct);

        if (syncState == null)
        {
            syncState = new SyncState { Key = "default" };
            _dbContext.SyncStates.Add(syncState);
        }

        syncState.TotalMessagesFetched += newCount;
        syncState.LastSyncAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Repairs emails whose Date was set to fetch time (parsing failure fallback).
    /// Re-fetches InternalDate from Gmail for these records.
    /// </summary>
    public async Task<int> RepairDatesAsync(
        Action<string>? onStatus,
        CancellationToken ct)
    {
        // Suspect dates: null (parse failure), DateTimeOffset.MinValue (legacy fallback),
        // or suspiciously close to FetchedAt (older legacy fallback to UtcNow).
        var allEmails = await _dbContext.Emails.ToListAsync(ct);
        var suspect = allEmails
            .Where(e => e.Date is null
                     || e.Date == DateTimeOffset.MinValue
                     || Math.Abs((e.Date.Value - e.FetchedAt).TotalMinutes) < 5)
            .ToList();

        if (suspect.Count == 0)
        {
            onStatus?.Invoke("No emails with suspect dates found.");
            return 0;
        }

        onStatus?.Invoke($"Found {suspect.Count} emails with dates matching fetch time. Re-fetching from Gmail...");

        var repaired = 0;
        var errors = 0;

        for (var i = 0; i < suspect.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                // Minimal fetch — just need InternalDate
                var getRequest = GetGmailService().Users.Messages.Get("me", suspect[i].MessageId);
                getRequest.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Minimal;

                var message = await getRequest.ExecuteAsync(ct);

                var correctedDate = ParseInternalDate(message.InternalDate);
                if (correctedDate.HasValue && correctedDate.Value != suspect[i].Date)
                {
                    suspect[i].Date = correctedDate.Value;
                    repaired++;
                }
            }
            catch (Google.GoogleApiException ex) when (ex.HttpStatusCode is System.Net.HttpStatusCode.TooManyRequests
                or System.Net.HttpStatusCode.Forbidden)
            {
                onStatus?.Invoke($"Rate limited at {i + 1}/{suspect.Count}. Waiting 60s...");
                await Task.Delay(TimeSpan.FromSeconds(60), ct);
                i--;
                continue;
            }
            catch (Exception ex)
            {
                errors++;
                if (errors <= 3)
                    onStatus?.Invoke($"Error for {suspect[i].MessageId}: {ex.Message}");
            }

            // Save in batches of 100
            if ((i + 1) % 100 == 0 || i == suspect.Count - 1)
            {
                await _dbContext.SaveChangesAsync(ct);
                onStatus?.Invoke($"  Progress: {i + 1}/{suspect.Count} checked, {repaired} repaired");
            }
        }

        await _dbContext.SaveChangesAsync(ct);
        onStatus?.Invoke($"Repair complete: {repaired} dates corrected, {errors} errors.");
        return repaired;
    }

    internal static EmailRecord ParseEmailRecord(Message message)
    {
        var headers = message.Payload?.Headers ?? [];
        var headerDict = headers.ToDictionary(h => h.Name, h => h.Value, StringComparer.OrdinalIgnoreCase);

        var fromHeader = headerDict.TryGetValue("From", out var from) ? from : string.Empty;
        var fromDomain = ExtractEmailDomain(fromHeader);
        var to = headerDict.TryGetValue("To", out var toVal) ? toVal : string.Empty;
        var subject = headerDict.TryGetValue("Subject", out var subj) ? subj : string.Empty;
        var dateStr = headerDict.TryGetValue("Date", out var date) ? date : string.Empty;
        var hasListUnsubscribe = headerDict.ContainsKey("List-Unsubscribe");

        var gmailCategory = ExtractCategory(message.LabelIds);
        var gmailLabels = string.Join(",", message.LabelIds ?? []);

        // Prefer InternalDate (epoch ms from Gmail, always accurate) over the Date header
        var parsedDate = ParseInternalDate(message.InternalDate)
                      ?? ParseDateHeader(dateStr);

        return new EmailRecord
        {
            MessageId = message.Id,
            ThreadId = message.ThreadId,
            From = fromHeader,
            FromDomain = fromDomain,
            To = to,
            Subject = subject,
            Snippet = message.Snippet ?? string.Empty,
            Date = parsedDate,
            SizeEstimate = message.SizeEstimate ?? 0,
            HasListUnsubscribe = hasListUnsubscribe,
            GmailLabels = gmailLabels,
            GmailCategory = gmailCategory,
            FetchedAt = DateTimeOffset.UtcNow
        };
    }

    internal static DateTimeOffset? ParseInternalDate(long? internalDateMs) => internalDateMs is > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(internalDateMs.Value) : null;

    internal static string ExtractEmailDomain(string? fromHeader)
    {
        if (string.IsNullOrWhiteSpace(fromHeader))
            return string.Empty;

        try
        {
            var emailPart = fromHeader;
            if (fromHeader.Contains('<'))
            {
                var start = fromHeader.IndexOf('<');
                var end = fromHeader.IndexOf('>');
                if (start >= 0 && end > start)
                    emailPart = fromHeader.Substring(start + 1, end - start - 1);
            }

            return emailPart.Contains('@') ? emailPart.Split('@')[1].Trim().ToLowerInvariant() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    internal static DateTimeOffset? ParseDateHeader(string? dateStr)
    {
        if (string.IsNullOrWhiteSpace(dateStr))
            return null;

        if (DateTimeOffset.TryParse(dateStr, out var result))
            return result;

        // Strip trailing parenthetical timezone names: "(UTC)", "(PDT)", etc.
        var cleaned = MyRegex().Replace(dateStr, "").Trim();
        if (cleaned != dateStr && DateTimeOffset.TryParse(cleaned, out result))
            return result;

        // Try RFC 2822 / email-style formats
        string[] formats = [
            "ddd, d MMM yyyy HH:mm:ss zzz",
            "ddd, dd MMM yyyy HH:mm:ss zzz",
            "d MMM yyyy HH:mm:ss zzz",
            "dd MMM yyyy HH:mm:ss zzz",
            "ddd, d MMM yyyy HH:mm:ss",
            "ddd, dd MMM yyyy HH:mm:ss",
        ];
        return DateTimeOffset.TryParseExact(cleaned, formats,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AllowWhiteSpaces, out result)
            ? result
            : null;
    }

    private static string ExtractCategory(IList<string>? labelIds)
    {
        return labelIds == null || labelIds.Count == 0
            ? string.Empty
            : labelIds.FirstOrDefault(l => l.StartsWith("CATEGORY_", StringComparison.OrdinalIgnoreCase))
            ?? string.Empty;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\s*\([^)]*\)\s*$")]
    private static partial System.Text.RegularExpressions.Regex MyRegex();
}
