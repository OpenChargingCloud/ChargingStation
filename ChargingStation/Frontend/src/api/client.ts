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

/** Who is signed in to the web interface. */
export interface Me {
    username:  string;
    session:   { createdAt: string; expiresAt: string };
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


/** One name server this station may ask. */
export interface DNSServer {
    address:       string | null;
    domainName:    string | null;
    port:          number;
    transport:     string;
    queryTimeout:  string | null;
}

/** What may be changed about the name resolution while the station runs. */
export interface DNSSettings {
    /** null leaves it to the server's own default. */
    recursionDesired:  boolean | null;
    useCache:          boolean;
    dnssecOK:          boolean;
    followCNAMEs:      boolean;
    maxCNAMEFollows:   number;
    maxRetries:        number;
}

/** How this station resolves names: what was decided at construction, and what still can be. */
export interface DNSConfiguration {
    servers:         DNSServer[];
    queryTimeout:    string;
    udpPayloadSize:  number;
    ednsOptions:     number;
    clientSubnet:    string | null;
    settings:        DNSSettings;
    cache:           { cleanUpEvery: string; negativeCacheTTL: string };
}


/** What may be changed about the time client while the station runs. */
export interface NTSSettings {
    /** null waits for an answer without a timeout. */
    timeoutSeconds: number | null;
}

/** Where this station gets the time from, and how its key exchange is doing. */
export interface NTSConfiguration {
    server:    Record<string, unknown>;
    settings:  NTSSettings;
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
}


/** One place a vehicle can be plugged into this charging station. */
export interface EVSE {
    /** Which one it is, counting from 1 as OCPP does. */
    id:                 number;
    /** What can be plugged into it, in OCPP 2.1's vocabulary. */
    connectorTypes:     string[];
    maxPower_kW:        number;
    operative:          boolean;
    /** What is written on the housing, e.g. "A". */
    physicalReference:  string | null;
    meterType:          string | null;
    meterSerialNumber:  string | null;
}

/** The EVSEs of this station: what is saved, what is running, and what may be picked. */
export interface EVSEConfiguration {
    /** What the file says - what the station will have at the next start. */
    evses:            EVSE[];
    /** What the OCPP nodes were built with at the last start. */
    running:          EVSE[];
    /** Whether those two differ, i.e. whether a restart is owed. */
    restartRequired:  boolean;
    file:             string;
    maxEVSEs:         number;
    maxPower_kW:      number;
    /** Every connector type the OCPP stack knows, for the picker. */
    connectorTypes:   string[];
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
        get:   ()                               => request<DNSConfiguration>('GET', '/configuration/dns'),
        /** Only the fields given are changed; the answer is the whole configuration as it now stands. */
        save:  (settings: Partial<DNSSettings>) => request<DNSConfiguration>('PUT', '/configuration/dns', settings)
    },

    nts: {
        get:   ()                               => request<NTSConfiguration>('GET', '/configuration/nts'),
        save:  (settings: Partial<NTSSettings>) => request<NTSConfiguration>('PUT', '/configuration/nts', settings)
    },

    evses: {
        get:   ()               => request<EVSEConfiguration>('GET', '/configuration/evses'),
        /** All of them at once: they are only valid together. */
        save:  (evses: EVSE[])  => request<EVSEConfiguration>('PUT', '/configuration/evses', { evses })
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
