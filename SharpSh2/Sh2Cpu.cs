using System;
using System.Collections.Generic;

namespace SharpSh2
{
	public enum CpuState
	{
		PowerOff,
		ProgramExecution,
		Sleep,
		Standby,
		ExceptionProcessing,
	}

	// TODO: Right now this assumes endianness of the host machine (usually little endian)
	// The endianness should instead be configurable, perhaps via keyword define

	// TODO: Make this cycle accurate (currently just takes 1 cycle per instruction)

	// TODO: finish placeholder interrupt implementation

	// Inspired by & referenced from the MAME source code
	// https://github.com/mamedev/mame/blob/master/src/devices/cpu/sh/sh.cpp

	/// <summary>
	/// Implementation of an SH-2 CPU.
	/// </summary>
	public class Sh2Cpu
	{
		#region Constants

		protected const uint EXCEPT_CPU_ADDR_ERROR = 9 << 2;
		protected const uint EXCEPT_DMA_ADDR_ERROR = 10 << 2;
		protected const uint EXCEPT_ILLEGAL_INS = 4 << 2;
		protected const uint EXCEPT_ILLEGAL_SLOT_INS = 6 << 2;
		protected const uint EXCEPT_INT_IRQ = 64 << 2;
		protected const uint EXCEPT_INT_NMI = 11 << 2;
		protected const uint EXCEPT_INT_USER_BRK = 12 << 2;
		protected const uint EXCEPT_POWERON_PC = 0 << 2;
		protected const uint EXCEPT_POWERON_SP = 1 << 2;
		protected const uint EXCEPT_RESET_PC = 2 << 2;
		protected const uint EXCEPT_RESET_SP = 3 << 2;
		protected const uint EXCEPT_TRAP = 32 << 2;
		protected const int GBR = 17;
		protected const int MACH = 19;
		protected const int MACL = 20;
		protected const int PC = 22;
		protected const int PR = 21;
		protected const uint SH_I = 0b0011110000;
		protected const uint SH_M = 0b1000000000;
		protected const uint SH_Q = 0b0100000000;
		protected const uint SH_S = 0b0000000010;
		protected const uint SH_T = 0b0000000001;
		protected const uint SH_FLAGS = SH_M | SH_Q | SH_I | SH_S | SH_T;
		protected const int SP = 15;
		protected const int SR = 16;
		protected const int VBR = 18;
		#endregion

		#region Properties

		/// <summary>
		/// Gets the current state of the CPU
		/// </summary>
		public CpuState State => _state;

		#endregion

		#region Fields

		/// <summary>
		/// Bus this CPU is connected to
		/// </summary>
		protected readonly IBus _bus;

		/// <summary>
		/// R0-R15, SR, GBR, VBR, MACH, MACL, PR, PC
		/// </summary>
		protected readonly uint[] _regs;
		protected uint _delay;
		protected uint _delayedBranchTarget;
		protected Dictionary<int, Action<ushort>> _dispatch;
		protected uint _irq;
		protected CpuState _state;

		#endregion

		#region Constructor

		public Sh2Cpu(IBus bus)
		{
			if (bus == null)
			{
				throw new ArgumentNullException($"{nameof(bus)}");
			}

			_bus = bus;
			_regs = new uint[23];
			_dispatch = new Dictionary<int, Action<ushort>>();

			_dispatch[0x0000] = Execute0000;
			_dispatch[0x1000] = MOVLS4;
			_dispatch[0x2000] = Op0010;
			_dispatch[0x3000] = Op0011;
			_dispatch[0x4000] = Execute4000;
			_dispatch[0x5000] = MOVLL4;
			_dispatch[0x6000] = Op0110;
			_dispatch[0x7000] = ADDI;
			_dispatch[0x8000] = Op1000;
			_dispatch[0x9000] = MOVWI;
			_dispatch[0xA000] = BRA;
			_dispatch[0xB000] = BSR;
			_dispatch[0xC000] = Op1100;
			_dispatch[0xD000] = MOVLI;
			_dispatch[0xE000] = MOVI;
			_dispatch[0xF000] = ExecuteF000;

			_state = CpuState.PowerOff;
		}

		#endregion

		#region API

		/// <summary>
		/// Run a single cycle
		/// </summary>
		public void Cycle()
		{
			uint mask = (_regs[SR] & SH_I) >> 4;

			// check irq lines 0..7
			for (int i = 7; i >= 0; i--)
			{
				if ((_irq & (1 << i)) == 0) continue;

				if (i > mask)
				{
					// handle interrupt

					_irq &= ~(1U << i);

					Push32(_regs[SR]);
					Push32(_regs[PC]);

					_regs[SR] &= ~SH_I;
					_regs[SR] |= (uint)(i & 0xF) << 4;

					_state = CpuState.ExceptionProcessing;
				}

				break;
			}

			if (_state != CpuState.ProgramExecution && _state != CpuState.ExceptionProcessing)
			{
				return;
			}

			uint instr_addr = _regs[PC];
			ushort instr = _bus.Read16(_regs[PC]);

			if (_delay != 0)
			{
				_regs[PC] = _delayedBranchTarget = _delay;
				_delay = 0;
			}
			else
			{
				_delayedBranchTarget = 0;
				_regs[PC] += 2;
			}

			_dispatch[instr & 0xF000](instr);
		}

		/// <summary>
		/// Asserts an external IRQ line (IRQ0-IRQ7)
		/// </summary>
		/// <param name="irq">IRQ signal to assert (0-7)</param>
		public void IRQ(int irq)
		{
			if (irq < 0 || irq >= 8) throw new ArgumentOutOfRangeException($"{nameof(irq)}");
			_irq |= 1U << irq;
		}

		/// <summary>
		/// Asserts external NMI line
		/// </summary>
		public void NMI()
		{
			_state = CpuState.ExceptionProcessing;

			Push32(_regs[SR]);
			Push32(_regs[PC]);

			// set interrupt mask to 15
			_regs[SR] |= SH_I;
			_regs[PC] = _regs[VBR] + EXCEPT_INT_NMI;
		}

		/// <summary>
		/// Performs a reset equivalent to power-on reset
		/// </summary>
		public void PowerOn()
		{
			_regs[VBR] = 0;
			_regs[PC] = _bus.Read32(EXCEPT_POWERON_PC);
			_regs[SP] = _bus.Read32(EXCEPT_POWERON_SP);
			_regs[SR] = SH_I;
			_delay = 0;
			_delayedBranchTarget = 0;

			_state = CpuState.ProgramExecution;
		}

		/// <summary>
		/// Performs a reset equivalent to a manual reset
		/// </summary>
		public void SoftReset()
		{
			// NOTE: according to the manual, there's actually a *different* set of vectors for PC/SP for manual reset vs power-on reset
			// I guess so you can begin executing from a different entry point depending on whether it was a hard or soft reset?

			_regs[PC] = _bus.Read32(_regs[VBR] + EXCEPT_RESET_PC);
			_regs[SP] = _bus.Read32(_regs[VBR] + EXCEPT_RESET_SP);
			_regs[VBR] = 0;
			_regs[SR] = SH_I;
			_delay = 0;
			_delayedBranchTarget = 0;

			_state = CpuState.ProgramExecution;
		}
		private void CHECK_DELAY_SLOT_PC()
		{
			if (_delayedBranchTarget != 0)
			{
				ILLEGAL();
			}
		}

		private void Execute0000(ushort op)
		{
			switch (op & 0x3F)
			{
				case 0x00: ILLEGAL(); break;
				case 0x01: ILLEGAL(); break;
				case 0x02: STCSR(op); break;
				case 0x03: BSRF(op); break;
				case 0x04: MOVBS0(op); break;
				case 0x05: MOVWS0(op); break;
				case 0x06: MOVLS0(op); break;
				case 0x07: MULL(op); break;
				case 0x08: CLRT(); break;
				case 0x09: NOP(); break;
				case 0x0a: STSMACH(op); break;
				case 0x0b: RTS(); break;
				case 0x0c: MOVBL0(op); break;
				case 0x0d: MOVWL0(op); break;
				case 0x0e: MOVLL0(op); break;
				case 0x0f: MAC_L(op); break;
				case 0x10: ILLEGAL(); break;
				case 0x11: ILLEGAL(); break;
				case 0x12: STCGBR(op); break;
				case 0x13: ILLEGAL(); break;
				case 0x14: MOVBS0(op); break;
				case 0x15: MOVWS0(op); break;
				case 0x16: MOVLS0(op); break;
				case 0x17: MULL(op); break;
				case 0x18: SETT(); break;
				case 0x19: DIV0U(); break;
				case 0x1a: STSMACL(op); break;
				case 0x1B: SLEEP(); break;
				case 0x1c: MOVBL0(op); break;
				case 0x1d: MOVWL0(op); break;
				case 0x1e: MOVLL0(op); break;
				case 0x1f: MAC_L(op); break;
				case 0x20: ILLEGAL(); break;
				case 0x21: ILLEGAL(); break;
				case 0x22: STCVBR(op); break;
				case 0x23: BRAF(op); break;
				case 0x24: MOVBS0(op); break;
				case 0x25: MOVWS0(op); break;
				case 0x26: MOVLS0(op); break;
				case 0x27: MULL(op); break;
				case 0x28: CLRMAC(); break;
				case 0x29: MOVT(op); break;
				case 0x2a: STSPR(op); break;
				case 0x2b: RTE(); break;
				case 0x2c: MOVBL0(op); break;
				case 0x2d: MOVWL0(op); break;
				case 0x2e: MOVLL0(op); break;
				case 0x2f: MAC_L(op); break;
				case 0x30: ILLEGAL(); break;
				case 0x31: ILLEGAL(); break;
				case 0x32: ILLEGAL(); break;
				case 0x33: ILLEGAL(); break;
				case 0x34: MOVBS0(op); break;
				case 0x35: MOVWS0(op); break;
				case 0x36: MOVLS0(op); break;
				case 0x37: MULL(op); break;
				case 0x38: ILLEGAL(); break;
				case 0x39: ILLEGAL(); break;
				case 0x3a: ILLEGAL(); break;
				case 0x3b: ILLEGAL(); break;
				case 0x3c: MOVBL0(op); break;
				case 0x3d: MOVWL0(op); break;
				case 0x3e: MOVLL0(op); break;
				case 0x3f: MAC_L(op); break;
			}
		}

		private void Execute4000(ushort op)
		{
			switch (op & 0x3F)
			{
				case 0x00: SHLL(op); break;
				case 0x01: SHLR(op); break;
				case 0x02: STSMMACH(op); break;
				case 0x03: STCMSR(op); break;
				case 0x04: ROTL(op); break;
				case 0x05: ROTR(op); break;
				case 0x06: LDSMMACH(op); break;
				case 0x07: LDCMSR(op); break;
				case 0x08: SHLL2(op); break;
				case 0x09: SHLR2(op); break;
				case 0x0a: LDSMACH(op); break;
				case 0x0b: JSR(op); break;
				case 0x0c: ILLEGAL(); break;
				case 0x0d: ILLEGAL(); break;
				case 0x0e: LDCSR(op); break;
				case 0x0f: MAC_W(op); break;
				case 0x10: DT(op); break;
				case 0x11: CMPPZ(op); break;
				case 0x12: STSMMACL(op); break;
				case 0x13: STCMGBR(op); break;
				case 0x14: ILLEGAL(); break;
				case 0x15: CMPPL(op); break;
				case 0x16: LDSMMACL(op); break;
				case 0x17: LDCMGBR(op); break;
				case 0x18: SHLL8(op); break;
				case 0x19: SHLR8(op); break;
				case 0x1a: LDSMACL(op); break;
				case 0x1b: TAS(op); break;
				case 0x1c: ILLEGAL(); break;
				case 0x1d: ILLEGAL(); break;
				case 0x1e: LDCGBR(op); break;
				case 0x1f: MAC_W(op); break;
				case 0x20: SHAL(op); break;
				case 0x21: SHAR(op); break;
				case 0x22: STSMPR(op); break;
				case 0x23: STCMVBR(op); break;
				case 0x24: ROTCL(op); break;
				case 0x25: ROTCR(op); break;
				case 0x26: LDSMPR(op); break;
				case 0x27: LDCMVBR(op); break;
				case 0x28: SHLL16(op); break;
				case 0x29: SHLR16(op); break;
				case 0x2a: LDSPR(op); break;
				case 0x2b: JMP(op); break;
				case 0x2c: ILLEGAL(); break;
				case 0x2d: ILLEGAL(); break;
				case 0x2e: LDCVBR(op); break;
				case 0x2f: MAC_W(op); break;
				case 0x30: ILLEGAL(); break;
				case 0x31: ILLEGAL(); break;
				case 0x32: ILLEGAL(); break;
				case 0x33: ILLEGAL(); break;
				case 0x34: ILLEGAL(); break;
				case 0x35: ILLEGAL(); break;
				case 0x36: ILLEGAL(); break;
				case 0x37: ILLEGAL(); break;
				case 0x38: ILLEGAL(); break;
				case 0x39: ILLEGAL(); break;
				case 0x3a: ILLEGAL(); break;
				case 0x3b: ILLEGAL(); break;
				case 0x3c: ILLEGAL(); break;
				case 0x3d: ILLEGAL(); break;
				case 0x3e: ILLEGAL(); break;
				case 0x3f: MAC_W(op); break;
			}
		}

		private void ExecuteF000(ushort op)
		{
			ILLEGAL();
		}

		private void ILLEGAL()
		{
			_state = CpuState.ExceptionProcessing;

			Push32(_regs[SR]);

			// if this is an instruction immediately following a delayed branch, then we push the target of the branch
			// and process the illegal slot instruction exception
			if (_delayedBranchTarget != 0)
			{
				Push32(_delayedBranchTarget);
				_regs[PC] = _regs[VBR] + EXCEPT_ILLEGAL_SLOT_INS;
			}
			// otherwise, we push the address of the illegal instruction and process a regular illegal instruction exception
			else
			{
				Push32(_regs[PC] - 2); // PC already points at the next instruction, but what we push needs to point at *this* instruction
				_regs[PC] = _regs[VBR] + EXCEPT_ILLEGAL_INS;
			}
		}

		private void Op0010(ushort op)
		{
			switch (op & 0xF)
			{
				case 0: MOVBS(op); break;
				case 1: MOVWS(op); break;
				case 2: MOVLS(op); break;
				case 3: ILLEGAL(); break;
				case 4: MOVBM(op); break;
				case 5: MOVWM(op); break;
				case 6: MOVLM(op); break;
				case 7: DIV0S(op); break;
				case 8: TST(op); break;
				case 9: AND(op); break;
				case 10: XOR(op); break;
				case 11: OR(op); break;
				case 12: CMPSTR(op); break;
				case 13: XTRCT(op); break;
				case 14: MULU(op); break;
				case 15: MULS(op); break;
			}
		}

		private void Op0011(ushort op)
		{
			switch (op & 0xF)
			{
				case 0: CMPEQ(op); break;
				case 1: ILLEGAL(); break;
				case 2: CMPHS(op); break;
				case 3: CMPGE(op); break;
				case 4: DIV1(op); break;
				case 5: DMULU(op); break;
				case 6: CMPHI(op); break;
				case 7: CMPGT(op); break;
				case 8: SUB(op); break;
				case 9: ILLEGAL(); break;
				case 10: SUBC(op); break;
				case 11: SUBV(op); break;
				case 12: ADD(op); break;
				case 13: DMULS(op); break;
				case 14: ADDC(op); break;
				case 15: ADDV(op); break;
			}
		}

		private void Op0110(ushort op)
		{
			switch (op & 0xF)
			{
				case 0: MOVBL(op); break;
				case 1: MOVWL(op); break;
				case 2: MOVLL(op); break;
				case 3: MOV(op); break;
				case 4: MOVBP(op); break;
				case 5: MOVWP(op); break;
				case 6: MOVLP(op); break;
				case 7: NOT(op); break;
				case 8: SWAPB(op); break;
				case 9: SWAPW(op); break;
				case 10: NEGC(op); break;
				case 11: NEG(op); break;
				case 12: EXTUB(op); break;
				case 13: EXTUW(op); break;
				case 14: EXTSB(op); break;
				case 15: EXTSW(op); break;
			}
		}

		private void Op1000(ushort op)
		{
			switch (op & (0xF << 8))
			{
				case 0 << 8: MOVBS4(op); break;
				case 1 << 8: MOVWS4(op); break;
				case 2 << 8: ILLEGAL(); break;
				case 3 << 8: ILLEGAL(); break;
				case 4 << 8: MOVBL4(op); break;
				case 5 << 8: MOVWL4(op); break;
				case 6 << 8: ILLEGAL(); break;
				case 7 << 8: ILLEGAL(); break;
				case 8 << 8: CMPIM(op); break;
				case 9 << 8: BT(op); break;
				case 10 << 8: ILLEGAL(); break;
				case 11 << 8: BF(op); break;
				case 12 << 8: ILLEGAL(); break;
				case 13 << 8: BTS(op); break;
				case 14 << 8: ILLEGAL(); break;
				case 15 << 8: BFS(op); break;
			}
		}

		private void Op1100(ushort op)
		{
			switch (op & (0xF << 8))
			{
				case 0 << 8: MOVBSG(op); break;
				case 1 << 8: MOVWSG(op); break;
				case 2 << 8: MOVLSG(op); break;
				case 3 << 8: TRAPA(op); break;
				case 4 << 8: MOVBLG(op); break;
				case 5 << 8: MOVWLG(op); break;
				case 6 << 8: MOVLLG(op); break;
				case 7 << 8: MOVA(op); break;
				case 8 << 8: TSTI(op); break;
				case 9 << 8: ANDI(op); break;
				case 10 << 8: XORI(op); break;
				case 11 << 8: ORI(op); break;
				case 12 << 8: TSTM(op); break;
				case 13 << 8: ANDM(op); break;
				case 14 << 8: XORM(op); break;
				case 15 << 8: ORM(op); break;
			}
		}

		private ushort Pop16()
		{
			ushort val = _bus.Read16(_regs[SP]);
			_regs[SP] += 2;
			return val;
		}

		private uint Pop32()
		{
			uint val = _bus.Read32(_regs[SP]);
			_regs[SP] += 4;
			return val;
		}

		private byte Pop8()
		{
			byte val = _bus.Read8(_regs[SP]);
			_regs[SP]++;
			return val;
		}

		private void Push16(ushort value)
		{
			_regs[SP] -= 2;
			_bus.Write16(_regs[SP], value);
		}

		private void Push32(uint value)
		{
			_regs[SP] -= 4;
			_bus.Write32(_regs[SP], value);
		}

		private void Push8(byte value)
		{
			_regs[SP]--;
			_bus.Write8(_regs[SP], value);
		}
		#endregion

		#region Instructions

		private void ADD(ushort op)
		{
			//	ADD Rm,Rn
			//		Rn + Rm → Rn
			//	0011 nnnn mmmm 1100

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			_regs[n] += _regs[m];
		}

		private void ADDC(ushort op)
		{
			//	ADDC Rm,Rn
			//		Rn + Rm + T → Rn, carry → T
			//	0011 nnnn mmmm 1110

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			uint tmp1 = _regs[n] + _regs[m];
			uint tmp0 = _regs[n];

			_regs[n] = tmp1 + (_regs[SR] & SH_T);

			if (tmp0 > tmp1 || tmp1 > _regs[n])
			{
				_regs[SR] |= SH_T;
			}
			else
			{
				_regs[SR] &= ~SH_T;
			}
		}

		private void ADDI(ushort op)
		{
			//	ADD #imm,Rn
			//		Rn + imm → Rn
			//	0111 nnnn iiiiiiii

			uint imm = (uint)(sbyte)(op & 0xFF);
			uint n = (uint)(op >> 8) & 0xF;

			_regs[n] += imm;
		}

		private void ADDV(ushort op)
		{
			//	ADDV Rm,Rn
			//		Rn + Rm → Rn, Overflow → T
			//	0011 nnnn mmmm 1111

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			_regs[n] += _regs[m];

			// for some reason MAME has a big if/else nest here that it really doesn't have to be doing.
			// it probably relies on compiler optimization or something, idk.
			// this is smaller and quicker to read anyway

			if (_regs[n] > int.MaxValue - _regs[m])
			{
				_regs[SR] |= SH_T;
			}
			else
			{
				_regs[SR] &= ~SH_T;
			}
		}

		private void AND(ushort op)
		{
			//	AND Rm,Rn
			//		Rn & Rm → Rn
			//	0010 nnnn mmmm 1001

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			_regs[n] &= _regs[m];
		}

		private void ANDI(ushort op)
		{
			//	AND #imm,R0
			//		R0 & imm → R0
			//	11001001 iiiiiiii

			byte imm = (byte)(op & 0xFF);
			_regs[0] &= imm;
		}

		private void ANDM(ushort op)
		{
			//	AND.B #imm,@(R0,GBR)
			//		(R0 + GBR) & imm → (R0 + GBR)
			//	11001101 iiiiiiii

			uint ea = _regs[0] + _regs[VBR];
			byte imm = (byte)(op & 0xFF);
			_bus.Write8(ea, (byte)(_bus.Read8(ea) & imm));
		}

		private void BF(ushort op)
		{
			CHECK_DELAY_SLOT_PC();

			//	BF label
			//		If T = 0, disp × 2 + PC → PC; if T = 1, nop (where label is disp × 2 + PC)
			//	10001011 dddddddd

			if ((_regs[SR] & SH_T) == 0)
			{
				uint d = (uint)(op & 0xFF);
				int disp = ((int)d << 24) >> 24;
				_regs[PC] = (uint)(_regs[PC] + 2 + (disp * 2));
			}
		}

		private void BFS(ushort op)
		{
			//	BF/S label
			//		If T = 0, disp × 2 + PC → PC; if T = 1, nop
			//	10001111 dddddddd

			if ((_regs[SR] & SH_T) == 0)
			{
				int disp = (sbyte)(op & 0xFF);
				_delay = (uint)(_regs[PC] + 2 + (disp * 2));
			}
		}

		private void BRA(ushort op)
		{
			CHECK_DELAY_SLOT_PC();

			//	BRA label
			//		Delayed branch, disp × 2 + PC → PC
			//	1010 dddddddddddd

			int disp = (op << 20) >> 20;

			_delay = (uint)(_regs[PC] + 2 + (disp * 2));
		}

		private void BRAF(ushort op)
		{
			CHECK_DELAY_SLOT_PC();

			//	BRAF label
			//		Delayed branch, Rm + PC → PC
			//	0000 mmmm 00100011

			uint m = (uint)(op >> 8);

			_delay = _regs[PC] + 2 + _regs[m];
		}

		private void BSR(ushort op)
		{
			CHECK_DELAY_SLOT_PC();

			//	BSR label
			//		Delayed branch, PC → PR, disp × 2 + PC → PC
			//	1011 dddddddddddd

			int disp = (op << 20) >> 20;
			_regs[PR] = _regs[PC] + 2;
			_delay = (uint)(_regs[PC] + 2 + (disp * 2));
		}

		private void BSRF(ushort op)
		{
			CHECK_DELAY_SLOT_PC();

			//	BSRF Rm
			//		Delayed branch, PC → PR, Rm + PC → PC
			//	0000 mmmm 00000011

			uint m = (uint)(op >> 8) & 0xF;

			_regs[PR] = _regs[PC] + 2;
			_delay = _regs[PC] + _regs[m] + 2;
		}

		private void BT(ushort op)
		{
			CHECK_DELAY_SLOT_PC();

			//	BT label
			//		If T = 1, disp × 2+ PC → PC; if T = 0, nop
			//	10001001 dddddddd

			// TODO: Honestly not sure how this is any different from BT/S??

			if ((_regs[SR] & SH_T) != 0)
			{
				int disp = (sbyte)(op & 0xFF);
				_delay = (uint)(_regs[PC] + 2 + (disp * 2));
			}
		}

		private void BTS(ushort op)
		{
			CHECK_DELAY_SLOT_PC();

			//	BT/S label
			//		If T = 1, disp × 2 + PC → PC; if T = 0, nop
			//	10001101 dddddddd

			if ((_regs[SR] & SH_T) != 0)
			{
				int disp = (sbyte)(op & 0xFF);
				_delay = (uint)(_regs[PC] + 2 + (disp * 2));
			}
		}

		private void CLRMAC()
		{
			//	CLRMAC
			//		0 → MACH, MACL
			//	0000000000101000

			_regs[MACH] = _regs[MACL] = 0;
		}

		private void CLRT()
		{
			//	CLRT
			//		0 → T
			//	0000000000001000

			_regs[SR] &= ~SH_T;
		}

		private void CMPEQ(ushort op)
		{
			//	CMP/EQ  Rm,Rn
			//		If Rn = Rm, 1 → T
			//	0011 nnnn mmmm 0000

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			if (_regs[n] == _regs[m])
			{
				_regs[SR] |= SH_T;
			}
			else
			{
				_regs[SR] &= ~SH_T;
			}
		}

		private void CMPGE(ushort op)
		{
			//	CMP/GE  Rm,Rn
			//		If Rn >= Rm with signed data, 1 → T
			//	0011 nnnn mmmm 0011

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			if ((int)_regs[n] >= (int)_regs[m])
			{
				_regs[SR] |= SH_T;
			}
			else
			{
				_regs[SR] &= ~SH_T;
			}
		}

		private void CMPGT(ushort op)
		{
			//	CMP/GT  Rm,Rn
			//		If Rn > Rm with signed data, 1 → T
			//	0011 nnnn mmmm 0111

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			if ((int)_regs[n] > (int)_regs[m])
			{
				_regs[SR] |= SH_T;
			}
			else
			{
				_regs[SR] &= ~SH_T;
			}
		}

		private void CMPHI(ushort op)
		{
			//	CMP/HI  Rm,Rn
			//		If Rn > Rm with unsigned data, 1 → T
			//	0011 nnnn mmmm 0110

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			if (_regs[n] > _regs[m])
			{
				_regs[SR] |= SH_T;
			}
			else
			{
				_regs[SR] &= ~SH_T;
			}
		}

		private void CMPHS(ushort op)
		{
			//	CMP/HS Rm,Rn
			//		If Rn≥Rm with unsigned data, 1 → T
			//	0011 nnnn mmmm 0010

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			if (_regs[n] >= _regs[m])
			{
				_regs[SR] |= SH_T;
			}
			else
			{
				_regs[SR] &= ~SH_T;
			}
		}

		private void CMPIM(ushort op)
		{
			//	CMP/EQ #imm,R0
			//		If R0 = imm, 1 → T
			//	10001000 iiiiiiii

			// TODO: is this *supposed* to be sign extended? MAME seems to, so I guess I will too

			uint imm = (uint)(sbyte)(op & 0xFF);

			if (_regs[0] == imm)
			{
				_regs[SR] |= SH_T;
			}
			else
			{
				_regs[SR] &= ~SH_T;
			}
		}

		private void CMPPL(ushort op)
		{
			//	CMP/PL Rn
			//		If Rn>0, 1 → T
			//	0100 nnnn 00010101

			uint n = (uint)(op >> 8) & 0xF;

			if ((int)_regs[n] > 0)
			{
				_regs[SR] |= SH_T;
			}
			else
			{
				_regs[SR] &= ~SH_T;
			}
		}

		private void CMPPZ(ushort op)
		{
			//	CMP/PZ  Rn
			//		If Rn ≥ 0, 1 → T
			//	0100 nnnn 0001 0001

			uint n = (uint)(op >> 8) & 0xF;

			if ((int)_regs[n] >= 0)
			{
				_regs[SR] |= SH_T;
			}
			else
			{
				_regs[SR] &= ~SH_T;
			}
		}

		private void CMPSTR(ushort op)
		{
			//	CMP/STR Rm,Rn
			//		If Rn and Rm have an equivalent byte, 1 → T
			//	0010 nnnn mmmm 1100

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			uint tmp, b3, b2, b1, b0;

			tmp = _regs[m] ^ _regs[n];

			b3 = (tmp >> 24) & 0xFF;
			b2 = (tmp >> 16) & 0xFF;
			b1 = (tmp >> 8) & 0xFF;
			b0 = tmp & 0xFF;

			if ((b3 | b2 | b1 | b0) != 0)
				_regs[SR] &= ~SH_T;
			else
				_regs[SR] |= SH_T;
		}

		private void DIV0S(ushort op)
		{
			//	DIV0S Rm,Rn
			//		MSB of Rn → Q, MSB of Rm → M, M ^ Q → T
			//	0010 nnnn mmmm 0111

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			uint Q = (_regs[n] >> 31);
			uint M = (_regs[m] >> 31);

			if (Q != 0)
			{
				_regs[SR] |= SH_Q;
			}
			else
			{
				_regs[SR] &= ~SH_Q;
			}

			if (M != 0)
			{
				_regs[SR] |= SH_M;
			}
			else
			{
				_regs[SR] &= ~SH_M;
			}

			if ((Q ^ M) != 0)
			{
				_regs[SR] |= SH_T;
			}
			else
			{
				_regs[SR] &= ~SH_T;
			}
		}

		private void DIV0U()
		{
			//	DIV0U
			//		0 → M/Q/T
			//	0000000000011001

			_regs[SR] &= ~(SH_Q | SH_M | SH_T);
		}

		private void DIV1(ushort op)
		{
			//	DIV1 Rm,Rn
			//		Single-step division(Rn / Rm)
			//	0011 nnnn mmmm 0100

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			// not even gonna *try* to understand what the hell this is doing ._.

			uint tmp0;
			uint old_q;

			old_q = _regs[SR] & SH_Q;
			if ((0x80000000 & _regs[n]) != 0)
				_regs[SR] |= SH_Q;
			else
				_regs[SR] &= ~SH_Q;

			_regs[n] = (_regs[n] << 1) | (_regs[SR] & SH_T);

			if (old_q == 0)
			{
				if ((_regs[SR] & SH_M) == 0)
				{
					tmp0 = _regs[n];
					_regs[n] -= _regs[m];
					if ((_regs[SR] & SH_Q) == 0)
					{
						if (_regs[n] > tmp0)
							_regs[SR] |= SH_Q;
						else
							_regs[SR] &= ~SH_Q;
					}
					else
					{
						if (_regs[n] > tmp0)
							_regs[SR] &= ~SH_Q;
						else
							_regs[SR] |= SH_Q;
					}
				}
				else
				{
					tmp0 = _regs[n];
					_regs[n] += _regs[m];
					if ((_regs[SR] & SH_Q) == 0)
					{
						if (_regs[n] < tmp0)
							_regs[SR] &= ~SH_Q;
						else
							_regs[SR] |= SH_Q;
					}
					else
					{
						if (_regs[n] < tmp0)
							_regs[SR] |= SH_Q;
						else
							_regs[SR] &= ~SH_Q;
					}
				}
			}
			else
			{
				if ((_regs[SR] & SH_M) == 0)
				{
					tmp0 = _regs[n];
					_regs[n] += _regs[m];
					if ((_regs[SR] & SH_Q) == 0)
						if (_regs[n] < tmp0)
							_regs[SR] |= SH_Q;
						else
							_regs[SR] &= ~SH_Q;
					else
						if (_regs[n] < tmp0)
						_regs[SR] &= ~SH_Q;
					else
						_regs[SR] |= SH_Q;
				}
				else
				{
					tmp0 = _regs[n];
					_regs[n] -= _regs[m];
					if ((_regs[SR] & SH_Q) == 0)
						if (_regs[n] > tmp0)
							_regs[SR] &= ~SH_Q;
						else
							_regs[SR] |= SH_Q;
					else
						if (_regs[n] > tmp0)
						_regs[SR] |= SH_Q;
					else
						_regs[SR] &= ~SH_Q;
				}
			}

			tmp0 = (_regs[SR] & (SH_Q | SH_M));
			if ((tmp0 == 0) || (tmp0 == 0x300)) /* if Q == M set T else clear T */
				_regs[SR] |= SH_T;
			else
				_regs[SR] &= ~SH_T;
		}

		private void DMULS(ushort op)
		{
			//	DMULS.L Rm,Rn
			//		Signed operation of Rn x Rm → MACH, MACL
			//	0011 nnnn mmmm 1101

			// TODO: make sure this is correct
			// I honestly have no earthly idea WTF MAME is doing here.
			// According to some testing this is equivalent, so.... eh??

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			long a = (int)_regs[n];
			long b = (int)_regs[m];

			ulong c = (ulong)(a * b);

			_regs[MACL] = (uint)(c & 0xFFFFFFFF);
			_regs[MACH] = (uint)(c >> 32) & 0xFFFFFFFF;
		}

		private void DMULU(ushort op)
		{
			//	DMULU.L Rm,Rn
			//		Unsigned operation of Rn x Rm → MACH, MACL
			//	0011 nnnn mmmm 0101

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			ulong a = _regs[n];
			ulong b = _regs[m];

			ulong c = a * b;

			_regs[MACL] = (uint)(c & 0xFFFFFFFF);
			_regs[MACH] = (uint)(c >> 32) & 0xFFFFFFFF;
		}

		private void DT(ushort op)
		{
			//	DT Rn
			//		Rn - 1 → Rn; if Rn is 0, 1 → T, if Rn is nonzero, 0 → T
			//	0100 nnnn 00010000

			uint n = (uint)(op >> 8) & 0xF;

			_regs[n]--;

			if (_regs[n] == 0)
			{
				_regs[SR] |= SH_T;
			}
			else
			{
				_regs[SR] &= ~SH_T;
			}
		}

		private void EXTSB(ushort op)
		{
			//	EXTS.B Rm,Rn
			//		A byte in Rm is sign - extended → Rn
			//	0110 nnnn mmmm 1110

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			_regs[n] = (uint)(sbyte)(_regs[m] & 0xFF);
		}

		private void EXTSW(ushort op)
		{
			//	EXTS.W Rm,Rn
			//		A word in Rm is sign - extended → Rn
			//	0110 nnnn mmmm 1111

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			_regs[n] = (uint)(short)(_regs[m] & 0xFFFF);
		}

		private void EXTUB(ushort op)
		{
			//	EXTU.B Rm,Rn
			//		A byte in Rm is zero - extended → Rn
			//	0110 nnnn mmmm 1100

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			_regs[n] = _regs[m] & 0xFF;
		}

		private void EXTUW(ushort op)
		{
			//	EXTU.W Rm,Rn
			//		A word in Rm is zero - extended → Rn
			//	0110 nnnn mmmm 1101

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			_regs[n] = _regs[m] & 0xFFFF;
		}

		private void JMP(ushort op)
		{
			CHECK_DELAY_SLOT_PC();

			//	JMP @Rm
			//		Delayed branch, Rm → PC
			//	0100 mmmm 00101011

			uint m = (uint)(op >> 8) & 0xF;
			_delay = m;
		}

		private void JSR(ushort op)
		{
			CHECK_DELAY_SLOT_PC();

			//	JSR @Rm
			//		Delayed branch, PC → PR, Rm → PC
			//	0100 mmmm 00001011

			uint m = (uint)(op >> 8) & 0xF;

			_regs[PR] = _regs[PC] + 2;
			_delay = _regs[m];
		}

		private void LDCGBR(ushort op)
		{
			//	LDC Rm,GBR
			//		Rm → GBR
			//	0100 mmmm 00011110

			uint m = (uint)(op >> 8) & 0xF;

			_regs[GBR] = _regs[m];
		}

		private void LDCMGBR(ushort op)
		{
			//	LDC.L @Rm+,GBR 
			//		(Rm) → GBR, Rm + 4 → Rm
			//	0100 mmmm 00010111

			uint m = (uint)(op >> 8) & 0xF;

			_regs[GBR] = _bus.Read32(_regs[m]);
			_regs[m] += 4;
		}

		private void LDCMSR(ushort op)
		{
			//	LDS.L @Rm+,SR
			//		(Rm) → SR, Rm + 4 → Rm
			//	0100 mmmm 00000111

			uint m = (uint)(op >> 8) & 0xF;

			_regs[SR] = _bus.Read32(_regs[m]);
			_regs[m] += 4;
		}

		private void LDCMVBR(ushort op)
		{
			//	LDC.L @Rm+,GBR
			//		(Rm) → GBR, Rm + 4 → Rm
			//	0100 mmmm 00010111

			uint m = (uint)(op >> 8) & 0xF;

			_regs[GBR] = _bus.Read32(_regs[m]);
			_regs[m] += 4;
		}

		private void LDCSR(ushort op)
		{
			//	LDC Rm,SR
			//		Rm → SR
			//	0100 mmmm 00001110

			uint m = (uint)(op >> 8) & 0xF;

			_regs[SR] = _regs[m];
		}

		private void LDCVBR(ushort op)
		{
			//	LDC Rm,VBR
			//		Rm → VBR
			//	0100 mmmm 00101110

			uint m = (uint)(op >> 8) & 0xF;

			_regs[VBR] = _regs[m];
		}

		private void LDSMACH(ushort op)
		{
			//	LDS Rm,MACH
			//		Rm → MACH
			//	0100 mmmm 00001010

			uint m = (uint)(op >> 8) & 0xF;

			_regs[MACH] = _regs[m];
		}

		private void LDSMACL(ushort op)
		{
			//	LDS Rm,MACL
			//		Rm → MACL
			//	0100 mmmm 00011010

			uint m = (uint)(op >> 8) & 0xF;
			_regs[MACL] = _regs[m];
		}

		private void LDSMMACH(ushort op)
		{
			//	LDS.L @Rm+,MACH
			//		(Rm) → MACH, Rm + 4 → Rm
			//	0100 mmmm 00000110

			uint m = (uint)(op >> 8) & 0xF;

			_regs[MACH] = _bus.Read32(_regs[m]);
			_regs[m] += 4;
		}

		private void LDSMMACL(ushort op)
		{
			//	LDS.L   @Rm+,MACL
			//		(Rm) → MACL, Rm + 4 → Rm
			//	0100 mmmm 00010110

			uint m = (uint)(op >> 8) & 0xF;

			_regs[MACL] = _bus.Read32(_regs[m]);
			_regs[m] += 4;
		}

		private void LDSMPR(ushort op)
		{
			//	LDS.L @Rm+,GBR
			//		(Rm) → PR, Rm + 4 → Rm
			//	0100 mmmm 00100110

			uint m = (uint)(op >> 8) & 0xF;

			_regs[PR] = _bus.Read32(_regs[m]);
			_regs[m] += 4;
		}

		private void LDSPR(ushort op)
		{
			//	LDS Rm,PR
			//		Rm → PR
			//	0100 mmmm 00101010

			uint m = (uint)(op >> 8) & 0xF;

			_regs[PR] = _regs[m];
		}

		private void MAC_L(ushort op)
		{
			//	MAC.L @Rm+,@Rn+
			//		Signed operation of(Rn) × (Rm) + MAC → MAC
			//	0000 nnnn mmmm 1111

			uint n = (uint)(op >> 8) & 0xF;
			uint m = (uint)(op >> 4) & 0xF;

			// just like with DMULS, MAME does a bunch of shit here that honestly it doesn't look like it needs to be doing.
			// we're going to not do any of that.

			long a = (int)_bus.Read32(_regs[n]);
			long b = (int)_bus.Read32(_regs[m]);
			ulong macl = _regs[MACL];
			ulong mach = _regs[MACH];
			long mac = (long)(macl | (mach << 32));

			long c = (a * b) + mac;

			uint cl = (uint)(c & 0xFFFFFFFF);
			uint ch = (uint)(c >> 32) & 0xFFFFFFFF;

			_regs[MACL] = cl;
			_regs[MACH] = ch;
		}

		private void MAC_W(ushort op)
		{
			//	MAC.W @Rm+,@Rn+
			//		Signed, (Rn) × (Rm) + MAC → MAC
			//	0100 nnnn mmmm 1111

			uint n = (uint)(op >> 8) & 0xF;
			uint m = (uint)(op >> 4) & 0xF;

			int a = (short)_bus.Read16(_regs[n]);
			int b = (short)_bus.Read16(_regs[m]);

			_regs[MACL] = (uint)(a * b) + _regs[MACL];
		}

		private void MOV(ushort op)
		{
			//	MOV Rm,Rn
			//		Rm → Rn
			//	0110 nnnn mmmm 0011

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			_regs[n] = _regs[m];
		}

		private void MOVA(ushort op)
		{
			//	MOVA @(disp,PC),R0
			//		disp × 4 + PC → R0
			//	11000111 dddddddd

			uint disp = (uint)(op & 0xFF);
			uint ea = ((_regs[PC] + 2) & ~3U) + (disp * 4);
			_regs[0] = ea;
		}

		private void MOVBL(ushort op)
		{
			//	MOV.B @Rm,Rn
			//		(Rm) → Sign extension → Rn
			//	0110 nnnn mmmm 0000

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			_regs[n] = (uint)(sbyte)_bus.Read8(_regs[m]);
		}

		private void MOVBL0(ushort op)
		{
			//	MOV.B @(R0,Rm),Rn
			//		(R0 + Rm) → Sign extension → Rn
			//	0000 nnnn mmmm 1100

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			uint ea = _regs[0] + _regs[m];
			_regs[n] = (uint)(sbyte)_bus.Read8(ea);
		}

		private void MOVBL4(ushort op)
		{
			//	MOV.B @(disp,Rm),Rn
			//		(disp + Rm) → Sign extension → R0
			//	10000100 mmmm dddd

			uint d = (uint)(op & 0xF);
			uint m = (uint)(op >> 4) & 0xF;

			uint ea = _regs[m] + d;
			_regs[0] = (uint)(sbyte)_bus.Read8(ea);
		}

		private void MOVBLG(ushort op)
		{
			//	MOV.B @(disp,GBR),R0
			//		(disp + GBR) → Sign extension → R0
			//	11000100 dddddddd

			uint disp = (uint)(op & 0xFF);
			uint ea = _regs[GBR] + (disp * 2);
			_regs[0] = (uint)(sbyte)_bus.Read8(ea);
		}

		private void MOVBM(ushort op)
		{
			//	MOV.B Rm,@–Rn
			//		Rn–1 → Rn, Rm → (Rn)
			//	0010 nnnn mmmm 0100

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			_bus.Write8(_regs[n]--, (byte)_regs[m]);
		}

		private void MOVBP(ushort op)
		{
			//	MOV.B @Rm+,Rn
			//		(Rm) → Sign extension → Rn, Rm + 1 → Rm
			//	0110 nnnn mmmm 0100

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			_regs[n] = (uint)(sbyte)_bus.Read8(_regs[m]);
			_regs[m]++;
		}

		private void MOVBS(ushort op)
		{
			//	MOV.B Rm,@Rn
			//		Rm → (Rn)
			//	0010 nnnn mmmm 0000

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			_bus.Write8(_regs[n], (byte)_regs[m]);
		}

		private void MOVBS0(ushort op)
		{
			//	MOV.B Rm,@(R0,Rn)
			//		Rm → (R0 + Rn)
			//	0000 nnnn mmmm 0100

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			_bus.Write8(_regs[0] + _regs[n], (byte)_regs[m]);
		}

		private void MOVBS4(ushort op)
		{
			//	MOV.B R0,@(disp, Rn)
			//		R0 → (disp + Rn)
			//	10000000 nnnn dddd

			uint d = (uint)(op & 0xF);
			uint n = (uint)(op >> 4) & 0xF;

			uint ea = _regs[n] + (d * 2);
			_bus.Write8(ea, (byte)_regs[0]);
		}

		private void MOVBSG(ushort op)
		{
			//	MOV.B R0,@(disp, GBR)
			//		R0 → (disp + GBR)
			//	11000000 dddddddd

			uint disp = (uint)(op & 0xFF);
			uint ea = _regs[GBR] + disp;
			_bus.Write8(ea, (byte)_regs[0]);
		}

		private void MOVI(ushort op)
		{
			//	MOV #imm,Rn
			//		mm → Sign extension → Rn
			//	1110 nnnn iiiiiiii

			uint imm = (uint)(sbyte)(op & 0xFF);
			uint n = (uint)(op >> 8) & 0xF;

			_regs[n] = imm;

		}

		private void MOVLI(ushort op)
		{
			//	MOV.L @(disp,PC),Rn
			//		(disp × 4 + PC) → Rn
			//	1101 nnnn dddddddd

			uint disp = (uint)(op & 0xFF);
			uint n = (uint)(op >> 8) & 0xF;
			uint addr = ((_regs[PC] + 2) & 0xFFFFFFFC) + (disp << 2);
			uint val = _bus.Read32(addr);
			_regs[n] = val;
		}

		private void MOVLL(ushort op)
		{
			//	MOV.L @Rm,Rn
			//		(Rm) → Rn
			//	0110 nnnn mmmm 0010

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			_regs[n] = _bus.Read32(_regs[m]);
		}

		private void MOVLL0(ushort op)
		{
			//	MOV.L @(R0,Rm),Rn
			//		(R0 + Rm) → Rn
			//	0000 nnnn mmmm 1110

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			_regs[n] = _bus.Read32(_regs[m] + _regs[0]);
		}

		private void MOVLL4(ushort op)
		{
			//	MOV.L @(disp,Rm),Rn
			//		(disp × 4 + Rm) → Rn
			//	0101 nnnn mmmm dddd

			uint d = (uint)(op & 0xF);
			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			uint ea = _regs[m] + (d << 2);
			_regs[n] = _bus.Read32(ea);
		}

		private void MOVLLG(ushort op)
		{
			//	MOV.L @(disp,GBR),R0
			//		(disp × 4 + GBR) → R0
			//	11000110 dddddddd

			uint disp = (uint)(op & 0xFF);
			uint ea = _regs[GBR] + (disp * 4);
			_regs[0] = _bus.Read32(ea);
		}

		private void MOVLM(ushort op)
		{
			//	MOV.L Rm,@–Rn
			//		Rn–4 → Rn, Rm → (Rn)
			//	0010 nnnn mmmm 0110

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			uint data = _regs[m];
			_regs[n] -= 4;
			_bus.Write32(_regs[n], data);
		}

		private void MOVLP(ushort op)
		{
			//	MOV.L @Rm+,Rn
			//		(Rm) → Rn, Rm + 4 → Rm
			//	0110 nnnn mmmm 0110

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			_regs[n] = _bus.Read32(_regs[m]);
			_regs[m] += 4;
		}

		private void MOVLS(ushort op)
		{
			//	MOV.L Rm,@Rn
			//		Rm → (Rn)
			//	0010 nnnn mmmm 0010

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			_bus.Write32(_regs[n], _regs[m]);
		}

		private void MOVLS0(ushort op)
		{
			//	MOV.L Rm,@(R0,Rn)
			//		Rm → (R0 + Rn)
			//	0000 nnnn mmmm 0110

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			_bus.Write32(_regs[n] + _regs[0], _regs[m]);
		}

		private void MOVLS4(ushort op)
		{
			//	MOV.L Rm,@(disp,Rn)
			//		Rm → (disp × 4 + Rn)
			//	0001 nnnn mmmm dddd

			uint d = (uint)(op & 0xF);
			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			uint ea = _regs[n] + (d << 2);

			_bus.Write32(ea, _regs[m]);
		}

		private void MOVLSG(ushort op)
		{
			//	MOV.L R0,@(disp,GBR)
			//		R0 → (disp × 4 + GBR)
			//	11000010 dddddddd

			uint disp = (uint)(op & 0xFF);
			uint ea = _regs[GBR] + (disp * 4);
			_bus.Write32(ea, _regs[0]);
		}

		private void MOVT(ushort op)
		{
			//	MOVT Rn
			//		T → Rn
			//	0000 nnnn 00101001

			uint n = (uint)(op >> 8) & 0xF;

			_regs[n] = _regs[SR] & SH_T;
		}

		private void MOVWI(ushort op)
		{
			//	MOV.W @(disp, PC),Rn
			//		(disp × 2 + PC) → Sign extension → Rn
			//	1001 nnnn dddddddd

			uint disp = (uint)op & 0xFF;
			uint n = (uint)(op >> 8) & 0xF;
			uint ea = _regs[PC] + 2 + (disp * 2);
			_regs[n] = (uint)(short)_bus.Read16(ea);
		}

		private void MOVWL(ushort op)
		{
			//	MOV.W @Rm,Rn
			//		(Rm) → Sign extension → Rn
			//	0110 nnnn mmmm 0001

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			_regs[n] = (uint)(short)_bus.Read16(_regs[m]);
		}

		private void MOVWL0(ushort op)
		{
			//	MOV.W @(R0,Rm),Rn
			//		(R0 + Rm) → Sign extension → Rn
			//	0000 nnnn mmmm 1101

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			_regs[n] = (uint)(short)_bus.Read16(_regs[m] + _regs[0]);
		}

		private void MOVWL4(ushort op)
		{
			//	MOV.W @(disp,Rm),Rn
			//		(disp × 2 + Rm) → Sign extension → R0
			//	10000101 mmmm dddd

			uint d = (uint)(op & 0xF);
			uint m = (uint)(op >> 4) & 0xF;

			uint ea = _regs[m] + (d * 2);
			_regs[0] = (uint)(short)_bus.Read16(ea);
		}

		private void MOVWLG(ushort op)
		{
			//	MOV.W @(disp,GBR),R0
			//		(disp × 2 + GBR) → Sign extension → R0
			//	11000101 dddddddd

			uint disp = (uint)(op & 0xFF);
			uint ea = _regs[GBR] + (disp * 2);
			_regs[0] = (uint)(short)_bus.Read16(ea);
		}

		private void MOVWM(ushort op)
		{
			//	MOV.W Rm,@–Rn
			//		Rn–2 → Rn, Rm → (Rn)
			//	0010 nnnn mmmm 0101

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			_regs[n] -= 2;
			_bus.Write16(_regs[n], (ushort)_regs[m]);
		}

		private void MOVWP(ushort op)
		{
			//	MOV.W @Rm+,Rn
			//		(Rm) → Sign extension → Rn, Rm + 2 → Rm
			//	0110 nnnn mmmm 0101

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			_regs[n] = (uint)(short)_bus.Read16(_regs[m]);
			_regs[m] += 2;
		}

		private void MOVWS(ushort op)
		{
			//	MOV.W Rm,@Rn
			//		Rm → (Rn)
			//	0010 nnnn mmmm 0001

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			_bus.Write16(_regs[n], (ushort)_regs[m]);
		}

		private void MOVWS0(ushort op)
		{
			//	MOV.W Rm,@(R0,Rn)
			//		Rm → (R0 + Rn)
			//	0000 nnnn mmmm 0101

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			_bus.Write16(_regs[n] + _regs[0], (ushort)_regs[m]);
		}

		private void MOVWS4(ushort op)
		{
			//	MOV.W R0,@(disp, Rn)
			//		R0 → (disp × 2 + Rn)
			//	10000001 nnnn dddd

			uint d = (uint)(op & 0xF);
			uint n = (uint)(op >> 4) & 0xF;

			uint ea = _regs[n] + (d * 2);
			_bus.Write16(ea, (ushort)_regs[0]);
		}

		private void MOVWSG(ushort op)
		{
			//	MOV.W R0,@(disp, GBR)
			//		R0 → (disp × 2 + GBR)
			//	11000001 dddddddd

			uint disp = (uint)(op & 0xFF);
			uint ea = _regs[GBR] + (disp * 2);
			_bus.Write16(ea, (ushort)_regs[0]);
		}

		private void MULL(ushort op)
		{
			//	MUL.L Rm,Rn
			//		Rn × Rm → MACL
			//	0000 nnnn mmmm 0111

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			_regs[MACL] = _regs[m] * _regs[n];
		}

		private void MULS(ushort op)
		{
			//	MULS.W Rm,Rn
			//		Signed operation of Rn × Rm → MACL
			//	0010 nnnn mmmm 1111

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			_regs[MACL] = (uint)(short)((short)_regs[m] * (short)_regs[n]);
		}

		private void MULU(ushort op)
		{
			//	MULU.W Rm,Rn
			//		Unsigned operation of Rn × Rm → MACL
			//	0010 nnnn mmmm 1110

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			_regs[MACL] = (ushort)((ushort)_regs[m] * (ushort)_regs[n]);
		}

		private void NEG(ushort op)
		{
			//	NEG Rm,Rn
			//		0 - Rm → Rn
			//	0110 nnnn mmmm 1011

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			_regs[n] = 0 - _regs[m];
		}

		private void NEGC(ushort op)
		{
			//	NEGC Rm,Rn
			//		0 - Rm - T → Rn, borrow → T
			//	0110 nnnn mmmm 1010

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			uint tmp = _regs[m];

			_regs[n] = 0 - tmp - (_regs[SR] & SH_T);

			if (tmp != 0 || (_regs[SR] & SH_T) != 0)
			{
				_regs[SR] |= SH_T;
			}
			else
			{
				_regs[SR] &= ~SH_T;
			}
		}

		private void NOP()
		{
		}

		private void NOT(ushort op)
		{
			//	NOT Rm,Rn
			//		~Rm → Rn
			//	0110 nnnn mmmm 0111

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			_regs[n] = ~_regs[m];
		}

		private void OR(ushort op)
		{
			//	OR Rm,Rn
			//		Rn | Rm → Rn
			//	0010 nnnn mmmm 1011

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			_regs[n] |= _regs[m];
		}

		private void ORI(ushort op)
		{
			//	OR #imm,R0
			//		R0 | imm → R0
			//	11001011 iiiiiiii

			byte imm = (byte)(op & 0xFF);
			_regs[0] |= imm;
		}

		private void ORM(ushort op)
		{
			//	OR.B #imm,@(R0,GBR)
			//		(R0 + GBR) | imm → (R0 + GBR)
			//	11001111 iiiiiiii

			uint ea = _regs[0] + _regs[VBR];
			byte imm = (byte)(op & 0xFF);
			_bus.Write8(ea, (byte)(_bus.Read8(ea) | imm));
		}

		private void ROTCL(ushort op)
		{
			//	ROTCL Rn
			//		T ← Rn ← T
			//	0100 nnnn 00100100

			uint n = (uint)(op >> 8) & 0xF;

			uint temp = (_regs[n] >> 31) & SH_T;
			_regs[n] = (_regs[n] << 1) | (_regs[SR] & SH_T);
			_regs[SR] = (_regs[SR] & ~SH_T) | temp;
		}

		private void ROTCR(ushort op)
		{
			//	ROTCR Rn
			//		T → Rn → T
			//	0100 nnnn 00100101

			uint n = (uint)(op >> 8) & 0xF;

			uint temp = (_regs[SR] & SH_T) << 31;
			if ((_regs[n] & SH_T) != 0)
				_regs[SR] |= SH_T;
			else
				_regs[SR] &= ~SH_T;
			_regs[n] = (_regs[n] >> 1) | temp;
		}

		private void ROTL(ushort op)
		{
			//	ROTL Rn
			//		T ← Rn ← MSB
			//	0100 nnnn 00000100

			uint n = (uint)(op >> 8) & 0xF;

			_regs[SR] = (_regs[SR] & ~SH_T) | ((_regs[n] >> 31) & SH_T);
			_regs[n] = (_regs[n] << 1) | (_regs[n] >> 31);
		}

		private void ROTR(ushort op)
		{
			//	ROTR Rn
			//		LSB → Rn → T
			//	0100 nnnn 00000101

			uint n = (uint)(op >> 8) & 0xF;

			_regs[SR] = (_regs[SR] & ~SH_T) | ((_regs[n] >> 31) & SH_T);
			_regs[n] = (_regs[n] >> 1) | (_regs[n] << 31);
		}

		private void RTE()
		{
			//	RTE
			//		Delayed branch, stack area → PC / SR
			//	0000000000101011

			_delay = Pop32();
			_regs[SR] = Pop32() & SH_FLAGS;
		}

		private void RTS()
		{
			CHECK_DELAY_SLOT_PC();

			// RTS
			//		Delayed branch, PR → PC
			//	0000000000001011

			_delay = _regs[PR];
		}

		private void SETT()
		{
			//	SETT
			//		1 → T
			//	0000000000011000

			_regs[SR] |= SH_T;
		}

		private void SHAL(ushort op)
		{
			//	SHAL
			//		T ← Rn ← 0
			//	0100 nnnn 00100000

			uint n = (uint)(op >> 8) & 0xF;

			_regs[SR] = (_regs[SR] & ~SH_T) | ((_regs[n] >> 31) & SH_T);
			_regs[n] <<= 1;
		}

		private void SHAR(ushort op)
		{
			//	SHAR
			//		MSB → Rn → T
			//	0100 nnnn 00100001

			uint n = (uint)(op >> 8) & 0xF;

			_regs[SR] = (_regs[SR] & ~SH_T) | (_regs[n] & SH_T);
			_regs[n] = (uint)((int)_regs[n] >> 1);
		}

		private void SHLL(ushort op)
		{
			//	SHLL Rn
			//		T ← Rn ← 0
			//	0100 nnnn 00000000

			uint n = (uint)(op >> 8) & 0xF;

			_regs[SR] = (_regs[SR] & ~SH_T) | ((_regs[n] >> 31) & SH_T);
			_regs[n] <<= 1;
		}

		private void SHLL16(ushort op)
		{
			//	SHLL16 Rn
			//		Rn<<16 → Rn
			//	0100 nnnn 00101000

			uint n = (uint)(op >> 8) & 0xF;

			_regs[n] <<= 16;
		}

		private void SHLL2(ushort op)
		{
			//	SHLL2 Rn
			//		Rn<<2 → Rn
			//	0100 nnnn 00001000

			uint n = (uint)(op >> 8) & 0xF;
			_regs[n] <<= 2;
		}

		private void SHLL8(ushort op)
		{
			//	SHLL8 Rn
			//		Rn<<8 → Rn
			//	0100 nnnn 00011000

			uint n = (uint)(op >> 8) & 0xF;

			_regs[n] <<= 8;
		}

		private void SHLR(ushort op)
		{
			//	SHLR Rn
			//		0 → Rn → T
			//	0100 nnnn 00000001

			uint n = (uint)(op >> 8) & 0xF;

			_regs[SR] = (_regs[SR] & ~SH_T) | ((_regs[n] >> 31) & SH_T);
			_regs[n] >>= 1;
		}

		private void SHLR16(ushort op)
		{
			//	SHLR16 Rn
			//		Rn>>16 → Rn
			//	0100 nnnn 00101001

			uint n = (uint)(op >> 8) & 0xF;
		}

		private void SHLR2(ushort op)
		{
			//	SHLR2 Rn
			//		Rn>>2 → Rn
			//	0100 nnnn 00001000

			uint n = (uint)(op >> 8) & 0xF;
			_regs[n] >>= 2;
		}

		private void SHLR8(ushort op)
		{
			//	SHLR8 Rn
			//		Rn>>8 → Rn
			//	0100 nnnn 00011001

			uint n = (uint)(op >> 8) & 0xF;

			_regs[n] >>= 8;
		}

		private void SLEEP()
		{
			_state = CpuState.Sleep;
		}
		private void STCGBR(ushort op)
		{
			//	STC GBR,Rn
			//		GBR → Rn
			//	0000 nnnn 00010010

			uint n = (uint)(op >> 8) & 0xF;

			_regs[n] = _regs[GBR];
		}

		private void STCMGBR(ushort op)
		{
			//	STC.L GBR,@–Rn
			//		Rn – 4 → Rn, GBR → (Rn)
			//	0100 nnnn 00010011

			uint n = (uint)(op >> 8) & 0xF;

			_regs[n] -= 4;
			_bus.Write32(_regs[n], _regs[GBR]);
		}

		private void STCMSR(ushort op)
		{
			//	STC.L SR,@–Rn
			//		Rn – 4 → Rn, SR → (Rn)
			//	0100 nnnn 00000011

			uint n = (uint)(op >> 8) & 0xF;

			_regs[n] -= 4;
			_bus.Write32(_regs[n], _regs[SR]);
		}

		private void STCMVBR(ushort op)
		{
			//	STC.L VBR,@–Rn
			//		Rn – 4 → Rn, SR → (Rn)
			//	0100 nnnn 00000011

			uint n = (uint)(op >> 8) & 0xF;

			_regs[n] -= 4;
			_bus.Write32(_regs[n], _regs[VBR]);
		}

		private void STCSR(ushort op)
		{
			//	STC SR,Rn
			//		SR → Rn
			//	0000 nnnn 00000010

			uint n = (uint)(op >> 8) & 0xF;

			_regs[n] = _regs[SR];
		}

		private void STCVBR(ushort op)
		{
			//	STC VBR,Rn
			//		VBR → Rn
			//	0000 nnnn 00100010

			uint n = (uint)(op >> 8) & 0xF;

			_regs[n] = _regs[VBR];
		}

		private void STSMACH(ushort op)
		{
			//	STS MACH,Rn
			//		MACH → Rn
			//	0000 nnnn 00001010

			uint n = (uint)(op >> 8) & 0xF;

			_regs[n] = _regs[MACH];
		}

		private void STSMACL(ushort op)
		{
			//	STS MACL,Rn
			//		MACL → Rn
			//	0000 nnnn 00011010

			uint n = (uint)(op >> 8) & 0xF;

			_regs[n] = _regs[MACL];
		}

		private void STSMMACH(ushort op)
		{
			//	STS.L MACH,@–Rn
			//		Rn – 4 → Rn, MACH → (Rn)
			//	0100 nnnn 00000010

			uint n = (uint)(op >> 8) & 0xF;

			_regs[n] -= 4;
			_bus.Write32(_regs[n], _regs[MACH]);
		}

		private void STSMMACL(ushort op)
		{
			//	STS.L MACL,@–Rn
			//		Rn – 4 → Rn, MACL → (Rn)
			//	0100 nnnn 00010010

			uint n = (uint)(op >> 8) & 0xF;

			_regs[n] -= 4;
			_bus.Write32(_regs[n], _regs[MACL]);
		}

		private void STSMPR(ushort op)
		{
			//	STS.L PR,@–Rn
			//		Rn–4 → Rn, PR → (Rn)
			//	0100 nnnn 00100010

			uint n = (uint)(op >> 8) & 0xF;

			_regs[n] -= 4;
			_bus.Write32(_regs[PR], _regs[n]);
		}

		private void STSPR(ushort op)
		{
			//	STS PR,Rn
			//		PR → Rn
			//	0000 nnnn 00101010

			uint n = (uint)(op >> 8) & 0xF;

			_regs[n] = _regs[PR];
		}

		private void SUB(ushort op)
		{
			//	SUB Rm,Rn
			//		Rn–Rm → Rn
			//	0011 nnnn mmmm 1000

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			_regs[n] -= _regs[m];
		}

		private void SUBC(ushort op)
		{
			//	SUBC Rm,Rn
			//		Rn–Rm → Rn, Borrow → T
			//	0011 nnnn mmmm 1010

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			uint tmp1 = _regs[n] - _regs[m];
			uint tmp0 = _regs[n];
			_regs[n] = tmp1 - (_regs[SR] & SH_T);
			
			if (tmp0 < tmp1)
				_regs[SR] |= SH_T;
			else
				_regs[SR] &= ~SH_T;

			if (tmp1 < _regs[n])
				_regs[SR] |= SH_T;
		}

		private void SUBV(ushort op)
		{
			//	SUBV Rm,Rn
			//		Rn–Rm → Rn, Underflow → T
			//	0011 nnnn mmmm 1011

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			int lhs = (int)_regs[n];
			int rhs = (int)_regs[m];

			if ((lhs < 0 && (lhs > int.MaxValue + rhs)) ||
				(lhs > 0 && (lhs < int.MinValue + rhs)))
			{
				_regs[SR] |= SH_T;
			}
			else
			{
				_regs[SR] &= ~SH_T;
			}

			_regs[n] -= _regs[m];
		}

		private void SWAPB(ushort op)
		{
			//	SWAP.B Rm,Rn 
			//		Rm → Swap upper and lower 2 bytes → Rn
			//	0110 nnnn mmmm 1000

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			uint lo = _regs[m] & 0xFF;
			uint hi = (_regs[m] >> 8) & 0xFF;

			_regs[n] = (lo << 8) | hi | (_regs[m] & 0xFFFF0000);
		}

		private void SWAPW(ushort op)
		{
			//	SWAP.W Rm,Rn 
			//		Rm → Swap upper and lower 2 words → Rn
			//	0110 nnnn mmmm 1000

			uint m = (uint)(op >> 4) & 0xF;
			uint n = (uint)(op >> 8) & 0xF;

			uint lo = _regs[m] & 0xFFFF;
			uint hi = (_regs[m] >> 16) & 0xFFFF;

			_regs[n] = (lo << 16) | hi;
		}

		private void TAS(ushort op)
		{
			//	TAS.B @Rn
			//		If (Rn) is 0, 1 → T; 1 → MSB of(Rn)
			//	0100 nnnn 00011011

			uint n = (uint)(op >> 8) & 0xF;
			byte tmp = _bus.Read8(_regs[n]);

			if (tmp == 0)
			{
				_regs[SR] |= SH_T;
			}
			else
			{
				_regs[SR] &= ~SH_T;
			}

			tmp |= 0x80;
			_bus.Write8(_regs[n], tmp);
		}

		private void TRAPA(ushort op)
		{
			//	TRAPA #imm
			//		PC/SR → stack area, (imm × 4 + VBR) → PC
			//	11000011 iiiiiiii

			uint imm = (uint)(op & 0xFF);

			Push32(_regs[SR]);
			Push32(_regs[PC]);

			_state = CpuState.ExceptionProcessing;
			_regs[PC] = _bus.Read32(_regs[VBR] + (imm * 4));
		}

		private void TST(ushort op)
		{
			//	TST Rm,Rn
			//		Rn & Rm; if the result is 0, 1 → T
			//	0010 nnnn mmmm 1000

			uint n = (uint)(op >> 8) & 0xF;
			uint m = (uint)(op >> 4) & 0xF;

			if ((_regs[n] & _regs[m]) == 0)
			{
				_regs[SR] |= SH_T;
			}
			else
			{
				_regs[SR] &= ~SH_T;
			}
		}

		private void TSTI(ushort op)
		{
			//	TST #imm,R0
			//		R0 & imm; if the result is 0, 1 → T
			//	11001000 iiiiiiii

			byte imm = (byte)(op & 0xFF);

			if ((_regs[0] & imm) == 0)
			{
				_regs[SR] |= SH_T;
			}
			else
			{
				_regs[SR] &= ~SH_T;
			}
		}

		private void TSTM(ushort op)
		{
			//	TST.B #imm,@(R0,GBR)
			//		(R0 + GBR) & imm; if the result is 0, 1 → T
			//	11001100 iiiiiiii

			uint ea = _regs[0] + _regs[VBR];
			byte imm = (byte)(op & 0xFF);

			if ((_bus.Read8(ea) & imm) == 0)
			{
				_regs[SR] |= SH_T;
			}
			else
			{
				_regs[SR] &= ~SH_T;
			}
		}

		private void XOR(ushort op)
		{
			//	XOR Rm,Rn
			//		Rn ^ Rm → Rn
			//	0010 nnnn mmmm 1010

			uint n = (uint)(op >> 8) & 0xF;
			uint m = (uint)(op >> 4) & 0xF;

			_regs[n] ^= _regs[m];
		}

		private void XORI(ushort op)
		{
			//	XOR #imm,R0
			//		R0 ^ imm → R0
			//	11001010 iiiiiiii

			byte imm = (byte)(op & 0xFF);
			_regs[0] ^= imm;
		}

		private void XORM(ushort op)
		{
			//	XOR.B #imm,@(R0,GBR)
			//		(R0 + GBR) ^ imm → (R0 + GBR)
			//	11001110 iiiiiiii

			uint ea = _regs[0] + _regs[VBR];
			byte imm = (byte)(op & 0xFF);
			_bus.Write8(ea, (byte)(_bus.Read8(ea) ^ imm));
		}
		private void XTRCT(ushort op)
		{
			//	XTRCT Rm,Rn
			//		Center 32 bits of Rm and Rn → Rn
			//	0010 nnnn mmmm 1101

			uint n = (uint)(op >> 8) & 0xF;
			uint m = (uint)(op >> 4) & 0xF;

			_regs[n] = (_regs[n] >> 16) | (_regs[m] << 16);
		}
		#endregion
	}
}
