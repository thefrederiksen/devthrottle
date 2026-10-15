// The saved session filters on the signed-in account (issue #4009), read and written through the Gateway so a filter
// saved in one Cockpit tab is there in every tab, on every machine, and on the phone. A saved filter is a name, a
// colour and the repos and machines it shows; what it MATCHES is the one shared rule in ./filter.
//
// The colour's hex comes from the Gateway on every filter and in the palette, so no client maps a colour key to a
// colour itself.
import { useCallback, useEffect, useState } from "react";
import { authHeaders, gatewayFetch, GatewayError } from "../api/client";
import { describeAndReport } from "../errors/reportClientError";
import type { NamedFilter } from "./filter";

const ROUTE = "/gateway/session-filters";

export interface SavedSessionFilter extends NamedFilter {
  id: string;
  name: string;
  colour: string;
  colourHex: string;
  repos: string[];
  machines: string[];
  createdAtUtc: string;
  updatedAtUtc: string;
}

export interface SavedFilterColour {
  key: string;
  label: string;
  hex: string;
}

export interface SavedFilterList {
  filters: SavedSessionFilter[];
  colours: SavedFilterColour[];
}

// What a person saves: the Gateway checks it and answers with a sentence when it refuses.
export interface SavedFilterInput {
  name: string;
  colour: string;
  repos: string[];
  machines: string[];
}

export async function listSavedFilters(signal?: AbortSignal): Promise<SavedFilterList> {
  const res = await gatewayFetch(ROUTE, { method: "GET", headers: { Accept: "application/json", ...authHeaders() }, signal });
  if (!res.ok) throw await GatewayError.from(res, "load your saved filters");
  const body = (await res.json()) as { filters?: unknown[]; colours?: unknown[] };
  return {
    filters: (body.filters ?? []).map(toFilter),
    colours: (body.colours ?? []).map((c) => {
      const r = c as Record<string, unknown>;
      return { key: String(r.key ?? ""), label: String(r.label ?? ""), hex: String(r.hex ?? "") };
    }),
  };
}

export async function createSavedFilter(input: SavedFilterInput): Promise<SavedSessionFilter> {
  const res = await gatewayFetch(ROUTE, {
    method: "POST",
    headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
    body: JSON.stringify(input),
  });
  if (!res.ok) throw await GatewayError.from(res, "save the filter");
  return toFilter(await res.json());
}

export async function updateSavedFilter(id: string, input: SavedFilterInput): Promise<SavedSessionFilter> {
  const res = await gatewayFetch(`${ROUTE}/${encodeURIComponent(id)}`, {
    method: "PUT",
    headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
    body: JSON.stringify(input),
  });
  if (!res.ok) throw await GatewayError.from(res, "change the filter");
  return toFilter(await res.json());
}

export async function deleteSavedFilter(id: string): Promise<void> {
  const res = await gatewayFetch(`${ROUTE}/${encodeURIComponent(id)}`, {
    method: "DELETE",
    headers: { Accept: "application/json", ...authHeaders() },
  });
  if (!res.ok) throw await GatewayError.from(res, "delete the filter");
}

function toFilter(raw: unknown): SavedSessionFilter {
  const r = raw as Record<string, unknown>;
  const list = (v: unknown) => (Array.isArray(v) ? v.map(String) : []);
  return {
    id: String(r.id ?? ""),
    name: String(r.name ?? ""),
    colour: String(r.colour ?? ""),
    colourHex: String(r.colourHex ?? ""),
    repos: list(r.repos),
    machines: list(r.machines),
    createdAtUtc: String(r.createdAtUtc ?? ""),
    updatedAtUtc: String(r.updatedAtUtc ?? ""),
  };
}

export interface SavedFiltersState {
  /** null until the first load answers. */
  filters: SavedSessionFilter[] | null;
  colours: SavedFilterColour[];
  /** The load's failure, in words; null when the last load succeeded. */
  error: string | null;
  /** Read the list again (after another tab or the phone changed it). */
  reload: () => void;
  create: (input: SavedFilterInput) => Promise<SavedSessionFilter>;
  update: (id: string, input: SavedFilterInput) => Promise<SavedSessionFilter>;
  remove: (id: string) => Promise<void>;
}

// The saved filters, loaded once on mount and again after every write, and on reload(). A write's refusal is thrown
// to the caller, which shows the Gateway's sentence where the person is looking.
export function useSavedFilters(): SavedFiltersState {
  const [filters, setFilters] = useState<SavedSessionFilter[] | null>(null);
  const [colours, setColours] = useState<SavedFilterColour[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [generation, setGeneration] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    listSavedFilters(controller.signal)
      .then((list) => {
        setFilters(list.filters);
        setColours(list.colours);
        setError(null);
      })
      .catch((err: unknown) => {
        if (controller.signal.aborted) return;
        setError(describeAndReport("saved-session-filters", "load your saved filters", err));
      });
    return () => controller.abort();
  }, [generation]);

  const reload = useCallback(() => setGeneration((g) => g + 1), []);

  const create = useCallback(async (input: SavedFilterInput) => {
    const saved = await createSavedFilter(input);
    reload();
    return saved;
  }, [reload]);

  const update = useCallback(async (id: string, input: SavedFilterInput) => {
    const saved = await updateSavedFilter(id, input);
    reload();
    return saved;
  }, [reload]);

  const remove = useCallback(async (id: string) => {
    await deleteSavedFilter(id);
    reload();
  }, [reload]);

  return { filters, colours, error, reload, create, update, remove };
}
