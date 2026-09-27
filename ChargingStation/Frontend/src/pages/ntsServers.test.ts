/**
 * What the NTS page tells the station when a time server is added, edited or
 * deleted, asked directly.
 *
 * Run with `npm test`. The station is sent the whole list every time, so what
 * is pinned here is that the list sent is the list shown with exactly one
 * change in it - and that it reads the way the configuration file would.
 */

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

import type { NTSHeldTo, NTSTimeSource } from '../api/client';
import { editedEntry, entryOf, nameTaken, readable, withServer, withoutServer } from './ntsServers.ts';


const usual = { ntsKE: 4460, ntp: 123 };

/** A server as the station shows it. */
const shown = (hostname: string, more: Partial<NTSTimeSource> = {}): NTSTimeSource =>
    ({ hostname, priority: 0, ntsKEPort: 4460, ntpPort: 123, enabled: true, ...more });


describe('a time server turned back into its entry', () => {

    it('is a bare name when everything else is the usual', () => {

        assert.deepEqual(entryOf(shown('ptbtime1.ptb.de.'), usual),
                         { hostname: 'ptbtime1.ptb.de' });

    });

    it('says what is not the usual, and nothing that is', () => {

        assert.deepEqual(entryOf(shown('time.local.', { priority: 9, ntsKEPort: 4461, enabled: false }), usual),
                         { hostname: 'time.local', priority: 9, ntsKEPort: 4461, enabled: false });

    });

});


describe('what a time server is held to, turned back into its entry', () => {

    const A = 'A'.repeat(64);
    const B = 'B'.repeat(64);
    const R = 'C'.repeat(64);

    /** What the station says a server is held to, the way it says it. */
    const held = (more: Partial<NTSHeldTo>): NTSHeldTo =>
        ({ certificate: null, root: null, certificates: [], roots: [], onMismatch: 'refuse', trustOnFirstUse: 'none', ...more });

    it('is the pin the file had, told to the station again', () => {

        assert.deepEqual(entryOf(shown('ptbtime1.ptb.de.', { heldTo: held({ certificate: A, certificates: [ A ] }) }), usual),
                         { hostname: 'ptbtime1.ptb.de', certificateFingerprint: A });

    });

    it('is a list where there are several of a kind, the way the file writes them', () => {

        assert.deepEqual(entryOf(shown('ptbtime1.ptb.de.', { heldTo: held({ certificate: A, certificates: [ A, B ], root: R, roots: [ R ] }) }), usual),
                         { hostname: 'ptbtime1.ptb.de', certificateFingerprints: [ A, B ], rootFingerprint: R });

    });

    it('keeps what is to be learned on first use, and what a mismatch comes to', () => {

        assert.deepEqual(entryOf(shown('ptbtime1.ptb.de.', { heldTo: held({ onMismatch: 'record', trustOnFirstUse: 'root' }) }), usual),
                         { hostname: 'ptbtime1.ptb.de', onMismatch: 'record', trustOnFirstUse: 'root' });

    });

    it('keeps a pin that was learned, and that it is learned', () => {

        assert.deepEqual(entryOf(shown('ptbtime1.ptb.de.', { heldTo: held({ root: R, roots: [ R ], trustOnFirstUse: 'root' }) }), usual),
                         { hostname: 'ptbtime1.ptb.de', rootFingerprint: R, trustOnFirstUse: 'root' });

    });

    it('reads a station that names only the first pin of each kind', () => {

        // What a station said before a server could be held to several.
        assert.deepEqual(entryOf(shown('ptbtime1.ptb.de.', { heldTo: { certificate: A, root: R } }), usual),
                         { hostname: 'ptbtime1.ptb.de', certificateFingerprint: A, rootFingerprint: R });

    });

    it('leaves a server held to nothing a bare name', () => {

        assert.deepEqual(entryOf(shown('ptbtime1.ptb.de.', { heldTo: null }), usual),
                         { hostname: 'ptbtime1.ptb.de' });

    });

    it('survives a change to another server of the list, which is sent whole', () => {

        const list = [ shown('a.example.', { heldTo: held({ certificate: A, certificates: [ A ] }) }),
                       shown('b.example.') ].map(source => entryOf(source, usual));

        assert.deepEqual(withServer(list, 1, { hostname: 'b.example', priority: 3 }),
                         [ { hostname: 'a.example', certificateFingerprint: A }, { hostname: 'b.example', priority: 3 } ]);

    });

});


describe('a time server as the dialog saves it', () => {

    const A = 'A'.repeat(64);
    const R = 'C'.repeat(64);

    /** A server held to a certificate and a root, set to learn its root, recorded when it does not match. */
    const pinned = { hostname: 'ptbtime1.ptb.de', priority: 2, certificateFingerprint: A, rootFingerprint: R,
                     onMismatch: 'record' as const, trustOnFirstUse: 'root' as const };

    it('keeps what the server is held to when only something else about it was changed', () => {

        assert.deepEqual(editedEntry(pinned, { hostname: 'ptbtime1.ptb.de', priority: 5 }),
                         { hostname: 'ptbtime1.ptb.de', priority: 5, certificateFingerprint: A, rootFingerprint: R,
                           onMismatch: 'record', trustOnFirstUse: 'root' });

    });

    it('takes a name in other letters for the same server', () => {

        assert.deepEqual(editedEntry(pinned, { hostname: 'PTBtime1.ptb.de' }),
                         { hostname: 'PTBtime1.ptb.de', certificateFingerprint: A, rootFingerprint: R,
                           onMismatch: 'record', trustOnFirstUse: 'root' });

    });

    it('holds a server given another name to none of the old one\'s fingerprints, and lets it learn its own', () => {

        assert.deepEqual(editedEntry(pinned, { hostname: 'ptbtime2.ptb.de' }),
                         { hostname: 'ptbtime2.ptb.de', onMismatch: 'record', trustOnFirstUse: 'root' });

    });

    it('holds a server given another name to nothing, where the old one learned nothing', () => {

        assert.deepEqual(editedEntry({ hostname: 'ptbtime1.ptb.de', certificateFingerprint: A, onMismatch: 'accept' },
                                     { hostname: 'ptbtime2.ptb.de' }),
                         { hostname: 'ptbtime2.ptb.de' });

    });

    it('is what was typed for a new server', () => {

        assert.deepEqual(editedEntry(null, { hostname: 'ptbtime3.ptb.de', enabled: false }),
                         { hostname: 'ptbtime3.ptb.de', enabled: false });

    });

});


describe('the list a change sends', () => {

    const list = [ { hostname: 'a.example' }, { hostname: 'b.example' }, { hostname: 'c.example' } ];

    it('replaces exactly the one that was edited', () => {

        assert.deepEqual(withServer(list, 1, { hostname: 'b.example', enabled: false }),
                         [ { hostname: 'a.example' }, { hostname: 'b.example', enabled: false }, { hostname: 'c.example' } ]);

    });

    it('adds a new one at the end', () => {

        assert.deepEqual(withServer(list, null, { hostname: 'd.example' }).map(entry => entry.hostname),
                         [ 'a.example', 'b.example', 'c.example', 'd.example' ]);

    });

    it('leaves out exactly the one that was deleted', () => {

        assert.deepEqual(withoutServer(list, 0).map(entry => entry.hostname),
                         [ 'b.example', 'c.example' ]);

    });

    it('leaves the list it was made from alone, which is what the page goes back to when the station says no', () => {

        withServer(list, 0, { hostname: 'x.example' });
        withoutServer(list, 2);

        assert.deepEqual(list.map(entry => entry.hostname), [ 'a.example', 'b.example', 'c.example' ]);

    });

});


describe('a name that is already taken', () => {

    const list = [ { hostname: 'ptbtime1.ptb.de' }, { hostname: 'ptbtime2.ptb.de' } ];

    it('is taken whatever its case and its root dot', () => {

        assert.equal(nameTaken(list, 'PTBTIME2.ptb.de.', null), true);

    });

    it('is not taken by the server being edited itself, or it could not be saved unchanged', () => {

        assert.equal(nameTaken(list, 'ptbtime2.ptb.de', 1), false);

    });

    it('is read without the root dot', () => {

        assert.equal(readable('ptbtime1.ptb.de.'), 'ptbtime1.ptb.de');
        assert.equal(readable('ptbtime1.ptb.de'),  'ptbtime1.ptb.de');

    });

});
