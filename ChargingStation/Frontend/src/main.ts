import './styles/app.scss';

// FontAwesome: the CSS ends up in the extracted stylesheet, the referenced
// font files become hashed assets below /assets/.
import '@fortawesome/fontawesome-free/css/fontawesome.css';
import '@fortawesome/fontawesome-free/css/solid.css';

import { nodeMenu, startNode } from '@node/start';

import { configurationPage }   from './pages/configuration';
import { dnsPage }             from './pages/dns';
import { ntsPage }             from './pages/nts';
import { v2gPage }             from './pages/v2g';
import { powerPage }           from './pages/power';
import { displayPage }         from './pages/display';
import { evsesPage }           from './pages/evses';
import { rfidPage }            from './pages/rfid';
import { authenticationPage }  from './pages/authentication';
import { connectionsPage }     from './pages/connections';
import { clientKeysPage }      from './pages/clientKeys';
import { certificatesPage }    from './pages/certificates';
import { calibrationPage }     from './pages/calibration';

// What a charging station has pages for beside what every node has: the
// vehicle below the cable, what it may draw, the screen on its front, its
// EVSEs and card readers, the back ends it dials and what it proves itself
// with, and what it is calibrated under. Each entry is shown to whoever may
// read what its page reads, as CSHTTPAPI asks it - the three pages about
// dialling out all read the connections. The sign-in, the log, the frame,
// "/" - the first page of the menu somebody may open - and following the log
// while somebody is signed in are every node's; see WWCP_Node's start.ts.
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
            { path: '/configuration/calibration',     label: 'Calibration',      icon: 'fa-scale-balanced',  permission: [ 'calibration:read' ]  }
        ]),
        nodeMenu.logs
    ],

    pages: {
        '/configuration':                 configurationPage,
        '/configuration/dns':             dnsPage,
        '/configuration/nts':             ntsPage,
        '/configuration/v2g':             v2gPage,
        '/configuration/power':           powerPage,
        '/configuration/display':         displayPage,
        '/configuration/evses':           evsesPage,
        '/configuration/rfid':            rfidPage,
        '/configuration/authentication':  authenticationPage,
        '/configuration/connections':     connectionsPage,
        '/configuration/client-keys':     clientKeysPage,
        '/configuration/certificates':    certificatesPage,
        '/configuration/calibration':     calibrationPage
    }

});
