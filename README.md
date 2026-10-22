# tools-mcp

An MCP server for [tools.encycam.com](https://tools.encycam.com), the cutting-tool catalogue for ENCY CAM. It works
from Cursor, Claude Code or Codex.

A signed-in person tells their own agent what they need. The agent finds the tools on the site with that
person's account, checks what fits what, and puts the tools into a library in the person's account. ENCY opens
that library through the Tools catalogue extension. It is the tools.encycam.com counterpart of
[dmc-mcp](https://github.com/EncySoftware/dmc-mcp).

Status: 0.1.0, not released yet. 0.2.0 adds the hosted server for Hermes.

| Tool | What it does |
|---|---|
| `whoami` | Who the agent works as: the account whose supplier catalogues and libraries it sees. |
| `list_kits` | Our ready kits and the supplier catalogues, with items, how many can go into a kit, and downloads. A supplier catalogue is marked as one: pick from it with `find_components`. |
| `describe_kit` | One kit or catalogue: its description, its classes with codes and counts, and for our own kits every item with its parameters. |
| `download_kit` | Saves one of **our** kits' files (`.mKit` / `.db`). It refuses a supplier catalogue: its file is raw catalogue data, which ENCY cannot open. |
| `find_components` | Searches every kit and catalogue the account can see: words or a designation, kit, class code, family, machine type, maker, coating, coolant, shank, diameter range, what fits a given component, only what can go into a kit, and sorting. A row gives the id, class, size, whether it can go into a kit, the files, and "goes into N, takes M". At most 50 rows a page. |
| `search_options` | The values the search filters can take for the current search: classes with codes, families, makers, coatings, coolant, shanks and the diameter range. |
| `describe_component` | One component by id: every parameter its supplier publishes, its connection, and which files exist. |
| `what_fits` | What a component goes into and what it takes, with totals. |
| `assemble_to_spindle` | The chain up to the part that mounts in the machine, with the number of other choices at each step, or why it stopped. |
| `check_fit` | Which of 2 to 50 hand-picked components takes which, checked before they are built. |
| `save_component_file` | Saves the supplier's STEP model, drawing or photo, named after the component. |
| `build_kit` | Builds the chosen components plus custom drills and end mills into one `.mKit` and saves it. Up to 500. |
| `build_custom_tool` | A drill or end mill the catalogue lacks, built by its numbers as an `.mKit`. |
| `list_libraries` | The person's libraries, with how many items each holds and how many of them are gone from the catalogue. |
| `show_library` | What a library holds, marking items the supplier has since withdrawn. |
| `create_library` | Creates a library, empty or with items. A library of the same name stops it unless `force=true`. |
| `add_to_library` | Adds components and custom tools to a library and keeps what it holds. |
| `remove_from_library` | Takes the named items out and keeps every other item as it is. Only on the person's request. |
| `rename_library` | Renames a library. |
| `delete_library` | Deletes a library for good. Only on the person's explicit request. |
| `export_library` | Saves a library as one `.mKit`. |

Every reply that leaves something in a library ends with how to open it in ENCY: the Tools catalogue extension
lists the account's libraries.

## What it looks like

- *"Find a Capto C6 holder from Sandvik for a CNMG 120408 insert and put it in my 'Turning' library."*
  1. `find_components` finds the insert.
  2. `what_fits` finds the holders.
  3. `assemble_to_spindle` gives the chain to the machine.
  4. `list_libraries`, then `add_to_library` or `create_library`.
- *"What Iscar end mills Ø10–12 are there that can go into a kit?"* `search_options`, then `find_components`
  with `buildableOnly`.
- *"Make me a kit of these five and save it."* `build_kit`.
- *"A Ø7.3 drill, 90 mm long - there isn't one in the catalogue."* `build_custom_tool`, or `add_to_library`
  with a custom tool.
- *"Which of my libraries have items that are gone from the catalogue?"* `list_libraries` and `show_library`.

## Install (Cursor, Claude Code, Codex)

It needs the .NET 8 SDK or newer. The tool rolls forward to a newer runtime when 8 is not installed.

```bash
dotnet tool install -g EncySoftware.ToolsMcp
tools-mcp setup
```

Until the package is on nuget.org, install it from the `.nupkg` attached to the
[latest release](https://github.com/EncySoftware/tools-mcp/releases):
`dotnet tool install -g EncySoftware.ToolsMcp --add-source <folder with the .nupkg>`.

`setup` does four things, then you restart the editor:
- writes the `ency-tools` server into `~/.cursor/mcp.json`, merging it with your other servers;
- registers it in Claude Code for your user, when Claude Code's CLI is installed;
- adds it to Codex's `config.toml`, when Codex is on the machine;
- signs you in.

`--no-login` skips the sign-in. One sign-in serves every editor.

**Sign-in.** `tools-mcp login` opens the ENCY sign-in page in your browser.
- Use the same account as on tools.encycam.com.
- The tool keeps only a refresh token, in `%APPDATA%\tools-mcp\auth.json`, and never sees your password.
- Supplier catalogues are visible only to a signed-in account, so every tool needs the sign-in.

**Something not working?** Run `tools-mcp doctor`. It prints one line per check:
- the sign-in;
- whether tools.encycam.com accepts the token, and whose it is;
- whether the server is registered in Cursor, Claude Code and Codex.

**By hand instead of `setup`.** Run `tools-mcp login`, then put this in `~/.cursor/mcp.json`:

```json
{ "mcpServers": { "ency-tools": { "command": "tools-mcp" } } }
```

and for Codex `codex mcp add ency-tools -- tools-mcp`, or in `~/.codex/config.toml`:

```toml
[mcp_servers.ency-tools]
command = "tools-mcp"
```

## Files

Downloads and builds go to `Downloads\ENCY tools` unless a tool is given a `folder`. A file you already have is
never written over: a second `Kit.mKit` becomes `Kit (2).mKit`. The hosted server hands out links instead (below).

## What it will not do

- **Export a supplier catalogue wholesale.** The catalogues are closed to anonymous visitors.
  - A search page holds at most 50 rows.
  - `download_kit` refuses a supplier catalogue.
  - No tool returns a whole catalogue.
  - A kit or a library holds at most 500 items, as the server allows.
- **Change or delete a library you did not ask about.**
- **Overwrite a file.**
- **Work without a sign-in.**

## Settings

| Variable | What it does |
|---|---|
| `TOOLS_API` | another API address (a test server); the default is in `src/Brand.cs` |
| `TOOLS_TOKEN` | a ready access token instead of the stored sign-in, for debugging and CI. It lives minutes. |
| `TOOLS_CLIENT_ID`, `TOOLS_KEYCLOAK_TOKEN_ENDPOINT` | another Keycloak client or server |
| `TOOLS_AUTH_FILE` | where the refresh token is kept (tests) |

## Hosted server (for Hermes)

`tools-mcp serve` runs the same tools over MCP Streamable HTTP, for an agent that cannot start a local process. On the
tools server it answers at **`https://tools.encycam.com/mcp`**.

- **Every call is a person's.** It carries `Authorization: Bearer <access token>` of the person the agent works for: a
  `licsys` access token (not the ID or the refresh token) minted for a client named in `TOOLS_MCP_CLIENTS` (its `azp`)
  or for an audience named in `TOOLS_MCP_AUDIENCE`. The call runs with that token, so it sees that person's supplier
  catalogues and libraries. There is no key and no account of the server's own.
- A refused token gets `401` with `error="invalid_token"`: refresh it and repeat the request. `503` means Keycloak could
  not be reached to check it: try again in a minute. MCP clients that discover the sign-in themselves (RFC 9728) find
  the realm at `https://tools.encycam.com/.well-known/oauth-protected-resource/mcp`, which every 401 names.
- **Files come back as links.** `download_kit`, `build_kit`, `build_custom_tool`, `export_library` and
  `save_component_file` reply with `https://tools.encycam.com/mcp/files/<id>/<name>`. The link opens without a token for
  1 hour, so the person can open it in a browser or a messenger. A `folder` is refused. At most 100 MB a file, 300 MB a
  person, 1 GB in all.
- **The token goes on to the site.** tools.encycam.com takes a token only from the clients its backend lists in
  `TOOLSDB_ACCEPTED_CLIENTS`, so the agent's client goes there too (`deploy/README-go-live.md` in tools-encycam-com).
  The server never stores, logs or echoes a token.
- **A log line a call:** the tool, the first 12 hex of a hash of the person, and the `kit`, `classCode`, `page` and
  `pageSize` it was given. Not the name and not what was searched for.

| Variable | Default | Purpose |
|---|---|---|
| `TOOLS_MCP_CLIENTS` | none | comma-separated `azp` values a token must have; this or `TOOLS_MCP_AUDIENCE` is required |
| `TOOLS_MCP_AUDIENCE` | none | comma-separated `aud` values, one of which a token must carry |
| `TOOLS_MCP_ISSUER` | `https://webservices.encycam.com/keycloak/realms/licsys` | the realm whose access tokens are taken |
| `TOOLS_MCP_PUBLIC_URL` | `https://tools.encycam.com` | the site as clients see it: for the links, the 401 and the metadata |
| `TOOLS_MCP_DATA` | `/data` | where the links' files are kept |

**Running it (operators).** On the tools server, once, with this repository's `deploy/` folder at hand:

    mkdir -p /opt/tools-mcp/data && chown 1654:1654 /opt/tools-mcp/data && chmod 700 /opt/tools-mcp/data
    cp deploy/tools-mcp.env.example /opt/tools-mcp/tools-mcp.env && chmod 600 /opt/tools-mcp/tools-mcp.env
    sh deploy/update.sh 0.2.0

Then put `deploy/nginx-tools-mcp.conf` into the site's `server` block (`/etc/nginx/sites-enabled/tools.encycam.com`)
and run `nginx -t && systemctl reload nginx`. After that, `sh deploy/update.sh <version>` for every release. The
container listens on `127.0.0.1:8097`, with 512 MB of memory and its logs capped at 3 × 10 MB. At startup
`docker logs tools-mcp` says who may come in and whether the realm's keys could be fetched.

## Development

```bash
dotnet test tests/ToolsMcp.Tests.csproj
```

The design and the plan it was built from are in `docs/superpowers/`.

## Releases

Push a version tag (`v0.1.0`). `publish-tool.yml` then:
- tests;
- packs with that version;
- attaches the package to the GitHub release;
- pushes it to nuget.org through trusted publishing.

To set the publishing up once on nuget.org, add a trusted publishing policy for this repository and the workflow
file `publish-tool.yml`, as for `EncySoftware.DmcMcp`.
