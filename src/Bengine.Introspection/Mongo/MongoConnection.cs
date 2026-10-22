namespace Bengine.Introspection.Mongo;

using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;

/// <summary>
/// A MongoDB endpoint opened for one database: the client for the server and
/// the database a session works in. MongoDB clients are meant to be long-lived
/// (each runs its own pool and server monitoring), so one client per server
/// and credentials is shared by every connection opened through this class
/// and closed with <see cref="Close"/>.
/// </summary>
public sealed partial class MongoConnection
{
    private static readonly ConcurrentDictionary<string, MongoClient> Clients = new(StringComparer.Ordinal);

    private MongoConnection(MongoClient client, string database)
    {
        Client = client;
        DatabaseName = database;
    }

    /// <summary>Gets the shared client.</summary>
    public MongoClient Client { get; }

    /// <summary>Gets the name of the database the connection works in.</summary>
    public string DatabaseName { get; }

    /// <summary>Gets the database the connection works in.</summary>
    public IMongoDatabase Database => Client.GetDatabase(DatabaseName);

    /// <summary>Gets the <c>admin</c> database, for server-wide commands.</summary>
    public IMongoDatabase Admin => Client.GetDatabase("admin");

    /// <summary>Opens a connection: gets the shared client for the URL and checks the server answers and accepts the login.</summary>
    /// <param name="connectionString">A <c>mongodb://</c> or <c>mongodb+srv://</c> URL; its path names the database (default <c>test</c>).</param>
    /// <param name="owner">An id grouping the clients to close together (the server registration), or empty.</param>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    /// <returns>The open connection.</returns>
    /// <exception cref="IntrospectionException">The URL is invalid, or the server cannot be reached or refused the login.</exception>
    public static async Task<MongoConnection> OpenAsync(string connectionString, string owner = "", CancellationToken cancellationToken = default)
    {
        MongoUrl? url = null;
        MongoClientSettings settings;
        try
        {
            url = MongoUrl.Create(connectionString);

            // The driver reads the credential here: a user name without a password is refused, not sent.
            settings = Settings(url);
        }
        catch (Exception ex) when (ex is MongoConfigurationException or ArgumentException or FormatException)
        {
            throw new IntrospectionException(
                url is { Username.Length: > 0, Password: null or "" }
                    ? $"The MongoDB user {url.Username} needs a password: enter it, or clear the user name to connect without signing in."
                    : $"The MongoDB connection URL is invalid. {ex.Message}",
                ex);
        }

        var database = string.IsNullOrEmpty(url.DatabaseName) ? "test" : url.DatabaseName;
        var key = owner + "|" + new MongoUrlBuilder(url.ToString()) { DatabaseName = null }.ToMongoUrl();
        var client = Clients.GetOrAdd(key, _ => new MongoClient(settings));
        try
        {
            await client.GetDatabase(database)
                .RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A client that cannot connect (or holds a wrong password) is not kept.
            if (Clients.TryRemove(key, out var failed))
            {
                failed.Dispose();
            }

            throw new IntrospectionException($"Could not open a MongoDB connection. {Message(ex)}", ex);
        }

        return new MongoConnection(client, database);
    }

    /// <summary>Closes the clients opened for an owner (on disconnect or when the registration changes).</summary>
    /// <param name="owner">The owner id given to <see cref="OpenAsync"/>.</param>
    public static void Close(string owner)
    {
        foreach (var key in Clients.Keys.Where(k => k.StartsWith(owner + "|", StringComparison.Ordinal)).ToList())
        {
            if (Clients.TryRemove(key, out var client))
            {
                client.Dispose();
            }
        }
    }

    /// <summary>Gets a collection of the connection's database (or another database of the server).</summary>
    /// <param name="name">The collection name.</param>
    /// <param name="database">The database, or <c>null</c> for the connection's own.</param>
    /// <returns>The collection.</returns>
    public IMongoCollection<BsonDocument> Collection(string name, string? database = null) =>
        Client.GetDatabase(database ?? DatabaseName).GetCollection<BsonDocument>(name);

    /// <summary>Gets whether a failure was caused by rejected or missing credentials.</summary>
    /// <param name="exception">The failure.</param>
    /// <returns><c>true</c> if the server refused the login.</returns>
    public static bool IsAuthenticationError(Exception exception) => exception switch
    {
        MongoAuthenticationException => true,
        MongoCommandException { Code: 18 } => true,
        MongoCommandException { Code: 13 } command => command.ErrorMessage?.Contains("requires authentication", StringComparison.OrdinalIgnoreCase) == true,
        _ => false,
    };

    /// <summary>Turns a driver failure into a short message.</summary>
    /// <param name="exception">The failure.</param>
    /// <returns>The message.</returns>
    public static string Message(Exception exception) => exception switch
    {
        MongoAuthenticationException => "Authentication failed: check the user name, password, and authentication database.",
        MongoCommandException command => $"{command.ErrorMessage ?? command.Message} ({command.CodeName}, code {command.Code})",
        MongoWriteException { WriteError: { } error } => $"{error.Message} (code {error.Code})",
        MongoBulkWriteException bulk => string.Join("; ", bulk.WriteErrors.Select(e => $"{e.Message} (code {e.Code})")),
        TimeoutException timeout => "The MongoDB server did not answer in time"
            + (Reason().Match(timeout.Message) is { Success: true } reason ? $": {reason.Groups[1].Value.Trim()}." : "."),
        _ => exception.Message,
    };

    private static MongoClientSettings Settings(MongoUrl url)
    {
        var settings = MongoClientSettings.FromUrl(url);
        settings.ApplicationName ??= "Bengine";

        // A desktop client should fail fast when a server is down, not after 30 s.
        if (url.ServerSelectionTimeout == MongoDefaults.ServerSelectionTimeout)
        {
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(10);
        }

        if (url.ConnectTimeout == MongoDefaults.ConnectTimeout)
        {
            settings.ConnectTimeout = TimeSpan.FromSeconds(10);
        }

        return settings;
    }

    // The innermost cause in a server-selection timeout's cluster description.
    [GeneratedRegex(@"(?:SocketException \(\d+\)|MongoAuthenticationException|AuthenticationException|IOException): ([^\r\n""}]+)")]
    private static partial Regex Reason();
}
