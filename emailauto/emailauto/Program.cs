using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

var email = Environment.GetEnvironmentVariable("GMAIL_ADDRESS")
            ?? throw new Exception("Missing env var: GMAIL_ADDRESS");

var password = Environment.GetEnvironmentVariable("GMAIL_APP_PASSWORD")
               ?? throw new Exception("Missing env var: GMAIL_APP_PASSWORD");

// Optional override (defaults to hosts.csv in the working directory)
var hostsCsvPath = Environment.GetEnvironmentVariable("HOSTS_CSV_PATH") ?? "hosts.csv";

// Safety / throttling
const int MAX_PER_LOOP = 50;
var delay = TimeSpan.FromMinutes(1);

// Load disposable/spam host list
var disposableHosts = LoadHostsCsv(hostsCsvPath);
Console.WriteLine($"{DateTimeOffset.Now:u} Loaded {disposableHosts.Count} hosts from {hostsCsvPath}");

// Customize over time
var highAllowDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    // "yourcompany.com",
    // "yourbank.com",
};

var lowKeywords = new[]
{
    "unsubscribe", "newsletter", "promo", "promotion", "sale", "deal", "% off", "webinar", "limited time"
};

var highKeywords = new[]
{
    "invoice", "receipt", "order", "interview", "offer", "appointment", "urgent", "contract"
};

var personDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "gmail.com", "outlook.com", "hotmail.com", "live.com", "yahoo.com", "icloud.com", "me.com"
};

while (true)
{
    try
    {
        using var client = new ImapClient();
        client.Connect("imap.gmail.com", 993, SecureSocketOptions.SslOnConnect);
        client.Authenticate(email, password);

        var inbox = client.Inbox;
        inbox.Open(FolderAccess.ReadWrite);

        // Ensure label folders exist
        var lowFolder = await GetOrCreateFolderAsync(client, "Low");
        var medFolder = await GetOrCreateFolderAsync(client, "Med");
        var highFolder = await GetOrCreateFolderAsync(client, "High");

        // All mail currently in Inbox
        var uids = inbox.Search(SearchQuery.All);
        Console.WriteLine($"{DateTimeOffset.Now:u} Inbox messages found: {uids.Count}");

        int processed = 0;

        // Process newest-first
        foreach (var uid in uids.Reverse())
        {
            if (processed >= MAX_PER_LOOP)
                break;

            // Skip things already marked deleted
            var summary = inbox.Fetch(new[] { uid }, MessageSummaryItems.Flags).FirstOrDefault();
            if (summary?.Flags?.HasFlag(MessageFlags.Deleted) == true)
                continue;

            var msg = inbox.GetMessage(uid);

            var bucket = Classify(msg, disposableHosts, highAllowDomains, personDomains, lowKeywords, highKeywords);
            var target = bucket switch
            {
                Bucket.Low => lowFolder,
                Bucket.Med => medFolder,
                Bucket.High => highFolder,
                _ => medFolder
            };

            var fromMailbox = msg.From.Mailboxes.FirstOrDefault();
            var fromAddress = fromMailbox?.Address ?? "(unknown)";

            Console.WriteLine($"{DateTimeOffset.Now:u} [{target.Name}] {fromAddress} | {msg.Subject}");

            // Copy to label folder
            inbox.CopyTo(uid, target);

            // Mark seen (optional) and remove from Inbox
            inbox.AddFlags(uid, MessageFlags.Seen, true);
            inbox.AddFlags(uid, MessageFlags.Deleted, true);

            processed++;
        }

        if (processed > 0)
        {
            inbox.Expunge();
            Console.WriteLine($"{DateTimeOffset.Now:u} Processed {processed} message(s), expunged Inbox.");
        }

        client.Disconnect(true);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"{DateTimeOffset.Now:u} ERROR: {ex}");
    }

    await Task.Delay(delay);
}

static Bucket Classify(
    MimeMessage msg,
    HashSet<string> disposableHosts,
    HashSet<string> highAllowDomains,
    HashSet<string> personDomains,
    string[] lowKeywords,
    string[] highKeywords)
{
    var fromMailbox = msg.From.Mailboxes.FirstOrDefault();
    var fromAddress = fromMailbox?.Address ?? "";
    var fromDomain = ExtractDomain(fromAddress);

    var subject = (msg.Subject ?? "").ToLowerInvariant();

    // 1) Any no-reply variants -> Low
    var localPart = fromAddress.Contains('@') ? fromAddress.Split('@')[0] : fromAddress;
    if (Regex.IsMatch(localPart, @"\b(no[\-_]?reply|noreply|do[\-_]?not[\-_]?reply|donotreply)\b",
                      RegexOptions.IgnoreCase))
    {
        return Bucket.Low;
    }

    // 2) If domain is in disposable/spam host list -> Low
    if (!string.IsNullOrWhiteSpace(fromDomain) && disposableHosts.Contains(fromDomain))
        return Bucket.Low;

    // 3) Low keywords
    if (lowKeywords.Any(k => subject.Contains(k)))
        return Bucket.Low;

    // 4) High allowlist
    if (!string.IsNullOrWhiteSpace(fromDomain) && highAllowDomains.Contains(fromDomain))
        return Bucket.High;

    // 5) High keywords
    if (highKeywords.Any(k => subject.Contains(k)))
        return Bucket.High;

    // 6) Person-like domains -> High
    if (!string.IsNullOrWhiteSpace(fromDomain) && personDomains.Contains(fromDomain))
        return Bucket.High;

    // Default -> Med
    return Bucket.Med;
}

static string ExtractDomain(string email)
{
    if (string.IsNullOrWhiteSpace(email))
        return "";

    var at = email.LastIndexOf('@');
    if (at < 0 || at == email.Length - 1)
        return "";

    return email[(at + 1)..].Trim().Trim('.').ToLowerInvariant();
}

static HashSet<string> LoadHostsCsv(string path)
{
    var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    if (!File.Exists(path))
    {
        Console.WriteLine($"{DateTimeOffset.Now:u} WARNING: hosts.csv not found at '{path}'. Disposable-host filtering will be disabled.");
        return set;
    }

    foreach (var rawLine in File.ReadLines(path))
    {
        var line = rawLine.Trim();

        if (string.IsNullOrWhiteSpace(line))
            continue;

        // allow simple CSV like:
        // host
        // example.com
        if (line.Equals("host", StringComparison.OrdinalIgnoreCase))
            continue;

        // remove surrounding quotes if any
        line = line.Trim('"');

        // If there's a comma, take first column (defensive)
        var firstCol = line.Split(',')[0].Trim();
        if (string.IsNullOrWhiteSpace(firstCol))
            continue;

        set.Add(firstCol.ToLowerInvariant());
    }

    return set;
}

static async Task<IMailFolder> GetOrCreateFolderAsync(ImapClient client, string folderName)
{
    var root = await GetRootFolderAsync(client);

    var existing = await TryGetSubfolderAsync(root, folderName);
    if (existing != null) return existing;

    return await root.CreateAsync(folderName, true);
}

static async Task<IMailFolder> GetRootFolderAsync(ImapClient client)
{
    // Prefer the personal namespace root if available
    if (client.PersonalNamespaces != null && client.PersonalNamespaces.Count > 0)
        return await client.GetFolderAsync(client.PersonalNamespaces[0].Path);

    // Fallback: INBOX's parent is usually the root
    if (client.Inbox.ParentFolder != null)
        return client.Inbox.ParentFolder;

    // Last resort
    return await client.GetFolderAsync("INBOX");
}

static async Task<IMailFolder?> TryGetSubfolderAsync(IMailFolder root, string folderName)
{
    foreach (var f in await root.GetSubfoldersAsync(false))
    {
        if (string.Equals(f.Name, folderName, StringComparison.OrdinalIgnoreCase))
            return f;
    }
    return null;
}

enum Bucket { Low, Med, High }