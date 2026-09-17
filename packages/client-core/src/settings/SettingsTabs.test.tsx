// The Settings page body, rendered. These are the checks that the UNIFICATION actually holds - the
// reason the tabs and the cards were moved into client-core in the first place.
//
// Why render rather than assert the tab list alone: the tab list already has its own test (tabs.test.ts),
// and a tab list can be perfectly correct while the panel behind a tab renders nothing. That is exactly
// the failure this work exists to prevent - a "Transcription" tab that offers no way to test your
// microphone is worse than no tab, because it tells the user they have already looked.
//
// The panels are the REAL MicTestPanel / TranscriptionTestPanel / MicrophoneQualityPanel. They are not
// stubbed on purpose: stubbing them would prove the tab renders a stub. They need no microphone to draw
// their resting state, and the microphone-quality panel reports its own load failure when the Gateway
// cannot be reached, which is a rendered state, not a blank.
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { SettingsTabPanel, SettingsTabStrip } from "./SettingsTabs";
import { setTtsModel, setWingmanFastModel, setWingmanModel, ttsSample } from "../api/ai";
import { setSpokenLanguage, setSpokenVoice } from "../api/language";

// The account snapshot every AI-ish tab reads. Values are arbitrary but must be present: a tab that
// renders "Loading..." forever proves nothing about its content.
const SNAPSHOT = {
  provider: "devthrottle",
  wingmanModel: "test-thinking-model",
  wingmanFastModel: "test-fast-model",
  ttsModel: "test-speech-model",
  ttsVoice: "test-voice",
  voices: ["test-voice"],
  transcriptionModel: "test-transcription-model",
  catalogAvailable: false,
};

vi.mock("../api/ai", () => ({
  getAiProvider: vi.fn(async () => SNAPSHOT),
  getAiModels: vi.fn(async () => []),
  setWingmanModel: vi.fn(),
  setWingmanFastModel: vi.fn(),
  setTtsModel: vi.fn(),
  setTtsVoice: vi.fn(),
  testChat: vi.fn(),
  ttsSample: vi.fn(),
}));

// ---- The Language tab's document (issue #1010) -----------------------------------------------------
//
// The Gateway folds this whole thing: the voice list is already filtered per language, the labels are
// already assembled, the sample is already in the right language, and `voice` is the voice the ACCOUNT
// will be spoken with. So the fake below is shaped like the real document and the tests assert that the
// tab RENDERS it rather than re-derives it. The lopsided counts are the real ones - French genuinely has
// one voice - because a one-entry list is the case most likely to be mishandled.
const EN_VOICES = [
  { id: "af_bella", label: "Bella - English, American female" },
  { id: "bm_george", label: "George - English, British male" },
];
const FR_VOICES = [{ id: "ff_siwis", label: "Siwis - French, female" }];
const ES_VOICES = [
  { id: "ef_dora", label: "Dora - Spanish, female" },
  { id: "em_alex", label: "Alex - Spanish, male" },
];
const SAMPLES: Record<string, string> = {
  en: "Hi, I'm your DevThrottle wingman. This is how I'll sound.",
  fr: "Bonjour, je suis votre wingman DevThrottle.",
  es: "Hola, soy tu wingman de DevThrottle.",
};

function languageDocument(language: string, voice: string) {
  return {
    language,
    voice,
    languages: [
      { code: "en", label: "English", note: "Default", sample: SAMPLES.en, voices: EN_VOICES },
      { code: "fr", label: "French", note: "Francais", sample: SAMPLES.fr, voices: FR_VOICES },
      { code: "es", label: "Spanish", note: "Espanol", sample: SAMPLES.es, voices: ES_VOICES },
    ],
  };
}

// What each language's voice would resolve to, standing in for the Gateway's per-language memory: English
// has been left on George, and the other two have never been chosen so they fall to their defaults.
const REMEMBERED: Record<string, string> = { en: "bm_george", fr: "ff_siwis", es: "ef_dora" };
let languageState = languageDocument("en", REMEMBERED.en);

vi.mock("../api/language", () => ({
  getSpokenLanguage: vi.fn(async () => languageState),
  setSpokenLanguage: vi.fn(async (code: string) => {
    languageState = languageDocument(code, REMEMBERED[code]);
    return languageState;
  }),
  setSpokenVoice: vi.fn(async (code: string, voice: string) => {
    REMEMBERED[code] = voice;
    languageState = languageDocument(code, voice);
    return languageState;
  }),
}));

function mount(ui: React.ReactNode) {
  return render(<MemoryRouter>{ui}</MemoryRouter>);
}

beforeEach(() => {
  vi.clearAllMocks();
  // Each test starts from the same account: English, on the English voice it had chosen.
  REMEMBERED.en = "bm_george";
  REMEMBERED.fr = "ff_siwis";
  REMEMBERED.es = "ef_dora";
  languageState = languageDocument("en", REMEMBERED.en);
  // No Gateway in a unit test. Every card must therefore render its own explicit state - a heading and
  // either its content or its own error line - and never a blank panel.
  vi.stubGlobal("fetch", vi.fn(async () => {
    throw new Error("no Gateway in this test");
  }));
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("the Settings tab strip", () => {
  it("offers the four shared tabs on the phone", () => {
    mount(<SettingsTabStrip active="notifications" onSelect={() => {}} surface="mobile" />);
    expect(screen.getAllByRole("tab").map((t) => t.textContent)).toEqual([
      "Notifications",
      "Language",
      "Transcription",
      "Fleet Manager",
    ]);
  });

  // The Cockpit-only tab, rendered (issue #550). The tab set decides this, not the shell, so the strip is
  // where it can be seen: the same four in the same order, and one more that the phone above does not get.
  it("offers those same four plus Injected text on the Cockpit", () => {
    mount(<SettingsTabStrip active="notifications" onSelect={() => {}} surface="cockpit" />);
    expect(screen.getAllByRole("tab").map((t) => t.textContent)).toEqual([
      "Notifications",
      "Language",
      "Transcription",
      "Fleet Manager",
      "Injected text",
    ]);
  });

  // Hidden means hidden on both surfaces. Asserted through the RENDERED strip and not only through the
  // tab list, because the strip is what a person actually sees; a list can be right while a shell puts a
  // button back.
  it("offers no AI button on either surface", () => {
    for (const surface of ["cockpit", "mobile"] as const) {
      const { unmount } = mount(
        <SettingsTabStrip active="notifications" onSelect={() => {}} surface={surface} />,
      );
      expect(screen.queryByRole("tab", { name: "AI" })).toBeNull();
      unmount();
    }
  });

  // The Assistant was removed from the product (the Fleet Manager mission, step 9). Its tab must not come
  // back on either surface, hidden or not.
  it("offers no Assistant button on either surface", () => {
    for (const surface of ["cockpit", "mobile"] as const) {
      const { unmount } = mount(
        <SettingsTabStrip active="notifications" onSelect={() => {}} surface={surface} />,
      );
      expect(screen.getAllByRole("tab").length).toBeGreaterThan(0);
      expect(screen.queryByRole("tab", { name: "Assistant" })).toBeNull();
      unmount();
    }
  });

  it("marks exactly the active tab selected", () => {
    mount(<SettingsTabStrip active="transcription" onSelect={() => {}} surface="cockpit" />);
    const selected = screen.getAllByRole("tab").filter((t) => t.getAttribute("aria-selected") === "true");
    expect(selected.map((t) => t.textContent)).toEqual(["Transcription"]);
  });
});

describe("the Transcription tab", () => {
  // THE point of this work: the dictation checks used to be a separate page on the desktop and two
  // separate screens on the phone, so neither Settings page could answer "why is my dictation wrong?".
  it("carries the transcription model and BOTH on-demand checks", async () => {
    mount(<SettingsTabPanel tab="transcription" />);

    expect(await screen.findByText("test-transcription-model")).toBeTruthy();
    expect(screen.getByRole("heading", { name: "Test your microphone" })).toBeTruthy();
    expect(screen.getByRole("heading", { name: "Test transcription" })).toBeTruthy();
    expect(screen.getByRole("heading", { name: "How your microphones are doing" })).toBeTruthy();
  });

  // The phone has no Transcription Health page and no account page. A shared component that hard-coded
  // those links would put dead links on the phone, so the links are opt-in per surface.
  it("renders no health link unless the mounting surface says it has one", async () => {
    const { unmount } = mount(<SettingsTabPanel tab="transcription" />);
    await screen.findByText("test-transcription-model");
    expect(screen.queryByRole("link", { name: "Transcription Health" })).toBeNull();
    unmount();

    mount(<SettingsTabPanel tab="transcription" transcriptionHealthHref="/transcription" />);
    await screen.findByText("test-transcription-model");
    expect(screen.getByRole("link", { name: "Transcription Health" })).toBeTruthy();
  });
});

// The AI tab is no longer offered anywhere - not in the strip, and not by ?tab= either. The COMPONENT is
// still here and still built, because hiding it is meant to be reversible by one word rather than by
// restoring a deleted screen (see tabs.ts). These are the checks that the thing being kept is still whole:
// mounted directly, it draws in full and it writes nothing.
describe("the AI tab, hidden but kept", () => {
  it("draws the whole tab, with the account's saved models selected", async () => {
    mount(<SettingsTabPanel tab="ai" />);

    const thinking = (await screen.findByLabelText("Thinking model")) as HTMLSelectElement;
    const fast = screen.getByLabelText("Fast model") as HTMLSelectElement;
    expect(thinking.value).toBe("test-thinking-model");
    expect(fast.value).toBe("test-fast-model");
    expect(screen.getByLabelText("Speech model")).toBeTruthy();
    expect(screen.getByLabelText("Voice")).toBeTruthy();
  });

  // The classic version of this bug: a control that is no longer in front of anybody quietly writes over
  // what it holds - typically a null - the first time it is drawn. The stored models live on the Gateway
  // per account, they are still what the wingman runs on, and merely rendering this tab must write
  // NOTHING. Only a person choosing does that.
  it("writes no model setting merely by being rendered", async () => {
    mount(<SettingsTabPanel tab="ai" />);
    await screen.findByLabelText("Thinking model");
    expect(setWingmanModel).not.toHaveBeenCalled();
    expect(setWingmanFastModel).not.toHaveBeenCalled();
  });
});

describe("what moved off the AI tab", () => {
  // The fleet-brain model moved from the AI screen to the Car Mode tab, then to the Assistant tab, and left
  // the product with the Assistant (the Fleet Manager mission, step 9). The end phrase and its live tester
  // went with Car Mode (#1028). None of them may come back onto the AI tab.
  it("offers no fleet-brain model and no end phrase on the AI tab", async () => {
    mount(<SettingsTabPanel tab="ai" />);
    expect(await screen.findByLabelText("Thinking model")).toBeTruthy();
    expect(screen.queryByLabelText("Model")).toBeNull();
    expect(screen.queryByLabelText("End phrase (say this to finish your turn)")).toBeNull();
  });

  it("keeps the account link off surfaces that have no account page", async () => {
    const { unmount } = mount(<SettingsTabPanel tab="ai" />);
    await screen.findByLabelText("Thinking model");
    expect(screen.queryByRole("link", { name: "Manage account" })).toBeNull();
    unmount();

    mount(<SettingsTabPanel tab="ai" accountHref="/account" />);
    await screen.findByLabelText("Thinking model");
    expect(screen.getByRole("link", { name: "Manage account" })).toBeTruthy();
  });
});

// ---- The Language tab (issue #1010) ----------------------------------------------------------------
//
// These are the checks that the tab RENDERS THE GATEWAY'S DOCUMENT and derives nothing of its own. That
// distinction is the whole reason this screen ships last: the reverted multilingual build decided a
// language's consequences in the browser, from a model catalog the hosted Gateway refuses to serve, so on
// hosted the client held an empty list and the guard that should have caught it never fired
// (devthrottle_internal#547). A tab that re-derives is a tab that can be right in a test and empty in
// production.
describe("the Language tab", () => {
  it("offers all three languages, each with the word the Gateway put under it", async () => {
    mount(<SettingsTabPanel tab="language" />);

    const english = (await screen.findByRole("radio", { name: /English/ })) as HTMLInputElement;
    expect(english.checked).toBe(true);
    expect(screen.getByRole("radio", { name: /French/ })).toBeTruthy();
    expect(screen.getByRole("radio", { name: /Spanish/ })).toBeTruthy();
    // "Default" under English, and each other language's own name for itself - folded on the Gateway, so
    // the test asserts they are SHOWN, not that the client worked out which one to show.
    expect(screen.getByText("Default")).toBeTruthy();
    expect(screen.getByText("Francais")).toBeTruthy();
    expect(screen.getByText("Espanol")).toBeTruthy();
  });

  it("shows the account's own voice selected, from the list for its language", async () => {
    mount(<SettingsTabPanel tab="language" />);

    const voice = (await screen.findByLabelText("Voice")) as HTMLSelectElement;
    expect(voice.value).toBe("bm_george");
    expect(Array.from(voice.options).map((o) => o.textContent)).toEqual([
      "Bella - English, American female",
      "George - English, British male",
    ]);
  });

  // THE ACCEPTANCE ROW: the voice list is filtered to the selected language. Checked on FRENCH, the
  // language with exactly one voice - the case where a control could most plausibly be hidden, emptied, or
  // filled with English voices without anybody noticing in English.
  it("filters the voice list to the chosen language, and keeps the control visible for French", async () => {
    mount(<SettingsTabPanel tab="language" />);
    const french = await screen.findByRole("radio", { name: /French/ });

    fireEvent.click(french);

    await waitFor(() => expect(setSpokenLanguage).toHaveBeenCalledWith("fr"));
    const voice = (await screen.findByLabelText("Voice")) as HTMLSelectElement;
    // Still there, and holding exactly one item. One item is a list, not an empty control.
    expect(Array.from(voice.options).map((o) => o.value)).toEqual(["ff_siwis"]);
    expect(voice.value).toBe("ff_siwis");
  });

  // PER-LANGUAGE VOICE MEMORY, the acceptance row spelled out: English -> French -> English gets the
  // original English voice back. Nothing is overwritten, so there is no restore step to go wrong - which
  // is the part of the reverted design this replaces.
  it("gives the English voice back after a trip through French", async () => {
    mount(<SettingsTabPanel tab="language" />);

    fireEvent.click(await screen.findByRole("radio", { name: /French/ }));
    await waitFor(() => expect(setSpokenLanguage).toHaveBeenCalledWith("fr"));
    await waitFor(() => expect((screen.getByLabelText("Voice") as HTMLSelectElement).value).toBe("ff_siwis"));

    fireEvent.click(screen.getByRole("radio", { name: /English/ }));
    await waitFor(() => expect(setSpokenLanguage).toHaveBeenCalledWith("en"));
    await waitFor(() =>
      expect((screen.getByLabelText("Voice") as HTMLSelectElement).value).toBe("bm_george"),
    );
  });

  // A voice is always recorded WITH the language it was chosen for, never against "whatever is selected
  // now". Two requests in flight during a language switch would otherwise file a voice under the wrong
  // language, and the per-language memory is the one mechanism this screen rests on.
  it("records a voice choice against the language it was made for", async () => {
    mount(<SettingsTabPanel tab="language" />);
    const voice = (await screen.findByLabelText("Voice")) as HTMLSelectElement;

    fireEvent.change(voice, { target: { value: "af_bella" } });

    await waitFor(() => expect(setSpokenVoice).toHaveBeenCalledWith("en", "af_bella"));
  });

  // Merely opening the tab must write nothing. The setting changes what every session sounds like, and a
  // screen that saves on render would repoint an account's language by being looked at.
  it("writes nothing merely by being rendered", async () => {
    mount(<SettingsTabPanel tab="language" />);
    await screen.findByLabelText("Voice");

    expect(setSpokenLanguage).not.toHaveBeenCalled();
    expect(setSpokenVoice).not.toHaveBeenCalled();
  });

  // THE SAMPLE AUDITIONS THE REAL THING. Two properties in one assertion, and both are failures that
  // shipped last time: the text is the SELECTED LANGUAGE'S sentence (auditioning a French voice on English
  // words tests nothing), and the model is EMPTY so the Gateway uses the account's own engine. A language
  // must never pick an engine - that is what got the last build reverted - and the client is one of the
  // places that could still try.
  it("plays the selected language's sentence, on the account's own engine", async () => {
    Object.defineProperty(URL, "createObjectURL", { value: () => "blob:sample", writable: true });
    Object.defineProperty(window.HTMLMediaElement.prototype, "play", {
      value: async () => {},
      writable: true,
    });
    vi.mocked(ttsSample).mockResolvedValue(new Blob(["audio"]));

    mount(<SettingsTabPanel tab="language" />);
    fireEvent.click(await screen.findByRole("radio", { name: /Spanish/ }));
    await waitFor(() => expect(setSpokenLanguage).toHaveBeenCalledWith("es"));

    fireEvent.click(screen.getByRole("button", { name: "Play sample" }));

    await waitFor(() => expect(ttsSample).toHaveBeenCalledWith(SAMPLES.es, "", "ef_dora"));
    expect(setTtsModel).not.toHaveBeenCalled();
  });

  // The line that exists to stop people hunting for a setting that should not exist. Dictation sends no
  // language field and the provider detects it; French and Spanish dictation already worked and always did.
  it("says that dictation needs nothing set", async () => {
    mount(<SettingsTabPanel tab="language" />);
    await screen.findByLabelText("Voice");

    expect(screen.getByText(/Typing and dictation are unaffected/)).toBeTruthy();
  });

  // No speech MODEL on this screen, in any language. The model control belongs to the AI tab; a language
  // choosing an engine is the reverted failure, and a control here would be the first step back to it.
  it("offers no speech model control", async () => {
    mount(<SettingsTabPanel tab="language" />);
    await screen.findByLabelText("Voice");

    expect(screen.queryByLabelText("Speech model")).toBeNull();
    expect(screen.queryByLabelText("Thinking model")).toBeNull();
  });
});
