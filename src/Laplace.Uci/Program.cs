using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Laplace.Uci;

try
{
    string Value(string name) => Array.IndexOf(args, name) is var i && i >= 0 && i + 1 < args.Length
        ? args[i + 1] : throw new ArgumentException($"Missing {name}.");
    var command = args.FirstOrDefault() ?? "help";
    if (command == "help")
    {
        Console.WriteLine("laplace-uci-connect catalog|connect|verify|serve --configuration FILE [--profile NAME]");
        return 0;
    }
    var config = ConnectorConfiguration.Read(Value("--configuration"));
    using var lifetime = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; lifetime.Cancel(); };
    if (command == "catalog")
    {
        Console.WriteLine(JsonSerializer.Serialize(config.Profiles.Select(p => new
        {
            id = p.Key, p.Value.Label, p.Value.Implementation, p.Value.Environment, p.Value.Knowledge, p.Value.Transport
        }), ConnectorConfiguration.Json));
        return 0;
    }
    if (command is "connect" or "verify")
    {
        var id = Value("--profile");
        var profile = config.Get(id);
        await using var engine = await Connections.OpenAsync(profile, config.MaxLineBytes, lifetime.Token);
        if (command == "connect")
        {
            using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false, true));
            using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
            await Connections.RelayAsync(new TextConnection(input, output, config.MaxLineBytes), engine, lifetime.Token);
        }
        else
        {
            lifetime.CancelAfter(TimeSpan.FromSeconds(120));
            var lines = new List<string>();
            async Task Until(string prefix)
            {
                while (await engine.ReceiveAsync(lifetime.Token) is { } line)
                {
                    if (line.StartsWith("info string ")) Console.Error.WriteLine(line);
                    if (line.StartsWith("id ") || line.StartsWith("bestmove ") || line.StartsWith("info string providers"))
                    {
                        if (lines.Count == 256) lines.RemoveAt(0);
                        lines.Add(line);
                    }
                    if (line == prefix || line.StartsWith(prefix + " ")) return;
                }
                throw new IOException($"Engine closed before {prefix}.");
            }
            await engine.SendAsync("uci", lifetime.Token); await Until("uciok");
            await engine.SendAsync("isready", lifetime.Token); await Until("readyok");
            await engine.SendAsync("ucinewgame", lifetime.Token);
            await engine.SendAsync("isready", lifetime.Token); await Until("readyok");
            await engine.SendAsync("position startpos", lifetime.Token);
            await engine.SendAsync("go depth 1", lifetime.Token); await Until("bestmove");
            if (lines.Last() is "bestmove 0000" or "bestmove (none)") throw new IOException("Engine produced no move from the initial position.");
            Console.WriteLine(JsonSerializer.Serialize(new { profile = id, profile.Implementation, profile.Environment,
                profile.Knowledge, verifiedUtc = DateTimeOffset.UtcNow, lines }, ConnectorConfiguration.Json));
        }
        return 0;
    }
    if (command != "serve") throw new ArgumentException("Unknown command.");
    var expected = SHA256.HashData(Encoding.UTF8.GetBytes(ConnectorConfiguration.Token(config.ServerTokenFile)));
    var uri = new Uri(config.Listen);
    if (!uri.IsLoopback) throw new InvalidDataException("Bind the connector behind the installation's authenticated HTTPS ingress on loopback.");
    var builder = WebApplication.CreateBuilder();
    builder.WebHost.UseUrls(config.Listen);
    var app = builder.Build();
    app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15), KeepAliveTimeout = TimeSpan.FromSeconds(15) });
    using var slots = new SemaphoreSlim(config.MaxSessions);
    app.MapGet("/health/live", () => Results.Json(new { service = "laplace-uci", live = true }));
    app.Map("/uci/{profile}", async (HttpContext context, string profile) =>
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(authorization.StartsWith("Bearer ", StringComparison.Ordinal) ? authorization[7..] : ""));
        if (!CryptographicOperations.FixedTimeEquals(actual, expected)) { context.Response.StatusCode = 401; return; }
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
        if (!config.Profiles.TryGetValue(profile, out var selected) || selected.Transport != "process") { context.Response.StatusCode = 404; return; }
        if (!await slots.WaitAsync(0, context.RequestAborted)) { context.Response.StatusCode = 429; return; }
        try
        {
            await using var engine = await Connections.OpenAsync(selected, config.MaxLineBytes, context.RequestAborted);
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await using var peer = new WebSocketConnection(socket, config.MaxLineBytes);
            await Connections.RelayAsync(peer, engine, context.RequestAborted);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or System.Net.WebSockets.WebSocketException)
        {
            app.Logger.LogWarning("UCI session ended for profile {Profile}: {Failure}", profile, ex.GetType().Name);
            context.Abort();
        }
        finally { slots.Release(); }
    });
    await app.RunAsync();
    return 0;
}
catch (Exception ex)
{
    // Transport errors can contain connection details; protocol stdout remains untouched.
    Console.Error.WriteLine($"laplace-uci-connect: {ex.GetType().Name}: {ex.Message}");
    return 1;
}
