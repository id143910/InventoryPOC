import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

/**
 * The wire contract, and the one client that speaks it.
 *
 * Every request the application makes is here, and every shape it expects back. The
 * paths are relative, so the dev server's proxy decides where the API is and nothing
 * in the app has an address in it.
 */

export const IS = 'is';
export const CONTAINS = 'contains';
export const ALL = 'all';
export const ANY = 'any';

/** How a metadata key reads, which decides what can be asked of it. */
export const TEXT = 'text';
export const DATE = 'date';
export const NUMBER = 'number';
export const TAGS = 'tags';
export const LONG_TEXT = 'long_text';
/** A date that is a deadline: asked like a date, shown as how near it is. */
export const EXPIRY = 'expiry';

/** Operators that ask for a threshold rather than a value. */
export const ORDERING = ['before', 'after', 'greater than', 'less than'];

/** One test, on the link a stage crosses or on the entity it lands on. */
export interface Condition {
  field: string;
  operator: string;
  value: string;
}

/**
 * One set of rows: how you got there, and what narrows it. `hop` is null for
 * stage 0, the set you start from - the only thing that distinguishes it, which is
 * why one control builds them all.
 */
export interface Stage {
  hop: string | null;
  match: string;
  conditions: Condition[];
}

/** A start and a chain of stages. Everything the table is showing. */
export interface Query {
  type: string;
  key: string | null;
  stages: Stage[];
  frozen: boolean;
  page?: number;
  /** Asked for by the dashboard, which wants a count and a few names, not a table. */
  page_size?: number;
  /** One column to order by, `-column` for descending. Absent is the default order. */
  sort?: string | null;
}

/** One merged header field: the trusted value, and who disagreed. */
export interface Value {
  key: string;
  label: string;
  value: unknown;
  source: string;
  reported: Record<string, unknown>;
  mismatch: boolean;
}

/** One source's row: everything that system said about one entity. */
export interface Facet {
  source: string;
  source_type: string;
  external_id: string | null;
  discovered_at: string | null;
  last_seen_at: string | null;
  synced_at: string | null;
  frozen: boolean;
  metadata: Record<string, unknown>;
}

/** An entity as a row in a table: merged, without every source's full account. */
export interface EntityRow {
  type: string;
  natural_key: string;
  label: string;
  known: boolean;
  sources: string[];
  values: Value[];
}

/** One entity on the other end of a relation, and how often it is there. */
export interface Neighbour {
  type: string;
  natural_key: string;
  occurrences: number;
}

/**
 * One shape an entity takes part in, counted in things rather than rows.
 *
 * "installed on 12 servers" is the useful sentence, so `neighbours` leads;
 * `occurrences` is the larger number behind it, because the same certificate can
 * be installed on one host more than once.
 */
export interface Box {
  hop: string;
  reads: string;
  other_type: string;
  other_label: string;
  neighbours: number;
  occurrences: number;
  frozen: number;
  listed: boolean;
  others: number;
  named: Neighbour[];
}

/** Something else that can be done with an entity: a box to read, or a button. */
export interface Plugin {
  name: string;
  label: string;
  kind: string;
}

export const BOX = 'box';
export const ACTION = 'action';

/** What a plugin answered. A failure is a result with `ok` false, not an error. */
export interface Answer extends Plugin {
  ok: boolean;
  status: number;
  fields: { label: string; value: string | null }[];
  message: string | null;
}

/** An entity as a page: the row, plus what each system said on its own. */
export interface Entity extends EntityRow {
  facets: Facet[];
  hops: { hop: string; reads: string; total: number; active: number }[];
  neighbourhood: Box[];
  plugins: Plugin[];
}

/** One relation row: the entity reached, and what makes this row distinct. */
export interface Occurrence {
  /** The stage-0 entities this row began from. Empty when the query began at one. */
  origins: string[];
  from: { type: string; natural_key: string };
  entity: { type: string; natural_key: string };
  metadata: Record<string, unknown>;
  source: string;
  last_seen_at: string | null;
  frozen: boolean;
}

export interface Result<T> {
  reads: string;
  data: T[];
  total: number;
  page: number;
  pages: number;
  /** One per declared metadata key of the last hop's shape. Absent with no hops. */
  columns?: string[];
  reached?: string;
  sort?: string;
}

/** One entity a search found. */
export interface Found {
  type: string;
  label: string;
  natural_key: string;
}

export interface Schema {
  shapes: {
    source_type: string;
    relation_type: string;
    target_type: string;
    count: number;
    standing_on_source: string;
    standing_on_target: string;
  }[];
  /** What may be asked of a field, keyed by how the field reads. */
  operators: Record<string, string[]>;
  /** How each named metadata key reads. Anything absent is text. */
  field_types: Record<string, string>;
  matches: string[];
  /** The label the config gives a merged field, by key. */
  labels: Record<string, string>;
  types: { type: string; label: string; count: number }[];
  /** The source a person writes as, and the fields only they ever set. */
  manual: { source: string; source_type: string; fields: { key: string; label: string }[] };
  /** Every declared type's header fields, so one not filled in can still be offered. */
  entity_types: {
    type: string;
    label: string;
    filters: string[];
    unified: { key: string; label: string }[];
  }[];
}

/** One field a condition at some stage can be about, and how it reads. */
export interface Field {
  value: string;
  label: string;
  group: string;
  type: string;
}

/** What a stage can offer: where it can go, and what it can be filtered on. */
export interface StageOptions {
  hops: { hop: string; reads: string; total: number; active: number }[];
  fields: Field[];
}

@Injectable({ providedIn: 'root' })
export class Api {
  private readonly http = inject(HttpClient);

  schema(): Observable<Schema> {
    return this.http.get<Schema>('/api/schema');
  }

  entity(type: string, key: string, frozen = false): Observable<Entity> {
    return this.http.get<Entity>(
      `/api/entities/${encodeURIComponent(type)}/${encodeURIComponent(key)}`,
      { params: { frozen } },
    );
  }

  /**
   * Run one plugin against one entity: read a box, or do an action.
   *
   * The config decides which a plugin is and which method reaches it, so this only
   * has to know it is one or the other.
   */
  plugin(type: string, key: string, name: string, kind: string): Observable<Answer> {
    const url = `/api/entities/${encodeURIComponent(type)}/${encodeURIComponent(key)}/plugins/${name}`;
    return kind === BOX ? this.http.get<Answer>(url) : this.http.post<Answer>(url, {});
  }

  /**
   * What a person says about one entity, merged into the manual source's row.
   *
   * The one write in the application. A field given as an empty string is an
   * override let go of, and the syncs are trusted again.
   */
  manual(type: string, key: string, fields: Record<string, unknown>) {
    return this.http.put<void>(
      `/api/entities/${encodeURIComponent(type)}/${encodeURIComponent(key)}/manual`,
      { fields },
    );
  }

  /** With no hops the rows are entities; with hops they are occurrences. */
  entities(query: Query): Observable<Result<EntityRow>> {
    return this.run<EntityRow>(query);
  }

  occurrences(query: Query): Observable<Result<Occurrence>> {
    return this.run<Occurrence>(query);
  }

  /** What one stage can offer, given where it stands and which hop it took. */
  stage(type: string, key: string | null, hop: string | null): Observable<StageOptions> {
    const params: Record<string, string> = { type };
    if (key) params['key'] = key;
    if (hop) params['hop'] = hop;
    return this.http.get<StageOptions>('/api/stage', { params });
  }

  /**
   * The values one condition's field takes. Read from the rows the stage crosses,
   * so a choice is only ever something that would match.
   */
  values(query: Query, stage: number, condition: number) {
    return this.http.post<{ data: { value: string; count: number }[]; truncated: boolean }>(
      '/api/values',
      { query, stage, condition },
    );
  }

  /** Entities by any part of their key, those starting with it first. */
  search(text: string): Observable<Found[]> {
    return this.http.get<Found[]>('/api/search', { params: { q: text } });
  }

  /** The same query as a file: every row rather than a page. */
  csv(query: Query): Observable<Blob> {
    return this.http.post('/api/query/csv', query, { responseType: 'blob' });
  }

  private run<T>(query: Query): Observable<Result<T>> {
    return this.http.post<Result<T>>('/api/query', query, {
      params: { page: query.page ?? 1 },
    });
  }
}

/**
 * The control a condition's value wants.
 *
 * A threshold is not in the list of values present, and neither is a fragment, so
 * those are typed in - and the input knows what kind of value it is after. Anything
 * else picks from what is actually there.
 */
export function entryFor(operator: string, type: string): string {
  if (ORDERING.includes(operator)) {
    // A date is typed, not picked: `now+60d` is a date the calendar cannot express,
    // and it is the one worth saving, so the same box has to take both.
    return type === NUMBER ? 'number' : 'text';
  }
  return operator === CONTAINS ? 'text' : '';
}

/** What an empty threshold box says it will take. */
export function hintFor(type: string): string {
  if (type === DATE || type === EXPIRY) return '2027-06-01 or now+60d';
  if (type === TAGS) return 'a tag';
  return type === NUMBER ? 'a number' : 'text';
}

/** A blank condition: the row the builder offers to add another. */
export function blankCondition(): Condition {
  return { field: '', operator: IS, value: '' };
}

/**
 * A query as URL parameters.
 *
 * The query lives in the URL, so every state the interface can reach is a link -
 * which means a saved query, a box on an entity's page and the builder's own
 * navigation are all the same thing, and all go through here.
 */
export function paramsOf(query: Query, page = 1): Record<string, string | null> {
  return {
    type: query.type,
    key: query.key || null,
    stages: encodeStages(query.stages),
    frozen: query.frozen ? '1' : null,
    sort: query.sort || null,
    page: page > 1 ? String(page) : null,
  };
}

/** A query's stages, as they travel in the browser's URL. */
export function encodeStages(stages: Stage[]): string {
  return JSON.stringify(stages.map((stage) => [
    stage.hop ?? '',
    stage.match,
    stage.conditions.filter((item) => item.field).map((item) => [item.field, item.operator, item.value]),
  ]));
}

export function decodeStages(raw: string | null): Stage[] | null {
  if (!raw) return null;
  try {
    const parsed = JSON.parse(raw) as [string, string, [string, string, string][]][];
    return parsed.map(([hop, match, conditions]) => ({
      hop: hop || null,
      match,
      conditions: conditions.map(([field, operator, value]) => ({ field, operator, value })),
    }));
  } catch {
    return null;
  }
}

/** How near a deadline is, in words, and how worried to be about it. */
export interface Nearness {
  words: string;
  tone: 'past' | 'soon' | 'later';
}

/** Anything under this many days away is close enough to colour. */
export const SOON_DAYS = 30;

export function nearness(value: unknown, today = new Date()): Nearness | null {
  const text = typeof value === 'string' ? value : '';
  if (!/^\d{4}-\d{2}-\d{2}/.test(text)) return null;
  const day = 86_400_000;
  const due = Date.UTC(+text.slice(0, 4), +text.slice(5, 7) - 1, +text.slice(8, 10));
  const now = Date.UTC(today.getFullYear(), today.getMonth(), today.getDate());
  const days = Math.round((due - now) / day);
  const span = (count: number) => {
    const size = Math.abs(count);
    if (size >= 365) return `${Math.round(size / 365)} year${Math.round(size / 365) === 1 ? '' : 's'}`;
    if (size >= 60) return `${Math.round(size / 30)} months`;
    return `${size} day${size === 1 ? '' : 's'}`;
  };
  if (days === 0) return { words: 'today', tone: 'past' };
  if (days < 0) return { words: `${span(days)} ago`, tone: 'past' };
  return { words: `in ${span(days)}`, tone: days < SOON_DAYS ? 'soon' : 'later' };
}
