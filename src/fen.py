# fen_gate.py
"""
Lightweight FEN sanity checks to avoid sending absurd positions
(e.g., 55 white pawns) to an engine or remote API.

Usage:
------
from fen_gate import fen_ok, fen_diagnose

if not fen_ok(fen):
    print("[warn] FEN failed sanity checks:")
    for msg in fen_diagnose(fen):
        print("  -", msg)
    sys.exit(1)

Design notes:
-------------
- Focuses on *cheap* guards: structure, pawn counts, back-rank pawns, total
  piece caps, and (optionally) king counts.
- Does not try to prove legality. It just prevents obviously broken OCR.
- By default we DO NOT require exactly one king per side (strict=False), since
  imperfect recognition might miss a king. Turn on strict=True if desired.
"""

from __future__ import annotations
import re
from typing import Dict, List, Tuple

# Allowed piece symbols in FEN
PIECES = "PNBRQKpnbrqk"

# Regex for a quick placement-level validation (only characters, no structure)
_RE_FEN_PLACEMENT = re.compile(r"^[PNBRQKpnbrqk1-8/]+$")

def _expand_rank(rank: str) -> str:
    """Expand a single FEN rank into an 8-char string using '.' for empties."""
    out = []
    for ch in rank:
        if ch.isdigit():
            out.append("." * int(ch))
        else:
            out.append(ch)
    expanded = "".join(out)
    if len(expanded) != 8:
        raise ValueError(f"Rank does not expand to 8 squares: '{rank}' -> '{expanded}'")
    return expanded

def _placement_to_grid(placement: str) -> List[str]:
    """Return list of 8 strings (each len 8). FEN lists rank 8 first, then 7..1."""
    ranks = placement.split("/")
    if len(ranks) != 8:
        raise ValueError(f"Placement must have 8 ranks, got {len(ranks)}")
    return [_expand_rank(r) for r in ranks]

def fen_piece_counts_from_placement(placement: str) -> Dict[str, int]:
    """Count each piece symbol from the piece placement field."""
    counts = {c: 0 for c in PIECES}
    for ch in placement:
        if ch in counts:
            counts[ch] += 1
    return counts

def _side_total(counts: Dict[str, int], side: str) -> int:
    """Total number of pieces for 'white' or 'black' from counts."""
    if side == "white":
        symbols = "PNBRQK"
    else:
        symbols = "pnbrqk"
    return sum(counts.get(s, 0) for s in symbols)

def _has_back_rank_pawns(grid: List[str]) -> bool:
    """True if any pawn sits on rank 8 (grid[0]) or rank 1 (grid[7])."""
    rank8 = grid[0]
    rank1 = grid[7]
    return any(ch == "P" for ch in rank8+rank1) or any(ch == "p" for ch in rank8+rank1)

def _basic_fen_split(fen: str) -> Tuple[str, List[str]]:
    """
    Split FEN into (placement, rest_fields).
    Accept 1+ fields; only the first (placement) is required for sanity checks.
    """
    parts = fen.strip().split()
    if not parts:
        raise ValueError("Empty FEN string")
    return parts[0], parts[1:]

def _check_structure(placement: str, problems: List[str]) -> List[str]:
    if not _RE_FEN_PLACEMENT.match(placement):
        problems.append("Placement contains invalid characters.")
        return problems
    try:
        grid = _placement_to_grid(placement)
    except ValueError as e:
        problems.append(str(e))
        return problems

    # Verify every expanded rank has exactly 8 squares
    for i, r in enumerate(grid, start=8):
        if len(r) != 8:
            problems.append(f"Rank {i} does not have 8 squares after expansion.")
    return problems

def _check_counts_and_rules(placement: str, strict: bool, problems: List[str]) -> List[str]:
    counts = fen_piece_counts_from_placement(placement)

    # 1) Pawns per side
    if counts["P"] > 8:
        problems.append(f"Too many white pawns: {counts['P']} (>8).")
    if counts["p"] > 8:
        problems.append(f"Too many black pawns: {counts['p']} (>8).")

    # 2) Total piece caps
    total_white = _side_total(counts, "white")
    total_black = _side_total(counts, "black")
    if total_white > 16:
        problems.append(f"Too many white pieces total: {total_white} (>16).")
    if total_black > 16:
        problems.append(f"Too many black pieces total: {total_black} (>16).")
    if total_white + total_black > 32:
        problems.append(f"Too many pieces overall: {total_white + total_black} (>32).")

    # 3) (Optional) exactly one king per side
    if strict:
        if counts["K"] != 1:
            problems.append(f"White king count is {counts['K']} (expected 1).")
        if counts["k"] != 1:
            problems.append(f"Black king count is {counts['k']} (expected 1).")
    else:
        # Even in non-strict mode, reject >1 kings which is almost certainly garbage
        if counts["K"] > 1:
            problems.append(f"White king count is {counts['K']} (>1).")
        if counts["k"] > 1:
            problems.append(f"Black king count is {counts['k']} (>1).")

    # 4) Back-rank pawn rule (no pawns on ranks 1 or 8)
    try:
        grid = _placement_to_grid(placement)
        if _has_back_rank_pawns(grid):
            problems.append("Pawns on back rank (rank 1 or 8).")
    except ValueError:
        # If structure already broken, we already logged it elsewhere
        pass

    return problems

def fen_diagnose(fen: str, strict: bool = False) -> List[str]:
    """
    Return a list of human-readable problems with the FEN. Empty list means "looks sane".
    This is intentionally conservative and *not* a legality checker.
    """
    problems: List[str] = []
    try:
        placement, _ = _basic_fen_split(fen)
    except ValueError as e:
        return [str(e)]

    # Structure / character-level checks
    _check_structure(placement, problems)

    # Counting / rule-of-thumb checks
    _check_counts_and_rules(placement, strict, problems)

    return problems

def fen_ok(fen: str, strict: bool = False) -> bool:
    """
    Convenience boolean wrapper. True = passes sanity checks, False = has problems.
    """
    return len(fen_diagnose(fen, strict=strict)) == 0

# ---------------------------
# Optional: simple CLI helper
# ---------------------------
if __name__ == "__main__":
    import sys

    if len(sys.argv) == 1:
        print("Usage: python fen_gate.py '<FEN string>' [--strict]")
        sys.exit(2)

    strict = False
    args = [a for a in sys.argv[1:] if a.strip()]
    if "--strict" in args:
        strict = True
        args.remove("--strict")

    fen_in = " ".join(args)
    issues = fen_diagnose(fen_in, strict=strict)
    if issues:
        print("FEN is NOT OK:")
        for msg in issues:
            print(" -", msg)
        sys.exit(1)
    else:
        print("FEN looks sane.")
        sys.exit(0)
