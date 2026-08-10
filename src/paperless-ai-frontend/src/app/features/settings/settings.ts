import { Component, OnInit, signal, inject, computed } from '@angular/core';
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
import { MatChipsModule } from '@angular/material/chips';
import { DragDropModule, CdkDragDrop, moveItemInArray } from '@angular/cdk/drag-drop';
import { ApiService, SYSTEM_PROMPT_KEY, USER_PROMPT_KEY, AiConnection, AiProviderConfig } from '../../core/services/api';

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
    { key: 'Paperless:ErrorTagName', label: 'Tag-Name für Fehler', hint: 'Standard: paperless-ai-error – wird bei fehlgeschlagenen Jobs gesetzt' },
    { key: 'Paperless:ReviewTagName', label: 'Tag-Name für Review', hint: 'Standard: paperless-ai-review – wird nach erfolgreichem KI-Job gesetzt' },
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

const PROCESSING_TOGGLE_FIELDS: SettingField[] = [
  {
    key: 'Processing:OcrEnabled',
    label: 'OCR-Verarbeitung aktiv',
    hint: 'Dokumente mit OCR-Tag werden durch Azure Document Intelligence verarbeitet'
  },
  {
    key: 'Processing:AiEnabled',
    label: 'KI-Verarbeitung aktiv',
    hint: 'Dokumente mit KI-Tag werden durch Azure OpenAI analysiert und erhalten danach den Review-Tag'
  }
];

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
    MatDividerModule,
    MatChipsModule,
    DragDropModule
  ],
  templateUrl: './settings.html',
  styleUrl: './settings.scss'
})
export class SettingsComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly snack = inject(MatSnackBar);

  sections = CONNECTION_FIELDS.map((fields, i) => ({ title: SECTION_TITLES[i], fields }));
  processingToggleFields = PROCESSING_TOGGLE_FIELDS;
  toggleFields = TOGGLE_FIELDS;
  values = signal<Record<string, string>>({});
  saving = signal(false);
  systemPromptKey = SYSTEM_PROMPT_KEY;
  userPromptKey = USER_PROMPT_KEY;

  // Fallback Provider
  connections = signal<AiConnection[]>([]);
  providerConfigs = signal<AiProviderConfig[]>([]);

  openAiConnections = computed(() => this.connections().filter(c =>
    this.providerConfigs().some(p => p.serviceType === 'OpenAI' && p.connectionId === c.id) ||
    !this.providerConfigs().some(p => p.connectionId === c.id)
  ));
  docIntelConnections = computed(() => this.connections().filter(c =>
    this.providerConfigs().some(p => p.serviceType === 'DocumentIntelligence' && p.connectionId === c.id) ||
    !this.providerConfigs().some(p => p.connectionId === c.id)
  ));

  openAiConfigs = computed(() =>
    [...this.providerConfigs().filter(c => c.serviceType === 'OpenAI')].sort((a, b) => a.priority - b.priority));
  docIntelConfigs = computed(() =>
    [...this.providerConfigs().filter(c => c.serviceType === 'DocumentIntelligence')].sort((a, b) => a.priority - b.priority));

  newOpenAiConn = signal({ name: '', endpoint: '', apiKey: '' });
  newDocIntelConn = signal({ name: '', endpoint: '', apiKey: '' });
  editingConnId = signal<number | null>(null);
  editConn = signal({ name: '', endpoint: '', apiKey: '' });

  newOpenAiConfig = signal({ connectionId: 0, deploymentName: '' });
  newDocIntelConfig = signal({ connectionId: 0, outputFormat: 'text', analysisModel: 'auto' });

  editingConfigId = signal<number | null>(null);
  editingConfig = signal<{ connectionId: number; deploymentName: string; outputFormat: string; analysisModel: string; isEnabled: boolean } | null>(null);

  readonly outputFormatOptions = [
    { value: 'text', label: 'Text' },
    { value: 'markdown', label: 'Markdown' }
  ];
  readonly analysisModelOptions = [
    { value: 'auto', label: 'Automatisch' },
    { value: 'prebuilt-read', label: 'prebuilt-read' },
    { value: 'prebuilt-layout', label: 'prebuilt-layout' }
  ];

  ngOnInit() {
    this.api.getSettings().subscribe(v => this.values.set({ ...v }));
    this.loadProviders();
  }

  loadProviders() {
    this.api.getConnections().subscribe(c => this.connections.set(c));
    this.api.getProviderConfigs().subscribe(c => this.providerConfigs.set(c));
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

  // ── Connection Management ────────────────────────────────────────────────

  addConnection(type: 'openai' | 'docintel') {
    const c = type === 'openai' ? this.newOpenAiConn() : this.newDocIntelConn();
    if (!c.name || !c.endpoint) return;
    this.api.createConnection(c.name, c.endpoint, c.apiKey).subscribe(() => {
      type === 'openai'
        ? this.newOpenAiConn.set({ name: '', endpoint: '', apiKey: '' })
        : this.newDocIntelConn.set({ name: '', endpoint: '', apiKey: '' });
      this.loadProviders();
    });
  }

  startEditConn(conn: AiConnection) {
    this.editingConnId.set(conn.id);
    this.editConn.set({ name: conn.name, endpoint: conn.endpoint, apiKey: '' });
  }

  saveEditConn() {
    const id = this.editingConnId();
    if (id === null) return;
    const c = this.editConn();
    this.api.updateConnection(id, c.name, c.endpoint, c.apiKey || undefined).subscribe(() => {
      this.editingConnId.set(null);
      this.loadProviders();
    });
  }

  deleteConnection(id: number) {
    this.api.deleteConnection(id).subscribe(() => this.loadProviders());
  }

  // ── Priority Config Management ───────────────────────────────────────────

  addOpenAiConfig() {
    const c = this.newOpenAiConfig();
    if (!c.connectionId) return;
    this.api.createProviderConfig({
      serviceType: 'OpenAI',
      connectionId: c.connectionId,
      deploymentName: c.deploymentName || null,
      isEnabled: true
    }).subscribe(() => {
      this.newOpenAiConfig.set({ connectionId: 0, deploymentName: '' });
      this.loadProviders();
    });
  }

  addDocIntelConfig() {
    const c = this.newDocIntelConfig();
    if (!c.connectionId) return;
    this.api.createProviderConfig({
      serviceType: 'DocumentIntelligence',
      connectionId: c.connectionId,
      outputFormat: c.outputFormat,
      analysisModel: c.analysisModel,
      isEnabled: true
    }).subscribe(() => {
      this.newDocIntelConfig.set({ connectionId: 0, outputFormat: 'text', analysisModel: 'auto' });
      this.loadProviders();
    });
  }

  startEditConfig(cfg: AiProviderConfig) {
    this.editingConfigId.set(cfg.id);
    this.editingConfig.set({
      connectionId: cfg.connectionId,
      deploymentName: cfg.deploymentName ?? '',
      outputFormat: cfg.outputFormat ?? 'text',
      analysisModel: cfg.analysisModel ?? 'auto',
      isEnabled: cfg.isEnabled
    });
  }

  saveConfig() {
    const id = this.editingConfigId();
    const ec = this.editingConfig();
    if (id === null || !ec) return;
    this.api.updateProviderConfig(id, {
      connectionId: ec.connectionId,
      deploymentName: ec.deploymentName || null,
      outputFormat: ec.outputFormat,
      analysisModel: ec.analysisModel,
      isEnabled: ec.isEnabled
    }).subscribe(() => {
      this.editingConfigId.set(null);
      this.editingConfig.set(null);
      this.loadProviders();
    });
  }

  cancelEditConfig() {
    this.editingConfigId.set(null);
    this.editingConfig.set(null);
  }

  toggleConfig(config: AiProviderConfig) {
    this.api.updateProviderConfig(config.id, {
      connectionId: config.connectionId,
      deploymentName: config.deploymentName,
      outputFormat: config.outputFormat,
      analysisModel: config.analysisModel,
      isEnabled: !config.isEnabled
    }).subscribe(() => this.loadProviders());
  }

  deleteConfig(id: number) {
    this.api.deleteProviderConfig(id).subscribe(() => this.loadProviders());
  }

  dropOpenAi(event: CdkDragDrop<AiProviderConfig[]>) {
    const list = [...this.openAiConfigs()];
    moveItemInArray(list, event.previousIndex, event.currentIndex);
    this.providerConfigs.update(all => [
      ...all.filter(c => c.serviceType !== 'OpenAI'),
      ...list.map((c, i) => ({ ...c, priority: i + 1 }))
    ]);
    this.api.reorderProviderConfigs('OpenAI', list.map(c => c.id)).subscribe();
  }

  dropDocIntel(event: CdkDragDrop<AiProviderConfig[]>) {
    const list = [...this.docIntelConfigs()];
    moveItemInArray(list, event.previousIndex, event.currentIndex);
    this.providerConfigs.update(all => [
      ...all.filter(c => c.serviceType !== 'DocumentIntelligence'),
      ...list.map((c, i) => ({ ...c, priority: i + 1 }))
    ]);
    this.api.reorderProviderConfigs('DocumentIntelligence', list.map(c => c.id)).subscribe();
  }

  resetPrompt(key: string) {
    this.api.resetPrompt(key).subscribe(res => {
      this.values.update(v => ({ ...v, [key]: res.value }));
      this.snack.open('Prompt auf Standard zurückgesetzt', 'OK', { duration: 3000 });
    });
  }
}
