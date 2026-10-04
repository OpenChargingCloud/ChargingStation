/**
 * What the EVSEs page says somebody may do who may change some of it and not
 * what is fitted - and the page drawn, in a document of happy-dom, against a
 * stand-in station.
 *
 * Run with `npm test`. What is pinned is that it says only what they may: the
 * station's operator may take an EVSE out of service and not correct what its
 * cable may deliver, and the page used to tell it it could.
 *
 * Drawn, the fields are the draft: what is typed into one, and its focus,
 * outlives the rest of the page being drawn anew and the list being saved,
 * as a browser takes the focus away while the page is held still; a number
 * half typed is not written over while it is typed; the list saved says what
 * the station took, "Discard changes" what it has; an EVSE keeps its card
 * while the one before it goes, and another type of cable typed and not added
 * stays with its EVSE across a save; a list refused stays, with why.
 */

import { open, refused, until, type Asked } from '../../test/station.ts';
import { chromeTakesTheFocus } from '@node/../test/dom.ts';

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

import type { EVSE, EVSEConfiguration } from '../api/client.ts';

const { evsesPage, mayChangeShortOfWhatIsFitted } = await import('./evses.ts');


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


let held: EVSEConfiguration;

/** Refuses every change where told to. */
let refuseChanges = false;

function anEVSE(id: number): EVSE {
    return {
        id,
        connectors:         [ { id: 1, type: 'sType2', maxPower_kW: 22 }, { id: 2, type: 'cCCS2', maxPower_kW: 22 } ],
        maxPower_kW:        22,
        operative:          true,
        physicalReference:  String.fromCharCode(64 + id),
        meterType:          null,
        meterSerialNumber:  null
    };
}

function aStation(): EVSEConfiguration {
    return {
        evses:                   [ anEVSE(1), anEVSE(2) ],
        file:                    'chargingstation.json',
        maxEVSEs:                8,
        maxConnectors:           4,
        maxPower_kW:             400,
        maxConnectorTypeLength:  32,
        uplinkPowerLimit_kW:     null,
        connectorTypes:          [ 'sType2', 'cCCS2', 'cType2' ]
    };
}

function station({ method, path, body }: Asked): unknown {

    if (path === '/configuration/evses' && method === 'PUT') {
        if (refuseChanges)
            return refused(400, "EVSE 1, connector 'sType2': a cable may not be configured for more (30 kW) than the EVSE feeding it (22 kW).");
        // A station takes what it takes: a tenth of a kW at the finest.
        const { evses } = body as { evses: EVSE[] };
        held = { ...held, evses: evses.map(evse => ({ ...evse, maxPower_kW: Math.round(evse.maxPower_kW * 10) / 10 })) };
        return held;
    }

    if (path === '/configuration/evses')
        return held;

    return undefined;

}

async function opened(): Promise<HTMLElement> {
    held          = aStation();
    refuseChanges = false;
    return open(evsesPage, '/configuration/evses', [ 'evses:read', 'evses:edit', 'power:edit', 'availability:edit' ],
                station, root => root.querySelector('#evses section') !== null);
}

const cardOf     = (root: HTMLElement, place: number) => root.querySelectorAll<HTMLElement>('#evses section.evse')[place]!;
const fieldOf    = (root: HTMLElement, place: number, name: string) => cardOf(root, place).querySelector<HTMLInputElement>(`[data-field="${name}"]`)!;
const anotherOf  = (root: HTMLElement, place: number) => cardOf(root, place).querySelector<HTMLInputElement>('[data-custom]')!;
const saved      = (root: HTMLElement) => root.querySelector('#form-note')?.textContent === 'Saved, and in effect.';

/** Typed by hand: the value set, and the input said. */
function type(input: HTMLInputElement, text: string): void {
    input.value = text;
    input.dispatchEvent(new Event('input', { bubbles: true }));
}


describe('the EVSEs page', () => {

    it('keeps what is typed, and its focus, while a cable is removed elsewhere and the list is saved', async () => {

        const root    = await opened();
        const browser = chromeTakesTheFocus(root);
        const typed   = fieldOf(root, 1, 'meterSerialNumber');

        type(typed, 'SN-0815');
        typed.focus();

        cardOf(root, 0).querySelector<HTMLButtonElement>('[data-remove-connector]')!.click();
        root.querySelector<HTMLButtonElement>('#save')!.click();
        await until(() => saved(root) && held.evses[0].connectors.length === 1, 'the list was not saved');
        browser.disconnect();

        assert.ok(fieldOf(root, 1, 'meterSerialNumber') === typed,  'the field was made anew');
        assert.equal(typed.value,                                   'SN-0815');
        assert.ok(document.activeElement === typed,                 'the focus went');

    });

    it('does not write over a number while it is typed', async () => {

        const root  = await opened();
        const power = fieldOf(root, 0, 'maxPower_kW');

        type(power, '1');
        type(power, '11.');

        assert.equal(fieldOf(root, 0, 'maxPower_kW').value, '11.', 'the number was written over while it was typed');

    });

    it('shows the list as the station took it once it is saved', async () => {

        const root = await opened();

        type(fieldOf(root, 0, 'maxPower_kW'), '11.04');

        root.querySelector<HTMLButtonElement>('#save')!.click();
        await until(() => saved(root) && held.evses[0].maxPower_kW === 11, 'the list was not saved');

        assert.equal(fieldOf(root, 0, 'maxPower_kW').value, '11', 'the field says what was typed, not what the station took');

    });

    it('puts every field back to what the station has when the changes are discarded', async () => {

        const root = await opened();

        type(fieldOf(root, 1, 'physicalReference'), 'Z');
        fieldOf(root, 0, 'operative').click();

        root.querySelector<HTMLButtonElement>('#revert')!.click();
        await until(() => root.querySelector<HTMLButtonElement>('#revert')!.disabled, 'the changes were not discarded');

        assert.equal(fieldOf(root, 1, 'physicalReference').value,  'B',  'what was typed stayed');
        assert.equal(fieldOf(root, 0, 'operative').checked,        true, 'what was clicked stayed');

    });

    it('keeps the card of an EVSE while the one before it is removed', async () => {

        const root   = await opened();
        const second = cardOf(root, 1);

        cardOf(root, 0).querySelector<HTMLButtonElement>('h2 [data-remove]')!.click();
        await until(() => root.querySelectorAll('#evses section.evse').length === 1, 'the EVSE was not removed');

        assert.ok(cardOf(root, 0) === second, 'the card was made anew');
        assert.equal(fieldOf(root, 0, 'physicalReference').value, 'B');

    });

    it('keeps another type of cable typed and not added with its EVSE across a save', async () => {

        const root = await opened();

        type(anotherOf(root, 1), 'cChaoJi');
        type(fieldOf(root, 0, 'meterType'), 'Eastron');

        root.querySelector<HTMLButtonElement>('#save')!.click();
        await until(() => saved(root) && held.evses[0].meterType === 'Eastron', 'the list was not saved');

        assert.equal(anotherOf(root, 1).value, 'cChaoJi', 'what was typed went');

    });

    it('keeps a list the station refused, and says why', async () => {

        const root = await opened();

        refuseChanges = true;
        type(fieldOf(root, 0, 'meterType'), 'Eastron');

        root.querySelector<HTMLButtonElement>('#save')!.click();
        await until(() => root.querySelector('#form-error')?.textContent !== '', 'the refusal was not said');

        assert.equal(root.querySelector('#form-error')!.textContent,
                     "EVSE 1, connector 'sType2': a cable may not be configured for more (30 kW) than the EVSE feeding it (22 kW).");
        assert.equal(fieldOf(root, 0, 'meterType').value, 'Eastron', 'what was typed went');

    });

});
