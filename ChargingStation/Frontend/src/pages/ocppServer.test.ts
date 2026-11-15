/**
 * The OCPP server page drawn, in a document of happy-dom, against a stand-in
 * station: the form sends what it says and the page says what the station
 * took; a refusal keeps the form as set and says why; somebody who may only
 * read it is told so and can change nothing; where the other end dials is
 * said, with a server without TLS and a port that was taken said to be in
 * the way; and who is written down is listed with how it stands.
 *
 * Run with `npm test`.
 */

import { open, refused, submit, until, type Asked } from '../../test/station.ts';

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

import type { OCPPServerConfiguration, OCPPServerUpdate } from '../api/client.ts';

const { ocppServerPage } = await import('./ocppServer.ts');


let held: OCPPServerConfiguration;

/** What the last save sent. */
let sent: OCPPServerUpdate | undefined;

/** Refuses every change where told to. */
let refuseChanges = false;

function aStation(): OCPPServerConfiguration {
    return {
        enabled:  false,
        tls:      true,
        servers:  [
                      { ocppVersion: 'OCPP2.1', port: null, served: false, url: null, connected: [] },
                      { ocppVersion: 'OCPP1.6', port: null, served: false, url: null, connected: [] }
                  ],
        inbound:  [],
        file:     'configuration.json'
    };
}

function station({ method, path, body }: Asked): unknown {

    if (path === '/configuration/ocpp-server' && method === 'PUT') {

        if (refuseChanges)
            return refused(400, "'ocppServer' is switched on and names no port.");

        const update = body as OCPPServerUpdate;
        sent = update;

        held = {
            ...held,
            enabled:  update.enabled,
            servers:  [
                          { ocppVersion: 'OCPP2.1', port: update.port,   served: update.enabled && update.port   !== null,
                            url: update.port   !== null ? `wss://127.0.0.1:${update.port}/`   : null, connected: [] },
                          { ocppVersion: 'OCPP1.6', port: update.port16, served: update.enabled && update.port16 !== null,
                            url: update.port16 !== null ? `wss://127.0.0.1:${update.port16}/` : null, connected: [] }
                      ]
        };

        return held;

    }

    if (path === '/configuration/ocpp-server')
        return held;

    return undefined;

}

async function opened(Station: Partial<OCPPServerConfiguration> = {},
                      Permissions = [ 'connections:read', 'connections:edit' ]): Promise<HTMLElement> {
    held          = { ...aStation(), ...Station };
    sent          = undefined;
    refuseChanges = false;
    return open(ocppServerPage, '/configuration/ocpp-server', Permissions,
                station, root => root.querySelector('#ocpp-server-form') !== null);
}

const field = (root: HTMLElement, name: string) => root.querySelector<HTMLInputElement>(`#ocpp-server-form input[name="${name}"]`)!;
const said  = (root: HTMLElement, selector: string) => root.querySelector(selector)?.textContent?.replace(/\s+/g, ' ').trim() ?? '';


describe('the OCPP server page', () => {

    it('switches it on with a port, and says where the other end dials', async () => {

        const root = await opened();

        assert.match(said(root, '#ocpp-server-state'), /^Not served/);

        field(root, 'enabled').checked = true;
        field(root, 'port').value      = '9443';

        submit(root, '#ocpp-server-form');
        await until(() => /^Saved/.test(root.querySelector('#form-note')?.textContent ?? ''), 'the form was not saved');

        assert.deepEqual(sent, { enabled: true, port: 9443, port16: null });
        assert.match(said(root, '#ocpp-server-state'), /^Served: OCPP2\.1/);
        assert.match(said(root, '[data-url="OCPP2.1"]'), /wss:\/\/127\.0\.0\.1:9443\/<its name>/);
        assert.match(said(root, '[data-url="OCPP1.6"]'), /no port/);
        assert.equal(field(root, 'enabled').checked, true, 'the switch does not say what the station took');
        assert.equal(field(root, 'port').value, '9443');

    });

    it('keeps the form as set where the station refused, and says why', async () => {

        const root = await opened();

        refuseChanges = true;

        field(root, 'enabled').checked = true;

        submit(root, '#ocpp-server-form');
        await until(() => (root.querySelector('#form-error')?.textContent ?? '') !== '', 'the refusal was not said');

        assert.match(said(root, '#form-error'), /names no port/);
        assert.equal(field(root, 'enabled').checked, true, 'what was set was thrown away');
        assert.match(said(root, '#ocpp-server-state'), /^Not served/);

    });

    it('tells somebody who may only read it so, and lets them change nothing', async () => {

        const root = await opened({}, [ 'connections:read' ]);

        assert.match(said(root, '.notice'), /change it/);
        assert.equal(field(root, 'enabled').disabled, true);
        assert.equal(field(root, 'port').disabled,    true);
        assert.equal(root.querySelector<HTMLButtonElement>('#ocpp-server-form button[type="submit"]')!.disabled, true);

    });

    it('says what is in the way: no TLS, and a port that was taken', async () => {

        const root = await opened({
                               enabled:  true,
                               tls:      false,
                               servers:  [
                                             { ocppVersion: 'OCPP2.1', port: 9443, served: true,  url: 'ws://127.0.0.1:9443/', connected: [] },
                                             { ocppVersion: 'OCPP1.6', port: 9444, served: false, url: 'ws://127.0.0.1:9444/', connected: [],
                                               problem: 'Only one usage of each socket address is normally permitted.' }
                                         ]
                           });

        assert.match(said(root, '#no-tls'), /crosses the network in the clear/);
        assert.match(said(root, '[data-problem="OCPP1.6"]'), /could not be served on port 9444: Only one usage/);
        assert.match(said(root, '[data-url="OCPP1.6"]'), /not served/);

    });

    it('lists who is written down, and how each stands', async () => {

        const root = await opened({
                               enabled:  true,
                               servers:  [
                                             { ocppVersion: 'OCPP2.1', port: 9443, served: true,  url: 'wss://127.0.0.1:9443/', connected: [ 'csms-01' ] },
                                             { ocppVersion: 'OCPP1.6', port: null, served: false, url: null,                    connected: [] }
                                         ],
                               inbound:  [
                                             { id: 'c1', description: 'The CSMS',        name: 'csms-01', ocppVersion: 'OCPP2.1', letIn: true  },
                                             { id: 'c2', description: 'Its successor',   name: 'csms-02', ocppVersion: 'OCPP2.1', letIn: true  },
                                             { id: 'c3', description: 'A controller',    name: 'lc-01',   ocppVersion: 'OCPP2.1', letIn: false }
                                         ]
                           });

        assert.match(said(root, '[data-inbound="c1"]'), /The CSMS csms-01 OCPP2\.1 connected/);
        assert.match(said(root, '[data-inbound="c2"]'), /waited for/);
        assert.match(said(root, '[data-inbound="c3"]'), /not let in/);

    });

    it('says that nothing is written down where nothing is', async () => {

        const root = await opened();

        assert.match(said(root, '#ocpp-server-inbound'), /No connection the other end makes is written down/);

    });

});
