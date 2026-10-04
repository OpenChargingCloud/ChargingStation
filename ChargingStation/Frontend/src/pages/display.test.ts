/**
 * The Display page drawn, in a document of happy-dom, against a stand-in
 * station: quiet hours saved say what the station took and keep the field,
 * and its focus, as a browser takes the focus away while the page is held
 * still; "Keep no quiet hours" leaves the form empty and itself off; hours
 * refused keep what is typed, and why.
 *
 * Run with `npm test`.
 */

import { field, open, refused, submit, until, type Asked } from '../../test/station.ts';
import { chromeTakesTheFocus } from '@node/../test/dom.ts';

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

import type { DisplayConfiguration } from '../api/client.ts';

const { displayPage } = await import('./display.ts');


let held: DisplayConfiguration;

/** Refuses every change where told to. */
let refuseChanges = false;

function aStation(): DisplayConfiguration {
    return {
        dimFrom:   '22:00',
        dimUntil:  '06:00',
        dimTo:     0.3,
        quietNow:  false,
        limits:    { darkestDimTo: 0.1, defaultDimTo: 0.3 },
        file:      'chargingstation.json'
    };
}

function station({ method, path, body }: Asked): unknown {

    if (path === '/configuration/display' && method === 'PUT') {
        if (refuseChanges)
            return refused(400, "'display' needs both 'dimFrom' and 'dimUntil', or neither.");
        const update = body as Partial<DisplayConfiguration>;
        // A station takes what it takes: a level of a whole per cent, and no
        // level where there are no hours to keep it in.
        held = {
            ...held,
            dimFrom:   update.dimFrom  ?? null,
            dimUntil:  update.dimUntil ?? null,
            dimTo:     update.dimFrom === undefined || update.dimTo === undefined || update.dimTo === null
                           ? null
                           : Math.round(update.dimTo * 100) / 100
        };
        return held;
    }

    if (path === '/configuration/display')
        return held;

    return undefined;

}

async function opened(): Promise<HTMLElement> {
    held          = aStation();
    refuseChanges = false;
    return open(displayPage, '/configuration/display', [ 'display:read', 'display:edit' ],
                station, root => root.querySelector('#display-form') !== null);
}

const hours = (root: HTMLElement, name: 'dimFrom' | 'dimUntil' | 'dimTo') => field(root, '#display-form', name);
const saved = (root: HTMLElement) => root.querySelector('#form-note')?.textContent === 'Saved.';


describe('the Display page', () => {

    it('keeps the field saved, and its focus, while a browser takes the focus away from the page held still', async () => {

        const root    = await opened();
        const browser = chromeTakesTheFocus(root);
        const typed   = hours(root, 'dimFrom');

        typed.value = '23:00';
        typed.focus();

        submit(root, '#display-form');
        await until(() => saved(root) && held.dimFrom === '23:00', 'the hours were not saved');
        browser.disconnect();

        assert.ok(hours(root, 'dimFrom') === typed,  'the field was made anew');
        assert.ok(document.activeElement === typed,  'the focus went');

    });

    it('shows the hours as the station took them once they are saved, with nothing left to save', async () => {

        const root = await opened();

        hours(root, 'dimTo').value = '42.4';

        submit(root, '#display-form');
        await until(() => saved(root) && held.dimTo === 0.42, 'the level was not saved');

        assert.equal(hours(root, 'dimTo').value,         '42', 'the field says what was typed, not what the station took');
        assert.equal(hours(root, 'dimTo').defaultValue,  '42');

    });

    it('leaves the form empty, and its own button off, once no quiet hours are kept', async () => {

        const root = await opened();

        hours(root, 'dimFrom').value = '21:00';

        root.querySelector<HTMLButtonElement>('#no-quiet-hours')!.click();
        await until(() => saved(root) && held.dimFrom === null, 'the hours were not taken away');

        assert.deepEqual([ hours(root, 'dimFrom').value, hours(root, 'dimUntil').value, hours(root, 'dimTo').value ],
                         [ '', '', '' ], 'the form says hours the station no longer keeps');
        assert.equal(root.querySelector<HTMLButtonElement>('#no-quiet-hours')!.disabled, true,
                     'it offers to take away hours there are none of');

    });

    it('keeps what is typed into hours the station refused, and says why', async () => {

        const root = await opened();

        refuseChanges = true;
        hours(root, 'dimUntil').value = '';

        submit(root, '#display-form');
        await until(() => root.querySelector('#form-error')?.textContent !== '', 'the refusal was not said');

        assert.equal(root.querySelector('#form-error')!.textContent,
                     "'display' needs both 'dimFrom' and 'dimUntil', or neither.");
        assert.equal(hours(root, 'dimUntil').value,  '', 'what was typed went');
        assert.equal(hours(root, 'dimFrom').value,   '22:00');

    });

});
