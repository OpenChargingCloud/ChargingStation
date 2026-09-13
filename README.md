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
`--frontend <dir>`, `--evses <file>`, `--verbose`, `--quiet`, `--no-trace`,
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

The web interface has a page per thing that can be configured, and each says
which of its fields may be changed and which only describe what is there. A
client that was handed its name servers at construction cannot be given
different ones afterwards, and a page that offered to try would be lying about
what the button does.

| | |
|---|---|
| `/configuration`        | what the station is made of, read-only |
| `/configuration/dns`    | the DNS client: its servers, and the cache, DNSSEC, CNAME and retry settings it will take |
| `/configuration/nts`    | the NTS client: its server, its cookie pool, and the timeout it will take |
| `/configuration/evses`  | the EVSEs: add, remove, renumber, and pick connector types |

    GET  /api/v1/configuration/dns      PUT with the fields to change
    GET  /api/v1/configuration/nts      PUT with the fields to change
    GET  /api/v1/configuration/evses    PUT with {"evses": [...]}, all of them at once

### EVSEs

They live in `evses.json` (`--evses <file>`), because how many outlets a
charging station has is a property of the hardware and should not have to be
repeated at every start. Without the file the station has one 22 kW type 2
socket.

They are edited as a whole rather than one at a time, because they are only
valid together: OCPP numbers them from 1 upwards without gaps, so removing the
third of four is a change to two of them.

Saving writes the file. The OCPP nodes are told how many EVSEs they have when
they are built, i.e. once at the start, so a saved change reaches them at the
next start - and until then the page says the two differ rather than letting
somebody believe otherwise.

Connector types are checked against the vocabulary OCPP 2.1 defines, which is
read off the protocol stack itself rather than copied here. `ConnectorType`
is a set of predefined strings and its own `TryParse` accepts anything
non-empty, so an EVSE offering `tpye2` would otherwise be saved without a
word and matched by no vehicle ever.


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
