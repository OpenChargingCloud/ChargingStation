/**
 * The Grid connection page drawn, in a document of happy-dom, against a
 * stand-in station: a limit saved says what the station took and keeps the
 * field, and its focus, as a browser takes the focus away while the page is
 * held still; one refused keeps what is typed, and why; Reload puts back what
 * the station has.
 *
 * Run with `npm test`.
 */

import { asked, field, open, refused, submit, until, type Asked } from '../../test/station.ts';
import { chromeTakesTheFocus } from '@node/../test/dom.ts';

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

import type { PowerConfiguration, PowerUpdate } from '../api/client.ts';

const { powerPage } = await import('./power.ts');


let held: PowerConfiguration;

/** Refuses every change where told to. */
let refuseChanges = false;

function aStation(): PowerConfiguration {
    return {
        uplinkPowerLimit_kW:  55,
        evses:                [ { id: 1, maxPower_kW: 22 }, { id: 2, maxPower_kW: 22 } ],
        evsesTotal_kW:        44,
        limits:               { maxUplinkPowerLimit_kW: 50000, maxEVSEPowerLimit_kW: 400 },
        file:                 'chargingstation.json'
    };
}

function station({ method, path, body }: Asked): unknown {

    if (path === '/configuration/power' && method === 'PUT') {
        if (refuseChanges)
            return refused(400, "'uplinkPowerLimit_kW' must be more than 0 and at most 50000 kW.");
        // A station takes what it takes: a tenth of a kW at the finest.
        const { uplinkPowerLimit_kW } = body as PowerUpdate;
        held = { ...held, uplinkPowerLimit_kW: uplinkPowerLimit_kW === null ? null : Math.round(uplinkPowerLimit_kW * 10) / 10 };
        return held;
    }

    if (path === '/configuration/power')
        return held;

    return undefined;

}

async function opened(): Promise<HTMLElement> {
    held          = aStation();
    refuseChanges = false;
    return open(powerPage, '/configuration/power', [ 'power:read', 'power:edit' ],
                station, root => root.querySelector('#power-form') !== null);
}

const limit = (root: HTMLElement) => field(root, '#power-form', 'uplinkPowerLimit_kW');
const saved = (root: HTMLElement) => root.querySelector('#form-note')?.textContent === 'Saved.';


describe('the Grid connection page', () => {

    it('keeps the field saved, and its focus, while a browser takes the focus away from the page held still', async () => {

        const root    = await opened();
        const browser = chromeTakesTheFocus(root);
        const typed   = limit(root);

        typed.value = '43';
        typed.focus();

        submit(root, '#power-form');
        await until(() => saved(root) && held.uplinkPowerLimit_kW === 43, 'the limit was not saved');
        browser.disconnect();

        assert.ok(limit(root) === typed,             'the field was made anew');
        assert.ok(document.activeElement === typed,  'the focus went');

    });

    it('shows the limit as the station took it once it is saved, with nothing left to save', async () => {

        const root = await opened();

        limit(root).value = '43.25';

        submit(root, '#power-form');
        await until(() => saved(root) && held.uplinkPowerLimit_kW === 43.3, 'the limit was not saved');

        assert.equal(limit(root).value,         '43.3', 'the field says what was typed, not what the station took');
        assert.equal(limit(root).defaultValue,  '43.3');

    });

    it('keeps what is typed into a limit the station refused, and says why', async () => {

        const root = await opened();

        refuseChanges = true;
        limit(root).value = '60000';

        submit(root, '#power-form');
        await until(() => root.querySelector('#form-error')?.textContent !== '', 'the refusal was not said');

        assert.equal(root.querySelector('#form-error')!.textContent,  "'uplinkPowerLimit_kW' must be more than 0 and at most 50000 kW.");
        assert.equal(limit(root).value,                               '60000', 'what was typed went');

    });

    it('puts back what the station has on Reload', async () => {

        const root = await opened();

        limit(root).value = '12';

        root.querySelector<HTMLButtonElement>('#reload')!.click();
        await until(() => asked.filter(one => one.method === 'GET' && one.path === '/configuration/power').length === 2,
                    'Reload did not ask the station');
        await until(() => limit(root).value === '55', 'Reload kept what was typed');

    });

});
