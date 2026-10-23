import { describe, it, expect } from "vitest";
import { extractLinks, tryConvertFileUrlToLocalPath } from "./historyLinks";

// These mirror the applicable subset of the C# LinkDetectorTests
// (src/CcDirector.Core.Tests/LinkDetectorTests.cs) - the cases the History browser call exercises:
// FindAllLinkMatches(line, repoPath = null, pathExistsCheck = null), which yields URLs + ABSOLUTE
// paths only (relative-path guessing and the on-disk space-extension need a repo root + an existence
// check the browser does not have). Keeping these in step with the C# recognizer is the guarantee
// that the History links do not diverge (issue #970).

// Convenience: extractLinks dedups per body; for a single link on one line it returns [{text,isUrl}].
function links(line: string) {
  return extractLinks(line);
}

describe("quoted paths", () => {
  it.each([
    ['Check "D:\\Projects\\file.txt" for details', "D:\\Projects\\file.txt"],
    ["Check 'D:\\Projects\\file.txt' for details", "D:\\Projects\\file.txt"],
    ["Check `D:\\Projects\\file.txt` for details", "D:\\Projects\\file.txt"],
  ])("matches a quoted Windows path: %s", (line, expected) => {
    expect(links(line)).toEqual([{ text: expected, isUrl: false }]);
  });

  it("matches a quoted path containing spaces", () => {
    const line = '"D:\\Projects\\course\\sample-course\\AI Agents Join the Club.pdf"';
    expect(links(line)).toEqual([
      { text: "D:\\Projects\\course\\sample-course\\AI Agents Join the Club.pdf", isUrl: false },
    ]);
  });

  it("matches a quoted unix path", () => {
    expect(links('"/c/Users/test/my file.txt"')).toEqual([
      { text: "/c/Users/test/my file.txt", isUrl: false },
    ]);
  });

  it("does not match empty quotes", () => {
    expect(links('he said ""')).toEqual([]);
  });

  it("still matches the unquoted part when the quote is unclosed", () => {
    expect(links('"D:\\path\\file.txt')).toEqual([{ text: "D:\\path\\file.txt", isUrl: false }]);
  });

  it("returns a single match when quoted and unquoted overlap", () => {
    expect(links('"D:\\path\\file.txt"')).toEqual([{ text: "D:\\path\\file.txt", isUrl: false }]);
  });
});

describe("absolute windows paths", () => {
  it.each(["D:\\path\\file.txt", "C:\\Users\\test\\Documents\\report.pdf", "D:/path/file.txt"])(
    "matches %s",
    (path) => {
      expect(links(path)).toEqual([{ text: path, isUrl: false }]);
    },
  );

  it("strips a trailing :line number", () => {
    expect(links("D:\\path\\file.cs:42")).toEqual([{ text: "D:\\path\\file.cs", isUrl: false }]);
  });

  it("strips a trailing :line:col number", () => {
    expect(links("D:\\path\\file.cs:42:10")).toEqual([{ text: "D:\\path\\file.cs", isUrl: false }]);
  });

  it("matches just the path when embedded in prose", () => {
    expect(links("Error in D:\\src\\file.cs:10 at runtime")).toEqual([
      { text: "D:\\src\\file.cs", isUrl: false },
    ]);
  });

  it("strips a trailing comma", () => {
    expect(links("Check D:\\path\\file.txt, then proceed")).toEqual([
      { text: "D:\\path\\file.txt", isUrl: false },
    ]);
  });

  it("strips a trailing period", () => {
    expect(links("Check D:\\path\\file.txt. Then proceed")).toEqual([
      { text: "D:\\path\\file.txt", isUrl: false },
    ]);
  });

  it("does not include following parentheses", () => {
    expect(links("(see D:\\path\\file.txt)")).toEqual([{ text: "D:\\path\\file.txt", isUrl: false }]);
  });

  it("without an existence check, a path with spaces stops at the first space", () => {
    expect(links("Created at D:\\Test Root\\Outer Folder\\file.md.")[0]).toEqual({
      text: "D:\\Test",
      isUrl: false,
    });
  });
});

describe("unix paths", () => {
  it.each(["/c/Users/test/file.txt", "/d/Projects/myapp/src/main.cs"])("matches %s", (path) => {
    expect(links(path)).toEqual([{ text: path, isUrl: false }]);
  });
});

describe("urls", () => {
  it.each([
    "https://example.com",
    "http://example.com/path",
    "https://github.com/user/repo/blob/main/file.cs",
  ])("matches %s", (url) => {
    expect(links(url)).toEqual([{ text: url, isUrl: true }]);
  });

  it("matches a git@ url", () => {
    expect(links("git@github.com:user/repo.git")).toEqual([
      { text: "git@github.com:user/repo.git", isUrl: true },
    ]);
  });

  it("matches a url in the middle of text", () => {
    expect(links("Visit https://docs.example.com/guide for help")).toEqual([
      { text: "https://docs.example.com/guide", isUrl: true },
    ]);
  });

  it("strips a trailing period from a url", () => {
    expect(links("Visit https://example.com/page. Thanks")).toEqual([
      { text: "https://example.com/page", isUrl: true },
    ]);
  });

  it("strips a trailing period from a localhost url", () => {
    expect(links("available at http://localhost:4001.")).toEqual([
      { text: "http://localhost:4001", isUrl: true },
    ]);
  });
});

describe("ordering and dedup", () => {
  it("returns urls before absolute paths, both detected", () => {
    expect(links("See D:\\file.txt and https://example.com for more")).toEqual([
      { text: "https://example.com", isUrl: true },
      { text: "D:\\file.txt", isUrl: false },
    ]);
  });

  it("returns multiple non-overlapping paths", () => {
    expect(links("D:\\file1.txt and D:\\file2.txt")).toEqual([
      { text: "D:\\file1.txt", isUrl: false },
      { text: "D:\\file2.txt", isUrl: false },
    ]);
  });

  it("dedups repeated links across lines, first-seen order", () => {
    const body = "D:\\a.txt\nhttps://x.com\nD:\\a.txt";
    expect(extractLinks(body)).toEqual([
      { text: "D:\\a.txt", isUrl: false },
      { text: "https://x.com", isUrl: true },
    ]);
  });
});

describe("drive-letter boundary guard (issue #252)", () => {
  it("does not mis-claim a letter inside a word as a drive path", () => {
    const result = links("node:/foo/bar baz");
    expect(result.some((l) => l.text.startsWith("e:"))).toBe(false);
  });

  it("still matches an ordinary absolute path after the guard", () => {
    expect(links("See D:\\Repos\\file.txt here")).toEqual([
      { text: "D:\\Repos\\file.txt", isUrl: false },
    ]);
  });
});

describe("file:// urls (issue #252)", () => {
  it("surfaces the whole span as a local Path link, not a broken e:/ path", () => {
    const line = "file:///D:/Workspaces/example-project/marketing/marketing.html";
    const result = links(line);
    expect(result).toEqual([
      { text: "D:\\Workspaces\\example-project\\marketing\\marketing.html", isUrl: false },
    ]);
    expect(result[0].text).not.toContain("e:\\\\");
  });

  it("resolves a file url in a sentence", () => {
    expect(links("Report at file:///D:/Repos/x.html now")).toEqual([
      { text: "D:\\Repos\\x.html", isUrl: false },
    ]);
  });

  it("strips a trailing period on a file url", () => {
    expect(links("See file:///D:/Repos/x.html.")).toEqual([
      { text: "D:\\Repos\\x.html", isUrl: false },
    ]);
  });

  it("decodes percent-encoded spaces", () => {
    expect(links("file:///D:/My%20Docs/report.md")).toEqual([
      { text: "D:\\My Docs\\report.md", isUrl: false },
    ]);
  });

  it.each([
    ["file:///D:/Repos/x.html", "D:\\Repos\\x.html"],
    ["file:///C:/a/b.txt", "C:\\a\\b.txt"],
  ])("tryConvertFileUrlToLocalPath(%s)", (url, expected) => {
    expect(tryConvertFileUrlToLocalPath(url)).toBe(expected);
  });

  it.each(["", "https://example.com/x", "not a url"])(
    "tryConvertFileUrlToLocalPath returns null for %s",
    (input) => {
      expect(tryConvertFileUrlToLocalPath(input)).toBeNull();
    },
  );
});

describe("edge cases", () => {
  it.each(["", "   "])("returns [] for empty/whitespace body: %s", (body) => {
    expect(extractLinks(body)).toEqual([]);
  });

  it("returns [] for null/undefined", () => {
    expect(extractLinks(null)).toEqual([]);
    expect(extractLinks(undefined)).toEqual([]);
  });
});
