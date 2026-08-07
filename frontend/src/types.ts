// Statuses of the two-phase pipeline (Content Understanding -> Azure OpenAI mapping).
export type ProcessingStatus =
  | 'Queued'
  | 'Extracting'   // Phase 1 — Azure AI Content Understanding
  | 'Mapping'       // Phase 2 — Azure OpenAI structured transform
  | 'Done'
  | 'Failed';

// One row of WH_Midwest_Sales.dbo.Sales — the canonical schema from journal §5.
export interface AnalyticsTransaction {
  sourceName: string;
  manufacturer: string;
  customerId: string;
  customerName: string;
  date: string; // ISO date, e.g. "2026-06-14"
  city: string;
  state: string;
  productFamily: string;
  partNo: string;
  partDescription: string;
  quantity: number;
  netSales: number;
  commission: number;
}

// Summary row as returned by GET /api/documents (DocumentsController.GetAll).
export interface DocumentSummary {
  id: string;
  fileName: string;
  fileSizeBytes: number;
  uploadedAt: string; // ISO datetime
  manufacturer: string;
  periodMonth: string; // "01".."12"
  periodYear: string;  // "2026"
  status: ProcessingStatus;
  progressPct: number; // 0-100, drives the progress bar during Extracting/Mapping
  errorMessage: string | null;
  /** True when the pipeline knows data was lost or unverified — totals are not trustworthy. */
  hasWarnings: boolean;
  totalNetSales: number | null;   // null until Done
  totalCommission: number | null; // null until Done
  lineCount: number | null;
  customerCount: number | null;
  customInstructions: string | null;
  /** True for year-to-date reports, whose totals accumulate from January. */
  isCumulative: boolean;
  /** The period's own activity, derived by subtracting the previous month. Null if unavailable. */
  monthlyNetSales: number | null;
  monthlyCommission: number | null;
  monthlyLineCount: number | null;
}

// Progress-only row from GET /api/documents/status — what the polling loop fetches.
export interface DocumentStatus {
  id: string;
  status: ProcessingStatus;
  progressPct: number;
  errorMessage: string | null;
  hasWarnings: boolean;
}

// Full detail as returned by GET /api/documents/{id} (DocumentsController.GetById).
export interface DocumentDetail extends DocumentSummary {
  rawExtractionJson: string;
  canonicalRecords: AnalyticsTransaction[];
  sourceUrl: string;
}

export interface UploadRequest {
  manufacturer: string;
  periodMonth: string;
  periodYear: string;
  customInstructions: string;
}