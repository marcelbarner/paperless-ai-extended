import { Component, OnInit, signal, inject, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { MatCardModule } from '@angular/material/card';
import { MatTableModule } from '@angular/material/table';
import { MatInputModule } from '@angular/material/input';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { ApiService, MetadataItem, MetadataType } from '../../../core/services/api';

const META_LABELS: Record<string, string> = {
  correspondents: 'Korrespondenten',
  'document-types': 'Dokumenttypen',
  tags: 'Tags',
  'storage-paths': 'Speicherpfade',
  'custom-fields': 'Custom Fields'
};

@Component({
  selector: 'app-metadata-list',
  imports: [
    CommonModule,
    FormsModule,
    MatCardModule,
    MatTableModule,
    MatInputModule,
    MatFormFieldModule,
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule,
    MatSnackBarModule
  ],
  templateUrl: './metadata-list.html',
  styleUrl: './metadata-list.scss'
})
export class MetadataListComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly route = inject(ActivatedRoute);
  private readonly snack = inject(MatSnackBar);

  type = signal<MetadataType>('correspondents');
  label = computed(() => META_LABELS[this.type()] ?? this.type());
  items = signal<MetadataItem[]>([]);
  editingId = signal<number | null>(null);
  editDescription = signal('');
  loading = signal(true);
  syncing = signal(false);
  displayedColumns = ['name', 'description', 'actions'];

  ngOnInit() {
    this.route.params.subscribe(params => {
      this.type.set(params['type'] as MetadataType);
      this.load();
    });
  }

  load() {
    this.loading.set(true);
    this.api.getMetadata(this.type()).subscribe(items => {
      this.items.set(items);
      this.loading.set(false);
    });
  }

  sync() {
    this.syncing.set(true);
    this.api.syncMetadata(this.type()).subscribe(res => {
      this.syncing.set(false);
      this.snack.open(`${res.synced} Einträge synchronisiert`, 'OK', { duration: 3000 });
      this.load();
    });
  }

  startEdit(item: MetadataItem) {
    this.editingId.set(item.entityId);
    this.editDescription.set(item.description);
  }

  saveEdit(item: MetadataItem) {
    this.api.updateDescription(this.type(), item.entityId, item.name, this.editDescription()).subscribe(() => {
      item.description = this.editDescription();
      this.editingId.set(null);
      this.snack.open('Beschreibung gespeichert', 'OK', { duration: 2000 });
    });
  }

  cancelEdit() {
    this.editingId.set(null);
  }
}
