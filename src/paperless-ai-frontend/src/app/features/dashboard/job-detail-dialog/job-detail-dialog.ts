import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatChipsModule } from '@angular/material/chips';
import { MatDividerModule } from '@angular/material/divider';
import { MatTabsModule } from '@angular/material/tabs';
import { Clipboard } from '@angular/cdk/clipboard';
import { MatButtonModule as MatBtn } from '@angular/material/button';
import { ProcessingJob } from '../../../core/services/api';

export interface JobDialogData {
  job: ProcessingJob;
  paperlessUrl: string;
}

interface OcrResult {
  charCount: number;
  preview: string;
  contentVerified: boolean;
  pdfSizeBytes: number;
}

interface AiResult {
  title: string | null;
  created: string | null;
  correspondentId: number | null;
  newCorrespondent: string | null;
  documentTypeId: number | null;
  newDocumentType: string | null;
  tagIds: number[];
  newTags: string[];
  storagePathId: number | null;
  newStoragePath: string | null;
  customFields: Record<string, unknown>;
  reasoning: string | null;
  sentSystemPrompt: string | null;
  sentUserPrompt: string | null;
}

@Component({
  selector: 'app-job-detail-dialog',
  imports: [
    CommonModule,
    MatDialogModule,
    MatButtonModule,
    MatIconModule,
    MatChipsModule,
    MatDividerModule,
    MatTabsModule
  ],
  templateUrl: './job-detail-dialog.html',
  styleUrl: './job-detail-dialog.scss'
})
export class JobDetailDialogComponent {
  private readonly data = inject<JobDialogData>(MAT_DIALOG_DATA);
  readonly dialogRef = inject(MatDialogRef<JobDetailDialogComponent>);
  private readonly clipboard = inject(Clipboard);

  get job(): ProcessingJob { return this.data.job; }
  get paperlessDocUrl(): string {
    return `${this.data.paperlessUrl}/documents/${this.job.documentId}/details/`;
  }

  previewExpanded = false;

  get parsed(): OcrResult | AiResult | null {
    if (!this.job.resultJson) return null;
    try { return JSON.parse(this.job.resultJson); }
    catch { return null; }
  }

  get ocrResult(): OcrResult | null {
    const p = this.parsed;
    return p && 'charCount' in p ? p as OcrResult : null;
  }

  get aiResult(): AiResult | null {
    const p = this.parsed;
    return p && 'reasoning' in p ? p as AiResult : null;
  }

  get customFieldEntries(): [string, unknown][] {
    return Object.entries(this.aiResult?.customFields ?? {});
  }

  get previewText(): string {
    const preview = this.ocrResult?.preview ?? '';
    return this.previewExpanded ? preview : preview.substring(0, 400) + (preview.length > 400 ? '…' : '');
  }

  get canExpand(): boolean {
    return (this.ocrResult?.preview?.length ?? 0) > 400;
  }

  statusColor(status: string): string {
    return ({ Pending: 'accent', Processing: 'primary', Done: '', Failed: 'warn' } as Record<string, string>)[status] ?? '';
  }

  formatBytes(bytes: number): string {
    return bytes > 1024 * 1024
      ? (bytes / 1024 / 1024).toFixed(1) + ' MB'
      : (bytes / 1024).toFixed(0) + ' KB';
  }

  formatValue(v: unknown): string {
    return v === null || v === undefined ? '—' : String(v);
  }

  copy(text: string | null | undefined) {
    if (text) this.clipboard.copy(text);
  }
}
