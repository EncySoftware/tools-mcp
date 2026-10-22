# tools-mcp as a hosted server — design

Date: 2026-10-22. Status: builder-checked (three questions settled as my calls, marked); waiting for the user's approval.

## Why

Hermes is an agent that cannot start a local process. It needs tools.encycam.com through MCP, as it already reaches
Digital Machine Center through the hosted dmc-mcp. The user (22 October): "we also made a server MCP in DMC, can the same
be done here?", then "yes", for Hermes.

dmc-mcp's hosted mode is the pattern: `C:\spryt\dmc-mcp\docs\superpowers\specs\2026-10-06-remote-server-design.md` and
`2026-10-08-per-user-sign-in-design.md`. What follows is only what is different here.

## What it is

`tools-mcp serve` runs the same 21 tools over MCP Streamable HTTP, stateless, at **`https://tools.encycam.com/mcp`**. Its
code is ported from dmc-mcp: `ServeCommand`, `Door`, `UserTokens`, `IssuerKeys`, `Caller`, with their tests and
`TestIssuer`. The stdio mode is unchanged.

## Who a call runs as: the person, never a shared account

- A call must carry `Authorization: Bearer <access token>` of the person Hermes acts for. The token is checked as
  dmc-mcp checks it:
  - issuer `https://webservices.encycam.com/keycloak/realms/licsys`;
  - the signature against the realm's keys;
  - `typ=Bearer` and a `sub`;
  - the lifetime;
  - `azp` in `TOOLS_MCP_CLIENTS`, or `aud` in `TOOLS_MCP_AUDIENCE`.
- `TOOLS_MCP_CLIENTS=dealerspace-mcp,tools-encycam` (my call). The first is Hermes's client. The second is the site's
  own: its tokens are what the tools API takes anyway, so the hosted server gives them nothing new. It also lets the
  deployed server be checked with a `tools-mcp login` token before Hermes connects.
- A refused token gets a 401 with RFC 9728 `resource_metadata`, and the metadata is served at
  `/.well-known/oauth-protected-resource[/mcp]`.
- **No shared key**, unlike dmc-mcp. Behind a key there is one account: everyone's tools would land in that account's
  libraries, and the closed supplier catalogues would open to whoever holds the key. The server refuses to start
  unless `TOOLS_MCP_CLIENTS` or `TOOLS_MCP_AUDIENCE` is set.
- The person's token goes on to the tools API, the same realm the site signs in with. The API takes a token only when
  its `azp` is in `TOOLSDB_ACCEPTED_CLIENTS`, so **Hermes's client is added there too**.
- Inside, the tool classes do not change. In serve mode, `TokenProvider` is a per-request one that hands out the
  caller's token (`Caller`, as in dmc-mcp).
- Tokens are never stored, logged or echoed.
- **One log line per tool call** (my call): the tool, a short hash of the person's `sub` (no name or email), and for a
  search the kit, the class and the page.
  - Through the hosted server every API call comes from one address. Without this line, wholesale paging could not
    be told from ordinary use - which is what the user's "a limit comes if the logs show it" (22 October) relies on.
  - The container's logs are capped: json-file, 10 MB × 3.

## Files: a link instead of a folder

- On the hosted server, the five tools that save a file (`download_kit`, `build_kit`, `build_custom_tool`,
  `export_library`, `save_component_file`) keep the file in a download store instead. They reply with a link,
  `https://tools.encycam.com/mcp/files/<128-bit id>/<name>`.
- **The link is the credential** (my call). It carries a 128-bit random id, lives 1 hour and opens without a token.
  - A person's browser or messenger sends no token, so a token-only link would fail in exactly the place Hermes shows it.
    Fetching it with the token inside Hermes's model would put the token in the model's context.
  - The id is kept out of logs: nginx's access log is off for `/mcp`, and the server never logs the path.
  - A leaked link gives one person's kit file for at most an hour.
- Limits: 100 MB a file, 1 GB in all, 300 MB a person. A janitor sweeps what is left, as dmc-mcp's does for uploads.
- A `folder` argument is refused: the server never writes where an agent points.
- Libraries need no file and work as they do locally. They remain the main way into ENCY.

## Where it runs

- On the tools server, beside the backend: a container built from the published NuGet package, as dmc-mcp's
  `Dockerfile`, bound to **`127.0.0.1:8097`**.
- Checked on 2026-10-22: it is the same server that runs the hosted dmc-mcp, and it has Docker 27.5.1.
  - Ports 8095 (dmc-mcp), 8096 (dmc-model3d), 8090 and 8091 are taken.
  - About 1.3 GB of memory is free out of 3.8, so the container is capped at 512 MB.
  - The tools site's nginx (`/etc/nginx/sites-enabled/tools.encycam.com`) has `/assets/`, `/api/`, `/kc-api/` and `/`;
    the routes for `/mcp` are added there.
- nginx proxies `location /mcp` (and `/.well-known/oauth-protected-resource`) with no buffering, 15-minute timeouts and
  access logs off.
- `deploy/update.sh <version>` is ported from dmc-mcp, and the user runs it.

## Not in this

- A shared key.
- Uploads: the API takes no files.
- Writing to any disk an agent names.

## Needed from people

- Hermes's Keycloak client. On 2026-10-22 dmc-mcp on that server had `DMC_MCP_CLIENTS=dealer-space,dealerspace-mcp`.
  - Hermes's is, by its name, **`dealerspace-mcp`**. Confirm with Danil that it is Hermes's, and that Hermes sends each
    person's own token: if it uses only dmc-mcp's key, it cannot use this server until it does.
  - It goes into `TOOLS_MCP_CLIENTS` and `TOOLSDB_ACCEPTED_CLIENTS` (today `tools-encycam,authorizer-ency`).
  - `dealer-space` is not added: it is the DealerSpace site's client, and the API was closed to it on purpose (commit
    02388d5).
- **Order:**
  1. 0.1.0 is released first: the backend branch merged and deployed, the package published.
  2. Then 0.2.0 with `serve`.
  3. Then the container, the nginx route and the two settings.
  4. Then Hermes connects to `https://tools.encycam.com/mcp`.
