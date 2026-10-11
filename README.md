# Laplace-UCI

Configured UCI connections for Laplace's chess interfaces. The connector runs a named
engine locally, through SSH, or through an authenticated WebSocket session. Each session
owns one engine process. It preserves UCI commands, analysis and receipts; it neither
selects chess moves itself nor substitutes a different engine after a failure.

Laplace's live game admission remains part of the selected implementation. The Lichess
adapter must admit confirmed moves and outcomes to the same knowledge target used by
the selected Laplace engine. The configuration's `knowledge` label identifies that
binding; it is not a database connection string or evidence that the binding was tested.
Curated seed promotion and continuous admission of live observations are separate.

## Commands

```
laplace-uci-connect catalog --configuration installation.json
laplace-uci-connect verify --configuration installation.json --profile mono-staging
laplace-uci-connect connect --configuration installation.json --profile mono-staging
laplace-uci-connect serve --configuration installation.json
```

`connect` speaks UCI on stdin/stdout. Diagnostics go to stderr. It works with a UCI
client, the monorepo's Lichess adapter, Cutechess or Fastchess. `verify` performs the
engine's actual handshake, readiness, new-game initialization and depth-one search.
An absent move is a failure; the command never disables substrate participation.

`serve` binds loopback behind the installation's HTTPS ingress. Each `/uci/{profile}`
connection needs a bearer token from the configured private token file. Only declared
local process profiles are callable; clients cannot submit commands, paths or credentials
to spawn arbitrary processes. The installation limits concurrent sessions. An unavailable
profile returns 404, capacity exhaustion 429 and missing/incorrect authorization 401.

## Configuration

See `configuration.example.json`. All paths are installation configuration. Executable
arguments are arrays. Environment files are parsed as data; no shell expressions execute.
SSH uses existing host aliases, keys and strict host-key checking. SSH profiles should
invoke a remote `connect` command whose process profile owns remote paths and credentials.
WebSocket profiles require `wss://`; `ws://` is accepted only for loopback verification.
TLS validation is never bypassed. Tokens stay in private files populated by the host's
credential manager, not in profiles, URLs, command arguments or browser responses.

The three implementation lines retain their own engines: monorepo (`laplace-mono`),
Refactor (`laplace-refactor`) and modular (`laplace`). A profile does not imply an engine
exists or passes its invention-level acceptance; `verify` reports what actually answered.
Stockfish and Lc0 may be explicitly named comparison profiles. They are not fallbacks for
Laplace. Transport fallback or changing profiles during an active game is not automatic.

## Build and install

.NET 10 SDK is required to build; the ASP.NET Core 10 runtime runs the published service.

```
dotnet publish src/Laplace.Uci/Laplace.Uci.csproj -c Release -o <staging-directory>
```

Set `LAPLACE_UCI_BUILD_ROOT` to place intermediates outside the checkout. Admin setup
installs service identities, directory permissions, the private configuration and ingress.
Normal connections and chess operations do not require administrator privileges.

Protocol references: [Stockfish's UCI documentation](https://official-stockfish.github.io/docs/stockfish-wiki/UCI-&-Commands.html)
and [ASP.NET Core WebSockets](https://learn.microsoft.com/aspnet/core/fundamentals/websockets?view=aspnetcore-10.0).


Linux host installation uses `sudo python3 setup.py --installation <host.json> --enrollment <private-enrollment.json>`.
The declared revision must match the published connector. Setup installs an unprivileged
service, a scoped OpenBao Agent, private rendered credentials and declared nginx locations.
It requires a fresh agent render on every setup run. The installer checks listener health;
run `verify` for every engine profile before marking an environment usable.
The source enrollment is consumed; the installed root-only enrollment supports reruns.

Configuration precedence is `--configuration`, `LAPLACE_UCI_CONFIG`, then the current
user's application configuration directory under `Laplace/uci/configuration.json`.
An installation's service configuration is separate from a human's client profiles.
