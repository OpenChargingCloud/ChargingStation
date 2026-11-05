/**
 * The Nameplate page drawn, in a document of happy-dom, against a stand-in
 * station: what is saved is the whole section, a blank field sent as the
 * default; a refusal keeps what is typed and says why; somebody who may only
 * read it is told so and can change nothing; and where each OCPP node stands
 * with its CSMS is said in words.
 *
 * Run with `npm test`.
 */

import { field, open, refused, submit, until, type Asked } from '../../test/station.ts';

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

import type { NameplateConfiguration, NameplateUpdate, Registration } from '../api/client.ts';

const { nameplatePage } = await import('./nameplate.ts');


let held: NameplateConfiguration;

/** What the last save sent, the whole section. */
let sent: Partial<NameplateUpdate> | undefined;

/** Refuses every change where told to. */
let refuseChanges = false;

const notAsked: Registration = { state: 'NotAsked', since: null, interval: null, nextTry: null, problem: null };

function aStation(): NameplateConfiguration {
    return {
        vendor:           null,
        model:            null,
        serialNumber:     null,
        firmwareVersion:  null,
        said:             { vendor: 'GraphDefined', model: 'ChargingStationCLI', serialNumber: null, firmwareVersion: '0.1.0+62e7b65' },
        limits:           { vendor: 20, model: 20, serialNumber: 25, firmwareVersion: 50 },
        registration:     { '1.6': notAsked, '2.1': notAsked },
        file:             'chargingstation.json'
    };
}

function station({ method, path, body }: Asked): unknown {

    if (path === '/configuration/nameplate' && method === 'PUT') {
        if (refuseChanges)
            return refused(400, "'nameplate.vendor' may hold nothing but printable ASCII - it is all OCPP 1.6 takes.");
        const update = body as NameplateUpdate;
        sent = update;
        held = {
            ...held,
            ...update,
            said: {
                vendor:           update.vendor          ?? 'GraphDefined',
                model:            update.model           ?? 'ChargingStationCLI',
                serialNumber:     update.serialNumber,
                firmwareVersion:  update.firmwareVersion ?? '0.1.0+62e7b65'
            }
        };
        return held;
    }

    if (path === '/configuration/nameplate')
        return held;

    return undefined;

}

async function opened(Station: Partial<NameplateConfiguration> = {},
                      Permissions = [ 'nameplate:read', 'nameplate:edit' ]): Promise<HTMLElement> {
    held          = { ...aStation(), ...Station };
    sent          = undefined;
    refuseChanges = false;
    return open(nameplatePage, '/configuration/nameplate', Permissions,
                station, root => root.querySelector('#nameplate-form') !== null);
}

const plate = (root: HTMLElement, name: keyof NameplateUpdate) => field(root, '#nameplate-form', name);


describe('the Nameplate page', () => {

    it('sends the whole section, a blank field as the default, and says what the station took', async () => {

        const root = await opened({ firmwareVersion: 'fw-1.0' });

        plate(root, 'vendor').value           = ' Test Vendor ';
        plate(root, 'serialNumber').value     = 'SN-4711';
        plate(root, 'firmwareVersion').value  = '';

        submit(root, '#nameplate-form');
        await until(() => /^Saved/.test(root.querySelector('#form-note')?.textContent ?? ''), 'the nameplate was not saved');

        assert.deepEqual(sent, { vendor: 'Test Vendor', model: null, serialNumber: 'SN-4711', firmwareVersion: null });
        assert.match(root.textContent!.replace(/\s+/g, ' '), /Test Vendor ChargingStationCLI, firmware 0\.1\.0\+62e7b65, serial number SN-4711/);
        assert.equal(plate(root, 'vendor').value, 'Test Vendor', 'the field does not say what the station took');

    });

    it('keeps what is typed into a nameplate the station refused, and says why', async () => {

        const root = await opened();

        refuseChanges = true;
        plate(root, 'vendor').value = 'Müller';

        submit(root, '#nameplate-form');
        await until(() => root.querySelector('#form-error')?.textContent !== '', 'the refusal was not said');

        assert.match(root.querySelector('#form-error')!.textContent!, /printable ASCII/);
        assert.equal(plate(root, 'vendor').value, 'Müller', 'what was typed went');

    });

    it('holds each field to what OCPP 1.6 takes, and offers the defaults', async () => {

        const root = await opened();

        assert.deepEqual([ plate(root, 'vendor').maxLength, plate(root, 'model').maxLength,
                           plate(root, 'serialNumber').maxLength, plate(root, 'firmwareVersion').maxLength ],
                         [ 20, 20, 25, 50 ]);
        assert.equal(plate(root, 'vendor').placeholder,           'GraphDefined');
        assert.equal(plate(root, 'firmwareVersion').placeholder,  '0.1.0+62e7b65');

    });

    it('tells somebody who may only read it so, and lets them change nothing', async () => {

        const root = await opened({}, [ 'nameplate:read' ]);

        assert.match(root.querySelector('.notice')!.textContent!, /may look at the nameplate but not change it/);
        assert.equal(plate(root, 'vendor').disabled, true);
        assert.equal(root.querySelector<HTMLButtonElement>('#nameplate-form button[type="submit"]')!.disabled, true);

    });

    it('says where each OCPP node stands with its CSMS', async () => {

        const root = await opened({ registration: {
                         '1.6': { state: 'Accepted', since: new Date().toISOString(), interval: 300, nextTry: null, problem: null },
                         '2.1': { state: 'Unanswered', since: null, interval: 60, nextTry: new Date().toISOString(), problem: 'timeout' }
                     } });

        const rows = [...root.querySelectorAll('.table-scroll #registration tbody tr')].map(row => row.textContent!.replace(/\s+/g, ' ').trim());

        assert.equal(rows.length, 2);
        assert.match(rows[0]!, /^1\.6 Accepted, a heartbeat every 300 s\./);
        assert.match(rows[1]!, /^2\.1 No answer \(timeout\) - asks again in 60 s/);

    });

});
