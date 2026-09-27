import type { DNSConfiguration, DNSServer } from '../api/client';
import { learnedOnly, pinsIn } from './pins';

/**
 * How long the DNS page waits for the station to look something up, and what
 * it tells the station of a name server when the list is saved.
 *
 * Apart from the page, because these are the parts that decide something. The
 * first was wrong in a way nobody sees until a name server does not answer: it
 * added the servers' timeouts up, as if the station asked them one after
 * another, when it asks all of them at once.
 */


/**
 * Whether a name server asked over this transport shows a certificate, and so
 * can be held to one: over TLS, and over HTTPS in all its forms.
 */
export function isEncrypted(transport: string): boolean {

    const name = transport.toUpperCase();

    return name === 'TLS' || name.startsWith('HTTPS');

}


/**
 * A name server as the station is told it when the list is saved: what its
 * entry in the configuration keeps, and none of what the station only says
 * about it - what it is held to as a page reads it, what was made of its
 * certificate, what it was last believed with.
 *
 * What it is held to goes back with it, although the page shows none of it,
 * because the station is told the whole list and keeps what it is sent. But
 * only where it is asked over TLS or HTTPS: the station refuses a pin on a
 * server that shows no certificate, rightly, and would refuse the whole list
 * with it - so a server switched to UDP lets go of its pins when it is saved.
 * And a server given another address is another server: it keeps what its
 * entry was to learn on first use, and none of the old one's fingerprints.
 *
 * @param server         the server as the page has it now.
 * @param loadedAddress  the address it had when the station last said the list, or null for one added since.
 */
export function entryOf(server: DNSServer, loadedAddress: string | null): DNSServer {

    const entry: DNSServer = {
        address:              server.address,
        port:                 server.port,
        transport:            server.transport,
        queryTimeoutSeconds:  server.queryTimeoutSeconds
    };

    if (!isEncrypted(server.transport))
        return entry;

    const pins = pinsIn(server);

    return loadedAddress !== null && sameAddress(loadedAddress, server.address)
               ? { ...entry, ...pins }
               : { ...entry, ...learnedOnly(pins) };

}


/** Whether two addresses name the same server: without case, and without the root's dot. */
function sameAddress(one: string, other: string): boolean {

    const plain = (address: string) => address.trim().replace(/\.$/, '').toLowerCase();

    return plain(one) === plain(other);

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
