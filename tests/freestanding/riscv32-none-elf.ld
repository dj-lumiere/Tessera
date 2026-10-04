/* A link script for tests/freestanding and tests/freestanding_float on riscv32-none-elf: the code runs from flash and
   .data's first values are copied from there into RAM. It defines the symbols the builder's entry uses: __stack_top,
   __bss_start and __bss_end, and __data_start, __data_end and __data_load. It doesn't define __global_pointer$,
   since the entry leaves gp alone. */
ENTRY(_start)
MEMORY
{
    FLASH (rx) : ORIGIN = 0x20000000, LENGTH = 4M
    RAM (rwx) : ORIGIN = 0x80000000, LENGTH = 1M
}
SECTIONS
{
    .text : { *(.text .text.*) *(.rodata .rodata.* .srodata .srodata.*) } > FLASH
    .data : { __data_start = .; *(.data .data.* .sdata .sdata.*) __data_end = .; } > RAM AT > FLASH
    __data_load = LOADADDR(.data);
    .bss (NOLOAD) : { __bss_start = .; *(.bss .bss.* .sbss .sbss.* COMMON) __bss_end = .; } > RAM
    __stack_top = ORIGIN(RAM) + LENGTH(RAM);
}
