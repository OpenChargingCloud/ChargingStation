import { config } from '../config';


// What the JSON API answers. Everything below /api/v1 except the sign-in needs
// the session cookie, which the browser sends by itself because every request
// here is same-origin.

/** How loudly a log entry asks to be read. */
export type LogLevel = 'debug' | 'info' | 'notice' | 'warning' | 'error' | 'critical';

/** The levels in the order the station defines them, quietest first. */
export const logLevels: LogLevel[] = ['debug', 'info', 'notice', 'warning', 'error', 'critical'];

/** One thing that happened inside the charging station. */
export interface LogEntry {
    /** A number that only ever grows, so the page can tell what it has seen. */
    id:         number;
    timestamp:  string;
    level:      LogLevel;
    /** What it is about: "ocpp", "15118", "http", ... - without the level. */
    tags:       string[];
    message:    string;
    /** Whatever else belongs to it, when there is more than one line to say. */
    data?:      unknown;
}

/** What a page of the log brings back. */
export interface LogPage {
    /** The newest id of the whole log, whatever this page was filtered by. */
    lastId:    number;
    capacity:  number;
    tags:      string[];
    entries:   LogEntry[];
}

/**
 * What somebody signed in to this station may do.
 *
 * A copy of what the station enforces, not the enforcement: it is here so a
 * page can grey out what this person may not do instead of offering it and
 * letting them find out by being refused. Every request is checked again on
 * arrival, so editing this list in a browser buys a button that answers 403.
 */
export type Permission = 'readConfiguration'
                       | 'changeNetworkSettings'
                       | 'runDiagnostics'
                       | 'changeAvailability'
                       | 'changePowerLimits'
                       | 'manageCalibration'
                       | 'changeHardware';

/** Who is signed in to the web interface. */
export interface Me {
    username:     string;
    roles:        string[];
    permissions:  Permission[];
}

/** How the station is doing right now. */
export interface Status {
    service:    string;
    version:    string;
    hermod:     string | null;
    timestamp:  string;
    startedAt:  string;
    uptime:     string;
    sessions:   number;
    log:        { entries: number; capacity: number; lastId: number; tags: string[] };
}

/**
 * What the station is made of. Only the shape the Configuration page relies
 * on is named; the rest is rendered from whatever the station sends, so that
 * a new section on the server needs no change here.
 */
export interface Configuration {
    station:     Record<string, unknown>;
    http:        Record<string, unknown>;
    web:         Record<string, unknown>;
    log:         Record<string, unknown>;
    time:        Record<string, unknown>;
    ocpp:        Record<string, unknown>[];
    assemblies:  Record<string, unknown>[];
}


/** One name server this station asks. */
export interface DNSServer {
    /** An IP address or a host name. */
    address:              string;
    port:                 number;
    transport:            string;
    queryTimeoutSeconds:  number | null;
}

/** What may be changed about the name resolution while the station runs. */
export interface DNSSettings {
    queryTimeoutSeconds:  number;
    /** null leaves it to the server's own default. */
    recursionDesired:     boolean | null;
    useCache:             boolean;
    dnssecOK:             boolean;
    followCNAMEs:         boolean;
    maxCNAMEFollows:      number;
    maxRetries:           number;
}

/** How this station resolves names. */
export interface DNSConfiguration {
    enabled:    boolean;
    servers:    DNSServer[];
    settings:   DNSSettings;
    /** What was decided when the client was made, and is not on offer. */
    fixed:      Record<string, unknown>;
    limits: {
        maxServers:       number;
        maxQueryTimeout:  number;
        transports:       string[];
        recordTypes:      string[];
    };
    file:       string;
}

/** What a PUT to the DNS configuration may carry; everything is optional. */
export interface DNSUpdate {
    enabled?:              boolean;
    servers?:              DNSServer[];
    queryTimeoutSeconds?:  number;
    recursionDesired?:     boolean | null;
    useCache?:             boolean;
    dnssecOK?:             boolean;
    followCNAMEs?:         boolean;
    maxCNAMEFollows?:      number;
    maxRetries?:           number;
}

/** One resource record a test query brought back. */
export interface DNSRecord {
    name:        string;
    type:        string;
    timeToLive:  number;
    value:       string;
}

/** What a test query brought back. */
export interface DNSQueryResult {
    name:           string;
    /** Which single name server was asked, or null when all of them were. */
    asked?:         string | null;
    /** Set when an address was typed and a reverse name was asked for instead. */
    turnedAround?:  string | null;
    recordTypes:    string[];
    ok:             boolean;
    error?:         string;
    responseCode?:  string;
    server?:        string;
    runtime_ms?:    number;
    authoritative?: boolean;
    truncated?:     boolean;
    dnssec?:        string | null;
    timedOut?:      boolean;
    answers:        DNSRecord[];
    more?:          number;
}


/** One line of what happened while a time server was being asked. */
export interface TimeServerTestStep {
    at_ms:  number;
    level:  'info' | 'notice' | 'warning' | 'error';
    text:   string;
}

/** What came of asking one time server everything. */
export interface TimeServerTest {
    host:        string;
    ok:          boolean;
    runtime_ms:  number;
    steps:       TimeServerTestStep[];
}

/**
 * What may be changed about the time servers while the station runs. What is
 * left out stays as it is; the list of servers is one value and replaces the
 * station's whole.
 */
export interface NTSUpdate {
    enabled?:              boolean;
    servers?:              NTSServerEntry[];
    minServers?:           number;
    maxDeviationSeconds?:  number;
    checkEverySeconds?:    number;
    timeoutSeconds?:       number;
}

/**
 * One time server as the configuration names it. Whatever is left out is the
 * usual: priority 0, the usual ports, switched on.
 */
export interface NTSServerEntry {
    hostname:    string;
    priority?:   number;
    ntsKEPort?:  number;
    ntpPort?:    number;
    enabled?:    boolean;
}

/** How one synchronisation went, step by step. */
export interface NTSSyncResult {
    ok:           boolean;
    server:       string;
    at:           string;
    error?:       string;
    step?:        string;
    runtime_ms?:  number;
    offset_ms?:   number | null;

    /** What the group concluded: the median, how many answered, how far apart. */
    group?:       {
        name:               string;
        answered:           number;
        required:           number;
        offset_ms:          number | null;
        spread_ms:          number | null;
        deviationExceeded:  boolean;
    };

    /** One entry per server asked, answered or not. */
    servers?:     NTSServerResult[];

    /** Only from the detailed test of a single server. */
    ntske?:       Record<string, unknown>;
    ntp?:         Record<string, unknown>;
}

/** What one time server of a group said. */
export interface NTSServerResult {
    hostname:       string;
    ok:             boolean;
    offset_ms?:     number | null;
    roundTrip_ms?:  number | null;
    authenticated?: boolean | null;
    keyExchange?:   string;
    error?:         string | null;
}

/** One server of this station's group, and what its key exchange is doing. */
export interface NTSTimeSource {
    hostname:       string;
    priority:       number;
    ntsKEPort:      number;
    ntpPort:        number;
    enabled:        boolean;
    cookies?:       number | null;
    lastExchange?:  string | null;
    aeadAlgorithm?: string | null;

    /**
     * The root CA the certificate chain of the last key exchange ended at -
     * the chain this station built, so the root it judged the certificate by -
     * or null before the first exchange.
     */
    rootCA?:        NTSRootCA | null;
}

/** A root CA, by a name to call it, its subject, and its SHA-256 fingerprint. */
export interface NTSRootCA {
    name:         string;
    subject:      string;
    fingerprint:  string;
}

/** Where this station gets the time from, and how its key exchange is doing. */
export interface NTSConfiguration {
    enabled:   boolean;

    /**
     * Every server this station has, switched on or not, in the order they
     * were configured - and the rules for believing them.
     */
    timeSources?:  NTSTimeSource[];
    group?:        { name: string; minServers: number; maxDeviationSeconds: number };

    /**
     * What may be changed about the group and the test. The quorum is the one
     * wanted; the group's own can be lower while it has fewer servers on.
     */
    settings:  {
        timeoutSeconds:       number | null;
        checkEverySeconds:    number;
        minServers:           number;
        maxDeviationSeconds:  number;
    };
    /** What any new client starts with, the group's and the test's alike. */
    policy:    Record<string, unknown>;
    lastSync:  NTSSyncResult | null;
    limits:    {
        maxTimeout:        number;
        minCheckEvery:     number;
        maxCheckEvery:     number;
        minDeviation:      number;
        maxDeviation:      number;
        defaultNTSKEPort:  number;
        defaultNTPPort:    number;
    };
    file:      string;
    /** Only on the answer to a synchronisation, which carries both. */
    result?:   NTSSyncResult;
}


/**
 * One cable or socket of an EVSE.
 *
 * Its shape and its limit are two different kinds of fact, and the web
 * interface treats them as two: what plug is fitted takes the hardware
 * permission, what it may deliver takes the power-limit one.
 */
export interface Connector {
    /** Which one it is, counting from 1 within its EVSE. */
    id:           number;
    /** What can be plugged into it, in OCPP 2.1's vocabulary. */
    type:         string;
    /** The most this cable may deliver; never more than its EVSE. */
    maxPower_kW:  number;
}

/** One place a vehicle can be plugged into this charging station. */
export interface EVSE {
    /** Which one it is, counting from 1 as OCPP does. */
    id:                 number;
    connectors:         Connector[];
    /** The most this EVSE can deliver, through whichever cable is in use. */
    maxPower_kW:        number;
    operative:          boolean;
    /** What is written on the housing, e.g. "A". */
    physicalReference:  string | null;
    meterType:          string | null;
    meterSerialNumber:  string | null;
}

/** The EVSEs of this station, and what may be plugged into one. */
export interface EVSEConfiguration {
    evses:                   EVSE[];
    file:                    string;
    maxEVSEs:                number;
    maxConnectors:           number;
    maxPower_kW:             number;
    maxConnectorTypeLength:  number;
    /** What the whole station may draw, for context; null when nobody has said. */
    uplinkPowerLimit_kW:     number | null;
    /**
     * The connector types OCPP 2.1 names itself, for the picker. Not a closed
     * list: anything may be typed, because a plug this station has never heard
     * of is still a plug somebody can charge from.
     */
    connectorTypes:          string[];
}


/**
 * What came up on the wire below the charging cable.
 *
 * Null while the station has not been started; and every field here may
 * disagree with what was asked for, which is the reason it is sent at all.
 */
export interface V2GLinkStatus {
    /** The interface actually chosen, which need not be the one named. */
    interface:      string | null;
    linkLocal:      string | null;
    v2gEndpoint:    string | null;
    v2gTLS:         boolean;
    /** Whether SDP is really answering, not whether it was asked to. */
    sdp:            boolean;
    /** Whether it is really also answering vehicles on this machine. */
    sdpLoopback:    boolean;
    slac:           boolean;
    slacTransport:  string | null;
    slacSessions:   number;
    evseId:         string;
    /** The 10BASE-T1S bus of a megawatt coupler, where this station coordinates one. */
    t1s:            T1SBusStatus | null;
}

/** One node on the coupler's bus: the vehicle, or a sensor in a pin. */
export interface T1SNodeStatus {
    id:             number;
    name:           string;
    /** Vehicle, TemperatureSensor, and whatever else joins. */
    role:           string;
    mac:            string;
    /** Transmit opportunities per cycle: the vehicle asks for more than a sensor. */
    weight:         number;
    lastSeen:       string;
    /** Cycles in a row this node did not answer; five and it is given up for lost. */
    missed:         number;
    frames:         number;
    yields:         number;
    /** What the pin reads, where the node is a temperature sensor. */
    temperatureC:   number | null;
    /** normal, warning, overload or lost - the station's opinion of it. */
    thermal:        string | null;
}

/** The bus below a megawatt coupler, as the station coordinating it sees it. */
export interface T1SBusStatus {
    /** What the medium is: "UDP multicast 239.151.18.1:2354" or "AF_PACKET on eth1". */
    medium:         string;
    mac:            string;
    cycle:          number;
    /** Frames that arrived outside their sender's turn: a fault, or a node that is not ours. */
    outOfTurn:      number;
    /** Nodes that asked to join in the same opportunity. */
    collisions:     number;
    thermal: {
        state:      string;
        alarm:      boolean;
        warningC:   number;
        overloadC:  number;
    };
    nodes:          T1SNodeStatus[];
}

/** What this station offers a vehicle below the charging cable. */
export interface V2GConfiguration {
    enabled:         boolean;
    /** Whether the SECC Discovery Protocol answers vehicles. */
    sdp:             boolean;
    /** Whether it also answers a vehicle running on this same machine. */
    loopback:        boolean;
    /** The powerline interface, or null to let the station pick one. */
    interface:       string | null;
    /** 0 lets the system pick a port, which is what SDP then advertises. */
    port:            number;
    evseId:          string;
    slac:            string;
    /** Which medium the coupler's bus is on: none, auto, afpacket or udp. */
    t1sTransport:    string;
    /** The group and port of the emulated medium, or null for the library's default. */
    t1sBus:          string | null;
    t1sInterface:    string | null;
    t1sName:         string | null;
    t1sCycleMs:      number;
    t1sWarningC:     number;
    t1sOverloadC:    number;
    /** The group the emulation uses when none is named, for the placeholder. */
    t1sDefaultBus:   string;
    /**
     * Whether a V2G server certificate was passed on the command line. Not
     * settable from the page - a certificate is a file and a password - but it
     * is what decides whether the endpoint speaks TLS.
     */
    certificate:     boolean;
    /** The transports this station knows, for the picker. */
    slacTransports:  string[];
    /** The same, for the bus below a megawatt coupler. */
    t1sTransports:   string[];
    /** Whether the station has been started; nothing comes up before that. */
    running:         boolean;
    link:            V2GLinkStatus | null;
    file:            string;
}

/** What a PUT to the V2G configuration may carry; everything is optional. */
export interface V2GUpdate {
    enabled?:    boolean;
    sdp?:        boolean;
    loopback?:   boolean;
    interface?:  string | null;
    port?:       number;
    evseId?:     string;
    slac?:       string;
    t1sTransport?:   string;
    t1sBus?:         string | null;
    t1sInterface?:   string | null;
    t1sName?:        string | null;
    t1sCycleMs?:     number;
    t1sWarningC?:    number;
    t1sOverloadC?:   number;
}


/** What this station may draw from the grid, and what it could deliver. */
export interface PowerConfiguration {
    /** The most the whole station may draw; null when nobody has said. */
    uplinkPowerLimit_kW:  number | null;
    evses:                { id: number; maxPower_kW: number }[];
    /** What the EVSEs could draw together, which may legitimately be more. */
    evsesTotal_kW:        number;
    limits:               { maxUplinkPowerLimit_kW: number; maxEVSEPowerLimit_kW: number };
    file:                 string;
}

/**
 * When the screen on the front of the station is dim, and how dim.
 *
 * Both ends of the window or neither: one end is not a window. Times are
 * written the way a person writes them - "22:00" - in the station's own local
 * time, and a window that crosses midnight is the ordinary case rather than a
 * special one.
 */
export interface DisplayConfiguration {
    /** When the quiet hours begin, or null when this station keeps none. */
    dimFrom:    string | null;
    /** When they end. Earlier than dimFrom means they cross midnight. */
    dimUntil:   string | null;
    /** How bright the screen is while nothing is happening, or null for the default. */
    dimTo:      number | null;
    /** Whether it is one of them at this moment, as the station reckons it. */
    quietNow:   boolean;
    limits:     { darkestDimTo: number; defaultDimTo: number };
    file:       string;
}

/** What a PUT to the power configuration carries; null takes the limit away. */
export interface PowerUpdate {
    uplinkPowerLimit_kW:  number | null;
}


/**
 * One calibration certificate this station runs under.
 *
 * Everything below the PEM was read out of it rather than typed: an issuer
 * somebody types can disagree with the certificate it was typed from, and then
 * there is no telling which of the two the station means.
 */
export interface CalibrationCertificate {
    /** What it is called here - the name it is changed and removed by. */
    id:                 string;
    description:        string | null;
    pem:                string;
    subject:            string;
    issuer:             string;
    serialNumber:       string;
    notBefore:          string;
    notAfter:           string;
    thumbprintSHA256:   string;
    expired:            boolean;
    notYetValid:        boolean;
    /** Negative once it has run out. */
    daysLeft:           number;
}

/** The calibration certificates of this station. */
export interface CalibrationConfiguration {
    certificates:  CalibrationCertificate[];
    limits: {
        maxCertificates:       number;
        maxIdLength:           number;
        maxDescriptionLength:  number;
        maxPEMLength:          number;
        expiryWarningDays:     number;
    };
    file:          string;
}

/** One card reader this station has, and where it sits. */
export interface RFIDReader {
    id:       string;
    kind:     string;
    /** Which EVSE it belongs to, or null when it serves the whole station. */
    evse:     number | null;
    enabled:  boolean;
    /** Whether this station has a driver for this kind of reader at all. */
    hasDriver?:  boolean;
    /** Whether its cards are typed into the display rather than held against it. */
    fake?:       boolean;
}

/** The card readers of this station. */
export interface RFIDConfiguration {
    readers:     RFIDReader[];
    evses:       { id: number; label: string | null }[];
    /** The kinds this station names itself. Not a closed list. */
    kinds:       string[];
    /** The one kind whose cards are typed in. */
    fakeKind:    string;
    maxReaders:  number;
    file:        string;
}

/**
 * One set of credentials this station can prove itself with.
 *
 * The secret half is never in here. `hasSecret` is what takes its place, so a
 * page can tell "not configured yet" from "configured, and you are not being
 * shown it".
 */
export interface StationLogin {
    id:                  string;
    /** What somebody wrote down that it is for, e.g. "CSMS login". */
    description:         string;
    kind:                'basic' | 'totp';
    /** The name the other end knows this station by. */
    login:               string;
    createdAt:           string;
    hasSecret:           boolean;
    /** Whether the one-time password is bound to the TLS session. TOTP only. */
    tlsChannelBinding?:  boolean;
    validitySeconds?:    number;
    length?:             number;
    alphabet?:           string;
    hashAlgorithm?:      string;
}

/** What goes in when credentials are written down or changed. */
export interface LoginToSave {
    id?:                 string;
    description:         string;
    kind:                'basic' | 'totp';
    login:               string;
    /** Empty means "keep whatever is already there". */
    secret:              string;
    validitySeconds?:    number;
    length?:             number;
    alphabet?:           string;
    hashAlgorithm?:      string;
    tlsChannelBinding?:  boolean;
}

/** One place this station dials. */
export interface StationConnection {
    id:                  string;
    description:         string;
    url:                 string;
    connectionType:      string;
    /** Which OCPP is spoken here, and so which of this station's two nodes dials. */
    ocppVersion:         string;
    autoConnect:  boolean;
    /** Whether the URL makes a TLS connection, which decides what the rest can mean. */
    secure:              boolean;
    createdAt:           string;
    authenticationId?:   string;
    certificateId?:      string;
    warnings?:           string[];
}

/** What goes in when a connection is written down or changed. */
export interface ConnectionToSave {
    id?:                 string;
    description:         string;
    url:                 string;
    connectionType:      string;
    ocppVersion:         string;
    autoConnect:  boolean;
    authenticationId:    string | null;
    certificateId:       string | null;
}

/** One line of what happened while a connection was being tested. */
export interface ConnectionTestStep {
    /** Milliseconds since the test started. */
    at_ms:  number;
    level:  'info' | 'notice' | 'warning' | 'error';
    text:   string;
}

/** What came of testing one connection. */
export interface ConnectionTest {
    description:  string;
    url:          string;
    ok:           boolean;
    runtime_ms:   number;
    steps:        ConnectionTestStep[];
}

/** A client certificate, as far as a connection is concerned. */
export interface ConnectionCertificate {
    id:                  string;
    subject:             string;
    algorithm:           string;
    hasCertificate:      boolean;
    canBeHeldUp:         boolean;
}

/**
 * Everything the two pages work from.
 *
 * One shape for both, from one handler on the station: the Connections page
 * needs the credentials in order to offer them, and the Authentication page
 * needs the connections in order to say which ones a removal would break.
 */
export interface StationConnections {
    directory:             string;
    authentications:       StationLogin[];
    connections:           StationConnection[];
    certificates:          ConnectionCertificate[];
    connectionTypes:       string[];
    ocppVersions:          string[];
    maxDescriptionLength:  number;
    minSharedSecretLength: number;
    /** Always false, and said out loud: a secret is written here and never read back. */
    secretsAreReadable:    boolean;
    totpDefaults: {
        validitySeconds:   number;
        length:            number;
        alphabet:          string;
        hashAlgorithm:     string;
    };
}

/** One kind of key this station will make for itself. */
export interface KeyAlgorithm {
    /** How it is written in the API, e.g. "ed448". */
    id:       string;
    /** How it is written on a page, e.g. "Ed448". */
    name:     string;
    /** What somebody choosing it should know. */
    remark:   string;
}

/** One key of this station, its signing request, and its certificate if it has one. */
export interface StationKey {
    id:            string;
    algorithm:     string;
    createdAt:     string;
    subject:       string;
    /** Whether this is the one the station would hold up when it dials. */
    inUse:         boolean;
    /**
     * Whether this station can load the certificate together with its key at
     * all. False is about the platform and not about the certificate: .NET has
     * no key object for an Ed448 or an ML-DSA key today.
     */
    canBeHeldUp:   boolean;
    cannotBeHeldUp?: string;
    /** The request waiting to be collected, while there is no certificate yet. */
    csr?:          string;
    certificate?: {
        subject:           string;
        issuer:            string;
        serialNumber:      string;
        notBefore:         string;
        notAfter:          string;
        thumbprintSHA256:  string;
        /** How many were sent along between it and a root. */
        intermediates:     number;
        /** Negative once it has run out. */
        daysLeft:          number;
        expired:           boolean;
        notYetValid:       boolean;
    };
    warnings?:     string[];
}

/** The keys and certificates this station dials a back end with. */
export interface StationCertificates {
    directory:             string;
    /** What the station thinks the time is, so a page does not guess which clock the days are counted by. */
    now:                   string;
    inUseId:               string | null;
    entries:               StationKey[];
    algorithms:            KeyAlgorithm[];
    defaultAlgorithm:      string;
    maxSubjectLength:      number;
    /** Always false, and said out loud: a key that arrived from elsewhere is one somebody else has a copy of. */
    canImportPrivateKeys:  boolean;
}

/** What a certificate looks like on the way in: the rest is read out of the PEM. */
export interface CalibrationCertificateUpdate {
    id:            string;
    description?:  string | null;
    pem:           string;
}


/**
 * The station answered, and said no.
 *
 * The fields are written out rather than declared in the constructor, as are
 * NoAnswer's below: constructor parameter properties are one of the few pieces
 * of TypeScript that cannot simply be stripped away, and this file is read as
 * it stands by the same test runner that reads the display's rules.
 */
export class ApiError extends Error {

    readonly status:  number;
    readonly body?:   unknown;

    constructor(status:   number,
                message:  string,
                body?:    unknown) {

        super(message);

        this.name    = 'ApiError';
        this.status  = status;
        this.body    = body;

    }

    get isUnauthorized(): boolean {
        return this.status === 401;
    }

}


/**
 * Nothing came back at all.
 *
 * Not an ApiError, because the two are different things to be told: an
 * ApiError is the station answering and saying no, with a sentence of its own
 * about why. This is the station saying nothing - and a page that can tell the
 * two apart can say so, instead of repeating a status that was never sent.
 */
export class NoAnswer extends Error {

    readonly reason:  'ran out of time' | 'could not be reached';

    constructor(reason:   'ran out of time' | 'could not be reached',
                message:  string) {

        super(message);

        this.name    = 'NoAnswer';
        this.reason  = reason;

    }

}


/**
 * How long the web interface waits for the station to answer about itself.
 *
 * Measured against a station that had gone quiet rather than away - the case
 * a refused connection does not cover, and the one a car park's network
 * actually produces: 98 seconds after Save, the request was still open, both
 * buttons of the form were still greyed out, and the page said nothing at all.
 * Seven pages clicked through in that state left nine requests hanging, more
 * than the browser will even keep connections open for.
 *
 * Fifteen seconds is four orders of magnitude more than this station needs:
 * every read and write of its own configuration measured between 1 and 7
 * milliseconds. That is the point. The deadline is here to notice silence and
 * not slowness, so it can be generous enough that a slow link never trips it.
 */
export const answerWithin = 15_000;

/**
 * And how long for the station to do something and then answer.
 *
 * Longer, because a write is a file and - for the EVSEs - the OCPP nodes being
 * rebuilt from it, and because giving up on a write is the worse mistake of
 * the two to make: the station may have carried it out and only been slow to
 * say so.
 */
export const actWithin = 30_000;

/**
 * How long a question the station has to put to somebody else may take: the
 * timeouts of the steps it takes one after another, added up, and the usual
 * allowance on top - so that what the page gives up on is silence from the
 * station rather than patience it was told to have.
 */
export function afterAsking(Timeouts: number[]): number {
    return Timeouts.reduce((total, seconds) => total + seconds * 1000, 0) + answerWithin;
}


let unauthorizedHandler: (() => void) | null = null;

/** Called whenever the API answers 401, i.e. the session is gone. */
export function onUnauthorized(handler: () => void): void {
    unauthorizedHandler = handler;
}


/**
 * One request to the station, with a deadline.
 *
 * The deadline covers reading the body as well as opening the connection: a
 * station that sends its headers and then stops mid-answer hangs exactly as
 * thoroughly as one that never starts.
 *
 * Exported so that the tests can drive it at a deadline short enough to be a
 * test; everything the pages do goes through `api` below.
 */
/**
 * Sign in at the HTTPExt API and answer with who is now signed in.
 *
 * Two requests rather than one, and that is not a detour. The HTTPExt API is
 * the only place that can check a password - the store it reads is private to
 * it - but it answers in its own shape and knows nothing of this station's
 * roles. So it sets the session cookie, and "me" is asked afterwards for the
 * roles and permissions this frontend actually works from.
 *
 * Form-urlencoded because that is what its sign-in route accepts, and the
 * field is called "login" rather than "username".
 */
async function signIn(username: string, password: string): Promise<Me> {

    const giveUp = new AbortController();
    const timer  = setTimeout(() => giveUp.abort(), actWithin);

    let response: Response;

    try
    {
        response = await fetch(config.extBase + '/login', {
                             method:       'POST',
                             headers:      {
                                               'Content-Type':  'application/x-www-form-urlencoded',
                                               'Accept':        'application/json'
                                           },
                             credentials:  'same-origin',
                             signal:       giveUp.signal,
                             body:         new URLSearchParams({ login: username, password }).toString()
                         });
    }
    catch (problem)
    {
        throw nothingCameBack(problem, 'POST', actWithin, giveUp.signal.aborted);
    }
    finally
    {
        clearTimeout(timer);
    }

    if (!response.ok) {

        // Its refusals carry a "description"; ours carry an "error". Both are
        // shown to somebody who just typed a password, so both are read.
        let message = `${response.status} ${response.statusText}`;

        try {
            const json = JSON.parse(await response.text());
            if (typeof json === 'object' && json !== null) {
                if      ('description' in json && typeof json.description === 'string')  message = json.description;
                else if ('error'       in json && typeof json.error       === 'string')  message = json.error;
            }
        }
        catch { /* the status line says enough */ }

        throw new ApiError(response.status, message, null);

    }

    return request<Me>('GET', '/auth/me');

}


export async function request<T>(method:  string,
                                 path:    string,
                                 body?:   unknown,
                                 within:  number = method === 'GET' ? answerWithin : actWithin): Promise<T> {

    const headers: Record<string, string> = { 'Accept': 'application/json' };

    if (body !== undefined)
        headers['Content-Type'] = 'application/json';

    const giveUp = new AbortController();
    const timer  = setTimeout(() => giveUp.abort(), within);

    let response:  Response;
    let text:      string;

    try
    {

        // Same origin, so the session cookie travels with every request.
        response = await fetch(config.apiBase + path, {
                             method,
                             headers,
                             credentials: 'same-origin',
                             signal:      giveUp.signal,
                             body:        body !== undefined ? JSON.stringify(body) : undefined
                         });

        if (response.status === 401)
            unauthorizedHandler?.();

        if (response.status === 204) {
            // Nothing to read, but reading it lets the browser finish the
            // request cleanly instead of aborting an unconsumed body.
            await response.arrayBuffer();
            return undefined as T;
        }

        text = await response.text();

    }
    catch (problem)
    {
        throw nothingCameBack(problem, method, within, giveUp.signal.aborted);
    }
    finally
    {
        clearTimeout(timer);
    }

    let json: unknown = null;

    try {
        json = text.length > 0 ? JSON.parse(text) : null;
    }
    catch {
        if (response.ok)
            throw new ApiError(response.status, `Invalid JSON in the response of ${method} ${path}`, text);
    }

    if (!response.ok) {

        const message = typeof json === 'object' && json !== null && 'error' in json && typeof json.error === 'string'
                            ? json.error
                            : `${response.status} ${response.statusText}`;

        throw new ApiError(response.status, message, json);

    }

    return json as T;

}


/**
 * What to say when nothing came back, in words somebody can act on.
 *
 * A read that runs out of time changed nothing, and can be told so. A write
 * that runs out of time is the honest awkward case: the page stopped waiting,
 * but the station may well have done the thing and been slow to say so, and
 * telling somebody that it did not work would invite them to do it twice. So
 * it says what is actually known - that the waiting stopped - and where to
 * look for the rest.
 */
function nothingCameBack(Problem:  unknown,
                         Method:   string,
                         Within:   number,
                         GaveUp:   boolean): unknown {

    const seconds = Math.round(Within / 1000);

    if (GaveUp)
        return new NoAnswer(
                   'ran out of time',
                   Method === 'GET'
                       ? `The station did not answer within ${seconds} seconds. ` +
                         'It may be busy, restarting, or no longer reachable from here.'
                       : `The station did not answer within ${seconds} seconds, so this page ` +
                         'stopped waiting. It may still have carried this out - reload to see ' +
                         'what it now says.'
               );

    // The browser's own word for this is "Failed to fetch", which on a page
    // about a charging station names neither the station nor what to do next.
    if (Problem instanceof TypeError)
        return new NoAnswer(
                   'could not be reached',
                   'The station could not be reached. It may be switched off, restarting, ' +
                   'or on the other side of a network that is down.'
               );

    return Problem;

}


export const api = {

    /** The Server-Sent Events stream; the browser sends the session cookie along. */
    eventsURL: `${config.apiBase}/events`,

    auth: {
        me:      ()                                    => request<Me>  ('GET',  '/auth/me'),
        login:   signIn,
        logout:  ()                                    => request<void>('POST', '/auth/logout')
    },

    status:         () => request<Status>       ('GET', '/status'),
    configuration:  () => request<Configuration>('GET', '/configuration'),

    dns: {
        get:   ()                    => request<DNSConfiguration>('GET', '/configuration/dns'),
        /** Only the fields given are changed; the answer is the whole configuration as it now stands. */
        save:  (update: DNSUpdate)   => request<DNSConfiguration>('PUT', '/configuration/dns', update),
        /**
         * Make the station look a name up. A POST because it sends traffic.
         *
         * @param seconds  how long the name servers asked may take - see
         *                 pages/dnsServers.ts.
         * @param server   which configured name server to ask, by its place in
         *                 the list - or undefined to resolve the way the
         *                 station resolves anything else, asking all of them
         *                 at once.
         */
        query: (name: string, recordTypes: string[], seconds: number, server?: number) =>
                   request<DNSQueryResult>('POST', '/configuration/dns/query', { name, recordTypes, server },
                                           afterAsking([ seconds ]))
    },

    nts: {
        get:   ()                    => request<NTSConfiguration>('GET', '/configuration/nts'),
        save:  (update: NTSUpdate)   => request<NTSConfiguration>('PUT', '/configuration/nts', update),
        /**
         * Ask one time server everything: the name, the key exchange, the
         * authenticated NTP request, each one written down as it happens.
         *
         * @param timeoutSeconds  what the station allows each of the two steps.
         * @param host            which server, on the ports it is configured
         *                        with, or undefined for the configured one.
         */
        test:  (timeoutSeconds: number, host?: string) => request<TimeServerTest>(
                                               'POST', '/configuration/nts/test', { host },
                                               afterAsking([timeoutSeconds, timeoutSeconds])),
        /**
         * Ask every server of the group, with every step in the log - two steps
         * over the network per server, so two of the station's own timeouts
         * before the page stops believing in it.
         *
         * @param timeoutSeconds  what the station allows each of the two steps.
         */
        sync:  (timeoutSeconds: number) => request<NTSConfiguration>(
                                               'POST', '/configuration/nts/sync', {},
                                               afterAsking([timeoutSeconds, timeoutSeconds])
                                           )
    },

    display: {
        get:   ()                              => request<DisplayConfiguration>('GET', '/configuration/display'),
        // The whole section at once, because its fields are not independent -
        // and an empty object is how dimming is turned off.
        save:  (update: Partial<DisplayConfiguration>) => request<DisplayConfiguration>('PUT', '/configuration/display', update)
    },

    v2g: {
        get:   ()                    => request<V2GConfiguration>('GET', '/configuration/v2g'),
        /**
         * Takes the link down and brings it up again, so it answers later than
         * the other configuration calls do - and a vehicle in the middle of a
         * SLAC match goes down with it.
         */
        save:  (update: V2GUpdate)   => request<V2GConfiguration>('PUT', '/configuration/v2g', update, 30_000)
    },

    power: {
        get:   ()                      => request<PowerConfiguration>('GET', '/configuration/power'),
        /** null takes the limit away rather than setting it to nothing. */
        save:  (update: PowerUpdate)   => request<PowerConfiguration>('PUT', '/configuration/power', update)
    },

    evses: {
        get:   ()               => request<EVSEConfiguration>('GET', '/configuration/evses'),
        /**
         * All of them at once: they are only valid together.
         *
         * Which permission this needs depends on what actually changed, and the
         * station works that out by comparing what it is sent with what it has
         * - so a 403 here can arrive for a request that a moment ago would have
         * gone through.
         */
        save:  (evses: EVSE[])  => request<EVSEConfiguration>('PUT', '/configuration/evses', { evses })
    },

    rfid: {
        get:   ()                        => request<RFIDConfiguration>('GET', '/configuration/rfid'),
        /**
         * All of them at once. Which permission this needs depends on what
         * changed - moving a reader is not the same statement as switching one
         * off - and the station works that out by comparing.
         */
        save:  (readers: RFIDReader[])   => request<RFIDConfiguration>('PUT', '/configuration/rfid', { readers })
    },

    authentications: {

        get:     ()                  => request<StationConnections>('GET', '/configuration/authentications'),

        add:     (entry: LoginToSave) =>
                     request<{ id: string; connections: StationConnections }>(
                         'POST', '/configuration/authentications', entry),

        /** A secret left empty keeps the one already there. */
        update:  (entry: LoginToSave) =>
                     request<StationConnections>('POST', '/configuration/authentications/update', entry),

        /** Refused while a connection is using it, and the refusal names it. */
        remove:  (id: string) =>
                     request<StationConnections>('POST', '/configuration/authentications/remove', { id })

    },

    connections: {

        get:     ()                       => request<StationConnections>('GET', '/configuration/connections'),

        add:     (entry: ConnectionToSave) =>
                     request<{ id: string; connections: StationConnections }>(
                         'POST', '/configuration/connections', entry),

        update:  (entry: ConnectionToSave) =>
                     request<StationConnections>('POST', '/configuration/connections/update', entry),

        remove:  (id: string) =>
                     request<StationConnections>('POST', '/configuration/connections/remove', { id }),

        /**
         * Make this connection once, and say everything that happened.
         *
         * Takes the fields rather than an identification, because the page
         * offers this beside a connection being written down for the first
         * time as well as beside one that exists - and both times what
         * somebody means is "test what is on the screen". Nothing is stored.
         *
         * Slower than the other writes on purpose: the station stays connected
         * for about two seconds to see whether anything is said, so the
         * deadline has to cover the connection plus that.
         */
        test:    (entry: ConnectionToSave) =>
                     request<ConnectionTest>('POST', '/configuration/connections/test', entry,
                                             afterAsking([ 2 ]))

    },

    certificates: {

        get:     ()  => request<StationCertificates>('GET', '/configuration/certificates'),

        /**
         * A new key and the signing request to be handed to whoever issues
         * certificates for this station.
         *
         * Slower than the other writes by a wide margin - an RSA 4096 or an
         * SLH-DSA key takes seconds to generate, and the deadline for a write
         * covers it.
         */
        create:  (subject: string, algorithm: string) =>
                     request<{ id: string; csr: string; certificates: StationCertificates }>(
                         'POST', '/configuration/certificates', { subject, algorithm }),

        /** The certificate that came back, and whatever intermediates came with it. */
        add:     (pem: string) =>
                     request<{ id: string; warnings: string[]; certificates: StationCertificates }>(
                         'POST', '/configuration/certificates/import', { pem }),

        /** The identification travels in the body, as it does everywhere else in this API. */
        remove:  (id: string) =>
                     request<StationCertificates>('POST', '/configuration/certificates/remove', { id })

    },

    calibration: {
        get:   ()                                             => request<CalibrationConfiguration>('GET', '/configuration/calibration'),
        /** All of them at once: what a station is certified for is one statement. */
        save:  (certificates: CalibrationCertificateUpdate[]) => request<CalibrationConfiguration>('PUT', '/configuration/calibration', { certificates })
    },

    /**
     * A page of the log, oldest of the returned entries first.
     *
     * @param limit  at most this many entries
     * @param after  only what is newer than this id
     * @param tag    only entries carrying this tag - a level counting as one
     */
    logs: (limit?: number, after?: number, tag?: string) => {

        const query = new URLSearchParams();

        if (limit !== undefined)  query.set('limit', String(limit));
        if (after !== undefined)  query.set('after', String(after));
        if (tag)                  query.set('tag',   tag);

        const suffix = query.size > 0 ? `?${query}` : '';

        return request<LogPage>('GET', `/logs${suffix}`);

    }

};
