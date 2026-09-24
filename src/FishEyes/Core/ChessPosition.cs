using System.Text;

namespace FishEyes;

// A deliberately conservative position: no castling or en passant rights can
// be established from a screenshot. All move checks use that same assumption.
public sealed class ChessPosition(char[] squares)
{
    public char[] Squares { get; } = (char[])squares.Clone(); // a8 ... h1; '.' = empty
    public string Placement
    {
        get
        {
            var text = new StringBuilder();
            for (int row = 0; row < 8; row++)
            {
                int empty = 0;
                for (int col = 0; col < 8; col++)
                {
                    char piece = Squares[row * 8 + col];
                    if (piece == '.') { empty++; continue; }
                    if (empty > 0) { text.Append(empty); empty = 0; }
                    text.Append(piece);
                }
                if (empty > 0) text.Append(empty);
                if (row != 7) text.Append('/');
            }
            return text.ToString();
        }
    }
    public string Fen(bool white) => $"{Placement} {(white ? 'w' : 'b')} - - 0 1";
    public static ChessPosition Parse(string placement)
    {
        var chars = new List<char>();
        foreach (char c in placement.Split(' ')[0])
            if (c is >= '1' and <= '8') chars.AddRange(Enumerable.Repeat('.', c - '0'));
            else if (c != '/') chars.Add(c);
        if (chars.Count != 64 || chars.Any(c => !".PNBRQKpnbrqk".Contains(c)))
            throw new ArgumentException("Invalid chess position.");
        return new(chars.ToArray());
    }
    public string? InvalidReason(bool white)
    {
        if (Squares.Length != 64 || Squares.Count(c => c == 'K') != 1 || Squares.Count(c => c == 'k') != 1)
            return "Both kings must be visible.";
        foreach (bool side in new[] { true, false })
        {
            if (Squares.Count(c => c != '.' && char.IsUpper(c) == side) > 16 ||
                Squares.Count(c => c == (side ? 'P' : 'p')) > 8)
                return "Too many pieces detected.";
        }
        if (Squares.Take(8).Concat(Squares.Skip(56)).Any(c => char.ToLowerInvariant(c) == 'p'))
            return "A pawn was detected on the last rank.";
        if (IsAttacked(Array.IndexOf(Squares, white ? 'k' : 'K'), white))
            return "This turn assumption is illegal (opposing king in check).";
        return null;
    }
    public bool IsAttacked(int target, bool byWhite)
    {
        if (target < 0) return true;
        for (int from = 0; from < 64; from++)
        {
            char p = Squares[from];
            if (p != '.' && char.IsUpper(p) == byWhite && Reaches(from, target, true)) return true;
        }
        return false;
    }
    private bool Reaches(int from, int to, bool attack)
    {
        if (from == to) return false;
        char p = Squares[from];
        int dr = to / 8 - from / 8, dc = to % 8 - from % 8;
        int ar = Math.Abs(dr), ac = Math.Abs(dc);
        switch (char.ToLowerInvariant(p))
        {
            case 'p':
                int step = char.IsUpper(p) ? -1 : 1;
                if (attack) return dr == step && ac == 1;
                if (ac == 1) return dr == step && Squares[to] != '.';
                if (dc != 0 || Squares[to] != '.') return false;
                return dr == step || (dr == 2 * step && from / 8 == (step == -1 ? 6 : 1) && Squares[from + step * 8] == '.');
            case 'n': return (ar == 1 && ac == 2) || (ar == 2 && ac == 1);
            case 'k': return Math.Max(ar, ac) == 1;
            case 'b': if (ar != ac) return false; break;
            case 'r': if (dr != 0 && dc != 0) return false; break;
            case 'q': if (dr != 0 && dc != 0 && ar != ac) return false; break;
            default: return false;
        }
        int delta = Math.Sign(dr) * 8 + Math.Sign(dc);
        for (int square = from + delta; square != to; square += delta)
            if (Squares[square] != '.') return false;
        return true;
    }
    public bool IsLegal(string move, bool white)
    {
        if (move.Length is not (4 or 5) || !TrySquare(move[..2], out int from) || !TrySquare(move.Substring(2, 2), out int to)) return false;
        char p = Squares[from], captured = Squares[to];
        if (p == '.' || char.IsUpper(p) != white || (captured != '.' && (char.IsUpper(captured) == white || char.ToLowerInvariant(captured) == 'k'))) return false;
        bool promotion = char.ToLowerInvariant(p) == 'p' && to / 8 is 0 or 7;
        if (promotion != (move.Length == 5) || (promotion && !"qrbn".Contains(move[4]))) return false;
        if (!Reaches(from, to, false)) return false;
        var after = (char[])Squares.Clone();
        after[from] = '.';
        after[to] = promotion ? (white ? char.ToUpperInvariant(move[4]) : move[4]) : p;
        var next = new ChessPosition(after);
        return !next.IsAttacked(Array.IndexOf(after, white ? 'K' : 'k'), !white);
    }
    public bool HasLegalMove(bool white)
    {
        for (int from = 0; from < 64; from++)
            if (Squares[from] != '.' && char.IsUpper(Squares[from]) == white)
                for (int to = 0; to < 64; to++)
                {
                    string move = SquareName(from) + SquareName(to);
                    if (char.ToLowerInvariant(Squares[from]) == 'p' && to / 8 is 0 or 7) move += "q";
                    if (IsLegal(move, white)) return true;
                }
        return false;
    }
    public static string SquareName(int index) => $"{(char)('a' + index % 8)}{8 - index / 8}";
    public static bool TrySquare(string square, out int index)
    {
        index = -1;
        if (square.Length != 2 || square[0] is < 'a' or > 'h' || square[1] is < '1' or > '8') return false;
        index = (8 - (square[1] - '0')) * 8 + square[0] - 'a';
        return true;
    }
}
