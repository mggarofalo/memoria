import picomatch from "picomatch";

/**
 * The names a document path may use. These rules are lexical and identical on
 * every platform, so a path accepted on one host is also valid on the others
 * and a corpus can move between them.
 */

/**
 * Directories QMD's scan never enters (`reindexCollection` in the SDK's
 * store.js). A document written under one is accepted and then never indexed.
 * `test/path-rules.test.ts` pins this list to the installed SDK.
 */
export const QMD_EXCLUDED_DIRS: readonly string[] = ["node_modules", ".git", ".cache", "vendor", "dist", "build"];

/** Longest segment that fits on NTFS (255 UTF-16 units), ext4 and APFS (255 bytes). */
const MAX_SEGMENT_BYTES = 255;

/**
 * Far deeper than any corpus needs. It also bounds the cost of matching a glob
 * against a request's path, which grows with how many places a pattern's literal
 * parts can land.
 */
export const MAX_PATH_SEGMENTS = 32;

/** Windows device names. The superscript digits are devices too. */
const RESERVED_NAME = /^(?:CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³]|CONIN\$|CONOUT\$|CLOCK\$)$/i;

/**
 * Case folding wide enough for NTFS and APFS. It joins some names those
 * filesystems keep apart (`ß` and `ss`), which costs at most a refused alias.
 */
export function foldName(name: string): string {
  return name.normalize("NFC").toUpperCase().toLowerCase();
}

/** Windows ignores everything after the first dot, and trailing spaces, when it matches a device name. */
function isReservedName(segment: string): boolean {
  const stem = segment.split(".", 1)[0]!.trimEnd();
  return RESERVED_NAME.test(stem);
}

interface SegmentRule {
  breaks: (segment: string) => boolean;
  message: string;
}

const SEGMENT_RULES: readonly SegmentRule[] = [
  { breaks: (s) => s === "" || s === "." || s === "..", message: "path must not contain empty, '.' or '..' segments" },
  // Windows strips trailing dots and spaces when it opens a file, so `.. /` reaches the filesystem as `../`.
  { breaks: (s) => s.endsWith("."), message: "path segments must not end in a dot" },
  { breaks: (s) => /^\s|\s$/u.test(s), message: "path segments must not start or end with whitespace" },
  // Cc is control characters, Cf is format characters including bidi overrides, Cs is lone surrogates.
  { breaks: (s) => /[\p{Cc}\p{Cf}\p{Cs}\p{Zl}\p{Zp}]/u.test(s), message: "path must not contain control, format or bidirectional characters" },
  // A colon also addresses an NTFS alternate data stream.
  { breaks: (s) => /[<>:"|?*]/.test(s), message: 'path must not contain any of < > : " | ? *' },
  { breaks: isReservedName, message: "path must not use a Windows reserved device name" },
  // `hidden~1.md` is another name for `.hidden.md` on a volume with 8.3 names, which would get past the hidden-file rule.
  { breaks: (s) => /~\d+(?:\.|$)/.test(s), message: "path segments must not have the shape of an 8.3 short name (~N)" },
  // NFD and NFC spellings are two files on NTFS and one on APFS.
  { breaks: (s) => s !== s.normalize("NFC"), message: "path must be in Unicode normalization form NFC" },
  { breaks: (s) => Buffer.byteLength(s, "utf8") > MAX_SEGMENT_BYTES, message: `path segments must not exceed ${MAX_SEGMENT_BYTES} bytes` },
];

/** Why this path segment is refused, or undefined when it is acceptable. */
export function segmentProblem(segment: string): string | undefined {
  return SEGMENT_RULES.find((rule) => rule.breaks(segment))?.message;
}

/** Extglob groups can backtrack exponentially. A leading `!` inverts a rule, which an ignore list has no use for. */
const UNSUPPORTED_IGNORE_SYNTAX = /[@!+*?]\(|^!/;
/** Each wildcard adds a power to the worst-case regex cost, and the subject is a request's path. See the timing test. */
const MAX_IGNORE_WILDCARDS = 3;

/**
 * The first ignore pattern this sidecar will not evaluate, or undefined. The
 * patterns come only from QMD's operator-written collection config, but each
 * compiles to a regex that runs against a caller-supplied path, so a pattern
 * that could backtrack badly is refused rather than run or skipped. Skipping one
 * would bring back the 202 for a document that is never indexed.
 */
export function unsupportedIgnorePattern(patterns: readonly string[]): string | undefined {
  return patterns.find((p) => UNSUPPORTED_IGNORE_SYNTAX.test(p) || (p.match(/\*+/g)?.length ?? 0) > MAX_IGNORE_WILDCARDS);
}

/**
 * The same test fast-glob applies when deciding not to enter a directory: the
 * pattern ends in a globstar, or its last part has no wildcard. `a/*` hides the
 * files directly in `a` but not what is below `a/b`.
 */
function skipsMatchingDirectories(pattern: string): boolean {
  return pattern.endsWith("/**") || !/[*?[\]{}()!]/.test(pattern.slice(pattern.lastIndexOf("/") + 1));
}

/**
 * Whether QMD's scan would skip this path because of an ignore pattern. The scan
 * passes the patterns to fast-glob, so that is the behaviour to match, including
 * skipping everything below a matching directory. A test compares this with
 * fast-glob itself.
 */
export function matchesIgnorePattern(segments: readonly string[], patterns: readonly string[]): boolean {
  return patterns.map((p) => p.replace(/^\.\//, "")).some((pattern) => {
    const matches = picomatch(pattern, { dot: true, posix: true });
    if (matches(segments.join("/"))) return true;
    return skipsMatchingDirectories(pattern) && segments.slice(0, -1).some((_, i) => matches(segments.slice(0, i + 1).join("/")));
  });
}

const EXCLUDED_FOLDED = new Set(QMD_EXCLUDED_DIRS.map(foldName));

/**
 * Why QMD would never index this path even though the collection's glob
 * matches it, or undefined. QMD's own check is case-sensitive, but on NTFS and
 * APFS `Vendor/` can be the same directory as `vendor/`, so this one is not.
 */
export function unindexableProblem(segments: readonly string[]): string | undefined {
  if (segments.some((s) => s.startsWith("."))) return "QMD does not index hidden files or directories";
  if (segments.some((s) => EXCLUDED_FOLDED.has(foldName(s)))) return `QMD does not index paths under ${QMD_EXCLUDED_DIRS.join(", ")}`;
  return undefined;
}
