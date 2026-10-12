using System.Text.Json;
using System.Text.Json.Serialization;

namespace Laplace.Uci;

public sealed record PublishedEngineRelease
{
    public required string Directory { get; init; }
    public required string SelectionFile { get; init; }
    public required string Executable { get; init; }

    public string Resolve()
    {
        if (!Path.IsPathFullyQualified(Directory) || !Path.IsPathFullyQualified(SelectionFile)
            || string.IsNullOrWhiteSpace(Executable) || Path.IsPathRooted(Executable)
            || Executable.Split('/', '\\').Any(p => p is ".." or "." or ""))
            throw new InvalidDataException("A published engine needs absolute release/selection paths and a relative executable path without traversal.");
        using var selection = JsonDocument.Parse(File.ReadAllText(SelectionFile));
        var revision = selection.RootElement.GetProperty("revision").GetString();
        if (revision is null || revision.Length != 64 || revision.Any(c => !char.IsAsciiHexDigit(c)))
            throw new InvalidDataException("Invalid selected engine release revision.");
        string path = Path.Combine(Directory, revision, Executable);
        if (!File.Exists(path)) throw new FileNotFoundException("The selected release has no declared engine executable.", path);
        return Path.GetFullPath(path);
    }
}

public sealed record EngineProfile
{
    public required string Label { get; init; }
    public required string Implementation { get; init; }
    public required string Environment { get; init; }
    public required string Knowledge { get; init; }
    public required string Transport { get; init; }
    public string? Executable { get; init; }
    public PublishedEngineRelease? Release { get; init; }
    public string[] Arguments { get; init; } = [];
    public string[] EnvironmentFiles { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public string? SshHost { get; init; }
    public string? Endpoint { get; init; }
    public string? TokenFile { get; init; }
    public int ConnectTimeoutSeconds { get; init; } = 15;
    public string ResolveExecutable() => Release?.Resolve() ?? Executable
        ?? throw new InvalidDataException("The engine profile has no executable.");
}

public sealed record ConnectorConfiguration
{
    public int Version { get; init; } = 1;
    public Dictionary<string, EngineProfile> Profiles { get; init; } = [];
    public string Listen { get; init; } = "http://127.0.0.1:5192";
    public string? ServerTokenFile { get; init; }
    public int MaxSessions { get; init; } = 4;
    public int MaxLineBytes { get; init; } = 65536;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    public static ConnectorConfiguration Read(string path)
    {
        var value = JsonSerializer.Deserialize<ConnectorConfiguration>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("Empty connector configuration.");
        if (value.Version != 1 || value.MaxSessions is < 1 or > 128 || value.MaxLineBytes is < 1024 or > 1048576)
            throw new InvalidDataException("Unsupported connector version or limits.");
        foreach (var (id, p) in value.Profiles)
        {
            if (id.Length == 0 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
                throw new InvalidDataException("Profile names must contain letters, digits, hyphens or underscores.");
            if (new[] { p.Label, p.Implementation, p.Environment, p.Knowledge }.Any(string.IsNullOrWhiteSpace)
                || p.ConnectTimeoutSeconds is < 1 or > 120)
                throw new InvalidDataException($"Profile {id} has missing identity or invalid timeout.");
            if (p.Transport is "process" or "ssh")
            {
                if (p.Release is not null)
                {
                    if (p.Transport != "process" || p.Executable is not null)
                        throw new InvalidDataException($"Profile {id}: a published release replaces the local process executable.");
                    _ = p.Release.Resolve();
                }
                else if (string.IsNullOrWhiteSpace(p.Executable)) throw new InvalidDataException($"Profile {id} needs an executable.");
                if (p.Transport == "ssh" && (string.IsNullOrWhiteSpace(p.SshHost) || p.SshHost.StartsWith('-')
                    || p.SshHost.Any(char.IsWhiteSpace))) throw new InvalidDataException($"Profile {id} needs an SSH host alias.");
                if (p.Transport == "ssh" && (p.EnvironmentFiles.Length != 0 || p.WorkingDirectory is not null))
                    throw new InvalidDataException($"Profile {id}: configure environment files and working directory on the remote process profile.");
            }
            else if (p.Transport == "websocket")
            {
                if (p.Release is not null) throw new InvalidDataException($"Profile {id}: configure the release on the remote process profile.");
                if (!Uri.TryCreate(p.Endpoint, UriKind.Absolute, out var uri)
                    || (uri.Scheme != "wss" && !(uri.Scheme == "ws" && uri.IsLoopback))
                    || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
                    || string.IsNullOrWhiteSpace(p.TokenFile))
                    throw new InvalidDataException($"Profile {id} needs a secure WebSocket endpoint and token file.");
            }
            else throw new InvalidDataException($"Profile {id} has unsupported transport.");
        }
        return value;
    }

    public EngineProfile Get(string id) => Profiles.GetValueOrDefault(id)
        ?? throw new InvalidDataException($"Unknown UCI profile: {id}");

    public static string Token(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("A token file is required.");
        var token = File.ReadAllText(path).Trim();
        if (token.Length < 32 || token.Any(char.IsWhiteSpace)) throw new InvalidDataException("Invalid connector token file.");
        return token;
    }
}
