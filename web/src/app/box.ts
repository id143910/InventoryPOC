import { DecimalPipe } from '@angular/common';
import { Component, input } from '@angular/core';
import { MatCardModule } from '@angular/material/card';
import { RouterLink } from '@angular/router';
import { Params } from '@angular/router';

/** One entity worth naming inside a box. */
export interface Named {
  type: string;
  natural_key: string;
  occurrences?: number;
}

/**
 * A count you can read and a way through to the rows behind it.
 *
 * "Installed on 12 servers" is the sentence; the twelve rows live in the query
 * builder, which the heading links to. Where there are only a few, they are named
 * here and you never need to go - and where there are many, a few are named and
 * the rest are "and 40 others".
 *
 * Both surfaces that show counts use this: the neighbourhood of one entity, and a
 * saved query on the dashboard. They are the same idea seen from two sides, so
 * they are the same box.
 */
@Component({
  selector: 'app-box',
  imports: [DecimalPipe, RouterLink, MatCardModule],
  templateUrl: './box.html',
})
export class BoxComponent {
  readonly heading = input.required<string>();
  readonly note = input('');
  readonly to = input.required<unknown[]>();
  readonly params = input<Params>({});
  readonly named = input<Named[]>([]);
  /** How many are not named here. Nothing is said when none are missing. */
  readonly others = input(0);
}
