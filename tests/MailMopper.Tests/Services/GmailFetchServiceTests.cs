using Google.Apis.Gmail.v1.Data;
using MailMopper.Services;

namespace MailMopper.Tests.Services;

public class GmailFetchServiceTests
{
    #region ParseDateHeader

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseDateHeader_NullOrEmpty_ReturnsNull(string? input) =>
        Assert.Null(GmailFetchService.ParseDateHeader(input));

    [Fact]
    public void ParseDateHeader_ValidIsoDate_ReturnsCorrectValue()
    {
        var result = GmailFetchService.ParseDateHeader("2024-01-15T10:30:00+00:00");
        Assert.NotNull(result);
        Assert.Equal(2024, result!.Value.Year);
        Assert.Equal(1, result.Value.Month);
        Assert.Equal(15, result.Value.Day);
    }

    [Fact]
    public void ParseDateHeader_Rfc2822Format_ReturnsCorrectValue()
    {
        var result = GmailFetchService.ParseDateHeader("Mon, 15 Jan 2024 10:30:00 +0000");
        Assert.NotNull(result);
        Assert.Equal(2024, result!.Value.Year);
    }

    [Fact]
    public void ParseDateHeader_ParentheticalTimezone_StripsAndParses()
    {
        var result = GmailFetchService.ParseDateHeader("Mon, 15 Jan 2024 10:30:00 +0000 (UTC)");
        Assert.NotNull(result);
        Assert.Equal(2024, result!.Value.Year);
    }

    [Fact]
    public void ParseDateHeader_ParentheticalTimezoneWithDirectParse_ReturnsCorrect()
    {
        var result = GmailFetchService.ParseDateHeader("Thu, 19 Dec 2024 05:51:35 +0000 (UTC)");
        Assert.NotNull(result);
        Assert.Equal(2024, result!.Value.Year);
        Assert.Equal(12, result.Value.Month);
    }

    [Fact]
    public void ParseDateHeader_NumericTimezone_Rfc2822_ReturnsCorrect()
    {
        var result = GmailFetchService.ParseDateHeader("Tue, 01 Mar 2022 14:00:00 -0500");
        Assert.NotNull(result);
        Assert.Equal(2022, result!.Value.Year);
    }

    [Fact]
    public void ParseDateHeader_CompactFormat_ReturnsCorrect()
    {
        var result = GmailFetchService.ParseDateHeader("1 Mar 2022 14:00:00 -0500");
        Assert.NotNull(result);
        Assert.Equal(2022, result!.Value.Year);
    }

    [Fact]
    public void ParseDateHeader_InvalidString_ReturnsNull()
    {
        Assert.Null(GmailFetchService.ParseDateHeader("not a date"));
        Assert.Null(GmailFetchService.ParseDateHeader("garbage text here"));
    }

    [Fact]
    public void ParseDateHeader_RealisticGmailFormat_ReturnsCorrect()
    {
        var result = GmailFetchService.ParseDateHeader("Thu, 19 Dec 2024 05:51:35 +0000");
        Assert.NotNull(result);
        Assert.Equal(2024, result!.Value.Year);
        Assert.Equal(12, result.Value.Month);
        Assert.Equal(19, result.Value.Day);
    }

    #endregion

    #region ExtractEmailDomain

    [Theory]
    [InlineData("user@example.com", "example.com")]
    [InlineData("test@mail.google.com", "mail.google.com")]
    [InlineData("Name <user@domain.com>", "domain.com")]
    [InlineData("\"Display Name\" <contact@company.org>", "company.org")]
    [InlineData("noreply+nospam@service.io", "service.io")]
    public void ExtractEmailDomain_ValidFormats_ReturnsDomain(string input, string expected) =>
        Assert.Equal(expected, GmailFetchService.ExtractEmailDomain(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    [InlineData("no-at-sign-here")]
    public void ExtractEmailDomain_InvalidFormats_ReturnsEmpty(string? input) =>
        Assert.Equal(string.Empty, GmailFetchService.ExtractEmailDomain(input));

    [Fact]
    public void ExtractEmailDomain_AngleBrackets_ExtractsCorrectly() =>
        Assert.Equal("company.com", GmailFetchService.ExtractEmailDomain("<support@company.com>"));

    #endregion

    #region ParseInternalDate

    [Fact]
    public void ParseInternalDate_Null_ReturnsNull() =>
        Assert.Null(GmailFetchService.ParseInternalDate(null));

    [Fact]
    public void ParseInternalDate_Zero_ReturnsNull() =>
        Assert.Null(GmailFetchService.ParseInternalDate(0));

    [Fact]
    public void ParseInternalDate_Negative_ReturnsNull() =>
        Assert.Null(GmailFetchService.ParseInternalDate(-1));

    [Fact]
    public void ParseInternalDate_ValidMilliseconds_ReturnsCorrectDate()
    {
        var epochMs = 1705316400000L; // 2024-01-15T11:00:00 UTC
        var result = GmailFetchService.ParseInternalDate(epochMs);
        Assert.NotNull(result);
        Assert.Equal(2024, result!.Value.Year);
        Assert.Equal(1, result.Value.Month);
        Assert.Equal(15, result.Value.Day);
    }

    #endregion

    #region ParseEmailRecord

    private static Message BuildMessage(
        string id = "msg-1",
        string threadId = "thread-1",
        long? internalDateMs = null,
        Dictionary<string, string>? headers = null,
        List<string>? labelIds = null,
        string? snippet = null,
        int? sizeEstimate = null)
    {
        var message = new Message
        {
            Id = id,
            ThreadId = threadId,
            InternalDate = internalDateMs,
            Snippet = snippet,
            SizeEstimate = sizeEstimate,
            Payload = new MessagePart(),
            LabelIds = labelIds
        };

        if (headers != null)
        {
            message.Payload.Headers = [.. headers.Select(h => new MessagePartHeader
            {
                Name = h.Key,
                Value = h.Value
            })];
        }

        return message;
    }

    [Fact]
    public void ParseEmailRecord_BasicMessage_ReturnsCorrectFields()
    {
        var headers = new Dictionary<string, string>
        {
            ["From"] = "sender@example.com",
            ["To"] = "me@gmail.com",
            ["Subject"] = "Hello World",
            ["Date"] = "Mon, 15 Jan 2024 10:30:00 +0000"
        };

        var message = BuildMessage(
            id: "abc123",
            threadId: "t1",
            headers: headers,
            snippet: "Preview text",
            sizeEstimate: 1024);

        var record = GmailFetchService.ParseEmailRecord(message);

        Assert.Equal("abc123", record.MessageId);
        Assert.Equal("t1", record.ThreadId);
        Assert.Equal("sender@example.com", record.From);
        Assert.Equal("example.com", record.FromDomain);
        Assert.Equal("me@gmail.com", record.To);
        Assert.Equal("Hello World", record.Subject);
        Assert.Equal("Preview text", record.Snippet);
        Assert.Equal(1024, record.SizeEstimate);
    }

    [Fact]
    public void ParseEmailRecord_InternalDatePreferredOverDateHeader()
    {
        var headers = new Dictionary<string, string>
        {
            ["Date"] = "Mon, 15 Jan 2024 10:30:00 +0000"
        };

        var internalDateMs = 1734567890000L; // different date than header

        var message = BuildMessage(headers: headers, internalDateMs: internalDateMs);

        var record = GmailFetchService.ParseEmailRecord(message);

        Assert.NotNull(record.Date);
        Assert.Equal(1734567890000L, record.Date!.Value.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void ParseEmailRecord_NoInternalDate_FallsBackToDateHeader()
    {
        var headers = new Dictionary<string, string>
        {
            ["Date"] = "Mon, 15 Jan 2024 10:30:00 +0000"
        };

        var message = BuildMessage(headers: headers, internalDateMs: null);

        var record = GmailFetchService.ParseEmailRecord(message);

        Assert.NotNull(record.Date);
        Assert.Equal(2024, record.Date!.Value.Year);
    }

    [Fact]
    public void ParseEmailRecord_NoDateAtAll_ReturnsNullDate()
    {
        var headers = new Dictionary<string, string>
        {
            ["From"] = "sender@test.com"
        };

        var message = BuildMessage(headers: headers, internalDateMs: null);

        var record = GmailFetchService.ParseEmailRecord(message);

        Assert.Null(record.Date);
    }

    [Fact]
    public void ParseEmailRecord_ListUnsubscribe_Detected()
    {
        var headers = new Dictionary<string, string>
        {
            ["From"] = "newsletter@example.com",
            ["List-Unsubscribe"] = "<mailto:unsubscribe@example.com>",
            ["Date"] = "Mon, 15 Jan 2024 10:30:00 +0000"
        };

        var message = BuildMessage(headers: headers, internalDateMs: 1705316400000L);

        var record = GmailFetchService.ParseEmailRecord(message);

        Assert.True(record.HasListUnsubscribe);
    }

    [Fact]
    public void ParseEmailRecord_NoListUnsubscribe_False()
    {
        var headers = new Dictionary<string, string>
        {
            ["From"] = "person@gmail.com",
            ["Date"] = "Mon, 15 Jan 2024 10:30:00 +0000"
        };

        var message = BuildMessage(headers: headers, internalDateMs: 1705316400000L);

        var record = GmailFetchService.ParseEmailRecord(message);

        Assert.False(record.HasListUnsubscribe);
    }

    [Fact]
    public void ParseEmailRecord_GmailCategory_Extracted()
    {
        var headers = new Dictionary<string, string>
        {
            ["From"] = "alerts@social.com",
            ["Date"] = "Mon, 15 Jan 2024 10:30:00 +0000"
        };

        var message = BuildMessage(
            headers: headers,
            internalDateMs: 1705316400000L,
            labelIds: ["CATEGORY_SOCIAL", "INBOX", "UNREAD"]);

        var record = GmailFetchService.ParseEmailRecord(message);

        Assert.Equal("CATEGORY_SOCIAL", record.GmailCategory);
        Assert.Contains("CATEGORY_SOCIAL", record.GmailLabels);
    }

    [Fact]
    public void ParseEmailRecord_NoCategoryLabel_ReturnsEmpty()
    {
        var headers = new Dictionary<string, string>
        {
            ["From"] = "user@test.com",
            ["Date"] = "Mon, 15 Jan 2024 10:30:00 +0000"
        };

        var message = BuildMessage(
            headers: headers,
            internalDateMs: 1705316400000L,
            labelIds: ["INBOX"]);

        var record = GmailFetchService.ParseEmailRecord(message);

        Assert.Equal(string.Empty, record.GmailCategory);
    }

    [Fact]
    public void ParseEmailRecord_FetchedAtSetToCurrentTime()
    {
        var headers = new Dictionary<string, string>
        {
            ["From"] = "user@test.com",
            ["Date"] = "Mon, 15 Jan 2024 10:30:00 +0000"
        };

        var message = BuildMessage(headers: headers, internalDateMs: 1705316400000L);

        var before = DateTimeOffset.UtcNow;
        var record = GmailFetchService.ParseEmailRecord(message);
        var after = DateTimeOffset.UtcNow;

        Assert.InRange(record.FetchedAt, before, after);
    }

    [Fact]
    public void ParseEmailRecord_MissingFromHeader_DefaultsToEmpty()
    {
        var headers = new Dictionary<string, string>
        {
            ["Date"] = "Mon, 15 Jan 2024 10:30:00 +0000"
        };

        var message = BuildMessage(headers: headers, internalDateMs: 1705316400000L);

        var record = GmailFetchService.ParseEmailRecord(message);

        Assert.Equal(string.Empty, record.From);
        Assert.Equal(string.Empty, record.FromDomain);
    }

    [Fact]
    public void ParseEmailRecord_EmptyLabels_DefaultsToEmptyStrings()
    {
        var headers = new Dictionary<string, string>
        {
            ["From"] = "user@test.com",
            ["Date"] = "Mon, 15 Jan 2024 10:30:00 +0000"
        };

        var message = BuildMessage(headers: headers, internalDateMs: 1705316400000L);

        var record = GmailFetchService.ParseEmailRecord(message);

        Assert.Equal(string.Empty, record.GmailLabels);
        Assert.Equal(string.Empty, record.GmailCategory);
    }

    #endregion
}
