import { Component, OnInit, signal, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatListModule } from '@angular/material/list';
import { MatChipsModule } from '@angular/material/chips';
import { MatDividerModule } from '@angular/material/divider';
import { MatTabsModule } from '@angular/material/tabs';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { JsonPipe } from '@angular/common';
import { Clipboard } from '@angular/cdk/clipboard';
import { ApiService, PlaygroundDocument, AiResult } from '../../core/services/api';

@Component({
  selector: 'app-playground',
  imports: [
    CommonModule, FormsModule, JsonPipe,
    MatCardModule, MatFormFieldModule, MatInputModule,
    MatButtonModule, MatIconModule, MatProgressSpinnerModule,
    MatListModule, MatChipsModule, MatDividerModule,
    MatTabsModule, MatExpansionModule, MatSnackBarModule, MatTooltipModule
  ],
  templateUrl: './playground.html',
  styleUrl: './playground.scss'
})
export class PlaygroundComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly snack = inject(MatSnackBar);
  private readonly clipboard = inject(Clipboard);

  // Dokument-Auswahl
  searchQuery = signal('');
  documents = signal<PlaygroundDocument[]>([]);
  selectedDoc = signal<PlaygroundDocument | null>(null);
  loadingDocs = signal(false);

  // Prompts
  systemPrompt = signal('');
  userPromptTemplate = signal('');

  // Vorschau
  previewing = signal(false);
  preview = signal<{ systemPrompt: string; userPrompt: string; charCount: { system: number; user: number; total: number } } | null>(null);

  // Ausführung
  running = signal(false);
  result = signal<AiResult | null>(null);
  applying = signal(false);
  savingPrompts = signal(false);

  ngOnInit() {
    this.api.getSettings().subscribe(s => {
      this.systemPrompt.set(s['AI:SystemPrompt'] ?? '');
      this.userPromptTemplate.set(s['AI:UserPromptTemplate'] ?? '');
    });
    this.searchDocuments();
  }

  searchDocuments() {
    this.loadingDocs.set(true);
    const q = this.searchQuery();
    this.api.playgroundDocuments(q || undefined).subscribe({
      next: docs => { this.documents.set(docs); this.loadingDocs.set(false); },
      error: () => this.loadingDocs.set(false)
    });
  }

  selectDoc(doc: PlaygroundDocument) {
    this.selectedDoc.set(doc);
    this.result.set(null);
    this.preview.set(null);
  }

  showPreview() {
    const doc = this.selectedDoc();
    if (!doc) return;
    this.previewing.set(true);
    this.api.playgroundPreview(doc.id, this.systemPrompt(), this.userPromptTemplate()).subscribe({
      next: p => { this.preview.set(p); this.previewing.set(false); },
      error: () => { this.previewing.set(false); this.snack.open('Vorschau fehlgeschlagen', 'OK', { duration: 3000 }); }
    });
  }

  run() {
    const doc = this.selectedDoc();
    if (!doc) return;
    this.running.set(true);
    this.result.set(null);
    this.preview.set(null);
    this.api.playgroundRun(doc.id, this.systemPrompt(), this.userPromptTemplate()).subscribe({
      next: res => { this.result.set(res); this.running.set(false); },
      error: err => {
        this.running.set(false);
        this.snack.open('Fehler: ' + (err.error?.title ?? err.message), 'OK', { duration: 5000 });
      }
    });
  }

  apply() {
    const doc = this.selectedDoc();
    const res = this.result();
    if (!doc || !res) return;
    this.applying.set(true);
    this.api.playgroundApply(doc.id, res).subscribe({
      next: () => {
        this.applying.set(false);
        this.snack.open('Metadaten erfolgreich auf Paperless angewendet', 'OK', { duration: 4000 });
      },
      error: () => {
        this.applying.set(false);
        this.snack.open('Fehler beim Anwenden', 'OK', { duration: 4000 });
      }
    });
  }

  savePrompts() {
    this.savingPrompts.set(true);
    this.api.playgroundSavePrompts(this.systemPrompt(), this.userPromptTemplate()).subscribe({
      next: () => {
        this.savingPrompts.set(false);
        this.snack.open('Prompts gespeichert', 'OK', { duration: 3000 });
      },
      error: () => {
        this.savingPrompts.set(false);
        this.snack.open('Fehler beim Speichern', 'OK', { duration: 3000 });
      }
    });
  }

  resetPrompts() {
    this.api.getSettings().subscribe(s => {
      this.systemPrompt.set(s['AI:SystemPrompt'] ?? '');
      this.userPromptTemplate.set(s['AI:UserPromptTemplate'] ?? '');
    });
  }

  tryParseJson(json: string): unknown[] | null {
    try { const r = JSON.parse(json); return Array.isArray(r) ? r : null; }
    catch { return null; }
  }

  formatValue(v: unknown): string {
    return v === null || v === undefined ? '—' : String(v);
  }

  copy(text: string) { this.clipboard.copy(text); }
}
