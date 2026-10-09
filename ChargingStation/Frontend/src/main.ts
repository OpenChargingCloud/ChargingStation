import './styles/app.scss';

// FontAwesome: the CSS ends up in the extracted stylesheet, the referenced
// font files become hashed assets below /assets/.
import '@fortawesome/fontawesome-free/css/fontawesome.css';
import '@fortawesome/fontawesome-free/css/solid.css';

import { toURL } from '@node/basePath';
import { html } from '@node/view';
import { nodeMenu, startNode } from '@node/start';

import { configurationPage }   from './pages/configuration';
import { v2gPage }             from './pages/v2g';
import { powerPage }           from './pages/power';
import { displayPage }         from './pages/display';
import { evsesPage }           from './pages/evses';
import { rfidPage }            from './pages/rfid';
import { authenticationPage }  from './pages/authentication';
import { connectionsPage }     from './pages/connections';
import { clientKeysPage }      from './pages/clientKeys';
import { calibrationPage }     from './pages/calibration';

// What a charging station has pages for beside what every node has: the
// vehicle below the cable, what it may draw, the screen on its front, its
// EVSEs and card readers, the back ends it dials and what it proves itself
// with, and what it is calibrated under. Each entry is shown to whoever may
// read what its page reads, as CSHTTPAPI asks it - the three pages about
// dialling out all read the connections. The sign-in, the log, the name
// servers, the time servers, the certificate store, the frame, "/" - the
// first page of the menu somebody may open - and following the log while
// somebody is signed in are every node's; see WWCP_Node's start.ts.
startNode({

    name:  'Charging Station',
    icon:  'fa-charging-station',

    menu: [
        nodeMenu.configuration([
            nodeMenu.dns,
            nodeMenu.nts,
            { path: '/configuration/v2g',             label: 'V2G',              icon: 'fa-car-side',        permission: [ 'v2g:read' ]          },
            { path: '/configuration/power',           label: 'Grid connection',  icon: 'fa-bolt',            permission: [ 'power:read' ]        },
            { path: '/configuration/display',         label: 'Display',          icon: 'fa-desktop',         permission: [ 'display:read' ]      },
            { path: '/configuration/evses',           label: 'EVSEs',            icon: 'fa-plug',            permission: [ 'evses:read' ]        },
            { path: '/configuration/rfid',            label: 'RFID',             icon: 'fa-id-card',         permission: [ 'rfid:read' ]         },
            { path: '/configuration/authentication',  label: 'Authentication',   icon: 'fa-user-lock',       permission: [ 'connections:read' ]  },
            { path: '/configuration/connections',     label: 'Connections',      icon: 'fa-network-wired',   permission: [ 'connections:read' ]  },
            { path: '/configuration/client-keys',     label: 'Client keys',      icon: 'fa-key',             permission: [ 'connections:read' ]  },
            nodeMenu.certificates,
            nodeMenu.identities,
            nodeMenu.ssh,
            { path: '/configuration/calibration',     label: 'Calibration',      icon: 'fa-scale-balanced',  permission: [ 'calibration:read' ]  }
        ]),
        nodeMenu.logs
    ],

    // The certificate store in the station's words where every node's do not
    // say enough: that three of the roots are Plug & Charge's, what a vehicle,
    // a contract and an OEM's provisioning chain to, and that the keys the
    // station dials its back ends with are on a page of their own, made here
    // and never imported.
    certificates: {
        hints: {
            believes:     html`
                Trust anchors. Every switched-on root of a kind is believed at once. A TLS root vouches for
                the time servers and the name servers it is kept for, beside the roots of the machine this
                station runs on. The V2G, Mobility Operator and OEM roots are kept for Plug &amp; Charge:
                what a vehicle's certificate, a contract and an OEM provisioning certificate chain to.
            `,
            presents:     html`
                A TLS identity, with its private key: what this station would show a server that asks for
                one. The keys it dials its back ends with are not here
                but on the <a href="${toURL('/configuration/client-keys')}">Client keys</a> page, because
                they are made on this station and never imported.
            `,
            unencrypted:  html`can take the identity this station presents in TLS.`
        }
    },

    pages: {
        '/configuration':                 configurationPage,
        '/configuration/v2g':             v2gPage,
        '/configuration/power':           powerPage,
        '/configuration/display':         displayPage,
        '/configuration/evses':           evsesPage,
        '/configuration/rfid':            rfidPage,
        '/configuration/authentication':  authenticationPage,
        '/configuration/connections':     connectionsPage,
        '/configuration/client-keys':     clientKeysPage,
        '/configuration/calibration':     calibrationPage
    }

});
