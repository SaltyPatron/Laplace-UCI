# Configuration reference

The connector reads one JSON document. Resolution order is `--configuration FILE`,
`LAPLACE_UCI_CONFIG`, then `<ApplicationData>/Laplace/uci/configuration.json`.
The installed desktop command supplies its installation's file explicitly, so it
works from any working directory. Configuration is read when a connector starts;
token files and engine environment files are read when a session opens.

## Connector

| Field | Default / requirement | Effect |
| --- | --- | --- |
| `version` | `1`; only version 1 accepted | Configuration format |
| `profiles` | Object keyed by profile name | Names use ASCII letters, digits, `_`, `-` |
| `listen` | `http://127.0.0.1:5192` | Loopback listener for `serve`; IIS supplies its own listener when hosted in-process |
| `serverTokenFile` | Required by `serve` | Private bearer token, at least 32 characters without whitespace; reread for every incoming request |
| `maxSessions` | `4`; range 1–128 | Concurrent engine processes per connector server; excess sessions receive HTTP 429 |
| `maxLineBytes` | `65536`; range 1024–1048576 | Maximum UTF-8 UCI line/frame size |

Unknown connector or profile properties fail parsing. Limits describe this transport
installation; they do not limit Laplace's knowledge representation.

## Each engine profile

| Field | Default / requirement | Effect |
| --- | --- | --- |
| `label` | Required nonempty string | Human-readable engine name |
| `implementation` | Required nonempty string | Implementation identity, e.g. `laplace-mono` |
| `environment` | Required nonempty string | Installation environment label |
| `knowledge` | Required nonempty string | Human-readable knowledge target; not a credential or proof of database identity |
| `transport` | Required: `process`, `ssh`, `websocket` | Connection mechanism |
| `executable` | Required for process/SSH | Local engine executable, or installed remote connector executable |
| `arguments` | `[]` | Argument array; local invocation does not use a shell |
| `environmentFiles` | `[]`; process only | Ordered `NAME=value` files; later assignments replace earlier ones |
| `workingDirectory` | Inherited; process only | Engine working directory |
| `sshHost` | Required for SSH | Existing SSH host alias/address; batch mode and strict host-key checking are enabled |
| `endpoint` | Required for WebSocket | `wss://` URL, or `ws://` on loopback only; credentials/query/fragment are rejected |
| `tokenFile` | Required for WebSocket | Private bearer token read for each connection |
| `connectTimeoutSeconds` | `15`; range 1–120 | SSH/WebSocket connection establishment timeout |

The server accepts only its declared `process` profiles. A remote caller cannot
supply an executable or environment. Each accepted session owns one process;
closing the connection disposes that process. WebSocket keepalive interval and
timeout are 15 seconds. `verify` allows 120 seconds for handshake, readiness,
new-game initialization and a real depth-one search.

The monorepo's Lichess controller additionally checks actual PostgreSQL cluster and
database identity reported by the engine, and requests `Refresh Knowledge` before
each decision. Those semantics belong to the monorepo, not this generic transport.

## Host installation

Host declarations and private enrollments are separate files. An administrator
runs setup; ordinary `catalog`, `connect` and `verify` operations need no elevation.
Secrets are held in scoped OpenBao records and rendered into private files.

| Linux declaration | Meaning |
| --- | --- |
| `host`, `user`, `service` | Target hostname and dedicated OS/systemd identities |
| `install_directory`, `revision`, `publish_directory` | Versioned connector installation and its already-published input |
| `state_directory`, `configuration_directory` | Mutable state/credentials and root-owned configuration |
| `connector` | Connector document described above |
| `secret_files` | Map OpenBao field names to filenames under the private secrets directory |
| `public_files` | Map supporting filenames to text, e.g. the database trust certificate |
| `nginx_server_files` | Optional existing nginx server files receiving the UCI WebSocket location |
| `entrypoint` | Absolute installed CLI symlink |

Linux enrollment fields are `address`, `record`, `role_id`, `secret_id`; the
installation enrollments also identify their host. The workload reads exactly its
declared record. `--render` is an agent operation, not a human launch command.

| Windows IIS declaration | Meaning |
| --- | --- |
| `host`, `site`, `hostname` | Target Windows machine, dedicated IIS pool/site and HTTPS name |
| `directory`, `publish`, `revision` | Installation root and published connector input |
| `engine` | Installed engine executable; its directory receives pool read/execute access |
| `bao` | Existing OpenBao executable copied into the protected service installation |
| `address`, `runtime_record`, `certificate_record` | HTTPS OpenBao address and exact KV paths |
| `database_certificate` | PEM trust certificate text |
| `remote_clients` | Allowed source addresses/subnets for the setup's TCP 443 firewall rule |
| `connector` | Connector document described above |

The Windows runtime record supplies `server_token` and `engine_env`; the certificate
record supplies `pfx_base64` and `password`. The private enrollment contains `host`,
`address`, `role_id`, `secret_id`. The credential agent checks static secrets every
minute. IIS uses its own application pool identity, with idle and periodic recycling
disabled to avoid cutting off an engine session on an otherwise idle web server.
`-Render` is an administrator credential-agent operation. `-Plan` reads the declaration
without installing. Setup verifies HTTPS; `verify` proves an engine session separately.

Certificate distribution imports renewed material supplied by the issuer. Neither
connector installer issues an ACME certificate, starts a bot, promotes seeds or
switches a knowledge database.
