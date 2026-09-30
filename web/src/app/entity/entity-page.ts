import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { rxResource, toSignal } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatIconModule } from '@angular/material/icon';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import {
  ACTION, ALL, Answer, Api, BOX, Box, Plugin, TAGS, Value, encodeStages,
} from '../api';
import { BoxComponent } from '../box';

/**
 * One entity, to read rather than to query.
 *
 * A wiki page: what the systems agree it is, what each of them says on its own,
 * and the neighbourhood - a box per relation, counted in things rather than rows,
 * naming its neighbours when there are few enough to read. Every name is a link to
 * that entity's own page, so the graph is browsable by clicking.
 *
 * Deliberately no builder and no conditions. A box is a link into the query
 * surface, which is where rows, metadata and filtering belong.
 *
 * The header's fields can be written, though, which is reading's natural other
 * half: you are looking at what the systems say, so this is where you would correct
 * it. A correction is not an edit of a sync's row - it is one more row, from the
 * manual source, which outranks them and leaves what they said on display.
 */
@Component({
  selector: 'app-entity-page',
  imports: [
    RouterLink, BoxComponent,
    MatButtonModule, MatCardModule, MatChipsModule, MatIconModule,
  ],
  templateUrl: './entity-page.html',
})
export class EntityPageComponent {
  private readonly api = inject(Api);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  /** The route's `:type` and `:key`, and its `?frozen=1`. */
  readonly type = input.required<string>();
  readonly key = input.required<string>();
  readonly frozenParam = input<string | undefined>(undefined, { alias: 'frozen' });
  readonly frozen = computed(() => this.frozenParam() === '1');

  readonly schema = toSignal(this.api.schema(), { initialValue: null });

  /**
   * The entity the route names. When the route names another one, the read in
   * flight for the last is cancelled, so a slow answer about the entity you just
   * left can never land on the page of the one you are reading.
   */
  private readonly found = rxResource({
    params: () => ({ type: this.type(), key: this.key(), frozen: this.frozen() }),
    stream: ({ params }) => this.api.entity(params.type, params.key, params.frozen),
  });
  readonly entity = computed(() => (this.found.hasValue() ? this.found.value() : null));
  readonly missing = computed(() => this.found.status() === 'error');

  /** The field being written, while one is being written, and what is typed in it. */
  readonly editing = signal<string | null>(null);
  readonly draft = signal('');

  /** The tag being typed. Tags are added one at a time, so they have their own. */
  readonly tagDraft = signal('');

  /**
   * What the plugins said, by name: the boxes read when the page opened, and the
   * actions somebody has pressed. Each is fetched on its own, so a slow endpoint
   * holds up only itself and never the page.
   */
  readonly boxes = signal<Answer[]>([]);
  readonly acted = signal<Answer[]>([]);
  readonly running = signal<string | null>(null);

  constructor() {
    // Every box this entity has, read afresh whenever the entity is. The reads for
    // an entity no longer on the page are cancelled rather than left to land on it.
    effect((onCleanup) => {
      const found = this.entity();
      this.boxes.set([]);
      this.acted.set([]);
      if (!found) return;
      const reads = new Subscription();
      for (const plugin of found.plugins.filter((item) => item.kind === BOX)) {
        reads.add(this.api.plugin(found.type, found.natural_key, plugin.name, BOX).subscribe({
          next: (answer) => this.boxes.update((kept) => [...kept, answer]),
          // One that cannot be read arrives as its reason - otherwise it would say
          // "Reading…" forever.
          error: (response: { error?: { detail?: string } }) =>
            this.boxes.update((kept) => [...kept, {
              ...plugin, ok: false, status: 0, fields: [],
              message: response.error?.detail ?? `${plugin.label} could not be read`,
            }]),
        }));
      }
      onCleanup(() => reads.unsubscribe());
    });
  }

  /**
   * Every field the header carries, whether or not anything has filled it in.
   *
   * The config's list rather than the values present, because a comment nobody has
   * written yet still needs somewhere to be written.
   */
  fields(): { key: string; label: string }[] {
    const found = this.entity();
    if (!found) return [];
    const declared = this.schema()?.entity_types.find((item) => item.type === found.type)?.unified;
    return declared ?? found.values.map((value) => ({ key: value.key, label: value.label }));
  }

  valueOf(key: string): Value | undefined {
    return this.entity()?.values.find((value) => value.key === key);
  }

  /** True where the value on the header is a person's rather than a sync's. */
  written(key: string): boolean {
    return this.valueOf(key)?.source === this.schema()?.manual.source;
  }

  /** A field only a person ever sets is prose, so it is given room to be prose. */
  prose(key: string): boolean {
    return (this.schema()?.manual.fields ?? []).some((field) => field.key === key);
  }

  /** Start writing one field, from whatever it currently says. */
  edit(key: string): void {
    this.draft.set(`${this.valueOf(key)?.value ?? ''}`);
    this.editing.set(key);
  }

  save(key: string, value: string): void {
    this.editing.set(null);
    this.write(key, value);
  }

  /** A list of words rather than a value, so it gets chips rather than a text box. */
  tagged(key: string): boolean {
    return this.schema()?.field_types?.[key] === TAGS;
  }

  tags(key: string): string[] {
    const value = this.valueOf(key)?.value;
    return Array.isArray(value) ? value.map((tag) => `${tag}`) : [];
  }

  tag(key: string): void {
    const given = this.tagDraft().trim();
    if (!given) return;
    this.tagDraft.set('');
    this.write(key, [...this.tags(key), given]);
  }

  untag(key: string, tag: string): void {
    // An empty list is a field let go of, which is what dropping the last tag means.
    this.write(key, this.tags(key).filter((kept) => kept !== tag));
  }

  private write(key: string, value: unknown): void {
    const found = this.entity()!;
    // Read again rather than patched locally: the facets change too, and the page
    // keeps showing the old answer until the new one is in.
    this.api.manual(found.type, found.natural_key, { [key]: value })
      .subscribe(() => this.found.reload());
  }

  /**
   * The boxes this type has, whether or not they have answered yet.
   *
   * From the config rather than from the answers, so the section is there while they
   * are still being read and the page does not jump when they arrive.
   */
  readable(): Plugin[] {
    return (this.entity()?.plugins ?? []).filter((item) => item.kind === BOX);
  }

  /** The buttons this type offers. Nothing is called until one is pressed. */
  actions(): Plugin[] {
    return (this.entity()?.plugins ?? []).filter((item) => item.kind === ACTION);
  }

  act(plugin: Plugin): void {
    const found = this.entity()!;
    this.running.set(plugin.name);
    this.api.plugin(found.type, found.natural_key, plugin.name, ACTION).subscribe({
      next: (answer) => {
        this.running.set(null);
        // The newest answer for a plugin replaces its last one; pressing twice should
        // not read as two things having happened.
        this.acted.update((kept) => [...kept.filter((one) => one.name !== answer.name), answer]);
      },
      error: (response: { error?: { detail?: string } }) => {
        this.running.set(null);
        this.acted.update((kept) => [...kept.filter((one) => one.name !== plugin.name), {
          ...plugin, ok: false, status: 0, fields: [],
          message: response.error?.detail ?? `${plugin.label} could not be done`,
        }]);
      },
    });
  }

  toggleFrozen(): void {
    this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { frozen: this.frozen() ? null : '1' },
    });
  }

  /** What a box is counting, in things rather than rows. */
  note(box: Box): string {
    const things = `${box.neighbours.toLocaleString()} ${box.other_label.toLowerCase()}${box.neighbours === 1 ? '' : 's'}`;
    // The larger number behind it, where they differ: the same certificate can be
    // installed on one host twice.
    const occurrences = box.occurrences === box.neighbours
      ? '' : ` · ${box.occurrences.toLocaleString()} occurrences`;
    return things + occurrences + (box.frozen ? ` · ${box.frozen} no longer reported` : '');
  }

  /** The query a box opens: this entity, then that one hop. */
  params(hop: string): Record<string, string> {
    const entity = this.entity()!;
    const params: Record<string, string> = {
      type: entity.type,
      key: entity.natural_key,
      stages: encodeStages([
        { hop: null, match: ALL, conditions: [] },
        { hop, match: ALL, conditions: [] },
      ]),
    };
    if (this.frozen()) {
      params['frozen'] = '1';
    }
    return params;
  }

  /** The query that starts from this entity with nothing followed yet. */
  get here(): Record<string, string> {
    const entity = this.entity()!;
    return { type: entity.type, key: entity.natural_key };
  }

  date(value: string | null): string {
    return value ? value.slice(0, 10) : '—';
  }

  pairs(metadata: Record<string, unknown>): { key: string; value: unknown }[] {
    return Object.entries(metadata).map(([key, value]) => ({ key, value }));
  }

  labelOf(key: string): string {
    return key.replace(/_/g, ' ');
  }
}
