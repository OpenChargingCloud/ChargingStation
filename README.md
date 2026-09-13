# ChargingStation

One EV charging station, with a web interface in front of it: a C# HTTP
backend built on [Hermod](https://github.com/Vanaheimr/Hermod), and a frontend
of HTML, SCSS and TypeScript bundled by webpack and embedded into the
assembly - so the station is one binary to deploy and needs nothing installed
beside it.

Nothing is rendered on the server. The browser loads one bundle and talks to
the station over a JSON API and one Server-Sent Events stream.

```
  browser  ──  GET /                       the SPA stub and the bundle
           ──  POST /api/v1/auth/login     the session cookie
           ──  GET  /api/v1/configuration  what the station is made of
           ──  GET  /api/v1/logs           what happened up to now
           ──  GET  /api/v1/events         and everything from now on (SSE)
```


## Running it

From the repository that has this one as a submodule
([ChargingStationCLI](https://github.com/OpenChargingCloud/ChargingStationCLI)):

```
dotnet run --project ChargingStationCLI
```

At the first start there is no web login, so the station makes one up for the
user `root`, writes its hash to `web-login.json` and prints the password once:

```
  ┌─ First start: there was no web login, so one was made up for you ─────────
  │  user      root
  │  password  QBDD77Lc7HseB-xORuuw8RpX
  │  It is shown here once and kept only as a hash. Write it down.
  └───────────────────────────────────────────────────────────────────────────
```

Then open http://127.0.0.1:2348/ and sign in.

`--help` lists the rest: `--port`, `--any`, `--web-login <file>`,
`--frontend <dir>`, `--config <file>`, `--verbose`, `--quiet`, `--no-trace`,
and the `--v2g` family below.


## Building

`dotnet build` builds the frontend too: the `ChargingStation.csproj` runs
`npm ci` (only when `Frontend/node_modules` is missing) and `npm run build`
(only when something under `Frontend/src` changed), then embeds every file of
`Frontend/dist` as a manifest resource named
`cloud.charging.open.ChargingStation.HTTPRoot.<dir>.<file>`.

* `dotnet build -p:SkipFrontendBuild=true` - backend only, reusing the
  existing `dist/`.
* Directory names below `dist/` must not contain a dot: the server maps the
  URL path `assets/main.1234.js` onto the resource name
  `<prefix>assets.main.1234.js`, and a dot in a directory name would make that
  ambiguous.

While working on the frontend, skip the round trip through MSBuild:

```
npm --prefix libs/ChargingStation/ChargingStation/Frontend run watch
dotnet run --project ChargingStationCLI -- --frontend libs/ChargingStation/ChargingStation/Frontend/dist
```

`--frontend` serves the files from disk, so a reload in the browser shows what
webpack has just written.


## What is where

| | |
|---|---|
| `ChargingStation.cs`      | the station: the OCPP nodes, the HTTP server, and everything below wired together |
| `HTTPAPI/CSHTTPAPI.cs`    | the JSON API at `/api`: sign-in, status, configuration, log, event stream |
| `Web/WebSessions.cs`      | who is signed in: one login in front of Hermod's `SessionStore`, and the cookie its token travels in |
| `Web/WebLogin*.cs`        | that one login and the file it lives in, its password a `SecurePassword` and never in the clear |
| `ChargingStation.Configuration.cs` | what the Configuration pages read and write: one resource per thing, each saying which fields may be changed |
| `EVSEs/`                  | the EVSEs this station has, and the file they live in |
| `ISO15118/V2GLink.cs`     | the wire below the charging cable: SLAC, SDP and the V2G endpoint, and every event of theirs in the log |
| `Logging/EventLog.cs`     | everything that happens, with timestamps and tags, kept in a ring buffer and handed on at once |
| `Logging/TraceBridge.cs`  | what the libraries below write with `DebugX`, into the same log |
| `Frontend/`               | the npm project: `src/pages/` are the pages, `src/shell.ts` the menu around them |

Serving the bundle is Hermod's: `MapSinglePageApplication` with an
`EmbeddedContentSource` or a `FileSystemContentSource` does the entity tags,
the conditional requests, the Brotli and gzip negotiation, the caching policy
and the security headers, and answers a URL that names no file with the stub.
This project brings the bundle and one literal route for `/favicon.ico`, which
browsers ask for whatever the page says and which the bundle carries as an SVG.


## The wire below the charging cable

Off unless `--v2g` asks for it: binding UDP 15118, joining an IPv6 multicast
group and putting a listener on a link-local address is not something to do on
a developer's laptop because the binary happened to start.

Three things, in the order a vehicle meets them:

1. **SLAC** matches the powerline modem in the car to the one in this station,
   so that the two share a network and nobody talks to the car parked next to
   it (ISO 15118-3, HomePlug Green PHY). The real medium is AF_PACKET and
   therefore Linux; `--slac-udp 127.0.0.1:0` runs a simulated one on a bench.
   The simulated medium is never chosen by itself - a station that matched
   vehicles over UDP without being told to would look like it works and be
   talking to nothing.
2. **SDP** answers the car asking, over IPv6 multicast, where the V2G endpoint
   is.
3. The **V2G endpoint** is what that answer points at: a TCP listener, with
   TLS 1.3 where `--v2g-cert` gave it a certificate.

They are wired to each other and not merely started next to each other: the
listener is bound first, and the port the operating system gave it is what SDP
advertises. Likewise, a station without a certificate advertises `NoTLS`
rather than sending every vehicle into a handshake that cannot finish - and
says so, loudly, at startup.

```
18:14:58,892 NOTICE  15118 v2g tls  The V2G endpoint is listening on [::]:53256 without TLS.
18:14:58,901 NOTICE  15118 sdp      SDP is answering on 'eth1', pointing vehicles at port 53256.
18:14:58,907 NOTICE  15118 slac     SLAC is listening on the powerline interface.
18:15:17,596 NOTICE  15118 v2g      A vehicle connected to the V2G endpoint.
18:15:17,617 INFO    15118 v2g      Its first V2GTP frame is ExiMainstream, 12 bytes.
```

What is **not** there yet is the session above the listener - SupportedAppProtocol,
the EXI messages of -2 or -20, the charging loop. A connection is accepted, its
first V2GTP frame is read and named, and then it is closed again, which is the
difference between "the listener is bound" and "a vehicle came all the way
through SLAC and SDP and got here".


## Configuration

Everything this station can be told in writing lives in one file,
`configuration.json` (`--config <file>`), with one section per subject:

```json
{
  "dns":   { "enabled": true, "servers": [ { "address": "9.9.9.9" } ], "useCache": true },
  "nts":   { "enabled": true, "hostname": "ptbtime1.ptb.de" },
  "evses": [ { "id": 1, "connectorTypes": [ "sType2" ], "maxPower_kW": 22 } ]
}
```

One file rather than one per subject, because these are read together, changed
together and backed up together - and because "what is this station configured
as" should have one answer that fits on a screen instead of a directory to go
through.

**What is missing is not what is empty.** A section the file does not mention
leaves whatever the station was handed at construction; a station handed
nothing falls back to the system default. So the order is: system default, then
what the constructor was given, then what the file says - each only where it
actually speaks. That is what makes a three-line file a legitimate thing to
have: somebody who only cares about the name servers should not have to write
down the retry count to say so.

A section this station does not know is passed over and **kept**: a file
written by a newer station starts an older one, and saving from the web
interface will not delete the part it did not understand.

### The pages

| | |
|---|---|
| `/configuration`        | what the station is made of, read-only |
| `/configuration/dns`    | name resolution: on/off, the servers, the settings, and a test |
| `/configuration/nts`    | the time source: on/off, the server, the cookie pool, and "Sync now" |
| `/configuration/evses`  | the EVSEs: add, remove, renumber, pick connector types |

    GET  /api/v1/configuration/dns          PUT with the fields to change
    POST /api/v1/configuration/dns/query    {"name": "...", "recordTypes": ["A"]}
    GET  /api/v1/configuration/nts          PUT with the fields to change
    POST /api/v1/configuration/nts/sync     NTS-KE + one authenticated NTP request
    GET  /api/v1/configuration/evses        PUT with {"evses": [...]}, all of them at once

Every change takes effect at once - no restart, and no page that says a restart
is owed. The file is written first and the change applied second, because a
change that was applied but not written down disappears at the next start
without anybody noticing, and that is the worse of the two failures.

A PUT changes only the fields it names. A form with six checkboxes on it sends
six checkboxes, and a save that replaced the whole section would take the name
servers with it because the form had nothing to say about them.

### Who may change what

Every login carries roles, in `web-login.json`:

```json
{ "username": "root", "roles": ["cpo"], "password": "$pbkdf2-sha256$..." }
```

| role | may |
|---|---|
| `viewer`      | read the configuration |
| `cpo`         | that, plus change DNS and NTS and run their tests |
| `systemadmin` | that, plus change the EVSEs |

The hardware is its own permission because it describes something somebody
installed: saying there is a CCS socket where a type 2 socket is bolted to the
wall does not change the wall - it changes what every vehicle and every back
end is told about it, and nothing further down is in a position to notice that
it is wrong. The network settings are reversible and they complain; a wrong
connector does neither.

A login file without `"roles"` describes a system administrator, which is what
the one login of a station used to be. An **unknown** role is refused at
startup rather than granting nothing: a connector type this station has never
heard of is still a socket somebody can plug a car into, but a role it has
never heard of is a role it cannot enforce.

The browser is told its own permissions so a page can grey out what it may not
do. That is a copy of what the station enforces, not the enforcement: every
request is checked again on arrival, so editing the list in a browser buys a
button that answers 403.

### DNS

Switching name resolution off takes the servers away from the DNS client,
which is what off means - for everything that was handed that client, not only
for the parts of this station that remember to check a flag first. A query then
fails at once and says why, instead of quietly going to whatever this machine
happens to have configured.

Changing the servers exchanges them inside the client rather than building
another one, so the HTTP server, the OCPP nodes and the time client all resolve
through the new ones without a restart. The connections pooled to servers that
are gone are closed, and the cache is emptied: the point of naming other
servers is usually that the old ones were answering wrongly, and keeping their
answers would hide exactly the change that was wanted.

The test asks this station to look a name up and shows what came back, record
by record. Every step goes into the event log, so the Logs page of anybody
watching shows it too.

### NTS

"Sync now" does the whole exchange - the key exchange over TLS, then one
authenticated NTP request - and logs each step rather than only the outcome,
because the useful answer to "why can I not reach my time server" is which step
it got to.

It does **not** step the clock of this station. That is a different thing, with
meter readings and certificates hanging off it, and not something a button does
by surprise.

Pointing the station at another server replaces the client rather than
reconfiguring it: the cookies and keys an NTS client holds were issued by the
host it was made for, and carrying them to a different one would at best fail.

### EVSEs

They are edited as a whole rather than one at a time, because they are only
valid together: OCPP numbers them from 1 upwards without gaps, so removing the
third of four is a change to two of them.

Saving rebuilds both OCPP nodes from the new list - 1.6 as connectors, 2.1 as
EVSEs - so what the page shows and what a back end would be told are never two
different things.

Connector types are **not** a closed list. `OCPPv2_1.ConnectorType` is a set of
predefined strings rather than an enumeration, and that is the point of it: a
plug this station has never heard of is still a plug somebody can charge from,
and a station that refused to describe one would be useless at exactly the
moment a new standard arrives. So anything may be typed and is passed on as
written. What OCPP 2.1 does name itself is offered as chips - read off the
protocol stack by reflection, not copied here - and a typed type that matches
one of them is rewritten in the protocol's spelling, so that `stype2` and
`sType2` do not reach a back end as two different sockets. Anything else is
marked in the page and named once in the log, because a new plug and a typo
look alike from here and only the person who typed it can tell them apart.


## The clock

`ChargingStation` takes a `TimeProvider` as its last constructor parameter and
hands it to everything of its own that asks what time it is: the timestamp of
every log entry, `CreatedAt`, the uptime the status resource reports, and the
sessions - through Hermod's `SessionStore`, which takes one too. The system
clock by default; an NTS-disciplined or a fake one where a test or a
calibration says so.

It is assigned first in the constructor, before the event log is built, because
the log stamps its entries with it - a clock set afterwards would leave the log
reading the system one, which is a log that cannot be held against anything.

```csharp
sealed class FixedClock(DateTimeOffset Start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = Start;
    public override DateTimeOffset GetUtcNow() => Now;
}

var clock   = new FixedClock(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));
var station = new ChargingStation(TimeProvider: clock);

station.Sessions.TryLogin("root", password, out var session);   // 1 live session
clock.Now = clock.Now.AddHours(13);                             // past the 12 hour idle timeout
var gone  = station.Sessions.Count;                             // 0
```


## The log

Every entry carries a timestamp, a level (`debug`, `info`, `notice`,
`warning`, `error`, `critical`) and any number of tags (`http`, `ocpp`,
`15118`, `web`, `auth`, ...). The Logs page filters on both - the level counts
as a tag, so `critical` and `ocpp` can be picked together.

Anything in the station can write to it:

```csharp
station.Log.Warning("The CSMS did not answer the BootNotification.", "ocpp");
```

What the libraries below write through Illias' `DebugX` lands there too,
tagged `trace` plus whatever `TraceBridge` recognises in the text. That works
in a debug build only: `Debug.WriteLine` carries `[Conditional("DEBUG")]`, so
a release build of those libraries compiles the calls away. `--no-trace`
switches the bridge off.
