# engine.py
"""
Backends:
- local   : Stockfish via python-chess (UCI). Needs a stockfish.exe on disk.
- lichess : Lichess Cloud Evaluation API (HTTP). No local engine required.

Install:
    pip install python-chess requests

Env vars (optional):
    STOCKFISH_PATH   = path to stockfish binary (for local backend)
"""

from __future__ import annotations
from dataclasses import dataclass
from typing import Optional, List, Literal, Dict, Any
import os
import json

# --- Common result type -------------------------------------------------------

@dataclass
class AnalysisResult:
    bestmove_uci: Optional[str]
    bestmove_san: Optional[str]
    score_cp: Optional[int]       # centipawns, POV side-to-move
    score_mate: Optional[int]     # +N → mate for side-to-move in N
    pv_uci: List[str]
    pv_san: List[str]

# --- Backend: Lichess Cloud Eval ---------------------------------------------

class LichessCloudEngine:
    """
    Lightweight client for Lichess cloud eval:
      GET https://lichess.org/api/cloud-eval?fen=...&multiPv=K
    Returns cached/queued Stockfish evaluations.
    """
    BASE_URL = "https://lichess.org/api/cloud-eval"

    def __init__(self, multipv_default: int = 1, user_agent: str = "chess-cheater/1.0"):
        import requests  # lazy import
        self.requests = requests
        self.headers = {"User-Agent": user_agent}
        self._fen = None
        self._multipv_default = multipv_default

    def set_fen(self, fen: str):
        self._fen = fen

    def _fetch(self, fen: str, multipv: int) -> Dict[str, Any]:
        resp = self.requests.get(
            self.BASE_URL,
            params={"fen": fen, "multiPv": multipv},
            headers=self.headers,
            timeout=15,
        )
        resp.raise_for_status()
        return resp.json()

    def _to_results(self, data: Dict[str, Any]) -> List[AnalysisResult]:
        # Lichess returns "pvs": [{ "moves":"e2e4 e7e5 ...", "cp": 23, "mate": null }, ...]
        pvs = data.get("pvs") or []
        results: List[AnalysisResult] = []
        for line in pvs:
            moves_uci = (line.get("moves") or "").split()
            # SAN conversion not available via API; leave SAN empty (optional: compute via python-chess)
            results.append(AnalysisResult(
                bestmove_uci=moves_uci[0] if moves_uci else None,
                bestmove_san=None,
                score_cp=line.get("cp"),
                score_mate=line.get("mate"),
                pv_uci=moves_uci,
                pv_san=[],
            ))
        # Guarantee at least one result object
        return results or [AnalysisResult(None, None, None, None, [], [])]

    def analyse_best(self, depth: Optional[int] = None, movetime_ms: Optional[int] = None) -> AnalysisResult:
        data = self._fetch(self._fen, multipv=1)
        return self._to_results(data)[0]

    def analyse_multipv(self, k: int = 3, depth: Optional[int] = None, movetime_ms: Optional[int] = None) -> List[AnalysisResult]:
        data = self._fetch(self._fen, multipv=k)
        return self._to_results(data)

    def close(self):
        pass  # nothing to close

# --- Backend: Local UCI (python-chess + Stockfish) ---------------------------

class LocalUciEngine:
    def __init__(self, engine_path: Optional[str] = None, threads: int = 2, hash_mb: int = 128, skill: Optional[int] = None):
        import chess
        import chess.engine
        self.chess = chess
        self.engine_mod = chess.engine

        path = engine_path or os.environ.get("STOCKFISH_PATH", "stockfish")
        if not os.path.exists(path) and path == "stockfish":
            # Let OS PATH resolution try; otherwise give clearer error later
            pass
        elif not os.path.exists(path):
            raise FileNotFoundError(f"Stockfish not found at: {path}. Pass --engine <path> or set STOCKFISH_PATH.")

        self.engine = self.engine_mod.SimpleEngine.popen_uci(path)
        # Configure options (ignore if unsupported)
        opts = {"Threads": threads, "Hash": hash_mb}
        if skill is not None:
            opts["Skill Level"] = max(0, min(20, skill))
        try:
            self.engine.configure(opts)
        except self.engine_mod.EngineError:
            pass

        self.board = self.chess.Board()

    def set_fen(self, fen: str):
        self.board = self.chess.Board(fen)

    def _limit(self, depth: Optional[int], movetime_ms: Optional[int]):
        if movetime_ms is not None:
            return self.engine_mod.Limit(time=movetime_ms / 1000.0)
        if depth is not None:
            return self.engine_mod.Limit(depth=depth)
        return self.engine_mod.Limit(depth=12)

    def _info_to_result(self, info: Dict[str, Any]) -> AnalysisResult:
        move = info["pv"][0] if "pv" in info and info["pv"] else None
        uci = move.uci() if move else None
        san = self.board.san(move) if move else None

        score_cp = None
        score_mate = None
        if "score" in info and info["score"] is not None:
            s = info["score"].pov(self.board.turn)
            if s.is_mate():
                score_mate = s.mate()
            else:
                score_cp = s.score()

        pv_uci = [m.uci() for m in info.get("pv", [])]
        # Build SAN PV on a copy
        b = self.board.copy()
        pv_san = []
        for m in info.get("pv", []):
            pv_san.append(b.san(m))
            b.push(m)

        return AnalysisResult(uci, san, score_cp, score_mate, pv_uci, pv_san)

    def analyse_best(self, depth: Optional[int] = 15, movetime_ms: Optional[int] = None) -> AnalysisResult:
        info = self.engine.analyse(self.board, self._limit(depth, movetime_ms), multipv=1)
        return self._info_to_result(info)

    def analyse_multipv(self, k: int = 3, depth: Optional[int] = 15, movetime_ms: Optional[int] = None) -> List[AnalysisResult]:
        infos = self.engine.analyse(self.board, self._limit(depth, movetime_ms), multipv=k)
        if isinstance(infos, dict):
            infos = [infos]
        # Sort by multipv (1..k)
        infos = sorted(infos, key=lambda x: x.get("multipv", 1))
        return [self._info_to_result(i) for i in infos]

    def close(self):
        try:
            self.engine.quit()
        except Exception:
            pass

# --- Front facade ------------------------------------------------------------

Backend = Literal["local", "lichess"]

class ChessEngine:
    """
    Uniform facade. Choose backend="local" or "lichess".
    """
    def __init__(
        self,
        backend: Backend = "local",
        engine_path: Optional[str] = None,
        threads: int = 2,
        hash_mb: int = 128,
        skill: Optional[int] = None,
        user_agent: str = "chess-cheater/1.0",
    ):
        if backend == "local":
            self.impl = LocalUciEngine(engine_path, threads, hash_mb, skill)
        elif backend == "lichess":
            self.impl = LichessCloudEngine(user_agent=user_agent)
        else:
            raise ValueError("backend must be 'local' or 'lichess'")

    def set_fen(self, fen: str):
        self.impl.set_fen(fen)

    def analyse_best(self, depth: Optional[int] = 15, movetime_ms: Optional[int] = None) -> AnalysisResult:
        return self.impl.analyse_best(depth=depth, movetime_ms=movetime_ms)

    def analyse_multipv(self, k: int = 3, depth: Optional[int] = 15, movetime_ms: Optional[int] = None) -> List[AnalysisResult]:
        return self.impl.analyse_multipv(k=k, depth=depth, movetime_ms=movetime_ms)

    def close(self):
        self.impl.close()

# --- CLI ---------------------------------------------------------------------

if __name__ == "__main__":
    import argparse
    ap = argparse.ArgumentParser(description="Query Stockfish via local UCI or Lichess Cloud API.")
    ap.add_argument("--fen", required=True, help="Position FEN")
    ap.add_argument("--backend", choices=["local", "lichess"], default="local")
    ap.add_argument("--engine", default=None, help="Path to local stockfish.exe (for backend=local)")
    ap.add_argument("--depth", type=int, default=15, help="Search depth (local backend)")
    ap.add_argument("--movetime", type=int, default=None, help="Search time ms (overrides depth, local backend)")
    ap.add_argument("--multipv", type=int, default=1, help="Number of lines")
    ap.add_argument("--threads", type=int, default=2)
    ap.add_argument("--hash", type=int, default=128)
    ap.add_argument("--skill", type=int, default=None)
    ap.add_argument("--backend-agent", default="chess-cheater/1.0", help="User-Agent for Lichess backend")
    args = ap.parse_args()

    eng = ChessEngine(
        backend=args.backend,
        engine_path=args.engine,
        threads=args.threads,
        hash_mb=args.hash,
        skill=args.skill,
        user_agent=args.backend-agent if hasattr(args, "backend-agent") else "chess-cheater/1.0",
    )
    eng.set_fen(args.fen)
    try:
        if args.multipv > 1:
            results = eng.analyse_multipv(k=args.multipv, depth=None if args.movetime else args.depth, movetime_ms=args.movetime)
            print(json.dumps([r.__dict__ for r in results], indent=2))
        else:
            r = eng.analyse_best(depth=None if args.movetime else args.depth, movetime_ms=args.movetime)
            print(json.dumps(r.__dict__, indent=2))
    finally:
        eng.close()
