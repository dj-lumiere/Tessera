using System.Text.RegularExpressions;

namespace Tessera;

/// The names an instruction set gives to what an assembly operand can be besides a general, vector or float register:
/// system registers (AArch64's RNDR and CNTVCT_EL0, 32-bit ARM's PRIMASK and FPSCR, RISC-V's CSRs, x86's control,
/// debug and segment registers) and the named options some instructions take (a barrier's domain, a cache or TLB
/// operation, a coprocessor and its registers, a fence's sets, a rounding mode). Each is written as the architecture's
/// manual spells it, so it never collides with Tessera's own register names (R0, V3, S3, SP, FLAGS), and the builder
/// passes it to the assembler as written.
internal static partial class AsmSystemNames
{
    public enum Kind
    {
        /// A system register: its value changes on its own (a counter, a random number) or writing it changes the
        /// machine, so a routine that names one keeps its side effects.
        Register,
        /// A named option of an instruction, a word the instruction set defines: `ISH`, `CIVAC`, `rw`, `rtz`.
        Option,
    }

    /// What `name` is on this architecture ("x86_64", "aarch64", "arm", or "riscv"), or null when it is none of these.
    public static Kind? Lookup(string arch, string name) => arch switch
    {
        "aarch64" when A64Registers.Contains(name) || A64Generic().IsMatch(name) => Kind.Register,
        "aarch64" when A64Options.Contains(name) => Kind.Option,
        "arm" when ArmRegisters.Contains(name) => Kind.Register,
        "arm" when ArmOptions.Contains(name) => Kind.Option,
        "riscv" when RvCsrs.Contains(name) => Kind.Register,
        "riscv" when RvOptions.Contains(name) => Kind.Option,
        "x86_64" when X86Registers.Contains(name) => Kind.Register,
        _ => null,
    };

    /// AArch64's generic encoding of a system register: `S<op0>_<op1>_C<n>_C<m>_<op2>`, `S3_3_C2_C4_0` for RNDR.
    [GeneratedRegex(@"^S[0-3]_[0-7]_C(1[0-5]|[0-9])_C(1[0-5]|[0-9])_[0-7]$")]
    private static partial Regex A64Generic();

    private static IEnumerable<string> Levels(string name, params int[] levels) => levels.Select(l => $"{name}_EL{l}");

    private static IEnumerable<string> Numbered(string name, int first, int last) =>
        Enumerable.Range(first, last - first + 1).Select(n => name + n);

    /// AArch64's system registers by the names the Arm manual gives them. A register missing here is written in its
    /// generic form, which the builder checks only for its shape.
    private static readonly HashSet<string> A64Registers =
    [
        // Random numbers, the condition flags and the processor state.
        "RNDR", "RNDRRS", "NZCV", "DAIF", "CurrentEL", "SPSel", "FPCR", "FPSR", "SVCR",
        // EL0: cache geometry, thread pointers, the generic timer, the performance counters.
        "CTR_EL0", "DCZID_EL0", "TPIDR_EL0", "TPIDRRO_EL0", "TPIDR2_EL0",
        "CNTFRQ_EL0", "CNTPCT_EL0", "CNTVCT_EL0", "CNTPCTSS_EL0", "CNTVCTSS_EL0",
        "CNTP_CTL_EL0", "CNTP_CVAL_EL0", "CNTP_TVAL_EL0", "CNTV_CTL_EL0", "CNTV_CVAL_EL0", "CNTV_TVAL_EL0",
        "PMCR_EL0", "PMCCNTR_EL0", "PMCNTENSET_EL0", "PMCNTENCLR_EL0", "PMOVSCLR_EL0", "PMOVSSET_EL0", "PMSELR_EL0",
        "PMXEVTYPER_EL0", "PMXEVCNTR_EL0", "PMUSERENR_EL0", "PMCCFILTR_EL0", "PMSWINC_EL0",
        // Identification.
        "MIDR_EL1", "MPIDR_EL1", "REVIDR_EL1", "AIDR_EL1", "CCSIDR_EL1", "CCSIDR2_EL1", "CLIDR_EL1", "CSSELR_EL1",
        "ID_AA64PFR0_EL1", "ID_AA64PFR1_EL1", "ID_AA64ZFR0_EL1", "ID_AA64SMFR0_EL1", "ID_AA64DFR0_EL1",
        "ID_AA64DFR1_EL1", "ID_AA64AFR0_EL1", "ID_AA64AFR1_EL1", "ID_AA64ISAR0_EL1", "ID_AA64ISAR1_EL1",
        "ID_AA64ISAR2_EL1", "ID_AA64MMFR0_EL1", "ID_AA64MMFR1_EL1", "ID_AA64MMFR2_EL1",
        // Stack pointers, exceptions, memory management and control, at the levels that have them.
        "SP_EL0", "SP_EL1", "SP_EL2",
        .. Levels("ELR", 1, 2, 3), .. Levels("SPSR", 1, 2, 3), .. Levels("ESR", 1, 2, 3), .. Levels("FAR", 1, 2, 3),
        .. Levels("VBAR", 1, 2, 3), .. Levels("SCTLR", 1, 2, 3), .. Levels("ACTLR", 1, 2, 3),
        .. Levels("TTBR0", 1, 2, 3), .. Levels("TTBR1", 1, 2), .. Levels("TCR", 1, 2, 3), .. Levels("MAIR", 1, 2, 3),
        .. Levels("AMAIR", 1, 2, 3), .. Levels("TPIDR", 1, 2, 3), .. Levels("CONTEXTIDR", 1, 2),
        .. Levels("AFSR0", 1, 2, 3), .. Levels("AFSR1", 1, 2, 3), .. Levels("RVBAR", 1, 2, 3), .. Levels("RMR", 1, 2, 3),
        .. Levels("MDCR", 2, 3), .. Levels("CPTR", 2, 3),
        "CPACR_EL1", "HCR_EL2", "SCR_EL3", "MDSCR_EL1", "OSLAR_EL1", "OSLSR_EL1", "PAR_EL1", "ISR_EL1",
        "VTTBR_EL2", "VTCR_EL2", "VMPIDR_EL2", "VPIDR_EL2", "HSTR_EL2", "HACR_EL2",
        // The timer's control at EL1 and EL2.
        "CNTKCTL_EL1", "CNTHCTL_EL2", "CNTVOFF_EL2", "CNTHP_CTL_EL2", "CNTHP_CVAL_EL2", "CNTHP_TVAL_EL2",
        "CNTPS_CTL_EL1", "CNTPS_CVAL_EL1", "CNTPS_TVAL_EL1",
        // The GIC's CPU interface.
        "ICC_PMR_EL1", "ICC_IAR0_EL1", "ICC_IAR1_EL1", "ICC_EOIR0_EL1", "ICC_EOIR1_EL1", "ICC_HPPIR0_EL1",
        "ICC_HPPIR1_EL1", "ICC_BPR0_EL1", "ICC_BPR1_EL1", "ICC_DIR_EL1", "ICC_RPR_EL1", "ICC_SGI0R_EL1",
        "ICC_SGI1R_EL1", "ICC_ASGI1R_EL1", "ICC_CTLR_EL1", "ICC_CTLR_EL3", "ICC_SRE_EL1", "ICC_SRE_EL2",
        "ICC_SRE_EL3", "ICC_IGRPEN0_EL1", "ICC_IGRPEN1_EL1", "ICC_IGRPEN1_EL3",
        // Processor state fields `msr` sets from an immediate: msr(DAIFSet, 2).
        "DAIFSet", "DAIFClr", "PAN", "UAO", "DIT", "SSBS", "TCO", "ALLINT",
    ];

    /// The words AArch64 instructions take as operands: a barrier's domain (`dmb ish`), a cache, TLB or address
    /// translation operation (`dc civac`, `tlbi vmalle1`, `at s1e1r`), a prefetch operation (`prfm pldl1keep`), a
    /// branch target's kind (`bti c`), and `sys`'s `C0`–`C15`.
    private static readonly HashSet<string> A64Options =
    [
        "SY", "ST", "LD", "ISH", "ISHST", "ISHLD", "NSH", "NSHST", "NSHLD", "OSH", "OSHST", "OSHLD",
        "IVAC", "ISW", "CSW", "CISW", "ZVA", "CVAC", "CVAU", "CIVAC", "CVAP", "CVADP", "GVA", "GZVA",
        "IALLUIS", "IALLU", "IVAU",
        "S1E0R", "S1E0W", "S1E1R", "S1E1W", "S1E1RP", "S1E1WP", "S1E2R", "S1E2W", "S1E3R", "S1E3W",
        "S12E0R", "S12E0W", "S12E1R", "S12E1W",
        .. new[] { "VMALLE1", "VAE1", "ASIDE1", "VAAE1", "VALE1", "VAALE1", "ALLE1", "ALLE2", "ALLE3", "VAE2", "VALE2",
                   "VAE3", "VALE3", "IPAS2E1", "IPAS2LE1", "VMALLS12E1" }
            .SelectMany(op => new[] { op, op + "IS", op + "OS" }),
        .. from kind in new[] { "PLD", "PLI", "PST" }
           from level in new[] { "L1", "L2", "L3" }
           from policy in new[] { "KEEP", "STRM" }
           select kind + level + policy,
        "C", "J", "JC", "CSYNC",
        .. Numbered("C", 0, 15),
    ];

    /// 32-bit ARM's special registers by the names the Arm manuals give them, the ones `mrs` and `msr` (and the VFP's
    /// `vmrs` and `vmsr`) read and write. A coprocessor's registers have no names: `mrc` and `mcr` reach them by number.
    private static readonly HashSet<string> ArmRegisters =
    [
        // A and R profiles: the program status registers and their fields.
        "APSR", "APSR_nzcvq", "APSR_g", "APSR_nzcvqg", "CPSR", "SPSR",
        .. new[] { "c", "x", "s", "f", "fc", "fs", "fx", "sc", "sx", "xc", "fsx", "fsc", "fxc", "sxc", "fsxc" }
            .SelectMany(fields => new[] { "CPSR_" + fields, "SPSR_" + fields }),
        // M profile (Cortex-M): the program status views, the stack pointers and their limits, the masks, CONTROL,
        // and the Non-secure ones from the Secure state.
        "XPSR", "IPSR", "EPSR", "IAPSR", "EAPSR", "IEPSR", "MSP", "PSP", "MSPLIM", "PSPLIM", "PRIMASK", "BASEPRI",
        "BASEPRI_MAX", "FAULTMASK", "CONTROL",
        "MSP_NS", "PSP_NS", "MSPLIM_NS", "PSPLIM_NS", "PRIMASK_NS", "BASEPRI_NS", "FAULTMASK_NS", "CONTROL_NS", "SP_NS",
        // The VFP's system registers, and the flags `vmrs` copies its comparison into (APSR_nzcv).
        "FPSID", "FPSCR", "FPEXC", "FPINST", "FPINST2", "MVFR0", "MVFR1", "MVFR2", "APSR_nzcv",
    ];

    /// The words 32-bit ARM instructions take as operands: a barrier's domain (`dsb sy`, `dmb ish`), a coprocessor
    /// and its registers for `mrc` and `mcr` (`mrc p15, 0, r0, c13, c0, 3`), and the interrupt masks `cpsid` and
    /// `cpsie` set or clear (`cpsid i`).
    private static readonly HashSet<string> ArmOptions =
    [
        "SY", "ST", "LD", "ISH", "ISHST", "ISHLD", "NSH", "NSHST", "NSHLD", "OSH", "OSHST", "OSHLD",
        .. Numbered("p", 0, 15), .. Numbered("c", 0, 15),
        "a", "i", "f", "ai", "af", "if", "aif",
    ];

    /// RISC-V's CSRs by the names the privileged and unprivileged specifications give them. A CSR missing here is
    /// written by its number, an immediate.
    private static readonly HashSet<string> RvCsrs =
    [
        // Unprivileged: floating point, counters, vectors, the entropy source.
        "fflags", "frm", "fcsr", "cycle", "time", "instret", "cycleh", "timeh", "instreth",
        .. Numbered("hpmcounter", 3, 31), .. Numbered("hpmcounter", 3, 31).Select(n => n + "h"),
        "vstart", "vxsat", "vxrm", "vcsr", "vl", "vtype", "vlenb", "seed", "jvt",
        // Supervisor.
        "sstatus", "sie", "stvec", "scounteren", "senvcfg", "sscratch", "sepc", "scause", "stval", "sip", "satp",
        "stimecmp", "stimecmph", "scontext",
        // Hypervisor and virtual supervisor.
        "hstatus", "hedeleg", "hideleg", "hie", "hcounteren", "hgeie", "htval", "hip", "hvip", "htinst", "hgeip",
        "henvcfg", "hgatp", "vsstatus", "vsie", "vstvec", "vsscratch", "vsepc", "vscause", "vstval", "vsip", "vsatp",
        // Machine.
        "mvendorid", "marchid", "mimpid", "mhartid", "mconfigptr", "mstatus", "mstatush", "misa", "medeleg",
        "mideleg", "mie", "mtvec", "mcounteren", "menvcfg", "menvcfgh", "mscratch", "mepc", "mcause", "mtval", "mip",
        "mtinst", "mtval2", "mseccfg", "mseccfgh", "mcycle", "minstret", "mcycleh", "minstreth", "mcountinhibit",
        .. Numbered("pmpcfg", 0, 15), .. Numbered("pmpaddr", 0, 63),
        .. Numbered("mhpmcounter", 3, 31), .. Numbered("mhpmcounter", 3, 31).Select(n => n + "h"),
        .. Numbered("mhpmevent", 3, 31),
        // Debug and trigger.
        "tselect", "tdata1", "tdata2", "tdata3", "mcontext", "dcsr", "dpc", "dscratch0", "dscratch1",
    ];

    /// The words RISC-V instructions take as operands: a fence's predecessor and successor sets (`fence rw, rw`), a
    /// float instruction's rounding mode (`fcvt.w.d a0, fa0, rtz`), and `vsetvli`'s element width, group multiplier
    /// and tail and mask policies (`vsetvli t0, a0, e32, m1, ta, ma`).
    private static readonly HashSet<string> RvOptions =
    [
        // Every nonempty set of i, o, r, w, in that order.
        .. Enumerable.Range(1, 15).Select(bits => string.Concat("iorw".Where((_, i) => (bits & (8 >> i)) != 0))),
        "rne", "rtz", "rdn", "rup", "rmm", "dyn",
        "e8", "e16", "e32", "e64", "m1", "m2", "m4", "m8", "mf2", "mf4", "mf8", "ta", "tu", "ma", "mu",
    ];

    /// x86's control and debug registers (`mov rax, cr3`) and the segment registers as selectors (`mov ax, ds`). FS
    /// and GS are Tessera's own names already: as an operand they are the selector too.
    private static readonly HashSet<string> X86Registers =
        [.. Numbered("CR", 0, 15), .. Numbered("DR", 0, 7), "CS", "DS", "ES", "SS"];
}
