/**
 * The V2G page drawn, in a document of happy-dom, against a stand-in station:
 * the settings saved keep the field, and its focus, as a browser takes the
 * focus away while the page is held still, and say what the station took;
 * half a pair of thermal limits is refused with the station's sentence and
 * stays typed; Reload puts back what the station has.
 *
 * Run with `npm test`.
 */

import { asked, field, open, refused, submit, until, type Asked } from '../../test/station.ts';
import { chromeTakesTheFocus } from '@node/../test/dom.ts';

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

import type { V2GConfiguration } from '../api/client.ts';

const { v2gPage } = await import('./v2g.ts');


let held: V2GConfiguration;

function aStation(): V2GConfiguration {
    return {
        enabled:         false,
        sdp:             true,
        loopback:        false,
        interface:       null,
        port:            15118,
        evseId:          'DE*GEF*E1',
        slac:            'auto',
        t1sTransport:    'none',
        t1sBus:          null,
        t1sInterface:    null,
        t1sName:         null,
        t1sCycleMs:      250,
        t1sWarningC:     70,
        t1sOverloadC:    90,
        t1sDefaultBus:   '239.255.15.118:15118',
        certificate:     false,
        slacTransports:  [ 'none', 'auto', 'afpacket' ],
        t1sTransports:   [ 'none', 'auto', 'afpacket', 'udp' ],
        running:         true,
        link:            null,
        file:            'chargingstation.json'
    };
}

function station({ method, path, body }: Asked): unknown {

    if (path === '/configuration/v2g' && method === 'PUT') {
        const update = body as Partial<V2GConfiguration>;
        const warning  = update.t1sWarningC  ?? null;
        const overload = update.t1sOverloadC ?? null;
        // V2GConfiguration.cs
        if ((warning === null) !== (overload === null))
            return refused(400, "'v2g.t1sWarningC' and 'v2g.t1sOverloadC' belong together; give both or neither.");
        // A station takes what it takes: a cycle in whole tens of milliseconds.
        held = { ...held, ...Object.fromEntries(Object.entries(update).filter(([ , value ]) => value !== null)) } as V2GConfiguration;
        held.t1sCycleMs = Math.round(held.t1sCycleMs / 10) * 10;
        return held;
    }

    if (path === '/configuration/v2g')
        return held;

    return undefined;

}

async function opened(): Promise<HTMLElement> {
    held = aStation();
    return open(v2gPage, '/configuration/v2g', [ 'v2g:read', 'v2g:edit' ],
                station, root => root.querySelector('#v2g-form') !== null);
}

const setting = (root: HTMLElement, name: string) => field(root, '#v2g-form', name);
const saved   = (root: HTMLElement) => root.querySelector('#form-note')?.textContent === 'Saved.';


describe('the V2G page', () => {

    it('keeps the field saved, and its focus, while a browser takes the focus away from the page held still', async () => {

        const root    = await opened();
        const browser = chromeTakesTheFocus(root);
        const typed   = setting(root, 'evseId');

        typed.value = 'DE*GEF*E2';
        typed.focus();

        submit(root, '#v2g-form');
        await until(() => saved(root) && held.evseId === 'DE*GEF*E2', 'the settings were not saved');
        browser.disconnect();

        assert.ok(setting(root, 'evseId') === typed,  'the field was made anew');
        assert.ok(document.activeElement === typed,   'the focus went');

    });

    it('shows the settings as the station took them once they are saved, with nothing left to save', async () => {

        const root = await opened();

        setting(root, 't1sCycleMs').value = '333';

        submit(root, '#v2g-form');
        await until(() => saved(root) && held.t1sCycleMs === 330, 'the settings were not saved');

        assert.equal(setting(root, 't1sCycleMs').value,         '330', 'the field says what was typed, not what the station took');
        assert.equal(setting(root, 't1sCycleMs').defaultValue,  '330');

    });

    it('keeps half a pair of thermal limits typed, and says why the station refused it', async () => {

        const root = await opened();

        setting(root, 't1sOverloadC').value = '';

        submit(root, '#v2g-form');
        await until(() => root.querySelector('#form-error')?.textContent !== '', 'the refusal was not said');

        assert.equal(root.querySelector('#form-error')!.textContent,
                     "'v2g.t1sWarningC' and 'v2g.t1sOverloadC' belong together; give both or neither.");
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
