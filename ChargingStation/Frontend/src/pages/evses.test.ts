/**
 * What the EVSEs page says somebody may do who may change some of it and not
 * what is fitted.
 *
 * Run with `npm test`. What is pinned is that it says only what they may: the
 * station's operator may take an EVSE out of service and not correct what its
 * cable may deliver, and the page used to tell it it could.
 */

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

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
