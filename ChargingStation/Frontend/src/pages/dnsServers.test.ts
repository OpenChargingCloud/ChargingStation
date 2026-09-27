/**
 * How long the DNS page waits for the station to look something up, asked
 * directly.
 *
 * Run with `npm test`. What is pinned is what was wrong: the page added the
 * servers' timeouts up, as if the station asked them one after another, when
 * it asks all of them at once - and it is what a server asked again takes
 * that the page has to be willing to wait for, not what it takes once.
 */

import { strict as assert }  from 'node:assert';
import { registerHooks }     from 'node:module';
import { describe, it }      from 'node:test';

import type { DNSConfiguration, DNSServer } from '../api/client';

// The pages are written for webpack, which does not want the extension in a
// relative import; Node does. One hook puts it back for whatever this test
// loads - an entry reads what a server is held to from pins.ts.
registerHooks({
    resolve(specifier, context, next) {
        return specifier.startsWith('.') && !specifier.endsWith('.ts')
                   ? next(`${specifier}.ts`, context)
                   : next(specifier, context);
    }
});

const { allServersTake, entryOf, oneServerTakes } = await import('./dnsServers.ts');


/** A name server as the station shows it, with a timeout of its own or none. */
const server = (queryTimeoutSeconds: number | null = null): DNSServer =>
    ({ address: '192.0.2.1', port: 53, transport: 'UDP', queryTimeoutSeconds });

/** What the station says about its name resolution, with these servers. */
const configuration = (servers: DNSServer[], queryTimeoutSeconds = 10, maxRetries = 1): DNSConfiguration =>
    ({
        enabled:   true,
        servers,
        settings:  { queryTimeoutSeconds, recursionDesired: null, useCache: true, dnssecOK: false,
                     followCNAMEs: true, maxCNAMEFollows: 8, maxRetries },
        fixed:     {},
        limits:    { maxServers: 16, maxQueryTimeout: 120, transports: [ 'UDP' ], recordTypes: [ 'A' ] },
        file:      'configuration.json'
    });


describe('one name server', () => {

    it('takes its own timeout for every time it is asked', () => {

        assert.equal(oneServerTakes(configuration([ server(3) ], 10, 1), 0), 6);
        assert.equal(oneServerTakes(configuration([ server(3) ], 10, 0), 0), 3);

    });

    it('and the client\'s where it has none of its own', () => {

        assert.equal(oneServerTakes(configuration([ server() ], 10, 1), 0), 20);

    });

});


describe('all of them', () => {

    it('take as long as the slowest of them and not their sum, because they are asked at once', () => {

        // One after another, these three would take 16 seconds.
        assert.equal(allServersTake(configuration([ server(3), server(3), server(10) ], 5, 0)), 10);

    });

    it('each for every time it is asked', () => {

        assert.equal(allServersTake(configuration([ server(3), server() ], 5, 1)), 10);

    });

    it('take nothing when there are none', () => {

        assert.equal(allServersTake(configuration([])), 0);
        assert.equal(allServersTake(null),              0);

    });

});


describe('a name server as the station is told it when the list is saved', () => {

    const A = 'A'.repeat(64);
    const R = 'C'.repeat(64);

    /** A name server over TLS held to a certificate and a root, as the station shows it. */
    const pinned = (): DNSServer => ({
        address: '192.0.2.53', port: 853, transport: 'TLS', queryTimeoutSeconds: null,
        certificateFingerprint: A, rootFingerprint: R, onMismatch: 'record', trustOnFirstUse: 'root',
        heldTo:    { certificate: A, root: R, certificates: [ A ], roots: [ R ], onMismatch: 'record', trustOnFirstUse: 'root' },
        judgement: { outcome: 'believed' },
        known:     { certificate: A, root: R, since: '2026-09-27T08:00:00Z' }
    });

    it('keeps what it is held to, and none of what the station only says about it', () => {

        assert.deepEqual(entryOf(pinned(), '192.0.2.53'),
                         { address: '192.0.2.53', port: 853, transport: 'TLS', queryTimeoutSeconds: null,
                           certificateFingerprint: A, rootFingerprint: R, onMismatch: 'record', trustOnFirstUse: 'root' });

    });

    it('lets go of its pins once it is asked over UDP, which shows no certificate, and the station would refuse the list', () => {

        assert.deepEqual(entryOf({ ...pinned(), transport: 'UDP', port: 53 }, '192.0.2.53'),
                         { address: '192.0.2.53', port: 53, transport: 'UDP', queryTimeoutSeconds: null });

    });

    it('keeps them over every kind of HTTPS', () => {

        for (const transport of [ 'HTTPS', 'HTTPS_Binary', 'HTTPS_JSON', 'HTTPS_GET' ])
            assert.equal(entryOf({ ...pinned(), transport }, '192.0.2.53').certificateFingerprint, A, transport);

    });

    it('holds a server given another address to none of the old one\'s fingerprints, and lets it learn its own', () => {

        assert.deepEqual(entryOf({ ...pinned(), address: '192.0.2.54' }, '192.0.2.53'),
                         { address: '192.0.2.54', port: 853, transport: 'TLS', queryTimeoutSeconds: null,
                           onMismatch: 'record', trustOnFirstUse: 'root' });

    });

    it('takes a name in other letters, or with the root dot, for the same server', () => {

        assert.equal(entryOf({ ...pinned(), address: 'DNS.Example.' }, 'dns.example').certificateFingerprint, A);

    });

    it('is what was typed for a server added on the page', () => {

        assert.deepEqual(entryOf({ address: '192.0.2.99', port: 853, transport: 'TLS', queryTimeoutSeconds: 5 }, null),
                         { address: '192.0.2.99', port: 853, transport: 'TLS', queryTimeoutSeconds: 5 });

    });

});
