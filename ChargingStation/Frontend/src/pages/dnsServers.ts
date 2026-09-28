import type { DNSConfiguration, DNSServer, DNSServerEntry } from '../api/client';
import { pinsIn, withAsShown, withPins } from './pins';

/**
 * How long the DNS page waits for the station to look something up, and what
 * it tells the station about a name server.
 *
 * Apart from the page, because these are the parts that decide when the page
 * stops believing in the station and what the station is told - and the first
 * was wrong in a way nobody sees until a name server does not answer: it added
 * the servers' timeouts up, as if the station asked them one after another,
 * when it asks all of them at once.
 */


/**
 * Whether a name server asked over this transport shows a certificate: over
 * TLS and over HTTPS, in all its forms, and over nothing else.
 */
export function isEncrypted(transport: string): boolean {

    const name = transport.toUpperCase();

    return name === 'TLS' || name.startsWith('HTTPS');

}


/**
 * A name server as the station is told it: what its configuration keeps, in
 * the order the file keeps it, and none of what the station only says about it
 * - what was made of its certificate, what it was last believed with.
 *
 * What it is held to only where it is asked over TLS or HTTPS. The station
 * refuses a pin on a server that shows no certificate, rightly: somebody would
 * believe it held to something it is never compared with. So a server switched
 * to UDP lets go of its pins when it is saved - which the page says before -
 * and keeps them in the draft until then, for whoever switches it back.
 */
export function entryOf(server: DNSServerEntry): DNSServerEntry {

    const entry: DNSServerEntry = {
        address:              server.address,
        port:                 server.port,
        transport:            server.transport,
        queryTimeoutSeconds:  server.queryTimeoutSeconds
    };

    return isEncrypted(server.transport)
               ? withPins(entry, pinsIn(server))
               : entry;

}


/**
 * A name server as the page sends it: its entry, and - for a server the page
 * loaded - what the page showed it held to, so that the station changes only
 * what was changed on the page and keeps a root the server learned while the
 * page was open; see asShown.
 *
 * Not for a server added on the page, which the station has nothing of, and
 * not in what the page compares to say whether anything was changed: what a
 * server was shown held to is never a change.
 */
export function sentOf(server: DNSServer): DNSServerEntry {

    return server.heldTo === undefined
               ? entryOf(server)
               : withAsShown(entryOf(server), server.heldTo);

}


/**
 * The longest one name server can honestly take, in seconds.
 *
 * Its own timeout where it has one and the client's where it has not, times
 * the number of attempts - and the attempts are the part that is easy to
 * forget: measured against a name server that does not answer at all, a query
 * with a ten second timeout came back after twenty, because the station tries
 * again. A deadline of ten would have given up on a station that was still
 * doing what it was told.
 *
 * @param configuration  what the station said about its name resolution.
 * @param index          the server's place in the list.
 */
export function oneServerTakes(configuration: DNSConfiguration | null, index: number): number {

    const timeout = configuration?.servers[index]?.queryTimeoutSeconds ??
                    configuration?.settings.queryTimeoutSeconds ?? 0;

    return timeout * ((configuration?.settings.maxRetries ?? 0) + 1);

}


/**
 * The longest all of them can honestly take, in seconds: the longest any one
 * of them can.
 *
 * Not their sum. The station asks every server at once and takes the first
 * usable answer, so a lookup that nobody answers ends when the slowest of them
 * gives up - measured on a WWCP node, two name servers that never answer, at
 * three seconds each, took 3.0 seconds, and not 6.
 *
 * @param configuration  what the station said about its name resolution.
 */
export function allServersTake(configuration: DNSConfiguration | null): number {
    return Math.max(0, ...(configuration?.servers ?? []).map((_, index) => oneServerTakes(configuration, index)));
}
