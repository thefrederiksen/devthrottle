import { useMemo, useState } from "react";
import type { KeyboardEvent, ReactNode } from "react";
import {
  filterAndSortRows,
  nextSort,
  type SortDirection,
  type SortState,
} from "./dataTableCore";
import { useDismissOnBackdrop } from "./useDismissOnBackdrop";

// The shared Cockpit data table (issue #1245). Every list in the app used to be a bespoke <table>: no
// sort, no search, and - at its worst on the Schedule page - a whole multi-paragraph prompt crammed
// into one grid cell so the rows became a wall of prose you could not scan. This one component gives a
// list three capabilities the rest of the app then inherits: sortable columns, a search box, and an
// optional row-detail drawer for the long content a grid cell must never hold. A grid cell holds one
// short scannable value; the drawer holds the detail. The Schedule page is the first adopter; the
// Directors page (issue #1246) is next.
//
// It is generic over the row type T. A page describes its columns (how to render and, when sortable,
// how to derive a sort value), hands the table its rows and a function that flattens each row to the
// text the search box matches, and - if a row has detail worth reading - a renderer for the drawer.
// The table owns the search text, the sort state, and which row's drawer is open; the page owns the
// data and the meaning of every cell.

/** One column of the table. */
export interface DataTableColumn<T> {
  /** A stable key for this column (used for the sort state and React keys). */
  key: string;
  /** The column heading. */
  header: string;
  /** Renders this column's cell for a row. Keep it to one short scannable value - never a body of text. */
  render: (row: T) => ReactNode;
  /** When true, the header is clickable and sorts by `sortValue`. */
  sortable?: boolean;
  /** The value this row sorts by for this column. Return a number for times/counts so they order
   *  numerically rather than by their display text. Required to make a column meaningfully sortable. */
  sortValue?: (row: T) => string | number;
  /** Optional extra class on every cell in this column (for example "mono" or "dim"). */
  className?: string;
  /** Cell text alignment; defaults to left. */
  align?: "left" | "right";
  /** Optional hover title on the header cell (for example, to explain the column). */
  headerTitle?: string;
  /** Optional fixed column width (a CSS length, for example "180px"). The table uses a fixed layout,
   *  so widths keep the columns steady and let long cells truncate rather than reflow. */
  width?: string;
}

export interface DataTableProps<T> {
  /** The columns, left to right. */
  columns: DataTableColumn<T>[];
  /** The rows to show (already loaded by the page). */
  rows: T[];
  /** A stable unique key for a row (its id). Also identifies which row's drawer is open. */
  rowKey: (row: T) => string;
  /** Flattens a row to the single string the search box matches (name, machine, repository, prompt, ...). */
  searchableText: (row: T) => string;
  /** The search box placeholder. */
  searchPlaceholder?: string;
  /** The initial sort (column key + direction). Omit for the rows' natural order. */
  defaultSort?: SortState;
  /** Shown in place of the table when there are no rows at all. */
  emptyMessage?: ReactNode;
  /** Shown when there are rows but the search query matches none of them. */
  noMatchMessage?: ReactNode;
  /** Extra toolbar content pinned to the right of the search box (for example, a "New" button). */
  toolbarExtra?: ReactNode;
  /** Renders the drawer body for a row. When provided, activating a row opens the drawer. */
  renderDetail?: (row: T) => ReactNode;
  /** The drawer's title for a row (defaults to nothing). */
  detailTitle?: (row: T) => ReactNode;
  /** Optional actions pinned to the drawer header (for example, Run now / Edit). */
  detailActions?: (row: T) => ReactNode;
  /** Called when a row is activated (clicked, or Enter/Space with the row focused), before the drawer
   *  opens. A page uses this to load the row's detail data (for example, its run history). */
  onRowActivate?: (row: T) => void;
  /** Optional grouping. When given, the filtered and sorted rows are shown under one header row per group, each
   *  group keeping the table's sort inside it. Search still runs over every row first, so a group with no match
   *  disappears rather than showing an empty header. */
  grouping?: DataTableGrouping<T>;
}

/** How a DataTable groups its rows: which group a row belongs to, the order of the groups, and each header. */
export interface DataTableGrouping<T> {
  /** The key of the group a row belongs to. */
  groupOf: (row: T) => string;
  /** Orders two group keys (negative puts the first one first). */
  compareGroups: (a: string, b: string) => number;
  /** The header row's content for a group, given the group's visible rows. */
  renderHeader: (groupKey: string, rows: T[]) => ReactNode;
}

export function DataTable<T>({
  columns,
  rows,
  rowKey,
  searchableText,
  searchPlaceholder = "Search",
  defaultSort,
  emptyMessage,
  noMatchMessage,
  toolbarExtra,
  renderDetail,
  detailTitle,
  detailActions,
  onRowActivate,
  grouping,
}: DataTableProps<T>) {
  const [query, setQuery] = useState("");
  const [sort, setSort] = useState<SortState | null>(defaultSort ?? null);
  // The open drawer is tracked by row key, not by a row object, so it stays pinned to the same job as
  // polling replaces the row array. When the keyed row disappears (deleted), the drawer closes itself.
  const [detailKey, setDetailKey] = useState<string | null>(null);

  const sortValueFor = useMemo(() => {
    const byKey = new Map(columns.map((column) => [column.key, column] as const));
    return (row: T, columnKey: string): string | number | null => {
      const column = byKey.get(columnKey);
      if (column === undefined || column.sortValue === undefined) return null;
      return column.sortValue(row);
    };
  }, [columns]);

  const visibleRows = useMemo(
    () => filterAndSortRows(rows, query, searchableText, sort, sortValueFor),
    [rows, query, searchableText, sort, sortValueFor],
  );

  const detailRow = detailKey === null ? null : rows.find((row) => rowKey(row) === detailKey) ?? null;

  const activate = (row: T) => {
    if (onRowActivate !== undefined) onRowActivate(row);
    if (renderDetail !== undefined) setDetailKey(rowKey(row));
  };

  const onRowKeyDown = (event: KeyboardEvent<HTMLTableRowElement>, row: T) => {
    if (event.key === "Enter" || event.key === " ") {
      event.preventDefault();
      activate(row);
    }
  };

  const rowsAreActivatable = onRowActivate !== undefined || renderDetail !== undefined;

  // The detail drawer closes on a backdrop click, but only when the press STARTED on the backdrop -
  // selecting text in the drawer and releasing outside it is a selection, not a dismissal (see
  // useDismissOnBackdrop).
  const dismissDetail = useDismissOnBackdrop(() => setDetailKey(null));

  return (
    <div className="ui-table-wrap">
      <div className="ui-table-toolbar">
        <input
          className="ui-table-search"
          type="search"
          value={query}
          placeholder={searchPlaceholder}
          aria-label={searchPlaceholder}
          onChange={(event) => setQuery(event.target.value)}
        />
        {toolbarExtra !== undefined && <div className="ui-table-toolbar-extra">{toolbarExtra}</div>}
      </div>

      {rows.length === 0 ? (
        <div className="ui-table-empty">{emptyMessage}</div>
      ) : visibleRows.length === 0 ? (
        <div className="ui-table-empty">
          {noMatchMessage ?? `No rows match "${query.trim()}".`}
        </div>
      ) : (
        <table className="ui-table">
          <thead>
            <tr>
              {columns.map((column) => {
                const active = sort !== null && sort.columnKey === column.key;
                const indicator = !active ? "" : sort.direction === "asc" ? " ^" : " v";
                return (
                  <th
                    key={column.key}
                    className={headerClass(column, active)}
                    style={column.width !== undefined ? { width: column.width } : undefined}
                    title={column.headerTitle}
                    aria-sort={active ? ariaSort(sort.direction) : undefined}
                    onClick={
                      column.sortable === true && column.sortValue !== undefined
                        ? () => setSort((current) => nextSort(current, column.key))
                        : undefined
                    }
                  >
                    {column.header}
                    {active && <span className="ui-table-sort-mark">{indicator}</span>}
                  </th>
                );
              })}
            </tr>
          </thead>
          <tbody>
            {(grouping === undefined ? [{ groupKey: null, rows: visibleRows }] : groupRows(visibleRows, grouping)).map(
              (group) => [
                group.groupKey === null || grouping === undefined ? null : (
                  <tr key={`group:${group.groupKey}`} className="ui-table-group">
                    <td colSpan={columns.length}>{grouping.renderHeader(group.groupKey, group.rows)}</td>
                  </tr>
                ),
                ...group.rows.map((row) => {
              const key = rowKey(row);
              return (
                <tr
                  key={key}
                  className={`ui-table-row${rowsAreActivatable ? " activatable" : ""}${
                    key === detailKey ? " open" : ""
                  }`}
                  tabIndex={rowsAreActivatable ? 0 : undefined}
                  role={rowsAreActivatable ? "button" : undefined}
                  onClick={rowsAreActivatable ? () => activate(row) : undefined}
                  onKeyDown={rowsAreActivatable ? (event) => onRowKeyDown(event, row) : undefined}
                >
                  {columns.map((column) => (
                    <td
                      key={column.key}
                      className={cellClass(column)}
                      onClick={
                        // A cell may host its own controls (buttons, toggles); stop those clicks from
                        // also activating the row's drawer. Cells opt in via the "ui-table-cell-stop"
                        // class, marked through the column className.
                        column.className !== undefined && column.className.includes("ui-table-cell-stop")
                          ? (event) => event.stopPropagation()
                          : undefined
                      }
                    >
                      {column.render(row)}
                    </td>
                  ))}
                </tr>
              );
                }),
              ],
            )}
          </tbody>
        </table>
      )}

      {detailRow !== null && renderDetail !== undefined && (
        <div className="ui-drawer-backdrop" {...dismissDetail}>
          <aside className="ui-drawer" role="dialog" aria-modal="true">
            <header className="ui-drawer-head">
              <div className="ui-drawer-title">{detailTitle !== undefined ? detailTitle(detailRow) : null}</div>
              <div className="ui-drawer-head-actions">
                {detailActions !== undefined && detailActions(detailRow)}
                <button
                  type="button"
                  className="ui-drawer-close"
                  aria-label="Close"
                  onClick={() => setDetailKey(null)}
                >
                  x
                </button>
              </div>
            </header>
            <div className="ui-drawer-body">{renderDetail(detailRow)}</div>
          </aside>
        </div>
      )}
    </div>
  );
}

function headerClass<T>(column: DataTableColumn<T>, active: boolean): string {
  const classes = ["ui-table-th"];
  if (column.sortable === true && column.sortValue !== undefined) classes.push("sortable");
  if (active) classes.push("active");
  if (column.align === "right") classes.push("right");
  return classes.join(" ");
}

function cellClass<T>(column: DataTableColumn<T>): string {
  const classes = ["ui-table-td"];
  if (column.className !== undefined && column.className.length > 0) classes.push(column.className);
  if (column.align === "right") classes.push("right");
  return classes.join(" ");
}

function ariaSort(direction: SortDirection): "ascending" | "descending" {
  return direction === "asc" ? "ascending" : "descending";
}

// Split already filtered and sorted rows into their groups, keeping the sort inside each group, and order the
// groups. Pure, so the grouping rule is the same however the table is drawn.
export function groupRows<T>(
  rows: T[],
  grouping: Pick<DataTableGrouping<T>, "groupOf" | "compareGroups">,
): { groupKey: string; rows: T[] }[] {
  const byGroup = new Map<string, T[]>();
  for (const row of rows) {
    const key = grouping.groupOf(row);
    const bucket = byGroup.get(key);
    if (bucket === undefined) byGroup.set(key, [row]);
    else bucket.push(row);
  }
  return Array.from(byGroup.entries())
    .sort(([a], [b]) => grouping.compareGroups(a, b))
    .map(([groupKey, groupRowsInOrder]) => ({ groupKey, rows: groupRowsInOrder }));
}
