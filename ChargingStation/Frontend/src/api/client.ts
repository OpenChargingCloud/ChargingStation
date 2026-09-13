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
