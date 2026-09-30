/**
 * What the EVSEs page says somebody may do who may change some of it and not
 * what is fitted, and how it reads a number typed into it.
 *
 * Run with `npm test`. What is pinned is that it says only what they may: the
 * station's operator may take an EVSE out of service and not correct what its
 * cable may deliver, and the page used to tell it it could. And that a field
 * for power somebody emptied is not read as 0 kW.
 */

import { strict as assert }  from 'node:assert';
import { readFileSync }      from 'node:fs';
import { describe, it }      from 'node:test';

import { numbersReadAsZeroWhenEmptied } from '@node/../test/pages.ts';
import { mayChangeShortOfWhatIsFitted } from './evses.ts';


describe('what the EVSEs page says somebody may do short of what is fitted', () => {

    it('says taking them out of service alone, where that is all they may - as the operator of a station may', () => {
        assert.equal(mayChangeShortOfWhatIsFitted(true, false), 'take these EVSEs out of service');
    });

    it('says correcting what they may deliver alone, where that is all they may', () => {
        assert.equal(mayChangeShortOfWhatIsFitted(false, true), 'correct what these EVSEs and their cables may deliver');
    });

    it('says both, where they may do both - as whoever commissions a station may', () => {
        assert.equal(mayChangeShortOfWhatIsFitted(true, true),
                     'take these EVSEs out of service and correct what they and their cables may deliver');
    });

});

describe('what the EVSEs page reads from a field for power', () => {

    // The page has no form - a field says what it changes as it is typed
    // into - and the rules of every page (WWCP_Node's test/pages.ts) ask only
    // a page with a form how it reads its numbers. Asked here of this one, by
    // the same reading (found by the local controller).
    it('reads an emptied one as not said, which a cable takes for as much as its EVSE, rather than as 0 kW', () => {

        const page = { name: 'evses.ts', source: readFileSync(new URL('./evses.ts', import.meta.url), 'utf-8') };
        const read = numbersReadAsZeroWhenEmptied(page);

        assert.deepEqual(read, [], `evses.ts reads ${read.join(', ')} with Number(), which makes an emptied field 0`);

    });

});
