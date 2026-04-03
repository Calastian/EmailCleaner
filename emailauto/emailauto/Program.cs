using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;

var email = Environment.GetEnvironmentVariable("GMAIL_ADDRESS")
            ?? throw new Exception("Missing env var: GMAIL_ADDRESS");

var password = Environment.GetEnvironmentVariable("GMAIL_APP_PASSWORD")
               ?? throw new Exception("Missing env var: GMAIL_APP_PASSWORD");

while (true)
{
    try
    {
        using var client = new ImapClient();
        client.Connect("imap.gmail.com", 993, MailKit.Security.SecureSocketOptions.SslOnConnect);
        client.Authenticate(email, password);

        var inbox = client.Inbox;
        inbox.Open(FolderAccess.ReadOnly);

        var uids = inbox.Search(SearchQuery.NotSeen);
        Console.WriteLine($"{DateTimeOffset.Now:u} Unread messages: {uids.Count}");

        foreach (var uid in uids.Take(10))
        {
            var message = inbox.GetMessage(uid);
            Console.WriteLine($"From: {message.From} | Subject: {message.Subject}");
        }

        client.Disconnect(true);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"{DateTimeOffset.Now:u} ERROR: {ex.Message}");
    }

    await Task.Delay(TimeSpan.FromMinutes(1));
}