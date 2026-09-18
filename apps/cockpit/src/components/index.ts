// The shared Cockpit user-interface kit (issue #1244). Import components from "../components" so a page
// never reaches for a one-off button class again. The kit's stylesheet (components.css) is imported once
// in main.tsx alongside the app theme, so these components style themselves from the shared navy
// palette recorded in docs/CockpitVisualStyle.md.

export { Button } from "./Button";
export type { ButtonProps, ButtonVariant } from "./Button";

export { PageHeader } from "./PageHeader";
export type { PageHeaderProps } from "./PageHeader";

export { LoadingState } from "./LoadingState";
export type { LoadingStateProps } from "./LoadingState";

export { EmptyState } from "./EmptyState";
export type { EmptyStateProps } from "./EmptyState";

export { ErrorBanner } from "./ErrorBanner";
export type { ErrorBannerProps } from "./ErrorBanner";

export { ConfirmDialog } from "./ConfirmDialog";
export type { ConfirmDialogProps } from "./ConfirmDialog";

export { FileViewerModal } from "./FileViewerModal";
export type { FileViewerModalProps } from "./FileViewerModal";

export { StatusMessage } from "./StatusMessage";
export type { StatusMessageProps } from "./StatusMessage";

export { DataTable } from "./DataTable";
export type { DataTableColumn, DataTableProps } from "./DataTable";

export {
  matchesQuery,
  compareSortValues,
  filterAndSortRows,
  nextSort,
} from "./dataTableCore";
export type { SortDirection, SortState } from "./dataTableCore";

export { useDismissOnBackdrop } from "./useDismissOnBackdrop";
export type { BackdropDismissHandlers } from "./useDismissOnBackdrop";

export { useFlash } from "./useFlash";
export type { Flash, FlashController } from "./useFlash";

export { NavIcon } from "./NavIcon";
export type { NavIconName, NavIconProps } from "./NavIcon";

export { Chevron } from "./Chevron";
export type { ChevronProps } from "./Chevron";
