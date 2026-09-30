import { DecimalPipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, input, linkedSignal, signal } from '@angular/core';
import { rxResource, toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTableModule } from '@angular/material/table';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Observable, map } from 'rxjs';
import {
  ALL, Api, EntityRow, LONG_TEXT, Occurrence, Query, Result, Stage, Value,
  blankCondition, decodeStages, paramsOf,
} from '../api';
import { Saved } from '../saved';
import { BuilderComponent } from './builder';

/** The table's rows and the columns that read them, which must change together. */
interface View {
  entities: boolean;
  result: Result<EntityRow | Occurrence>;
  columns: string[];
}

/**
 * The query surface: one builder, one table.
 *
 * Listing, filtering and traversing are the same thing, so they are the same
 * query - a start and a chain of stages. With no hops the rows are entities; with
 * hops they are the occurrences of the last one. Starting from a named entity
 * pins stage 0 to it, as a chip that links to its page and can be dismissed.
 *
 * The query lives in the URL, so every state the interface can reach is a link.
 */
@Component({
  selector: 'app-query-page',
  imports: [
    DecimalPipe,
    FormsModule, RouterLink, BuilderComponent,
    MatButtonModule, MatCardModule, MatChipsModule,
    MatFormFieldModule, MatIconModule, MatInputModule,
    MatPaginatorModule, MatProgressBarModule, MatTableModule, MatTooltipModule,
  ],
  templateUrl: './query-page.html',
})
export class QueryPageComponent {
  private readonly api = inject(Api);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  readonly saved = inject(Saved);

  /** The query, as the URL carries it. */
  readonly typeParam = input<string | undefined>(undefined, { alias: 'type' });
  readonly keyParam = input<string | undefined>(undefined, { alias: 'key' });
  readonly stagesParam = input<string | undefined>(undefined, { alias: 'stages' });
  readonly frozenParam = input<string | undefined>(undefined, { alias: 'frozen' });
  readonly pageParam = input<string | undefined>(undefined, { alias: 'page' });

  readonly schema = toSignal(this.api.schema(), { initialValue: null });

  /** The query the URL describes. With no type named, the first one there is. */
  readonly query = computed<Query>(() => ({
    type: this.typeParam() ?? this.schema()?.types[0]?.type ?? '',
    key: this.keyParam() ?? null,
    stages: decodeStages(this.stagesParam() ?? null) ?? [
      { hop: null, match: ALL, conditions: [blankCondition()] },
    ],
    frozen: this.frozenParam() === '1',
    page: Number(this.pageParam() ?? 1),
  }));

  /**
   * The answer to the query in the URL. A new query cancels the one in flight, so
   * a slow answer to an older question can never land on the table after the
   * newer one. Each answer is shaped by the query it belongs to - see `view`.
   */
  private readonly answer = rxResource({
    params: () => (this.query().type ? this.query() : undefined),
    stream: ({ params: query }) => {
      // The query decides which call to make; the answer decides what the table is.
      const entities = query.stages.length <= 1;
      const call: Observable<Result<EntityRow | Occurrence>> = entities
        ? this.api.entities(query)
        : this.api.occurrences(query);
      return call.pipe(map((result): View => ({ entities, result, columns: this.shape(entities, query, result) })));
    },
  });
  readonly loading = computed(() => this.answer.isLoading());
  readonly error = computed(() => this.answer.status() === 'error'
    ? (this.answer.error() as HttpErrorResponse | undefined)?.error?.detail ?? 'That query could not be built.'
    : null);

  /** The name being given to this query, while one is being given. */
  readonly naming = signal<string | null>(null);

  /**
   * What the table is showing, as one value.
   *
   * The rows and the columns that read them have to change together. Deriving the
   * columns from the query while the rows came from the last response leaves one
   * render where a table of occurrences is read as a table of entities, and the
   * cells reach for fields that are not there. So the shape travels with the rows,
   * worked out from the query they answered rather than the one in the URL now.
   *
   * While the next answer is on its way the last one stays on screen, as a table
   * does when you turn its page; a query that fails clears it.
   */
  readonly view = linkedSignal<{ answered: View | undefined; failed: boolean }, View | null>({
    source: () => ({
      answered: this.answer.hasValue() ? this.answer.value() : undefined,
      failed: this.answer.status() === 'error',
    }),
    computation: (now, before) => now.answered ?? (now.failed ? null : before?.value ?? null),
  });

  readonly columns = computed(() => this.view()?.columns ?? []);
  readonly rows = computed(() => this.view()?.result.data ?? []);
  readonly listsEntities = computed(() => this.view()?.entities ?? true);
  readonly result = computed(() => this.view()?.result ?? null);

  /** Every change to the query goes through the URL, so back and links both work. */
  apply(query: Query, page = 1): void {
    this.router.navigate([], {
      relativeTo: this.route,
      queryParams: paramsOf(query, page),
      queryParamsHandling: 'merge',
    });
  }

  /** Back to the bare type: every hop and every condition let go of at once. */
  reset(): void {
    this.apply({
      type: this.query().type, key: null, frozen: false,
      stages: [{ hop: null, match: ALL, conditions: [blankCondition()] }],
    });
  }

  /**
   * Offer to name this query. The sentence it reads as is the obvious first
   * suggestion, since it is already a description of what you built.
   */
  name(): void {
    this.naming.set(this.result()?.reads ?? '');
  }

  keep(name: string): void {
    this.saved.save(name, this.query());
    this.naming.set(null);
  }

  onStages(stages: Stage[]): void {
    this.apply({ ...this.query(), stages });
  }

  onType(type: string): void {
    // A different type starts a different query; its old hops cannot apply.
    this.apply({ type, key: null, stages: [{ hop: null, match: ALL, conditions: [blankCondition()] }], frozen: this.query().frozen });
  }

  /** Let go of the pinned entity and start from the whole type instead. */
  dismiss(): void {
    this.apply({ ...this.query(), key: null });
  }

  toggleFrozen(): void {
    this.apply({ ...this.query(), frozen: !this.query().frozen });
  }

  onPage(event: PageEvent): void {
    this.apply(this.query(), event.pageIndex + 1);
  }

  cell(row: EntityRow | Occurrence, column: string): unknown {
    if (this.listsEntities()) {
      return this.merged(row, column)?.value ?? '';
    }
    const occurrence = row as Occurrence;
    switch (column) {
      case 'entity': return occurrence.entity?.natural_key;
      case 'from': return occurrence.from?.natural_key;
      case 'source': return occurrence.source;
      default: return occurrence.metadata?.[column] ?? '';
    }
  }

  /** Who disagreed, for the cell's tooltip. */
  disagreement(value: Value): string {
    return Object.entries(value.reported)
      .filter(([source]) => source !== value.source)
      .map(([source, reported]) => `${source} says ${reported}`)
      .join(', ');
  }

  /** Prose, which a table shows cut short rather than letting it widen the row. */
  long(column: string): boolean {
    return this.schema()?.field_types?.[column] === LONG_TEXT;
  }

  /** One merged field of an entity row, for a column that shows it. */
  merged(row: EntityRow | Occurrence, column: string): Value | undefined {
    return ((row as EntityRow).values ?? []).find((value) => value.key === column);
  }

  /** Two systems describing one entity may disagree; the row says so. */
  hasMismatch(row: EntityRow | Occurrence): boolean {
    return ((row as EntityRow).values ?? []).some((value) => value.mismatch);
  }

  /** What to head a column: the config's label where it has one, the key made
      readable where it has none. */
  label(column: string): string {
    if (column === 'entity') return this.view()?.result.reached ?? 'Entity';
    if (column === 'from') return 'Via';
    if (column === 'source' || column === 'sources') return 'Reported by';
    if (column === 'natural_key') return this.query().type;
    return this.schema()?.labels?.[column] ?? column.replace(/_/g, ' ');
  }

  /**
   * One column per field the answer declares, plus the ends of it. A table of
   * entities lists what the config merges onto them; a table of occurrences lists
   * what its shape declares. Either way the columns come from the answer.
   */
  private shape(entities: boolean, query: Query, result: Result<EntityRow | Occurrence>): string[] {
    if (entities) {
      return ['natural_key', ...(result.columns ?? []), 'sources'];
    }
    const via = query.stages.length > 2 ? ['from'] : [];
    return [...via, 'entity', ...(result.columns ?? []), 'source'];
  }
}
