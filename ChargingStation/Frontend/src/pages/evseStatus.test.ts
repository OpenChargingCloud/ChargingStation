/**
 * The EVSEs page drawn, in a document of happy-dom, against a stand-in
 * station: a tab for each EVSE, named by what is written on its housing; the
 * one chosen kept in the address; what the station says on its event stream
 * drawn as it comes, without asking again; and a station without EVSEs, or one
 * that cannot be asked, said so.
 *
 * Run with `npm test`.
 */

import { asked, leave, open, refused, until, type Asked } from '../../test/station.ts';

import { strict as assert }       from 'node:assert';
import { afterEach, describe, it } from 'node:test';

import type { EVSEStatus } from '../api/client.ts';


/** The stream the page follows, as a test drives it. */
class Stream {

    static latest: Stream | null = null;

    readyState = 0;

    private readonly listeners = new Map<string, ((event: unknown) => void)[]>();

    readonly url: string;

    constructor(url: string) {
        this.url      = url;
        Stream.latest = this;
    }

    addEventListener(name: string, listener: (event: unknown) => void): void {
        this.listeners.set(name, [...(this.listeners.get(name) ?? []), listener]);
    }

    close(): void {
        this.readyState = 2;
    }

    /** An event the station publishes. */
    publish(name: string, data: unknown): void {
        for (const listener of this.listeners.get(name) ?? [])
            listener({ data: JSON.stringify(data) });
    }

}

(globalThis as unknown as { EventSource: unknown }).EventSource = Stream;

const { logs }            = await import('@node/logs/store');
const { evseStatusPage }  = await import('./evseStatus.ts');


function anEVSE(Id: number, Label: string, More: Partial<EVSEStatus> = {}): EVSEStatus {
    return {
        id:                 Id,
        label:              Label,
        physicalReference:  Label,
        status:             'available',
        operative:          true,
        closing:            false,
        maxPower_kW:        22,
        connectors:         [ { id: 1, type: 'sType2', maxPower_kW: 22 } ],
        session:            null,
        reservation:        null,
        meter:              null,
        ...More
    };
}

let evses: EVSEStatus[] = [];

function station({ method, path }: Asked): unknown {

    if (method === 'GET' && path === '/status/evses')
        return { timestamp: '2026-10-19T10:00:00.000Z', evses };

    return refused(404, `Nothing at ${method} ${path}`);

}

const panel = (root: HTMLElement, id: number) => root.querySelector<HTMLElement>(`#panel-evse-${id}`);

afterEach(() => {
    leave();
    logs.stop();
});


describe('the EVSEs page', () => {

    it('has a tab for every EVSE, named by its physical reference, and opens on the first', async () => {

        evses = [ anEVSE(1, 'A'), anEVSE(2, 'B', { status: 'inoperative', operative: false }) ];

        const root = await open(evseStatusPage, '/evses', [ 'evses:read' ], station, root => panel(root, 2) !== null);

        const tabs = [...root.querySelectorAll('[role="tab"]')].map(tab => tab.textContent!.trim());

        assert.deepEqual(tabs, [ 'EVSE A', 'EVSE B' ]);
        assert.equal(panel(root, 1)!.hidden, false);
        assert.equal(panel(root, 2)!.hidden, true);
        assert.match(panel(root, 1)!.textContent!, /Available/);
        assert.match(panel(root, 2)!.textContent!, /Out of service/);
        assert.deepEqual(asked.map(request => request.path), [ '/status/evses' ]);

    });

    it('shows the tab chosen, and keeps it in the address', async () => {

        evses = [ anEVSE(1, 'A'), anEVSE(2, 'B') ];

        const root = await open(evseStatusPage, '/evses', [ 'evses:read' ], station, root => panel(root, 2) !== null);

        root.querySelector<HTMLButtonElement>('#tab-evse-2')!.click();

        assert.equal(panel(root, 1)!.hidden, true);
        assert.equal(panel(root, 2)!.hidden, false);
        assert.equal(new URL(location.href).searchParams.get('tab'), 'evse-2');

    });

    it('opens on the EVSE the address names', async () => {

        evses = [ anEVSE(1, 'A'), anEVSE(2, 'B') ];

        const root = await open(evseStatusPage, '/evses?tab=evse-2', [ 'evses:read' ], station, root => panel(root, 2) !== null);

        assert.equal(panel(root, 2)!.hidden, false);
        assert.equal(panel(root, 1)!.hidden, true);

    });

    it('draws what the station says on its stream as it comes, without asking again', async () => {

        evses = [ anEVSE(1, 'A'), anEVSE(2, 'B') ];

        logs.start();

        const root = await open(evseStatusPage, '/evses', [ 'evses:read' ], station, root => panel(root, 1) !== null);

        Stream.latest!.publish('evses', {
            evses: [
                anEVSE(1, 'A', {
                    status:   'occupied',
                    session:  { method: 'RFID', startedAt: '2026-10-19T09:58:00.000Z', provider: { name: 'Elektro Mobil GmbH', logo: null } }
                }),
                anEVSE(2, 'B')
            ]
        });

        await until(() => /Charging/.test(panel(root, 1)!.textContent!), 'the session that started was not drawn');

        assert.match(panel(root, 1)!.textContent!, /Elektro Mobil GmbH/);
        assert.equal(asked.length, 1, 'the page asked the station again instead of taking what it said');

    });

    it('says what is written down about a meter, and that nothing is read from it', async () => {

        evses = [ anEVSE(1, 'A', { meter: { type: 'SunSpec 213', serialNumber: 'EnergyMeter01' } }) ];

        const root = await open(evseStatusPage, '/evses', [ 'evses:read' ], station, root => panel(root, 1) !== null);

        assert.match(panel(root, 1)!.textContent!, /EnergyMeter01/);
        assert.match(panel(root, 1)!.textContent!, /reads no meter yet/);

    });

    it('says where EVSEs are added, where there are none', async () => {

        evses = [];

        const root = await open(evseStatusPage, '/evses', [ 'evses:read' ], station, root => /No EVSEs/.test(root.textContent ?? ''));

        assert.ok(root.querySelector('a[href$="/configuration/evses"]'), 'no link to where EVSEs are added');

    });

    it('says so when the station cannot be asked', async () => {

        const root = await open(evseStatusPage, '/evses', [ 'evses:read' ],
                                () => refused(500, 'The station is busy.'),
                                root => root.querySelector('.error-box') !== null);

        assert.match(root.querySelector('.error-box')!.textContent!, /The station is busy/);

    });

});
