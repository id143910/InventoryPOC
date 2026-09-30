import { Component, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatIconModule } from '@angular/material/icon';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import {
  ACTION, ALL, Answer, Api, BOX, Box, EXPIRY, Entity, Facet, Plugin, Schema, TAGS, Value, encodeStages,
} from '../api';
import { BoxComponent } from '../box';
import { ExpiryComponent } from '../expiry';

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
    RouterLink, BoxComponent, ExpiryComponent,
    MatButtonModule, MatCardModule, MatChipsModule, MatIconModule,
  ],
  templateUrl: './entity-page.html',
})
export class EntityPageComponent {
  private readonly api = inject(Api);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  readonly entity = signal<Entity | null>(null);
  readonly schema = signal<Schema | null>(null);
  readonly frozen = signal(false);
  readonly missing = signal(false);

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
    this.api.schema().subscribe((schema) => this.schema.set(schema));
    this.route.paramMap.subscribe((params) => {
      const type = params.get('type')!;
      const key = params.get('key')!;
      this.route.queryParamMap.subscribe((query) => {
        this.frozen.set(query.get('frozen') === '1');
        this.read(type, key);
      });
    });
  }

  private read(type: string, key: string): void {
    this.api.entity(type, key, this.frozen()).subscribe({
      next: (found) => {
        this.entity.set(found);
        this.missing.set(false);
        this.fill(found);
      },
      error: () => {
        this.entity.set(null);
        this.missing.set(true);
      },
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

  /** A deadline, shown with how near it is. */
  expiry(key: string): boolean {
    return this.schema()?.field_types?.[key] === EXPIRY;
  }

  /** Every source but the person, whose word the header already shows as theirs. */
  systems(): Facet[] {
    const manual = this.schema()?.manual.source;
    return (this.entity()?.facets ?? []).filter((facet) => facet.source !== manual);
  }

  /**
   * How a source's rows got here. A system's "manual" means somebody typed the
   * record into it - not the same thing as a person's override here, which is why
   * the two are never both called manual.
   */
  kind(facet: Facet): string {
    return facet.source_type === 'automated' ? 'automated feed' : 'entered by hand';
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
    this.api.manual(found.type, found.natural_key, { [key]: value })
      .subscribe(() => this.read(found.type, found.natural_key));
  }

  /**
   * Read every box this type has. They arrive as they arrive, and one that cannot
   * be read arrives as its reason - otherwise it would say "Reading…" forever.
   */
  private fill(found: Entity): void {
    this.boxes.set([]);
    this.acted.set([]);
    for (const plugin of found.plugins.filter((item) => item.kind === BOX)) {
      this.api.plugin(found.type, found.natural_key, plugin.name, BOX).subscribe({
        next: (answer) => this.boxes.update((kept) => [...kept, answer]),
        error: (response: { error?: { detail?: string } }) =>
          this.boxes.update((kept) => [...kept, {
            ...plugin, ok: false, status: 0, fields: [],
            message: response.error?.detail ?? `${plugin.label} could not be read`,
          }]),
      });
    }
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
