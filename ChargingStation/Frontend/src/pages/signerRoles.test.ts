/**
 * The Signer roles page drawn, in a document of happy-dom, against a stand-in
 * station: a station without an owner says so and is given its first one from
 * a public key; a role assignment is sent as it was pasted, and a refusal is
 * said with its reason; the mode of the policy is saved; the keys of a role
 * are removed by its version; forgetting every key is asked twice; and
 * somebody who may only read is told so and can change nothing.
 *
 * Run with `npm test`.
 */

import { open, refused, submit, until, type Asked } from '../../test/station.ts';

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

import type { Signatures } from '../api/client.ts';

const { signerRolesPage } = await import('./signerRoles.ts');


let held: Signatures;

/** What was sent, by its path. */
let sent: Record<string, unknown>;

/** What the station says to the next change, where it refuses it. */
let refusal: string | undefined;

const ownerKey = { kty: 'EC', crv: 'P-256', x: 'f83OJ3D2xF1Bg8vub9tLe1gHMzV76e8Tus9uPHvRVEU', y: 'x_FEzRu9m36HLN_tue659LNpXW6pCyStikYjKIWI5a0' };

function aStation(): Signatures {
    return {
        settings:       { mode: 'Off', acceptFormat0: false, maxClockSkew: 300, maxSignatureAge: 900,
                          allowFirstOwnerByCSMS: false, allowPermissionsByCSMS: false, quorum: 1 },
        algorithms:     [ 'ES256', 'ES384', 'ES512' ],
        hasOwner:       false,
        ownerRole:      'systemadmin',
        policyVersion:  0,
        roles:          [
                            { role: 'systemadmin', permissions: [ '*:read', '*:edit', '*:run' ], version: 0, keys: [] },
                            { role: 'support',     permissions: [ 'state:read', 'reset:run' ],   version: 0, keys: [], description: 'Telephone support' }
                        ]
    };
}

function withKeys(role: string, version: number, kid: string, label?: string): void {
    held = { ...held,
             hasOwner: held.hasOwner || role === 'systemadmin',
             roles:    held.roles.map(known => known.role === role
                                                   ? { ...known, version, keys: [ label === undefined ? { kid } : { kid, label } ] }
                                                   : known) };
}

function station({ method, path, body }: Asked): unknown {

    if (!path.startsWith('/configuration/signatures'))
        return undefined;

    if (method === 'GET')
        return held;

    if (refusal !== undefined)
        return refused(403, refusal);

    sent[path] = body;

    const json = body as Record<string, unknown>;

    switch (path) {

        case '/configuration/signatures/settings':
            held = { ...held, settings: { ...held.settings, ...json } };
            break;

        case '/configuration/signatures/roles/firstOwner':
            withKeys('systemadmin', 1, 'owner-key-id', typeof json['label'] === 'string' ? json['label'] : undefined);
            break;

        case '/configuration/signatures/roles':
            withKeys(String(json['role']), Number(json['version']), 'support-key-id');
            break;

        case '/configuration/signatures/roles/remove':
            held = { ...held, roles: held.roles.map(known => known.role === json['role'] ? { ...known, keys: [] } : known) };
            break;

        case '/configuration/signatures/policy':
            held = { ...held, policyVersion: Number(json['version']), policy: json };
            break;

        case '/configuration/signatures/reset':
            held = { ...aStation(), settings: held.settings };
            break;

    }

    return held;

}

async function opened(Station: Partial<Signatures> = {},
                      Permissions = [ 'signatures:read', 'signatures:edit' ]): Promise<HTMLElement> {
    held     = { ...aStation(), ...Station };
    sent     = {};
    refusal  = undefined;
    return open(signerRolesPage, '/configuration/signer-roles', Permissions,
                station, root => root.querySelector('#settings-form') !== null);
}

const said   = (root: HTMLElement, selector: string) => root.querySelector(selector)?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
const field  = <T extends HTMLElement>(root: HTMLElement, selector: string) => root.querySelector<T>(selector)!;


describe('the Signer roles page', () => {

    it('says that there is no owner, and makes the first one from a public key', async () => {

        const root = await opened();

        assert.match(said(root, '#owner-state'), /no owner yet/);
        assert.match(said(root, '#policy-state'), /^Off/);

        field<HTMLTextAreaElement>(root, '#owner-form textarea[name="jwk"]').value  = JSON.stringify(ownerKey);
        field<HTMLInputElement>   (root, '#owner-form input[name="label"]').value   = "The operator's key";

        submit(root, '#owner-form');
        await until(() => /first owner/.test(said(root, '#roles-note')), 'the first owner was not made');

        assert.deepEqual(sent['/configuration/signatures/roles/firstOwner'], { jwk: ownerKey, label: "The operator's key" });
        assert.match(said(root, '#owner-state'), /The owner is the role systemadmin/);
        assert.match(said(root, 'tr[data-role="systemadmin"]'), /owner-key-id - The operator's key/);
        assert.equal(root.querySelector('#owner-form'), null, 'a station with an owner still asks for its first one');

    });

    it('says what is no JSON, and sends nothing', async () => {

        const root = await opened();

        field<HTMLTextAreaElement>(root, '#owner-form textarea[name="jwk"]').value = 'this is no key';

        submit(root, '#owner-form');
        await until(() => said(root, '#owner-error') !== '', 'nothing was said of what is no JSON');

        assert.match(said(root, '#owner-error'), /no JSON object/);
        assert.deepEqual(sent, {});

    });

    it('sends a role assignment as it was pasted', async () => {

        const root        = await opened({ hasOwner: true });
        const assignment  = { role: 'support', keys: [ { kid: 'k', jwk: ownerKey } ], version: 2, issued: '2026-11-13T12:00:00.000Z',
                              signatures: [ { protected: 'e30', signature: 'AAAA' } ] };

        field<HTMLTextAreaElement>(root, '#import-form textarea[name="assignment"]').value = JSON.stringify(assignment, null, 4);

        submit(root, '#import-form');
        await until(() => /^Imported/.test(said(root, '#import-note')), 'the role assignment was not imported');

        assert.deepEqual(sent['/configuration/signatures/roles'], assignment);
        assert.match(said(root, '#import-note'), /'support' has the keys of version 2/);
        assert.match(said(root, 'tr[data-role="support"]'), /support-key-id/);

    });

    it('says why the station did not take a role assignment', async () => {

        const root = await opened({ hasOwner: true });

        refusal = 'NotAuthorized: The object is not signed by an owner of this node!';

        field<HTMLTextAreaElement>(root, '#import-form textarea[name="assignment"]').value = '{ "role": "support", "version": 2 }';

        submit(root, '#import-form');
        await until(() => said(root, '#import-error') !== '', 'the refusal was not said');

        assert.match(said(root, '#import-error'), /not signed by an owner/);
        assert.match(said(root, 'tr[data-role="support"]'), /no keys/);

    });

    it('saves the mode of the policy', async () => {

        const root = await opened({ hasOwner: true });

        field<HTMLSelectElement>(root, '#settings-form select[name="mode"]').value = 'Enforce';

        submit(root, '#settings-form');
        await until(() => /^Saved/.test(said(root, '#settings-note')), 'the mode was not saved');

        assert.deepEqual(sent['/configuration/signatures/settings'],
                         { mode: 'Enforce', quorum: 1, allowFirstOwnerByCSMS: false, allowPermissionsByCSMS: false });
        assert.match(said(root, '#policy-state'), /^Enforced/);

    });

    it('removes the keys of a role by the version that is stored', async () => {

        const root = await opened({ hasOwner: true });

        withKeys('support', 4, 'support-key-id');
        field<HTMLButtonElement>(root, '.btn[title="Reload"], .reload, button[data-reload]')?.click();

        // Drawn anew from what the station holds now.
        const again = await opened({ ...held });

        field<HTMLButtonElement>(again, 'button[data-remove="support"]').click();
        await until(() => /no keys any more/.test(said(again, '#roles-note')), 'the keys were not removed');

        assert.deepEqual(sent['/configuration/signatures/roles/remove'], { role: 'support', version: 4 });
        assert.match(said(again, 'tr[data-role="support"]'), /no keys/);

    });

    it('forgets every key only where it is asked twice', async () => {

        const root = await opened({ hasOwner: true });

        submit(root, '#reset-form');
        await until(() => said(root, '#reset-error') !== '', 'nothing was said of a reset that was not confirmed');

        assert.deepEqual(sent, {});

        field<HTMLInputElement>(root, '#reset-form input[name="confirm"]').value = 'reset';

        submit(root, '#reset-form');
        await until(() => /no owner yet/.test(said(root, '#owner-state')), 'the keys were not forgotten');

        assert.deepEqual(sent['/configuration/signatures/reset'], { confirm: 'reset' });

    });

    it('lets somebody who may only read change nothing', async () => {

        const root = await opened({ hasOwner: true }, [ 'signatures:read' ]);

        assert.match(said(root, '.notice'), /signer roles/);

        for (const control of root.querySelectorAll<HTMLButtonElement | HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement>('form button, form input, form select, form textarea'))
            assert.equal(control.disabled, true, `${control.tagName} ${control.getAttribute('name') ?? ''} can be changed`);

        assert.equal(root.querySelector('button[data-remove]'), null);

    });

});
