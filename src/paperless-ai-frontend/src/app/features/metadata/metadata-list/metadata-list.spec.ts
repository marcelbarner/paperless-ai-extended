import { ComponentFixture, TestBed } from '@angular/core/testing';

import { MetadataList } from './metadata-list';

describe('MetadataList', () => {
  let component: MetadataList;
  let fixture: ComponentFixture<MetadataList>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [MetadataList],
    }).compileComponents();

    fixture = TestBed.createComponent(MetadataList);
    component = fixture.componentInstance;
    await fixture.whenStable();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });
});
