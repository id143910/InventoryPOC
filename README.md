# Inventory Graph POC

A read-only view over inventory the syncs deliver, built around three facts a
plain node-and-edge model gets wrong:

> A certificate keeps one name across every renewal.
>
> The same certificate can be installed twice on one server — two stores, or an
> old thumbprint beside the one that replaced it.
>
> ServiceNow and AppViewX both describe it, and they do not always agree.

It answers questions that cross several relationships with a type constraint at
each hop — *the **packages** on this server*, *every **server** where a
**certificate** this team manages sits in a Java keystore*.

## Running it

| | | |
| --- | --- | --- |
| [api/](api/) | .NET 9 minimal API, Dapper | the graph, read and written |
| [web/](web/) | Angular 22, Material, Tailwind | the two surfaces |
| [schema.yaml](schema.yaml) | YAML | what the rows cannot say about themselves |
| `inventory.db` | SQLite | the data, committed with the repository |

```powershell
# the API on :5080
cd api\Inventory.Api; dotnet run --urls http://127.0.0.1:5080

# the app on :4200, proxying /api and /docs to it
cd web; npm install; npm start     # then open http://localhost:4200
```

> The Angular dev server binds IPv6 first, so use `http://localhost:4200`, not
> `127.0.0.1`.

Nothing else to set up: `inventory.db` is in the repository. That is deliberate — it
is both the data the application shows and the fixture the tests read, and there is no
generator here to rebuild it from, so it travels with the code. Its write-ahead log
does not: `.gitignore` leaves `inventory.db-wal` and `-shm` out, because a
checkpointed database holds everything they held.

## The config

Three questions the rows cannot answer for themselves, so they live in
[schema.yaml](schema.yaml) and nowhere else:

- which fields to merge when several systems describe one entity, and who wins
- which metadata keys identify an occurrence of a relation rather than merely
  describing it
- which fields are worth offering as filters
- which fields are a person's to set rather than a sync's
- what else can be done with an entity, and at which endpoint

```yaml
certificate:
  authority: [appviewx, snow, sccm]     # most trusted first
  unified:
    - { key: current_not_after, label: Latest expiry }
  filters: [issuer, key_algorithm, current_not_after]

- source_type: certificate
  relation_type: installed_on
  target_type: server
  identity: [thumbprint, location]      # what tells two installs apart
  attributes: [location_type, not_after, bound_to]
  filters: [location, location_type, thumbprint, not_after]
```

YAML on disk because people edit it and git reviews it — and it gets real comments,
which JSON could only fake with a `_comment` array. It reaches the application as
plain data and the API serves it as JSON.

It is read through a **source seam**, `ISchemaSource`, so "the file beside the
repository" can become "whatever the gitops revision currently says" without anything
downstream noticing.

Everything else — the shapes, the counts, the vocabulary, the values behind every
filter — is read back out of the data.

## What a person says

A sync is not the only thing with an opinion. Somebody looking at an entity can
correct a field, tag it, or leave a note on it — and none of that edits what ADF
owns:

```
PUT /api/entities/server/prod-server-0007/manual
{ "fields": { "environment": "laboratory", "comment": "out of prod for the audit" } }
```

That writes **one more row** of `inventory_entity`, from the source `schema.yaml`
calls manual:

```yaml
manual:
  source: Maestro
  source_type: manual
  fields:
    - { key: tags, label: Tags }
    - { key: comment, label: Comment }
```

`Config.Ranking` puts that source ahead of the declared authority, and the merge
that already existed does everything else. So the person's value is the one on the
header, every system's value is still beside it as a disagreement, and nothing was
overwritten — the graph simply has one more account of the thing. Clearing the last
field deletes the row, and the syncs are trusted again.

`fields` are keys only a person ever sets. They are not an annex to the header:
they merge, display, filter and get a column exactly like any other field, which is
why a comment is queryable (`comment contains audit`) without a line of code
written for it.

### Tags, and what a denial means

A tag is that same mechanism with a type. `tags: tags` in `field_types` makes the
key a list, so it is asked `includes` rather than `is` — one entity carries several
and any of them may be the one meant — and the condition compiles to a membership
test over `json_each`. The value picker lists the tags themselves rather than the
lists they came in, so nothing new was needed for it.

Tags did surface a bug older than they are. An entity is one row per source, and a
condition is asked of the group: it holds when **any** source makes the claim. Ask a
*denial* that way and one system's silence overturns another's assertion — every
server would be "not production", because SCCM never mentioned its environment, and
every entity would exclude every tag. So a denial now holds when **no** source makes
the claim, which is also what makes the two halves add up:

```
certificate tags includes audit      2
certificate tags excludes audit    598   (of 600)
```

Note that `before` and `after` do *not* add up like that, and should not: both are
claims, so two sources reporting different expiries can make both true of one
certificate.

## Plugins

An entity is worth more than reading if the things you would *do* with it are on its
page. A plugin is a REST endpoint and a name for it, declared per type:

```yaml
plugins:
  certificate:
    - name: renew
      label: Renew
      kind: action
      url: http://127.0.0.1:5080/mock/devops/pipelines/certificate-renewal/runs
      body:
        certificate: "{natural_key}"
        thumbprint: "{current_thumbprint}"
      message: state
```

Two kinds, because a page does only two things with one. A **box** is read when the
page opens and shown beside the neighbourhood — something another system knows that
this graph does not. An **action** is a button, and is called only when pressed. The
config decides which a plugin is, and therefore which method reaches it; the page
cannot press a button by reading.

`{...}` is filled from the entity — `natural_key`, `entity_type`, or any field on its
merged header — so the renewal sends the thumbprint it is replacing without a line of
code knowing that certificates have thumbprints. A url substitution is
percent-encoded and a body one is JSON-encoded, each for where it lands. A
placeholder nothing reports is **refused**: a pipeline given a blank thumbprint would
do the wrong thing quietly, and that is worth saying out loud.

`fields` are the paths worth reading out of the answer, dotted for nesting, because a
real endpoint does not answer flat:

```yaml
      fields:
        - { path: data.status, label: Status }
        - { path: data.throughput_mbps, label: Throughput }
```

The browser never calls these itself — the API does, so the endpoint needs no CORS,
can sit inside the network, and can hold a credential the browser must not see. Only
urls written in the config are ever called, and a plugin that is slow, unreachable or
answering 502 is a box that says so rather than a page that breaks.

The two declared plugins point at `/mock`, which is this API pretending to be Azure
DevOps and Dynatrace — shaped like them, nested answer and all, because a plugin that
only worked against a friendly answer would prove nothing. The runner cannot tell:
it makes a real request to a real url out of the config. Going real is changing two
urls and deleting [Plugins/Mock.cs](api/Inventory.Api/Plugins/Mock.cs).

The connectivity example is on `server` rather than `site`, because this graph has no
site type; pointing it at one is a one-line change.

## The two tables

Everything is a row a sync wrote — bar the manual source above — so both carry the
same provenance block, and both keep their payload in a JSON `metadata` column.

```
inventory_entity                          inventory_relation
  entity_id                                 relation_id
  natural_key      the identity             relation_type
  entity_type                               source_key / source_type
  external_id      its id over there        target_key / target_type
  source           snow, sccm, appviewx     relation_source
  source_type      automated | manual       relation_source_type
  discovered_at                             discovered_at
  last_seen_at                              last_seen_at
  synced_at                                 synced_at
  metadata                                  metadata
```

Writes happen in ADF, outside this application, so nothing here enforces
identity — no uniqueness, no foreign keys, only the indexes the reads need.

**One thing, several rows.** ServiceNow's and SCCM's account of
`prod-server-0007` are two rows. The logical entity is
`(entity_type, natural_key)`, and that pair is what relations point at, so the
graph does not care how many systems described a thing.

**Relations reference natural keys.** A relation can therefore exist before
either endpoint does. A certificate found on `edge-appliance-0060` that no
server inventory carries still has a page, marked *not inventoried — known only
from relations*.

**An occurrence is not a pair.** This is the shift the rest hangs on.
`cert x installed_on server y` is not a unique fact:

```
certificate service-0003.example.com  →  prod-server-0022
    { thumbprint: 0A:D4:4E:…, location: "My",                    location_type: winstore }
    { thumbprint: 0A:D4:4E:…, location: "C:/java/certtrust.jks", location_type: keystore }
    { thumbprint: 94:38:91:…, location: "My",                    location_type: winstore }
```

One certificate, one server, three installs: the current version in two stores,
and last year's thumbprint still in the Windows store. Modelling that as
entities would mean an entity per version per location. Instead there is one
certificate entity, and the version and the place live on the relation.

## Reading a relation

The full sentence is always `source_type relation_type target_type` — *server
managed by team*. Standing on one of its ends, that end is already the heading
of the page, so it drops out and what is left names the table:

```
on the server        managed by team            (listing teams)
on the team          server managed by          (listing servers)
on the server        package installed on       (listing packages)
on the package       installed on server        (listing servers)
```

The name plus the type you are standing on rebuilds the shape, which is what
keeps the two halves consistent. No phrasing table: a new relation label only
has to be named as a verb reading source to target, and both readings follow.

| Shape | Standing on the source | Standing on the target |
| --- | --- | --- |
| certificate `installed_on` server | installed on server | certificate installed on |
| package `installed_on` server | installed on server | package installed on |
| application `runs_on` server | runs on server | application runs on |
| pipeline `deploys` application | deploys application | pipeline deploys |
| server `managed_by` team | managed by team | server managed by |
| server `owned_by` stakeholder | owned by stakeholder | server owned by |

## Frozen, not deleted

`last_seen_at < synced_at` means the sync ran and the source did not return the
row. Nothing is deleted, because a system going quiet about a certificate is not
the same as the certificate being gone. Staleness is a row-level predicate
needing no join, and it is the default filter on every read, one link away.

The same holds one level up: SCCM can go quiet about a host the CMDB still
lists, and the entity shows that source as quiet while staying live itself.

## Three surfaces

Querying, reading and keeping are different jobs, so they are different pages, and
they meet only at links.

### `/` — the queries you kept

A query worth running twice is worth naming. Naming one puts it in this browser's
`localStorage` — it belongs to a person, not to the graph, whose rows are written
by the syncs — and this page runs each one for its count and its first few rows:

> **Certificates I manage expiring soon**
> 12 certificates
> service-0003.example.com · service-0041.example.com · and 9 others

That is the same box an entity's page shows its neighbours in, because it is the
same idea seen from the other side: a count you can read, and a way through to the
rows. The heading opens the query in the builder. With nothing saved there is
nothing to show, so this page steps aside and the builder is where you land.

A saved date is why relative dates exist. `now`, `now-7d`, `now+60d`, `now+3M` —
Grafana's shape, resolved when the query runs, so "expiring soon" still means that
next month. Anything not beginning with `now` is a date as typed.

### `/query` — the query builder

Listing, filtering and traversing are the same thing, so they are one table with
one builder above it.

A **query** is a start and a chain of **stages**. Stage 0 is the set you begin
with; every stage after it follows a hop. Each stage carries up to three
**conditions** and says whether **all** or **any** of them must hold. Stage 0 has
no hop — that is the only thing that distinguishes it, which is why one control
builds them all.

```
[ Server (2,000) ▾ ]              where all ▾  [ environment ▾ ] [ is ▾ ] [ production (1,200) ▾ ]
                                               [ region      ▾ ] [ is ▾ ] [ eu-west-1 (500)     ▾ ]
 →  [ certificate installed on ▾ ]  where all ▾  [ location ▾ ] [ is ▾ ] [ My (2) ▾ ]
 →  [ and then…                ▾ ]
```

Starting from one named entity pins stage 0 to it, as a chip that links to its
page and carries an **×** to let go of it and start from the whole type instead.

| | |
| --- | --- |
| **no hops** | a filtered list of a type — the rows are entities, one column per field the config merges onto them |
| **one hop** | what one entity reaches, one row per occurrence, one column per declared metadata key |
| **more hops** | a traversal, with a *Via* column saying where each row came from |

A condition is about the link a stage crosses (`link:location`) or the entity it
lands on (`entity:environment`, `entity:natural_key`, `entity:external_id`), with
`is`, `is not` or `contains`. Stage 0 has no link, so only the second kind applies
there — and `natural_key contains …` is the search box.

Every choice is a picker, and every picker is read out of the data. The first hop
offers what this entity actually has, counted; the rest offer what the type they
landed on permits. The field picker offers the link's keys and the landed
entity's, grouped by which is which. The value picker lists what that field
actually takes at that point in the chain, commonest first with a count each —
every earlier stage applied and this condition's siblings too, so a choice is
never a dead end, and its own value ignored, so changing your mind still offers
the full list. Past sixty values it says so rather than pretending to be
complete. `contains` gets a text box instead, since a fragment is not in any list.

Blank rows are how you add: the trailing hop and the trailing condition do
nothing until you fill them in, and setting a field back to `—` removes that
condition. There is no apply button: changing a control runs the query.

Every change goes through the router, so every state the interface can reach is a URL
that opens on its own — `?type=server&stages=[["","all",[["entity:environment","is","production"]]]]`
— which is also what makes a saved query nothing more than a name and a link.

### `/entity/{type}/{natural_key}` — the entity

A wiki page, to read rather than to query: no hop pickers, no conditions, no
`<select>` at all. Three parts.

**What the systems agree it is** — the merged header, with any disagreement
flagged.

**What each system says** — one block per source with its own metadata, its id
over there, and its discovered / last-seen / synced stamps, marked when that
source has gone quiet. This is the part a result table has no room for.

**The neighbourhood** — a box per relation, named by the convention and counted in
*things* rather than rows:

```
┌─ certificate installed on ─────────┐  ┌─ package installed on ─────────────┐
│ 1 certificate · 3 occurrences      │  │ 3 packages                         │
├────────────────────────────────────┤  ├────────────────────────────────────┤
│ service-0003.example.com ×3        │  │ dotnet-runtime  openssl  postgresql│
└────────────────────────────────────┘  └────────────────────────────────────┘

┌─ installed on server ──────────────┐  ┌─ managed by team ──────────────────┐
│ 600 servers · 629 occurrences      │  │ 1 team                             │
├────────────────────────────────────┤  ├────────────────────────────────────┤
│ prod-server-0001  prod-server-0005 │  │ Team 07                            │
│ prod-server-0008  and 597 others   │  │                                    │
└────────────────────────────────────┘  └────────────────────────────────────┘
```

`1 certificate · 3 occurrences` is the model's central fact at a glance: one
thing on the other end, three installs of it. A neighbour carrying more than one
gets a `×3` beside its name, so the awkward case is visible without a table.

Every name is a link to that entity's own page, which is what makes the graph
browsable by clicking. A box names its neighbours while there are few enough to
read (eight) and names three and counts the rest when there are not — so a
certificate on three hosts and a package on six hundred both render, and both
cost two queries.

The box heading is the way through to the rows: it opens the builder on that
relation, pinned to this entity. Nothing on the page filters, because the surface
that filters is one click away.

### Looking alike on purpose

Both front ends are styled with **Tailwind** and the same palette — slate under
everything, cyan for anything that does something — so a comparison is about
behaviour rather than about colour. The server-rendered version uses Tailwind
straight from the CDN; the Angular one compiles it, and has no `.scss` anywhere.

Angular Material needs its own theme, and the trick is that its prebuilt
`cyan-orange` theme is already dark with a cyan primary. Only the *hue* of that
dark is wrong — its surfaces are teal-black where this application reads
slate-blue — so [web/src/styles.css](web/src/styles.css) imports that theme and
reshades one family of `--mat-sys-*` variables to Tailwind's slate and cyan.
Roughly forty lines instead of the two hundred variables a theme from scratch
would need, and Material's components pick it up without knowing.

## The code

```
schema.yaml            the config: types, relations, field types, the manual
                       source, and the plugins — with a source seam behind it
inventory.db           the data, committed; written by the syncs and by one person

api/Inventory.Api/     .NET 9 minimal API + Dapper — JSON only
  Schema/Config.cs     the config, the seam, the naming convention, the vocabulary
  Db.cs                SQLite, its pragmas, and why it is open for writing
  Entities/EntityReader.cs   views, the merge, and the neighbourhood
  Entities/ManualWriter.cs   the one write: what a person says
  Queries/Model.cs     Query, Stage, Condition, Hop — validation
  Queries/Options.cs   shapes, hops, fields
  Queries/Runner.cs    the SQL, written out
  Queries/Moment.cs    dates written relative to today
  Plugins/PluginRunner.cs    calling the endpoints the config names
  Plugins/Mock.cs      two endpoints pretending to be somebody else's
  Program.cs           the endpoints

api/Inventory.Api.Tests/   102 xUnit against the committed database

web/src/
  styles.css           Tailwind, plus Material's dark cyan theme reshaded to slate
  app/api.ts           the wire contract, typed, and the one client
  app/saved.ts         the queries you kept, in this browser
  app/box.ts           a count, and the way through to the rows behind it
  app/dashboard/       the saved queries, answered — route /
  app/query/           the builder and the table — route /query
  app/entity/          the entity page — route /entity/:type/:key
```

### Two deliberate choices

**Minimal APIs, not controllers**, because there is no per-endpoint ceremony worth a
class.

**Dapper, not EF Core**: this application's value is its hand-built SQL — a CTE per
stage, a window function for a neighbourhood, `MAX(CASE …)` where a condition must
hold for an entity rather than for one of its source rows. EF would either fight that
or force raw SQL anyway, and [Runner.cs](api/Inventory.Api/Queries/Runner.cs) lets you
read exactly what the database will do.

## API

- `GET /api/entities/{type}/{natural_key}` — the merged view, each source's
  account, the hops it has, and the neighbourhood its page is made of
- `GET /api/stage?type=&key=&hop=` — what one stage can offer: the hops available
  from where it stands, and the fields a condition on it can be about
- `POST /api/values` — the values one condition's field takes
- `POST /api/query` — the table's query, whatever shape it is
- `PUT /api/entities/{type}/{natural_key}/manual` — what a person says: the one
  write, and the only endpoint that is not a read
- `GET|POST /api/entities/{type}/{natural_key}/plugins/{name}` — run one plugin:
  GET reads a box, POST does an action, and the config decides which is which
- `GET /api/schema` — the shapes, both readings, the identity keys, the
  operators, and every field a condition can be about

```json
{"type": "team", "key": "Team 03", "stages": [
  {},
  {"hop": "incoming:managed_by:certificate", "match": "any", "conditions": [
    {"field": "entity:issuer", "value": "DigiCert Global G2"},
    {"field": "entity:key_algorithm", "value": "ECDSA-P256"}
  ]},
  {"hop": "outgoing:installed_on:server", "conditions": [
    {"field": "link:location", "value": "My"},
    {"field": "entity:environment", "value": "production"}
  ]}
]}
```

```powershell
Invoke-RestMethod -Method Post http://127.0.0.1:8000/api/query -ContentType 'application/json' -Body '{"type":"server","stages":[{"match":"any","conditions":[{"field":"entity:environment","value":"staging"},{"field":"entity:environment","value":"development"}]}]}'
```

Omit `key` to start from the whole type; give no hops and the rows are entities.
The response carries `reads`, the query as one sentence.

## Performance

Against the seeded database: 2,830 entities across 5,430 source rows, 13,393
relations, 139 of them frozen.

| | latency |
| --- | --- |
| merged entity header (2 sources) | 0.2 ms |
| the hop picker — both ends, counted active and frozen | 0.4 ms |
| one hop from an entity | 0.7 ms |
| two hops, both stages filtered | 2.0 ms |
| four hops | 1.8 ms |
| a value picker on a hop | 0.9 ms |
| list a whole type, unfiltered | 2.4 ms |
| list a type, one condition on its metadata | 8.2 ms |
| list a type, two conditions (`all` / `any`) | 9.8 / 10.1 ms |
| list a type, `contains` on the key | 1.7 ms |
| a whole filtered type, then a hop | 10.5 ms |
| an entity's whole neighbourhood, any size | 2 queries |

- **Stage count does not change the number of queries** — two, whatever the
  length, and conditions add none. Every stage is a set operation inside one
  statement: it joins the relation table to the previous stage's keys.
- **Both directions resolve from an index.** `ix_relation_out` and
  `ix_relation_in` each cover the whole hop predicate, so `EXPLAIN QUERY PLAN`
  reports `SEARCH inventory_relation USING INDEX …`; a test asserts it.
- **Intermediate stages deduplicate**, which stops a fan-out from multiplying out
  and stops a certificate installed three times on one host from dragging that
  host through three times.
- **A condition on the landed entity is an `EXISTS`, not a join**, so a stage
  still resolves from the relation index and asks the entity table only about the
  rows that survived — and the rule becomes "any source says so", which is what
  you want when two systems describe the same thing. On `natural_key` it needs no
  lookup at all: the key is on the relation already.
- **Stage 0 groups instead of correlating.** It is already selecting from the
  entity table, so it groups by the key and tests each condition with
  `MAX(CASE …)` — the same "any source says so", in one pass rather than a
  correlated lookup per row scanned. That is the 8 ms above; the cost that
  remains is honest, since filtering on a JSON metadata field with no index over
  it means reading every row of the type. Every other shape of query stays under
  a couple of milliseconds.
- **The header is one query** however many systems describe the entity, and the
  hop picker is one query for both ends. Tests assert the counts.
- **An entity page is bounded.** Its neighbourhood is two queries whatever the
  entity: one counts the shapes, the other names the first handful of neighbours
  in each with a window function. A hub entity never drags six hundred
  neighbours into memory to throw most of them away.
- **Field names are validated, not quoted**, since they reach SQL as a JSON path.
- **SQLite is configured for reads**: WAL, a 64 MB page cache, memory-mapped
  reads, a warm pool. Shape aggregates are cached for 30 seconds.

## The seeded data

Shaped to contain the awkward cases rather than an average of them, and
`test_model.py` fails if any stops being produced:

- every server described by both ServiceNow and SCCM, disagreeing on OS version
  on every ninth host; every fifth certificate carrying a pre-renewal expiry in
  the CMDB
- certificates in two stores on one host, renewals beside the thumbprint they
  replaced, and hosts with both at once
- hosts carrying two versions of one package; applications with two instances on
  one server
- every fortieth host gone quiet in SCCM while the CMDB still lists it
- certificates on appliances no server inventory carries

## Tests

```powershell
cd api;     dotnet test                                                          # 102
```

**102 xUnit** over the real seeded database: the naming convention, `all` vs `any`,
conditions at every stage, values that narrow with their siblings under `all` and
not under `any`, boxes counted in things rather than rows, relative dates resolved
against a fixed today, and that an undeclared type or shape still works.

The writes are tested against that same database and clear up after themselves,
which is the honest way to test a write: the row has to really land, outrank the
syncs without silencing them, and really go away again. They each annotate their own
entity, because xUnit runs test classes in parallel and two of them annotating one
server would clear each other's row.

The plugins are tested against a stub endpoint rather than the mock in the
application, so what they assert is this code's own behaviour - what it sends, what it
reads back, and what it says when the far end is not there.

They read the committed `inventory.db` rather than building a fixture, which is why a
count in an assertion is a count you can go and look at.
