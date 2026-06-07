using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;

namespace MailMopper.Services;

/// <summary>
/// Production wrapper that delegates to the real Google GmailService.
/// </summary>
public class GmailApiWrapper(GmailSession session) : IGmailApi
{
    private readonly GmailSession _session = session ?? throw new ArgumentNullException(nameof(session));

    private GmailService GetGmailService() =>
        _session.Service ?? throw new InvalidOperationException("GmailSession not authenticated. Call AuthenticateAsync first.");

    public async Task BatchModifyAsync(IList<string> messageIds, IList<string>? addLabelIds, IList<string>? removeLabelIds, CancellationToken ct)
    {
        var request = new BatchModifyMessagesRequest
        {
            Ids = messageIds,
            AddLabelIds = addLabelIds,
            RemoveLabelIds = removeLabelIds
        };

        await GetGmailService().Users.Messages.BatchModify(request, "me").ExecuteAsync(ct);
    }
}
