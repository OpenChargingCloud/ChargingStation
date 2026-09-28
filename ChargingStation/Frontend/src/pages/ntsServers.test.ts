/**
 * What the NTS page tells the station when a time server is added, edited or
 * deleted, asked directly.
 *
 * Run with `npm test`. The station is sent the whole list every time, so what
 * is pinned here is that the list sent is the list shown with exactly one
 * change in it - and that it reads the way the configuration file would.
 */

import { strict as assert }  from 'node:assert';
import { readFileSync }      from 'node:fs';
import { registerHooks }     from 'node:module';
import { describe, it }      from 'node:test';

import type { NTSTimeSource, ServerPins } from '../api/client';

// The list is written for webpack, which does not want the extension in a
// relative import, and it imports what a server is held to from pins.ts; Node
// wants the extension. One hook puts it back - see the client's test.
registerHooks({
    resolve(specifier, context, next) {
        return specifier.startsWith('.') && !specifier.endsWith('.ts')
                   ? next(`${specifier}.ts`, context)
                   : next(specifier, context);
    }
});

const { entryOf, nameTaken, readable, sentOf, withServer, withoutServer } = await import('./ntsServers.ts');


const usual = { ntsKE: 4460, ntp: 123 };

/** A server as the station shows it. */
const shown = (hostname: string, more: Partial<NTSTimeSource> = {}): NTSTimeSource =>
    ({ hostname, priority: 0, ntsKEPort: 4460, ntpPort: 123, enabled: true, ...more });

/** Two fingerprints, as the station writes them. */
const root         = 'a'.repeat(64);
const certificate  = 'b'.repeat(64);
const renewal      = 'c'.repeat(64);

/** What the station says a server is held to. */
const heldTo = (more: Partial<ServerPins>): ServerPins =>
    ({ certificate: null, root: null, certificates: [], roots: [], onMismatch: 'refuse', trustOnFirstUse: null, ...more });


describe('a time server turned back into its entry', () => {

    it('is a bare name when everything else is the usual', () => {

        assert.deepEqual(entryOf(shown('ptbtime1.ptb.de.'), usual),
                         { hostname: 'ptbtime1.ptb.de' });

    });

    it('says what is not the usual, and nothing that is', () => {

        assert.deepEqual(entryOf(shown('time.local.', { priority: 9, ntsKEPort: 4461, enabled: false }), usual),
                         { hostname: 'time.local', priority: 9, ntsKEPort: 4461, enabled: false });

    });

    it('keeps what it is held to - it went missing from every server whenever any one was saved', () => {

        assert.deepEqual(entryOf(shown('ptbtime1.ptb.de.', { heldTo: heldTo({ root, roots: [ root ], onMismatch: 'record' }) }), usual),
                         { hostname: 'ptbtime1.ptb.de', rootFingerprint: root, onMismatch: 'record' });

    });

    it('keeps a root it learned on first use, and that it learns', () => {

        assert.deepEqual(entryOf(shown('ptbtime1.ptb.de.', { heldTo: heldTo({ root, roots: [ root ], trustOnFirstUse: 'root' }) }), usual),
                         { hostname: 'ptbtime1.ptb.de', rootFingerprint: root, trustOnFirstUse: 'root' });

    });

    it('keeps several certificates as a list, the way the file writes them', () => {

        assert.deepEqual(entryOf(shown('time.local.', { heldTo: heldTo({ certificate, certificates: [ certificate, renewal ] }) }), usual),
                         { hostname: 'time.local', certificateFingerprints: [ certificate, renewal ] });

    });

    it('says nothing of pins where the station says it is held to nothing', () => {

        assert.deepEqual(entryOf(shown('ptbtime1.ptb.de.', { heldTo: null }), usual),
                         { hostname: 'ptbtime1.ptb.de' });

    });

});


describe('a time server sent back', () => {

    it('goes with what the page showed it held to, so that a root it learned while the page was open is kept', () => {

        // What was measured: ptbtime1.ptb.de learned its root on first use
        // after the page was loaded, and the save of another server's
        // priority took it out of the file and out of effect. The entry says
        // what it is held to as the page shows it; beside it, what the page
        // showed, from which the station can tell that the root was
        // not taken away here.
        assert.deepEqual(sentOf(shown('ptbtime1.ptb.de.', { heldTo: heldTo({ trustOnFirstUse: 'root' }) }), usual),
                         { hostname: 'ptbtime1.ptb.de', trustOnFirstUse: 'root', pinsAsShown: { trustOnFirstUse: 'root' } });

    });

    it('says a server shown held to nothing was shown so', () => {

        assert.deepEqual(sentOf(shown('ptbtime2.ptb.de.', { heldTo: null }), usual),
                         { hostname: 'ptbtime2.ptb.de', pinsAsShown: {} });

    });

    it('is what the NTS page sends every server of its list as', () => {

        const page = readFileSync(new URL('./nts.ts', import.meta.url), 'utf-8');

        assert.match(page, /\.map\(source => sentOf\(source, usual\)\)/,
                     'the NTS page sends its list without what it showed each server held to');

    });

});


describe('the list a change of one server sends', () => {

    it('still holds every other server to what it was held to', () => {

        const sources  = [ shown('ptbtime1.ptb.de.', { heldTo: heldTo({ root, roots: [ root ], trustOnFirstUse: 'root' }) }),
                           shown('ptbtime2.ptb.de.', { heldTo: heldTo({ certificate, certificates: [ certificate ] }) }) ];
        const list     = sources.map(source => entryOf(source, usual));

        assert.deepEqual(withServer(list, 1, { ...list[1], priority: 1 }),
                         [ { hostname: 'ptbtime1.ptb.de', rootFingerprint: root, trustOnFirstUse: 'root' },
                           { hostname: 'ptbtime2.ptb.de', certificateFingerprint: certificate, priority: 1 } ]);

        assert.deepEqual(withoutServer(list, 1),
                         [ { hostname: 'ptbtime1.ptb.de', rootFingerprint: root, trustOnFirstUse: 'root' } ]);

    });

});


describe('the entry the dialog saves', () => {

    // Asked of the page's source, as Node has no browser to open it in, and
    // the views it draws with cannot be loaded here.
    const page = readFileSync(new URL('./nts.ts', import.meta.url), 'utf-8');

    it('goes with what the dialog showed the server held to, where it edits one the page loaded', () => {

        assert.match(page, /if \(shown !== null\)\s+entry\.pinsAsShown = asShown\(shown\.heldTo\);/,
                     'the NTS dialog saves a server without what it showed it held to');

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
