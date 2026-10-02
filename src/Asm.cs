using System.Numerics;
using System.Text;

namespace Tessera;

/// An `#external("asm")` routine lowered to one LLVM inline-assembly call: its text, the operands the builder places,
/// and what it changes. Outputs come first in LLVM's operand numbering, then the inputs, each tied to its parameter's
/// output (the builder never assumes an input register keeps its value).
public sealed record AsmPlan(
    string Text,
    bool Intel,
    List<(string Constraint, DType Type)> Outputs,
    List<(string Constraint, int Param)> Inputs,
    List<string> Clobbers,
    List<int> Results,
    bool SideEffect,
    string? Memory,
    bool Naked = false);

public sealed partial class Compiler
{
    private readonly Dictionary<string, AsmPlan> _asmPlans = [];

    public static bool IsAsm(RoutineDecl r) => r.Attr("external") is { First: "asm" };

    /// A `#naked` assembly routine: a function of its own whose whole body is the assembly, not put in place.
    public static bool IsNaked(RoutineDecl r) => IsAsm(r) && r.Attr("naked") is not null;

    /// The plan of an assembly routine's instance, built once and checked even if the routine is never called.
    public AsmPlan PlanAsm(Instance sig)
    {
        if (!_asmPlans.TryGetValue(sig.Symbol, out var plan))
            _asmPlans[sig.Symbol] = plan = new AsmLowering(this, sig).Plan();
        return plan;
    }
}

/// Reads an assembly routine's body literally: the method is the mnemonic, the receiver the first operand, the
/// arguments the rest, and the type arguments the destination's type and the sources'.
internal sealed class AsmLowering
{
    private enum Arch { X86, A64, Rv }

    private enum RegClass { Gpr, Vec, Fpr, Sp, Zero, Pc, Flags, Fs, Gs }

    /// A register as the body names it. Index is the encoding's number for the numbered classes.
    private sealed record Reg(RegClass Class, int Index, string Written);

    /// A result of the routine: a parameter's register, or a fixed register.
    private sealed record Result(int Param, Reg? Reg, DType Type);

    private static readonly string[] X86Names64 =
        ["rax", "rcx", "rdx", "rbx", "rsp", "rbp", "rsi", "rdi", "r8", "r9", "r10", "r11", "r12", "r13", "r14", "r15"];
    private static readonly string[] X86Names32 =
        ["eax", "ecx", "edx", "ebx", "esp", "ebp", "esi", "edi", "r8d", "r9d", "r10d", "r11d", "r12d", "r13d", "r14d", "r15d"];
    private static readonly string[] X86Names16 =
        ["ax", "cx", "dx", "bx", "sp", "bp", "si", "di", "r8w", "r9w", "r10w", "r11w", "r12w", "r13w", "r14w", "r15w"];
    private static readonly string[] X86Names8 =
        ["al", "cl", "dl", "bl", "spl", "bpl", "sil", "dil", "r8b", "r9b", "r10b", "r11b", "r12b", "r13b", "r14b", "r15b"];

    private readonly Compiler _c;
    private readonly Instance _sig;
    private readonly RoutineDecl _r;
    private readonly Arch _arch;

    /// Each parameter's output index; a parameter's register is the same one in and out.
    private readonly int[] _paramOutput;
    private readonly List<(string Constraint, DType Type)> _outputs = [];
    /// The fixed registers that are outputs, by LLVM register name, with their output index.
    private readonly Dictionary<string, int> _fixedOutputs = [];
    /// Every fixed register the body names, by LLVM register name: each counts as changed.
    private readonly Dictionary<string, Pos> _named = [];
    private bool _flagsTested;
    /// `#naked`: the body is a whole function, entered by a call under the C convention and left by `ret`.
    private readonly bool _naked;

    public AsmLowering(Compiler c, Instance sig)
    {
        _c = c;
        _sig = sig;
        _r = sig.Decl;
        if (_r.Attr("target") is not { } target || !target.Args.Any(a => a is { Key: "arch", Negated: false }))
            throw new CompileError(_r.Pos,
                $"assembly belongs to one architecture: give '{_r.DisplayName}' #target(arch: \"{c.Target.Arch}\")");
        _arch = c.Target.Arch switch
        {
            "x86_64" => Arch.X86,
            "aarch64" => Arch.A64,
            "riscv64" or "riscv32" => Arch.Rv,
            var other => throw new CompileError(_r.Pos,
                $"assembly is written for x86_64, aarch64, riscv64 and riscv32; '{_r.DisplayName}' is built for {other}"),
        };
        _paramOutput = new int[_r.Params.Count];
        _naked = _r.Attr("naked") is not null;
    }

    public AsmPlan Plan()
    {
        CheckDeclaration();
        var results = Results();
        if (_naked)
        {
            CheckNakedRegisters(results);
            return new AsmPlan(Body(), _arch == Arch.X86, [], [], [], [], SideEffect: true, Memory: null, Naked: true);
        }

        // Every parameter is an output tied to its input, in its own register or the one after its '='.
        var inputs = new List<(string, int)>();
        for (int i = 0; i < _r.Params.Count; i++)
        {
            var t = _sig.Params[i];
            if (_r.Params[i].Register is { } placed)
            {
                var reg = ParamRegister(i);
                string name = LlvmName(reg, t);
                if (_fixedOutputs.ContainsKey(name))
                    throw new CompileError(placed.Pos, $"two parameters arrive in {Shown(reg)}");
                _paramOutput[i] = AddOutput($"={{{name}}}", t);
                _fixedOutputs[name] = _paramOutput[i];
            }
            else _paramOutput[i] = AddOutput("=" + Letter(ClassOf(t, _r.Params[i].Pos), t), t);
            inputs.Add((_paramOutput[i].ToString(), i));
        }

        // A fixed result register that no parameter arrives in is an output of its own.
        var resultOutputs = new List<int>();
        foreach (var item in results)
        {
            if (item.Reg is null) { resultOutputs.Add(_paramOutput[item.Param]); continue; }
            string name = LlvmName(item.Reg, item.Type);
            if (!_fixedOutputs.TryGetValue(name, out int index))
            {
                index = AddOutput($"={{{name}}}", item.Type);
                _fixedOutputs[name] = index;
            }
            else if (!_outputs[index].Type.Equals(item.Type))
                throw new CompileError(_r.Pos, $"{Shown(item.Reg)} is both {_outputs[index].Type} and {item.Type}");
            resultOutputs.Add(index);
        }

        string text = Body();
        return new AsmPlan(text, _arch == Arch.X86, _outputs, inputs, Clobbers(), resultOutputs,
            SideEffect: _r.Attr("pure") is null && _r.Attr("readonly") is null,
            Memory: _r.Attr("pure") is not null ? "none" : _r.Attr("readonly") is not null ? "read" : null);
    }

    // ── #naked ──────────────────────────────────────────────────────────────

    /// A #naked routine is entered by a call, so each parameter is where the C calling convention puts it, and the
    /// result goes where it expects one. Each parameter names that register (`%from: @Addr = REG7`), and the builder
    /// checks it against the convention, since the routine itself can't move anything.
    private void CheckNakedRegisters(List<Result> results)
    {
        int ints = 0, floats = 0;
        for (int i = 0; i < _r.Params.Count; i++)
        {
            var p = _r.Params[i];
            var cls = ClassOf(_sig.Params[i], p.Pos);
            if (_sig.Params[i].Repr is VectorType)
                throw new CompileError(p.Pos, "a #naked routine takes integers, pointers and floats, which arrive in registers");
            var abi = cls == RegClass.Gpr ? ArgRegister(true, ints++, i, p.Pos) : ArgRegister(false, floats++, i, p.Pos);
            if (p.Register is not { } placed)
                throw new CompileError(p.Pos,
                    $"a #naked routine names where each parameter arrives: {p.Name}: {_sig.Params[i]} = {abi.Written}");
            var reg = ParamRegister(i);
            if (reg.Class != abi.Class || reg.Index != abi.Index)
                throw new CompileError(placed.Pos,
                    $"{_c.Target.Arch}-{_c.Target.Os}'s calling convention passes {p.Name} in {Shown(abi)}, not {Shown(reg)}: "
                    + $"{p.Name}: {_sig.Params[i]} = {abi.Written}");
        }
        foreach (var item in results)
        {
            if (item.Reg is null)
                throw new CompileError(_r.Pos, "a #naked routine returns in the convention's result register, not a parameter's");
            bool isInt = ClassOf(item.Type, _r.Pos) == RegClass.Gpr;
            var abi = new Reg(isInt ? RegClass.Gpr : _arch == Arch.Rv ? RegClass.Fpr : RegClass.Vec,
                _arch == Arch.Rv ? 10 : 0, "");
            abi = abi with { Written = WrittenName(abi) };
            if (results.Count > 1 || item.Reg.Class != abi.Class || item.Reg.Index != abi.Index)
                throw new CompileError(_r.Pos, $"a #naked routine returns its {item.Type} in {Shown(abi)}: return({abi.Written})");
        }
    }

    /// The register the C calling convention passes the n-th integer (or float) argument in. `position` is the
    /// parameter's place among all of them, which Windows x64 counts instead.
    private Reg ArgRegister(bool integer, int n, int position, Pos pos)
    {
        int[] sysV = [7, 6, 2, 1, 8, 9], win64 = [1, 2, 8, 9];
        bool windows = _arch == Arch.X86 && _c.Target.Os == "windows";
        int index = windows ? position : n;
        int count = (_arch, integer) switch
        {
            (Arch.X86, true) => windows ? 4 : 6,
            (Arch.X86, false) => windows ? 4 : 8,
            _ => 8,
        };
        if (index >= count)
            throw new CompileError(pos, "this parameter goes on the stack, and a #naked routine reads only parameters in registers");
        var reg = (_arch, integer) switch
        {
            (Arch.X86, true) => new Reg(RegClass.Gpr, windows ? win64[index] : sysV[index], ""),
            (Arch.X86, false) => new Reg(RegClass.Vec, index, ""),
            (Arch.A64, true) => new Reg(RegClass.Gpr, index, ""),
            (Arch.A64, false) => new Reg(RegClass.Vec, index, ""),
            (_, true) => new Reg(RegClass.Gpr, 10 + index, ""),
            _ => new Reg(RegClass.Fpr, 10 + index, ""),
        };
        return reg with { Written = WrittenName(reg) };
    }

    private static string WrittenName(Reg r) =>
        (r.Class switch { RegClass.Vec => "VREG", RegClass.Fpr => "FREG", _ => "REG" }) + r.Index;

    private int AddOutput(string constraint, DType type)
    {
        _outputs.Add((constraint, type));
        return _outputs.Count - 1;
    }

    private void CheckDeclaration()
    {
        if (_r.TypeParams.Count != 0)
            throw new CompileError(_r.Pos, $"an assembly routine takes no type parameters, and '{_r.DisplayName}' has some");
        if (_r.Attr("pure") is not null && _r.Attr("readonly") is not null)
            throw new CompileError(_r.Pos, "#pure already touches no memory; #readonly says less, so write one of them");
        if (_naked)
        {
            foreach (var name in new[] { "clobbers", "pure", "readonly", "inline", "callconv" })
                if (_r.Attr(name) is { } a)
                    throw new CompileError(a.Pos, name == "inline"
                        ? "a #naked routine is called, never put in place"
                        : $"a #naked routine keeps the C calling convention by itself, so it takes no #{name}");
        }
        else if (_r.Attr("inline") is not null || _r.Attr("noinline") is not null || _r.Attr("export") is not null)
            throw new CompileError(_r.Pos, "an assembly routine is put in place at each call; it has no function to inline, keep, or export");
        foreach (var b in _r.Blocks!)
            if (b.Params.Count != 0)
                throw new CompileError(b.Pos, "an assembly block takes no parameters: registers carry values from block to block");
    }

    // ── Registers ───────────────────────────────────────────────────────────

    /// The register a bare name names, or an error saying what the target has.
    private Reg Register(string name, Pos pos)
    {
        Reg? r = name switch
        {
            "SP" => _arch switch { Arch.X86 => new Reg(RegClass.Gpr, 4, name), Arch.A64 => new Reg(RegClass.Sp, 31, name), _ => new Reg(RegClass.Gpr, 2, name) },
            "ZERO" => _arch switch { Arch.A64 => new Reg(RegClass.Zero, 31, name), Arch.Rv => new Reg(RegClass.Gpr, 0, name), _ => null },
            "FP" => _arch == Arch.A64 ? new Reg(RegClass.Gpr, 29, name) : null,
            "LR" => _arch switch { Arch.A64 => new Reg(RegClass.Gpr, 30, name), Arch.Rv => new Reg(RegClass.Gpr, 1, name), _ => null },
            "PC" => new Reg(RegClass.Pc, 0, name),
            "FLAGS" => _arch == Arch.Rv ? null : new Reg(RegClass.Flags, 0, name),
            "FS" => _arch == Arch.X86 ? new Reg(RegClass.Fs, 0, name) : null,
            "GS" => _arch == Arch.X86 ? new Reg(RegClass.Gs, 0, name) : null,
            _ => Numbered(name),
        };
        if (r is null)
            throw new CompileError(pos, $"{_c.Target.Arch} has no register {name}");
        if (_arch == Arch.A64 && r is { Class: RegClass.Gpr, Index: 31 })
            throw new CompileError(pos, "aarch64's x31 is the stack pointer in some instructions and the zero register in others: write SP or ZERO");
        return r;
    }

    private Reg? Numbered(string name)
    {
        foreach (var (prefix, cls) in new[] { ("REG", RegClass.Gpr), ("VREG", RegClass.Vec), ("FREG", RegClass.Fpr) })
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.Length == prefix.Length) continue;
            string digits = name[prefix.Length..];
            if (!digits.All(char.IsAsciiDigit) || (digits.Length > 1 && digits[0] == '0')) return null;
            int n = int.Parse(digits);
            int count = (cls, _arch) switch
            {
                (RegClass.Gpr, Arch.X86) => 16,
                (RegClass.Gpr, _) => 32,
                (RegClass.Fpr, Arch.Rv) => 32,
                (RegClass.Fpr, _) => 0,
                _ => 32,
            };
            return n < count ? new Reg(cls, n, name) : null;
        }
        return null;
    }

    /// The register after a parameter's '=': `%hi: U64 = REG2`.
    private Reg ParamRegister(int param)
    {
        var placed = _r.Params[param].Register!;
        if (placed.Escaped) throw new CompileError(placed.Pos, $"a register is a bare name, not `{placed.Text}`");
        var reg = Register(placed.Text, placed.Pos);
        var cls = ClassOf(_sig.Params[param], _r.Params[param].Pos);
        if (reg.Class != cls || IsStackOrZero(reg) || reg.Class is RegClass.Sp or RegClass.Zero)
            throw new CompileError(placed.Pos, $"{_r.Params[param].Name} is {_sig.Params[param]}, which can't arrive in {Shown(reg)}");
        return reg;
    }

    /// The register class a value of this type is placed in.
    private RegClass ClassOf(DType t, Pos pos) => t.Repr switch
    {
        IntType or BoolType or PtrType or CallableType => RegClass.Gpr,
        FloatType => _arch == Arch.Rv ? RegClass.Fpr : RegClass.Vec,
        VectorType => RegClass.Vec,
        _ => throw new CompileError(pos, $"an assembly operand is an integer, a float, a pointer or a vector, not {t}"),
    };

    private int Bits(DType t, Pos pos) => t.Repr switch
    {
        IntType it => it.Bits,
        BoolType => 8,
        PtrType or CallableType => _c.USize.Bits,
        FloatType ft => ft.Bits,
        VectorType vt => checked((int)(Bits(vt.Elem, pos) * vt.Count)),
        _ => throw new CompileError(pos, $"an assembly operand is an integer, a float, a pointer or a vector, not {t}"),
    };

    /// The constraint letter LLVM places a value of this class in.
    private string Letter(RegClass cls, DType t) => (_arch, cls) switch
    {
        (_, RegClass.Gpr) => "r",
        (Arch.X86, _) => Bits(t, _r.Pos) > 256 ? "v" : "x",
        (Arch.A64, _) => "w",
        (Arch.Rv, RegClass.Fpr) => "f",
        _ => "vr",
    };

    /// The register's name as LLVM's constraints and clobbers spell it.
    private string LlvmName(Reg r, DType? t) => (_arch, r.Class) switch
    {
        (Arch.X86, RegClass.Gpr) => X86Names64[r.Index],
        (Arch.X86, RegClass.Vec) => (t is null ? 128 : Bits(t, _r.Pos)) switch { > 256 => "zmm", > 128 => "ymm", _ => "xmm" } + r.Index,
        (Arch.A64, RegClass.Gpr) => "x" + r.Index,
        (Arch.A64, RegClass.Sp) => "sp",
        (Arch.A64, RegClass.Vec) => "v" + r.Index,
        (Arch.Rv, RegClass.Gpr) => "x" + r.Index,
        (Arch.Rv, RegClass.Fpr) => "f" + r.Index,
        (Arch.Rv, RegClass.Vec) => "v" + r.Index,
        _ => throw new CompileError(_r.Pos, $"{r.Written} isn't a register a value can be in"),
    };

    /// `RDX=REG2`: the architecture's name next to Tessera's, as a build error shows a register.
    private string Shown(Reg r) => r.Class switch
    {
        RegClass.Gpr or RegClass.Vec or RegClass.Fpr => $"{LlvmName(r, null).ToUpperInvariant()}={r.Written}",
        _ => r.Written,
    };

    /// The register as the assembler writes it, viewed at this type: `eax` for a U32 in REG0.
    private string RegText(Reg r, DType t, Pos pos)
    {
        int bits = Bits(t, pos);
        switch (_arch, r.Class)
        {
            case (Arch.X86, RegClass.Gpr):
                return bits switch
                {
                    8 => X86Names8[r.Index], 16 => X86Names16[r.Index], 32 => X86Names32[r.Index], 64 => X86Names64[r.Index],
                    _ => throw new CompileError(pos, $"{Shown(r)} holds 8, 16, 32 or 64 bits, not {t}"),
                };
            case (Arch.X86, RegClass.Vec):
                return LlvmName(r, t);
            case (Arch.X86, RegClass.Pc):
                return "rip";
            case (Arch.A64, RegClass.Gpr):
                return (bits <= 32 ? "w" : "x") + r.Index;
            case (Arch.A64, RegClass.Sp):
                return bits <= 32 ? "wsp" : "sp";
            case (Arch.A64, RegClass.Zero):
                return bits <= 32 ? "wzr" : "xzr";
            case (Arch.A64, RegClass.Vec):
                return t.Repr is VectorType vt ? $"v{r.Index}.{Arrangement(vt, pos)}" : $"{ScalarLetter(bits, pos)}{r.Index}";
            case (_, RegClass.Pc):
                return "pc";
            case (Arch.Rv, _):
                return LlvmName(r, t);
        }
        throw new CompileError(pos, $"{r.Written} isn't an operand here");
    }

    /// AArch64's scalar view of a vector register: b, h, s, d or q by width.
    private static string ScalarLetter(int bits, Pos pos) => bits switch
    {
        8 => "b", 16 => "h", 32 => "s", 64 => "d", 128 => "q",
        _ => throw new CompileError(pos, $"aarch64's vector registers hold 8, 16, 32, 64 or 128 bits as a scalar, not {bits}"),
    };

    /// AArch64's arrangement for a vector type: `Vector<F32, 4>` is `4s`.
    private string Arrangement(VectorType vt, Pos pos)
    {
        int lane = Bits(vt.Elem, pos);
        if (lane * vt.Count is not (64 or 128))
            throw new CompileError(pos, $"aarch64's vectors are 64 or 128 bits, and {vt} is {lane * vt.Count}");
        return $"{vt.Count}{ScalarLetter(lane, pos)}";
    }

    /// A parameter's register as the instruction writes it: a reference LLVM fills in once it places the value.
    private string ParamText(int param, DType t, Pos pos)
    {
        if (_r.Params[param].Register is not null) return RegText(ParamRegister(param), t, pos);
        int n = _paramOutput[param];
        var cls = ClassOf(_sig.Params[param], pos);
        if (cls != ClassOf(t, pos) && !(_arch == Arch.X86 && cls == RegClass.Vec))
            throw new CompileError(pos, $"{_r.Params[param].Name} is in a register for {_sig.Params[param]}, which can't be viewed as {t}");
        int bits = Bits(t, pos);
        return (_arch, cls) switch
        {
            (Arch.X86, RegClass.Gpr) => bits switch
            {
                8 => $"${{{n}:b}}", 16 => $"${{{n}:w}}", 32 => $"${{{n}:k}}", 64 => $"${{{n}:q}}",
                _ => throw new CompileError(pos, $"a general register holds 8, 16, 32 or 64 bits, not {t}"),
            },
            (Arch.X86, _) => bits switch { > 256 => $"${{{n}:g}}", > 128 => $"${{{n}:t}}", _ => $"${{{n}:x}}" },
            (Arch.A64, RegClass.Gpr) => bits <= 32 ? $"${{{n}:w}}" : $"${{{n}:x}}",
            (Arch.A64, _) => t.Repr is VectorType vt ? $"${{{n}}}.{Arrangement(vt, pos)}" : $"${{{n}:{ScalarLetter(bits, pos)}}}",
            _ => $"${{{n}}}",
        };
    }

    private int ParamIndex(ValueRef v)
    {
        int i = _r.Params.FindIndex(p => p.Name == v.Name);
        if (i < 0) throw new CompileError(v.Pos, $"{v.Name} is not a parameter of '{_r.DisplayName}'");
        return i;
    }

    /// Notes a fixed register the body names: it counts as changed, unless it is a result or a parameter's.
    private void Name(Reg r, DType? t, Pos pos)
    {
        if (r.Class is RegClass.Gpr or RegClass.Vec or RegClass.Fpr && !IsStackOrZero(r))
            _named.TryAdd(LlvmName(r, t), pos);
    }

    /// The stack pointer and the zero register are read, never counted as changed: LLVM can't give them up.
    private bool IsStackOrZero(Reg r) =>
        (_arch, r.Class, r.Index) is (Arch.X86, RegClass.Gpr, 4) or (Arch.Rv, RegClass.Gpr, 0 or 2);

    // ── Results ─────────────────────────────────────────────────────────────

    /// What `return(…)` names, the same in every block: nothing, one register, or a tuple of them.
    private List<Result> Results()
    {
        List<Result>? found = null;
        string? foundKey = null;
        foreach (var target in _r.Blocks!.SelectMany(b => Targets(b.Terminator)))
        {
            if (target is not ReturnTarget ret) continue;
            var items = ResultItems(ret);
            string key = string.Join(",", items.Select(i => i.Reg is null ? $"%{i.Param}" : LlvmName(i.Reg, i.Type)));
            if (foundKey is not null && key != foundKey)
                throw new CompileError(ret.Pos, "every return of an assembly routine names the same registers");
            found = items;
            foundKey = key;
        }
        return found ?? (_sig.Ret is VoidType ? [] : throw new CompileError(_r.Pos, $"'{_r.DisplayName}' never returns its {_sig.Ret}"));
    }

    private static IEnumerable<Target> Targets(Terminator t) => t switch
    {
        JumpTerm j => [j.Target],
        BranchTerm b => [b.IfTrue, b.IfFalse],
        TargetTerm tt => [tt.Target],
        _ => [],
    };

    private List<Result> ResultItems(ReturnTarget ret)
    {
        var rt = _sig.Ret;
        if (ret.Value is null)
            return rt is VoidType ? [] : throw new CompileError(ret.Pos, $"'{_r.DisplayName}' returns {rt}: name its register, return(REG0)");
        if (rt is VoidType) throw new CompileError(ret.Pos, $"'{_r.DisplayName}' returns nothing: return()");
        if (rt is RecordType { IsTuple: true } tuple)
        {
            if (ret.Value is not ArrayLit { Type: null } lit || lit.Elements.Count != tuple.Args.Count)
                throw new CompileError(ret.Pos, $"'{_r.DisplayName}' returns {rt}: name {tuple.Args.Count} registers, return({{ REG0, REG2 }})");
            return lit.Elements.Select((e, i) => ResultItem(e, tuple.Args[i])).ToList();
        }
        return [ResultItem(ret.Value, rt)];
    }

    private Result ResultItem(Expr e, DType t)
    {
        switch (e)
        {
            case ValueRef v:
            {
                int i = ParamIndex(v);
                if (!_sig.Params[i].Equals(t))
                    throw new CompileError(v.Pos, $"{v.Name} is {_sig.Params[i]}, and the result is {t}");
                return new Result(i, null, t);
            }
            case PresetRef { Owner: null, Path: null } p:
            {
                var reg = Register(p.Name, p.Pos);
                if (reg.Class != ClassOf(t, p.Pos) || IsStackOrZero(reg) || reg.Class is RegClass.Sp or RegClass.Zero)
                    throw new CompileError(p.Pos, $"a result of type {t} can't be in {Shown(reg)}");
                return new Result(-1, reg, t);
            }
            default:
                throw new CompileError(e.Pos, "a result is a register (REG0) or a parameter's register (%a)");
        }
    }

    // ── Body ────────────────────────────────────────────────────────────────

    /// A label private to the object file: Mach-O spells the prefix `L`, ELF and COFF `.L`. `${:uid}` is unique per
    /// expansion, so the labels of two calls never meet.
    private string Label(string block) => $"{(_c.Target.Os == "macos" ? "L" : ".L")}tess${{:uid}}_{block}";

    private const string EndLabel = "end";

    private string Body()
    {
        var blocks = _r.Blocks!;
        var names = new HashSet<string>();
        foreach (var b in blocks)
        {
            if (!b.Name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_') || b.Name == EndLabel)
                throw new CompileError(b.Pos, $"an assembly block's name is a label: letters, digits and '_', and not '{EndLabel}'");
            if (!names.Add(b.Name)) throw new CompileError(b.Pos, $"block '{b.Name}' is declared twice");
        }

        var lines = new List<string>();
        bool endUsed = false;
        for (int k = 0; k < blocks.Count; k++)
        {
            var b = blocks[k];
            string? next = k + 1 < blocks.Count ? blocks[k + 1].Name : null;
            if (k > 0) lines.Add(Label(b.Name) + ":");
            foreach (var s in b.Stmts) lines.Add(Instruction(s));

            // Where a target goes, and whether that is the next line anyway: the block that follows, or the end.
            (string Label, bool FallsThrough) Where(Target t, Pos pos)
            {
                switch (t)
                {
                    case CallTarget ct:
                        if (!names.Contains(ct.Name)) throw new CompileError(ct.Pos, $"no block named '{ct.Name}'");
                        if (ct.Args.Count != 0) throw new CompileError(ct.Pos, "an assembly block takes no arguments");
                        return (Label(ct.Name), ct.Name == next);
                    case ReturnTarget when _naked:
                        endUsed = true;
                        return (Label(EndLabel), false);
                    case ReturnTarget:
                        endUsed |= next is not null;
                        return (Label(EndLabel), next is null);
                    default:
                        throw new CompileError(pos, "an assembly block goes to a block or returns");
                }
            }
            // A jump to the block that follows is left out, as is a return from the last one.
            string? JumpText(Target t, Pos pos) => Where(t, pos) is (var l, false) ? l : null;

            switch (b.Terminator)
            {
                case JumpTerm j:
                    if (JumpText(j.Target, j.Pos) is { } jl) lines.Add($"{Jump()} {jl}");
                    break;
                case BranchTerm br:
                {
                    // The true target is always branched to, even when it follows: the false one is then the jump.
                    var (taken, _) = Where(br.IfTrue, br.Pos);
                    if (taken == Label(EndLabel)) endUsed = true;
                    lines.Add(ConditionalJump(br.Cond, taken));
                    if (JumpText(br.IfFalse, br.Pos) is { } other) lines.Add($"{Jump()} {other}");
                    break;
                }
                case TargetTerm { Target: ReturnTarget } when _naked:
                    lines.Add("ret");
                    break;
                case TargetTerm { Target: ReturnTarget r }:
                    if (JumpText(r, r.Pos) is { } rl) lines.Add($"{Jump()} {rl}");
                    break;
                case TargetTerm { Target: UnreachableTarget }:
                    break;
                case TargetTerm { Target: ExprTarget e }:
                    throw new CompileError(e.Pos, "an assembly block ends with jump, branch or return; this instruction needs one after it");
                default:
                    throw new CompileError(b.Terminator.Pos, "an assembly block ends with jump, branch or return");
            }
        }
        if (endUsed) lines.Add(Label(EndLabel) + ":");
        if (endUsed && _naked) lines.Add("ret");
        return string.Join("\n\t", lines);
    }

    private string Jump() => _arch switch { Arch.X86 => "jmp", Arch.A64 => "b", _ => "j" };

    private string Instruction(Stmt s)
    {
        if (s is not ExprStmt es)
            throw new CompileError(s.Pos, "an assembly body holds instructions, one per line: REG0.add<U64, U64>(REG3)");
        string prefix = "";
        foreach (var a in es.Attributes)
        {
            if (a.Name != "asm_prefix" || a.Args is not [{ Key: null, Negated: false, Expr: null } p] || p.Value.Length == 0)
                throw new CompileError(a.Pos, "an instruction takes #asm_prefix(\"lock\"), the prefix as the assembler spells it");
            prefix += Escape(p.Value) + " ";
        }

        (string mnemonic, Expr? receiver, List<TypeRef> typeArgs, List<Expr> args) = es.Value switch
        {
            CallExpr { Path: null } c => (c.Name, (Expr?)null, c.TypeArgs, c.Args),
            NsCallExpr { Owner: { Args.Count: 0, Path: null } o } ns =>
                (ns.Name, new PresetRef(null, o.Name, ns.Pos), ns.TypeArgs, ns.Args),
            MethodCallExpr m => (m.Name, m.Receiver, m.TypeArgs, m.Args),
            _ => throw new CompileError(es.Pos, "an instruction is written REG0.mnemonic<T, U>(operands), %a.mnemonic<…>(…) or mnemonic()"),
        };
        var operands = receiver is null ? args : [receiver, .. args];
        // One type per register, in order: a register, a parameter's register, or the memory a memory operand
        // reads (its size). An immediate and a condition take none.
        int wanted = operands.Count(IsTyped);
        if (typeArgs.Count != wanted)
            throw new CompileError(es.Pos, wanted switch
            {
                0 => $"'{mnemonic}' has no register operand, so it takes no types",
                1 => $"'{mnemonic}' has one register operand, so it takes its type: {mnemonic}<U64>",
                _ => $"'{mnemonic}' has {wanted} register operands, so it takes {wanted} types, one each: "
                     + $"{mnemonic}<{string.Join(", ", Enumerable.Repeat("U64", wanted))}>",
            });
        var types = new Queue<DType>(typeArgs.Select(t => _c.ResolveType(t, _sig.Env)));
        var texts = operands.Select(o => Operand(o, IsTyped(o) ? types.Dequeue() : null)).ToList();
        return prefix + Escape(mnemonic) + (texts.Count == 0 ? "" : " " + string.Join(", ", texts));
    }

    /// `$` is LLVM's operand marker, so a written one is doubled.
    private static string Escape(string written) => written.Replace("$", "$$");

    /// Whether an operand takes a type: a register or memory does, an immediate or a condition doesn't.
    private static bool IsTyped(Expr e) => e is not (IntLit or AsmCondExpr or PresetRef { Owner.Name: "FLAGS" });

    private string Operand(Expr e, DType? type)
    {
        DType t = type!;
        switch (e)
        {
            case PresetRef { Owner: null, Path: null } p:
            {
                var reg = Register(p.Name, p.Pos);
                if (reg.Class is RegClass.Flags or RegClass.Fs or RegClass.Gs)
                    throw new CompileError(p.Pos, reg.Class == RegClass.Flags
                        ? "FLAGS is read by a branch condition (FLAGS.Carry, lt<U64>), not as an operand"
                        : $"{p.Name} is a base address, read through offset: {p.Name}.offset(0x28)");
                if (reg.Class == RegClass.Pc && _arch != Arch.A64)
                    throw new CompileError(p.Pos, "PC is read through offset: PC.offset(8)");
                Name(reg, t, p.Pos);
                return RegText(reg, t, p.Pos);
            }
            case ValueRef v:
                return ParamText(ParamIndex(v), t, v.Pos);
            case IntLit lit:
                return _arch == Arch.A64 ? "#" + lit.Value : lit.Value.ToString();
            case AsmCondExpr or PresetRef { Owner.Name: "FLAGS" }:
                return ConditionOperand(e);
            case NsCallExpr or MethodCallExpr:
                return Memory(e, t);
            default:
                throw new CompileError(e.Pos, "an operand is a register, a parameter, an integer literal or a memory operand");
        }
    }

    // ── Memory operands ─────────────────────────────────────────────────────

    private sealed record Mem(Expr Base, Expr? Index, int Scale, BigInteger Offset, bool Update, bool After);

    private Mem ReadMemory(Expr e)
    {
        (Expr? inner, string name, List<Expr> args, Pos pos) = e switch
        {
            NsCallExpr { Owner: { Args.Count: 0, Path: null } o, TypeArgs.Count: 0 } ns =>
                ((Expr?)new PresetRef(null, o.Name, ns.Pos), ns.Name, ns.Args, ns.Pos),
            MethodCallExpr { TypeArgs.Count: 0 } m => (m.Receiver, m.Name, m.Args, m.Pos),
            _ => throw new CompileError(e.Pos, "a memory operand is built from its base register: REG3.offset(8)"),
        };
        BigInteger Literal(Expr x) => x is IntLit l ? l.Value
            : throw new CompileError(x.Pos, "an offset or a scale is an integer literal");
        bool IsBase(Expr x) => x is PresetRef { Owner: null } or ValueRef;

        switch (name)
        {
            case "offset" when args.Count == 1 && IsBase(inner!):
                return new Mem(inner!, null, 1, Literal(args[0]), false, false);
            case "offset" when args.Count == 1:
            {
                var m = ReadMemory(inner!);
                if (m.Offset != 0 || m.Update || m.After)
                    throw new CompileError(pos, "a memory operand takes one offset");
                return m with { Offset = Literal(args[0]) };
            }
            case "index" when args.Count == 2 && IsBase(inner!):
            {
                var scale = Literal(args[1]);
                if (scale != 1 && scale != 2 && scale != 4 && scale != 8)
                    throw new CompileError(args[1].Pos, "an index's scale is 1, 2, 4 or 8");
                if (!IsBase(args[0])) throw new CompileError(args[0].Pos, "an index is a register or a parameter");
                return new Mem(inner!, args[0], (int)scale, 0, false, false);
            }
            case "after_offset" when args.Count == 1 && IsBase(inner!):
                return new Mem(inner!, null, 1, Literal(args[0]), false, true);
            case "update" when args.Count == 0:
            {
                var m = ReadMemory(inner!);
                if (m.Update || m.After) throw new CompileError(pos, "a memory operand updates its base once");
                return m with { Update = true };
            }
            default:
                throw new CompileError(pos, $"'{name}' isn't a memory operand: offset(n), index(reg, scale), after_offset(n), update()");
        }
    }

    /// An address register as the assembler writes it: always the full width.
    private string AddressText(Expr e)
    {
        var addr = _c.USize;
        if (e is ValueRef v)
        {
            int i = ParamIndex(v);
            if (ClassOf(_sig.Params[i], v.Pos) != RegClass.Gpr)
                throw new CompileError(v.Pos, $"{v.Name} is {_sig.Params[i]}, not an address");
            return ParamText(i, addr, v.Pos);
        }
        var p = (PresetRef)e;
        var reg = Register(p.Name, p.Pos);
        if (reg.Class is not (RegClass.Gpr or RegClass.Sp or RegClass.Pc or RegClass.Fs or RegClass.Gs))
            throw new CompileError(p.Pos, $"{Shown(reg)} can't hold an address");
        Name(reg, addr, p.Pos);
        return reg.Class is RegClass.Fs or RegClass.Gs ? "" : RegText(reg, addr, p.Pos);
    }

    private string Memory(Expr e, DType t)
    {
        var m = ReadMemory(e);
        if ((m.Update || m.After) && _arch != Arch.A64)
            throw new CompileError(e.Pos, "update() and after_offset() are aarch64's");
        string baseText = AddressText(m.Base);
        string? index = m.Index is null ? null : AddressText(m.Index);
        var off = m.Offset;
        switch (_arch)
        {
            case Arch.X86:
            {
                string segment = m.Base is PresetRef { Name: "FS" or "GS" } seg ? seg.Name.ToLowerInvariant() + ":" : "";
                var parts = new StringBuilder(baseText);
                if (index is not null) parts.Append($"{(parts.Length > 0 ? " + " : "")}{index}*{m.Scale}");
                if (off != 0 || parts.Length == 0)
                    parts.Append(parts.Length == 0 ? off.ToString() : off < 0 ? $" - {-off}" : $" + {off}");
                return $"{X86Size(t, e.Pos)} ptr {segment}[{parts}]";
            }
            case Arch.A64:
            {
                if (index is not null && off != 0)
                    throw new CompileError(e.Pos, "aarch64 has no address with both an index and an offset");
                if (m.After) return $"[{baseText}], #{off}";
                string inner = index is not null
                    ? m.Scale == 1 ? $"{baseText}, {index}" : $"{baseText}, {index}, lsl #{BitOperations.Log2((uint)m.Scale)}"
                    : off == 0 ? baseText : $"{baseText}, #{off}";
                return $"[{inner}]{(m.Update ? "!" : "")}";
            }
            default:
                if (index is not null) throw new CompileError(e.Pos, "RISC-V has no indexed address: add the index first");
                return $"{off}({baseText})";
        }
    }

    private string X86Size(DType t, Pos pos) => (Bits(t, pos) / 8) switch
    {
        1 => "byte", 2 => "word", 4 => "dword", 8 => "qword", 10 => "tbyte", 16 => "xmmword", 32 => "ymmword", 64 => "zmmword",
        var n => throw new CompileError(pos, $"x86 has no memory operand of {n} bytes ({t})"),
    };

    // ── Branches ────────────────────────────────────────────────────────────

    private string ConditionalJump(Expr cond, string label)
    {
        switch (cond)
        {
            case AsmCondExpr or PresetRef { Owner.Name: "FLAGS" } when _arch != Arch.Rv:
                return (_arch == Arch.X86 ? "j" : "b.") + ConditionCode(cond) + " " + label;
            case AsmCondExpr or PresetRef { Owner.Name: "FLAGS" }:
                throw new CompileError(cond.Pos, "RISC-V has no flags: compare two registers, branch REG10.lt<U64>(REG11) ? … : …");
            case NsCallExpr or MethodCallExpr when _arch == Arch.Rv:
            {
                var (left, name, types, args, pos) = cond switch
                {
                    NsCallExpr { Owner: { Args.Count: 0, Path: null } o } ns =>
                        ((Expr)new PresetRef(null, o.Name, ns.Pos), ns.Name, ns.TypeArgs, ns.Args, ns.Pos),
                    MethodCallExpr { Receiver: ValueRef } m => (m.Receiver, m.Name, m.TypeArgs, m.Args, m.Pos),
                    _ => throw new CompileError(cond.Pos, "a RISC-V branch compares two registers: REG10.lt<U64>(REG11)"),
                };
                if (args.Count != 1 || types.Count > 1)
                    throw new CompileError(pos, $"a RISC-V branch compares two registers: REG10.{name}<U64>(REG11)");
                bool? signed = Signedness(name, types.Count == 1 ? types[0] : null, pos);
                var operandType = types.Count == 1 ? _c.ResolveType(types[0], _sig.Env) : _c.USize;
                string a = CompareOperand(left, operandType), b = CompareOperand(args[0], operandType);
                string u = signed == false ? "u" : "";
                return name switch
                {
                    "eq" => $"beq {a}, {b}, {label}",
                    "ne" => $"bne {a}, {b}, {label}",
                    "lt" => $"blt{u} {a}, {b}, {label}",
                    "ge" => $"bge{u} {a}, {b}, {label}",
                    "gt" => $"blt{u} {b}, {a}, {label}",
                    _ => $"bge{u} {b}, {a}, {label}",
                };
            }
            default:
                throw new CompileError(cond.Pos, _arch == Arch.Rv
                    ? "a RISC-V branch compares two registers: REG10.lt<U64>(REG11)"
                    : "an assembly branch tests the flags: eq, ne, lt<U64> … or a FLAGS field such as FLAGS.Carry");
        }
    }

    /// The architecture's code for a condition on the flags, `lt<U64>` or `FLAGS.Carry`: x86's `b` / `c`, aarch64's
    /// `lo` / `cs`. Reading the flags means an instruction here set them.
    private string ConditionCode(Expr cond)
    {
        _flagsTested = true;
        if (cond is AsmCondExpr c) return FlagCondition(c.Name, Signedness(c.Name, c.Type, c.Pos));
        var f = (PresetRef)cond;
        if (f.Owner!.Args.Count != 0 || f.Owner.Path is not null)
            throw new CompileError(f.Pos, $"FLAGS.{f.Name} names a flag of the last instruction");
        return (f.Name, _arch) switch
        {
            ("Carry", Arch.X86) => "c", ("Carry", _) => "cs",
            ("Overflow", Arch.X86) => "o", ("Overflow", _) => "vs",
            ("Sign", Arch.X86) => "s", ("Sign", _) => "mi",
            ("Zero" or "Equal", Arch.X86) => "e", ("Zero" or "Equal", _) => "eq",
            ("Parity", Arch.X86) => "p",
            _ => throw new CompileError(f.Pos,
                $"{_c.Target.Arch}'s FLAGS has no {f.Name}: Carry, Overflow, Sign, Zero (Equal){(_arch == Arch.X86 ? ", Parity" : "")}"),
        };
    }

    /// A condition as an operand, as aarch64's `cset x1, cs` and `csel x0, x1, x2, lo` take one. x86 and RISC-V have
    /// no such operand: x86 spells the condition in the mnemonic (`setc`, `cmovb`), RISC-V has no flags.
    private string ConditionOperand(Expr cond) => _arch switch
    {
        Arch.A64 => ConditionCode(cond),
        Arch.X86 => throw new CompileError(cond.Pos,
            "x86 spells a condition in the mnemonic (setc, cmovb), so it isn't an operand; a branch tests it"),
        _ => throw new CompileError(cond.Pos, "RISC-V has no flags: compare two registers, branch REG10.lt<U64>(REG11) ? … : …"),
    };

    private string CompareOperand(Expr e, DType t) => e switch
    {
        PresetRef or ValueRef => Operand(e, t),
        _ => throw new CompileError(e.Pos, "a RISC-V branch compares registers or parameters"),
    };

    /// Whether an ordered comparison is signed, from its type; null for eq and ne, which need none.
    private bool? Signedness(string name, TypeRef? type, Pos pos)
    {
        if (name is not ("eq" or "ne" or "lt" or "le" or "gt" or "ge"))
            throw new CompileError(pos, $"'{name}' isn't a comparison: eq, ne, lt, le, gt, ge");
        if (type is null)
            return name is "eq" or "ne" ? null
                : throw new CompileError(pos, $"'{name}' compares in order, so it takes a type for the signedness: {name}<S64> or {name}<U64>");
        return _c.ResolveType(type, _sig.Env).Repr switch
        {
            IntType { Kind: IntKind.Signed } => true,
            IntType or PtrType => false,
            var t => throw new CompileError(pos, $"a comparison's type is an integer type, not {t}"),
        };
    }

    /// The condition code of a comparison of the flags: x86's (`jb`) or aarch64's (`b.lo`).
    private string FlagCondition(string name, bool? signed) => (_arch, name, signed) switch
    {
        (Arch.X86, "eq", _) => "e", (Arch.X86, "ne", _) => "ne",
        (Arch.X86, "lt", true) => "l", (Arch.X86, "lt", _) => "b",
        (Arch.X86, "le", true) => "le", (Arch.X86, "le", _) => "be",
        (Arch.X86, "gt", true) => "g", (Arch.X86, "gt", _) => "a",
        (Arch.X86, "ge", true) => "ge", (Arch.X86, _, _) => "ae",
        (_, "eq", _) => "eq", (_, "ne", _) => "ne",
        (_, "lt", true) => "lt", (_, "lt", _) => "lo",
        (_, "le", true) => "le", (_, "le", _) => "ls",
        (_, "gt", true) => "gt", (_, "gt", _) => "hi",
        (_, "ge", true) => "ge", _ => "hs",
    };

    // ── Clobbers ────────────────────────────────────────────────────────────

    private List<string> Clobbers()
    {
        var clobbers = new List<string>();
        void Add(string c)
        {
            if (!clobbers.Contains(c)) clobbers.Add(c);
        }
        foreach (var name in _named.Keys)
            if (!_fixedOutputs.ContainsKey(name)) Add($"~{{{name}}}");

        if (_r.Attr("clobbers") is { } attr)
        {
            foreach (var a in attr.Args)
            {
                if (a.Key is not null || a.Negated || a.Expr is not null)
                    throw new CompileError(attr.Pos, "#clobbers lists registers, FLAGS and MEMORY: #clobbers(REG2, FLAGS)");
                if (a.Value == "MEMORY") { Add("~{memory}"); continue; }
                if (a.Value == "FLAGS")
                {
                    if (_arch == Arch.Rv) throw new CompileError(attr.Pos, "RISC-V has no flags");
                    Add(FlagsClobber);
                    continue;
                }
                var reg = Register(a.Value, attr.Pos);
                if (reg.Class == RegClass.Sp || IsStackOrZero(reg) && reg.Index != 0)
                    throw new CompileError(attr.Pos, "SP can't be changed: assembly that moves the stack pointer breaks the routine around it");
                if (reg.Class is not (RegClass.Gpr or RegClass.Vec or RegClass.Fpr))
                    throw new CompileError(attr.Pos, $"{a.Value} can't be listed in #clobbers");
                if (IsStackOrZero(reg)) continue;
                string name = LlvmName(reg, null);
                if (!_fixedOutputs.ContainsKey(name)) Add($"~{{{name}}}");
            }
        }
        // A branch on the flags means an instruction here set them.
        if (_flagsTested) Add(FlagsClobber);
        // Without #pure or #readonly, the assembly may read and write any memory.
        if (_r.Attr("pure") is null && _r.Attr("readonly") is null) Add("~{memory}");
        if (_arch == Arch.X86)
        {
            Add("~{dirflag}");
            Add("~{fpsr}");
        }
        return clobbers;
    }

    private string FlagsClobber => _arch == Arch.X86 ? "~{flags}" : "~{cc}";
}
