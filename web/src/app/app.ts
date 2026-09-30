import { Component, inject } from '@angular/core';
import { MatToolbarModule } from '@angular/material/toolbar';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { Saved } from './saved';
import { SearchComponent } from './search';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, MatToolbarModule, SearchComponent],
  templateUrl: './app.html',
})
export class App {
  /** The Saved tab is only there once something is saved, as the dashboard is. */
  readonly saved = inject(Saved);
}
