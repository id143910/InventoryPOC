import { DecimalPipe } from '@angular/common';
import { Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { MatInputModule } from '@angular/material/input';
import { RouterLink } from '@angular/router';
import {
  ALL, ANY, Api, Condition, Field, Query, Stage, TEXT, blankCondition, entryFor, hintFor,
} from '../api';

/** One condition's row: what it asks, and how the field it asks of reads. */
interface ConditionRow {
  condition: Condition;
  /** How this condition's field reads, which is what decides the rest. */
  type: string;
  values: { value: string; count: number }[];
  truncated: boolean;
}

/** One stage's row: where it goes, and what narrows it. */
interface StageRow {
  index: number;
  standing: string;
  hops: { hop: string; reads: string; total: number; active: number }[];
  fields: Field[];
  conditions: ConditionRow[];
}

/** A condition row as the template needs it: what may be asked, and how to ask it. */
interface ConditionView extends ConditionRow {
  operators: string[];
  /** An input type where a threshold or a fragment is wanted, empty for a picker. */
  entry: string;
  hint: string;
}

interface StageView extends StageRow {
  conditions: ConditionView[];
}

/**
 * The query builder. One control, repeated.
 *
 * A stage is where a set of rows came from and what narrows it, and the only thing
 * special about the first is that it has no hop - it is the type you start from, or
 * the entity you are standing on. Blank rows are how you add: the trailing hop and
 * the trailing condition do nothing until filled in, and clearing a field removes
 * that condition.
 *
 * Every picker is read out of the data: the hops a type or an entity actually has,
 * the fields the config declares, and the values a field actually takes at that
 * point in the chain.
 */
@Component({
  selector: 'app-builder',
  imports: [
    DecimalPipe, FormsModule, RouterLink,
    MatFormFieldModule, MatInputModule, MatSelectModule,
  ],
  templateUrl: './builder.html',
})
export class BuilderComponent {
  private readonly api = inject(Api);

  readonly query = input.required<Query>();
  readonly types = input<{ type: string; label: string; count: number }[]>([]);
  readonly stagesChange = output<Stage[]>();
  readonly typeChange = output<string>();
  readonly dismiss = output<void>();

  readonly maxStages = 4;
  readonly maxConditions = 3;
  readonly matches = [ALL, ANY];
  readonly rows = signal<StageRow[]>([]);

  /** What may be asked of each kind of field; the API publishes it. */
  readonly operators = input<Record<string, string[]>>({});

  /**
   * The rows, with what each field's type allows read off the schema.
   *
   * Derived rather than stored, because the rows and the schema arrive in two
   * responses whose order is not ours to choose. Folding the schema in as each stage
   * answered meant that when the schema was the slower of the two, the operator
   * picker was built from nothing and stayed empty - so before and after were real
   * on the wire and unreachable in the interface.
   */
  readonly view = computed<StageView[]>(() => this.rows().map((row) => ({
    ...row,
    conditions: row.conditions.map((item) => ({
      ...item,
      operators: this.operators()[item.type] ?? [],
      entry: entryFor(item.condition.operator, item.type),
      hint: hintFor(item.type),
    })),
  })));

  readonly pinned = computed(() => this.query().key);

  constructor() {
    // The builder is a view of the query; when the query changes it asks the API
    // what each stage can now offer, one stage at a time.
    effect(() => {
      const query = this.query();
      if (query.type) {
        this.describe(query);
      }
    });
  }

  hopOf(index: number): string {
    return this.query().stages[index]?.hop ?? '';
  }

  matchOf(index: number): string {
    return this.query().stages[index]?.match ?? ALL;
  }

  /** The connective only means something once two conditions are set. */
  showsMatch(row: StageView): boolean {
    return row.conditions.filter((item) => item.condition.field).length > 1;
  }

  /** How one field reads, from the fields this stage offers. */
  private typeOf(fields: Field[], name: string): string {
    return fields.find((field) => field.value === name)?.type ?? TEXT;
  }

  grouped(fields: Field[]): { group: string; fields: Field[] }[] {
    const groups = new Map<string, Field[]>();
    for (const field of fields) {
      groups.set(field.group, [...(groups.get(field.group) ?? []), field]);
    }
    return [...groups].map(([group, items]) => ({ group, fields: items }));
  }

  onHop(index: number, hop: string): void {
    const stages = this.stages();
    if (!hop) {
      // Clearing a hop ends the chain rather than leaving a hole in it.
      this.stagesChange.emit(stages.slice(0, index));
      return;
    }
    const kept: Stage = stages[index]
      ? { ...stages[index], hop }
      : { hop, match: ALL, conditions: [blankCondition()] };
    this.stagesChange.emit([...stages.slice(0, index), kept]);
  }

  onMatch(index: number, match: string): void {
    this.replace(index, (stage) => ({ ...stage, match }));
  }

  onField(index: number, slot: number, field: string): void {
    this.replace(index, (stage) => {
      const conditions = [...stage.conditions];
      // A field cleared is a condition removed; a field set keeps neither its value
      // nor its operator, because the new field may not be able to answer it. It
      // starts on `is` where it can, and on the first thing it can be asked where not.
      const allowed = this.operators()[this.typeOf(this.rows()[index]?.fields ?? [], field)] ?? [];
      const blank = blankCondition();
      const operator = allowed.length && !allowed.includes(blank.operator) ? allowed[0] : blank.operator;
      conditions[slot] = { ...blank, field, operator };
      return { ...stage, conditions: conditions.filter((item, at) => item.field || at === slot) };
    });
  }

  onOperator(index: number, slot: number, operator: string): void {
    // A different operator may want a different kind of value, so the old one goes.
    this.replace(index, (stage) => {
      const conditions = [...stage.conditions];
      conditions[slot] = { ...conditions[slot], operator, value: '' };
      return { ...stage, conditions };
    });
  }

  onValue(index: number, slot: number, value: string): void {
    this.replace(index, (stage) => {
      const conditions = [...stage.conditions];
      conditions[slot] = { ...conditions[slot], value };
      return { ...stage, conditions };
    });
  }

  private stages(): Stage[] {
    return this.query().stages;
  }

  private replace(index: number, change: (stage: Stage) => Stage): void {
    const stages = this.stages();
    const at = stages[index] ?? { hop: null, match: ALL, conditions: [] };
    this.stagesChange.emit(stages.map((stage, position) => (position === index ? change(at) : stage)));
  }

  /**
   * What each stage can offer, plus one blank stage to go further and one blank
   * condition in each to add another.
   */
  private describe(query: Query): void {
    const stages = query.stages.length ? query.stages : [{ hop: null, match: ALL, conditions: [] }];
    const types = [query.type, ...stages.slice(1).map((stage) => stage.hop!.split(':')[2])];
    const rows: StageRow[] = [];

    for (let index = 0; index < stages.length + (stages.length <= this.maxStages ? 1 : 0); index++) {
      const stage = stages[index];
      const standing = types[index - 1] ?? query.type;
      const conditions = [...(stage?.conditions.filter((item) => item.field) ?? [])];
      if (conditions.length < this.maxConditions) {
        conditions.push(blankCondition());
      }
      rows[index] = {
        index,
        standing: index === 0 ? query.type : standing,
        hops: [],
        fields: [],
        conditions: conditions.map((condition) => ({
          condition, type: TEXT, values: [], truncated: false,
        })),
      };

      // Stage 0 stands on the start type; every later stage stands where the one
      // before it landed.
      const where = index === 0 ? query.type : types[index - 1];
      const key = index === 1 && query.key ? query.key : null;
      this.api.stage(where, index === 0 ? null : key, stage?.hop ?? null).subscribe((options) => {
        // A field's type is the one thing a stage's own answer settles; what that
        // type allows to be asked is the schema's business, and `view` folds the two.
        const conditions = rows[index].conditions.map((row) => ({
          ...row, type: this.typeOf(options.fields, row.condition.field),
        }));
        rows[index] = { ...rows[index], hops: options.hops, fields: options.fields, conditions };
        this.rows.set([...rows]);

        // Only a picker needs the values; a threshold or a fragment is typed in.
        conditions.forEach((row, slot) => {
          if (!row.condition.field || entryFor(row.condition.operator, row.type)) return;
          this.api.values(query, index, slot).subscribe((found) => {
            const current = rows[index].conditions[slot];
            rows[index].conditions[slot] = { ...current, values: found.data, truncated: found.truncated };
            this.rows.set([...rows]);
          });
        });
      });
    }
    this.rows.set([...rows]);
  }
}
