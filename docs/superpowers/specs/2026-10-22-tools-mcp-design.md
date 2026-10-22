# tools-mcp: an MCP server for tools.encycam.com — design

Date: 2026-10-22. Status: approved by the user on 2026-10-22. The plan is
`docs/superpowers/plans/2026-10-22-tools-mcp.md`.

## Why

tools.encycam.com is the cutting-tool catalogue for ENCY CAM. It holds our own kits and the
closed supplier catalogues (Sandvik, Kennametal, Walter, Iscar, Tungaloy, TaeguTec). On the site a person can:

- search components;
- see what fits what;
- assemble a chain up to the spindle;
- build a selection into an `.mKit`;
- keep libraries in their account, which ENCY opens through the EncyToolsCatalog extension.

The ask (the user, 22 October): an MCP server that can do **everything the site can do**. The
tasks come from **signed-in users**. A person tells their own agent (Claude Code, Cursor, Codex)
what they need, and the agent does it on the site with that person's account.

The user approved this shape: build it the way `dmc-mcp` (C:\spryt\dmc-mcp) is built. That
means a C# .NET tool on NuGet, installed into Claude Code / Cursor / Codex, signing in through the
browser with the same account as the site. **What the agent finds goes into a library in the
person's account, and the person opens that library in ENCY through the extension, as today.**

## What it looks like

- "Find a Capto C6 holder from Sandvik for a CNMG 120408 insert and put it in my 'Turning' library."
  1. `find_components` finds the insert.
  2. `what_fits` shows the holders it goes into.
  3. `assemble_to_spindle` gives the chain up to the machine.
  4. `list_libraries` shows whether 'Turning' exists, then `add_to_library` (or `create_library`)
     adds the holder.
  5. The reply names the library and says to open it in ENCY through the Tools catalogue extension.
- "What Iscar end mills Ø10–12 are there that can go into a kit?" `search_options` and
  `find_components` with `buildableOnly`.
- "Make me a kit of these five and save it." `build_kit` writes the `.mKit` and returns its path.
- "A Ø7.3 drill, 90 mm long — there isn't one in the catalogue." `build_custom_tool`, or
  `add_to_library` with a custom tool.
- "Which of my libraries have items that are gone from the catalogue?" `list_libraries` and
  `show_library` report the `present` flag.

## Goals

- Every capability of the public API (`/api/docs/openapi`) is reachable through a tool. The full
  map is in **Tools** below.
- The agent works as the signed-in person and sees what the person sees on the site. That
  includes the closed supplier catalogues and the person's own libraries.
- Installing and signing in takes one command, as with dmc-mcp: `dotnet tool install` plus
  `tools-mcp setup`.
- Replies give an agent what it needs to continue. They are prose plus ids, ending in the next
  step. Errors carry the fix.

## Non-goals (v1)

- **A hosted / remote mode** (dmc-mcp's `serve`, Streamable HTTP, keys, per-person Keycloak tokens
  for a remote agent). The user asked for signed-in users with their own agents. The hosted mode
  of dmc-mcp exists for one remote agent, Hermes, and can be lifted later if such an agent appears.
- **Writing into ENCY directly.** The path into ENCY is the account library and the extension,
  plus `.mKit` files for those who want a file.
- **Class icons as images.** `/api/classes/{code}/icon` serves the site's tiles and an agent gains
  nothing from it. Pictures of a component are saved through `save_component_file`.
- **Site statistics** (`/api/summary`, download counters) beyond what `list_kits` shows.

## Anti-goals: it fails even if it works when…

- **It becomes a way to export a supplier catalogue wholesale.** The catalogues are closed to
  anonymous visitors for legal reasons (Andrei, 22 September: "закроем tools под кейлок, то что
  мы делаем парсинг — не совсем юридически").
  - An agent paging through 68 thousand Sandvik rows and dumping them is the thing the closing
    was meant to stop.
  - So a page holds at most 50 rows and no tool returns a whole catalogue.
  - `download_kit` refuses supplier catalogues. Their file is the raw catalogue data, which ENCY
    cannot open anyway.
  - A kit or a library stays within the server's own caps (500).
  - These caps slow an agent down but do not stop one that keeps paging. A signed-in person can
    already page through the API (up to 200 rows a request), and the MCP is a public package.
    Only a per-account limit on the server would really stop it, and that would bind the site
    and the extension too.
  - **The user decided (22 October): no server-side limit in v1.** The MCP gives nothing beyond
    what a signed-in person can already do on the site. If wholesale downloads show up in the
    logs, a per-account limit comes next.
- **It changes or deletes a person's library they did not ask about.** `delete_library` and
  `remove_from_library` act only on an explicit request. Their descriptions say so, and a
  deletion cannot be undone.
- **It overwrites a file on the person's disk.** A name that is taken gets ` (2)`, ` (3)` and so on.
- **It runs anonymously without saying so.** Without a sign-in an agent would see only our
  kits and present that as the whole catalogue. Every tool needs a sign-in. Without one it
  answers `ERROR: not signed in - run tools-mcp login`.

## Shape

These are my calls unless the user says otherwise.

- **Package and naming.**
  - New repository `C:\spryt\tools-mcp`, GitHub `EncySoftware/tools-mcp`, created by the user or
    with their OK.
  - Package `EncySoftware.ToolsMcp`, command `tools-mcp`.
  - Registered in agents under the server name **`ency-tools`**, because "tools" alone is
    ambiguous in an agent's list.
- **Build and transport.** Same as dmc-mcp:
  - net8.0 with `RollForward=Major`, `PackAsTool`;
  - the `ModelContextProtocol` SDK (the version dmc-mcp uses, 1.4.1, or newer);
  - stdio transport, with logs on stderr only.
- **Subcommands.** Same as dmc-mcp:
  - no arguments: serve over stdio;
  - `login` (browser only; see Sign-in for why there is no `--password`);
  - `setup [--no-login]`: merges into `~/.cursor/mcp.json`, runs `claude mcp add --scope user`
    and adds a Codex `config.toml` entry, never overwriting;
  - `doctor`: version, signed in as whom, the API answers, the three agent registrations.
- **Settings.** Every value (site URL, API URL, Keycloak URL / realm / client, auth folder) lives
  in one `Brand.cs`, with environment overrides (`TOOLS_API`, `TOOLS_CLIENT_ID`, `TOOLS_TOKEN`).
- **Update hint.** A version check against nuget.org on start, silent on failure.

## Sign-in

- Keycloak `https://webservices.encycam.com/keycloak/`, realm `licsys`, the same realm as the site.
- **The client is the site's own: `tools-encycam`.** Nothing changes in Keycloak or on the server.
  - `deploy/README-go-live.md` records it as public, Standard Flow, PKCE S256, with valid
    redirect URIs `https://tools.encycam.com/*` and **`http://127.0.0.1:*/callback`**.
  - The EncyToolsCatalog extension already signs in through it this way: loopback, scope
    `openid offline_access` (`C:\spryt\EncyToolsCatalog\src\Account\BrowserLogin.cs`).
  - It is already in `TOOLSDB_ACCEPTED_CLIENTS` on prod (`tools-encycam,authorizer-ency`).
  - Borrowing `dealer-space`, as dmc-mcp does, would mean accepting every DealerSpace token for a
    tools account. Commit `02388d5` ("only our own client's tokens open an account") exists to
    stop exactly that.
- **The flow** is dmc-mcp's, with that client:
  - authorization code + PKCE with a loopback redirect. The code is the extension's
    `BrowserLogin`: `http://localhost:<free port>/callback` first, and
    `http://127.0.0.1:<port>/callback` when localhost will not bind. The extension signs in
    through `tools-encycam` exactly this way, so it is proven against this client.
  - scope `openid offline_access`;
  - the refresh token is stored in `%APPDATA%\tools-mcp\auth.json`, 0600 on Unix;
  - refresh happens under a semaphore, because Keycloak rotates refresh tokens.
  - **No `login --password`.** Direct Access Grants are off on `tools-encycam`, and browser
    sign-in is the only way.
- **Beside the extension.** The extension and the MCP sign in separately and hold separate
  sessions and refresh tokens. Rotating one should not touch the other. The smoke run checks
  this: both signed in on one machine, both still working the next day.
- **`TOOLS_TOKEN`** is a debug/CI override only, as `DMC_TOKEN` is in dmc-mcp. An access token
  lives minutes, so a token handed in at start would break partway through a session. No host
  hands in its token in v1 (my call). If ENCY's own agent ever runs this MCP, that needs a
  hand-off that refreshes.

## Tools

The names are snake_case verb_noun and the parameters camelCase, as in dmc-mcp. Each tool
returns `Task<string>` prose with the ids an agent needs next. Errors start with `ERROR:` and
carry the fix. Every tool calls the existing API with the person's Bearer token.

| Tool | API | What it does |
|---|---|---|
| `whoami` | `GET /api/me` | Who the agent works as (username, email). It is the first thing to call when in doubt. |
| `list_kits` | `GET /api/kits` (`q`, `discipline`, `ency`) + `GET /api/downloads` | Our kits and the supplier catalogues: slug, name, maker, format, ENCY version, items, how many can go into a kit, downloads. Catalogues are marked as such. |
| `describe_kit` | `GET /api/kits/{slug}` | The description and classes with counts and names. For our own kits it also gives the items with their parameters. |
| `download_kit` | `GET /api/kits/{slug}/download` | Saves our kit's file (`.mKit` / `.db`). **Refuses a supplier catalogue** and points to `find_components` + `build_kit` / `add_to_library`. |
| `find_components` | `GET /api/components` + `GET /api/components/compat-counts` | Search by `query`, `kit`, `classCode`, `family`, `machineType` (API `for`), `maker`, `coating`, `coolant`, `shank`, `minDiameterMm`/`maxDiameterMm`, `fitsComponentId` (API `fits`), `buildableOnly` (API `pullable`), `encyVersion`, sort, page. **pageSize ≤ 50.** A row gives id, name, maker, kit, class code and name, Ø, length, whether it can go into a kit, whether it has a model, drawing and picture, and "goes into N / takes M". |
| `search_options` | `GET /api/components/sections`, `/families`, `/choices`, `/size-range`, `/shanks` | The values the filters can take under the current filters: classes, families, makers, coatings, coolant, Ø range, shanks. It lets an agent ask the right question instead of guessing codes. |
| `describe_component` | **new** `GET /api/components/{id}` + `/compat` | One component: identity, all parameters, its connection, and what files exist. |
| `what_fits` | `GET /api/components/{id}/compat` | What it goes into and what it takes, with totals. The full list pages through `find_components(fitsComponentId=…)`. |
| `assemble_to_spindle` | `GET /api/components/{id}/assembly` | The chain up to the part that mounts in the machine, with the number of alternatives at each step, or why it stopped. |
| `check_fit` | `GET /api/components/accepts-among` | Which of the given components accept which, to check a hand-picked assembly before building it. |
| `save_component_file` | `GET /api/components/{id}/model` \| `/drawing` \| `/picture` | Saves the supplier's STEP model, drawing or photo. |
| `build_kit` | `POST /api/pull` | Builds the chosen components plus custom drills / end mills into one `.mKit` (≤ 500) and saves it. `socketShape`, API `sockets`: the default is what released ENCY reads, and `list` is for ENCY builds after 1 September 2026. |
| `build_custom_tool` | `POST /api/custom-tool` | A drill or end mill by its numbers (Ø 0.1–100, L 1–400, shank) as an `.mKit`. |
| `list_libraries` | `GET /api/me/libraries` | The person's libraries. |
| `show_library` | `GET /api/me/libraries/{id}` | Its items, with `present` = still in the catalogue. |
| `create_library` | `POST /api/me/libraries` | Looks for a library of the same name first and stops unless `force=true`. |
| `add_to_library` | `POST /api/me/libraries/{id}/items` | Component ids and/or custom tools. The server keeps one of each, takes only what can go into a kit (an ENCY 2 row cannot), and fills in the name and kit itself. |
| `remove_from_library` | **new** `POST /api/me/libraries/{id}/items/remove` | Removes the named items and keeps every other item exactly as stored, including items gone from the catalogue. Acts only on an explicit request. |
| `rename_library` | `PUT /api/me/libraries/{id}` with `name` only | Renames a library. With no `items` in the body the server leaves them untouched. |
| `delete_library` | `DELETE /api/me/libraries/{id}` | Deletes a library. Only on the person's explicit request, and it cannot be undone. |
| `export_library` | `POST /api/me/libraries/{id}/pull` | Saves the library as an `.mKit`, with `socketShape` as above. |

Every reply that ends in a library also says how to get it into ENCY: the Tools catalogue
extension, which shows the account's libraries.

## Files

- Files are written only for downloads and builds. The default folder is
  `~/Downloads/ENCY tools` (created on first use), and a `folder` parameter overrides it. The
  reply gives the full path.
- The name comes from the server's `Content-Disposition`, sanitized as dmc-mcp's `FileNames.Safe`
  does. A taken name gets ` (2)`. A partial file is deleted.
- Nothing is uploaded from disk: the API takes no files.

## Backend changes (tools-encycam-com)

1. **`GET /api/components/{id}`**: the component's `ComponentDto` and its parameters. Today a
   component is described only by the row a search returned. `/compat` gives the parameters but
   not the component's own name, kit or class. The endpoint follows the same visibility rule as
   the rest: a closed catalogue's row exists only for a signed-in caller. It goes in the OpenAPI
   description with a test.
2. **`POST /api/me/libraries/{id}/items/remove`** (body: the component ids and/or custom tools to
   take out). It removes them and keeps every other stored item as it is, unchecked. It is the
   mirror of `POST …/items`.
   - Today the only way to remove is `PUT` with the whole list, and `PUT` re-checks every row
     (`MeController.checkedItems`). It answers 400 when any row is gone from the catalogue or can
     no longer go into a kit.
   - So "remove one, keep the rest" fails in exactly the libraries that hold such rows. The site
     works around it by dropping gone rows on every save, which the MCP must not do behind the
     person's back.
   - A server-side remove also cannot overwrite an edit made on the site or in the extension in
     the meantime, as a read-modify-write `PUT` can.

No server setting changes: the MCP signs in with the site's own client (see Sign-in).

## Limits it lives within

- The server's limits are 50 libraries per account, 500 items per library and 500 components per
  build. The MCP passes the server's own message through.
- Only buildable components (`pullable`) can go into a kit or a library. `find_components` says
  which rows are buildable, and `buildableOnly` keeps to them.
- Component ids are stable between releases, so a library and an agent's remembered ids keep
  working.

## Testing

As in dmc-mcp:

- xUnit with no network. Tool methods are called directly against a fake API client that
  records calls and the token each carried.
- An in-memory Keycloak issuer for the sign-in and refresh paths, and a fake process runner for
  `setup`.
- One test per tool for the happy path and for the error that carries a fix (401 → `login`, 404,
  the server's 400 messages).
- Tests for the anti-goals: page cap, refusal of a catalogue download, `create_library` duplicate
  stop, no overwrite of files.
- Before release, a manual smoke run on prod with a real account, in Claude Code.

## Release

As in dmc-mcp:

- `ci.yml` runs the tests on every push.
- `publish-tool.yml` runs on a `v*` tag. It packs with the tag's version, attaches the package to
  the GitHub release and pushes to nuget.org through trusted publishing.
- The README has the tool table, a "What it looks like" section and the environment variables.
- The first version is 0.1.0.

## Who decided what

- **The user:**
  - everything the site can do;
  - tasks come from signed-in users, each through their own agent;
  - built like dmc-mcp: C#, NuGet, Claude Code / Cursor / Codex, browser sign-in;
  - found tools go into the account library, and ENCY opens it through the extension;
  - no server-side limit against wholesale export in v1; the MCP's own caps are the guard.
- **Settled from the project:** sign-in through the site's own client `tools-encycam` (loopback
  allowed, already accepted), with no password fallback.
- **My calls (open to change):**
  - the names `tools-mcp` / `EncySoftware.ToolsMcp` / `ency-tools`;
  - stdio only in v1;
  - `TOOLS_TOKEN` as a debug override only;
  - the anti-goals about wholesale export, and the 50-row page;
  - the file folder;
  - the two new endpoints, `GET /api/components/{id}` and `POST …/items/remove`;
  - the tool list and its names.
- **Left to the builder:** code structure beyond what dmc-mcp already shows, description wording,
  `sort` values (whatever the API accepts), caching, and the exact text of replies.
