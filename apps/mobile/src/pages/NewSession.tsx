import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Link, useNavigate } from "react-router-dom";
import {
  createSession,
  gatewayErrorMessage,
  getAgents,
  getDirectors,
  getKnownRepositories,
  type AgentChoice,
  type DirectorInfo,
  type RepoInfo,
} from "@devthrottle/client-core/api/client";
import { durationLabel, useNow } from "@devthrottle/client-core/sessions/waiting";

type ActiveStep = "director" | "agent" | "repository" | "review";

/**
 * How many of the one repository list's rows this step shows before you type a search. It is a
 * LENGTH, not an opinion about which rows deserve to be there: the Gateway has already decided the
 * order, so the top of its list is the top of this one.
 */
const TOP_REPOSITORY_COUNT = 5;
const SEARCH_REPOSITORY_COUNT = 50;
const AGENT_STORAGE_PREFIX = "mobile.newSession.agent.";

function timeOfDay(iso: string): string {
  if (!iso) return "";
  const timestamp = new Date(iso);
  if (Number.isNaN(timestamp.getTime())) return "";
  return timestamp.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
}

function directorLabel(director: DirectorInfo): string {
  if (director.displayName.trim()) return director.displayName.trim();
  if (director.machineName.trim()) return director.machineName.trim();
  return director.directorId || "Director";
}

function directorContext(director: DirectorInfo, now: number): string {
  const parts: string[] = [];
  if (director.machineName.trim() && director.machineName.trim() !== directorLabel(director)) {
    parts.push(director.machineName.trim());
  }
  const uptime = durationLabel(director.startedAt, now);
  if (uptime) parts.push(uptime === "just now" ? "started just now" : "running for " + uptime);
  const seen = timeOfDay(director.lastSeen);
  if (seen) parts.push("seen " + seen);
  if (director.version.trim()) parts.push("version " + director.version.trim());
  if (director.directorId) parts.push("Director " + director.directorId.slice(0, 8));
  return parts.join(" · ");
}

function repositoryLabel(repository: RepoInfo): string {
  if (repository.name.trim()) return repository.name.trim();
  const parts = repository.path.replace(/[\\/]+$/, "").split(/[\\/]/).filter(Boolean);
  return parts.length ? parts[parts.length - 1] : repository.path;
}

function rememberedAgent(directorId: string): string | null {
  try {
    return window.localStorage.getItem(AGENT_STORAGE_PREFIX + directorId);
  } catch {
    return null;
  }
}

function rememberAgent(directorId: string, agentType: string): void {
  try {
    window.localStorage.setItem(AGENT_STORAGE_PREFIX + directorId, agentType);
  } catch {
    // Remembering a choice is optional when browser storage is unavailable.
  }
}

function stepSummary(empty: string, selected: string | null): string {
  return selected?.trim() || empty;
}

export function NewSession() {
  const navigate = useNavigate();
  const now = useNow(1000);

  const [activeStep, setActiveStep] = useState<ActiveStep>("director");
  const [directors, setDirectors] = useState<DirectorInfo[] | null>(null);
  const [directorsError, setDirectorsError] = useState<string | null>(null);
  const [directorReload, setDirectorReload] = useState(0);
  const [selectedId, setSelectedId] = useState<string | null>(null);

  const [agents, setAgents] = useState<AgentChoice[] | null>(null);
  const [agentsError, setAgentsError] = useState<string | null>(null);
  const [agentReload, setAgentReload] = useState(0);
  const [selectedAgentType, setSelectedAgentType] = useState<string | null>(null);

  const [knownRepositories, setKnownRepositories] = useState<RepoInfo[] | null>(null);
  const [knownRepositoriesError, setKnownRepositoriesError] = useState<string | null>(null);
  const [repositoryReload, setRepositoryReload] = useState(0);
  const [repositoryQuery, setRepositoryQuery] = useState("");
  const [selectedRepository, setSelectedRepository] = useState<RepoInfo | null>(null);
  const [manualPath, setManualPath] = useState("");
  const [manualOpen, setManualOpen] = useState(false);

  const [creating, setCreating] = useState(false);
  const [createError, setCreateError] = useState<string | null>(null);

  const selectedIdRef = useRef<string | null>(null);
  const createInFlightRef = useRef(false);

  const selectedDirector = directors?.find((director) => director.directorId === selectedId) ?? null;
  const selectedAgent = agents?.find((agent) => agent.type === selectedAgentType) ?? null;

  const clearDirectorChoices = useCallback(() => {
    setAgents(null);
    setAgentsError(null);
    setSelectedAgentType(null);
    setKnownRepositories(null);
    setKnownRepositoriesError(null);
    setRepositoryQuery("");
    setSelectedRepository(null);
    setManualPath("");
    setManualOpen(false);
    setCreateError(null);
  }, []);

  const chooseDirector = useCallback((directorId: string) => {
    if (directorId === selectedIdRef.current) {
      setActiveStep("agent");
      return;
    }

    selectedIdRef.current = directorId;
    clearDirectorChoices();
    setSelectedId(directorId);
    setActiveStep("agent");
  }, [clearDirectorChoices]);

  useEffect(() => {
    const controller = new AbortController();
    setDirectors(null);
    setDirectorsError(null);
    getDirectors(controller.signal)
      .then((list) => {
        if (controller.signal.aborted) return;
        setDirectors(list);
        if (list.length > 0) {
          const firstId = list[0].directorId;
          if (firstId !== selectedIdRef.current) clearDirectorChoices();
          selectedIdRef.current = firstId;
          setSelectedId(firstId);
        } else {
          clearDirectorChoices();
          selectedIdRef.current = null;
          setSelectedId(null);
        }
      })
      .catch((error) => {
        if (controller.signal.aborted) return;
        setDirectorsError(gatewayErrorMessage(error));
      });
    return () => controller.abort();
  }, [directorReload, clearDirectorChoices]);

  useEffect(() => {
    if (!selectedId) return;

    const directorId = selectedId;
    const agentController = new AbortController();
    const isCurrent = () => !agentController.signal.aborted && selectedIdRef.current === directorId;

    setAgents(null);
    setAgentsError(null);

    getAgents(directorId, agentController.signal)
      .then((list) => {
        if (!isCurrent()) return;
        setAgents(list);
        setSelectedAgentType((selected) => {
          if (selected !== null && list.some((agent) => agent.type === selected)) return selected;
          if (list.length === 0) return null;
          const stored = rememberedAgent(directorId);
          return (list.find((agent) => agent.type === stored) ?? list[0]).type;
        });
      })
      .catch((error) => {
        if (!isCurrent()) return;
        setAgents([]);
        setAgentsError(gatewayErrorMessage(error));
      });

    return () => agentController.abort();
  }, [selectedId, agentReload]);

  useEffect(() => {
    if (!selectedId) return;

    const directorId = selectedId;
    const knownController = new AbortController();
    const isCurrent = () => !knownController.signal.aborted && selectedIdRef.current === directorId;

    setKnownRepositories(null);
    setKnownRepositoriesError(null);

    getKnownRepositories(directorId, knownController.signal)
      .then((list) => {
        if (!isCurrent()) return;
        setKnownRepositories(list);
      })
      .catch((error) => {
        if (knownController.signal.aborted || !isCurrent()) return;
        setKnownRepositories([]);
        setKnownRepositoriesError(gatewayErrorMessage(error));
      });

    return () => knownController.abort();
  }, [selectedId, repositoryReload]);

  // THE ONE REPOSITORY LIST, RENDERED IN THE ORDER IT ARRIVED. The Gateway has already put the most
  // recently used repositories first and the ones nobody has ever opened beneath them, and it has
  // already served one entry per repository for the whole machine. So this screen does not sort,
  // does not merge a second source in, and does not de-duplicate: it slices and it filters, both of
  // which keep the order they were handed (Critical Rule 7 in CLAUDE.md - the client is dumb, the
  // Gateway owns all ruling).
  //
  // It used to read a second route as well, GET /directors/{id}/repos - the Director's own registry -
  // and merge the two here. That merge could not avoid ruling: it had to decide which of two records
  // of one repository won, in what order the result came out, and which two paths were the same
  // repository. So the phone showed the Director's answer and the Cockpit showed the Gateway's, for
  // one machine. The one-repository-list mission exists to end exactly that, and phase 5 ended it
  // here. Do not add a second source back.
  const allRepositories = useMemo(() => knownRepositories ?? [], [knownRepositories]);
  const matchedRepositories = useMemo(() => {
    const query = repositoryQuery.trim().toLocaleLowerCase();
    if (!query) return allRepositories.slice(0, TOP_REPOSITORY_COUNT);
    return allRepositories.filter((repository) =>
      repositoryLabel(repository).toLocaleLowerCase().includes(query)
      || repository.path.toLocaleLowerCase().includes(query),
    );
  }, [allRepositories, repositoryQuery]);
  const visibleRepositories = repositoryQuery.trim()
    ? matchedRepositories.slice(0, SEARCH_REPOSITORY_COUNT)
    : matchedRepositories;

  const chooseAgent = (agent: AgentChoice) => {
    if (!selectedId) return;
    setSelectedAgentType(agent.type);
    rememberAgent(selectedId, agent.type);
    setCreateError(null);
    setActiveStep("repository");
  };

  const chooseRepository = (repository: RepoInfo) => {
    setSelectedRepository(repository);
    setManualPath("");
    setCreateError(null);
    setActiveStep("review");
  };

  const chooseManualPath = () => {
    const path = manualPath.trim();
    if (!path) {
      setCreateError("Enter a repository path before continuing.");
      return;
    }
    chooseRepository({ name: "", path, lastUsed: "" });
  };

  const create = useCallback(async () => {
    if (createInFlightRef.current) return;
    if (!selectedId || selectedDirector === null) {
      setCreateError("Choose a Director first.");
      setActiveStep("director");
      return;
    }
    if (selectedAgent === null) {
      setCreateError("Choose an agent before creating the session.");
      setActiveStep("agent");
      return;
    }
    if (selectedRepository === null || !selectedRepository.path.trim()) {
      setCreateError("Choose a repository before creating the session.");
      setActiveStep("repository");
      return;
    }

    createInFlightRef.current = true;
    setCreating(true);
    setCreateError(null);
    try {
      const session = await createSession(selectedId, selectedRepository.path, {
        agent: selectedAgent.type,
      });
      if (!session.sessionId) throw new Error("The created session had no identifier.");
      navigate("/session/" + encodeURIComponent(session.sessionId));
    } catch (error) {
      setCreateError(gatewayErrorMessage(error));
      setCreating(false);
      createInFlightRef.current = false;
    }
  }, [navigate, selectedAgent, selectedDirector, selectedId, selectedRepository]);

  const canReview = selectedDirector !== null && selectedAgent !== null && selectedRepository !== null;
  const repositoryListSettled = knownRepositories !== null;

  return (
    <div className="screen newsession-screen">
      <header className="app-bar newsession-app-bar">
        <Link className="back-link" to="/">&larr; Roster</Link>
        <h1>Start a session</h1>
      </header>

      <div className="newsession-steps">
        <section className={"newsession-step-card" + (activeStep === "director" ? " is-active" : "")}>
          <button
            type="button"
            className="newsession-step-toggle"
            aria-expanded={activeStep === "director"}
            onClick={() => setActiveStep("director")}
          >
            <span className="newsession-step-number">1</span>
            <span className="newsession-step-heading">
              <span className="newsession-step-title">Director</span>
              <span className="newsession-step-summary">
                {stepSummary("Choose where the session will run", selectedDirector ? directorLabel(selectedDirector) : null)}
              </span>
            </span>
            <span aria-hidden="true">{activeStep === "director" ? "−" : "+"}</span>
          </button>

          {activeStep === "director" && (
            <div className="newsession-step-panel">
              {directorsError !== null && (
                <div className="banner banner-error newsession-inline-error" role="alert">
                  <span>{directorsError}</span>
                  <button type="button" className="newsession-retry" onClick={() => setDirectorReload((value) => value + 1)}>
                    Retry
                  </button>
                </div>
              )}
              {directors === null && directorsError === null && <p className="status-line">Loading Directors…</p>}
              {directors !== null && directors.length === 0 && <p className="status-line">No Directors are available.</p>}
              {directors !== null && directors.length > 0 && (
                <ul className="roster newsession-choice-list">
                  {directors.map((director) => (
                    <li
                      key={director.directorId}
                      className={"row" + (director.directorId === selectedId ? " row-selected" : "")}
                    >
                      <button
                        type="button"
                        className="picker-link"
                        aria-label={"Select Director " + directorLabel(director)}
                        onClick={() => chooseDirector(director.directorId)}
                      >
                        <span className="row-body">
                          <span className="row-name">{directorLabel(director)}</span>
                          <span className="row-context">{directorContext(director, now)}</span>
                        </span>
                        {director.directorId === selectedId && <span className="picker-check">Selected</span>}
                      </button>
                    </li>
                  ))}
                </ul>
              )}
            </div>
          )}
        </section>

        <section className={"newsession-step-card" + (activeStep === "agent" ? " is-active" : "")}>
          <button
            type="button"
            className="newsession-step-toggle"
            aria-expanded={activeStep === "agent"}
            disabled={selectedDirector === null}
            onClick={() => setActiveStep("agent")}
          >
            <span className="newsession-step-number">2</span>
            <span className="newsession-step-heading">
              <span className="newsession-step-title">Agent</span>
              <span className="newsession-step-summary">
                {stepSummary("Choose the coding tool", selectedAgent?.displayName ?? null)}
              </span>
            </span>
            <span aria-hidden="true">{activeStep === "agent" ? "−" : "+"}</span>
          </button>

          {activeStep === "agent" && (
            <div className="newsession-step-panel">
              {agents === null && agentsError === null && <p className="status-line">Loading agents…</p>}
              {agentsError !== null && (
                <div className="banner banner-error newsession-inline-error" role="alert">
                  <span>Could not load agents: {agentsError}</span>
                  <button type="button" className="newsession-retry" onClick={() => setAgentReload((value) => value + 1)}>
                    Retry
                  </button>
                </div>
              )}
              {agents !== null && agents.length === 0 && agentsError === null && (
                <p className="status-line">No agents are configured on this Director. A session cannot be created here.</p>
              )}
              {agents !== null && agents.length > 0 && (
                <ul className="roster newsession-choice-list">
                  {agents.map((agent) => (
                    <li key={agent.type} className={"row" + (agent.type === selectedAgentType ? " row-selected" : "")}>
                      <button
                        type="button"
                        className="picker-link"
                        aria-label={"Select agent " + agent.displayName}
                        onClick={() => chooseAgent(agent)}
                      >
                        <span className="row-body">
                          <span className="row-name">{agent.displayName}</span>
                          <span className="row-context">
                            {agent.modelLabel.trim() || "Uses the agent’s configured default model"}
                          </span>
                        </span>
                        {agent.type === selectedAgentType && <span className="picker-check">Selected</span>}
                      </button>
                    </li>
                  ))}
                </ul>
              )}
            </div>
          )}
        </section>

        <section className={"newsession-step-card" + (activeStep === "repository" ? " is-active" : "")}>
          <button
            type="button"
            className="newsession-step-toggle"
            aria-expanded={activeStep === "repository"}
            disabled={selectedAgent === null}
            onClick={() => setActiveStep("repository")}
          >
            <span className="newsession-step-number">3</span>
            <span className="newsession-step-heading">
              <span className="newsession-step-title">Repository</span>
              <span className="newsession-step-summary">
                {stepSummary("Choose the working directory", selectedRepository ? repositoryLabel(selectedRepository) : null)}
              </span>
            </span>
            <span aria-hidden="true">{activeStep === "repository" ? "−" : "+"}</span>
          </button>

          {activeStep === "repository" && (
            <div className="newsession-step-panel">
              <label className="newsession-search-label" htmlFor="newsession-repository-search">
                Search known repositories
              </label>
              <input
                id="newsession-repository-search"
                className="term-input newsession-search"
                type="search"
                autoComplete="off"
                autoCapitalize="off"
                autoCorrect="off"
                spellCheck={false}
                placeholder="Repository name or path"
                value={repositoryQuery}
                onChange={(event) => setRepositoryQuery(event.target.value)}
              />
              {/* The note and the empty-state sentence below both describe the list, so neither is
                  shown when the list could not be loaded: a screen that says "showing the top five"
                  above an error, and "no repositories are known on this machine" beside one, is
                  telling the reader two things it does not know. Not loaded is not the same fact as
                  nothing there. */}
              {!repositoryQuery.trim() && knownRepositoriesError === null && (
                <p className="newsession-recent-note">
                  Showing the top five of this machine&rsquo;s repository list, most recently used first.
                </p>
              )}
              {repositoryQuery.trim() && matchedRepositories.length > visibleRepositories.length && (
                <p className="newsession-recent-note" role="status">
                  Showing {visibleRepositories.length} of {matchedRepositories.length} matches. Type more to narrow the results.
                </p>
              )}

              {!repositoryListSettled && visibleRepositories.length === 0 && (
                <p className="status-line">Loading repositories…</p>
              )}
              {knownRepositoriesError !== null && (
                <div className="banner banner-error newsession-source-error" role="alert">
                  The repository list could not be loaded: {knownRepositoriesError}
                </div>
              )}
              {knownRepositoriesError !== null && (
                <button type="button" className="newsession-retry standalone" onClick={() => setRepositoryReload((value) => value + 1)}>
                  Retry the repository list
                </button>
              )}

              {repositoryListSettled && knownRepositoriesError === null && visibleRepositories.length === 0 && (
                <p className="status-line">
                  {repositoryQuery.trim()
                    ? "No known repositories match that search."
                    : "No repositories are known on this machine yet."}
                </p>
              )}
              {/* The row key is the path itself: the Gateway serves one entry per repository for
                  this machine, so there is nothing left for a client rule to de-duplicate - and the
                  rule that used to do it here decided whether a path was a Windows one from the
                  path's own shape, which is a judgement this mission has already found wrong in
                  four places. */}
              {visibleRepositories.length > 0 && (
                <ul className="roster newsession-choice-list newsession-repository-list">
                  {visibleRepositories.map((repository) => (
                    <li key={repository.path} className="row">
                      <button
                        type="button"
                        className="picker-link"
                        aria-label={"Select repository " + repositoryLabel(repository)}
                        onClick={() => chooseRepository(repository)}
                      >
                        <span className="row-body">
                          <span className="row-name">{repositoryLabel(repository)}</span>
                          <span className="row-context newsession-path">{repository.path}</span>
                        </span>
                      </button>
                    </li>
                  ))}
                </ul>
              )}

              <button
                type="button"
                className="newsession-manual-toggle"
                aria-expanded={manualOpen}
                onClick={() => setManualOpen((open) => !open)}
              >
                {manualOpen ? "Hide manual path" : "Enter a path manually"}
              </button>
              {manualOpen && (
                <div className="newsession-manual-panel">
                  <label className="newsession-search-label" htmlFor="newsession-path">Repository path</label>
                  <input
                    id="newsession-path"
                    className="term-input newsession-search"
                    type="text"
                    inputMode="text"
                    autoComplete="off"
                    autoCapitalize="off"
                    autoCorrect="off"
                    spellCheck={false}
                    placeholder="D:\Repositories\my-project"
                    value={manualPath}
                    onChange={(event) => {
                      setManualPath(event.target.value);
                      setCreateError(null);
                    }}
                    onKeyDown={(event) => {
                      if (event.key === "Enter") {
                        event.preventDefault();
                        chooseManualPath();
                      }
                    }}
                  />
                  <button type="button" className="newsession-select-path" onClick={chooseManualPath}>
                    Use this path
                  </button>
                </div>
              )}
            </div>
          )}
        </section>

        {activeStep !== "review" && createError !== null && (
          <div className="banner banner-error newsession-inline-error" role="alert">{createError}</div>
        )}

        {activeStep === "review" && canReview && (
          <section className="newsession-review" aria-labelledby="newsession-review-title">
            <h2 id="newsession-review-title">Review the new session</h2>
            <dl>
              <div>
                <dt>Director</dt>
                <dd>{directorLabel(selectedDirector)}</dd>
              </div>
              <div>
                <dt>Machine</dt>
                <dd>{selectedDirector.machineName || "Not reported"}</dd>
              </div>
              <div>
                <dt>Agent</dt>
                <dd>
                  {selectedAgent.displayName}
                  <span>{selectedAgent.modelLabel.trim() || "Configured default model"}</span>
                </dd>
              </div>
              <div>
                <dt>Repository</dt>
                <dd>
                  {repositoryLabel(selectedRepository)}
                  <span className="newsession-path">{selectedRepository.path}</span>
                </dd>
              </div>
            </dl>
            {createError !== null && <div className="banner banner-error" role="alert">{createError}</div>}
          </section>
        )}
      </div>

      <footer className="newsession-footer">
        <div className="newsession-footer-note" aria-live="polite">
          {creating ? "Creating the session…" : activeStep === "review" ? "Ready to create." : "Review all choices before creating."}
        </div>
        <button
          type="button"
          className="term-btn newsession-primary"
          disabled={!canReview || creating}
          onClick={() => {
            if (activeStep === "review") {
              void create();
            } else {
              setCreateError(null);
              setActiveStep("review");
            }
          }}
        >
          {creating ? "Creating…" : activeStep === "review" ? "Create session" : "Review selections"}
        </button>
      </footer>
    </div>
  );
}
