# tools-mcp hosted server Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `tools-mcp serve` — the 21 tools over MCP Streamable HTTP at `https://tools.encycam.com/mcp`, each call running
as the person whose Keycloak token it carries, files handed out as one-hour links — for the Hermes agent.

**Architecture:** Port dmc-mcp's hosted mode (`C:\spryt\dmc-mcp\src`: `ServeCommand`, `Door`, `UserTokens`, `IssuerKeys`,
`Caller`, the upload store's shape) into `C:\spryt\tools-mcp`, without the key. A per-request `TokenProvider` hands the
tools the caller's token, so the tool classes keep their code; a file sink replaces "save to a folder" with "keep and
link" on the hosted server. A container built from the NuGet package runs beside dmc-mcp on the tools server.

**Tech Stack:** .NET 8 (`RollForward=Major`), ASP.NET Core, ModelContextProtocol + ModelContextProtocol.AspNetCore 1.4.1,
Microsoft.IdentityModel.JsonWebTokens 8.23.0, xUnit, Docker, nginx.

**Spec:** `docs/superpowers/specs/2026-10-22-hosted-server-design.md` (this repository). The pattern's own designs:
`C:\spryt\dmc-mcp\docs\superpowers\specs\2026-10-06-remote-server-design.md`, `2026-10-08-per-user-sign-in-design.md`.

## Global Constraints

- URL `https://tools.encycam.com/mcp`; container port 8080 published on `127.0.0.1:8097`; `--memory 512m --memory-swap 512m --pids-limit 128`; docker logs `json-file` `max-size=10m` `max-file=3`.
- Settings (environment): `TOOLS_MCP_ISSUER` (default `https://webservices.encycam.com/keycloak/realms/licsys`), `TOOLS_MCP_CLIENTS`, `TOOLS_MCP_AUDIENCE` — at least one of the last two, or `serve` refuses to start; `TOOLS_MCP_PUBLIC_URL` (default `https://tools.encycam.com`, https and no path); `TOOLS_MCP_DATA` (default `/data`).
- Prod values: `TOOLS_MCP_CLIENTS=dealerspace-mcp,tools-encycam`; the backend's `TOOLSDB_ACCEPTED_CLIENTS` gains `dealerspace-mcp`.
- **No key.** Only `Authorization: Bearer <access token>`: issuer as above, asymmetric signature from the realm's keys, `typ=Bearer`, a `sub`, `exp`/`nbf` within 30 s, `azp` in the clients or `aud` in the audiences — as dmc-mcp's `UserTokens`.
- 401 carries `WWW-Authenticate: Bearer … resource_metadata="https://tools.encycam.com/.well-known/oauth-protected-resource/mcp"`; that metadata (and the one without `/mcp`) is public.
- Transport stateless (`WithHttpTransport(o => o.Stateless = true)`); `Microsoft.AspNetCore` logs at Warning; a token is never stored, logged or echoed.
- Files: `https://tools.encycam.com/mcp/files/<32 hex>/<name>`, 1 hour, opens without a token; 100 MB a file, 1 GB in all, 300 MB a person; a `folder` argument is refused on the hosted server.
- One log line per `tools/call`: the tool, the first 12 hex of the caller's owner hash, and `kit`, `classCode`, `page`, `pageSize` when given.
- Hosted wording: a refused sign-in says `the person must sign in to tools.encycam.com again in the agent`, not `run tools-mcp login`.
- Version 0.2.0. The stdio mode does not change: all 93 existing tests keep passing.
- House style as in the rest of the repository: comments say why, with dates; commits `area: sentence` + `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **A licsys token from another client** (`dealer-space`, any other app of the realm) → 401, and no tool runs. Test: Task 2 `ATokenFromAnotherClientIsRefused`.
2. **Two people calling at the same moment** → each call carries only its own token; nothing leaks through a singleton. Test: Task 2 `TwoPeopleAtOnceEachRunAsThemselves`.
3. **A link after its hour, or an id never handed out** → a 404 that says nothing more; within the hour it opens without a token. Tests: Task 3 `ALinkOpensWithoutATokenForAnHour`, `AnUnknownOrExpiredLinkIs404`.
4. **A kit over 100 MB, or a person over 300 MB** → a reply that says so and keeps no half-file. Test: Task 3 `AFileOverTheLimitIsRefusedAndNotKept`.
5. **The token in a log or a reply** — on success and on every refusal path → never. Tests: Task 2 `NoTokenReachesTheLog`, Task 4 `TheCallLineNamesNoToken`.

---

### Task 1: Checking a person's token

**Files:**
- Modify: `src/ToolsMcp.csproj` — add `ModelContextProtocol.AspNetCore` 1.4.1, `Microsoft.IdentityModel.JsonWebTokens` 8.23.0, `<FrameworkReference Include="Microsoft.AspNetCore.App" />`; `Version` 0.2.0.
- Create (ported from `C:\spryt\dmc-mcp\src`, namespace `ToolsMcp`): `src/UserTokens.cs` (`TokenSettings`, `TokenCheck`, `UserTokens`), `src/IssuerKeys.cs`, `src/Caller.cs`.
- Create (ported from `C:\spryt\dmc-mcp\tests`): `tests/TestIssuer.cs`, `tests/UserTokensTests.cs`, `tests/IssuerKeysTests.cs`, `tests/CallerTests.cs`.

**Interfaces:**
- Produces: `TokenSettings(string issuer, IReadOnlyList<string> clients, IReadOnlyList<string> audiences)`, `static TokenSettings? Parse(string? issuer, string? clients, string? audience)` — null when neither clients nor audience is set (no "off" here: the hosted tools server always checks tokens); `UserTokens(TokenSettings, IssuerKeys)` with `Task<TokenCheck> Check(string token, CancellationToken)`; `TokenCheck(Caller? Caller, string? Refusal, bool Unavailable)`; `Caller` with `Person(token, name, issuer, subject)`, `AccessToken`, `Name`, `Owner` (`"user:" + sha256(iss+"\n"+sub)`), `TokenHash`, `static Caller? Current`, `static IDisposable Enter(Caller)`.
- Changes from dmc-mcp: no `Caller.Server` and no key; `TokenSettings.DefaultIssuer` from `Brand`; messages name tools.encycam.com.

- [ ] **Step 1:** Port the four test files, adapted: no key or server-account cases; `TokenSettings.Parse(null, null, null)` is `null`; the test realm's client is `dealerspace-mcp`. Keep dmc-mcp's cases for issuer, signature, `typ`, lifetime, `azp`/`aud`, key rotation, Keycloak down (503), and the AsyncLocal scope ending with the request.
- [ ] **Step 2:** `dotnet test tests/ToolsMcp.Tests.csproj` — Expected: compile errors, the types are missing.
- [ ] **Step 3:** Port the three source files as described.
- [ ] **Step 4:** `dotnet test tests/ToolsMcp.Tests.csproj` — Expected: all pass (93 + the ported ones).
- [ ] **Step 5:** `git commit -m "tokens: a person's own Keycloak token, checked as dmc-mcp checks it"`

### Task 2: `tools-mcp serve` — the door and the per-request sign-in

**Files:**
- Create: `src/ServeCommand.cs` (`ServeSettings`, `ServeCommand`), `src/Door.cs`, `src/CallerTokens.cs`.
- Modify: `src/Replies.cs` (the sign-in fix text), `src/Program.cs` (`serve` → `return await ServeCommand.Run(args[1..]);`).
- Create: `tests/ServeTests.cs`.

**Interfaces:**
- Consumes: Task 1's `UserTokens`, `TokenSettings`, `Caller`.
- Produces:
  - `ServeSettings(string DataDir, TokenSettings Tokens, string PublicUrl)` with `ResourceUrl` (`PublicUrl + "/mcp"`) and `MetadataUrl` (`PublicUrl + "/.well-known/oauth-protected-resource/mcp"`).
  - `ServeCommand.Read(Func<string, string?> env) → (ServeSettings? Settings, string? Error)` — error `set TOOLS_MCP_CLIENTS (Hermes's Keycloak client) or TOOLS_MCP_AUDIENCE: without them no token can be accepted` when both are empty; `TOOLS_MCP_PUBLIC_URL` checked as dmc-mcp checks `DMC_MCP_PUBLIC_URL`.
  - `ServeCommand.Build(string[] args, ServeSettings settings, Action<IServiceCollection>? configure = null) → WebApplication` — as dmc-mcp's `Build` without the key, the upload endpoint, `Who`/`DiskGate`/`SignInKeepAlive`; registers `IToolsApi`→`ToolsApi`, `TokenProvider`→`CallerTokens`, the five tool classes, `WithHttpTransport(o => o.Stateless = true)`, `ServerInstructions = Instructions`, the two metadata routes, `MapMcp("/mcp")`, and the startup line `Ways in: tokens from <issuer>, clients …; each call runs as the token's person; files as links for 1 hour`.
  - `ServeCommand.Instructions`: `"Hosted ENCY tools server (tools.encycam.com). Every call runs as the person whose access token it carries: their supplier catalogues, their libraries. Files come back as links that open for 1 hour without a token; a folder cannot be given. Libraries are the way into ENCY: the Tools catalogue extension shows them."`
  - `Door(UserTokens tokens, string metadataUrl, ILogger log)` with `Task Handle(HttpContext, Func<Task> next)`: the metadata paths and `/mcp/files/` pass without credentials; any other `/mcp…` path needs a bearer that passes `UserTokens.Check` (503 when the keys cannot be had, 401 with `invalid_token` and `resource_metadata` otherwise, the refusal reason logged, never the token); paths outside `/mcp` are 401.
  - `sealed class CallerTokens : TokenProvider` — `GetAccessToken()` returns `Caller.Current?.AccessToken`.
  - `Replies.SignInFix` (`internal static string`, default `"run tools-mcp login"`), used by `NotSignedIn` and `SignInAgain`; `ServeCommand.Build` sets it to `"the person must sign in to tools.encycam.com again in the agent"`.

- [ ] **Step 1: Write the failing tests** in `ServeTests` (the app on `127.0.0.1:0`, `FakeToolsApi` and `TestIssuer` swapped in through `configure`, as dmc-mcp's `ServeTokenTests` does):
  - `WithoutATokenItIs401WithTheMetadata` — no Authorization → 401; `WWW-Authenticate` contains `resource_metadata="https://tools.test/.well-known/oauth-protected-resource/mcp"`; `GET` that metadata → 200 with `authorization_servers` = the test issuer.
  - `APersonsTokenReachesTheTools` — initialize + `tools/list` with a test-realm token → 21 tools.
  - `ACallRunsAsTheTokensPerson` — `tools/call whoami` → the `FakeToolsApi` call carried exactly that token.
  - `ATokenFromAnotherClientIsRefused` — a token with `azp=dealer-space` → 401, `FakeToolsApi.Calls` empty.
  - `TwoPeopleAtOnceEachRunAsThemselves` — two parallel `whoami` calls with two people's tokens → each recorded call carries its own token.
  - `NoTokenReachesTheLog` — after a good call and a refused one, the captured logs contain neither token.
  - `ItDoesNotStartWithoutClientsOrAudience` — `Read(env without both)` → the error above.
  - `TheServerKeepsNoSessionToLose` — as dmc-mcp's.
- [ ] **Step 2:** Run → Expected: compile errors (`ServeCommand` missing).
- [ ] **Step 3:** Implement the files above.
- [ ] **Step 4:** Run the whole suite → Expected: all pass.
- [ ] **Step 5:** `git commit -m "serve: the tools over HTTP, each call as the person whose token it carries"`

### Task 3: Files as links on the hosted server

**Files:**
- Create: `src/FileSink.cs` (`IFileSink`, `LocalFiles`, `LinkFiles`), `src/DownloadStore.cs`, `src/DownloadJanitor.cs`.
- Modify: the four tool classes that save files (`CatalogueTools`, `ComponentTools`, `BuildTools`, `LibraryTools`) to take `IFileSink? files = null` (default `LocalFiles.Instance`) and reply with what it returns; `ServeCommand.Build` registers `DownloadStore(Path.Combine(DataDir, "files"))`, `IFileSink`→`LinkFiles`, the janitor, and `GET /mcp/files/{id}/{name}`.
- Create: `tests/DownloadStoreTests.cs`; add hosted cases to `tests/ServeTests.cs`.

**Interfaces:**
- `interface IFileSink { string Deliver(DownloadedFile file, string? folder); }` — returns the reply's line.
  - `LocalFiles.Instance.Deliver` → `"Saved: " + SavedFiles.Save(file, folder)` (today's behavior; the existing tests pin it).
  - `LinkFiles(DownloadStore store, string publicUrl).Deliver` → `ERROR: this server cannot save into a folder - leave folder out, and a link comes back` when `folder` is not blank; else `"Download (1 hour): " + publicUrl + "/mcp/files/" + id + "/" + Uri.EscapeDataString(name)`; a refusal of the store (`DownloadStore.FullException`) becomes `ERROR: <its message>`.
- `DownloadStore(string root, Func<DateTimeOffset>? clock = null)`: `const long MaxFile = 100 MB, MaxTotal = 1 GB, MaxPerPerson = 300 MB`; `TimeSpan Lifetime = 1 h`; `string Keep(DownloadedFile file, string owner)` → a 128-bit random id (32 hex), the file stored as `<root>/<id>/<safe name>` with `.owner` and `.expires`; `(string Path, string Name)? Find(string id)` → null for an unknown, malformed or expired id; `int Purge()`; `FullException(string message)` with messages naming the limit (`a file holds at most 100 MB`, `your links hold 300 MB - wait for older ones to expire (1 hour)`, `the server's file space is full - try again later`).
- The route: `Find(id)` null → 404 with an empty body; else the file, `Content-Disposition: attachment` (`filename*` UTF-8), `Cache-Control: no-store`. The owner is `Caller.Current.Owner`.

- [ ] **Step 1: Write the failing tests:**
  - `DownloadStoreTests.KeepsAFileUnderARandomId` (32 hex, `Find` gives the name and the bytes), `ALinkLivesAnHour` (clock + 61 min → `Find` null, `Purge` removes it), `AFileOverTheLimitIsRefusedAndNotKept` (100 MB + 1 → `FullException`, nothing left on disk), `APersonOverTheirShareIsRefused`.
  - `ServeTests.ABuiltKitComesBackAsALink` — `tools/call build_kit` → reply has `Download (1 hour): https://tools.test/mcp/files/`; `ALinkOpensWithoutATokenForAnHour` — GET that path with no Authorization → 200 and the bytes; `AnUnknownOrExpiredLinkIs404`; `AFolderIsRefusedOnTheHostedServer` — `build_kit` with `folder` → the ERROR line, nothing kept.
- [ ] **Step 2:** Run → Expected: compile errors.
- [ ] **Step 3:** Implement; the janitor sweeps every 10 minutes (`Purge`).
- [ ] **Step 4:** Run the whole suite → Expected: all pass, the 93 stdio tests unchanged.
- [ ] **Step 5:** `git commit -m "files: a link for an hour on the hosted server, the folder as before at home"`

### Task 4: One log line per call

**Files:**
- Modify: `src/ServeCommand.cs` — `.WithRequestFilters(f => f.AddCallToolFilter(next => async (context, ct) => { log…; return await next(context, ct); }))`.
- Test: `tests/ServeTests.cs`.

**Interfaces:**
- The line: `Call {Tool} by {Who} {Args}` at Information — `Who` = the first 12 hex after `user:` in `Caller.Current.Owner`; `Args` = `kit=…, classCode=…, page=…, pageSize=…` for those present in `context.Params.Arguments`, nothing else.

- [ ] **Step 1:** Tests `EveryCallIsLoggedWithWhoAndWhere` (a `find_components` call with `kit` and `page` → one line with the tool, a 12-hex who, `kit=` and `page=`) and `TheCallLineNamesNoToken` (the line holds neither the token nor the username).
- [ ] **Step 2:** Run → Expected: FAIL (no such line).
- [ ] **Step 3:** Implement the filter.
- [ ] **Step 4:** Run the whole suite → Expected: all pass.
- [ ] **Step 5:** `git commit -m "serve: a line per call - which tool, whose (hashed), which kit and page"`

### Task 5: Container, nginx and the README

**Files:**
- Create: `Dockerfile` (as dmc-mcp's: the package from nuget.org — `encysoftware.toolsmcp/${VERSION}` — on `mcr.microsoft.com/dotnet/aspnet:10.0`, user `app`, `/data` volume, `TOOLS_MCP_DATA=/data`, `ASPNETCORE_HTTP_PORTS=8080`, `CMD ["serve"]`).
- Create: `deploy/update.sh` (as dmc-mcp's, with the Global Constraints' port, limits and log options; env file `/opt/tools-mcp/tools-mcp.env`, data `/opt/tools-mcp/data`), `deploy/tools-mcp.env.example`, `deploy/nginx-tools-mcp.conf` (`location /mcp` → `http://127.0.0.1:8097`, `proxy_buffering off`, `proxy_read_timeout 900s`, `access_log off`; the two `/.well-known/oauth-protected-resource` locations).
- Modify: `README.md` — a "Hosted server (for Hermes)" section: what it is, the settings table, first-time setup, update, the nginx include, the backend's accepted client.
- Modify (in `C:\spryt\tools-encycam-com`, worktree branch `tools-mcp-api`): `deploy/README-go-live.md` — `TOOLSDB_ACCEPTED_CLIENTS` gains `dealerspace-mcp` for the hosted tools-mcp.

- [ ] **Step 1:** Write the files.
- [ ] **Step 2:** Verify locally: `TOOLS_MCP_CLIENTS=tools-encycam TOOLS_MCP_DATA=<temp> dotnet run --project src -- serve --urls http://127.0.0.1:18097`, then `curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:18097/.well-known/oauth-protected-resource/mcp` → `200`, and `curl … -X POST http://127.0.0.1:18097/mcp` → `401`. Run the whole suite → all pass.
- [ ] **Step 3:** `git commit -m "deploy: the hosted server in a container beside dmc-mcp, behind nginx at /mcp"` (and the backend doc line on its branch).

### Task 6: Release and connect (the user's steps; Claude checks after each)

- [ ] 0.1.0 is out first (the backend branch merged and deployed, the GitHub repository, NuGet publishing, tag `v0.1.0`).
- [ ] Tag `v0.2.0` → `publish-tool.yml` publishes.
- [ ] On the server: `/opt/tools-mcp/data` owned by UID 1654 (the image's `app`), `/opt/tools-mcp/tools-mcp.env` from the example with `TOOLS_MCP_CLIENTS=dealerspace-mcp,tools-encycam`, `sh update.sh 0.2.0`.
- [ ] nginx: include `deploy/nginx-tools-mcp.conf` in `/etc/nginx/sites-enabled/tools.encycam.com`, `nginx -t && systemctl reload nginx`.
- [ ] Backend: `TOOLSDB_ACCEPTED_CLIENTS=tools-encycam,authorizer-ency,dealerspace-mcp`, restart `tools-backend`.
- [ ] Check: `curl https://tools.encycam.com/.well-known/oauth-protected-resource/mcp` → 200; `initialize` + `tools/list` with a `tools-encycam` token (from `tools-mcp login`) → 21 tools; `docker logs tools-mcp` shows the Ways-in line and no token.
- [ ] Danil gets the URL `https://tools.encycam.com/mcp` and the rule: the person's own token through `dealerspace-mcp`.
