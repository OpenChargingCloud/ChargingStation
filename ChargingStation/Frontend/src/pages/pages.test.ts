/**
 * What the charging station's own pages are held to: what every page of every
 * kind of node is, by the rules of WWCP_Node's test/pages.ts - and that every
 * link carries the base, until those rules say so as well.
 *
 * Run with `npm test`. "@node/.." is WWCP_Node/Frontend, where the rules are.
 */

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

import { everyPageIn, pagesIn } from '@node/../test/pages.ts';


const directory = new URL('./', import.meta.url);

everyPageIn(directory, {
    withForms: [ 'authentication.ts', 'calibration.ts', 'clientKeys.ts', 'connections.ts',
                 'display.ts', 'power.ts', 'rfid.ts', 'v2g.ts' ]
});


describe('a link on a page', () => {

    for (const page of pagesIn(directory))
        it(`${page.name} carries the base in every link, so that one opened in a tab of its own stays in the station`, () => {

            // A left click is the router's, which takes the base off and puts
            // it on again. A link opened in a tab of its own, or copied, is the
            // browser's - and "/logs" below "/ChargingStation" is not the
            // station's log, where several nodes share one HTTP server.
            const bare = [ ...page.source.matchAll(/href="(\/[^"]*)"/g) ].map(match => match[1]);

            assert.deepEqual(bare, [], `${page.name} links to ${bare.join(', ')} without the base: write href="\${toURL('...')}"`);

        });

});
