/* A link script for tests/freestanding and tests/freestanding_float on aarch64-none-elf: a loader puts the program in
   RAM at 0x40080000 (QEMU virt's RAM), so .data needs no copy. It defines the symbols the builder's entry uses: __stack_top, __bss_start and
   __bss_end. */
ENTRY(_start)
SECTIONS
{
    . = 0x40080000;
    .text : { *(.text .text.*) }
    .rodata : { *(.rodata .rodata.*) }
    .data : { *(.data .data.*) }
    .bss (NOLOAD) : { __bss_start = .; *(.bss .bss.* COMMON) __bss_end = .; }
    . = ALIGN(16);
    . = . + 0x10000;
    __stack_top = .;
}
