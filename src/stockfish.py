import requests
from typing import Optional, Dict

STOCKFISH_API = "https://stockfish.online/api/s/v2.php"

def analyze_fen(fen: str, depth: int = 15, timeout: float = 20.0) -> Dict:
    """
    Send a GET request to the Stockfish REST API for a given FEN.
    Depth defaults to 15 (good calculation vs. speed).
    Returns the API's JSON as a Python dict.

    Raises requests.HTTPError on non-2xx responses.
    """
    params = {
        "fen": fen,
        "depth": str(depth)
    }
    resp = requests.get(STOCKFISH_API, params=params, timeout=timeout, headers={
        "Accept": "application/json"
    })
    resp.raise_for_status()  # raise if HTTP error
    data = resp.json()

    # Optional sanity checks
    if not isinstance(data, dict) or not data.get("success", False):
        raise RuntimeError(f"Engine call failed or unexpected response: {data}")

    return data

def describe_result(result: Dict) -> str:
    """
    Create a short human-readable summary from the API result.
    Known keys include: success, evaluation, mate, bestmove, continuation.
    """
    best = result.get("bestmove")
    eval_cp = result.get("evaluation")  # centipawns (positive = White better)
    mate_in = result.get("mate")        # e.g., 3 means mate in 3 for side to move
    cont = result.get("continuation")

    if mate_in is not None:
        eval_text = f"mate in {mate_in}"
    elif eval_cp is not None:
        eval_text = f"eval ≈ {eval_cp} (cp)"
    else:
        eval_text = "no evaluation"

    return f"Best move: {best} | {eval_text}\nLine: {cont}"

if __name__ == "__main__":
    # Example usage
    example_fen = input("Enter FEN: ").strip()
    try:
        res = analyze_fen(example_fen, depth=15)
        print(describe_result(res))
        # If you need raw fields:
        # print(res)  # {'success': True, 'evaluation': 0.97, 'mate': None, 'bestmove': 'b7b6', ...}
    except Exception as e:
        print("Error:", e)
