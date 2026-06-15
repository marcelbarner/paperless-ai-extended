import { Component, OnInit, signal, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTabsModule } from '@angular/material/tabs';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatDividerModule } from '@angular/material/divider';
import { ApiService, SYSTEM_PROMPT_KEY, USER_PROMPT_KEY } from '../../core/services/api';

interface SettingOption { value: string; label: string; }

interface SettingField {
  key: string;
  label: string;
  hint?: string;
  type?: 'text' | 'password' | 'select' | 'toggle';
  options?: SettingOption[];
}

const CONNECTION_FIELDS: SettingField[][] = [
  [
    { key: 'Paperless:BaseUrl', label: 'Paperless URL', hint: 'z.B. http://192.168.1.100:8000' },
    { key: 'Paperless:Token', label: 'Paperless API Token', type: 'password' },
    { key: 'Paperless:OcrTagName', label: 'Tag-Name für OCR', hint: 'Standard: paperless-ai-ocr' },
    { key: 'Paperless:AiTagName', label: 'Tag-Name für KI-Verarbeitung', hint: 'Standard: paperless-ai-process' },
    { key: 'Polling:IntervalSeconds', label: 'Polling-Intervall (Sekunden)', hint: 'Standard: 30' }
  ],
  [
    { key: 'Azure:DocumentIntelligence:Endpoint', label: 'Document Intelligence Endpoint' },
    { key: 'Azure:DocumentIntelligence:Key', label: 'Document Intelligence Key', type: 'password' },
    {
      key: 'Azure:DocumentIntelligence:OutputFormat',
      label: 'OCR-Ausgabeformat',
      type: 'select',
      options: [
        { value: 'text', label: 'Text – Fließtext, kein Struktur-Markup' },
        { value: 'markdown', label: 'Markdown – Tabellen, Überschriften, Listen (empfohlen)' }
      ]
    },
    {
      key: 'Azure:DocumentIntelligence:Model',
      label: 'Analyse-Modell',
      type: 'select',
      options: [
        { value: 'auto', label: 'Automatisch (read für Text, layout für Markdown)' },
        { value: 'prebuilt-read', label: 'prebuilt-read – schnell, nur Fließtext' },
        { value: 'prebuilt-layout', label: 'prebuilt-layout – Tabellen, Struktur, langsamer' }
      ]
    }
  ],
  [
    { key: 'Azure:OpenAI:Endpoint', label: 'Azure OpenAI Endpoint' },
    { key: 'Azure:OpenAI:Key', label: 'Azure OpenAI Key', type: 'password' },
    { key: 'Azure:OpenAI:DeploymentName', label: 'Deployment Name', hint: 'z.B. gpt-4o' }
  ]
];

const SECTION_TITLES = ['Paperless-NGX', 'Azure Document Intelligence (OCR)', 'Azure OpenAI'];

const TOGGLE_FIELDS: SettingField[] = [
  {
    key: 'AI:CanCreate:Correspondent',
    label: 'Korrespondenten anlegen',
    hint: 'KI darf neue Korrespondenten in Paperless erstellen'
  },
  {
    key: 'AI:CanCreate:DocumentType',
    label: 'Dokumenttypen anlegen',
    hint: 'KI darf neue Dokumenttypen in Paperless erstellen'
  },
  {
    key: 'AI:CanCreate:Tag',
    label: 'Tags anlegen',
    hint: 'KI darf neue Tags in Paperless erstellen'
  },
  {
    key: 'AI:CanCreate:StoragePath',
    label: 'Speicherpfade anlegen',
    hint: 'KI darf neue Speicherpfade in Paperless erstellen'
  },
  {
    key: 'AI:CanCreate:CustomField',
    label: 'Custom Fields anlegen',
    hint: 'KI darf neue benutzerdefinierte Felder anlegen (Name + Datentyp + Wert)'
  }
];

@Component({
  selector: 'app-settings',
  imports: [
    FormsModule,
    MatCardModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatButtonModule,
    MatIconModule,
    MatTabsModule,
    MatSlideToggleModule,
    MatSnackBarModule,
    MatTooltipModule,
    MatDividerModule
  ],
  templateUrl: './settings.html',
  styleUrl: './settings.scss'
})
export class SettingsComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly snack = inject(MatSnackBar);

  sections = CONNECTION_FIELDS.map((fields, i) => ({ title: SECTION_TITLES[i], fields }));
  toggleFields = TOGGLE_FIELDS;
  values = signal<Record<string, string>>({});
  saving = signal(false);
  systemPromptKey = SYSTEM_PROMPT_KEY;
  userPromptKey = USER_PROMPT_KEY;

  ngOnInit() {
    this.api.getSettings().subscribe(v => this.values.set({ ...v }));
  }

  getValue(key: string): string {
    return this.values()[key] ?? '';
  }

  isTrue(key: string): boolean {
    return this.values()[key] === 'true';
  }

  setValue(key: string, value: string) {
    this.values.update(v => ({ ...v, [key]: value }));
  }

  setToggle(key: string, checked: boolean) {
    this.setValue(key, checked ? 'true' : 'false');
  }

  save() {
    this.saving.set(true);
    this.api.saveSettings(this.values()).subscribe({
      next: () => {
        this.saving.set(false);
        this.snack.open('Einstellungen gespeichert', 'OK', { duration: 3000 });
      },
      error: () => {
        this.saving.set(false);
        this.snack.open('Fehler beim Speichern', 'OK', { duration: 3000 });
      }
    });
  }

  resetPrompt(key: string) {
    this.api.resetPrompt(key).subscribe(res => {
      this.values.update(v => ({ ...v, [key]: res.value }));
      this.snack.open('Prompt auf Standard zurückgesetzt', 'OK', { duration: 3000 });
    });
  }
}
