#!/usr/bin/env python3
"""
vendor.py - vendor MewUI / MewVG (+ extensions, + their git submodules) into MewStage's third_party/.

Data-driven: every pin (repo, commit, archive hash), every component (upstream folder -> third_party folder,
include/exclude globs, MewStage-authored "preserve" files), every patch and its category live in
third_party/vendor.lock.toml. Re-running after bumping a pin performs a clean, reproducible re-vendor.

How it works (no git, no `patch` binary, nothing that a stock Windows box lacks):
  1. Each source is a GitHub codeload tarball of ONE commit
     (https://codeload.github.com/<owner>/<repo>/tar.gz/<commit>). It is fetched (or taken from --tarball-dir)
     and verified against the lock: the archive sha256 first, else the content digest (sha256 over every
     path + file hash), because GitHub has re-compressed archives before without changing their content.
  2. Files are read straight out of the archive into memory - nothing is extracted, so a hostile path
     (absolute, '..', a link) can never escape; any such member a component would use aborts the run.
  3. The patch series is applied IN MEMORY by a strict pure-Python unified-diff applier: every hunk's context
     must match exactly (an offset is allowed, fuzz is not), else the run aborts naming the hunk - the signal
     that upstream moved the patched code and the patch must be reviewed.
  4. Only then is the disk touched: each component folder is made equal to the expected file set (changed
     files rewritten, stale files removed). Never touched: bin/, obj/, files matching the component's
     `preserve` globs (MewStage-authored), and folders that are another component's destination.
  5. The applied patch set is recorded in third_party/.vendor-state.toml, so --check compares the tree with
     the lock AS VENDORED (the categories that were skipped stay skipped) rather than with "every patch".

Patch categories: each patch in the lock names a category ([categories.*]); a mandatory category can never be
skipped (the script refuses). Default: every patch applied.

Usage:
  python vendor.py                         vendor every component (lock order), every patch
  python vendor.py --skip-category a,b     vendor without the patches of categories a and b
  python vendor.py --only-mandatory        vendor with the mandatory categories only
  python vendor.py --check                 compare the tree with the lock + recorded patch set; exit 1 on drift
  python vendor.py --list                  list sources and components
  python vendor.py --list-patches          list every patch with its category, component and purpose
  python vendor.py --components a,b        a subset of the components
  python vendor.py --tarball-dir DIR       take <repo>-<commit8>.tar.gz from DIR instead of downloading
  python vendor.py --emit-svg-generated    rebuild MewUI.Svg's committed generated sources (needs dotnet)
  python vendor.py --self-test             test the patch applier
Exit codes: 0 ok, 1 drift or patch failure, 2 usage. Python 3.11+ (tomllib). Console output is plain ASCII.
"""
import argparse
import hashlib
import os
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
import tomllib
import urllib.request
from pathlib import Path, PurePosixPath

HERE = Path(__file__).resolve().parent            # third_party/tools
THIRD_PARTY = HERE.parent                         # third_party/
CODELOAD = "https://codeload.github.com/{owner}/{repo}/tar.gz/{commit}"
NEVER_TOUCH = {"bin", "obj"}                      # build output
STATE_FILE = THIRD_PARTY / ".vendor-state.toml"


class VendorError(Exception):
    """A loud, expected failure (bad pin, hash mismatch, patch that no longer applies, bad usage)."""

    def __init__(self, message, exit_code=1):
        super().__init__(message)
        self.exit_code = exit_code


# ------------------------------------------------------------------------------------------------ globs
_GLOB_RX = {}


def _glob_rx(glob):
    rx = _GLOB_RX.get(glob)
    if rx is None:
        out, i = [], 0
        while i < len(glob):
            if glob.startswith("**/", i):
                out.append("(?:.*/)?"); i += 3
            elif glob.startswith("**", i):
                out.append(".*"); i += 2
            elif glob[i] == "*":
                out.append("[^/]*"); i += 1
            elif glob[i] == "?":
                out.append("[^/]"); i += 1
            else:
                out.append(re.escape(glob[i])); i += 1
        rx = _GLOB_RX[glob] = re.compile("^" + "".join(out) + "$")
    return rx


def matches(rel, globs):
    return any(_glob_rx(g).match(rel) for g in globs)


# ------------------------------------------------------------------------------------------------ sources
def sha256_bytes(b):
    return hashlib.sha256(b).hexdigest()


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def safe_rel(name):
    """Validate an archive-relative POSIX path; return it, or raise for anything that could escape."""
    if not name or "\\" in name or name.startswith("/") or re.match(r"^[A-Za-z]:", name):
        raise VendorError(f"unsafe archive path: {name!r}")
    parts = PurePosixPath(name).parts
    if any(p in ("..", "") for p in parts):
        raise VendorError(f"unsafe archive path: {name!r}")
    return "/".join(parts)


class Source:
    """One upstream commit, read from its codeload tarball into an in-memory index (no extraction)."""

    def __init__(self, name, cfg):
        self.name = name
        self.repo = cfg["repo"].rstrip("/")
        m = re.match(r"^https://github\.com/([^/]+)/([^/]+?)(?:\.git)?$", self.repo)
        if not m:
            raise VendorError(f"[{name}] repo must be a https://github.com/<owner>/<repo> URL: {self.repo}")
        self.owner, self.repo_name = m.group(1), m.group(2)
        self.commit = cfg["commit"]
        if not re.fullmatch(r"[0-9a-f]{40}", self.commit):
            raise VendorError(f"[{name}] commit must be a full 40-hex SHA (pin by commit, never a branch): {self.commit}")
        self.archive_sha256 = cfg["archive_sha256"]
        self.content_sha256 = cfg["content_sha256"]
        self.files = {}      # rel -> bytes
        self.links = {}      # rel -> link target (never vendored)

    @property
    def archive_name(self):
        return f"{self.repo_name}-{self.commit[:8]}.tar.gz"

    @property
    def url(self):
        return CODELOAD.format(owner=self.owner, repo=self.repo_name, commit=self.commit)

    def load(self, cache_dir, tarball_dir):
        if tarball_dir:
            path = Path(tarball_dir) / self.archive_name
            if not path.is_file():
                raise VendorError(f"[{self.name}] --tarball-dir given but {path} is missing")
        else:
            path = Path(cache_dir) / self.archive_name
            if not path.is_file() or sha256_file(path) != self.archive_sha256:
                print(f"  fetch {self.url}")
                path.parent.mkdir(parents=True, exist_ok=True)
                tmp = path.with_suffix(".part")
                with urllib.request.urlopen(self.url, timeout=120) as r, open(tmp, "wb") as f:
                    shutil.copyfileobj(r, f)
                os.replace(tmp, path)
        self._read(path)
        archive = sha256_file(path)
        content = self.content_digest()
        if content != self.content_sha256:
            raise VendorError(
                f"[{self.name}] CONTENT MISMATCH for {self.archive_name}\n"
                f"  lock content_sha256 {self.content_sha256}\n  got                 {content}\n"
                f"  The archive does not hold commit {self.commit} as pinned. Do not edit the lock to match "
                f"unless the pin itself was changed on purpose.")
        if archive != self.archive_sha256:
            print(f"  NOTE [{self.name}] archive sha256 differs from the lock but the CONTENT digest matches "
                  f"(GitHub re-compressed the archive); update archive_sha256 = \"{archive}\"")
        print(f"  [{self.name}] {self.owner}/{self.repo_name} @ {self.commit[:12]}: {len(self.files)} files, verified")

    def _read(self, path):
        prefix = None
        with tarfile.open(path, "r:gz") as tf:
            for m in tf.getmembers():
                if m.name == "pax_global_header" or m.type in (tarfile.XGLTYPE, tarfile.XHDTYPE):
                    continue
                top, _, rest = m.name.partition("/")
                if prefix is None:
                    prefix = top
                elif top != prefix:
                    raise VendorError(f"[{self.name}] archive has more than one top folder ({prefix}, {top})")
                if not rest or m.isdir():
                    continue
                rel = safe_rel(rest.rstrip("/"))
                if m.issym() or m.islnk():
                    self.links[rel] = m.linkname
                elif m.isfile():
                    self.files[rel] = tf.extractfile(m).read()
                else:
                    raise VendorError(f"[{self.name}] unsupported archive member type for {rel!r}")
        expected_top = f"{self.repo_name}-{self.commit}"
        if prefix != expected_top:
            raise VendorError(f"[{self.name}] archive top folder is {prefix!r}, expected {expected_top!r}")

    def content_digest(self):
        h = hashlib.sha256()
        for rel in sorted(set(self.files) | set(self.links)):
            if rel in self.files:
                h.update(f"F {rel} {sha256_bytes(self.files[rel])}\n".encode("utf-8"))
            else:
                h.update(f"L {rel} {self.links[rel]}\n".encode("utf-8"))
        return h.hexdigest()


# ------------------------------------------------------------------------------------------------ patcher
class Hunk:
    def __init__(self, old_start, header):
        self.old_start = old_start
        self.header = header
        self.old = []        # [content]
        self.new = []        # [(content, eol_override)] eol_override '' = "no newline at end of file"
        self.old_noeol = False


class FilePatch:
    def __init__(self, old_path, new_path):
        self.old_path = old_path   # None = new file
        self.new_path = new_path   # None = delete
        self.hunks = []


def _patch_path(token, strip):
    token = token.split("\t", 1)[0].strip()
    if token == "/dev/null":
        return None
    parts = token.split("/")
    if len(parts) <= strip:
        raise VendorError(f"patch path {token!r} has fewer than {strip} leading components")
    return safe_rel("/".join(parts[strip:]))


def parse_patch(text, strip=1):
    """Parse a unified diff (git or plain). Strict: malformed hunks abort."""
    lines = text.replace("\r\n", "\n").split("\n")
    if lines and lines[-1] == "":
        lines.pop()
    patches, cur, i = [], None, 0
    hunk_rx = re.compile(r"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")
    while i < len(lines):
        ln = lines[i]
        if ln.startswith("--- ") and i + 1 < len(lines) and lines[i + 1].startswith("+++ "):
            cur = FilePatch(_patch_path(ln[4:], strip), _patch_path(lines[i + 1][4:], strip))
            patches.append(cur)
            i += 2
            continue
        m = hunk_rx.match(ln)
        if m:
            if cur is None:
                raise VendorError("hunk before any file header")
            old_n = int(m.group(2)) if m.group(2) is not None else 1
            new_n = int(m.group(4)) if m.group(4) is not None else 1
            h = Hunk(int(m.group(1)), ln)
            i += 1
            got_old = got_new = 0
            last = None
            while i < len(lines) and (got_old < old_n or got_new < new_n or lines[i].startswith("\\")):
                body = lines[i]
                if body.startswith("\\"):
                    if last == "old":
                        h.old_noeol = True
                    elif last == "new":
                        c, _ = h.new[-1]; h.new[-1] = (c, "")
                    elif last == "both":
                        h.old_noeol = True
                        c, _ = h.new[-1]; h.new[-1] = (c, "")
                    i += 1
                    continue
                tag, content = (body[:1], body[1:]) if body else (" ", "")
                if tag == " ":
                    h.old.append(content); h.new.append((content, None)); got_old += 1; got_new += 1; last = "both"
                elif tag == "-":
                    h.old.append(content); got_old += 1; last = "old"
                elif tag == "+":
                    h.new.append((content, None)); got_new += 1; last = "new"
                else:
                    raise VendorError(f"malformed hunk line {i + 1}: {body[:60]!r}")
                i += 1
            if got_old != old_n or got_new != new_n:
                raise VendorError(f"hunk {h.header!r}: counts do not match its header")
            cur.hunks.append(h)
            continue
        i += 1   # git metadata lines (diff --git, index, new file mode ...) and prose
    if not patches:
        raise VendorError("patch contains no file sections")
    return patches


def patch_purpose(text):
    """The first paragraph of the patch header (the prose before the first file section), one line."""
    head = []
    for ln in text.replace("\r\n", "\n").split("\n"):
        if ln.startswith("--- ") or ln.startswith("diff "):
            break
        if not ln.strip():
            if head:
                break
            continue
        head.append(ln.strip())
    return " ".join(head)


def _split_lines(text):
    """[(content, eol)] preserving each line's own terminator ('\\r\\n', '\\n' or '' for the last)."""
    out = []
    for raw in text.splitlines(keepends=True):
        if raw.endswith("\r\n"):
            out.append((raw[:-2], "\r\n"))
        elif raw.endswith("\n") or raw.endswith("\r"):
            out.append((raw[:-1], raw[-1]))
        else:
            out.append((raw, ""))
    return out


def apply_file_patch(fp, data, label):
    """Apply one file's hunks to `data` (bytes or None). Returns new bytes or None (deleted)."""
    if fp.old_path is None:
        if data is not None:
            raise VendorError(f"{label}: creates {fp.new_path} but it already exists upstream")
        bom, lines = "", []
    else:
        if data is None:
            raise VendorError(f"{label}: {fp.old_path} does not exist upstream")
        text = data.decode("utf-8")
        bom = "\ufeff" if text.startswith("\ufeff") else ""
        lines = _split_lines(text[len(bom):])
    crlf = sum(1 for _, e in lines if e == "\r\n")
    eol = "\r\n" if crlf * 2 > len(lines) else "\n"
    pos = 0          # hunks apply in order and never overlap
    offset = 0
    for h in fp.hunks:
        want = max(0, h.old_start - 1 + offset) if h.old else max(0, h.old_start + offset)
        n = len(h.old)
        cands = []
        for start in range(pos, len(lines) - n + 1):
            if all(lines[start + k][0] == h.old[k] for k in range(n)):
                cands.append(start)
        if not cands:
            raise VendorError(f"{label}: HUNK NO LONGER APPLIES (upstream moved this code - review the patch):\n"
                              f"  {h.header}\n  first context line: {h.old[0] if h.old else '(none)'!r}")
        start = min(cands, key=lambda s: abs(s - want))
        replaced = lines[start:start + n]
        tail_eol = replaced[-1][1] if replaced else eol
        new = []
        for k, (c, override) in enumerate(h.new):
            e = override if override is not None else eol
            if override is None and k == len(h.new) - 1 and start + n == len(lines) and replaced and tail_eol == "" and not h.old_noeol:
                e = ""
            new.append((c, e))
        lines[start:start + n] = new
        offset += len(new) - n
        pos = start + len(new)
    if fp.new_path is None:
        return None
    return (bom + "".join(c + e for c, e in lines)).encode("utf-8")


def self_test():
    """Exercise the patch applier on the cases real upstream files present (CRLF, BOM, offset, no final newline,
    a hunk that no longer applies). Run: vendor.py --self-test"""
    patch = ("--- a/f.txt\n+++ b/f.txt\n@@ -2,3 +2,3 @@\n b\n-c\n+C\n d\n")
    fp = parse_patch(patch)[0]
    assert apply_file_patch(fp, b"a\nb\nc\nd\ne\n", "t1") == b"a\nb\nC\nd\ne\n"
    assert apply_file_patch(fp, b"x\r\ny\r\na\r\nb\r\nc\r\nd\r\n", "t2") == b"x\r\ny\r\na\r\nb\r\nC\r\nd\r\n"
    assert apply_file_patch(fp, "\ufeffa\nb\nc\nd".encode("utf-8"), "t3") == "\ufeffa\nb\nC\nd".encode("utf-8")
    tail = parse_patch("--- a/f\n+++ b/f\n@@ -1,2 +1,2 @@\n a\n-b\n\\ No newline at end of file\n+B\n")[0]
    assert apply_file_patch(tail, b"a\nb", "t4") == b"a\nB\n"
    new = parse_patch("--- /dev/null\n+++ b/n.cs\n@@ -0,0 +1,2 @@\n+x\n+y\n")[0]
    assert apply_file_patch(new, None, "t5") == b"x\ny\n"
    try:
        apply_file_patch(fp, b"a\nb\nX\nd\n", "t6")
        raise AssertionError("a stale hunk applied")
    except VendorError as e:
        assert "NO LONGER APPLIES" in str(e)
    for bad in ("../x", "/abs", "a/../b", "C:/x", "a\\b"):
        try:
            safe_rel(bad)
            raise AssertionError(f"unsafe path accepted: {bad}")
        except VendorError:
            pass
    assert patch_purpose("Purpose line one\ncontinues here.\n\nMore prose.\n--- a/x\n+++ b/x\n") == "Purpose line one continues here."
    print("vendor: self-test PASSED (9 cases)")


# ------------------------------------------------------------------------------------------------ components
class PatchRef:
    def __init__(self, entry, categories, component):
        if not isinstance(entry, dict) or "file" not in entry or "category" not in entry:
            raise VendorError(f"[{component}] every patch must be a table {{ file = ..., category = ... }}: {entry!r}")
        self.file = entry["file"]
        self.category = entry["category"]
        if self.category not in categories:
            raise VendorError(f"[{component}] patch {self.file} names unknown category {self.category!r}")
        self.mandatory = bool(categories[self.category].get("mandatory", False))
        self.component = component


class Component:
    def __init__(self, name, cfg, sources, categories):
        self.name = name
        src = cfg["source"]
        if src not in sources:
            raise VendorError(f"[{name}] unknown source {src!r}")
        self.source = sources[src]
        self.src_root = cfg.get("src_root", "").strip("/")
        self.dest_rel = cfg["dest"].strip("/")
        self.dest = THIRD_PARTY / self.dest_rel
        self.include = cfg.get("include", ["**"])
        self.exclude = cfg.get("exclude", [])
        self.preserve = cfg.get("preserve", [])
        self.patches = [PatchRef(p, categories, name) for p in cfg.get("patches", [])]
        self.extra = cfg.get("extra", {})      # dest-rel -> source-root-rel (single files from elsewhere in the repo)

    def expected(self, skip_categories):
        """dest-relative path -> bytes, after include/exclude, extras and the (filtered) patch series."""
        files = {}
        pre = self.src_root + "/" if self.src_root else ""
        for rel, data in self.source.files.items():
            if not rel.startswith(pre):
                continue
            sub = rel[len(pre):]
            if matches(sub, self.include) and not matches(sub, self.exclude):
                files[sub] = data
        for rel in self.source.links:
            sub = rel[len(pre):] if rel.startswith(pre) else None
            if sub is not None and matches(sub, self.include) and not matches(sub, self.exclude):
                raise VendorError(f"[{self.name}] {rel} is a link in the archive; links are never vendored")
        for dest_rel, src_rel in self.extra.items():
            if src_rel not in self.source.files:
                raise VendorError(f"[{self.name}] extra {src_rel} is not in the archive")
            files[safe_rel(dest_rel)] = self.source.files[src_rel]
        for p in self.patches:
            if p.category in skip_categories:
                print(f"  [{self.name}] patch SKIPPED ({p.category}): {p.file}")
                continue
            pp = THIRD_PARTY / p.file
            if not pp.is_file():
                raise VendorError(f"[{self.name}] patch not found: {p.file}")
            for fp in parse_patch(pp.read_text(encoding="utf-8")):
                target = fp.old_path or fp.new_path
                out = apply_file_patch(fp, files.get(target), f"[{self.name}] {p.file}")
                if out is None:
                    files.pop(target, None)
                else:
                    files[fp.new_path] = out
            print(f"  [{self.name}] patch applied ({p.category}): {p.file}")
        for rel in [r for r in files if matches(r, self.preserve)]:
            del files[rel]        # a preserved path is MewStage's; upstream never writes over it
        return files


def protected_dirs(comp, all_comps):
    """dest-relative folders inside comp.dest that belong to OTHER components."""
    out = []
    for c in all_comps:
        if c is comp:
            continue
        if c.dest_rel.startswith(comp.dest_rel + "/"):
            out.append(c.dest_rel[len(comp.dest_rel) + 1:])
    return out


def disk_files(comp, all_comps):
    """dest-relative path -> absolute Path for every file this component owns on disk."""
    owned = {}
    if not comp.dest.is_dir():
        return owned
    prot = protected_dirs(comp, all_comps)
    for root, dirs, files in os.walk(comp.dest):
        rel_root = Path(root).relative_to(comp.dest).as_posix()
        rel_root = "" if rel_root == "." else rel_root
        keep = []
        for d in dirs:
            rel = f"{rel_root}/{d}" if rel_root else d
            if d in NEVER_TOUCH or rel in prot:
                continue
            keep.append(d)
        dirs[:] = keep
        for f in files:
            rel = f"{rel_root}/{f}" if rel_root else f
            if matches(rel, comp.preserve):
                continue
            owned[rel] = Path(root) / f
    return owned


def sync_component(comp, all_comps, check, skip_categories):
    exp = comp.expected(skip_categories)
    have = disk_files(comp, all_comps)
    added = sorted(r for r in exp if r not in have)
    removed = sorted(r for r in have if r not in exp)
    changed = sorted(r for r in exp if r in have and have[r].read_bytes() != exp[r])
    verb = "drift" if check else "sync"
    print(f"[{comp.name}] {comp.source.name}:{comp.src_root or '.'} -> third_party/{comp.dest_rel}: "
          f"{len(exp)} files; {verb}: +{len(added)} ~{len(changed)} -{len(removed)}")
    if check:
        for tag, rels in (("missing", added), ("differs", changed), ("extra", removed)):
            for r in rels[:20]:
                print(f"    {tag}: {r}")
            if len(rels) > 20:
                print(f"    {tag}: ... {len(rels) - 20} more")
        return bool(added or removed or changed)
    for r in removed:
        have[r].unlink()
    for r in added + changed:
        out = comp.dest / r
        out.parent.mkdir(parents=True, exist_ok=True)
        tmp = out.with_name(out.name + ".vendor-tmp")
        tmp.write_bytes(exp[r])
        os.replace(tmp, out)
    _prune_empty(comp.dest, protected_dirs(comp, all_comps))
    return False


def _prune_empty(dest, prot):
    if not dest.is_dir():
        return
    for root, dirs, files in os.walk(dest, topdown=False):
        p = Path(root)
        rel = p.relative_to(dest).as_posix()
        if p == dest or any(part in NEVER_TOUCH for part in Path(rel).parts):
            continue
        if any(rel == d or d.startswith(rel + "/") for d in prot):
            continue
        try:
            if not any(p.iterdir()):
                p.rmdir()
        except OSError:
            pass


# ------------------------------------------------------------------------------------------------ vendor state
def write_state(skip_categories, categories):
    applied = sorted(c for c in categories if c not in skip_categories)
    skipped = sorted(skip_categories)
    text = ("# Written by third_party/tools/vendor.py; records which patch categories the tree was vendored with.\n"
            "# --check compares the tree against this set. Re-run vendor.py to change it. Committed on purpose.\n"
            f"applied_categories = [{', '.join(repr(c) for c in applied)}]\n"
            f"skipped_categories = [{', '.join(repr(c) for c in skipped)}]\n")
    STATE_FILE.write_text(text.replace("'", '"'), encoding="utf-8")


def read_state(categories):
    """The skipped-category set the tree was vendored with (empty when no state file exists)."""
    if not STATE_FILE.is_file():
        return set()
    with open(STATE_FILE, "rb") as f:
        state = tomllib.load(f)
    skipped = set(state.get("skipped_categories", []))
    unknown = skipped - set(categories)
    if unknown:
        raise VendorError(f"{STATE_FILE.name} names unknown categories {sorted(unknown)}; re-run vendor.py")
    return skipped


# ------------------------------------------------------------------------------------------------ svg emit
SVG_GEN_README = """# Generated SVG element/attribute registry (committed)

Output of SvgNet's `AvailableElementsGenerator` source generator, committed so the normal build needs no
build-time Roslyn component and NativeAOT builds match JIT builds. Compiled by MewUI.Svg.csproj (MewStage-authored).
DO NOT edit by hand. Regenerate after bumping the SvgNet pin in third_party/vendor.lock.toml:
    python third_party/tools/vendor.py --emit-svg-generated
Generator source: svg-net/SVG (Ms-PL). See third_party/NOTICE.
"""


def run(cmd):
    r = subprocess.run(cmd, capture_output=True, text=True)
    if r.returncode != 0:
        raise VendorError(f"command failed: {' '.join(cmd)}\n{r.stdout}\n{r.stderr}")
    return r


def emit_svg_generated(lock):
    comp = lock["components"].get("svg")
    if comp is None:
        raise VendorError("lock has no [components.svg]")
    dest = THIRD_PARTY / comp["dest"]
    csproj = dest / "MewUI.Svg.csproj"
    if not csproj.is_file():
        raise VendorError(f"not found: {csproj}")
    if shutil.which("dotnet") is None:
        raise VendorError("dotnet is not on PATH - the SVG generated-source rebuild needs the .NET SDK")
    # The emit build restores SvgNet's generator project, which writes a packages.lock.json the lock does not
    # own (the generator is not built in normal mode). Remove it again so --check stays clean after an emit.
    gen_lock = dest / "ThirdParty" / "SvgNet" / "Generators" / "packages.lock.json"
    gen_lock_existed = gen_lock.exists()
    tmp = tempfile.mkdtemp(prefix="svggen-")
    try:
        print(f"[emit-svg-generated] building {csproj.name} with the SvgNet generator ...")
        run(["dotnet", "build", str(csproj), "-c", "Release", "--nologo", "-v", "q",
             "-p:MewUISvgEmitGenerated=true", "-p:EmitCompilerGeneratedFiles=true",
             f"-p:CompilerGeneratedFilesOutputPath={tmp}"])
        gen = dest / "Generated"
        outs = sorted(Path(tmp).rglob("*.g.cs"))
        if not outs:
            raise VendorError("the generator emitted 0 files (build or generator problem)")
        if gen.exists():
            shutil.rmtree(gen)
        gen.mkdir(parents=True)
        for f in outs:
            shutil.copy2(f, gen / f.name)
        (gen / ".committed").write_text("committed\n", encoding="ascii")
        (gen / "README.md").write_text(SVG_GEN_README, encoding="ascii")
        print(f"[emit-svg-generated] wrote {len(outs)} committed *.g.cs -> third_party/{comp['dest']}/Generated")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
        if not gen_lock_existed and gen_lock.exists():
            gen_lock.unlink()


# ------------------------------------------------------------------------------------------------ main
def load_lock(path):
    with open(path, "rb") as f:
        lock = tomllib.load(f)
    for key in ("sources", "components", "meta", "categories"):
        if key not in lock:
            raise VendorError(f"{path}: missing [{key}]")
    order = lock["meta"].get("order", [])
    missing = [c for c in lock["components"] if c not in order]
    unknown = [c for c in order if c not in lock["components"]]
    if missing or unknown:
        raise VendorError(f"{path}: meta.order out of step with [components.*] (missing {missing}, unknown {unknown})")
    return lock


def resolve_skip(args, categories):
    """The categories to skip, validated: unknown names and mandatory categories are refused (exit 2)."""
    skip = set()
    if args.only_mandatory:
        skip = {c for c, cfg in categories.items() if not cfg.get("mandatory", False)}
    for name in [s.strip() for s in (args.skip_category or "").split(",") if s.strip()]:
        if name not in categories:
            raise VendorError(f"--skip-category: unknown category {name!r} (have: {', '.join(sorted(categories))})", 2)
        if categories[name].get("mandatory", False):
            raise VendorError(f"--skip-category: {name!r} is mandatory and cannot be skipped", 2)
        skip.add(name)
    return skip


def main(argv=None):
    ap = argparse.ArgumentParser(description="Vendor MewUI/MewVG into third_party/ from vendor.lock.toml.")
    ap.add_argument("--components", default="", help="comma-separated subset (default: all, lock order)")
    ap.add_argument("--tarball-dir", default=None, help="folder holding <repo>-<commit8>.tar.gz (no download)")
    ap.add_argument("--cache-dir", default=None, help="download cache (default: third_party/.vendor-cache)")
    ap.add_argument("--check", action="store_true", help="report drift from the lock; change nothing; exit 1 on drift")
    ap.add_argument("--list", action="store_true", help="list sources and components, then exit")
    ap.add_argument("--list-patches", action="store_true", help="list every patch with its category and purpose, then exit")
    ap.add_argument("--skip-category", default="", help="comma-separated patch categories NOT to apply (never a mandatory one)")
    ap.add_argument("--only-mandatory", action="store_true", help="apply the mandatory patch categories only")
    ap.add_argument("--lock", default=None, help="lock file (default: third_party/vendor.lock.toml)")
    ap.add_argument("--emit-svg-generated", action="store_true", help="rebuild MewUI.Svg/Generated (needs dotnet)")
    ap.add_argument("--self-test", action="store_true", help="test the patch applier and path checks, then exit")
    args = ap.parse_args(argv)
    if args.self_test:
        self_test()
        return 0

    lock = load_lock(Path(args.lock) if args.lock else THIRD_PARTY / "vendor.lock.toml")
    categories = lock["categories"]
    if args.emit_svg_generated:
        emit_svg_generated(lock)
        return 0

    sources = {n: Source(n, c) for n, c in lock["sources"].items()}
    order = lock["meta"]["order"]
    comps = [Component(n, lock["components"][n], sources, categories) for n in order]
    if args.list:
        for s in sources.values():
            print(f"source    {s.name:12} {s.owner}/{s.repo_name} @ {s.commit}")
        for c in comps:
            print(f"component {c.name:20} {c.source.name}:{c.src_root or '.'} -> third_party/{c.dest_rel}")
        return 0
    if args.list_patches:
        for cat, cfg in categories.items():
            flag = "MANDATORY" if cfg.get("mandatory", False) else "optional"
            print(f"category  {cat:14} {flag:9} {cfg.get('description', '')}")
        for c in comps:
            for p in c.patches:
                pp = THIRD_PARTY / p.file
                purpose = patch_purpose(pp.read_text(encoding="utf-8")) if pp.is_file() else "(missing file)"
                print(f"patch     {p.category:14} {c.name:12} {p.file}\n          {purpose}")
        return 0

    if args.check:
        if args.skip_category or args.only_mandatory:
            raise VendorError("--check reads the recorded patch set from .vendor-state.toml; do not combine it with --skip-category / --only-mandatory", 2)
        skip = read_state(categories)
    else:
        skip = resolve_skip(args, categories)

    want = [w.strip() for w in args.components.split(",") if w.strip()] or order
    bad = [w for w in want if w not in order]
    if bad:
        raise VendorError(f"unknown component(s) {bad} (have: {', '.join(order)})", 2)
    chosen = [c for c in comps if c.name in want]
    cache = Path(args.cache_dir) if args.cache_dir else THIRD_PARTY / ".vendor-cache"
    mode = "CHECK" if args.check else "VENDOR"
    print(f"vendor: {mode} into {THIRD_PARTY}")
    print(f"vendor: patch categories skipped: {', '.join(sorted(skip)) if skip else '(none)'}")
    for s in sorted({c.source.name for c in chosen}):
        sources[s].load(cache, args.tarball_dir)
    drift = False
    for c in chosen:
        drift |= sync_component(c, comps, args.check, skip)
    if args.check:
        print("vendor: tree MATCHES the lock" if not drift else "vendor: DRIFT from the lock (see above)")
        return 1 if drift else 0
    if len(chosen) == len(comps):
        write_state(skip, categories)
        print(f"vendor: recorded the applied patch set in third_party/{STATE_FILE.name}")
    svg = lock["components"].get("svg")
    if svg and not (THIRD_PARTY / svg["dest"] / "Generated" / ".committed").exists():
        print("NOTE: MewUI.Svg's committed generated sources are absent - run: vendor.py --emit-svg-generated")
    print(f"vendor: done ({len(chosen)} components). Next: dotnet restore + build (see third_party/NOTICE).")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except VendorError as e:
        print(f"ERROR: {e}")
        sys.exit(e.exit_code)
