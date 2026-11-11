/**
 * The Connections page drawn, in a document of happy-dom, against a stand-in
 * station: a connection half written, and its focus, outlive another being
 * saved, as a browser takes the focus away while the page is held still, and
 * where the connections stand being asked again; one written down empties the
 * form; one saved says what the station took; the card after one removed
 * stays; a connection refused stays typed, with why; the test dialog says
 * what came of it and no longer that it is connecting; an ssh:// connection
 * shows the key this station signs in with and its host key, and a save of it
 * says which host key the page showed; a wss:// connection says whether its
 * server is known by key pin, by a key pin learned on probation or by its CA,
 * a save of it says which key pins the page showed, and the test dialog says
 * which key pin the server showed.
 *
 * Run with `npm test`.
 */

import { field, leave, open, refused, submit, until, type Asked } from '../../test/station.ts';
import { chromeTakesTheFocus } from '@node/../test/dom.ts';

import { strict as assert }     from 'node:assert';
import { after, describe, it }  from 'node:test';

import type { ConnectionState, ConnectionToSave, StationConnection, StationConnections } from '../api/client.ts';

const { connectionsPage } = await import('./connections.ts');


let held: StationConnections;

/** Where the stand-in says the connections stand, asked on their own. */
let standing: Record<string, ConnectionState> = {};

/** What the page sent the last time it saved a connection. */
let lastSaved: ConnectionToSave | null = null;

/** What the page sent the last time it tested a connection. */
let lastTested: ConnectionToSave | null = null;

/** Two key pins, as the station writes them: 64 lowercase hexadecimal digits. */
const pinnedKey   = '6a1f0c3e'.repeat(8);
const learnedKey  = 'c47d1803'.repeat(8);

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
        lastSaved   = entry;
        // A station takes what it takes: a URL as it writes it.
        held = { ...held, connections: held.connections.map(one => one.id === entry.id
                                                                   ? { ...one, description: entry.description, url: entry.url.toLowerCase() }
                                                                   : one) };
        return held;
    }

    if (path === '/configuration/connections/pinSetSigners') {
        const { publicKey, description } = body as { publicKey: string; description: string };
        held = { ...held, pinSetSigners: [ ...(held.pinSetSigners ?? []), { publicKey, description, createdAt: '2026-11-10T12:00:00.000Z' } ] };
        return held;
    }

    if (path === '/configuration/connections/pinSetSigners/remove') {
        const { publicKey } = body as { publicKey: string };
        held = { ...held, pinSetSigners: (held.pinSetSigners ?? []).filter(one => one.publicKey !== publicKey) };
        return held;
    }

    if (path === '/configuration/connections/remove') {
        const { id } = body as { id: string };
        held = { ...held, connections: held.connections.filter(one => one.id !== id) };
        return held;
    }

    if (path === '/configuration/connections/test') {
        lastTested = body as ConnectionToSave;
        // A TLS server shows a key, and the station says its key pin.
        return { ok: true, runtime_ms: 2012, steps: [ { at_ms: 0, level: 'info', text: 'Connecting.' },
                                                      { at_ms: 2012, level: 'info', text: 'Stayed connected.' } ],
                 ...(lastTested.url.startsWith('wss://') ? { serverKeyPin: learnedKey } : {}) };
    }

    if (path === '/status/connections')
        return { timestamp: '2026-10-04T12:00:05Z', states: standing };

    if (path === '/configuration/connections')
        return held;

    return undefined;

}

/** A station whose connection over SSH learned a host key on its first connection, which is on probation. */
function aStationOnProbation(): StationConnections {
    const station = anSshStation();
    return { ...station,
             connections: [ { ...station.connections[0]!, sshTrustOnFirstUse: 'HostKey', sshLearnedHostKey: 'SHA256:learnedOnce',
                              sshLearnedAt: '2026-11-10T10:00:00.000Z' }, station.connections[1]! ] };
}

/** A station whose first connection goes over SSH and pins no host key yet, nor trusts on first use. */
function anSshStation(): StationConnections {
    const station = aStation();
    const ssh     = aConnection('c1', { url: 'ssh://cs001@csms.example.org:2222', secure: false, transport: 'ssh',
                                        sshTrustOnFirstUse: 'None', sshOnPinMismatch: 'Refuse', sshProbationTime: 600 });
    return { ...station,
             connections:   [ ssh, station.connections[1] ],
             sshClientKey:  { publicKey: 'ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIStandIn chargingstation@bench',
                              fingerprint: 'SHA256:standIn', path: 'connections/ssh/id_ed25519' } };
}

/**
 * A station whose first connection pins the key of its local controller, and
 * whose second learned a key pin on its first connection, which is on
 * probation.
 */
function aKeyPinnedStation(): StationConnections {
    const station = aStation();
    return { ...station,
             connections: [ { ...station.connections[0]!, connectionType: 'LocalController', keyPinned: true, serverKeyPins: [ pinnedKey ],
                              serverKeyPinState: 'Configured', trustOnFirstUse: 'None', rawPublicKeys: 'Off', serverKeyPinProbationTime: 600 },
                            { ...station.connections[1]!, keyPinned: true, serverKeyPinState: 'Learned', trustOnFirstUse: 'Key', rawPublicKeys: 'Off',
                              serverKeyPinProbationTime: 600, serverKeyPinLearned: learnedKey, serverKeyPinLearnedAt: '2026-11-11T10:00:00.000Z' } ] };
}

async function opened(make: () => StationConnections = aStation): Promise<HTMLElement> {
    held     = make();
    lastSaved = null;
    lastTested = null;
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
    after(leave);

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

    it('shows an ssh:// connection with the key this station signs in with, and its host key', async () => {

        const root = await opened(anSshStation);

        assert.equal(root.querySelector<HTMLTextAreaElement>('#ssh-public-key')?.value,
                     'ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIStandIn chargingstation@bench', 'the key to hand the server is not shown');

        const chips = [ ...cardOf(root, 'c1')!.querySelectorAll('.key-head .chip') ].map(chip => chip.textContent?.trim());

        assert.ok(chips.includes('SSH'),     'the connection does not say it goes over SSH');
        assert.ok(!chips.includes('no TLS'), 'a connection over SSH is said to be unencrypted');
        assert.match(root.querySelector('[data-host-key="c1"]')?.textContent ?? '', /none pinned - not dialled/);

        // A key learned on first use is shown on probation, and is no pin.
        const learning = await opened(aStationOnProbation);
        assert.equal(learning.querySelector('[data-host-key="c1"]')?.textContent?.trim(), 'SHA256:learnedOnce');
        assert.match(cardOf(learning, 'c1')?.textContent ?? '', /on probation/);
        assert.equal(field(learning, 'form[data-edit="c1"]', 'sshHostKey').value, '', 'a learned key was offered as a pin');

        // A station without one has no card for it.
        const plain = await opened();
        assert.equal(plain.querySelector('#ssh-key'), null, 'a station that dials nothing over SSH shows a key card');

    });

    it('lists the signers of pin sets, and takes and removes one', async () => {

        const root = await opened(anSshStation);

        assert.match(root.querySelector('#no-signers')?.textContent ?? '', /no pin set is believed/);

        field(root, '#signer-form', 'signerDescription').value = 'The CSMS';
        field(root, '#signer-form', 'signerPublicKey').value   = 'A2bTQUlr4dmD5Pyt1mN6S0+rQe8Rf1CGRiq3Z46VsHyC';
        submit(root, '#signer-form');

        await until(() => root.querySelector('[data-signer="A2bTQUlr4dmD5Pyt1mN6S0+rQe8Rf1CGRiq3Z46VsHyC"]') !== null, 'the signer was not shown');

        assert.match(root.querySelector('#pin-set-signers')?.textContent ?? '', /The CSMS/);

        const realConfirm = globalThis.confirm;
        globalThis.confirm = () => true;

        try
        {
            root.querySelector<HTMLButtonElement>('[data-signer] button')!.click();
            await until(() => root.querySelector('[data-signer]') === null, 'the signer was not removed');
        }
        finally
        {
            globalThis.confirm = realConfirm;
        }

        // A station that dials nothing over SSH and believes no signer has no card for it.
        const plain = await opened();
        assert.equal(plain.querySelector('#pin-set-signers'), null);

    });

    it('says which host key the page showed when an ssh:// connection is saved', async () => {

        const root = await opened(anSshStation);

        submit(root, 'form[data-edit="c1"]');
        await until(() => lastSaved !== null, 'the connection was not saved');

        assert.equal(lastSaved!.sshHostKey,          '', 'a host key was sent that nobody typed');
        assert.equal(lastSaved!.sshHostKeyAsShown,   '', 'what the page showed was not sent beside it');
        assert.equal(lastSaved!.sshTrustOnFirstUse,  'None');
        assert.equal(lastSaved!.sshOnPinMismatch,    'Refuse');
        assert.equal(lastSaved!.sshProbationTime,    600);

        lastSaved = null;
        await until(() => root.querySelector('[data-note="c1"]')?.textContent === 'Saved.', 'the save was not said');

        field(root, 'form[data-edit="c1"]', 'sshHostKey').value = '  SHA256:typedIn, SHA256:next ';
        root.querySelector<HTMLSelectElement>('form[data-edit="c1"] [name="sshTrustOnFirstUse"]')!.value = 'HostKey';
        root.querySelector<HTMLSelectElement>('form[data-edit="c1"] [name="sshOnPinMismatch"]')!.value   = 'Record';
        field(root, 'form[data-edit="c1"]', 'sshProbationTime').value = '120';

        submit(root, 'form[data-edit="c1"]');
        await until(() => lastSaved !== null, 'the connection was not saved again');

        assert.equal(lastSaved!.sshHostKey,          'SHA256:typedIn, SHA256:next');
        assert.equal(lastSaved!.sshHostKeyAsShown,   '', 'what the page showed became what was typed');
        assert.equal(lastSaved!.sshTrustOnFirstUse,  'HostKey');
        assert.equal(lastSaved!.sshOnPinMismatch,    'Record');
        assert.equal(lastSaved!.sshProbationTime,    120);

    });

    it('shows how the server of a wss:// connection is known: by its key pins, by a key pin learned on probation, or by its CA', async () => {

        const root   = await opened(aKeyPinnedStation);
        const chips  = (id: string) => [ ...cardOf(root, id)!.querySelectorAll('.key-head .chip') ].map(chip => chip.textContent?.trim());

        assert.match(root.querySelector('[data-key-pins="c1"]')?.textContent ?? '', new RegExp(pinnedKey));
        assert.match(root.querySelector('[data-key-pins="c1"]')?.textContent ?? '', /by key pin/);
        assert.ok(chips('c1').includes('key pin'), 'a key-pinned connection does not say so');
        assert.equal(field(root, 'form[data-edit="c1"]', 'serverKeyPins').value, pinnedKey, 'the key pins are not in the form');

        // A key pin learned on first use is shown on probation, and is no pin.
        assert.match(root.querySelector('[data-key-pins="c2"]')?.textContent ?? '', new RegExp(learnedKey));
        assert.match(root.querySelector('[data-key-pins="c2"]')?.textContent ?? '', /on probation/);
        assert.equal(field(root, 'form[data-edit="c2"]', 'serverKeyPins').value, '', 'a learned key pin was offered as a pin');
        assert.equal(root.querySelector<HTMLSelectElement>('form[data-edit="c2"] [name="trustOnFirstUse"]')!.value, 'Key');

        // One without either is checked by its CA, and says so.
        const plain = await opened();
        assert.match(plain.querySelector('[data-key-pins="c1"]')?.textContent ?? '', /by its certificate/);
        assert.ok(![ ...cardOf(plain, 'c1')!.querySelectorAll('.key-head .chip') ].some(chip => chip.textContent?.trim() === 'key pin'),
                  'a connection checked by its CA says it is key-pinned');

        // An ssh:// connection has no TLS server to know.
        const ssh = await opened(anSshStation);
        assert.equal(ssh.querySelector('[data-key-pins="c1"]'), null, 'a connection over SSH shows key pins');

    });

    it('says which key pins the page showed when a wss:// connection is saved', async () => {

        const root = await opened(aKeyPinnedStation);

        submit(root, 'form[data-edit="c1"]');
        await until(() => lastSaved !== null, 'the connection was not saved');

        assert.deepEqual(lastSaved!.serverKeyPins,         [ pinnedKey ], 'the key pins shown were not sent');
        assert.deepEqual(lastSaved!.serverKeyPinsAsShown,  [ pinnedKey ], 'what the page showed was not sent beside them');
        assert.equal    (lastSaved!.trustOnFirstUse,            'None');
        assert.equal    (lastSaved!.rawPublicKeys,              'Off');
        assert.equal    (lastSaved!.serverKeyPinProbationTime,  600);

        lastSaved = null;
        await until(() => root.querySelector('[data-note="c1"]')?.textContent === 'Saved.', 'the save was not said');

        field(root, 'form[data-edit="c1"]', 'serverKeyPins').value = ` ${pinnedKey}, sha256//xH3dLvfmXGMKZkcSgVkUm07ThDCv+kdHDlKUwCWeGZY= `;
        root.querySelector<HTMLSelectElement>('form[data-edit="c1"] [name="trustOnFirstUse"]')!.value  = 'Key';
        root.querySelector<HTMLSelectElement>('form[data-edit="c1"] [name="rawPublicKeys"]')!.value    = 'Prefer';
        field(root, 'form[data-edit="c1"]', 'serverKeyPinProbationTime').value = '120';

        submit(root, 'form[data-edit="c1"]');
        await until(() => lastSaved !== null, 'the connection was not saved again');

        assert.deepEqual(lastSaved!.serverKeyPins,         [ pinnedKey, 'sha256//xH3dLvfmXGMKZkcSgVkUm07ThDCv+kdHDlKUwCWeGZY=' ]);
        assert.deepEqual(lastSaved!.serverKeyPinsAsShown,  [ pinnedKey ], 'what the page showed became what was typed');
        assert.equal    (lastSaved!.trustOnFirstUse,            'Key');
        assert.equal    (lastSaved!.rawPublicKeys,              'Prefer');
        assert.equal    (lastSaved!.serverKeyPinProbationTime,  120);

    });

    it('says in the test dialog which key pin the server of a wss:// connection showed, and tests the key pins on the screen', async () => {

        const root = await opened(aKeyPinnedStation);

        field(root, 'form[data-edit="c1"]', 'serverKeyPins').value = learnedKey;

        editOf(root, 'c1').querySelector<HTMLButtonElement>('[data-test]')!.click();
        await until(() => document.querySelector('dialog .test-log') !== null, 'the test did not say what came of it');

        assert.equal(document.querySelector('#test-key-pin')?.textContent?.trim(), learnedKey, 'the key pin shown was not said');
        assert.deepEqual(lastTested!.serverKeyPins, [ learnedKey ], 'the test was not of the key pins on the screen');

        document.querySelector<HTMLButtonElement>('dialog #test-close')!.click();

        // Over SSH there is no key pin to say.
        const ssh = await opened(anSshStation);

        editOf(ssh, 'c1').querySelector<HTMLButtonElement>('[data-test]')!.click();
        await until(() => document.querySelector('dialog .test-log') !== null, 'the test over SSH did not say what came of it');

        assert.equal(document.querySelector('#test-key-pin'), null, 'a key pin was said of a connection over SSH');

        document.querySelector<HTMLButtonElement>('dialog #test-close')!.click();

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
