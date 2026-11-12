/**
 * The menu of a charging station: who is shown which entry - the support,
 * who sees what is going on and nothing else, the EVSEs as they stand and no
 * page about how the station is set up; the viewer every page.
 *
 * Run with `npm test`.
 */

import '../test/station.ts';

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

import type { NodeMe }       from '@node/api/client';

const { auth }         = await import('@node/auth');
const { visibleMenu }  = await import('@node/shell');
const { stationMenu }  = await import('./menu.ts');

/** Somebody signed in who may do this, and nothing else. */
const signedIn = (...Permissions: string[]): NodeMe =>
    ({ username: 'alice', roles: [ 'desk' ], permissions: Permissions as NodeMe['permissions'], mayReadTheLog: false });

const paths = (Entries: { path: string; children?: { path: string }[] }[]): string[] =>
    Entries.flatMap(entry => [ entry.path, ...(entry.children ?? []).map(child => child.path) ]);


describe('the menu of a charging station', () => {

    it('shows the support the EVSEs as they stand, and nothing about how the station is set up', () => {

        auth.set(signedIn('state:read', 'state:run', 'reset:run', 'unlock:run'));

        assert.deepEqual(paths(visibleMenu(stationMenu)), [ '/evses' ]);

    });

    it("shows whoever may read the EVSEs' configuration only that, without what is going on", () => {

        auth.set(signedIn('configuration:read', 'evses:read'));

        const shown = paths(visibleMenu(stationMenu));

        assert.ok (shown.includes('/configuration/evses'), 'the EVSEs under Configuration were not shown');
        assert.ok(!shown.includes('/evses'),               'the EVSEs as they stand were shown to whoever may not read what is going on');

    });

});
