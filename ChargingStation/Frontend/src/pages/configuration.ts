import { api, type Configuration, type Status } from '../api/client';
import { cardView, librariesCardView } from '@node/cardViews';
import type { Page } from '@node/router';
import { reloadButton, shell } from '@node/shell';
import { errorMessage, formatSince, formatValue } from '@node/ui';
import { html, render, type TemplateResult } from '@node/view';

/**
 * What this charging station is made of - read-only for now: it answers "what
 * am I running", not "change it".
 *
 * The fields of each section are rendered from whatever the station sends
 * rather than from a list kept here, so a field added on the server shows up
 * without a change to this page. The sections are not: which of them there
 * are, their order and their headings are decided here, and a section the
 * server adds shows up once it has a card below.
 */
export const configurationPage: Page = {

    title: 'Configuration',

    render({ root }) {

        const content = shell(root, {
            active:    '/configuration',
            title:     'Configuration',
            subtitle:  'What this charging station is made of.',
            actions:   reloadButton(() => load())
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        let cancelled = false;

        async function load(): Promise<void> {

            try
            {

                const [configuration, status] = await Promise.all([
                    api.configuration(),
                    api.status()
                ]);

                if (cancelled)
                    return;

                render(content, configurationCards(configuration, status));

            }
            catch (problem)
            {

                if (cancelled)
                    return;

                render(content, html`
                    <div class="error-box">The configuration could not be loaded: ${errorMessage(problem)}</div>
                `);

            }

        }

        void load();

        return () => { cancelled = true; };

    }

};


/**
 * The cards, one for each section the station sends: the station, what every
 * node says of itself, the link below the cable and the OCPP nodes above it,
 * and what it was all built from.
 */
export function configurationCards(configuration: Configuration, status: Status): TemplateResult {

    return html`

        <div class="cards">

            ${cardView('Station',       'fa-charging-station', configuration.station, html`
                <div class="kv">
                    <span class="k">Uptime</span>
                    <span class="v">${status.uptime} <span class="muted">(started ${formatSince(status.startedAt)})</span></span>
                </div>
            `)}

            ${cardView('HTTP server',   'fa-server',           configuration.http)}
            ${cardView('Accounts',      'fa-user-lock',        configuration.web)}
            ${cardView('Event log',     'fa-list-ul',          configuration.log)}
            ${cardView('Time',          'fa-clock',            configuration.time)}
            ${cardView('V2G',           'fa-car-side',         configuration.v2g)}

            ${configuration.ocpp.map(node => cardView(
                `OCPP ${formatValue(node.version)} - ${formatValue(node.role)}`,
                'fa-plug',
                node
            ))}

            ${librariesCardView(configuration.assemblies)}

        </div>

    `;

}
