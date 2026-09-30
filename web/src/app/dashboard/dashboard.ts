import { Component, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { Observable } from 'rxjs';
import { Api, EntityRow, Occurrence, Query, Result, Schema, paramsOf } from '../api';
import { BoxComponent, Named } from '../box';
import { Saved } from '../saved';

/** One saved query, as much of its answer as a box can hold. */
interface Card {
  name: string;
  query: Query;
  params: Record<string, string | null>;
  note: string;
  named: Named[];
  others: number;
}

/** How many of a card's answer are worth naming on the front of it. */
const NAMED = 3;

/**
 * What you saved, answered.
 *
 * Each saved query is run for its count and its first few rows, which is the same
 * thing an entity's page does with its neighbours - so it is the same box, and
 * clicking one opens it in the builder where the rest of the rows are.
 *
 * With nothing saved there is nothing to show, so this steps aside and the builder
 * is the landing page, as it was before.
 */
@Component({
  selector: 'app-dashboard',
  imports: [BoxComponent, RouterLink],
  templateUrl: './dashboard.html',
})
export class DashboardComponent {
  private readonly api = inject(Api);
  private readonly router = inject(Router);
  readonly saved = inject(Saved);

  readonly cards = signal<Card[]>([]);
  readonly schema = signal<Schema | null>(null);

  constructor() {
    if (!this.saved.all().length) {
      this.router.navigate(['/query'], { replaceUrl: true });
      return;
    }
    this.api.schema().subscribe((schema) => this.schema.set(schema));
    this.cards.set(this.saved.all().map((item) => ({
      name: item.name,
      query: item.query,
      params: paramsOf(item.query),
      note: 'counting…',
      named: [],
      others: 0,
    })));
    this.cards().forEach((card) => this.count(card));
  }

  forget(name: string): void {
    this.saved.remove(name);
    this.cards.update((cards) => cards.filter((card) => card.name !== name));
    if (!this.saved.all().length) {
      this.router.navigate(['/query']);
    }
  }

  /** Run one saved query small: a total, and the first few things it reaches. */
  private count(card: Card): void {
    const entities = card.query.stages.length <= 1;
    const asked: Query = { ...card.query, page: 1, page_size: NAMED };
    const call: Observable<Result<EntityRow | Occurrence>> = entities
      ? this.api.entities(asked)
      : this.api.occurrences(asked);
    call.subscribe({
      next: (result: Result<EntityRow | Occurrence>) => this.fill(card, entities, result),
      error: () => this.amend(card, { note: 'this query can no longer be run' }),
    });
  }

  private fill(card: Card, entities: boolean, result: Result<EntityRow | Occurrence>): void {
    // A page of occurrences can name one entity twice - the same certificate in two
    // stores - and naming it twice on the front of a box says nothing, so the names
    // are the distinct ones among them.
    const reached = result.data.map((row) => (entities
      ? { type: (row as EntityRow).type, natural_key: (row as EntityRow).natural_key }
      : (row as Occurrence).entity));
    const named: Named[] = [];
    for (const one of reached) {
      if (!named.some((kept) => kept.natural_key === one.natural_key && kept.type === one.type)) {
        named.push(one);
      }
    }
    this.amend(card, {
      named,
      others: Math.max(0, result.total - named.length),
      note: entities
        ? `${result.total.toLocaleString()} ${this.plural(card.query.type, result.total)}`
        : `${result.total.toLocaleString()} ${result.total === 1 ? 'occurrence' : 'occurrences'}`,
    });
  }

  private amend(card: Card, change: Partial<Card>): void {
    this.cards.update((cards) => cards.map((one) => (one.name === card.name ? { ...one, ...change } : one)));
  }

  private plural(type: string, total: number): string {
    const label = (this.schema()?.types.find((one) => one.type === type)?.label ?? type).toLowerCase();
    return total === 1 ? label : `${label}s`;
  }
}
