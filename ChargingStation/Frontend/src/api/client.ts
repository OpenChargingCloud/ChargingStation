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
    session:      { createdAt: string; expiresAt: string };
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


/** What may be changed about the time client while the station runs. */
export interface NTSUpdate {
    enabled?:         boolean;
    hostname?:        string;
    ntsKEPort?:       number;
    ntpPort?:         number;
    timeoutSeconds?:  number;
}

/** How one synchronisation went, step by step. */
export interface NTSSyncResult {
    ok:           boolean;
    server:       string;
    at:           string;
    error?:       string;
    step?:        string;
    runtime_ms?:  number;
    ntske?:       Record<string, unknown>;
    ntp?:         Record<string, unknown>;
}

/** Where this station gets the time from, and how its key exchange is doing. */
export interface NTSConfiguration {
    enabled:   boolean;
    server:    { hostname: string; ntsKEPort: number; ntpPort: number } & Record<string, unknown>;
    settings:  { timeoutSeconds: number | null };
    cookies: {
        available:     number;
        maxPoolSize:   number;
        lowWatermark:  number;
        seeded:        number;
        received:      number;
        consumed:      number;
        dropped:       number;
        isLow:         boolean;
        isEmpty:       boolean;
        isFull:        boolean;
    };
    policy:  Record<string, unknown>;
    keyExchange: {
        automatic:                 number;
        aeadAlgorithms:            string[];
        compliantExporterContext:  boolean;
        lastExchange:              { error: string | null; warnings: string[]; servers: string[] } | null;
    };
    lastSync:  NTSSyncResult | null;
    limits:    { maxTimeout: number };
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

/** What a certificate looks like on the way in: the rest is read out of the PEM. */
export interface CalibrationCertificateUpdate {
    id:            string;
    description?:  string | null;
    pem:           string;
}


export class ApiError extends Error {

    constructor(public readonly status:  number,
                message:                 string,
                public readonly body?:   unknown) {
        super(message);
        this.name = 'ApiError';
    }

    get isUnauthorized(): boolean {
        return this.status === 401;
    }

}


let unauthorizedHandler: (() => void) | null = null;

/** Called whenever the API answers 401, i.e. the session is gone. */
export function onUnauthorized(handler: () => void): void {
    unauthorizedHandler = handler;
}


async function request<T>(method: string, path: string, body?: unknown): Promise<T> {

    const headers: Record<string, string> = { 'Accept': 'application/json' };

    if (body !== undefined)
        headers['Content-Type'] = 'application/json';

    // Same origin, so the session cookie travels with every request.
    const response = await fetch(config.apiBase + path, {
                               method,
                               headers,
                               credentials: 'same-origin',
                               body: body !== undefined ? JSON.stringify(body) : undefined
                           });

    if (response.status === 401)
        unauthorizedHandler?.();

    if (response.status === 204) {
        // Nothing to read, but reading it lets the browser finish the request
        // cleanly instead of aborting an unconsumed body.
        await response.arrayBuffer();
        return undefined as T;
    }

    const text = await response.text();
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


export const api = {

    /** The Server-Sent Events stream; the browser sends the session cookie along. */
    eventsURL: `${config.apiBase}/events`,

    auth: {
        me:      ()                                    => request<Me>  ('GET',  '/auth/me'),
        login:   (username: string, password: string)  => request<Me>  ('POST', '/auth/login', { username, password }),
        logout:  ()                                    => request<void>('POST', '/auth/logout')
    },

    status:         () => request<Status>       ('GET', '/status'),
    configuration:  () => request<Configuration>('GET', '/configuration'),

    dns: {
        get:   ()                    => request<DNSConfiguration>('GET', '/configuration/dns'),
        /** Only the fields given are changed; the answer is the whole configuration as it now stands. */
        save:  (update: DNSUpdate)   => request<DNSConfiguration>('PUT', '/configuration/dns', update),
        /** Make the station look a name up. A POST because it sends traffic. */
        query: (name: string, recordTypes: string[]) =>
                   request<DNSQueryResult>('POST', '/configuration/dns/query', { name, recordTypes })
    },

    nts: {
        get:   ()                    => request<NTSConfiguration>('GET', '/configuration/nts'),
        save:  (update: NTSUpdate)   => request<NTSConfiguration>('PUT', '/configuration/nts', update),
        /** One key exchange and one authenticated NTP request, with every step in the log. */
        sync:  ()                    => request<NTSConfiguration>('POST', '/configuration/nts/sync', {})
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
