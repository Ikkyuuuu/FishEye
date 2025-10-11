# recognize_pieces.py
# pip install opencv-python numpy

from __future__ import annotations
import os
from typing import Dict, List, Optional, Tuple
import cv2
import numpy as np

PIECE_NAMES = ["wP","wN","wB","wR","wQ","wK","bP","bN","bB","bR","bQ","bK"]

# ------------------------- template IO -------------------------

def load_templates(template_dir: str) -> Dict[str, Tuple[np.ndarray, Optional[np.ndarray]]]:
    """
    Load piece templates as (gray, mask) where mask is from alpha channel.
    If no alpha, mask=None. We use TM_CCORR_NORMED with mask support.
    """
    tmpls: Dict[str, Tuple[np.ndarray, Optional[np.ndarray]]] = {}
    for name in PIECE_NAMES:
        p = os.path.join(template_dir, f"{name}.png")
        if not os.path.exists(p):
            continue
        img = cv2.imread(p, cv2.IMREAD_UNCHANGED)  # may be RGBA
        if img is None:
            continue
        if img.ndim == 3 and img.shape[2] == 4:
            bgr = img[:, :, :3]
            alpha = img[:, :, 3]
            gray = cv2.cvtColor(bgr, cv2.COLOR_BGR2GRAY)
            # Build a binary mask from alpha (>0 where piece exists).
            mask = (alpha > 0).astype(np.uint8) * 255
        else:
            gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
            mask = None
        tmpls[name] = (gray, mask)
    if len(tmpls) < 12:
        missing = [n for n in PIECE_NAMES if n not in tmpls]
        print(f"[recognize_pieces] Warning: missing templates: {missing}")
    return tmpls

# ------------------------- preprocessing & empties -------------------------

def is_empty_square(square_bgr: np.ndarray, var_thresh: float = 55.0, edge_thresh: float = 0.018) -> bool:
    """
    Conservative empty test (variance + edge density).
    Raise thresholds to reduce false piece detections on textured squares.
    """
    g = cv2.cvtColor(square_bgr, cv2.COLOR_BGR2GRAY)
    if g.var() < var_thresh:
        return True
    edges = cv2.Canny(g, 60, 180)
    density = float(np.count_nonzero(edges)) / edges.size
    return density < edge_thresh

def prep_gray(square_bgr: np.ndarray) -> np.ndarray:
    g = cv2.cvtColor(square_bgr, cv2.COLOR_BGR2GRAY)
    g = cv2.GaussianBlur(g, (3,3), 0)
    return g

def _resize_keep_h(img: np.ndarray, target_h: int) -> np.ndarray:
    h, w = img.shape[:2]
    scale = float(target_h) / max(1, h)
    return cv2.resize(img, (max(1, int(w*scale)), target_h), interpolation=cv2.INTER_AREA)

# ------------------------- matching (masked) -------------------------

def match_piece(square_bgr: np.ndarray,
               templates: Dict[str, Tuple[np.ndarray, Optional[np.ndarray]]],
               empty_first: bool = True,
               accept_thresh: float = 0.62) -> Tuple[Optional[str], float]:
    """
    Returns (label, score). Uses TM_CCORR_NORMED with alpha masks when available.
    """
    if empty_first and is_empty_square(square_bgr):
        return None, 0.0

    sq = prep_gray(square_bgr)
    best_name, best_val = None, -1.0

    # Try a few scales for robustness (piece size within square varies by theme)
    for name, (tmpl_gray, tmpl_mask) in templates.items():
        for scale in (0.70, 0.80, 0.90):
            t = _resize_keep_h(tmpl_gray, max(12, int(scale * sq.shape[0])))
            m = None
            if tmpl_mask is not None:
                m = _resize_keep_h(tmpl_mask, t.shape[0])
            # TM_CCORR_NORMED supports mask in OpenCV >= 4.2 for 8U templates
            res = cv2.matchTemplate(sq, t, cv2.TM_CCORR_NORMED, mask=m)
            _, max_val, _, _ = cv2.minMaxLoc(res)
            if max_val > best_val:
                best_val, best_name = max_val, name

    if best_val < accept_thresh:
        return None, float(best_val)
    return best_name, float(best_val)

def recognize_warped_board(warped_bgr: np.ndarray,
                           templates: Dict[str, Tuple[np.ndarray, Optional[np.ndarray]]]) -> Tuple[List[Optional[str]], List[float]]:
    """
    Split the warped board and return labels64 (a8..h1) + scores.
    """
    h, w = warped_bgr.shape[:2]
    s = h // 8
    labels, scores = [], []
    for r in range(8):
        for c in range(8):
            y0, y1 = r*s, (r+1)*s
            x0, x1 = c*s, (c+1)*s
            sq = warped_bgr[y0:y1, x0:x1]
            lab, sc = match_piece(sq, templates)
            labels.append(lab)
            scores.append(sc)
    return labels, scores
