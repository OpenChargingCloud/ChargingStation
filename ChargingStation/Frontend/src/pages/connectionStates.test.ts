/**
 * What the Connections page says beside a connection about where it stands.
 *
 * Run with `npm test`. What is pinned is what the page must not get wrong
 * while somebody relies on it: how long and how soon are counted on the
 * station's clock and not the browser's, a connection changed after the start
 * is said to be dialled as it was, and one removed but still dialled is not
 * left out.
 */

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

import type { ConnectionState } from '../api/client';
import { between, noLongerWrittenDown, stateLine } from './connectionStates.ts';


const dialled = { url: 'ws://csms.example.org/cs001', ocppVersion: 'OCPP2.1', autoConnect: true };

/** A state as the station sends one, dialled as above, since midnight 2020. */
const state = (status: ConnectionState['status'], more: Partial<ConnectionState> = {}): ConnectionState =>
    ({
        status,
        since:        '2020-01-01T00:00:00.000Z',
        said:         `What the station said when it became ${status}.`,
        description:  'CSMS',
        url:          dialled.url,
        ocppVersion:  dialled.ocppVersion,
        ...more
    });


describe('how long', () => {

    it('is said in the words somebody would use', () => {

        assert.equal(between('2020-01-01T00:00:00Z', '2020-01-01T00:00:42Z'), '42 s');
        assert.equal(between('2020-01-01T00:00:00Z', '2020-01-01T00:05:59Z'), '5 min');
        assert.equal(between('2020-01-01T00:00:00Z', '2020-01-01T02:03:00Z'), '2 h 3 min');
        assert.equal(between('2020-01-01T00:00:00Z', '2020-01-02T04:00:00Z'), '1 d 4 h');

    });

    it('is never less than nothing, whatever two clocks make of it', () => {

        assert.equal(between('2020-01-01T00:00:10Z', '2020-01-01T00:00:00Z'), '0 s');

    });

});


describe('where a connection stands', () => {

    it('is counted on the station\'s clock, which is the one that was sent along', () => {

        // Years before the browser's own clock: counted on that, it would say so.
        const line = stateLine(dialled, state('connected'), '2020-01-01T00:05:00.000Z');

        assert.equal(line?.chip, 'connected');
        assert.equal(line?.tone, 'on');
        assert.equal(line?.when, 'for 5 min');

    });

    it('lost: waited out, and when the next attempt is', () => {

        const line = stateLine(dialled,
                               state('lost', { attempt: 3, nextAttemptAt: '2020-01-01T00:05:08.000Z' }),
                               '2020-01-01T00:05:00.000Z');

        assert.equal(line?.chip, 'lost');
        assert.equal(line?.tone, 'warn');
        assert.equal(line?.when, 'for 5 min, and tried again by itself; attempt 3 in 8 s');

    });

    it('an attempt that is due is not said to be in the past', () => {

        const line = stateLine(dialled,
                               state('trying', { attempt: 2, nextAttemptAt: '2020-01-01T00:04:59.000Z' }),
                               '2020-01-01T00:05:00.000Z');

        assert.equal(line?.chip, 'not reached');
        assert.equal(line?.when, 'for 5 min, and tried again by itself; attempt 2 is being made');

    });

    it('refused: not tried again, which asks something else of whoever is looking', () => {

        const line = stateLine(dialled, state('refused'), '2020-01-01T00:00:30.000Z');

        assert.equal(line?.chip, 'refused');
        assert.equal(line?.tone, 'bad');
        assert.equal(line?.when, 'for 30 s, and not tried again');
        assert.equal(line?.said, 'What the station said when it became refused.');

    });

    it('one that is only written down gets no line at all', () => {

        assert.equal(stateLine({ ...dialled, autoConnect: false }, undefined, '2020-01-01T00:00:00.000Z'), null);

    });

    it('one set to connect by itself that was not dialled is said to wait for the next start', () => {

        const line = stateLine(dialled, undefined, '2020-01-01T00:00:00.000Z');

        assert.equal(line?.chip, 'not dialled');
        assert.match(line?.said ?? '', /next start/);

    });

});


describe('a connection changed since the start', () => {

    it('is said to be dialled as it was', () => {

        const line = stateLine({ ...dialled, url: 'ws://elsewhere.example.org/cs001' },
                               state('connected'),
                               '2020-01-01T00:00:10.000Z');

        assert.equal(line?.notes.length, 1);
        assert.match(line!.notes[0]!, /Dialled as ws:\/\/csms\.example\.org\/cs001 \(OCPP2\.1\).*next start/);

    });

    it('and so is the other node', () => {

        const line = stateLine({ ...dialled, ocppVersion: 'OCPP1.6' }, state('connected'), '2020-01-01T00:00:10.000Z');

        assert.equal(line?.notes.length, 1);

    });

    it('switched off, it is still on until the next start', () => {

        const line = stateLine({ ...dialled, autoConnect: false }, state('lost'), '2020-01-01T00:00:10.000Z');

        assert.deepEqual(line?.notes, [ 'No longer set to connect by itself. That takes effect at the next start - ' +
                                        'until then it stays as it is.' ]);

    });

    it('and one that is left as it was needs no note', () => {

        assert.deepEqual(stateLine(dialled, state('connected'), '2020-01-01T00:00:10.000Z')?.notes, []);

    });

});


describe('a connection removed since the start', () => {

    it('is listed while the station still dials it', () => {

        const orphans = noLongerWrittenDown([ { id: 'kept' } ],
                                            { kept: state('connected'), removed: state('lost') });

        assert.deepEqual(orphans.map(([ id ]) => id), [ 'removed' ]);

    });

});
