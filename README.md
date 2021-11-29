# SharpSH2
A Hitachi SH-2 instruction set emulator written in C#

# Motivation
This project was inspired by the MAME source code and was originally intended as a core component of the Aion fantasy console (which is itself written in C#/FNA)

# Usage
The CPU relies on a virtual memory bus which it will read from and write to during operation. The simplest possible bus is a single flat memory object:
```
using SharpSH2;

// initialize memory buffer with a size of 4KiB
var memory = new LinearMemory(4096);
```
An instance of the CPU can then be constructed:
```
var cpu = new Sh2Cpu(memory);
```
The CPU must first be initialized, and then can be stepped a cycle at a time:
```
cpu.PowerOn();

while (running)
{
  cpu.Cycle();
}
```

# TODO
- [ ] Currently assumes endianness of host machine (usually little endian). Should change this to allow for selecting either little or big endian
- [ ] Currently not cycle accurate, assumes 1 clock cycle per instruction
- [ ] Interrupt implementation needs lots of work
- [ ] Needs more thorough tests
