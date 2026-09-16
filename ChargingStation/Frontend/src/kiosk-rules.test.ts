/**
 * The rules the display decides by, asked directly.
 *
 * Run with `npm test`, which is Node's own runner reading the TypeScript as it
 * stands - no bundler, no browser, no dependency that is not already here.
 *
 * What is pinned is what was got wrong: a rule that let four notices take half
 * the screen, an arrangement that drew two outlets as a column of air on an
 * upright panel, and a code that stayed on screen after it had died. How large
 * things are drawn is still measured in a browser; this is about what the page
 * decides before it draws anything.
 */

import { strict as assert } from 'node:assert';
import { describe, it }     from 'node:test';

import { atMostMessages,
         columnsFor,
         hasMoreToSay,
         messagesToShow,
         qrCodeIsStillGood,
         type DisplayMessage } from './kiosk-rules.ts';


const front = (id: string): DisplayMessage => ({ id, priority: 'AlwaysFront', text: id });
const usual = (id: string): DisplayMessage => ({ id, priority: 'NormalCycle', text: id });

/** Every notice this place shows over enough turns for each to have had one. */
const seenOver = (messages: DisplayMessage[], turns: number): Set<string> => {

    const seen = new Set<string>();

    for (let cycle = 0; cycle < turns; cycle++)
        for (const message of messagesToShow(messages, cycle))
            seen.add(message.id);

    return seen;

};


describe('which notices are shown', () => {

    it('shows everything there is when it fits', () => {

        const both = [front('a'), usual('b')];

        assert.deepEqual(messagesToShow(both, 0).map(m => m.id), ['a', 'b']);
        assert.equal(hasMoreToSay(both, 0), false);

    });

    it('never fills a place with more than it has room for', () => {

        const many = [front('a'), front('b'), front('c'), front('d'), usual('e')];

        for (let cycle = 0; cycle < 20; cycle++)
            assert.ok(messagesToShow(many, cycle).length <= atMostMessages,
                      `turn ${cycle} showed more than ${atMostMessages} notices at once`);

    });

    it('lets every notice come round, however many ask for the front', () => {

        // The case that took half a 1080 px screen: four demanding the front,
        // and one ordinary one that must not be buried by them.
        const many = [front('a'), front('b'), front('c'), front('d'), usual('e')];

        assert.deepEqual([...seenOver(many, 8)].sort(), ['a', 'b', 'c', 'd', 'e']);

    });

    it('keeps a place for the ordinary ones, whatever is marked important', () => {

        const drowning = [front('a'), front('b'), front('c'), usual('quiet')];

        for (let cycle = 0; cycle < 12; cycle++)
            assert.ok(messagesToShow(drowning, cycle).some(m => m.id === 'quiet'),
                      `turn ${cycle} left out the ordinary notice`);

    });

    it('says when there is more to say than is being said', () => {

        assert.equal(hasMoreToSay([], 0), false);
        assert.equal(hasMoreToSay([usual('a')], 0), false);
        assert.equal(hasMoreToSay([usual('a'), usual('b')], 0), true);
        assert.equal(hasMoreToSay([front('a'), front('b'), front('c')], 0), true);

    });

    it('is quiet when there is nothing to say', () => {

        assert.deepEqual(messagesToShow([], 0), []);
        assert.deepEqual(messagesToShow([], 7), []);

    });

});


describe('how the outlets are laid out', () => {

    it('gives one outlet the whole screen', () => {

        assert.equal(columnsFor(1, 1920, 1080), 1);

    });

    it('puts two side by side on a screen wider than it is tall', () => {

        assert.equal(columnsFor(2, 1920, 1080), 2);
        assert.equal(columnsFor(2, 3840, 2160), 2);

    });

    it('puts two above each other on a screen turned upright', () => {

        // Two outlets on a 1080x1920 panel used to come out 502 px wide and
        // 1694 tall - a column of air with a letter at the top of it.
        assert.equal(columnsFor(2, 1080, 1920), 1);

    });

    it('prefers a clean rectangle to a ragged last row', () => {

        // Four across and two over would give cards marginally closer to
        // square, and a row with two holes in it.
        assert.equal(columnsFor(6, 1920, 1080), 3);

    });

    it('never asks for more columns than there are outlets', () => {

        for (let count = 1; count <= 8; count++)
            for (const [width, height] of [[1920, 1080], [1080, 1920], [800, 480], [3840, 2160], [1024, 600]]) {

                const columns = columnsFor(count, width, height);

                assert.ok(columns >= 1 && columns <= count,
                          `${count} outlets on ${width}x${height} wanted ${columns} columns`);

            }

    });

    it('answers something usable before the page has been measured', () => {

        // The first draw happens before the browser has laid anything out, and
        // an arrangement of zero columns is not a thing.
        assert.equal(columnsFor(2, 0, 0), 2);
        assert.equal(columnsFor(4, Number.NaN, Number.NaN), 4);

    });

});


describe('whether a payment code is still worth showing', () => {

    const said     = '2026-03-01T12:00:00.000Z';
    const expires  = '2026-03-01T12:00:30.000Z';

    it('shows a code that still has time on it', () => {

        assert.equal(qrCodeIsStillGood(expires, said, 0),      true);
        assert.equal(qrCodeIsStillGood(expires, said, 25_000), true);

    });

    it('drops one that has run out while the station was unreachable', () => {

        assert.equal(qrCodeIsStillGood(expires, said, 30_000), false);
        assert.equal(qrCodeIsStillGood(expires, said, 90_000), false);

    });

    it('counts only the station\'s own two timestamps', () => {

        // The same thirty seconds, said by a station whose clock is years out
        // from this screen's. A display bolted to a wall has whatever clock
        // somebody left in it, and it must not enter this answer.
        assert.equal(qrCodeIsStillGood('1999-01-01T00:00:30Z', '1999-01-01T00:00:00Z', 10_000), true);
        assert.equal(qrCodeIsStillGood('2099-01-01T00:00:30Z', '2099-01-01T00:00:00Z', 10_000), true);

    });

    it('shows nothing rather than guess when a date makes no sense', () => {

        assert.equal(qrCodeIsStillGood('not a date', said, 0), false);
        assert.equal(qrCodeIsStillGood(expires, 'not a date', 0), false);

    });

});
