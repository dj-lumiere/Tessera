/* A link script for tests/freestanding and tests/freestanding_float on arm-none-eabi (Cortex-M): the vector table
   starts flash (4 MiB, room for freestanding_float's whole soft-float runtime at -O0), the code runs from flash, and .data's first values are copied from there into RAM. It defines the
   symbols the builder's entry uses: __stack_top (the vector table's first word), __bss_start and __bss_end, and
   __data_start, __data_end and __data_load. */
ENTRY(_start)
MEMORY
{
    FLASH (rx) : ORIGIN = 0x00000000, LENGTH = 4M
    RAM (rwx) : ORIGIN = 0x20000000, LENGTH = 64K
}
SECTIONS
{
    .isr_vector : { KEEP(*(.isr_vector)) } > FLASH
    .text : { *(.text .text.*) *(.rodata .rodata.*) } > FLASH
    .ARM.exidx : { *(.ARM.exidx*) } > FLASH
    .data : { __data_start = .; *(.data .data.*) __data_end = .; } > RAM AT > FLASH
    __data_load = LOADADDR(.data);
    .bss (NOLOAD) : { __bss_start = .; *(.bss .bss.* COMMON) __bss_end = .; } > RAM
    __stack_top = ORIGIN(RAM) + LENGTH(RAM);
}
