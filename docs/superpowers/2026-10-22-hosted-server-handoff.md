# The hosted server: hand-off (2026-10-22)

The plan `plans/2026-10-22-hosted-server.md` is done through Task 5 on `main` (cdc29f0..9fd2c51, 189 tests). A fresh
review of the whole branch found nothing critical. It found one important defect, fixed in 9fd2c51: a file was held
in memory twice before the 100 MB check. Two minors were fixed with it: an agent's tool name could forge a log line,
and only one of the five file tools was tested on the hosted server. The backend's go-live doc has two commits on
`tools-mcp-api` (43a202c, 8fd0429).

## Release (Task 6, the user's steps)

1. **0.1.0 first.**
   - Merge `tools-mcp-api` into the backend's main and deploy. Then check `GET /api/components/{id}` and
     `POST /api/me/libraries/{id}/items/remove`.
   - Create `github.com/EncySoftware/tools-mcp` and set up NuGet trusted publishing.
   - Tag **`v0.1.0` on `e6e30e9`**, not on main's tip. Main already holds `serve` (0.2.0), and `publish-tool.yml`
     packs whatever the tag points at.
2. **Hermes's client.** Danil confirms that `dealerspace-mcp` is Hermes's Keycloak client, and that Hermes sends each
   person's own access token. Until then it goes neither into `TOOLS_MCP_CLIENTS` nor into the backend's
   `TOOLSDB_ACCEPTED_CLIENTS`. The list opens the API to whatever application owns the client.
3. **0.2.0.** Tag `v0.2.0` on main.
4. **On the server**, as the README's "Hosted server" section says:
   - set up `/opt/tools-mcp` (data owned by 1654, the env file from the example);
   - run `sh update.sh 0.2.0`;
   - put the nginx snippet into the site's server block, then run `nginx -t && systemctl reload nginx`;
   - add `dealerspace-mcp` to `TOOLSDB_ACCEPTED_CLIENTS` and restart `tools-backend`.
5. **Check:**
   - `curl https://tools.encycam.com/.well-known/oauth-protected-resource/mcp` returns JSON with `"resource"`. Without
     the nginx part the site answers 200 with its own index.html.
   - With a `tools-mcp login` token: `initialize`, then `tools/list` (21 tools), then `tools/call whoami`. Only this
     last step reaches the backend through the container.
   - One `build_kit` link opened through nginx.
   - `docker logs tools-mcp` shows the "Ways in" line and "Keys of … loaded", and no token.
   - If every call says "sign in again", the backend does not accept the client yet: check `TOOLSDB_ACCEPTED_CLIENTS`
     first.
6. Danil gets `https://tools.encycam.com/mcp`.

## Decisions made on the way

- **Setup:** the work went on tools-mcp's own main, which is unpushed. The cost was underrated: the 0.1.0 tag has to
  go on e6e30e9 (step 1).
- **Task 1:** the `/me` record was already called `Caller`. It is `Account` now, so the ported per-request `Caller`
  keeps its name.
- **Task 2:**
  - The hosted sign-in wording is `TokenProvider.SignInFix`, overridden by `CallerTokens`. A process-wide static set by
    `Build` would have changed the text under the stdio tests running beside it.
  - The links' door rule and the "files as links" startup words came with Task 3's tests.
- **Task 3:**
  - `IFileSink.Check(folder)` runs first in each file tool, and a refusal throws into the reply. The plan's
    error-line-from-`Deliver` would refuse a folder only after the site had built the file or counted the download.
  - `DownloadStore` takes smaller limits in tests, so the share tests do not write hundreds of megabytes.
- **Task 4:** argument values in the call line are cleaned: no control characters, 60 characters at most.
- **Task 5:** `.gitattributes` keeps `Dockerfile` and `deploy/` LF in a Windows checkout.
- **After the review:**
  - The SDK's own lines below Warning are off on the hosted server. They quote the agent's tool name on every call;
    the call line is the record.
  - These stand as the spec has them:
    - the token goes on to the API;
    - the link is the credential;
    - `tools-encycam` is among the clients;
    - with both allow-lists set, a token must match both (the README now says so);
    - the share counts bytes;
    - stdio users need the ASP.NET Core runtime, which the SDK ships;
    - dmc-mcp's `RunUntilStopped` is not ported (the janitor cannot fault, and the container restarts);
    - no nupkg hash, no `nosniff`.

## Deferred minors

- On the hosted server, the five file tools' descriptions still speak of the person's disk and offer `folder`. The
  server instructions correct this, and a call with a folder gets a clear error.
- Every 401 from the backend says "sign in again", including when the backend does not accept the agent's client
  (see step 5).
- A relative `TOOLS_MCP_DATA` makes links answer 500. The image sets `/data`.
- Refused tokens are logged at Information with no rate limit, so garbage requests can push call lines out of the
  capped logs. `limit_req` in nginx would stop that.
