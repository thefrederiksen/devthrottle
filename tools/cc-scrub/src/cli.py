#!/usr/bin/env python
"""cc-scrub - find denylisted text in screenshots and destroy it.

Pure Python. Every dependency is a pip wheel. The text recognizer is the
operating system's own, reached through the single seam in ocr_backend.py.
No external executable is installed, required or looked for - in particular
this tool never calls tesseract - and it launches no subprocess at all.

Exit codes:
    0   work done and proved (or --check-only found nothing)
    1   denylist hits remain readable, or --check-only found hits
    2   broken instrument / usage error (OCR unavailable, unreadable image,
        zero text read from an image, bad arguments)

All output is ASCII only.
"""

import argparse
import math
import os
import stat as stat_module
import sys
import tempfile

try:
    from PIL import Image, ImageDraw, ImageFilter
except ImportError as exc:
    sys.stderr.write("FATAL: Pillow is not installed (%s).\n" % exc)
    sys.stderr.write("Fix: python -m pip install Pillow\n")
    sys.exit(2)

from .ocr_backend import ScrubError, get_backend


IMAGE_SUFFIXES = (".png", ".jpg", ".jpeg", ".bmp")
SCRUBBED_MARK = "-scrubbed"

# A ceiling on the pixels ONE scaled read may ask for, in megapixels.
#
# Megapixels here means 1,000,000 pixels, the decimal unit the flag name and
# the documentation say. It was implemented as 1024*1024 - mebipixels - so a
# documented 192 megapixel ceiling actually admitted 201,326,592 pixels, and
# a genuine 200 megapixel image passed under it.
#
# The engine's limit on the longest side is not a limit on area: a square
# input just under that limit, read at scale 3, is about 268 megapixels -
# roughly 800 MB of RGB data for the scaled copy alone, before the resize's
# own working set and before the engine takes its copy. That allocation is
# not a read the tool can perform; it is a way to bring the machine down.
#
# The default allows the sizes people actually scrub - a 4K screenshot at
# scale 3 is 75 megapixels, a 5K one is 132 - and refuses the pathological
# case by name. It is a guard, not a measurement of this machine, so it is
# exposed as --max-megapixels for a caller who genuinely needs more and has
# the memory for it.
DEFAULT_MAX_MEGAPIXELS = 192

TOOL_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_TERMS_FILE = os.path.join(TOOL_ROOT, "terms.txt")
EXAMPLE_TERMS_FILE = os.path.join(TOOL_ROOT, "terms.example.txt")


# ----------------------------------------------------------------- text tools

def stat_or_none(path, purpose):
    """os.stat, where a genuine absence is None and an error stops the run.

    This exists because os.path.exists, isfile and isdir all return False for
    EVERY error - and in this tool False is routinely the answer that PERMITS
    an action: publish over that file, treat those two paths as different
    files, that directory is not a directory. A permission error, an I/O
    error or a race would therefore have switched a guard off silently, which
    is the one way this tool can destroy something.

    Exactly two errors are answers rather than failures. FileNotFoundError is
    a genuine absence. NotADirectoryError is what a path whose parent
    component is a file looks like, which is also a genuine absence of the
    thing named. Everything else is a broken instrument and raises.

    `purpose` is what the caller needed the answer for, so the error says why
    the run stopped and not merely that it did.
    """
    try:
        return os.stat(path)
    except (FileNotFoundError, NotADirectoryError):
        return None
    except OSError as exc:
        raise ScrubError(
            "cannot inspect %s, which is needed to %s: %s. Refusing to guess, "
            "because an unreadable path is not an answer - and here the "
            "convenient guess is the one that permits the write."
            % (path, purpose, exc))


def lstat_or_none(path, purpose):
    """os.lstat, for questions about the ENTRY rather than about its target.

    stat_or_none follows symbolic links, which is right for classification -
    is this a file, is this a directory - and wrong for "is there something
    here that a write would clobber". A DANGLING SYMLINK is the case that
    separates them: the entry plainly exists, and a following stat raises
    FileNotFoundError, so the existence question answered "absent" and a run
    with no --force replaced the link and reported success.
    """
    try:
        return os.lstat(path)
    except (FileNotFoundError, NotADirectoryError):
        return None
    except OSError as exc:
        raise ScrubError(
            "cannot inspect the directory entry %s, which is needed to %s: "
            "%s. Refusing to guess, because an unreadable path is not an "
            "answer - and here the convenient guess is the one that permits "
            "the write." % (path, purpose, exc))


def path_exists(path, purpose):
    """Does the TARGET exist? Follows links. For identity questions."""
    return stat_or_none(path, purpose) is not None


def entry_exists(path, purpose):
    """Is there a directory entry here at all? Does not follow links.

    This is the question to ask before writing to a name.
    """
    return lstat_or_none(path, purpose) is not None


def path_is_directory(path, purpose):
    info = stat_or_none(path, purpose)
    return info is not None and stat_module.S_ISDIR(info.st_mode)


def path_is_regular_file(path, purpose):
    info = stat_or_none(path, purpose)
    return info is not None and stat_module.S_ISREG(info.st_mode)


def ascii_safe(text):
    """Return text with every non-ASCII character replaced by '?'.

    Repo rule: nothing this tool prints may contain non-ASCII. OCR output
    routinely contains them, so every string that reaches stdout goes
    through here.
    """
    return text.encode("ascii", "replace").decode("ascii")


# Glyph confusion classes. OCR of small anti-aliased UI text confuses
# characters that share a shape. These classes are applied identically to
# the denylist term and to the OCR text, so matching survives the
# confusion. This is deliberate over-matching in the safe direction: the
# worst case is that a few extra pixels get blurred, and every hit is
# printed with its coordinates and its source text so it can be checked by
# eye. Turn it off with --no-fold to require an exact match.
#
# The 'l'/'t' pair is in here because it is the confusion that actually
# bites on screenshots of terminals: a URL like example.ai can come back as
# exampte.ai, because the engine misreads the l as a t.
_FOLD_CLASSES = (
    ("o", "o0"),
    ("l", "li1tj!|"),
    ("s", "s5"),
    ("z", "z2"),
    ("b", "b8"),
    ("g", "g9"),
)

_FOLD_MAP = {}
for _target, _members in _FOLD_CLASSES:
    for _ch in _members:
        _FOLD_MAP[_ch] = _target


def normalise(text, fold=True):
    """Fold glyph classes first, then drop everything that is not a-z0-9.

    The order matters, and getting it the wrong way round silently killed
    two of the six advertised folds. Two members of the l class - '!' and
    '|' - are not alphanumeric, so filtering first threw them away instead
    of folding them: an engine that read "internal" as "!nternal" produced
    "nternal" while the term produced "lnternal", and the term was missed.
    Folding first means a mapped character always becomes its
    representative, and only unmapped characters face the alphanumeric
    filter.

    Dropping the remaining non-alphanumerics is what lets a term match
    across OCR word splits and injected punctuation noise: the OCR reads
    "https : //example. ai/path/session_011" and the term
    "example.ai/path/session" still lands.
    """
    out = []
    for ch in text.lower():
        if fold and ch in _FOLD_MAP:
            out.append(_FOLD_MAP[ch])
            continue
        if ch.isascii() and (ch.isalpha() or ch.isdigit()):
            out.append(ch)
    return "".join(out)


def load_terms(path):
    """Read the denylist. One term per line; '#' starts a comment."""
    if not path_is_regular_file(path, "read the denylist"):
        raise ScrubError(
            "terms file not found: %s. The denylist is your own config and is "
            "never committed; copy %s to %s and put your terms in it."
            % (path, EXAMPLE_TERMS_FILE, DEFAULT_TERMS_FILE))
    # The boundary goes around the READ. Asking whether the path is a
    # regular file and then opening it outside any boundary leaves two ways
    # out: the file can change between the two, and its contents can be
    # invalid UTF-8, which escaped as a raw UnicodeDecodeError rather than
    # the documented exit 2.
    terms = []
    try:
        with open(path, "r", encoding="utf-8") as handle:
            for raw in handle:
                line = raw.split("#", 1)[0].strip()
                if line:
                    terms.append(line)
    except (OSError, UnicodeDecodeError) as exc:
        raise ScrubError("cannot read the terms file %s: %s" % (path, exc))
    if not terms:
        raise ScrubError("terms file %s has no terms in it" % path)
    return terms


def validate_terms(terms, fold):
    """Refuse any term with nothing left to match on after normalisation.

    A term made only of punctuation or non-ASCII normalises to the empty
    string. Such a term used to be counted in the banner and then skipped
    in silence, so a run could report "no hits against 6 terms" having
    actually checked five. The banner and the work must agree, so this is
    an error and not a warning - and it is judged under the normalisation
    that is actually in force, because folding changes which characters
    survive.
    """
    dead = [term for term in terms if not normalise(term, fold)]
    if dead:
        raise ScrubError(
            "these denylist terms have nothing left to match on after "
            "normalisation%s, so they would be counted in the banner and "
            "never checked: %s. Remove them, or write them with letters or "
            "digits."
            % (" with glyph folding on" if fold else " with --no-fold",
               ", ".join("'%s'" % ascii_safe(term) for term in dead)))


# ------------------------------------------------------------------------ ocr

class OcrPass(object):
    """One OCR read of one image at one scale."""

    def __init__(self, scale, lines, word_count):
        self.scale = scale
        self.lines = lines          # list of list of (text, rect) at scale 1
        self.word_count = word_count


def check_scales_fit(size, scales, backend, label,
                     max_megapixels=DEFAULT_MAX_MEGAPIXELS):
    """Refuse before reading if any scale is beyond the engine or the budget.

    Two separate limits, both checked at every scale including scale 1.
    Scale 1 used to be exempt, on the assumption that a native image is
    always small enough - which is not true. An oversized screenshot reached
    the engine unchecked and failed there, after the whole file had been
    decoded into memory, instead of failing here deterministically and by
    name.

    The side limit is the engine's own. The area limit is ours, because
    passing the side limit says nothing about the allocation: see
    DEFAULT_MAX_MEGAPIXELS.
    """
    width, height = size
    budget = max_megapixels * 1000000
    for scale in scales:
        scaled_width, scaled_height = width * scale, height * scale
        if max(scaled_width, scaled_height) > backend.max_image_dimension:
            raise ScrubError(
                "%s is %dx%d; at scale %d that is %dx%d, over the %s limit of "
                "%d px on the longest side. Use a smaller --scales value, or "
                "a smaller image."
                % (label, width, height, scale, scaled_width, scaled_height,
                   backend.name, backend.max_image_dimension))
        pixels = scaled_width * scaled_height
        if pixels > budget:
            # The exact count leads, and the megapixel figure carries enough
            # precision that a value just over the cap cannot print AS the
            # cap: 192,020,000 pixels used to render as "192.0 megapixels,
            # over the 192 megapixel budget", which reads as nonsense.
            raise ScrubError(
                "%s is %dx%d; at scale %d that is %dx%d, %s pixels (%.3f "
                "megapixels), over the %d megapixel budget for one read. Use "
                "a smaller --scales value, a smaller image, or raise "
                "--max-megapixels if this machine has the memory for it."
                % (label, width, height, scale, scaled_width, scaled_height,
                   "{:,}".format(pixels), pixels / 1000000.0, max_megapixels))


def ocr_image(image, scale, lang, backend,
              max_megapixels=DEFAULT_MAX_MEGAPIXELS):
    """OCR image upscaled by 'scale'; return an OcrPass with rects at scale 1.

    Small grey UI text is invisible to the engine at native resolution, so
    the image is enlarged with LANCZOS before the read and every rectangle
    is divided back down afterwards.
    """
    check_scales_fit(image.size, [scale], backend, "the image", max_megapixels)
    if scale != 1:
        width, height = image.size[0] * scale, image.size[1] * scale
        try:
            scaled = image.resize((width, height), Image.LANCZOS)
        except (MemoryError, OSError, ValueError) as exc:
            raise ScrubError(
                "cannot enlarge the image to %dx%d for the scale %d read: %s"
                % (width, height, scale, exc))
    else:
        scaled = image

    try:
        words = backend.recognize(scaled, lang)
    except ScrubError as exc:
        raise ScrubError("at scale %d: %s" % (scale, exc))
    except MemoryError as exc:
        raise ScrubError(
            "ran out of memory in the scale %d read of a %dx%d image: %s. Use "
            "a smaller --scales value or a smaller image."
            % (scale, scaled.size[0], scaled.size[1], exc))

    lines = []
    current_line = None
    current_words = []
    words_seen = 0
    for word in words:
        if current_line is not None and word["line"] != current_line:
            if current_words:
                lines.append(current_words)
            current_words = []
        current_line = word["line"]
        box = (word["x"] / float(scale),
               word["y"] / float(scale),
               word["w"] / float(scale),
               word["h"] / float(scale))
        current_words.append((word["text"], box))
        words_seen += 1
    if current_words:
        lines.append(current_words)
    return OcrPass(scale, lines, words_seen)


# -------------------------------------------------------------------- matching

class Hit(object):
    def __init__(self, term, box, scale, source_text):
        self.term = term
        self.box = box                    # (x0, y0, x1, y1) floats, scale 1
        self.scales = [scale]
        self.source_text = source_text

    def as_rect(self):
        x0, y0, x1, y1 = self.box
        return (int(x0), int(y0), int(x1 - x0), int(y1 - y0))


def find_hits(ocr_pass, terms, fold):
    """Match every term against every OCR line of one pass.

    Each line is concatenated into one normalised string with a map back to
    the word that produced each character, so a term split across adjacent
    OCR words still matches and the covering words' rectangles are unioned
    into a single hit rectangle.
    """
    hits = []
    for words in ocr_pass.lines:
        joined = []
        owner = []
        for index, (text, _box) in enumerate(words):
            piece = normalise(text, fold)
            joined.append(piece)
            owner.extend([index] * len(piece))
        line_norm = "".join(joined)
        if not line_norm:
            continue
        line_text = " ".join(text for text, _box in words)

        for term in terms:
            needle = normalise(term, fold)
            if not needle:
                raise ScrubError(
                    "term '%s' normalises to nothing and cannot be matched; "
                    "validate_terms must reject it before any image is read."
                    % ascii_safe(term))
            start = line_norm.find(needle)
            while start != -1:
                covered = sorted(set(owner[start:start + len(needle)]))
                boxes = [words[i][1] for i in covered]
                x0 = min(b[0] for b in boxes)
                y0 = min(b[1] for b in boxes)
                x1 = max(b[0] + b[2] for b in boxes)
                y1 = max(b[1] + b[3] for b in boxes)
                hits.append(Hit(term, (x0, y0, x1, y1),
                                ocr_pass.scale, line_text))
                start = line_norm.find(needle, start + 1)
    return hits


def collect_hits(image, terms, scales, lang, fold, backend,
                 max_megapixels=DEFAULT_MAX_MEGAPIXELS):
    """Run every scale, union the hits, merge overlapping rectangles.

    Every scale always runs - this is not a fallback chain. Different
    scales read the same small text differently, and the union is what
    makes detection reliable.
    """
    passes = []
    for scale in scales:
        passes.append(ocr_image(image, scale, lang, backend, max_megapixels))

    total_words = sum(p.word_count for p in passes)
    raw = []
    for ocr_pass in passes:
        raw.extend(find_hits(ocr_pass, terms, fold))

    merged = merge_hits(raw)
    return merged, passes, total_words


def merge_hits(hits):
    """Merge hits of the same term whose rectangles overlap or touch."""
    merged = []
    for hit in hits:
        for other in merged:
            if other.term == hit.term and _overlaps(other.box, hit.box):
                other.box = (min(other.box[0], hit.box[0]),
                             min(other.box[1], hit.box[1]),
                             max(other.box[2], hit.box[2]),
                             max(other.box[3], hit.box[3]))
                for scale in hit.scales:
                    if scale not in other.scales:
                        other.scales.append(scale)
                if len(hit.source_text) > len(other.source_text):
                    other.source_text = hit.source_text
                break
        else:
            merged.append(hit)
    for hit in merged:
        hit.scales.sort()
    merged.sort(key=lambda h: (h.box[1], h.box[0]))
    return merged


def _overlaps(a, b):
    return not (a[2] < b[0] or b[2] < a[0] or a[3] < b[1] or b[3] < a[1])


# -------------------------------------------------------------------- redaction

def _pad_box(box, pad, size):
    """Grow the float rectangle outwards to whole pixels, then pad it.

    Every edge moves away from the text: floor the left and top, ceil the
    right and bottom. Rounding the far edges instead used to lose the last
    column or row of pixels whenever the rectangle ended on a fraction
    below .5 - invisible at the default padding, and a strip of readable
    text left behind at --pad 0.
    """
    x0 = max(0, int(math.floor(box[0])) - pad)
    y0 = max(0, int(math.floor(box[1])) - pad)
    x1 = min(size[0], int(math.ceil(box[2])) + pad)
    y1 = min(size[1], int(math.ceil(box[3])) + pad)
    return (x0, y0, x1, y1)


def _median_colour(region):
    """Per-channel median of the region, read off the channel histograms.

    The median of a text region is its background colour, so a patch filled
    with it reads as a deliberate redaction rather than a black bar.
    """
    rgb = region.convert("RGB")
    total = rgb.size[0] * rgb.size[1]
    colour = []
    for channel in rgb.split():
        histogram = channel.histogram()
        seen = 0
        for value, count in enumerate(histogram):
            seen += count
            if seen * 2 >= total:
                colour.append(value)
                break
    return tuple(colour)


def _destroy(region):
    """Mosaic the region down to a handful of cells, then blur it smooth.

    What this does, and what it does not do, stated exactly - because this
    is the sentence someone leans on when deciding whether to publish.

    The original pixel values are not present in the output. Downsampling to
    a handful of cells discards them, and the Gaussian pass on top only
    removes the blocky edges.

    What remains is a low-frequency AVERAGE of the covered region. That is
    not nothing, and it is not proof against a determined recovery attempt:
    recovering text from a mosaic is a documented attack when the alphabet is
    small, and a Gaussian blur is a linear low-pass filter, not an eraser.

    For anything leaving the organisation, --patch is the mode to use. It
    paints an opaque solid colour over the region and leaves no signal from
    the covered pixels at all.
    """
    width, height = region.size
    cells_x = max(1, width // 14)
    cells_y = max(1, height // 6)
    mosaic = region.resize((cells_x, cells_y), Image.BILINEAR)
    mosaic = mosaic.resize((width, height), Image.NEAREST)
    radius = max(6.0, height / 2.0)
    return mosaic.filter(ImageFilter.GaussianBlur(radius=radius))


def redact(image, hits, pad, mode):
    """Return a new image with every hit rectangle destroyed."""
    out = image.copy()
    for hit in hits:
        box = _pad_box(hit.box, pad, out.size)
        if box[2] <= box[0] or box[3] <= box[1]:
            raise ScrubError("hit for term '%s' produced an empty rectangle %s"
                             % (ascii_safe(hit.term), box))
        region = out.crop(box)
        if mode == "patch":
            # A PLAIN rectangle, not a rounded one. The contract of this mode
            # is full coverage of the hit rectangle, and rounded corners are
            # a cosmetic preference that broke it: the corners were never
            # painted, so the mode documented as leaving no signal from the
            # covered pixels was the only one that provably left ORIGINAL
            # pixels inside the rectangle. Coverage wins over the shape.
            colour = _median_colour(region)
            draw = ImageDraw.Draw(out)
            draw.rectangle(box, fill=colour)
        else:
            out.paste(_destroy(region), box)
    return out


# ------------------------------------------------------------------- reporting

def describe(hit):
    x, y, w, h = hit.as_rect()
    return ("term='%s' rect=(x=%d y=%d w=%d h=%d) scales=%s ocr_line='%s'"
            % (ascii_safe(hit.term), x, y, w, h,
               ",".join(str(s) for s in hit.scales),
               ascii_safe(hit.source_text)))


# ----------------------------------------------------------------- per image

def is_same_file(first, second):
    """True when the two paths name the same file on disk.

    Comparing os.path.abspath strings is not enough and the difference is
    not academic. Windows paths are case-insensitive, so 'shot.png' and
    'SHOT.PNG' are one file whose absolute paths compare unequal; the same
    goes for a hard link, a symbolic link, a junction and an 8.3 short name.
    Every one of those would have slipped past a string comparison and let
    the tool overwrite the very input it says it refuses to touch - which
    would destroy the only unredacted copy.

    os.path.samefile asks the operating system, which is the only answer
    that covers all of those. It needs both paths to exist, so a destination
    that has not been created yet is compared canonically instead. That is a
    precondition, not a degraded mode: the two branches answer the same
    question about two different situations, and neither one guesses.

    Existence is established with stat_or_none and not with os.path.exists.
    An errored exists() answers False, which drops straight through to the
    canonical comparison - and that comparison is lexical, so on a
    case-insensitive POSIX volume it answers "different files" and this
    guard, the one standing between a run and the only unredacted copy of
    the input, is switched off by an unreadable path.
    """
    first_there = path_exists(first, "tell whether it is the input image")
    second_there = path_exists(second, "tell whether it is the input image")

    if first_there and second_there:
        try:
            return os.path.samefile(first, second)
        except OSError as exc:
            raise ScrubError(
                "cannot tell whether %s and %s are the same file: %s. "
                "Refusing to write, because writing would risk destroying "
                "the input." % (first, second, exc))

    if first_there != second_there:
        # One is there and the other genuinely is not, so they cannot be one
        # file. No lexical guesswork needed or wanted.
        return False

    # Neither exists. Nothing on disk can be asked, and a path that does not
    # exist cannot be an alias of a file that does, so the canonical
    # spellings are all there is. In this tool's own call the input always
    # exists, so this branch is only ever reached by a direct caller.
    return (os.path.normcase(os.path.realpath(first))
            == os.path.normcase(os.path.realpath(second)))


def process_image(path, out_path, terms, args, backend):
    """Scrub one image. Returns True if the image ends up proved clean."""
    print("")
    print("IMAGE %s" % path)
    try:
        image = Image.open(path)
    except Exception as exc:
        raise ScrubError("cannot read image %s: %s" % (path, exc))

    # Size comes off the header, before the pixels are decoded. An image too
    # big for the engine is refused here rather than after megabytes have
    # been read into memory for a read that could never have happened.
    check_scales_fit(image.size, args.scales, backend, path,
                     args.max_megapixels)

    try:
        image.load()
        image = image.convert("RGB")
    except Exception as exc:
        raise ScrubError("cannot decode image %s: %s" % (path, exc))
    print("  size %dx%d  scales %s  fold %s"
          % (image.size[0], image.size[1],
             ",".join(str(s) for s in args.scales),
             "on" if not args.no_fold else "off"))

    hits, passes, total_words = collect_hits(
        image, terms, args.scales, args.lang, not args.no_fold, backend,
        args.max_megapixels)

    for ocr_pass in passes:
        print("  ocr scale %d: %d words in %d lines"
              % (ocr_pass.scale, ocr_pass.word_count, len(ocr_pass.lines)))

    # Broken instrument, not a clean image. An empty OCR result can never
    # be read as "there was nothing sensitive here".
    if total_words == 0:
        raise ScrubError(
            "OCR read ZERO words from %s across scales %s. That is a broken "
            "read, not a clean image. Refusing to call this scrubbed."
            % (path, ",".join(str(s) for s in args.scales)))

    print("  HITS: %d" % len(hits))
    for hit in hits:
        print("    %s" % describe(hit))

    if args.check_only:
        if hits:
            print("  CHECK-ONLY: %d hit(s) present, image NOT modified." % len(hits))
            return False
        print("  CHECK-ONLY: no hits against %d terms over %d OCR words."
              % (len(terms), total_words))
        return True

    if not hits:
        print("  Nothing to redact. No output written (input left untouched).")
        return True

    scrubbed = redact(image, hits, args.pad, args.mode)

    # The input is never a legal destination, and --force does not change
    # that: it would destroy the only unredacted copy.
    if is_same_file(out_path, path):
        raise ScrubError("refusing to overwrite the input image %s (the output "
                         "path %s is the same file)" % (path, out_path))

    # An existing output may be one that has already been verified. Replacing
    # it is a decision the caller makes on purpose, never a side effect.
    if entry_exists(out_path, "tell whether an output is already there") \
            and not args.force:
        raise ScrubError(
            "output %s already exists. Refusing to replace it - it may be an "
            "output that has already passed verification. Delete it, point -o "
            "somewhere else, or pass --force to replace it." % out_path)

    # Write a candidate beside the destination, verify THAT file, and publish
    # it to the authoritative name only once it has passed.
    #
    # Writing straight to the final name and verifying afterwards means a run
    # that fails verification still leaves a file under the *-scrubbed name -
    # a name whose whole meaning is "this was checked" - and worse, that file
    # has already replaced whatever verified output was there before. The
    # temporary lives in the same directory so the publish is one atomic
    # rename on the same volume.
    out_dir = os.path.dirname(os.path.abspath(out_path))
    try:
        handle, temp_path = tempfile.mkstemp(
            prefix=os.path.basename(out_path) + ".", suffix=".tmp", dir=out_dir)
        os.close(handle)
    except OSError as exc:
        raise ScrubError("cannot create a temporary file in %s: %s"
                         % (out_dir, exc))
    published = False
    try:
        try:
            scrubbed.save(temp_path, format="PNG")
        except Exception as exc:
            raise ScrubError("cannot write the candidate output %s: %s"
                             % (temp_path, exc))
        print("  CANDIDATE %s (mode=%s pad=%d)" % (temp_path, args.mode, args.pad))

        # Mandatory verify pass: re-OCR the CANDIDATE, from disk.
        print("  VERIFY: re-reading %s" % temp_path)
        try:
            verify_image = Image.open(temp_path)
            verify_image.load()
        except Exception as exc:
            raise ScrubError("cannot read back the candidate output %s: %s"
                             % (temp_path, exc))
        try:
            verify_image = verify_image.convert("RGB")
        except Exception as exc:
            raise ScrubError("cannot decode the candidate output %s: %s"
                             % (temp_path, exc))
        remaining, verify_passes, verify_words = collect_hits(
            verify_image, terms, args.scales, args.lang, not args.no_fold,
            backend, args.max_megapixels)
        verify_image.close()
        for ocr_pass in verify_passes:
            print("    verify scale %d: %d words in %d lines"
                  % (ocr_pass.scale, ocr_pass.word_count, len(ocr_pass.lines)))

        # The verify instrument must itself prove it can read. A verify pass
        # that reads nothing proves nothing.
        if verify_words == 0:
            raise ScrubError(
                "verify OCR read ZERO words from the candidate for %s. The "
                "verify instrument is broken; the result is not proved and "
                "nothing was published." % out_path)

        if remaining:
            print("  VERIFY FAILED: %d denylist hit(s) still readable in the "
                  "candidate:" % len(remaining))
            for hit in remaining:
                print("    %s" % describe(hit))
            print("  NOT PUBLISHED: %s was not written." % out_path)
            return False

        # The existence check above ran BEFORE the candidate was written, and
        # everything since - the write, three OCR passes, the matching - is a
        # window in which something else can put a file at the destination. A
        # correct answer at inspection time does not make this write safe, so
        # the write itself has to carry the guarantee.
        #
        # os.link is the portable no-clobber publish: it raises
        # FileExistsError rather than replacing. os.replace is the opposite -
        # it silently overwrites - so it is used ONLY when --force has asked
        # for exactly that.
        if args.force:
            try:
                os.replace(temp_path, out_path)
            except OSError as exc:
                raise ScrubError(
                    "cannot publish the verified candidate %s to %s: %s"
                    % (temp_path, out_path, exc))
        else:
            try:
                os.link(temp_path, out_path)
            except FileExistsError:
                raise ScrubError(
                    "%s appeared while this image was being verified. "
                    "Refusing to replace it - nothing was published. Re-run, "
                    "or pass --force if you mean to replace it." % out_path)
            except OSError as exc:
                # A volume with no hard links cannot give a no-clobber
                # publish, and there is no fallback: falling back to
                # os.replace would silently destroy whatever is at the
                # destination, which is the entire thing this branch exists
                # to prevent. It stops here, loudly.
                raise ScrubError(
                    "cannot publish %s without replacing whatever is there: "
                    "%s. The no-clobber publish is a hard link, and this "
                    "volume does not support one. There is no fallback - "
                    "replacing is only ever done when --force asks for it. "
                    "Pass --force if you mean to replace, or write the output "
                    "to a volume that supports hard links."
                    % (out_path, exc))
            # The destination and the candidate are now two names for one
            # file. Dropping the candidate name leaves exactly the published
            # one behind.
            try:
                os.remove(temp_path)
            except OSError as exc:
                sys.stderr.write(
                    "WARNING: published %s but could not remove the candidate "
                    "name %s: %s\n" % (out_path, temp_path, exc))
        published = True
        print("  WROTE %s (mode=%s pad=%d)" % (out_path, args.mode, args.pad))
        print("  VERIFY PASSED: %d hit(s) found, %d region(s) redacted, verify "
              "OCR read %d words in the output and 0 denylist hit(s) remain."
              % (len(hits), len(hits), verify_words))
        return True
    finally:
        # No existence check here, deliberately. mkstemp created the file and
        # `published` already records whether os.replace consumed it, so an
        # exists() call would add nothing except one more place for an error
        # to become a boolean. The removal is simply attempted.
        #
        # This is also the one place in the tool where a failure must NOT
        # become the outcome. An exception is usually in flight - the verify
        # failure that stopped the publish - and raising from a finally would
        # replace the real reason with a housekeeping complaint. So it is
        # reported loudly and nothing is gated on it. That is why this differs
        # from the case probe, where a failed cleanup IS fatal: there, no
        # exception is in flight and the function's whole job is to return a
        # verdict about a directory that has just refused to behave.
        if not published:
            try:
                os.remove(temp_path)
            except FileNotFoundError:
                pass
            except OSError as exc:
                sys.stderr.write(
                    "WARNING: could not remove the unpublished candidate %s: "
                    "%s\n" % (temp_path, exc))


# ----------------------------------------------------------------------- main

def gather_inputs(target):
    if path_is_regular_file(target, "read it as an image"):
        return [target]
    if path_is_directory(target, "read it as a folder of images"):
        found = []
        try:
            names = sorted(os.listdir(target))
        except OSError as exc:
            raise ScrubError("cannot list the directory %s: %s"
                             % (target, exc))
        for name in names:
            stem, suffix = os.path.splitext(name)
            if suffix.lower() in IMAGE_SUFFIXES and not stem.endswith(SCRUBBED_MARK):
                found.append(os.path.join(target, name))
        if not found:
            raise ScrubError("no images found in directory %s" % target)
        return found
    raise ScrubError("no such file or directory: %s" % target)


def _make_directory(path):
    try:
        os.makedirs(path)
    except OSError as exc:
        raise ScrubError("cannot create the output directory %s: %s"
                         % (path, exc))


def output_path_for(source, out_option, many):
    """Where one input's output goes.

    With no -o, beside the input as <stem>-scrubbed.png. With -o naming a
    directory, or with more than one input, into that directory under the
    same name. With -o naming a file and exactly one input, that file
    verbatim - the name is the caller's, suffix and all.
    """
    stem, _suffix = os.path.splitext(os.path.basename(source))
    default_name = stem + SCRUBBED_MARK + ".png"
    if not out_option:
        return os.path.join(os.path.dirname(os.path.abspath(source)), default_name)
    out_option_is_directory = path_is_directory(
        out_option, "decide whether -o names a folder or a file")
    if many or out_option_is_directory:
        if not out_option_is_directory:
            _make_directory(out_option)
        return os.path.join(out_option, default_name)
    parent = os.path.dirname(os.path.abspath(out_option))
    if parent and not path_is_directory(parent,
                                        "create the output folder"):
        _make_directory(parent)
    return out_option


def directory_is_case_insensitive(directory):
    """Ask the directory itself whether two spellings are one file.

    A probe file is created under a deliberately mixed-case name, the same
    name is looked up with its case swapped, and the filesystem's answer is
    taken. The probe is always removed again.

    This exists because os.path.normcase is the HOST's rule, not the
    destination's. On Windows it lower-cases and on POSIX it is the identity
    function, so a lexical comparison silently stops detecting anything on a
    case-insensitive volume mounted under a POSIX host - which is the normal
    state of a Mac. The question is about the volume the outputs land on, so
    it is asked there.

    It is not answerable without touching the disk, and it is not guessed: a
    probe that cannot be created or cannot be read back is an error, never
    an assumption of case sensitivity.

    This covers case aliasing, which is the aliasing that output names built
    from input stems actually run into. It is not a general test for every
    way a filesystem can make two names one file.
    """
    try:
        handle, probe = tempfile.mkstemp(prefix="ccScrubCase", suffix=".Probe",
                                         dir=directory)
        os.close(handle)
    except OSError as exc:
        raise ScrubError(
            "cannot probe the output directory %s to find out whether it "
            "treats two spellings of a name as one file: %s. Refusing to "
            "guess, because guessing wrong lets one output silently replace "
            "another." % (directory, exc))
    # os.path.exists is not usable here. It swallows every OSError and
    # returns False, and False is the answer that switches the
    # output-collision check OFF - so a permission error, an I/O error or a
    # race read as "case-sensitive" and let one output overwrite another.
    # That is the failure this whole check exists to prevent, arriving
    # through the check itself.
    #
    # Exactly one error is an answer: FileNotFoundError, which is what a
    # case-sensitive volume genuinely looks like. Every other error is a
    # broken instrument and stops the run.
    answer = None
    read_error = None
    try:
        swapped = os.path.join(directory,
                               os.path.basename(probe).swapcase())
        try:
            os.stat(swapped)
        except FileNotFoundError:
            answer = False
        else:
            answer = os.path.samefile(swapped, probe)
    except OSError as exc:
        read_error = exc

    remove_error = None
    try:
        os.remove(probe)
    except OSError as exc:
        remove_error = exc

    if read_error is not None:
        raise ScrubError(
            "cannot read back the case probe %s: %s. Refusing to guess, "
            "because guessing case-sensitive would switch the "
            "output-collision check off and let one result overwrite "
            "another.%s"
            % (probe, read_error,
               "" if remove_error is None else
               " The probe could not be removed either (%s) and is still "
               "there." % remove_error))
    if remove_error is not None:
        raise ScrubError(
            "the case probe %s could not be removed: %s. Refusing to return a "
            "verdict from a directory that is not behaving as this check "
            "assumes." % (probe, remove_error))
    return answer


def plan_outputs(sources, out_option):
    """Map every input to its output up front, and refuse any collision.

    Output names are built from the input's STEM, so shot.png and shot.jpg
    both want shot-scrubbed.png - and on a filesystem that treats two
    spellings as one file, so do Shot.png and shot.jpg. Left alone, the
    second result silently overwrites the first and the summary reports two
    images cleaned when only one survived. Every destination is therefore
    worked out before any image is read, and a collision stops the run
    instead of destroying a result.

    Whether two spellings are one file is decided by the destination
    directory, asked once per directory - never by the host's normcase,
    which is the identity function on POSIX and would make this check do
    nothing at all on a case-insensitive Mac volume.
    """
    many = len(sources) > 1
    plan = []
    claimed = {}
    insensitive = {}
    for source in sources:
        destination = output_path_for(source, out_option, many)
        directory = os.path.dirname(os.path.abspath(destination))
        if directory not in insensitive:
            insensitive[directory] = directory_is_case_insensitive(directory)
        key = os.path.abspath(destination)
        if insensitive[directory]:
            key = key.lower()
        if key in claimed:
            raise ScrubError(
                "%s and %s would both be written to %s. Rename one input, "
                "give -o separate directories, or scrub them in separate "
                "runs." % (claimed[key], source, destination))
        claimed[key] = source
        plan.append((source, destination))
    return plan


def parse_scales(text):
    scales = []
    for piece in text.split(","):
        piece = piece.strip()
        if not piece:
            continue
        if not piece.isdigit() or int(piece) < 1:
            raise ScrubError("--scales takes positive whole numbers, got '%s'"
                             % ascii_safe(piece))
        value = int(piece)
        if value not in scales:
            scales.append(value)
    if not scales:
        raise ScrubError("--scales is empty")
    return sorted(scales)


def build_parser():
    parser = argparse.ArgumentParser(
        prog="cc-scrub",
        description="Blur denylisted text out of screenshots (pure Python).")
    parser.add_argument("target", help="image file or directory of images")
    parser.add_argument("-o", "--out",
                        help="output file (single input) or directory")
    parser.add_argument("--terms-file", default=DEFAULT_TERMS_FILE,
                        help="denylist file, one term per line")
    parser.add_argument("--check-only", action="store_true",
                        help="report hits and coordinates, change nothing, "
                             "exit 1 if any hit")
    parser.add_argument("--patch", dest="mode", action="store_const",
                        const="patch",
                        help="paint an opaque rounded patch instead of blurring")
    parser.add_argument("--blur", dest="mode", action="store_const",
                        const="blur", help="mosaic and blur the region (default)")
    parser.add_argument("--force", action="store_true",
                        help="replace an existing output file (never the "
                             "input, which is always refused)")
    parser.add_argument("--pad", type=int, default=4,
                        help="pixels of padding around each hit (default 4)")
    parser.add_argument("--max-megapixels", type=int,
                        default=DEFAULT_MAX_MEGAPIXELS,
                        help="ceiling on the pixels one scaled read may ask "
                             "for, in megapixels (default %d)"
                             % DEFAULT_MAX_MEGAPIXELS)
    parser.add_argument("--scales", default="1,2,3",
                        help="OCR upscale factors, comma separated "
                             "(default 1,2,3)")
    parser.add_argument("--lang", default="en",
                        help="OCR language tag (default en)")
    parser.add_argument("--no-fold", action="store_true",
                        help="disable OCR glyph-confusion folding and require "
                             "an exact normalised match")
    parser.set_defaults(mode="blur")
    return parser


def main(argv):
    parser = build_parser()
    args = parser.parse_args(argv)

    try:
        args.scales = parse_scales(args.scales)
        if args.pad < 0:
            raise ScrubError("--pad cannot be negative")
        if args.max_megapixels < 1:
            raise ScrubError("--max-megapixels must be at least 1")
        terms = load_terms(args.terms_file)
        validate_terms(terms, not args.no_fold)
        sources = gather_inputs(args.target)
        plan = ([(source, None) for source in sources] if args.check_only
                else plan_outputs(sources, args.out))
        backend = get_backend()
    except ScrubError as exc:
        sys.stderr.write("FATAL: %s\n" % ascii_safe(str(exc)))
        return 2

    print("cc-scrub")
    print("  ocr engine : %s" % backend.name)
    print("  terms file : %s (%d terms)" % (args.terms_file, len(terms)))
    for term in terms:
        print("    - %s" % ascii_safe(term))
    print("  images     : %d" % len(sources))
    print("  mode       : %s" % ("check-only" if args.check_only else args.mode))

    failures = []
    for source, out_path in plan:
        try:
            ok = process_image(source, out_path, terms, args, backend)
        except ScrubError as exc:
            sys.stderr.write("FATAL: %s\n" % ascii_safe(str(exc)))
            return 2
        if not ok:
            failures.append(source)

    print("")
    print("SUMMARY: %d image(s) processed, %d clean, %d failed."
          % (len(sources), len(sources) - len(failures), len(failures)))
    if failures:
        for source in failures:
            print("  FAILED %s" % source)
        return 1
    return 0


def main_entry():
    """The console-script entry point. The tool runs through the shared failure reporter (issue #3642): a
    failure is reported to the Gateway, and the exit code and the printed error stay exactly as they were."""
    from cc_shared.tool_errors import run_tool

    run_tool(lambda: main(sys.argv[1:]), "cc-scrub")


if __name__ == "__main__":
    main_entry()
