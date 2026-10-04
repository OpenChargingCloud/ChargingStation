/**
 * What the Configuration page draws of what the station sends.
 *
 * Run with `npm test`. What is pinned is that every section the station sends
 * has a card. The fields of a section are drawn from whatever the station
 * sends, but which sections there are is the page's to say, and a section
 * without a card is left out without a word - as "v2g", the link below the
 * cable, was from the day the station first sent it. The sections here are
 * those of ChargingStation.ConfigurationJSON(), in its order.
 */

import '@node/../test/dom.ts';

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

import type { Configuration, Status } from '../api/client';
import type { TemplateResult } from '@node/view';

const { render }              = await import('@node/view.ts');
const { configurationCards }  = await import('./configuration.ts');


/** Every section the station sends, each with a field that says which it is. */
const configuration: Configuration = {
    station:     { said: 'in the station section' },
    http:        { said: 'in the http section' },
    web:         { said: 'in the web section' },
    log:         { said: 'in the log section' },
    v2g:         { said: 'in the v2g section' },
    time:        { said: 'in the time section' },
    assemblies:  [ { name: 'in the assemblies section', version: '1.0.0' } ],
    ocpp:        [ { version: '2.1', role: 'Charging Station', said: 'in the ocpp section' } ]
};

const status: Status = {
    service:    'ChargingStation',
    version:    '1.0.0',
    hermod:     null,
    timestamp:  '2026-09-29T12:05:00.000Z',
    startedAt:  '2026-09-29T12:00:00.000Z',
    uptime:     '0:05:00',
    sessions:   1,
    log:        { entries: 0, capacity: 10000, lastId: 0, tags: [] }
};

/** The cards, drawn into a document. */
function drawn(cards: TemplateResult): HTMLElement {
    const root = document.createElement('div');
    render(root, cards);
    return root;
}

/** The card under a heading, or nothing where there is none. */
function cardTitled(root: HTMLElement, title: string): HTMLElement | undefined {
    return [...root.querySelectorAll<HTMLElement>('section.card')].
               find(card => card.querySelector('h2')?.textContent?.trim() === title);
}

/** What a card says of one field, by the field's heading. */
function valueOf(card: HTMLElement, key: string): string | undefined {
    return [...card.querySelectorAll('.kv')].
               find(line => line.querySelector('.k')?.textContent?.trim() === key)?.
               querySelector('.v')?.textContent?.trim();
}


describe('the cards of the Configuration page', () => {

    it('draws every section the station sends', () => {

        const text     = drawn(configurationCards(configuration, status)).textContent ?? '';
        const missing  = Object.keys(configuration).filter(section => !text.includes(`in the ${section} section`));

        assert.deepEqual(missing, [], `Sections without a card: ${missing.join(', ')}.`);

    });

    it('says of a station that offers a vehicle nothing below the cable that it does not', () => {

        // What a station started without --v2g sends: its link is off.
        const v2g = cardTitled(drawn(configurationCards({ ...configuration, v2g: { enabled: false } }, status)), 'V2G');

        assert.ok(v2g !== undefined, 'There is no V2G card.');
        assert.equal(valueOf(v2g, 'Enabled'), 'no');

    });

});
