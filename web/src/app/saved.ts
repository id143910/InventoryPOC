import { Injectable, signal } from '@angular/core';
import { Query } from './api';

/**
 * The queries worth keeping, named, in this browser.
 *
 * They live in localStorage rather than on the server because they belong to a
 * person, not to the graph, and the graph's rows are written by the syncs. The
 * cost is honest: they do not follow you to another machine.
 *
 * A saved query keeps only what makes it that query - where it starts, how it
 * hops, what narrows it. Which page you were on is not part of it, and a date
 * written as `now-60d` stays relative, so "expiring soon" still means that
 * next month.
 */

export interface SavedQuery {
  name: string;
  query: Query;
}

const KEY = 'inventory.saved-queries';

@Injectable({ providedIn: 'root' })
export class Saved {
  private readonly items = signal<SavedQuery[]>(read());

  /** Every saved query, in the order they were saved. */
  readonly all = this.items.asReadonly();

  /** Save under a name, replacing whatever that name meant before. */
  save(name: string, query: Query): void {
    const kept: SavedQuery = { name: name.trim(), query: bare(query) };
    if (!kept.name) return;
    this.items.update((items) => [...items.filter((item) => item.name !== kept.name), kept]);
    this.flush();
  }

  remove(name: string): void {
    this.items.update((items) => items.filter((item) => item.name !== name));
    this.flush();
  }

  private flush(): void {
    try {
      localStorage.setItem(KEY, JSON.stringify(this.items()));
    } catch {
      // A browser that refuses storage still gets a working session, just not a
      // remembered one.
    }
  }
}

/** What makes a query that query, without where you happened to be in it. */
function bare(query: Query): Query {
  return {
    type: query.type,
    key: query.key,
    stages: query.stages.filter((stage, index) => index === 0 || stage.hop),
    frozen: query.frozen,
  };
}

function read(): SavedQuery[] {
  try {
    const raw = JSON.parse(localStorage.getItem(KEY) ?? '[]');
    return Array.isArray(raw) ? raw.filter((item) => item?.name && item?.query?.type) : [];
  } catch {
    return [];
  }
}
