using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;

namespace Laplace.Uci;

public interface IUciConnection : IAsyncDisposable
{
    ValueTask SendAsync(string line, CancellationToken ct);
    ValueTask<string?> ReceiveAsync(CancellationToken ct);
}

public static class Connections
{
    public static async Task<IUciConnection> OpenAsync(EngineProfile profile, int maxLineBytes, CancellationToken ct)
    {
        if (profile.Transport == "websocket")
        {
            var socket = new ClientWebSocket();
            try
            {
                socket.Options.SetRequestHeader("Authorization", "Bearer " + ConnectorConfiguration.Token(profile.TokenFile));
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(15);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(profile.ConnectTimeoutSeconds));
                await socket.ConnectAsync(new Uri(profile.Endpoint!), deadline.Token);
                return new WebSocketConnection(socket, maxLineBytes);
            }
            catch { socket.Dispose(); throw; }
        }
        return new ProcessConnection(profile, maxLineBytes);
    }

    public static async Task RelayAsync(IUciConnection left, IUciConnection right, CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        async Task Pump(IUciConnection source, IUciConnection target)
        {
            while (await source.ReceiveAsync(lifetime.Token) is { } line)
            {
                await target.SendAsync(line, lifetime.Token);
                if (line.Trim() == "quit") return;
            }
        }
        var a = Task.Run(() => Pump(left, right), lifetime.Token);
        var b = Task.Run(() => Pump(right, left), lifetime.Token);
        var first = await Task.WhenAny(a, b);
        lifetime.Cancel();
        try { await first; }
        finally
        {
            try { await Task.WhenAll(a, b); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
    }
}

public sealed class TextConnection(TextReader input, TextWriter output, int maxLineBytes) : IUciConnection
{
    private readonly char[] buffer = new char[4096];
    private int offset;
    private int length;
    public async ValueTask SendAsync(string line, CancellationToken ct)
    {
        Validate(line, maxLineBytes);
        await output.WriteLineAsync(line.AsMemory(), ct);
        await output.FlushAsync(ct);
    }
    public async ValueTask<string?> ReceiveAsync(CancellationToken ct)
    {
        // ReadLineAsync could allocate arbitrarily before checking a malicious peer's limit.
        var line = new StringBuilder();
        while (true)
        {
            if (offset == length)
            {
                length = await input.ReadAsync(buffer.AsMemory(), ct).AsTask().WaitAsync(ct);
                offset = 0;
                if (length == 0) return line.Length == 0 ? null : Complete(line);
            }
            int start = offset;
            while (offset < length && buffer[offset] != '\n') offset++;
            line.Append(buffer, start, offset - start);
            if (line.Length > maxLineBytes) throw new InvalidDataException("UCI line exceeds configured limit.");
            if (offset < length) { offset++; return Complete(line); }
        }
    }
    private string Complete(StringBuilder line)
    {
        var value = line.ToString().TrimEnd('\r');
        Validate(value, maxLineBytes);
        return value;
    }
    public static void Validate(string line, int limit)
    {
        if (line.Contains('\n') || line.Contains('\r') || Encoding.UTF8.GetByteCount(line) > limit)
            throw new InvalidDataException("Invalid or oversized UCI line.");
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class ProcessConnection : IUciConnection
{
    private readonly Process process;
    private readonly TextConnection text;
    private readonly Task errors;
    public ProcessConnection(EngineProfile profile, int limit)
    {
        var info = new ProcessStartInfo(profile.Transport == "ssh" ? "ssh" : profile.Executable!)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        if (profile.Transport == "ssh")
        {
            // Only the installed profile supplies shell arguments, never an HTTP caller.
            foreach (var arg in new[] { "-T", "-oBatchMode=yes", "-oStrictHostKeyChecking=yes",
                $"-oConnectTimeout={profile.ConnectTimeoutSeconds}", "--", profile.SshHost! }) info.ArgumentList.Add(arg);
            static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";
            info.ArgumentList.Add("exec " + string.Join(' ', new[] { profile.Executable! }.Concat(profile.Arguments).Select(Quote)));
        }
        else
        {
            foreach (var arg in profile.Arguments) info.ArgumentList.Add(arg);
            if (profile.WorkingDirectory is not null) info.WorkingDirectory = profile.WorkingDirectory;
            foreach (var file in profile.EnvironmentFiles)
            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;
                int equals = line.IndexOf('=');
                if (equals < 1) throw new InvalidDataException("Invalid engine environment file.");
                var key = line[..equals].Trim();
                var value = line[(equals + 1)..].Trim();
                if (value.Length > 1 && value[0] is '\'' or '"' && value[^1] == value[0]) value = value[1..^1];
                info.Environment[key] = value;
            }
        }
        process = Process.Start(info) ?? throw new IOException("Engine process did not start.");
        text = new TextConnection(process.StandardOutput, process.StandardInput, limit);
        // Engine diagnostics retain stderr; only UCI messages reach stdout.
        errors = Task.Run(async () =>
        {
            var buffer = new char[4096];
            int count;
            while ((count = await process.StandardError.ReadAsync(buffer)) != 0)
                await Console.Error.WriteAsync(buffer.AsMemory(0, count));
        });
    }
    public ValueTask SendAsync(string line, CancellationToken ct) => text.SendAsync(line, ct);
    public async ValueTask<string?> ReceiveAsync(CancellationToken ct)
    {
        var line = await text.ReceiveAsync(ct);
        if (line is null && process.HasExited && process.ExitCode != 0)
            throw new IOException($"Engine connection exited with code {process.ExitCode}.");
        return line;
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!process.HasExited)
            {
                try { await process.StandardInput.WriteLineAsync("quit"); process.StandardInput.Close(); } catch (IOException) { }
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (TimeoutException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            }
            await errors;
        }
        finally { process.Dispose(); }
    }
}

public sealed class WebSocketConnection(WebSocket socket, int limit) : IUciConnection
{
    public async ValueTask SendAsync(string line, CancellationToken ct)
    {
        TextConnection.Validate(line, limit);
        await socket.SendAsync(Encoding.UTF8.GetBytes(line).AsMemory(), WebSocketMessageType.Text, true, ct);
    }
    public async ValueTask<string?> ReceiveAsync(CancellationToken ct)
    {
        var buffer = new byte[Math.Min(limit, 4096)];
        using var data = new MemoryStream();
        while (true)
        {
            var message = await socket.ReceiveAsync(buffer.AsMemory(), ct);
            if (message.MessageType == WebSocketMessageType.Close) return null;
            if (message.MessageType != WebSocketMessageType.Text || data.Length + message.Count > limit)
                throw new InvalidDataException("Invalid or oversized UCI WebSocket message.");
            data.Write(buffer, 0, message.Count);
            if (message.EndOfMessage)
            {
                var line = new UTF8Encoding(false, true).GetString(data.ToArray());
                TextConnection.Validate(line, limit);
                return line;
            }
        }
    }
    public ValueTask DisposeAsync() { socket.Abort(); socket.Dispose(); return ValueTask.CompletedTask; }
}
