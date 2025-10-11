# detect_board.py
# pip install opencv-python numpy
import cv2, os, argparse
import numpy as np

FILES = "abcdefgh"
RANKS = "87654321"  # after warp, top row is rank 8

# ---------- geometry / detection ----------
def _order_corners(pts4):
    pts = pts4.reshape(4, 2).astype(np.float32)
    s = pts.sum(axis=1); diff = np.diff(pts, axis=1).reshape(-1)
    tl = pts[np.argmin(s)]; br = pts[np.argmax(s)]
    tr = pts[np.argmin(diff)]; bl = pts[np.argmax(diff)]
    return np.array([tl, tr, br, bl], dtype=np.float32)

def _quad_squareness_score(quad):
    q = _order_corners(quad)
    d01 = np.linalg.norm(q[1]-q[0]); d12 = np.linalg.norm(q[2]-q[1])
    d23 = np.linalg.norm(q[3]-q[2]); d30 = np.linalg.norm(q[0]-q[3])
    aspect = (min(d01,d23)/(max(d01,d23)+1e-6))*(min(d12,d30)/(max(d12,d30)+1e-6))
    def cos(a,b,c):
        v1=a-b; v2=c-b
        return np.dot(v1,v2)/(np.linalg.norm(v1)*np.linalg.norm(v2)+1e-6)
    rightness = 1 - np.mean([abs(cos(q[0],q[1],q[2])),
                              abs(cos(q[1],q[2],q[3])),
                              abs(cos(q[2],q[3],q[0])),
                              abs(cos(q[3],q[0],q[1]))])
    area = cv2.contourArea(quad.astype(np.float32))
    return (0.5*aspect + 0.5*rightness) * (area + 1.0)

def find_board_corners(bgr, canny=(60,180), blur=5, top_k=10):
    gray = cv2.cvtColor(bgr, cv2.COLOR_BGR2GRAY)
    if blur and blur > 0:
        gray = cv2.GaussianBlur(gray, (blur, blur), 0)
    edges = cv2.Canny(gray, canny[0], canny[1])
    contours,_ = cv2.findContours(edges, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    if not contours: raise RuntimeError("No contours found")
    candidates=[]
    for cnt in sorted(contours, key=cv2.contourArea, reverse=True)[:max(50, top_k)]:
        peri=cv2.arcLength(cnt, True)
        approx=cv2.approxPolyDP(cnt, 0.02*peri, True)
        if len(approx)==4 and cv2.isContourConvex(approx):
            candidates.append(approx.reshape(4,2))
    if not candidates:
        for cnt in sorted(contours, key=cv2.contourArea, reverse=True)[:max(200,5*top_k)]:
            peri=cv2.arcLength(cnt, True)
            approx=cv2.approxPolyDP(cnt, 0.03*peri, True)
            if len(approx)==4 and cv2.isContourConvex(approx):
                candidates.append(approx.reshape(4,2))
    if not candidates:
        raise RuntimeError("Could not find a convex quadrilateral that looks like a board.")
    return _order_corners(max(candidates, key=_quad_squareness_score))

def warp_board(bgr, corners, out_size=800):
    dst = np.array([[0,0],[out_size-1,0],[out_size-1,out_size-1],[0,out_size-1]], dtype=np.float32)
    M = cv2.getPerspectiveTransform(corners.astype(np.float32), dst)
    warped = cv2.warpPerspective(bgr, M, (out_size, out_size))
    return warped, M

def _mean_brightness(img): return float(cv2.cvtColor(img, cv2.COLOR_BGR2GRAY).mean())

def rotate_to_a1_dark(board_img, N=8):
    h,w = board_img.shape[:2]; s = h//N
    def bl(img): return _mean_brightness(img[(N-1)*s:N*s, 0:s])
    best_img, best_k, best_score = board_img, 0, -1e9
    cur = board_img
    for k in range(4):
        a1 = bl(cur)
        a2 = _mean_brightness(cur[(N-2)*s:(N-1)*s, 0:s])
        b1 = _mean_brightness(cur[(N-1)*s:N*s, 1*s:2*s])
        score = -(a1 - 0.5*(a2+b1))   # darker a1 => higher score
        if score > best_score:
            best_img, best_k, best_score = cur, k, score
        cur = cv2.rotate(cur, cv2.ROTATE_90_CLOCKWISE)
    return best_img, best_k

def detect_and_warp_board(bgr, out_size=800, ensure_a1_dark=True):
    corners = find_board_corners(bgr)
    warped, M = warp_board(bgr, corners, out_size)
    k = 0
    if ensure_a1_dark:
        warped, k = rotate_to_a1_dark(warped, 8)
    return warped, corners, M, k

# ---------- grid ----------
def draw_grid_overlay(board_img, line_thickness=2):
    N=8; h,w = board_img.shape[:2]; s_h, s_w = h//N, w//N
    vis = board_img.copy()
    for r in range(1,N):
        y=r*s_h; cv2.line(vis,(0,y),(w-1,y),(0,0,0),line_thickness,cv2.LINE_AA)
    for c in range(1,N):
        x=c*s_w; cv2.line(vis,(x,0),(x,h-1),(0,0,0),line_thickness,cv2.LINE_AA)
    font=cv2.FONT_HERSHEY_SIMPLEX; scale=max(0.4, s_h/160.0); thick=max(1,int(s_h/200))
    for c,f in enumerate(FILES):
        x=c*s_w+int(0.05*s_w); y=h-int(0.08*s_h)
        cv2.putText(vis,f,(x,y),font,scale,(0,0,0),thick,cv2.LINE_AA)
    for r,rk in enumerate(RANKS):
        x=int(0.05*s_w); y=r*s_h+int(0.15*s_h)
        cv2.putText(vis,rk,(x,y),font,scale,(0,0,0),thick,cv2.LINE_AA)
    return vis

def squares_with_metadata(board_img, N=8):
    h,w = board_img.shape[:2]; s_h, s_w = h//N, w//N
    out=[]
    for r in range(N):
        for c in range(N):
            name=f"{FILES[c]}{RANKS[r]}"
            x0,y0=c*s_w, r*s_h; x1,y1=(c+1)*s_w, (r+1)*s_h
            out.append({"name":name,"xyxy":(x0,y0,x1,y1)})
    return out

# ---------- CLI ----------
def main():
    ap = argparse.ArgumentParser(description="Detect + warp chessboard, draw grid, save crops/overlay.")
    ap.add_argument("image", help="Path to input photo/screenshot")
    ap.add_argument("--size", type=int, default=800, help="Output square size (px)")
    ap.add_argument("--no-a1-fix", action="store_true", help="Skip auto-rotate to make a1 dark")
    ap.add_argument("--show", action="store_true", help="Show windows for warped and grid overlay")
    ap.add_argument("--save-squares", action="store_true", help="Save 64 crops named a8..h1.png")
    ap.add_argument("--outdir", default="out_squares", help="Directory to save square crops")
    ap.add_argument("--save-grid", default="", help="Path to save grid overlay PNG (e.g., grid.png)")
    args = ap.parse_args()

    img = cv2.imread(args.image)
    if img is None: raise SystemExit(f"Could not read: {args.image}")

    warped, corners, M, k = detect_and_warp_board(img, args.size, ensure_a1_dark=not args.no_a1_fix)
    overlay = draw_grid_overlay(warped)

    print("Corners (tl,tr,br,bl):\n", corners.astype(int))
    print("Homography M:\n", M)
    print(f"Rotations applied (90° CW): {k}")

    if args.save_squares:
        os.makedirs(args.outdir, exist_ok=True)
        for info in squares_with_metadata(warped):
            x0,y0,x1,y1 = info["xyxy"]
            crop = warped[y0:y1, x0:x1]
            cv2.imwrite(os.path.join(args.outdir, f"{info['name']}.png"), crop)
        print(f"Saved 64 squares to: {args.outdir}")

    if args.save_grid:
        cv2.imwrite(args.save_grid, overlay)
        print(f"Grid overlay saved to: {args.save_grid}")

    if args.show:
        cv2.imshow("warped", warped)
        cv2.imshow("grid", overlay)
        cv2.waitKey(0); cv2.destroyAllWindows()

if __name__ == "__main__":
    main()
