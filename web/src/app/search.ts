import { Component, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatAutocompleteModule, MatAutocompleteSelectedEvent } from '@angular/material/autocomplete';
import { Router } from '@angular/router';
import { Subject, debounceTime, distinctUntilChanged, of, switchMap } from 'rxjs';
import { Api, Found } from './api';

/**
 * Straight to one entity by its name.
 *
 * The builder answers "which servers…"; this answers "prod-server-0036, please".
 * Any part of a key finds it, those starting with what was typed first, and picking
 * one opens its page.
 */
@Component({
  selector: 'app-search',
  imports: [MatAutocompleteModule],
  template: `
    <input #box type="search" placeholder="find an entity…" aria-label="find an entity"
           class="w-36 rounded border border-slate-700 bg-slate-950 px-2.5 py-1 text-sm text-slate-100 outline-none focus:border-cyan-400/60 sm:w-64"
           [value]="text()" (input)="typed($any($event.target).value)" [matAutocomplete]="found">
    <mat-autocomplete #found="matAutocomplete" (optionSelected)="open($event); box.blur()">
      @for (item of results(); track item.type + item.natural_key) {
        <mat-option [value]="item">
          <span class="text-sm">{{ item.natural_key }}</span>
          <span class="ml-2 text-xs text-slate-500">{{ item.label }}</span>
        </mat-option>
      }
    </mat-autocomplete>
  `,
})
export class SearchComponent {
  private readonly api = inject(Api);
  private readonly router = inject(Router);
  private readonly asked = new Subject<string>();

  readonly text = signal('');

  /** Asked as the typing pauses, and only the latest answer is kept. */
  readonly results = toSignal(this.asked.pipe(
    debounceTime(150),
    distinctUntilChanged(),
    switchMap((text) => (text.trim().length < 2 ? of<Found[]>([]) : this.api.search(text))),
  ), { initialValue: [] as Found[] });

  typed(text: string): void {
    this.text.set(text);
    this.asked.next(text);
  }

  open(event: MatAutocompleteSelectedEvent): void {
    const item = event.option.value as Found;
    this.text.set('');
    this.asked.next('');
    this.router.navigate(['/entity', item.type, item.natural_key]);
  }
}
