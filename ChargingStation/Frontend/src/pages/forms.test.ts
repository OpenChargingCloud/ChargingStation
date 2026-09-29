/**
 * What the charging station's pages are held to beyond the rules of WWCP_Node's
 * test/pages.ts, until those say it too: every form a page draws is known by an
 * id, or by a data-id.
 *
 * A page drawn anew puts back what was typed into its other forms with
 * keepDrafts, and keepDrafts finds a form again by its id, or by its data-id
 * where it has none. A form with neither cannot be told from its neighbours,
 * and is drawn as the node has it: the forms drawn for each set of credentials
 * and for each connection had neither, and what was typed into one went when
 * another was saved.
 *
 * Run with `npm test`.
 */

import { strict as assert } from 'node:assert';
import { describe, it }     from 'node:test';

import { pagesIn, type Page } from '@node/../test/pages.ts';


const page = (source: string): Page => ({ name: 'page.ts', source });

/** The forms of a page known by neither an id nor a data-id, as they are written. */
function formsKnownByNothing(Page: Page): string[] {
    return [ ...Page.source.matchAll(/<form(\s[^>]*)?>/g) ].
               filter(match => !/\s(id|data-id)="[^"]+"/.test(match[1] ?? '')).
               map(match => match[0].replace(/\s+/g, ' '));
}


describe('a form known by neither an id nor a data-id', () => {

    it('is found, and a form drawn for each entry with a data-id is not', () => {
        assert.deepEqual(formsKnownByNothing(page(`<form id="add-form" class="form-stack">
                                                   <form class="form-stack" data-id="\${entry.id}" data-edit="\${entry.id}">
                                                   <form class="form-stack"
                                                         data-edit="\${entry.id}" data-kind-form="\${entry.id}">`)),
                         [ '<form class="form-stack" data-edit="${entry.id}" data-kind-form="${entry.id}">' ]);
    });

});


describe('a form on a page drawn anew', () => {

    for (const shown of pagesIn(new URL('./', import.meta.url)).filter(one => one.source.includes('<form')))
        it(`${shown.name} is known by an id or a data-id, so that keepDrafts finds it again`, () => {
            assert.deepEqual(formsKnownByNothing(shown), [],
                             `${shown.name} draws a form keepDrafts cannot tell from its neighbours, and what is typed ` +
                             `into it goes when the page draws itself anew - an id, or a data-id for a form drawn for each entry`);
        });

});
