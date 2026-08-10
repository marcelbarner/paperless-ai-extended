import { Component, OnInit, OnDestroy, signal, inject, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatCardModule } from '@angular/material/card';
import { MatTableModule } from '@angular/material/table';
import { MatChipsModule } from '@angular/material/chips';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatSortModule, Sort } from '@angular/material/sort';
import { MatSelectModule } from '@angular/material/select';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatDialogModule, MatDialog } from '@angular/material/dialog';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { ApiService, ProcessingJob, JobStat } from '../../core/services/api';
import { JobDetailDialogComponent } from './job-detail-dialog/job-detail-dialog';

@Component({
  selector: 'app-dashboard',
  imports: [
    CommonModule,
    MatCardModule,
    MatTableModule,
    MatChipsModule,
    MatButtonModule,
    MatIconModule,
    MatTooltipModule,
    MatProgressSpinnerModule,
    MatPaginatorModule,
    MatSelectModule,
    MatFormFieldModule,
    MatDialogModule,
    MatSlideToggleModule,
    MatCheckboxModule,
    MatSortModule
  ],
  templateUrl: './dashboard.html',
  styleUrl: './dashboard.scss'
})
export class DashboardComponent implements OnInit, OnDestroy {
  private readonly api = inject(ApiService);
  private readonly dialog = inject(MatDialog);
  private refreshTimer?: ReturnType<typeof setInterval>;

  jobs = signal<ProcessingJob[]>([]);
  stats = signal<JobStat[]>([]);
  loading = signal(true);
  total = signal(0);
  page = signal(1);
  pageSize = signal(20);
  statusFilter = signal<string>('');
  jobTypeFilter = signal<string[]>([]);
  sortBy = signal('updatedAt');
  sortDir = signal<'asc' | 'desc'>('desc');
  showAll = signal(false);
  selectedIds = signal<Set<number>>(new Set());
  paperlessUrl = signal('http://localhost:8000');
  pollingEnabled = signal(true);
  togglingPolling = signal(false);

  selectedFailedIds = computed(() => {
    const ids = this.selectedIds();
    return this.jobs()
      .filter(j => j.status === 'Failed' && ids.has(j.id))
      .map(j => j.id);
  });

  selectedCancellableIds = computed(() => {
    const ids = this.selectedIds();
    return this.jobs()
      .filter(j => (j.status === 'Pending' || j.status === 'Processing') && ids.has(j.id))
      .map(j => j.id);
  });

  selectedAnyIds = computed(() => {
    const ids = this.selectedIds();
    return this.jobs().filter(j => ids.has(j.id)).map(j => j.id);
  });

  allOnPageSelected = computed(() => {
    const ids = this.selectedIds();
    const visible = this.jobs();
    return visible.length > 0 && visible.every(j => ids.has(j.id));
  });

  displayedColumns = ['select', 'documentTitle', 'jobType', 'status', 'updatedAt', 'actions'];

  ngOnInit() {
    this.api.getSettings().subscribe(s => {
      const url = s['Paperless:BaseUrl'];
      if (url) this.paperlessUrl.set(url);
      this.pollingEnabled.set(s['Polling:Enabled'] !== 'false');
    });
    this.load();
    this.refreshTimer = setInterval(() => this.load(), 15000);
  }

  ngOnDestroy() {
    if (this.refreshTimer) clearInterval(this.refreshTimer);
  }

  load() {
    const filter = this.statusFilter() || undefined;
    const jobTypes = this.jobTypeFilter().length > 0 ? this.jobTypeFilter() : undefined;
    const [page, pageSize] = this.showAll() ? [undefined, undefined] : [this.page(), this.pageSize()];
    this.api.getJobs(filter, page, pageSize, jobTypes, this.sortBy(), this.sortDir()).subscribe(p => {
      this.jobs.set(p.jobs);
      this.total.set(p.total);
      this.loading.set(false);
      this.selectedIds.set(new Set());
    });
    this.api.getJobStats().subscribe(stats => this.stats.set(stats));
  }

  toggleShowAll() {
    this.showAll.update(v => !v);
    this.page.set(1);
    this.load();
  }

  onFilterChange(status: string) {
    this.statusFilter.set(status);
    this.page.set(1);
    this.load();
  }

  onSortChange(sort: Sort) {
    this.sortBy.set(sort.active || 'updatedAt');
    this.sortDir.set((sort.direction || 'desc') as 'asc' | 'desc');
    this.page.set(1);
    this.load();
  }

  onJobTypeFilterChange(types: string[]) {
    this.jobTypeFilter.set(types);
    this.page.set(1);
    this.load();
  }

  onPage(event: PageEvent) {
    this.page.set(event.pageIndex + 1);
    this.pageSize.set(event.pageSize);
    this.load();
  }

  toggleSelect(job: ProcessingJob, checked: boolean) {
    this.selectedIds.update(ids => {
      const next = new Set(ids);
      checked ? next.add(job.id) : next.delete(job.id);
      return next;
    });
  }

  isSelected(job: ProcessingJob): boolean {
    return this.selectedIds().has(job.id);
  }

  toggleSelectAll(checked: boolean) {
    this.selectedIds.update(ids => {
      const next = new Set(ids);
      this.jobs().forEach(j => checked ? next.add(j.id) : next.delete(j.id));
      return next;
    });
  }

  batchRetry() {
    const ids = this.selectedFailedIds();
    if (ids.length === 0) return;
    this.api.batchRetry(ids).subscribe(() => this.load());
  }

  batchCancel() {
    const ids = this.selectedCancellableIds();
    if (ids.length === 0) return;
    this.api.batchCancel(ids).subscribe(() => this.load());
  }

  batchDelete() {
    const ids = this.selectedAnyIds();
    if (ids.length === 0) return;
    this.api.batchDelete(ids).subscribe(() => this.load());
  }

  cancelAll() {
    const jobTypes = this.jobTypeFilter().length > 0 ? this.jobTypeFilter() : undefined;
    const status = this.statusFilter() ? [this.statusFilter()] : undefined;
    this.api.cancelAll(jobTypes, status).subscribe(() => this.load());
  }

  deleteAll() {
    const jobTypes = this.jobTypeFilter().length > 0 ? this.jobTypeFilter() : undefined;
    const status = this.statusFilter() ? [this.statusFilter()] : undefined;
    this.api.deleteAll(jobTypes, status).subscribe(() => this.load());
  }

  retryAll() {
    const jobTypes = this.jobTypeFilter().length > 0 ? this.jobTypeFilter() : undefined;
    this.api.retryAll(jobTypes).subscribe(() => this.load());
  }

  togglePolling(enabled: boolean) {
    this.togglingPolling.set(true);
    this.api.saveSettings({ 'Polling:Enabled': enabled ? 'true' : 'false' }).subscribe({
      next: () => {
        this.pollingEnabled.set(enabled);
        this.togglingPolling.set(false);
      },
      error: () => this.togglingPolling.set(false)
    });
  }

  retry(job: ProcessingJob) {
    this.api.retryJob(job.id).subscribe(() => this.load());
  }

  openDetail(job: ProcessingJob) {
    this.dialog.open(JobDetailDialogComponent, {
      data: { job, paperlessUrl: this.paperlessUrl() },
      width: '720px',
      maxHeight: '90vh'
    });
  }

  paperlessDocUrl(job: ProcessingJob): string {
    return `${this.paperlessUrl()}/documents/${job.documentId}/details/`;
  }

  statCount(status: string): number {
    return this.stats().find(s => s.status === status)?.count ?? 0;
  }

  statusColor(status: string): string {
    return ({ Pending: 'accent', Processing: 'primary', Done: '', Failed: 'warn', Cancelled: '' } as Record<string, string>)[status] ?? '';
  }
}
