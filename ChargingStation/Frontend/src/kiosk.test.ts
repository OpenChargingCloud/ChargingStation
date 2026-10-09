/**
 * The display drawn, in a document of happy-dom, against a stand-in station.
 *
 * Run with `npm test`. The display is not a page of the router but a script
 * that starts as it is loaded: it asks the station every two seconds, ticks
 * its clock every second, and draws #kiosk. So this file gives it what the
 * served page would - the element, the API's address - and holds its timers:
 * mock.timers turns setInterval into a clock that moves only when it is told,
 * and the stylesheet the script imports is nothing here.
 *
 * What is pinned: a card number typed into the dialog, its focus and its
 * cursor outlive the card being sent and refused; an outlet keeps its card
 * while what the station says about it changes; the clock keeps ticking while
 * the page is drawn anew around it.
 */

import '@node/../test/dom.ts';

import { registerHooks } from 'node:module';

import { strict as assert }   from 'node:assert';
import { describe, it, mock } from 'node:test';

// What webpack makes of the stylesheet, Node cannot: here it is nothing.
registerHooks({
    resolve(specifier, context, next) {
        return specifier.endsWith('.scss')
                   ? { url: 'data:text/javascript,', format: 'module', shortCircuit: true }
                   : next(specifier, context);
    }
});

mock.timers.enable({ apis: [ 'setInterval' ] });

document.head.innerHTML = '<meta name="api-base" content="/api" />';
document.body.innerHTML = '<div id="kiosk"></div>';


interface Asked { method: string; path: string; body: unknown }

const asked: Asked[] = [];

/** What the station says the second outlet is doing. */
let secondIs = 'available';

/** What the station says about a card held up; null for yes. */
let refusal: string | null = null;

/** Whether the station says the picture should walk against burn-in. */
let walking = false;

function aStation() {
    return {
        station:      { name: 'Test station', logo: null, language: 'en' },
        timestamp:    new Date().toISOString(),
        clock:        { now: '2026-10-04T12:00:00Z', source: 'nts',
                        nts: { enabled: true, group: null, server: 'ptbtime1.ptb.de.', lastServer: 'ptbtime1.ptb.de.', servers: null,
                               minServers: null, asked: 1, answered: 1, checkedAt: '2026-10-04T11:59:30Z', ageSeconds: 30,
                               offset_ms: 2, everySeconds: 900 },
                        legal: false, authority: null, why: 'notClaimed' },
        evses:        [ 1, 2 ].map(id => ({
                            id, label: `EVSE ${id}`, status: id === 2 ? secondIs : 'available', closing: false, maxPower_kW: 22,
                            currentPower_kW: null, powerIsSimulated: true,
                            connectors: [ { id: 1, type: 'sType2', maxPower_kW: 22 } ],
                            session: null, reservation: null, messages: [], qrCode: null, rfid: null })),
        holds:        null,
        messages:     [],
        rfid:         { id: 'reader-a', kind: 'fake', fake: true, ready: true },
        webPayments:  false,
        dim:          null,
        keepMoving:   walking
    };
}

globalThis.fetch = (async (input: string | URL | Request, init?: RequestInit) => {

    const path   = String(input);
    const method = init?.method ?? 'GET';

    asked.push({ method, path, body: init?.body === undefined ? undefined : JSON.parse(String(init.body)) });

    const json = (status: number, body: unknown) =>
        new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

    if (path === '/api/kiosk')
        return json(200, aStation());

    // ChargingStation.Kiosk.cs
    if (path === '/api/kiosk/rfid')
        return refusal === null ? json(200, { started: true }) : json(400, { error: refusal });

    return json(404, { error: `nothing at ${method} ${path}` });

}) as typeof fetch;


const wait = (ms = 0) => new Promise(resolve => setTimeout(resolve, ms));

async function until(what: () => boolean, failure: string): Promise<void> {
    for (let i = 0; i < 200 && !what(); i++)
        await wait(5);
    assert.ok(what(), failure);
}

/** As much time as the display's own timers are told has passed. */
async function passes(ms: number): Promise<void> {
    mock.timers.tick(ms);
    await wait(20);
}

const root = document.querySelector<HTMLElement>('#kiosk')!;

const cardOf = (id: number) => [...root.querySelectorAll<HTMLElement>('.kiosk-evse-cell')].
                                   find(cell => cell.querySelector('.kiosk-evse-label')?.textContent?.trim() === `EVSE ${id}`);

await import('./kiosk.ts');

await until(() => root.querySelector('.kiosk-evses') !== null, 'the display did not draw');


describe('the display', () => {

    it('keeps the card number typed, its focus and its cursor, while the card is sent and refused', async () => {

        refusal = 'EVSE 1 is already charging.';

        root.querySelector<HTMLButtonElement>('.kiosk-foot [data-reader]')!.click();
        await until(() => root.querySelector('#uid') !== null, 'the card dialog did not open');

        try
        {

            const uid = root.querySelector<HTMLInputElement>('#uid')!;

            uid.value = '04A22B3C';
            uid.dispatchEvent(new Event('input', { bubbles: true }));
            uid.setSelectionRange(2, 2);

            root.querySelector<HTMLButtonElement>('#dialog-ok')!.click();
            await until(() => root.querySelector('.kiosk-dialog-note')?.textContent === 'EVSE 1 is already charging.', 'the refusal was not said');

            assert.ok(root.querySelector('#uid') === uid,  'the field was made anew');
            assert.equal(uid.value,                        '04A22B3C');
            assert.ok(document.activeElement === uid,      'the focus went');
            assert.equal(uid.selectionStart,               2, 'the cursor went');

        }
        finally
        {
            // Closed whatever came of it: while it is open the display draws
            // nothing, and the tests after this one would be asking that.
            root.querySelector<HTMLButtonElement>('#dialog-cancel')?.click();
            await until(() => root.querySelector('#uid') === null, 'the card dialog did not close');
        }

    });

    it('keeps the card of an outlet while what the station says about it changes', async () => {

        const first = cardOf(1);

        secondIs = 'occupied';
        await passes(2000);
        await until(() => cardOf(2)?.querySelector('.kiosk-status')?.textContent?.trim() === 'charging', 'the outlet was not drawn anew');

        assert.ok(cardOf(1) === first, 'the card was made anew');

    });

    it('keeps ticking its clock while the page is drawn anew', async () => {

        await passes(1000);

        const ticked = root.querySelector('#clock')?.textContent ?? '';

        assert.match(ticked, /\d{1,2}:\d{2}:\d{2}/, 'the clock did not tick');

        secondIs = 'reserved';
        await passes(2000);
        await until(() => cardOf(2)?.querySelector('.kiosk-status')?.textContent?.trim() === 'reserved', 'the outlet was not drawn anew after a tick');

        assert.equal(root.querySelectorAll('#clock').length, 1, 'the clock is there twice');
        assert.match(root.querySelector('#clock')?.textContent ?? '', /\d{1,2}:\d{2}:\d{2}/, 'the clock went');

    });

    it('keeps its picture still where the station does not say to walk', async () => {

        walking = false;

        await passes(2000);
        await passes(45_000);
        await passes(45_000);

        const where = [ root.style.getPropertyValue('--drift-x'), root.style.getPropertyValue('--drift-y') ];

        assert.ok(where.every(value => value === '' || Number(value) === 0),
                  `the picture walked to ${where.join(', ')} on a station that did not say to`);

    });

    it('walks where the station says to, a step gliding rather than jumping', async () => {

        walking = true;

        await passes(2000);
        await passes(45_000);

        const where  = [ root.style.getPropertyValue('--drift-x'), root.style.getPropertyValue('--drift-y') ];
        const takes  = root.style.getPropertyValue('--drift-takes');

        assert.ok(where.some(value => value !== '' && Number(value) !== 0), 'the picture did not take a step');
        assert.match(takes, /^\d+ms$/, 'the step is not told how long it takes, and jumps');
        assert.ok(Number.parseInt(takes) >= 1000, `a step of ${takes} is a jump`);

        // And told to stop, it goes back to the middle at the next poll, not
        // at the next step.
        walking = false;
        await passes(2000);

        assert.deepEqual([ root.style.getPropertyValue('--drift-x'), root.style.getPropertyValue('--drift-y') ], [ '0', '0' ],
                         'the picture stayed where it had walked to');

    });

});
