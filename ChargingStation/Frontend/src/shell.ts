import { auth } from './auth';
import { config } from './config';
import { html, must, render, type HTMLFragment } from './html';

/**
 * The frame every signed-in page sits in: the menu on the left, a heading and
 * whatever the page puts under it on the right.
 *
 * The menu is here and not in main.ts because each page renders itself into an
 * emptied outlet - so the frame is drawn again with every navigation, and the
 * entry that is current is simply the one that says so. One place decides what
 * the station has pages for.
 */

export interface MenuEntry {
    path:   string;
    label:  string;
    /** A Font Awesome class, e.g. "fa-sliders". */
    icon:   string;
}

/** What the station can show. Only these two matter for now. */
export const menu: MenuEntry[] = [
    { path: '/configuration', label: 'Configuration', icon: 'fa-sliders'      },
    { path: '/logs',          label: 'Logs',          icon: 'fa-list-ul'      }
];


export interface ShellOptions {
    /** The menu entry to mark as the current one. */
    active:     string;
    /** The heading of the page. */
    title:      string;
    /** One line under the heading, or nothing. */
    subtitle?:  string;
    /** Buttons and such, shown at the right of the heading. */
    actions?:   HTMLFragment;
}


/**
 * Draw the frame into the given root and hand back the element the page is to
 * render itself into.
 */
export function shell(root:     HTMLElement,
                      options:  ShellOptions): HTMLElement {

    render(root, html`
        <div class="shell">

            <nav class="sidebar" aria-label="Sections">

                <div class="brand">
                    <i class="fa-solid fa-charging-station"></i>
                    <span>Charging Station</span>
                </div>

                <ul class="menu">
                    ${menu.map(entry => html`
                        <li>
                            <a href="${entry.path}"
                               class="${entry.path === options.active ? 'active' : ''}"
                               ${entry.path === options.active ? html`aria-current="page"` : ''}>
                                <i class="fa-solid ${entry.icon}"></i>
                                <span>${entry.label}</span>
                            </a>
                        </li>
                    `)}
                </ul>

                <div class="sidebar-foot">
                    <div class="who" title="Signed in">
                        <i class="fa-solid fa-user"></i>
                        <span>${auth.user?.username ?? '-'}</span>
                    </div>
                    <button type="button" id="sign-out" class="btn small">Sign out</button>
                    <div class="versions small muted">
                        station ${config.serverVersion} &middot; web ${config.frontendVersion}
                    </div>
                </div>

            </nav>

            <main class="content">

                <header class="page-head">
                    <div>
                        <h1>${options.title}</h1>
                        ${options.subtitle ? html`<p class="muted">${options.subtitle}</p>` : ''}
                    </div>
                    <div class="page-actions">${options.actions ?? ''}</div>
                </header>

                <div id="content-body" class="content-body"></div>

            </main>

        </div>
    `);

    must<HTMLButtonElement>(root, '#sign-out').
        addEventListener('click', () => void auth.signOut());

    return must<HTMLElement>(root, '#content-body');

}
