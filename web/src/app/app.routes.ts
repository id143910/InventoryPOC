import { Routes } from '@angular/router';

/**
 * Three surfaces, because querying, reading and keeping are different jobs.
 *
 *   /                    the queries you saved, each as a box
 *   /query               the query builder and its table
 *   /entity/:type/:key   one entity, to read and to browse from
 *
 * They meet at links only: a saved box opens the builder, an entity named in a
 * query links to its page, and its page carries links back into the builder. With
 * nothing saved the dashboard steps aside, so the builder is still where you land.
 */
export const routes: Routes = [
  {
    path: '',
    loadComponent: () => import('./dashboard/dashboard').then((m) => m.DashboardComponent),
    title: 'Inventory graph',
  },
  {
    path: 'query',
    loadComponent: () => import('./query/query-page').then((m) => m.QueryPageComponent),
    title: 'Inventory graph',
  },
  {
    path: 'entity/:type/:key',
    loadComponent: () => import('./entity/entity-page').then((m) => m.EntityPageComponent),
  },
  { path: '**', redirectTo: '' },
];
