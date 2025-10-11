# main.py
"""
End-to-end runner:
  image -> detect/warp -> recognize -> FEN sanity-check -> engine (lichess/local)

Requires:
  pip install opencv-python numpy python-chess requests
"""

from __future__ import annotations

import os
import sys
import argparse
from typing import Dict, List, Optional, Any

import cv2
import numpy as np

# --- Local modules in your repo ---
from detect_board import detect_and_warp_board, draw_grid_overlay
from recognize_pieces import load_templates, recognize_warped_board
from engine import ChessEngine

# Try to use your fen.py if it provides build_fen; otherwise we fall back to our own builder.
try:
    from fen import build_fen as _build_fen_from_repo  # type: ignore
except Exception:
    _build_fen_from_repo = None  # we'll compute FEN ourselves if not available

# Optional FEN sanity checker (from fen_gate.py). If it's not present, a small fallback keeps running.
try:
    from fen_gate import fen_ok, fen_diagnose  # type: ignore
except Exception:
    def fen_ok(fen: str, strict: bool = False) -> bool:
        try:
            placement = fen.split()[0]
            if placement.count("/") != 7:
                return False
            # digits > 8 are impossible in a rank
            for ch in placement:
                if ch.isdigit() and int(ch) > 8:
                    return False
            if placement.count("P") > 8 or placement.count("p") > 8:
                return False
            return True
        except Exception:
            return False

    def fen_diagnose(fen: str, strict: bool = False) -> List[str]:
        msgs = []
        try:
            placement = fen.split()[0]
        except Exception:
            return ["Empty or malformed FEN"]
        if placement.count("/") != 7:
            msgs.append("Piece placement does not have 8 ranks.")
        # naive pawn caps
        if placement.count("P") > 8:
            msgs.append("Too many white pawns (>8).")
        if placement.count("p") > 8:
            msgs.append("Too many black pawns (>8).")
        return msgs


# ------------- utils -------------

PIECE_NAMES = ["wP","wN","wB","wR","wQ","wK","bP","bN","bB","bR","bQ","bK"]

def imread_any(path: str) -> np.ndarray:
    """Robust imread for Windows paths with non-ASCII characters."""
    data = np.fromfile(path, dtype=np.uint8)
    img = cv2.imdecode(data, cv2.IMREAD_COLOR)
    if img is None:
        raise FileNotFoundError(f"Could not read image: {path}")
    return img

def count_labels(labels64: List[Optional[str]]) -> Dict[str,int]:
    counts = {k: 0 for k in PIECE_NAMES}
    for lab in labels64:
        if lab in counts:
            counts[lab] += 1
    return counts

def labels_to_fen(labels64: List[Optional[str]],
                  side_to_move: str = "w",
                  castling: str = "-",
                  ep: str = "-",
                  halfmove: int = 0,
                  fullmove: int = 1) -> str:
    """
    Build a FEN from a 64-length list of labels like 'wP','bK', or None.
    Assumes labels64 is ordered row-major from top-left to bottom-right of the
    warped board (rank 8 to rank 1, each rank left->right). This matches the
    usual output of a properly oriented warp where top-left is a8.
    """
    if len(labels64) != 64:
        raise ValueError(f"labels64 must have length 64, got {len(labels64)}")

    # map label -> FEN char
    m = {
        "wP":"P","wN":"N","wB":"B","wR":"R","wQ":"Q","wK":"K",
        "bP":"p","bN":"n","bB":"b","bR":"r","bQ":"q","bK":"k",
    }

    ranks: List[str] = []
    for r in range(8):
        row = labels64[r*8:(r+1)*8]  # 0 is rank8 .. 7 is rank1
        empties = 0
        fen_rank = []
        for lab in row:
            if lab is None or lab not in m:
                empties += 1
            else:
                if empties > 0:
                    fen_rank.append(str(empties))
                    empties = 0
                fen_rank.append(m[lab])
        if empties > 0:
            fen_rank.append(str(empties))
        ranks.append("".join(fen_rank))

    placement = "/".join(ranks)
    return f"{placement} {side_to_move} {castling} {ep} {halfmove} {fullmove}"

def fmt_score(cp: Optional[int], mate: Optional[int]) -> str:
    if mate is not None:
        return f"#{mate}"
    if cp is None:
        return "?"
    return f"{cp/100.0:+.2f}"

def normalize_result(item: Any) -> Dict[str, Any]:
    """
    Make various engine result shapes look the same for printing.
    Returns dict with keys: score_cp (int|None), score_mate (int|None), pv_uci (List[str])
    """
    # Objects with attributes
    score_cp = getattr(item, "score_cp", None)
    score_mate = getattr(item, "score_mate", None)
    pv_uci = getattr(item, "pv_uci", None)
    if pv_uci is None and hasattr(item, "pv"):
        pv = getattr(item, "pv", None)
        if isinstance(pv, list):
            pv_uci = [str(x) for x in pv]

    # Dicts (e.g., from lichess JSON or custom wrappers)
    if isinstance(item, dict):
        if score_cp is None:
            score_cp = item.get("score_cp", item.get("cp"))
        if score_mate is None:
            score_mate = item.get("score_mate", item.get("mate"))
        if pv_uci is None:
            if "pv_uci" in item and isinstance(item["pv_uci"], list):
                pv_uci = item["pv_uci"]
            elif "pv" in item and isinstance(item["pv"], list):
                pv_uci = [str(x) for x in item["pv"]]
            elif "moves" in item and isinstance(item["moves"], str):
                pv_uci = item["moves"].split()

    if pv_uci is None:
        pv_uci = []

    return {"score_cp": score_cp, "score_mate": score_mate, "pv_uci": pv_uci}


# ------------- CLI -------------

def parse_args() -> argparse.Namespace:
    ap = argparse.ArgumentParser(
        description="Board OCR -> FEN -> engine analysis (Lichess cloud or local Stockfish)"
    )
    ap.add_argument("image", help="Path to the input board image")
    ap.add_argument("--backend", choices=["lichess","local"], default="lichess", help="Engine backend")
    ap.add_argument("--engine", dest="engine_path", default=os.getenv("STOCKFISH_PATH"),
                    help="Path to Stockfish (required for --backend local)")
    ap.add_argument("--threads", type=int, default=2, help="Local engine threads")
    ap.add_argument("--hash", type=int, default=128, help="Local engine hash size (MB)")
    ap.add_argument("--skill", type=int, default=None, help="Local engine Skill Level")

    ap.add_argument("--multipv", type=int, default=1, help="Number of PV lines")
    ap.add_argument("--depth", type=int, default=18, help="Search depth if --movetime not set")
    ap.add_argument("--movetime", type=int, default=0, help="Movetime ms; overrides depth when >0")

    ap.add_argument("--templates", default=None,
                    help="Path to piece templates folder (defaults to ../data/templates)")
    ap.add_argument("--show", action="store_true", help="Show warped board and grid overlay")
    ap.add_argument("--save-overlay", default=None, help="Path to save grid overlay PNG")
    ap.add_argument("--strict-fen", action="store_true", help="Use stricter FEN sanity checks")

    return ap.parse_args()


# ------------- main flow -------------

def main():
    args = parse_args()

    # --- 1) read + detect + warp ---
    bgr = imread_any(args.image)
    warped, corners, M, rot_k = detect_and_warp_board(bgr, out_size=800, ensure_a1_dark=True)

    # --- 2) load templates + recognize all 64 squares ---
    tmpl_dir = args.templates
    if not tmpl_dir:
        # default to ../data/templates relative to this file
        root = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
        tmpl_dir = os.path.join(root, "data", "templates")
    templates = load_templates(tmpl_dir)

    labels64, scores = recognize_warped_board(warped, templates)
    counts = count_labels(labels64)

    # --- 3) build + print FEN, sanity-gate before engine queries ---
    if callable(_build_fen_from_repo):
        # Use your repo's FEN builder if present
        fen = _build_fen_from_repo(labels64, side_to_move="w", castling="-", ep="-", halfmove=0, fullmove=1)  # type: ignore
    else:
        # Safe fallback: build FEN here
        fen = labels_to_fen(labels64, side_to_move="w", castling="-", ep="-", halfmove=0, fullmove=1)

    print("Detected counts:", counts)
    print("FEN:", fen)

    if not fen_ok(fen, strict=args.strict_fen):
        print("[warn] FEN failed sanity checks; refusing to analyse.")
        for msg in fen_diagnose(fen, strict=args.strict_fen):
            print("  -", msg)
        sys.exit(2)

    # --- 4) engine analysis ---
    eng = ChessEngine(
        backend=args.backend,
        engine_path=args.engine_path,
        threads=args.threads,
        hash_mb=args.hash,
        skill=args.skill,
        user_agent="chess-cheater/1.0",
    )
    eng.set_fen(fen)

    try:
        if args.multipv > 1:
            raw_results = eng.analyse_multipv(
                k=args.multipv,
                depth=None if args.movetime and args.movetime > 0 else args.depth,
                movetime_ms=args.movetime if args.movetime and args.movetime > 0 else None,
            )
        else:
            raw_results = [eng.analyse_best(
                depth=None if args.movetime and args.movetime > 0 else args.depth,
                movetime_ms=args.movetime if args.movetime and args.movetime > 0 else None,
            )]
    except Exception as e:
        # Handle Lichess 404 (cloud miss) nicely, and suggest/attempt local fallback
        from requests.exceptions import HTTPError
        if isinstance(e, HTTPError) and getattr(e.response, "status_code", None) == 404:
            print("[info] Lichess cloud has no cached eval for this position (404).")
            if args.backend == "lichess" and args.engine_path:
                print("[info] Falling back to local engine.")
                eng = ChessEngine(
                    backend="local",
                    engine_path=args.engine_path,
                    threads=args.threads,
                    hash_mb=args.hash,
                    skill=args.skill,
                    user_agent="chess-cheater/1.0",
                )
                eng.set_fen(fen)
                raw_results = eng.analyse_multipv(
                    k=args.multipv,
                    depth=None if args.movetime and args.movetime > 0 else args.depth,
                    movetime_ms=args.movetime if args.movetime and args.movetime > 0 else None,
                ) if args.multipv > 1 else [eng.analyse_best(
                    depth=None if args.movetime and args.movetime > 0 else args.depth,
                    movetime_ms=args.movetime if args.movetime and args.movetime > 0 else None,
                )]
            else:
                print("Tip: pass --backend local and --engine <path-to-stockfish> to analyse uncached positions offline.")
                sys.exit(3)
        else:
            print("[error] Analysis failed:", repr(e))
            sys.exit(4)

    if not raw_results:
        print("[warn] No analysis lines returned.")
        sys.exit(5)

    # --- 5) print results nicely ---
    results = [normalize_result(r) for r in raw_results]
    for i, r in enumerate(results, start=1):
        score = fmt_score(r["score_cp"], r["score_mate"])
        pv = " ".join(r["pv_uci"][:12]) if r["pv_uci"] else ""
        print(f"{i:>2}. score {score} | pv: {pv}")

    # --- 6) visualize (optional) ---
    if args.show or args.save_overlay:
        overlay = draw_grid_overlay(warped, line_thickness=2)
        if args.save_overlay:
            ok, buf = cv2.imencode(".png", overlay)
            if ok:
                np.array(buf).tofile(args.save_overlay)
                print(f"Saved overlay to: {args.save_overlay}")
            else:
                print("[warn] Failed to encode overlay PNG.")
        if args.show:
            cv2.imshow("warped board", warped)
            cv2.imshow("grid overlay", overlay)
            cv2.waitKey(0); cv2.destroyAllWindows()


if __name__ == "__main__":
    main()
