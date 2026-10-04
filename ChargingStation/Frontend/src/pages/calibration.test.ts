/**
 * The Calibration page drawn, in a document of happy-dom, against a stand-in
 * station: a certificate half written into the add form, and its focus,
 * outlive another being removed and the list being saved, as a browser takes
 * the focus away while the page is held still; one added empties the form;
 * "Discard changes" takes what is half written with it; certificates keep
 * their card while another goes; a list refused stays, with why.
 *
 * Run with `npm test`.
 */

import { field, open, refused, submit, until, type Asked } from '../../test/station.ts';
import { chromeTakesTheFocus } from '@node/../test/dom.ts';

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

import type { CalibrationCertificate, CalibrationConfiguration, CalibrationCertificateUpdate } from '../api/client.ts';

const { calibrationPage } = await import('./calibration.ts');


let held: CalibrationConfiguration;

/** Refuses every change where told to. */
let refuseChanges = false;

function aCertificate(id: string): CalibrationCertificate {
    return {
        id,
        description:       `the meter of ${id}`,
        pem:               `-----BEGIN CERTIFICATE-----\nMIIB${id}\n-----END CERTIFICATE-----`,
        subject:           `CN=${id}`,
        issuer:            'CN=PTB',
        serialNumber:      '01',
        notBefore:         '2026-01-01T00:00:00Z',
        notAfter:          '2030-01-01T00:00:00Z',
        thumbprintSHA256:  'ab'.repeat(32),
        expired:           false,
        notYetValid:       false,
        daysLeft:          1180
    };
}

function aStation(): CalibrationConfiguration {
    return {
        certificates:  [ aCertificate('meter-evse-1'), aCertificate('meter-evse-2') ],
        limits:        { maxCertificates: 8, maxIdLength: 64, maxDescriptionLength: 120, maxPEMLength: 16384, expiryWarningDays: 30 },
        file:          'chargingstation.json'
    };
}

function station({ method, path, body }: Asked): unknown {

    if (path === '/configuration/calibration' && method === 'PUT') {
        if (refuseChanges)
            return refused(400, "The calibration certificate id 'meter 3' may hold only letters, digits, '-', '_' and '.'.");
        // A station reads the rest out of the PEM.
        const { certificates } = body as { certificates: CalibrationCertificateUpdate[] };
        held = { ...held, certificates: certificates.map(given => ({ ...aCertificate(given.id), description: given.description ?? null, pem: given.pem })) };
        return held;
    }

    if (path === '/configuration/calibration')
        return held;

    return undefined;

}

async function opened(): Promise<HTMLElement> {
    held          = aStation();
    refuseChanges = false;
    return open(calibrationPage, '/configuration/calibration', [ 'calibration:read', 'calibration:edit' ],
                station, root => root.querySelector('#add-form') !== null);
}

const named  = (root: HTMLElement) => field(root, '#add-form', 'id');
const pem    = (root: HTMLElement) => field<HTMLTextAreaElement>(root, '#add-form', 'pem');
const card   = (root: HTMLElement, id: string) => [...root.querySelectorAll<HTMLElement>('section.certificate')].
                                                      find(one => one.querySelector('h2')?.textContent?.includes(id));
const remove = (root: HTMLElement, id: string) => card(root, id)!.querySelector<HTMLButtonElement>('[data-remove]')!.click();
const saved  = (root: HTMLElement) => root.querySelector('#form-note')?.textContent === 'Saved.';


describe('the Calibration page', () => {

    it('keeps what is half written into the add form, and its focus, while another certificate goes and the list is saved', async () => {

        const root    = await opened();
        const browser = chromeTakesTheFocus(root);
        const typed   = pem(root);

        named(root).value = 'meter-evse-3';
        typed.value       = '-----BEGIN CERTIFICATE-----\nhalf';
        typed.focus();

        remove(root, 'meter-evse-2');
        root.querySelector<HTMLButtonElement>('#save')!.click();
        await until(() => saved(root) && held.certificates.length === 1, 'the list was not saved');
        browser.disconnect();

        assert.ok(pem(root) === typed,               'the field was made anew');
        assert.equal(typed.value,                    '-----BEGIN CERTIFICATE-----\nhalf');
        assert.equal(named(root).value,              'meter-evse-3');
        assert.ok(document.activeElement === typed,  'the focus went');

    });

    it('keeps the card of a certificate while the one before it is removed', async () => {

        const root   = await opened();
        const second = card(root, 'meter-evse-2');

        remove(root, 'meter-evse-1');
        await until(() => card(root, 'meter-evse-1') === undefined, 'the certificate was not removed');

        assert.ok(card(root, 'meter-evse-2') === second, 'the card was made anew');

    });

    it('empties the add form once a certificate is added, and shows what the station read out of it once saved', async () => {

        const root = await opened();

        named(root).value = 'meter-evse-3';
        pem(root).value   = '-----BEGIN CERTIFICATE-----\nMIIBmeter-evse-3\n-----END CERTIFICATE-----';

        submit(root, '#add-form');
        await until(() => card(root, 'meter-evse-3') !== undefined, 'the certificate was not added');

        assert.equal(named(root).value,  '', 'the form still holds what was added');
        assert.equal(pem(root).value,    '');

        root.querySelector<HTMLButtonElement>('#save')!.click();
        await until(() => saved(root), 'the list was not saved');

        assert.match(card(root, 'meter-evse-3')!.textContent ?? '', /CN=meter-evse-3/, 'what the station read out of the PEM is not shown');

    });

    it('takes what is half written with it when the changes are discarded', async () => {

        const root = await opened();

        remove(root, 'meter-evse-2');
        named(root).value = 'meter-evse-3';

        root.querySelector<HTMLButtonElement>('#revert')!.click();
        await until(() => card(root, 'meter-evse-2') !== undefined, 'the changes were not discarded');

        assert.equal(named(root).value, '', 'what was half written stayed');

    });

    it('keeps a list the station refused, and says why', async () => {

        const root = await opened();

        refuseChanges = true;
        remove(root, 'meter-evse-2');

        root.querySelector<HTMLButtonElement>('#save')!.click();
        await until(() => root.querySelector('#form-error')?.textContent !== '', 'the refusal was not said');

        assert.equal(root.querySelector('#form-error')!.textContent,
                     "The calibration certificate id 'meter 3' may hold only letters, digits, '-', '_' and '.'.");
        assert.equal(card(root, 'meter-evse-2'),                                 undefined, 'what was changed went');
        assert.equal(root.querySelector<HTMLButtonElement>('#save')!.disabled,  false,     'there is nothing left to save');

    });

});
