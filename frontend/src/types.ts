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
  /** The customer company this report belongs to (the signed-in user's own, unless they are an admin). */
  clientName: string;
}

// Progress-only row from GET /api/documents/status — what the polling loop fetches.
export interface DocumentStatus {
  id: string;
  status: ProcessingStatus;
  progressPct: number;
  errorMessage: string | null;
  hasWarnings: boolean;
}

// Header-level detail from GET /api/documents/{id}. The heavy content is fetched per tab, a page at
// a time (see CanonicalPage / ExtractedPage / SourcePage below).
export interface DocumentDetail extends DocumentSummary {
  sourceUrl: string | null;
}

export interface CanonicalPage {
  records: AnalyticsTransaction[];
  page: number;
  pageSize: number;
  totalRecords: number;
  /** Totals over the WHOLE document, not just this page. */
  totalNetSales: number;
  totalCommission: number;
}

export interface ExtractedPage {
  text: string;
  page: number;
  pageCount: number;
  totalChars: number;
}

export interface SourcePage {
  sheets: { name: string; rowCount: number }[];
  sheetIndex: number;
  headers: string[];
  rows: string[][];
  page: number;
  pageSize: number;
  totalRows: number;
}

// From GET /api/me.
export interface Me {
  email: string;
  displayName: string;
  clientId: string;
  clientName: string;
  isAdmin: boolean;
  authEnabled: boolean;
}

// From GET /api/admin/clients and /api/admin/users (admins only).
export interface ClientInfo {
  id: string;
  name: string;
  userCount: number;
  createdDate: string;
}

export interface AppUser {
  id: string;
  email: string;
  displayName: string | null;
  clientId: string;
  clientName: string;
  isAdmin: boolean;
  /** False until the person has signed in once. */
  hasSignedIn: boolean;
}

export interface UploadRequest {
  manufacturer: string;
  periodMonth: string;
  periodYear: string;
  customInstructions: string;
}

// From GET/POST/PUT /api/manufacturers (ManufacturersController) — the admin-managed replacement
// for the previously hardcoded MANUFACTURERS array.
export interface Manufacturer {
  id: string;
  name: string;
  /** Pre-fills the "Processing instructions" box whenever this manufacturer is picked for a new upload. */
  defaultInstructions: string | null;
  createdDate: string;
}

// From GET /api/manufacturers/{id}/prompt-history — past DefaultInstructions values, newest first.
export interface ManufacturerPromptHistoryEntry {
  id: string;
  instructions: string;
  createdDate: string;
}