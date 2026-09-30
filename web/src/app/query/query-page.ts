import { DecimalPipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
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
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Observable } from 'rxjs';
import {
  ALL, Api, EntityRow, Occurrence, Query, Result, Schema, Stage, Value,
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
    MatPaginatorModule, MatProgressBarModule, MatTableModule,
  ],
  templateUrl: './query-page.html',
})
export class QueryPageComponent {
  private readonly api = inject(Api);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  readonly saved = inject(Saved);

  readonly schema = signal<Schema | null>(null);
  readonly query = signal<Query>({ type: '', key: null, stages: [], frozen: false, page: 1 });
  readonly error = signal<string | null>(null);
  readonly loading = signal(false);

  /** The name being given to this query, while one is being given. */
  readonly naming = signal<string | null>(null);

  /**
   * What the table is showing, as one value.
   *
   * The rows and the columns that read them have to change together. Deriving the
   * columns from the query while the rows came from the last response leaves one
   * render where a table of occurrences is read as a table of entities, and the
   * cells reach for fields that are not there. So the shape travels with the rows.
   */
  readonly view = signal<View | null>(null);

  readonly columns = computed(() => this.view()?.columns ?? []);
  readonly rows = computed(() => this.view()?.result.data ?? []);
  readonly listsEntities = computed(() => this.view()?.entities ?? true);
  readonly result = computed(() => this.view()?.result ?? null);

  constructor() {
    this.api.schema().subscribe((schema) => {
      this.schema.set(schema);
      this.route.queryParamMap.subscribe((params) => {
        const type = params.get('type') ?? schema.types[0]?.type ?? '';
        const key = params.get('key');
        const stages = decodeStages(params.get('stages')) ?? [
          { hop: null, match: ALL, conditions: [blankCondition()] },
        ];
        this.query.set({
          type,
          key,
          stages,
          frozen: params.get('frozen') === '1',
          page: Number(params.get('page') ?? 1),
        });
        this.load();
      });
    });
  }

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

  private load(): void {
    const query = this.query();
    if (!query.type) return;
    // The query decides which call to make; the answer decides what the table is.
    const entities = query.stages.length <= 1;
    this.loading.set(true);
    this.error.set(null);
    const call: Observable<Result<EntityRow | Occurrence>> = entities
      ? this.api.entities(query)
      : this.api.occurrences(query);
    call.subscribe({
      next: (result: Result<EntityRow | Occurrence>) => {
        this.view.set({ entities, result, columns: this.shape(entities, query, result) });
        this.loading.set(false);
      },
      error: (response: { error?: { detail?: string } }) => {
        this.error.set(response.error?.detail ?? 'That query could not be built.');
        this.view.set(null);
        this.loading.set(false);
      },
    });
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
