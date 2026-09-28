using System.Numerics;
using System.Text;

namespace Tessera;

public enum TokenKind
{
    Ident,       // routine, S64, entry, add, ...
    Value,       // %name
    Pointer,     // #name
    At,          // @
    Int,         // 42, -5, 0xFF
    Byte,        // b'A': a Byte
    Char,        // 'A': a Char, a Unicode scalar value
    Str,         // "..."
    LParen, RParen, LBracket, RBracket, LBrace, RBrace, Lt, Gt,
    Float,       // 3.14, 1.5e10 — IntValue holds the double's bits
    Comma, Colon, ColonEq, Eq, Arrow, Question, Dot, Underscore, Bang,
    Newline,
    Eof,
}

public readonly record struct Pos(string File, int Line, int Col)
{
    public override string ToString() => $"{File}:{Line}:{Col}";
}

/// IntValue holds an integer literal (a magnitude up to 256 bits, with its sign), a character's code, or a float's
/// IEEE double bits.
public sealed record Token(TokenKind Kind, string Text, Pos Pos, BigInteger IntValue = default);

public sealed class CompileError(Pos pos, string message) : Exception($"{pos}: error: {message}")
{
    public Pos Pos { get; } = pos;
}

/// Turns source text into tokens. Layout is not significant beyond newlines: a newline ends a statement unless it
/// is inside (), [], {} or follows a trailing backslash. Consecutive newlines collapse into one token.
public sealed class Lexer(string file, string src)
{
    /// The widest integer types are S256 and U256, so a literal's magnitude is at most 2^256 - 1.
    private const int MaxLiteralBits = 256;
    private static readonly BigInteger MaxLiteral = (BigInteger.One << MaxLiteralBits) - 1;

    private int _i;
    private int _line = 1;
    private int _col = 1;
    private int _depth;
    private readonly List<Token> _tokens = [];

    public List<Token> Lex()
    {
        while (true)
        {
            SkipSpaceAndComments();
            if (_i >= src.Length) break;

            var pos = Here();
            char c = src[_i];

            if (c == ';') throw new CompileError(pos, "comments start with '//' (';' was the comment marker until 2026-09-28)");

            if (c == '\\' && RestOfLineIsBlank(_i + 1))
            {
                // line continuation: drop the backslash and the newline
                Advance();
                SkipSpaceAndComments();
                if (Peek() == '\r') Advance();
                if (Peek() == '\n') Advance();
                continue;
            }

            if (c == '\n' || c == '\r')
            {
                if (c == '\r' && Peek(1) == '\n') Advance();
                Advance();
                // A line that starts with `?` or `:` continues the previous one (a multi-line branch, as the
                // stdlib writes them). No statement can start with either.
                if (NextLineContinues()) continue;
                if (_depth == 0 && _tokens.Count > 0 && _tokens[^1].Kind != TokenKind.Newline)
                    _tokens.Add(new Token(TokenKind.Newline, "\\n", pos));
                continue;
            }

            if (c == '%' || c == '#')
            {
                Advance();
                string name = ReadIdent();
                if (name.Length == 0) throw new CompileError(pos, $"expected a name after '{c}'");
                _tokens.Add(new Token(c == '%' ? TokenKind.Value : TokenKind.Pointer, c + name, pos));
                continue;
            }

            if (c == 'b' && Peek(1) == '\'')
            {
                Advance();
                long v = ReadCharLiteral(pos, byteOnly: true);
                _tokens.Add(new Token(TokenKind.Byte, $"b'{v}'", pos, v));
                continue;
            }

            if (c == '\'')
            {
                long v = ReadCharLiteral(pos, byteOnly: false);
                _tokens.Add(new Token(TokenKind.Char, $"'{v}'", pos, v));
                continue;
            }

            if (char.IsAsciiLetter(c) || (c == '_' && IsIdentChar(Peek(1))))
            {
                string name = ReadIdent();
                _tokens.Add(new Token(TokenKind.Ident, name, pos));
                continue;
            }

            if (char.IsAsciiDigit(c) || (c == '-' && char.IsAsciiDigit(Peek(1))))
            {
                _tokens.Add(ReadNumber(pos));
                continue;
            }

            if (c == '"')
            {
                _tokens.Add(ReadString(pos));
                continue;
            }

            Advance();
            TokenKind kind;
            switch (c)
            {
                case '(': kind = TokenKind.LParen; _depth++; break;
                case ')': kind = TokenKind.RParen; _depth--; break;
                case '[': kind = TokenKind.LBracket; _depth++; break;
                case ']': kind = TokenKind.RBracket; _depth--; break;
                case '{': kind = TokenKind.LBrace; _depth++; break;
                case '}': kind = TokenKind.RBrace; _depth--; break;
                case '<': kind = TokenKind.Lt; break;
                case '>': kind = TokenKind.Gt; break;
                case ',': kind = TokenKind.Comma; break;
                case '?': kind = TokenKind.Question; break;
                case '!': kind = TokenKind.Bang; break;
                case '.': kind = TokenKind.Dot; break;
                case '@': kind = TokenKind.At; break;
                case '_': kind = TokenKind.Underscore; break;
                case '=': kind = TokenKind.Eq; break;
                case ':':
                    if (Peek() == '=') { Advance(); kind = TokenKind.ColonEq; }
                    else kind = TokenKind.Colon;
                    break;
                case '-':
                    if (Peek() == '>') { Advance(); kind = TokenKind.Arrow; break; }
                    throw new CompileError(pos, "unexpected '-' (Tessera has no operators; use .sub())");
                default:
                    throw new CompileError(pos, $"unexpected character '{c}'");
            }
            if (_depth < 0) throw new CompileError(pos, $"unmatched '{c}'");
            _tokens.Add(new Token(kind, c.ToString(), pos));
        }

        if (_tokens.Count > 0 && _tokens[^1].Kind != TokenKind.Newline)
            _tokens.Add(new Token(TokenKind.Newline, "\\n", Here()));
        _tokens.Add(new Token(TokenKind.Eof, "", Here()));
        return _tokens;
    }

    private Pos Here() => new(file, _line, _col);

    private char Peek(int ahead = 0) => _i + ahead < src.Length ? src[_i + ahead] : '\0';

    private void Advance()
    {
        if (src[_i] == '\n') { _line++; _col = 1; }
        else _col++;
        _i++;
    }

    private static bool IsIdentChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    /// `//` starts a comment, and `///` a doc comment, which the lexer skips the same way.
    private bool IsComment(int j) => j + 1 < src.Length && src[j] == '/' && src[j + 1] == '/';

    private void SkipSpaceAndComments()
    {
        while (_i < src.Length)
        {
            char c = src[_i];
            if (c == ' ' || c == '\t' || c == '﻿') Advance();
            else if (IsComment(_i)) { while (_i < src.Length && src[_i] != '\n' && src[_i] != '\r') Advance(); }
            else break;
        }
    }

    private bool NextLineContinues()
    {
        int j = _i;
        while (j < src.Length)
        {
            char c = src[j];
            if (c is ' ' or '\t' or '\r' or '\n') { j++; continue; }
            if (IsComment(j))
            {
                while (j < src.Length && src[j] != '\n') j++;
                continue;
            }
            return c == '?' || (c == ':' && (j + 1 >= src.Length || src[j + 1] != '='));
        }
        return false;
    }

    private bool RestOfLineIsBlank(int from)
    {
        for (int j = from; j < src.Length; j++)
        {
            char c = src[j];
            if (c == '\n' || c == '\r') return true;
            if (IsComment(j)) return true;
            if (c != ' ' && c != '\t') return false;
        }
        return true;
    }

    private string ReadIdent()
    {
        int start = _i;
        while (_i < src.Length && IsIdentChar(src[_i])) Advance();
        return src[start.._i];
    }

    private Token ReadNumber(Pos pos)
    {
        int start = _i;
        bool neg = false;
        if (Peek() == '-') { neg = true; Advance(); }

        int radix = 10;
        if (Peek() == '0' && (Peek(1) is 'x' or 'b' or 'o'))
        {
            radix = Peek(1) switch { 'x' => 16, 'b' => 2, _ => 8 };
            Advance();
            Advance();
        }

        int digitsStart = _i;
        Func<char, bool> isDigit = radix == 10 ? char.IsAsciiDigit : char.IsAsciiHexDigit;
        while (_i < src.Length && (isDigit(src[_i]) || src[_i] == '_')) Advance();

        // A float has a fraction (`1.5`) or an exponent (`1e10`). `0.sub(...)` is an integer and a method call.
        bool fraction = radix == 10 && Peek() == '.' && char.IsAsciiDigit(Peek(1));
        bool exponent = radix == 10 && (Peek() is 'e' or 'E')
                        && (char.IsAsciiDigit(Peek(1)) || (Peek(1) is '+' or '-' && char.IsAsciiDigit(Peek(2))));
        if (fraction || exponent) return ReadFloat(start, pos);

        string digits = src[digitsStart.._i].Replace("_", "");
        if (digits.Length == 0) throw new CompileError(pos, "malformed number");
        if (_i < src.Length && char.IsAsciiLetterOrDigit(src[_i]))
            throw new CompileError(pos, $"unexpected '{src[_i]}' in a number");

        BigInteger mag = BigInteger.Zero;
        foreach (char d in digits)
        {
            int dv = Convert.ToInt32(d.ToString(), 16);
            if (dv >= radix) throw new CompileError(pos, $"digit '{d}' is invalid in base {radix}");
            mag = mag * radix + dv;
            if (mag > MaxLiteral) throw new CompileError(pos, $"integer literal overflows {MaxLiteralBits} bits");
        }
        if (neg && mag > (MaxLiteral >> 1) + 1)
            throw new CompileError(pos, $"integer literal is below the {MaxLiteralBits}-bit range");

        // A magnitude above a type's signed range is that type's two's-complement bit pattern (IntConst).
        BigInteger value = neg ? -mag : mag;
        return new Token(TokenKind.Int, src[start.._i], pos, value);
    }

    private Token ReadFloat(int start, Pos pos)
    {
        if (Peek() == '.')
        {
            Advance();
            while (_i < src.Length && (char.IsAsciiDigit(src[_i]) || src[_i] == '_')) Advance();
        }
        if (Peek() is 'e' or 'E')
        {
            Advance();
            if (Peek() is '+' or '-') Advance();
            while (_i < src.Length && char.IsAsciiDigit(src[_i])) Advance();
        }
        string text = src[start.._i];
        if (!double.TryParse(text.Replace("_", ""), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double value))
            throw new CompileError(pos, $"malformed float literal '{text}'");
        return new Token(TokenKind.Float, text, pos, BitConverter.DoubleToInt64Bits(value));
    }

    private long ReadCharLiteral(Pos pos, bool byteOnly)
    {
        Advance(); // opening quote
        long v;
        char c = Peek();
        if (c == '\\')
        {
            Advance();
            char e = Peek();
            Advance();
            v = e switch
            {
                'n' => 10, 't' => 9, 'r' => 13, '0' => 0, '\\' => '\\', '\'' => '\'', '"' => '"',
                'x' => ReadHexByte(pos),
                _ => throw new CompileError(pos, $"unknown escape '\\{e}'"),
            };
        }
        else if (byteOnly)
        {
            if (c > 0x7F) throw new CompileError(pos, "a byte literal must hold one ASCII character");
            v = c;
            Advance();
        }
        else if (char.IsHighSurrogate(c) && char.IsLowSurrogate(Peek(1)))
        {
            v = char.ConvertToUtf32(c, Peek(1));
            Advance();
            Advance();
        }
        else
        {
            v = c;
            Advance();
        }
        if (Peek() != '\'')
            throw new CompileError(pos, byteOnly
                ? "a byte literal must hold exactly one byte"
                : "a character literal must hold exactly one character");
        Advance();
        return v;
    }

    private long ReadHexByte(Pos pos)
    {
        string hex = $"{Peek()}{Peek(1)}";
        if (!char.IsAsciiHexDigit(hex[0]) || !char.IsAsciiHexDigit(hex[1]))
            throw new CompileError(pos, "\\x needs two hex digits");
        Advance();
        Advance();
        return Convert.ToInt64(hex, 16);
    }

    private Token ReadString(Pos pos)
    {
        Advance();
        var sb = new StringBuilder();
        while (true)
        {
            if (_i >= src.Length || Peek() == '\n') throw new CompileError(pos, "unterminated string literal");
            char c = Peek();
            Advance();
            if (c == '"') break;
            if (c != '\\') { sb.Append(c); continue; }
            char e = Peek();
            Advance();
            sb.Append(e switch
            {
                'n' => '\n', 't' => '\t', 'r' => '\r', '0' => '\0', '\\' => '\\', '"' => '"', '\'' => '\'',
                'x' => (char)ReadHexByte(pos),
                _ => throw new CompileError(pos, $"unknown escape '\\{e}'"),
            });
        }
        return new Token(TokenKind.Str, sb.ToString(), pos);
    }
}
