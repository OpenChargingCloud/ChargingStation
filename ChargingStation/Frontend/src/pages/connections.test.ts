/**
 * The Connections page drawn, in a document of happy-dom, against a stand-in
 * station: a connection half written, and its focus, outlive another being
 * saved, as a browser takes the focus away while the page is held still, and
 * where the connections stand being asked again; one written down empties the
 * form; one saved says what the station took; the card after one removed
 * stays; a connection refused stays typed, with why; the test dialog says
 * what came of it and no longer that it is connecting.
 *
 * Run with `npm test`.
 */

import { field, left, open, refused, submit, until, type Asked } from '../../test/station.ts';
import { chromeTakesTheFocus } from '@node/../test/dom.ts';

import { strict as assert }     from 'node:assert';
import { after, describe, it }  from 'node:test';

import type { ConnectionState, ConnectionToSave, StationConnection, StationConnections } from '../api/client.ts';

const { connectionsPage } = await import('./connections.ts');


let held: StationConnections;

/** Where the stand-in says the connections stand, asked on their own. */
let standing: Record<string, ConnectionState> = {};

function aConnection(id: string, more: Partial<StationConnection> = {}): StationConnection {
    return { id, description: `CSMS ${id}`, url: `wss://csms.example.org/${id}`, connectionType: 'CSMS', ocppVersion: 'OCPP2.1',
             autoConnect: true, secure: true, createdAt: '2026-10-01T10:00:00Z', ...more };
}

function aState(status: ConnectionState['status'], connection: StationConnection): ConnectionState {
    return { status, since: '2026-10-04T11:59:00Z', said: `It is ${status}.`, description: connection.description,
             url: connection.url, ocppVersion: connection.ocppVersion };
}

function aStation(): StationConnections {
    const connections = [ aConnection('c1'), aConnection('c2') ];
    return {
        directory:              'connections',
        authentications:        [],
        connections,
        certificates:           [],
        connectionTypes:        [ 'CSMS', 'CSMSBackup', 'LocalController' ],
        ocppVersions:           [ 'OCPP2.1', 'OCPP1.6' ],
        maxDescriptionLength:   120,
        minSharedSecretLength:  16,
        secretsAreReadable:     false,
        totpDefaults:           { validitySeconds: 30, length: 12, alphabet: 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567', hashAlgorithm: 'SHA256' },
        timestamp:              '2026-10-04T12:00:00Z',
        states:                 { c1: aState('connected', connections[0]), c2: aState('trying', connections[1]) }
    };
}

function station({ method, path, body }: Asked): unknown {

    if (path === '/configuration/connections' && method === 'POST') {
        const entry = body as ConnectionToSave;
        // ConnectionEntry.TryParse
        if (entry.url.trim() === '')
            return refused(400, 'A connection needs somewhere to go.');
        const made = aConnection('c3', { description: entry.description, url: entry.url });
        held = { ...held, connections: [ made, ...held.connections ] };
        return { id: made.id, connections: held };
    }

    if (path === '/configuration/connections/update') {
        const entry = body as ConnectionToSave;
        // A station takes what it takes: a URL as it writes it.
        held = { ...held, connections: held.connections.map(one => one.id === entry.id
                                                                   ? { ...one, description: entry.description, url: entry.url.toLowerCase() }
                                                                   : one) };
        return held;
    }

    if (path === '/configuration/connections/remove') {
        const { id } = body as { id: string };
        held = { ...held, connections: held.connections.filter(one => one.id !== id) };
        return held;
    }

    if (path === '/configuration/connections/test')
        return { ok: true, runtime_ms: 2012, steps: [ { at_ms: 0, level: 'info', text: 'Connecting.' },
                                                      { at_ms: 2012, level: 'info', text: 'Stayed connected.' } ] };

    if (path === '/status/connections')
        return { timestamp: '2026-10-04T12:00:05Z', states: standing };

    if (path === '/configuration/connections')
        return held;

    return undefined;

}

async function opened(): Promise<HTMLElement> {
    held     = aStation();
    standing = held.states;
    return open(connectionsPage, '/configuration/connections', [ 'connections:read', 'connections:edit', 'connections:run' ],
                station, root => root.querySelector('#add-form') !== null);
}

const editOf  = (root: HTMLElement, id: string) => root.querySelector<HTMLFormElement>(`form[data-edit="${id}"]`)!;
const cardOf  = (root: HTMLElement, id: string) => editOf(root, id)?.closest('.card');
const chipOf  = (root: HTMLElement, id: string) => root.querySelector(`[data-state="${id}"] .chip`)?.textContent?.trim();


describe('the Connections page', () => {

    // It asks where the connections stand every five seconds, for as long as
    // it is open.
    after(left);

    it('keeps a connection half written, and its focus, while another is saved', async () => {

        const root    = await opened();
        const browser = chromeTakesTheFocus(root);
        const typed   = field(root, '#add-form', 'url');

        typed.value = 'wss://lc.exam';
        typed.focus();

        submit(root, 'form[data-edit="c2"]');
        await until(() => root.querySelector('[data-note="c2"]')?.textContent === 'Saved.', 'the connection was not saved');
        browser.disconnect();

        assert.ok(field(root, '#add-form', 'url') === typed,  'the field was made anew');
        assert.equal(typed.value,                             'wss://lc.exam');
        assert.ok(document.activeElement === typed,           'the focus went');

    });

    it('keeps a connection half written, and its focus, while where the connections stand is asked again', async () => {

        const root  = await opened();
        const typed = field(root, '#add-form', 'description');

        typed.value = 'the spare';
        typed.focus();

        standing = { ...standing, c2: aState('connected', held.connections[1]) };
        await until(() => chipOf(root, 'c2') === 'connected', 'where the connections stand was not drawn anew', 7_000);

        assert.ok(field(root, '#add-form', 'description') === typed,  'the field was made anew');
        assert.equal(typed.value,                                     'the spare');
        assert.ok(document.activeElement === typed,                   'the focus went');

    });

    it('empties the form once a connection is written down', async () => {

        const root = await opened();

        field(root, '#add-form', 'description').value = 'the spare';
        field(root, '#add-form', 'url').value         = 'wss://spare.example.org/cs001';

        submit(root, '#add-form');
        await until(() => root.querySelector('#add-note')?.textContent === 'Written down.', 'the connection was not written down');

        assert.equal(field(root, '#add-form', 'url').value, '', 'the form still holds what was written down');
        assert.ok(editOf(root, 'c3') !== null, 'the connection written down is not shown');

    });

    it('shows a connection as the station took it once it is saved', async () => {

        const root = await opened();

        field(root, 'form[data-edit="c1"]', 'url').value = 'wss://CSMS.example.org/c1';

        submit(root, 'form[data-edit="c1"]');
        await until(() => root.querySelector('[data-note="c1"]')?.textContent === 'Saved.', 'the connection was not saved');

        assert.equal(field(root, 'form[data-edit="c1"]', 'url').value, 'wss://csms.example.org/c1', 'the field says what was typed, not what the station took');

    });

    it('keeps the card of a connection while the one before it is removed', async () => {

        const root   = await opened();
        const second = cardOf(root, 'c2');

        editOf(root, 'c1').querySelector<HTMLButtonElement>('[data-remove]')!.click();
        await until(() => root.querySelector('form[data-edit="c1"]') === null, 'the connection was not removed');

        assert.ok(cardOf(root, 'c2') === second, 'the card was made anew');

    });

    it('keeps a connection the station refused typed, and says why', async () => {

        const root = await opened();

        field(root, '#add-form', 'description').value = 'nowhere';

        submit(root, '#add-form');
        await until(() => root.querySelector('#add-error')?.textContent !== '', 'the refusal was not said');

        assert.equal(root.querySelector('#add-error')!.textContent, 'A connection needs somewhere to go.');
        assert.equal(field(root, '#add-form', 'description').value, 'nowhere', 'what was typed went');

    });

    it('says in the test dialog what came of it, and no longer that it is connecting', async () => {

        const root = await opened();

        editOf(root, 'c1').querySelector<HTMLButtonElement>('[data-test]')!.click();
        await until(() => document.querySelector('dialog .test-log') !== null, 'the test did not say what came of it');

        const dialog = document.querySelector('dialog')!;

        assert.equal(dialog.querySelector('.loading'),                                  null, 'it still says it is connecting');
        assert.equal(dialog.querySelector<HTMLButtonElement>('#test-close')!.disabled,  false);

        dialog.querySelector<HTMLButtonElement>('#test-close')!.click();
        assert.equal(document.querySelector('dialog'), null, 'the dialog stayed');

    });

});
