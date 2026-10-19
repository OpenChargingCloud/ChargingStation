/**
 * The Meters page drawn, in a document of happy-dom, against a stand-in
 * station: a tab for each EVSE's meter; a save in one tab says how that
 * meter is read - where, whom it is believed to be, whom the station is to it
 * to read and to write - and leaves the others; and the Modbus client asks a
 * read and a write as the page says it will, shows what came back down to
 * the registers, and writes only for somebody who may.
 *
 * Run with `npm test`.
 */

import { asked, change, field, open, refused, submit, type, until, type Asked } from '../../test/station.ts';

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

import type { MeterLink, MetersConfiguration, MeterUpdate, ModbusAsk } from '../api/client.ts';

const { metersPage, registerValues } = await import('./meters.ts');
const { unsaved }                    = await import('@node/unsaved');


let held: MetersConfiguration;

function aMeter(EVSE: number, Label: string, Host: string | null): MeterLink {
    return {
        evse:        EVSE,
        label:       Label,
        read:        Host !== null,
        host:        Host,
        port:        Host === null ? null : 802,
        unit:        Host === null ? null : 1,
        server:      Host === null ? null : `${Host}:802`,
        identities:  { read: null, write: null },
        heldTo:      null,
        judgement:   null,
        known:       null
    };
}

function aStation(): MetersConfiguration {
    return {
        meters:      [ aMeter(1, 'A', '10.0.0.5'), aMeter(2, 'B', null) ],
        identities:  [ { id: 'aaaaaaaaaaaaaaaa', label: 'Meter reader', role: 'ReadOnlySunSpec',    subject: 'CN=Reader' },
                       { id: 'bbbbbbbbbbbbbbbb', label: 'Meter writer', role: 'GridServiceSunSpec', subject: 'CN=Writer' } ],
        usage:       'meter',
        defaults:    { port: 802, unit: 1 },
        running:     true,
        file:        'chargingstation.json'
    };
}

const meterOf = (EVSE: number) => held.meters.find(meter => meter.evse === EVSE)!;

function station({ method, path, body }: Asked): unknown {

    if (path === '/configuration/meters' && method === 'PUT') {
        const update = body as MeterUpdate;
        if (update.host === 'nowhere.invalid')
            return refused(400, "'meters.host' is 'nowhere.invalid', which is no host a meter can be reached at.");
        // MetersConfiguration.cs: a blank host reads it nowhere; 802 and 1 where nothing is said.
        const changed: MeterLink = update.host.trim() === ''
                                       ? aMeter(update.evse, meterOf(update.evse).label, null)
                                       : { ...meterOf(update.evse), read: true, host: update.host.trim(), port: update.port ?? 802, unit: update.unit ?? 1,
                                           identities: update.identities,
                                           heldTo: update.certificateFingerprint
                                                       ? { certificate: update.certificateFingerprint, root: null, certificates: [ update.certificateFingerprint ], roots: [],
                                                           onMismatch: 'refuse', trustOnFirstUse: null }
                                                       : null };
        held = { ...held, meters: held.meters.map(meter => meter.evse === update.evse ? changed : meter) };
        return held;
    }

    if (path === '/configuration/meters')
        return held;

    if (path === '/status/meters')
        return { meters: [ { evse: 1, label: 'A', endpoint: { host: '10.0.0.5', port: 802, unit: 1 }, state: 'noIdentity',
                             since: '2026-10-19T10:00:00.000Z', error: 'There is no TLS identity in this station\'s certificate store whose certificate carries a SunSpec role.' },
                           { evse: 2, label: 'B', endpoint: { host: '10.0.0.6', port: 802, unit: 1 }, state: 'reading', since: '2026-10-19T10:00:00.000Z', error: null,
                             connection: { protocol: 'Tls13', cipherSuite: 'TLS_AES_128_GCM_SHA256', meter: null, judged: 'accepted',
                                           identity: { id: 'aaaaaaaaaaaaaaaa', label: 'Meter reader', role: 'ReadOnlySunSpec', subject: 'CN=Reader' },
                                           sunSpec: { base: 40000, models: [ { id: 1, address: 40002, length: 66 }, { id: 101, address: 40070, length: 50 }, { id: 203, address: 40122, length: 105 } ], meter: 203 } },
                             reading: null } ] };

    if (path === '/meters/modbus' && method === 'POST') {
        const ask = body as ModbusAsk;
        return ask.function === 3
                   ? { at: '2026-10-19T10:00:00.000Z', endpoint: { host: '10.0.0.5', port: 802, unit: 1 }, function: 3, name: 'Read Holding Registers',
                       operation: 'read', address: ask.address, count: ask.count, request: '039C400002', response: '030453756E53', outcome: 'answered',
                       identity: { id: 'aaaaaaaaaaaaaaaa', label: 'Meter reader', role: 'ReadOnlySunSpec', subject: 'CN=Reader' },
                       registers: [ { address: 40000, value: 0x5375 }, { address: 40001, value: 0x6E53 } ], ms: 12.5 }
                   : { at: '2026-10-19T10:00:00.000Z', endpoint: { host: '10.0.0.5', port: 802, unit: 1 }, function: ask.function, name: 'Write Multiple Registers',
                       operation: 'write', address: ask.address, count: ask.values?.length ?? 1, request: '109C9E0002040001CAFE', response: '109C9E0002',
                       outcome: 'exception', exception: { code: 1, name: 'IllegalFunction' },
                       identity: { id: 'bbbbbbbbbbbbbbbb', label: 'Meter writer', role: 'GridServiceSunSpec', subject: 'CN=Writer' } };
    }

    return undefined;

}

const everything = [ 'meters:read', 'meters:edit', 'meters:run' ];

async function opened(Permissions = everything, Station = aStation()): Promise<HTMLElement> {
    held = Station;
    return open(metersPage, '/configuration/meters', Permissions, station, root => root.querySelector('#panel-evse-1') !== null);
}

const form    = (EVSE: number) => `#meter-form-${EVSE}`;
const client  = (EVSE: number) => `#modbus-form-${EVSE}`;
const panel   = (root: HTMLElement, EVSE: number) => root.querySelector<HTMLElement>(`#panel-evse-${EVSE}`)!;
const saved   = (root: HTMLElement, EVSE = 1) => root.querySelector(`${form(EVSE)} .form-notice`)?.textContent === 'Saved.';
const sent    = <T>(Path: string) => asked.filter(one => one.path === Path && one.method !== 'GET').map(one => one.body as T);


describe('the Meters page', () => {

    it('has a tab for each EVSE\'s meter, opens on the first, and offers the identities of a SunSpec role to read and to write', async () => {

        const root = await opened();

        assert.deepEqual([...root.querySelectorAll('[role="tab"]')].map(tab => tab.textContent!.trim()), [ 'EVSE A', 'EVSE B' ]);
        assert.equal(panel(root, 1).hidden, false);
        assert.equal(panel(root, 2).hidden, true);

        const writers = [...field<HTMLSelectElement>(root, form(1), 'writeIdentity').options].map(option => option.textContent!.trim());

        assert.deepEqual(writers, [ 'nobody - nothing is written to it', 'Meter reader (ReadOnlySunSpec)', 'Meter writer (GridServiceSunSpec)' ]);
        assert.match(panel(root, 1).querySelector('.meter-error')!.textContent!, /SunSpec role/, 'how the meter goes now is not said');

    });

    it('says which SunSpec models a meter has, and which one it is read by', async () => {

        const station = aStation();
        station.meters[1] = aMeter(2, 'B', '10.0.0.6');

        const root = await opened(everything, station);

        assert.equal(panel(root, 2).querySelector('.meter-sunspec')!.textContent!.replace(/\s+/g, ' ').trim(), 'from 40000, models 1, 101, 203 - read by 203');

    });

    it('saves how the meter of the EVSE whose tab it is is read, and leaves the others as they were', async () => {

        const root = await opened();

        root.querySelector<HTMLButtonElement>('#tab-evse-2')!.click();

        type(field(root, form(2), 'host'), ' meter-b.local ');
        change(field<HTMLSelectElement>(root, form(2), 'readIdentity'),  'aaaaaaaaaaaaaaaa');
        change(field<HTMLSelectElement>(root, form(2), 'writeIdentity'), 'bbbbbbbbbbbbbbbb');
        type(field<HTMLTextAreaElement>(root, form(2), 'pinCertificates'), 'AB'.repeat(32));

        submit(root, form(2));
        await until(() => saved(root, 2) && meterOf(2).read, 'the second EVSE\'s meter was not saved');

        const update = sent<MeterUpdate>('/configuration/meters')[0];

        assert.equal(update.evse,                    2);
        assert.equal(update.host,                    'meter-b.local');
        assert.equal(update.port,                    null, 'a port not typed was sent as something');
        assert.deepEqual(update.identities,          { read: 'aaaaaaaaaaaaaaaa', write: 'bbbbbbbbbbbbbbbb' });
        assert.equal(update.certificateFingerprint,  'ab'.repeat(32));
        assert.deepEqual(update.pinsAsShown,         {}, 'what the page showed it held to was not said');
        assert.equal(meterOf(1).host,                '10.0.0.5', 'the first EVSE\'s meter was changed by a save of the second');

        assert.equal(field(root, form(2), 'port').value, '802', 'the port the station took is not shown');

    });

    it('reads a meter nowhere once its host is cleared, and keeps a refusal typed with why', async () => {

        const root = await opened();

        type(field(root, form(1), 'host'), 'nowhere.invalid');
        submit(root, form(1));

        await until(() => (root.querySelector(`${form(1)} .form-error`)?.textContent ?? '') !== '', 'the refusal was not said');

        assert.match(root.querySelector(`${form(1)} .form-error`)!.textContent!, /no host a meter can be reached at/);
        assert.equal(field(root, form(1), 'host').value, 'nowhere.invalid', 'what was typed went');

        type(field(root, form(1), 'host'), '');
        submit(root, form(1));

        await until(() => saved(root) && !meterOf(1).read, 'the meter is still read');

    });

    it('reads registers as the identity to read with, and shows them down to the hexadecimal and the text', async () => {

        const root = await opened();

        field(root, client(1), 'address').value = '40000';
        field(root, client(1), 'count').value   = '2';

        assert.match(root.querySelector(`${client(1)} .modbus-as`)!.textContent!, /least role that may read/);

        submit(root, client(1));
        await until(() => panel(root, 1).querySelector('table.modbus-registers') !== null, 'what came back was not drawn');

        assert.deepEqual(sent<ModbusAsk>('/meters/modbus'), [ { evse: 1, function: 3, address: 40000, count: 2 } ]);

        const rows = [...panel(root, 1).querySelectorAll('.table-scroll table.modbus-registers tbody tr')].map(row => row.textContent!.replace(/\s+/g, ' ').trim());

        assert.deepEqual(rows, [ '40000 21365 0x5375 21365 Su', '40001 28243 0x6E53 28243 nS' ]);
        assert.match(panel(root, 1).textContent!, /03 9C 40 00 02/, 'what was sent is not shown');
        assert.match(panel(root, 1).textContent!, /Meter reader \(ReadOnlySunSpec\)/);

    });

    it('writes several registers as the identity to write with, and says what the meter refused', async () => {

        const writes = aStation();
        writes.meters[0].identities = { read: null, write: 'bbbbbbbbbbbbbbbb' };

        const root = await opened(everything, writes);

        change(field<HTMLSelectElement>(root, client(1), 'function'), '16');

        await until(() => root.querySelector(`${client(1)} [name="values"]`) !== null, 'the values to write are not asked for');

        assert.match(root.querySelector(`${client(1)} .modbus-as`)!.textContent!, /Written as Meter writer \(GridServiceSunSpec\)/);

        field(root, client(1), 'address').value = '40094';
        type(field(root, client(1), 'values'), '1, 0xCAFE');

        submit(root, client(1));
        await until(() => panel(root, 1).querySelector('.modbus-answer') !== null, 'what came back was not drawn');

        assert.deepEqual(sent<ModbusAsk>('/meters/modbus'), [ { evse: 1, function: 16, address: 40094, values: [ 1, 0xCAFE ] } ]);
        assert.match(panel(root, 1).querySelector('.modbus-answer')!.textContent!, /refused: IllegalFunction \(1\)/);

    });

    it('writes nothing for somebody who may only read the meters, and refuses values that are none before asking', async () => {

        const reader = await opened([ 'meters:read', 'meters:edit' ]);

        change(field<HTMLSelectElement>(reader, client(1), 'function'), '6');
        await until(() => reader.querySelector(`${client(1)} [name="values"]`) !== null, 'the value to write is not asked for');

        assert.equal(reader.querySelector<HTMLButtonElement>(`${client(1)} button[type="submit"]`)!.disabled, true,
                     'somebody who may not write to a meter is offered a write');

        const writer = await opened();

        change(field<HTMLSelectElement>(writer, client(1), 'function'), '6');
        await until(() => writer.querySelector(`${client(1)} [name="values"]`) !== null, 'the value to write is not asked for');

        type(field(writer, client(1), 'values'), '0x1FFFF');
        submit(writer, client(1));

        await until(() => (writer.querySelector(`${client(1)} .form-error`)?.textContent ?? '') !== '', 'the value that is none was not refused');

        assert.equal(sent<ModbusAsk>('/meters/modbus').length, 0, 'a value that is none was sent');

    });

    it('is held by what is typed and not saved or asked, and not by a request that was asked', async () => {

        const root = await opened();

        type(field(root, client(1), 'address'), '40070');
        assert.equal(unsaved.any(), true, 'a request typed and not asked does not hold the page');

        submit(root, client(1));
        await until(() => panel(root, 1).querySelector('table.modbus-registers') !== null, 'what came back was not drawn');

        assert.equal(unsaved.any(), false, 'a request that was asked holds the page');
        assert.equal(field(root, client(1), 'address').value, '40070', 'the request asked went from its fields');

        type(field(root, form(1), 'host'), '10.0.0.6');
        assert.equal(unsaved.any(), true, 'a change to how the meter is read does not hold the page');

    });

});


describe('the register values typed into a write', () => {

    it('are decimal or hexadecimal, separated by commas or blanks', () => {
        assert.deepEqual(registerValues('1, 0xCAFE 65535;0x0'), [ 1, 0xCAFE, 65535, 0 ]);
    });

    it('are refused where one is none, or there are none', () => {
        assert.match(registerValues('1, 65536')   as string, /'65536' is no register value/);
        assert.match(registerValues('-1')         as string, /'-1' is no register value/);
        assert.match(registerValues('  ')         as string, /Say what is written/);
    });

});
