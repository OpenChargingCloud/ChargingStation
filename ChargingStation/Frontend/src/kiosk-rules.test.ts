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
         bundleIn,
         cableLimitWorthSaying,
         columnsFor,
         darkestDim,
         dimTo,
         driftAt,
         driftSteps,
         hasMoreToSay,
         howTightlyToListCables,
         messagesToShow,
         nameOfCable,
         qrCodeIsStillGood,
         whatIsHappening,
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


describe('the walk that keeps the picture off the same pixels', () => {

    it('comes back to where it started, and not before', () => {

        assert.deepEqual(driftAt(0), driftAt(driftSteps));
        assert.notDeepEqual(driftAt(0), driftAt(1));

    });

    it('stands in every place of the ring, equally often', () => {

        const visits = new Map<string, number>();

        for (let step = 0; step < driftSteps * 5; step++) {
            const where = driftAt(step);
            const key   = `${where.x},${where.y}`;
            visits.set(key, (visits.get(key) ?? 0) + 1);
        }

        assert.equal(visits.size, driftSteps, 'the walk does not visit every place');
        assert.deepEqual([...new Set(visits.values())], [5], 'some places are stood on more than others');

    });

    it('never stands still in the middle', () => {

        // A step of nothing is a step that lets a bright edge stay where it is.
        for (let step = 0; step < driftSteps; step++)
            assert.notDeepEqual(driftAt(step), { x: 0, y: 0 });

    });

    it('stays within one step of the middle, in both directions', () => {

        // The page turns a step into a fraction of the screen and takes it out
        // of the padding on the other side. More than one step would take the
        // padding negative and push the picture off its own screen.
        for (let step = -20; step < 20; step++) {
            const where = driftAt(step);
            assert.ok(Math.abs(where.x) <= 1 && Math.abs(where.y) <= 1,
                      `step ${step} went to ${where.x},${where.y}`);
        }

    });

});


describe('whether the station has a newer display than this one', () => {

    const served = (bundle: string) =>
        `<!doctype html><html><head><meta charset="utf-8"/>` +
        `<script defer="defer" src="${bundle}"></script>` +
        `<link href="/assets/kiosk.2a63d8d65b1c2a864c21.css" rel="stylesheet"></head><body></body></html>`;

    it('finds the bundle a served page would run', () => {

        assert.equal(bundleIn(served('/assets/kiosk.92aaf883c78eaaf943af.js')),
                     '/assets/kiosk.92aaf883c78eaaf943af.js');

    });

    it('tells two builds apart', () => {

        assert.notEqual(bundleIn(served('/assets/kiosk.92aaf883c78eaaf943af.js')),
                        bundleIn(served('/assets/kiosk.0000000000000000cafe.js')));

    });

    it('says nothing about a page that is not this display', () => {

        // A captive portal, a proxy's apology, a login page somebody put in
        // front of the station. Reloading towards one of those is a display
        // that turns itself off.
        assert.equal(bundleIn('<html><body>Please sign in to the guest network.</body></html>'), null);
        assert.equal(bundleIn('<html><head><script src="/assets/main.170dd60a5b2f61b938b6.js"></script></head></html>'), null);
        assert.equal(bundleIn(''), null);

    });

});


describe('how bright the screen is', () => {

    const awake  = 2 * 60 * 1000;
    const ageing = awake + 1;

    it('is full brightness outside the quiet hours, whoever is there', () => {

        assert.equal(dimTo(null,      ageing, awake), null);
        assert.equal(dimTo(undefined, ageing, awake), null);

    });

    it('goes down in the quiet hours once nothing has happened for a while', () => {

        assert.equal(dimTo(0.3, ageing, awake), 0.3);

    });

    it('is full brightness while anything is still recent', () => {

        // Somebody who has just held a card up is standing in front of it
        // reading what it says about their charge.
        assert.equal(dimTo(0.3, 0,         awake), null);
        assert.equal(dimTo(0.3, awake - 1, awake), null);

    });

    it('never goes dark, whatever it is asked for', () => {

        // A dark display is one nobody can tell from a broken one, and the
        // person it turns away is the one arriving at two in the morning.
        assert.equal(dimTo(0,     ageing, awake), darkestDim);
        assert.equal(dimTo(-5,    ageing, awake), darkestDim);
        assert.equal(dimTo(0.001, ageing, awake), darkestDim);

    });

    it('never asks for more than full brightness', () => {

        assert.equal(dimTo(4, ageing, awake), 1);

    });

});


describe('what counts as something happening', () => {

    const evse = (over: Record<string, unknown> = {}) =>
        ({ id: 1, status: 'available', messages: [], ...over }) as never;

    const state = (evses: unknown[], messages: DisplayMessage[] = []) =>
        ({ evses, messages }) as never;

    it('notices an outlet going from free to charging', () => {

        assert.notEqual(whatIsHappening(state([evse()])),
                        whatIsHappening(state([evse({ status: 'occupied', session: { method: 'RFID' } })])));

    });

    it('notices a hold being placed and let go', () => {

        assert.notEqual(whatIsHappening(state([evse()])),
                        whatIsHappening(state([evse({ status: 'reserved', reservation: { until: 'later' } })])));

    });

    it('notices an outlet on its way out of service', () => {

        assert.notEqual(whatIsHappening(state([evse({ status: 'occupied', session: { method: 'RFID' } })])),
                        whatIsHappening(state([evse({ status: 'occupied', session: { method: 'RFID' }, closing: true })])));

    });

    it('notices a back end asking for something to be read out', () => {

        const notice: DisplayMessage = { id: 'n1', priority: 'NormalCycle', text: 'The barrier closes at ten.' };

        assert.notEqual(whatIsHappening(state([evse()])),
                        whatIsHappening(state([evse()], [notice])));

        assert.notEqual(whatIsHappening(state([evse()])),
                        whatIsHappening(state([evse({ messages: [notice] })])));

    });

    it('does not notice a car charging through the night', () => {

        // The power reading moves every second and the payment code turns over
        // every half minute. A screen that woke for either would be at full
        // brightness all night with a car parked at it, which is the one case
        // where nobody is looking at all.
        const charging = (kW: number, code: string) =>
            state([evse({ status:          'occupied',
                          session:         { method: 'RFID' },
                          currentPower_kW: kW,
                          qrCode:          { url: code, expiresAt: 'whenever' } })]);

        assert.equal(whatIsHappening(charging(11.2, 'one')),
                     whatIsHappening(charging(43.9, 'two')));

    });

    it('does not notice the clock', () => {

        const free = () => ({ evses: [evse()], messages: [], timestamp: String(Math.random()) }) as never;

        assert.equal(whatIsHappening(free()), whatIsHappening(free()));

    });

});


describe('what a bay says about its cables', () => {

    it('calls a plug what is printed on it, not what OCPP calls it', () => {

        // Measured on the screen: "cChaDeMo", "sType2Cable", "cGBT-DC". A
        // display on the front of a charging station is read by the person
        // holding the plug.
        assert.equal(nameOfCable('cCCS2'),    'CCS');
        assert.equal(nameOfCable('cChaDeMo'), 'CHAdeMO');
        assert.equal(nameOfCable('sType2'),   'Type 2');
        assert.equal(nameOfCable('cGBT-DC'),  'GB/T');
        assert.equal(nameOfCable('cCCS1'),    'CCS 1');
        assert.equal(nameOfCable('sCEE-7-7'), 'Schuko');

    });

    it('passes a plug it has never heard of through as it was written', () => {

        // The configuration lets one be typed in on purpose, because a plug
        // OCPP has not named is still a plug somebody can charge from - and a
        // name somebody chose beats anything this table could make up.
        assert.equal(nameOfCable('Kupplung Nord 3'), 'Kupplung Nord 3');
        assert.equal(nameOfCable(''), '');

    });

    it('says what a cable can do only where it is not what the bay can do', () => {

        // The card already carries "up to 150 kW". Saying it again on every
        // chip says nothing and costs the width that decides how many fit on a
        // row - which decides how much of the card is left for the code.
        assert.equal(cableLimitWorthSaying(150, 150), false);
        assert.equal(cableLimitWorthSaying(22,  22),  false);

        // And a 22 kW socket on a 150 kW bay is the one thing somebody
        // choosing between two cables has to know.
        assert.equal(cableLimitWorthSaying(22,  150), true);
        assert.equal(cableLimitWorthSaying(50,  150), true);

    });

    it('does not let a rounding difference count as a difference', () => {
        assert.equal(cableLimitWorthSaying(22.0, 22.001), false);
    });

});


describe('how much room the cables may take', () => {

    it('lets a bay with one or two of them have all of it', () => {
        assert.equal(howTightlyToListCables(1), 'roomy');
        assert.equal(howTightlyToListCables(2), 'roomy');
    });

    it('takes room back as the list grows, because the code is what the card is for', () => {

        // Measured at 1920x1080: eight cables listed at full size took four
        // rows and left 166 px of code, against 414 px on the bay beside it.
        assert.equal(howTightlyToListCables(3), 'tight');
        assert.equal(howTightlyToListCables(5), 'tightest');
        assert.equal(howTightlyToListCables(8), 'tightest');

    });

    it('never gives one more cable more room than one fewer', () => {

        const roominess = { roomy: 3, tight: 2, tightest: 1 };

        for (let count = 1; count < 16; count++)
            assert.ok(roominess[howTightlyToListCables(count + 1)] <=
                      roominess[howTightlyToListCables(count)],
                      `${count + 1} cables were given more room than ${count}`);

    });

});
