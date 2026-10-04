/**
 * The RFID page drawn, in a document of happy-dom, against a stand-in
 * station: a reader half written into the add form, and its focus, outlive
 * another being switched off and the list being saved, as a browser takes
 * the focus away while the page is held still; a switch says the draft, and
 * goes back with "Discard changes"; readers keep their card while another
 * goes; one added empties the form; a list refused stays, with why.
 *
 * Run with `npm test`.
 */

import { change, field, open, refused, submit, until, type Asked } from '../../test/station.ts';
import { chromeTakesTheFocus } from '@node/../test/dom.ts';

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

import type { RFIDConfiguration, RFIDReader } from '../api/client.ts';

const { rfidPage } = await import('./rfid.ts');


let held: RFIDConfiguration;

/** Refuses every change where told to. */
let refuseChanges = false;

function aStation(): RFIDConfiguration {
    return {
        readers:     [ { id: 'reader-a', kind: 'fake',   evse: 1, enabled: true, hasDriver: true, fake: true },
                       { id: 'reader-b', kind: 'pn532',  evse: 2, enabled: true, hasDriver: true } ],
        evses:       [ { id: 1, label: null }, { id: 2, label: 'left' }, { id: 3, label: null } ],
        kinds:       [ 'fake', 'pn532' ],
        fakeKind:    'fake',
        maxReaders:  4,
        file:        'chargingstation.json'
    };
}

function station({ method, path, body }: Asked): unknown {

    if (path === '/configuration/rfid' && method === 'PUT') {
        if (refuseChanges)
            return refused(400, "The RFID reader 'reader-c' is at EVSE 4, and this station has 3 EVSE(s).");
        const { readers } = body as { readers: RFIDReader[] };
        held = { ...held, readers: readers.map(reader => ({ ...reader, hasDriver: true, fake: reader.kind === 'fake' })) };
        return held;
    }

    if (path === '/configuration/rfid')
        return held;

    return undefined;

}

async function opened(): Promise<HTMLElement> {
    held          = aStation();
    refuseChanges = false;
    return open(rfidPage, '/configuration/rfid', [ 'rfid:read', 'rfid:edit', 'availability:edit' ],
                station, root => root.querySelector('#add-form') !== null);
}

const named  = (root: HTMLElement) => field(root, '#add-form', 'id');
const card   = (root: HTMLElement, id: string) => [...root.querySelectorAll<HTMLElement>('section.reader')].
                                                      find(one => one.querySelector('h2')?.textContent?.includes(id));
const onOff  = (root: HTMLElement, id: string) => card(root, id)!.querySelector<HTMLInputElement>('input[type="checkbox"]')!;
const saved  = (root: HTMLElement) => root.querySelector('#form-note')?.textContent === 'Saved, and in effect.';


describe('the RFID page', () => {

    it('keeps what is half written into the add form, and its focus, while another reader is switched off and saved', async () => {

        const root    = await opened();
        const browser = chromeTakesTheFocus(root);
        const typed   = named(root);

        typed.value = 'reader-c';
        typed.focus();

        change(onOff(root, 'reader-b'), false);
        root.querySelector<HTMLButtonElement>('#save')!.click();
        await until(() => saved(root) && held.readers[1].enabled === false, 'the list was not saved');
        browser.disconnect();

        assert.ok(named(root) === typed,             'the field was made anew');
        assert.equal(typed.value,                    'reader-c');
        assert.ok(document.activeElement === typed,  'the focus went');

    });

    it('puts a switch back to what the station has when the changes are discarded', async () => {

        const root = await opened();

        change(onOff(root, 'reader-a'), false);
        await until(() => root.querySelector<HTMLButtonElement>('#revert')?.disabled === false, 'the change was not taken');

        root.querySelector<HTMLButtonElement>('#revert')!.click();
        await until(() => root.querySelector<HTMLButtonElement>('#revert')?.disabled === true, 'the changes were not discarded');

        assert.equal(onOff(root, 'reader-a').checked, true, 'the switch says what was clicked');

    });

    it('keeps the card of a reader while the one before it is removed', async () => {

        const root   = await opened();
        const second = card(root, 'reader-b');

        card(root, 'reader-a')!.querySelector<HTMLButtonElement>('[data-remove]')!.click();
        await until(() => card(root, 'reader-a') === undefined, 'the reader was not removed');

        assert.ok(card(root, 'reader-b') === second, 'the card was made anew');

    });

    it('empties the add form once a reader is added', async () => {

        const root = await opened();

        named(root).value = 'reader-c';
        field(root, '#add-form', 'kind').value = 'pn532';
        change(field<HTMLSelectElement>(root, '#add-form', 'where'), '3');

        submit(root, '#add-form');
        await until(() => card(root, 'reader-c') !== undefined, 'the reader was not added');

        assert.deepEqual([ named(root).value, field(root, '#add-form', 'kind').value, field<HTMLSelectElement>(root, '#add-form', 'where').value ],
                         [ '', '', '' ], 'the form still holds what was added');

    });

    it('keeps a list the station refused, and says why', async () => {

        const root = await opened();

        refuseChanges = true;
        change(onOff(root, 'reader-b'), false);

        root.querySelector<HTMLButtonElement>('#save')!.click();
        await until(() => root.querySelector('#form-error')?.textContent !== '', 'the refusal was not said');

        assert.equal(root.querySelector('#form-error')!.textContent, "The RFID reader 'reader-c' is at EVSE 4, and this station has 3 EVSE(s).");
        assert.equal(onOff(root, 'reader-b').checked, false, 'what was changed went');

    });

});
