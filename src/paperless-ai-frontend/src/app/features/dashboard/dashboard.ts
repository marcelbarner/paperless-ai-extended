import { Component, OnInit, OnDestroy, signal, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatCardModule } from '@angular/material/card';
import { MatTableModule } from '@angular/material/table';
import { MatChipsModule } from '@angular/material/chips';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatSelectModule } from '@angular/material/select';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatDialogModule, MatDialog } from '@angular/material/dialog';
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
    MatDialogModule
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
  paperlessUrl = signal('http://localhost:8000');

  displayedColumns = ['documentTitle', 'jobType', 'status', 'updatedAt', 'actions'];

  ngOnInit() {
    this.api.getSettings().subscribe(s => {
      const url = s['Paperless:BaseUrl'];
      if (url) this.paperlessUrl.set(url);
    });
    this.load();
    this.refreshTimer = setInterval(() => this.load(), 15000);
  }

  ngOnDestroy() {
    if (this.refreshTimer) clearInterval(this.refreshTimer);
  }

  load() {
    const filter = this.statusFilter() || undefined;
    this.api.getJobs(filter, this.page(), this.pageSize()).subscribe(p => {
      this.jobs.set(p.jobs);
      this.total.set(p.total);
      this.loading.set(false);
    });
    this.api.getJobStats().subscribe(stats => this.stats.set(stats));
  }

  onFilterChange(status: string) {
    this.statusFilter.set(status);
    this.page.set(1);
    this.load();
  }

  onPage(event: PageEvent) {
    this.page.set(event.pageIndex + 1);
    this.pageSize.set(event.pageSize);
    this.load();
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
    return ({ Pending: 'accent', Processing: 'primary', Done: '', Failed: 'warn' } as Record<string, string>)[status] ?? '';
  }
}
