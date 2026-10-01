namespace Tessera;

/// The C calling convention's demands on a routine's signature, per target, mirroring what clang emits for the same
/// C types. Every routine uses it except `#callconv("fast")` ones, so a Tessera routine can be a C callback, an
/// `#export`, or the far side of a prebuilt boundary.
public static class CAbi
{
    /// The extension attribute a narrow integer or Bool parameter or return value carries (`signext` / `zeroext`,
    /// with a leading space), or "". The caller of a C function extends the value to register width only when the
    /// target's ABI says so, and LLVM does it from these attributes on the call.
    public static string Ext(BuildTarget target, DType type, bool isReturn)
    {
        int bits;
        bool signed;
        switch (type.Repr)
        {
            case BoolType: bits = 1; signed = false; break;
            case IntType it: bits = it.Bits; signed = it.Kind == IntKind.Signed; break;
            default: return "";
        }
        bool extend = (target.Arch, target.Os) switch
        {
            // Win64 and AArch64 (except Apple's) leave the upper bits undefined; Win64 still extends a Bool.
            ("x86_64", "windows") => bits == 1,
            ("aarch64", not "macos") => false,
            // AVR passes in 8-bit registers: a parameter widens Bool and 8-bit values, a return value 16-bit ones.
            ("avr", _) => isReturn ? bits == 16 : bits <= 8,
            // 64-bit registers that hold a 32-bit value sign-extended (riscv64 even for unsigned; MIPS for
            // parameters, where every integer parameter up to 64 bits is extended), or extended by its signedness (POWER).
            ("riscv64", _) => bits <= 32,
            ("mips" or "mipsel" or "mips64" or "mips64el", _) => isReturn ? bits < 32 : bits <= 64,
            ("powerpc64le", _) => bits <= 32,
            _ => bits < 32,
        };
        if (!extend) return "";
        bool signExtend = bits == 32 && target.Arch is "riscv64" or "mips" or "mipsel" or "mips64" or "mips64el" || signed && bits != 1;
        return signExtend ? " signext" : " zeroext";
    }
}
