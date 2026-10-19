/**
 * The V2G page drawn, in a document of happy-dom, against a stand-in station:
 * a tab for each EVSE's link; a save in one tab changes that EVSE's link and
 * leaves the others' as they were; the settings saved keep the field, and its
 * focus, as a browser takes the focus away while the page is held still, and
 * say what the station took; half a pair of thermal limits is refused with the
 * station's sentence and stays typed; Reload puts back what the station has.
 *
 * Run with `npm test`.
 */

import { asked, field, open, refused, submit, until, type Asked } from '../../test/station.ts';
import { chromeTakesTheFocus } from '@node/../test/dom.ts';

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

import type { V2GConfiguration, V2GLinkConfiguration, V2GUpdate } from '../api/client.ts';

const { v2gPage } = await import('./v2g.ts');


let held: V2GConfiguration;

function aLink(EVSE: number, Label: string): V2GLinkConfiguration {
    return {
        evse:            EVSE,
        label:           Label,
        enabled:         false,
        sdp:             true,
        loopback:        false,
        interface:       null,
        port:            15117 + EVSE,
        evseId:          `DE*GEF*E${EVSE}`,
        slac:            'auto',
        mode:            'ac',
        t1sTransport:    'none',
        t1sBus:          null,
        t1sInterface:    null,
        t1sName:         null,
        t1sCycleMs:      250,
        t1sWarningC:     70,
        t1sOverloadC:    90,
        link:            null
    };
}

function aStation(): V2GConfiguration {
    return {
        certificate:     false,
        slacTransports:  [ 'none', 'auto', 'afpacket' ],
        t1sTransports:   [ 'none', 'auto', 'afpacket', 'udp' ],
        t1sDefaultBus:   '239.255.15.118:15118',
        running:         true,
        links:           [ aLink(1, 'A'), aLink(2, 'B') ],
        file:            'chargingstation.json'
    };
}

const linkOf = (EVSE: number) => held.links.find(link => link.evse === EVSE)!;

function station({ method, path, body }: Asked): unknown {

    if (path === '/configuration/v2g' && method === 'PUT') {
        const update   = body as V2GUpdate;
        const warning  = update.t1sWarningC  ?? null;
        const overload = update.t1sOverloadC ?? null;
        // V2GConfiguration.cs
        if ((warning === null) !== (overload === null))
            return refused(400, "'v2g.t1sWarningC' and 'v2g.t1sOverloadC' belong together; give both or neither.");
        // A station takes what it takes: a cycle in whole tens of milliseconds.
        const { evse, ...change } = update;
        const changed = { ...linkOf(evse), ...Object.fromEntries(Object.entries(change).filter(([ , value ]) => value !== null)) } as V2GLinkConfiguration;
        changed.t1sCycleMs = Math.round(changed.t1sCycleMs / 10) * 10;
        held = { ...held, links: held.links.map(link => link.evse === evse ? changed : link) };
        return held;
    }

    if (path === '/configuration/v2g')
        return held;

    return undefined;

}

async function opened(path = '/configuration/v2g'): Promise<HTMLElement> {
    held = aStation();
    return open(v2gPage, path, [ 'v2g:read', 'v2g:edit' ],
                station, root => root.querySelector('#v2g-form-1') !== null);
}

const form    = (EVSE: number) => `#v2g-form-${EVSE}`;
const setting = (root: HTMLElement, name: string, EVSE = 1) => field(root, form(EVSE), name);
const saved   = (root: HTMLElement, EVSE = 1) => root.querySelector(`${form(EVSE)} .form-notice`)?.textContent === 'Saved.';
const refusal = (root: HTMLElement, EVSE = 1) => root.querySelector(`${form(EVSE)} .form-error`)?.textContent ?? '';
const panel   = (root: HTMLElement, EVSE: number) => root.querySelector<HTMLElement>(`#panel-evse-${EVSE}`)!;


describe('the V2G page', () => {

    it('has a tab for each EVSE\'s link, opens on the first, and says the certificate once', async () => {

        const root = await opened();

        assert.deepEqual([...root.querySelectorAll('[role="tab"]')].map(tab => tab.textContent!.trim()), [ 'EVSE A', 'EVSE B' ]);
        assert.equal(panel(root, 1).hidden, false);
        assert.equal(panel(root, 2).hidden, true);
        assert.equal([...root.querySelectorAll('h2')].filter(heading => /Endpoint certificate/.test(heading.textContent!)).length, 1);

    });

    it('opens on the EVSE the address names', async () => {

        const root = await opened('/configuration/v2g?tab=evse-2');

        assert.equal(panel(root, 2).hidden, false);
        assert.equal(panel(root, 1).hidden, true);

    });

    it('saves the link of the EVSE whose tab it is, and leaves the others as they were', async () => {

        const root = await opened();

        root.querySelector<HTMLButtonElement>('#tab-evse-2')!.click();

        setting(root, 'port', 2).value = '15200';
        field<HTMLSelectElement>(root, form(2), 'mode').value = 'dc';

        submit(root, form(2));
        await until(() => saved(root, 2) && linkOf(2).port === 15200, 'the second EVSE\'s link was not saved');

        const put = asked.find(one => one.method === 'PUT')!.body as V2GUpdate;

        assert.equal(put.evse,        2);
        assert.equal(put.mode,        'dc');
        assert.equal(linkOf(1).port,  15118, 'the first EVSE\'s link was changed by a save of the second');

    });

    it('keeps the field saved, and its focus, while a browser takes the focus away from the page held still', async () => {

        const root    = await opened();
        const browser = chromeTakesTheFocus(root);
        const typed   = setting(root, 'evseId');

        typed.value = 'DE*GEF*E2';
        typed.focus();

        submit(root, form(1));
        await until(() => saved(root) && linkOf(1).evseId === 'DE*GEF*E2', 'the settings were not saved');
        browser.disconnect();

        assert.ok(setting(root, 'evseId') === typed,  'the field was made anew');
        assert.ok(document.activeElement === typed,   'the focus went');

    });

    it('shows the settings as the station took them once they are saved, with nothing left to save', async () => {

        const root = await opened();

        setting(root, 't1sCycleMs').value = '333';

        submit(root, form(1));
        await until(() => saved(root) && linkOf(1).t1sCycleMs === 330, 'the settings were not saved');

        assert.equal(setting(root, 't1sCycleMs').value,         '330', 'the field says what was typed, not what the station took');
        assert.equal(setting(root, 't1sCycleMs').defaultValue,  '330');

    });

    it('keeps half a pair of thermal limits typed, and says why the station refused it', async () => {

        const root = await opened();

        setting(root, 't1sOverloadC').value = '';

        submit(root, form(1));
        await until(() => refusal(root) !== '', 'the refusal was not said');

        assert.equal(refusal(root),  "'v2g.t1sWarningC' and 'v2g.t1sOverloadC' belong together; give both or neither.");
        assert.equal(setting(root, 't1sOverloadC').value,  '',   'what was typed went');
        assert.equal(setting(root, 't1sWarningC').value,   '70');

    });

    it('puts back what the station has on Reload', async () => {

        const root = await opened();

        setting(root, 'interface').value = 'eth7';

        root.querySelector<HTMLButtonElement>('#reload')!.click();
        await until(() => asked.filter(one => one.method === 'GET' && one.path === '/configuration/v2g').length === 2,
                    'Reload did not ask the station');
        await until(() => setting(root, 'interface').value === '', 'Reload kept what was typed');

    });

});
