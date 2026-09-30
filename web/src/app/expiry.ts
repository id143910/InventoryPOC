import { Component, booleanAttribute, computed, input } from '@angular/core';
import { nearness } from './api';

/**
 * A deadline, with how near it is.
 *
 * The date as the systems wrote it, then "in 12 days" beside it - amber once it is
 * inside a month, rose once it has passed. One component, so a table cell and an
 * entity's header read a certificate's expiry the same way.
 */
@Component({
  selector: 'app-expiry',
  template: `
    <span class="whitespace-nowrap">{{ value() }}</span>
    @if (near(); as near) {
      <span class="whitespace-nowrap rounded px-1.5 py-px text-[0.7rem]"
            [class]="stacked() ? '-ml-1.5 mt-0.5 block w-fit' : 'ml-1.5'"
            [class.bg-rose-500/15]="near.tone === 'past'" [class.text-rose-300]="near.tone === 'past'"
            [class.bg-amber-500/15]="near.tone === 'soon'" [class.text-amber-300]="near.tone === 'soon'"
            [class.text-slate-500]="near.tone === 'later'">
        {{ near.words }}
      </span>
    }
  `,
})
export class ExpiryComponent {
  readonly value = input<unknown>();
  /** Under the date rather than beside it, so a table column stays narrow. */
  readonly stacked = input(false, { transform: booleanAttribute });
  readonly near = computed(() => nearness(this.value()));
}
