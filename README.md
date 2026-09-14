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
  "power": { "uplinkPowerLimit_kW": 55 },
  "evses": [ { "id": 1, "maxPower_kW": 22,
               "connectors": [ { "id": 1, "type": "sType2", "maxPower_kW": 22 } ] } ],
  "calibration": [ { "id": "meter-evse-1", "pem": "-----BEGIN CERTIFICATE-----\n..." } ],
  "rfid":  [ { "id": "reader-a", "kind": "PC/SC", "evse": 1, "enabled": true } ],
  "operator": { "name": "Stadtwerke Musterstadt",
                "emps": [ { "id": "emp-one", "name": "Elektro Mobil GmbH", "tokenPrefixes": [ "04A2" ] } ] },
  "webPayments": { "enabled": true, "urlTemplate": "https://pay.example.org/{evseId}/{TOTP}",
                   "sharedSecret": "..." }
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
| `/configuration`             | what the station is made of, read-only |
| `/configuration/dns`         | name resolution: on/off, the servers, the settings, and a test |
| `/configuration/nts`         | the time source: on/off, the server, the cookie pool, and "Sync now" |
| `/configuration/power`       | what the grid connection allows, against what the EVSEs could draw |
| `/configuration/evses`       | the EVSEs: add, remove, renumber, the cables and their limits |
| `/configuration/rfid`        | the card readers: which ones, where they sit, and on or off |
| `/configuration/calibration` | the calibration certificates this station runs under |

    GET  /api/v1/configuration/dns          PUT with the fields to change
    POST /api/v1/configuration/dns/query    {"name": "...", "recordTypes": ["A"]}
    GET  /api/v1/configuration/nts          PUT with the fields to change
    POST /api/v1/configuration/nts/sync     NTS-KE + one authenticated NTP request
    GET  /api/v1/configuration/power        PUT with {"uplinkPowerLimit_kW": 55}, null clears it
    GET  /api/v1/configuration/evses        PUT with {"evses": [...]}, all of them at once
    GET  /api/v1/configuration/rfid         PUT with {"readers": [...]}, all of them at once
    GET  /api/v1/reservations               POST {"idToken": "...", "evse": 1, "minutes": 15}
    POST /api/v1/reservations/cancel        {"reservationId": "..."}
    GET  /api/v1/messages                   POST {"text": "...", "priority": "...", "state": "...", "evse": 1}
    POST /api/v1/messages/clear             {"id": "..."}
    GET  /api/v1/configuration/calibration  PUT with {"certificates": [...]}, all of them at once

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
| `cpo`         | that, plus change DNS and NTS, run their tests, and take an EVSE or a card reader out of service |
| `installer`   | that, plus the power limits and the calibration certificates |
| `systemadmin` | that, plus change what the station is made of |

The two steps above the operator are different in kind, and that is the whole
reason there are two of them.

Taking something out of service is the **operator's**: something is wrong with
an outlet, or somebody is working on it, and the person who finds that out is
the one running the station rather than the one who installed it a year ago. It
is also the one statement here that is safe to be wrong about in the careful
direction - a station that says an outlet is unusable when it is not serves
nobody, which is a nuisance; the other way round is a driver standing in the
rain.

The **installer** works on equipment that is already there and corrects the
numbers and the papers that came with it. The grid operator says the connection
may draw 55 kW rather than the 80 kW on the order, the cable that went in is a
32 A one, and here is the certificate of the meter that was fitted.

The **system administrator** says what the equipment *is* - how many sockets
there are and what shape they have. That is a claim nothing further down can
check: saying there is a CCS socket where a type 2 socket is bolted to the wall
does not change the wall, it changes what every vehicle and every back end is
told about it. The network settings are reversible and they complain; a wrong
connector does neither.

`PUT /configuration/evses` therefore needs **whichever** of three permissions
the request turns out to call for, because the request cannot say: the whole
list is sent either way, and taking an EVSE out of service, correcting a
cable's limit and inventing a socket are the same document. So nothing beyond
reading gets in at the door, the station compares what it was sent with what it
has, and the answer decides - under the same lock that then applies the change,
so nothing moves between the question and the answer. One save can be more than
one kind of change at once, and then all of them are needed. An installer who
changes a plug type gets

    403  This changes the equipment of this station. This needs the
         systemadmin role.

and the page says the same thing before the button is pressed, by making the
same comparison in the browser.

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

Each EVSE has a limit and so does each of its cables, and they are not the same
number. An EVSE serves one vehicle at a time, so its limit is what the power
stage behind all its cables can deliver; a cable's is what that cable can
carry. A DC charger with CCS on one side and CHAdeMO on the other is one EVSE
with a 300 kW cable and a 50 kW one, and giving both the EVSE's number would
tell a CHAdeMO vehicle it may draw six times what its cable is rated for. A
cable may not be configured above the EVSE feeding it. The per-cable limits
reach OCPP 1.6, where a connector *is* a cable and has a `MaxPower`; OCPP 2.1
is told the shapes but has nowhere in `EVSESpec` to put the limits.

The older spelling still reads:

```json
{ "id": 1, "connectorTypes": [ "sType2" ], "maxPower_kW": 22 }
```

Every cable is then whatever its EVSE is, which is what it meant before there
was anywhere else to put the number. The file is rewritten in the newer
spelling the next time it is saved.

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

### Power

`"power": { "uplinkPowerLimit_kW": 55 }` is what the connection behind the
meter allows. It belongs to the building rather than to the station: it is what
the grid operator and the fuse permit, and it stays put while EVSEs are added
and taken away in front of it.

It is entirely normal for it to be below the sum of what the EVSEs could
deliver - four 22 kW outlets on a 55 kW connection is how most stations are
built, and load management is what the difference is for. So the two are not
checked against each other and neither is called wrong; the station says in its
log what it is looking at, and the page shows them side by side.

Nothing in this station enforces the limit yet, because there is no load
management here to enforce it with. Today it is a number the station knows and
reports.

### Calibration

`"calibration"` holds the certificates this station runs under, and asks for
nothing but the PEM:

```json
{ "id": "meter-evse-1", "description": "The meter in EVSE 1", "pem": "-----BEGIN CERTIFICATE-----\n..." }
```

The subject, the issuer, the serial number and the validity are read out of the
certificate rather than typed beside it, and deliberately not written back to
the file. A copy of them could only ever start disagreeing with the certificate
it was copied from, and then there is no telling which of the two the station
means.

A certificate that has already run out is kept and said so about rather than
refused - the station that may not hold its own expired certificate is the
station that cannot show what it was running under last month. One that runs
out within 90 days is a warning in the log at every start, because a
certificate running out does not stop a station from charging: it stops what it
charged from being billable, which is noticed a month later by somebody who was
not there.


### RFID

A station has one reader for the whole housing or one per EVSE, and which of the
two it is changes what the display has to do: a reader that belongs to the
station has to ask which outlet the card is for, a reader beside an outlet does
not. So `"evse": null` is the station-wide case, there is at most one reader per
place, and the placement is configured rather than discovered.

The kinds are an open set, for the same reason connector types are: a reader
this station has no driver for is still a reader somebody bolted on, and
refusing to write it down would not make it go away. It is configured, it is
shown, it is said once in the log, and it reads nothing.

**`GraphDefined.FakeRFID` is the one kind with anything behind it**, and its
cards are typed into the display rather than held against it. That is for
testing and says so on the page, in the log at every start, and on the display.

A UID is normalised on the way in - upper case, no separators - because readers
disagree about colons and dashes and a station that passed those on as it found
them would report one card as several.

### Operator and providers

`"operator"` is whose station this is: a name and maybe a logo, and the list of
e-mobility providers whose cards the display puts a name to. Cards are
recognised by the start of their UID, which is a coarse rule and deliberately
so - it is enough to print a name on a screen and nowhere near enough to bill
anybody, which is the right way round for a thing on a wall in public. Somebody
charging ad hoc has no provider, and the name over that session is the
operator's, because that is who they are buying from.

### Web payments

`"webPayments"` is the QR code on the display: a URL carrying a time-based
one-time password over a shared secret, so that a photograph of yesterday's
screen is worth nothing. `{evseId}` in the template is filled in per outlet and
`{TOTP}` with the password. A code with less than five seconds left is not shown
at all - a code somebody photographs and then cannot use is worse than no code,
because the second attempt looks like the station is broken.

**This is the one section with no page and no HTTP route.** The shared secret is
the one piece of configuration in this station worth stealing, and the display
it ends up on hangs in public - so it stays in the file, and changing it is
editing the file and restarting. Everything else this station can be told still
changes while it runs.

## The display

A page with no sign-in on it, for the screen on the front of the station:
`http://<host>:2349/` by default, `--kiosk-port <n>`, `--no-kiosk` to leave it
out. It shows each EVSE with its label, whether it is free, reserved, charging
or out of service, what it is drawing against what it could, the shape and limit
of each cable, who is charging (PnC, RFID, AdHoc or Remote) and their provider's
name or logo, the QR code to pay with, and a card symbol where there is a
reader.

### Why a second server and not a second page

Everything on the display is public by design, and the machine it is shown on is
a screen bolted to a charging station in a car park. Somebody with a keyboard, a
USB port or a way out of a browser's full-screen mode is standing at that
machine, and whatever that machine can reach, they can reach.

A page on the main server would mean the display and the administration share an
origin: the sign-in form, the session cookie and every configuration route are
one URL away from the screen, and keeping them apart would rest on every route
being correctly gated, for ever, by everybody who adds one. A second listener
makes it a property of the deployment instead of a property of the code - and,
the part that actually matters, it can be **bound to a different address**: the
display on the screen's own network, the administration on the maintenance side,
neither reachable from the other's wire. That is a sentence somebody can check
with a port scanner, which "we reviewed the handlers" is not.

    $ curl -s -o /dev/null -w '%{http_code}\n' http://localhost:2349/api/v1/auth/login
    404

The cost is one more socket: the same process, the same station object, the same
log, and a second entry point in the bundle so that the file a screen in a car
park downloads does not contain the sign-in form and every configuration page.

The display can read, and it can do exactly one thing: hold a card against a
reader whose cards are typed in. A station with only real readers configured has
no write on that port at all, and the refusal is by what the reader is rather
than by who is asking - which is the only kind of rule that holds where there is
no sign-in.

### Reservations

`reserved` on the display is OCPP's: a `ReserveNow` makes it, a
`CancelReservation` or its own expiry date ends it, and it is kept in the
OCPP 2.1 node rather than beside it - a second list would be a second opinion
about whether an outlet is taken. The station answers as OCPP expects:
`Rejected` for a request it cannot make sense of (an EVSE it does not have, a
date already past, a plug that outlet does not have), `Unavailable` for an
outlet out of service, `Occupied` for one that is charging or already held.
A reservation naming no EVSE is a promise that one will be free, and is refused
when none could be.

A held outlet shows no QR code - somebody paying at the screen would be buying
an outlet that belongs to somebody else - and it lets exactly one card in: the
one it is held for, or one of its group. The right card starts charging and
takes the reservation up; the wrong one is turned away with the time it runs
out at. **The display never shows the token it is waiting for**: a screen in a
car park printing somebody's card number is printing it for everybody walking
past, and the way to prove the outlet is yours is to hold your card against the
reader.

A reservation that names **no** EVSE is a promise that one outlet will be free
rather than a claim on any particular one, so it is shown where it is true -
across the heading of the display, not beside an outlet it would be saying
something untrue about. It carries its own button and is let go of the same way.

The display can also let a reservation go: a held outlet carries a "Cancel
reservation" button, and pressing it asks for the card. That is the whole of the
authorisation, and it is the only kind there can be on a port with no sign-in -
anybody may press the button, only the card gets anywhere. A card that is not
the one is refused without being told how close it was - and for the hold over
the whole station the refusal does not even say that there is one, because a
card somebody else's promise is none of is a card that should not learn it
exists. So the two things the display can change are the same gesture with the
same proof: what you may do here is what you can hold up, not who you say you
are.

Nothing is connected to a CSMS yet, so `POST /api/v1/reservations` builds a real
`ReserveNowRequest` and hands it to the same method the incoming OCPP handler
calls. It is a way in, not a second implementation - the day a CSMS does
connect, it is the same code answering it. Holding an outlet takes it out of
general use, which is the same kind of statement as taking one out of service,
so it is the operator's.

Reconfiguring the EVSEs rebuilds both OCPP nodes, and a new node starts knowing
nothing - so what is charging and what is held is carried across. A corrected
number or a switch is the same station and both survive; a change to the
hardware is a different station, and a reservation for "EVSE 2" would afterwards
be a promise about something else, so those are let go of and said so about in
the log.

### The clock, and what it is worth

The display carries the time, and under it one line saying what that time is
worth. Two different questions, and a charging station has to keep them apart.

The time shown is the station's **own system clock**. Whether it is any good is
answered by asking a server that knows - and **this station does not set its
clock from the answer**. It measures the difference and reports it. Stepping the
clock of a machine that meters energy and writes signed records is not something
a background task does by surprise: a jump backwards puts two readings out of
order with nothing in the record to say why. Measuring is the part that can be
done safely and the part that tells somebody whether there is a problem.

**"Legal time" is never guessed.** Nothing here can tell from a hostname whether
a server disseminates a country's legal time - that is a fact about an
institution, not about DNS. So the operator says so in `nts.legalTimeAuthority`,
and the station then repeats that claim only while it can stand behind all four
of:

| | |
|---|---|
| the claim exists | `legalTimeAuthority` is configured |
| checking is on | NTS is enabled |
| it was actually checked | and recently - `legalTimeMaxAgeSeconds`, an hour by default |
| the clock was close | within `legalTimeToleranceSeconds`, a second by default |

Any one of them missing and the display says the time is unverified **and why**:
`no time authority configured`, `time checking is switched off`, `not checked
yet`, `last check too long ago`, or `clock is -347 ms out`. A true statement
about a station that has not checked is worth more than the word "legal" over a
clock nobody verified.

The check runs by itself every `nts.checkEverySeconds` (fifteen minutes by
default), the first one a minute after starting - everything else is still
coming up, and a display that says "unverified" for a minute after a start is
telling the truth.

The digits tick in place rather than through a redraw, and the time comes from
the station's clock carried forward by the difference between two readings of
the screen's own - so a screen whose clock is hours out still shows the
station's time to the second.

### Display messages

OCPP 2.1's `SetDisplayMessage`, shown where it says it should be shown. Three
things decide that, and all three come from the message rather than from the
screen:

| | |
|---|---|
| **when** | a start and an end; a message whose time has passed is not a message |
| **where** | no EVSE means the whole housing, an EVSE means beside that outlet |
| **what is happening** | `Charging`, `Idle`, `Unavailable`, ... or nothing, for always |

So "unplug before you leave" is written once, for the charging state, and is on
screen exactly while somebody is charging - not before, not after, and not at
the outlet next to them.

**They stack.** A station holds as many as a back end sends, and the priority
says how they share the screen: `AlwaysFront` and `InFront` stay put,
everything else takes its turn, about seven seconds each, in a fixed order so
that a message does not lose its place when another arrives. A screen that can
only ever show one line has to be taken apart the first time a second message
arrives, which is why this was built with the second one in mind.

This station shows text and says so: `ASCII` and `UTF8` are accepted, `HTML`,
`URI` and `QRCODE` are refused with `NotSupportedMessageFormat` rather than
accepted and quietly dropped. A message for an EVSE it does not have is
`Rejected`. A message arriving under an id it already holds **replaces** it -
that is a back end correcting or extending a message, and refusing it would
leave no way to change one except to clear it and hope nobody reads the screen
in between.

`POST /api/v1/messages` builds a real `MessageInfo` and hands it to the same
method the incoming OCPP handler calls, for the same reason the reservations do.
Saying something on the front of the station is the operator's to do.

### What drives it

The status of an EVSE comes from its own configuration, from OCPP's
reservations, and from the sessions;
the QR code is a real one-time password over the configured secret. The sessions
are started and stopped by the card readers, and today the only reader with
anything behind it is the fake one - so a station nobody has touched shows every
outlet as free. Nothing here is connected to a CSMS, because nothing in this
station is yet.

**The power figure is simulated and says so**, on the screen and in the JSON.
This station has no energy meter and is not connected to anything that has one.
A dash where the power should be would be honest and useless to look at; a
number that pretends to be a meter reading would be useful and a lie.

The page polls every two seconds rather than holding an event stream open. A
display is the one client where a dropped connection must not be noticed by
anybody: polling recovers by itself, and by the time somebody walks up to the
screen it is right again.

**It is exactly the screen, never more.** A display has no scroll bar and nobody
in front of it to use one: anything below the fold does not exist. Six outlets
on a 1080p screen used to put the second row half off the bottom, and the
station looked like it had four. Checked at 1920x1080, 1080x1920 portrait,
1024x600 and 800x480, with six outlets in every state at once - nothing
overflows and nothing scrolls.

What gives way as the cards get shorter is decided rather than left to the
browser. Each card is measured against its own cell, and below about 420 px
there is no room for a code a phone could read - so none is drawn, because an
unreadable code is worse than none: somebody tries, fails, and blames the
station. Below 400 px everything moves closer together, and on an outlet that is
busy the plug list goes first, since it is the one thing nobody standing there
can act on. The outlet still says whether it is free, which is what matters from
three metres away.

**It is touched, not clicked.** Everything on this screen is sized in vmin,
which is right for something read from three metres away and wrong for
something poked with a finger: on an 800x480 panel a vmin is under five pixels,
and the cancel button came out seventeen pixels high with eight-pixel type.
Every control now has a floor in real pixels - 44 px for a button, 48 px and
17 px type for a field, which is also the size below which a browser zooms in
when a field is tapped. Where a card gets short the text and the spacing give
way; the controls do not.

**Out of contact it stops claiming things.** Measured by killing the station
with the page open: after about thirteen seconds the heading says so, the cards
dim and the status word becomes "not known" - "free" is a promise that somebody
can walk up and plug in, and a screen that has not been told anything for half
a minute cannot make it. The payment codes go at once rather than when the
banner appears, because a code carries about thirty seconds of life and a dead
one is worse than none: somebody scans it, pays nothing, and concludes the
station is broken. Starting the station again brings all of it back on its own,
with nobody touching the screen.

How much life a code has left is worked out from the difference between two of
the station's own timestamps, never by comparing one of them with this
machine's clock. A screen bolted to a wall has whatever clock somebody left in
it. Tested with that clock seven minutes out: no effect. A clock that *jumps* -
an NTP correction - costs one poll cycle, after which it is consistent again.


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
